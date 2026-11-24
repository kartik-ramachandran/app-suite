import { useCallback, useEffect, useRef, useState } from 'react'
import api from '../services/api'
import { writeLifeVoiceEntry, type LifeVoiceEntry } from '../services/api'
import PhosphorIcon from './PhosphorIcon'
import './LifeVoiceAgent.css'

/**
 * Talk it through: a short spoken conversation with a journalling companion.
 *
 * Listening and speaking happen in the browser (Web Speech API), so no audio
 * leaves the device and no transcription key is needed — only a chat model,
 * which can be a free local Ollama model. When the conversation ends it is
 * filed through the normal journal pipeline, so the user still reviews what
 * gets added to their tracker.
 */

interface Turn { role: 'agent' | 'user'; text: string }

type Phase = 'idle' | 'thinking' | 'speaking' | 'listening' | 'saving'

// The Web Speech API isn't in TypeScript's DOM types.
interface SpeechResultEvent { resultIndex: number; results: ArrayLike<{ isFinal: boolean; 0: { transcript: string } }> }
interface Recognizer {
  lang: string; continuous: boolean; interimResults: boolean
  onresult: ((e: SpeechResultEvent) => void) | null
  onerror: ((e: { error: string }) => void) | null
  onend: (() => void) | null
  start(): void; stop(): void; abort(): void
}
type RecognizerCtor = new () => Recognizer

const getRecognizer = (): RecognizerCtor | null => {
  const w = window as unknown as { SpeechRecognition?: RecognizerCtor; webkitSpeechRecognition?: RecognizerCtor }
  return w.SpeechRecognition ?? w.webkitSpeechRecognition ?? null
}

const MOODS = ['😊 Good', '😐 Okay', '😔 Low', '😤 Stressed', '🤩 Great']

/** A natural-sounding voice in the user's language, if the device has one. */
function pickVoice(): SpeechSynthesisVoice | undefined {
  const voices = window.speechSynthesis?.getVoices() ?? []
  const lang = (navigator.language || 'en').slice(0, 2)
  const mine = voices.filter(v => v.lang.toLowerCase().startsWith(lang))
  return mine.find(v => /natural|neural|online|google/i.test(v.name)) ?? mine.find(v => v.default) ?? mine[0] ?? voices[0]
}

type Props = { onSaved?: (entry: LifeVoiceEntry) => void }

