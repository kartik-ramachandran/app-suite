import PhosphorIcon from '../components/PhosphorIcon'
import { AgentStage, useFieldView } from '../gamefield'
import type { FieldModel } from '../gamefield'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import ExportButtons from '../components/ExportButtons'
import './Prism.css'

const API = import.meta.env.VITE_API_URL ??
  (typeof window !== 'undefined' && window.location.hostname !== 'localhost'
    ? window.location.origin
    : 'http://localhost:5220')

type ToolKey =
  | 'profile'
  | 'map'
  | 'clean'
  | 'dedupe'
  | 'validate'
  | 'transform'
  | 'convert'
  | 'extract'

type AuxInput = 'schema' | 'rules' | 'instruction' | 'notes' | 'format' | 'text'

interface ToolInfo {
  key: ToolKey
  name: string
  icon: string
  cta: string
  description: string
  inputs: AuxInput[]
  fileOptional?: boolean
}

// Osmos-style autonomous data engineering: AI compiles the intent into
// deterministic operations the backend executes over the full file.
// Every run is queued as a background ModuleJob — the page polls for the
// result and the notification bell announces completion.
const TOOLS: ToolInfo[] = [
  { key: 'profile', name: 'Data Profiling', icon: 'bi-clipboard-data', cta: 'Profile data', inputs: [],
    description: 'Column types, completeness, quality score, issues found and recommended fixes.' },
  { key: 'map', name: 'AutoMap to Schema', icon: 'bi-signpost-split', cta: 'Map to schema', inputs: ['schema', 'notes'],
    description: 'Map a messy file onto your target schema — AI matches columns by meaning and reformats values.' },
  { key: 'clean', name: 'AutoClean', icon: 'bi-stars', cta: 'Auto-clean data', inputs: ['notes'],
    description: 'Detect and fix inconsistent dates, casing, whitespace, currency symbols and category labels.' },
  { key: 'dedupe', name: 'Deduplication', icon: 'bi-files', cta: 'Remove duplicates', inputs: ['notes'],
    description: 'AI picks the identity key, normalizes it, and removes duplicate records.' },
  { key: 'validate', name: 'Validation Rules', icon: 'bi-patch-check', cta: 'Validate data', inputs: ['rules'],
    description: 'Write rules in plain English — every row is checked and violations are reported.' },
  { key: 'transform', name: 'NL Transform', icon: 'bi-shuffle', cta: 'Transform data', inputs: ['instruction'],
    description: 'Describe the change — filter, rename, derive, split, sort — and it is applied to the whole file.' },
  { key: 'convert', name: 'Format Convert', icon: 'bi-arrow-repeat', cta: 'Convert file', inputs: ['format'],
    description: 'Convert between CSV, JSON and Excel (XLSX) with clean, consistent output.' },
  { key: 'extract', name: 'Text to Table', icon: 'bi-body-text', cta: 'Extract table', inputs: ['text'], fileOptional: true,
    description: 'Paste unstructured text — emails, reports, logs — and get back a structured table.' },
]

const dataAccept = '.csv,.tsv,.txt,.json,.xlsx,.pdf,text/csv,application/json,application/pdf,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet'

interface Metric { label: string; value: string; tone: string }
interface Section { heading: string; items: string[] }
interface Preview { headers: string[]; rows: string[][]; totalRows: number }
interface Download { fileName: string; contentType: string; base64: string }
interface PrismReport {
  headline: string
  metrics: Metric[]
  sections: Section[]
  preview?: Preview | null
  download?: Download | null
}
interface JobStatus {
  jobId: number
  tool: string
  title: string
  status: 'running' | 'completed' | 'failed'
  error?: string | null
  report?: PrismReport | null
  resultExpired?: boolean
  hasDownload?: boolean
}
interface JobListItem {
  jobId: number
  tool: string
  title: string
  status: 'running' | 'completed' | 'failed'
  error?: string | null
  createdAt: string
  durationMs: number
  hasResult: boolean
}
interface ToolResult { jobId: number; report: PrismReport; hasDownload: boolean }

const POLL_MS = 2000

const toolLabel = (key: string) => TOOLS.find(t => t.key === key)?.name ?? key

