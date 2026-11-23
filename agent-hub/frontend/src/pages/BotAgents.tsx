import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useRef, useCallback, useLayoutEffect, useMemo } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import { usePageMeta } from '../utils/usePageMeta'
import '../App.css'
import './AgentChat.css'
import './BotAgents.css'
import './BotChat.css'

const API = import.meta.env.VITE_API_URL ??
  (typeof window !== 'undefined' && window.location.hostname !== 'localhost'
    ? window.location.origin : 'http://localhost:5220')

interface BotMessage {
  role: 'user' | 'assistant'
  content: string
  streaming?: boolean
}

const ALLOWED_TYPES = [
  'image/jpeg', 'image/png', 'image/webp', 'image/gif',
  'application/pdf', 'text/plain',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document',
  'application/msword',
]

export interface BotConfig {
  id: string
  name: string
  emoji: string
  tagline: string
  description: string
  category: string
  acceptsFiles: boolean
  suggested: string[]
}

export const BOT_AGENTS: BotConfig[] = [
  // Finance
  {
    id: 'finance-lens',
    name: 'Finance Lens',
    emoji: 'bi-bar-chart-line',
    tagline: 'Finance document analysis',
    description: 'Read balance sheets, P&L, and cash flow statements. Extract KPIs, flag anomalies, and compare periods.',
    category: 'Finance',
    acceptsFiles: true,
    suggested: ['Analyse this balance sheet', 'Compare Q1 vs Q2 revenue', 'What are the main cost drivers?'],
  },
  {
    id: 'money-doc',
    name: 'MoneyDoc AI',
    emoji: 'bi-card',
    tagline: 'Bank statements, receipts & invoices',
    description: 'Upload any financial document. Get structured summaries with spending categories and transaction tables.',
    category: 'Finance',
    acceptsFiles: true,
    suggested: ['Summarise my bank statement', 'Categorise these transactions', 'How much did I spend on food?'],
  },
  {
    id: 'statement-iq',
    name: 'StatementIQ',
    emoji: 'bi-bank',
    tagline: 'Bank & credit card intelligence',
    description: 'Deep-dive into transaction history. Spot patterns, recurring charges, and anomalies across statements.',
    category: 'Finance',
    acceptsFiles: true,
    suggested: ['Find all recurring subscriptions', 'What are my top 5 spending categories?', 'Flag any unusual transactions'],
  },
  {
    id: 'receipt-iq',
    name: 'ReceiptIQ',
    emoji: 'bi-receipt',
    tagline: 'Receipt & invoice extraction',
    description: 'Extract every line item, total, tax amount, vendor, and date from receipts and invoices instantly.',
    category: 'Finance',
    acceptsFiles: true,
    suggested: ['Extract all line items', 'What is the tax breakdown?', 'List all vendors and amounts'],
  },
  {
    id: 'loan-iq',
    name: 'LoanIQ',
    emoji: 'bi-house',
    tagline: 'Loan & repayment analysis',
    description: 'Repayment schedules, snowball vs avalanche comparisons, and extra payment scenarios — all as educational data.',
    category: 'Finance',
    acceptsFiles: true,
    suggested: ['Show snowball vs avalanche for my debts', 'How much interest if I pay $200 extra/month?', 'When will I be debt free?'],
  },
  {
    id: 'tax-prep',
    name: 'TaxPrep AI',
    emoji: 'bi-files',
    tagline: 'Tax document organisation',
    description: 'Upload tax returns and payslips. Extract deductible categories and summarise key numbers clearly.',
    category: 'Finance',
    acceptsFiles: true,
    suggested: ['Extract my deductible categories', 'Summarise my income sources', 'What PAYE was deducted?'],
  },
  {
    id: 'cashflow-copilot',
    name: 'Cashflow Copilot',
    emoji: 'bi-graph-up-arrow',
    tagline: 'Monthly finance summary',
    description: 'Summarise income vs expenses, calculate savings rate, and get 50/30/20 framework breakdowns.',
    category: 'Finance',
    acceptsFiles: true,
    suggested: ['Give me a 50/30/20 breakdown', 'What is my monthly savings rate?', 'Summarise last month\'s cashflow'],
  },
  // Productivity
  {
    id: 'calendar-bot',
    name: 'Calendar Bot',
    emoji: 'bi-calendar',
    tagline: 'Scheduling & daily planning',
    description: 'Plan meetings, prioritise your day, draft agendas, and manage time blocks conversationally.',
    category: 'Productivity',
    acceptsFiles: true,
    suggested: ['Plan my day with 3 focus blocks', 'Draft an agenda for a 1-hour team meeting', 'I have 5 tasks — help me prioritise'],
  },
  {
    id: 'email-reply',
    name: 'Email Reply Bot',
    emoji: 'bi-envelope',
    tagline: 'Professional email replies',
    description: 'Paste an email and get a polished, ready-to-send reply — formal, friendly, assertive, or apologetic.',
    category: 'Productivity',
    acceptsFiles: true,
    suggested: ['Reply to this email professionally', 'Write a polite follow-up', 'Decline this meeting politely'],
  },
  {
    id: 'resume-bot',
    name: 'Resume Bot',
    emoji: 'bi-file-earmark-text',
    tagline: 'Resume & cover letters',
    description: 'Rewrite bullets to be impact-driven, tailor CVs to job descriptions, and write compelling cover letters.',
    category: 'Productivity',
    acceptsFiles: true,
    suggested: ['Rewrite my resume bullets', 'Tailor my CV to this job description', 'Write a cover letter for this role'],
  },
  {
    id: 'interview-coach',
    name: 'Interview Coach',
    emoji: 'bi-bullseye',
    tagline: 'Role-specific interview prep',
    description: 'Practice for Solution Architect, PM, Cloud Engineer, or any role. STAR method coaching and feedback.',
    category: 'Productivity',
    acceptsFiles: true,
    suggested: ['Practice for AI Solution Architect interview', 'Mock interview for Product Manager role', 'Give me STAR method coaching'],
  },
  // Lifestyle
  {
    id: 'home-improvement',
    name: 'Home Improvement Bot',
    emoji: 'bi-house-heart',
    tagline: 'Renovation value advice',
    description: 'Describe or upload photos of your home. Get improvement suggestions ranked by ROI and budget tier.',
    category: 'Lifestyle',
    acceptsFiles: true,
    suggested: ['What renovations add the most value?', 'Best improvements under $5,000', 'What should I do before selling?'],
  },
  {
    id: 'gita-guide',
    name: 'Life Clarity Coach',
    emoji: 'bi-compass',
    tagline: 'Work, stress & decision clarity',
    description: 'Grounded help for work pressure, office politics, relationships, stress, and hard decisions.',
    category: 'Lifestyle',
    acceptsFiles: true,
    suggested: ['I feel overwhelmed with work', 'How do I handle office politics?', 'Help me make an important decision'],
  },
  // Legal & Travel
  {
    id: 'legal-summary',
    name: 'Legal Summary Bot',
    emoji: 'bi-bank',
    tagline: 'Plain-English legal docs',
    description: 'Upload contracts, tenancy agreements, or employment letters. Explains clauses and flags unusual terms.',
    category: 'Legal & Travel',
    acceptsFiles: true,
    suggested: ['What does this tenancy agreement say?', 'Are there any red flags in this contract?', 'Explain this employment clause'],
  },
  {
    id: 'travel-planner',
    name: 'Travel Planner Bot',
    emoji: 'bi-airplane',
    tagline: 'Trips, tickets, weather & local picks',
    description: 'Upload your flight ticket or hotel booking and get a full plan — day-by-day, weather-based packing, dietary-aware food, and live local picks. Confirm plans and add them straight to your calendar.',
    category: 'Legal & Travel',
    acceptsFiles: true,
    suggested: ['Plan my trip from this ticket (attach it)', 'Christchurch in July — what to pack and eat', 'Vegetarian food near my hotel', 'Add my confirmed flights to my calendar'],
  },
]

