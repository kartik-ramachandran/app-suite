import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useLayoutEffect, useMemo } from 'react'
import { useTranslation } from 'react-i18next'
import api from '../services/api'
import ConfirmDialog from '../components/ConfirmDialog'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import { usePageMeta } from '../utils/usePageMeta'
import '../App.css'

interface Evaluation {
  id: string
  name: string
  promptA: string
  promptB: string
  testInput?: string
  resultA?: string
  resultB?: string
  model: string
  tokensA: number
  tokensB: number
  latencyAMs: number
  latencyBMs: number
  winner?: string
  notes?: string
  status: string
  createdAt: string
}

const FALLBACK_MODELS = [
  'anthropic.claude-3-haiku-20240307-v1:0',
  'anthropic.claude-3-5-sonnet-20240620-v1:0',
  'anthropic.claude-3-5-sonnet-20241022-v2:0',
  'anthropic.claude-3-opus-20240229-v1:0',
]
interface ModelOption { provider: string; model: string }

export default function Evaluations() {
  const { t } = useTranslation()
  usePageMeta('Prompt Evaluations – A/B Testing | GenPrompt', 'Compare two prompts side-by-side and vote on which performs better.')

  const { setBreadcrumbs, setHeaderActions } = useLayoutControls()
  const { stageProps } = useFieldView('evaluations')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Evaluations' }])
    setHeaderActions(
      <button className="btn btn-primary btn-sm" onClick={() => setShowNew(true)}>
        <PhosphorIcon className="bi bi-plus-lg"></PhosphorIcon> {t('evaluations.createNew')}
      </button>
    )
    return () => { setBreadcrumbs([]); setHeaderActions(null) }
  }, [])

  const [evals, setEvals] = useState<Evaluation[]>([])
  const [loading, setLoading] = useState(true)
  const [showNew, setShowNew] = useState(false)
  const [selected, setSelected] = useState<Evaluation | null>(null)

  // New eval form
  const [name, setName] = useState('')
  const [promptA, setPromptA] = useState('')
  const [promptB, setPromptB] = useState('')
  const [testInput, setTestInput] = useState('')
  const [model, setModel] = useState('anthropic.claude-3-haiku-20240307-v1:0')
  const [availableModels, setAvailableModels] = useState<ModelOption[]>(
    FALLBACK_MODELS.map(m => ({ provider: 'AmazonBedrock', model: m }))
  )
  const [creating, setCreating] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [deleteTarget, setDeleteTarget] = useState<Evaluation | null>(null)

  // Vote
  const [voting, setVoting] = useState(false)
  const [voteNote, setVoteNote] = useState('')

  useEffect(() => {
    loadEvals()
    fetch('/api/prompts/models')
      .then(r => r.ok ? r.json() : null)
      .then((data: ModelOption[] | null) => { if (data?.length) setAvailableModels(data) })
      .catch(() => {})
  }, [])

  const loadEvals = async () => {
    try {
      setLoading(true)
      const res = await api.get('/evaluations')
      setEvals(res.data.evaluations)
    } catch { setError('Failed to load evaluations') }
    finally { setLoading(false) }
  }

  const handleCreate = async () => {
    if (!name.trim() || !promptA.trim() || !promptB.trim()) {
      setError('Name, Prompt A, and Prompt B are required')
      return
    }
    try {
      setCreating(true)
      setError(null)
      const res = await api.post('/evaluations', { name, promptA, promptB, testInput: testInput || undefined, model })
      setEvals(prev => [res.data, ...prev])
      setSelected(res.data)
      setShowNew(false)
      setName(''); setPromptA(''); setPromptB(''); setTestInput('')
    } catch { setError('Failed to create evaluation') }
    finally { setCreating(false) }
  }

  const handleVote = async (winner: string) => {
    if (!selected) return
    try {
      setVoting(true)
      const res = await api.post(`/evaluations/${selected.id}/vote`, { winner, notes: voteNote || undefined })
      setSelected(res.data)
      setEvals(prev => prev.map(e => e.id === res.data.id ? res.data : e))
      setVoteNote('')
    } catch { setError('Failed to submit vote') }
    finally { setVoting(false) }
  }

  const handleDelete = async () => {
    if (!deleteTarget) return
    await api.delete(`/evaluations/${deleteTarget.id}`)
    setEvals(prev => prev.filter(e => e.id !== deleteTarget.id))
    if (selected?.id === deleteTarget.id) setSelected(null)
    setDeleteTarget(null)
  }

  const handleRerun = async (e: React.MouseEvent, ev: Evaluation) => {
    e.stopPropagation()
    try {
      const res = await api.post('/evaluations', { name: ev.name, promptA: ev.promptA, promptB: ev.promptB, testInput: ev.testInput, model: ev.model })
      setEvals(prev => [res.data, ...prev])
      setSelected(res.data)
    } catch { setError('Failed to re-run evaluation') }
  }

  const formatTriggered = (dateStr: string) => {
    const d = new Date(dateStr)
    return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  }

  const winnerBadge = (winner?: string) => {
    if (!winner) return null
    const colors: Record<string, string> = { A: '#d62976', B: '#833ab4', Tie: '#f59e0b' }
    return <span style={{ background: colors[winner] + '22', color: colors[winner], border: `1px solid ${colors[winner]}44`, borderRadius: '12px', padding: '0.15rem 0.6rem', fontSize: '0.75rem', fontWeight: 600 }}>{winner === 'Tie' ? '🤝 Tie' : `Winner: ${winner}`}</span>
  }

  // Gamefield: prompt A and prompt B run the same brief; the judge calls it.
  const field = useMemo<FieldModel>(() => {
    const e = selected
    const side = (key: 'A' | 'B') => {
      const prompt = key === 'A' ? e?.promptA : e?.promptB
      const out = key === 'A' ? e?.resultA : e?.resultB
      const tokens = key === 'A' ? e?.tokensA : e?.tokensB
      const ms = key === 'A' ? e?.latencyAMs : e?.latencyBMs
      return {
        id: `prompt-${key}`,
        name: `Prompt ${key}`,
        role: e ? `${tokens ?? 0} tokens · ${((ms ?? 0) / 1000).toFixed(1)}s` : 'not run yet',
        status: (creating ? 'working' : out ? 'done' : 'idle') as 'working' | 'done' | 'idle',
        detail: creating ? 'Running the test input…' : bubble(out || prompt),
        output: out || prompt || undefined,
      }
    }

    return {
      title: 'Evaluations',
      subtitle: selected ? selected.name : `${evals.length} evaluation${evals.length === 1 ? '' : 's'} on file`,
      running: creating || voting,
      agents: [
        side('A'),
        side('B'),
        {
          id: 'judge',
          name: 'Judge',
          role: selected?.model || model,
          status: (error ? 'failed' : creating ? 'working' : selected?.winner ? 'done' : selected ? 'blocked' : 'idle') as 'failed' | 'working' | 'done' | 'blocked' | 'idle',
          detail: error ? bubble(error)
            : selected?.winner ? `Winner: prompt ${selected.winner}.`
            : selected ? 'Both sides in — pick a winner.'
            : undefined,
          output: selected?.notes || undefined,
          boss: true,
        },
      ],
      messages: selected?.resultA || selected?.resultB
        ? [
            { id: 'a-in', from: 'prompt-A', to: 'judge' },
            { id: 'b-in', from: 'prompt-B', to: 'judge' },
          ]
        : [],
      events: [
        ...(selected?.winner ? [{ id: 'win', text: `Prompt ${selected.winner} won this round.`, tone: 'good' as const }] : []),
        ...(error ? [{ id: 'err', text: error, tone: 'bad' as const }] : []),
      ],
    }
  }, [selected, evals, creating, voting, error, model])

  return (
    <>
      <ConfirmDialog
        open={!!deleteTarget}
        title="Delete evaluation?"
        message={`This will permanently delete "${deleteTarget?.name ?? 'this evaluation'}".`}
        confirmLabel="Delete"
        onConfirm={handleDelete}
        onCancel={() => setDeleteTarget(null)}
      />
      <AgentStage {...stageProps} model={field} />

      <div style={{ display: 'grid', gridTemplateColumns: selected ? '360px 1fr' : '1fr', gap: '1.5rem', height: 'calc(100vh - 120px)', padding: '24px' }}>

        {/* List */}
        <div style={{ overflowY: 'auto' }}>
          {error && <div className="error-message" style={{ marginBottom: '1rem' }}>{error}<button onClick={() => setError(null)} aria-label="Dismiss" style={{ marginLeft: '0.5rem', background: 'none', border: 'none', cursor: 'pointer' }}><PhosphorIcon className="bi bi-x" /></button></div>}

          {loading ? (
            <div className="loading-spinner" style={{ padding: '2rem', textAlign: 'center' }}><div className="spinner"></div></div>
          ) : evals.length === 0 ? (
            <div className="empty-state" style={{ textAlign: 'center', padding: '3rem 1rem' }}>
              <PhosphorIcon className="bi bi-columns-gap" style={{ fontSize: '3rem', color: 'var(--text-muted)', display: 'block', marginBottom: '1rem' }}></PhosphorIcon>
              <h3>{t('evaluations.noEvaluations')}</h3>
              <p style={{ color: 'var(--text-muted)', marginBottom: '1.5rem' }}>Create an A/B test to compare two prompt variants.</p>
              <button className="btn btn-primary" onClick={() => setShowNew(true)}><PhosphorIcon className="bi bi-plus-lg"></PhosphorIcon> {t('evaluations.createNew')}</button>
            </div>
          ) : (
            evals.map(ev => (
              <div key={ev.id} onClick={() => setSelected(ev)} style={{ padding: '1rem', border: `1.5px solid ${selected?.id === ev.id ? 'var(--accent)' : 'var(--border)'}`, borderRadius: '10px', marginBottom: '0.75rem', cursor: 'pointer', background: selected?.id === ev.id ? 'var(--accent-light, rgba(214,41,118,0.06))' : 'var(--card-bg)' }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', marginBottom: '0.375rem' }}>
                  <strong style={{ fontSize: '0.9rem' }}>{ev.name}</strong>
                  <div style={{ display: 'flex', gap: '0.25rem' }}>
                    <button onClick={e => handleRerun(e, ev)} style={{ background: 'none', border: 'none', cursor: 'pointer', color: '#94a3b8', fontSize: '0.85rem' }} title="Re-run"><PhosphorIcon className="bi bi-arrow-repeat"></PhosphorIcon></button>
                    <button onClick={e => { e.stopPropagation(); setDeleteTarget(ev) }} style={{ background: 'none', border: 'none', cursor: 'pointer', color: '#94a3b8', fontSize: '0.85rem' }}><PhosphorIcon className="bi bi-trash"></PhosphorIcon></button>
                  </div>
                </div>
                <div style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap', alignItems: 'center' }}>
                  <span style={{ fontSize: '0.75rem', color: 'var(--text-muted)' }}>{ev.model}</span>
                  <span style={{ fontSize: '0.72rem', color: ev.status === 'Completed' ? '#16a34a' : ev.status === 'Failed' ? '#dc2626' : '#f59e0b' }}><PhosphorIcon className="bi bi-circle-fill" /> {ev.status}</span>
                  {winnerBadge(ev.winner)}
                </div>
                <div style={{ fontSize: '0.72rem', color: 'var(--text-muted)', marginTop: '0.25rem' }}>
                  <PhosphorIcon className="bi bi-clock" style={{ marginRight: '0.25rem' }}></PhosphorIcon>Triggered {formatTriggered(ev.createdAt)}
                </div>
              </div>
            ))
          )}
        </div>

        {/* Detail */}
        {selected && (
          <div style={{ overflowY: 'auto', padding: '0.25rem' }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '1.25rem' }}>
              <h2 style={{ margin: 0, fontSize: '1.1rem' }}>{selected.name}</h2>
              <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
                {winnerBadge(selected.winner)}
                <button onClick={() => setSelected(null)} aria-label="Close" style={{ background: 'none', border: 'none', cursor: 'pointer', fontSize: '1.2rem', color: '#94a3b8' }}><PhosphorIcon className="bi bi-x" /></button>
              </div>
            </div>

            {/* Stats row */}
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.75rem', marginBottom: '1.25rem' }}>
              {(['A','B'] as const).map(side => {
                const tokens = side === 'A' ? selected.tokensA : selected.tokensB
                const latency = side === 'A' ? selected.latencyAMs : selected.latencyBMs
                const isWinner = selected.winner === side
                return (
                  <div key={side} style={{ padding: '0.75rem', background: isWinner ? 'rgba(214,41,118,0.08)' : 'var(--bg-secondary)', border: `1.5px solid ${isWinner ? '#d62976' : 'var(--border)'}`, borderRadius: '8px', textAlign: 'center' }}>
                    <div style={{ fontWeight: 700, marginBottom: '0.25rem' }}>Prompt {side} {isWinner && '🏆'}</div>
                    <div style={{ fontSize: '0.75rem', color: 'var(--text-muted)' }}>{tokens} tokens · {latency}ms</div>
                  </div>
                )
              })}
            </div>

            {/* Side-by-side results */}
            {selected.status === 'Completed' && (
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.75rem', marginBottom: '1.25rem' }}>
                {(['A','B'] as const).map(side => {
                  const result = side === 'A' ? selected.resultA : selected.resultB
                  const prompt = side === 'A' ? selected.promptA : selected.promptB
                  const isWinner = selected.winner === side
                  return (
                    <div key={side}>
                      <div style={{ fontWeight: 600, fontSize: '0.8rem', marginBottom: '0.375rem', display:'flex', alignItems:'center', gap:'0.375rem' }}>
                        Prompt {side} {isWinner && <span style={{ color: '#d62976' }}><PhosphorIcon className="bi bi-check" /> Winner</span>}
                      </div>
                      <div style={{ fontSize: '0.75rem', color: 'var(--text-muted)', marginBottom: '0.375rem', background: 'var(--bg-secondary)', padding: '0.5rem', borderRadius: '6px', maxHeight: '80px', overflowY: 'auto', whiteSpace: 'pre-wrap' }}>{prompt}</div>
                      <div style={{ background: 'var(--bg-secondary)', borderRadius: '8px', padding: '0.75rem', fontSize: '0.85rem', lineHeight: '1.6', whiteSpace: 'pre-wrap', maxHeight: '300px', overflowY: 'auto', border: `1.5px solid ${isWinner ? '#d62976' : 'var(--border)'}` }}>
                        {result || '—'}
                      </div>
                    </div>
                  )
                })}
              </div>
            )}

            {/* Vote */}
            {selected.status === 'Completed' && !selected.winner && (
              <div style={{ background: 'var(--card-bg)', border: '1px solid var(--border)', borderRadius: '10px', padding: '1rem', marginBottom: '1rem' }}>
                <h4 style={{ margin: '0 0 0.75rem 0', fontSize: '0.9rem' }}>Which prompt performed better?</h4>
                <div style={{ display: 'flex', gap: '0.75rem', marginBottom: '0.75rem' }}>
                  {(['A','B','Tie'] as const).map(w => (
                    <button key={w} className="btn btn-secondary btn-sm" onClick={() => handleVote(w)} disabled={voting} style={{ flex: 1 }}>
                      {voting ? '…' : w === 'Tie' ? '🤝 Tie' : `Prompt ${w} wins`}
                    </button>
                  ))}
                </div>
                <input type="text" placeholder="Notes (optional)" value={voteNote} onChange={e => setVoteNote(e.target.value)} style={{ width: '100%', padding: '0.4rem 0.6rem', border: '1px solid var(--border)', borderRadius: '6px', fontSize: '0.85rem', background: 'var(--bg-secondary)', color: 'var(--text)' }} />
              </div>
            )}

            {selected.winner && selected.notes && (
              <div style={{ background: 'var(--bg-secondary)', borderRadius: '8px', padding: '0.75rem', marginBottom: '1rem', fontSize: '0.85rem' }}>
                <strong>Notes:</strong> {selected.notes}
              </div>
            )}
          </div>
        )}
      </div>

      {/* New Evaluation Modal */}
      {showNew && (
        <div className="modal-overlay" onClick={() => setShowNew(false)}>
          <div className="modal-content" onClick={e => e.stopPropagation()} style={{ maxWidth: '1200px', width: '98%', maxHeight: '92vh', overflowY: 'auto' }}>
            <div className="modal-header">
              <h2><PhosphorIcon className="bi bi-columns-gap"></PhosphorIcon> {t('evaluations.createNew')}</h2>
              <button className="modal-close" onClick={() => setShowNew(false)}>&times;</button>
            </div>
            <div className="modal-body">
              {error && <div className="error-message" style={{ marginBottom: '1rem' }}>{error}</div>}
              <div className="form-group" style={{ marginBottom: '1rem' }}>
                <label className="form-label">Name</label>
                <input type="text" className="form-control" value={name} onChange={e => setName(e.target.value)} placeholder="e.g., Formal vs Friendly tone test" />
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '1rem', marginBottom: '1rem' }}>
                <div className="form-group">
                  <label className="form-label">Prompt A</label>
                  <textarea className="form-control" value={promptA} onChange={e => setPromptA(e.target.value)} rows={14} placeholder="You are a formal customer support agent..." style={{ resize: 'vertical' }} />
                </div>
                <div className="form-group">
                  <label className="form-label">Prompt B</label>
                  <textarea className="form-control" value={promptB} onChange={e => setPromptB(e.target.value)} rows={14} placeholder="You are a friendly, casual support assistant..." style={{ resize: 'vertical' }} />
                </div>
              </div>
              <div className="form-group" style={{ marginBottom: '1rem' }}>
                <label className="form-label">Test Input (user message)</label>
                <textarea className="form-control" value={testInput} onChange={e => setTestInput(e.target.value)} rows={5} placeholder="I need help with my order..." style={{ resize: 'vertical' }} />
              </div>
              <div className="form-group">
                <label className="form-label">Model</label>
                <select className="form-control" value={model} onChange={e => setModel(e.target.value)} style={{ maxWidth: '300px' }}>
                  {Object.entries(
                    availableModels.reduce((acc, { provider, model: m }) => {
                      (acc[provider] = acc[provider] || []).push(m); return acc
                    }, {} as Record<string, string[]>)
                  ).map(([provider, models]) => (
                    <optgroup key={provider} label={provider}>
                      {models.map(m => <option key={m} value={m}>{m}</option>)}
                    </optgroup>
                  ))}
                </select>
              </div>
            </div>
            <div className="modal-footer">
              <button className="btn btn-secondary" onClick={() => setShowNew(false)}>{t('common.cancel')}</button>
              <button className="btn btn-primary" onClick={handleCreate} disabled={creating}>
                {creating ? <><div className="btn-spinner"></div> {t('evaluations.running')}</> : <><PhosphorIcon className="bi bi-play-fill"></PhosphorIcon> {t('evaluations.run')}</>}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  )
}
