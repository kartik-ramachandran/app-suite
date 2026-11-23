import PhosphorIcon from '../components/PhosphorIcon'
import { AgentStage, useFieldView } from '../gamefield'
import type { FieldModel } from '../gamefield'
import { useEffect, useMemo, useRef, useState } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import ExportButtons from '../components/ExportButtons'
import ConfirmDialog from '../components/ConfirmDialog'
import './InteractionIntelligence.css'

const API = import.meta.env.VITE_API_URL ??
  (typeof window !== 'undefined' && window.location.hostname !== 'localhost'
    ? window.location.origin
    : 'http://localhost:5220')

type ToolKey =
  | 'summaries'
  | 'wrap-up'
  | 'quality'
  | 'compliance'
  | 'coaching'
  | 'sentiment'
  | 'knowledge'
  | 'audit'

interface ToolInfo {
  key: ToolKey
  name: string
  icon: string
  cta: string
  description: string
}

// Industry-agnostic suite — works for support, healthcare, finance, sales, legal, etc.
const TOOLS: ToolInfo[] = [
  { key: 'summaries', name: 'Conversation Summaries', icon: 'bi-soundwave', cta: 'Summarise recording',
    description: 'Instant transcript, summary, key points, action items, sentiment and escalation read.' },
  { key: 'wrap-up', name: 'Wrap-Up Automation', icon: 'bi-lightning-charge', cta: 'Generate wrap-up',
    description: 'Auto-draft notes, a ready-to-send follow-up, record updates and tags for after the interaction.' },
  { key: 'quality', name: 'Quality Scoring', icon: 'bi-clipboard-check', cta: 'Score interaction',
    description: 'Score the interaction against best-practice criteria with strengths and areas to improve.' },
  { key: 'compliance', name: 'Compliance Checks', icon: 'bi-shield-check', cta: 'Run compliance check',
    description: 'Flag policy, regulatory or disclosure gaps with evidence and a pass/review/fail verdict.' },
  { key: 'coaching', name: 'Coaching Insights', icon: 'bi-mortarboard', cta: 'Generate coaching',
    description: 'Personalised coaching: what went well, growth areas and stronger rephrasings.' },
  { key: 'sentiment', name: 'Sentiment & Escalation', icon: 'bi-emoji-neutral', cta: 'Analyse sentiment',
    description: 'Tone, emotional shifts, escalation triggers and de-escalation opportunities.' },
  { key: 'knowledge', name: 'Knowledge Assist', icon: 'bi-lightbulb', cta: 'Suggest answers',
    description: 'Surface accurate suggested answers for the questions and issues raised.' },
  { key: 'audit', name: 'Audit & Assurance', icon: 'bi-file-earmark-lock', cta: 'Build assurance report',
    description: 'An assurance report with checks, findings, risk rating and recommendations.' },
]

// Recordings (transcribed via speech-to-text) OR ready-made transcripts.
const uploadAccept = 'audio/*,video/*,.mp3,.mp4,.m4a,.wav,.webm,.mpeg,.mpga,.ogg,.flac,.txt,.md,.vtt,.srt,.pdf,.docx'
const TRANSCRIPT_FILE_RE = /\.(txt|md|vtt|srt|pdf|docx)$/i
const isTranscriptFile = (f: File | null) => !!f && TRANSCRIPT_FILE_RE.test(f.name)

interface Metric { label: string; value: string; tone: string }
interface Section { heading: string; items: string[] }
interface Pair { label: string; value: string }
interface Analysis { headline: string; metrics: Metric[]; sections: Section[]; pairs: Pair[] }
interface SessionListItem { sessionId: string; sourceLabel: string; updatedAt: string; tools: string[] }

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
  if (t === 'good') return 'ii-pill ii-pill-low'
  if (t === 'warn') return 'ii-pill ii-pill-med'
  if (t === 'bad') return 'ii-pill ii-pill-high'
  return 'ii-pill ii-pill-neutral'
}


/** Flattens an analysis into the plain text the Gamefield inspector shows. */
function flattenAnalysis(a: Analysis): string {
  return [a.headline, ...a.sections.map(sec => sec.heading + '\n' + sec.items.map(i => '- ' + i).join('\n'))].join('\n\n')
}

