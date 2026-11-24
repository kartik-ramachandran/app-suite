import PhosphorIcon from '../components/PhosphorIcon'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import { useState, useEffect, useRef, useCallback, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import api from '../services/api'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import { takeHandoff } from '../utils/fileHandoff'
import './AgentChat.css'

const API = import.meta.env.VITE_API_URL ??
  (typeof window !== 'undefined' && window.location.hostname !== 'localhost'
    ? window.location.origin : 'http://localhost:5220')

// ── Types ─────────────────────────────────────────────────────────────────────
interface Session { sessionId: string; title: string; updatedAt: string; messageCount: number }
interface Message { id?: number; role: 'user' | 'assistant'; content: string; createdAt?: string; streaming?: boolean; savedPromptId?: string }

function updateLastAssistant(
  messages: Message[],
  updater: (message: Message) => Message
) {
  const updated = [...messages]
  const last = updated[updated.length - 1]
  if (last?.role === 'assistant') {
    updated[updated.length - 1] = updater(last)
  }
  return updated
}

const SUGGESTED = [
  'Create an agent for triaging customer support emails',
  'Schedule a daily job that summarises new prompt ideas',
  'Draft a persona for a concise technical writing assistant',
  'Generate a research brief about AI workflow automation',
]

const INTENT_WORDS = ['show', 'select', 'choose', 'pick', 'list', 'configure', 'run', 'schedule', 'generate', 'create', 'draft', 'prepare', 'produce', 'make', 'need', 'want']
const MODULE_FORM_PREFIX = '[[agent-chat:module-form:'
const MODULE_FORM_SUFFIX = ']]'

const MODULE_FORM_CONFIG: Record<string, {
  icon: string
  title: string
  description: string
  placeholder: string
  actionLabel: string
  buildMessage: (value: string) => string
}> = {
  Prompt: {
    icon: 'bi-pencil-square',
    title: 'Create a prompt',
    description: "Tell me what the prompt should help with, and I'll create a reusable draft in Prompt Builder.",
    placeholder: 'Example: summarising customer interview notes into product insights',
    actionLabel: 'Create prompt',
    buildMessage: value => `Create a prompt for ${value}`,
  },
  'Invoice Agent': {
    icon: 'bi-receipt',
    title: 'Analyse an invoice or receipt',
    description: 'Tell me what financial document this is and what you want extracted or summarised.',
    placeholder: 'Example: bank statement and summarise spending, category totals, and 50/30/20 breakdown',
    actionLabel: 'Analyse financial document',
    buildMessage: value => `Analyse this ${value}`,
  },
  'Media Studio': {
    icon: 'bi-image',
    title: 'Create a media prompt',
    description: 'Tell me the subject, format, style, platform, and any constraints for the image, video, voice, or script prompt.',
    placeholder: 'Example: hero image for a SaaS launch, clean studio lighting, LinkedIn ad, no text in image',
    actionLabel: 'Create media prompt',
    buildMessage: value => `Generate a media prompt for ${value}`,
  },
  Agents: {
    icon: 'bi-send',
    title: 'Create an agent',
    description: "Tell me the agent's job, users, tools, guardrails, inputs, outputs, and handoff behaviour.",
    placeholder: 'Example: customer support triage agent that classifies urgency, suggests replies, and escalates billing issues',
    actionLabel: 'Create agent',
    buildMessage: value => `Create an agent for ${value}`,
  },
  Personas: {
    icon: 'bi-person-badge',
    title: 'Create a persona',
    description: 'Tell me the role, tone, expertise, audience, and communication style this persona should maintain.',
    placeholder: 'Example: direct UX reviewer for SaaS dashboards, concise tone, prioritises accessibility and workflow clarity',
    actionLabel: 'Create persona',
    buildMessage: value => `Draft a persona for ${value}`,
  },
  Marketplace: {
    icon: 'bi-shop',
    title: 'Create a marketplace draft',
    description: 'Tell me who the public prompt is for, the task it solves, inputs it needs, and what a good output looks like.',
    placeholder: 'Example: public prompt for SaaS onboarding emails with product, audience, tone, and CTA variables',
    actionLabel: 'Create marketplace draft',
    buildMessage: value => `Create marketplace prompt for ${value}`,
  },
  Jobs: {
    icon: 'bi-calendar-event',
    title: 'Create a scheduled job',
    description: 'Tell me what should run, how often, what inputs it uses, and where the result should go.',
    placeholder: 'Example: weekly summary of new prompt ideas every Monday morning with highlights and next actions',
    actionLabel: 'Create job',
    buildMessage: value => `Schedule a job to ${value}`,
  },
  Research: {
    icon: 'bi-search',
    title: 'Create a research brief',
    description: "Tell me the topic, audience, time horizon, decision you're supporting, and any constraints.",
    placeholder: 'Example: AI workflow automation trends for SMB operations teams over the next 12 months',
    actionLabel: 'Create research brief',
    buildMessage: value => `Generate research brief about ${value}`,
  },
  Calendar: {
    icon: 'bi-calendar-event',
    title: 'Plan a schedule or meeting',
    description: 'Tell me what you need to plan — a meeting, a daily schedule, or a time-blocked work session.',
    placeholder: 'Example: plan my Tuesday with 3 deep focus blocks and 2 short meetings',
    actionLabel: 'Plan it',
    buildMessage: value => `Help me plan: ${value}`,
  },
  'Finance Bot': {
    icon: 'bi-currency-dollar',
    title: 'Analyse a financial document',
    description: 'Tell me what financial document you have and what you want to understand about it.',
    placeholder: 'Example: my bank statement for July — summarise spending categories and flag subscriptions',
    actionLabel: 'Open Finance Bot',
    buildMessage: value => `Analyse my financial document: ${value}`,
  },
  'Career Bot': {
    icon: 'bi-briefcase',
    title: 'Get career help',
    description: 'Tell me what you need — interview prep, resume rewrite, or a professional email reply.',
    placeholder: 'Example: practice interview for a Product Manager role at a Series B startup',
    actionLabel: 'Open Career Bot',
    buildMessage: value => `Help me with my career: ${value}`,
  },
  'Lifestyle Bot': {
    icon: 'bi-globe2',
    title: 'Plan travel, home, or life',
    description: 'Tell me what you need — travel itinerary, renovation advice, contract summary, or life clarity.',
    placeholder: 'Example: 7-day trip to Japan, vegetarian, budget $2,000, direct flights from Sydney',
    actionLabel: 'Open Lifestyle Bot',
    buildMessage: value => `Help me with: ${value}`,
  },
}

function moduleFormMarker(title: string) {
  return `${MODULE_FORM_PREFIX}${title}${MODULE_FORM_SUFFIX}`
}

function getModuleFormTitle(content: string) {
  if (!content.startsWith(MODULE_FORM_PREFIX) || !content.endsWith(MODULE_FORM_SUFFIX)) return null
  const title = content.slice(MODULE_FORM_PREFIX.length, -MODULE_FORM_SUFFIX.length)
  return MODULE_FORM_CONFIG[title] ? title : null
}

const CHAT_MODULE_TRIGGERS = [
  {
    title: 'Prompt',
    subtitle: 'Create reusable prompt drafts',
    words: ['prompt', 'prompts', 'prompt builder', 'template', 'system prompt', 'instruction', 'variables'],
  },
  {
    title: 'Invoice Agent',
    subtitle: 'Analyse documents, spending, debt scenarios, and budgeting breakdowns',
    words: ['receipt', 'invoice', 'bank statement', 'document', 'pdf', 'analyse', 'extract', 'expenses', 'debt payoff', 'snowball', 'avalanche', 'extra payment', '50/30/20', 'what-if'],
  },
  {
    title: 'Media Studio',
    subtitle: 'Create image, video, or voice prompt drafts',
    example: 'Generate a media prompt for a product launch visual',
    words: ['media', 'image', 'video', 'voice', 'dall-e', 'midjourney', 'stable diffusion', 'script'],
  },
  {
    title: 'Agents',
    subtitle: 'Create autonomous agent blueprints',
    example: 'Create an agent for customer support triage',
    words: ['agent', 'agents', 'autonomous', 'tool', 'tools', 'run agent', 'assistant agent'],
  },
  {
    title: 'Personas',
    subtitle: 'Create reusable AI personalities',
    example: 'Draft a persona for a direct UX reviewer',
    words: ['persona', 'personas', 'personality', 'tone', 'character', 'role', 'voice'],
  },
  {
    title: 'Marketplace',
    subtitle: 'Create public prompt drafts',
    example: 'Create marketplace prompt for SaaS onboarding emails',
    words: ['marketplace', 'public prompt', 'publish', 'listing', 'template', 'community prompt'],
  },
  {
    title: 'Jobs',
    subtitle: 'Create scheduled background work',
    example: 'Schedule a daily job to summarise prompt ideas',
    words: ['job', 'jobs', 'schedule', 'scheduled job', 'automation', 'daily', 'weekly', 'monthly'],
  },
  {
    title: 'Research',
    subtitle: 'Prepare research scenarios',
    example: 'Generate research brief about AI workflow automation',
    words: ['research', 'brief', 'scenario', 'analysis', 'forecast', 'trend', 'investigate'],
  },
  {
    title: 'Calendar',
    subtitle: 'Plan events, meetings, and daily schedules',
    example: 'Plan my day with 3 deep focus blocks',
    words: ['calendar', 'meeting', 'schedule', 'event', 'agenda', 'time block', 'plan my day'],
  },
  {
    title: 'Finance Bot',
    subtitle: 'Analyse financial docs with MoneyDoc AI, StatementIQ, LoanIQ, and more',
    example: 'Analyse my bank statement for spending patterns',
    words: ['bank statement', 'receipt', 'invoice', 'loan', 'cashflow', 'tax', 'spending', 'transactions', 'balance sheet'],
  },
  {
    title: 'Career Bot',
    subtitle: 'Interview coaching, resume writing, and email replies',
    example: 'Practice for an AI Solution Architect interview',
    words: ['interview', 'resume', 'cv', 'cover letter', 'email reply', 'job application', 'career'],
  },
  {
    title: 'Lifestyle Bot',
    subtitle: 'Travel planning, home improvement, legal summaries, and life clarity',
    example: 'Plan a 7-day trip to Japan on a budget',
    words: ['travel', 'trip', 'itinerary', 'home improvement', 'renovation', 'contract', 'tenancy', 'work stress', 'office politics', 'decision clarity'],
  },
]

const ALL_MODULE_LINKS = [
  { label: 'Prompts', path: '/prompts', icon: 'bi-pencil-square' },
  { label: 'Agents', path: '/agents', icon: 'bi-robot' },
  { label: 'Bot Agents', path: '/bot-agents', icon: 'bi-chat-heart' },
  { label: 'Calendar', path: '/calendar', icon: 'bi-calendar-event' },
  { label: 'Invoice Agent', path: '/invoice-agent', icon: 'bi-receipt' },
  { label: 'Research', path: '/research', icon: 'bi-search' },
  { label: 'Media Studio', path: '/media-studio', icon: 'bi-image' },
  { label: 'Chains', path: '/chains', icon: 'bi-diagram-3' },
  { label: 'Personas', path: '/personas', icon: 'bi-person-badge' },
  { label: 'Marketplace', path: '/marketplace', icon: 'bi-shop' },
  { label: 'Jobs', path: '/jobs', icon: 'bi-calendar2-check' },
  { label: 'Swarm', path: '/swarm', icon: 'bi-grid-3x3' },
  { label: 'Coordinator', path: '/coordinator', icon: 'bi-diagram-2' },
]

// ── Main component ─────────────────────────────────────────────────────────────
export default function AgentChat() {
  const { stageProps } = useFieldView('agent-chat')

  const navigate = useNavigate()
  const [sessions, setSessions]         = useState<Session[]>([])
  const [activeId, setActiveId]         = useState<string | null>(null)
  const [messages, setMessages]         = useState<Message[]>([])
  const [input, setInput]               = useState('')
  const [inlineModuleText, setInlineModuleText] = useState<Record<string, string>>({})
  const [promptPersonaLenses, setPromptPersonaLenses] = useState<{ id: string; title: string; category: string; description: string; systemPrompt: string }[]>([])
  const [promptSelectedPersonaId, setPromptSelectedPersonaId] = useState<string>('default')
  const [loadingPromptPersonas, setLoadingPromptPersonas] = useState(false)
  const [promptGoalSuggestions, setPromptGoalSuggestions] = useState<string[]>([])
  const [promptSelectedGoals, setPromptSelectedGoals] = useState<string[]>([])
  const [loadingPromptGoals, setLoadingPromptGoals] = useState(false)
  const [mediaPersonaLenses, setMediaPersonaLenses] = useState<{ id: string; title: string; category: string; description: string; systemPrompt: string }[]>([])
  const [mediaSelectedPersonaId, setMediaSelectedPersonaId] = useState<string>('default')
  const [loadingMediaPersonas, setLoadingMediaPersonas] = useState(false)
  const [mediaGoalSuggestions, setMediaGoalSuggestions] = useState<string[]>([])
  const [mediaSelectedGoals, setMediaSelectedGoals] = useState<string[]>([])
  const [loadingMediaGoals, setLoadingMediaGoals] = useState(false)
  const [sending, setSending]           = useState(false)
  const [files, setFiles]               = useState<File[]>([])
  const [cloudUrl, setCloudUrl]         = useState('')
  const [showCloud, setShowCloud]       = useState(false)
  // Attached Signal recording (transcript + analyses) the agent can talk to.
  const [attachedSession, setAttachedSession] = useState<{ id: string; label: string } | null>(null)
  const [showInteractionPicker, setShowInteractionPicker] = useState(false)
  const [interactionSessions, setInteractionSessions] = useState<{ sessionId: string; sourceLabel: string; updatedAt: string; tools: string[] }[]>([])
  const [renaming, setRenaming]         = useState<string | null>(null)
  const [renameVal, setRenameVal]       = useState('')
  const [sidebarOpen, setSidebarOpen]   = useState(false)
  const [deleteTarget, setDeleteTarget] = useState<Session | null>(null)
  const [deleting, setDeleting]         = useState(false)
  const messagesEndRef  = useRef<HTMLDivElement>(null)
  const fileInputRef    = useRef<HTMLInputElement>(null)
  const textareaRef     = useRef<HTMLTextAreaElement>(null)
  const abortRef        = useRef<AbortController | null>(null)

  const token = localStorage.getItem('authToken')

  const authHeader = { Authorization: `Bearer ${token}` }

  // ── Load sessions ───────────────────────────────────────────────────────────
  const loadSessions = useCallback(async () => {
    try {
      const res = await fetch(`${API}/api/agentchat/sessions`, { headers: authHeader })
      if (res.ok) setSessions(await res.json())
    } catch { /* ignore */ }
  }, [])

  useEffect(() => { loadSessions() }, [loadSessions])

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph) — prefill only, never send
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/agent-chat')
    if (handoff?.input) setInput(handoff.input)
  }, [])

  // A file arriving from the free PDF tools ("Summarise with AI" / "Extract
  // tables"). Attach the file and prefill the ask, but never auto-send: the
  // user should see what is about to happen and press Send themselves.
  useEffect(() => {
    const h = takeHandoff('chat-summarise') ?? takeHandoff('chat-tables')
    if (!h) return
    setFiles([new File([h.blob], h.name, { type: h.blob.type || 'application/pdf' })])
    if (h.prompt) setInput(h.prompt)
    textareaRef.current?.focus()
  }, [])

  // ── Load session messages ───────────────────────────────────────────────────
  const loadSession = useCallback(async (sid: string) => {
    setActiveId(sid)
    setSidebarOpen(false)
    try {
      const res = await fetch(`${API}/api/agentchat/sessions/${sid}/messages`, { headers: authHeader })
      if (res.ok) {
        const data = await res.json()
        setMessages(data.messages)
      }
    } catch { /* ignore */ }
  }, [])

  // ── New chat ────────────────────────────────────────────────────────────────
  function newChat() {
    setActiveId(null)
    setMessages([])
    setInput('')
    setFiles([])
    setCloudUrl('')
    setShowCloud(false)
    setSidebarOpen(false)
  }

  // ── Auto-scroll ─────────────────────────────────────────────────────────────
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages])

  useEffect(() => {
    if (!deleteTarget) return
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape' && !deleting) setDeleteTarget(null)
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [deleteTarget, deleting])

  // ── Auto-resize textarea ────────────────────────────────────────────────────
  function handleInputChange(e: React.ChangeEvent<HTMLTextAreaElement>) {
    setInput(e.target.value)
    e.target.style.height = 'auto'
    e.target.style.height = Math.min(e.target.scrollHeight, 160) + 'px'
  }

  // ── Send ────────────────────────────────────────────────────────────────────
  async function openInteractionPicker() {
    setShowInteractionPicker(true)
    try {
      const res = await fetch(`${API}/api/interaction/sessions?take=50`, { headers: authHeader })
      if (res.ok) {
        const d = await res.json() as { items: { sessionId: string; sourceLabel: string; updatedAt: string; tools: string[] }[] }
        setInteractionSessions(d.items ?? [])
      }
    } catch { /* ignore */ }
  }

  async function send(textOverride?: string) {
    const text = (textOverride ?? input).trim()
    if (!text && files.length === 0 && !cloudUrl.trim() && !attachedSession) return
    if (sending) return

    setSending(true)
    const attachmentText = [
      ...files.map(f => `📎 ${f.name}`),
      cloudUrl ? `🔗 ${cloudUrl}` : '',
      attachedSession ? `🎙 ${attachedSession.label}` : '',
    ].filter(Boolean).join('  ')
    const userMsg: Message = { role: 'user', content: [text, attachmentText].filter(Boolean).join('\n\n') }
    setMessages(prev => [...prev, userMsg, { role: 'assistant', content: '', streaming: true }])
    setInput('')
    setFiles([])
    setCloudUrl('')
    setShowCloud(false)
    if (textareaRef.current) textareaRef.current.style.height = 'auto'

    const form = new FormData()
    if (activeId) form.append('sessionId', activeId)
    form.append('message', text)
    files.forEach(f => form.append('files', f))
    if (cloudUrl.trim()) form.append('cloudUrl', cloudUrl.trim())
    if (attachedSession) form.append('interactionSessionId', attachedSession.id)

    abortRef.current = new AbortController()

    try {
      const res = await fetch(`${API}/api/agentchat/send`, {
        method: 'POST',
        headers: authHeader,
        body: form,
        signal: abortRef.current.signal,
      })

      // Extract new session ID if this is a new chat
      const newSid = res.headers.get('X-Session-Id')
      if (newSid && !activeId) setActiveId(newSid)

      if (!res.body) throw new Error('No response body')

      const reader  = res.body.getReader()
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
              setMessages(prev => updateLastAssistant(prev, last => ({
                ...last,
                content: last.content + payload.text,
              })))
            } else if (payload.text && currentEvent === 'error') {
              setMessages(prev => updateLastAssistant(prev, last => ({
                ...last,
                content: payload.text,
              })))
            } else if (currentEvent === 'saved' && payload.promptId) {
              setMessages(prev => updateLastAssistant(prev, last => ({
                ...last,
                savedPromptId: payload.promptId,
              })))
            }
            currentEvent = ''
          } catch { /* malformed line */ }
        }
      }
    } catch (err: any) {
      if (err.name !== 'AbortError') {
        setMessages(prev => updateLastAssistant(prev, last => ({
          ...last,
          content: 'Something went wrong. Please try again.',
        })))
      }
    } finally {
      setMessages(prev => prev.map(m => ({ ...m, streaming: false })))
      setSending(false)
      await loadSessions()
    }
  }

  function openModuleFormMessage(title: string) {
    const marker = moduleFormMarker(title)
    setMessages(prev => {
      const hasOpenForm = prev.some(m => m.content === marker)
      return hasOpenForm ? prev : [...prev, { role: 'assistant', content: marker }]
    })
  }

  function sendInlineModuleForm(title: string) {
    const config = MODULE_FORM_CONFIG[title]
    const value = inlineModuleText[title]?.trim()
    if (!config || !value) return
    let message = config.buildMessage(value)
    if (title === 'Prompt') {
      const lens = promptPersonaLenses.find(p => p.id === promptSelectedPersonaId)
      if (lens) message += `\n\nPersona lens: ${lens.title} — ${lens.systemPrompt}`
      if (promptSelectedGoals.length > 0) message += `\n\nFocus on these goals:\n${promptSelectedGoals.map((g, idx) => `${idx + 1}. ${g}`).join('\n')}`
    }
    if (title === 'Media Studio') {
      const lens = mediaPersonaLenses.find(p => p.id === mediaSelectedPersonaId)
      if (lens) message += `\n\nPersona lens: ${lens.title} — ${lens.systemPrompt}`
      if (mediaSelectedGoals.length > 0) message += `\n\nFocus on these goals:\n${mediaSelectedGoals.map((g, i) => `${i + 1}. ${g}`).join('\n')}`
    }
    void send(message)
    setInlineModuleText(prev => ({ ...prev, [title]: '' }))
    setPromptPersonaLenses([])
    setPromptSelectedPersonaId('default')
    setPromptGoalSuggestions([])
    setPromptSelectedGoals([])
    setMediaPersonaLenses([])
    setMediaSelectedPersonaId('default')
    setMediaGoalSuggestions([])
    setMediaSelectedGoals([])
  }

  async function fetchPromptPersonas(intent: string) {
    if (!intent.trim()) return
    try {
      setLoadingPromptPersonas(true)
      const res = await api.post<{ personas: { id: string; title: string; category: string; description: string; systemPrompt: string }[] }>('/prompts/suggest-personas', { intent })
      setPromptPersonaLenses(res.data.personas ?? [])
      setPromptSelectedPersonaId('default')
    } catch {
      setPromptPersonaLenses([])
    } finally {
      setLoadingPromptPersonas(false)
    }
  }

  async function fetchPromptGoals(intent: string) {
    const lens = promptPersonaLenses.find(l => l.id === promptSelectedPersonaId)
    setLoadingPromptGoals(true)
    setPromptGoalSuggestions([])
    setPromptSelectedGoals([])
    try {
      const res = await api.post<{ goals: string[] }>('/prompts/generate-suggestions', {
        intent, style: 'professional', category: 'prompt',
        personaInstruction: lens?.systemPrompt ?? null,
      })
      setPromptGoalSuggestions(res.data.goals ?? [])
    } catch { /* silently fall through */ }
    finally { setLoadingPromptGoals(false) }
  }

  async function fetchMediaPersonas(intent: string) {
    if (!intent.trim()) return
    setLoadingMediaPersonas(true)
    setMediaPersonaLenses([])
    setMediaSelectedPersonaId('default')
    setMediaGoalSuggestions([])
    setMediaSelectedGoals([])
    try {
      const res = await api.post<{ personas: { id: string; title: string; category: string; description: string; systemPrompt: string }[] }>('/prompts/suggest-personas', { intent, style: 'creative', category: 'media' })
      setMediaPersonaLenses(res.data.personas ?? [])
    } catch { /* silently fall through */ }
    finally { setLoadingMediaPersonas(false) }
  }

  async function fetchMediaGoals(intent: string) {
    const lens = mediaPersonaLenses.find(l => l.id === mediaSelectedPersonaId)
    setLoadingMediaGoals(true)
    setMediaGoalSuggestions([])
    setMediaSelectedGoals([])
    try {
      const res = await api.post<{ goals: string[] }>('/prompts/generate-suggestions', {
        intent, style: 'creative', category: 'media',
        personaInstruction: lens?.systemPrompt ?? null,
      })
      setMediaGoalSuggestions(res.data.goals ?? [])
    } catch { /* silently fall through */ }
    finally { setLoadingMediaGoals(false) }
  }

  function stop() {
    abortRef.current?.abort()
    setSending(false)
  }

  // ── Rename ──────────────────────────────────────────────────────────────────
  async function commitRename(sid: string) {
    if (!renameVal.trim()) return
    await fetch(`${API}/api/agentchat/sessions/${sid}/rename`, {
      method: 'PATCH',
      headers: { ...authHeader, 'Content-Type': 'application/json' },
      body: JSON.stringify({ title: renameVal }),
    })
    setRenaming(null)
    await loadSessions()
  }

  // ── Delete ──────────────────────────────────────────────────────────────────
  async function confirmDeleteSession() {
    if (!deleteTarget || deleting) return
    setDeleting(true)
    try {
      await fetch(`${API}/api/agentchat/sessions/${deleteTarget.sessionId}`, { method: 'DELETE', headers: authHeader })
      if (activeId === deleteTarget.sessionId) newChat()
      setDeleteTarget(null)
      await loadSessions()
    } finally {
      setDeleting(false)
    }
  }

  function formatRelativeTime(dt: string) {
    const diff = Date.now() - new Date(dt).getTime()
    const m = Math.floor(diff / 60000)
    if (m < 60) return `${m}m ago`
    const h = Math.floor(m / 60)
    if (h < 24) return `${h}h ago`
    return `${Math.floor(h / 24)}d ago`
  }

  // ── Render ──────────────────────────────────────────────────────────────────
  // Gamefield: the assistant at its desk with the last few turns on the floor.
  const field = useMemo<FieldModel>(() => {
    const turns = messages.slice(-6)
    const lastReply = [...messages].reverse().find(m => m.role === 'assistant')

    return {
      title: 'Agent Chat',
      subtitle: sessions.find(x => x.sessionId === activeId)?.title ?? 'A new conversation',
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
          id: 'assistant',
          name: 'Assistant',
          role: `${messages.length} messages${files.length ? ` · ${files.length} files` : ''}`,
          status: (sending ? 'working' : lastReply ? 'done' : 'idle') as 'working' | 'done' | 'idle',
          detail: sending ? 'Working on a reply…' : bubble(lastReply?.content),
          output: lastReply?.content,
          boss: true,
        },
      ],
      messages: turns.map((m, i) =>
        m.role === 'user'
          ? { id: `up-${i}`, from: `turn-${i}`, to: 'assistant' }
          : { id: `down-${i}`, from: 'assistant', to: `turn-${i}` }),
      events: [],
    }
  }, [messages, sending, files, sessions, activeId])

  return (
    <div className="ac-root">
      <AgentStage {...stageProps} model={field} height={340} />

      {/* Mobile overlay */}
      {sidebarOpen && <div className="ac-overlay" onClick={() => setSidebarOpen(false)} />}

      {deleteTarget && (
        <div className="ac-dialog-overlay" onClick={() => !deleting && setDeleteTarget(null)}>
          <div
            className="ac-dialog"
            role="dialog"
            aria-modal="true"
            aria-labelledby="ac-delete-title"
            onClick={e => e.stopPropagation()}
          >
            <div className="ac-dialog-icon"><PhosphorIcon className="bi bi-trash3" /></div>
            <div className="ac-dialog-copy">
              <h2 id="ac-delete-title">Delete chat?</h2>
              <p>
                This will permanently remove <strong>{deleteTarget.title}</strong> and all messages in it.
              </p>
            </div>
            <div className="ac-dialog-actions">
              <button className="ac-dialog-cancel" onClick={() => setDeleteTarget(null)} disabled={deleting}>
                Cancel
              </button>
              <button className="ac-dialog-delete" onClick={confirmDeleteSession} disabled={deleting}>
                {deleting ? <PhosphorIcon className="bi bi-arrow-repeat ac-spin" /> : <PhosphorIcon className="bi bi-trash3" />}
                Delete
              </button>
            </div>
          </div>
        </div>
      )}

      {/* ── Sidebar ── */}
      <aside className={`ac-sidebar${sidebarOpen ? ' open' : ''}`}>
        <div className="ac-sidebar-header">
          <div className="ac-sidebar-brand">
            <PhosphorIcon className="bi bi-chat-dots-fill" />
            <span>Agent Chat</span>
          </div>
          <button className="ac-new-btn" onClick={newChat}>
            <PhosphorIcon className="bi bi-plus-lg" /> New chat
          </button>
        </div>

        <div className="ac-sessions-label">
          <span>Recent</span>
          <span className="ac-sessions-count">{sessions.length}</span>
        </div>

        <div className="ac-sessions-list">
          {sessions.length === 0 && (
            <div className="ac-sessions-empty">No chats yet</div>
          )}
          {sessions.map(s => (
            <div
              key={s.sessionId}
              className={`ac-session-item${activeId === s.sessionId ? ' active' : ''}`}
              onClick={() => loadSession(s.sessionId)}
            >
              {renaming === s.sessionId ? (
                <input
                  className="ac-rename-input"
                  value={renameVal}
                  autoFocus
                  onChange={e => setRenameVal(e.target.value)}
                  onKeyDown={e => {
                    if (e.key === 'Enter') commitRename(s.sessionId)
                    if (e.key === 'Escape') setRenaming(null)
                  }}
                  onBlur={() => commitRename(s.sessionId)}
                  onClick={e => e.stopPropagation()}
                />
              ) : (
                <>
                  <div className="ac-session-title">{s.title}</div>
                  <div className="ac-session-meta">{s.messageCount} messages · {formatRelativeTime(s.updatedAt)}</div>
                  <div className="ac-session-actions">
                    <button onClick={e => { e.stopPropagation(); setRenaming(s.sessionId); setRenameVal(s.title) }}>
                      <PhosphorIcon className="bi bi-pencil" />
                    </button>
                    <button onClick={e => { e.stopPropagation(); setDeleteTarget(s) }}>
                      <PhosphorIcon className="bi bi-trash" />
                    </button>
                  </div>
                </>
              )}
            </div>
          ))}
        </div>
      </aside>

      {/* ── Main chat ── */}
      <div className="ac-main">

        {/* Header */}
        <div className="ac-header">
          <button className="ac-hamburger" onClick={() => setSidebarOpen(o => !o)}>
            <PhosphorIcon className="bi bi-list" />
          </button>
          <div className="ac-header-info">
            <div className="ac-header-title">
              {messages.length === 0 ? 'New agent chat' : sessions.find(s => s.sessionId === activeId)?.title ?? 'Agent chat'}
            </div>
            <div className="ac-header-sub">Ask about prompts, AI workflows, or attach a document</div>
          </div>
          <div className="ac-header-right">
            <span className="ac-message-count">{messages.filter(m => !m.streaming).length} messages</span>
            {/* Cloud connect buttons */}
            <button
              className={`ac-cloud-btn${showCloud ? ' active' : ''}`}
              onClick={() => setShowCloud(o => !o)}
              title="Import from Google Drive or OneDrive"
            >
              <PhosphorIcon className="bi bi-cloud-arrow-up" /> Cloud
            </button>
          </div>
        </div>

        {/* Cloud URL panel */}
        {showCloud && (
          <div className="ac-cloud-panel">
            <div className="ac-cloud-panel-header">
              <PhosphorIcon className="bi bi-cloud-fill" /> Import document from cloud storage
              <button className="ac-cloud-close" onClick={() => setShowCloud(false)} title="Close">
                <PhosphorIcon className="bi bi-x-lg" />
              </button>
            </div>
            <p className="ac-cloud-panel-sub">
              Paste a public share link from Google Drive or OneDrive. The document will be read and used as context — never stored.
            </p>
            <div className="ac-cloud-providers">
              <div className="ac-cloud-provider">
                <svg width="18" height="18" viewBox="0 0 48 48" fill="none" xmlns="http://www.w3.org/2000/svg">
                  <path d="M15.1 35.3l-9.5-16.5 9-15.6L24 19.2z" fill="#0066DA"/>
                  <path d="M32.9 35.3H15.1L24 19.2l8.9-3.3z" fill="#00AC47"/>
                  <path d="M42.4 18.8l-9.5 16.5H15.1l8.9-15.6z" fill="#EA4335"/>
                  <path d="M24 3.2l9.4 15.9-8.5 0.1L15.6 3.2z" fill="#00832D"/>
                  <path d="M33.4 19.1l9 16.2H15.1L24 19.2z" fill="#2684FC"/>
                  <path d="M15.6 3.2l8.4 16-9 0.1L6.1 3.2z" fill="#FFBA00"/>
                </svg>
                Google Drive
              </div>
              <div className="ac-cloud-provider">
                <svg width="18" height="18" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
                  <path d="M5.5 12.5L2 17h20l-3.5-4.5L14 18l-4-3z" fill="#0364B8"/>
                  <path d="M8.5 6L4 13.5h8l-2-5z" fill="#0078D4"/>
                  <path d="M8.5 6l6 0.5 3 7H12z" fill="#1490DF"/>
                  <path d="M14.5 6.5l5 7H12z" fill="#28A8E8"/>
                </svg>
                OneDrive
              </div>
            </div>
            <div className="ac-cloud-input-row">
              <input
                className="ac-cloud-input"
                type="url"
                placeholder="https://drive.google.com/file/d/... or https://1drv.ms/..."
                value={cloudUrl}
                onChange={e => setCloudUrl(e.target.value)}
              />
              <button className="ac-cloud-use-btn" onClick={() => setShowCloud(false)} disabled={!cloudUrl.trim()}>
                Use link
              </button>
            </div>
            {cloudUrl && (
              <div className="ac-cloud-selected">
                <PhosphorIcon className="bi bi-check-circle-fill" /> Link ready — send a message to analyse the document
              </div>
            )}
          </div>
        )}

        {/* Messages */}
        <div className="ac-messages" data-visualize>
          {messages.length === 0 && (
            <div className="ac-empty">
              <div className="ac-empty-icon">
                <PhosphorIcon className="bi bi-chat-dots" />
              </div>
              <h2>GenPrompt AI Assistant</h2>
              <p>Ask about prompts, AI workflows, techniques, or attach a document for analysis.</p>
              <div className="ac-suggested-grid">
                {SUGGESTED.map(s => (
                  <button key={s} className="ac-suggested-btn" onClick={() => send(s)}>
                    {s}
                  </button>
                ))}
              </div>
            </div>
          )}

          {messages.map((m, i) => (
            <div key={i} className={`ac-msg ac-msg--${m.role}`}>
              <div className="ac-msg-avatar">
                {m.role === 'user'
                  ? <PhosphorIcon className="bi bi-person-fill" />
                  : <PhosphorIcon className="bi bi-stars" />
                }
              </div>
              <div className="ac-msg-bubble">
                {m.role === 'assistant' && getModuleFormTitle(m.content) ? (
                  <div className="ac-inline-form">
                    <div className="ac-inline-form-icon">
                      <PhosphorIcon className={`bi ${MODULE_FORM_CONFIG[getModuleFormTitle(m.content)!].icon}`} />
                    </div>
                    <div className="ac-inline-form-body">
                      <h3>{MODULE_FORM_CONFIG[getModuleFormTitle(m.content)!].title}</h3>
                      <p>{MODULE_FORM_CONFIG[getModuleFormTitle(m.content)!].description}</p>
                      <textarea
                        value={inlineModuleText[getModuleFormTitle(m.content)!] ?? ''}
                        onChange={e => {
                          setInlineModuleText(prev => ({ ...prev, [getModuleFormTitle(m.content)!]: e.target.value }))
                          if (getModuleFormTitle(m.content) === 'Prompt') {
                            setPromptPersonaLenses([])
                            setPromptSelectedPersonaId('default')
                          }
                          if (getModuleFormTitle(m.content) === 'Media Studio') {
                            setMediaPersonaLenses([])
                            setMediaSelectedPersonaId('default')
                            setMediaGoalSuggestions([])
                            setMediaSelectedGoals([])
                          }
                        }}
                        placeholder={MODULE_FORM_CONFIG[getModuleFormTitle(m.content)!].placeholder}
                        rows={3}
                      />
                      {/* 3-step flow for Prompt and Media Studio modules */}
                      {(getModuleFormTitle(m.content) === 'Prompt' || getModuleFormTitle(m.content) === 'Media Studio') && (() => {
                        const isPrompt = getModuleFormTitle(m.content) === 'Prompt'
                        const personaLenses = isPrompt ? promptPersonaLenses : mediaPersonaLenses
                        const selectedPersonaId = isPrompt ? promptSelectedPersonaId : mediaSelectedPersonaId
                        const setSelectedPersonaId = isPrompt ? setPromptSelectedPersonaId : setMediaSelectedPersonaId
                        const goalSuggestions = isPrompt ? promptGoalSuggestions : mediaGoalSuggestions
                        const selectedGoals = isPrompt ? promptSelectedGoals : mediaSelectedGoals
                        const setSelectedGoals = isPrompt ? setPromptSelectedGoals : setMediaSelectedGoals
                        const loadingPersonas = isPrompt ? loadingPromptPersonas : loadingMediaPersonas
                        const loadingGoals = isPrompt ? loadingPromptGoals : loadingMediaGoals
                        const intentText = inlineModuleText[isPrompt ? 'Prompt' : 'Media Studio'] ?? ''
                        const fetchPersonas = () => isPrompt ? fetchPromptPersonas(intentText) : fetchMediaPersonas(intentText)
                        const fetchGoals = () => isPrompt ? fetchPromptGoals(intentText) : fetchMediaGoals(intentText)
                        const clearAll = () => { if (isPrompt) { setPromptPersonaLenses([]); setPromptSelectedPersonaId('default'); setPromptGoalSuggestions([]); setPromptSelectedGoals([]) } else { setMediaPersonaLenses([]); setMediaSelectedPersonaId('default'); setMediaGoalSuggestions([]); setMediaSelectedGoals([]) } }
                        const actionLabel = isPrompt ? 'Create prompt' : 'Create media prompt'
                        const sendKey = isPrompt ? 'Prompt' : 'Media Studio'

                        return (
                          <div style={{ marginTop: 10 }}>
                            {personaLenses.length === 0 ? (
                              <div className="ac-inline-form-actions">
                                <button type="button" className="ac-inline-form-secondary"
                                  onClick={() => { setMessages(prev => prev.filter((_, idx) => idx !== i)); clearAll() }}>
                                  Cancel
                                </button>
                                <button type="button" className="ac-inline-form-primary"
                                  onClick={fetchPersonas}
                                  disabled={!intentText.trim() || loadingPersonas}>
                                  {loadingPersonas ? <><span className="pg-pill-spinner" /> Suggesting…</> : <><PhosphorIcon className="bi bi-person-bounding-box" /> Choose Persona →</>}
                                </button>
                              </div>
                            ) : goalSuggestions.length === 0 ? (
                              <>
                                <div style={{ fontSize: '0.75rem', fontWeight: 600, color: '#555', marginBottom: 6 }}>
                                  <PhosphorIcon className="bi bi-person-bounding-box" style={{ marginRight: 4, color: '#833ab4' }} />Choose a Persona
                                </div>
                                <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(148px, 1fr))', gap: 6, marginBottom: 10 }}>
                                  <button type="button" className="pg-persona-card" data-selected={selectedPersonaId === 'default'} onClick={() => setSelectedPersonaId('default')}
                                    style={{ padding: '8px 10px', borderRadius: 8, cursor: 'pointer', border: selectedPersonaId === 'default' ? '2px solid #833ab4' : '1.5px solid #d1d5db', background: selectedPersonaId === 'default' ? 'rgba(131,58,180,0.07)' : '#fff' }}>
                                    <div style={{ textAlign: 'center' }}><div style={{ fontSize: '0.65rem', fontWeight: 700, color: '#833ab4', textTransform: 'uppercase', marginBottom: 2 }}>Default</div><div style={{ fontWeight: 700, fontSize: '0.78rem', color: '#000', marginBottom: 3 }}>Balanced AI</div></div>
                                    <div style={{ fontSize: '0.7rem', color: '#555', lineHeight: 1.3, textAlign: 'left' }}>Let AI infer the best approach.</div>
                                  </button>
                                  {personaLenses.map(lens => (
                                    <button key={lens.id} type="button" className="pg-persona-card" data-selected={selectedPersonaId === lens.id} onClick={() => setSelectedPersonaId(lens.id)}
                                      style={{ padding: '8px 10px', borderRadius: 8, cursor: 'pointer', border: selectedPersonaId === lens.id ? '2px solid #833ab4' : '1.5px solid #d1d5db', background: selectedPersonaId === lens.id ? 'rgba(131,58,180,0.07)' : '#fff' }}>
                                      <div style={{ textAlign: 'center' }}><div style={{ fontSize: '0.65rem', fontWeight: 700, color: '#833ab4', textTransform: 'uppercase', marginBottom: 2 }}>{lens.category}</div><div style={{ fontWeight: 700, fontSize: '0.78rem', color: '#000', marginBottom: 3 }}>{lens.title}</div></div>
                                      <div style={{ fontSize: '0.7rem', color: '#555', lineHeight: 1.3, textAlign: 'left' }}>{lens.description}</div>
                                    </button>
                                  ))}
                                </div>
                                <div className="ac-inline-form-actions">
                                  <button type="button" className="ac-inline-form-secondary" onClick={() => { if (isPrompt) { setPromptPersonaLenses([]); setPromptSelectedPersonaId('default') } else { setMediaPersonaLenses([]); setMediaSelectedPersonaId('default') } }}>Back</button>
                                  <button type="button" className="ac-inline-form-primary" onClick={fetchGoals} disabled={loadingGoals}>
                                    {loadingGoals ? <><span className="pg-pill-spinner" /> Getting goals…</> : <><PhosphorIcon className="bi bi-list-check" /> Generate Goals →</>}
                                  </button>
                                </div>
                              </>
                            ) : (
                              <>
                                <div style={{ fontSize: '0.75rem', fontWeight: 600, color: '#555', marginBottom: 6 }}>
                                  <PhosphorIcon className="bi bi-list-check" style={{ marginRight: 4, color: '#833ab4' }} />Select goals
                                </div>
                                <div style={{ display: 'flex', flexDirection: 'column', gap: 4, marginBottom: 10 }}>
                                  {goalSuggestions.map((goal, gi) => (
                                    <label key={gi} style={{ display: 'flex', alignItems: 'flex-start', gap: 8, cursor: 'pointer', fontSize: '0.8rem', color: '#222', padding: '4px 6px', borderRadius: 6, background: selectedGoals.includes(goal) ? 'rgba(131,58,180,0.07)' : 'transparent' }}>
                                      <input type="checkbox" checked={selectedGoals.includes(goal)}
                                        onChange={() => setSelectedGoals((prev: string[]) => prev.includes(goal) ? prev.filter(g => g !== goal) : [...prev, goal])}
                                        style={{ marginTop: 2, accentColor: '#833ab4', flexShrink: 0 }} />
                                      {goal}
                                    </label>
                                  ))}
                                </div>
                                <div className="ac-inline-form-actions">
                                  <button type="button" className="ac-inline-form-secondary" onClick={() => { if (isPrompt) { setPromptGoalSuggestions([]); setPromptSelectedGoals([]) } else { setMediaGoalSuggestions([]); setMediaSelectedGoals([]) } }}>Back</button>
                                  <button type="button" className="ac-inline-form-primary" onClick={() => sendInlineModuleForm(sendKey)} disabled={selectedGoals.length === 0 || sending}>
                                    <PhosphorIcon className="bi bi-send" /> {actionLabel}
                                  </button>
                                </div>
                              </>
                            )}
                          </div>
                        )
                      })()}
                      <div className="ac-inline-form-actions" style={{ display: ['Media Studio', 'Prompt'].includes(getModuleFormTitle(m.content) ?? '') ? 'none' : undefined }}>
                        <button
                          type="button"
                          className="ac-inline-form-secondary"
                          onClick={() => {
                            setMessages(prev => prev.filter((_, index) => index !== i))
                            setPromptPersonaLenses([])
                            setPromptSelectedPersonaId('default')
                          }}
                        >
                          Cancel
                        </button>
                        <button
                          type="button"
                          className="ac-inline-form-primary"
                          onClick={() => sendInlineModuleForm(getModuleFormTitle(m.content)!)}
                          disabled={!(inlineModuleText[getModuleFormTitle(m.content)!] ?? '').trim() || sending}
                        >
                          <PhosphorIcon className="bi bi-send" /> {MODULE_FORM_CONFIG[getModuleFormTitle(m.content)!].actionLabel}
                        </button>
                      </div>
                    </div>
                  </div>
                ) : m.role === 'assistant' ? (
                  <div className="ac-msg-content ac-md">
                    <ReactMarkdown
                      remarkPlugins={[remarkGfm]}
                      components={{
                        h1: ({children}) => <h1 className="ac-md-h1">{children}</h1>,
                        h2: ({children}) => <h2 className="ac-md-h2">{children}</h2>,
                        h3: ({children}) => <h3 className="ac-md-h3">{children}</h3>,
                        h4: ({children}) => <h4 className="ac-md-h4">{children}</h4>,
                        p:  ({children}) => <p  className="ac-md-p">{children}</p>,
                        ul: ({children}) => <ul className="ac-md-ul">{children}</ul>,
                        ol: ({children}) => <ol className="ac-md-ol">{children}</ol>,
                        li: ({children}) => <li className="ac-md-li">{children}</li>,
                        code: ({className, children}) => {
                          const isBlock = className?.includes('language-')
                          return isBlock
                            ? <code className="ac-md-code-block">{children}</code>
                            : <code className="ac-md-code">{children}</code>
                        },
                        pre:   ({children}) => <pre   className="ac-md-pre">{children}</pre>,
                        table: ({children}) => <div className="ac-table-wrap"><table className="ac-md-table">{children}</table></div>,
                        th:    ({children}) => <th className="ac-md-th">{children}</th>,
                        td:    ({children}) => <td className="ac-md-td">{children}</td>,
                        blockquote: ({children}) => <blockquote className="ac-md-blockquote">{children}</blockquote>,
                        strong: ({children}) => <strong className="ac-md-strong">{children}</strong>,
                        em: ({children}) => <em className="ac-md-em">{children}</em>,
                        del: ({children}) => <del className="ac-md-del">{children}</del>,
                        a: ({href, children}) => (
                          <a className="ac-md-link" href={href} target="_blank" rel="noreferrer">
                            {children}
                          </a>
                        ),
                        input: props => <input {...props} className="ac-md-checkbox" disabled />,
                        hr: () => <hr className="ac-md-hr" />,
                      }}
                    >
                      {m.content}
                    </ReactMarkdown>
                    {m.streaming && m.content && <span className="ac-cursor" />}
                  </div>
                ) : (
                  <div className="ac-msg-content ac-msg-user-text">{m.content}</div>
                )}
                {m.streaming && !m.content && (
                  <div className="ac-typing">
                    <span /><span /><span />
                  </div>
                )}
                {m.savedPromptId && !m.streaming && (
                  <a
                    href={`/create-prompt?id=${m.savedPromptId}`}
                    className="ac-saved-prompt-banner"
                    target="_blank" rel="noreferrer"
                  >
                    <PhosphorIcon className="bi bi-check-circle-fill" />
                    Prompt saved to your library
                    <span className="ac-saved-prompt-cta">Open in Prompt Builder <PhosphorIcon className="bi bi-arrow-right" /></span>
                  </a>
                )}
              </div>
            </div>
          ))}
          <div ref={messagesEndRef} />
        </div>

        {/* Input bar */}
        <div className="ac-input-bar">
          {(files.length > 0 || cloudUrl || attachedSession) && (
            <div className="ac-attachment-pills">
              {files.map((f, i) => (
                <div key={i} className="ac-attachment-pill">
                  <PhosphorIcon className="bi bi-paperclip" />
                  <span>{f.name}</span>
                  <button onClick={() => setFiles(prev => prev.filter((_, idx) => idx !== i))}>
                    <PhosphorIcon className="bi bi-x" />
                  </button>
                </div>
              ))}
              {cloudUrl && (
                <div className="ac-attachment-pill">
                  <PhosphorIcon className="bi bi-cloud-fill" />
                  <span>{new URL(cloudUrl).hostname}</span>
                  <button onClick={() => setCloudUrl('')}>
                    <PhosphorIcon className="bi bi-x" />
                  </button>
                </div>
              )}
              {attachedSession && (
                <div className="ac-attachment-pill" title="The agent will answer using this recording">
                  <PhosphorIcon className="bi bi-soundwave" />
                  <span>{attachedSession.label}</span>
                  <button onClick={() => setAttachedSession(null)}>
                    <PhosphorIcon className="bi bi-x" />
                  </button>
                </div>
              )}
            </div>
          )}
          <div className="ac-input-row">
            <input
              ref={fileInputRef}
              type="file"
              multiple
              style={{ display: 'none' }}
              accept="image/*,application/pdf,text/plain,.txt,.docx"
              onChange={e => { if (e.target.files) setFiles(prev => [...prev, ...Array.from(e.target.files!)]); e.target.value = '' }}
            />
            {/* Textarea on the left */}
            <textarea
              ref={textareaRef}
              className="ac-textarea"
              placeholder="Ask the agent…"
              value={input}
              rows={1}
              onChange={handleInputChange}
              onKeyDown={e => {
                if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); send() }
              }}
            />
            {/* Right column: attach icons on top, send on bottom */}
            <div className="ac-input-right">
              <div className="ac-input-top">
                <button className="ac-attach-btn" title="Attach file" onClick={() => fileInputRef.current?.click()}>
                  <PhosphorIcon className="bi bi-paperclip" />
                </button>
                <button
                  className={`ac-cloud-attach-btn${showCloud ? ' active' : ''}`}
                  title="Import from Google Drive or OneDrive"
                  onClick={() => setShowCloud(o => !o)}
                >
                  <PhosphorIcon className="bi bi-cloud-arrow-up" />
                </button>
                <button
                  className={`ac-cloud-attach-btn${attachedSession ? ' active' : ''}`}
                  title="Talk to a Signal recording"
                  onClick={openInteractionPicker}
                >
                  <PhosphorIcon className="bi bi-soundwave" />
                </button>
              </div>
              <div className="ac-input-send">
                {sending
                  ? <button className="ac-send-btn ac-stop-btn" onClick={stop}><PhosphorIcon className="bi bi-stop-fill" /></button>
                  : <button className="ac-send-btn" onClick={() => send()} disabled={!input.trim() && files.length === 0 && !cloudUrl}>
                      <PhosphorIcon className="bi bi-send-fill" />
                    </button>
                }
              </div>
            </div>
          </div>
          <div className="ac-input-hint">Enter to send · Shift+Enter for new line · Attach files, cloud links, or a recording as context</div>
        </div>

        {showInteractionPicker && (
          <div className="ac-modal-overlay" onClick={() => setShowInteractionPicker(false)}>
            <div className="ac-modal" onClick={e => e.stopPropagation()}>
              <div className="ac-modal-head">
                <h3><PhosphorIcon className="bi bi-soundwave" /> Talk to a recording</h3>
                <button onClick={() => setShowInteractionPicker(false)}><PhosphorIcon className="bi bi-x-lg" /></button>
              </div>
              <p className="ac-modal-sub">Pick a Signal recording. The agent will answer using its transcript and analyses.</p>
              <div className="ac-modal-list">
                {interactionSessions.length === 0 ? (
                  <div className="ac-modal-empty">No recordings yet. Process one in Signal first.</div>
                ) : interactionSessions.map(s => (
                  <button
                    key={s.sessionId}
                    className="ac-modal-item"
                    onClick={() => { setAttachedSession({ id: s.sessionId, label: s.sourceLabel || 'Recording' }); setShowInteractionPicker(false) }}
                  >
                    <PhosphorIcon className="bi bi-soundwave" />
                    <div>
                      <div className="ac-modal-item-title">{s.sourceLabel || 'Recording'}</div>
                      <div className="ac-modal-item-meta">{s.tools.length} {s.tools.length === 1 ? 'analysis' : 'analyses'}</div>
                    </div>
                  </button>
                ))}
              </div>
            </div>
          </div>
        )}
      </div>

      <aside className="ac-trigger-panel">
        <div className="ac-trigger-panel-inner">
          <div className="ac-trigger-kicker">Triggers</div>
          <h2>Chat module keywords</h2>
          <p>Use an intent word plus a module keyword group to create starter records from chat.</p>

          <div className="ac-trigger-card ac-trigger-intents">
            <h3>Intent words</h3>
            <div className="ac-trigger-chips">
              {INTENT_WORDS.map(word => (
                <button key={word} type="button" onClick={() => setInput(word)}>
                  {word}
                </button>
              ))}
            </div>
          </div>

          {CHAT_MODULE_TRIGGERS.map(group => (
            <div key={group.title} className="ac-trigger-card">
              <div className="ac-trigger-card-head">
                <h3>{group.title}</h3>
                <button type="button" onClick={() => openModuleFormMessage(group.title)} title={`Open ${group.title} form`}>
                  <PhosphorIcon className="bi bi-send" />
                </button>
              </div>
              <p>{group.subtitle}</p>
              <div className="ac-trigger-chips">
                {group.words.map(word => (
                  <button key={word} type="button" onClick={() => setInput(prev => `${prev ? `${prev.trim()} ` : ''}${word}`)}>
                    {word}
                  </button>
                ))}
              </div>
            </div>
          ))}

          <div className="ac-trigger-card">
            <div className="ac-trigger-card-head">
              <h3>All Modules</h3>
            </div>
            <p>Navigate directly to any module.</p>
            <div className="ac-module-nav-grid">
              {ALL_MODULE_LINKS.map(m => (
                <button
                  key={m.path}
                  type="button"
                  className="ac-module-nav-btn"
                  onClick={() => navigate(m.path)}
                >
                  <PhosphorIcon className={`bi ${m.icon}`} />
                  <span>{m.label}</span>
                </button>
              ))}
            </div>
          </div>
        </div>
      </aside>
    </div>
  )
}
