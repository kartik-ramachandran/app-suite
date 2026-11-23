import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useLayoutEffect } from 'react'
import { useTranslation } from 'react-i18next'
import api from '../services/api'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import FileUpload from '../components/FileUpload'
import PromptPicker from '../components/PromptPicker'
import '../App.css'

interface Persona {
  id: number
  name: string
  systemPrompt: string
  description?: string
  tone?: string
  expertise?: string
  constraints?: string
  icon?: string
  category?: string
  // Enhanced fields
  exampleResponses?: string
  personalityTraits?: string
  communicationStyle?: string
  industryFocus?: string
  targetAudience?: string
  isTemplate?: boolean
  templateType?: string
  version?: number
  // Existing
  isPublic: boolean
  usageCount: number
  likeCount: number
  createdAt: string
  updatedAt: string
}

interface GeneratedPersona {
  name: string
  systemPrompt: string
  description: string
  tone: string
  expertise: string
  constraints: string
  icon: string
  category: string
}

const PERSONA_CATEGORIES = [
  'Development', 'Writing', 'Marketing', 'Support', 'Education',
  'Business', 'Creative', 'Data', 'Design', 'Sales', 'Legal', 'Health', 'Finance', 'Other'
]

const TONE_OPTIONS = [
  'Formal', 'Casual', 'Witty', 'Empathetic', 'Direct', 'Enthusiastic',
  'Analytical', 'Supportive', 'Authoritative', 'Friendly'
]

const COMMUNICATION_STYLES = [
  'bullet-points', 'paragraphs', 'code-first', 'conversational', 'structured'
]

const INDUSTRY_OPTIONS = [
  'Tech', 'Finance', 'Healthcare', 'Legal', 'Marketing', 'Education', 'Retail', 'Manufacturing', 'General'
]

const TARGET_AUDIENCES = [
  'Developers', 'Business Users', 'Support Teams', 'Content Creators', 'General Public'
]

const PersonaIcon = ({ icon }: { icon?: string }) => {
  const value = icon?.trim()
  if (!value) return <span aria-hidden="true">🤖</span>

  const bootstrapIcon = value.match(/^(?:bi\s+)?(bi-[a-z0-9-]+)$/i)
  if (bootstrapIcon) return <PhosphorIcon className={`bi ${bootstrapIcon[1]}`} aria-hidden="true" />

  return <span aria-hidden="true">{value}</span>
}

