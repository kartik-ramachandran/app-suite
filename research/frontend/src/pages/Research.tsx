import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useLayoutEffect, useRef, useEffect, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import api from '../services/api'
import { STUDY_STYLES } from '../data/studyStyles'
import PromptPicker from '../components/PromptPicker'
import AgentFileUpload from '../components/AgentFileUpload'
import UnsavedOutputNotice, { useUnsavedOutputWarning } from '../components/UnsavedOutputNotice'
import ExportButtons from '../components/ExportButtons'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import '../App.css'
import './AITools.css'

const SCENARIO_TEMPLATES = [
  { value: '', label: '— Scenario template —' },
  { value: 'technology', label: 'Technology disruption' },
  { value: 'market', label: 'Market evolution' },
  { value: 'geopolitical', label: 'Geopolitical shift' },
  { value: 'climate', label: 'Climate transition' },
  { value: 'workforce', label: 'Workforce transformation' },
]

const TEMPLATE_EXAMPLES: Record<string, string> = {
  technology: 'Will AI coding assistants make junior software engineers redundant?',
  market: 'How will the rise of agentic AI reshape enterprise SaaS pricing models?',
  geopolitical: 'What happens to global supply chains if US-China trade tensions escalate further?',
  climate: 'How will mandatory carbon pricing affect manufacturing competitiveness by 2030?',
  workforce: 'What does hybrid work mean for commercial real estate values in major cities?',
}

const DEFAULT_HORIZONS = ['3 months', '6 months', '1 year', '2 years', '5 years']
const HORIZON_COLORS = ['#833ab4', '#22c55e', '#f59e0b', '#ec4899', '#fa7e1e', '#a855f7', '#f97316']

interface AgentLogEntry {
  agent: string
  horizon: string
  horizonIndex: number
  content: string
  confidence: number
}

interface HorizonSummary {
  horizon: string
  horizonIndex: number
  consensus: string
  averageConfidence: number
}

const getSummaryPoints = (text: string) => {
  const points = text
    .split(/\n+/)
    .map(line => line.trim())
    .filter(Boolean)
    .map(line => line.replace(/^[-*•]\s+/, '').replace(/^\d+[.)]\s+/, '').trim())
    .filter(Boolean)

  if (points.length === 1 && points[0].length > 220) {
    return points[0]
      .split(/(?<=[.!?])\s+(?=[A-Z0-9])/)
      .map(point => point.trim())
      .filter(Boolean)
  }

  return points
}

function SummaryBullets({ text }: { text: string }) {
  const points = getSummaryPoints(text)
  if (points.length === 0) return null

  return (
    <ul style={{ margin: 0, paddingLeft: 18, display: 'flex', flexDirection: 'column', gap: 8 }}>
      {points.map((point, i) => (
        <li key={i} style={{ fontSize: 13, lineHeight: 1.6, color: 'var(--text-primary)', paddingLeft: 2 }}>
          {point}
        </li>
      ))}
    </ul>
  )
}

