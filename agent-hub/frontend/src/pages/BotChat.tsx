import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useRef, useCallback, useLayoutEffect, useMemo } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import { usePageMeta } from '../utils/usePageMeta'
import { BOT_AGENTS } from './BotAgents'
import api from '../services/api'
import './AgentChat.css'
import './BotChat.css'

const API = import.meta.env.VITE_API_URL ??
  (typeof window !== 'undefined' && window.location.hostname !== 'localhost'
    ? window.location.origin : 'http://localhost:5220')

interface Message {
  role: 'user' | 'assistant'
  content: string
  streaming?: boolean
}

interface ChatSession {
  sessionId: string
  title: string
  botType?: string | null
  updatedAt: string
  messageCount: number
}

const ALLOWED_TYPES = [
  'image/jpeg', 'image/png', 'image/webp', 'image/gif',
  'application/pdf', 'text/plain',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  'application/msword',
]

export default function BotChat() {
  const { botType } = useParams<{ botType: string }>()
  const navigate = useNavigate()
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('bot-chat')

  const bot = BOT_AGENTS.find(b => b.id === botType)

  usePageMeta(
    bot ? `${bot.name} – GenPrompt Bot Agents` : 'Bot Chat – GenPrompt',
    bot ? `${bot.tagline}. ${bot.description} Powered by GenPrompt.` : 'Chat with a specialised AI bot on GenPrompt.'
  )
  useLayoutEffect(() => {
    setBreadcrumbs([
      { label: 'Dashboard', path: '/dashboard' },
      { label: 'Bot Agents', path: '/bot-agents' },
      { label: bot?.name ?? 'Bot Chat' },
    ])
    return () => setBreadcrumbs([])
  }, [bot])

  const [messages, setMessages] = useState<Message[]>([])
  const [input, setInput] = useState('')
  const [sending, setSending] = useState(false)
  const [files, setFiles] = useState<File[]>([])
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [suggestedIdx, setSuggestedIdx] = useState<number | null>(null)
  const [sessions, setSessions] = useState<ChatSession[]>([])
  const [historyOpen, setHistoryOpen] = useState(false)

  const messagesEndRef = useRef<HTMLDivElement>(null)
  const messagesContainerRef = useRef<HTMLDivElement>(null)
  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const abortRef = useRef<AbortController | null>(null)

  const token = localStorage.getItem('authToken')
  const authHeader = { Authorization: `Bearer ${token ?? ''}` }

  useEffect(() => {
    const container = messagesContainerRef.current
    if (container) container.scrollTop = container.scrollHeight
  }, [messages])

  // ── Recent chats (history) for this bot ──
  const loadSessions = useCallback(async () => {
    try {
      const { data } = await api.get('/agentchat/sessions', { params: { botType } })
      setSessions(data)
    } catch { /* ignore */ }
  }, [botType])

  useEffect(() => { void loadSessions() }, [loadSessions])

  const openSession = async (id: string) => {
    setHistoryOpen(false)
    if (id === sessionId) return
    try {
      const { data } = await api.get(`/agentchat/sessions/${id}/messages`)
      setMessages((data.messages ?? []).map((m: { role: string; content: string }) => ({ role: m.role as 'user' | 'assistant', content: m.content })))
      setSessionId(id)
    } catch { /* ignore */ }
  }

  const startNewChat = () => { setMessages([]); setSessionId(null); setHistoryOpen(false) }

  const handleInputChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    setInput(e.target.value)
    e.target.style.height = 'auto'
    e.target.style.height = Math.min(e.target.scrollHeight, 160) + 'px'
  }

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      void send()
    }
  }

  const addFiles = (incoming: FileList | File[]) => {
    const valid = Array.from(incoming).filter(f => ALLOWED_TYPES.includes(f.type) && f.size <= 104_857_600)
    setFiles(prev => [...prev, ...valid].slice(0, 5))
  }

  const send = useCallback(async (textOverride?: string) => {
    const text = (textOverride ?? input).trim()
    if (!text && files.length === 0) return
    if (sending) return

    setSending(true)
    setSuggestedIdx(null)

    const attachmentLabel = files.length > 0 ? files.map(f => `📎 ${f.name}`).join('  ') : ''
    const userContent = [text, attachmentLabel].filter(Boolean).join('\n\n')
    setMessages(prev => [...prev, { role: 'user', content: userContent }, { role: 'assistant', content: '', streaming: true }])
    setInput('')
    setFiles([])
    if (textareaRef.current) textareaRef.current.style.height = 'auto'

    const form = new FormData()
    if (sessionId) form.append('sessionId', sessionId)
    form.append('message', text)
    if (botType) form.append('botType', botType)
    files.forEach(f => form.append('files', f))

    abortRef.current = new AbortController()

    try {
      const res = await fetch(`${API}/api/agentchat/send`, {
        method: 'POST',
        headers: authHeader,
        body: form,
        signal: abortRef.current.signal,
      })

      const newSid = res.headers.get('X-Session-Id')
      if (newSid && !sessionId) setSessionId(newSid)

      if (!res.body) throw new Error('No response body')

      const reader = res.body.getReader()
      const decoder = new TextDecoder()
      let buf = ''
      let currentEvent = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buf += decoder.decode(value, { stream: true })
        const lines = buf.split('\n')
        buf = lines.pop() ?? ''

        for (const line of lines) {
          if (line.startsWith('event: ')) { currentEvent = line.slice(7).trim(); continue }
          if (!line.startsWith('data: ')) continue
          try {
            const payload = JSON.parse(line.slice(6))
            if (payload.text && currentEvent === 'chunk') {
              setMessages(prev => {
                const updated = [...prev]
                const last = updated[updated.length - 1]
                if (last?.role === 'assistant') updated[updated.length - 1] = { ...last, content: last.content + payload.text }
                return updated
              })
            }
            currentEvent = ''
          } catch { /* skip */ }
        }
      }
    } catch (err: any) {
      if (err.name !== 'AbortError') {
        setMessages(prev => {
          const updated = [...prev]
          const last = updated[updated.length - 1]
          if (last?.role === 'assistant') updated[updated.length - 1] = { ...last, content: 'Something went wrong. Please try again.' }
          return updated
        })
      }
    } finally {
      setMessages(prev => prev.map(m => ({ ...m, streaming: false })))
      setSending(false)
      void loadSessions()
    }
  }, [input, files, sending, sessionId, botType, loadSessions])

  if (!bot) {
    return (
      <div style={{ padding: 48, textAlign: 'center' }}>
        <PhosphorIcon className="bi bi-robot" style={{ fontSize: 48, color: 'var(--text-secondary)' }} />
        <p style={{ color: 'var(--text-secondary)', marginTop: 12 }}>Bot not found.</p>
        <button className="btn btn-primary" style={{ marginTop: 12 }} onClick={() => navigate('/bot-agents')}>
          Back to Bot Agents
        </button>
      </div>
    )
  }

  const isEmpty = messages.length === 0

  // Gamefield: the bot at its desk with the conversation as the work in hand.
  const field = useMemo<FieldModel>(() => {
    const turns = messages.slice(-6)
    const lastReply = [...messages].reverse().find(m => m.role === 'assistant')

    return {
      title: bot.name,
      subtitle: bot.tagline,
      running: sending,
      agents: [
        ...turns.map((m, i) => ({
          id: `turn-${i}`,
          name: m.role === 'user' ? 'You' : 'Reply',
          role: m.role === 'user' ? 'asked' : 'answered',
          status: 'done' as const,
          detail: bubble(m.content),
          output: m.content,
        })),
        {
          id: 'bot',
          name: bot.name,
          role: `${messages.length} messages${files.length ? ` · ${files.length} files` : ''}`,
          status: (sending ? 'working' : lastReply ? 'done' : 'idle') as 'working' | 'done' | 'idle',
          detail: sending ? 'Typing a reply…' : bubble(lastReply?.content),
          output: lastReply?.content,
          boss: true,
        },
      ],
      messages: turns
        .map((m, i) => (m.role === 'user' ? { id: `to-bot-${i}`, from: `turn-${i}`, to: 'bot' } : { id: `from-bot-${i}`, from: 'bot', to: `turn-${i}` })),
      events: [],
    }
  }, [messages, sending, files])

  return (
    <div className="bc-root">
      <AgentStage {...stageProps} model={field} height={340} />

      {/* Bot header */}
      <div className="bc-header">
        <button className="bc-back-btn" onClick={() => navigate('/bot-agents')}>
          <PhosphorIcon className="bi bi-arrow-left" />
        </button>
        <div className="bc-bot-icon"><PhosphorIcon className={`bi ${bot.emoji}`} /></div>
        <div className="bc-bot-info">
          <div className="bc-bot-name">{bot.name}</div>
          <div className="bc-bot-tagline">{bot.tagline}</div>
        </div>
        {bot.category === 'Finance' && (
          <div className="bc-finance-disclaimer">
            [Please consult with an expert. The information presented here is for educational purpose.]
          </div>
        )}
        {bot.category === 'Legal & Travel' && (
          <div className="bc-legal-travel-disclaimer">
            [Please verify important legal, travel, visa, and safety information with a qualified expert or official source.]
          </div>
        )}
        <div className="bc-header-pills">
          <span className="bc-pill" data-tooltip="Files are processed for this chat and are not saved.">
            <PhosphorIcon className="bi bi-shield-check" /> Secure
          </span>
          {bot.acceptsFiles && (
            <span className="bc-pill" data-tooltip="Attached files are used as temporary chat context and are not saved.">
              <PhosphorIcon className="bi bi-paperclip" /> Files supported
            </span>
          )}
          <button
            className={`bc-pill bc-pill-btn${historyOpen ? ' bc-pill-active' : ''}`}
            title="Recent chats"
            onClick={() => setHistoryOpen(o => !o)}
          >
            <PhosphorIcon className="bi bi-clock-history" /> History{sessions.length > 0 ? ` (${sessions.length})` : ''}
          </button>
          {sessionId && (
            <button
              className="bc-pill bc-pill-btn"
              title="Start new conversation"
              onClick={startNewChat}
            >
              <PhosphorIcon className="bi bi-plus-circle" /> New chat
            </button>
          )}
        </div>
      </div>

      {historyOpen && (
        <>
          <div className="bc-history-backdrop" onClick={() => setHistoryOpen(false)} />
          <div className="bc-history">
            <div className="bc-history-head">
              <span>Recent chats</span>
              <button onClick={startNewChat}><PhosphorIcon className="bi bi-plus-lg" /> New</button>
            </div>
            {sessions.length === 0 ? (
              <div className="bc-history-empty">No previous chats with {bot.name} yet.</div>
            ) : (
              <ul className="bc-history-list">
                {sessions.map(s => (
                  <li key={s.sessionId}>
                    <button
                      className={s.sessionId === sessionId ? 'bc-history-item active' : 'bc-history-item'}
                      onClick={() => openSession(s.sessionId)}
                    >
                      <span className="bc-history-title">{s.title || 'Untitled chat'}</span>
                      <span className="bc-history-meta">{s.messageCount} msg{s.messageCount === 1 ? '' : 's'} · {relativeTime(s.updatedAt)}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        </>
      )}

      {/* Messages */}
      <div className="bc-messages" ref={messagesContainerRef}>
        {isEmpty ? (
          <div className="bc-empty">
            <div className="bc-empty-icon"><PhosphorIcon className={`bi ${bot.emoji}`} /></div>
            <h2 className="bc-empty-name">{bot.name}</h2>
            <p className="bc-empty-desc">{bot.description}</p>
            <div className="bc-suggestions">
              {bot.suggested.map((s, i) => (
                <button
                  key={i}
                  className={`bc-suggestion-chip${suggestedIdx === i ? ' active' : ''}`}
                  onClick={() => { setSuggestedIdx(i); void send(s) }}
                >
                  {s}
                </button>
              ))}
            </div>
          </div>
        ) : (
          messages.map((msg, i) => {
            const plan = (msg.role === 'assistant' && !msg.streaming && bot.id === 'travel-planner')
              ? extractCalendarPlan(msg.content) : null
            const displayContent = plan?.cleaned ?? msg.content
            return (
            <div key={i} className={`bc-msg bc-msg-${msg.role}`}>
              {msg.role === 'assistant' && (
                <div className="bc-msg-avatar"><PhosphorIcon className={`bi ${bot.emoji}`} /></div>
              )}
              <div className={`bc-msg-bubble bc-bubble-${msg.role}`}>
                {msg.role === 'assistant' ? (
                  msg.streaming && !msg.content ? (
                    <span className="bc-typing"><span /><span /><span /></span>
                  ) : (
                    <div className="ac-msg-content ac-md">
                      <ReactMarkdown
                        remarkPlugins={[remarkGfm]}
                        components={{
                          h1: ({ children }) => <h1 className="ac-md-h1">{children}</h1>,
                          h2: ({ children }) => <h2 className="ac-md-h2">{children}</h2>,
                          h3: ({ children }) => <h3 className="ac-md-h3">{children}</h3>,
                          h4: ({ children }) => <h4 className="ac-md-h4">{children}</h4>,
                          p: ({ children }) => <p className="ac-md-p">{children}</p>,
                          ul: ({ children }) => <ul className="ac-md-ul">{children}</ul>,
                          ol: ({ children }) => <ol className="ac-md-ol">{children}</ol>,
                          li: ({ children }) => <li className="ac-md-li">{children}</li>,
                          code: ({ className, children }) => {
                            const isBlock = className?.includes('language-')
                            return isBlock ? <code className="ac-md-code-block">{children}</code> : <code className="ac-md-code">{children}</code>
                          },
                          pre: ({ children }) => <pre className="ac-md-pre">{children}</pre>,
                          table: ({ children }) => <div className="ac-table-wrap"><table className="ac-md-table">{children}</table></div>,
                          th: ({ children }) => <th className="ac-md-th">{children}</th>,
                          td: ({ children }) => <td className="ac-md-td">{children}</td>,
                          blockquote: ({ children }) => <blockquote className="ac-md-blockquote">{children}</blockquote>,
                          strong: ({ children }) => <strong className="ac-md-strong">{children}</strong>,
                          em: ({ children }) => <em className="ac-md-em">{children}</em>,
                          del: ({ children }) => <del className="ac-md-del">{children}</del>,
                          a: ({ href, children }) => <a className="ac-md-link" href={href} target="_blank" rel="noreferrer">{children}</a>,
                          input: props => <input {...props} className="ac-md-checkbox" disabled />,
                          hr: () => <hr className="ac-md-hr" />,
                        }}
                      >
                        {displayContent}
                      </ReactMarkdown>
                      {msg.streaming && msg.content && <span className="ac-cursor" />}
                      {plan && plan.events.length > 0 && <AddToCalendarButton events={plan.events} />}
                    </div>
                  )
                ) : (
                  <span style={{ whiteSpace: 'pre-wrap' }}>{msg.content}</span>
                )}
              </div>
            </div>
            )
          })
        )}
        <div ref={messagesEndRef} />
      </div>

      {/* Input area */}
      <div className="bc-input-area">
        {files.length > 0 && (
          <div className="bc-file-chips">
            {files.map((f, i) => (
              <span key={i} className="bc-file-chip">
                <PhosphorIcon className="bi bi-paperclip" /> {f.name}
                <button onClick={() => setFiles(prev => prev.filter((_, j) => j !== i))}><PhosphorIcon className="bi bi-x" /></button>
              </span>
            ))}
          </div>
        )}
        <div className="bc-input-row">
          {bot.acceptsFiles && (
            <>
              <input
                ref={fileInputRef}
                type="file"
                multiple
                accept="image/*,application/pdf,text/plain,.docx"
                style={{ display: 'none' }}
                onChange={e => e.target.files && addFiles(e.target.files)}
              />
              <button
                className="bc-attach-btn"
                onClick={() => fileInputRef.current?.click()}
                title="Attach files"
              >
                <PhosphorIcon className="bi bi-paperclip" />
              </button>
            </>
          )}
          <textarea
            ref={textareaRef}
            className="bc-textarea"
            placeholder={`Message ${bot.name}…`}
            value={input}
            onChange={handleInputChange}
            onKeyDown={handleKeyDown}
            rows={1}
            disabled={sending}
          />
          {sending ? (
            <button className="bc-send-btn bc-stop-btn" onClick={() => abortRef.current?.abort()}>
              <PhosphorIcon className="bi bi-stop-fill" />
            </button>
          ) : (
            <button
              className="bc-send-btn"
              onClick={() => void send()}
              disabled={!input.trim() && files.length === 0}
            >
              <PhosphorIcon className="bi bi-send-fill" />
            </button>
          )}
        </div>
        <div className="bc-input-footer">
          <span>{bot.name} uses AI — verify important information independently.</span>
        </div>
      </div>
    </div>
  )
}

function relativeTime(iso: string): string {
  const then = new Date(iso).getTime()
  if (isNaN(then)) return ''
  const m = Math.floor((Date.now() - then) / 60000)
  if (m < 1) return 'just now'
  if (m < 60) return `${m}m ago`
  const h = Math.floor(m / 60)
  if (h < 24) return `${h}h ago`
  const d = Math.floor(h / 24)
  if (d < 7) return `${d}d ago`
  return new Date(iso).toLocaleDateString()
}

const TRAVEL_TZ = Intl.DateTimeFormat().resolvedOptions().timeZone

interface PlanEvent { title: string; startDateTime: string; endDateTime?: string; location?: string; description?: string }

// The travel bot emits a ```calendar fenced block of confirmed events. Pull it out
// (so the raw JSON isn't shown) and offer to add them to the user's calendar.
function extractCalendarPlan(content: string): { events: PlanEvent[]; cleaned: string } | null {
  const m = content.match(/```calendar\s*([\s\S]*?)```/i)
  if (!m) return null
  let events: PlanEvent[] = []
  try {
    const parsed = JSON.parse(m[1].trim())
    events = Array.isArray(parsed) ? parsed : (parsed.events ?? [])
  } catch { return null }
  events = (events || []).filter(e => e && e.title && e.startDateTime)
  if (events.length === 0) return null
  return { events, cleaned: content.replace(m[0], '').trim() }
}

function AddToCalendarButton({ events }: { events: PlanEvent[] }) {
  const [status, setStatus] = useState<'idle' | 'adding' | 'done' | 'error'>('idle')
  const [note, setNote] = useState('')

  const add = async () => {
    setStatus('adding')
    try {
      const { data } = await api.post('/travel/add-to-calendar', { events, timezone: TRAVEL_TZ })
      setStatus('done')
      setNote(`Added ${data.added} event${data.added === 1 ? '' : 's'}${data.syncedToProvider ? ' — synced to your connected calendar' : ''}.`)
    } catch {
      setStatus('error')
      setNote('Could not add to calendar. Please try again.')
    }
  }

  return (
    <div className="bc-cal-add">
      {status === 'done' ? (
        <span className="bc-cal-done"><PhosphorIcon className="bi bi-check-circle-fill" /> {note}</span>
      ) : (
        <button className="bc-cal-btn" onClick={add} disabled={status === 'adding'}>
          <PhosphorIcon className="bi bi-calendar-plus" />
          {status === 'adding' ? 'Adding…' : `Add ${events.length} to my calendar`}
        </button>
      )}
      {status === 'error' && <span className="bc-cal-err">{note}</span>}
    </div>
  )
}