function Personas() {
  const { t } = useTranslation()
  const { setBreadcrumbs } = useLayoutControls()
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Personas' }])
    return () => setBreadcrumbs([])
  }, [])
  const [personas, setPersonas] = useState<Persona[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [toast, setToast] = useState<{ message: string; visible: boolean }>({ message: '', visible: false })
  const [showModal, setShowModal] = useState(false)
  const [editingPersona, setEditingPersona] = useState<Persona | null>(null)
  const [showConfirmModal, setShowConfirmModal] = useState(false)
  const [confirmAction, setConfirmAction] = useState<(() => void) | null>(null)
  const [confirmMessage, setConfirmMessage] = useState('')

  // Form state
  const [formName, setFormName] = useState('')
  const [formSystemPrompt, setFormSystemPrompt] = useState('')
  const [formDescription, setFormDescription] = useState('')
  const [formTone, setFormTone] = useState('')
  const [formExpertise, setFormExpertise] = useState('')
  const [formConstraints, setFormConstraints] = useState('')
  const [formIcon, setFormIcon] = useState('')
  const [formCategory, setFormCategory] = useState('')
  
  // Enhanced form fields
  const [formCommunicationStyle, setFormCommunicationStyle] = useState('')
  const [formPersonalityTraits, setFormPersonalityTraits] = useState('')
  const [formIndustryFocus, setFormIndustryFocus] = useState('')
  const [formTargetAudience, setFormTargetAudience] = useState('')
  
  // Templates
  const [templates, setTemplates] = useState<Persona[]>([])
  const [showTemplates, setShowTemplates] = useState(false)

  // AI generation
  const [aiDescription, setAiDescription] = useState('')
  const [generatingAI, setGeneratingAI] = useState(false)

  const showToast = (message: string) => {
    setToast({ message, visible: true })
    setTimeout(() => setToast({ message: '', visible: false }), 2000)
  }

  useEffect(() => {
    fetchPersonas()
    fetchTemplates()
  }, [])

  const fetchPersonas = async () => {
    try {
      setLoading(true)
      const response = await api.get<Persona[]>('/personas')
      setPersonas(Array.isArray(response.data) ? response.data : [])
    } catch {
      setError('Failed to load personas')
    } finally {
      setLoading(false)
    }
  }

  const fetchTemplates = async () => {
    try {
      const response = await api.get<Persona[]>('/personas/templates')
      setTemplates(Array.isArray(response.data) ? response.data : [])
    } catch {
      console.error('Failed to load templates')
    }
  }

  const resetForm = () => {
    setFormName('')
    setFormSystemPrompt('')
    setFormDescription('')
    setFormTone('')
    setFormExpertise('')
    setFormConstraints('')
    setFormIcon('')
    setFormCategory('')
    setFormCommunicationStyle('')
    setFormPersonalityTraits('')
    setFormIndustryFocus('')
    setFormTargetAudience('')
    setAiDescription('')
    setEditingPersona(null)
  }

  const openCreateModal = () => {
    resetForm()
    setShowModal(true)
  }

  const openEditModal = (persona: Persona) => {
    setEditingPersona(persona)
    setFormName(persona.name)
    setFormSystemPrompt(persona.systemPrompt)
    setFormDescription(persona.description || '')
    setFormTone(persona.tone || '')
    setFormExpertise(persona.expertise || '')
    setFormConstraints(persona.constraints || '')
    setFormIcon(persona.icon || '')
    setFormCategory(persona.category || '')
    setFormCommunicationStyle(persona.communicationStyle || '')
    setFormPersonalityTraits(persona.personalityTraits || '')
    setFormIndustryFocus(persona.industryFocus || '')
    setFormTargetAudience(persona.targetAudience || '')
    setShowModal(true)
  }

  const cloneTemplate = async (template: Persona) => {
    try {
      await api.post(`/personas/templates/${template.id}/clone`)
      showToast(`Cloned: ${template.name}`)
      fetchPersonas()
    } catch {
      showToast('Failed to clone template')
    }
  }

  const handleGenerateWithAI = async () => {
    if (!aiDescription.trim()) return
    try {
      setGeneratingAI(true)
      const response = await api.post<GeneratedPersona>('/personas/generate', { description: aiDescription })
      const gen = response.data
      setFormName(gen.name)
      setFormSystemPrompt(gen.systemPrompt)
      setFormDescription(gen.description)
      setFormTone(gen.tone)
      setFormExpertise(gen.expertise)
      setFormConstraints(gen.constraints)
      setFormIcon(gen.icon)
      setFormCategory(gen.category)
      showToast('Persona generated with AI!')
    } catch {
      setError('Failed to generate persona')
    } finally {
      setGeneratingAI(false)
    }
  }

  const handleSavePersona = async () => {
    if (!formName.trim() || !formSystemPrompt.trim()) {
      setError('Name and System Prompt are required')
      return
    }

    try {
      const data = {
        name: formName,
        systemPrompt: formSystemPrompt,
        description: formDescription,
        tone: formTone,
        expertise: formExpertise,
        constraints: formConstraints,
        icon: formIcon,
        category: formCategory,
        communicationStyle: formCommunicationStyle,
        personalityTraits: formPersonalityTraits,
        industryFocus: formIndustryFocus,
        targetAudience: formTargetAudience
      }

      if (editingPersona) {
        await api.put(`/personas/${editingPersona.id}`, { ...data, id: editingPersona.id })
        showToast('Persona updated!')
      } else {
        await api.post('/personas', data)
        showToast('Persona created!')
      }

      setShowModal(false)
      resetForm()
      fetchPersonas()
    } catch {
      setError('Failed to save persona')
    }
  }

  const handleDelete = (id: number) => {
    setConfirmMessage('Delete this persona? This cannot be undone.')
    setConfirmAction(() => async () => {
      try {
        await api.delete(`/personas/${id}`)
        showToast('Persona deleted')
        fetchPersonas()
      } catch { showToast('Failed to delete') }
      setShowConfirmModal(false)
    })
    setShowConfirmModal(true)
  }

  const handleShare = async (id: number) => {
    try {
      await api.post(`/personas/${id}/share`)
      showToast('Persona shared!')
      fetchPersonas()
    } catch { showToast('Failed to share') }
  }

  const handleUnshare = async (id: number) => {
    try {
      await api.post(`/personas/${id}/unshare`)
      showToast('Persona unshared')
      fetchPersonas()
    } catch { showToast('Failed to unshare') }
  }

  if (loading) return <div className="loading">{t('common.loading')}</div>

  return (
    <>
      <div className="app-container personas-page">
        {error && (
          <div className="error-message">
            {error}
            <button onClick={() => setError(null)} style={{ marginLeft: '1rem' }}><PhosphorIcon className="bi bi-x"></PhosphorIcon></button>
          </div>
        )}

        <div className="prompts-section personas-section">
          <div className="prompts-toolbar">
            <div className="prompts-toolbar-left">
              <h2>{t('personas.title')} <span className="prompt-count">({personas.length})</span></h2>
            </div>
            <div className="prompts-toolbar-right">
              <button 
                className={`btn btn-secondary ${showTemplates ? 'active' : ''}`} 
                onClick={() => setShowTemplates(!showTemplates)}
                style={{ marginRight: '0.5rem' }}
              >
                <PhosphorIcon className="bi bi-collection"></PhosphorIcon> {showTemplates ? 'Hide' : 'Browse'} Templates ({templates.length})
              </button>
              <button className="btn btn-primary create-prompt-btn" onClick={openCreateModal}>
                <PhosphorIcon className="bi bi-plus-lg"></PhosphorIcon> {t('personas.createNew')}
              </button>
            </div>
          </div>

          {/* Persona Templates */}
          {showTemplates && templates.length > 0 && (
            <div className="persona-template-panel">
              <h3 style={{ marginBottom: '1rem', fontSize: '1.1rem' }}>
                <PhosphorIcon className="bi bi-stars"></PhosphorIcon> Pre-Built Persona Templates
              </h3>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(min(280px, 100%), 1fr))', gap: '1rem' }}>
                {templates.map((template) => (
                  <div key={template.id} className="persona-template-card">
                    <div style={{ display: 'flex', alignItems: 'start', justifyContent: 'space-between', marginBottom: '0.5rem' }}>
                      <div>
                        <span className="persona-template-icon"><PersonaIcon icon={template.icon} /></span>
                        <strong>{template.name}</strong>
                      </div>
                    </div>
                    {template.category && <span className="prompt-category" style={{ fontSize: '0.75rem' }}>{template.category}</span>}
                    {template.description && (
                      <p style={{ fontSize: '0.85rem', color: '#65676b', margin: '0.5rem 0', lineHeight: '1.4' }}>
                        {template.description}
                      </p>
                    )}
                    {template.targetAudience && (
                      <div style={{ fontSize: '0.75rem', color: '#888', marginBottom: '0.5rem' }}>
                        <PhosphorIcon className="bi bi-people"></PhosphorIcon> {template.targetAudience}
                      </div>
                    )}
                    <button className="btn btn-primary btn-sm" onClick={() => cloneTemplate(template)} style={{ width: '100%' }}>
                      <PhosphorIcon className="bi bi-plus-circle"></PhosphorIcon> Use This Template
                    </button>
                  </div>
                ))}
              </div>
            </div>
          )}

          <div className="prompts-list">
            {personas.length === 0 ? (
              <div className="empty-state">
                <PhosphorIcon className="bi bi-person-badge"></PhosphorIcon>
                <h3>{t('personas.noPersonas')}</h3>
                <p>{t('personas.noPersonas')}</p>
                <button className="btn btn-primary empty-state-btn" onClick={openCreateModal}>
                  {t('personas.createNew')}
                </button>
              </div>
            ) : (
              personas.map((persona) => (
                <div key={persona.id} className="prompt-card persona-card-modern">
                  <div className="prompt-header">
                    <div className="prompt-header-left">
                      <div className="prompt-title-row">
                        <span className="persona-card-avatar"><PersonaIcon icon={persona.icon} /></span>
                        <h3 className="prompt-title">{persona.name}</h3>
                        {persona.isPublic && <span className="public-badge">Public</span>}
                        {persona.usageCount > 0 && (
                          <span style={{ fontSize: '0.75rem', background: '#e3f2fd', color: '#1976d2', padding: '0.2rem 0.5rem', borderRadius: '12px', marginLeft: '0.5rem' }}>
                            <PhosphorIcon className="bi bi-lightning-charge"></PhosphorIcon> {persona.usageCount} uses
                          </span>
                        )}
                      </div>
                    </div>
                    <div className="prompt-actions">
                      {persona.isPublic ? (
                        <button className="btn btn-secondary btn-sm" onClick={() => handleUnshare(persona.id)}>
                          <PhosphorIcon className="bi bi-lock"></PhosphorIcon> Unshare
                        </button>
                      ) : (
                        <button className="btn btn-share btn-sm" onClick={() => handleShare(persona.id)}>
                          <PhosphorIcon className="bi bi-share"></PhosphorIcon> {t('common.share')}
                        </button>
                      )}
                      <button className="btn btn-secondary btn-sm" onClick={() => openEditModal(persona)}>{t('common.edit')}</button>
                      <button className="btn btn-danger btn-sm" onClick={() => handleDelete(persona.id)}>{t('common.delete')}</button>
                    </div>
                  </div>
                  <div className="persona-chip-row">
                  {persona.category && <span className="prompt-category persona-chip persona-chip-category">{persona.category}</span>}
                  {persona.targetAudience && (
                    <span className="prompt-category persona-chip persona-chip-audience">
                      <PhosphorIcon className="bi bi-people"></PhosphorIcon> {persona.targetAudience}
                    </span>
                  )}
                  </div>
                  {persona.description && <div className="prompt-content" style={{ marginTop: '0.5rem' }}>{persona.description}</div>}
                  <div className="persona-meta-row">
                    {persona.tone && <span><PhosphorIcon className="bi bi-chat-heart"></PhosphorIcon> {persona.tone}</span>}
                    {persona.communicationStyle && <span><PhosphorIcon className="bi bi-chat-square-text"></PhosphorIcon> {persona.communicationStyle}</span>}
                    {persona.industryFocus && <span><PhosphorIcon className="bi bi-building"></PhosphorIcon> {persona.industryFocus}</span>}
                    {persona.personalityTraits && <span><PhosphorIcon className="bi bi-star"></PhosphorIcon> {persona.personalityTraits}</span>}
                  </div>
                  {persona.expertise && (
                    <div className="persona-expertise">
                      <strong>Expertise:</strong> {persona.expertise}
                    </div>
                  )}
                </div>
              ))
            )}
          </div>
        </div>
      </div>

      {/* Create/Edit Modal */}
      {showModal && (
        <div className="modal-overlay" onClick={() => setShowModal(false)}>
          <div className="modal-content" style={{ maxWidth: '700px' }} onClick={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h2><PhosphorIcon className="bi bi-person-badge"></PhosphorIcon> {editingPersona ? t('personas.title') : t('personas.createNew')}</h2>
              <button className="modal-close" onClick={() => setShowModal(false)}><PhosphorIcon className="bi bi-x"></PhosphorIcon></button>
            </div>
            <div className="modal-body">
              {/* AI Generation Section */}
              {!editingPersona && (
                <div style={{ marginBottom: '1.5rem', padding: '1rem', background: 'linear-gradient(135deg, #fff5fb, #f0f0ff)', borderRadius: '12px' }}>
                  <label style={{ fontWeight: 600, marginBottom: '0.5rem', display: 'block' }}>
                    <PhosphorIcon className="bi bi-lightning-charge-fill" style={{ color: '#60a5fa' }}></PhosphorIcon> {t('personas.generateWithAI')}
                  </label>
                  <div style={{ display: 'flex', gap: '0.5rem' }}>
                    <input
                      type="text"
                      value={aiDescription}
                      onChange={(e) => setAiDescription(e.target.value)}
                      placeholder='e.g., "A sarcastic senior Python developer who gives blunt code reviews"'
                      style={{ flex: 1, borderRadius: '6px', padding: '0.875rem 1rem', border: '1px solid #ccd0d5', background: '#f0f2f5' }}
                    />
                    <button className="btn btn-primary" onClick={handleGenerateWithAI} disabled={generatingAI || !aiDescription.trim()}>
                      {generatingAI ? <><div className="btn-spinner"></div> {t('personas.generating')}</> : <><PhosphorIcon className="bi bi-magic"></PhosphorIcon> {t('common.generate')}</>}
                    </button>
                  </div>
                </div>
              )}

              <div className="form-group">
                <label>{t('personas.icon')}</label>
                <input type="text" value={formIcon} onChange={(e) => setFormIcon(e.target.value)} placeholder="e.g., 🧑‍💻" maxLength={4} style={{ width: '80px' }} />
              </div>
              <div className="form-group">
                <label>{t('personas.name')} *</label>
                <input type="text" value={formName} onChange={(e) => setFormName(e.target.value)} placeholder="e.g., Sarcastic Code Reviewer" required />
              </div>
              <div className="form-row">
                <div className="form-group form-group-half">
                  <label>{t('personas.category')}</label>
                  <select value={formCategory} onChange={(e) => setFormCategory(e.target.value)} className="style-select">
                    <option value="">Select category...</option>
                    {PERSONA_CATEGORIES.map(cat => <option key={cat} value={cat}>{cat}</option>)}
                  </select>
                </div>
                <div className="form-group form-group-half">
                  <label>{t('personas.tone')}</label>
                  <select value={formTone} onChange={(e) => setFormTone(e.target.value)} className="style-select">
                    <option value="">Select tone...</option>
                    {TONE_OPTIONS.map(t => <option key={t} value={t}>{t}</option>)}
                  </select>
                </div>
              </div>
              <div className="form-group">
                <label>{t('personas.description')}</label>
                <input type="text" value={formDescription} onChange={(e) => setFormDescription(e.target.value)} placeholder="Brief description of what this persona does" />
              </div>
              <div className="form-group">
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 4 }}>
                  <label style={{ margin: 0 }}>{t('personas.systemPrompt')} *</label>
                  <PromptPicker onSelect={p => setFormSystemPrompt(p.content)} />
                </div>
                <textarea value={formSystemPrompt} onChange={(e) => setFormSystemPrompt(e.target.value)} placeholder="Act as a senior Python developer..." rows={6} required />
              </div>
              <div className="form-group">
                <label>{t('personas.expertise')}</label>
                <input type="text" value={formExpertise} onChange={(e) => setFormExpertise(e.target.value)} placeholder="e.g., Python, Django, FastAPI, DevOps" />
              </div>
              
              {/* Enhanced Character Building Fields */}
              <div style={{ background: '#f8f9fa', padding: '1rem', borderRadius: '8px', margin: '1rem 0' }}>
                <h4 style={{ fontSize: '0.95rem', marginBottom: '1rem', color: '#495057' }}>
                  <PhosphorIcon className="bi bi-stars"></PhosphorIcon> Enhanced Character Profile
                </h4>
                <div className="form-row">
                  <div className="form-group form-group-half">
                    <label>Communication Style</label>
                    <select value={formCommunicationStyle} onChange={(e) => setFormCommunicationStyle(e.target.value)} className="style-select">
                      <option value="">Select style...</option>
                      {COMMUNICATION_STYLES.map(s => <option key={s} value={s}>{s}</option>)}
                    </select>
                  </div>
                  <div className="form-group form-group-half">
                    <label>Industry Focus</label>
                    <select value={formIndustryFocus} onChange={(e) => setFormIndustryFocus(e.target.value)} className="style-select">
                      <option value="">Select industry...</option>
                      {INDUSTRY_OPTIONS.map(i => <option key={i} value={i}>{i}</option>)}
                    </select>
                  </div>
                </div>
                <div className="form-row">
                  <div className="form-group form-group-half">
                    <label>Personality Traits</label>
                    <input type="text" value={formPersonalityTraits} onChange={(e) => setFormPersonalityTraits(e.target.value)} placeholder="e.g., analytical, patient, direct" />
                  </div>
                  <div className="form-group form-group-half">
                    <label>Target Audience</label>
                    <select value={formTargetAudience} onChange={(e) => setFormTargetAudience(e.target.value)} className="style-select">
                      <option value="">Select audience...</option>
                      {TARGET_AUDIENCES.map(a => <option key={a} value={a}>{a}</option>)}
                    </select>
                  </div>
                </div>
              </div>
              
              <div className="form-group">
                <label>Constraints</label>
                <textarea value={formConstraints} onChange={(e) => setFormConstraints(e.target.value)} placeholder="What should this persona NOT do?" rows={2} />
              </div>

              {/* Context Files — Upload or Google Drive */}
              <FileUpload
                entityType="persona"
                entityId={editingPersona?.id ?? null}
              />
            </div>
            <div className="modal-footer">
              <button className="btn btn-secondary" onClick={() => setShowModal(false)}>{t('common.cancel')}</button>
              <button className="btn btn-primary" onClick={handleSavePersona}>
                <PhosphorIcon className="bi bi-check"></PhosphorIcon> {editingPersona ? t('common.update') : t('personas.createNew')}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Confirm Modal */}
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

export default Personas
