import PhosphorIcon from '../components/PhosphorIcon'
import { useLayoutEffect, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { compressImage } from '../utils/imageCompress'
import SnapButton from '../components/SnapButton'
import './snaptools.css'

interface Ev { title?: string; date?: string; time?: string; location?: string }
interface Result { summary: string; tasks: string[]; events: Ev[] }

export default function EmailTasks() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('email-tasks')
  useLayoutEffect(() => { setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Email to Tasks' }]); return () => setBreadcrumbs([]) }, [])

  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [result, setResult] = useState<Result | null>(null)
  const [copied, setCopied] = useState(false)

  const onFile = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError(''); setBusy(true); setResult(null)
    try { const imageData = await compressImage(files[0], 1600); const { data } = await api.post('/tasks/extract', { imageData }); setResult(data) }
    catch (e: unknown) { setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not read that screenshot.') }
    finally { setBusy(false) }
  }
  const copy = async () => {
    if (!result) return
    const text = [...result.tasks.map(t => `- [ ] ${t}`), ...result.events.map(e => `📅 ${e.title} — ${e.date}${e.time ? ' ' + e.time : ''}${e.location ? ' @ ' + e.location : ''}`)].join('\n')
    await navigator.clipboard.writeText(text); setCopied(true); setTimeout(() => setCopied(false), 1800)
  }


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Email to Tasks',
    busy: busy,
    error,
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-envelope-check" /> Email → Tasks &amp; Events</h1>
        <p>Screenshot an email or message and I'll pull out the to-dos and any events — no more re-reading long threads. Nothing is stored.</p>
      </div>
      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
      <AgentStage {...stageProps} model={field} />

      <div className="st-card" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <span className="st-muted">Works with a screenshot of an email, chat, or message.</span>
        <SnapButton onPick={onFile} busy={busy} label="Extract tasks" />
      </div>

      {result && (
        <div className="st-card">
          {result.summary && <p className="st-muted" style={{ marginTop: 0 }}>{result.summary}</p>}
          {result.tasks.length === 0 && result.events.length === 0 && <p>Nothing actionable found in that message.</p>}
          {result.tasks.length > 0 && (
            <div className="st-section">
              <h3><PhosphorIcon className="bi bi-check2-square" /> Tasks</h3>
              {result.tasks.map((t, i) => <div key={i} className="wb-check"><PhosphorIcon className="bi bi-square" /> {t}</div>)}
            </div>
          )}
          {result.events.length > 0 && (
            <div className="st-section">
              <h3><PhosphorIcon className="bi bi-calendar-event" /> Events</h3>
              {result.events.map((e, i) => <div key={i} className="wb-check"><PhosphorIcon className="bi bi-calendar" /> <span><strong>{e.title}</strong> — {e.date}{e.time ? ` ${e.time}` : ''}{e.location ? ` @ ${e.location}` : ''}</span></div>)}
            </div>
          )}
          <div className="wb-toolbar" style={{ marginTop: 16, marginBottom: 0 }}>
            <button className="st-btn st-btn-primary" onClick={copy}>{copied ? <><PhosphorIcon className="bi bi-check-lg" /> Copied</> : <><PhosphorIcon className="bi bi-clipboard" /> Copy all</>}</button>
          </div>
        </div>
      )}
    </div>
  )
}
