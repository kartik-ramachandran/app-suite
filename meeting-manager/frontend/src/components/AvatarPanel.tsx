import PhosphorIcon from './PhosphorIcon'
import { useState, useRef, useEffect } from 'react'
import api from '../services/api'

interface AvatarPanelProps {
  mode?: 'calendar' | 'general'
  systemPrompt?: string
  greeting?: string
  onTranscript?: (messages: TranscriptMsg[]) => void
  compact?: boolean
}

interface TranscriptMsg { role: 'user' | 'agent'; text: string }

const LISA_STYLES = ['casual-sitting', 'graceful-sitting', 'graceful-standing', 'technical-sitting', 'technical-standing']

export default function AvatarPanel({ mode = 'calendar', systemPrompt, greeting, onTranscript, compact = false }: AvatarPanelProps) {
  const [state, setState] = useState<'idle' | 'connecting' | 'active'>('idle')
  const [selectedStyle, setSelectedStyle] = useState('casual-sitting')
  const [transcript, setTranscript] = useState<TranscriptMsg[]>([])
  const [isAgentSpeaking, setIsAgentSpeaking] = useState(false)
  const [isListening, setIsListening] = useState(false)
  const [error, setError] = useState('')
  const [dailyBriefing, setDailyBriefing] = useState<string | null>(null)

  const videoRef = useRef<HTMLVideoElement>(null)
  const transcriptRef = useRef<HTMLDivElement>(null)
  const synthRef = useRef<any>(null)
  const recognizerRef = useRef<any>(null)
  const sdkRef = useRef<any>(null)
  const sessionIdRef = useRef('')

  useEffect(() => {
    loadDailyBriefing()
  }, [])

  useEffect(() => {
    if (transcriptRef.current)
      transcriptRef.current.scrollTop = transcriptRef.current.scrollHeight
    onTranscript?.(transcript)
  }, [transcript])

  const loadDailyBriefing = async () => {
    try {
      const tz = Intl.DateTimeFormat().resolvedOptions().timeZone
      const res = await api.get(`/avatar/daily-briefing?timezone=${encodeURIComponent(tz)}`)
      setDailyBriefing(res.data.spoken)
    } catch { /* non-fatal */ }
  }

  const loadSdk = async (): Promise<any> => {
    if ((window as any).SpeechSDK) return (window as any).SpeechSDK
    return new Promise((resolve, reject) => {
      const script = document.createElement('script')
      script.src = 'https://aka.ms/csspeech/jsbrowserpackageraw'
      script.onload = () => resolve((window as any).SpeechSDK)
      script.onerror = () => reject(new Error('Failed to load Azure Speech SDK'))
      document.head.appendChild(script)
    })
  }

  const addMessage = (role: 'user' | 'agent', text: string) => {
    setTranscript(prev => [...prev, { role, text }])
  }

  const speakText = async (text: string) => {
    if (!synthRef.current || !sdkRef.current) return
    setIsAgentSpeaking(true)
    try {
      await new Promise<void>((resolve, reject) => {
        synthRef.current.speakTextAsync(
          text,
          (result: any) => {
            if (result?.reason === sdkRef.current.ResultReason.SynthesizingAudioCompleted) resolve()
            else reject(new Error(result?.errorDetails ?? 'Avatar synthesis failed'))
          },
          (err: any) => reject(new Error(typeof err === 'string' ? err : JSON.stringify(err)))
        )
      })
    } catch (e: any) {
      setError(e?.message ?? 'Avatar speech failed')
    } finally {
      setIsAgentSpeaking(false)
    }
  }

  const sendToLlm = async (userText: string) => {
    setIsListening(false)
    setIsAgentSpeaking(true)
    try {
      const { data } = await api.post('/avatar/llm', {
        sessionId: sessionIdRef.current,
        userText,
        systemPrompt,
        mode,
      })
      if (data.reply) {
        addMessage('agent', data.reply)
        await speakText(data.reply)
      }
    } catch { /* silently ignore */ }
    finally {
      setIsAgentSpeaking(false)
      setIsListening(true)
    }
  }

  const startCall = async () => {
    setError('')
    setState('connecting')

    try {
      const { data } = await api.get('/avatar/token', {
        params: { avatarCharacter: 'lisa', avatarStyle: selectedStyle }
      })
      sessionIdRef.current = data.sessionId

      let iceServers: RTCIceServer[] = []
      try {
        const { data: ice } = await api.get('/avatar/ice-token')
        iceServers = [{ urls: ice.Urls, username: ice.Username, credential: ice.Password }]
      } catch (iceErr: any) {
        throw new Error(`Azure relay ICE token failed: ${iceErr?.response?.data?.message ?? iceErr?.message}`)
      }

      const SDK = await loadSdk()
      sdkRef.current = SDK

      const speechConfig = SDK.SpeechConfig.fromAuthorizationToken(data.token, data.region)
      speechConfig.speechSynthesisVoiceName = data.ttsVoice

      const avatarConfig = new SDK.AvatarConfig(data.avatarCharacter, data.avatarStyle)
      const peerConnection = new RTCPeerConnection({ iceServers })

      peerConnection.ontrack = (ev: RTCTrackEvent) => {
        if (ev.streams.length > 0 && videoRef.current && !videoRef.current.srcObject) {
          videoRef.current.srcObject = ev.streams[0]
          videoRef.current.play().catch(() => {})
        }
      }

      peerConnection.addTransceiver('video', { direction: 'sendrecv' })
      peerConnection.addTransceiver('audio', { direction: 'sendrecv' })

      synthRef.current = new SDK.AvatarSynthesizer(speechConfig, avatarConfig)

      const startResult = await synthRef.current.startAvatarAsync(peerConnection)
      if (startResult?.reason === SDK.ResultReason.Canceled)
        throw new Error(`Avatar failed to start: ${startResult.errorDetails || 'service unavailable'}`)

      const audioConfig = SDK.AudioConfig.fromDefaultMicrophoneInput()
      recognizerRef.current = new SDK.SpeechRecognizer(speechConfig, audioConfig)
      recognizerRef.current.recognized = async (_: any, e: any) => {
        const text = e.result?.text?.trim()
        if (!text) return
        addMessage('user', text)
        await sendToLlm(text)
      }
      recognizerRef.current.startContinuousRecognitionAsync()
      setIsListening(true)
      setState('active')

      const greetText = greeting ?? dailyBriefing ?? "Hello! I'm your personal AI work agent. How can I help you today?"
      addMessage('agent', greetText)
      await speakText(greetText)
    } catch (err: any) {
      setError(err?.message ?? 'Failed to start avatar. Check Azure Speech configuration.')
      setState('idle')
    }
  }

  const endCall = async () => {
    setIsListening(false)
    setIsAgentSpeaking(false)
    try {
      recognizerRef.current?.stopContinuousRecognitionAsync()
      recognizerRef.current?.close()
      await synthRef.current?.stopAvatarAsync()
      synthRef.current?.close()
    } catch { /* ignore cleanup */ }
    recognizerRef.current = null
    synthRef.current = null
    if (videoRef.current) videoRef.current.srcObject = null
    if (sessionIdRef.current) {
      await api.delete(`/avatar/session/${sessionIdRef.current}`).catch(() => {})
      sessionIdRef.current = ''
    }
    setState('idle')
  }

  if (compact) return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div style={{
        position: 'relative', background: '#0a0a0a', borderRadius: 10,
        overflow: 'hidden', aspectRatio: '4/3', minHeight: 140,
        display: 'flex', alignItems: 'center', justifyContent: 'center'
      }}>
        {state === 'idle' && (
          <div style={{ textAlign: 'center', color: '#aaa' }}>
            <PhosphorIcon className="bi bi-person-video2" style={{ fontSize: 28, display: 'block', marginBottom: 6 }} />
            <div style={{ fontSize: 11 }}>Avatar ready</div>
          </div>
        )}
        {state === 'connecting' && <div className="spinner" style={{ borderColor: 'rgba(255,255,255,0.2)', borderTopColor: '#fff', width: 24, height: 24 }} />}
        <video ref={videoRef} autoPlay playsInline style={{ width: '100%', height: '100%', objectFit: 'cover', display: state === 'active' ? 'block' : 'none' }} />
        {state === 'active' && (
          <div style={{ position: 'absolute', bottom: 6, left: 8, background: 'rgba(0,0,0,0.6)', borderRadius: 12, padding: '3px 10px', fontSize: 10, color: '#fff', display: 'flex', alignItems: 'center', gap: 5 }}>
            <span style={{ width: 6, height: 6, borderRadius: '50%', background: isAgentSpeaking ? '#22c55e' : '#888', animation: isAgentSpeaking ? 'pulse 1s infinite' : 'none' }} />
            {isAgentSpeaking ? 'Speaking' : isListening ? 'Listening' : 'Idle'}
          </div>
        )}
      </div>
      {error && <div style={{ fontSize: 11, color: '#ef4444', padding: '4px 8px', background: '#ef444410', borderRadius: 6 }}>{error}</div>}
      {state === 'idle'
        ? <button className="btn btn-primary" style={{ fontSize: 12, height: 32 }} onClick={startCall}>
            <PhosphorIcon className="bi bi-play-fill" />Start Briefing
          </button>
        : <button className="btn btn-secondary" style={{ fontSize: 12, height: 32, color: '#ef4444' }} onClick={endCall}>
            <PhosphorIcon className="bi bi-stop-fill" />End
          </button>
      }
    </div>
  )

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 300px', gap: 16, alignItems: 'start' }}>

        {/* Avatar video */}
        <div style={{ position: 'relative', background: '#0a0a0a', borderRadius: 12, overflow: 'hidden', aspectRatio: '16/9', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
          {state === 'idle' && (
            <div style={{ textAlign: 'center', color: '#aaa', padding: '2rem' }}>
              <PhosphorIcon className="bi bi-person-video2" style={{ fontSize: 48, display: 'block', marginBottom: 12, color: 'var(--primary)' }} />
              <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4, color: '#fff' }}>Lisa — Personal Work Agent</div>
              <div style={{ fontSize: 12 }}>Connect Azure Speech to activate avatar</div>
            </div>
          )}
          {state === 'connecting' && (
            <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', gap: 12 }}>
              <div className="spinner" style={{ width: 36, height: 36, borderWidth: 3, borderColor: 'rgba(255,255,255,0.2)', borderTopColor: 'var(--primary)' }} />
              <div style={{ color: '#aaa', fontSize: 13 }}>Connecting avatar…</div>
            </div>
          )}
          <video ref={videoRef} autoPlay playsInline style={{ width: '100%', height: '100%', objectFit: 'cover', display: state === 'active' ? 'block' : 'none' }} />
          {state === 'active' && (
            <div style={{ position: 'absolute', bottom: 12, left: 12, background: 'rgba(0,0,0,0.65)', backdropFilter: 'blur(8px)', borderRadius: 20, padding: '5px 12px', fontSize: 12, color: '#fff', display: 'flex', alignItems: 'center', gap: 7 }}>
              <span style={{ width: 8, height: 8, borderRadius: '50%', background: isAgentSpeaking ? '#22c55e' : '#888', flexShrink: 0, animation: isAgentSpeaking ? 'pulse 1s infinite' : 'none' }} />
              {isAgentSpeaking ? 'Agent speaking' : isListening ? 'Listening…' : 'Idle'}
            </div>
          )}
        </div>

        {/* Controls */}
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {/* Style picker */}
          {state === 'idle' && (
            <div className="card" style={{ padding: '12px 14px' }}>
              <div style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', letterSpacing: 0.8, marginBottom: 8 }}>AVATAR STYLE</div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
                {LISA_STYLES.map(s => (
                  <button key={s} onClick={() => setSelectedStyle(s)} style={{
                    textAlign: 'left', padding: '5px 8px', borderRadius: 6, fontSize: 12, cursor: 'pointer',
                    background: selectedStyle === s ? 'var(--primary)' : 'transparent',
                    color: selectedStyle === s ? '#fff' : 'var(--text-secondary)',
                    border: 'none', fontWeight: selectedStyle === s ? 600 : 400,
                  }}>
                    {s.split('-').map(w => w.charAt(0).toUpperCase() + w.slice(1)).join(' ')}
                  </button>
                ))}
              </div>
            </div>
          )}

          {/* Transcript */}
          <div className="card" style={{ padding: '12px 14px', flex: 1 }}>
            <div style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', letterSpacing: 0.8, marginBottom: 8 }}>TRANSCRIPT</div>
            <div ref={transcriptRef} style={{ height: 220, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 8 }}>
              {transcript.length === 0
                ? <div style={{ color: 'var(--text-muted)', fontSize: 12, textAlign: 'center', marginTop: 32 }}>Conversation will appear here…</div>
                : transcript.map((m, i) => (
                  <div key={i} style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
                    <span style={{ fontSize: 10, fontWeight: 700, textTransform: 'uppercase', letterSpacing: 0.5, color: m.role === 'user' ? 'var(--primary)' : 'var(--text-muted)' }}>{m.role === 'user' ? 'You' : 'Agent'}</span>
                    <span style={{ fontSize: 12, color: 'var(--text-primary)', lineHeight: 1.5 }}>{m.text}</span>
                  </div>
                ))
              }
            </div>
          </div>

          {error && <div style={{ padding: '8px 12px', background: '#ef444410', border: '1px solid #ef444430', borderRadius: 8, fontSize: 12, color: '#ef4444' }}>{error}</div>}

          {state === 'idle'
            ? <button className="btn btn-primary" style={{ height: 40 }} onClick={startCall}>
                <PhosphorIcon className="bi bi-play-circle" />Start Avatar Session
              </button>
            : state === 'connecting'
              ? <button className="btn btn-secondary" style={{ height: 40 }} disabled><div className="spinner" style={{ width: 16, height: 16 }} />Connecting…</button>
              : <button style={{ height: 40, borderRadius: 8, border: 'none', background: '#ef4444', color: '#fff', fontSize: 14, fontWeight: 600, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8 }} onClick={endCall}>
                  <PhosphorIcon className="bi bi-stop-circle" />End Session
                </button>
          }
        </div>
      </div>

      {state === 'idle' && (
        <div style={{ padding: '8px 12px', background: 'var(--bg-secondary)', borderRadius: 8, fontSize: 12, color: 'var(--text-muted)', display: 'flex', alignItems: 'center', gap: 8 }}>
          <PhosphorIcon className="bi bi-info-circle" style={{ flexShrink: 0 }} />
          Start a session to talk with a lifelike AI avatar — it listens, thinks, and answers out loud.
        </div>
      )}
    </div>
  )
}
