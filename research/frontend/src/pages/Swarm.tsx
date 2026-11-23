import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useLayoutEffect, useMemo, useRef, type ReactNode } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import PromptPicker from '../components/PromptPicker'
import AgentFileUpload from '../components/AgentFileUpload'
import ExportButtons from '../components/ExportButtons'
import UnsavedOutputNotice, { useUnsavedOutputWarning } from '../components/UnsavedOutputNotice'
import api from '../services/api'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import '../App.css'
import './AITools.css'

function renderMarkdown(text: string): ReactNode {
  const lines = text.split('\n')
  const nodes: ReactNode[] = []
  let listItems: string[] = []

  const flushList = (key: string) => {
    if (listItems.length) {
      nodes.push(
        <ul key={key} style={{ paddingLeft: 20, margin: '6px 0' }}>
          {listItems.map((li, i) => (
            <li key={i} style={{ fontSize: 14, lineHeight: 1.7, color: 'var(--text-primary)' }}
              dangerouslySetInnerHTML={{ __html: inlineMd(li) }} />
          ))}
        </ul>
      )
      listItems = []
    }
  }

  const inlineMd = (s: string) =>
    s.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
     .replace(/\*(.+?)\*/g, '<em>$1</em>')
     .replace(/`(.+?)`/g, '<code style="background:rgba(0,0,0,0.06);padding:1px 5px;border-radius:4px;font-size:0.9em">$1</code>')

  lines.forEach((line, i) => {
    const key = String(i)
    if (/^---+$/.test(line.trim())) {
      flushList(key + 'l')
      nodes.push(<hr key={key} style={{ border: 'none', borderTop: '1px solid var(--border)', margin: '12px 0' }} />)
    } else if (/^### (.+)/.test(line)) {
      flushList(key + 'l')
      nodes.push(<h4 key={key} style={{ fontSize: 14, fontWeight: 700, margin: '14px 0 4px', color: 'var(--text-primary)' }}
        dangerouslySetInnerHTML={{ __html: inlineMd(line.replace(/^### /, '')) }} />)
    } else if (/^## (.+)/.test(line)) {
      flushList(key + 'l')
      nodes.push(<h3 key={key} style={{ fontSize: 15, fontWeight: 700, margin: '16px 0 4px', color: 'var(--text-primary)' }}
        dangerouslySetInnerHTML={{ __html: inlineMd(line.replace(/^## /, '')) }} />)
    } else if (/^# (.+)/.test(line)) {
      flushList(key + 'l')
      nodes.push(<h2 key={key} style={{ fontSize: 16, fontWeight: 700, margin: '16px 0 6px', color: 'var(--text-primary)' }}
        dangerouslySetInnerHTML={{ __html: inlineMd(line.replace(/^# /, '')) }} />)
    } else if (/^[-*] (.+)/.test(line)) {
      listItems.push(line.replace(/^[-*] /, ''))
    } else if (line.trim() === '') {
      flushList(key + 'l')
      nodes.push(<div key={key} style={{ height: 6 }} />)
    } else {
      flushList(key + 'l')
      nodes.push(<p key={key} style={{ fontSize: 14, lineHeight: 1.75, margin: 0, color: 'var(--text-primary)' }}
        dangerouslySetInnerHTML={{ __html: inlineMd(line) }} />)
    }
  })
  flushList('end')
  return <>{nodes}</>
}

interface SwarmAgent { perspective: string; prompt: string; confidenceScore: number }
interface SwarmResult { agents: SwarmAgent[]; consensusPrompt: string; averageConfidence: number }

const PERSPECTIVE_TEMPLATES = [
  { value: '', label: 'No template' },
  { value: 'devil,optimist,realist,critic,innovator', label: "Devil's Advocate Mix" },
  { value: 'technical,business,ux,security,performance', label: 'Product Team' },
  { value: 'expert,beginner,skeptic,enthusiast,pragmatist', label: 'Audience Spectrum' },
  { value: 'short-term,long-term,risk,opportunity,execution', label: 'Strategic Lenses' },
]

export default function Swarm() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('swarm')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Swarm' }])
    return () => setBreadcrumbs([])
  }, [])

  const [intent, setIntent] = useState('')
  const [agentCount, setAgentCount] = useState(5)
  const [perspectives, setPerspectives] = useState('')
  const [perspectiveTemplate, setPerspectiveTemplate] = useState('')

  const [fileContext, setFileContext] = useState('')
  const [tools, setTools] = useState('auto')
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<SwarmResult | null>(null)
  const [error, setError] = useState('')
  const [selectedPrompt, setSelectedPrompt] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)
  const exportRef = useRef<HTMLDivElement>(null)

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph)
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/swarm')
    if (handoff?.input) setIntent(handoff.input)
  }, [])

  const handleTemplateChange = (val: string) => {
    setPerspectiveTemplate(val)
    if (val) setPerspectives(val)
  }

  const handleGenerate = async () => {
    if (!intent.trim()) return
    setLoading(true)
    setResult(null)
    setError('')
    setSelectedPrompt(null)
    const perspectiveList = perspectives.split(',').map(s => s.trim()).filter(Boolean)
    try {
      const res = await api.post<SwarmResult>('/swarm/generate', {
        intent,
        agentCount,
        perspectives: perspectiveList.length ? perspectiveList : undefined,
        useWebSearch: tools !== 'none',
        fileContext: fileContext || undefined,
      })
      setResult(res.data)
      setSelectedPrompt(res.data.consensusPrompt)
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Swarm generation failed')
    } finally {
      setLoading(false)
    }
  }

  const handleReset = () => {
    setIntent('')
    setAgentCount(5)
    setPerspectives('')
    setPerspectiveTemplate('')
    setTools('auto')
    setFileContext('')
    setResult(null)
    setError('')
    setSelectedPrompt(null)
  }

  const copyPrompt = async (text: string) => {
    await navigator.clipboard.writeText(text)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  const confidenceColor = (score: number) =>
    score >= 0.8 ? '#22c55e' : score >= 0.6 ? '#f59e0b' : '#ef4444'
  const hasGeneratedContent = loading || Boolean(result)
  useUnsavedOutputWarning(hasGeneratedContent)

  // --- Gamefield: one desk per perspective agent, consensus in the corner office
  const field = useMemo<FieldModel>(() => {
    const named = perspectives.split(',').map(s => s.trim()).filter(Boolean)
    const roster = result
      ? result.agents.map((a, i) => ({
          id: `agent-${i}`,
          name: a.perspective || `Agent ${i + 1}`,
          role: `${Math.round(a.confidenceScore * 100)}% confident`,
          status: 'done' as const,
          output: a.prompt,
          detail: bubble(a.prompt),
        }))
      : Array.from({ length: agentCount }).map((_, i) => ({
          id: `agent-${i}`,
          name: named[i] || `Agent ${i + 1}`,
          role: tools === 'none' ? 'no tools' : 'web search',
          status: (loading ? 'working' : 'idle') as 'working' | 'idle',
          detail: loading ? 'Forming an angle…' : undefined,
        }))

    const consensus = {
      id: 'consensus',
      name: 'Consensus',
      role: result ? `${Math.round(result.averageConfidence * 100)}% average` : 'synthesiser',
      status: (result ? 'done' : loading ? 'blocked' : 'idle') as 'done' | 'blocked' | 'idle',
      detail: result ? bubble(result.consensusPrompt) : loading ? 'Waiting on the floor…' : undefined,
      output: result?.consensusPrompt,
      boss: true,
    }

    return {
      title: 'Swarm Intelligence',
      subtitle: intent ? bubble(intent, 70) : 'Give the room a question to work on.',
      running: loading,
      agents: [...roster, consensus],
      messages: result ? roster.map(a => ({ id: `msg-${a.id}`, from: a.id, to: 'consensus' })) : [],
      events: error
        ? [{ id: 'err', text: error, tone: 'bad' as const }]
        : result
          ? [{ id: 'done', text: `${result.agents.length} angles synthesised into one consensus prompt.`, tone: 'good' as const }]
          : loading
            ? [{ id: 'run', text: `${agentCount} agents briefed and working.`, tone: 'info' as const }]
            : [],
    }
  }, [result, loading, agentCount, perspectives, tools, intent, error])

  return (
    <div className="ai-tool-page ai-tool-page-swarm" style={{ padding: '24px', display: 'flex', flexDirection: 'column', gap: 20 }}>

      {/* Header */}
      <div className="ai-tool-hero">
        <div>
        <span className="ai-tool-kicker"><PhosphorIcon className="bi bi-cpu" /> Multi-agent synthesis</span>
        <h1 style={{ fontSize: 22, fontWeight: 700, margin: '0 0 4px' }}><span className="ai-tool-hero-icon"><PhosphorIcon className="bi bi-stars" /></span>Swarm Intelligence</h1>
        <p style={{ color: 'var(--text-secondary)', fontSize: 13, margin: 0 }}>
          Spawn agents, each forming their own research angle, reasoning step-by-step, then synthesising consensus.
        </p>
        </div>
        <div className="ai-tool-hero-stats">
          <div><strong>{agentCount}</strong><span>Agents</span></div>
          <div><strong>{tools === 'none' ? 'Off' : 'On'}</strong><span>Tools</span></div>
        </div>
      </div>

      {/* Input card */}
      <div className="card ai-tool-input-card" style={{ padding: '20px 24px' }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 10 }}>
          <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1 }}>QUESTION</label>
          <PromptPicker onSelect={p => setIntent(p.content)} />
        </div>
        <textarea
          className="form-textarea"
          style={{ minHeight: 120, fontSize: 15, lineHeight: 1.6, marginBottom: 4 }}
          placeholder="e.g. A customer support agent that handles billing questions professionally"
          value={intent}
          onChange={e => setIntent(e.target.value)}
          disabled={loading}
        />

        <AgentFileUpload
          disabled={loading}
          onFilesChange={(ctx) => setFileContext(ctx)}
        />

        {/* Bottom toolbar */}
        <div className="ai-tool-controls" style={{
          display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap',
          borderTop: '1px solid var(--border)', paddingTop: 14, marginTop: 8
        }}>
          {/* Perspective template */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
            <span style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-muted)', letterSpacing: 0.8 }}>PERSPECTIVE TEMPLATE</span>
            <select
              className="form-input"
              style={{ fontSize: 12, padding: '4px 8px', height: 30 }}
              value={perspectiveTemplate}
              onChange={e => handleTemplateChange(e.target.value)}
              disabled={loading}
            >
              {PERSPECTIVE_TEMPLATES.map(t => <option key={t.value} value={t.value}>{t.label}</option>)}
            </select>
          </div>

          <div style={{ width: 1, height: 36, background: 'var(--border)', flexShrink: 0 }} />

          {/* Agent count */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
            <span style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-muted)', letterSpacing: 0.8 }}>AGENTS</span>
            <input
              type="number"
              min={1} max={50}
              className="form-input"
              style={{ fontSize: 12, padding: '4px 8px', height: 30, width: 64 }}
              value={agentCount}
              onChange={e => setAgentCount(Math.max(1, Math.min(50, parseInt(e.target.value) || 1)))}
              disabled={loading}
            />
          </div>

          <div style={{ width: 1, height: 36, background: 'var(--border)', flexShrink: 0 }} />

          {/* Perspectives */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 2, flex: 1, minWidth: 160 }}>
            <span style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-muted)', letterSpacing: 0.8 }}>PERSPECTIVES</span>
            <input
              className="form-input"
              style={{ fontSize: 12, padding: '4px 8px', height: 30 }}
              placeholder="optimist, critic, realist, …"
              value={perspectives}
              onChange={e => setPerspectives(e.target.value)}
              disabled={loading}
            />
          </div>

          <div style={{ width: 1, height: 36, background: 'var(--border)', flexShrink: 0 }} />

          {/* Tools */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
            <span style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-muted)', letterSpacing: 0.8 }}>TOOLS</span>
            <select
              className="form-input"
              style={{ fontSize: 12, padding: '4px 8px', height: 30 }}
              value={tools}
              onChange={e => setTools(e.target.value)}
              disabled={loading}
            >
              <option value="auto">Auto (web search)</option>
              <option value="search">Web Search</option>
              <option value="none">No tools</option>
            </select>
          </div>

          {/* Spacer */}
          <div style={{ flex: 1 }} />

          {/* Buttons */}
          <button
            className="btn btn-primary"
            onClick={handleGenerate}
            disabled={loading || !intent.trim()}
            style={{ height: 34, fontSize: 13 }}
          >
            {loading
              ? <><div className="spinner" />Running…</>
              : <><PhosphorIcon className="bi bi-play-fill" />Run Swarm</>
            }
          </button>
          <button className="btn btn-secondary" onClick={handleReset} disabled={loading} style={{ height: 34, fontSize: 13 }}>
            Reset
          </button>
        </div>

        {error && <p style={{ color: '#ef4444', marginTop: 12, fontSize: 13 }}>{error}</p>}
      </div>

      {hasGeneratedContent && <UnsavedOutputNotice />}

      <AgentStage
        {...stageProps}
        model={field}
        actions={[
          { id: 'run', label: loading ? 'Running…' : 'Run swarm', icon: 'bi-play-fill', tone: 'primary', disabled: loading || !intent.trim(), onClick: handleGenerate },
          { id: 'reset', label: 'Reset', icon: 'bi-arrow-counterclockwise', disabled: loading, onClick: handleReset },
        ]}
      />

      {/* Loading state */}
      {loading && (
        <div className="card ai-tool-status-card" style={{ padding: 20 }}>
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginBottom: 12 }}>
            {Array.from({ length: agentCount }).map((_, i) => (
              <div key={i} style={{
                width: 40, height: 40, borderRadius: 20,
                background: 'var(--bg-secondary)', border: '2px solid var(--primary)',
                display: 'flex', alignItems: 'center', justifyContent: 'center',
                animation: `pulse 1.5s ease-in-out ${i * 0.15}s infinite`
              }}>
                <PhosphorIcon className="bi bi-robot" style={{ color: 'var(--primary)', fontSize: 16 }} />
              </div>
            ))}
          </div>
          <p style={{ color: 'var(--text-secondary)', fontSize: 14, margin: 0 }}>
            {agentCount} agents reasoning in parallel…
          </p>
        </div>
      )}

      {/* Results */}
      {result && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
          {/* Consensus */}
          <div className="card ai-tool-result-card swarm-consensus-card" style={{ padding: 20, borderLeft: '4px solid var(--primary)' }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 12 }}>
              <div>
                <h3 style={{ fontSize: 15, fontWeight: 600, margin: '0 0 2px' }}>
                  <PhosphorIcon className="bi bi-stars" style={{ color: 'var(--primary)' }} />Consensus Prompt
                </h3>
                <span style={{ fontSize: 12, color: 'var(--text-secondary)' }}>
                  Average confidence: {(result.averageConfidence * 100).toFixed(0)}%
                </span>
              </div>
              <div style={{ display: 'flex', gap: 8 }}>
                <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => copyPrompt(result.consensusPrompt)}>
                  <PhosphorIcon className={`bi ${copied ? 'bi-check2' : 'bi-clipboard'}`}  />
                  {copied ? 'Copied' : 'Copy'}
                </button>
              </div>
            </div>
            <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 10 }}>
              <ExportButtons
                title={intent.trim() || 'Agent Swarm'}
                filenameBase="swarm"
                subtitle="GenPrompt Swarm"
                getBodyHtml={() => exportRef.current?.innerHTML}
                size="sm"
              />
            </div>
            <div ref={exportRef} style={{ background: 'var(--bg-secondary)', padding: '16px 20px', borderRadius: 8 }}>
              {renderMarkdown(result.consensusPrompt)}
            </div>
          </div>

          {/* Agent grid */}
          <div>
            <h3 style={{ fontSize: 14, fontWeight: 600, marginBottom: 12, color: 'var(--text-secondary)', letterSpacing: 0.5, textTransform: 'uppercase' }}>
              Agent Perspectives ({result.agents.length})
            </h3>
            <div className="ai-tool-card-grid" style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(360px, 1fr))', gap: 14 }}>
              {result.agents.map((agent, i) => (
                <div
                  key={i}
                  className="card ai-tool-result-card swarm-agent-card"
                  style={{
                    padding: 16, cursor: 'pointer',
                    border: selectedPrompt === agent.prompt ? '2px solid var(--primary)' : '1.5px solid var(--border)',
                    transition: 'border-color 0.2s'
                  }}
                  onClick={() => setSelectedPrompt(agent.prompt)}
                >
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 8 }}>
                    <span style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', textTransform: 'uppercase', letterSpacing: 0.5 }}>
                      Agent {i + 1}
                    </span>
                    <span style={{
                      fontSize: 11, fontWeight: 700, padding: '2px 8px', borderRadius: 12,
                      background: confidenceColor(agent.confidenceScore) + '22',
                      color: confidenceColor(agent.confidenceScore)
                    }}>
                      {(agent.confidenceScore * 100).toFixed(0)}%
                    </span>
                  </div>
                  <p style={{ fontSize: 12, color: 'var(--text-secondary)', marginBottom: 8, fontStyle: 'italic' }}>{agent.perspective}</p>
                  <div style={{ fontSize: 13, lineHeight: 1.5, maxHeight: 100, overflow: 'hidden', maskImage: 'linear-gradient(to bottom, black 60%, transparent 100%)' }}>
                    {renderMarkdown(agent.prompt)}
                  </div>
                  <div style={{ marginTop: 10, display: 'flex', gap: 6 }}>
                    <button className="btn btn-secondary" style={{ fontSize: 11, padding: '3px 10px' }}
                      onClick={e => { e.stopPropagation(); copyPrompt(agent.prompt) }}>
                      <PhosphorIcon className="bi bi-clipboard"  />Copy
                    </button>
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
