import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import PhosphorIcon from '../components/PhosphorIcon'
import { useLayoutControls } from '../components/Layout'
import api from '../services/api'
import './VideoStudio.css'

interface StyleOpt { id: string; label: string; blurb: string }
interface PaletteOpt { id: string; label: string; primary: string; accent: string }
interface VoiceOpt { id: string; label: string; vibe: string; gender: string }
interface Options {
  styles: StyleOpt[]
  palettes: PaletteOpt[]
  voices: VoiceOpt[]
  lengths: number[]
  narrationStyles: string[]
  narrationAvailable: boolean
  aiAvailable: boolean
  dailyCap: number
}
interface Inspect { fileName: string; pageCount: number; imageCount: number; wordCount: number; preview: string }
interface Job {
  id: string; title: string; status: string; error?: string | null
  sceneCount: number; scenesDone: number; durationSeconds: number
  createdAt?: string | null; hasAudio: boolean
}
interface InkDoc { id: number; title?: string; updatedAt?: string }

type Source = 'document' | 'saved' | 'prompt'

const STATUS_LABEL: Record<string, string> = {
  queued: 'Queued',
  scripting: 'Writing the script',
  narrating: 'Recording narration',
  rendering: 'Rendering scenes',
  assembling: 'Assembling the video',
  done: 'Ready',
  failed: 'Failed',
}

const NARRATION_LABEL: Record<string, string> = {
  explainer: 'Friendly explainer',
  storyteller: 'Storyteller',
  energetic: 'Energetic presenter',
  calm: 'Calm and measured',
}

const fmtLength = (s: number) => (s < 60 ? `${s}s` : s % 60 === 0 ? `${s / 60} min` : `${Math.floor(s / 60)}m ${s % 60}s`)
const fmtDuration = (s: number) => `${Math.floor(s / 60)}:${String(Math.round(s % 60)).padStart(2, '0')}`

const apiError = (e: unknown, fallback: string) => {
  const d = (e as { response?: { data?: { error?: unknown } } })?.response?.data
  return typeof d?.error === 'string' ? d.error : fallback
}

/** Swatch that previews a slide style in the chosen palette — no network, pure CSS. */
function StylePreview({ styleId, primary, accent }: { styleId: string; primary: string; accent: string }) {
  return (
    <div className={`vs-preview vs-preview-${styleId}`} style={{ ['--p' as string]: primary, ['--a' as string]: accent }}>
      <span className="vs-pv-title" />
      <span className="vs-pv-rule" />
      <span className="vs-pv-line" />
      <span className="vs-pv-line short" />
      <span className="vs-pv-shape" />
    </div>
  )
}