export default function LifeVoiceAgent({ onSaved }: Props) {
  const [open, setOpen] = useState(false)
  const [turns, setTurns] = useState<Turn[]>([])
  const [phase, setPhase] = useState<Phase>('idle')
  const [interim, setInterim] = useState('')
  const [typed, setTyped] = useState('')
  const [mood, setMood] = useState<string | null>(null)
  const [voiceOn, setVoiceOn] = useState(true)
  const [ended, setEnded] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState<LifeVoiceEntry | null>(null)

  const recRef = useRef<Recognizer | null>(null)
  const heardRef = useRef('')
  const turnsRef = useRef<Turn[]>([])
  const endRef = useRef<HTMLDivElement>(null)
  const canListen = !!getRecognizer()
  const canSpeak = typeof window !== 'undefined' && 'speechSynthesis' in window

  useEffect(() => { turnsRef.current = turns }, [turns])
  useEffect(() => { endRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }) }, [turns, interim, phase])

  const stopAll = useCallback(() => {
    recRef.current?.abort()
    recRef.current = null
    if (canSpeak) window.speechSynthesis.cancel()
  }, [canSpeak])

  useEffect(() => stopAll, [stopAll])

  const speak = (text: string) => new Promise<void>(resolve => {
    if (!voiceOn || !canSpeak) { resolve(); return }
    window.speechSynthesis.cancel()
    const u = new SpeechSynthesisUtterance(text)
    const v = pickVoice()
    if (v) u.voice = v
    u.rate = 1.0
    u.onend = () => resolve()
    u.onerror = () => resolve()
    setPhase('speaking')
    window.speechSynthesis.speak(u)
  })

  const listen = () => {
    const Ctor = getRecognizer()
    if (!Ctor) return
    const rec = new Ctor()
    rec.lang = navigator.language || 'en-US'
    rec.continuous = true
    rec.interimResults = true
    heardRef.current = ''
    rec.onresult = e => {
      let finalText = '', partial = ''
      for (let i = 0; i < e.results.length; i++) {
        const r = e.results[i]
        if (r.isFinal) finalText += r[0].transcript + ' '
        else partial += r[0].transcript
      }
      heardRef.current = finalText.trim()
      setInterim((finalText + partial).trim())
    }
    rec.onerror = ev => {
      if (ev.error === 'not-allowed' || ev.error === 'service-not-allowed')
        setError('Microphone access was blocked. Allow it in your browser, or type your answers below.')
      else if (ev.error !== 'no-speech' && ev.error !== 'aborted')
        setError('The microphone stopped listening. Tap the mic to try again, or type instead.')
    }
    rec.onend = () => { if (recRef.current === rec) { recRef.current = null; setPhase(p => (p === 'listening' ? 'idle' : p)) } }
    recRef.current = rec
    setInterim('')
    setPhase('listening')
    try { rec.start() } catch { setPhase('idle') }
  }

  /** Ask the agent for its next line, show it, say it, then listen. */
  const agentTurn = async (history: Turn[], chosenMood: string | null) => {
    setPhase('thinking'); setError(null)
    try {
      const res = await api.post<{ reply: string; done?: boolean; safetyStop?: boolean }>(
        '/lifetracker/voice-agent/reply', { turns: history, mood: chosenMood }, { timeout: 600000 })
      const next = [...history, { role: 'agent' as const, text: res.data.reply }]
      setTurns(next)
      await speak(res.data.reply)
      if (res.data.done || res.data.safetyStop) { setEnded(true); setPhase('idle'); return }
      if (canListen) listen()
      else setPhase('idle')
    } catch (e) {
      const ax = e as { response?: { data?: { error?: string } } }
      setError(ax.response?.data?.error ?? 'The companion didn’t answer. Try again.')
      setPhase('idle')
    }
  }

  const start = async (chosenMood: string | null) => {
    setMood(chosenMood); setTurns([]); setEnded(false); setSaved(null); setError(null)
    await agentTurn([], chosenMood)
  }

  /** Finish the user's turn (from voice or typing) and pass it to the agent. */
  const sendAnswer = async (text: string) => {
    const answer = text.trim()
    recRef.current?.stop()
    recRef.current = null
    setInterim('')
    if (!answer) { setPhase('idle'); return }
    const next = [...turnsRef.current, { role: 'user' as const, text: answer }]
    setTurns(next)
    setTyped('')
    await agentTurn(next, mood)
  }

  const finishListening = () => sendAnswer(heardRef.current || interim)

  const save = async () => {
    stopAll()
    const said = turns.filter(t => t.role === 'user')
    if (said.length === 0) { setError('Say or type at least one answer first.'); return }
    setPhase('saving'); setError(null)
    const text = turns
      .map(t => (t.role === 'agent' ? `(Asked: ${t.text})` : t.text))
      .join('\n')
    try {
      const res = await writeLifeVoiceEntry(text)
      setSaved(res.data)
      setEnded(true)
      onSaved?.(res.data)
    } catch (e) {
      const ax = e as { response?: { data?: { error?: string } } }
      setError(ax.response?.data?.error ?? 'Could not save this conversation.')
    } finally { setPhase('idle') }
  }

  const close = () => { stopAll(); setOpen(false); setTurns([]); setPhase('idle'); setEnded(false); setSaved(null); setError(null) }

  if (!open) {
    return (
      <button className="lva-launch" onClick={() => setOpen(true)}>
        <span className="lva-launch-orb"><PhosphorIcon className="bi bi-soundwave" /></span>
        <span><strong>Talk it through</strong><em>A short spoken chat that asks the right questions</em></span>
      </button>
    )
  }

  const busy = phase === 'thinking' || phase === 'saving'
  const userTurns = turns.filter(t => t.role === 'user').length

  return (
    <div className="lva-root">
      <div className="lva-top">
        <div className={`lva-orb lva-${phase}`} aria-hidden="true"><span /><span /><span /></div>
        <div className="lva-status">
          <strong>
            {phase === 'thinking' ? 'Thinking…' : phase === 'speaking' ? 'Speaking…' : phase === 'listening' ? 'Listening…'
              : phase === 'saving' ? 'Saving your entry…' : ended ? 'All done' : turns.length ? 'Your turn' : 'Ready when you are'}
          </strong>
          <span>{canListen ? 'Your voice stays in this browser.' : 'Voice isn’t supported in this browser, so type your answers.'}</span>
        </div>
        {canSpeak && (
          <button className="lva-icon" onClick={() => { setVoiceOn(v => !v); window.speechSynthesis.cancel() }}
            title={voiceOn ? 'Mute the companion' : 'Read replies aloud'}>
            <PhosphorIcon className={`bi ${voiceOn ? 'bi-volume-up-fill' : 'bi-volume-mute-fill'}`} />
          </button>
        )}
        <button className="lva-icon" onClick={close} title="Close"><PhosphorIcon className="bi bi-x-lg" /></button>
      </div>

      {turns.length === 0 && !busy ? (
        <div className="lva-start">
          <p>How are you arriving today?</p>
          <div className="lva-moods">
            {MOODS.map(m => <button key={m} onClick={() => start(m)}>{m}</button>)}
          </div>
          <button className="lva-skip" onClick={() => start(null)}>Just start</button>
        </div>
      ) : (
        <div className="lva-thread">
          {turns.map((t, i) => (
            <div key={i} className={`lva-bubble lva-${t.role}`}>{t.text}</div>
          ))}
          {phase === 'listening' && interim && <div className="lva-bubble lva-user lva-interim">{interim}</div>}
          {phase === 'thinking' && <div className="lva-bubble lva-agent lva-dots"><span /><span /><span /></div>}
          <div ref={endRef} />
        </div>
      )}

      {turns.length > 0 && !saved && (
        <div className="lva-controls">
          {!ended && canListen && (
            phase === 'listening'
              ? <button className="lva-mic lva-mic-on" onClick={finishListening}><PhosphorIcon className="bi bi-check-lg" /> Done talking</button>
              : <button className="lva-mic" onClick={listen} disabled={busy || phase === 'speaking'}><PhosphorIcon className="bi bi-mic-fill" /> Answer</button>
          )}
          {!ended && (
            <form className="lva-type" onSubmit={e => { e.preventDefault(); sendAnswer(typed) }}>
              <input value={typed} onChange={e => setTyped(e.target.value)} placeholder="…or type your answer" disabled={busy} />
              <button type="submit" disabled={!typed.trim() || busy} aria-label="Send"><PhosphorIcon className="bi bi-arrow-up" /></button>
            </form>
          )}
          <button className="lva-save" onClick={save} disabled={busy || userTurns === 0}>
            <PhosphorIcon className="bi bi-journal-check" /> {ended ? 'Save as today’s entry' : 'Finish & save'}
          </button>
        </div>
      )}

      {saved && (
        <div className="lva-saved">
          <PhosphorIcon className="bi bi-check-circle-fill" />
          <div>
            <strong>Saved: {saved.headline || 'today’s entry'}</strong>
            <span>Review what to add to your tracker in “Recent entries” below.</span>
          </div>
          <button className="lva-skip" onClick={() => start(null)}>Talk again</button>
        </div>
      )}

      {error && <div className="lva-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
    </div>
  )
}
