import { useCallback, useEffect, useRef, useState } from 'react'
import PhosphorIcon from './PhosphorIcon'
import {
  recordLifeVoiceEntry, writeLifeVoiceEntry, getLifeVoiceEntries,
  applyLifeVoiceEntry, deleteLifeVoiceEntry, reprocessLifeVoiceEntry,
  type LifeVoiceEntry,
} from '../services/api'
import './LifeVoice.css'

/** Max recording length. Whisper's file ceiling is 25 MB; this stays well inside it. */
const MAX_SECONDS = 15 * 60

const fmt = (s: number) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`

type Props = { onApplied?: () => void }

/**
 * Speak about your life; get structure back.
 *
 * The flow is deliberately three-step — record, review, accept — rather than
 * writing straight through to the tracker. The model is guessing at what a
 * rambling monologue meant, and a wrong guess silently written into someone's
 * life record is far worse than one extra click.
 */
export default function LifeVoiceRecorder({ onApplied }: Props) {
  const [recording, setRecording] = useState(false)
  const [paused, setPaused] = useState(false)
  const [seconds, setSeconds] = useState(0)
  const [levels, setLevels] = useState<number[]>(Array(28).fill(0.06))
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [entries, setEntries] = useState<LifeVoiceEntry[]>([])
  const [active, setActive] = useState<LifeVoiceEntry | null>(null)
  const [showType, setShowType] = useState(false)
  const [typed, setTyped] = useState('')

  // Which extracted items the user wants to keep.
  const [keepCheckIn, setKeepCheckIn] = useState(true)
  const [keepGoals, setKeepGoals] = useState<Set<number>>(new Set())
  const [keepDecisions, setKeepDecisions] = useState<Set<number>>(new Set())
  const [keepPeople, setKeepPeople] = useState<Set<number>>(new Set())

  const recorderRef = useRef<MediaRecorder | null>(null)
  const chunksRef = useRef<Blob[]>([])
  const streamRef = useRef<MediaStream | null>(null)
  const audioCtxRef = useRef<AudioContext | null>(null)
  const rafRef = useRef<number | null>(null)
  const tickRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const startedAtRef = useRef(0)

  const loadEntries = useCallback(async () => {
    try {
      const res = await getLifeVoiceEntries(20)
      setEntries(res.data.items)
    } catch { /* not signed in, or offline */ }
  }, [])

  useEffect(() => { loadEntries() }, [loadEntries])

  // Everything here holds an OS-level resource — a dangling mic stream keeps
  // the browser's recording indicator lit long after the user left the page.
  const teardown = useCallback(() => {
    if (rafRef.current) cancelAnimationFrame(rafRef.current)
    if (tickRef.current) clearInterval(tickRef.current)
    rafRef.current = null
    tickRef.current = null
    audioCtxRef.current?.close().catch(() => {})
    audioCtxRef.current = null
    streamRef.current?.getTracks().forEach(t => t.stop())
    streamRef.current = null
  }, [])

  useEffect(() => teardown, [teardown])

  const preselect = (entry: LifeVoiceEntry) => {
    const x = entry.extraction
    setKeepCheckIn(!!x?.checkIn)
    setKeepGoals(new Set(x?.goals.map((_, i) => i) ?? []))
    setKeepDecisions(new Set(x?.decisions.map((_, i) => i) ?? []))
    setKeepPeople(new Set(x?.relationships.map((_, i) => i) ?? []))
  }

  const start = async () => {
    setError(null)
    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true },
      })
      streamRef.current = stream

      // Live level meter. Purely feedback, but without it users can't tell the
      // mic is actually hearing them and re-record needlessly.
      const ctx = new AudioContext()
      audioCtxRef.current = ctx
      const analyser = ctx.createAnalyser()
      analyser.fftSize = 256
      ctx.createMediaStreamSource(stream).connect(analyser)
      const buf = new Uint8Array(analyser.frequencyBinCount)

      const draw = () => {
        analyser.getByteFrequencyData(buf)
        const avg = buf.reduce((a, b) => a + b, 0) / buf.length / 255
        setLevels(prev => [...prev.slice(1), Math.max(0.06, Math.min(1, avg * 2.4))])
        rafRef.current = requestAnimationFrame(draw)
      }
      draw()

      const mime = MediaRecorder.isTypeSupported('audio/webm;codecs=opus')
        ? 'audio/webm;codecs=opus'
        : MediaRecorder.isTypeSupported('audio/webm') ? 'audio/webm' : ''
      const rec = new MediaRecorder(stream, mime ? { mimeType: mime } : undefined)
      chunksRef.current = []
      rec.ondataavailable = e => { if (e.data.size > 0) chunksRef.current.push(e.data) }
      rec.onstop = () => void upload()
      rec.start(1000)
      recorderRef.current = rec

      startedAtRef.current = Date.now()
      setSeconds(0)
      setRecording(true)
      setPaused(false)
      tickRef.current = setInterval(() => {
        const s = (Date.now() - startedAtRef.current) / 1000
        setSeconds(s)
        if (s >= MAX_SECONDS) stop()
      }, 200)
    } catch (e) {
      const err = e as DOMException
      setError(err.name === 'NotAllowedError'
        ? 'Microphone access was blocked. Allow it in your browser settings, then try again.'
        : err.name === 'NotFoundError'
        ? 'No microphone was found.'
        : 'Could not start recording.')
      teardown()
    }
  }

  const stop = () => {
    if (recorderRef.current?.state !== 'inactive') recorderRef.current?.stop()
    setRecording(false)
    setPaused(false)
    if (rafRef.current) cancelAnimationFrame(rafRef.current)
    if (tickRef.current) clearInterval(tickRef.current)
    setLevels(Array(28).fill(0.06))
  }

  const togglePause = () => {
    const rec = recorderRef.current
    if (!rec) return
    if (rec.state === 'recording') { rec.pause(); setPaused(true) }
    else if (rec.state === 'paused') { rec.resume(); setPaused(false) }
  }

  const upload = async () => {
    const duration = (Date.now() - startedAtRef.current) / 1000
    const blob = new Blob(chunksRef.current, { type: 'audio/webm' })
    teardown()

    if (blob.size < 1200) { setError('That was too short to hear. Try speaking for a few seconds.'); return }

    setBusy('Transcribing and reading your entry…')
    try {
      const res = await recordLifeVoiceEntry(blob, duration)
      setActive(res.data)
      preselect(res.data)
      await loadEntries()
    } catch (e) {
      const ax = e as { response?: { data?: { error?: string; message?: string } } }
      setError(ax.response?.data?.error ?? ax.response?.data?.message ?? 'Could not save that entry.')
    } finally { setBusy(null) }
  }

  const submitTyped = async () => {
    if (!typed.trim()) return
    setBusy('Reading your entry…')
    setError(null)
    try {
      const res = await writeLifeVoiceEntry(typed.trim())
      setActive(res.data)
      preselect(res.data)
      setTyped('')
      setShowType(false)
      await loadEntries()
    } catch (e) {
      const ax = e as { response?: { data?: { error?: string } } }
      setError(ax.response?.data?.error ?? 'Could not save that entry.')
    } finally { setBusy(null) }
  }

  const apply = async () => {
    if (!active) return
    setBusy('Adding to your tracker…')
    try {
      await applyLifeVoiceEntry(active.id, {
        applyCheckIn: keepCheckIn,
        goalIndexes: [...keepGoals],
        decisionIndexes: [...keepDecisions],
        relationshipIndexes: [...keepPeople],
      })
      setActive({ ...active, applied: true })
      await loadEntries()
      onApplied?.()
    } catch (e) {
      const ax = e as { response?: { data?: { error?: string } } }
      setError(ax.response?.data?.error ?? 'Could not add that to your tracker.')
    } finally { setBusy(null) }
  }

  const retry = async (id: number) => {
    setBusy('Re-reading that entry…')
    try {
      const res = await reprocessLifeVoiceEntry(id)
      setActive(res.data)
      preselect(res.data)
      await loadEntries()
    } catch { setError('Could not re-read that entry.') }
    finally { setBusy(null) }
  }

  const remove = async (id: number) => {
    await deleteLifeVoiceEntry(id).catch(() => {})
    if (active?.id === id) setActive(null)
    await loadEntries()
  }

  const toggle = (set: Set<number>, apply2: (s: Set<number>) => void, i: number) => {
    const next = new Set(set)
    next.has(i) ? next.delete(i) : next.add(i)
    apply2(next)
  }

  const x = active?.extraction
  const nothingFound = active?.status === 'ready' && x
    && !x.checkIn && !x.goals.length && !x.decisions.length && !x.relationships.length

  return (
    <div className="lv-root">
      {/* ── Recorder ─────────────────────────────────────────────────── */}
      <div className={`lv-stage${recording ? ' lv-live' : ''}`}>
        <div className="lv-stage-head">
          <h3>Talk about your life</h3>
          <p>Say whatever's on your mind — your day, a decision you're weighing, someone
             you've been thinking about. It becomes structure you can keep.</p>
        </div>

        <div className="lv-wave" aria-hidden="true">
          {levels.map((l, i) => (
            <span key={i} style={{ height: `${Math.round(l * 100)}%`, animationDelay: `${i * 18}ms` }} />
          ))}
        </div>

        <div className="lv-timer">
          {recording ? fmt(seconds) : '0:00'}
          {recording && <em>{paused ? 'paused' : 'listening'}</em>}
        </div>

        <div className="lv-controls">
          {!recording ? (
            <>
              <button className="lv-rec" onClick={start} disabled={!!busy}>
                <PhosphorIcon className="bi bi-mic-fill" /> Start talking
              </button>
              <button className="lv-ghost" onClick={() => setShowType(v => !v)} disabled={!!busy}>
                <PhosphorIcon className="bi bi-keyboard" /> Type instead
              </button>
            </>
          ) : (
            <>
              <button className="lv-stop" onClick={stop}>
                <PhosphorIcon className="bi bi-stop-fill" /> Done
              </button>
              <button className="lv-ghost" onClick={togglePause}>
                <PhosphorIcon className={`bi ${paused ? 'bi-play-fill' : 'bi-pause-fill'}`} />
                {paused ? 'Resume' : 'Pause'}
              </button>
            </>
          )}
        </div>

        {showType && !recording && (
          <div className="lv-type">
            <textarea
              value={typed}
              onChange={e => setTyped(e.target.value)}
              placeholder="Write what's going on…"
              rows={4}
            />
            <button className="lv-rec" onClick={submitTyped} disabled={!typed.trim() || !!busy}>
              Read this
            </button>
          </div>
        )}

        {busy && <div className="lv-busy"><span className="lv-spin" />{busy}</div>}
        {error && <div className="lv-error"><PhosphorIcon className="bi bi-exclamation-triangle" />{error}</div>}
      </div>

      {/* ── Review before anything is written ─────────────────────────── */}
      {active && (
        <div className="lv-review">
          <div className="lv-review-head">
            <div>
              <h4>{active.headline}</h4>
              {active.sentiment && <span className="lv-mood">{active.sentiment}</span>}
            </div>
            <button className="lv-x" onClick={() => setActive(null)} aria-label="Close">
              <PhosphorIcon className="bi bi-x-lg" />
            </button>
          </div>

          {active.narrative && <p className="lv-narrative">{active.narrative}</p>}

          {active.status === 'failed' && (
            <div className="lv-error">
              Your words were saved, but I couldn't read structure out of them.
              <button className="lv-link" onClick={() => retry(active.id)}>Try again</button>
            </div>
          )}

          {nothingFound && (
            <p className="lv-none">Nothing specific to file from this one — it's saved as a journal entry.</p>
          )}

          {active.status === 'ready' && x && !active.applied && (
            <>
              <div className="lv-found">
                {x.checkIn && (
                  <label className={`lv-item${keepCheckIn ? ' lv-on' : ''}`}>
                    <input type="checkbox" checked={keepCheckIn} onChange={() => setKeepCheckIn(v => !v)} />
                    <div>
                      <strong>Daily check-in</strong>
                      <span>
                        {[
                          x.checkIn.mood != null && `mood ${x.checkIn.mood}/10`,
                          x.checkIn.energy != null && `energy ${x.checkIn.energy}/10`,
                          x.checkIn.focus != null && `focus ${x.checkIn.focus}/10`,
                          x.checkIn.sleepHours != null && `${x.checkIn.sleepHours}h sleep`,
                          x.checkIn.exerciseMinutes != null && `${x.checkIn.exerciseMinutes}m exercise`,
                        ].filter(Boolean).join(' · ') || 'notes only'}
                      </span>
                    </div>
                  </label>
                )}

                {x.goals.map((g, i) => (
                  <label key={`g${i}`} className={`lv-item${keepGoals.has(i) ? ' lv-on' : ''}`}>
                    <input type="checkbox" checked={keepGoals.has(i)}
                           onChange={() => toggle(keepGoals, setKeepGoals, i)} />
                    <div>
                      <strong>Goal · {g.area ?? 'Growth'}</strong>
                      <span>{g.title}</span>
                    </div>
                  </label>
                ))}

                {x.decisions.map((d, i) => (
                  <label key={`d${i}`} className={`lv-item${keepDecisions.has(i) ? ' lv-on' : ''}`}>
                    <input type="checkbox" checked={keepDecisions.has(i)}
                           onChange={() => toggle(keepDecisions, setKeepDecisions, i)} />
                    <div>
                      <strong>Decision</strong>
                      <span>{d.title}</span>
                    </div>
                  </label>
                ))}

                {x.relationships.map((p, i) => (
                  <label key={`p${i}`} className={`lv-item${keepPeople.has(i) ? ' lv-on' : ''}`}>
                    <input type="checkbox" checked={keepPeople.has(i)}
                           onChange={() => toggle(keepPeople, setKeepPeople, i)} />
                    <div>
                      <strong>Person · {p.circle ?? 'Friends'}</strong>
                      <span>{p.name}</span>
                    </div>
                  </label>
                ))}
              </div>

              {(x.themes.length > 0 || x.openLoops.length > 0) && (
                <div className="lv-tags">
                  {x.themes.map((t, i) => <span key={`t${i}`} className="lv-tag">{t}</span>)}
                  {x.openLoops.map((t, i) => <span key={`o${i}`} className="lv-tag lv-open">{t}</span>)}
                </div>
              )}

              {!nothingFound && (
                <button className="lv-apply" onClick={apply} disabled={!!busy}>
                  <PhosphorIcon className="bi bi-check2-all" /> Add to my tracker
                </button>
              )}
            </>
          )}

          {active.applied && (
            <div className="lv-done"><PhosphorIcon className="bi bi-check-circle-fill" /> Added to your tracker</div>
          )}
        </div>
      )}

      {/* ── History ──────────────────────────────────────────────────── */}
      {entries.length > 0 && (
        <div className="lv-history">
          <h4>Recent entries</h4>
          <ul>
            {entries.map(e => (
              <li key={e.id} className={active?.id === e.id ? 'lv-sel' : undefined}>
                <button className="lv-hit" onClick={() => { setActive(e); preselect(e) }}>
                  <span className="lv-date">{new Date(e.entryDate).toLocaleDateString(undefined, { month: 'short', day: 'numeric' })}</span>
                  <span className="lv-head">{e.headline || 'Untitled entry'}</span>
                  <span className="lv-meta">
                    {e.durationSeconds > 0 && fmt(e.durationSeconds)}
                    {e.applied && <em className="lv-chip">filed</em>}
                    {e.status === 'failed' && <em className="lv-chip lv-bad">unread</em>}
                  </span>
                </button>
                <button className="lv-del" onClick={() => remove(e.id)} aria-label="Delete entry">
                  <PhosphorIcon className="bi bi-trash" />
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}
