import PhosphorIcon from '../components/PhosphorIcon'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import { useState, useRef, useCallback, useMemo, useEffect } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import ExpenseTracker from '../components/ExpenseTracker'
import ModuleVoiceChat from '../components/ModuleVoiceChat'
import { takeHandoff } from '../utils/fileHandoff'
import './DocAgent.css'

const API = import.meta.env.VITE_API_URL ??
  (typeof window !== 'undefined' && window.location.hostname !== 'localhost'
    ? window.location.origin
    : 'http://localhost:5220')

const ANALYSIS_TYPES = [
  { value: 'receipts',  label: 'Receipts & Expenses', icon: 'bi-receipt',       desc: 'Itemise and categorise receipts' },
  { value: 'bank',      label: 'Bank Statements',     icon: 'bi-bank',           desc: 'Summarise transactions & spending' },
  { value: 'invoices',  label: 'Invoices',            icon: 'bi-file-earmark-text', desc: 'Extract totals, due dates & actions' },
  { value: 'general',   label: 'General Docs',        icon: 'bi-file-earmark',   desc: 'Analyse any document or scan' },
]

const formatFileSize = (bytes: number) => {
  if (bytes >= 1_048_576) return `${(bytes / 1_048_576).toFixed(1)} MB`
  return `${Math.max(1, Math.round(bytes / 1024))} KB`
}

