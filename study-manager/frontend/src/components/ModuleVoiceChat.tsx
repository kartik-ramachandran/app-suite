import { useEffect, useRef, useState } from 'react'
import PhosphorIcon from './PhosphorIcon'
import api from '../services/api'
import './ModuleVoiceChat.css'

/**
 * Ask your wardrobe, kitchen, or travel plans a question — out loud.
 *
 * These modules already hold rich personal data (every garment you own, what is
 * in your pantry, where you are going) but the only way to use it was clicking
 * through forms. "What can I wear to a wedding in October?" is a question you
 * ask out loud, and the answer needs the wardrobe you actually have.
 *
 * Voice and text share one transcript. Typing is not a fallback — it is how
 * people use this on a train, and it skips the transcription cost entirely.
 */

const MAX_SECONDS = 60   // a question, not a monologue

type Module =
  | 'wardrobe' | 'kitchen' | 'travel' | 'bookshelf' | 'plant' | 'warranty' | 'medicine' | 'study'
  | 'bills' | 'expenses' | 'cosmos' | 'meetings'

interface Turn {
  role: 'you' | 'assistant'
  text: string
  disclaimer?: string
  withheld?: boolean
}

const PLACEHOLDERS: Record<Module, string> = {
  wardrobe: 'What can I wear to a wedding in October?',
  kitchen: 'What can I cook with what I have tonight?',
  travel: 'What should I not miss on my trip?',
  bookshelf: 'What should I read next?',
  plant: 'Which of my plants needs watering?',
  warranty: 'What cover is about to run out?',
  medicine: "What's expired in my cabinet?",
  study: 'Quiz me on what I saved',
  bills: "What's due this week?",
  expenses: 'Where did my money go last month?',
  cosmos: 'What did I note about this?',
  meetings: 'What did I agree to?',
}

const TITLES: Record<Module, string> = {
  wardrobe: 'Ask your wardrobe',
  kitchen: 'Ask your kitchen',
  travel: 'Ask your travel planner',
  bookshelf: 'Ask your shelf',
  plant: 'Ask your gardener',
  warranty: 'Ask about your cover',
  medicine: 'Ask about your cabinet',
  study: 'Ask your study partner',
  bills: 'Ask about your bills',
  expenses: 'Ask about your spending',
  cosmos: 'Ask your notes',
  meetings: 'Ask about your meetings',
}

/**
 * Domains where a wrong answer can hurt someone. The backend screens these and
 * fails closed; the UI marks them so the caution reads as part of the answer
 * rather than a footnote.
 */
const SENSITIVE: Module[] = ['kitchen', 'plant', 'medicine']

/**
 * What the browser currently thinks about our microphone permission.
 *
 * This matters because of a browser rule that is easy to miss: once the user
 * picks "Block", `getUserMedia` stops prompting and simply rejects. Calling it
 * again does nothing visible, so a "try again" button looks broken. The only
 * way out is for the user to change the setting themselves — so when we are in
 * that state we have to say so, and say exactly where.
 *
 * 'prompt' is different: the browser WILL ask again (the user dismissed the bar
 * rather than blocking), so retrying there is genuinely useful.
 */
type MicState = 'unknown' | 'prompt' | 'granted' | 'denied' | 'unsupported'

/** Per-browser recovery steps. Vague advice is useless at this point. */
function recoverySteps(): string[] {
  const ua = navigator.userAgent
  if (/Edg\//.test(ua)) {
    return [
      'Click the lock icon at the left of the address bar.',
      'Set Microphone to "Allow".',
      'Come back here and press the microphone again.',
    ]
  }
  if (/Firefox\//.test(ua)) {
    return [
      'Click the microphone (or lock) icon in the address bar.',
      'Remove the "Blocked" setting for this site.',
      'Press the microphone here again.',
    ]
  }
  if (/Safari\//.test(ua) && !/Chrome|Chromium|Edg\//.test(ua)) {
    return [
      'Open Safari → Settings for This Website…',
      'Set Microphone to "Allow".',
      'Press the microphone here again.',
    ]
  }
  // Chrome and Chromium-based default
  return [
    'Click the icon at the left of the address bar (a lock, sliders, or a crossed-out microphone).',
    'Set Microphone to "Allow".',
    'Press the microphone here again.',
  ]
}