const timeAgo = (iso: string) => {
  const d = new Date(iso.endsWith('Z') || iso.includes('+') ? iso : iso + 'Z')
  const s = Math.max(1, Math.floor((Date.now() - d.getTime()) / 1000))
  if (s < 60) return `${s}s ago`
  const m = Math.floor(s / 60); if (m < 60) return `${m}m ago`
  const h = Math.floor(m / 60); if (h < 24) return `${h}h ago`
  return `${Math.floor(h / 24)}d ago`
}

const toneClass = (tone: string) => {
  const t = (tone || '').toLowerCase()
  if (t === 'good') return 'prism-pill prism-pill-good'
  if (t === 'warn') return 'prism-pill prism-pill-warn'
  if (t === 'bad') return 'prism-pill prism-pill-bad'
  return 'prism-pill prism-pill-neutral'
}


/** Flattens a Prism report into the plain text the Gamefield inspector shows. */
function flattenReport(r: PrismReport): string {
  return [r.headline, ...r.sections.map(sec => sec.heading + '\n' + sec.items.map(i => '- ' + i).join('\n'))].join('\n\n')
}

export default function Prism() {
  const { stageProps } = useFieldView('prism')

  const { toolName } = useParams<{ toolName?: string }>()
  const navigate = useNavigate()

  const activeTool = useMemo<ToolInfo>(
    () => TOOLS.find(t => t.key === toolName) ?? TOOLS[0],
    [toolName]
  )

  const [file, setFile] = useState<File | null>(null)
  const [isDragOver, setIsDragOver] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // Results and in-flight jobs are tracked per tool so several jobs can run at once.
  const [results, setResults] = useState<Partial<Record<ToolKey, ToolResult>>>({})
  const [running, setRunning] = useState<Partial<Record<ToolKey, number>>>({})
  const [jobs, setJobs] = useState<JobListItem[]>([])

  const [targetSchema, setTargetSchema] = useState('')
  const [rules, setRules] = useState('')
  const [instruction, setInstruction] = useState('')
  const [notes, setNotes] = useState('')
  const [format, setFormat] = useState('csv')
  const [rawText, setRawText] = useState('')
  const fileInputRef = useRef<HTMLInputElement>(null)
  const prismResultRef = useRef<HTMLDivElement>(null)
  const pollTimers = useRef<Map<number, ReturnType<typeof setInterval>>>(new Map())

  const result = results[activeTool.key] ?? null
  const busy = running[activeTool.key] !== undefined
  const has = (input: AuxInput) => activeTool.inputs.includes(input)

  const authHeaders = (): Record<string, string> => {
    const token = localStorage.getItem('authToken')
    return token ? { Authorization: `Bearer ${token}` } : {}
  }

  const readError = async (res: Response): Promise<string> => {
    try { const p = await res.json(); if (p?.error) return p.error } catch { /* ignore */ }
    return `Request failed (${res.status})`
  }

  const fetchJobs = async () => {
    try {
      const res = await fetch(`${API}/api/prism/jobs?take=30`, { headers: authHeaders() })
      if (!res.ok) return
      const data = await res.json() as { items: JobListItem[] }
      setJobs(data.items ?? [])
    } catch { /* ignore */ }
  }

  const stopPolling = (jobId: number) => {
    const timer = pollTimers.current.get(jobId)
    if (timer) { clearInterval(timer); pollTimers.current.delete(jobId) }
  }

  const applyFinishedJob = (tool: ToolKey, job: JobStatus) => {
    setRunning(prev => { const next = { ...prev }; delete next[tool]; return next })
    if (job.status === 'completed' && job.report) {
      setResults(prev => ({ ...prev, [tool]: { jobId: job.jobId, report: job.report!, hasDownload: !!job.hasDownload } }))
    } else if (job.status === 'failed') {
      setError(job.error || 'The job failed.')
    } else if (job.resultExpired) {
      setError('This job completed but its result has expired (results are kept for 24 hours).')
    }
    fetchJobs()
  }

  // Poll one job until it finishes. Safe to call for jobs already being polled.
  const pollJob = (tool: ToolKey, jobId: number) => {
    if (pollTimers.current.has(jobId)) return
    setRunning(prev => ({ ...prev, [tool]: jobId }))
    const timer = setInterval(async () => {
      try {
        const res = await fetch(`${API}/api/prism/job/${jobId}`, { headers: authHeaders() })
        if (!res.ok) {
          if (res.status === 401 || res.status === 404) {
            stopPolling(jobId)
            setRunning(prev => { const next = { ...prev }; delete next[tool]; return next })
          }
          return
        }
        const job = await res.json() as JobStatus
        if (job.status !== 'running') {
          stopPolling(jobId)
          applyFinishedJob(tool, job)
        }
      } catch { /* transient network error — keep polling */ }
    }, POLL_MS)
    pollTimers.current.set(jobId, timer)
  }

  // On mount: load the job panel and resume polling anything still running
  // (jobs keep executing server-side while the user is away).
  useEffect(() => {
    const timers = pollTimers.current
    ;(async () => {
      try {
        const res = await fetch(`${API}/api/prism/jobs?take=30`, { headers: authHeaders() })
        if (!res.ok) return
        const data = await res.json() as { items: JobListItem[] }
        setJobs(data.items ?? [])
        for (const j of data.items ?? []) {
          const tool = TOOLS.find(t => t.key === j.tool)?.key
          if (tool && j.status === 'running') pollJob(tool, j.jobId)
        }
      } catch { /* not logged in / offline */ }
    })()
    return () => {
      timers.forEach(t => clearInterval(t))
      timers.clear()
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const selectTool = (key: ToolKey) => {
    navigate(`/prism/${key}`)
    setError(null)
  }

  const acceptFile = (f: File | null) => {
    setFile(f)
    setError(null)
  }

  const handleRun = async () => {
    if (activeTool.key === 'extract') {
      if (!rawText.trim() && !file) { setError('Paste some text or upload a .txt file first.'); return }
    } else if (!file) {
      setError('Upload a data file first (CSV, TSV, JSON, or XLSX).'); return
    }
    if (activeTool.key === 'map' && !targetSchema.trim()) { setError('Enter the target schema — one column per line.'); return }
    if (activeTool.key === 'transform' && !instruction.trim()) { setError('Describe the transformation you want.'); return }

    setError(null)
    try {
      const form = new FormData()
      if (file) form.append('file', file)
      if (has('schema')) form.append('targetSchema', targetSchema.trim())
      if (has('rules') && rules.trim()) form.append('rules', rules.trim())
      if (has('instruction')) form.append('instruction', instruction.trim())
      if (has('notes') && notes.trim()) form.append('notes', notes.trim())
      if (has('format')) form.append('format', format)
      if (has('text') && rawText.trim()) form.append('text', rawText.trim())

      const res = await fetch(`${API}/api/prism/${activeTool.key}`, {
        method: 'POST',
        headers: authHeaders(),
        body: form,
      })
      if (!res.ok) throw new Error(await readError(res))
      const payload = await res.json() as { jobId: number }
      setResults(prev => { const next = { ...prev }; delete next[activeTool.key]; return next })
      pollJob(activeTool.key, payload.jobId)
      fetchJobs()
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Something went wrong.')
    }
  }

  const downloadResult = async (jobId: number) => {
    try {
      const res = await fetch(`${API}/api/prism/job/${jobId}/download`, { headers: authHeaders() })
      if (!res.ok) throw new Error(await readError(res))
      const disposition = res.headers.get('Content-Disposition') ?? ''
      const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)
      const fileName = match ? decodeURIComponent(match[1]) : 'prism-output'
      const blob = await res.blob()
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url
      a.download = fileName
      a.click()
      URL.revokeObjectURL(url)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Download failed.')
    }
  }

  // Load a finished job from the panel into its tool's workspace.
  const openJob = async (item: JobListItem) => {
    const tool = TOOLS.find(t => t.key === item.tool)?.key
    if (!tool) return
    if (item.status === 'running') { pollJob(tool, item.jobId); navigate(`/prism/${tool}`); return }
    if (item.status === 'failed') { setError(item.error || 'The job failed.'); navigate(`/prism/${tool}`); return }
    try {
      const res = await fetch(`${API}/api/prism/job/${item.jobId}`, { headers: authHeaders() })
      if (!res.ok) return
      const job = await res.json() as JobStatus
      if (job.report) {
        setResults(prev => ({ ...prev, [tool]: { jobId: job.jobId, report: job.report!, hasDownload: !!job.hasDownload } }))
        setError(null)
      } else {
        setError('This job\'s result has expired (results are kept for 24 hours). Run it again.')
      }
      navigate(`/prism/${tool}`)
    } catch { /* ignore */ }
  }

  const statusIcon = (status: string) =>
    status === 'running' ? 'bi-arrow-repeat prism-spin'
      : status === 'completed' ? 'bi-check-circle-fill'
      : 'bi-x-circle-fill'


  // Gamefield: every Prism job is a desk on the processing floor, with the
  // dispatcher in the corner office holding the tool you have open.
  const field = useMemo<FieldModel>(() => {
    const recent = jobs.slice(0, 9)
    const runningTools = Object.keys(running) as ToolKey[]

    const desks = recent.map(j => {
      const res = results[j.tool as ToolKey]
      return {
        id: `job-${j.jobId}`,
        name: j.title || toolLabel(j.tool),
        role: `${toolLabel(j.tool)}${j.durationMs > 0 ? ` · ${(j.durationMs / 1000).toFixed(1)}s` : ''}`,
        status: (j.status === 'running' ? 'working' : j.status === 'failed' ? 'failed' : 'done') as 'working' | 'failed' | 'done',
        detail: j.error || (res?.report.headline ?? undefined),
        output: res?.report ? flattenReport(res.report) : j.error || undefined,
      }
    })

    return {
      title: 'Prism',
      subtitle: activeTool.name,
      running: runningTools.length > 0,
      agents: [
        ...desks,
        {
          id: 'dispatch',
          name: 'Dispatch',
          role: runningTools.length ? `${runningTools.length} in flight` : 'idle',
          status: (error ? 'failed' : runningTools.length ? 'working' : 'idle') as 'failed' | 'working' | 'idle',
          detail: error || (runningTools.length ? 'Feeding jobs to the floor…' : undefined),
          boss: true,
        },
      ],
      messages: desks.filter(d => d.status !== 'working').map(d => ({ id: `back-${d.id}`, from: d.id, to: 'dispatch' })),
      events: recent.map(j => ({
        id: `ev-${j.jobId}`,
        text: `${toolLabel(j.tool)} — ${j.status}`,
        tone: (j.status === 'failed' ? 'bad' : j.status === 'completed' ? 'good' : 'info') as 'bad' | 'good' | 'info',
      })),
    }
  }, [jobs, running, results, activeTool, error])

  return (
    <div className="prism-page">
      {/* ── Sidebar ── */}
      <aside className="prism-sidebar">
        <div className="prism-sidebar-head">
          <span className="prism-kicker">Prism</span>
          <p>Autonomous data engineering — turn messy files into clean, schema-ready data.</p>
        </div>
        <nav className="prism-tool-list">
          {TOOLS.map(tool => (
            <button
              key={tool.key}
              className={`prism-tool-item${tool.key === activeTool.key ? ' prism-tool-active' : ''}`}
              onClick={() => selectTool(tool.key)}
            >
              <PhosphorIcon className={`bi ${tool.icon}`} />
              <span className="prism-tool-name">{tool.name}</span>
              {running[tool.key] !== undefined
                ? <PhosphorIcon className="bi bi-arrow-repeat prism-spin prism-tool-running" />
                : results[tool.key] && <PhosphorIcon className="bi bi-check-circle-fill prism-tool-done" />}
            </button>
          ))}
        </nav>
      </aside>

      {/* ── Workspace ── */}
      <main className="prism-workspace">
        <header className="prism-header">
          <div className="prism-header-icon"><PhosphorIcon className={`bi ${activeTool.icon}`} /></div>
          <div>
            <h1>{activeTool.name}</h1>
            <p>{activeTool.description}</p>
          </div>
        </header>

        <AgentStage {...stageProps} model={field} />

        <div className="prism-panel">
          {/* Text input replaces the dropzone as the primary input for extract */}
          {has('text') && (
            <>
              <textarea
                className="prism-textarea"
                rows={8}
                placeholder={'Paste unstructured text here — an email thread, a report, meeting notes, a pasted table…'}
                value={rawText}
                onChange={e => setRawText(e.target.value)}
              />
              <div className="prism-or"><span>or upload a text file</span></div>
            </>
          )}

          <div
            className={`prism-dropzone${isDragOver ? ' prism-dropzone-over' : ''}`}
            onDragOver={e => { e.preventDefault(); setIsDragOver(true) }}
            onDragLeave={() => setIsDragOver(false)}
            onDrop={e => {
              e.preventDefault(); setIsDragOver(false)
              if (e.dataTransfer.files?.[0]) acceptFile(e.dataTransfer.files[0])
            }}
            onClick={() => fileInputRef.current?.click()}
          >
            <input
              ref={fileInputRef}
              type="file"
              accept={activeTool.key === 'extract' ? '.txt,.md,.pdf,text/plain,application/pdf' : dataAccept}
              hidden
              onChange={e => acceptFile(e.target.files?.[0] ?? null)}
            />
            <PhosphorIcon className="bi bi-cloud-arrow-up" />
            {file ? (
              <div className="prism-file-chip" onClick={e => e.stopPropagation()}>
                <PhosphorIcon className="bi bi-file-earmark-spreadsheet" />
                <span>{file.name}</span>
                <button onClick={() => acceptFile(null)} aria-label="Remove file"><PhosphorIcon className="bi bi-x" /></button>
              </div>
            ) : (
              <>
                <p className="prism-drop-title">
                  {activeTool.fileOptional ? 'Optional: drag & drop a text file' : 'Drag & drop a data file, or click to browse'}
                </p>
                <p className="prism-drop-sub">
                  {activeTool.key === 'extract' ? 'TXT, Markdown, or PDF' : 'CSV, TSV, JSON, Excel (XLSX), or PDF — up to 200 MB'}
                </p>
              </>
            )}
          </div>

          {has('schema') && (
            <div className="prism-field">
              <label>Target schema <span className="prism-req">required</span></label>
              <textarea
                className="prism-textarea"
                rows={6}
                placeholder={'One target column per line, descriptions optional:\nfirst_name\nlast_name\nemail: primary contact email\nsignup_date: ISO date'}
                value={targetSchema}
                onChange={e => setTargetSchema(e.target.value)}
              />
            </div>
          )}

          {has('rules') && (
            <div className="prism-field">
              <label>Validation rules <span className="prism-opt">plain English — leave empty to let AI propose checks</span></label>
              <textarea
                className="prism-textarea"
                rows={5}
                placeholder={'One rule per line:\nemail must be a valid email and unique\namount must be a number between 0 and 10000\nstatus must be one of active, churned, trial'}
                value={rules}
                onChange={e => setRules(e.target.value)}
              />
            </div>
          )}

          {has('instruction') && (
            <div className="prism-field">
              <label>What should change? <span className="prism-req">required</span></label>
              <textarea
                className="prism-textarea"
                rows={4}
                placeholder={'e.g. Keep only rows where country is New Zealand, combine first and last name into full_name, and sort by amount descending'}
                value={instruction}
                onChange={e => setInstruction(e.target.value)}
              />
            </div>
          )}

          {has('notes') && (
            <div className="prism-field">
              <label>Extra instructions <span className="prism-opt">optional</span></label>
              <input
                className="prism-input"
                type="text"
                placeholder={activeTool.key === 'dedupe' ? 'e.g. treat rows with the same email as duplicates' : 'e.g. dates should be YYYY-MM-DD, phone numbers NZ format'}
                value={notes}
                onChange={e => setNotes(e.target.value)}
              />
            </div>
          )}

          {has('format') && (
            <div className="prism-field">
              <label>Output format</label>
              <div className="prism-format-row">
                {(['csv', 'json', 'xlsx'] as const).map(f => (
                  <button
                    key={f}
                    type="button"
                    className={`prism-format-btn${format === f ? ' prism-format-active' : ''}`}
                    onClick={() => setFormat(f)}
                  >
                    {f.toUpperCase()}
                  </button>
                ))}
              </div>
            </div>
          )}

          {error && <div className="prism-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

          <button className="prism-run" onClick={handleRun} disabled={busy}>
            {busy
              ? <><PhosphorIcon className="bi bi-arrow-repeat prism-spin" /> Running in background…</>
              : <><PhosphorIcon className={`bi ${activeTool.icon}`} /> {activeTool.cta}</>}
          </button>

          {busy && (
            <p className="prism-async-note">
              <PhosphorIcon className="bi bi-info-circle" /> This runs as a background job — you can switch tools, queue more work,
              or leave this page. The notification bell will tell you when it finishes.
            </p>
          )}

          {/* Results */}
          {result && (
            <div className="prism-results-block">
              <div className="prism-results-toolbar" style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 10 }}>
                <ExportButtons
                  title={result.report.headline || activeTool.name}
                  filenameBase={`prism-${activeTool.key}`}
                  subtitle={`GenPrompt Prism · ${activeTool.name}`}
                  getBodyHtml={() => prismResultRef.current?.innerHTML}
                  size="sm"
                />
              </div>
              <div className="prism-results" ref={prismResultRef}>
              {result.report.headline && <div className="prism-headline">{result.report.headline}</div>}

              {result.report.metrics?.length > 0 && (
                <div className="prism-result-meta">
                  {result.report.metrics.map((m, i) => (
                    <span key={i} className={toneClass(m.tone)}>{m.label}: {m.value}</span>
                  ))}
                </div>
              )}

              {result.hasDownload && result.report.download && (
                <button className="prism-download" onClick={() => downloadResult(result.jobId)}>
                  <PhosphorIcon className="bi bi-download" /> Download {result.report.download.fileName}
                </button>
              )}

              {result.report.preview && result.report.preview.headers.length > 0 && (
                <section className="prism-card">
                  <h3><PhosphorIcon className="bi bi-table" /> Preview
                    <span className="prism-preview-note">
                      {result.report.preview.rows.length < result.report.preview.totalRows
                        ? `first ${result.report.preview.rows.length} of ${result.report.preview.totalRows.toLocaleString()} rows`
                        : `${result.report.preview.totalRows.toLocaleString()} rows`}
                    </span>
                  </h3>
                  <div className="prism-table-wrap">
                    <table className="prism-table">
                      <thead>
                        <tr>{result.report.preview.headers.map((h, i) => <th key={i}>{h}</th>)}</tr>
                      </thead>
                      <tbody>
                        {result.report.preview.rows.map((row, r) => (
                          <tr key={r}>{row.map((cell, c) => <td key={c}>{cell}</td>)}</tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </section>
              )}

              {result.report.sections?.map((s, i) => (
                <section key={i} className="prism-card">
                  <h3>{s.heading}</h3>
                  {s.items?.length > 0
                    ? <ul>{s.items.map((it, j) => <li key={j}>{it}</li>)}</ul>
                    : <p className="prism-muted">Nothing to report.</p>}
                </section>
              ))}
              </div>
            </div>
          )}
        </div>
      </main>

      {/* ── Jobs panel ── */}
      <aside className="prism-jobs">
        <div className="prism-jobs-head">
          <span>Jobs</span>
          <button className="prism-jobs-refresh" onClick={fetchJobs} title="Refresh"><PhosphorIcon className="bi bi-arrow-clockwise" /></button>
        </div>
        <div className="prism-jobs-list">
          {jobs.length === 0 ? (
            <div className="prism-jobs-empty"><PhosphorIcon className="bi bi-clock-history" /><p>No jobs yet</p></div>
          ) : jobs.map(j => (
            <div
              key={j.jobId}
              className={`prism-job-item prism-job-${j.status}`}
              onClick={() => openJob(j)}
              role="button"
              tabIndex={0}
            >
              <div className="prism-job-title">
                <PhosphorIcon className={`bi ${statusIcon(j.status)}`} /> {j.title}
              </div>
              <div className="prism-job-meta">
                {toolLabel(j.tool)} · {timeAgo(j.createdAt)}
                {j.status === 'completed' && j.durationMs > 0 && ` · ${(j.durationMs / 1000).toFixed(1)}s`}
                {j.status === 'running' && ' · running…'}
                {j.status === 'failed' && ' · failed'}
              </div>
            </div>
          ))}
        </div>
      </aside>
    </div>
  )
}
