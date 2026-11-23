import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useLayoutEffect, useRef, useMemo } from 'react'
import ReactMarkdown from 'react-markdown'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import PromptPicker from '../components/PromptPicker'
import UnsavedOutputNotice, { useUnsavedOutputWarning } from '../components/UnsavedOutputNotice'
import ExportButtons from '../components/ExportButtons'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import '../App.css'
import './AITools.css'

type PermissionMode = 'auto' | 'none' | 'custom'
interface Step { type: string; content?: string; toolName?: string; toolInput?: string; toolOutput?: string }

function ToolStep({ step }: { step: Step }) {
  const [open, setOpen] = useState(false)
  let inputPretty = ''
  try { inputPretty = step.toolInput ? JSON.stringify(JSON.parse(step.toolInput), null, 2) : '' } catch { inputPretty = step.toolInput || '' }

  return (
    <div style={{ border: '1px solid #833ab425', borderRadius: 8, overflow: 'hidden', background: 'var(--card-bg)' }}>
      <button
        onClick={() => setOpen(o => !o)}
        style={{ width: '100%', background: '#833ab408', border: 'none', cursor: 'pointer', padding: '8px 12px', display: 'flex', alignItems: 'center', gap: 8, textAlign: 'left' }}
      >
        <span style={{ width: 20, height: 20, borderRadius: 4, background: '#833ab418', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
          <PhosphorIcon className="bi bi-tools" style={{ color: '#833ab4', fontSize: 10 }} />
        </span>
        <span style={{ fontSize: 12, fontWeight: 600, color: '#833ab4', fontFamily: 'monospace' }}>{step.toolName}</span>
        {inputPretty && (
          <span style={{ fontSize: 11, color: 'var(--text-secondary)', flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {step.toolInput?.slice(0, 80)}
          </span>
        )}
        <PhosphorIcon className={`bi bi-chevron-${open ? 'up' : 'down'}`} style={{ fontSize: 10, color: 'var(--text-secondary)', marginLeft: 'auto', flexShrink: 0 }} />
      </button>
      {open && (
        <div style={{ padding: '10px 12px', borderTop: '1px solid #833ab415', display: 'flex', flexDirection: 'column', gap: 8 }}>
          {inputPretty && (
            <div>
              <div style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, marginBottom: 4 }}>INPUT</div>
              <pre style={{ margin: 0, fontSize: 12, lineHeight: 1.5, whiteSpace: 'pre-wrap', fontFamily: 'monospace', color: 'var(--text-secondary)', background: 'var(--bg-secondary)', padding: '8px', borderRadius: 6 }}>{inputPretty}</pre>
            </div>
          )}
          {step.toolOutput && (
            <div>
              <div style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, marginBottom: 4 }}>OUTPUT</div>
              <pre style={{ margin: 0, fontSize: 12, lineHeight: 1.5, whiteSpace: 'pre-wrap', fontFamily: 'monospace', color: 'var(--text-secondary)', background: 'var(--bg-secondary)', padding: '8px', borderRadius: 6, maxHeight: 200, overflowY: 'auto' }}>{step.toolOutput}</pre>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function ThinkingStep({ step }: { step: Step }) {
  const [open, setOpen] = useState(false)
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      <button
        onClick={() => setOpen(o => !o)}
        style={{ background: 'none', border: 'none', cursor: 'pointer', display: 'flex', alignItems: 'center', gap: 6, padding: '2px 0', width: 'fit-content' }}
      >
        <PhosphorIcon className="bi bi-lightbulb" style={{ color: '#f59e0b', fontSize: 12 }} />
        <span style={{ fontSize: 12, color: 'var(--text-secondary)', fontStyle: 'italic' }}>Thinking…</span>
        <PhosphorIcon className={`bi bi-chevron-${open ? 'up' : 'down'}`} style={{ fontSize: 10, color: 'var(--text-secondary)' }} />
      </button>
      {open && (
        <pre style={{ margin: '4px 0 0 24px', fontSize: 12, lineHeight: 1.5, whiteSpace: 'pre-wrap', fontFamily: 'inherit', color: 'var(--text-secondary)', opacity: 0.7, background: 'var(--bg-secondary)', padding: '8px 12px', borderRadius: 6, borderLeft: '2px solid #f59e0b40' }}>
          {step.content}
        </pre>
      )}
    </div>
  )
}

function StepBadge({ type }: { type: string }) {
  const map: Record<string, { icon: string; color: string; label: string }> = {
    step:    { icon: 'bi-arrow-right-circle', color: '#833ab4', label: 'Step' },
    message: { icon: 'bi-chat-text',          color: '#d62976', label: 'Message' },
    info:    { icon: 'bi-info-circle',         color: '#64748b', label: 'Info' },
  }
  const s = map[type] || map.info
  return (
    <span style={{ width: 20, height: 20, borderRadius: 10, background: s.color + '18', border: `1px solid ${s.color}33`, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0, marginTop: 2 }}>
      <PhosphorIcon className={`bi ${s.icon}`} style={{ fontSize: 10, color: s.color }} />
    </span>
  )
}

export default function Coordinator() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('coordinator')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Coordinator' }])
    return () => setBreadcrumbs([])
  }, [])

  const [question, setQuestion] = useState('')
  const [permissionMode, setPermissionMode] = useState<PermissionMode>('auto')
  const [workerCount, setWorkerCount] = useState(3)
  const [perspectives, setPerspectives] = useState('')
  const [running, setRunning] = useState(false)
  const [steps, setSteps] = useState<Step[]>([])
  const [error, setError] = useState('')
  const abortRef = useRef<AbortController | null>(null)
  const logEndRef = useRef<HTMLDivElement | null>(null)
  const answerRef = useRef<HTMLDivElement | null>(null)
  const exportRef = useRef<HTMLDivElement>(null)

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph). Prefill only.
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/coordinator')
    if (handoff?.input) setQuestion(handoff.input)
  }, [])

  const handleRun = async () => {
    if (!question.trim() || running) return
    abortRef.current?.abort()
    const ctrl = new AbortController()
    abortRef.current = ctrl
    setRunning(true)
    setSteps([])
    setError('')

    const authToken = localStorage.getItem('authToken')
    const baseUrl = import.meta.env.VITE_API_BASE_URL || '/api'

    try {
      const resp = await fetch(`${baseUrl}/coordinator/run`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(authToken ? { Authorization: `Bearer ${authToken}` } : {})
        },
        body: JSON.stringify({
          question,
          permissionMode,
          workerCount,
          perspectives: perspectives.split(',').map(s => s.trim()).filter(Boolean)
        }),
        signal: ctrl.signal
      })
      if (!resp.ok) { setError('Failed to start coordinator'); setRunning(false); return }

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
            if (data.type === 'done') { setRunning(false); setTimeout(() => answerRef.current?.scrollIntoView({ behavior: 'smooth', block: 'start' }), 100); return }
            if (data.type === 'error') { setError(data.content); setRunning(false); return }
            setSteps(prev => [...prev, data])
            setTimeout(() => logEndRef.current?.scrollIntoView({ behavior: 'smooth', block: 'nearest' }), 50)
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

  const finalAnswer = steps.find(s => s.type === 'output')
  const logSteps = steps.filter(s => s.type !== 'output')

  const toolCallCount = logSteps.filter(s => s.type === 'tool_call').length
  // Gamefield: the coordinator keeps the corner office and dispatches a
  // researcher desk for every tool call it makes; a desk lights up on the call
  // and files its findings when the result comes back.
  const field = useMemo<FieldModel>(() => {
    type Call = { id: string; name: string; role: string; detail?: string; output?: string; done: boolean }
    const calls: Call[] = []
    let pending: Call | null = null

    for (const step of logSteps) {
      if (step.type === 'tool_call') {
        pending = {
          id: `call-${calls.length}`,
          name: `Researcher ${calls.length + 1}`,
          role: step.toolName || 'tool',
          detail: bubble(step.toolInput),
          done: false,
        }
        calls.push(pending)
      } else if (step.type === 'tool_result') {
        const target = pending || calls[calls.length - 1]
        if (target) {
          target.output = step.toolOutput || step.content
          target.detail = bubble(step.toolOutput || step.content) || target.detail
          target.done = true
          pending = null
        }
      } else if (step.type === 'thinking' && pending) {
        pending.detail = bubble(step.content) || pending.detail
      }
    }

    const desks = calls.map(c => ({
      id: c.id,
      name: c.name,
      role: c.role,
      status: (c.done ? 'done' : running ? 'working' : 'idle') as 'done' | 'working' | 'idle',
      detail: c.detail,
      output: c.output,
    }))

    const lastThought = [...logSteps].reverse().find(st => st.type === 'thinking')

    return {
      title: 'Coordinator',
      subtitle: question ? bubble(question, 70) : 'Ask something worth splitting up.',
      running,
      agents: [
        ...desks,
        {
          id: 'master',
          name: 'Coordinator',
          role: `${workerCount} workers · ${permissionMode === 'none' ? 'no tools' : 'web search'}`,
          status: (finalAnswer ? 'done' : running ? 'working' : error ? 'failed' : 'idle') as 'done' | 'working' | 'failed' | 'idle',
          detail: error ? bubble(error) : finalAnswer ? bubble(finalAnswer.content) : bubble(lastThought?.content),
          output: finalAnswer?.content,
          boss: true,
        },
      ],
      messages: calls.flatMap(c => [
        { id: `out-${c.id}`, from: 'master', to: c.id },
        ...(c.done ? [{ id: `back-${c.id}`, from: c.id, to: 'master' }] : []),
      ]),
      events: [
        ...calls.map(c => ({
          id: `ev-${c.id}`,
          text: c.done ? `${c.name} reported back.` : `${c.name} dispatched (${c.role}).`,
          tone: (c.done ? 'good' : 'info') as 'good' | 'info',
        })),
        ...(finalAnswer ? [{ id: 'final', text: 'Findings synthesised into the final answer.', tone: 'good' as const }] : []),
        ...(error ? [{ id: 'err', text: error, tone: 'bad' as const }] : []),
      ],
    }
  }, [logSteps, running, finalAnswer, error, question, workerCount, permissionMode])

  const hasGeneratedContent = running || steps.length > 0
  useUnsavedOutputWarning(hasGeneratedContent)

  return (
    <div className="ai-tool-page ai-tool-page-coordinator" style={{ padding: '24px', display: 'flex', flexDirection: 'column', gap: 20 }}>

      {/* Header */}
      <div className="ai-tool-hero">
        <div>
        <span className="ai-tool-kicker"><PhosphorIcon className="bi bi-diagram-3" /> Worker orchestration</span>
        <h1 style={{ fontSize: 24, fontWeight: 700, margin: '0 0 6px', display: 'flex', alignItems: 'center', gap: 10 }}>
          <span className="ai-tool-hero-icon" style={{ width: 36, height: 36, borderRadius: 10, background: 'linear-gradient(135deg,#833ab4,#833ab4)', display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}>
            <PhosphorIcon className="bi bi-diagram-3" style={{ color: '#fff', fontSize: 16 }} />
          </span>
          Coordinator
        </h1>
        <p style={{ color: 'var(--text-secondary)', fontSize: 14, margin: 0 }}>
          A master LLM spawns isolated worker agents, collects their findings, and synthesises a structured final answer.
        </p>
        </div>
        <div className="ai-tool-hero-stats">
          <div><strong>{workerCount}</strong><span>Workers</span></div>
          <div><strong>{permissionMode === 'none' ? 'Off' : 'Auto'}</strong><span>Tools</span></div>
        </div>
      </div>

      {/* Input card */}
      <div className="card ai-tool-input-card" style={{ padding: '20px 24px' }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 8 }}>
          <label style={{ fontSize: 12, fontWeight: 600, color: 'var(--text-secondary)', letterSpacing: 0.5 }}>QUESTION</label>
          <PromptPicker onSelect={p => setQuestion(p.content)} />
        </div>
        <textarea
          rows={4}
          className="form-textarea"
          placeholder="Ask a complex question — the coordinator will research it with multiple parallel workers…"
          value={question}
          onChange={e => setQuestion(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) handleRun() }}
          disabled={running}
          style={{ fontSize: 14, lineHeight: 1.6, marginBottom: 14, resize: 'vertical' }}
        />
        <div className="ai-tool-controls" style={{ display: 'flex', alignItems: 'center', gap: 16, flexWrap: 'wrap' }}>
          {/* Workers */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <span style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 0.8 }}>WORKERS (1–10)</span>
            <input
              type="number"
              min={1} max={10}
              value={workerCount}
              onChange={e => setWorkerCount(Math.max(1, Math.min(10, +e.target.value)))}
              disabled={running}
              className="form-input"
              style={{ width: 70, height: 34, fontSize: 14, textAlign: 'center' }}
            />
          </div>
          {/* Perspectives */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4, flex: 1, minWidth: 180 }}>
            <span style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 0.8 }}>PERSPECTIVES (comma-separated, optional)</span>
            <input
              type="text"
              placeholder="Technical, Business, Risk, Creative…"
              value={perspectives}
              onChange={e => setPerspectives(e.target.value)}
              disabled={running}
              className="form-input"
              style={{ height: 34, fontSize: 13 }}
            />
          </div>
          {/* Tools */}
          <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            <span style={{ fontSize: 10, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 0.8 }}>TOOLS</span>
            <select
              className="form-input"
              value={permissionMode}
              onChange={e => setPermissionMode(e.target.value as PermissionMode)}
              disabled={running}
              style={{ fontSize: 13, height: 34, width: 'auto' }}
            >
              <option value="auto">Auto (web search)</option>
              <option value="none">No tools</option>
            </select>
          </div>
          {/* Run/Stop */}
          <div style={{ display: 'flex', alignItems: 'flex-end', paddingBottom: 0, gap: 8, alignSelf: 'flex-end' }}>
            {running ? (
              <button className="btn btn-danger" onClick={handleStop} style={{ height: 34, fontSize: 13, display: 'flex', alignItems: 'center', gap: 6 }}>
                <PhosphorIcon className="bi bi-stop-fill" /> Stop
              </button>
            ) : (
              <button
                className="btn btn-primary"
                onClick={handleRun}
                disabled={!question.trim()}
                style={{ height: 34, fontSize: 13, display: 'flex', alignItems: 'center', gap: 6 }}
              >
                <PhosphorIcon className="bi bi-play-fill" /> Run
              </button>
            )}
          </div>
        </div>
      </div>

      {error && (
        <div style={{ color: '#ef4444', fontSize: 13, padding: '10px 14px', background: '#ef444410', borderRadius: 8, border: '1px solid #ef444430', display: 'flex', alignItems: 'center', gap: 8 }}>
          <PhosphorIcon className="bi bi-exclamation-triangle-fill" style={{ flexShrink: 0 }} />
          {error}
        </div>
      )}

      {hasGeneratedContent && <UnsavedOutputNotice />}

      {/* Running status bar */}
      {running && (
        <div className="card ai-tool-status-card" style={{ padding: '14px 20px', border: '1px solid #833ab425', background: 'linear-gradient(135deg, #833ab405, #833ab405)' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
            <div className="spinner" style={{ width: 18, height: 18, flexShrink: 0 }} />
            <div style={{ flex: 1 }}>
              <div style={{ fontSize: 13, fontWeight: 600, color: 'var(--text-primary)' }}>Coordinator working…</div>
              <div style={{ fontSize: 12, color: 'var(--text-secondary)', marginTop: 2 }}>
                {logSteps.length} steps completed
                {toolCallCount > 0 && ` · ${toolCallCount} tool call${toolCallCount > 1 ? 's' : ''}`}
              </div>
            </div>
            <div style={{ display: 'flex', gap: 6 }}>
              {['Spawning workers', 'Researching', 'Synthesising'].map((label, i) => (
                <span key={i} style={{ fontSize: 11, padding: '3px 8px', borderRadius: 12, background: '#833ab412', color: '#833ab4', border: '1px solid #833ab425' }}>{label}</span>
              ))}
            </div>
          </div>
          {logSteps.length > 0 && (
            <div style={{ marginTop: 10, padding: '8px 10px', background: 'var(--bg-secondary)', borderRadius: 6, fontSize: 12, color: 'var(--text-secondary)', fontFamily: 'monospace', lineHeight: 1.5 }}>
              {logSteps.slice(-1)[0]?.content?.slice(0, 120) || logSteps.slice(-1)[0]?.toolName || '…'}
            </div>
          )}
        </div>
      )}

      <AgentStage
        {...stageProps}
        model={field}
        actions={running
          ? [{ id: 'stop', label: 'Stop', icon: 'bi-stop-circle', tone: 'danger', onClick: handleStop }]
          : [{ id: 'run', label: 'Run', icon: 'bi-play-fill', tone: 'primary', disabled: !question.trim(), onClick: handleRun }]}
      />

      {/* Coordinator log */}
      {logSteps.length > 0 && (
        <div className="card ai-tool-result-card coordinator-log-card" style={{ padding: '20px 24px' }}>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 14 }}>
            <h4 style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, margin: 0 }}>COORDINATOR LOG</h4>
            <span style={{ fontSize: 11, color: 'var(--text-secondary)', background: 'var(--bg-secondary)', padding: '2px 8px', borderRadius: 10 }}>
              {logSteps.length} steps
              {toolCallCount > 0 && ` · ${toolCallCount} tool calls`}
            </span>
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {logSteps.map((step, i) => {
              if (step.type === 'tool_call' || step.type === 'tool_result') {
                return <ToolStep key={i} step={step} />
              }
              if (step.type === 'thinking') {
                return <ThinkingStep key={i} step={step} />
              }
              return (
                <div key={i} style={{ display: 'flex', gap: 10, alignItems: 'flex-start' }}>
                  <StepBadge type={step.type} />
                  <p style={{ margin: 0, fontSize: 13, lineHeight: 1.6, color: 'var(--text-secondary)', whiteSpace: 'pre-wrap' }}>
                    {step.content}
                  </p>
                </div>
              )
            })}
            <div ref={logEndRef} />
          </div>
        </div>
      )}

      {/* Final answer */}
      {finalAnswer && (
        <div ref={answerRef} className="card ai-tool-result-card coordinator-answer-card" style={{ padding: '24px 28px', border: '1px solid #22c55e30', background: 'var(--card-bg)' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 20, paddingBottom: 14, borderBottom: '1px solid var(--border-color)' }}>
            <div style={{ width: 28, height: 28, borderRadius: 14, background: '#22c55e15', border: '1.5px solid #22c55e50', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>
              <PhosphorIcon className="bi bi-check2" style={{ fontSize: 13, color: '#22c55e' }} />
            </div>
            <span style={{ fontSize: 13, fontWeight: 700, color: '#22c55e', letterSpacing: 0.5 }}>FINAL ANSWER</span>
            <span style={{ marginLeft: 'auto', fontSize: 12, color: 'var(--text-secondary)' }}>
              {logSteps.length} steps · {toolCallCount} tool calls
            </span>
          </div>
          <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 10 }}>
            <ExportButtons
              title={question.trim() || 'Coordinator'}
              filenameBase="coordinator"
              subtitle="GenPrompt Coordinator"
              getBodyHtml={() => exportRef.current?.innerHTML}
              size="sm"
            />
          </div>
          <div ref={exportRef} className="markdown-body" style={{ fontSize: 14, lineHeight: 1.8 }}>
            <ReactMarkdown>{finalAnswer.content || ''}</ReactMarkdown>
          </div>
        </div>
      )}

    </div>
  )
}
