// Shared "download as HTML / PDF" for AI result views.
// HTML: builds a styled, standalone document and downloads it directly.
// PDF:  opens the browser print dialog (InkReel has no server PDF engine).

const DOC_STYLES = `
  body { margin: 0; padding: 32px; color: #0f172a; font-family: Inter, Arial, sans-serif; line-height: 1.65; background: #f8fafc; }
  main { max-width: 920px; margin: 0 auto; padding: 28px; border: 1px solid #dbe6f3; border-radius: 18px; background: #fff; }
  h1.export-title { margin: 0 0 6px; padding-bottom: 10px; border-bottom: 2px solid #f1f5f9; font-size: 24px; }
  h2, h3 { margin: 24px 0 14px; padding: 12px 14px; border-radius: 12px; background: linear-gradient(135deg, #0f172a 0%, #42115f 58%, #c13584 100%); color: #fff; font-size: 17px; }
  h4 { margin: 18px 0 8px; color: #475569; }
  ul, ol { margin: 12px 0; padding-left: 22px; }
  li { margin: 5px 0; }
  table { width: 100%; border-collapse: collapse; margin: 14px 0; font-size: 14px; }
  th { padding: 9px 12px; background: linear-gradient(135deg, #0f172a 0%, #42115f 58%, #c13584 100%); color: #fff; text-align: left; }
  td { padding: 9px 12px; border-bottom: 1px solid #e2e8f0; vertical-align: top; }
  tr:nth-child(even) td { background: #fafbfc; }
  code { padding: 2px 6px; border-radius: 5px; background: rgba(214,41,118,0.08); color: #c13584; }
  pre { overflow-x: auto; padding: 16px; border-radius: 12px; background: #0f172a; color: #fff; }
  blockquote { margin: 14px 0; padding: 10px 14px; border-left: 3px solid #d62976; background: rgba(214,41,118,0.06); }
  .export-meta { margin: 0 0 20px; color: #64748b; font-size: 13px; }
  /* Hide interactive chrome that may be copied in from the app's result DOM. */
  button, .export-btns, .st-btn, .doc-copy-btn { display: none !important; }
  @media print {
    body { padding: 0; background: #fff; }
    main { max-width: none; border: none; border-radius: 0; }
    h2, h3, th { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
  }
`

function esc(s: string): string {
  return s.replace(/[<>&]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;' }[c] as string))
}

export function buildExportHtml(title: string, bodyHtml: string, subtitle?: string): string {
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>${esc(title)}</title>
  <style>${DOC_STYLES}</style>
</head>
<body>
  <main>
    <h1 class="export-title">${esc(title)}</h1>
    ${subtitle ? `<p class="export-meta">${esc(subtitle)}</p>` : ''}
    ${bodyHtml}
  </main>
</body>
</html>`
}

export function downloadHtml(title: string, bodyHtml: string, filename: string, subtitle?: string): void {
  const html = buildExportHtml(title, bodyHtml, subtitle)
  const url = URL.createObjectURL(new Blob([html], { type: 'text/html;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = filename.endsWith('.html') ? filename : `${filename}.html`
  document.body.appendChild(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}


export function printToPdf(title: string, bodyHtml: string, subtitle?: string): void {
  const html = buildExportHtml(title, bodyHtml, subtitle)
  const frame = document.createElement('iframe')
  Object.assign(frame.style, { position: 'fixed', right: '0', bottom: '0', width: '0', height: '0', border: '0' })
  document.body.appendChild(frame)
  const doc = frame.contentDocument
  if (!doc) { frame.remove(); return }
  doc.open(); doc.write(html); doc.close()
  // Give the iframe a tick to lay out before invoking the print dialog.
  setTimeout(() => {
    frame.contentWindow?.focus()
    frame.contentWindow?.print()
    setTimeout(() => frame.remove(), 1000)
  }, 300)
}

export function exportStamp(): string {
  return new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')
}