export default function Research() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('research')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Research' }])
    return () => setBreadcrumbs([])
  }, [])

  const [template, setTemplate] = useState('')
  const [scenario, setScenario] = useState('')
  const [horizons, setHorizons] = useState<string[]>(DEFAULT_HORIZONS)
  const [newHorizon, setNewHorizon] = useState('')
  const [agentsPerHorizon, setAgentsPerHorizon] = useState(3)
  const [perspectives, setPerspectives] = useState('')
  const [fileContext, setFileContext] = useState('')
  const [tools, setTools] = useState('auto')
  const [running, setRunning] = useState(false)
  const [log, setLog] = useState<AgentLogEntry[]>([])
  const [summaries, setSummaries] = useState<HorizonSummary[]>([])
  const [finalSummary, setFinalSummary] = useState('')
  const [error, setError] = useState('')
  const [statusMsg, setStatusMsg] = useState('')
  const abortRef = useRef<AbortController | null>(null)
  const logEndRef = useRef<HTMLDivElement | null>(null)
  const exportRef = useRef<HTMLDivElement>(null)

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph).
  // Topic-shaped briefs collapse the forecast horizons into one deep-dive pass,
  // and the originating star is remembered so the findings weave back to it.
  const cosmosOriginRef = useRef<{ nodeId?: string; title?: string } | null>(null)
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/research')
    if (handoff?.input) {
      setScenario(handoff.input)
      if (handoff.sourceNodeId || handoff.title) {
        cosmosOriginRef.current = { nodeId: handoff.sourceNodeId, title: handoff.title }
      }
      if (handoff.mode === 'topic') setHorizons(['Deep dive'])
    }
  }, [])

  // Auto-weave finished findings into the cosmos, linked to the source star —
  // runs that start from a star save themselves; the manual button stays for
  // runs that didn't.
  const autoWeaveToCosmos = async (content: string) => {
    const origin = cosmosOriginRef.current
    if (!origin || !content.trim()) return
    try {
      const res = await api.post('/knowledge/nodes', {
        title: `Research: ${(origin.title ?? scenario ?? 'findings').slice(0, 120)}`,
        content,
        source: 'fetched',
        sourceModule: 'research',
      })
      const newId = (res.data as { id?: string })?.id
      if (newId && origin.nodeId) {
        await api.post('/knowledge/links', { sourceId: newId, targetId: origin.nodeId, label: 'research' })
      }
      setSavedToCosmos(true)
    } catch { /* best-effort — the manual Save to Cosmos button remains */ }
  }

  // "Present as…" — restyle the finished report through Study Studio's engine.
  const navigate = useNavigate()
  const [presentStyle, setPresentStyle] = useState('handwritten')
  const [presenting, setPresenting] = useState(false)
  const presentAs = async () => {
    if (!finalSummary.trim() || presenting) return
    setPresenting(true)
    try {
      const res = await api.post('/study/generate', {
        styleId: presentStyle,
        topic: scenario.trim() || 'Research findings',
        content: finalSummary,
      })
      navigate(`/study?doc=${res.data.id}`)
    } catch {
      window.alert('Could not restyle the report right now — please try again.')
    } finally {
      setPresenting(false)
    }
  }
  // "Save to Cosmos" — weave the finished report into the knowledge graph.
  const [savingToCosmos, setSavingToCosmos] = useState(false)
  const [savedToCosmos, setSavedToCosmos] = useState(false)
  const saveToCosmos = async () => {
    if (!finalSummary.trim() || savingToCosmos || savedToCosmos) return
    setSavingToCosmos(true)
    try {
      await api.post('/knowledge/nodes', {
        title: scenario.trim() || 'Research findings',
        content: finalSummary,
        source: 'fetched',
        sourceModule: 'research',
      })
      setSavedToCosmos(true)
    } catch {
      window.alert('Could not save to Cosmos right now — please try again.')
    } finally {
      setSavingToCosmos(false)
    }
  }
  const progressRef = useRef(0)

  useEffect(() => {
    logEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [log])

  const handleTemplateChange = (val: string) => {
    setTemplate(val)
    if (val && TEMPLATE_EXAMPLES[val] && !scenario) setScenario(TEMPLATE_EXAMPLES[val])
  }

  const addHorizon = () => {
    const h = newHorizon.trim()
    if (h && !horizons.includes(h)) { setHorizons(prev => [...prev, h]); setNewHorizon('') }
  }

  const removeHorizon = (h: string) => setHorizons(prev => prev.filter(x => x !== h))

  const handleRun = async () => {
    if (!scenario.trim() || running) return
    abortRef.current?.abort()
    const ctrl = new AbortController()
    abortRef.current = ctrl
    setRunning(true)
    setLog([])
    setSummaries([])
    setFinalSummary('')
    setSavedToCosmos(false)
    progressRef.current = 0
    setError('')
    setStatusMsg(`Spawning ${horizons.length * agentsPerHorizon} agents across ${horizons.length} horizons…`)

    const authToken = localStorage.getItem('authToken')
    const baseUrl = import.meta.env.VITE_API_BASE_URL || '/api'

    try {
      const resp = await fetch(`${baseUrl}/research/run`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(authToken ? { Authorization: `Bearer ${authToken}` } : {})
        },
        body: JSON.stringify({
          scenario,
          horizons,
          agentsPerHorizon,
          perspectives: perspectives.split(',').map(s => s.trim()).filter(Boolean),
          tools,
          fileContext: fileContext || undefined,
        }),
        signal: ctrl.signal
      })
      if (!resp.ok) { setError('Research failed to start'); setRunning(false); return }

      let horizonIndex = 0

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
            if (data.type === 'done') { setRunning(false); setStatusMsg('Research complete'); return }
            if (data.type === 'error') { setError(data.content); setRunning(false); return }
            if (data.type === 'horizon_start') {
              horizonIndex = data.index ?? horizonIndex
            }
            if (data.type === 'agent') {
              const hIdx = data.horizonIndex ?? horizons.indexOf(data.horizon)
              progressRef.current += 1 / (horizons.length * agentsPerHorizon)
              setLog(prev => {
                const next = [...prev, {
                  agent: data.agent,
                  horizon: data.horizon,
                  horizonIndex: hIdx,
                  content: data.content,
                  confidence: data.confidence ?? 0.7
                }]
                setStatusMsg(`${next.length} / ${horizons.length * agentsPerHorizon} agents completed`)
                return next
              })
            }
            if (data.type === 'horizon_done') {
              const hIdx = horizons.indexOf(data.horizon)
              setSummaries(prev => [...prev, {
                horizon: data.horizon,
                horizonIndex: hIdx >= 0 ? hIdx : prev.length,
                consensus: data.consensus ?? '',
                averageConfidence: data.averageConfidence ?? 0.7
              }])
            }
            if (data.type === 'final_summary') {
              setFinalSummary(data.content ?? '')
              if (data.content) autoWeaveToCosmos(data.content)
            }
          } catch { /* skip */ }
        }
      }
    } catch (e) {
      if ((e as Error).name !== 'AbortError') setError('Connection failed. Is the backend running?')
    } finally {
      setRunning(false)
    }
  }

  const handleStop = () => { abortRef.current?.abort(); setRunning(false) }
  const hasGeneratedContent = running || log.length > 0 || summaries.length > 0 || Boolean(finalSummary)
  useUnsavedOutputWarning(hasGeneratedContent)

  // Gamefield: one desk per horizon agent, the lab lead in the corner office.
  const field = useMemo<FieldModel>(() => {
    const planned = horizons.length * agentsPerHorizon
    const desks = log.length
      ? log.slice(0, 24).map((e, i) => ({
          id: `agent-${i}`,
          name: e.agent || `Agent ${i + 1}`,
          role: e.horizon,
          status: 'done' as const,
          detail: bubble(e.content),
          output: e.content,
          accent: HORIZON_COLORS[e.horizonIndex % HORIZON_COLORS.length],
        }))
      : Array.from({ length: Math.min(planned, 24) }).map((_, i) => ({
          id: `agent-${i}`,
          name: `Agent ${i + 1}`,
          role: horizons[Math.floor(i / agentsPerHorizon)] || 'horizon',
          status: (running ? 'working' : 'idle') as 'working' | 'idle',
          detail: running ? 'Reading the scenario…' : undefined,
          accent: HORIZON_COLORS[Math.floor(i / agentsPerHorizon) % HORIZON_COLORS.length],
        }))

    return {
      title: 'Research',
      subtitle: scenario ? bubble(scenario, 70) : 'Set a scenario and the horizons to study.',
      running,
      agents: [
        ...desks,
        {
          id: 'lead',
          name: 'Lab Lead',
          role: `${horizons.length} horizons`,
          status: (finalSummary ? 'done' : running ? 'working' : error ? 'failed' : 'idle') as 'done' | 'working' | 'failed' | 'idle',
          detail: error ? bubble(error) : bubble(finalSummary) || bubble(statusMsg),
          output: finalSummary || undefined,
          boss: true,
        },
      ],
      messages: desks.filter(d => d.status === 'done').map(d => ({ id: `in-${d.id}`, from: d.id, to: 'lead' })),
      events: [
        ...(statusMsg ? [{ id: 'status', text: statusMsg, tone: 'info' as const }] : []),
        ...summaries.map(sm => ({
          id: `sum-${sm.horizonIndex}`,
          text: `${sm.horizon}: consensus at ${Math.round(sm.averageConfidence * 100)}% confidence.`,
          tone: 'good' as const,
        })),
        ...(error ? [{ id: 'err', text: error, tone: 'bad' as const }] : []),
      ],
    }
  }, [log, summaries, finalSummary, running, error, statusMsg, horizons, agentsPerHorizon, scenario])

  return (
    <div className="ai-tool-page ai-tool-page-research" style={{ padding: '24px', display: 'flex', flexDirection: 'column', gap: 16 }}>

      <div className="ai-tool-hero">
        <div>
          <span className="ai-tool-kicker"><PhosphorIcon className="bi bi-graph-up" /> AI Research Lab</span>
          <h1><span className="ai-tool-hero-icon"><PhosphorIcon className="bi bi-compass" /></span>Research</h1>
          <p>Run horizon-based scenario analysis with parallel agents, optional web search, and consensus summaries.</p>
        </div>
        <div className="ai-tool-hero-stats">
          <div><strong>{horizons.length}</strong><span>Horizons</span></div>
          <div><strong>{horizons.length * agentsPerHorizon}</strong><span>Agents</span></div>
        </div>
      </div>

      {/* Top input panel */}
      <div className="card ai-tool-input-card" style={{ padding: '20px 24px' }}>
        <div style={{ marginBottom: 14 }}>
          <select
            value={template}
            onChange={e => handleTemplateChange(e.target.value)}
            style={{
              fontSize: 13, padding: '5px 12px', border: '1.5px solid var(--border)',
              borderRadius: 8, background: 'var(--bg-secondary)', color: 'var(--text-primary)',
              cursor: 'pointer', outline: 'none'
            }}
          >
            {SCENARIO_TEMPLATES.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
          </select>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 6 }}>
          <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1 }}>SCENARIO</label>
          <PromptPicker onSelect={p => setScenario(p.content)} />
        </div>
        <textarea
          rows={3}
          placeholder="Describe a scenario to predict… e.g. 'Will AI coding assistants make junior software engineers redundant?'"
          value={scenario}
          onChange={e => setScenario(e.target.value)}
          disabled={running}
          style={{
            width: '100%', border: '1.5px solid var(--border)', borderRadius: 8,
            padding: '10px 14px', fontSize: 14, lineHeight: 1.6, resize: 'none',
            background: 'var(--bg-secondary)', color: 'var(--text-primary)',
            fontFamily: 'inherit', outline: 'none', marginBottom: 8, boxSizing: 'border-box'
          }}
        />

        <AgentFileUpload
          disabled={running}
          onFilesChange={(ctx) => setFileContext(ctx)}
        />

        <div className="ai-tool-controls" style={{ display: 'flex', alignItems: 'flex-end', gap: 24, flexWrap: 'wrap', marginTop: 16 }}>
          {/* Time horizons */}
          <div style={{ flex: 1, minWidth: 280 }}>
            <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, display: 'block', marginBottom: 8 }}>TIME HORIZONS</label>
            <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
              {horizons.map(h => (
                <span key={h} style={{
                  display: 'inline-flex', alignItems: 'center', gap: 6,
                  padding: '4px 12px', border: '1.5px solid var(--border)',
                  borderRadius: 20, fontSize: 13, background: 'var(--card-bg)', fontWeight: 500
                }}>
                  {h}
                  <button onClick={() => removeHorizon(h)} aria-label="Remove horizon" style={{ background: 'none', border: 'none', cursor: 'pointer', padding: 0, lineHeight: 1, color: 'var(--text-muted)', fontSize: 16, display: 'flex' }}><PhosphorIcon className="bi bi-x" /></button>
                </span>
              ))}
              <span style={{ display: 'inline-flex', alignItems: 'center', gap: 6, fontSize: 13 }}>
                <input
                  value={newHorizon}
                  onChange={e => setNewHorizon(e.target.value)}
                  onKeyDown={e => e.key === 'Enter' && addHorizon()}
                  placeholder="e.g. 10 years"
                  className="form-input"
                  style={{ width: 130, fontSize: 12, height: 30, padding: '4px 10px' }}
                />
                <button onClick={addHorizon} className="btn btn-primary" style={{ height: 30, fontSize: 12, padding: '0 14px' }}>+ Add</button>
              </span>
            </div>
          </div>

          {/* Agents + perspectives + tools + run */}
          <div style={{ display: 'flex', alignItems: 'flex-end', gap: 12, flexWrap: 'wrap', flexShrink: 0 }}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              <span style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-muted)', letterSpacing: 0.8 }}>AGENTS</span>
              <input type="number" min={1} max={10} className="form-input"
                style={{ fontSize: 12, padding: '4px 8px', height: 30, width: 64, textAlign: 'center' }}
                value={agentsPerHorizon}
                onChange={e => setAgentsPerHorizon(Math.max(1, Math.min(10, parseInt(e.target.value) || 1)))}
                disabled={running} />
            </div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 200 }}>
              <span style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-muted)', letterSpacing: 0.8 }}>PERSPECTIVES (comma-separated, optional)</span>
              <input type="text" placeholder="Optimistic, Pessimistic, Technical…"
                value={perspectives} onChange={e => setPerspectives(e.target.value)}
                disabled={running} className="form-input"
                style={{ height: 30, fontSize: 12, padding: '4px 8px' }} />
            </div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              <span style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-muted)', letterSpacing: 0.8 }}>TOOLS</span>
              <select className="form-input" value={tools} onChange={e => setTools(e.target.value)}
                disabled={running} style={{ fontSize: 12, height: 30, padding: '4px 8px', width: 'auto' }}>
                <option value="auto">Auto (web search)</option>
                <option value="none">No tools</option>
              </select>
            </div>
            {running ? (
              <button className="btn btn-danger" onClick={handleStop} style={{ height: 34, fontSize: 13 }}>
                <PhosphorIcon className="bi bi-stop-fill" /> Stop
              </button>
            ) : (
              <button className="btn btn-primary" onClick={handleRun} disabled={!scenario.trim()} style={{ height: 34, fontSize: 13 }}>
                <PhosphorIcon className="bi bi-play-fill" /> Run Research
              </button>
            )}
          </div>
        </div>
      </div>

      {error && (
        <div style={{ color: '#ef4444', fontSize: 13, padding: '8px 12px', background: '#ef444410', borderRadius: 8, border: '1px solid #ef444430' }}>
          {error}
        </div>
      )}

      {hasGeneratedContent && <UnsavedOutputNotice />}

      {(running || statusMsg) && !error && (
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, padding: '8px 14px', background: running ? 'rgba(99,102,241,0.08)' : 'rgba(34,197,94,0.08)', borderRadius: 8, border: `1px solid ${running ? 'rgba(99,102,241,0.2)' : 'rgba(34,197,94,0.2)'}`, fontSize: 13 }}>
          {running && <div className="spinner" style={{ width: 14, height: 14, borderColor: 'rgba(99,102,241,0.3)', borderTopColor: '#833ab4', margin: 0 }} />}
          {!running && <PhosphorIcon className="bi bi-check2-circle" style={{ color: '#22c55e' }} />}
          <span style={{ color: running ? '#833ab4' : '#16a34a', fontWeight: 500 }}>{statusMsg}</span>
          {running && (
            <div style={{ flex: 1, height: 3, background: 'rgba(99,102,241,0.15)', borderRadius: 2, overflow: 'hidden' }}>
              <div style={{ height: '100%', background: '#833ab4', borderRadius: 2, width: `${Math.min(progressRef.current * 100, 95)}%`, transition: 'width 0.4s ease' }} />
            </div>
          )}
        </div>
      )}

      <AgentStage
        {...stageProps}
        model={field}
        actions={running
          ? [{ id: 'stop', label: 'Stop', icon: 'bi-stop-circle', tone: 'danger', onClick: handleStop }]
          : [{ id: 'run', label: 'Run research', icon: 'bi-play-fill', tone: 'primary', disabled: !scenario.trim(), onClick: handleRun }]}
      />

      {/* Results: Agent Log + Horizon Summaries */}
      {(log.length > 0 || summaries.length > 0 || running) && (
        <>
        <div className="ai-tool-results-grid research-results-grid" style={{ display: 'grid', gridTemplateColumns: '1fr 1.5fr', gap: 16 }}>

          {/* Agent Log */}
          <div className="card ai-tool-result-card" style={{ padding: '16px 20px' }}>
            <h4 style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, marginBottom: 16 }}>AGENT LOG</h4>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {log.length === 0 && running && (
                <>
                  {[1, 2, 3].map(i => (
                    <div key={i} style={{ borderLeft: `3px solid ${HORIZON_COLORS[i % HORIZON_COLORS.length]}`, paddingLeft: 12, opacity: 0.4 }}>
                      <div style={{ height: 10, background: 'var(--border)', borderRadius: 4, width: '40%', marginBottom: 6 }} />
                      <div style={{ height: 8, background: 'var(--border)', borderRadius: 4, width: '90%', marginBottom: 4 }} />
                      <div style={{ height: 8, background: 'var(--border)', borderRadius: 4, width: '70%' }} />
                    </div>
                  ))}
                </>
              )}
              {log.map((entry, i) => {
                const color = HORIZON_COLORS[entry.horizonIndex % HORIZON_COLORS.length]
                return (
                  <div key={i} style={{ borderLeft: `3px solid ${color}`, paddingLeft: 12 }}>
                    <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 3 }}>
                      <span style={{ fontSize: 11, color: 'var(--text-muted)', fontWeight: 600 }}>{entry.agent}</span>
                      <span style={{ fontSize: 11, padding: '1px 6px', borderRadius: 8, background: color + '22', color, fontWeight: 700 }}>{entry.horizon}</span>
                    </div>
                    <p style={{ margin: 0, fontSize: 12, lineHeight: 1.5, color: 'var(--text-secondary)', display: '-webkit-box', WebkitLineClamp: 4, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}>
                      {entry.content}
                    </p>
                    <div style={{ marginTop: 4, display: 'flex', alignItems: 'center', gap: 6 }}>
                      <div style={{ flex: 1, height: 2, background: 'var(--border)', borderRadius: 1 }}>
                        <div style={{ width: `${entry.confidence * 100}%`, height: '100%', background: color, borderRadius: 1 }} />
                      </div>
                      <span style={{ fontSize: 10, color: 'var(--text-muted)' }}>{Math.round(entry.confidence * 100)}%</span>
                    </div>
                  </div>
                )
              })}
              {running && log.length > 0 && (
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, color: 'var(--text-muted)', fontSize: 12, fontStyle: 'italic' }}>
                  <div className="spinner" style={{ width: 14, height: 14 }} /> Agents running…
                </div>
              )}
              <div ref={logEndRef} />
            </div>
          </div>

          {/* Horizon Summaries */}
          <div className="card ai-tool-result-card" style={{ padding: '16px 20px' }}>
            <h4 style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, marginBottom: 16 }}>HORIZON SUMMARIES</h4>
            {summaries.length === 0 ? (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
                {horizons.map((h, i) => (
                  <div key={h} style={{ padding: '12px 16px', border: '1px solid var(--border)', borderRadius: 8, borderLeft: `3px solid ${HORIZON_COLORS[i % HORIZON_COLORS.length]}` }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
                      <span style={{ fontSize: 12, fontWeight: 700, color: HORIZON_COLORS[i % HORIZON_COLORS.length] }}>{h}</span>
                      {running && <div className="spinner" style={{ width: 12, height: 12 }} />}
                    </div>
                    <div style={{ height: 8, background: 'var(--border)', borderRadius: 4, width: '80%', marginBottom: 4 }} />
                    <div style={{ height: 8, background: 'var(--border)', borderRadius: 4, width: '60%' }} />
                  </div>
                ))}
              </div>
            ) : (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
                {summaries.map((s, i) => {
                  const color = HORIZON_COLORS[s.horizonIndex % HORIZON_COLORS.length]
                  return (
                    <div key={i} style={{ padding: '14px 16px', border: '1px solid var(--border)', borderRadius: 8, borderLeft: `3px solid ${color}` }}>
                      <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 8 }}>
                        <span style={{ fontSize: 12, fontWeight: 700, color }}>{s.horizon}</span>
                        <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                          {Math.round(s.averageConfidence * 100)}% avg confidence
                        </span>
                      </div>
                      <SummaryBullets text={s.consensus} />
                    </div>
                  )
                })}
                {running && summaries.length < horizons.length && (
                  <div style={{ padding: '14px 16px', border: '1px dashed var(--border)', borderRadius: 8, display: 'flex', alignItems: 'center', gap: 10 }}>
                    <div className="spinner" style={{ width: 14, height: 14 }} />
                    <span style={{ fontSize: 13, color: 'var(--text-muted)' }}>Synthesising next horizon…</span>
                  </div>
                )}
              </div>
            )}
          </div>

        </div>

        {/* Final summary */}
        {(finalSummary || (!running && summaries.length > 0 && !finalSummary)) && (
          <>
          {finalSummary && (
            <div style={{ display: 'flex', justifyContent: 'flex-end', alignItems: 'center', gap: 8, marginBottom: 10, flexWrap: 'wrap' }}>
              {/* Present the findings in any Study Studio learning style. */}
              <select
                value={presentStyle}
                onChange={e => setPresentStyle(e.target.value)}
                disabled={presenting}
                style={{ padding: '6px 10px', borderRadius: 8, border: '1.5px solid var(--border)', background: 'var(--card-bg)', color: 'var(--text)', fontSize: 13 }}
                aria-label="Present findings as"
              >
                {STUDY_STYLES.map(s => <option key={s.id} value={s.id}>{s.label}</option>)}
              </select>
              <button
                type="button"
                className="btn btn-secondary"
                style={{ fontSize: 13, padding: '6px 12px' }}
                onClick={presentAs}
                disabled={presenting}
              >
                {presenting ? <><span className="spinner" style={{ width: 12, height: 12, marginRight: 6 }} /> Styling…</> : <><PhosphorIcon className="bi bi-easel" /> Present as</>}
              </button>
              <button
                type="button"
                className="btn btn-secondary"
                style={{ fontSize: 13, padding: '6px 12px' }}
                onClick={saveToCosmos}
                disabled={savingToCosmos || savedToCosmos}
                title="Add these findings to your knowledge cosmos"
              >
                {savingToCosmos
                  ? <><span className="spinner" style={{ width: 12, height: 12, marginRight: 6 }} /> Weaving…</>
                  : savedToCosmos
                    ? <><PhosphorIcon className="bi bi-check2" /> In Cosmos</>
                    : <><PhosphorIcon className="bi bi-stars" /> Save to Cosmos</>}
              </button>
              <ExportButtons
                title={scenario.trim() || 'Research'}
                filenameBase="research"
                subtitle="GenPrompt Research"
                getBodyHtml={() => exportRef.current?.innerHTML}
                size="sm"
              />
            </div>
          )}
          <div ref={exportRef} className="card ai-tool-result-card" style={{ padding: '24px 28px', border: '1px solid #22c55e30', background: 'var(--card-bg)' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 16, paddingBottom: 14, borderBottom: '1px solid var(--border)' }}>
              <div style={{ width: 28, height: 28, borderRadius: 14, background: '#22c55e15', border: '1.5px solid #22c55e50', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
                {finalSummary
                  ? <PhosphorIcon className="bi bi-check2" style={{ fontSize: 13, color: '#22c55e' }} />
                  : <div className="spinner" style={{ width: 14, height: 14 }} />}
              </div>
              <span style={{ fontSize: 13, fontWeight: 700, color: '#22c55e', letterSpacing: 0.5 }}>
                {finalSummary ? 'RESEARCH SUMMARY' : 'GENERATING SUMMARY…'}
              </span>
            </div>
            {finalSummary && (
              <div style={{ fontSize: 14, lineHeight: 1.8 }}>
                <SummaryBullets text={finalSummary} />
              </div>
            )}
          </div>
          </>
        )}
        </>
      )}
    </div>
  )
}
