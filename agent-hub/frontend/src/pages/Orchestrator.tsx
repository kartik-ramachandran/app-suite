import PhosphorIcon from '../components/PhosphorIcon'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, toStatus, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import api from '../services/api'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import './Orchestrator.css'

interface RunTask {
  id: number
  key: string
  title: string
  tool: string
  status: 'pending' | 'ready' | 'running' | 'waiting_approval' | 'done' | 'failed' | 'skipped'
  output?: string | null
  errorMessage?: string | null
  needsApproval: boolean
  approvedAt?: string | null
  durationMs: number
  attempts: number
  dependsOn: string
  arguments: string
}

interface RunDetail {
  id: string
  goal: string
  status: 'planning' | 'running' | 'waiting_approval' | 'completed' | 'failed' | 'cancelled'
  planSummary: string
  result?: string | null
  errorMessage?: string | null
  stepsUsed: number
  maxSteps: number
  createdAt: string
  completedAt?: string | null
  tasks: RunTask[]
}

interface RunSummary {
  id: string
  goal: string
  status: RunDetail['status']
  planSummary: string
  stepsUsed: number
  createdAt: string
  completedAt?: string | null
}

const ACTIVE: RunDetail['status'][] = ['planning', 'running', 'waiting_approval']

const TASK_ICON: Record<RunTask['status'], string> = {
  pending: 'bi-circle',
  ready: 'bi-circle-half',
  running: 'bi-arrow-repeat spin',
  waiting_approval: 'bi-hand-index-thumb',
  done: 'bi-check-circle-fill',
  failed: 'bi-x-circle-fill',
  skipped: 'bi-dash-circle',
}

