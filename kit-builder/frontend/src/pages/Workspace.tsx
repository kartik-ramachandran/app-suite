import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useLayoutEffect, useMemo, useRef, type ReactNode } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, toStatus, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import api from '../services/api'
import '../App.css'

// ── Types ─────────────────────────────────────────────────────────────────────

interface Role { id: string; title: string; description: string; scope: string }
interface TaskItem { roleTitle: string; task: string; rationale: string }
interface AgentOutput { roleTitle: string; output: string; status: 'waiting' | 'running' | 'done' | 'error' }

type Stage = 'setup' | 'decomposing' | 'review-tasks' | 'executing' | 'review-outputs' | 'synthesizing' | 'done'

// ── Org templates ─────────────────────────────────────────────────────────────

const ORG_TEMPLATES: { label: string; icon: string; roles: Omit<Role, 'id'>[] }[] = [
  {
    label: 'Startup',
    icon: 'bi-rocket-takeoff',
    roles: [
      { title: 'CEO', description: 'Visionary leader who sets strategy, direction, and culture', scope: 'Overall vision, stakeholder alignment, go-to-market strategy' },
      { title: 'CTO', description: 'Technical architect responsible for all engineering decisions', scope: 'Technology stack, scalability, security, engineering roadmap' },
      { title: 'Head of Product', description: 'Owns the product vision and roadmap, prioritises user value', scope: 'Feature prioritisation, user research, product-market fit' },
      { title: 'Head of Marketing', description: 'Drives growth, brand awareness, and user acquisition', scope: 'Go-to-market, channels, messaging, content, paid & organic growth' },
    ],
  },
  {
    label: 'Product Team',
    icon: 'bi-kanban',
    roles: [
      { title: 'Product Manager', description: 'Defines requirements and owns the product roadmap', scope: 'User stories, prioritisation, stakeholder management, success metrics' },
      { title: 'Engineer', description: 'Designs and builds the technical solution', scope: 'Architecture, implementation, code quality, technical tradeoffs' },
      { title: 'Designer', description: 'Creates the user experience and visual design', scope: 'User flows, wireframes, prototypes, design system, accessibility' },
      { title: 'QA Engineer', description: 'Ensures quality and prevents regressions', scope: 'Test strategy, edge cases, acceptance criteria, bug reporting' },
    ],
  },
  {
    label: 'Content Team',
    icon: 'bi-pencil-square',
    roles: [
      { title: 'Editor', description: 'Sets editorial direction and maintains content quality', scope: 'Content strategy, editorial calendar, quality standards, tone of voice' },
      { title: 'Writer', description: 'Produces long-form content that educates and engages', scope: 'Articles, guides, whitepapers, case studies' },
      { title: 'SEO Specialist', description: 'Optimises for search visibility and organic traffic', scope: 'Keyword research, on-page SEO, link strategy, ranking analysis' },
      { title: 'Social Media Manager', description: 'Grows and engages the brand community', scope: 'Social strategy, content creation, community engagement, analytics' },
    ],
  },
  {
    label: 'Custom',
    icon: 'bi-sliders',
    roles: [],
  },
]

const ROLE_COLORS = [
  '#833ab4', '#22c55e', '#f59e0b', '#ec4899',
  '#fa7e1e', '#a855f7', '#f97316', '#fa7e1e',
]

// ── Markdown renderer (reused from Swarm) ─────────────────────────────────────

