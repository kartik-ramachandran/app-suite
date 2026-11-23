import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect } from 'react'
import { useTranslation } from 'react-i18next'
import api from '../services/api'
import AuthenticatedLayout from '../components/AuthenticatedLayout'
import CustomSelect from '../components/CustomSelect'
import '../App.css'

interface PublicPersona {
  id: number
  name: string
  systemPrompt: string
  description?: string
  tone?: string
  expertise?: string
  constraints?: string
  icon?: string
  category?: string
  usageCount: number
  likeCount: number
  createdAt: string
  authorName: string
  authorPicture?: string
}

const CATEGORY_COLORS: Record<string, string> = {
  Marketing:   '#fef3c7|#d97706',
  Development: '#f9c5dc|#c13584',
  Writing:     '#f3e8ff|#833ab4',
  Support:     '#dcfce7|#16a34a',
  Education:   '#fff5fb|#c13584',
  Business:    '#fce7f3|#be185d',
  Creative:    '#fdf4ff|#a21caf',
  Data:        '#f0fdf4|#15803d',
  Design:      '#fff7ed|#ea580c',
  Sales:       '#fff5fb|#c13584',
  Legal:       '#fef9c3|#a16207',
  Health:      '#fff5fb|#d62976',
  Finance:     '#f0fdf4|#166534',
  Other:       '#f3f4f6|#6b7280',
}

function categoryStyle(cat?: string): { bg: string; text: string } {
  const val = CATEGORY_COLORS[cat ?? ''] ?? CATEGORY_COLORS['Other']
  const [bg, text] = val.split('|')
  return { bg, text }
}

function PersonaLibrary() {
  const { t } = useTranslation()
  const [personas, setPersonas] = useState<PublicPersona[]>([])
  const [loading, setLoading] = useState(true)
  const [categories, setCategories] = useState<string[]>([])
  const [selectedCategory, setSelectedCategory] = useState('')
  const [searchQuery, setSearchQuery] = useState('')
  const [sortBy, setSortBy] = useState('recent')
  const [copiedId, setCopiedId] = useState<number | null>(null)
  const [toast, setToast] = useState<{ message: string; visible: boolean }>({ message: '', visible: false })

  const showToast = (message: string) => {
    setToast({ message, visible: true })
    setTimeout(() => setToast({ message: '', visible: false }), 2000)
  }

  useEffect(() => { fetchPersonas() }, [selectedCategory, searchQuery, sortBy])
  useEffect(() => { fetchCategories() }, [])

  const fetchPersonas = async () => {
    try {
      setLoading(true)
      const params = new URLSearchParams()
      if (selectedCategory) params.append('category', selectedCategory)
      if (searchQuery) params.append('search', searchQuery)
      if (sortBy) params.append('sortBy', sortBy)
      const response = await api.get<PublicPersona[]>(`/personas/public?${params.toString()}`)
      setPersonas(Array.isArray(response.data) ? response.data : [])
    } catch {
      setPersonas([])
    } finally {
      setLoading(false)
    }
  }

  const fetchCategories = async () => {
    try {
      const response = await api.get<string[]>('/personas/public/categories')
      setCategories(Array.isArray(response.data) ? response.data : [])
    } catch { /* ignore */ }
  }

  const handleCopy = async (persona: PublicPersona) => {
    try {
      await navigator.clipboard.writeText(persona.systemPrompt)
      setCopiedId(persona.id)
      setTimeout(() => setCopiedId(null), 1800)
      showToast('System prompt copied!')
    } catch { showToast('Failed to copy') }
  }

  const expertiseList = (str?: string) =>
    str ? str.split(',').map(s => s.trim()).filter(Boolean).slice(0, 4) : []

  return (
    <AuthenticatedLayout
      pageTitle="Persona Library"
      breadcrumbs={[
        { label: 'Dashboard', path: '/dashboard' },
        { label: 'Personas', path: '/personas' },
        { label: 'Persona Library' }
      ]}
    >
      <div className="app-container">
        {/* ── Toolbar ── */}
        <div className="pl-toolbar">
          <div className="pl-search-wrap">
            <PhosphorIcon className="bi bi-search pl-search-icon" />
            <input
              type="text"
              className="pl-search"
              placeholder="Search personas…"
              value={searchQuery}
              onChange={e => setSearchQuery(e.target.value)}
            />
          </div>
          <CustomSelect
            value={selectedCategory}
            onChange={setSelectedCategory}
            className="pl-cs"
            options={[
              { value: '', label: 'All Categories' },
              ...categories.map(cat => ({ value: cat, label: cat }))
            ]}
          />
          <CustomSelect
            value={sortBy}
            onChange={setSortBy}
            className="pl-cs pl-cs--sm"
            options={[
              { value: 'recent', label: 'Recent' },
              { value: 'popular', label: 'Popular' },
            ]}
          />
        </div>

        {loading ? (
          <div className="loading">{t('common.loading')}</div>
        ) : personas.length === 0 ? (
          <div className="empty-state">
            <PhosphorIcon className="bi bi-person-badge" />
            <h3>No personas found</h3>
            <p>Be the first to share a persona!</p>
          </div>
        ) : (
          <div className="pl-grid">
            {personas.map(persona => {
              const { bg, text } = categoryStyle(persona.category)
              const tags = expertiseList(persona.expertise)
              return (
                <div key={persona.id} className="pl-card">
                  <div className="pl-card-top">
                    <div className="pl-icon">{persona.icon || '🤖'}</div>
                    <div className="pl-card-meta">
                      <h3 className="pl-name">{persona.name}</h3>
                      {persona.category && (
                        <span className="pl-cat" style={{ background: bg, color: text }}>{persona.category}</span>
                      )}
                    </div>
                  </div>

                  {persona.description && (
                    <p className="pl-desc">{persona.description}</p>
                  )}

                  {persona.tone && (
                    <div className="pl-tone">
                      <PhosphorIcon className="bi bi-soundwave" /> {persona.tone}
                    </div>
                  )}

                  {tags.length > 0 && (
                    <div className="pl-tags">
                      {tags.map(tag => <span key={tag} className="pl-tag">{tag}</span>)}
                    </div>
                  )}

                  <div className="pl-footer">
                    <span className="pl-author">by {persona.authorName}</span>
                    <button
                      className={`pl-copy-btn${copiedId === persona.id ? ' copied' : ''}`}
                      onClick={() => handleCopy(persona)}
                    >
                      <PhosphorIcon className={`bi ${copiedId === persona.id ? 'bi-check2' : 'bi-clipboard'}`} />
                      {copiedId === persona.id ? 'Copied!' : 'Copy Prompt'}
                    </button>
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </div>

      {toast.visible && <div className="toast-notification">{toast.message}</div>}
    </AuthenticatedLayout>
  )
}

export default PersonaLibrary