export default function Orchestrator() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('orchestrator')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Autopilot' }])
    return () => setBreadcrumbs([])
  }, [setBreadcrumbs])

  const [goal, setGoal] = useState('')
  const [starting, setStarting] = useState(false)
  const [runs, setRuns] = useState<RunSummary[]>([])
  const [run, setRun] = useState<RunDetail | null>(null)
  const [error, setError] = useState('')
  const [openTask, setOpenTask] = useState<number | null>(null)
  const pollRef = useRef<number | null>(null)

  // A star routed here from Cosmos arrives as the goal.
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/orchestrator')
    if (handoff?.input) setGoal(handoff.input)
  }, [])

  const loadRuns = useCallback(async () => {
    try {
      const { data } = await api.get('/orchestrator/runs')
      setRuns(data)
    } catch { /* the list is not worth an error banner */ }
  }, [])

  const loadRun = useCallback(async (id: string) => {
    try {
      const { data } = await api.get(`/orchestrator/runs/${id}`)
      setRun(data)
      return data as RunDetail
    } catch {
      setError('Could not load that run.')
      return null
    }
  }, [])

  useEffect(() => { loadRuns() }, [loadRuns])

  // Poll only while something is actually moving.
  useEffect(() => {
    if (!run || !ACTIVE.includes(run.status)) {
      if (pollRef.current) { window.clearInterval(pollRef.current); pollRef.current = null }
      return
    }
    pollRef.current = window.setInterval(async () => {
      const fresh = await loadRun(run.id)
      if (fresh && !ACTIVE.includes(fresh.status)) loadRuns()
    }, 2500)
    return () => { if (pollRef.current) window.clearInterval(pollRef.current) }
  }, [run, loadRun, loadRuns])

  const start = async () => {
    if (!goal.trim() || starting) return
    setStarting(true)
    setError('')
    try {
      const { data } = await api.post('/orchestrator/runs', { goal: goal.trim() })
      setGoal('')
      await loadRuns()
      await loadRun(data.id)
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not start that run.')
    } finally {
      setStarting(false)
    }
  }

  const decide = async (task: RunTask, approve: boolean) => {
    try {
      await api.post(`/orchestrator/tasks/${task.id}/${approve ? 'approve' : 'decline'}`)
      if (run) loadRun(run.id)
    } catch {
      setError('Could not record that decision.')
    }
  }

  const cancel = async () => {
    if (!run || !window.confirm('Cancel this run? Steps that have not started are skipped.')) return
    try {
      await api.post(`/orchestrator/runs/${run.id}/cancel`)
      loadRun(run.id)
      loadRuns()
    } catch {
      setError('Could not cancel.')
    }
  }

  // Gamefield: the planner takes the corner office, every task in the plan gets
  // a desk, and a finished dependency posts an envelope to whatever it unblocks.
  const field = useMemo<FieldModel>(() => {
    const tasks = run?.tasks ?? []
    const byKey = new Map(tasks.map(t => [t.key, t]))

    const desks = tasks.map(t => ({
      id: `task-${t.id}`,
      name: t.title,
      role: t.tool + (t.durationMs > 0 ? ` · ${(t.durationMs / 1000).toFixed(1)}s` : ''),
      status: toStatus(t.status),
      detail: t.status === 'waiting_approval'
        ? 'Needs your approval before this touches the outside world.'
        : t.errorMessage ? bubble(t.errorMessage) : bubble(t.output),
      output: t.errorMessage ? `${t.errorMessage}

${t.output || ''}`.trim() : (t.output || undefined),
      // Approve and skip have to be reachable from the floor, otherwise field
      // mode would stall every run that needs a decision.
      actions: t.status === 'waiting_approval'
        ? [
            { id: `approve-${t.id}`, label: 'Approve', icon: 'bi-check-lg', tone: 'primary' as const, onClick: () => decide(t, true) },
            { id: `skip-${t.id}`, label: 'Skip', icon: 'bi-dash-lg', onClick: () => decide(t, false) },
          ]
        : undefined,
    }))

    const messages: { id: string; from: string; to: string }[] = []
    for (const t of tasks) {
      let deps: string[] = []
      try { deps = JSON.parse(t.dependsOn || '[]') } catch { deps = [] }
      for (const key of deps) {
        const src = byKey.get(key)
        if (src && src.status === 'done') messages.push({ id: `dep-${src.id}-${t.id}`, from: `task-${src.id}`, to: `task-${t.id}` })
      }
      if (!deps.length && t.status !== 'pending') messages.push({ id: `brief-${t.id}`, from: 'planner', to: `task-${t.id}` })
    }

    const plannerStatus =
      !run ? 'idle' as const
      : run.status === 'planning' ? 'working' as const
      : run.status === 'waiting_approval' ? 'blocked' as const
      : run.status === 'running' ? 'idle' as const
      : run.status === 'completed' ? 'done' as const
      : run.status === 'failed' || run.status === 'cancelled' ? 'failed' as const
      : 'idle' as const

    return {
      title: 'Autopilot',
      subtitle: run ? bubble(run.goal, 70) : 'Describe an outcome to staff the floor.',
      running: !!run && ACTIVE.includes(run.status),
      agents: [
        ...desks,
        {
          id: 'planner',
          name: 'Planner',
          role: run ? `${run.stepsUsed}/${run.maxSteps} steps` : 'idle',
          status: plannerStatus,
          detail: run?.status === 'planning'
            ? 'Working out the steps…'
            : bubble(run?.errorMessage || run?.planSummary),
          output: run?.result || run?.planSummary || undefined,
          boss: true,
        },
      ],
      messages,
      events: [
        ...tasks.filter(t => t.status === 'done').map(t => ({ id: `ev-${t.id}`, text: `${t.title} finished.`, tone: 'good' as const })),
        ...tasks.filter(t => t.status === 'failed').map(t => ({ id: `er-${t.id}`, text: `${t.title} failed: ${t.errorMessage || 'no detail'}`, tone: 'bad' as const })),
        ...tasks.filter(t => t.status === 'waiting_approval').map(t => ({ id: `wa-${t.id}`, text: `${t.title} is waiting on your approval.`, tone: 'warn' as const })),
        ...(run?.result ? [{ id: 'result', text: 'Run complete — result on the planner’s desk.', tone: 'good' as const }] : []),
      ],
    }
  }, [run, decide])

  const waiting = run?.tasks.filter(t => t.status === 'waiting_approval') ?? []

  return (
    <div className="orc-page">
      <div className="orc-hero">
        <h1><PhosphorIcon className="bi bi-compass" /> Autopilot</h1>
        <p>Describe an outcome. It plans the work, runs the steps it can in parallel, and asks before anything touches the outside world.</p>
      </div>

      <div className="orc-compose">
        <textarea
          value={goal}
          onChange={e => setGoal(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) start() }}
          rows={3}
          placeholder="e.g. Research how our top 3 competitors price their API, then write a one-page comparison I can send the team"
        />
        <button className="orc-btn orc-btn-primary" onClick={start} disabled={starting || !goal.trim()}>
          {starting ? <><span className="orc-spin" /> Planning…</> : <><PhosphorIcon className="bi bi-play-fill" /> Run it</>}
        </button>
      </div>

      {error && <div className="orc-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      <div className="orc-body">
        <aside className="orc-runs">
          <h3>Runs</h3>
          {runs.length === 0 ? (
            <p className="orc-muted">Nothing yet.</p>
          ) : runs.map(r => (
            <button
              key={r.id}
              className={`orc-run-item${run?.id === r.id ? ' active' : ''}`}
              onClick={() => { setOpenTask(null); loadRun(r.id) }}
            >
              <span className={`orc-dot ${r.status}`} />
              <span className="orc-run-goal">{r.goal}</span>
              <small>{new Date(r.createdAt).toLocaleDateString()}</small>
            </button>
          ))}
        </aside>

        <section className="orc-detail">
          {!run ? (
            <div className="orc-placeholder">
              <PhosphorIcon className="bi bi-compass" />
              <p>Start a run, or pick one from the list to see its plan.</p>
            </div>
          ) : (
            <>
              <div className="orc-detail-head">
                <div>
                  <span className={`orc-status ${run.status}`}>{run.status.replace('_', ' ')}</span>
                  <h2>{run.goal}</h2>
                  {run.planSummary && <p className="orc-summary">{run.planSummary}</p>}
                </div>
                {ACTIVE.includes(run.status) && (
                  <button className="orc-btn" onClick={cancel}><PhosphorIcon className="bi bi-stop-circle" /> Cancel</button>
                )}
              </div>

              {run.status === 'planning' && (
                <div className="orc-planning"><span className="orc-spin" /> Working out the steps…</div>
              )}

              {run.errorMessage && <div className="orc-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {run.errorMessage}</div>}

              {waiting.length > 0 && (
                <div className="orc-approvals">
                  <h4><PhosphorIcon className="bi bi-hand-index-thumb" /> Waiting for you</h4>
                  {waiting.map(task => (
                    <div className="orc-approval" key={task.id}>
                      <div>
                        <strong>{task.title}</strong>
                        <small>{task.tool}</small>
                        <code>{task.arguments}</code>
                      </div>
                      <div className="orc-approval-actions">
                        <button className="orc-btn orc-btn-primary" onClick={() => decide(task, true)}>Approve</button>
                        <button className="orc-btn" onClick={() => decide(task, false)}>Skip</button>
                      </div>
                    </div>
                  ))}
                </div>
              )}

              <AgentStage
                {...stageProps}
                model={field}
                height={440}
                actions={ACTIVE.includes(run.status)
                  ? [{ id: 'cancel', label: 'Cancel run', icon: 'bi-stop-circle', tone: 'danger', onClick: cancel }]
                  : []}
              />

              {run.tasks.length > 0 && (
                <ol className="orc-tasks">
                  {run.tasks.map(task => (
                    <li key={task.id} className={`orc-task ${task.status}`}>
                      <button className="orc-task-head" onClick={() => setOpenTask(openTask === task.id ? null : task.id)}>
                        <PhosphorIcon className={`bi ${TASK_ICON[task.status]}`} />
                        <span className="orc-task-title">{task.title}</span>
                        <span className="orc-task-tool">{task.tool}</span>
                        {task.durationMs > 0 && <span className="orc-task-time">{(task.durationMs / 1000).toFixed(1)}s</span>}
                      </button>
                      {openTask === task.id && (
                        <div className="orc-task-body">
                          {task.errorMessage && <div className="orc-task-error">{task.errorMessage}</div>}
                          {task.output ? <pre>{task.output}</pre> : <p className="orc-muted">No output yet.</p>}
                        </div>
                      )}
                    </li>
                  ))}
                </ol>
              )}

              {run.result && (
                <div className="orc-result">
                  <h4><PhosphorIcon className="bi bi-file-earmark-check" /> Result</h4>
                  <pre>{run.result}</pre>
                </div>
              )}

              <p className="orc-meta">{run.stepsUsed} of {run.maxSteps} steps used</p>
            </>
          )}
        </section>
      </div>
    </div>
  )
}