function renderMd(text: string): ReactNode {
  const nodes: ReactNode[] = []
  let listItems: string[] = []
  const inlineMd = (s: string) =>
    s.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
     .replace(/\*(.+?)\*/g, '<em>$1</em>')
     .replace(/`(.+?)`/g, '<code style="background:rgba(0,0,0,0.06);padding:1px 4px;border-radius:3px;font-size:0.88em">$1</code>')

  const flush = (key: string) => {
    if (!listItems.length) return
    nodes.push(<ul key={key} style={{ paddingLeft: 18, margin: '4px 0' }}>
      {listItems.map((li, i) => <li key={i} style={{ fontSize: 13, lineHeight: 1.65, color: 'var(--text-primary)' }} dangerouslySetInnerHTML={{ __html: inlineMd(li) }} />)}
    </ul>)
    listItems = []
  }

  text.split('\n').forEach((line, i) => {
    const k = String(i)
    if (/^#{1,3} (.+)/.test(line)) { flush(k + 'l'); const lvl = line.match(/^(#{1,3})/)?.[1].length ?? 1; const sz = [16, 15, 14][lvl - 1]; nodes.push(<div key={k} style={{ fontSize: sz, fontWeight: 700, margin: '12px 0 4px', color: 'var(--text-primary)' }} dangerouslySetInnerHTML={{ __html: inlineMd(line.replace(/^#+\s/, '')) }} />) }
    else if (/^[-*] (.+)/.test(line)) { listItems.push(line.replace(/^[-*] /, '')) }
    else if (line.trim() === '') { flush(k + 'l'); nodes.push(<div key={k} style={{ height: 5 }} />) }
    else { flush(k + 'l'); nodes.push(<p key={k} style={{ fontSize: 13, lineHeight: 1.7, margin: 0, color: 'var(--text-primary)' }} dangerouslySetInnerHTML={{ __html: inlineMd(line) }} />) }
  })
  flush('end')
  return <>{nodes}</>
}

// ── Main component ─────────────────────────────────────────────────────────────

export default function Workspace() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('workspace')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Workspace' }])
    return () => setBreadcrumbs([])
  }, [])

  // Setup state
  const [selectedTemplate, setSelectedTemplate] = useState(0)
  const [roles, setRoles] = useState<Role[]>(ORG_TEMPLATES[0].roles.map((r, i) => ({ ...r, id: String(i) })))
  const [goal, setGoal] = useState('')

  // Flow state
  const [stage, setStage] = useState<Stage>('setup')
  const [tasks, setTasks] = useState<TaskItem[]>([])
  const [agentOutputs, setAgentOutputs] = useState<AgentOutput[]>([])
  const [synthesis, setSynthesis] = useState('')
  const [error, setError] = useState('')
  const [activeTab, setActiveTab] = useState<string>('synthesis')
  const [copied, setCopied] = useState(false)
  const abortRef = useRef<AbortController | null>(null)

  // ── Role management ────────────────────────────────────────────────────────

  const applyTemplate = (idx: number) => {
    setSelectedTemplate(idx)
    setRoles(ORG_TEMPLATES[idx].roles.map((r, i) => ({ ...r, id: String(Date.now() + i) })))
  }

  const addRole = () => {
    setRoles(prev => [...prev, { id: String(Date.now()), title: '', description: '', scope: '' }])
  }

  const updateRole = (id: string, field: keyof Role, value: string) => {
    setRoles(prev => prev.map(r => r.id === id ? { ...r, [field]: value } : r))
  }

  const removeRole = (id: string) => {
    setRoles(prev => prev.filter(r => r.id !== id))
  }

  // ── Stage 1: Decompose ────────────────────────────────────────────────────

  const handleDecompose = async () => {
    if (!goal.trim() || roles.filter(r => r.title.trim()).length === 0) return
    setError('')
    setStage('decomposing')
    try {
      const res = await api.post<{ tasks: TaskItem[] }>('/company/decompose', {
        goal,
        roles: roles.filter(r => r.title.trim()).map(r => ({ title: r.title, description: r.description, scope: r.scope }))
      })
      setTasks(res.data.tasks)
      setStage('review-tasks')
    } catch {
      setError('Failed to decompose goal. Please try again.')
      setStage('setup')
    }
  }

  // ── Stage 2: Execute (SSE) ────────────────────────────────────────────────

  const handleExecute = async () => {
    setError('')
    setStage('executing')
    const initialOutputs = tasks.map(t => ({ roleTitle: t.roleTitle, output: '', status: 'waiting' as const }))
    setAgentOutputs(initialOutputs)

    abortRef.current?.abort()
    const ctrl = new AbortController()
    abortRef.current = ctrl

    const authToken = localStorage.getItem('authToken')
    const baseUrl = import.meta.env.VITE_API_BASE_URL || '/api'

    try {
      const resp = await fetch(`${baseUrl}/company/execute`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...(authToken ? { Authorization: `Bearer ${authToken}` } : {}) },
        body: JSON.stringify({
          goal,
          roles: roles.filter(r => r.title.trim()).map(r => ({ title: r.title, description: r.description, scope: r.scope })),
          tasks: tasks.map(t => ({ roleTitle: t.roleTitle, task: t.task, rationale: t.rationale }))
        }),
        signal: ctrl.signal
      })

      if (!resp.ok) { setError('Execution failed to start'); setStage('review-tasks'); return }

      const reader = resp.body!.getReader()
      const decoder = new TextDecoder()
      let buf = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buf += decoder.decode(value, { stream: true })
        const lines = buf.split('\n')
        buf = lines.pop() || ''
        for (const line of lines) {
          if (!line.startsWith('data: ')) continue
          try {
            const data = JSON.parse(line.slice(6))
            if (data.type === 'done') { setStage('review-outputs'); return }
            if (data.type === 'error') { setError(data.message); setStage('review-tasks'); return }
            if (data.type === 'agent_start') {
              setAgentOutputs(prev => prev.map(o => o.roleTitle === data.roleTitle ? { ...o, status: 'running' } : o))
            }
            if (data.type === 'agent_done') {
              setAgentOutputs(prev => prev.map(o => o.roleTitle === data.roleTitle ? { ...o, output: data.output, status: 'done' } : o))
            }
            if (data.type === 'agent_error') {
              setAgentOutputs(prev => prev.map(o => o.roleTitle === data.roleTitle ? { ...o, status: 'error' } : o))
            }
          } catch { /* skip malformed event */ }
        }
      }
    } catch (e) {
      if ((e as Error).name !== 'AbortError') { setError('Connection failed. Is the backend running?'); setStage('review-tasks') }
    }
  }

  // ── Gamefield ─────────────────────────────────────────────────

  // Each role gets a desk; the chief of staff takes the corner office and does
  // the decomposition and the final synthesis.
  const field = useMemo<FieldModel>(() => {
    const named = roles.filter(r => r.title.trim())
    const outByRole = new Map(agentOutputs.map(o => [o.roleTitle, o]))
    const taskByRole = new Map(tasks.map(t => [t.roleTitle, t]))
    const titles = agentOutputs.length ? agentOutputs.map(o => o.roleTitle) : named.map(r => r.title)

    const workers = titles.map((title, i) => {
      const out = outByRole.get(title)
      const task = taskByRole.get(title)
      const role = named.find(r => r.title === title)
      return {
        id: `role-${title}`,
        name: title,
        role: role?.scope ? bubble(role.scope, 34) : task ? 'assigned' : 'awaiting a brief',
        status: out ? toStatus(out.status) : 'idle' as const,
        detail: out?.status === 'running'
          ? bubble(task?.task) || 'Working the brief…'
          : out?.status === 'done' ? bubble(out.output) : bubble(task?.task),
        output: out?.output || (task ? `TASK
${task.task}

WHY
${task.rationale}` : undefined),
        accent: ROLE_COLORS[i % ROLE_COLORS.length],
      }
    })

    const chiefStatus =
      stage === 'decomposing' || stage === 'synthesizing' ? 'working' as const
      : stage === 'review-tasks' || stage === 'review-outputs' ? 'blocked' as const
      : stage === 'done' ? 'done' as const
      : 'idle' as const

    const chiefDetail =
      stage === 'decomposing' ? 'Splitting the goal into briefs…'
      : stage === 'review-tasks' ? 'Briefs drafted — waiting on your sign-off.'
      : stage === 'executing' ? 'Watching the floor.'
      : stage === 'review-outputs' ? 'Everyone reported back. Say the word.'
      : stage === 'synthesizing' ? 'Pulling it all together…'
      : stage === 'done' ? bubble(synthesis)
      : undefined

    return {
      title: 'Workspace',
      subtitle: goal ? bubble(goal, 70) : 'Set a goal and staff the room.',
      running: stage === 'decomposing' || stage === 'executing' || stage === 'synthesizing',
      agents: [
        ...workers,
        {
          id: 'chief',
          name: 'Chief of Staff',
          role: 'decompose & synthesise',
          status: chiefStatus,
          detail: chiefDetail,
          output: synthesis || undefined,
          boss: true,
        },
      ],
      messages: agentOutputs
        .filter(o => o.status === 'done')
        .map(o => ({ id: `hand-${o.roleTitle}`, from: `role-${o.roleTitle}`, to: 'chief' })),
      events: [
        ...(tasks.length ? [{ id: 'brief', text: `${tasks.length} briefs written.`, tone: 'info' as const }] : []),
        ...agentOutputs.filter(o => o.status === 'done').map(o => ({ id: `ev-${o.roleTitle}`, text: `${o.roleTitle} filed their report.`, tone: 'good' as const })),
        ...agentOutputs.filter(o => o.status === 'error').map(o => ({ id: `er-${o.roleTitle}`, text: `${o.roleTitle} hit a problem.`, tone: 'bad' as const })),
        ...(synthesis ? [{ id: 'synth', text: 'Synthesis signed off by the chief of staff.', tone: 'good' as const }] : []),
        ...(error ? [{ id: 'err', text: error, tone: 'bad' as const }] : []),
      ],
    }
  }, [roles, tasks, agentOutputs, stage, synthesis, goal, error])

  // ── Stage 3: Synthesize ───────────────────────────────────────────────────

  const handleSynthesize = async () => {
    setError('')
    setStage('synthesizing')
    try {
      const res = await api.post<{ synthesis: string }>('/company/synthesize', {
        goal,
        outputs: agentOutputs.filter(o => o.status === 'done').map(o => ({ roleTitle: o.roleTitle, output: o.output }))
      })
      setSynthesis(res.data.synthesis)
      setActiveTab('synthesis')
      setStage('done')
    } catch {
      setError('Synthesis failed. Please try again.')
      setStage('review-outputs')
    }
  }

  // ── Reset ─────────────────────────────────────────────────────────────────

  const handleReset = () => {
    abortRef.current?.abort()
    setStage('setup')
    setTasks([])
    setAgentOutputs([])
    setSynthesis('')
    setError('')
    setGoal('')
  }

  const copyText = async (text: string) => {
    await navigator.clipboard.writeText(text)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  const validRoles = roles.filter(r => r.title.trim())
  const doneOutputs = agentOutputs.filter(o => o.status === 'done')

  // ── Render ────────────────────────────────────────────────────────────────

  return (
    <div style={{ padding: '24px', display: 'flex', flexDirection: 'column', gap: 20, maxWidth: 1100, margin: '0 auto' }}>

      {/* Header */}
      <div>
        <h1 style={{ fontSize: 22, fontWeight: 700, margin: '0 0 4px' }}>
          <PhosphorIcon className="bi bi-people" style={{ marginRight: 8, color: 'var(--primary)' }} />
          Workspace
        </h1>
        <p style={{ color: 'var(--text-secondary)', fontSize: 13, margin: 0 }}>
          Assemble a team of roles, define a goal, and let each member contribute their expertise — then synthesise a unified strategy.
        </p>
      </div>

      {error && (
        <div style={{ padding: '10px 14px', background: '#ef444410', border: '1px solid #ef444430', borderRadius: 8, color: '#ef4444', fontSize: 13, display: 'flex', alignItems: 'center', gap: 8 }}>
          <PhosphorIcon className="bi bi-exclamation-circle" />
          {error}
          <button onClick={() => setError('')} style={{ marginLeft: 'auto', background: 'none', border: 'none', cursor: 'pointer', color: '#ef4444', display: 'flex' }}>
            <PhosphorIcon className="bi bi-x" />
          </button>
        </div>
      )}

      {/* ── SETUP ── */}
      {(stage === 'setup' || stage === 'decomposing') && (
        <>
          {/* Template picker */}
          <div className="card" style={{ padding: '18px 20px' }}>
            <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, display: 'block', marginBottom: 10 }}>ORG TEMPLATE</label>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              {ORG_TEMPLATES.map((t, idx) => (
                <button
                  key={t.label}
                  className={`btn ${selectedTemplate === idx ? 'btn-primary' : 'btn-secondary'}`}
                  style={{ fontSize: 12, height: 32 }}
                  onClick={() => applyTemplate(idx)}
                >
                  <PhosphorIcon className={`bi ${t.icon}`} />
                  {t.label}
                </button>
              ))}
            </div>
          </div>

          {/* Roles */}
          <div className="card" style={{ padding: '18px 20px' }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 12 }}>
              <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1 }}>TEAM ROLES</label>
              <button className="btn btn-secondary" style={{ fontSize: 12, height: 30 }} onClick={addRole}>
                <PhosphorIcon className="bi bi-plus" />Add Role
              </button>
            </div>

            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))', gap: 12 }}>
              {roles.map((role, idx) => (
                <div key={role.id} style={{
                  border: '1.5px solid var(--border)', borderRadius: 10, padding: '12px 14px',
                  borderLeft: `3px solid ${ROLE_COLORS[idx % ROLE_COLORS.length]}`
                }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
                    <div style={{
                      width: 28, height: 28, borderRadius: 14, flexShrink: 0,
                      background: ROLE_COLORS[idx % ROLE_COLORS.length] + '20',
                      color: ROLE_COLORS[idx % ROLE_COLORS.length],
                      display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: 12, fontWeight: 700
                    }}>
                      {role.title ? role.title[0].toUpperCase() : '?'}
                    </div>
                    <input
                      className="form-input"
                      style={{ flex: 1, fontSize: 13, fontWeight: 600, height: 30, padding: '4px 8px' }}
                      placeholder="Role title (e.g. CEO)"
                      value={role.title}
                      onChange={e => updateRole(role.id, 'title', e.target.value)}
                    />
                    <button
                      onClick={() => removeRole(role.id)}
                      style={{ background: 'none', border: 'none', cursor: 'pointer', color: 'var(--text-muted)', display: 'flex', padding: 2 }}
                      title="Remove role"
                    >
                      <PhosphorIcon className="bi bi-x-lg" style={{ fontSize: 13 }} />
                    </button>
                  </div>
                  <input
                    className="form-input"
                    style={{ fontSize: 12, height: 28, padding: '3px 8px', marginBottom: 6, width: '100%', boxSizing: 'border-box' }}
                    placeholder="Description (who they are)"
                    value={role.description}
                    onChange={e => updateRole(role.id, 'description', e.target.value)}
                  />
                  <input
                    className="form-input"
                    style={{ fontSize: 12, height: 28, padding: '3px 8px', width: '100%', boxSizing: 'border-box' }}
                    placeholder="Scope (what they own)"
                    value={role.scope}
                    onChange={e => updateRole(role.id, 'scope', e.target.value)}
                  />
                </div>
              ))}
            </div>
          </div>

          {/* Goal + Run */}
          <div className="card" style={{ padding: '18px 20px' }}>
            <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, display: 'block', marginBottom: 8 }}>COMPANY GOAL</label>
            <textarea
              className="form-textarea"
              style={{ minHeight: 90, fontSize: 15, lineHeight: 1.6, marginBottom: 12 }}
              placeholder='e.g. "Launch a B2B SaaS product for construction project management and reach £50k MRR in 12 months"'
              value={goal}
              onChange={e => setGoal(e.target.value)}
              disabled={stage === 'decomposing'}
            />
            <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
              <button
                className="btn btn-primary"
                style={{ height: 36, fontSize: 13 }}
                onClick={handleDecompose}
                disabled={stage === 'decomposing' || !goal.trim() || validRoles.length === 0}
              >
                {stage === 'decomposing'
                  ? <><div className="spinner" />Assigning tasks…</>
                  : <><PhosphorIcon className="bi bi-diagram-3" />Build Task Plan</>
                }
              </button>
            </div>
          </div>
        </>
      )}

      {/* ── REVIEW TASKS ── */}
      {stage === 'review-tasks' && (
        <div className="card" style={{ padding: '20px 24px' }}>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 16 }}>
            <div>
              <h3 style={{ fontSize: 15, fontWeight: 600, margin: '0 0 2px' }}>
                <PhosphorIcon className="bi bi-clipboard-check" style={{ color: 'var(--primary)', marginRight: 6 }} />
                Review Task Plan
              </h3>
              <p style={{ fontSize: 12, color: 'var(--text-secondary)', margin: 0 }}>Each role has been assigned a specific task. Edit if needed, then execute.</p>
            </div>
            <div style={{ display: 'flex', gap: 8 }}>
              <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => setStage('setup')}>
                <PhosphorIcon className="bi bi-arrow-left" />Back
              </button>
              <button className="btn btn-primary" style={{ fontSize: 13 }} onClick={handleExecute}>
                <PhosphorIcon className="bi bi-play-fill" />Execute Agents
              </button>
            </div>
          </div>

          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {tasks.map((t, idx) => {
              const roleIdx = validRoles.findIndex(r => r.title === t.roleTitle)
              const color = ROLE_COLORS[(roleIdx >= 0 ? roleIdx : idx) % ROLE_COLORS.length]
              return (
                <div key={t.roleTitle} style={{ display: 'flex', gap: 12, padding: '12px 14px', border: '1.5px solid var(--border)', borderLeft: `3px solid ${color}`, borderRadius: 8 }}>
                  <div style={{ width: 70, flexShrink: 0 }}>
                    <span style={{ fontSize: 11, fontWeight: 700, color, textTransform: 'uppercase', letterSpacing: 0.5 }}>{t.roleTitle}</span>
                  </div>
                  <div style={{ flex: 1 }}>
                    <textarea
                      className="form-textarea"
                      style={{ minHeight: 56, fontSize: 13, lineHeight: 1.55, padding: '6px 10px', marginBottom: 4 }}
                      value={t.task}
                      onChange={e => setTasks(prev => prev.map(x => x.roleTitle === t.roleTitle ? { ...x, task: e.target.value } : x))}
                    />
                    <p style={{ fontSize: 11, color: 'var(--text-muted)', margin: 0, fontStyle: 'italic' }}>{t.rationale}</p>
                  </div>
                </div>
              )
            })}
          </div>
        </div>
      )}

      {stage !== 'setup' && (
        <AgentStage
          {...stageProps}
          model={field}
          actions={
            stage === 'review-tasks' ? [{ id: 'exec', label: 'Send them to work', icon: 'bi-play-fill', tone: 'primary', onClick: handleExecute }]
            : stage === 'review-outputs' ? [{ id: 'synth', label: 'Synthesise', icon: 'bi-stars', tone: 'primary', onClick: handleSynthesize }]
            : stage === 'done' ? [{ id: 'reset', label: 'Start over', icon: 'bi-arrow-counterclockwise', onClick: handleReset }]
            : []
          }
        />
      )}

      {/* ── EXECUTING ── */}
      {stage === 'executing' && (
        <div className="card" style={{ padding: '20px 24px' }}>
          <h3 style={{ fontSize: 15, fontWeight: 600, margin: '0 0 16px' }}>
            <div className="spinner" style={{ display: 'inline-block', marginRight: 8, width: 14, height: 14, verticalAlign: 'middle' }} />
            Running {agentOutputs.length} agents…
          </h3>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {agentOutputs.map((o, idx) => {
              const color = ROLE_COLORS[idx % ROLE_COLORS.length]
              const statusIcon = o.status === 'done' ? 'bi-check-circle-fill' : o.status === 'running' ? 'bi-arrow-repeat' : o.status === 'error' ? 'bi-x-circle-fill' : 'bi-hourglass'
              const statusColor = o.status === 'done' ? '#22c55e' : o.status === 'running' ? 'var(--primary)' : o.status === 'error' ? '#ef4444' : 'var(--text-muted)'
              return (
                <div key={o.roleTitle} style={{ display: 'flex', alignItems: 'center', gap: 12, padding: '10px 14px', border: `1.5px solid ${o.status === 'running' ? 'var(--primary)' : 'var(--border)'}`, borderLeft: `3px solid ${color}`, borderRadius: 8, transition: 'border-color 0.2s' }}>
                  <span style={{ width: 80, fontSize: 12, fontWeight: 700, color, textTransform: 'uppercase', letterSpacing: 0.4, flexShrink: 0 }}>{o.roleTitle}</span>
                  <div style={{ flex: 1, height: 4, background: 'var(--border)', borderRadius: 2, overflow: 'hidden' }}>
                    <div style={{ height: '100%', background: color, borderRadius: 2, width: o.status === 'done' ? '100%' : o.status === 'running' ? '60%' : '0%', transition: 'width 0.6s ease' }} />
                  </div>
                  <PhosphorIcon className={`bi ${statusIcon}`} style={{ color: statusColor, fontSize: 15, flexShrink: 0, animation: o.status === 'running' ? 'spin 1s linear infinite' : 'none' }} />
                  <span style={{ fontSize: 11, color: statusColor, width: 54, flexShrink: 0, textAlign: 'right', fontWeight: 500 }}>
                    {o.status === 'done' ? 'Done' : o.status === 'running' ? 'Running' : o.status === 'error' ? 'Error' : 'Waiting'}
                  </span>
                </div>
              )
            })}
          </div>
        </div>
      )}

      {/* ── REVIEW OUTPUTS ── */}
      {stage === 'review-outputs' && (
        <>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
            <div>
              <h3 style={{ fontSize: 15, fontWeight: 600, margin: '0 0 2px' }}>Agent Outputs</h3>
              <p style={{ fontSize: 12, color: 'var(--text-secondary)', margin: 0 }}>
                {doneOutputs.length} of {agentOutputs.length} agents completed. Review, then synthesise into a strategy.
              </p>
            </div>
            <div style={{ display: 'flex', gap: 8 }}>
              <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => setStage('review-tasks')}>
                <PhosphorIcon className="bi bi-arrow-left" />Back
              </button>
              <button className="btn btn-primary" style={{ fontSize: 13 }} onClick={handleSynthesize} disabled={doneOutputs.length === 0}>
                <PhosphorIcon className="bi bi-stars" />Synthesise Strategy
              </button>
            </div>
          </div>

          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(380px, 1fr))', gap: 14 }}>
            {agentOutputs.map((o, idx) => {
              const color = ROLE_COLORS[idx % ROLE_COLORS.length]
              return (
                <div key={o.roleTitle} className="card" style={{ padding: '16px 18px', borderLeft: `3px solid ${color}` }}>
                  <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 10 }}>
                    <span style={{ fontSize: 12, fontWeight: 700, color, textTransform: 'uppercase', letterSpacing: 0.5 }}>{o.roleTitle}</span>
                    {o.status === 'error' && <span style={{ fontSize: 11, color: '#ef4444' }}>Failed</span>}
                  </div>
                  {o.status === 'done'
                    ? <div style={{ fontSize: 13, lineHeight: 1.6, maxHeight: 200, overflow: 'auto' }}>{renderMd(o.output)}</div>
                    : <p style={{ color: 'var(--text-muted)', fontSize: 13, fontStyle: 'italic' }}>No output</p>
                  }
                </div>
              )
            })}
          </div>
        </>
      )}

      {/* ── SYNTHESIZING ── */}
      {stage === 'synthesizing' && (
        <div className="card" style={{ padding: 32, textAlign: 'center' }}>
          <div className="spinner" style={{ width: 28, height: 28, margin: '0 auto 12px', borderWidth: 3 }} />
          <p style={{ fontSize: 14, color: 'var(--text-secondary)', margin: 0 }}>Synthesising your team's work into a unified strategy…</p>
        </div>
      )}

      {/* ── DONE ── */}
      {stage === 'done' && (
        <>
          {/* Tab bar */}
          <div style={{ display: 'flex', gap: 4, borderBottom: '1px solid var(--border)', flexWrap: 'wrap' }}>
            <button
              onClick={() => setActiveTab('synthesis')}
              style={{ padding: '8px 14px', fontSize: 13, fontWeight: activeTab === 'synthesis' ? 700 : 400, color: activeTab === 'synthesis' ? 'var(--primary)' : 'var(--text-secondary)', background: 'none', border: 'none', cursor: 'pointer', borderBottom: activeTab === 'synthesis' ? '2px solid var(--primary)' : '2px solid transparent', marginBottom: -1 }}
            >
              <PhosphorIcon className="bi bi-stars" style={{ marginRight: 5 }} />Strategy
            </button>
            {agentOutputs.filter(o => o.status === 'done').map((o, idx) => (
              <button
                key={o.roleTitle}
                onClick={() => setActiveTab(o.roleTitle)}
                style={{ padding: '8px 14px', fontSize: 13, fontWeight: activeTab === o.roleTitle ? 700 : 400, color: activeTab === o.roleTitle ? ROLE_COLORS[idx % ROLE_COLORS.length] : 'var(--text-secondary)', background: 'none', border: 'none', cursor: 'pointer', borderBottom: activeTab === o.roleTitle ? `2px solid ${ROLE_COLORS[idx % ROLE_COLORS.length]}` : '2px solid transparent', marginBottom: -1 }}
              >
                {o.roleTitle}
              </button>
            ))}
          </div>

          {/* Tab content */}
          <div className="card" style={{ padding: '20px 24px' }}>
            {activeTab === 'synthesis' ? (
              <>
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 14 }}>
                  <div>
                    <h3 style={{ fontSize: 15, fontWeight: 600, margin: '0 0 2px' }}>
                      <PhosphorIcon className="bi bi-stars" style={{ color: 'var(--primary)', marginRight: 6 }} />Unified Strategy
                    </h3>
                    <p style={{ fontSize: 12, color: 'var(--text-secondary)', margin: 0 }}>
                      Synthesised from {doneOutputs.length} agent{doneOutputs.length !== 1 ? 's' : ''}
                    </p>
                  </div>
                  <div style={{ display: 'flex', gap: 8 }}>
                    <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => copyText(synthesis)}>
                      <PhosphorIcon className={`bi ${copied ? 'bi-check2' : 'bi-clipboard'}`} />{copied ? 'Copied' : 'Copy'}
                    </button>
                    <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={handleReset}>
                      <PhosphorIcon className="bi bi-arrow-counterclockwise" />New Run
                    </button>
                  </div>
                </div>
                <div style={{ background: 'var(--bg-secondary)', padding: '16px 20px', borderRadius: 8 }}>
                  {renderMd(synthesis)}
                </div>
              </>
            ) : (
              (() => {
                const o = agentOutputs.find(x => x.roleTitle === activeTab)
                const idx = agentOutputs.findIndex(x => x.roleTitle === activeTab)
                const color = ROLE_COLORS[idx % ROLE_COLORS.length]
                if (!o) return null
                return (
                  <>
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 14 }}>
                      <h3 style={{ fontSize: 15, fontWeight: 600, margin: 0, color }}>{o.roleTitle}</h3>
                      <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => copyText(o.output)}>
                        <PhosphorIcon className={`bi ${copied ? 'bi-check2' : 'bi-clipboard'}`} />{copied ? 'Copied' : 'Copy'}
                      </button>
                    </div>
                    <div style={{ background: 'var(--bg-secondary)', padding: '16px 20px', borderRadius: 8 }}>
                      {renderMd(o.output)}
                    </div>
                  </>
                )
              })()
            )}
          </div>

          {/* Goal reminder */}
          <div style={{ padding: '10px 14px', background: 'var(--bg-secondary)', borderRadius: 8, fontSize: 12, color: 'var(--text-muted)' }}>
            <PhosphorIcon className="bi bi-bullseye" style={{ marginRight: 6 }} />
            <strong>Goal:</strong> {goal}
          </div>
        </>
      )}
    </div>
  )
}
