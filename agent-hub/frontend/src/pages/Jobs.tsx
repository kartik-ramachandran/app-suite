import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useLayoutEffect, useEffect, useMemo } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import PromptPicker from '../components/PromptPicker'
import ConfirmDialog from '../components/ConfirmDialog'
import api from '../services/api'
import '../App.css'

interface Job {
  id: number; name: string; agentType: string; chainId?: string; schedule: string
  hour: number; dayOfWeek: number; nextRunAt: string | null; lastRunAt: string | null
  lastRunStatus: string | null; deliverToEmail: string | null; isActive: boolean; input: string
}
interface JobRun { id: number; status: string; resultText: string | null; errorMessage: string | null; createdAt: string }
interface Chain { id: string; name: string }
interface Agent { id: number; name: string }
// Execution-log entries written by any module via the job-log/notifications layer.
interface ModuleJobLog {
  id: number; module: string; jobType: string; title: string; status: string
  errorMessage: string | null; durationMs: number; createdAt: string; completedAt: string | null
}

export default function Jobs() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('jobs')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Jobs' }])
    return () => setBreadcrumbs([])
  }, [])

  const [tab, setTab] = useState<'scheduled' | 'activity'>('scheduled')
  const [activity, setActivity] = useState<ModuleJobLog[]>([])
  const [activityLoading, setActivityLoading] = useState(false)
  const [jobs, setJobs] = useState<Job[]>([])
  const [chains, setChains] = useState<Chain[]>([])
  const [agents, setAgents] = useState<Agent[]>([])
  const [loading, setLoading] = useState(true)
  const [showForm, setShowForm] = useState(false)
  const [editJob, setEditJob] = useState<Job | null>(null)
  const [runsJobId, setRunsJobId] = useState<number | null>(null)
  const [runs, setRuns] = useState<JobRun[]>([])
  const [triggeringId, setTriggeringId] = useState<number | null>(null)
  const [toast, setToast] = useState('')
  const [deleteTarget, setDeleteTarget] = useState<Job | null>(null)
  const [form, setForm] = useState({
    name: '', agentType: 'prompt', chainId: '', agentId: '', input: '',
    schedule: 'daily', hour: '9', dayOfWeek: '1', deliverToEmail: ''
  })

  useEffect(() => {
    fetchJobs()
    api.get<Chain[]>('/chains').then(r => setChains(r.data)).catch(() => {})
    api.get<Agent[]>('/agents').then(r => setAgents(r.data)).catch(() => {})
  }, [])

  const fetchJobs = async () => {
    setLoading(true)
    try { const r = await api.get<Job[]>('/jobs'); setJobs(r.data) } catch { setJobs([]) }
    finally { setLoading(false) }
  }

  const fetchActivity = async () => {
    setActivityLoading(true)
    try {
      const r = await api.get<{ items: ModuleJobLog[]; unread: number }>('/notifications?take=100')
      setActivity(r.data.items ?? [])
    } catch { setActivity([]) }
    finally { setActivityLoading(false) }
  }

  useEffect(() => { if (tab === 'activity') fetchActivity() }, [tab])

  const showToast = (msg: string) => { setToast(msg); setTimeout(() => setToast(''), 3000) }

  const openCreate = () => {
    setEditJob(null)
    setForm({ name: '', agentType: 'prompt', chainId: '', agentId: '', input: '', schedule: 'daily', hour: '9', dayOfWeek: '1', deliverToEmail: '' })
    api.get<Agent[]>('/agents').then(r => setAgents(r.data)).catch(() => {})
    api.get<Chain[]>('/chains').then(r => setChains(r.data)).catch(() => {})
    setShowForm(true)
  }

  const openEdit = (job: Job) => {
    setEditJob(job)
    api.get<Agent[]>('/agents').then(r => setAgents(r.data)).catch(() => {})
    api.get<Chain[]>('/chains').then(r => setChains(r.data)).catch(() => {})
    const isAgent = job.agentType === 'agent'
    const agentId = isAgent ? (job.input.split(':')[0] ?? '') : ''
    const agentInput = isAgent && job.input.includes(':') ? job.input.slice(job.input.indexOf(':') + 1).trim() : job.input
    setForm({
      name: job.name, agentType: job.agentType, chainId: job.chainId?.toString() ?? '',
      agentId, input: agentInput, schedule: job.schedule, hour: job.hour.toString(),
      dayOfWeek: job.dayOfWeek.toString(), deliverToEmail: job.deliverToEmail ?? ''
    })
    setShowForm(true)
  }

  const handleSave = async () => {
    if (!form.name.trim()) return
    const encodedInput = form.agentType === 'agent' && form.agentId
      ? `${form.agentId}:${form.input}`
      : form.input
    const payload = {
      name: form.name, agentType: form.agentType,
      chainId: form.chainId || undefined,
      input: encodedInput, schedule: form.schedule,
      hour: parseInt(form.hour), dayOfWeek: parseInt(form.dayOfWeek),
      deliverToEmail: form.deliverToEmail || undefined
    }
    try {
      if (editJob) { await api.put(`/jobs/${editJob.id}`, payload) }
      else { await api.post('/jobs', payload) }
      showToast(editJob ? 'Job updated' : 'Job created')
      setShowForm(false)
      fetchJobs()
    } catch { showToast('Failed to save job') }
  }

  const handleDelete = async () => {
    if (!deleteTarget) return
    try {
      await api.delete(`/jobs/${deleteTarget.id}`)
      setDeleteTarget(null)
      fetchJobs()
      showToast('Job deleted')
    } catch { showToast('Failed') }
  }

  const handleTrigger = async (id: number) => {
    setTriggeringId(id)
    try {
      const r = await api.post<{ status: string; result: string }>(`/jobs/${id}/trigger`)
      showToast(`Job ran: ${r.data.result.slice(0, 80)}…`)
      fetchJobs()
    } catch { showToast('Failed to trigger job') }
    finally { setTriggeringId(null) }
  }

  const viewRuns = async (id: number) => {
    setRunsJobId(id)
    const r = await api.get<JobRun[]>(`/jobs/${id}/runs`)
    setRuns(r.data)
  }

  const fmtDate = (s: string | null) => s ? new Date(s.endsWith('Z') || s.includes('+') ? s : s + 'Z').toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : '—'
  const fmtDuration = (ms: number) => ms <= 0 ? '—' : ms < 1000 ? `${ms}ms` : ms < 60000 ? `${(ms / 1000).toFixed(1)}s` : `${Math.floor(ms / 60000)}m ${Math.round((ms % 60000) / 1000)}s`
  const statusColor = (s: string) => s === 'completed' || s === 'success' ? '#22c55e' : s === 'failed' ? '#ef4444' : '#6b7280'
  const scheduleLabel = (j: Job) => {
    const days = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']
    if (j.schedule === 'daily') return `Daily at ${j.hour}:00`
    if (j.schedule === 'weekly') return `Weekly ${days[(j.dayOfWeek - 1) % 7]} at ${j.hour}:00`
    if (j.schedule === 'monthly') return `Monthly on day ${j.dayOfWeek} at ${j.hour}:00`
    return 'Once'
  }

  // Gamefield: the night shift — one desk per scheduled job, scheduler in the corner.
  const field = useMemo<FieldModel>(() => {
    const desks = jobs.map(j => ({
      id: `job-${j.id}`,
      name: j.name,
      role: `${j.agentType} · ${j.schedule || 'no schedule'}`,
      status: (triggeringId === j.id ? 'working'
        : !j.isActive ? 'idle'
        : /fail|error/i.test(j.lastRunStatus || '') ? 'failed'
        : j.lastRunAt ? 'done'
        : 'idle') as 'working' | 'idle' | 'failed' | 'done',
      detail: triggeringId === j.id
        ? 'Running it now…'
        : j.nextRunAt ? `Next run ${new Date(j.nextRunAt).toLocaleString()}` : undefined,
    }))

    return {
      title: 'Jobs',
      subtitle: `${jobs.length} scheduled`,
      running: triggeringId != null,
      agents: [
        ...desks,
        {
          id: 'scheduler',
          name: 'Scheduler',
          role: `${activity.length} recent runs`,
          status: (triggeringId != null ? 'working' : 'idle') as 'working' | 'idle',
          detail: triggeringId != null ? 'Kicking off a run…' : undefined,
          boss: true,
        },
      ],
      messages: [],
      events: activity.slice(0, 20).map(a => ({
        id: `act-${a.id}`,
        text: bubble(`${a.module} · ${a.title} — ${a.status}`, 90) || 'run',
        tone: (/fail|error/i.test(a.status) ? 'bad' : 'good') as 'bad' | 'good',
      })),
    }
  }, [jobs, activity, triggeringId])

  return (
    <div style={{ padding: '24px' }}>
      <ConfirmDialog
        open={!!deleteTarget}
        title="Delete job?"
        message={`This will permanently delete "${deleteTarget?.name ?? 'this job'}".`}
        confirmLabel="Delete"
        onConfirm={handleDelete}
        onCancel={() => setDeleteTarget(null)}
      />
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
        <div>
          <h1 style={{ fontSize: 24, fontWeight: 700, margin: 0 }}>
            <PhosphorIcon className="bi bi-calendar-check" style={{ marginRight: 10, color: 'var(--primary)' }} />
            Jobs
          </h1>
          <p style={{ color: 'var(--text-secondary)', fontSize: 14, marginTop: 4 }}>
            {tab === 'scheduled'
              ? 'Automate prompt runs and get results delivered by email'
              : 'Every job executed across modules — with status, duration and errors'}
          </p>
        </div>
        {tab === 'scheduled' && (
          <button className="btn btn-primary" onClick={openCreate}>
            <PhosphorIcon className="bi bi-plus-lg" style={{ marginRight: 6 }} />New Job
          </button>
        )}
      </div>

      {/* Tabs */}
      <div style={{ display: 'flex', gap: 4, marginBottom: 20, borderBottom: '1px solid var(--border)' }}>
        {([['scheduled', 'Scheduled Jobs', 'bi-calendar-check'], ['activity', 'Activity Log', 'bi-list-check']] as const).map(([key, label, icon]) => (
          <button
            key={key}
            onClick={() => setTab(key)}
            style={{
              border: 'none', background: 'none', cursor: 'pointer', padding: '8px 14px',
              fontSize: 14, fontWeight: 600,
              color: tab === key ? 'var(--primary)' : 'var(--text-secondary)',
              borderBottom: tab === key ? '2px solid var(--primary)' : '2px solid transparent',
              marginBottom: -1
            }}
          >
            <PhosphorIcon className={`bi ${icon}`} style={{ marginRight: 6 }} />{label}
          </button>
        ))}
        {tab === 'activity' && (
          <button className="btn btn-secondary" style={{ marginLeft: 'auto', fontSize: 13 }} onClick={fetchActivity}>
            <PhosphorIcon className="bi bi-arrow-clockwise" style={{ marginRight: 4 }} />Refresh
          </button>
        )}
      </div>

      {toast && (
        <div className="card" style={{ padding: '10px 16px', marginBottom: 16, background: 'var(--primary)', color: '#fff', fontSize: 14 }}>
          {toast}
        </div>
      )}

      {tab === 'scheduled' && <AgentStage {...stageProps} model={field} />}

      {tab === 'scheduled' && (loading ? (
        <div className="card" style={{ padding: 40, textAlign: 'center', color: 'var(--text-secondary)' }}>
          <PhosphorIcon className="bi bi-hourglass-split" style={{ fontSize: 24 }} />
          <p>Loading jobs…</p>
        </div>
      ) : jobs.length === 0 ? (
        <div className="card" style={{ padding: 40, textAlign: 'center' }}>
          <PhosphorIcon className="bi bi-calendar-x" style={{ fontSize: 48, color: 'var(--text-secondary)' }} />
          <h3 style={{ marginTop: 12 }}>No scheduled jobs yet</h3>
          <p style={{ color: 'var(--text-secondary)', marginBottom: 16 }}>Schedule prompts or chains to run automatically</p>
          <button className="btn btn-primary" onClick={openCreate}>Create your first job</button>
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          {jobs.map(job => (
            <div key={job.id} className="card" style={{ padding: 16 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', flexWrap: 'wrap', gap: 8 }}>
                <div style={{ flex: 1, minWidth: 200 }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 10, marginBottom: 6 }}>
                    <span style={{ fontSize: 16, fontWeight: 600 }}>{job.name}</span>
                    <span style={{
                      fontSize: 11, padding: '2px 8px', borderRadius: 12, fontWeight: 600,
                      background: job.isActive ? '#22c55e22' : '#6b728022',
                      color: job.isActive ? '#22c55e' : '#6b7280'
                    }}>{job.isActive ? 'Active' : 'Paused'}</span>
                    {job.lastRunStatus && (
                      <span style={{
                        fontSize: 11, padding: '2px 8px', borderRadius: 12,
                        background: job.lastRunStatus === 'success' ? '#22c55e22' : '#ef444422',
                        color: job.lastRunStatus === 'success' ? '#22c55e' : '#ef4444'
                      }}>{job.lastRunStatus}</span>
                    )}
                  </div>
                  <div style={{ display: 'flex', gap: 16, fontSize: 13, color: 'var(--text-secondary)', flexWrap: 'wrap' }}>
                    <span><PhosphorIcon className="bi bi-clock" style={{ marginRight: 4 }} />{scheduleLabel(job)}</span>
                    <span><PhosphorIcon className="bi bi-arrow-clockwise" style={{ marginRight: 4 }} />Next: {fmtDate(job.nextRunAt)}</span>
                    <span><PhosphorIcon className="bi bi-check2-circle" style={{ marginRight: 4 }} />Last: {fmtDate(job.lastRunAt)}</span>
                    {job.deliverToEmail && <span><PhosphorIcon className="bi bi-envelope" style={{ marginRight: 4 }} />{job.deliverToEmail}</span>}
                  </div>
                </div>
                <div style={{ display: 'flex', gap: 8 }}>
                  <button className="btn btn-secondary" style={{ fontSize: 13 }}
                    onClick={() => viewRuns(job.id)}>
                    <PhosphorIcon className="bi bi-list-ul" style={{ marginRight: 4 }} />Runs
                  </button>
                  <button className="btn btn-secondary" style={{ fontSize: 13 }}
                    onClick={() => handleTrigger(job.id)}
                    disabled={triggeringId === job.id}>
                    {triggeringId === job.id
                      ? <><PhosphorIcon className="bi bi-hourglass-split" style={{ marginRight: 4 }} />Running…</>
                      : <><PhosphorIcon className="bi bi-play-fill" style={{ marginRight: 4 }} />Run Now</>}
                  </button>
                  <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => openEdit(job)}>
                    <PhosphorIcon className="bi bi-pencil" />
                  </button>
                  <button className="btn btn-secondary" style={{ fontSize: 13, color: '#ef4444' }}
                    onClick={() => setDeleteTarget(job)}>
                    <PhosphorIcon className="bi bi-trash" />
                  </button>
                </div>
              </div>
            </div>
          ))}
        </div>
      ))}

      {/* Activity Log tab — module job executions */}
      {tab === 'activity' && (activityLoading ? (
        <div className="card" style={{ padding: 40, textAlign: 'center', color: 'var(--text-secondary)' }}>
          <PhosphorIcon className="bi bi-hourglass-split" style={{ fontSize: 24 }} />
          <p>Loading activity…</p>
        </div>
      ) : activity.length === 0 ? (
        <div className="card" style={{ padding: 40, textAlign: 'center' }}>
          <PhosphorIcon className="bi bi-list-check" style={{ fontSize: 48, color: 'var(--text-secondary)' }} />
          <h3 style={{ marginTop: 12 }}>No activity yet</h3>
          <p style={{ color: 'var(--text-secondary)' }}>Jobs run by modules (e.g. Signal, Prism) will appear here.</p>
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {activity.map(a => (
            <div key={a.id} className="card" style={{ padding: 14, display: 'flex', alignItems: 'flex-start', gap: 12 }}>
              <PhosphorIcon className={`bi ${a.status === 'completed' ? 'bi-check-circle-fill' : a.status === 'failed' ? 'bi-x-circle-fill' : 'bi-arrow-repeat'}`}
                style={{ fontSize: 18, color: statusColor(a.status), marginTop: 2 }} />
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontSize: 15, fontWeight: 600, marginBottom: 4 }}>{a.title}</div>
                <div style={{ display: 'flex', gap: 14, flexWrap: 'wrap', fontSize: 13, color: 'var(--text-secondary)' }}>
                  <span style={{ color: 'var(--primary)', fontWeight: 600 }}>{a.module}</span>
                  <span><PhosphorIcon className="bi bi-tag" style={{ marginRight: 4 }} />{a.jobType}</span>
                  <span style={{ color: statusColor(a.status), fontWeight: 600, textTransform: 'capitalize' }}>{a.status}</span>
                  <span><PhosphorIcon className="bi bi-stopwatch" style={{ marginRight: 4 }} />{fmtDuration(a.durationMs)}</span>
                  <span><PhosphorIcon className="bi bi-clock" style={{ marginRight: 4 }} />{fmtDate(a.completedAt || a.createdAt)}</span>
                </div>
                {a.errorMessage && (
                  <p style={{ fontSize: 12, color: '#ef4444', margin: '6px 0 0' }}>{a.errorMessage}</p>
                )}
              </div>
            </div>
          ))}
        </div>
      ))}

      {/* Create/Edit form modal */}
      {showForm && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.5)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1000, padding: 16 }}>
          <div className="card" style={{ width: '100%', maxWidth: 500, padding: 24, maxHeight: '90vh', overflowY: 'auto' }}>
            <h2 style={{ fontSize: 18, fontWeight: 600, marginBottom: 20 }}>{editJob ? 'Edit Job' : 'New Scheduled Job'}</h2>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
              <div>
                <label className="form-label">Job Name</label>
                <input className="form-input" value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} placeholder="e.g. Daily market summary" />
              </div>
              <div>
                <label className="form-label">Agent Type</label>
                <select className="form-input" value={form.agentType} onChange={e => setForm(f => ({ ...f, agentType: e.target.value }))}>
                  <option value="prompt">Run a Prompt</option>
                  <option value="chain">Run a Chain</option>
                  <option value="agent">Run an Agent</option>
                </select>
              </div>
              {form.agentType === 'chain' ? (
                <div>
                  <label className="form-label">Select Chain</label>
                  <select className="form-input" value={form.chainId} onChange={e => setForm(f => ({ ...f, chainId: e.target.value }))}>
                    <option value="">— select chain —</option>
                    {chains.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
                  </select>
                </div>
              ) : form.agentType === 'agent' ? (
                <>
                  <div>
                    <label className="form-label">Select Agent</label>
                    <select className="form-input" value={form.agentId} onChange={e => setForm(f => ({ ...f, agentId: e.target.value }))}>
                      <option value="">— select agent —</option>
                      {agents.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}
                    </select>
                  </div>
                  <div>
                    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 4 }}>
                      <label className="form-label" style={{ margin: 0 }}>Task Input</label>
                      <PromptPicker onSelect={p => setForm(f => ({ ...f, input: p.content }))} />
                    </div>
                    <textarea className="form-textarea" rows={3} value={form.input} onChange={e => setForm(f => ({ ...f, input: e.target.value }))} placeholder="Enter the task or question for the agent" />
                  </div>
                </>
              ) : (
                <div>
                  <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 4 }}>
                    <label className="form-label" style={{ margin: 0 }}>Prompt / Input</label>
                    <PromptPicker onSelect={p => setForm(f => ({ ...f, input: p.content }))} />
                  </div>
                  <textarea className="form-textarea" rows={3} value={form.input} onChange={e => setForm(f => ({ ...f, input: e.target.value }))} placeholder="Enter the prompt or task description" />
                </div>
              )}
              <div>
                <label className="form-label">Schedule</label>
                <select className="form-input" value={form.schedule} onChange={e => setForm(f => ({ ...f, schedule: e.target.value }))}>
                  <option value="daily">Daily</option>
                  <option value="weekly">Weekly</option>
                  <option value="monthly">Monthly</option>
                </select>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12 }}>
                <div>
                  <label className="form-label">Hour (0-23 UTC)</label>
                  <input className="form-input" type="number" min={0} max={23} value={form.hour} onChange={e => setForm(f => ({ ...f, hour: e.target.value }))} />
                </div>
                {form.schedule !== 'daily' && (
                  <div>
                    <label className="form-label">{form.schedule === 'weekly' ? 'Day of Week (1=Mon)' : 'Day of Month'}</label>
                    <input className="form-input" type="number" min={1} max={form.schedule === 'weekly' ? 7 : 28} value={form.dayOfWeek} onChange={e => setForm(f => ({ ...f, dayOfWeek: e.target.value }))} />
                  </div>
                )}
              </div>
              <div>
                <label className="form-label">Deliver results to email (optional)</label>
                <input className="form-input" type="email" value={form.deliverToEmail} onChange={e => setForm(f => ({ ...f, deliverToEmail: e.target.value }))} placeholder="you@example.com" />
              </div>
            </div>
            <div style={{ display: 'flex', gap: 12, marginTop: 20 }}>
              <button className="btn btn-primary" onClick={handleSave} style={{ flex: 1, minHeight: 44 }}>
                {editJob ? 'Update Job' : 'Create Job'}
              </button>
              <button className="btn btn-secondary" onClick={() => setShowForm(false)} style={{ minHeight: 44, padding: '0 24px' }}>Cancel</button>
            </div>
          </div>
        </div>
      )}

      {/* Runs modal */}
      {runsJobId !== null && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.5)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 1000, padding: 16 }}>
          <div className="card" style={{ width: '100%', maxWidth: 600, padding: 24, maxHeight: '80vh', overflowY: 'auto' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 16 }}>
              <h2 style={{ fontSize: 18, fontWeight: 600 }}>Run History</h2>
              <button className="btn btn-secondary" onClick={() => setRunsJobId(null)}>Close</button>
            </div>
            {runs.length === 0 ? (
              <p style={{ color: 'var(--text-secondary)', textAlign: 'center', padding: 20 }}>No runs yet</p>
            ) : (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
                {runs.map(run => (
                  <div key={run.id} style={{ border: '1px solid var(--border)', borderRadius: 8, padding: 12 }}>
                    <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 8 }}>
                      <span style={{
                        fontSize: 12, padding: '2px 8px', borderRadius: 12, fontWeight: 600,
                        background: run.status === 'success' ? '#22c55e22' : run.status === 'failed' ? '#ef444422' : '#6b728022',
                        color: run.status === 'success' ? '#22c55e' : run.status === 'failed' ? '#ef4444' : '#6b7280'
                      }}>{run.status}</span>
                      <span style={{ fontSize: 12, color: 'var(--text-secondary)' }}>{fmtDate(run.createdAt)}</span>
                    </div>
                    {run.resultText && (
                      <pre style={{ fontSize: 12, background: 'var(--bg-secondary)', padding: 8, borderRadius: 4, whiteSpace: 'pre-wrap', maxHeight: 120, overflow: 'auto', margin: 0 }}>
                        {run.resultText.slice(0, 400)}{run.resultText.length > 400 ? '…' : ''}
                      </pre>
                    )}
                    {run.errorMessage && (
                      <p style={{ fontSize: 12, color: '#ef4444', margin: 0 }}>{run.errorMessage}</p>
                    )}
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
