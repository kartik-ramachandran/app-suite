import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useLayoutEffect, useEffect, useCallback, useMemo } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import { getAgents, getPublicAgents, createAgent, updateAgent, deleteAgent, createAgentFromPrompt, createAgentFromPersona } from '../services/api'
import PromptPicker, { type PromptPickerPrompt } from '../components/PromptPicker'
import ConfirmDialog from '../components/ConfirmDialog'
import api from '../services/api'
import '../App.css'
import './Agents.css'

interface Agent {
  id: number
  name: string
  description: string
  instructions: string
  model: string
  enabledTools: string
  toolPermissions: string
  inputSchemaJson: string
  memoryConfigJson: string
  guardrailsJson: string
  outputSchemaJson: string
  knowledgeSourcesJson: string
  integrationActionsJson: string
  modelRoutingJson: string
  benchmarkConfigJson: string
  governanceJson: string
  sourcePromptId?: string
  sourcePersonaId?: number
  fallbackBehavior: string
  escalationRules: string
  temperature: number
  maxIterations: number
  version?: number
  isTemplate?: boolean
  templateCategory?: string
  isPublic: boolean
  createdAt: string
  updatedAt: string
}

interface PublicAgentTemplate {
  id: number
  name: string
  description: string
  model: string
  enabledTools: string
  knowledgeSourcesJson: string
  integrationActionsJson: string
  templateCategory?: string
  isTemplate: boolean
  version: number
  authorName: string
}

interface PersonaSummary {
  id: number
  name: string
  category?: string
  isTemplate?: boolean
  isPublic?: boolean
}

interface AgentForm {
  name: string
  description: string
  instructions: string
  model: string
  enabledTools: string[]
  toolPermissions: Record<string, string>   // {web_search: 'always_allow' | 'confirm' | 'deny'}
  inputSchemaJson: string
  memoryConfigJson: string
  guardrailsJson: string
  outputSchemaJson: string
  knowledgeSourcesJson: string
  integrationActionsJson: string
  modelRoutingJson: string
  benchmarkConfigJson: string
  governanceJson: string
  fallbackBehavior: string
  escalationRules: string
  isTemplate: boolean
  templateCategory: string
  isPublic: boolean
  temperature: number
  maxIterations: number
}

interface StreamStep {
  type: string    // thinking | tool_call | tool_result | output | done | error
  content?: string
  toolName?: string
  toolInput?: string
  toolOutput?: string
}

const BLANK_FORM: AgentForm = {
  name: '', description: '', instructions: '',
  model: 'gpt-4o', enabledTools: [], toolPermissions: {},
  inputSchemaJson: '[{"name":"input","type":"string","required":true}]',
  memoryConfigJson: '{"mode":"session","writeBack":true}',
  guardrailsJson: '{"blockedTopics":[],"piiPolicy":"warn"}',
  outputSchemaJson: '{"type":"string"}',
  knowledgeSourcesJson: '[]',
  integrationActionsJson: '[]',
  modelRoutingJson: '{"mode":"single","models":["gpt-4o"]}',
  benchmarkConfigJson: '{"enabled":false}',
  governanceJson: '{"approvalMode":"none","citationRequired":false}',
  fallbackBehavior: 'Ask a clarifying question when the request is ambiguous.',
  escalationRules: 'Escalate when approval is required or the request falls outside configured guardrails.',
  isTemplate: false,
  templateCategory: 'General',
  isPublic: false,
  temperature: 0.7,
  maxIterations: 5
}

const PERMISSION_LABELS: Record<string, { label: string; color: string }> = {
  always_allow: { label: 'Allow',   color: '#16a34a' },
  confirm:      { label: 'Confirm', color: '#d97706' },
  deny:         { label: 'Deny',    color: '#dc2626' },
}

const TOOLS = [
  { id: 'web_search',   label: 'Web Search',   icon: 'bi-search',          desc: 'Search the web for up-to-date information' },
  { id: 'http_request', label: 'HTTP Request',  icon: 'bi-globe',           desc: 'Make GET/POST requests to external APIs' },
  { id: 'run_prompt',   label: 'Run Prompt',    icon: 'bi-chat-square-text',desc: 'Invoke a saved prompt by ID' },
  { id: 'run_chain',    label: 'Run Chain',     icon: 'bi-diagram-3',       desc: 'Execute a saved workflow chain' },
  { id: 'run_agent',    label: 'Run Agent',     icon: 'bi-robot',           desc: 'Delegate to another installed agent' },
  { id: 'send_email',   label: 'Send Email',    icon: 'bi-envelope',        desc: 'Email results to any address' },
  { id: 'memory_read',  label: 'Memory Read',   icon: 'bi-journal-text',    desc: 'Read stored memories for context' },
  { id: 'memory_write', label: 'Memory Write',  icon: 'bi-journal-plus',    desc: 'Persist facts to user memory' },
  { id: 'knowledge_lookup', label: 'Knowledge Lookup', icon: 'bi-book', desc: 'Search configured files, URLs, or notes' },
  { id: 'microsoft_teams_message', label: 'Teams Action', icon: 'bi-microsoft-teams', desc: 'Prepare a Microsoft Teams message action' },
  { id: 'microsoft_outlook_draft', label: 'Outlook Draft', icon: 'bi-envelope-paper', desc: 'Prepare an Outlook draft email' },
  { id: 'microsoft_excel_write', label: 'Excel Update', icon: 'bi-file-earmark-spreadsheet', desc: 'Prepare an Excel row update' },
  { id: 'microsoft_sharepoint_search', label: 'SharePoint Search', icon: 'bi-folder-symlink', desc: 'Search SharePoint knowledge sources' },
  { id: 'power_automate_flow', label: 'Power Automate', icon: 'bi-lightning-charge', desc: 'Trigger a Power Automate flow payload' },
]

const MODELS = ['gpt-4o', 'gpt-4o-mini', 'gpt-4-turbo', 'gpt-3.5-turbo', 'claude-3-5-sonnet', 'claude-3-haiku', 'gemini-1.5-pro', 'bedrock:anthropic.claude-3-5-sonnet']

