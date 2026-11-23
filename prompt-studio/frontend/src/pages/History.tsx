import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useLayoutEffect, useEffect } from 'react'
import { Link } from 'react-router-dom'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import api from '../services/api'
import '../App.css'

interface Execution {
  id: string; promptId?: string; promptName: string; model: string
  totalTokensUsed: number; rating?: number; createdAt: string; systemPrompt: string
}

export default function History() {
  const { setBreadcrumbs } = useLayoutControls()
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Run History' }])
    return () => setBreadcrumbs([])
  }, [])

  const [executions, setExecutions] = useState<Execution[]>([])
  const [loading, setLoading] = useState(true)
  const [page, setPage] = useState(1)
  const [hasMore, setHasMore] = useState(false)
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<Execution | null>(null)
  const [toast, setToast] = useState('')
  const PAGE_SIZE = 20

  useEffect(() => { fetchPage(1) }, [])

  const fetchPage = async (p: number) => {
    setLoading(true)
    try {
      const r = await api.get<{ executions: Execution[]; total: number }>('/executions', { params: { page: p, pageSize: PAGE_SIZE } })
      const rows = r.data.executions ?? []
      if (p === 1) setExecutions(rows)
      else setExecutions(prev => [...prev, ...rows])
      setHasMore(rows.length === PAGE_SIZE)
      setPage(p)
    } catch { setExecutions([]) }
    finally { setLoading(false) }
  }

  const handleDelete = async (id: string) => {
    try {
      await api.delete(`/executions/${id}`)
      setExecutions(prev => prev.filter(e => e.id !== id))
      setSelected(null)
      setToast('Deleted')
      setTimeout(() => setToast(''), 2000)
    } catch { setToast('Failed') }
  }

  const handleRate = async (id: string, rating: number) => {
    try {
      await api.post(`/executions/${id}/rate`, { rating })
      setExecutions(prev => prev.map(e => e.id === id ? { ...e, rating } : e))
    } catch { /* */ }
  }

  const filtered = executions.filter(e =>
    !search.trim() || e.promptName.toLowerCase().includes(search.toLowerCase())
  )

  const fmtDate = (s: string) => new Date(s).toLocaleString(undefined, {
    month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit'
  })

  const modelBadgeColor = (model: string) => {
    if (model.includes('gpt-5')) return '#10b981'
    if (model.includes('gpt-4')) return '#833ab4'
    if (model.includes('claude')) return '#f59e0b'
    return '#6b7280'
  }

  return (
    <div style={{ padding: '24px' }}>
      <div style={{ marginBottom: 20 }}>
        <h1 style={{ fontSize: 24, fontWeight: 700, marginBottom: 4 }}>
          <PhosphorIcon className="bi bi-clock-history" style={{ marginRight: 10, color: 'var(--primary)' }} />
          Run History
        </h1>
        <p style={{ color: 'var(--text-secondary)', fontSize: 14 }}>Every prompt execution, chain run, and AI call in one place</p>
      </div>

      {toast && (
        <div className="card" style={{ padding: '8px 16px', marginBottom: 12, background: 'var(--primary)', color: '#fff', fontSize: 13 }}>{toast}</div>
      )}

      <div style={{ display: 'flex', gap: 12, marginBottom: 16 }}>
        <div style={{ position: 'relative', flex: 1 }}>
          <PhosphorIcon className="bi bi-search" style={{ position: 'absolute', left: 10, top: '50%', transform: 'translateY(-50%)', color: 'var(--text-secondary)' }} />
          <input
            className="form-input"
            style={{ paddingLeft: 32 }}
            placeholder="Search by prompt name…"
            value={search}
            onChange={e => setSearch(e.target.value)}
          />
        </div>
        <button className="btn btn-secondary" onClick={() => fetchPage(1)}>
          <PhosphorIcon className="bi bi-arrow-clockwise" />
        </button>
      </div>

      <div style={{ display: 'flex', gap: 16 }}>
        {/* List */}
        <div style={{ flex: 1, minWidth: 0 }}>
          {loading && page === 1 ? (
            <div className="card" style={{ padding: 40, textAlign: 'center', color: 'var(--text-secondary)' }}>
              <PhosphorIcon className="bi bi-hourglass-split" style={{ fontSize: 24 }} /><p>Loading…</p>
            </div>
          ) : filtered.length === 0 ? (
            <div className="card" style={{ padding: 40, textAlign: 'center' }}>
              <PhosphorIcon className="bi bi-inbox" style={{ fontSize: 40, color: 'var(--text-secondary)' }} />
              <p style={{ color: 'var(--text-secondary)', marginTop: 12 }}>No runs found</p>
              {/* An empty state should offer the next step, not just report absence. */}
              <Link to="/create-prompt" className="btn btn-primary" style={{ marginTop: 6, display: 'inline-flex' }}>
                <PhosphorIcon className="bi bi-magic" /> Build your first prompt
              </Link>
            </div>
          ) : (
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
              {filtered.map(exec => (
                <div
                  key={exec.id}
                  className="card"
                  style={{
                    padding: '12px 16px', cursor: 'pointer',
                    border: selected?.id === exec.id ? '2px solid var(--primary)' : '1px solid var(--border)',
                    transition: 'border-color 0.15s'
                  }}
                  onClick={() => setSelected(exec)}
                >
                  <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', gap: 8 }}>
                    <div style={{ flex: 1, minWidth: 0 }}>
                      <p style={{ margin: 0, fontWeight: 600, fontSize: 14, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                        {exec.promptName}
                      </p>
                      <div style={{ display: 'flex', gap: 10, marginTop: 4, flexWrap: 'wrap', alignItems: 'center' }}>
                        <span style={{ fontSize: 11, padding: '1px 6px', borderRadius: 8, background: modelBadgeColor(exec.model) + '22', color: modelBadgeColor(exec.model), fontWeight: 600 }}>
                          {exec.model}
                        </span>
                        <span style={{ fontSize: 11, color: 'var(--text-secondary)' }}>
                          {exec.totalTokensUsed} tokens
                        </span>
                        <span style={{ fontSize: 11, color: 'var(--text-secondary)' }}>
                          {fmtDate(exec.createdAt)}
                        </span>
                        {exec.rating != null && (
                          <span style={{ fontSize: 11, color: exec.rating === 1 ? '#22c55e' : '#ef4444' }}>
                            {exec.rating === 1 ? '👍' : '👎'}
                          </span>
                        )}
                      </div>
                    </div>
                  </div>
                </div>
              ))}
              {hasMore && (
                <button className="btn btn-secondary" style={{ marginTop: 8 }}
                  onClick={() => fetchPage(page + 1)} disabled={loading}>
                  {loading ? 'Loading…' : 'Load more'}
                </button>
              )}
            </div>
          )}
        </div>

        {/* Detail panel */}
        {selected && (
          <div className="card" style={{ width: 340, padding: 16, alignSelf: 'flex-start', position: 'sticky', top: 80, flexShrink: 0 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: 12 }}>
              <h3 style={{ fontSize: 15, fontWeight: 600, margin: 0, flex: 1 }}>{selected.promptName}</h3>
              <button className="btn btn-secondary" style={{ fontSize: 12, padding: '2px 6px' }} onClick={() => setSelected(null)}>
                <PhosphorIcon className="bi bi-x" />
              </button>
            </div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10, fontSize: 13 }}>
              <div>
                <span style={{ color: 'var(--text-secondary)' }}>Model</span>
                <p style={{ margin: '2px 0', fontWeight: 600 }}>{selected.model}</p>
              </div>
              <div>
                <span style={{ color: 'var(--text-secondary)' }}>Tokens Used</span>
                <p style={{ margin: '2px 0', fontWeight: 600 }}>{selected.totalTokensUsed.toLocaleString()}</p>
              </div>
              <div>
                <span style={{ color: 'var(--text-secondary)' }}>Run at</span>
                <p style={{ margin: '2px 0' }}>{new Date(selected.createdAt).toLocaleString()}</p>
              </div>
              <div>
                <span style={{ color: 'var(--text-secondary)' }}>System Prompt (preview)</span>
                <pre style={{ fontSize: 11, background: 'var(--bg-secondary)', padding: 8, borderRadius: 4, whiteSpace: 'pre-wrap', maxHeight: 120, overflow: 'auto', marginTop: 4 }}>
                  {selected.systemPrompt?.slice(0, 300)}{(selected.systemPrompt?.length ?? 0) > 300 ? '…' : ''}
                </pre>
              </div>
              <div>
                <span style={{ color: 'var(--text-secondary)', fontSize: 12 }}>Rate this run:</span>
                <div style={{ display: 'flex', gap: 8, marginTop: 4 }}>
                  <button className="btn btn-secondary" style={{ fontSize: 18, padding: '4px 12px', background: selected.rating === 1 ? '#22c55e22' : undefined }}
                    onClick={() => handleRate(selected.id, 1)}>👍</button>
                  <button className="btn btn-secondary" style={{ fontSize: 18, padding: '4px 12px', background: selected.rating === -1 ? '#ef444422' : undefined }}
                    onClick={() => handleRate(selected.id, -1)}>👎</button>
                </div>
              </div>
              <button className="btn btn-secondary" style={{ color: '#ef4444', marginTop: 4 }}
                onClick={() => handleDelete(selected.id)}>
                <PhosphorIcon className="bi bi-trash" style={{ marginRight: 6 }} />Delete run
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