const CATEGORIES = ['All', 'Lifestyle', 'Finance', 'Productivity', 'Legal & Travel']

const CATEGORY_ICONS: Record<string, string> = {
  Finance: 'bi-currency-dollar',
  Productivity: 'bi-lightning-charge',
  Lifestyle: 'bi-heart',
  'Legal & Travel': 'bi-briefcase',
}

function orderedBots(bots: BotConfig[]) {
  return [...bots].sort((a, b) => {
    const lifestylePriority = (bot: BotConfig) => bot.id === 'gita-guide' ? 0 : bot.category === 'Lifestyle' ? 1 : 2
    const categoryRank = (bot: BotConfig) => CATEGORIES.indexOf(bot.category)
    return lifestylePriority(a) - lifestylePriority(b)
      || categoryRank(a) - categoryRank(b)
      || BOT_AGENTS.findIndex(bot => bot.id === a.id) - BOT_AGENTS.findIndex(bot => bot.id === b.id)
  })
}

export default function BotAgents() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('bot-agents')
  usePageMeta(
    'Bot Agents – GenPrompt | 15 Specialised AI Bots',
    'Chat with 15 specialised AI bots for finance analysis, resume writing, interview coaching, travel planning, legal summaries, scheduling, and more. Powered by GenPrompt.'
  )
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Bot Agents' }])
    return () => setBreadcrumbs([])
  }, [])

  const [activeCat, setActiveCat] = useState('All')
  const [activeBotId, setActiveBotId] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [showBotPicker, setShowBotPicker] = useState(false)
  const [messages, setMessages] = useState<BotMessage[]>([])
  const [input, setInput] = useState('')
  const [sending, setSending] = useState(false)
  const [files, setFiles] = useState<File[]>([])
  const [cloudUrl, setCloudUrl] = useState('')
  const [showCloud, setShowCloud] = useState(false)
  const [connectingCalendar, setConnectingCalendar] = useState<'google' | 'microsoft' | null>(null)
  const [sessionId, setSessionId] = useState<string | null>(null)
  const [suggestedIdx, setSuggestedIdx] = useState<number | null>(null)

  const messagesContainerRef = useRef<HTMLDivElement>(null)
  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const abortRef = useRef<AbortController | null>(null)
  const token = localStorage.getItem('authToken')
  const authHeader = { Authorization: `Bearer ${token ?? ''}` }

  const activeBot = activeBotId ? BOT_AGENTS.find(b => b.id === activeBotId) ?? null : null

  useEffect(() => {
    const container = messagesContainerRef.current
    if (container) container.scrollTop = container.scrollHeight
  }, [messages])

  const selectBot = (bot: BotConfig, suggestion?: string, index?: number) => {
    setActiveBotId(bot.id)
    setMessages([])
    setFiles([])
    setCloudUrl('')
    setShowCloud(false)
    setInput('')
    setSessionId(null)
    setSuggestedIdx(index ?? null)
    if (textareaRef.current) textareaRef.current.style.height = 'auto'
    if (suggestion) setTimeout(() => void send(suggestion, bot), 0)
  }

  const selectCategory = (category: string) => {
    setActiveCat(category)
    const first = orderedBots(BOT_AGENTS.filter(b => category === 'All' || b.category === category))[0]
    if (first) selectBot(first)
  }

  const handleInputChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    setInput(e.target.value)
    e.target.style.height = 'auto'
    e.target.style.height = Math.min(e.target.scrollHeight, 160) + 'px'
  }

  const addFiles = (incoming: FileList | File[]) => {
    const valid = Array.from(incoming).filter(f => ALLOWED_TYPES.includes(f.type) && f.size <= 104_857_600)
    setFiles(prev => [...prev, ...valid].slice(0, 5))
  }

  const connectCalendar = async (provider: 'google' | 'microsoft') => {
    setConnectingCalendar(provider)
    try {
      const res = await fetch(`${API}/api/calendar/connect/${provider}`, { headers: authHeader })
      if (!res.ok) throw new Error('Calendar connection failed')
      const data = await res.json()
      if (data.authUrl) window.location.href = data.authUrl
    } finally {
      setConnectingCalendar(null)
    }
  }

  const send = useCallback(async (textOverride?: string, botOverride?: BotConfig) => {
    const bot = botOverride ?? activeBot
    if (!bot) return
    const text = (textOverride ?? input).trim()
    if (!text && files.length === 0 && !cloudUrl.trim()) return
    if (sending) return

    setSending(true)
    setSuggestedIdx(null)

    const attachmentLabel = files.length > 0 ? files.map(f => `📎 ${f.name}`).join('  ') : ''
    const cloudLabel = cloudUrl.trim() ? `🔗 ${cloudUrl.trim()}` : ''
    const userContent = [text, attachmentLabel, cloudLabel].filter(Boolean).join('\n\n')
    setMessages(prev => [...prev, { role: 'user', content: userContent }, { role: 'assistant', content: '', streaming: true }])
    setInput('')
    setFiles([])
    setCloudUrl('')
    setShowCloud(false)
    if (textareaRef.current) textareaRef.current.style.height = 'auto'

    const form = new FormData()
    if (sessionId) form.append('sessionId', sessionId)
    form.append('message', text)
    form.append('botType', bot.id)
    files.forEach(f => form.append('files', f))
    if (cloudUrl.trim()) form.append('cloudUrl', cloudUrl.trim())

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
    }
  }, [activeBot, authHeader, cloudUrl, files, input, sending, sessionId])

  const filtered = orderedBots(BOT_AGENTS.filter(b => {
    const matchCat = activeCat === 'All' || b.category === activeCat
    const q = search.toLowerCase()
    const matchSearch = !q || b.name.toLowerCase().includes(q) || b.tagline.toLowerCase().includes(q) || b.description.toLowerCase().includes(q)
    return matchCat && matchSearch
  }))

  const groupedOrdered = CATEGORIES.slice(1)
    .map(cat => ({ cat, bots: orderedBots(filtered.filter(b => b.category === cat)) }))
    .filter(({ bots }) => bots.length > 0)

  const mobileFiltered = orderedBots(BOT_AGENTS.filter(b => {
    const q = search.toLowerCase()
    return !q || b.name.toLowerCase().includes(q) || b.tagline.toLowerCase().includes(q) || b.description.toLowerCase().includes(q)
  }))

  const mobileGroupedOrdered = CATEGORIES.slice(1)
    .map(cat => ({ cat, bots: mobileFiltered.filter(b => b.category === cat) }))
    .filter(({ bots }) => bots.length > 0)

  const pickBot = (bot: BotConfig, suggestion?: string, index?: number) => {
    selectBot(bot, suggestion, index)
    setShowBotPicker(false)
  }

  // Gamefield: the bot at its desk with the conversation as the work in hand.
  const field = useMemo<FieldModel>(() => {
    const turns = messages.slice(-6)
    const lastReply = [...messages].reverse().find(m => m.role === 'assistant')

    return {
      title: 'Bot Agents',
      subtitle: activeBot?.name ?? 'Pick a bot to open its desk.',
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
          name: activeBot?.name ?? 'Bot',
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
    <div className="bot-agents-page">
      <AgentStage {...stageProps} model={field} height={360} />

      <aside className="bot-agents-sidebar">
        <div className="bot-sidebar-header">
          <div className="bot-sidebar-brand">
            <PhosphorIcon className="bi bi-chat-heart-fill" />
            <span>Bot Agents</span>
          </div>
          <button className="bot-new-btn" type="button" onClick={() => { setActiveBotId(null); setMessages([]); setFiles([]); setCloudUrl(''); setShowCloud(false); setInput(''); setSessionId(null) }}>
            <PhosphorIcon className="bi bi-plus-lg" /> New chat
          </button>
        </div>
        <div className="bot-sidebar-label">
          <span>Categories</span>
          <span className="bot-sidebar-count">{CATEGORIES.length - 1}</span>
        </div>
        <div className="bot-sidebar-list">
          {CATEGORIES.map(c => {
            const count = c === 'All' ? BOT_AGENTS.length : BOT_AGENTS.filter(b => b.category === c).length
            return (
            <button
              key={c}
              type="button"
              className={`bot-sidebar-item${activeCat === c ? ' active' : ''}`}
              onClick={() => selectCategory(c)}
            >
              <span>{c !== 'All' && <PhosphorIcon className={`bi ${CATEGORY_ICONS[c]}`} />}{c}</span>
              <small>{count}</small>
            </button>
            )
          })}
        </div>
      </aside>

      <main className="bot-agents-main">
        <button
          type="button"
          className="bot-mobile-picker-btn"
          onClick={() => setShowBotPicker(true)}
        >
          <PhosphorIcon className="bi bi-chat-heart" />
          {activeBot ? activeBot.name : 'Choose bot'}
          <PhosphorIcon className="bi bi-chevron-up" />
        </button>

        {activeBot && (
          <div className="bc-header bot-inline-header">
            <div className="bc-bot-icon"><PhosphorIcon className={`bi ${activeBot.emoji}`} /></div>
            <div className="bc-bot-info">
              <div className="bc-bot-name">{activeBot.name}</div>
              <div className="bc-bot-tagline">{activeBot.tagline}</div>
            </div>
            {activeBot.category === 'Finance' && (
              <div className="bc-finance-disclaimer">
                [Please consult with an expert. The information presented here is for educational purpose.]
              </div>
            )}
            {activeBot.category === 'Legal & Travel' && (
              <div className="bc-legal-travel-disclaimer">
                [Please verify important legal, travel, visa, and safety information with a qualified expert or official source.]
              </div>
            )}
            <div className="bc-header-pills">
              <span className="bc-pill" data-tooltip="Files and cloud links are processed for this chat and are not saved."><PhosphorIcon className="bi bi-shield-check" /> Secure</span>
              <span className="bc-pill" data-tooltip="Attached files are used as temporary chat context and are not saved."><PhosphorIcon className="bi bi-paperclip" /> Files supported</span>
              <span className="bc-pill" data-tooltip="Cloud links are read for this request only; files are not saved."><PhosphorIcon className="bi bi-cloud-arrow-up" /> Cloud links</span>
              {sessionId && (
                <button className="bc-pill bc-pill-btn" type="button" onClick={() => { setMessages([]); setSessionId(null) }}>
                  <PhosphorIcon className="bi bi-plus-circle" /> New chat
                </button>
              )}
            </div>
          </div>
        )}

        <div className="bc-messages" ref={messagesContainerRef}>
          {!activeBot ? (
            <div className="bc-empty bot-empty-prompt">
              <div className="bc-empty-icon"><PhosphorIcon className="bi bi-chat-heart" /></div>
              <h2 className="bc-empty-name">Choose a bot to start</h2>
              <p className="bc-empty-desc">Pick a specialised bot to open the chat here in the same window.</p>
              <button type="button" className="bot-empty-picker-btn" onClick={() => setShowBotPicker(true)}>
                <PhosphorIcon className="bi bi-grid" /> Browse bots
              </button>
            </div>
          ) : messages.length === 0 ? (
            <div className="bc-empty bot-empty-prompt">
              <div className="bc-empty-icon"><PhosphorIcon className={`bi ${activeBot.emoji}`} /></div>
              <h2 className="bc-empty-name">{activeBot.name}</h2>
              <p className="bc-empty-desc">{activeBot.description}</p>
              <div className="bc-suggestions">
                {activeBot.suggested.map((s, i) => (
                  <button
                    key={s}
                    className={`bc-suggestion-chip${suggestedIdx === i ? ' active' : ''}`}
                    onClick={() => { setSuggestedIdx(i); void send(s) }}
                  >
                    {s}
                  </button>
                ))}
              </div>
            </div>
          ) : (
            messages.map((msg, i) => (
              <div key={i} className={`bc-msg bc-msg-${msg.role}`}>
                {msg.role === 'assistant' && <div className="bc-msg-avatar"><PhosphorIcon className={`bi ${activeBot.emoji}`} /></div>}
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
                          {msg.content}
                        </ReactMarkdown>
                        {msg.streaming && msg.content && <span className="ac-cursor" />}
                      </div>
                    )
                  ) : (
                    <span style={{ whiteSpace: 'pre-wrap' }}>{msg.content}</span>
                  )}
                </div>
              </div>
            ))
          )}
        </div>

        {activeBot && <div className="bc-input-area">
          {activeBot.id === 'calendar-bot' && (
            <div className="bot-calendar-connect">
              <span><PhosphorIcon className="bi bi-calendar-check" /> Link calendars for meeting prep</span>
              <button type="button" onClick={() => connectCalendar('google')} disabled={!!connectingCalendar}>
                <PhosphorIcon className="bi bi-google" /> Google
              </button>
              <button type="button" onClick={() => connectCalendar('microsoft')} disabled={!!connectingCalendar}>
                <PhosphorIcon className="bi bi-microsoft" /> Outlook
              </button>
            </div>
          )}
          {showCloud && (
            <div className="bot-cloud-row">
              <PhosphorIcon className="bi bi-cloud-arrow-up" />
              <input
                value={cloudUrl}
                onChange={e => setCloudUrl(e.target.value)}
                placeholder="Paste Google Drive or OneDrive document link..."
              />
              <button type="button" onClick={() => { setCloudUrl(''); setShowCloud(false) }} title="Remove cloud link">
                <PhosphorIcon className="bi bi-x-lg" />
              </button>
            </div>
          )}
          {files.length > 0 && (
            <div className="bc-file-chips">
              {files.map((f, i) => (
                <span key={`${f.name}-${i}`} className="bc-file-chip">
                  <PhosphorIcon className="bi bi-paperclip" /> {f.name}
                  <button onClick={() => setFiles(prev => prev.filter((_, j) => j !== i))}><PhosphorIcon className="bi bi-x" /></button>
                </span>
              ))}
            </div>
          )}
          <div className="bc-input-row">
            <input
              ref={fileInputRef}
              type="file"
              multiple
              accept="image/*,application/pdf,text/plain,.docx"
              style={{ display: 'none' }}
              onChange={e => e.target.files && addFiles(e.target.files)}
            />
            <button className="bc-attach-btn" onClick={() => fileInputRef.current?.click()} title="Attach files">
              <PhosphorIcon className="bi bi-paperclip" />
            </button>
            <button className={`bc-attach-btn${showCloud ? ' active' : ''}`} onClick={() => setShowCloud(v => !v)} title="Attach Google Drive or OneDrive link">
              <PhosphorIcon className="bi bi-cloud-arrow-up" />
            </button>
            <textarea
              ref={textareaRef}
              className="bc-textarea"
              placeholder={`Message ${activeBot.name}...`}
              value={input}
              onChange={handleInputChange}
              onKeyDown={e => {
                if (e.key === 'Enter' && !e.shiftKey) {
                  e.preventDefault()
                  void send()
                }
              }}
              rows={1}
              disabled={sending}
            />
            {sending ? (
              <button className="bc-send-btn bc-stop-btn" onClick={() => abortRef.current?.abort()}>
                <PhosphorIcon className="bi bi-stop-fill" />
              </button>
            ) : (
              <button className="bc-send-btn" onClick={() => void send()} disabled={!input.trim() && files.length === 0 && !cloudUrl.trim()}>
                <PhosphorIcon className="bi bi-send-fill" />
              </button>
            )}
          </div>
          <div className="bc-input-footer">
            <span>{activeBot.name} uses AI — verify important information independently.</span>
          </div>
        </div>}
      </main>

      {showBotPicker && (
        <div className="bot-mobile-picker" role="dialog" aria-modal="true" aria-label="Choose a bot">
          <button className="bot-mobile-picker-backdrop" type="button" onClick={() => setShowBotPicker(false)} aria-label="Close bot picker" />
          <div className="bot-mobile-picker-sheet">
            <div className="bot-mobile-picker-grip" />
            <div className="bot-mobile-picker-head">
              <div>
                <span className="bot-trigger-kicker">Bots</span>
                <h2>Choose a bot</h2>
              </div>
              <button type="button" onClick={() => setShowBotPicker(false)} aria-label="Close bot picker">
                <PhosphorIcon className="bi bi-x-lg" />
              </button>
            </div>

            <label className="bot-trigger-search bot-mobile-search">
              <PhosphorIcon className="bi bi-search" />
              <input
                placeholder="Search bots..."
                value={search}
                onChange={e => setSearch(e.target.value)}
              />
            </label>

            <div className="bot-mobile-picker-list">
              {mobileGroupedOrdered.length === 0 ? (
                <div className="bot-trigger-empty">No bots match your search.</div>
              ) : (
                mobileGroupedOrdered.map(({ cat, bots }) => (
                  <section key={cat} className="bot-trigger-group">
                    <div className="bot-trigger-group-title">
                      <PhosphorIcon className={`bi ${CATEGORY_ICONS[cat]}`} />
                      <span>{cat}</span>
                      <small>{bots.length}</small>
                    </div>
                    {bots.map(bot => (
                      <article
                        key={bot.id}
                        className={`bot-trigger-card${activeBot?.id === bot.id ? ' active' : ''}`}
                        onClick={() => pickBot(bot)}
                      >
                        <div className="bot-trigger-card-head">
                          <div className="bot-trigger-title">
                            <PhosphorIcon className={`bi ${bot.emoji} bot-trigger-icon`} />
                            <div>
                              <h3>{bot.name}</h3>
                              <p>{bot.tagline}</p>
                            </div>
                          </div>
                          <button type="button" onClick={(e) => { e.stopPropagation(); pickBot(bot) }} title={`Start ${bot.name}`}>
                            <PhosphorIcon className="bi bi-send" />
                          </button>
                        </div>
                        <p className="bot-trigger-desc">{bot.description}</p>
                        <div className="bot-trigger-chips">
                          {bot.suggested.slice(0, 3).map((s, i) => (
                            <button key={s} type="button" onClick={(e) => { e.stopPropagation(); pickBot(bot, s, i) }}>{s}</button>
                          ))}
                        </div>
                      </article>
                    ))}
                  </section>
                ))
              )}
            </div>
          </div>
        </div>
      )}

      <aside className="bot-agents-trigger-panel">
        <div className="bot-trigger-inner">
          <div className="bot-trigger-kicker">Bots</div>
          <h2>Focused agent library</h2>
          <p>Search, filter, then start the bot you need. Every bot supports file uploads and Google Drive or OneDrive links.</p>

          <label className="bot-trigger-search">
            <PhosphorIcon className="bi bi-search" />
            <input
              placeholder="Search bots..."
              value={search}
              onChange={e => setSearch(e.target.value)}
            />
          </label>

          {groupedOrdered.length === 0 ? (
            <div className="bot-trigger-empty">No bots match your search.</div>
          ) : (
            groupedOrdered.map(({ cat, bots }) => (
              <section key={cat} className="bot-trigger-group">
                <div className="bot-trigger-group-title">
                  <PhosphorIcon className={`bi ${CATEGORY_ICONS[cat]}`} />
                  <span>{cat}</span>
                  <small>{bots.length}</small>
                </div>
                {bots.map(bot => (
                  <article
                    key={bot.id}
                    className={`bot-trigger-card${activeBot?.id === bot.id ? ' active' : ''}`}
                    onClick={() => selectBot(bot)}
                  >
                    <div className="bot-trigger-card-head">
                      <div className="bot-trigger-title">
                        <PhosphorIcon className={`bi ${bot.emoji} bot-trigger-icon`} />
                        <div>
                          <h3>{bot.name}</h3>
                          <p>{bot.tagline}</p>
                        </div>
                      </div>
                      <button type="button" onClick={(e) => { e.stopPropagation(); selectBot(bot) }} title={`Start ${bot.name}`}>
                        <PhosphorIcon className="bi bi-send" />
                      </button>
                    </div>
                    <p className="bot-trigger-desc">{bot.description}</p>
                    <div className="bot-trigger-chips">
                      {bot.suggested.slice(0, 3).map((s, i) => <button key={s} type="button" onClick={(e) => { e.stopPropagation(); selectBot(bot, s, i) }}>{s}</button>)}
                    </div>
                  </article>
                ))}
              </section>
            ))
          )}
        </div>
      </aside>
    </div>
  )
}
