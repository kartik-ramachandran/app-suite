import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useLayoutEffect, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import api from '../services/api'
import '../App.css'

interface FollowUp {
  id: number
  workspaceId: number
  meetingTitle: string
  meetingDate: string
  description: string
  assignedTo: string
  dueDate: string | null
  status: 'open' | 'in_progress' | 'done'
  priority: 'high' | 'medium' | 'low'
}

const PRIORITY_COLOR: Record<string, string> = {
  high: '#ef4444',
  medium: '#f59e0b',
  low: '#22c55e',
}

const STATUS_LABEL: Record<string, string> = {
  open: 'Open',
  in_progress: 'In Progress',
  done: 'Done',
}

const STATUS_COLOR: Record<string, string> = {
  open: '#833ab4',
  in_progress: '#f59e0b',
  done: '#22c55e',
}

export default function FollowUpHub() {
  const { setBreadcrumbs } = useLayoutControls()
  const navigate = useNavigate()

  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Calendar', path: '/calendar' }, { label: 'Follow-up Hub' }])
    return () => setBreadcrumbs([])
  }, [])

  const [filter, setFilter] = useState<'all' | 'open' | 'overdue' | 'done'>('open')
  const [followUps, setFollowUps] = useState<FollowUp[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [updatingId, setUpdatingId] = useState<number | null>(null)

  useEffect(() => {
    loadFollowUps()
  }, [])

  const loadFollowUps = async () => {
    setLoading(true)
    setError('')
    try {
      const res = await api.get('/meetings/follow-ups')
      setFollowUps(res.data)
    } catch (e: any) {
      setError(e.response?.data?.message ?? 'Failed to load follow-ups')
    } finally {
      setLoading(false)
    }
  }

  const updateStatus = async (id: number, status: FollowUp['status']) => {
    setUpdatingId(id)
    try {
      await api.patch(`/meetings/follow-ups/${id}`, { status })
      setFollowUps(prev => prev.map(f => f.id === id ? { ...f, status } : f))
    } catch { /* ignore */ }
    finally { setUpdatingId(null) }
  }

  const now = new Date()

  const filtered = followUps.filter(f => {
    if (filter === 'open') return f.status !== 'done'
    if (filter === 'overdue') {
      if (f.status === 'done') return false
      if (!f.dueDate) return false
      return new Date(f.dueDate) < now
    }
    if (filter === 'done') return f.status === 'done'
    return true
  })

  const grouped = filtered.reduce<Record<string, FollowUp[]>>((acc, f) => {
    const key = f.meetingTitle || 'Unknown Meeting'
    ;(acc[key] = acc[key] ?? []).push(f)
    return acc
  }, {})

  const openCount = followUps.filter(f => f.status !== 'done').length
  const overdueCount = followUps.filter(f => f.status !== 'done' && f.dueDate && new Date(f.dueDate) < now).length
  const doneCount = followUps.filter(f => f.status === 'done').length

  return (
    <div style={{ padding: '24px', maxWidth: 860, margin: '0 auto' }}>

      {/* Header */}
      <div style={{ marginBottom: 20 }}>
        <h1 style={{ fontSize: 22, fontWeight: 700, margin: '0 0 4px', display: 'flex', alignItems: 'center', gap: 8 }}>
          <PhosphorIcon className="bi bi-arrow-repeat" style={{ color: 'var(--primary)' }} />Follow-up Hub
        </h1>
        <p style={{ color: 'var(--text-secondary)', fontSize: 13, margin: 0 }}>
          Track action items and follow-ups across all your meetings.
        </p>
      </div>

      {/* Stats row */}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 12, marginBottom: 20 }}>
        {[
          { label: 'Open', value: openCount, color: '#833ab4', icon: 'bi-circle' },
          { label: 'Overdue', value: overdueCount, color: '#ef4444', icon: 'bi-exclamation-circle' },
          { label: 'Completed', value: doneCount, color: '#22c55e', icon: 'bi-check-circle' },
        ].map(stat => (
          <div key={stat.label} className="card" style={{ padding: '14px 16px', display: 'flex', alignItems: 'center', gap: 12 }}>
            <div style={{ width: 36, height: 36, borderRadius: 10, background: `${stat.color}15`, display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
              <PhosphorIcon className={`bi ${stat.icon}`} style={{ color: stat.color, fontSize: 16 }} />
            </div>
            <div>
              <div style={{ fontSize: 22, fontWeight: 700, lineHeight: 1 }}>{stat.value}</div>
              <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 2 }}>{stat.label}</div>
            </div>
          </div>
        ))}
      </div>

      {/* Filter tabs */}
      <div style={{ display: 'flex', gap: 4, marginBottom: 20, background: 'var(--bg-secondary)', borderRadius: 10, padding: 4, width: 'fit-content' }}>
        {(['open', 'overdue', 'done', 'all'] as const).map(f => (
          <button key={f} onClick={() => setFilter(f)} style={{
            padding: '7px 16px', fontSize: 12, fontWeight: 600, borderRadius: 7, border: 'none', cursor: 'pointer',
            background: filter === f ? 'var(--bg-primary)' : 'transparent',
            color: filter === f ? 'var(--text-primary)' : 'var(--text-muted)',
            boxShadow: filter === f ? '0 1px 4px rgba(0,0,0,0.1)' : 'none',
            textTransform: 'capitalize',
          }}>
            {f}
            {f === 'overdue' && overdueCount > 0 && (
              <span style={{ marginLeft: 5, background: '#ef4444', color: '#fff', borderRadius: 10, padding: '0 5px', fontSize: 10, fontWeight: 700 }}>{overdueCount}</span>
            )}
          </button>
        ))}
      </div>

      {error && (
        <div style={{ padding: '8px 12px', background: '#ef444410', border: '1px solid #ef444430', borderRadius: 8, color: '#ef4444', fontSize: 13, marginBottom: 16 }}>
          <PhosphorIcon className="bi bi-exclamation-circle" style={{ marginRight: 6 }} />{error}
        </div>
      )}

      {loading ? (
        <div style={{ display: 'flex', alignItems: 'center', gap: 10, color: 'var(--text-muted)', padding: '40px 0', justifyContent: 'center' }}>
          <div className="spinner" />Loading follow-ups…
        </div>
      ) : filtered.length === 0 ? (
        <div style={{ textAlign: 'center', padding: '60px 0', color: 'var(--text-muted)' }}>
          <PhosphorIcon className="bi bi-check2-all" style={{ fontSize: 36, display: 'block', marginBottom: 12, color: 'var(--primary)' }} />
          <div style={{ fontWeight: 600, marginBottom: 4 }}>
            {filter === 'done' ? 'No completed follow-ups yet' : filter === 'overdue' ? 'Nothing overdue' : 'All caught up!'}
          </div>
          <div style={{ fontSize: 13 }}>
            {filter === 'open' ? 'Follow-ups from meetings will appear here.' : 'Switch filter to see other items.'}
          </div>
        </div>
      ) : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
          {Object.entries(grouped).map(([meetingTitle, items]) => (
            <div key={meetingTitle}>
              <div style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', letterSpacing: 0.8, marginBottom: 8, display: 'flex', alignItems: 'center', gap: 8 }}>
                <PhosphorIcon className="bi bi-camera-video" style={{ fontSize: 12 }} />
                {meetingTitle.toUpperCase()}
                {items[0]?.workspaceId && (
                  <button
                    onClick={() => navigate(`/calendar/meeting/${items[0].workspaceId}`)}
                    style={{ marginLeft: 'auto', fontSize: 10, fontWeight: 600, color: 'var(--primary)', background: 'none', border: 'none', cursor: 'pointer', padding: 0 }}
                  >
                    Open workspace <PhosphorIcon className="bi bi-arrow-right" />
                  </button>
                )}
              </div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                {items.map(item => <FollowUpCard key={item.id} item={item} updatingId={updatingId} onStatusChange={updateStatus} />)}
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

function FollowUpCard({ item, updatingId, onStatusChange }: {
  item: FollowUp
  updatingId: number | null
  onStatusChange: (id: number, status: FollowUp['status']) => void
}) {
  const now = new Date()
  const isOverdue = item.status !== 'done' && item.dueDate && new Date(item.dueDate) < now
  const isDone = item.status === 'done'

  return (
    <div className="card" style={{
      padding: '12px 16px',
      display: 'flex',
      alignItems: 'flex-start',
      gap: 12,
      opacity: isDone ? 0.65 : 1,
      borderLeft: isOverdue ? '3px solid #ef4444' : undefined,
    }}>
      {/* Status toggle */}
      <button
        onClick={() => onStatusChange(item.id, isDone ? 'open' : 'done')}
        disabled={updatingId === item.id}
        style={{
          width: 22, height: 22, borderRadius: '50%', border: `2px solid ${isDone ? '#22c55e' : 'var(--border)'}`,
          background: isDone ? '#22c55e' : 'transparent', cursor: 'pointer',
          display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0, marginTop: 2,
        }}
      >
        {isDone && <PhosphorIcon className="bi bi-check" style={{ color: '#fff', fontSize: 11 }} />}
        {updatingId === item.id && <div className="spinner" style={{ width: 10, height: 10, borderWidth: 1 }} />}
      </button>

      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 4, textDecoration: isDone ? 'line-through' : 'none', color: isDone ? 'var(--text-muted)' : 'var(--text-primary)' }}>
          {item.description}
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
          {/* Priority badge */}
          <span style={{ fontSize: 10, fontWeight: 700, color: PRIORITY_COLOR[item.priority], background: `${PRIORITY_COLOR[item.priority]}15`, borderRadius: 4, padding: '1px 6px' }}>
            {item.priority.toUpperCase()}
          </span>

          {/* Status */}
          <span style={{ fontSize: 10, fontWeight: 600, color: STATUS_COLOR[item.status] }}>
            {STATUS_LABEL[item.status]}
          </span>

          {/* Assignee */}
          {item.assignedTo && (
            <span style={{ fontSize: 11, color: 'var(--text-muted)', display: 'flex', alignItems: 'center', gap: 3 }}>
              <PhosphorIcon className="bi bi-person" style={{ fontSize: 10 }} />{item.assignedTo}
            </span>
          )}

          {/* Due date */}
          {item.dueDate && (
            <span style={{ fontSize: 11, color: isOverdue ? '#ef4444' : 'var(--text-muted)', display: 'flex', alignItems: 'center', gap: 3, fontWeight: isOverdue ? 600 : 400 }}>
              <PhosphorIcon className="bi bi-calendar3" style={{ fontSize: 10 }} />
              {isOverdue ? 'Overdue · ' : ''}
              {new Date(item.dueDate).toLocaleDateString('en-US', { month: 'short', day: 'numeric' })}
            </span>
          )}
        </div>
      </div>

      {/* Status selector */}
      {!isDone && (
        <select
          value={item.status}
          onChange={e => onStatusChange(item.id, e.target.value as FollowUp['status'])}
          disabled={updatingId === item.id}
          style={{ fontSize: 11, padding: '3px 6px', borderRadius: 6, border: '1px solid var(--border)', background: 'var(--bg-secondary)', color: 'var(--text-primary)', cursor: 'pointer', flexShrink: 0 }}
        >
          <option value="open">Open</option>
          <option value="in_progress">In Progress</option>
          <option value="done">Done</option>
        </select>
      )}
    </div>
  )
}