export default function ModuleVoiceChat({ module }: { module: Module }) {
  const [turns, setTurns] = useState<Turn[]>([])
  const [recording, setRecording] = useState(false)
  const [seconds, setSeconds] = useState(0)
  const [busy, setBusy] = useState(false)
  const [text, setText] = useState('')
  const [error, setError] = useState('')
  const [levels, setLevels] = useState<number[]>(Array(20).fill(0.06))
  const [mic, setMic] = useState<MicState>('unknown')

  const recorderRef = useRef<MediaRecorder | null>(null)
  const chunksRef = useRef<Blob[]>([])
  const streamRef = useRef<MediaStream | null>(null)
  const ctxRef = useRef<AudioContext | null>(null)
  const rafRef = useRef<number | null>(null)
  const tickRef = useRef<ReturnType<typeof setInterval> | null>(null)
  const startedAtRef = useRef(0)
  const endRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => () => teardown(), [])
  useEffect(() => { endRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }) }, [turns, busy])

  // Watch the permission, so if the user goes to browser settings and flips it
  // to Allow, the panel clears itself instead of stranding them on an error
  // that no longer applies.
  useEffect(() => {
    if (!navigator.mediaDevices?.getUserMedia) { setMic('unsupported'); return }
    const perms = navigator.permissions
    if (!perms?.query) return   // Safari <16: state is only knowable by asking

    let status: PermissionStatus | null = null
    const onChange = () => {
      if (!status) return
      const s = status.state as MicState
      setMic(s)
      if (s === 'granted') setError('')
    }

    perms.query({ name: 'microphone' as PermissionName })
      .then(st => { status = st; onChange(); st.addEventListener('change', onChange) })
      .catch(() => { /* Firefox rejects for 'microphone' — fall back to asking */ })

    return () => status?.removeEventListener('change', onChange)
  }, [])

  const teardown = () => {
    if (rafRef.current) cancelAnimationFrame(rafRef.current)
    if (tickRef.current) clearInterval(tickRef.current)
    ctxRef.current?.close().catch(() => {})
    streamRef.current?.getTracks().forEach(t => t.stop())
    rafRef.current = null; tickRef.current = null
    ctxRef.current = null; streamRef.current = null
    setLevels(Array(20).fill(0.06))
  }

  const start = async () => {
    setError('')

    if (!navigator.mediaDevices?.getUserMedia) {
      setMic('unsupported')
      setError('This browser cannot record audio. Type your question instead.')
      return
    }

    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        audio: { echoCancellation: true, noiseSuppression: true, autoGainControl: true },
      })
      setMic('granted')
      streamRef.current = stream

      // Live level meter: without it people cannot tell the mic is hearing them
      // and re-record needlessly.
      const ctx = new AudioContext()
      ctxRef.current = ctx
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
      rec.onstop = () => void send()
      rec.start(1000)
      recorderRef.current = rec

      startedAtRef.current = Date.now()
      setSeconds(0)
      setRecording(true)
      tickRef.current = setInterval(() => {
        const s = (Date.now() - startedAtRef.current) / 1000
        setSeconds(s)
        if (s >= MAX_SECONDS) stop()
      }, 200)
    } catch (e) {
      const err = e as DOMException
      teardown()

      // NotAllowedError covers two very different situations, and telling them
      // apart is the whole point: if the user merely dismissed the prompt the
      // browser will ask again, but if they chose "Block" it never will — and
      // a retry button that silently does nothing is worse than no button.
      if (err.name === 'NotAllowedError' || err.name === 'SecurityError') {
        let blocked = true
        try {
          const st = await navigator.permissions?.query({ name: 'microphone' as PermissionName })
          blocked = st?.state === 'denied'
        } catch { /* Firefox: assume blocked and show the steps */ }

        setMic(blocked ? 'denied' : 'prompt')
        setError(blocked
          ? ''   // the recovery panel says it better than a one-liner
          : 'Microphone access was dismissed. Press the microphone again and choose Allow.')
        return
      }

      if (err.name === 'NotFoundError' || err.name === 'OverconstrainedError') {
        setError('No microphone was found. Plug one in, or type your question instead.')
        return
      }

      // Another app or tab holds the device — common on Windows.
      if (err.name === 'NotReadableError' || err.name === 'AbortError') {
        setError('Your microphone is in use by another app. Close it and try again.')
        return
      }

      setError('Could not start recording. Type your question instead.')
    }
  }

  const stop = () => {
    setRecording(false)
    if (recorderRef.current?.state === 'recording') recorderRef.current.stop()
    if (tickRef.current) clearInterval(tickRef.current)
  }

  const send = async () => {
    teardown()
    const blob = new Blob(chunksRef.current, { type: 'audio/webm' })
    chunksRef.current = []
    if (blob.size < 1200) { setError("That was too short — hold the button while you speak."); return }

    setBusy(true); setError('')
    try {
      const body = new FormData()
      body.append('audio', blob, 'ask.webm')
      const { data } = await api.post(`/module-voice/${module}`, body)
      addTurns(data)
    } catch (e) {
      const status = (e as { response?: { status?: number } })?.response?.status
      setError(
        status === 502 ? "We couldn't hear that clearly. Try again somewhere quieter."
        : status === 503 ? 'The assistant is not available right now. Try again shortly.'
        : status === 429 ? 'That is a lot of questions at once — wait a moment and try again.'
        : status === 401 ? 'Your session expired. Sign in again to keep asking.'
        : 'Could not get an answer. Try again.'
      )
    } finally {
      setBusy(false)
    }
  }

  const sendText = async () => {
    const q = text.trim()
    if (!q) return
    setText(''); setBusy(true); setError('')
    setTurns(prev => [...prev, { role: 'you', text: q }])
    try {
      const { data } = await api.post(`/module-voice/${module}/text`, { question: q })
      setTurns(prev => [...prev, {
        role: 'assistant', text: data.answer || data.message || '',
        disclaimer: data.disclaimer, withheld: data.withheld,
      }])
    } catch (e) {
      // Surface WHY. A 503 (assistant not configured) and a 429 (too many
      // questions) need different responses from the user, and "try again"
      // is wrong advice for both.
      const status = (e as { response?: { status?: number } })?.response?.status
      setError(
        status === 503 ? 'The assistant is not available right now. Try again shortly.'
        : status === 429 ? 'That is a lot of questions at once — wait a moment and try again.'
        : status === 401 ? 'Your session expired. Sign in again to keep asking.'
        : 'Could not get an answer. Try again.'
      )
      // Drop the orphaned question: leaving it makes the panel look like it
      // swallowed the message.
      setTurns(prev => prev.slice(0, -1))
    } finally {
      setBusy(false)
    }
  }

  const addTurns = (data: {
    question?: string; answer?: string; message?: string
    disclaimer?: string; withheld?: boolean
  }) => {
    const next: Turn[] = []
    if (data.question) next.push({ role: 'you', text: data.question })
    next.push({
      role: 'assistant',
      text: data.answer || data.message || '',
      disclaimer: data.disclaimer,
      withheld: data.withheld,
    })
    setTurns(prev => [...prev, ...next])
  }

  return (
    <section className={`mvc ${SENSITIVE.includes(module) ? 'mvc-care' : ''}`}>
      <header className="mvc-head">
        <h3><PhosphorIcon className="bi bi-chat-dots" /> {TITLES[module]}</h3>
        <small>Uses what you've saved here — not generic advice.</small>
      </header>

      {turns.length > 0 && (
        <div className="mvc-thread">
          {turns.map((t, i) => (
            <div key={i} className={`mvc-turn mvc-${t.role}`}>
              <div className={`mvc-bubble ${t.withheld ? 'mvc-withheld' : ''}`}>
                {/* An empty answer still has to say something. Previously a
                    blank reply rendered as an empty grey bubble, which looks
                    like the app broke rather than like a real outcome. */}
                {t.text || <em className="mvc-empty">No answer came back — try asking again.</em>}
                {/* Server-enforced, never model-generated, never collapsed. */}
                {t.disclaimer && (
                  <p className="mvc-disclaimer">
                    <PhosphorIcon className="bi bi-info-circle" /> {t.disclaimer}
                  </p>
                )}
              </div>
            </div>
          ))}
          <div ref={endRef} />
        </div>
      )}

      {busy && <div className="mvc-busy"><span className="mvc-spin" /> Thinking…</div>}

      {recording && (
        <div className="mvc-meter">
          {levels.map((l, i) => <i key={i} style={{ height: `${Math.round(l * 100)}%` }} />)}
          <span className="mvc-secs">{Math.floor(seconds)}s</span>
        </div>
      )}

      {/* Blocked is a dead end for getUserMedia — the browser will not prompt
          again no matter how many times we ask. So instead of an error and a
          useless retry, show exactly where the setting is. */}
      {mic === 'denied' && (
        <div className="mvc-blocked">
          <strong>
            <PhosphorIcon className="bi bi-microphone-mute" /> Microphone is blocked for this site
          </strong>
          <p>Your browser won’t ask again until you change it:</p>
          <ol>
            {recoverySteps().map((s, i) => <li key={i}>{s}</li>)}
          </ol>
          <div className="mvc-blocked-actions">
            <button className="mvc-retry" onClick={start}>I’ve allowed it — try again</button>
            <span>or just type your question below</span>
          </div>
        </div>
      )}

      {error && <p className="mvc-error">{error}</p>}

      <div className="mvc-controls">
        <button
          className={`mvc-mic ${recording ? 'mvc-mic-on' : ''} ${mic === 'denied' ? 'mvc-mic-blocked' : ''}`}
          onClick={recording ? stop : start}
          disabled={busy || mic === 'unsupported'}
          title={
            mic === 'denied' ? 'Microphone blocked — click for how to fix it'
            : mic === 'unsupported' ? 'This browser cannot record audio'
            : recording ? 'Stop and send'
            : 'Ask out loud'
          }
        >
          <PhosphorIcon className={`bi ${
            recording ? 'bi-stop-fill'
            : mic === 'denied' || mic === 'unsupported' ? 'bi-mic-mute-fill'
            : 'bi-mic-fill'
          }`} />
        </button>

        <input
          type="text"
          className="mvc-input"
          placeholder={PLACEHOLDERS[module]}
          value={text}
          disabled={busy || recording}
          onChange={e => setText(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter') sendText() }}
        />

        <button className="mvc-send" onClick={sendText} disabled={busy || recording || !text.trim()}>
          <PhosphorIcon className="bi bi-arrow-up" />
        </button>
      </div>
    </section>
  )
}
