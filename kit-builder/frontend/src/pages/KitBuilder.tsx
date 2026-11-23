import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useLayoutEffect, useMemo } from 'react'
import { Link } from 'react-router-dom'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import api from '../services/api'
import { createAgent } from '../services/api'
import '../App.css'

// ── Types ─────────────────────────────────────────────────────────────────────

interface KitPersona {
  name: string; description: string; systemPrompt: string
  expertise: string; tone: string; category: string
}
interface KitAgent {
  name: string; description: string; instructions: string; enabledTools: string[]
}
interface KitSkill {
  name: string; description: string; instruction: string; category: string; tags: string[]
}
interface Kit { personas: KitPersona[]; agents: KitAgent[]; skills: KitSkill[] }

interface CreationSummary { personas: number; agents: number; skills: number }

// ── Constants ─────────────────────────────────────────────────────────────────

const EXAMPLES = [
  'A customer support team for a SaaS product',
  'A content marketing operation for a B2B startup',
  'A research and analysis team for investment decisions',
  'A software engineering team building a mobile app',
  'An HR onboarding and recruitment workflow',
  'A legal document review and contract drafting operation',
]

const SKILL_CAT_ICON: Record<string, string> = {
  marketing: 'bi-megaphone', engineering: 'bi-code-slash', support: 'bi-headset',
  legal: 'bi-shield-check', finance: 'bi-bar-chart-line', hr: 'bi-people',
  product: 'bi-layers', sales: 'bi-graph-up-arrow', data: 'bi-database', education: 'bi-mortarboard',
}

const TOOL_ICON: Record<string, string> = {
  web_search: 'bi-search', http_request: 'bi-globe', code_interpreter: 'bi-terminal',
}

// ── Step indicator ────────────────────────────────────────────────────────────

const STEPS = [
  { n: 1, label: 'Intent' },
  { n: 2, label: 'Review Kit' },
  { n: 3, label: 'Created' },
]

function StepBar({ current }: { current: number }) {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 0, marginBottom: 24 }}>
      {STEPS.map((s, i) => {
        const done = current > s.n
        const active = current === s.n
        return (
          <div key={s.n} style={{ display: 'flex', alignItems: 'center', flex: i < STEPS.length - 1 ? 1 : 'none' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 7, flexShrink: 0 }}>
              <div style={{
                width: 28, height: 28, borderRadius: 14,
                background: done ? '#22c55e' : active ? 'var(--primary)' : 'var(--bg-secondary)',
                border: done ? 'none' : active ? 'none' : '1.5px solid var(--border)',
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                fontSize: 12, fontWeight: 700,
                color: done || active ? '#fff' : 'var(--text-muted)',
                transition: 'background 0.2s',
              }}>
                {done ? <PhosphorIcon className="bi bi-check" /> : s.n}
              </div>
              <span style={{
                fontSize: 12, fontWeight: active ? 700 : 400,
                color: active ? 'var(--text-primary)' : done ? '#22c55e' : 'var(--text-muted)',
              }}>
                {s.label}
              </span>
            </div>
            {i < STEPS.length - 1 && (
              <div style={{
                flex: 1, height: 2, margin: '0 12px',
                background: done ? '#22c55e' : 'var(--border)',
                transition: 'background 0.3s',
              }} />
            )}
          </div>
        )
      })}
    </div>
  )
}

// ── Download ─────────────────────────────────────────────────────────────────

function buildMarkdown(description: string, kit: Kit, selectedPersonas: Set<string>, selectedAgents: Set<string>, selectedSkills: Set<string>): string {
  const lines = [`# AI Workspace Kit\n\n> **Use case:** ${description}\n\n---\n`]

  const ps = kit.personas.filter(p => selectedPersonas.has(p.name))
  const as_ = kit.agents.filter(a => selectedAgents.has(a.name))
  const ss = kit.skills.filter(s => selectedSkills.has(s.name))

  if (ps.length) {
    lines.push(`## Personas\n`)
    ps.forEach(p => lines.push(`### ${p.name}\n**Category:** ${p.category} | **Tone:** ${p.tone}\n\n**Description:** ${p.description}\n\n**Expertise:** ${p.expertise}\n\n**System Prompt:**\n\`\`\`\n${p.systemPrompt}\n\`\`\`\n`))
  }
  if (as_.length) {
    lines.push(`## Agents\n`)
    as_.forEach(a => lines.push(`### ${a.name}\n**Description:** ${a.description}\n\n**Tools:** ${a.enabledTools.join(', ') || 'none'}\n\n**Instructions:**\n\`\`\`\n${a.instructions}\n\`\`\`\n`))
  }
  if (ss.length) {
    lines.push(`## Skills\n`)
    ss.forEach(s => lines.push(`### ${s.name}\n**Category:** ${s.category} | **Tags:** ${s.tags.join(', ')}\n\n**Description:** ${s.description}\n\n**Instruction:**\n\`\`\`\n${s.instruction}\n\`\`\`\n`))
  }
  return lines.join('\n')
}

