import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import api from '../services/api'
import ConfirmDialog from '../components/ConfirmDialog'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { usePageMeta } from '../utils/usePageMeta'
import '../App.css'

interface CollectionSummary {
  id: string
  name: string
  description?: string
  emoji?: string
  isPublic: boolean
  itemCount: number
  createdAt: string
  updatedAt: string
}

interface CollectionPrompt {
  id: string
  name: string
  content: string
  category?: string
  description?: string
  difficulty?: string
  addedAt: string
}

interface CollectionDetail extends CollectionSummary {
  prompts: CollectionPrompt[]
}

const EMOJI_OPTIONS = ['📁', '⭐', '🔥', '🎯', '💡', '🚀', '🧠', '✍️', '💼', '🎨', '📝', '🔖']

function Collections() {
  usePageMeta('My Collections – GenPrompt', 'Organise your saved prompts into collections.')
  const navigate = useNavigate()

  const [collections, setCollections] = useState<CollectionSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [selected, setSelected] = useState<CollectionDetail | null>(null)
  const [loadingDetail, setLoadingDetail] = useState(false)

  const [showCreate, setShowCreate] = useState(false)
  const [newName, setNewName] = useState('')
  const [newDesc, setNewDesc] = useState('')
  const [newEmoji, setNewEmoji] = useState('📁')
  const [newPublic, setNewPublic] = useState(false)
  const [saving, setSaving] = useState(false)
  const [toast, setToast] = useState('')
  const [deleteTarget, setDeleteTarget] = useState<CollectionSummary | null>(null)

  const { setHeaderActions } = useLayoutControls()

  useEffect(() => {
    setHeaderActions(
      <button className="btn btn-primary btn-sm" onClick={() => setShowCreate(true)}>
        <PhosphorIcon className="bi bi-plus-lg" /> New Collection
      </button>
    )
    return () => setHeaderActions(null)
  }, [setHeaderActions])

  useEffect(() => { fetchCollections() }, [])

  const showToast = (msg: string) => {
    setToast(msg)
    setTimeout(() => setToast(''), 2500)
  }

  const fetchCollections = async () => {
    try {
      const res = await api.get<CollectionSummary[]>('/collections')
      setCollections(res.data)
    } catch {
      showToast('Failed to load collections')
    } finally {
      setLoading(false)
    }
  }

  const openCollection = async (id: string) => {
    setLoadingDetail(true)
    try {
      const res = await api.get<CollectionDetail>(`/collections/${id}`)
      setSelected(res.data)
    } catch {
      showToast('Failed to load collection')
    } finally {
      setLoadingDetail(false)
    }
  }

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!newName.trim()) return
    setSaving(true)
    try {
      await api.post('/collections', { name: newName, description: newDesc || null, emoji: newEmoji, isPublic: newPublic })
      setShowCreate(false)
      setNewName(''); setNewDesc(''); setNewEmoji('📁'); setNewPublic(false)
      fetchCollections()
      showToast('Collection created!')
    } catch {
      showToast('Failed to create collection')
    } finally {
      setSaving(false)
    }
  }

  const handleDelete = async () => {
    if (!deleteTarget) return
    try {
      await api.delete(`/collections/${deleteTarget.id}`)
      setCollections(prev => prev.filter(c => c.id !== deleteTarget.id))
      if (selected?.id === deleteTarget.id) setSelected(null)
      setDeleteTarget(null)
      showToast('Deleted')
    } catch {
      showToast('Failed to delete')
    }
  }

  const handleRemovePrompt = async (promptId: string) => {
    if (!selected) return
    try {
      await api.delete(`/collections/${selected.id}/prompts/${promptId}`)
      setSelected(prev => prev ? { ...prev, prompts: prev.prompts.filter(p => p.id !== promptId) } : prev)
      setCollections(prev => prev.map(c => c.id === selected.id ? { ...c, itemCount: c.itemCount - 1 } : c))
      showToast('Removed from collection')
    } catch {
      showToast('Failed to remove')
    }
  }

  return (
    <div className="app-container">
      <ConfirmDialog
        open={!!deleteTarget}
        title="Delete collection?"
        message={`Delete "${deleteTarget?.name ?? 'this collection'}"? Prompts inside it will not be deleted.`}
        confirmLabel="Delete"
        onConfirm={handleDelete}
        onCancel={() => setDeleteTarget(null)}
      />
      {toast && <div className="toast-notification toast-visible">{toast}</div>}

      <div className="dashboard-header">
        <h2>My Collections</h2>
        <p>Group your saved prompts into themed collections for quick access.</p>
      </div>

      {loading ? (
        <div className="loading">Loading...</div>
      ) : (
        <div style={{ display: 'flex', gap: '1.5rem', alignItems: 'flex-start' }}>
          {/* Sidebar — collection list */}
          <div style={{ width: 280, flexShrink: 0 }}>
            {collections.length === 0 ? (
              <div className="empty-state">
                <PhosphorIcon className="bi bi-folder-plus" style={{ fontSize: '2.5rem', color: '#94a3b8' }} />
                <p>No collections yet.</p>
                <button className="btn btn-primary" onClick={() => setShowCreate(true)}>Create your first</button>
              </div>
            ) : (
              <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
                {collections.map(c => (
                  <div
                    key={c.id}
                    onClick={() => openCollection(c.id)}
                    style={{
                      padding: '0.75rem 1rem',
                      borderRadius: 10,
                      border: `2px solid ${selected?.id === c.id ? '#d62976' : '#e2e8f0'}`,
                      background: selected?.id === c.id ? '#fff5fb' : '#fff',
                      cursor: 'pointer',
                      display: 'flex',
                      alignItems: 'center',
                      justifyContent: 'space-between',
                      gap: '0.5rem',
                    }}
                  >
                    <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', minWidth: 0 }}>
                      <span style={{ fontSize: '1.2rem' }}>{c.emoji || '📁'}</span>
                      <div style={{ minWidth: 0 }}>
                        <p style={{ margin: 0, fontWeight: 600, fontSize: '0.9rem', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{c.name}</p>
                        <p style={{ margin: 0, fontSize: '0.75rem', color: '#64748b' }}>{c.itemCount} prompt{c.itemCount !== 1 ? 's' : ''}</p>
                      </div>
                    </div>
                    <button
                      className="btn-icon-sm"
                      onClick={(e) => { e.stopPropagation(); setDeleteTarget(c) }}
                      title="Delete collection"
                      style={{ color: '#ef4444', flexShrink: 0 }}
                    >
                      <PhosphorIcon className="bi bi-trash" />
                    </button>
                  </div>
                ))}
              </div>
            )}
          </div>

          {/* Main — collection detail */}
          <div style={{ flex: 1, minWidth: 0 }}>
            {loadingDetail ? (
              <div className="loading">Loading...</div>
            ) : selected ? (
              <>
                <div style={{ display: 'flex', alignItems: 'center', gap: '0.75rem', marginBottom: '1.25rem' }}>
                  <span style={{ fontSize: '1.8rem' }}>{selected.emoji}</span>
                  <div>
                    <h3 style={{ margin: 0 }}>{selected.name}</h3>
                    {selected.description && <p style={{ margin: 0, color: '#64748b', fontSize: '0.875rem' }}>{selected.description}</p>}
                  </div>
                </div>

                {selected.prompts.length === 0 ? (
                  <div className="empty-state">
                    <p>No prompts in this collection yet.</p>
                    <button className="btn btn-secondary" onClick={() => navigate('/dashboard')}>Go to Dashboard to add</button>
                  </div>
                ) : (
                  <div className="prompt-grid">
                    {selected.prompts.map(p => (
                      <div key={p.id} className="prompt-card">
                        <div className="prompt-card-header">
                          <h4 className="prompt-card-title">{p.name}</h4>
                          <button className="btn-icon-sm" style={{ color: '#ef4444' }} onClick={() => handleRemovePrompt(p.id)} title="Remove from collection">
                            <PhosphorIcon className="bi bi-x-circle" />
                          </button>
                        </div>
                        <div style={{ display: 'flex', gap: '0.4rem', marginBottom: '0.5rem', flexWrap: 'wrap' }}>
                          {p.category && <span className="prompt-category">{p.category}</span>}
                          {p.difficulty && <span className={`difficulty-badge difficulty-${p.difficulty}`}>{p.difficulty.charAt(0).toUpperCase() + p.difficulty.slice(1)}</span>}
                        </div>
                        <p className="prompt-card-content">{p.content.substring(0, 180)}{p.content.length > 180 ? '...' : ''}</p>
                        <div className="prompt-card-actions">
                          <button className="btn btn-sm btn-secondary" onClick={() => { navigator.clipboard.writeText(p.content); showToast('Copied!') }}>
                            <PhosphorIcon className="bi bi-clipboard" /> Copy
                          </button>
                          <button className="btn btn-sm btn-secondary" onClick={() => navigate(`/create-prompt?edit=${p.id}`)}>
                            <PhosphorIcon className="bi bi-pencil" /> Edit
                          </button>
                        </div>
                      </div>
                    ))}
                  </div>
                )}
              </>
            ) : (
              <div className="empty-state" style={{ marginTop: '3rem' }}>
                <PhosphorIcon className="bi bi-collection" style={{ fontSize: '2.5rem', color: '#94a3b8' }} />
                <p>Select a collection to view its prompts.</p>
              </div>
            )}
          </div>
        </div>
      )}

      {/* Create collection modal */}
      {showCreate && (
        <div className="modal-overlay" onClick={() => setShowCreate(false)}>
          <div className="modal-content" style={{ maxWidth: 420 }} onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <h2>New Collection</h2>
              <button className="modal-close" onClick={() => setShowCreate(false)} aria-label="Close"><PhosphorIcon className="bi bi-x" /></button>
            </div>
            <form onSubmit={handleCreate} className="modal-body">
              <div className="form-group">
                <label>Emoji</label>
                <div style={{ display: 'flex', gap: '0.4rem', flexWrap: 'wrap', marginBottom: '0.5rem' }}>
                  {EMOJI_OPTIONS.map(e => (
                    <button
                      key={e} type="button"
                      onClick={() => setNewEmoji(e)}
                      style={{ fontSize: '1.3rem', padding: '0.25rem 0.4rem', borderRadius: 8, border: newEmoji === e ? '2px solid #d62976' : '2px solid #e2e8f0', background: newEmoji === e ? '#fff5fb' : '#fff', cursor: 'pointer' }}
                    >{e}</button>
                  ))}
                </div>
              </div>
              <div className="form-group">
                <label htmlFor="col-name">Name *</label>
                <input id="col-name" className="form-control" value={newName} onChange={e => setNewName(e.target.value)} placeholder="e.g. Marketing Prompts" required />
              </div>
              <div className="form-group">
                <label htmlFor="col-desc">Description</label>
                <input id="col-desc" className="form-control" value={newDesc} onChange={e => setNewDesc(e.target.value)} placeholder="Optional description" />
              </div>
              <div className="form-group" style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
                <input type="checkbox" id="col-public" checked={newPublic} onChange={e => setNewPublic(e.target.checked)} />
                <label htmlFor="col-public" style={{ margin: 0 }}>Make public</label>
              </div>
              <div style={{ display: 'flex', gap: '0.75rem', justifyContent: 'flex-end', marginTop: '1rem' }}>
                <button type="button" className="btn btn-secondary" onClick={() => setShowCreate(false)}>Cancel</button>
                <button type="submit" className="btn btn-primary" disabled={saving}>{saving ? 'Creating...' : 'Create'}</button>
              </div>
            </form>
          </div>
        </div>
      )}
    </div>
  )
}

export default Collections