const TEMPLATES = [
  { icon: 'bi-search', name: 'Web Research Assistant', description: 'Researches any topic online and delivers a concise summary with sources.', category: 'Research', instructions: 'You are a research assistant. When given a topic, search the web for current information, synthesise the key findings, and present a clear summary with the most important points. Always cite your sources.', enabledTools: ['web_search'], model: 'gpt-4o', temperature: 0.3, maxIterations: 5 },
  { icon: 'bi-newspaper', name: 'Daily News Briefing', description: 'Searches and summarises top news on any topic for your daily digest.', category: 'Monitoring', instructions: 'You are a news briefing assistant. Search for the latest news on the given topic, identify the 5 most important stories, and write a concise briefing. Include publication dates and source names.', enabledTools: ['web_search'], model: 'gpt-4o', temperature: 0.4, maxIterations: 4 },
  { icon: 'bi-graph-up-arrow', name: 'Market Research Report', description: 'Deep-dives into any market, industry, or company with competitive analysis.', category: 'Research', instructions: 'You are a market research analyst. Search for information about the given market or company, analyse key trends, competitors, opportunities and threats, and produce a structured report with an executive summary.', enabledTools: ['web_search'], model: 'gpt-4o', temperature: 0.3, maxIterations: 6 },
  { icon: 'bi-buildings', name: 'Competitor Monitor', description: 'Tracks what competitors are doing with automated weekly intelligence.', category: 'Monitoring', instructions: 'You are a competitive intelligence analyst. Search for recent news, product updates, pricing changes, and announcements from the specified competitor. Highlight anything that could impact our business and recommend responses.', enabledTools: ['web_search'], model: 'gpt-4o', temperature: 0.3, maxIterations: 5 },
  { icon: 'bi-pencil-square', name: 'Blog Post Writer', description: 'Researches a topic and writes a well-structured, SEO-optimised blog post.', category: 'Writing', instructions: 'You are a content writer. Research the given topic thoroughly, then write an engaging, well-structured blog post with a compelling headline, introduction, body sections with subheadings, and a conclusion. Optimise for readability and SEO.', enabledTools: ['web_search'], model: 'gpt-4o', temperature: 0.7, maxIterations: 4 },
  { icon: 'bi-lightbulb', name: 'Brainstorm Partner', description: 'Generates creative ideas and explores innovative angles on any subject.', category: 'Productivity', instructions: 'You are a creative brainstorming partner. When given a topic or challenge, generate diverse, innovative ideas across multiple angles. Think laterally, challenge assumptions, and provide concrete examples for each idea.', enabledTools: [], model: 'gpt-4o', temperature: 0.9, maxIterations: 3 },
  { icon: 'bi-bar-chart-line', name: 'Industry Trends Analyst', description: 'Tracks emerging trends in any industry with weekly AI-generated analysis.', category: 'Research', instructions: 'You are an industry analyst. Search for the latest trends, technologies, and developments in the specified industry. Identify emerging patterns, rank them by impact potential, and explain their implications for businesses operating in this space.', enabledTools: ['web_search'], model: 'gpt-4o', temperature: 0.4, maxIterations: 5 },
  { icon: 'bi-people', name: 'Customer Support Agent', description: 'Handles customer queries professionally using your knowledge base via HTTP.', category: 'Support', instructions: 'You are a professional customer support agent. Answer customer questions clearly and helpfully. If you need to look up specific information, use HTTP requests to check the knowledge base. Always be polite, empathetic, and solution-focused.', enabledTools: ['http_request'], model: 'gpt-4o-mini', temperature: 0.5, maxIterations: 4 },
  { icon: 'bi-beaker', name: 'Technical Researcher', description: 'Searches documentation, GitHub, and tech sources to answer deep technical questions.', category: 'Research', instructions: 'You are a technical research assistant specialising in software, APIs, and emerging technologies. Search for authoritative technical sources, official documentation, and community discussions to answer technical questions with precision.', enabledTools: ['web_search', 'http_request'], model: 'gpt-4o', temperature: 0.2, maxIterations: 7 },
]

const TEMPLATE_CATEGORIES = ['All', 'Research', 'Monitoring', 'Writing', 'Productivity', 'Support']