function downloadMd(description: string, kit: Kit, selectedPersonas: Set<string>, selectedAgents: Set<string>, selectedSkills: Set<string>) {
  const blob = new Blob([buildMarkdown(description, kit, selectedPersonas, selectedAgents, selectedSkills)], { type: 'text/markdown' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a'); a.href = url; a.download = 'workspace-kit.md'; a.click()
  URL.revokeObjectURL(url)
}

// ── Component ─────────────────────────────────────────────────────────────────

export default function KitBuilder() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('kit-builder')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Kit Builder' }])
    return () => setBreadcrumbs([])
  }, [])

  const [step, setStep] = useState<1 | 2 | 3>(1)
  const [description, setDescription] = useState('')
  const [loading, setLoading] = useState(false)
  const [kit, setKit] = useState<Kit | null>(null)
  const [error, setError] = useState('')

  // selection (Step 2)
  const [selectedPersonas, setSelectedPersonas] = useState(new Set<string>())
  const [selectedAgents, setSelectedAgents] = useState(new Set<string>())
  const [selectedSkills, setSelectedSkills] = useState(new Set<string>())

  // creation (Step 3)
  const [creating, setCreating] = useState(false)
  const [summary, setSummary] = useState<CreationSummary | null>(null)

  // ── Step 1 → 2: Generate ────────────────────────────────────────────────────

  const handleGenerate = async () => {
    if (!description.trim()) return
    setLoading(true)
    setError('')
    setKit(null)
    try {
      const res = await api.post<Kit>('/workspace/generate', { description })
      const k = res.data
      setKit(k)
      // default: all selected
      setSelectedPersonas(new Set(k.personas.map(p => p.name)))
      setSelectedAgents(new Set(k.agents.map(a => a.name)))
      setSelectedSkills(new Set(k.skills.map(s => s.name)))
      setStep(2)
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Generation failed. Please try again.')
    } finally {
      setLoading(false)
    }
  }

  // ── Step 2 → 3: Create selected ────────────────────────────────────────────

  const handleCreate = async () => {
    if (!kit) return
    setCreating(true)
    setError('')

    const ps = kit.personas.filter(p => selectedPersonas.has(p.name))
    const as_ = kit.agents.filter(a => selectedAgents.has(a.name))
    const ss = kit.skills.filter(s => selectedSkills.has(s.name))

    let personaCount = 0, agentCount = 0, skillCount = 0

    await Promise.allSettled([
      ...ps.map(p =>
        api.post('/personas', { name: p.name, systemPrompt: p.systemPrompt, description: p.description, expertise: p.expertise, tone: p.tone, category: p.category })
          .then(() => personaCount++)
      ),
      ...as_.map(a =>
        createAgent({ name: a.name, description: a.description, instructions: a.instructions, model: 'gpt-4o', enabledTools: JSON.stringify(a.enabledTools), toolPermissions: '{}', inputSchemaJson: '{}', memoryConfigJson: '{}', guardrailsJson: '{}', outputSchemaJson: '{}', knowledgeSourcesJson: '{}', integrationActionsJson: '[]', modelRoutingJson: '{}', benchmarkConfigJson: '{}', governanceJson: '{}', fallbackBehavior: 'respond_with_error', escalationRules: '', isTemplate: false, isPublic: false, temperature: 0.7, maxIterations: 5 })
          .then(() => agentCount++)
      ),
      ...ss.map(s =>
        api.post('/skills', { name: s.name, instruction: s.instruction, category: s.category, description: s.description, tags: s.tags })
          .then(() => skillCount++)
      ),
    ])

    setSummary({ personas: personaCount, agents: agentCount, skills: skillCount })
    setCreating(false)
    setStep(3)
  }

  const toggle = (set: Set<string>, name: string, setter: (s: Set<string>) => void) => {
    const next = new Set(set)
    next.has(name) ? next.delete(name) : next.add(name)
    setter(next)
  }

  const selectedCount = selectedPersonas.size + selectedAgents.size + selectedSkills.size

  // ── Render ──────────────────────────────────────────────────────────────────

  // Gamefield: three build crews under an architect who reads your brief.
  const field = useMemo<FieldModel>(() => {
    const crews: { id: string; name: string; role: string; count: number; made?: number }[] = [
      { id: 'personas', name: 'Personas', role: 'voices and briefs', count: kit?.personas.length ?? 0, made: summary?.personas },
      { id: 'agents', name: 'Agents', role: 'the doers', count: kit?.agents.length ?? 0, made: summary?.agents },
      { id: 'skills', name: 'Skills', role: 'reusable know-how', count: kit?.skills.length ?? 0, made: summary?.skills },
    ]

    const desks = crews.map(c => ({
      id: c.id,
      name: c.name,
      role: c.made != null ? `${c.made} created` : c.count ? `${c.count} drafted` : c.role,
      status: (loading || creating ? 'working' : c.made != null ? 'done' : c.count ? 'blocked' : 'idle') as 'working' | 'done' | 'blocked' | 'idle',
      detail: loading ? 'Drafting the kit…'
        : creating ? 'Creating them in your workspace…'
        : c.count && c.made == null ? 'Drafted — pick what you want to keep.'
        : undefined,
    }))

    return {
      title: 'Kit Builder',
      subtitle: description ? bubble(description, 70) : 'Describe the workspace you want built.',
      running: loading || creating,
      agents: [
        ...desks,
        {
          id: 'architect',
          name: 'Architect',
          role: `step ${step} of 3`,
          status: (error ? 'failed' : loading || creating ? 'working' : summary ? 'done' : kit ? 'blocked' : 'idle') as 'failed' | 'working' | 'done' | 'blocked' | 'idle',
          detail: error ? bubble(error)
            : summary ? 'Kit installed in your workspace.'
            : kit ? 'Kit drafted — waiting on your picks.'
            : loading ? 'Reading the brief…'
            : undefined,
          boss: true,
        },
      ],
      messages: kit ? desks.map(d => ({ id: `brief-${d.id}`, from: 'architect', to: d.id })) : [],
      events: [
        ...(kit ? [{ id: 'drafted', text: `Drafted ${kit.personas.length} personas, ${kit.agents.length} agents, ${kit.skills.length} skills.`, tone: 'info' as const }] : []),
        ...(summary ? [{ id: 'made', text: `Created ${summary.personas + summary.agents + summary.skills} items in your workspace.`, tone: 'good' as const }] : []),
        ...(error ? [{ id: 'err', text: error, tone: 'bad' as const }] : []),
      ],
    }
  }, [kit, summary, loading, creating, error, step, description])

  return (
    <div style={{ padding: '24px', maxWidth: 900, margin: '0 auto', display: 'flex', flexDirection: 'column', gap: 0 }}>

      {/* Header */}
      <div style={{ marginBottom: 20 }}>
        <h1 style={{ fontSize: 22, fontWeight: 700, margin: '0 0 4px' }}>
          <PhosphorIcon className="bi bi-boxes" style={{ marginRight: 8, color: 'var(--primary)' }} />
          Kit Builder
        </h1>
        <p style={{ color: 'var(--text-secondary)', fontSize: 13, margin: 0 }}>
          Describe what you want to accomplish — AI suggests the right personas, agents, and skills, then creates them in your library.
        </p>
      </div>

      <StepBar current={step} />

      {error && (
        <div style={{ padding: '10px 14px', background: '#ef444410', border: '1px solid #ef444430', borderRadius: 8, color: '#ef4444', fontSize: 13, display: 'flex', alignItems: 'center', gap: 8, marginBottom: 16 }}>
          <PhosphorIcon className="bi bi-exclamation-circle" />{error}
          <button onClick={() => setError('')} style={{ marginLeft: 'auto', background: 'none', border: 'none', cursor: 'pointer', color: '#ef4444', display: 'flex' }}><PhosphorIcon className="bi bi-x" /></button>
        </div>
      )}

      <AgentStage {...stageProps} model={field} />

      {/* ── STEP 1: Intent ── */}
      {step === 1 && (
        <div className="card" style={{ padding: '24px 28px' }}>
          <div style={{ marginBottom: 16 }}>
            <div style={{ fontSize: 16, fontWeight: 700, marginBottom: 4 }}>What do you want to build?</div>
            <div style={{ fontSize: 13, color: 'var(--text-secondary)' }}>Describe your use case, team, or workflow in plain language.</div>
          </div>

          <textarea
            className="form-textarea"
            style={{ minHeight: 110, fontSize: 15, lineHeight: 1.65, marginBottom: 12 }}
            placeholder="e.g. A customer support team for a SaaS product that handles billing, onboarding, and technical issues…"
            value={description}
            onChange={e => setDescription(e.target.value)}
            disabled={loading}
            onKeyDown={e => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) handleGenerate() }}
            autoFocus
          />

          <div style={{ marginBottom: 18 }}>
            <div style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', letterSpacing: 0.8, marginBottom: 8 }}>EXAMPLES — click to fill</div>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
              {EXAMPLES.map(ex => (
                <button
                  key={ex}
                  onClick={() => setDescription(ex)}
                  style={{
                    background: 'var(--bg-secondary)', border: '1px solid var(--border)', borderRadius: 20,
                    padding: '4px 12px', fontSize: 12, cursor: 'pointer', color: 'var(--text-secondary)',
                  }}
                >
                  {ex}
                </button>
              ))}
            </div>
          </div>

          <div style={{ display: 'flex', justifyContent: 'flex-end' }}>
            <button
              className="btn btn-primary"
              style={{ height: 38, fontSize: 14, minWidth: 160 }}
              onClick={handleGenerate}
              disabled={loading || !description.trim()}
            >
              {loading
                ? <><div className="spinner" />Generating kit…</>
                : <><PhosphorIcon className="bi bi-stars" />Generate Kit<PhosphorIcon className="bi bi-arrow-right" style={{ marginLeft: 4 }} /></>
              }
            </button>
          </div>
        </div>
      )}

      {/* Loading overlay during generation */}
      {loading && (
        <div className="card" style={{ padding: 28, marginTop: 12, textAlign: 'center' }}>
          <div className="spinner" style={{ width: 24, height: 24, margin: '0 auto 12px', borderWidth: 3 }} />
          <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 4 }}>Building your workspace kit…</div>
          <div style={{ fontSize: 13, color: 'var(--text-secondary)' }}>AI is designing personas, agents, and skills tailored to your use case.</div>
        </div>
      )}

      {/* ── STEP 2: Review Kit ── */}
      {step === 2 && kit && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>

          {/* Intent recap */}
          <div style={{ padding: '10px 14px', background: 'var(--bg-secondary)', borderRadius: 8, fontSize: 13, color: 'var(--text-secondary)', display: 'flex', alignItems: 'center', gap: 8 }}>
            <PhosphorIcon className="bi bi-lightbulb" style={{ color: 'var(--primary)', flexShrink: 0 }} />
            <span><strong style={{ color: 'var(--text-primary)' }}>Intent:</strong> {description}</span>
            <button onClick={() => setStep(1)} style={{ marginLeft: 'auto', background: 'none', border: 'none', cursor: 'pointer', color: 'var(--primary)', fontSize: 12, flexShrink: 0, display: 'flex', alignItems: 'center', gap: 4 }}>
              <PhosphorIcon className="bi bi-pencil" />Edit
            </button>
          </div>

          {/* Instructions */}
          <div style={{ fontSize: 13, color: 'var(--text-secondary)' }}>
            <strong style={{ color: 'var(--text-primary)' }}>Review and select</strong> what to add to your library. Deselect anything you don't need, then click <strong>Create Selected</strong>.
          </div>

          {/* Personas */}
          {kit.personas.length > 0 && (
            <section>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
                <h3 style={{ fontSize: 12, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, textTransform: 'uppercase', margin: 0 }}>
                  <PhosphorIcon className="bi bi-person-badge" style={{ marginRight: 5 }} />Personas
                </h3>
                <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>{selectedPersonas.size} of {kit.personas.length} selected</span>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))', gap: 10 }}>
                {kit.personas.map(p => {
                  const selected = selectedPersonas.has(p.name)
                  return (
                    <div
                      key={p.name}
                      onClick={() => toggle(selectedPersonas, p.name, setSelectedPersonas)}
                      className="card"
                      style={{
                        padding: '12px 14px', cursor: 'pointer', userSelect: 'none',
                        border: selected ? '2px solid var(--primary)' : '1.5px solid var(--border)',
                        opacity: selected ? 1 : 0.55, transition: 'all 0.15s',
                      }}
                    >
                      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 8, marginBottom: 6 }}>
                        <div style={{
                          width: 20, height: 20, borderRadius: 4, flexShrink: 0, marginTop: 1,
                          background: selected ? 'var(--primary)' : 'var(--bg-secondary)',
                          border: selected ? 'none' : '1.5px solid var(--border)',
                          display: 'flex', alignItems: 'center', justifyContent: 'center',
                        }}>
                          {selected && <PhosphorIcon className="bi bi-check" style={{ color: '#fff', fontSize: 12 }} />}
                        </div>
                        <div style={{ flex: 1 }}>
                          <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 2 }}>{p.name}</div>
                          <div style={{ display: 'flex', gap: 4 }}>
                            <span style={{ fontSize: 10, background: 'var(--bg-secondary)', padding: '1px 6px', borderRadius: 8, color: 'var(--text-muted)' }}>{p.category}</span>
                            <span style={{ fontSize: 10, background: 'var(--bg-secondary)', padding: '1px 6px', borderRadius: 8, color: 'var(--text-muted)' }}>{p.tone}</span>
                          </div>
                        </div>
                      </div>
                      <p style={{ fontSize: 12, color: 'var(--text-secondary)', margin: '0 0 4px', lineHeight: 1.5 }}>{p.description}</p>
                      <div style={{ fontSize: 11, color: 'var(--text-muted)', fontStyle: 'italic' }}>
                        <PhosphorIcon className="bi bi-lightning-charge" style={{ marginRight: 3 }} />{p.expertise}
                      </div>
                      <details onClick={e => e.stopPropagation()} style={{ marginTop: 8 }}>
                        <summary style={{ fontSize: 11, color: 'var(--primary)', cursor: 'pointer' }}>View system prompt</summary>
                        <pre style={{ fontSize: 11, lineHeight: 1.5, margin: '6px 0 0', background: 'var(--bg-secondary)', padding: '8px 10px', borderRadius: 6, whiteSpace: 'pre-wrap', wordBreak: 'break-word', color: 'var(--text-primary)', maxHeight: 140, overflow: 'auto' }}>{p.systemPrompt}</pre>
                      </details>
                    </div>
                  )
                })}
              </div>
            </section>
          )}

          {/* Agents */}
          {kit.agents.length > 0 && (
            <section>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
                <h3 style={{ fontSize: 12, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, textTransform: 'uppercase', margin: 0 }}>
                  <PhosphorIcon className="bi bi-robot" style={{ marginRight: 5 }} />Agents
                </h3>
                <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>{selectedAgents.size} of {kit.agents.length} selected</span>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))', gap: 10 }}>
                {kit.agents.map(a => {
                  const selected = selectedAgents.has(a.name)
                  return (
                    <div
                      key={a.name}
                      onClick={() => toggle(selectedAgents, a.name, setSelectedAgents)}
                      className="card"
                      style={{
                        padding: '12px 14px', cursor: 'pointer', userSelect: 'none',
                        border: selected ? '2px solid var(--primary)' : '1.5px solid var(--border)',
                        opacity: selected ? 1 : 0.55, transition: 'all 0.15s',
                      }}
                    >
                      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 8, marginBottom: 6 }}>
                        <div style={{
                          width: 20, height: 20, borderRadius: 4, flexShrink: 0, marginTop: 1,
                          background: selected ? 'var(--primary)' : 'var(--bg-secondary)',
                          border: selected ? 'none' : '1.5px solid var(--border)',
                          display: 'flex', alignItems: 'center', justifyContent: 'center',
                        }}>
                          {selected && <PhosphorIcon className="bi bi-check" style={{ color: '#fff', fontSize: 12 }} />}
                        </div>
                        <div style={{ fontSize: 13, fontWeight: 600 }}>{a.name}</div>
                      </div>
                      <p style={{ fontSize: 12, color: 'var(--text-secondary)', margin: '0 0 8px', lineHeight: 1.5 }}>{a.description}</p>
                      {a.enabledTools.length > 0 && (
                        <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap', marginBottom: 8 }}>
                          {a.enabledTools.map(t => (
                            <span key={t} style={{ fontSize: 10, background: 'rgba(99,102,241,0.1)', color: 'var(--primary)', padding: '2px 6px', borderRadius: 8, fontWeight: 600 }}>
                              <PhosphorIcon className={`bi ${TOOL_ICON[t] ?? 'bi-gear'}`} style={{ marginRight: 3 }} />{t}
                            </span>
                          ))}
                        </div>
                      )}
                      <details onClick={e => e.stopPropagation()}>
                        <summary style={{ fontSize: 11, color: 'var(--primary)', cursor: 'pointer' }}>View instructions</summary>
                        <pre style={{ fontSize: 11, lineHeight: 1.5, margin: '6px 0 0', background: 'var(--bg-secondary)', padding: '8px 10px', borderRadius: 6, whiteSpace: 'pre-wrap', wordBreak: 'break-word', color: 'var(--text-primary)', maxHeight: 140, overflow: 'auto' }}>{a.instructions}</pre>
                      </details>
                    </div>
                  )
                })}
              </div>
            </section>
          )}

          {/* Skills */}
          {kit.skills.length > 0 && (
            <section>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
                <h3 style={{ fontSize: 12, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, textTransform: 'uppercase', margin: 0 }}>
                  <PhosphorIcon className="bi bi-lightning-charge" style={{ marginRight: 5 }} />Skills
                </h3>
                <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>{selectedSkills.size} of {kit.skills.length} selected</span>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))', gap: 10 }}>
                {kit.skills.map(s => {
                  const selected = selectedSkills.has(s.name)
                  return (
                    <div
                      key={s.name}
                      onClick={() => toggle(selectedSkills, s.name, setSelectedSkills)}
                      className="card"
                      style={{
                        padding: '12px 14px', cursor: 'pointer', userSelect: 'none',
                        border: selected ? '2px solid var(--primary)' : '1.5px solid var(--border)',
                        opacity: selected ? 1 : 0.55, transition: 'all 0.15s',
                      }}
                    >
                      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 8, marginBottom: 6 }}>
                        <div style={{
                          width: 20, height: 20, borderRadius: 4, flexShrink: 0, marginTop: 1,
                          background: selected ? 'var(--primary)' : 'var(--bg-secondary)',
                          border: selected ? 'none' : '1.5px solid var(--border)',
                          display: 'flex', alignItems: 'center', justifyContent: 'center',
                        }}>
                          {selected && <PhosphorIcon className="bi bi-check" style={{ color: '#fff', fontSize: 12 }} />}
                        </div>
                        <div style={{ flex: 1 }}>
                          <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 3 }}>{s.name}</div>
                          <span style={{ fontSize: 10, background: 'var(--bg-secondary)', padding: '1px 6px', borderRadius: 8, color: 'var(--text-muted)' }}>
                            <PhosphorIcon className={`bi ${SKILL_CAT_ICON[s.category] ?? 'bi-stars'}`} style={{ marginRight: 3 }} />{s.category}
                          </span>
                        </div>
                      </div>
                      <p style={{ fontSize: 12, color: 'var(--text-secondary)', margin: '0 0 6px', lineHeight: 1.5 }}>{s.description}</p>
                      {s.tags.length > 0 && (
                        <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginBottom: 6 }}>
                          {s.tags.map(t => <span key={t} style={{ fontSize: 10, background: 'var(--bg-secondary)', border: '1px solid var(--border)', padding: '1px 5px', borderRadius: 8, color: 'var(--text-muted)' }}>#{t}</span>)}
                        </div>
                      )}
                      <details onClick={e => e.stopPropagation()}>
                        <summary style={{ fontSize: 11, color: 'var(--primary)', cursor: 'pointer' }}>View instruction</summary>
                        <pre style={{ fontSize: 11, lineHeight: 1.5, margin: '6px 0 0', background: 'var(--bg-secondary)', padding: '8px 10px', borderRadius: 6, whiteSpace: 'pre-wrap', wordBreak: 'break-word', color: 'var(--text-primary)', maxHeight: 140, overflow: 'auto' }}>{s.instruction}</pre>
                      </details>
                    </div>
                  )
                })}
              </div>
            </section>
          )}

          {/* Footer actions */}
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', paddingTop: 4, flexWrap: 'wrap', gap: 8 }}>
            <div style={{ display: 'flex', gap: 8 }}>
              <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => setStep(1)}>
                <PhosphorIcon className="bi bi-arrow-left" />Back
              </button>
              <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => downloadMd(description, kit, selectedPersonas, selectedAgents, selectedSkills)} disabled={selectedCount === 0}>
                <PhosphorIcon className="bi bi-download" />Download .md
              </button>
            </div>
            <button
              className="btn btn-primary"
              style={{ height: 38, fontSize: 14, minWidth: 200 }}
              onClick={handleCreate}
              disabled={creating || selectedCount === 0}
            >
              {creating
                ? <><div className="spinner" />Creating…</>
                : <><PhosphorIcon className="bi bi-cloud-upload" />Create Selected ({selectedCount})<PhosphorIcon className="bi bi-arrow-right" style={{ marginLeft: 4 }} /></>
              }
            </button>
          </div>
        </div>
      )}

      {/* ── STEP 3: Created ── */}
      {step === 3 && summary && kit && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>

          {/* Success banner */}
          <div style={{ padding: '20px 24px', background: 'rgba(34,197,94,0.08)', border: '1px solid rgba(34,197,94,0.25)', borderRadius: 10, textAlign: 'center' }}>
            <div style={{ fontSize: 32, marginBottom: 8 }}>
              <PhosphorIcon className="bi bi-check-circle-fill" style={{ color: '#22c55e' }} />
            </div>
            <div style={{ fontSize: 16, fontWeight: 700, marginBottom: 4 }}>Workspace kit created!</div>
            <div style={{ fontSize: 13, color: 'var(--text-secondary)' }}>
              {summary.personas} persona{summary.personas !== 1 ? 's' : ''}, {summary.agents} agent{summary.agents !== 1 ? 's' : ''}, and {summary.skills} skill{summary.skills !== 1 ? 's' : ''} added to your library.
            </div>
          </div>

          {/* Links to library sections */}
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 12 }}>
            {summary.personas > 0 && (
              <Link to="/personas" style={{ textDecoration: 'none' }}>
                <div className="card" style={{ padding: '16px 18px', textAlign: 'center', cursor: 'pointer', transition: 'border-color 0.15s' }}>
                  <PhosphorIcon className="bi bi-person-badge" style={{ fontSize: 22, color: 'var(--primary)', display: 'block', marginBottom: 6 }} />
                  <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 2 }}>{summary.personas} Persona{summary.personas !== 1 ? 's' : ''}</div>
                  <div style={{ fontSize: 11, color: 'var(--primary)' }}>View in Personas →</div>
                </div>
              </Link>
            )}
            {summary.agents > 0 && (
              <Link to="/agents" style={{ textDecoration: 'none' }}>
                <div className="card" style={{ padding: '16px 18px', textAlign: 'center', cursor: 'pointer' }}>
                  <PhosphorIcon className="bi bi-robot" style={{ fontSize: 22, color: 'var(--primary)', display: 'block', marginBottom: 6 }} />
                  <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 2 }}>{summary.agents} Agent{summary.agents !== 1 ? 's' : ''}</div>
                  <div style={{ fontSize: 11, color: 'var(--primary)' }}>View in Agents →</div>
                </div>
              </Link>
            )}
            {summary.skills > 0 && (
              <Link to="/skills" style={{ textDecoration: 'none' }}>
                <div className="card" style={{ padding: '16px 18px', textAlign: 'center', cursor: 'pointer' }}>
                  <PhosphorIcon className="bi bi-lightning-charge" style={{ fontSize: 22, color: 'var(--primary)', display: 'block', marginBottom: 6 }} />
                  <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 2 }}>{summary.skills} Skill{summary.skills !== 1 ? 's' : ''}</div>
                  <div style={{ fontSize: 11, color: 'var(--primary)' }}>View in Skills →</div>
                </div>
              </Link>
            )}
          </div>

          {/* Bottom actions */}
          <div style={{ display: 'flex', gap: 8, justifyContent: 'center', paddingTop: 4 }}>
            <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => downloadMd(description, kit, selectedPersonas, selectedAgents, selectedSkills)}>
              <PhosphorIcon className="bi bi-download" />Download .md
            </button>
            <button className="btn btn-primary" style={{ fontSize: 13 }} onClick={() => { setStep(1); setKit(null); setDescription(''); setSummary(null) }}>
              <PhosphorIcon className="bi bi-plus" />Build Another
            </button>
          </div>
        </div>
      )}
    </div>
  )
}