const normaliseReportTables = (markdown: string) => {
  const lines = markdown.split('\n')
  const out: string[] = []
  let i = 0

  while (i < lines.length) {
    const heading = lines[i]
    const isTotalsHeading = /^#{1,6}\s+Totals Grouped by Category\s*$/i.test(heading.trim())
      || /^Totals Grouped by Category\s*$/i.test(heading.trim())

    if (!isTotalsHeading) {
      out.push(heading)
      i += 1
      continue
    }

    out.push(heading)
    i += 1
    while (i < lines.length && !lines[i].trim()) {
      out.push(lines[i])
      i += 1
    }

    const preface: string[] = []
    const rows: Array<[string, string]> = []
    let grandTotal = ''

    while (i < lines.length) {
      const line = lines[i]
      const trimmed = line.trim()
      if (!trimmed) break
      if (/^#{1,6}\s+/.test(trimmed) || /^-{3,}$/.test(trimmed)) break

      const bulletMatch = trimmed.match(/^[-*•]\s+\*\*(.+?):\*\*\s*(.+)$/)
        ?? trimmed.match(/^[-*•]\s+(.+?):\s*(NZ\$?.+)$/i)
      const grandMatch = trimmed.match(/^\*\*?Grand Total(?: of Expenses)?:\*\*?\s*(.+)$/i)
        ?? trimmed.match(/^Grand Total(?: of Expenses)?:\s*(.+)$/i)

      if (bulletMatch) {
        rows.push([bulletMatch[1].trim(), bulletMatch[2].trim()])
        i += 1
        continue
      }
      if (grandMatch) {
        grandTotal = grandMatch[1].trim()
        i += 1
        continue
      }

      if (rows.length === 0) {
        preface.push(line)
        i += 1
        continue
      }

      break
    }

    if (rows.length > 0) {
      out.push(...preface)
      if (preface.length > 0 && preface.at(-1)?.trim()) out.push('')
      out.push('| Category | Total |')
      out.push('| --- | ---: |')
      rows.forEach(([category, total]) => out.push(`| ${category} | ${total} |`))
      if (grandTotal) out.push(`| **Grand Total** | **${grandTotal}** |`)
      continue
    }
  }

  return out.join('\n')
}

interface UploadedFile {
  id: string
  file: File
  preview?: string
}

export default function DocAgent() {
  const { stageProps } = useFieldView('doc-agent')

  const [view, setView] = useState<'analyse' | 'expenses'>('analyse')
  const [files, setFiles] = useState<UploadedFile[]>([])
  const [analysisType, setAnalysisType] = useState('receipts')
  const [dragging, setDragging] = useState(false)
  const [running, setRunning] = useState(false)
  const [progress, setProgress] = useState<string[]>([])
  const [result, setResult] = useState('')
  const [error, setError] = useState('')
  const [cloudUrl, setCloudUrl] = useState('')
  const [showCloud, setShowCloud] = useState(false)
  const inputRef = useRef<HTMLInputElement>(null)
  const resultRef = useRef<HTMLDivElement>(null)
  const selectedAnalysis = ANALYSIS_TYPES.find(t => t.value === analysisType) ?? ANALYSIS_TYPES[0]
  const formattedResult = normaliseReportTables(result)

  const ALLOWED_TYPES = [
    'image/jpeg','image/png','image/webp','image/gif',
    'application/pdf',
    'text/plain',
    'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
    'application/msword',
  ]

  // A file arriving from the free PDF tools ("Track as expense"): load it,
  // pre-select the receipts analysis, and let the user press Analyse. The
  // upload is not auto-run — an AI call the user did not ask for is a cost
  // and a surprise, in that order.
  useEffect(() => {
    const h = takeHandoff('expense')
    if (!h) return
    setAnalysisType('receipts')
    setFiles([{
      id: crypto.randomUUID(),
      file: new File([h.blob], h.name, { type: h.blob.type || 'application/pdf' }),
    }])
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const addFiles = (incoming: FileList | File[]) => {
    const arr = Array.from(incoming)
    const valid = arr.filter(f => ALLOWED_TYPES.includes(f.type) && f.size <= 104_857_600)
    const invalid = arr.length - valid.length
    if (invalid > 0) setError(`${invalid} file(s) skipped — only images and PDFs are supported.`)
    const next: UploadedFile[] = valid.slice(0, 10 - files.length).map(f => ({
      id: crypto.randomUUID(),
      file: f,
      preview: f.type.startsWith('image/') ? URL.createObjectURL(f) : undefined,
    }))
    setFiles(prev => [...prev, ...next].slice(0, 10))
  }

  const onDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    setDragging(false)
    addFiles(e.dataTransfer.files)
  }, [files])

  const removeFile = (id: string) => {
    setFiles(prev => {
      const f = prev.find(x => x.id === id)
      if (f?.preview) URL.revokeObjectURL(f.preview)
      return prev.filter(x => x.id !== id)
    })
  }

  const analyse = async () => {
    if (files.length === 0 && !cloudUrl.trim()) return
    setRunning(true)
    setProgress([])
    setResult('')
    setError('')

    const token = localStorage.getItem('authToken')
    const form = new FormData()
    form.append('analysisType', analysisType)
    files.forEach(f => form.append('files', f.file))
    if (cloudUrl.trim()) form.append('cloudUrl', cloudUrl.trim())

    try {
      const res = await fetch(`${API}/api/docagent/analyse`, {
        method: 'POST',
        headers: { Authorization: `Bearer ${token}` },
        body: form,
      })

      if (!res.ok) throw new Error(`Server error ${res.status}`)
      if (!res.body) throw new Error('No response body')

      const reader = res.body.getReader()
      const decoder = new TextDecoder()
      let buf = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buf += decoder.decode(value, { stream: true })
        const lines = buf.split('\n')
        buf = lines.pop() ?? ''

        for (const line of lines) {
          if (line.startsWith('event: ')) continue
          if (!line.startsWith('data: ')) continue
          try {
            const payload = JSON.parse(line.slice(6))
            if (payload.text) {
              setResult(prev => prev + payload.text)
              setTimeout(() => resultRef.current?.scrollTo({ top: resultRef.current.scrollHeight, behavior: 'smooth' }), 10)
            }
            if (payload.message) setProgress(prev => [...prev, payload.message])
            if (payload.message && line.includes('"error"')) setError(payload.message)
          } catch { /* malformed line */ }
        }
      }
    } catch (err: any) {
      setError(err.message ?? 'Analysis failed.')
    } finally {
      setRunning(false)
    }
  }

  const copyResult = () => {
    if (result) navigator.clipboard.writeText(result)
  }

  const exportFileName = (extension: 'html' | 'pdf') => {
    const stamp = new Date().toISOString().slice(0, 19).replace(/[:T]/g, '-')
    return `invoice-agent-analysis-${stamp}.${extension}`
  }

  const buildExportHtml = () => {
    const reportHtml = resultRef.current?.querySelector('.doc-result-text')?.innerHTML
    if (!reportHtml) return ''

    return `<!doctype html>
<html>
<head>
  <meta charset="utf-8" />
  <title>Invoice Agent Analysis</title>
  <style>
    body { margin: 0; padding: 32px; color: #0f172a; font-family: Inter, Arial, sans-serif; line-height: 1.65; background: #f8fafc; }
    main { max-width: 920px; margin: 0 auto; padding: 28px; border: 1px solid #dbe6f3; border-radius: 18px; background: #fff; }
    h1 { margin: 0 0 16px; padding-bottom: 10px; border-bottom: 2px solid #f1f5f9; font-size: 24px; }
    h2, h3 { margin: 24px 0 14px; padding: 12px 14px; border-radius: 12px; background: linear-gradient(135deg, #0f172a 0%, #42115f 58%, #c13584 100%); color: #fff; }
    h4 { margin: 18px 0 8px; color: #475569; }
    table { width: 100%; border-collapse: collapse; margin: 14px 0; font-size: 14px; }
    th { padding: 9px 12px; background: linear-gradient(135deg, #0f172a 0%, #42115f 58%, #c13584 100%); color: #fff; text-align: left; }
    td { padding: 9px 12px; border-bottom: 1px solid #e2e8f0; vertical-align: top; }
    tr:nth-child(even) td { background: #fafbfc; }
    code { padding: 2px 6px; border-radius: 5px; background: rgba(214,41,118,0.08); color: #c13584; }
    pre { overflow-x: auto; padding: 16px; border-radius: 12px; background: #0f172a; color: #fff; }
    blockquote { margin: 14px 0; padding: 10px 14px; border-left: 3px solid #d62976; background: rgba(214,41,118,0.06); }
    @media print {
      body { padding: 0; background: #fff; }
      main { max-width: none; border: none; border-radius: 0; }
      h2, h3, th { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
    }
  </style>
</head>
<body>
  <main>
    ${reportHtml}
  </main>
</body>
</html>`
  }

  const downloadHtml = () => {
    const html = buildExportHtml()
    if (!html) return
    const url = URL.createObjectURL(new Blob([html], { type: 'text/html;charset=utf-8' }))
    const link = document.createElement('a')
    link.href = url
    link.download = exportFileName('html')
    document.body.appendChild(link)
    link.click()
    link.remove()
    URL.revokeObjectURL(url)
  }

  const downloadPdf = () => {
    const html = buildExportHtml()
    if (!html) return
    const frame = document.createElement('iframe')
    frame.style.position = 'fixed'
    frame.style.right = '0'
    frame.style.bottom = '0'
    frame.style.width = '0'
    frame.style.height = '0'
    frame.style.border = '0'
    document.body.appendChild(frame)

    const doc = frame.contentDocument
    if (!doc) {
      frame.remove()
      return
    }

    doc.open()
    doc.write(html)
    doc.close()

    setTimeout(() => {
      frame.contentWindow?.focus()
      frame.contentWindow?.print()
      setTimeout(() => frame.remove(), 1000)
    }, 250)
  }

  // Gamefield: a reader desk per uploaded document, the analyst in the corner.
  const field = useMemo<FieldModel>(() => {
    const desks = files.map((f, i) => ({
      id: `doc-${i}`,
      name: f.file.name,
      role: analysisType,
      status: (result ? 'done' : running ? 'working' : 'idle') as 'done' | 'working' | 'idle',
      detail: running ? 'Reading the document…' : undefined,
    }))

    return {
      title: 'Invoice Agent',
      subtitle: files.length ? `${files.length} document${files.length === 1 ? '' : 's'} in the tray` : 'Drop documents in to open the floor.',
      running,
      agents: [
        ...desks,
        {
          id: 'analyst',
          name: 'Analyst',
          role: analysisType,
          status: (error ? 'failed' : running ? 'working' : result ? 'done' : 'idle') as 'failed' | 'working' | 'done' | 'idle',
          detail: error ? bubble(error) : bubble(progress[progress.length - 1]) || (result ? 'Analysis ready.' : undefined),
          output: result || undefined,
          boss: true,
        },
      ],
      messages: result ? desks.map(d => ({ id: `in-${d.id}`, from: d.id, to: 'analyst' })) : [],
      events: [
        ...progress.map((line, i) => ({ id: `p-${i}`, text: line, tone: 'info' as const })),
        ...(error ? [{ id: 'err', text: error, tone: 'bad' as const }] : []),
      ],
    }
  }, [files, running, result, error, progress, analysisType])

  // Keep the floor fully visible above the fold on shorter laptop displays,
  // while preserving the roomier presentation on tall desktop monitors.
  const fieldHeight = typeof window === 'undefined'
    ? 420
    : Math.min(520, Math.max(320, window.innerHeight - 360))

  return (
    <div className="doc-agent">
      <div className="doc-agent-header">
        <div className="doc-agent-title-group">
          <div className="doc-agent-header-icon"><PhosphorIcon className="bi bi-file-earmark-medical" /></div>
          <div>
            <span className="doc-agent-kicker">Private financial document analysis</span>
            <h1>Invoice Agent</h1>
            <p>Upload invoices, receipts, or bank statements. The AI extracts totals, due dates, expenses, and payment insights while keeping the workflow zero-storage.</p>
          </div>
        </div>
        {view === 'analyse' && (
          <div className="doc-agent-privacy-pill">
            <PhosphorIcon className="bi bi-shield-lock-fill" /> Zero storage · Processed in memory · Discarded immediately
          </div>
        )}
      </div>

      <div className="doc-view-tabs">
        <button className={`doc-view-tab${view === 'analyse' ? ' active' : ''}`} onClick={() => setView('analyse')}>
          <PhosphorIcon className="bi bi-cpu" /> Analyse documents
        </button>
        <button className={`doc-view-tab${view === 'expenses' ? ' active' : ''}`} onClick={() => setView('expenses')}>
          <PhosphorIcon className="bi bi-receipt" /> Expense tracker
        </button>
      </div>

      {view === 'expenses' && (
        <>
          {/* Ask about spending instead of reading the receipt list. */}
          <ModuleVoiceChat module="expenses" />
          <ExpenseTracker />
        </>
      )}

      {view === 'analyse' && <AgentStage {...stageProps} model={field} height={fieldHeight} />}

      <div className="doc-agent-body" style={view === 'expenses' ? { display: 'none' } : undefined}>
        {/* Left column — config */}
        <div className="doc-agent-config">
          {/* Analysis type */}
          <div className="doc-config-section">
            <label className="doc-config-label">Analysis type</label>
            <div className="doc-type-grid">
              {ANALYSIS_TYPES.map(t => (
                <button
                  key={t.value}
                  className={`doc-type-card${analysisType === t.value ? ' active' : ''}`}
                  onClick={() => setAnalysisType(t.value)}
                >
                  <PhosphorIcon className={`bi ${t.icon}`} />
                  <span>{t.label}</span>
                  <small>{t.desc}</small>
                </button>
              ))}
            </div>
          </div>

          <div className="doc-agent-summary-card">
            <div>
              <span>Selected workflow</span>
              <strong>{selectedAnalysis.label}</strong>
            </div>
            <PhosphorIcon className={`bi ${selectedAnalysis.icon}`} />
          </div>

          {/* Upload zone */}
          <div className="doc-config-section">
            <label className="doc-config-label">Documents <span>({files.length}/10)</span></label>
            <div
              className={`doc-drop-zone${dragging ? ' dragging' : ''}${files.length > 0 ? ' has-files' : ''}`}
              onDragOver={e => { e.preventDefault(); setDragging(true) }}
              onDragLeave={() => setDragging(false)}
              onDrop={onDrop}
              onClick={() => inputRef.current?.click()}
            >
              <input
                ref={inputRef}
                type="file"
                multiple
                accept="image/*,application/pdf,text/plain,.txt,.docx,application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                style={{ display: 'none' }}
                onChange={e => e.target.files && addFiles(e.target.files)}
              />
              {files.length === 0 ? (
                <>
                  <PhosphorIcon className="bi bi-cloud-upload doc-drop-icon" />
                  <p>Drag & drop files here or <span>browse</span></p>
                  <small>JPG · PNG · PDF · DOCX · TXT · max 100 MB each · up to 10 files</small>
                </>
              ) : (
                <p className="doc-drop-add"><PhosphorIcon className="bi bi-plus-circle" /> Add more files</p>
              )}
            </div>

            {files.length > 0 && (
              <ul className="doc-file-list">
                {files.map(f => (
                  <li key={f.id} className="doc-file-item">
                    {f.preview
                      ? <img src={f.preview} alt="" className="doc-file-thumb" />
                      : <span className="doc-file-pdf-icon">
                          <PhosphorIcon className={
                            f.file.type === 'application/pdf' ? 'bi bi-file-earmark-pdf' :
                            f.file.type === 'text/plain' ? 'bi bi-file-earmark-text' :
                            'bi bi-file-earmark-word'
                          } />
                        </span>
                    }
                    <span className="doc-file-name">{f.file.name}</span>
                    <span className="doc-file-size">{formatFileSize(f.file.size)}</span>
                    <button className="doc-file-remove" onClick={() => removeFile(f.id)}>
                      <PhosphorIcon className="bi bi-x" />
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>

          {/* Cloud URL import */}
          <div className="doc-config-section">
            <button
              className={`doc-cloud-toggle${showCloud ? ' active' : ''}`}
              onClick={() => setShowCloud(o => !o)}
            >
              <svg width="15" height="15" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg" style={{flexShrink:0}}>
                <path d="M12 2C8.13 2 5 5.13 5 9c0 2.38 1.19 4.47 3 5.74V17h8v-2.26C17.81 13.47 19 11.38 19 9c0-3.87-3.13-7-7-7z" fill="#4285F4"/>
                <path d="M9 21h6v-1H9v1zm0-2h6v-1H9v1z" fill="#34A853"/>
              </svg>
              <PhosphorIcon className="bi bi-cloud-arrow-up" />
              Import from Google Drive or OneDrive
              <PhosphorIcon className={`bi bi-chevron-${showCloud ? 'up' : 'down'} doc-cloud-chevron`} />
            </button>

            {showCloud && (
              <div className="doc-cloud-panel">
                <p className="doc-cloud-hint">
                  Paste a public share link — the document is fetched, read, and discarded. Never stored.
                </p>
                <div className="doc-cloud-providers">
                  <span className="doc-cloud-provider">
                    <svg width="14" height="14" viewBox="0 0 48 48"><path d="M15.1 35.3l-9.5-16.5 9-15.6L24 19.2z" fill="#0066DA"/><path d="M32.9 35.3H15.1L24 19.2l8.9-3.3z" fill="#00AC47"/><path d="M42.4 18.8l-9.5 16.5H15.1l8.9-15.6z" fill="#EA4335"/><path d="M24 3.2l9.4 15.9-8.5.1L15.6 3.2z" fill="#00832D"/><path d="M33.4 19.1l9 16.2H15.1L24 19.2z" fill="#2684FC"/><path d="M15.6 3.2l8.4 16-9 .1L6.1 3.2z" fill="#FFBA00"/></svg>
                    Google Drive
                  </span>
                  <span className="doc-cloud-provider">
                    <svg width="14" height="14" viewBox="0 0 24 24"><path d="M5.5 12.5L2 17h20l-3.5-4.5L14 18l-4-3z" fill="#0364B8"/><path d="M8.5 6L4 13.5h8l-2-5z" fill="#0078D4"/><path d="M8.5 6l6 .5 3 7H12z" fill="#1490DF"/><path d="M14.5 6.5l5 7H12z" fill="#28A8E8"/></svg>
                    OneDrive
                  </span>
                </div>
                <div className="doc-cloud-row">
                  <input
                    className="doc-cloud-input"
                    type="url"
                    placeholder="https://drive.google.com/file/d/... or https://1drv.ms/..."
                    value={cloudUrl}
                    onChange={e => setCloudUrl(e.target.value)}
                  />
                  {cloudUrl && (
                    <button className="doc-cloud-clear" onClick={() => setCloudUrl('')}>
                      <PhosphorIcon className="bi bi-x" />
                    </button>
                  )}
                </div>
                {cloudUrl && (
                  <div className="doc-cloud-ready">
                    <PhosphorIcon className="bi bi-check-circle-fill" /> Link ready — click Analyse to process
                  </div>
                )}
              </div>
            )}
          </div>

          {error && <div className="doc-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

          <button
            className="doc-analyse-btn"
            onClick={analyse}
            disabled={running || (files.length === 0 && !cloudUrl.trim())}
          >
            {running
              ? <><span className="doc-spinner" /> Analysing…</>
              : <><PhosphorIcon className="bi bi-cpu" /> Analyse Documents</>
            }
          </button>
        </div>

        {/* Right column — results */}
        <div className="doc-agent-result-pane">
          <div className="doc-result-overview">
            <div>
              <span>Ready to analyse</span>
              <strong>{files.length} / 10 documents</strong>
            </div>
            <div>
              <span>Mode</span>
              <strong>{selectedAnalysis.label}</strong>
            </div>
            <div>
              <span>Storage</span>
              <strong>Memory only</strong>
            </div>
          </div>

          {progress.length > 0 && (
            <div className="doc-progress-log">
              {progress.map((msg, i) => (
                <div key={i} className="doc-progress-item">
                  <PhosphorIcon className="bi bi-check-circle-fill" /> {msg}
                </div>
              ))}
              {running && <div className="doc-progress-item doc-progress-live"><span className="doc-spinner-sm" /> Processing…</div>}
            </div>
          )}

          {result ? (
            <div className="doc-result-wrap">
              <div className="doc-result-toolbar">
                <span className="doc-result-label"><PhosphorIcon className="bi bi-file-earmark-check" /> Analysis complete</span>
                <div className="doc-result-actions">
                  <button className="doc-copy-btn" onClick={downloadHtml}>
                    <PhosphorIcon className="bi bi-filetype-html" /> HTML
                  </button>
                  <button className="doc-copy-btn" onClick={downloadPdf}>
                    <PhosphorIcon className="bi bi-filetype-pdf" /> PDF
                  </button>
                  <button className="doc-copy-btn" onClick={copyResult}>
                    <PhosphorIcon className="bi bi-clipboard" /> Copy
                  </button>
                </div>
              </div>
              <div className="doc-result-body" ref={resultRef}>
                <div className="doc-result-text doc-md">
                  <ReactMarkdown
                    remarkPlugins={[remarkGfm]}
                    components={{
                      h1: ({children}) => <h1 className="doc-md-h1">{children}</h1>,
                      h2: ({children}) => <h2 className="doc-md-h2">{children}</h2>,
                      h3: ({children}) => <h3 className="doc-md-h3">{children}</h3>,
                      h4: ({children}) => <h4 className="doc-md-h4">{children}</h4>,
                      p:  ({children}) => <p  className="doc-md-p">{children}</p>,
                      ul: ({children}) => <ul className="doc-md-ul">{children}</ul>,
                      ol: ({children}) => <ol className="doc-md-ol">{children}</ol>,
                      li: ({children}) => <li className="doc-md-li">{children}</li>,
                      code: ({className, children}) => {
                        const isBlock = className?.includes('language-')
                        return isBlock
                          ? <code className="doc-md-code-block">{children}</code>
                          : <code className="doc-md-code">{children}</code>
                      },
                      pre:   ({children}) => <pre className="doc-md-pre">{children}</pre>,
                      table: ({children}) => <div className="doc-table-wrap"><table className="doc-md-table">{children}</table></div>,
                      th:    ({children}) => <th className="doc-md-th">{children}</th>,
                      td:    ({children}) => <td className="doc-md-td">{children}</td>,
                      blockquote: ({children}) => <blockquote className="doc-md-blockquote">{children}</blockquote>,
                      strong: ({children}) => <strong className="doc-md-strong">{children}</strong>,
                      em: ({children}) => <em className="doc-md-em">{children}</em>,
                      del: ({children}) => <del className="doc-md-del">{children}</del>,
                      a: ({href, children}) => (
                        <a className="doc-md-link" href={href} target="_blank" rel="noreferrer">
                          {children}
                        </a>
                      ),
                      input: props => <input {...props} className="doc-md-checkbox" disabled />,
                      hr: () => <hr className="doc-md-hr" />,
                    }}
                  >
                    {formattedResult}
                  </ReactMarkdown>
                </div>
              </div>
            </div>
          ) : (
            <div className="doc-result-empty">
              <div className="doc-empty-orbit">
                <PhosphorIcon className={`bi ${selectedAnalysis.icon} doc-empty-icon`} />
              </div>
              <p>Drop documents on the left, then run <strong>{selectedAnalysis.label}</strong>.</p>
              <p className="doc-empty-sub">Results stream here with extracted fields, summaries, and recommended next actions.</p>
              <div className="doc-empty-steps">
                <span><PhosphorIcon className="bi bi-upload" /> Upload</span>
                <span><PhosphorIcon className="bi bi-cpu" /> Analyse</span>
                <span><PhosphorIcon className="bi bi-clipboard-check" /> Copy result</span>
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