export default function Agents() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('agents')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Agents' }])
    return () => setBreadcrumbs([])
  }, [])

  const [tab, setTab] = useState<'templates' | 'my-agents' | 'a2a'>('templates')
  const [agents, setAgents] = useState<Agent[]>([])
  const [publicAgents, setPublicAgents] = useState<PublicAgentTemplate[]>([])
  const [personas, setPersonas] = useState<PersonaSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [templateCat, setTemplateCat] = useState('All')
  const [showForm, setShowForm] = useState(false)
  const [editingId, setEditingId] = useState<number | null>(null)
  const [form, setForm] = useState<AgentForm>(BLANK_FORM)
  const [saving, setSaving] = useState(false)
  const [installing, setInstalling] = useState<string | null>(null)
  const [installedNames, setInstalledNames] = useState<Set<string>>(new Set())
  const [runningId, setRunningId] = useState<number | null>(null)
  const [runInput, setRunInput] = useState('')
  const [runOutput, setRunOutput] = useState('')
  const [runSteps, setRunSteps] = useState<StreamStep[]>([])
  const [testAgentId, setTestAgentId] = useState<number | null>(null)
  const [toast, setToast] = useState('')
  const [selectedPrompt, setSelectedPrompt] = useState<PromptPickerPrompt | null>(null)
  const [selectedPersonaId, setSelectedPersonaId] = useState<number | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<Agent | null>(null)

  // A2A state
  const [agentCard, setAgentCard] = useState<any>(null)
  const [agentCardLoading, setAgentCardLoading] = useState(false)
  const [a2aSkill, setA2aSkill] = useState('generate-prompt')
  const [a2aInput, setA2aInput] = useState('')
  const [a2aAgentId, setA2aAgentId] = useState('')
  const [a2aRunning, setA2aRunning] = useState(false)
  const [a2aResult, setA2aResult] = useState('')
  const [copied, setCopied] = useState('')

  useEffect(() => { fetchAgents(); fetchPublicTemplates(); fetchPersonas() }, [])

  const fetchAgents = async () => {
    setLoading(true)
    try {
      const r = await getAgents()
      setAgents(r.data)
      setInstalledNames(new Set(r.data.map((a: Agent) => a.name)))
    } catch { setAgents([]) }
    finally { setLoading(false) }
  }

  const fetchPublicTemplates = async () => {
    try {
      const r = await getPublicAgents()
      setPublicAgents(r.data)
    } catch {
      setPublicAgents([])
    }
  }

  const fetchPersonas = async () => {
    try {
      const [ownedRes, templateRes] = await Promise.all([
        api.get('/personas'),
        api.get('/personas/templates')
      ])

      const owned = Array.isArray(ownedRes.data) ? ownedRes.data : []
      const templates = Array.isArray(templateRes.data) ? templateRes.data : []
      const merged = [...owned, ...templates]
      const deduped = merged.filter((persona, index, all) => all.findIndex(item => item.id === persona.id) === index)
      setPersonas(deduped)
    } catch {
      setPersonas([])
    }
  }

  const showToast = (msg: string) => { setToast(msg); setTimeout(() => setToast(''), 3000) }

  const openCreate = () => { setForm(BLANK_FORM); setEditingId(null); setShowForm(true) }
  const openEdit = (a: Agent) => {
    setForm({
      name: a.name, description: a.description, instructions: a.instructions, model: a.model,
      enabledTools: JSON.parse(a.enabledTools || '[]'),
      toolPermissions: JSON.parse((a as any).toolPermissions || '{}'),
      inputSchemaJson: a.inputSchemaJson || BLANK_FORM.inputSchemaJson,
      memoryConfigJson: a.memoryConfigJson || BLANK_FORM.memoryConfigJson,
      guardrailsJson: a.guardrailsJson || BLANK_FORM.guardrailsJson,
      outputSchemaJson: a.outputSchemaJson || BLANK_FORM.outputSchemaJson,
      knowledgeSourcesJson: a.knowledgeSourcesJson || BLANK_FORM.knowledgeSourcesJson,
      integrationActionsJson: a.integrationActionsJson || BLANK_FORM.integrationActionsJson,
      modelRoutingJson: a.modelRoutingJson || BLANK_FORM.modelRoutingJson,
      benchmarkConfigJson: a.benchmarkConfigJson || BLANK_FORM.benchmarkConfigJson,
      governanceJson: a.governanceJson || BLANK_FORM.governanceJson,
      fallbackBehavior: a.fallbackBehavior || BLANK_FORM.fallbackBehavior,
      escalationRules: a.escalationRules || BLANK_FORM.escalationRules,
      isTemplate: !!a.isTemplate,
      templateCategory: a.templateCategory || 'General',
      isPublic: !!a.isPublic,
      temperature: a.temperature, maxIterations: a.maxIterations
    })
    setEditingId(a.id)
    setShowForm(true)
  }

  const handleSave = async () => {
    if (!form.name.trim() || !form.instructions.trim()) return
    setSaving(true)
    try {
      const payload = { ...form, enabledTools: JSON.stringify(form.enabledTools), toolPermissions: JSON.stringify(form.toolPermissions) }
      if (editingId) await updateAgent(editingId, payload)
      else await createAgent(payload)
      await fetchAgents()
      setShowForm(false)
      showToast(editingId ? 'Agent updated' : 'Agent created')
    } catch { showToast('Failed to save') }
    finally { setSaving(false) }
  }

  const handleCreateFromPrompt = async (promptId: string) => {
    try {
      await createAgentFromPrompt(promptId)
      await fetchAgents()
      showToast('Agent blueprint created from prompt')
      setSelectedPrompt(null)
      setTab('my-agents')
    } catch {
      showToast('Failed to create from prompt')
    }
  }

  const handleCreateFromPersona = async () => {
    if (!selectedPersonaId) return
    try {
      await createAgentFromPersona(selectedPersonaId)
      await fetchAgents()
      showToast('Agent blueprint created from persona')
      setTab('my-agents')
    } catch {
      showToast('Failed to create from persona')
    }
  }

  const handleDelete = async () => {
    if (!deleteTarget) return
    try {
      await deleteAgent(deleteTarget.id)
      setAgents(prev => prev.filter(a => a.id !== deleteTarget.id))
      setDeleteTarget(null)
      showToast('Agent deleted')
    } catch { showToast('Failed to delete') }
  }

  const handleInstall = async (t: typeof TEMPLATES[0]) => {
    setInstalling(t.name)
    try {
      const defaultPerms: Record<string, string> = {}
      t.enabledTools.forEach(id => { defaultPerms[id] = 'always_allow' })
      const res = await createAgent({ name: t.name, description: t.description, instructions: t.instructions, model: t.model, enabledTools: JSON.stringify(t.enabledTools), toolPermissions: JSON.stringify(defaultPerms), temperature: t.temperature, maxIterations: t.maxIterations })
      // Auto-create a scheduled job for this agent
      try {
        await api.post('/jobs', {
          name: t.name,
          agentType: 'agent',
          input: `${res.data.id}:`,
          schedule: 'daily',
          hour: 9,
          dayOfWeek: 1,
        })
      } catch { /* job creation is best-effort */ }
      await fetchAgents()
      showToast(`"${t.name}" installed — a job was created for it`)
    } catch { showToast('Install failed') }
    finally { setInstalling(null) }
  }

  const handleRun = async () => {
    if (!testAgentId || !runInput.trim()) return
    setRunningId(testAgentId)
    setRunOutput('')
    setRunSteps([])
    const baseUrl = import.meta.env.VITE_API_BASE_URL || '/api'
    const authToken = localStorage.getItem('authToken')
    try {
      const resp = await fetch(`${baseUrl}/agents/${testAgentId}/run/stream`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...(authToken ? { Authorization: `Bearer ${authToken}` } : {}) },
        body: JSON.stringify({ input: runInput }),
      })
      if (!resp.ok || !resp.body) { setRunOutput('Request failed'); return }
      const reader = resp.body.getReader()
      const decoder = new TextDecoder()
      let buf = ''
      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buf += decoder.decode(value, { stream: true })
        const lines = buf.split('\n'); buf = lines.pop() || ''
        for (const line of lines) {
          const t = line.trim()
          if (!t.startsWith('data: ')) continue
          try {
            const ev: StreamStep = JSON.parse(t.slice(6))
            if (ev.type === 'done') setRunOutput((ev as any).finalOutput || '')
            else if (ev.type === 'error') setRunOutput(`Error: ${ev.content}`)
            else setRunSteps(prev => [...prev, ev])
          } catch { /* skip */ }
        }
      }
    } catch { setRunOutput('Streaming failed. Check backend.') }
    finally { setRunningId(null) }
  }

  const toggleTool = (toolId: string) => {
    setForm(f => {
      const enabled = f.enabledTools.includes(toolId)
        ? f.enabledTools.filter(t => t !== toolId)
        : [...f.enabledTools, toolId]
      const perms = { ...f.toolPermissions }
      if (!enabled.includes(toolId)) delete perms[toolId]
      else if (!perms[toolId]) perms[toolId] = 'always_allow'
      return { ...f, enabledTools: enabled, toolPermissions: perms }
    })
  }

  const setToolPermission = (toolId: string, perm: string) => {
    setForm(f => ({ ...f, toolPermissions: { ...f.toolPermissions, [toolId]: perm } }))
  }

  const filteredTemplates = templateCat === 'All' ? TEMPLATES : TEMPLATES.filter(t => t.category === templateCat)

  const fetchAgentCard = useCallback(async () => {
    setAgentCardLoading(true)
    try {
      const baseUrl = import.meta.env.VITE_API_BASE_URL?.replace('/api', '') || ''
      const r = await fetch(`${baseUrl}/.well-known/agent.json`)
      setAgentCard(await r.json())
    } catch { setAgentCard(null) }
    finally { setAgentCardLoading(false) }
  }, [])

  const copyText = (text: string, key: string) => {
    navigator.clipboard.writeText(text)
    setCopied(key)
    setTimeout(() => setCopied(''), 2000)
  }

  const runA2ATest = async () => {
    if (!a2aInput.trim()) return
    setA2aRunning(true)
    setA2aResult('')
    try {
      const payload: any = { skillId: a2aSkill, message: { parts: [{ text: a2aInput }] } }
      if (a2aSkill === 'run-agent' && a2aAgentId) payload.parameters = { agentId: a2aAgentId }
      const r = await api.post('/a2a/tasks', payload)
      const text = r.data.artifacts?.[0]?.parts?.[0]?.text ?? JSON.stringify(r.data, null, 2)
      setA2aResult(text)
    } catch (e: any) {
      setA2aResult(e.response?.data?.error ?? 'Request failed')
    }
    finally { setA2aRunning(false) }
  }

  // Gamefield: your saved agents at their desks, plus a tool bench for each call
  // the agent currently running has made.
  const field = useMemo<FieldModel>(() => {
    const benches = runSteps
      .filter(st => st.type === 'tool_call' || st.type === 'tool_result')
      .slice(0, 8)
      .map((st, i) => ({
        id: `step-${i}`,
        name: st.toolName || 'tool',
        role: 'tool bench',
        status: (st.type === 'tool_result' ? 'done' : 'working') as 'done' | 'working',
        detail: bubble(st.toolOutput || st.toolInput),
        output: st.toolOutput || st.toolInput,
      }))

    const desks = agents.slice(0, 12).map(a => ({
      id: `agent-${a.id}`,
      name: a.name,
      role: a.model || 'agent',
      status: (runningId === a.id ? 'working' : runOutput && runningId === a.id ? 'done' : 'idle') as 'working' | 'done' | 'idle',
      detail: runningId === a.id ? bubble(runInput) || 'Working the input…' : undefined,
      output: runningId === a.id ? runOutput : undefined,
    }))

    return {
      title: 'Agents',
      subtitle: `${agents.length} saved`,
      running: runningId != null || a2aRunning,
      agents: [
        ...desks,
        ...benches,
        {
          id: 'floor',
          name: 'Floor Lead',
          role: tab === 'a2a' ? 'agent-to-agent' : tab,
          status: (runningId != null || a2aRunning ? 'working' : runOutput || a2aResult ? 'done' : 'idle') as 'working' | 'done' | 'idle',
          detail: bubble(a2aResult || runOutput),
          output: a2aResult || runOutput || undefined,
          boss: true,
        },
      ],
      messages: benches.map(b => ({ id: `bench-${b.id}`, from: 'floor', to: b.id })),
      events: runSteps.slice(-15).map((st, i) => ({
        id: `rs-${i}`,
        text: bubble(st.toolName ? `${st.toolName}: ${st.toolInput ?? ''}` : st.content, 90) || st.type,
        tone: (st.type === 'error' ? 'bad' : st.type === 'output' ? 'good' : 'info') as 'bad' | 'good' | 'info',
      })),
    }
  }, [agents, runningId, runSteps, runOutput, runInput, a2aRunning, a2aResult, tab])

  return (
    <div className="agents-page" style={{ padding: 24, display: 'flex', flexDirection: 'column', gap: 20 }}>
      <ConfirmDialog
        open={!!deleteTarget}
        title="Delete agent?"
        message={`This will permanently delete "${deleteTarget?.name ?? 'this agent'}".`}
        confirmLabel="Delete"
        onConfirm={handleDelete}
        onCancel={() => setDeleteTarget(null)}
      />

      <AgentStage {...stageProps} model={field} />

      {/* Header */}
      <div className="agents-page-hero" style={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', flexWrap: 'wrap', gap: 12 }}>
        <div>
          <h1 style={{ fontSize: 22, fontWeight: 700, margin: '0 0 4px' }}>
            <PhosphorIcon className="bi bi-robot" style={{ marginRight: 10, color: 'var(--primary)' }} />Agents
          </h1>
          <p style={{ color: 'var(--text-secondary)', fontSize: 13, margin: 0 }}>
            Build AI agents with tools, memory, governance, connectors, model routing, and evaluation.
          </p>
        </div>
        <div className="agents-hero-actions" style={{ display: 'flex', flexWrap: 'wrap', gap: 8, alignItems: 'center', justifyContent: 'flex-end' }}>
          <PromptPicker label={selectedPrompt?.name || 'Select prompt'} onSelect={p => setSelectedPrompt(p)} />
          <button className="btn btn-success" onClick={() => selectedPrompt && handleCreateFromPrompt(selectedPrompt.id)} disabled={!selectedPrompt}>
            <PhosphorIcon className="bi bi-box-arrow-in-down-right" />Convert Prompt
          </button>
          <select className="form-input" style={{ width: 240 }} value={selectedPersonaId ?? ''} onChange={e => setSelectedPersonaId(e.target.value ? Number(e.target.value) : null)}>
            <option value="">Select persona</option>
            {personas.map(persona => <option key={persona.id} value={persona.id}>{persona.name}{persona.isTemplate ? ' · Template' : ''}</option>)}
          </select>
          <button className="btn btn-success" onClick={handleCreateFromPersona} disabled={!selectedPersonaId}>
            <PhosphorIcon className="bi bi-person-badge" />Convert Persona
          </button>
          <button className="btn btn-primary" onClick={openCreate}>
            <PhosphorIcon className="bi bi-plus-lg" />New Agent
          </button>
        </div>
      </div>

      {toast && (
        <div style={{ padding: '8px 16px', background: 'var(--primary)', color: '#fff', borderRadius: 8, fontSize: 13, fontWeight: 500 }}>
          {toast}
        </div>
      )}

      {/* Tabs */}
      <div className="agents-tabs" style={{ display: 'flex', gap: 4, borderBottom: '1px solid var(--border)', paddingBottom: 0 }}>
        {([
          ['templates', 'bi-grid-3x3-gap', 'Template Gallery'],
          ['my-agents', 'bi-person-gear', `My Agents (${agents.length})`],
          ['a2a', 'bi-share', 'A2A Integration'],
        ] as const).map(([id, icon, label]) => (
          <button className="agents-tab-btn" key={id} onClick={() => { setTab(id); if (id === 'a2a' && !agentCard) fetchAgentCard() }}
            style={{ background: 'none', border: 'none', borderBottom: tab === id ? '2px solid var(--primary)' : '2px solid transparent', color: tab === id ? 'var(--primary)' : 'var(--text-secondary)', fontWeight: tab === id ? 600 : 400, fontSize: 14, padding: '8px 16px', cursor: 'pointer', marginBottom: -1, transition: 'all 0.15s' }}>
            <PhosphorIcon className={`bi ${icon}`} style={{ marginRight: 6 }} />{label}
          </button>
        ))}
      </div>

      {/* Template Gallery */}
      {tab === 'templates' && (
        <div>
          <div className="agents-filter-row" style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginBottom: 20 }}>
            {TEMPLATE_CATEGORIES.map(c => (
              <button className="agents-filter-chip" key={c} onClick={() => setTemplateCat(c)}
                style={{ padding: '5px 14px', borderRadius: 20, border: `1.5px solid ${templateCat === c ? 'var(--primary)' : 'var(--border)'}`, background: templateCat === c ? 'var(--primary)' : 'var(--bg-card)', color: templateCat === c ? '#fff' : 'var(--text-secondary)', fontSize: 13, fontWeight: 500, cursor: 'pointer', transition: 'all 0.15s' }}>
                {c}
              </button>
            ))}
          </div>
          <div className="agents-template-grid" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))', gap: 16 }}>
            {filteredTemplates.map(t => {
              const isInstalled = installedNames.has(t.name)
              const isInstalling = installing === t.name
              return (
                <div key={t.name} className="card agent-template-card" style={{ padding: 20, display: 'flex', flexDirection: 'column', gap: 12 }}>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start' }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                      <PhosphorIcon className={`bi ${t.icon} agent-template-icon`} style={{ fontSize: 28 }} />
                      <div>
                        <div style={{ fontWeight: 600, fontSize: 15 }}>{t.name}</div>
                        <span style={{ fontSize: 11, padding: '2px 8px', borderRadius: 10, background: 'var(--bg-secondary)', color: 'var(--text-secondary)', fontWeight: 600 }}>{t.category}</span>
                      </div>
                    </div>
                  </div>
                  <p style={{ fontSize: 13, color: 'var(--text-secondary)', lineHeight: 1.6, margin: 0 }}>{t.description}</p>
                  <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                    {t.enabledTools.map(tool => (
                      <span key={tool} style={{ fontSize: 11, padding: '2px 8px', borderRadius: 10, background: 'rgba(24,119,242,0.08)', color: 'var(--primary)', fontWeight: 600 }}>
                        <PhosphorIcon className={`bi ${TOOLS.find(x => x.id === tool)?.icon ?? 'bi-gear'}`} style={{ marginRight: 4 }} />{tool.replace('_', ' ')}
                      </span>
                    ))}
                    {t.enabledTools.length === 0 && <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>No tools</span>}
                  </div>
                  <div className="agent-template-footer" style={{ borderTop: '1px solid var(--border)', paddingTop: 12, display: 'flex', gap: 8 }}>
                    {isInstalled && (
                      <button className="btn btn-secondary" disabled style={{ fontSize: 13 }}>
                        <PhosphorIcon className="bi bi-check2" />Installed
                      </button>
                    )}
                    <button className="btn btn-primary" onClick={() => handleInstall(t)} disabled={!!isInstalling} style={{ flex: 1, fontSize: 13 }}>
                      {isInstalling ? <><div className="spinner" />Installing…</> : isInstalled ? <><PhosphorIcon className="bi bi-download" />Install Again</> : <><PhosphorIcon className="bi bi-download" />Install</>}
                    </button>
                  </div>
                </div>
              )
            })}
          </div>

          {publicAgents.length > 0 && (
            <div style={{ marginTop: 24 }}>
              <h3 style={{ fontSize: 16, marginBottom: 12 }}>Community Agent Templates</h3>
              <div className="agents-template-grid" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(320px, 1fr))', gap: 16 }}>
                {publicAgents.slice(0, 6).map(agent => {
                  const tools = JSON.parse(agent.enabledTools || '[]') as string[]
                  return (
                    <div key={`public-${agent.id}`} className="card agent-template-card" style={{ padding: 18, display: 'flex', flexDirection: 'column', gap: 10 }}>
                      <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8 }}>
                        <div>
                          <div style={{ fontWeight: 700 }}>{agent.name}</div>
                          <div style={{ fontSize: 12, color: 'var(--text-secondary)' }}>by {agent.authorName}</div>
                        </div>
                        <span style={{ fontSize: 11, padding: '2px 8px', borderRadius: 10, background: 'var(--bg-secondary)' }}>{agent.templateCategory || 'General'}</span>
                      </div>
                      <p style={{ margin: 0, fontSize: 13, color: 'var(--text-secondary)', lineHeight: 1.6 }}>{agent.description}</p>
                      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                        {tools.slice(0, 4).map(t => <span key={t} style={{ fontSize: 11, padding: '2px 8px', borderRadius: 10, background: 'rgba(24,119,242,0.08)', color: 'var(--primary)' }}>{t}</span>)}
                      </div>
                    </div>
                  )
                })}
              </div>
            </div>
          )}
        </div>
      )}

      {/* My Agents */}
      {tab === 'my-agents' && (
        <div>
          {loading ? (
            <div className="card" style={{ padding: 40, textAlign: 'center', color: 'var(--text-secondary)' }}>
              <div className="spinner" style={{ margin: '0 auto 12px' }} /><p>Loading agents…</p>
            </div>
          ) : agents.length === 0 ? (
            <div className="card" style={{ padding: 48, textAlign: 'center' }}>
              <PhosphorIcon className="bi bi-robot" style={{ fontSize: 48, color: 'var(--text-secondary)' }} />
              <p style={{ color: 'var(--text-secondary)', margin: '12px 0 20px', fontSize: 15 }}>No agents yet. Install a template or create your own.</p>
              <div style={{ display: 'flex', gap: 10, justifyContent: 'center' }}>
                <button className="btn btn-secondary" onClick={() => setTab('templates')}><PhosphorIcon className="bi bi-grid-3x3-gap" />Browse Templates</button>
                <button className="btn btn-primary" onClick={openCreate}><PhosphorIcon className="bi bi-plus-lg" />Create Agent</button>
              </div>
            </div>
          ) : (
            <div className="agents-list" style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {agents.map(a => {
                const tools: string[] = JSON.parse(a.enabledTools || '[]')
                const isTesting = testAgentId === a.id
                return (
                  <div key={a.id} className="card agent-owned-card" style={{ padding: 20 }}>
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 12 }}>
                      <div style={{ flex: 1 }}>
                        <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 4 }}>
                          <PhosphorIcon className="bi bi-robot" style={{ color: 'var(--primary)', fontSize: 18 }} />
                          <span style={{ fontWeight: 600, fontSize: 15 }}>{a.name}</span>
                          <span style={{ fontSize: 11, padding: '2px 8px', borderRadius: 10, background: 'var(--bg-secondary)', color: 'var(--text-secondary)' }}>{a.model}</span>
                          {a.templateCategory && <span style={{ fontSize: 11, padding: '2px 8px', borderRadius: 10, background: 'rgba(16,185,129,0.12)', color: '#059669' }}>{a.templateCategory}</span>}
                          {!!a.isTemplate && <span style={{ fontSize: 11, padding: '2px 8px', borderRadius: 10, background: 'rgba(131,58,180,0.12)', color: '#833ab4' }}>Template v{a.version ?? 1}</span>}
                        </div>
                        {a.description && <p style={{ fontSize: 13, color: 'var(--text-secondary)', margin: '0 0 8px' }}>{a.description}</p>}
                        <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
                          {tools.map(t => (
                            <span key={t} style={{ fontSize: 11, padding: '2px 8px', borderRadius: 10, background: 'rgba(24,119,242,0.08)', color: 'var(--primary)', fontWeight: 600 }}>
                              <PhosphorIcon className={`bi ${TOOLS.find(x => x.id === t)?.icon ?? 'bi-gear'}`} style={{ marginRight: 4 }} />{t.replace('_', ' ')}
                            </span>
                          ))}
                          {tools.length === 0 && <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>No tools</span>}
                          {(a.knowledgeSourcesJson && a.knowledgeSourcesJson !== '[]') && <span style={{ fontSize: 11, color: '#c13584' }}>Knowledge connected</span>}
                          {(a.integrationActionsJson && a.integrationActionsJson !== '[]') && <span style={{ fontSize: 11, color: '#833ab4' }}>Microsoft actions ready</span>}
                        </div>
                      </div>
                      <div style={{ display: 'flex', gap: 8, flexShrink: 0 }}>
                        <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => { setTestAgentId(isTesting ? null : a.id); setRunInput(''); setRunOutput('') }}>
                          <PhosphorIcon className={`bi ${isTesting ? 'bi-chevron-up' : 'bi-play'}`} />{isTesting ? 'Close' : 'Test'}
                        </button>
                        <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => openEdit(a)}>
                          <PhosphorIcon className="bi bi-pencil" />Edit
                        </button>
                        <button className="btn btn-secondary" style={{ fontSize: 13, color: '#ef4444' }} onClick={() => setDeleteTarget(a)}>
                          <PhosphorIcon className="bi bi-trash" />
                        </button>
                      </div>
                    </div>

                    {/* Inline test runner */}
                    {isTesting && (
                      <div style={{ marginTop: 16, borderTop: '1px solid var(--border)', paddingTop: 16, display: 'flex', flexDirection: 'column', gap: 10 }}>
                        <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
                          <PromptPicker onSelect={p => setRunInput(p.content)} />
                        </div>
                        <textarea className="form-textarea" rows={2} placeholder="Enter a task for this agent…" value={runInput} onChange={e => setRunInput(e.target.value)} style={{ fontSize: 13 }} />
                        <button className="btn btn-primary" onClick={handleRun} disabled={!runInput.trim() || !!runningId} style={{ alignSelf: 'flex-start', fontSize: 13 }}>
                          {runningId === a.id ? <><div className="spinner" />Running…</> : <><PhosphorIcon className="bi bi-play-fill" />Run</>}
                        </button>
                        {/* Step-by-step trace */}
                        {runSteps.length > 0 && (
                          <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                            {runSteps.map((s, i) => (
                              <div key={i} style={{ fontSize: 12, padding: '8px 12px', borderRadius: 8, border: '1px solid var(--border)',
                                background: s.type === 'thinking' ? 'var(--bg-secondary)' : s.type === 'tool_call' ? 'rgba(214,41,118,0.07)' : s.type === 'tool_result' ? 'rgba(34,197,94,0.07)' : 'var(--bg-secondary)' }}>
                                <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: s.type === 'tool_call' || s.type === 'tool_result' ? 4 : 0 }}>
                                  <PhosphorIcon className={`bi ${s.type === 'thinking' ? 'bi-lightbulb' : s.type === 'tool_call' ? 'bi-wrench' : s.type === 'tool_result' ? 'bi-check2-circle' : 'bi-chat'}`}
                                    style={{ color: s.type === 'tool_call' ? '#3b82f6' : s.type === 'tool_result' ? '#16a34a' : 'var(--text-muted)', fontSize: 11 }} />
                                  <span style={{ fontWeight: 600, color: 'var(--text-secondary)', textTransform: 'uppercase', fontSize: 10, letterSpacing: '0.05em' }}>
                                    {s.type === 'tool_call' ? `${s.toolName}` : s.type}
                                  </span>
                                </div>
                                {s.type === 'tool_call' && s.toolInput && <div style={{ color: 'var(--text-muted)', fontFamily: 'monospace', fontSize: 11 }}>{s.toolInput}</div>}
                                {s.type === 'tool_result' && s.toolOutput && <div style={{ color: 'var(--text-secondary)', lineHeight: 1.5 }}>{s.toolOutput.slice(0, 300)}{s.toolOutput.length > 300 ? '…' : ''}</div>}
                                {s.type === 'thinking' && s.content && <div style={{ color: 'var(--text-secondary)', fontStyle: 'italic' }}>{s.content}</div>}
                                {s.type === 'done' && s.content && <div style={{ color: 'var(--text-secondary)' }}>{s.content}</div>}
                              </div>
                            ))}
                            {runningId === a.id && <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12, color: 'var(--text-muted)', padding: '4px 0' }}><div className="spinner" style={{ width: 12, height: 12 }} />Agent is thinking…</div>}
                          </div>
                        )}
                        {runOutput && (
                          <div style={{ background: 'var(--bg-secondary)', borderRadius: 8, padding: 14, fontSize: 13, lineHeight: 1.7, whiteSpace: 'pre-wrap', color: 'var(--text-primary)', borderLeft: '3px solid var(--primary)' }}>
                            <div style={{ fontSize: 10, fontWeight: 700, color: 'var(--primary)', marginBottom: 6, textTransform: 'uppercase', letterSpacing: '0.05em' }}>Final Output</div>
                            {runOutput}
                          </div>
                        )}
                      </div>
                    )}
                  </div>
                )
              })}
            </div>
          )}
        </div>
      )}

      {/* A2A Integration */}
      {tab === 'a2a' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>

          {/* Intro */}
          <div className="card" style={{ padding: 24, display: 'flex', gap: 20, alignItems: 'flex-start' }}>
            <div style={{ fontSize: 40 }}>🔗</div>
            <div>
              <h2 style={{ fontSize: 17, fontWeight: 700, margin: '0 0 6px' }}>Agent-to-Agent (A2A) Protocol</h2>
              <p style={{ color: 'var(--text-secondary)', fontSize: 14, lineHeight: 1.7, margin: 0 }}>
                Connect agents built in LangChain, CrewAI, OpenAI Assistants, or any A2A-compatible framework.
                GenPrompt exposes a standard agent card and task endpoint so external agents can invoke your prompts,
                chains, and installed agents. Generate an API key in <strong>Settings → API Keys</strong>, then use it as
                the <code style={{ background: 'var(--bg-secondary)', padding: '1px 6px', borderRadius: 4, fontSize: 12 }}>X-GenPrompt-Header</code> on task requests.
              </p>
            </div>
          </div>

          {/* Endpoints */}
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(360px, 1fr))', gap: 16 }}>
            {/* Agent Card */}
            <div className="card" style={{ padding: 20 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 12 }}>
                <span style={{ fontSize: 11, fontWeight: 700, padding: '3px 8px', borderRadius: 6, background: '#22c55e22', color: '#16a34a' }}>GET</span>
                <code style={{ fontSize: 13, fontWeight: 600 }}>/.well-known/agent.json</code>
                <span style={{ fontSize: 11, color: 'var(--text-muted)', marginLeft: 'auto' }}>Public</span>
              </div>
              <p style={{ fontSize: 13, color: 'var(--text-secondary)', margin: '0 0 12px' }}>Agent Card — discover capabilities, skills, and auth requirements.</p>
              <div style={{ display: 'flex', gap: 8 }}>
                <button className="btn btn-secondary" style={{ fontSize: 12 }} onClick={fetchAgentCard} disabled={agentCardLoading}>
                  {agentCardLoading ? <><div className="spinner" />Fetching…</> : <><PhosphorIcon className="bi bi-eye" />Preview</>}
                </button>
                <button className="btn btn-secondary" style={{ fontSize: 12 }} onClick={() => copyText(`curl ${window.location.origin}/.well-known/agent.json`, 'card-curl')}>
                  <PhosphorIcon className={`bi ${copied === 'card-curl' ? 'bi-check2' : 'bi-clipboard'}`} />{copied === 'card-curl' ? 'Copied!' : 'Copy curl'}
                </button>
              </div>
              {agentCard && (
                <pre style={{ marginTop: 12, background: 'var(--bg-secondary)', borderRadius: 8, padding: 12, fontSize: 11, lineHeight: 1.6, overflowX: 'auto', color: 'var(--text-primary)', maxHeight: 260, overflowY: 'auto' }}>
                  {JSON.stringify(agentCard, null, 2)}
                </pre>
              )}
            </div>

            {/* Task Submit */}
            <div className="card" style={{ padding: 20 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 12 }}>
                <span style={{ fontSize: 11, fontWeight: 700, padding: '3px 8px', borderRadius: 6, background: '#3b82f622', color: '#c13584' }}>POST</span>
                <code style={{ fontSize: 13, fontWeight: 600 }}>/api/a2a/tasks</code>
                <span style={{ fontSize: 11, color: 'var(--text-muted)', marginLeft: 'auto' }}>API Key</span>
              </div>
              <p style={{ fontSize: 13, color: 'var(--text-secondary)', margin: '0 0 12px' }}>Submit a task to any GenPrompt skill. Returns a result artifact.</p>
              <button className="btn btn-secondary" style={{ fontSize: 12 }} onClick={() => copyText(
`curl -X POST ${window.location.origin}/api/a2a/tasks \\
  -H "Content-Type: application/json" \\
  -H "X-GenPrompt-Header: YOUR_API_KEY" \\
  -d '{"skillId":"generate-prompt","message":{"parts":[{"text":"Write a prompt for summarising emails"}]}}'`, 'task-curl')}>
                <PhosphorIcon className={`bi ${copied === 'task-curl' ? 'bi-check2' : 'bi-clipboard'}`} />{copied === 'task-curl' ? 'Copied!' : 'Copy example curl'}
              </button>
            </div>
          </div>

          {/* Skills Reference */}
          <div className="card" style={{ padding: 20 }}>
            <h3 style={{ fontSize: 15, fontWeight: 700, margin: '0 0 14px' }}>Available Skills</h3>
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(280px, 1fr))', gap: 10 }}>
              {[
                { id: 'generate-prompt', icon: 'bi-stars', label: 'Generate Prompt', desc: 'Generate a production-ready AI prompt from a natural language goal.', param: null },
                { id: 'test-prompt', icon: 'bi-play-fill', label: 'Test Prompt', desc: 'Run a prompt through AI and return the output.', param: null },
                { id: 'execute-chain', icon: 'bi-diagram-3', label: 'Execute Chain', desc: 'Run a saved workflow chain. Requires chainId parameter.', param: 'chainId' },
                { id: 'swarm-generate', icon: 'bi-people', label: 'Swarm Generate', desc: 'Multi-agent consensus prompt synthesis.', param: null },
                { id: 'run-agent', icon: 'bi-robot', label: 'Run Agent', desc: 'Run one of your installed agents by ID. Requires agentId parameter.', param: 'agentId' },
              ].map(s => (
                <div key={s.id} style={{ padding: '12px 14px', borderRadius: 10, border: '1px solid var(--border)', background: 'var(--bg-secondary)' }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 4 }}>
                    <PhosphorIcon className={`bi ${s.icon}`} style={{ fontSize: 18 }} />
                    <span style={{ fontWeight: 600, fontSize: 13 }}>{s.label}</span>
                    {s.param && <span style={{ fontSize: 10, padding: '1px 6px', borderRadius: 6, background: 'rgba(251,146,60,0.15)', color: '#ea580c', marginLeft: 'auto' }}>+{s.param}</span>}
                  </div>
                  <p style={{ fontSize: 12, color: 'var(--text-secondary)', margin: '0 0 8px', lineHeight: 1.5 }}>{s.desc}</p>
                  <button className="btn btn-secondary" style={{ fontSize: 11, padding: '3px 10px' }} onClick={() => { setA2aSkill(s.id); const el = document.getElementById('a2a-tester'); el?.scrollIntoView({ behavior: 'smooth' }) }}>
                    Try it →
                  </button>
                </div>
              ))}
            </div>
          </div>

          {/* Live Tester */}
          <div className="card" style={{ padding: 24 }} id="a2a-tester">
            <h3 style={{ fontSize: 15, fontWeight: 700, margin: '0 0 4px' }}>Live Tester</h3>
            <p style={{ fontSize: 13, color: 'var(--text-secondary)', margin: '0 0 16px' }}>Test the A2A task endpoint directly using your session credentials.</p>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
                <div style={{ flex: 1, minWidth: 180 }}>
                  <label className="form-label">Skill</label>
                  <select className="form-input" value={a2aSkill} onChange={e => setA2aSkill(e.target.value)}>
                    <option value="generate-prompt">generate-prompt</option>
                    <option value="test-prompt">test-prompt</option>
                    <option value="execute-chain">execute-chain</option>
                    <option value="swarm-generate">swarm-generate</option>
                    <option value="run-agent">run-agent</option>
                  </select>
                </div>
                {a2aSkill === 'run-agent' && (
                  <div style={{ flex: 1, minWidth: 140 }}>
                    <label className="form-label">Agent ID</label>
                    <select className="form-input" value={a2aAgentId} onChange={e => setA2aAgentId(e.target.value)}>
                      <option value="">Select agent…</option>
                      {agents.map(a => <option key={a.id} value={String(a.id)}>{a.name}</option>)}
                    </select>
                  </div>
                )}
                {a2aSkill === 'execute-chain' && (
                  <div style={{ flex: 1, minWidth: 140 }}>
                    <label className="form-label">Chain ID</label>
                    <input className="form-input" placeholder="e.g. 3" value={a2aAgentId} onChange={e => setA2aAgentId(e.target.value)} />
                  </div>
                )}
              </div>
              <div>
                <label className="form-label">Input message</label>
                <textarea className="form-textarea" rows={3} placeholder="Enter your task…" value={a2aInput} onChange={e => setA2aInput(e.target.value)} />
              </div>
              <button className="btn btn-primary" style={{ alignSelf: 'flex-start' }} onClick={runA2ATest} disabled={!a2aInput.trim() || a2aRunning}>
                {a2aRunning ? <><div className="spinner" />Running…</> : <><PhosphorIcon className="bi bi-send" />Send Task</>}
              </button>
              {a2aResult && (
                <div>
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 8 }}>
                    <label className="form-label" style={{ margin: 0 }}>Response</label>
                    <button className="btn btn-secondary" style={{ fontSize: 12, padding: '3px 10px' }} onClick={() => copyText(a2aResult, 'a2a-result')}>
                      <PhosphorIcon className={`bi ${copied === 'a2a-result' ? 'bi-check2' : 'bi-clipboard'}`} />{copied === 'a2a-result' ? 'Copied!' : 'Copy'}
                    </button>
                  </div>
                  <pre style={{ background: 'var(--bg-secondary)', borderRadius: 8, padding: 14, fontSize: 12, lineHeight: 1.7, whiteSpace: 'pre-wrap', color: 'var(--text-primary)', maxHeight: 360, overflowY: 'auto' }}>{a2aResult}</pre>
                </div>
              )}
            </div>
          </div>

          {/* SDK snippet */}
          <div className="card" style={{ padding: 20 }}>
            <h3 style={{ fontSize: 15, fontWeight: 700, margin: '0 0 12px' }}>Python SDK Example</h3>
            <pre style={{ background: 'var(--bg-secondary)', borderRadius: 8, padding: 14, fontSize: 12, lineHeight: 1.7, color: 'var(--text-primary)', overflowX: 'auto', margin: 0 }}>{`import requests

GENPROMPT_URL = "${window.location.origin}"
API_KEY = "your-api-key-from-settings"

def call_genprompt(skill_id: str, message: str, params: dict = None):
    resp = requests.post(
        f"{GENPROMPT_URL}/api/a2a/tasks",
        headers={"X-GenPrompt-Header": API_KEY},
        json={
            "skillId": skill_id,
            "message": {"parts": [{"text": message}]},
            "parameters": params or {}
        }
    )
    resp.raise_for_status()
    return resp.json()["artifacts"][0]["parts"][0]["text"]

# Generate a prompt
result = call_genprompt("generate-prompt", "Write a prompt for classifying support tickets")
print(result)

# Run an installed agent by ID
result = call_genprompt("run-agent", "Research latest AI trends", {"agentId": "1"})`}
            </pre>
            <button className="btn btn-secondary" style={{ marginTop: 12, fontSize: 12 }} onClick={() => copyText(
              `import requests\n\nGENPROMPT_URL = "${window.location.origin}"\nAPI_KEY = "your-api-key-from-settings"\n\ndef call_genprompt(skill_id, message, params=None):\n    resp = requests.post(\n        f"{window.location.origin}/api/a2a/tasks",\n        headers={"X-GenPrompt-Header": API_KEY},\n        json={"skillId": skill_id, "message": {"parts": [{"text": message}]}, "parameters": params or {}}\n    )\n    resp.raise_for_status()\n    return resp.json()["artifacts"][0]["parts"][0]["text"]`, 'py-snippet')}>
              <PhosphorIcon className={`bi ${copied === 'py-snippet' ? 'bi-check2' : 'bi-clipboard'}`} />{copied === 'py-snippet' ? 'Copied!' : 'Copy Python snippet'}
            </button>
          </div>

        </div>
      )}

      {/* Create / Edit modal */}
      {showForm && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1000, padding: 20 }}>
          <div className="card" style={{ width: '100%', maxWidth: 760, padding: 28, maxHeight: '90vh', overflowY: 'auto' }}>
            <h2 style={{ fontSize: 18, fontWeight: 700, margin: '0 0 20px' }}>{editingId ? 'Edit Agent' : 'New Agent'}</h2>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
              <div>
                <label className="form-label">Name *</label>
                <input className="form-input" value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} placeholder="e.g. Market Research Agent" />
              </div>
              <div>
                <label className="form-label">Description</label>
                <input className="form-input" value={form.description} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} placeholder="What does this agent do?" />
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
                <div>
                  <label className="form-label">Template Category</label>
                  <input className="form-input" value={form.templateCategory} onChange={e => setForm(f => ({ ...f, templateCategory: e.target.value }))} placeholder="Research, Sales, Support..." />
                </div>
                <div style={{ display: 'flex', gap: 18, alignItems: 'center', paddingTop: 28 }}>
                  <label style={{ display: 'flex', gap: 8, alignItems: 'center', fontSize: 13 }}><input type="checkbox" checked={form.isTemplate} onChange={e => setForm(f => ({ ...f, isTemplate: e.target.checked }))} /> Template</label>
                  <label style={{ display: 'flex', gap: 8, alignItems: 'center', fontSize: 13 }}><input type="checkbox" checked={form.isPublic} onChange={e => setForm(f => ({ ...f, isPublic: e.target.checked }))} /> Community visible</label>
                </div>
              </div>
              <div>
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 4 }}>
                  <label className="form-label" style={{ margin: 0 }}>Instructions / System Prompt *</label>
                  <PromptPicker onSelect={p => setForm(f => ({ ...f, instructions: p.content }))} />
                </div>
                <textarea className="form-textarea" rows={5} value={form.instructions} onChange={e => setForm(f => ({ ...f, instructions: e.target.value }))} placeholder="You are a… Your job is to…" />
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
                <div>
                  <label className="form-label">Model</label>

              <div style={{ borderTop: '1px solid var(--border)', paddingTop: 12 }}>
                <h3 style={{ margin: '0 0 10px', fontSize: 15 }}>Agent Blueprint</h3>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
                  <div>
                    <label className="form-label">Input Schema JSON</label>
                    <textarea className="form-textarea" rows={4} value={form.inputSchemaJson} onChange={e => setForm(f => ({ ...f, inputSchemaJson: e.target.value }))} />
                  </div>
                  <div>
                    <label className="form-label">Output Schema JSON</label>
                    <textarea className="form-textarea" rows={4} value={form.outputSchemaJson} onChange={e => setForm(f => ({ ...f, outputSchemaJson: e.target.value }))} />
                  </div>
                </div>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginTop: 12 }}>
                  <div>
                    <label className="form-label">Fallback Behavior</label>
                    <textarea className="form-textarea" rows={3} value={form.fallbackBehavior} onChange={e => setForm(f => ({ ...f, fallbackBehavior: e.target.value }))} />
                  </div>
                  <div>
                    <label className="form-label">Escalation Rules</label>
                    <textarea className="form-textarea" rows={3} value={form.escalationRules} onChange={e => setForm(f => ({ ...f, escalationRules: e.target.value }))} />
                  </div>
                </div>
              </div>

              <div style={{ borderTop: '1px solid var(--border)', paddingTop: 12 }}>
                <h3 style={{ margin: '0 0 10px', fontSize: 15 }}>Memory, Knowledge, and Integrations</h3>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
                  <div>
                    <label className="form-label">Memory Config JSON</label>
                    <textarea className="form-textarea" rows={4} value={form.memoryConfigJson} onChange={e => setForm(f => ({ ...f, memoryConfigJson: e.target.value }))} />
                  </div>
                  <div>
                    <label className="form-label">Knowledge Sources JSON</label>
                    <textarea className="form-textarea" rows={4} value={form.knowledgeSourcesJson} onChange={e => setForm(f => ({ ...f, knowledgeSourcesJson: e.target.value }))} placeholder='[{"type":"url","name":"Docs","url":"https://..."}]' />
                  </div>
                </div>
                <div style={{ marginTop: 12 }}>
                  <label className="form-label">Integration Actions JSON</label>
                  <textarea className="form-textarea" rows={4} value={form.integrationActionsJson} onChange={e => setForm(f => ({ ...f, integrationActionsJson: e.target.value }))} placeholder='[{"toolName":"microsoft_teams_message","displayName":"Post to Launch Channel","target":"General"}]' />
                </div>
              </div>

              <div style={{ borderTop: '1px solid var(--border)', paddingTop: 12 }}>
                <h3 style={{ margin: '0 0 10px', fontSize: 15 }}>Governance and Routing</h3>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
                  <div>
                    <label className="form-label">Guardrails JSON</label>
                    <textarea className="form-textarea" rows={4} value={form.guardrailsJson} onChange={e => setForm(f => ({ ...f, guardrailsJson: e.target.value }))} />
                  </div>
                  <div>
                    <label className="form-label">Governance JSON</label>
                    <textarea className="form-textarea" rows={4} value={form.governanceJson} onChange={e => setForm(f => ({ ...f, governanceJson: e.target.value }))} placeholder='{"approvalMode":"manual","citationRequired":true}' />
                  </div>
                </div>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginTop: 12 }}>
                  <div>
                    <label className="form-label">Model Routing JSON</label>
                    <textarea className="form-textarea" rows={4} value={form.modelRoutingJson} onChange={e => setForm(f => ({ ...f, modelRoutingJson: e.target.value }))} placeholder='{"mode":"benchmark","models":["gpt-4o","claude-3-5-sonnet"]}' />
                  </div>
                  <div>
                    <label className="form-label">Benchmark Config JSON</label>
                    <textarea className="form-textarea" rows={4} value={form.benchmarkConfigJson} onChange={e => setForm(f => ({ ...f, benchmarkConfigJson: e.target.value }))} placeholder='{"enabled":true,"metrics":["latency","cost","safety"]}' />
                  </div>
                </div>
              </div>
                  <select className="form-input" value={form.model} onChange={e => setForm(f => ({ ...f, model: e.target.value }))}>
                    {MODELS.map(m => <option key={m} value={m}>{m}</option>)}
                  </select>
                </div>
                <div>
                  <label className="form-label">Max Iterations</label>
                  <input className="form-input" type="number" min={1} max={20} value={form.maxIterations} onChange={e => setForm(f => ({ ...f, maxIterations: parseInt(e.target.value) || 5 }))} />
                </div>
              </div>
              <div>
                <label className="form-label" style={{ marginBottom: 8 }}>Tools</label>
                <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 8 }}>
                  {TOOLS.map(tool => {
                    const active = form.enabledTools.includes(tool.id)
                    return (
                      <button key={tool.id} onClick={() => toggleTool(tool.id)}
                        style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '8px 12px', borderRadius: 10, border: `1.5px solid ${active ? 'var(--primary)' : 'var(--border)'}`, background: active ? 'rgba(24,119,242,0.07)' : 'var(--bg-card)', color: active ? 'var(--primary)' : 'var(--text-secondary)', fontSize: 13, fontWeight: 500, cursor: 'pointer', transition: 'all 0.15s', textAlign: 'left' }}>
                        <PhosphorIcon className={`bi ${tool.icon}`} style={{ flexShrink: 0 }} />
                        <div style={{ flex: 1, minWidth: 0 }}>
                          <div style={{ fontWeight: 600 }}>{tool.label}</div>
                          <div style={{ fontSize: 11, opacity: 0.8, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{tool.desc}</div>
                        </div>
                        {active && <PhosphorIcon className="bi bi-check2-circle" style={{ flexShrink: 0 }} />}
                      </button>
                    )
                  })}
                </div>
              </div>

              {/* Per-tool permission toggles */}
              {form.enabledTools.length > 0 && (
                <div>
                  <label className="form-label" style={{ marginBottom: 8 }}>Tool Permissions</label>
                  <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                    {form.enabledTools.map(toolId => {
                      const tool = TOOLS.find(t => t.id === toolId)
                      const perm = form.toolPermissions[toolId] || 'always_allow'
                      return (
                        <div key={toolId} style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '8px 12px', borderRadius: 8, border: '1px solid var(--border)', background: 'var(--bg-secondary)' }}>
                          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                            <PhosphorIcon className={`bi ${tool?.icon}`} style={{ color: 'var(--primary)', fontSize: 14 }} />
                            <span style={{ fontSize: 13, fontWeight: 500 }}>{tool?.label}</span>
                          </div>
                          <div style={{ display: 'flex', gap: 4 }}>
                            {Object.entries(PERMISSION_LABELS).map(([p, { label, color }]) => (
                              <button key={p} onClick={() => setToolPermission(toolId, p)}
                                style={{ padding: '3px 10px', borderRadius: 6, border: `1.5px solid ${perm === p ? color : 'var(--border)'}`, background: perm === p ? `${color}22` : 'transparent', color: perm === p ? color : 'var(--text-muted)', fontSize: 11, fontWeight: 600, cursor: 'pointer', transition: 'all 0.15s' }}>
                                {label}
                              </button>
                            ))}
                          </div>
                        </div>
                      )
                    })}
                  </div>
                </div>
              )}
            </div>
            <div style={{ display: 'flex', gap: 10, marginTop: 24 }}>
              <button className="btn btn-primary" onClick={handleSave} disabled={saving} style={{ flex: 1 }}>
                {saving ? <><div className="spinner" />Saving…</> : <><PhosphorIcon className="bi bi-check2" />{editingId ? 'Update' : 'Create'} Agent</>}
              </button>
              <button className="btn btn-secondary" onClick={() => setShowForm(false)}>Cancel</button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
