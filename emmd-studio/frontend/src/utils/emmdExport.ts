// Export an EMMD document three ways:
//   • .emmd   — the portable source file (with the seal comment when sealed)
//   • .html   — a self-contained, themed HTML "twin"
//   • .pdf    — that HTML run through our Snappdf engine (/api/pdf/html-to-pdf)
//
// The rendered body HTML is captured from the live EmmdRenderer container so the
// export always matches what the reader sees (a "PDF twin" of the on-screen doc).
import api from '../services/api'
import { parseEmmd, buildEmmdFile, type EmmdSealInfo } from './emmd'
import { buildEmmdCss, getTheme } from './emmdThemes'

function esc(s: string): string {
  return s.replace(/[<>&]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;' }[c] as string))
}

/** A print-friendly page frame wrapped around the themed document CSS. */
const PAGE_CSS = `
  html,body { margin:0; }
  body { padding:40px 32px; background:#f1f5f9; }
  .emmd-page { max-width:820px; margin:0 auto; padding:48px; background:#fff; border-radius:14px; box-shadow:0 1px 3px rgba(15,23,42,.12); }
  .emmd-doc.emmd-theme-dark { padding:0; }
  @media print {
    body { padding:0; background:#fff; }
    .emmd-page { max-width:none; padding:0; border-radius:0; box-shadow:none; }
    .emmd-doc h1,.emmd-doc h2,.emmd-doc th { -webkit-print-color-adjust:exact; print-color-adjust:exact; }
  }
`

/** Build a standalone, self-contained themed HTML document from rendered body HTML. */
export function buildEmmdStandaloneHtml(content: string, bodyHtml: string, themeOverride?: string): string {
  const parsed = parseEmmd(content)
  const themeId = getTheme(themeOverride || parsed.frontmatter.theme).id
  const css = buildEmmdCss(parsed.styleCss)
  const title = parsed.frontmatter.title || 'EMMD document'
  return `<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <meta name="generator" content="GenPrompt Enhanced MMD" />
  <title>${esc(title)}</title>
  <style>${css}\n${PAGE_CSS}</style>
</head>
<body>
  <div class="emmd-page">
    <div class="emmd-doc emmd-theme-${themeId}">${bodyHtml}</div>
  </div>
</body>
</html>`
}

function triggerDownload(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = filename
  document.body.appendChild(link)
  link.click()
  link.remove()
  URL.revokeObjectURL(url)
}

export function exportStamp(): string {
  return new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')
}

function ensureExt(name: string, ext: string): string {
  return name.endsWith(ext) ? name : `${name}${ext}`
}

/** Download the portable .emmd source (embeds the seal comment when sealed). */
export function downloadEmmdSource(content: string, seal: EmmdSealInfo | null, filenameBase: string): void {
  const file = buildEmmdFile(content, seal)
  triggerDownload(new Blob([file], { type: 'text/markdown;charset=utf-8' }), ensureExt(filenameBase, '.emmd'))
}

/** Download the themed, self-contained HTML twin. */
export function downloadEmmdHtml(content: string, bodyHtml: string, filenameBase: string, themeOverride?: string): void {
  const html = buildEmmdStandaloneHtml(content, bodyHtml, themeOverride)
  triggerDownload(new Blob([html], { type: 'text/html;charset=utf-8' }), ensureExt(filenameBase, '.html'))
}

/** Fallback when the PDF engine is unavailable: open the themed HTML in a hidden
 *  iframe and invoke the browser's print-to-PDF dialog. */
export function printEmmd(content: string, bodyHtml: string, themeOverride?: string): void {
  const html = buildEmmdStandaloneHtml(content, bodyHtml, themeOverride)
  const frame = document.createElement('iframe')
  Object.assign(frame.style, { position: 'fixed', right: '0', bottom: '0', width: '0', height: '0', border: '0' })
  document.body.appendChild(frame)
  const doc = frame.contentDocument
  if (!doc) { frame.remove(); return }
  doc.open(); doc.write(html); doc.close()
  setTimeout(() => {
    frame.contentWindow?.focus()
    frame.contentWindow?.print()
    setTimeout(() => frame.remove(), 1000)
  }, 300)
}

/** Generate a real PDF twin via the Snappdf engine and download it. Throws on failure. */
export async function downloadEmmdPdf(content: string, bodyHtml: string, filenameBase: string, themeOverride?: string): Promise<void> {
  const html = buildEmmdStandaloneHtml(content, bodyHtml, themeOverride)
  const form = new FormData()
  form.append('files', new File([html], `${filenameBase}.html`, { type: 'text/html' }))
  const res = await api.post('/pdf/html-to-pdf', form, {
    headers: { 'Content-Type': 'multipart/form-data' },
    responseType: 'blob',
  })
  triggerDownload(res.data as Blob, ensureExt(filenameBase, '.pdf'))
}