export default function VideoStudio() {
  const { setBreadcrumbs } = useLayoutControls()
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'InkReel', path: '/ink' }, { label: 'Video Studio' }])
    return () => setBreadcrumbs([])
  }, [])

  const [opts, setOpts] = useState<Options | null>(null)
  const [source, setSource] = useState<Source>('document')

  // Document source
  const [file, setFile] = useState<File | null>(null)
  const [inspect, setInspect] = useState<Inspect | null>(null)
  const [inspecting, setInspecting] = useState(false)
  const [fromPage, setFromPage] = useState(1)
  const [toPage, setToPage] = useState(1)
  const fileRef = useRef<HTMLInputElement>(null)

  // Saved-doc and prompt sources
  const [docs, setDocs] = useState<InkDoc[]>([])
  const [docId, setDocId] = useState<number | null>(null)
  const [prompt, setPrompt] = useState('')

  // Settings
  const [title, setTitle] = useState('')
  const [length, setLength] = useState(120)
  const [styleId, setStyleId] = useState('white-on-navy')
  const [paletteId, setPaletteId] = useState('trust-blue')
  const [customPrimary, setCustomPrimary] = useState('#1d4ed8')
  const [customAccent, setCustomAccent] = useState('#38bdf8')
  const [logo, setLogo] = useState<File | null>(null)
  const [logoUrl, setLogoUrl] = useState<string | null>(null)
  const logoRef = useRef<HTMLInputElement>(null)
  const [voiceId, setVoiceId] = useState('friendly-male')
  const [narration, setNarration] = useState('explainer')
  const [audience, setAudience] = useState('General audience')

  // Jobs
  const [jobs, setJobs] = useState<Job[]>([])
  const [creating, setCreating] = useState(false)
  const [error, setError] = useState('')
  const [playing, setPlaying] = useState<{ id: string; url: string } | null>(null)

  const loadJobs = async () => {
    try { setJobs((await api.get('/video')).data ?? []) } catch { /* ignore */ }
  }

  useEffect(() => {
    api.get('/video/options').then(r => setOpts(r.data)).catch(() => setError('Could not load the video options.'))
    api.get('/ink/docs').then(r => setDocs(r.data ?? [])).catch(() => {})
    void loadJobs()
  }, [])

  // Poll while anything is in flight.
  const inFlight = jobs.some(j => j.status !== 'done' && j.status !== 'failed')
  useEffect(() => {
    if (!inFlight) return
    const t = setInterval(() => { void loadJobs() }, 2500)
    return () => clearInterval(t)
  }, [inFlight])

  // Logo preview URL lifecycle
  useEffect(() => {
    if (!logo) { setLogoUrl(null); return }
    const url = URL.createObjectURL(logo)
    setLogoUrl(url)
    return () => URL.revokeObjectURL(url)
  }, [logo])

  // Revoke the player's blob URL when it changes.
  useEffect(() => () => { if (playing) URL.revokeObjectURL(playing.url) }, [playing])

  const palette = paletteId === 'custom'
    ? { primary: customPrimary, accent: customAccent }
    : opts?.palettes.find(p => p.id === paletteId) ?? { primary: '#1d4ed8', accent: '#38bdf8' }

  const onFile = async (f: File | null) => {
    setFile(f); setInspect(null); setError('')
    if (!f) return
    setInspecting(true)
    try {
      const fd = new FormData()
      fd.append('file', f)
      const { data } = await api.post<Inspect>('/video/inspect', fd)
      setInspect(data)
      setFromPage(1)
      setToPage(data.pageCount)
      if (!title.trim()) setTitle(f.name.replace(/\.[^.]+$/, ''))
    } catch (e) {
      setError(apiError(e, 'That document could not be read.'))
      setFile(null)
    } finally {
      setInspecting(false)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  const ready =
    (source === 'document' && !!file && !!inspect) ||
    (source === 'saved' && docId != null) ||
    (source === 'prompt' && prompt.trim().length >= 40)

  const create = async () => {
    if (!ready || creating) return
    setCreating(true); setError('')
    try {
      const fd = new FormData()
      if (source === 'document' && file) {
        fd.append('file', file)
        if (inspect && inspect.pageCount > 1) {
          fd.append('fromPage', String(fromPage))
          fd.append('toPage', String(toPage))
        }
      } else if (source === 'saved' && docId != null) {
        fd.append('docId', String(docId))
      } else {
        fd.append('prompt', prompt.trim())
      }
      if (title.trim()) fd.append('title', title.trim())
      fd.append('lengthSeconds', String(length))
      fd.append('styleId', styleId)
      fd.append('paletteId', paletteId)
      if (paletteId === 'custom') {
        fd.append('customPrimary', customPrimary)
        fd.append('customAccent', customAccent)
      }
      if (logo) fd.append('logo', logo)
      fd.append('voiceId', voiceId)
      fd.append('narrationStyle', narration)
      fd.append('audience', audience.trim() || 'General audience')

      await api.post('/video', fd, { timeout: 120000 })
      await loadJobs()
      window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' })
    } catch (e) {
      setError(apiError(e, 'Could not start the video — please try again.'))
    } finally {
      setCreating(false)
    }
  }

  /** Fetch through the authed client (the <video> tag can't send a bearer token). */
  const play = async (job: Job) => {
    try {
      const res = await api.get(`/video/${job.id}/file`, { responseType: 'blob', timeout: 300000 })
      setPlaying({ id: job.id, url: URL.createObjectURL(res.data as Blob) })
    } catch (e) {
      setError(apiError(e, 'Could not load that video.'))
    }
  }

  const download = async (job: Job) => {
    try {
      const res = await api.get(`/video/${job.id}/file`, { responseType: 'blob', timeout: 300000 })
      const url = URL.createObjectURL(res.data as Blob)
      const a = document.createElement('a')
      a.href = url
      a.download = `${(job.title || 'video').replace(/[^\w\- ]+/g, '').trim() || 'video'}.mp4`
      document.body.appendChild(a); a.click(); a.remove()
      setTimeout(() => URL.revokeObjectURL(url), 5000)
    } catch (e) {
      setError(apiError(e, 'Could not download that video.'))
    }
  }

  const remove = async (job: Job) => {
    if (!confirm(`Delete "${job.title}"?`)) return
    try {
      await api.delete(`/video/${job.id}`)
      if (playing?.id === job.id) setPlaying(null)
      setJobs(j => j.filter(x => x.id !== job.id))
    } catch (e) {
      setError(apiError(e, 'Could not delete that video.'))
    }
  }

  const pages = inspect?.pageCount ?? 1
  const selectedPages = Math.max(0, toPage - fromPage + 1)

  return (
    <div className="vs-page">
      <header className="vs-hero">
        <div className="vs-eyebrow"><PhosphorIcon className="bi bi-camera-video-fill" /> Video Studio</div>
        <h1>Turn any document into a <em>narrated video.</em></h1>
        <p>Upload a PDF or Word file — its own figures land on the slides that discuss them — or use a saved InkReel document, or just describe what you want.</p>
      </header>

      {opts && !opts.narrationAvailable && (
        <div className="vs-notice">
          <PhosphorIcon className="bi bi-info-circle" />
          Narration isn't configured on this server (AzureSpeech:SubscriptionKey), so videos render as timed slides without a voice-over.
        </div>
      )}
      {error && <div className="vs-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      <div className="vs-grid">
        {/* ── Left: source ── */}
        <section className="vs-card vs-source">
          <h2><span className="vs-step">1</span> Source</h2>
          <div className="vs-tabs" role="tablist">
            {([
              ['document', 'bi-file-earmark-pdf', 'Upload a document'],
              ['saved', 'bi-journal-richtext', 'Saved InkReel doc'],
              ['prompt', 'bi-chat-left-text', 'Describe it'],
            ] as [Source, string, string][]).map(([id, icon, label]) => (
              <button key={id} type="button" role="tab" aria-selected={source === id}
                className={source === id ? 'active' : ''} onClick={() => { setSource(id); setError('') }}>
                <PhosphorIcon className={`bi ${icon}`} /> {label}
              </button>
            ))}
          </div>

          {source === 'document' && (
            <>
              <button type="button" className={`vs-drop${file ? ' has-file' : ''}`} onClick={() => fileRef.current?.click()}
                onDragOver={e => e.preventDefault()}
                onDrop={e => { e.preventDefault(); void onFile(e.dataTransfer.files?.[0] ?? null) }}>
                {inspecting ? (
                  <><span className="vs-spin" /> Reading the document…</>
                ) : file && inspect ? (
                  <span className="vs-file">
                    <PhosphorIcon className="bi bi-file-earmark-text" />
                    <span>
                      <strong>{inspect.fileName}</strong>
                      <small>{inspect.pageCount} page{inspect.pageCount === 1 ? '' : 's'} · {inspect.wordCount.toLocaleString()} words · {inspect.imageCount} image{inspect.imageCount === 1 ? '' : 's'} found</small>
                    </span>
                  </span>
                ) : (
                  <span className="vs-drop-empty">
                    <PhosphorIcon className="bi bi-cloud-arrow-up" />
                    <strong>Drop a PDF, DOCX, MD or TXT here</strong>
                    <small>or click to choose · up to 40 MB</small>
                  </span>
                )}
              </button>
              <input ref={fileRef} type="file" hidden accept=".pdf,.docx,.md,.txt" onChange={e => void onFile(e.target.files?.[0] ?? null)} />

              {inspect && pages > 1 && (
                <div className="vs-range">
                  <label className="vs-label">Pages to include <span>{selectedPages} of {pages} selected</span></label>
                  <div className="vs-range-row">
                    <label>From <input type="number" min={1} max={toPage} value={fromPage}
                      onChange={e => setFromPage(Math.min(Math.max(1, Number(e.target.value) || 1), toPage))} /></label>
                    <label>To <input type="number" min={fromPage} max={pages} value={toPage}
                      onChange={e => setToPage(Math.max(Math.min(pages, Number(e.target.value) || pages), fromPage))} /></label>
                    <button type="button" className="vs-chip" onClick={() => { setFromPage(1); setToPage(pages) }}>All pages</button>
                  </div>
                </div>
              )}
              {inspect && inspect.preview && <p className="vs-preview-text">{inspect.preview}</p>}
            </>
          )}

          {source === 'saved' && (
            docs.length === 0 ? (
              <p className="vs-muted">No saved InkReel documents yet — write one in <strong>Documents</strong> and save it.</p>
            ) : (
              <div className="vs-doclist">
                {docs.map(d => (
                  <button key={d.id} type="button" className={`vs-doc${docId === d.id ? ' active' : ''}`}
                    onClick={() => { setDocId(d.id); if (!title.trim()) setTitle(d.title || '') }}>
                    <PhosphorIcon className="bi bi-file-earmark-richtext" />
                    <span><strong>{d.title || 'Untitled document'}</strong>
                      <small>{d.updatedAt ? new Date(d.updatedAt).toLocaleDateString() : ''}</small></span>
                    {docId === d.id && <PhosphorIcon className="bi bi-check-lg vs-doc-check" />}
                  </button>
                ))}
              </div>
            )
          )}

          {source === 'prompt' && (
            <textarea className="vs-prompt" rows={7} value={prompt} onChange={e => setPrompt(e.target.value)}
              placeholder="Describe the video — the topic, the key points, who it's for. At least a few sentences." />
          )}

          <label className="vs-label">Video title</label>
          <input className="vs-input" value={title} onChange={e => setTitle(e.target.value)} placeholder="Defaults to the document name" />

          <label className="vs-label">Audience</label>
          <input className="vs-input" value={audience} onChange={e => setAudience(e.target.value)} placeholder="e.g. New starters, executives, Year 10 students" />
        </section>

        {/* ── Right: settings ── */}
        <section className="vs-card vs-settings">
          <h2><span className="vs-step">2</span> Settings</h2>

          <label className="vs-label">Length</label>
          <div className="vs-chips">
            {(opts?.lengths ?? [30, 60, 90, 120, 180, 240]).map(l => (
              <button key={l} type="button" className={`vs-chip${length === l ? ' active' : ''}`} onClick={() => setLength(l)}>{fmtLength(l)}</button>
            ))}
          </div>

          <label className="vs-label">Slide style</label>
          <div className="vs-styles">
            {(opts?.styles ?? []).map(s => (
              <button key={s.id} type="button" className={`vs-style${styleId === s.id ? ' active' : ''}`} onClick={() => setStyleId(s.id)} title={s.blurb}>
                <StylePreview styleId={s.id} primary={palette.primary} accent={palette.accent} />
                <span>{s.label}</span>
              </button>
            ))}
          </div>

          <label className="vs-label">Brand colours</label>
          <div className="vs-palettes">
            {(opts?.palettes ?? []).map(p => (
              <button key={p.id} type="button" className={`vs-palette${paletteId === p.id ? ' active' : ''}`} onClick={() => setPaletteId(p.id)} title={p.label}>
                <span className="vs-dot" style={{ background: p.primary }} />
                <span className="vs-dot" style={{ background: p.accent }} />
                <small>{p.label}</small>
              </button>
            ))}
            <button type="button" className={`vs-palette${paletteId === 'custom' ? ' active' : ''}`} onClick={() => setPaletteId('custom')}>
              <span className="vs-dot vs-dot-rainbow" />
              <small>Custom</small>
            </button>
          </div>
          {paletteId === 'custom' && (
            <div className="vs-custom">
              <label>Primary <input type="color" value={customPrimary} onChange={e => setCustomPrimary(e.target.value)} /></label>
              <label>Accent <input type="color" value={customAccent} onChange={e => setCustomAccent(e.target.value)} /></label>
            </div>
          )}

          <label className="vs-label">Logo <span>top-left of every slide</span></label>
          <div className="vs-logo-row">
            <button type="button" className="vs-logo" onClick={() => logoRef.current?.click()}>
              {logoUrl ? <img src={logoUrl} alt="Logo preview" /> : <><PhosphorIcon className="bi bi-image" /> Upload logo</>}
            </button>
            {logo && <button type="button" className="vs-chip" onClick={() => setLogo(null)}>Remove</button>}
            <small className="vs-muted">PNG or WebP with transparency works best · max 2 MB</small>
          </div>
          <input ref={logoRef} type="file" hidden accept=".png,.webp,.jpg,.jpeg" onChange={e => {
            const f = e.target.files?.[0] ?? null
            if (f && f.size > 2 * 1024 * 1024) { setError('The logo must be 2 MB or smaller.'); return }
            setLogo(f)
            if (logoRef.current) logoRef.current.value = ''
          }} />

          <label className="vs-label">Voice {opts && !opts.narrationAvailable && <span>(not configured)</span>}</label>
          <div className="vs-voices">
            {(opts?.voices ?? []).map(v => (
              <button key={v.id} type="button" disabled={opts ? !opts.narrationAvailable : false}
                className={`vs-voice${voiceId === v.id ? ' active' : ''}`} onClick={() => setVoiceId(v.id)}>
                <PhosphorIcon className={`bi ${v.gender === 'female' ? 'bi-person-fill' : 'bi-person'}`} />
                <span><strong>{v.label}</strong><small>{v.vibe}</small></span>
              </button>
            ))}
          </div>

          <label className="vs-label">Narration style</label>
          <div className="vs-chips">
            {(opts?.narrationStyles ?? ['explainer', 'storyteller', 'energetic', 'calm']).map(n => (
              <button key={n} type="button" className={`vs-chip${narration === n ? ' active' : ''}`} onClick={() => setNarration(n)}>
                {NARRATION_LABEL[n] ?? n}
              </button>
            ))}
          </div>
        </section>
      </div>

      <div className="vs-create-row">
        <button type="button" className="vs-create" onClick={() => void create()} disabled={!ready || creating || inFlight || (opts ? !opts.aiAvailable : false)}>
          {creating ? <><span className="vs-spin" /> Starting…</>
            : inFlight ? <><span className="vs-spin" /> A video is rendering…</>
            : <><PhosphorIcon className="bi bi-stars" /> Create {fmtLength(length)} video</>}
        </button>
        <small className="vs-muted">One video renders at a time · up to {opts?.dailyCap ?? 20} a day</small>
      </div>

      {/* ── Jobs ── */}
      <section className="vs-card vs-jobs">
        <h2><span className="vs-step">3</span> Your videos</h2>
        {jobs.length === 0 ? (
          <p className="vs-muted">Nothing yet — your videos will appear here as they render.</p>
        ) : (
          <div className="vs-job-list">
            {jobs.map(j => {
              const pct = j.status === 'done' ? 100
                : j.status === 'assembling' ? 95
                : j.sceneCount > 0 ? Math.round(10 + (j.scenesDone / j.sceneCount) * 80)
                : j.status === 'scripting' ? 6 : 2
              return (
                <div key={j.id} className={`vs-job vs-job-${j.status}`}>
                  <div className="vs-job-main">
                    <strong>{j.title || 'Untitled video'}</strong>
                    <small>
                      {STATUS_LABEL[j.status] ?? j.status}
                      {j.sceneCount > 0 && j.status !== 'done' && j.status !== 'failed' && ` · scene ${Math.min(j.scenesDone + 1, j.sceneCount)} of ${j.sceneCount}`}
                      {j.status === 'done' && ` · ${fmtDuration(j.durationSeconds)} · ${j.sceneCount} scenes${j.hasAudio ? '' : ' · no narration'}`}
                    </small>
                    {j.status !== 'done' && j.status !== 'failed' && (
                      <div className="vs-bar"><span style={{ width: `${pct}%` }} /></div>
                    )}
                    {j.status === 'failed' && j.error && <p className="vs-job-error">{j.error}</p>}
                  </div>
                  <div className="vs-job-actions">
                    {j.status === 'done' && (
                      <>
                        <button type="button" className="vs-btn" onClick={() => void play(j)}><PhosphorIcon className="bi bi-play-fill" /> Play</button>
                        <button type="button" className="vs-btn" onClick={() => void download(j)}><PhosphorIcon className="bi bi-download" /> MP4</button>
                      </>
                    )}
                    {(j.status === 'done' || j.status === 'failed') && (
                      <button type="button" className="vs-btn vs-btn-del" onClick={() => void remove(j)} aria-label="Delete video"><PhosphorIcon className="bi bi-trash" /></button>
                    )}
                  </div>
                  {playing?.id === j.id && (
                    <video className="vs-player" src={playing.url} controls autoPlay />
                  )}
                </div>
              )
            })}
          </div>
        )}
      </section>
    </div>
  )
}