export default function InteractionIntelligence() {
  const { stageProps } = useFieldView('signal')

  const { toolName } = useParams<{ toolName?: string }>()
  const navigate = useNavigate()

  const activeTool = useMemo<ToolInfo>(
    () => TOOLS.find(t => t.key === toolName) ?? TOOLS[0],
    [toolName]
  )

  const [file, setFile] = useState<File | null>(null)
  const [url, setUrl] = useState('')
  const [pastedText, setPastedText] = useState('')
  // What the user wants out of this recording. Persists across tool switches and
  // is sent with every analysis so each tool is tailored to the goal.
  const [intent, setIntent] = useState('')
  // Goal picker: suggested options the user ticks; the goal is derived from them.
  const [showGoalModal, setShowGoalModal] = useState(false)
  const [goalOptions, setGoalOptions] = useState<string[]>([])
  const [selectedGoals, setSelectedGoals] = useState<string[]>([])
  const [goalSeed, setGoalSeed] = useState('')
  const [goalCustom, setGoalCustom] = useState('')
  const [loadingGoals, setLoadingGoals] = useState(false)
  const [goalError, setGoalError] = useState('')
  const [isDragOver, setIsDragOver] = useState(false)
  const [status, setStatus] = useState<'idle' | 'transcribing' | 'analysing'>('idle')
  const [error, setError] = useState<string | null>(null)
  const [transcript, setTranscript] = useState<string | null>(null)
  // Analyses cached per tool so switching tools (and returning to the page) keeps results.
  const [analyses, setAnalyses] = useState<Record<string, Analysis>>({})
  const [sourceLabel, setSourceLabel] = useState('')
  const [sessions, setSessions] = useState<SessionListItem[]>([])
  const [currentSessionId, setCurrentSessionId] = useState<string | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const resultsRef = useRef<HTMLDivElement>(null)

  const busy = status !== 'idle'
  const analysis = analyses[activeTool.key] ?? null
  // A pasted transcript or a text/caption/PDF/Word file skips speech-to-text.
  const usingTranscriptInput = !!pastedText.trim() || isTranscriptFile(file)

  // Identifies the current source so we know when to invalidate a cached transcript.
  const sourceKey = file ? `${file.name}:${file.size}`
    : url.trim() ? url.trim()
    : pastedText.trim() ? `paste:${pastedText.trim().length}:${pastedText.trim().slice(0, 48)}`
    : ''
  const transcriptSourceRef = useRef<string>('')
  // sessionId lives in a ref so it's available immediately after transcribe (no stale closure).
  const sessionIdRef = useRef<string | null>(null)
  const hasFreshTranscript = !!transcript && transcriptSourceRef.current === sourceKey

  const authHeaders = (): Record<string, string> => {
    const token = localStorage.getItem('authToken')
    return token ? { Authorization: `Bearer ${token}` } : {}
  }

  type SessionDto = { sessionId: string; sourceLabel: string; transcript: string; analysesJson: string; intent?: string | null }

  // Older sessions may hold a broken analysis where the model's whole JSON reply
  // ended up inside `headline` (the backend now parses defensively). Salvage those
  // for display by re-parsing the embedded JSON — including truncated replies,
  // which need unterminated strings/brackets closed before JSON.parse accepts them.
  const repairJson = (s: string): string => {
    let inString = false
    let escaped = false
    const stack: string[] = []
    for (const ch of s) {
      if (escaped) { escaped = false; continue }
      if (ch === '\\') { escaped = inString; continue }
      if (ch === '"') { inString = !inString; continue }
      if (inString) continue
      if (ch === '{') stack.push('}')
      else if (ch === '[') stack.push(']')
      else if ((ch === '}' || ch === ']') && stack[stack.length - 1] === ch) stack.pop()
    }
    let out = s
    if (inString) out += '"'
    while (stack.length) out += stack.pop()
    return out
  }

  const tryParseAnalysisBlob = (h: string): Partial<Analysis> | null => {
    const start = h.indexOf('{')
    if (start < 0) return null
    const blob = h.slice(start)
    // Typographic quotes and literal newlines/control characters inside strings
    // are the two things strict JSON parsers reject that models love to emit.
    const sanitized = blob.replace(/[\u201C\u201D]/g, '\"').replace(/[\u2018\u2019]/g, "'").replace(/[\u0000-\u001f]+/g, ' ')
    const attempts: string[] = []
    for (const b of [blob, sanitized]) {
      const repaired = repairJson(b)
      attempts.push(
        b.slice(0, b.lastIndexOf('}') + 1),
        b,
        repaired,
        repaired.replace(/,\s*([}\]])/g, '$1'),   // strip trailing commas
      )
    }
    for (const c of attempts) {
      if (!c) continue
      try {
        const p = JSON.parse(c)
        if (p && typeof p === 'object' && ('headline' in p || 'sections' in p)) return p
      } catch { /* try the next candidate */ }
    }
    return null
  }

  // Absolute last resort: never show JSON syntax to a person. Pull out the
  // human-readable sentences (long quoted strings) and present those.
  const humanizeBlob = (h: string): Analysis => {
    const sentences: string[] = []
    const re = /"((?:[^"\\]|\\.)*?)"/g
    let m: RegExpExecArray | null
    while ((m = re.exec(h)) !== null) {
      const text = m[1].replace(/\\n/g, ' ').replace(/\\"/g, '"').trim()
      if (text.length >= 25) sentences.push(text)   // skip keys and short values
    }
    return {
      headline: sentences[0] ?? 'Analysis',
      metrics: [],
      sections: sentences.length > 1
        ? [{ heading: 'Key points', items: sentences.slice(1) }]
        : [],
      pairs: [],
    }
  }

  const normalizeAnalysis = (a: Partial<Analysis> | null | undefined): Analysis => {
    const base: Analysis = {
      headline: a?.headline ?? '',
      metrics: a?.metrics ?? [],
      sections: a?.sections ?? [],
      pairs: a?.pairs ?? [],
    }
    const h = base.headline.trim()
    if (base.sections.length === 0 && base.metrics.length === 0 && h.startsWith('{')) {
      const parsed = tryParseAnalysisBlob(h)
      if (parsed) {
        return {
          headline: typeof parsed.headline === 'string' ? parsed.headline : '',
          metrics: Array.isArray(parsed.metrics) ? parsed.metrics : [],
          sections: Array.isArray(parsed.sections) ? parsed.sections : [],
          pairs: Array.isArray(parsed.pairs) ? parsed.pairs : [],
        }
      }
      // Unsalvageable as JSON — extract the readable sentences instead. A person
      // must never be shown raw JSON syntax.
      return humanizeBlob(h)
    }
    return base
  }

  // Load a session's data into the workspace. Returns the tool keys it contains.
  const applySession = (s: SessionDto): string[] => {
    let parsed: Record<string, Analysis> = {}
    try { parsed = JSON.parse(s.analysesJson || '{}') } catch { parsed = {} }
    for (const key of Object.keys(parsed)) parsed[key] = normalizeAnalysis(parsed[key])
    setFile(null); setUrl('')
    setTranscript(s.transcript)
    transcriptSourceRef.current = ''            // matches empty sourceKey so cached transcript is reused
    sessionIdRef.current = s.sessionId
    setAnalyses(parsed)
    setSourceLabel(s.sourceLabel)
    setIntent(s.intent || '')
    setCurrentSessionId(s.sessionId)
    setError(null)
    return Object.keys(parsed)
  }

  const fetchSessions = async () => {
    try {
      const res = await fetch(`${API}/api/interaction/sessions?take=50`, { headers: authHeaders() })
      if (!res.ok) return
      const data = await res.json() as { items: SessionListItem[] }
      setSessions(data.items ?? [])
    } catch { /* ignore */ }
  }

  // Deleting an analysis is not undoable, so it goes through the shared
  // confirm dialog rather than firing straight off the trash button.
  const [deleteTarget, setDeleteTarget] = useState<SessionListItem | null>(null)

  // The transcript drives every tool but had nowhere to be read. Collapsed by
  // default so it never buries the results.
  const [showTranscript, setShowTranscript] = useState(false)

  const deleteSession = async (id: string) => {
    try { await fetch(`${API}/api/interaction/session/${id}`, { method: 'DELETE', headers: authHeaders() }) } catch { /* ignore */ }
    setSessions(prev => prev.filter(s => s.sessionId !== id))
    if (currentSessionId === id) resetSource()
  }

  const confirmDelete = async () => {
    const target = deleteTarget
    setDeleteTarget(null)
    if (target) await deleteSession(target.sessionId)
  }

  const loadSession = async (id: string) => {
    try {
      const res = await fetch(`${API}/api/interaction/session/${id}`, { headers: authHeaders() })
      if (!res.ok) return
      const data = await res.json() as { session: SessionDto }
      const keys = applySession(data.session)
      // Jump to a tool that actually has results, if the current one doesn't.
      if (keys.length && !keys.includes(activeTool.key)) navigate(`/interaction-intelligence/${keys[0]}`)
    } catch { /* ignore */ }
  }

  // Live Meetings links here with the analysis it wants opened, so a recorded
  // meeting opens where its analysis actually lives instead of dead-ending.
  useEffect(() => {
    const wanted = new URLSearchParams(window.location.search).get('session')
    if (wanted) loadSession(wanted)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // On mount: restore the most recent session and load the history list.
  useEffect(() => {
    const token = localStorage.getItem('authToken')
    if (!token) return
    let cancelled = false
    ;(async () => {
      fetchSessions()
      try {
        const res = await fetch(`${API}/api/interaction/session`, { headers: { Authorization: `Bearer ${token}` } })
        if (!res.ok) return
        const data = await res.json() as { session: SessionDto | null }
        if (cancelled || !data.session) return
        applySession(data.session)
      } catch { /* not logged in / offline */ }
    })()
    return () => { cancelled = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const selectTool = (key: ToolKey) => {
    navigate(`/interaction-intelligence/${key}`)
    setError(null)
  }

  const resetSource = () => {
    setError(null); setAnalyses({}); setTranscript(null); setSourceLabel('')
    transcriptSourceRef.current = ''; sessionIdRef.current = null; setCurrentSessionId(null)
  }

  const acceptFile = (f: File | null) => { setFile(f); setUrl(''); setPastedText(''); resetSource() }

  const readError = async (res: Response): Promise<string> => {
    try { const p = await res.json(); if (p?.error) return p.error } catch { /* ignore */ }
    return `Request failed (${res.status})`
  }

  const ensureTranscript = async (): Promise<string> => {
    // Reuse a cached transcript when the source hasn't changed (avoids re-running
    // the expensive transcription when switching tools or re-analysing).
    if (hasFreshTranscript && transcript) return transcript

    setStatus('transcribing')

    // A pasted transcript or a text/caption/PDF/Word file skips speech-to-text
    // and goes to /transcript; audio/video (or a link) goes to /transcribe.
    const endpoint = usingTranscriptInput ? '/api/interaction/transcript' : '/api/interaction/transcribe'

    const form = new FormData()
    if (file) form.append('file', file)
    if (!file && url.trim() && !usingTranscriptInput) form.append('url', url.trim())
    if (!file && pastedText.trim()) form.append('text', pastedText.trim())
    if (intent.trim()) form.append('intent', intent.trim())

    const res = await fetch(`${API}${endpoint}`, {
      method: 'POST',
      headers: authHeaders(),
      body: form,
    })
    if (!res.ok) throw new Error(await readError(res))
    const payload = await res.json() as { transcript: string; sessionId: string }
    setTranscript(payload.transcript)
    sessionIdRef.current = payload.sessionId
    setCurrentSessionId(payload.sessionId)
    setAnalyses({})                       // new transcript → previous tool results no longer apply
    setSourceLabel(file ? file.name : url.trim() ? url.trim() : 'Pasted transcript')
    transcriptSourceRef.current = sourceKey
    return payload.transcript
  }

  const handleRun = async () => {
    if (!file && !url.trim() && !pastedText.trim() && !transcript) { setError('Upload a recording or transcript, paste a link, or paste a transcript first.'); return }
    setError(null)
    try {
      const t = await ensureTranscript()
      setStatus('analysing')
      const res = await fetch(`${API}/api/interaction/analyse`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ transcript: t, tool: activeTool.key, sessionId: sessionIdRef.current, intent: intent.trim() || null }),
      })
      if (!res.ok) throw new Error(await readError(res))
      const result = normalizeAnalysis(await res.json() as Analysis)
      setAnalyses(prev => ({ ...prev, [activeTool.key]: result }))
      fetchSessions()   // reflect the new/updated session in the History panel
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Something went wrong.')
    } finally {
      setStatus('idle')
    }
  }

  // ── Goal picker ──
  const openGoalModal = () => {
    setGoalError('')
    setShowGoalModal(true)
    if (goalOptions.length === 0) generateGoals(false)
  }

  const generateGoals = async (more: boolean) => {
    setLoadingGoals(true); setGoalError('')
    try {
      const res = await fetch(`${API}/api/interaction/suggest-goals`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...authHeaders() },
        body: JSON.stringify({ transcript: transcript || null, seed: goalSeed.trim() || null, existing: more ? goalOptions : [] }),
      })
      if (!res.ok) throw new Error(await readError(res))
      const data = await res.json() as { goals: string[] }
      setGoalOptions(prev => more ? [...prev, ...data.goals] : data.goals)
    } catch (e) {
      setGoalError(e instanceof Error ? e.message : 'Could not suggest goals — try again.')
    } finally {
      setLoadingGoals(false)
    }
  }

  const toggleGoal = (g: string) =>
    setSelectedGoals(prev => prev.includes(g) ? prev.filter(x => x !== g) : [...prev, g])

  // Derive a single goal statement from the selected options (+ any free text).
  const composeGoal = (goals: string[], custom: string): string => {
    const c = custom.trim()
    if (goals.length === 0) return c
    if (goals.length === 1 && !c) return goals[0]
    return 'Focus this analysis on: ' + goals.join('; ') + (c ? `. Also: ${c}` : '')
  }

  const applyGoals = () => {
    setIntent(composeGoal(selectedGoals, goalCustom))
    setShowGoalModal(false)
  }

  const clearGoal = () => { setIntent(''); setSelectedGoals([]); setGoalCustom('') }


  // Gamefield: transcription feeds the room, then one analyst desk per tool.
  const field = useMemo<FieldModel>(() => {
    const desks = TOOLS.map(t => {
      const done = !!analyses[t.key]
      const live = status === 'analysing' && t.key === activeTool.key
      return {
        id: `tool-${t.key}`,
        name: t.name,
        role: t.cta.toLowerCase(),
        status: (live ? 'working' : done ? 'done' : 'idle') as 'working' | 'done' | 'idle',
        detail: live ? 'Reading the transcript…' : analyses[t.key]?.headline,
        output: analyses[t.key] ? flattenAnalysis(analyses[t.key]) : undefined,
      }
    })

    return {
      title: 'Signal',
      subtitle: sourceLabel || 'Drop a recording or transcript to open the floor.',
      running: busy,
      agents: [
        ...desks,
        {
          id: 'transcriber',
          name: 'Transcription',
          role: usingTranscriptInput ? 'text in, no speech-to-text' : 'speech to text',
          status: (error ? 'failed' : status === 'transcribing' ? 'working' : transcript ? 'done' : 'idle') as 'failed' | 'working' | 'done' | 'idle',
          detail: error || (status === 'transcribing' ? 'Turning the audio into words…' : undefined),
          output: transcript || undefined,
          boss: true,
        },
      ],
      messages: transcript
        ? desks.filter(d => d.status !== 'idle').map(d => ({ id: `feed-${d.id}`, from: 'transcriber', to: d.id }))
        : [],
      events: [
        ...(transcript ? [{ id: 'tr', text: 'Transcript ready.', tone: 'good' as const }] : []),
        ...TOOLS.filter(t => analyses[t.key]).map(t => ({ id: `an-${t.key}`, text: `${t.name} finished.`, tone: 'good' as const })),
        ...(error ? [{ id: 'err', text: error, tone: 'bad' as const }] : []),
      ],
    }
  }, [analyses, status, activeTool, transcript, error, busy, sourceLabel, usingTranscriptInput])

  return (
    <div className="ii-page">
      <ConfirmDialog
        open={!!deleteTarget}
        title="Delete analysis?"
        message={`This will permanently delete "${deleteTarget?.sourceLabel || 'this analysis'}" and everything analysed from it.`}
        confirmLabel="Delete"
        onConfirm={confirmDelete}
        onCancel={() => setDeleteTarget(null)}
      />
      {/* ── Sidebar ── */}
      <aside className="ii-sidebar">
        <div className="ii-sidebar-head">
          <span className="ii-kicker">Signal</span>
          <p>Turn any recorded interaction into transcripts and structured insight — for any industry.</p>
        </div>
        <nav className="ii-tool-list">
          {TOOLS.map(tool => (
            <button
              key={tool.key}
              className={`ii-tool-item${tool.key === activeTool.key ? ' ii-tool-active' : ''}`}
              onClick={() => selectTool(tool.key)}
            >
              <PhosphorIcon className={`bi ${tool.icon}`} />
              <span className="ii-tool-name">{tool.name}</span>
            </button>
          ))}
        </nav>

        {/* Live meetings is its own module now; this is the way through to it. */}
        <a className="ii-module-link" href="/live-meetings">
          <PhosphorIcon className="bi bi-record-circle" />
          <span>
            <strong>Live meetings</strong>
            <small>Send a notetaker to a calendar meeting</small>
          </span>
          <PhosphorIcon className="bi bi-arrow-right" />
        </a>
      </aside>

      {/* ── Workspace ── */}
      <main className="ii-workspace">
        <header className="ii-header">
          <div className="ii-header-icon"><PhosphorIcon className={`bi ${activeTool.icon}`} /></div>
          <div className="ii-header-text">
            <h1>{activeTool.name}</h1>
            <p>{activeTool.description}</p>
          </div>
          <button className="ii-new-analysis" onClick={resetSource} title="Clear this recording and start a new analysis">
            <PhosphorIcon className="bi bi-plus-lg" /> New analysis
          </button>
        </header>

        <AgentStage {...stageProps} model={field} />

        <div className="ii-panel" data-visualize>
          {/* Upload */}
          <div
            className={`ii-dropzone${isDragOver ? ' ii-dropzone-over' : ''}`}
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
              accept={uploadAccept}
              hidden
              onChange={e => acceptFile(e.target.files?.[0] ?? null)}
            />
            <PhosphorIcon className="bi bi-cloud-arrow-up" />
            {file ? (
              <div className="ii-file-chip" onClick={e => e.stopPropagation()}>
                <PhosphorIcon className={`bi ${isTranscriptFile(file) ? 'bi-file-earmark-text' : 'bi-file-earmark-music'}`} />
                <span>{file.name}</span>
                <button onClick={() => acceptFile(null)} aria-label="Remove file"><PhosphorIcon className="bi bi-x" /></button>
              </div>
            ) : sourceLabel && transcript ? (
              <div className="ii-file-chip" onClick={e => e.stopPropagation()}>
                <PhosphorIcon className="bi bi-clock-history" />
                <span>{sourceLabel}</span>
                <button onClick={() => acceptFile(null)} aria-label="Start over" title="Start over"><PhosphorIcon className="bi bi-x" /></button>
              </div>
            ) : (
              <>
                <p className="ii-drop-title">Drag &amp; drop a recording or transcript, or click to browse</p>
                <p className="ii-drop-sub">Recording (MP3, MP4, M4A, WAV, WEBM, OGG, FLAC — up to 1&nbsp;GB) or transcript (TXT, VTT, SRT, PDF, DOCX)</p>
              </>
            )}
          </div>

          <div className="ii-or"><span>or share a link</span></div>

          <input
            className="ii-url-input"
            type="url"
            placeholder="https://example.com/recording.mp4"
            value={url}
            onChange={e => { setUrl(e.target.value); setFile(null); setPastedText(''); resetSource() }}
          />
          <p className="ii-hint">Paste a direct link to an audio/video file. The link must be publicly accessible.</p>

          <div className="ii-or"><span>or paste a transcript</span></div>

          <textarea
            className="ii-url-input ii-paste-area"
            rows={5}
            placeholder={'Paste a meeting transcript here — including a Teams or Zoom transcript (VTT/plain text). Speaker labels and timestamps are handled automatically.'}
            value={pastedText}
            onChange={e => { setPastedText(e.target.value); setFile(null); setUrl(''); resetSource() }}
          />
          <p className="ii-hint">Already have a transcript? Paste it (or upload the file above) to skip transcription and go straight to analysis.</p>

          {/* Goal / intent — pick from suggested goals; steers every tool. */}
          <div className="ii-goal">
            <div className="ii-goal-label">
              <PhosphorIcon className="bi bi-bullseye" /> What do you want from this recording?
              <span className="ii-goal-optional">optional</span>
            </div>
            {intent ? (
              <div className="ii-goal-set">
                <p className="ii-goal-current">{intent}</p>
                <div className="ii-goal-set-actions">
                  <button className="ii-goal-choose" onClick={openGoalModal}><PhosphorIcon className="bi bi-pencil" /> Edit goals</button>
                  <button className="ii-goal-clear" onClick={clearGoal}>Clear</button>
                </div>
              </div>
            ) : (
              <button className="ii-goal-choose" onClick={openGoalModal}>
                <PhosphorIcon className="bi bi-list-check" /> Choose your goals
              </button>
            )}
            <p className="ii-hint">Pick from suggested goals — they steer every tool toward what matters to you.</p>
          </div>

          {hasFreshTranscript && (
            <>
              <p className="ii-cached"><PhosphorIcon className="bi bi-check-circle" /> Transcribed — switch tools to run another analysis instantly.</p>
              {transcript && (
                <div className="ii-transcript">
                  <button
                    type="button"
                    className="ii-transcript-toggle"
                    onClick={() => setShowTranscript(v => !v)}
                    aria-expanded={showTranscript}
                  >
                    <PhosphorIcon className={`bi bi-chevron-${showTranscript ? 'down' : 'right'}`} />
                    <span>Transcript</span>
                    <small>{transcript.trim().split(/\s+/).length.toLocaleString()} words</small>
                  </button>
                  {showTranscript && (
                    <>
                      <pre className="ii-transcript-body">{transcript}</pre>
                      <div className="ii-transcript-actions">
                        <button type="button" onClick={() => navigator.clipboard.writeText(transcript)}>
                          <PhosphorIcon className="bi bi-clipboard" /> Copy
                        </button>
                      </div>
                    </>
                  )}
                </div>
              )}
            </>
          )}

          {error && <div className="ii-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

          <button className="ii-run" onClick={handleRun} disabled={busy}>
            {status === 'transcribing' ? <><PhosphorIcon className="bi bi-arrow-repeat ii-spin" /> {usingTranscriptInput ? 'Reading transcript…' : 'Transcribing…'}</>
              : status === 'analysing' ? <><PhosphorIcon className="bi bi-arrow-repeat ii-spin" /> Analysing…</>
              : <><PhosphorIcon className={`bi ${activeTool.icon}`} /> {activeTool.cta}</>}
          </button>

          {/* Results */}
          {analysis && (
            <div className="ii-results-block">
              <div className="ii-results-toolbar" style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 10 }}>
                <ExportButtons
                  title={analysis.headline || activeTool.name}
                  filenameBase={`signal-${activeTool.key}`}
                  subtitle={`GenPrompt Signal · ${activeTool.name}${sourceLabel ? ' · ' + sourceLabel : ''}`}
                  getBodyHtml={() => resultsRef.current?.innerHTML}
                  size="sm"
                />
              </div>
              <div className="ii-results" ref={resultsRef}>
              {analysis.headline && <div className="ii-headline">{analysis.headline}</div>}

              {analysis.metrics?.length > 0 && (
                <div className="ii-result-meta">
                  {analysis.metrics.map((m, i) => (
                    <span key={i} className={toneClass(m.tone)}>{m.label}: {m.value}</span>
                  ))}
                </div>
              )}

              {analysis.sections?.map((s, i) => (
                <section key={i} className="ii-card">
                  <h3>{s.heading}</h3>
                  {s.items?.length > 0
                    ? <ul>{s.items.map((it, j) => <li key={j}>{it}</li>)}</ul>
                    : <p className="ii-muted">Nothing flagged.</p>}
                </section>
              ))}

              {analysis.pairs?.length > 0 && (
                <section className="ii-card">
                  <h3>Details</h3>
                  <div className="ii-pairs">
                    {analysis.pairs.map((p, i) => (
                      <div key={i} className="ii-pair">
                        <div className="ii-pair-label">{p.label}</div>
                        <div className="ii-pair-value">{p.value}</div>
                      </div>
                    ))}
                  </div>
                </section>
              )}

              {transcript && (
                <details className="ii-card">
                  <summary><PhosphorIcon className="bi bi-file-text" /> Full transcript</summary>
                  <pre className="ii-transcript">{transcript}</pre>
                </details>
              )}
              </div>
            </div>
          )}
        </div>
      </main>

      {/* ── History (saved analyses) ── */}
      <aside className="ii-history">
        <div className="ii-history-head">
          <span>History</span>
          <button className="ii-history-new" onClick={resetSource} title="New analysis"><PhosphorIcon className="bi bi-plus-lg" /></button>
        </div>
        <div className="ii-history-list">
          {sessions.length === 0 ? (
            <div className="ii-history-empty"><PhosphorIcon className="bi bi-clock-history" /><p>No analyses yet</p></div>
          ) : sessions.map(s => (
            <div
              key={s.sessionId}
              className={`ii-history-item${s.sessionId === currentSessionId ? ' ii-history-active' : ''}`}
              onClick={() => loadSession(s.sessionId)}
              role="button"
              tabIndex={0}
            >
              <button
                className="ii-history-del"
                title="Delete"
                aria-label="Delete analysis"
                onClick={e => { e.stopPropagation(); setDeleteTarget(s) }}
              >
                <PhosphorIcon className="bi bi-trash" />
              </button>
              <div className="ii-history-title"><PhosphorIcon className="bi bi-soundwave" /> {s.sourceLabel || 'Recording'}</div>
              <div className="ii-history-meta">{timeAgo(s.updatedAt)} · {s.tools.length} {s.tools.length === 1 ? 'analysis' : 'analyses'}</div>
              {s.tools.length > 0 && (
                <div className="ii-history-chips">
                  {s.tools.map(tk => <span key={tk} className="ii-history-chip">{toolLabel(tk)}</span>)}
                </div>
              )}
            </div>
          ))}
        </div>
      </aside>

      {/* ── Goal picker modal ── */}
      {showGoalModal && (
        <div className="ii-modal-overlay" onClick={() => setShowGoalModal(false)}>
          <div className="ii-modal" onClick={e => e.stopPropagation()} role="dialog" aria-modal="true">
            <div className="ii-modal-head">
              <h3><PhosphorIcon className="bi bi-bullseye" /> What do you want from this recording?</h3>
              <button className="ii-modal-close" onClick={() => setShowGoalModal(false)} aria-label="Close"><PhosphorIcon className="bi bi-x-lg" /></button>
            </div>
            <p className="ii-modal-sub">
              Tick the goals that matter to you — they steer every tool.{' '}
              {transcript ? 'Suggestions are tailored to this recording.' : 'Transcribe the recording first for suggestions tailored to what’s actually in it.'}
            </p>

            <div className="ii-modal-seed">
              <input
                className="ii-url-input"
                placeholder="Add context to tailor suggestions (optional) — e.g. “I’m the compliance reviewer”"
                value={goalSeed}
                onChange={e => setGoalSeed(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') generateGoals(false) }}
              />
              <button className="ii-goal-choose" onClick={() => generateGoals(false)} disabled={loadingGoals}>
                {loadingGoals && goalOptions.length === 0 ? 'Thinking…' : goalOptions.length ? 'Regenerate' : 'Suggest goals'}
              </button>
            </div>

            {goalError && <div className="ii-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {goalError}</div>}

            <div className="ii-modal-list">
              {loadingGoals && goalOptions.length === 0 ? (
                <div className="ii-goal-loading"><PhosphorIcon className="bi bi-arrow-repeat ii-spin" /> Generating goals…</div>
              ) : goalOptions.length === 0 ? (
                <div className="ii-goal-loading">No suggestions yet — click “Suggest goals”.</div>
              ) : goalOptions.map((g, i) => (
                <label key={g} className={`ii-goal-option${selectedGoals.includes(g) ? ' ii-goal-option-on' : ''}`}>
                  <input type="checkbox" checked={selectedGoals.includes(g)} onChange={() => toggleGoal(g)} />
                  <span className="ii-goal-num">{i + 1}.</span>
                  <span className="ii-goal-text">{g}</span>
                </label>
              ))}
            </div>

            {goalOptions.length > 0 && (
              <button className="ii-goal-more" onClick={() => generateGoals(true)} disabled={loadingGoals}>
                {loadingGoals ? <><PhosphorIcon className="bi bi-arrow-repeat ii-spin" /> Loading…</> : <><PhosphorIcon className="bi bi-plus-lg" /> Show 20 more</>}
              </button>
            )}

            <div className="ii-modal-custom">
              <label htmlFor="ii-goal-custom">Or add your own</label>
              <textarea
                id="ii-goal-custom"
                rows={2}
                placeholder="Anything else you want this analysis to focus on…"
                value={goalCustom}
                onChange={e => setGoalCustom(e.target.value)}
              />
            </div>

            <div className="ii-modal-foot">
              <span className="ii-modal-count">{selectedGoals.length} selected</span>
              <div className="ii-modal-actions">
                <button className="ii-modal-cancel" onClick={() => setShowGoalModal(false)}>Cancel</button>
                <button className="ii-run ii-modal-apply" onClick={applyGoals} disabled={selectedGoals.length === 0 && !goalCustom.trim()}>
                  <PhosphorIcon className="bi bi-check2" /> Apply goal
                </button>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
