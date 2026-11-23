import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useLayoutEffect, useMemo } from 'react'
import { useNavigate } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import api from '../services/api'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView } from '../gamefield'
import type { FieldModel } from '../gamefield'
import '../App.css'

interface Chain {
  id: string
  name: string
  description?: string
  category?: string
  isPublic: boolean
  usageCount: number
  createdAt: string
  updatedAt: string
  nodeCount: number
}

function Chains() {
  const { t } = useTranslation()
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('chains')
  const navigate = useNavigate()
  const [chains, setChains] = useState<Chain[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [toast, setToast] = useState<{ message: string; visible: boolean }>({ message: '', visible: false })
  const [showConfirmModal, setShowConfirmModal] = useState(false)
  const [confirmAction, setConfirmAction] = useState<(() => void) | null>(null)
  const [confirmMessage, setConfirmMessage] = useState('')

  useLayoutEffect(() => {
    setBreadcrumbs([
      { label: t('nav.dashboard'), path: '/dashboard' },
      { label: t('chains.title') }
    ])
    return () => setBreadcrumbs([])
  }, [])

  const showToast = (message: string) => {
    setToast({ message, visible: true })
    setTimeout(() => setToast({ message: '', visible: false }), 2000)
  }

  useEffect(() => { fetchChains() }, [])

  const fetchChains = async () => {
    try {
      setLoading(true)
      const response = await api.get<Chain[]>('/chains')
      setChains(Array.isArray(response.data) ? response.data : [])
    } catch {
      setError('Failed to load workflows')
    } finally {
      setLoading(false)
    }
  }

  const handleDelete = (id: string) => {
    setConfirmMessage('Delete this workflow? This cannot be undone.')
    setConfirmAction(() => async () => {
      try {
        await api.delete(`/chains/${id}`)
        showToast('Workflow deleted')
        fetchChains()
      } catch { showToast('Failed to delete workflow') }
      setShowConfirmModal(false)
    })
    setShowConfirmModal(true)
  }

  const handleShare = async (id: string) => {
    try {
      await api.post(`/chains/${id}/share`)
      showToast('Workflow shared!')
      fetchChains()
    } catch { showToast('Failed to share workflow') }
  }

  const formatDate = (dateStr: string) => {
    const date = new Date(dateStr)
    const now = new Date()
    const diffMs = now.getTime() - date.getTime()
    const diffDays = Math.floor(diffMs / 86400000)
    if (diffDays === 0) return 'Today'
    if (diffDays === 1) return 'Yesterday'
    if (diffDays < 7) return `${diffDays}d ago`
    return date.toLocaleDateString()
  }

  if (loading) return (
    <div className="app-container"><div className="chains-loading"><div className="btn-spinner"></div> {t('common.loading')}</div></div>
  )

  // Gamefield: the chain library as a bullpen — one desk per saved chain.
  const field = useMemo<FieldModel>(() => ({
    title: 'Chains',
    subtitle: `${chains.length} saved`,
    running: false,
    agents: [
      ...chains.slice(0, 16).map(c => ({
        id: `chain-${c.id}`,
        name: c.name,
        role: c.category || 'chain',
        status: 'idle' as const,
        detail: c.description || undefined,
        output: c.description || undefined,
      })),
      {
        id: 'librarian',
        name: 'Librarian',
        role: loading ? 'loading…' : `${chains.length} on the shelf`,
        status: (error ? 'failed' : loading ? 'working' : 'idle') as 'failed' | 'working' | 'idle',
        detail: error || undefined,
        boss: true,
      },
    ],
    messages: [],
    events: error ? [{ id: 'err', text: error, tone: 'bad' as const }] : [],
  }), [chains, loading, error])

  return (
    <>
    <div className="app-container chains-page">
        <AgentStage {...stageProps} model={field} />

        {error && (
          <div className="error-message">
            {error}
            <button onClick={() => setError(null)} className="btn btn-sm"><PhosphorIcon className="bi bi-x"></PhosphorIcon></button>
          </div>
        )}

        <section className="chains-hero">
          <div>
            <span className="chains-eyebrow">
              <PhosphorIcon className="bi bi-diagram-3"></PhosphorIcon> Workflow Studio
            </span>
            <h1>Build multi-step prompt workflows</h1>
            <p>Chain inputs, prompts, and outputs into repeatable systems for research, content, operations, and automation.</p>
          </div>
          <button className="btn btn-primary chains-hero-action" onClick={() => navigate('/chain-builder')}>
            <PhosphorIcon className="bi bi-plus-lg"></PhosphorIcon> {t('chains.createNew')}
          </button>
        </section>

        <div className="prompts-section chains-section">
          <div className="prompts-toolbar">
            <div className="prompts-toolbar-left">
              <h2>Your Workflows <span className="prompt-count">({chains.length})</span></h2>
            </div>
            <div className="prompts-toolbar-right">
              <button className="btn btn-primary create-prompt-btn" onClick={() => navigate('/chain-builder')}>
                <PhosphorIcon className="bi bi-plus-lg"></PhosphorIcon> {t('chains.createNew')}
              </button>
            </div>
          </div>

          {chains.length === 0 ? (
            <div className="chains-empty-state">
              <div className="chains-empty-icon">
                <PhosphorIcon className="bi bi-diagram-3"></PhosphorIcon>
              </div>
              <h3>No workflows yet</h3>
              <p>Chain multiple prompts together to create powerful workflows.<br />Connect an input → process through prompts → get a final output.</p>
              <button className="btn btn-primary" onClick={() => navigate('/chain-builder')}>
                <PhosphorIcon className="bi bi-plus-lg"></PhosphorIcon> {t('chains.createNew')}
              </button>
            </div>
          ) : (
            <div className="chains-grid">
              {chains.map((chain) => (
                <div key={chain.id} className="chain-card" onClick={() => navigate(`/chain-builder/${chain.id}`)}>
                  <div className="chain-card-header">
                    <div className="chain-card-title-row">
                      <h3 className="chain-card-title">{chain.name}</h3>
                      {chain.isPublic && <span className="public-badge">Public</span>}
                    </div>
                    {chain.category && <span className="chain-card-category">{chain.category}</span>}
                  </div>
                  {chain.description && (
                    <p className="chain-card-desc">{chain.description}</p>
                  )}
                  <div className="chain-card-stats">
                    <span className="chain-stat">
                      <PhosphorIcon className="bi bi-diagram-3"></PhosphorIcon> {chain.nodeCount} {t('chains.nodes')}
                    </span>
                    <span className="chain-stat">
                      <PhosphorIcon className="bi bi-play-circle"></PhosphorIcon> {chain.usageCount} runs
                    </span>
                    <span className="chain-stat">
                      <PhosphorIcon className="bi bi-clock"></PhosphorIcon> {formatDate(chain.updatedAt)}
                    </span>
                  </div>
                  <div className="chain-card-actions" onClick={(e) => e.stopPropagation()}>
                    <button className="btn btn-sm btn-outline-secondary" onClick={() => navigate(`/chain-builder/${chain.id}`)}>
                      <PhosphorIcon className="bi bi-pencil"></PhosphorIcon> {t('common.edit')}
                    </button>
                    <button className="btn btn-sm btn-outline-secondary" onClick={() => handleShare(chain.id)}>
                      <PhosphorIcon className="bi bi-share"></PhosphorIcon> {t('common.share')}
                    </button>
                    <button className="btn btn-sm btn-outline-danger" onClick={() => handleDelete(chain.id)}>
                      <PhosphorIcon className="bi bi-trash"></PhosphorIcon>
                    </button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      </div>

      {showConfirmModal && (
        <div className="modal-overlay" onClick={() => setShowConfirmModal(false)}>
          <div className="modal-content confirm-modal" onClick={(e) => e.stopPropagation()}>
            <div className="modal-header"><h2><PhosphorIcon className="bi bi-exclamation-triangle"></PhosphorIcon> Confirm</h2></div>
            <div className="modal-body"><p className="confirm-message">{confirmMessage}</p></div>
            <div className="modal-footer">
              <button className="btn btn-secondary" onClick={() => setShowConfirmModal(false)}>{t('common.cancel')}</button>
              <button className="btn btn-danger" onClick={() => confirmAction && confirmAction()}>{t('common.delete')}</button>
            </div>
          </div>
        </div>
      )}

      {toast.visible && <div className="toast-notification">{toast.message}</div>}
    </>
  )
}

export default Chains
