import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useLayoutEffect, useMemo, useRef, useCallback } from 'react'
import { useNavigate, Link } from 'react-router-dom'
import { useTranslation } from 'react-i18next'
import api from '../services/api'
import { getPromptVersions, getPromptAudit, requestPromptApproval, approvePrompt, getTopPromptPerformers } from '../services/api'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import ExecutionModal from '../components/ExecutionModal'
import SavedFilesPanel from '../components/SavedFilesPanel'
import '../App.css'

interface Prompt {
  id: number
  name: string
  content: string
  category: string
  createdAt: string
  updatedAt: string
  description?: string
  isActive?: boolean
  isPublic?: boolean
  isTemplate?: boolean
  approvalStatus?: string
  requiresApproval?: boolean
  currentVersion?: number
  approvedAt?: string
}

interface PromptVersion {
  versionNumber: number
  status?: string
  createdAt: string
  changeNote?: string
}

interface PromptAuditEvent {
  eventType: string
  createdAt: string
  detailsJson?: string
}

interface TopPromptPerformer {
  id: string
  name: string
  category?: string
  executionCount?: number
  averageScore?: number
}

type QuickFilter = 'all' | 'favorites' | 'recent' | 'shared'

const CATEGORY_STYLES: Record<string, { bg: string; icon: string }> = {
  'Marketing': { bg: '#833ab4', icon: 'megaphone-fill' },
  'Development': { bg: '#3b82f6', icon: 'code-slash' },
  'Research': { bg: '#f59e0b', icon: 'search' },
  'Writing': { bg: '#10b981', icon: 'pen-fill' },
  'Analysis': { bg: '#f97316', icon: 'graph-up' },
  'Product': { bg: '#d62976', icon: 'box' },
  'Support': { bg: '#833ab4', icon: 'headset' },
  'Design': { bg: '#ec4899', icon: 'palette-fill' },
  'Imported': { bg: '#64748b', icon: 'file-earmark-text' },
}
const getCategoryStyle = (cat: string) =>
  CATEGORY_STYLES[cat] || { bg: '#833ab4', icon: 'stars' }

const STYLES = ['Professional', 'Creative', 'Technical', 'Casual', 'Academic']
const SUGGESTION_MODE_COPY: Record<'Intent' | 'Goals', { title: string; optionLabel: string; description: string; placeholder: string }> = {
  Intent: {
    title: 'Intent based',
    optionLabel: 'Intent - describe what you want to create',
    description: 'Use a natural-language request. The AI reads what you want to create and suggests the most relevant goals.',
    placeholder: "e.g., 'Create a customer support chatbot that handles complaints professionally'"
  },
  Goals: {
    title: 'Goal based',
    optionLabel: 'Goals - describe the outcomes you want',
    description: 'Describe the outcomes you want. The AI focuses on the objectives the prompt should achieve and suggests goal-driven options.',
    placeholder: "e.g., 'Improve response quality, reduce escalation, and keep replies on-brand'"
  }
}

function Dashboard() {
  const { t } = useTranslation()
  const navigate = useNavigate()
  const [prompts, setPrompts] = useState<Prompt[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const [showRecycler, setShowRecycler] = useState(false)
  const [deletedPrompts, setDeletedPrompts] = useState<Prompt[]>([])

  const [showConfirmModal, setShowConfirmModal] = useState(false)
  const [confirmAction, setConfirmAction] = useState<(() => void) | null>(null)
  const [confirmMessage, setConfirmMessage] = useState('')
  const [confirmButtonText, setConfirmButtonText] = useState('Confirm')

  const [toast, setToast] = useState<{ message: string; visible: boolean }>({ message: '', visible: false })

  const [bulkMode, setBulkMode] = useState(false)
  const [selectedPrompts, setSelectedPrompts] = useState<number[]>([])

  const [showImportModal, setShowImportModal] = useState(false)
  const [importFile, setImportFile] = useState<File | null>(null)
  const [importPreview, setImportPreview] = useState<{ name: string; content: string; category?: string }[]>([])
  const [importing, setImporting] = useState(false)
  const [showExportMenu, setShowExportMenu] = useState(false)

  const [showExecutionModal, setShowExecutionModal] = useState(false)
  const [executionPrompt, setExecutionPrompt] = useState<Prompt | null>(null)
  const [promptVersions, setPromptVersions] = useState<PromptVersion[]>([])
  const [promptAudit, setPromptAudit] = useState<PromptAuditEvent[]>([])
  const [topPerformers, setTopPerformers] = useState<TopPromptPerformer[]>([])

  const [selectedFolder, setSelectedFolder] = useState<string | null>(null)
  const [searchQuery, setSearchQuery] = useState('')
  const [quickFilter, setQuickFilter] = useState<QuickFilter>('all')
  const [showAllList, setShowAllList] = useState(false)
  const [isSidebarCollapsed, setIsSidebarCollapsed] = useState(false)
  const [isMobile, setIsMobile] = useState(() => window.innerWidth <= 900)

  const [favorites, setFavorites] = useState<number[]>(() => {
    try { return JSON.parse(localStorage.getItem('prompt-favorites') || '[]') } catch { return [] }
  })

  const [openMenuId, setOpenMenuId] = useState<number | null>(null)
  const menuRef = useRef<HTMLDivElement | null>(null)

  // Add to collection
  const [collections, setCollections] = useState<{ id: string; name: string; emoji?: string }[]>([])
  const [showCollectionPicker, setShowCollectionPicker] = useState(false)
  const [collectionTargetId, setCollectionTargetId] = useState<number | null>(null)

  // Wizard state
  const [wizardName, setWizardName] = useState('')
  const [wizardCategory, setWizardCategory] = useState('')
  const [wizardIntent, setWizardIntent] = useState('')
  const [wizardStyle, setWizardStyle] = useState('Professional')
  const [wizardSuggestionMode, setWizardSuggestionMode] = useState<'Intent' | 'Goals'>('Intent')
  const [wizardFiles, setWizardFiles] = useState<File[]>([])
  const [isDragOver, setIsDragOver] = useState(false)
  const [wizardStep, setWizardStep] = useState<1 | 2 | 3>(1)
  const [wizardPersonaLenses, setWizardPersonaLenses] = useState<{ id: string; title: string; category: string; description: string; systemPrompt: string }[]>([])
  const [wizardSelectedPersonaId, setWizardSelectedPersonaId] = useState<string>('default')
  const [wizardGoals, setWizardGoals] = useState<string[]>([])
  const [wizardSelectedGoals, setWizardSelectedGoals] = useState<string[]>([])
  const [wizardAdditionalContext, setWizardAdditionalContext] = useState('')
  const [wizardGeneratedContent, setWizardGeneratedContent] = useState('')
  const [editingPromptId, setEditingPromptId] = useState<number | null>(null)
  const [loadingSuggestions, setLoadingSuggestions] = useState(false)
  const [generatingPrompt, setGeneratingPrompt] = useState(false)
  const intentRef = useRef<HTMLTextAreaElement>(null)

  const NO_FOLDER = '(No folder)'
  const getFolderName = (name: string) => name.includes('/') ? name.split('/')[0] : NO_FOLDER

  const folders = useMemo(() => {
    const map = new Map<string, number>()
    prompts.forEach(p => {
      const f = getFolderName(p.name)
      map.set(f, (map.get(f) ?? 0) + 1)
    })
    return Array.from(map.entries()).sort((a, b) => {
      if (a[0] === NO_FOLDER) return 1
      if (b[0] === NO_FOLDER) return -1
      return a[0].localeCompare(b[0])
    })
  }, [prompts])

  const sevenDaysAgo = useMemo(() => { const d = new Date(); d.setDate(d.getDate() - 7); return d }, [])
  const recentCount = useMemo(() => prompts.filter(p => new Date(p.updatedAt || p.createdAt) >= sevenDaysAgo).length, [prompts, sevenDaysAgo])
  const hasFolders = folders.some(([name]) => name !== NO_FOLDER)

  const filteredPrompts = useMemo(() => prompts.filter(p => {
    if (quickFilter === 'favorites' && !favorites.includes(p.id)) return false
    if (quickFilter === 'shared' && !p.isPublic) return false
    if (quickFilter === 'recent' && new Date(p.updatedAt || p.createdAt) < sevenDaysAgo) return false
    if (quickFilter === 'all' && selectedFolder !== null && getFolderName(p.name) !== selectedFolder) return false
    if (searchQuery.trim()) {
      const q = searchQuery.toLowerCase()
      if (!p.name.toLowerCase().includes(q) && !p.content.toLowerCase().includes(q)) return false
    }
    return true
  }), [prompts, quickFilter, favorites, selectedFolder, searchQuery, sevenDaysAgo])

  const displayedPrompts = showAllList ? filteredPrompts : filteredPrompts.slice(0, 6)
  const editingPrompt = useMemo(
    () => prompts.find(prompt => prompt.id === editingPromptId) ?? null,
    [prompts, editingPromptId]
  )
  const isRequestApprovalDisabled = editingPrompt?.approvalStatus === 'pending' || editingPrompt?.approvalStatus === 'approved'

  const toggleFavorite = (id: number, e: React.MouseEvent) => {
    e.stopPropagation()
    setFavorites(prev => {
      const next = prev.includes(id) ? prev.filter(f => f !== id) : [...prev, id]
      localStorage.setItem('prompt-favorites', JSON.stringify(next))
      return next
    })
  }

  const showToast = (message: string) => {
    setToast({ message, visible: true })
    setTimeout(() => setToast({ message: '', visible: false }), 2000)
  }

  const showConfirmation = (message: string, action: () => void, buttonText = 'Confirm') => {
    setConfirmMessage(message); setConfirmAction(() => action)
    setConfirmButtonText(buttonText); setShowConfirmModal(true)
  }

  const handleCopyPrompt = async (content: string) => {
    try { await navigator.clipboard.writeText(content); showToast('Copied!') }
    catch { showToast('Failed to copy') }
  }

  const handleSharePrompt = async (id: number) =>
    showConfirmation('Share this prompt to the public library?', async () => {
      try { await api.post(`/prompts/${id}/share`); showToast('Shared!'); fetchPrompts() }
      catch { showToast('Failed to share') }
    }, 'Share')

  const handleUnsharePrompt = async (id: number) =>
    showConfirmation('Remove this prompt from the public library?', async () => {
      try { await api.post(`/prompts/${id}/unshare`); showToast('Removed'); fetchPrompts() }
      catch { showToast('Failed to unshare') }
    }, 'OK')

  const toggleBulkMode = () => { setBulkMode(!bulkMode); setSelectedPrompts([]) }
  const togglePromptSelection = (id: number) =>
    setSelectedPrompts(prev => prev.includes(id) ? prev.filter(p => p !== id) : [...prev, id])
  const selectAllPrompts = () =>
    setSelectedPrompts(selectedPrompts.length === filteredPrompts.length && filteredPrompts.length > 0 ? [] : filteredPrompts.map(p => p.id))

  const handleBulkDelete = async () => {
    if (!selectedPrompts.length) return
    try {
      await api.post('/prompts/bulk/delete', { ids: selectedPrompts })
      showToast(`${selectedPrompts.length} prompts moved to recycler`)
      setSelectedPrompts([]); setBulkMode(false); fetchPrompts(); fetchDeletedPrompts()
    } catch { showToast('Failed to delete') }
  }

  const handleBulkExport = (format: 'json' | 'csv') => {
    const toExport = selectedPrompts.length > 0 ? prompts.filter(p => selectedPrompts.includes(p.id)) : filteredPrompts
    if (!toExport.length) { showToast('No prompts to export'); return }
    let blob: Blob; let filename: string
    if (format === 'json') {
      blob = new Blob([JSON.stringify(toExport.map(p => ({ name: p.name, content: p.content, category: p.category, createdAt: p.createdAt })), null, 2)], { type: 'application/json' })
      filename = `genprompt-export-${new Date().toISOString().slice(0, 10)}.json`
    } else {
      const esc = (s: string) => `"${s.replace(/"/g, '""')}"`
      blob = new Blob(['Name,Content,Category,Created At\n' + toExport.map(p => [esc(p.name), esc(p.content), esc(p.category || ''), p.createdAt].join(',')).join('\n')], { type: 'text/csv' })
      filename = `genprompt-export-${new Date().toISOString().slice(0, 10)}.csv`
    }
    const url = URL.createObjectURL(blob); const a = document.createElement('a')
    a.href = url; a.download = filename; a.click(); URL.revokeObjectURL(url)
    showToast(`Exported ${toExport.length} prompts`); setShowExportMenu(false)
  }

  const handleConfirm = () => { confirmAction?.(); setShowConfirmModal(false); setConfirmAction(null); setConfirmMessage('') }
  const handleCancelConfirm = () => { setShowConfirmModal(false); setConfirmAction(null); setConfirmMessage('') }

  const resetWizard = () => {
    setEditingPromptId(null)
    setWizardStep(1)
    setWizardName('')
    setWizardCategory('')
    setWizardIntent('')
    setWizardStyle('Professional')
    setWizardSuggestionMode('Intent')
    setWizardFiles([])
    setWizardPersonaLenses([])
    setWizardSelectedPersonaId('default')
    setWizardGoals([])
    setWizardSelectedGoals([])
    setWizardAdditionalContext('')
    setWizardGeneratedContent('')
  }

  const fetchPrompts = async () => {
    try {
      setLoading(true)
      const r = await api.get<Prompt[]>('/prompts')
      setPrompts(Array.isArray(r.data) ? r.data : [])
      setError(null)
    } catch { setPrompts([]); setError('Failed to load prompts') }
    finally { setLoading(false) }
  }

  const fetchCollections = async () => {
    try {
      const r = await api.get<{ id: string; name: string; emoji?: string }[]>('/collections')
      setCollections(r.data)
    } catch { /* silent */ }
  }

  const handleAddToCollection = async (collectionId: string) => {
    if (!collectionTargetId) return
    try {
      await api.post(`/collections/${collectionId}/prompts/${collectionTargetId}`)
      showToast('Added to collection!')
    } catch (err: unknown) {
      const status = (err as { response?: { status?: number } })?.response?.status
      showToast(status === 409 ? 'Already in that collection' : 'Failed to add')
    }
    setShowCollectionPicker(false)
    setCollectionTargetId(null)
  }

  const loadPromptOps = async (promptId: string) => {
    try {
      const [versionsRes, auditRes] = await Promise.all([
        getPromptVersions(promptId),
        getPromptAudit(promptId)
      ])
      setPromptVersions(Array.isArray(versionsRes.data) ? versionsRes.data : [])
      setPromptAudit(Array.isArray(auditRes.data) ? auditRes.data : [])
    } catch {
      setPromptVersions([])
      setPromptAudit([])
    }
  }

  const loadTopPerformers = async () => {
    try {
      const r = await getTopPromptPerformers()
      setTopPerformers(Array.isArray(r.data) ? r.data : [])
    } catch {
      setTopPerformers([])
    }
  }

  const MEDIA_STUDIO_CATEGORIES = ['Image Generation', 'Video Script', 'Voice Prompt']
  const handleEdit = (prompt: Prompt) => {
    setOpenMenuId(null)
    if (MEDIA_STUDIO_CATEGORIES.includes(prompt.category)) {
      navigate(`/media-studio?load=${prompt.id}&category=${encodeURIComponent(prompt.category)}`)
      return
    }

    setEditingPromptId(prompt.id)
    setWizardName(prompt.name)
    setWizardCategory(prompt.category || '')
    setWizardIntent(prompt.content)
    setWizardGeneratedContent(prompt.content)
    setWizardGoals([])
    setWizardSelectedGoals([])
    setWizardAdditionalContext('')
    setWizardFiles([])
    setWizardPersonaLenses([])
    setWizardSelectedPersonaId('default')
    setWizardStep(1)
    loadPromptOps(String(prompt.id))
  }

  const handleRequestApproval = async () => {
    if (editingPromptId === null) return
    try {
      await requestPromptApproval(String(editingPromptId))
      showToast('Approval requested')
      await fetchPrompts()
      await loadPromptOps(String(editingPromptId))
    } catch {
      showToast('Failed to request approval')
    }
  }

  const handleApprovePrompt = async () => {
    if (editingPromptId === null) return
    try {
      await approvePrompt(String(editingPromptId))
      showToast('Prompt approved')
      await fetchPrompts()
      await loadPromptOps(String(editingPromptId))
    } catch {
      showToast('Failed to approve prompt')
    }
  }

  const handleDelete = (id: number) => {
    setOpenMenuId(null)
    showConfirmation('Move this prompt to the recycler?', async () => {
      try {
        const p = prompts.find(x => x.id === id)
        if (p) { await api.put(`/prompts/${id}`, { ...p, isActive: false }); fetchPrompts(); fetchDeletedPrompts() }
      } catch { setError('Failed to move prompt to recycler') }
    }, 'Delete')
  }

  const fetchDeletedPrompts = async () => {
    try { const r = await api.get<Prompt[]>('/prompts/deleted'); setDeletedPrompts(Array.isArray(r.data) ? r.data : []) }
    catch { /* non-fatal */ }
  }

  const handleRestore = async (id: number) => {
    try {
      const p = deletedPrompts.find(x => x.id === id)
      if (p) { await api.put(`/prompts/${id}`, { ...p, isActive: true }); fetchPrompts(); fetchDeletedPrompts() }
    } catch { setError('Failed to restore') }
  }

  const handlePermanentDelete = (id: number) =>
    showConfirmation('Permanently delete? This cannot be undone.', async () => {
      try { await api.delete(`/prompts/${id}`); fetchDeletedPrompts() }
      catch { setError('Failed to permanently delete') }
    }, 'Delete')

  const handleImportFile = async (file: File) => {
    try {
      const text = await file.text(); let parsedData: any[] = []
      if (file.name.endsWith('.json')) { const json = JSON.parse(text); parsedData = Array.isArray(json) ? json : [json] }
      else if (file.name.endsWith('.csv')) {
        const lines = text.split('\n').filter(l => l.trim())
        const headers = lines[0].split(',').map(h => h.trim().toLowerCase())
        for (let i = 1; i < lines.length; i++) {
          const vals = lines[i].split(','); const obj: any = {}
          headers.forEach((h, idx) => { obj[h] = vals[idx]?.trim() || '' }); parsedData.push(obj)
        }
      } else { showToast('Unsupported format'); return }
      setImportPreview(parsedData.map((item: any) => ({
        name: item.name || item.title || 'Imported Prompt',
        content: item.content || item.prompt || item.text || '',
        category: item.category || 'Imported'
      })).filter(p => p.content.trim()))
      setImportFile(file)
    } catch { showToast('Failed to parse file') }
  }

  const handleConfirmImport = async () => {
    if (!importPreview.length) return
    setImporting(true)
    try {
      let ok = 0
      for (const p of importPreview) { try { await api.post('/prompts', p); ok++ } catch { /* skip */ } }
      if (ok > 0) { showToast(`Imported ${ok} prompt(s)`); fetchPrompts() }
      setShowImportModal(false); setImportFile(null); setImportPreview([])
    } catch { showToast('Import failed') } finally { setImporting(false) }
  }

  const handleGenerateSuggestions = useCallback(async () => {
    if (!wizardIntent.trim() || loadingSuggestions) return
    try {
      setLoadingSuggestions(true)
      setWizardPersonaLenses([])
      setWizardSelectedPersonaId('default')
      const [goalsRes, personasRes] = await Promise.allSettled([
        api.post<{ goals: string[]; suggestedStyle: string }>('/prompts/generate-suggestions', {
          intent: wizardIntent,
          style: wizardStyle.toLowerCase(),
          category: wizardCategory || undefined,
          mode: wizardSuggestionMode.toLowerCase()
        }),
        api.post<{ personas: { id: string; title: string; category: string; description: string; systemPrompt: string }[] }>('/prompts/suggest-personas', {
          intent: wizardIntent,
          style: wizardStyle.toLowerCase(),
          category: wizardCategory || undefined
        })
      ])
      if (goalsRes.status === 'fulfilled') {
        setWizardGoals(goalsRes.value.data.goals ?? [])
      } else {
        showToast('Failed to generate suggestions. Please try again.')
        return
      }
      if (personasRes.status === 'fulfilled') {
        setWizardPersonaLenses(personasRes.value.data.personas ?? [])
      }
      setWizardSelectedGoals([])
      setWizardAdditionalContext('')
      setWizardStep(2)
    } finally {
      setLoadingSuggestions(false)
    }
  }, [wizardIntent, wizardStyle, wizardCategory, wizardSuggestionMode, loadingSuggestions])

  const handleGeneratePrompt = async () => {
    if (!wizardSelectedGoals.length || generatingPrompt) return
    try {
      setGeneratingPrompt(true)
      const selectedLens = wizardPersonaLenses.find(p => p.id === wizardSelectedPersonaId)
      const response = await api.post<{ prompt: string }>('/prompts/generate-prompt', {
        intent: wizardIntent,
        selectedGoals: wizardSelectedGoals,
        additionalContext: wizardAdditionalContext.trim() || null,
        style: wizardStyle.toLowerCase(),
        personaId: null,
        personaInstruction: selectedLens?.systemPrompt || null
      })
      setWizardGeneratedContent(response.data.prompt)
      setWizardStep(3)
    } catch {
      showToast('Failed to generate prompt. Please try again.')
    } finally {
      setGeneratingPrompt(false)
    }
  }

  const handleSaveWizardPrompt = async () => {
    if (!wizardName.trim() || !wizardGeneratedContent.trim()) {
      showToast('Name and content are required')
      return
    }
    try {
      if (editingPromptId !== null) {
        const existingPrompt = prompts.find(prompt => prompt.id === editingPromptId)
        await api.put(`/prompts/${editingPromptId}`, {
          id: editingPromptId,
          name: wizardName,
          content: wizardGeneratedContent,
          category: wizardCategory || undefined,
          description: existingPrompt?.description,
          isActive: existingPrompt?.isActive ?? true,
          isTemplate: existingPrompt?.isTemplate ?? false,
          isPublic: existingPrompt?.isPublic ?? false
        })
        showToast('Prompt updated!')
      } else {
        await api.post('/prompts', {
          name: wizardName,
          content: wizardGeneratedContent,
          category: wizardCategory || undefined
        })
        showToast('Prompt saved!')
      }
      fetchPrompts()
      resetWizard()
    } catch {
      showToast('Failed to save prompt')
    }
  }

  const { setBreadcrumbs, setHeaderActions } = useLayoutControls()
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard' }])
    setHeaderActions(null)
    return () => { setBreadcrumbs([]); setHeaderActions(null) }
  }, [])

  useEffect(() => { fetchPrompts(); fetchDeletedPrompts(); loadTopPerformers(); fetchCollections() }, [])
  useEffect(() => { setShowAllList(false) }, [selectedFolder, searchQuery, quickFilter])

  useEffect(() => {
    const handleResize = () => setIsMobile(window.innerWidth <= 900)
    window.addEventListener('resize', handleResize)
    return () => window.removeEventListener('resize', handleResize)
  }, [])

  useEffect(() => {
    const close = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setOpenMenuId(null)
    }
    document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [])

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === 'Enter' && document.activeElement === intentRef.current) {
        handleGenerateSuggestions()
      }
    }
    document.addEventListener('keydown', handler)
    return () => document.removeEventListener('keydown', handler)
  }, [handleGenerateSuggestions])

  if (loading) return <div className="loading">Loading prompts...</div>

  const effectiveSidebarCollapsed = isSidebarCollapsed

  const filterTitle: Record<QuickFilter, string> = {
    all: selectedFolder ?? 'My Prompts',
    favorites: 'Favorites',
    recent: 'Recent',
    shared: 'Shared with me'
  }

  const compactToolbarButtonStyle = {
    display: 'inline-flex',
    alignItems: 'center',
    justifyContent: 'center',
    width: 34,
    height: 34,
    padding: 0,
    border: '1px solid #d1d5db',
    borderRadius: 9,
    fontSize: '0.78rem',
    lineHeight: 1,
    flexShrink: 0,
  } as const

  // ── Sidebar item ──
  const SidebarItem = ({ active, icon, label, count, onClick }: { active: boolean; icon: string; label: string; count: number; onClick: () => void }) => (
    <button onClick={onClick} style={{
      display: 'flex', alignItems: 'center', gap: effectiveSidebarCollapsed ? 0 : 10, width: '100%',
      justifyContent: effectiveSidebarCollapsed ? 'center' : 'flex-start',
      padding: effectiveSidebarCollapsed ? '10px 0' : '9px 12px', border: 'none', borderRadius: 10, cursor: 'pointer',
      background: active ? 'linear-gradient(135deg, #833ab4 0%, #833ab4 100%)' : 'transparent',
      color: active ? '#fff' : 'var(--pg-text)', fontSize: '0.875rem',
      fontWeight: active ? 600 : 400, textAlign: 'left', transition: 'all 0.15s',
      boxShadow: active ? '0 2px 8px rgba(131,58,180,0.3)' : 'none'
    }}
      title={label}
      onMouseEnter={e => { if (!active) (e.currentTarget as HTMLButtonElement).style.background = 'var(--pg-bg-secondary)' }}
      onMouseLeave={e => { if (!active) (e.currentTarget as HTMLButtonElement).style.background = 'transparent' }}
    >
      <PhosphorIcon className={`bi bi-${icon}`} style={{ fontSize: '0.9rem', flexShrink: 0 }} />
      {!effectiveSidebarCollapsed && (
        <>
          <span style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{label}</span>
          <span style={{
            fontSize: '0.73rem', fontWeight: 700,
            background: active ? 'rgba(255,255,255,0.2)' : 'var(--pg-bg-secondary)',
            color: active ? '#fff' : 'var(--pg-text-light)', borderRadius: 12, padding: '1px 8px', flexShrink: 0
          }}>{count}</span>
        </>
      )}
    </button>
  )

  // ── Compact prompt card ──
  const PromptCard = ({ prompt }: { prompt: Prompt }) => {
    const cs = getCategoryStyle(prompt.category)
    const isSelected = selectedPrompts.includes(prompt.id)
    return (
      <div style={{
        padding: '11px 12px', border: `1.5px solid ${isSelected ? '#833ab4' : '#d1d5db'}`,
        borderRadius: 10, cursor: 'pointer', transition: 'all 0.15s',
        background: isSelected ? 'rgba(131,58,180,0.04)' : 'var(--pg-bg-secondary)'
      }}
        onMouseEnter={e => { const d = e.currentTarget as HTMLDivElement; if (!isSelected) { d.style.borderColor = '#833ab4'; d.style.background = 'rgba(131,58,180,0.03)' } }}
        onMouseLeave={e => { const d = e.currentTarget as HTMLDivElement; if (!isSelected) { d.style.borderColor = '#d1d5db'; d.style.background = 'var(--pg-bg-secondary)' } }}
      >
        <div style={{ display: 'flex', alignItems: 'flex-start', gap: 9 }}>
          {bulkMode && (
            <input type="checkbox" checked={isSelected} onChange={() => togglePromptSelection(prompt.id)}
              style={{ marginTop: 3, flexShrink: 0 }} onClick={e => e.stopPropagation()}
            />
          )}
          {/* Category icon */}
          <div style={{
            width: 28, height: 28, borderRadius: 7, background: cs.bg, flexShrink: 0,
            display: 'flex', alignItems: 'center', justifyContent: 'center'
          }}>
            <PhosphorIcon className={`bi bi-${cs.icon}`} style={{ color: '#fff', fontSize: '0.75rem' }} />
          </div>
          <div style={{ flex: 1, minWidth: 0 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 2 }}>
              <span style={{ fontWeight: 600, fontSize: '0.865rem', color: 'var(--pg-text)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flex: 1 }}>
                {prompt.name}
              </span>
              {prompt.currentVersion && <span style={{ fontSize: '0.64rem', fontWeight: 700, color: '#833ab4', border: '1px solid #ddd6fe', borderRadius: 20, padding: '1px 6px', background: '#f5f3ff', flexShrink: 0 }}>v{prompt.currentVersion}</span>}
              {prompt.approvalStatus && <span style={{ fontSize: '0.64rem', fontWeight: 700, color: prompt.approvalStatus === 'approved' ? '#16a34a' : '#b45309', border: '1px solid #fde68a', borderRadius: 20, padding: '1px 6px', background: '#fffbeb', flexShrink: 0 }}>{prompt.approvalStatus}</span>}
              <span style={{
                fontSize: '0.68rem', fontWeight: 600, color: '#16a34a',
                border: '1px solid #bbf7d0', borderRadius: 20, padding: '1px 6px',
                background: '#f0fdf4', flexShrink: 0
              }}>Active</span>
            </div>
            <p style={{
              margin: '0 0 4px 0', fontSize: '0.78rem', color: 'var(--pg-text-light)',
              overflow: 'hidden', display: '-webkit-box',
              WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', lineHeight: 1.45
            }}>{prompt.content}</p>
            <span style={{ fontSize: '0.73rem', color: 'var(--pg-text-light)' }}>
              {new Date(prompt.createdAt).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })}
            </span>
          </div>
          {/* 3-dot menu */}
          <div ref={openMenuId === prompt.id ? menuRef : undefined} style={{ position: 'relative', flexShrink: 0 }}>
            <button onClick={e => { e.stopPropagation(); setOpenMenuId(openMenuId === prompt.id ? null : prompt.id) }}
              style={{ border: 'none', background: 'none', cursor: 'pointer', padding: '2px 6px', borderRadius: 5, color: 'var(--pg-text-light)', fontSize: '1rem', lineHeight: 1 }}
            ><PhosphorIcon className="bi bi-three-dots" /></button>
            {openMenuId === prompt.id && (
              <div style={{
                position: 'absolute', right: 0, top: '100%', zIndex: 300,
                background: '#ffffff', border: '1px solid #e5e7eb',
                borderRadius: 10, boxShadow: '0 8px 24px rgba(0,0,0,0.12)',
                minWidth: 164, padding: '4px 0', marginTop: 4
              }}>
                <button className="export-dropdown-item" onClick={() => handleEdit(prompt)}><PhosphorIcon className="bi bi-pencil" /> Edit</button>
                <button className="export-dropdown-item" onClick={() => { setExecutionPrompt(prompt); setShowExecutionModal(true); setOpenMenuId(null) }}><PhosphorIcon className="bi bi-play-fill" /> Run</button>
                <button className="export-dropdown-item" onClick={() => { handleCopyPrompt(prompt.content); setOpenMenuId(null) }}><PhosphorIcon className="bi bi-clipboard" /> Copy</button>
                <button className="export-dropdown-item" onClick={e => { e.stopPropagation(); toggleFavorite(prompt.id, e); setOpenMenuId(null) }}>
                  <PhosphorIcon className={`bi bi-star${favorites.includes(prompt.id) ? '-fill' : ''}`} /> {favorites.includes(prompt.id) ? 'Unfavorite' : 'Favorite'}
                </button>
                {prompt.isPublic
                  ? <button className="export-dropdown-item" onClick={() => { setOpenMenuId(null); handleUnsharePrompt(prompt.id) }}><PhosphorIcon className="bi bi-lock" /> Unshare</button>
                  : <button className="export-dropdown-item" onClick={() => { setOpenMenuId(null); handleSharePrompt(prompt.id) }}><PhosphorIcon className="bi bi-share" /> Share</button>
                }
                <button className="export-dropdown-item" onClick={() => { setCollectionTargetId(prompt.id); setOpenMenuId(null); setShowCollectionPicker(true) }}><PhosphorIcon className="bi bi-collection" /> Add to Collection</button>
                <div style={{ borderTop: '1px solid #d1d5db', margin: '4px 0' }} />
                <button className="export-dropdown-item" style={{ color: '#ef4444' }} onClick={() => handleDelete(prompt.id)}><PhosphorIcon className="bi bi-trash" /> Delete</button>
              </div>
            )}
          </div>
        </div>
      </div>
    )
  }

  return (
    <>
      {showCollectionPicker && (
        <div className="modal-overlay" onClick={() => { setShowCollectionPicker(false); setCollectionTargetId(null) }}>
          <div className="modal-content" style={{ maxWidth: 380 }} onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <h2>Add to Collection</h2>
              <button className="modal-close" onClick={() => setShowCollectionPicker(false)} aria-label="Close"><PhosphorIcon className="bi bi-x" /></button>
            </div>
            <div className="modal-body">
              {collections.length === 0 ? (
                <p style={{ color: '#64748b', margin: 0 }}>No collections yet. <a href="/collections" style={{ color: '#d62976' }}>Create one first.</a></p>
              ) : (
                <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
                  {collections.map(c => (
                    <button key={c.id} onClick={() => handleAddToCollection(c.id)} style={{
                      display: 'flex', alignItems: 'center', gap: '0.6rem',
                      padding: '0.65rem 0.9rem', border: '1px solid #e2e8f0',
                      borderRadius: 8, background: '#fff', cursor: 'pointer',
                      fontSize: '0.9rem', fontWeight: 500, textAlign: 'left'
                    }}>
                      <span style={{ fontSize: '1.2rem' }}>{c.emoji || '📁'}</span>
                      {c.name}
                    </button>
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>
      )}

      {showConfirmModal && (
        <div className="modal-overlay" onClick={handleCancelConfirm}>
          <div className="modal-content confirm-modal" onClick={e => e.stopPropagation()}>
            <div className="modal-header"><h2><PhosphorIcon className="bi bi-exclamation-triangle" /> {t('common.confirm')}</h2></div>
            <div className="modal-body"><p className="confirm-message">{confirmMessage}</p></div>
            <div className="modal-footer">
              <button className="btn btn-secondary" onClick={handleCancelConfirm}>{t('common.cancel')}</button>
              <button className={`btn ${confirmButtonText === 'Delete' ? 'btn-danger' : 'btn-primary'}`} onClick={handleConfirm}>{confirmButtonText}</button>
            </div>
          </div>
        </div>
      )}

      {/* ══ MAIN 3-COLUMN LAYOUT ══ */}
      <div className="gp-dashboard-workspace" style={{ display: 'flex', flex: 1, flexDirection: isMobile ? 'column' : 'row' }}>

        {/* ── LEFT SIDEBAR ── */}
        <div className="gp-dashboard-rail" style={{
          width: isMobile ? '100%' : effectiveSidebarCollapsed ? 56 : 228,
          flexShrink: 0,
          borderRight: isMobile ? 'none' : '1px solid #d1d5db',
          borderBottom: isMobile ? '1px solid #d1d5db' : 'none',
          display: 'flex', flexDirection: 'column', overflowY: 'auto', padding: isMobile ? '14px 12px' : '18px 10px'
        }}>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: effectiveSidebarCollapsed ? 'center' : 'space-between', padding: '0 6px 10px' }}>
            {!effectiveSidebarCollapsed && (
              <div style={{ fontSize: '0.68rem', fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.1em', color: 'var(--pg-text-light)' }}>
                Quick Filters
              </div>
            )}
            <button
              type="button"
              onClick={() => setIsSidebarCollapsed(current => !current)}
              title={effectiveSidebarCollapsed ? 'Expand quick filters' : 'Collapse quick filters'}
              style={{
                border: '1px solid #d1d5db',
                background: 'var(--pg-bg-secondary)',
                cursor: 'pointer',
                color: 'var(--pg-text-light)',
                width: 30,
                height: 30,
                borderRadius: 8,
                display: 'inline-flex',
                alignItems: 'center',
                justifyContent: 'center',
                flexShrink: 0
              }}
            >
              <PhosphorIcon className={`bi ${isMobile ? (effectiveSidebarCollapsed ? 'bi-chevron-down' : 'bi-chevron-up') : `bi-layout-sidebar${effectiveSidebarCollapsed ? '-inset' : '-reverse'}`}`} />
            </button>
          </div>
          {(!isMobile || !effectiveSidebarCollapsed) && (
            <>
          <SidebarItem active={quickFilter === 'all' && selectedFolder === null} icon="collection-fill" label="My Prompts" count={prompts.length} onClick={() => { setQuickFilter('all'); setSelectedFolder(null) }} />
          <SidebarItem active={quickFilter === 'favorites'} icon="star" label="Favorites" count={favorites.filter(id => prompts.some(p => p.id === id)).length} onClick={() => { setQuickFilter('favorites'); setSelectedFolder(null) }} />
          <SidebarItem active={quickFilter === 'shared'} icon="people" label="Shared with me" count={prompts.filter(p => p.isPublic).length} onClick={() => { setQuickFilter('shared'); setSelectedFolder(null) }} />
          <SidebarItem active={quickFilter === 'recent'} icon="clock-history" label="Recent" count={recentCount} onClick={() => { setQuickFilter('recent'); setSelectedFolder(null) }} />
          <div style={{ margin: '10px 0', borderTop: '1px solid #d1d5db' }} />
          <SidebarItem active={showRecycler} icon="recycle" label="Recycler" count={deletedPrompts.length} onClick={() => { setShowRecycler(true); fetchDeletedPrompts() }} />

          {/* Folders */}
          <div style={{ marginTop: 18, paddingTop: 14, borderTop: '1px solid #d1d5db' }}>
            <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', padding: '0 6px 10px' }}>
              {!effectiveSidebarCollapsed && <span style={{ fontSize: '0.68rem', fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.1em', color: 'var(--pg-text-light)' }}>Folders</span>}
              <button onClick={() => showToast('Name a prompt "Folder/Prompt Name" to create a folder automatically')}
                style={{ border: 'none', background: 'none', cursor: 'pointer', color: 'var(--pg-text-light)', padding: '2px 5px', borderRadius: 4, fontSize: '0.9rem', lineHeight: 1, margin: effectiveSidebarCollapsed ? '0 auto' : 0 }}
                title="Create folder by naming prompts 'Folder/Name'"
              ><PhosphorIcon className="bi bi-plus" /></button>
            </div>
            {/* All Prompts row */}
            {(() => {
              const isA = quickFilter === 'all' && selectedFolder === null
              return (
                <button onClick={() => { setQuickFilter('all'); setSelectedFolder(null) }} style={{
                  display: 'flex', alignItems: 'center', gap: effectiveSidebarCollapsed ? 0 : 9, width: '100%',
                  justifyContent: effectiveSidebarCollapsed ? 'center' : 'flex-start',
                  padding: effectiveSidebarCollapsed ? '10px 0' : '8px 12px', border: 'none', borderRadius: 8, cursor: 'pointer',
                  background: isA ? 'rgba(214,41,118,0.1)' : 'transparent',
                  color: isA ? '#d62976' : 'var(--pg-text)', fontSize: '0.855rem', fontWeight: isA ? 600 : 400, textAlign: 'left'
                }}
                  title="All Prompts"
                  onMouseEnter={e => { (e.currentTarget as HTMLButtonElement).style.background = isA ? 'rgba(214,41,118,0.1)' : 'var(--pg-bg-secondary)' }}
                  onMouseLeave={e => { (e.currentTarget as HTMLButtonElement).style.background = isA ? 'rgba(214,41,118,0.1)' : 'transparent' }}
                >
                  <PhosphorIcon className="bi bi-folder2-open" style={{ color: '#d62976', fontSize: '0.85rem' }} />
                  {!effectiveSidebarCollapsed && (
                    <>
                      <span style={{ flex: 1 }}>All Prompts</span>
                      <span style={{ fontSize: '0.73rem', color: isA ? '#d62976' : 'var(--pg-text-light)' }}>{prompts.length}</span>
                    </>
                  )}
                </button>
              )
            })()}
            {!effectiveSidebarCollapsed && hasFolders && folders.filter(([f]) => f !== NO_FOLDER).map(([folder, count]) => {
              const isA = quickFilter === 'all' && selectedFolder === folder
              return (
                <button key={folder} onClick={() => { setQuickFilter('all'); setSelectedFolder(folder) }} style={{
                  display: 'flex', alignItems: 'center', gap: 9, width: '100%',
                  padding: '7px 12px', border: 'none', borderRadius: 8, cursor: 'pointer',
                  background: isA ? 'var(--pg-accent)' : 'transparent',
                  color: isA ? '#fff' : 'var(--pg-text)', fontSize: '0.855rem', fontWeight: isA ? 600 : 400, textAlign: 'left'
                }}
                  onMouseEnter={e => { if (!isA) (e.currentTarget as HTMLButtonElement).style.background = 'var(--pg-bg-secondary)' }}
                  onMouseLeave={e => { if (!isA) (e.currentTarget as HTMLButtonElement).style.background = 'transparent' }}
                >
                  <PhosphorIcon className="bi bi-folder2" style={{ color: isA ? '#fff' : '#94a3b8', fontSize: '0.85rem' }} />
                  <span style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{folder}</span>
                  <span style={{ fontSize: '0.73rem', color: isA ? 'rgba(255,255,255,0.8)' : 'var(--pg-text-light)' }}>{count}</span>
                </button>
              )
            })}
          </div>
            </>
          )}
        </div>

        {/* ── MIDDLE: PROMPT LIST ── */}
        <div className="gp-dashboard-list-pane" style={{
          width: isMobile ? '100%' : 300,
          flexShrink: 0,
          borderRight: isMobile ? 'none' : '1px solid #d1d5db',
          borderBottom: isMobile ? '1px solid #d1d5db' : 'none',
          display: 'flex', flexDirection: 'column'
        }}>
          {/* Header */}
          <div style={{ padding: '18px 16px 12px', borderBottom: '1px solid #d1d5db', flexShrink: 0 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 4 }}>
              <h2 style={{ margin: 0, fontSize: '1.15rem', fontWeight: 700, color: 'var(--pg-text)' }}>
                {filterTitle[quickFilter]}
              </h2>
              <span style={{ background: '#10b981', color: '#fff', borderRadius: 20, padding: '1px 9px', fontSize: '0.8rem', fontWeight: 700 }}>
                {filteredPrompts.length}
              </span>
            </div>
            <p style={{ margin: '0 0 12px', fontSize: '0.8rem', color: 'var(--pg-text-light)' }}>
              Create, manage and organise your AI prompts in one place.
            </p>
            {/* Toolbar */}
            <div style={{ display: 'flex', gap: 6, flexWrap: isMobile ? 'wrap' : 'nowrap', overflowX: 'auto', paddingBottom: 2, scrollbarWidth: 'none', justifyContent: 'center' }}>
              <button className={`btn btn-secondary btn-sm ${bulkMode ? 'active' : ''}`} onClick={toggleBulkMode} title="Select" style={compactToolbarButtonStyle}>
                <PhosphorIcon className="bi bi-check-square" />
              </button>
              <button className="btn btn-secondary btn-sm" onClick={() => setShowImportModal(true)} title="Import" style={compactToolbarButtonStyle}>
                <PhosphorIcon className="bi bi-upload" />
              </button>
              <div style={{ position: 'relative', flexShrink: 0 }}>
                <button className="btn btn-secondary btn-sm" onClick={() => setShowExportMenu(!showExportMenu)} title="Export" style={compactToolbarButtonStyle}>
                  <PhosphorIcon className="bi bi-download" />
                </button>
                {showExportMenu && (
                  <div className="export-dropdown-menu" style={{ right: 0, left: 'auto', zIndex: 200 }}>
                    <button className="export-dropdown-item" onClick={() => handleBulkExport('json')}><PhosphorIcon className="bi bi-filetype-json" /> JSON</button>
                    <button className="export-dropdown-item" onClick={() => handleBulkExport('csv')}><PhosphorIcon className="bi bi-filetype-csv" /> CSV</button>
                  </div>
                )}
              </div>
              <Link to="/library" className="btn btn-secondary btn-sm" title="Library" style={compactToolbarButtonStyle}>
                <PhosphorIcon className="bi bi-grid" />
              </Link>
            </div>
          </div>

          {/* Search bar */}
          <div style={{ padding: '10px 14px', borderBottom: '1px solid #d1d5db', flexShrink: 0 }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 7, background: 'var(--pg-bg-secondary)', border: '1px solid #d1d5db', borderRadius: 8, padding: '7px 11px' }}>
              <PhosphorIcon className="bi bi-search" style={{ color: 'var(--pg-text-light)', fontSize: '0.8rem', flexShrink: 0 }} />
              <input type="text" placeholder="Search prompts…" value={searchQuery} onChange={e => setSearchQuery(e.target.value)}
                style={{ flex: 1, border: 'none', outline: 'none', background: 'transparent', fontSize: '0.82rem', color: 'var(--pg-text)' }}
              />
              {searchQuery && <button onClick={() => setSearchQuery('')} style={{ border: 'none', background: 'none', cursor: 'pointer', color: 'var(--pg-text-light)', padding: 0 }}><PhosphorIcon className="bi bi-x-circle-fill" style={{ fontSize: '0.8rem' }} /></button>}
            </div>
          </div>

          {error && (
            <div style={{ margin: '8px 14px', padding: '8px 12px', background: '#fee2e2', borderRadius: 7, fontSize: '0.8rem', color: '#b91c1c', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              {error}
              <button onClick={() => setError(null)} style={{ border: 'none', background: 'none', cursor: 'pointer', color: '#b91c1c' }}><PhosphorIcon className="bi bi-x" /></button>
            </div>
          )}

          {/* Bulk bar */}
          {bulkMode && (
            <div style={{ padding: '8px 14px', borderBottom: '1px solid #d1d5db', background: 'var(--pg-bg-secondary)', flexShrink: 0 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: '0.8rem' }}>
                <label style={{ display: 'flex', alignItems: 'center', gap: 5, cursor: 'pointer', flex: 1, color: 'var(--pg-text)' }}>
                  <input type="checkbox" checked={selectedPrompts.length === filteredPrompts.length && filteredPrompts.length > 0} onChange={selectAllPrompts} />
                  All ({selectedPrompts.length}/{filteredPrompts.length})
                </label>
                <button className="btn btn-danger btn-sm" onClick={handleBulkDelete} disabled={!selectedPrompts.length} style={{ fontSize: '0.75rem', padding: '3px 8px' }}>
                  <PhosphorIcon className="bi bi-trash" />
                </button>
              </div>
            </div>
          )}

          {/* Prompt list */}
          <div style={{ flex: 1, overflowY: 'auto', padding: '10px 12px', display: 'flex', flexDirection: 'column', gap: 8 }}>
            {filteredPrompts.length === 0 ? (
              <div style={{ textAlign: 'center', padding: '3rem 1rem', color: 'var(--pg-text-light)' }}>
                <PhosphorIcon className="bi bi-file-earmark-text" style={{ fontSize: '2rem', marginBottom: 8, display: 'block' }} />
                <p style={{ margin: 0, fontSize: '0.85rem' }}>{searchQuery ? 'No prompts match your search.' : 'No prompts yet. Create your first!'}</p>
              </div>
            ) : (
              <>
                {displayedPrompts.map(p => <PromptCard key={p.id} prompt={p} />)}
                {filteredPrompts.length > 6 && (
                  <button onClick={() => setShowAllList(!showAllList)} style={{
                    border: 'none', background: 'none', cursor: 'pointer', color: 'var(--pg-accent)',
                    fontSize: '0.82rem', fontWeight: 600, padding: '8px 0', display: 'flex', alignItems: 'center', gap: 5
                  }}>
                    {showAllList ? 'Show less' : `View All Prompts (${filteredPrompts.length})`}
                    <PhosphorIcon className={`bi bi-arrow-${showAllList ? 'up' : 'right'}`} />
                  </button>
                )}
              </>
            )}
          </div>
        </div>

        {/* ── RIGHT: CREATE NEW PROMPT WIZARD ── */}
        <div className="gp-dashboard-builder-pane" style={{ flex: 1, minWidth: 0, overflowY: 'auto', background: 'var(--pg-bg)' }}>
          <div className="gp-dashboard-builder-content" style={{ padding: isMobile ? '16px 12px 24px' : '20px 28px 28px' }}>

            {/* Wizard header */}
            <div style={{ display: 'flex', alignItems: isMobile ? 'flex-start' : 'center', flexDirection: isMobile ? 'column' : 'row', gap: isMobile ? 12 : 0, justifyContent: 'space-between', marginBottom: 20 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                <div style={{ width: 36, height: 36, borderRadius: 10, background: 'linear-gradient(135deg, #ede9fe, #c7d2fe)', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '1.1rem' }}><PhosphorIcon className="bi bi-stars" /></div>
                <div>
                  <h2 style={{ margin: 0, fontSize: '1.1rem', fontWeight: 700, color: 'var(--pg-text)' }}>{editingPromptId !== null ? 'Edit Prompt' : 'Prompt Builder'}</h2>
                  <p style={{ margin: 0, fontSize: '0.8rem', color: 'var(--pg-text-light)' }}>{editingPromptId !== null ? 'Update this prompt without leaving the dashboard' : 'Use the guided editor to build and refine prompts'}</p>
                </div>
              </div>
              <button
                type="button"
                onClick={resetWizard}
                style={{
                  display: 'inline-flex', alignItems: 'center', gap: 6, padding: '7px 14px',
                  border: '1.5px solid #d1d5db', borderRadius: 8, fontSize: '0.83rem',
                  fontWeight: 500, color: 'var(--pg-text)', textDecoration: 'none', background: 'var(--pg-bg-secondary)',
                  cursor: 'pointer'
                }}
              >
                <PhosphorIcon className={`bi ${editingPromptId !== null ? 'bi-x-lg' : 'bi-arrow-counterclockwise'}`} /> {editingPromptId !== null ? 'Cancel Edit' : 'Reset'}
              </button>
            </div>

            {/* Step indicator */}
            <div style={{ display: 'flex', alignItems: 'flex-start', flexWrap: isMobile ? 'wrap' : 'nowrap', gap: isMobile ? 10 : 0, marginBottom: 28 }}>
              {[
                { n: 1, label: 'Describe', sub: 'What do you want to create?' },
                { n: 2, label: 'Goals', sub: 'What should it achieve?' },
                { n: 3, label: 'Prompt', sub: 'Review and generate' }
              ].map((step, i) => (
                <div
                  key={step.n}
                  style={{ display: 'flex', alignItems: 'flex-start', flex: 1, cursor: editingPromptId !== null ? 'pointer' : 'default' }}
                  onClick={() => {
                    if (editingPromptId !== null) {
                      setWizardStep(step.n as 1 | 2 | 3)
                    }
                  }}
                >
                  <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'center', flex: 1 }}>
                    <div style={{
                      width: 34, height: 34, borderRadius: '50%', display: 'flex', alignItems: 'center', justifyContent: 'center',
                      fontWeight: 700, fontSize: '0.9rem', marginBottom: 6, flexShrink: 0,
                      background: wizardStep >= step.n ? '#833ab4' : 'transparent',
                      color: wizardStep >= step.n ? '#fff' : '#9ca3af',
                      border: wizardStep >= step.n ? 'none' : '2px solid #d1d5db'
                    }}>
                      {wizardStep > step.n ? <PhosphorIcon className="bi bi-check-lg" /> : step.n}
                    </div>
                    <span style={{ fontSize: '0.82rem', fontWeight: wizardStep === step.n ? 700 : 400, color: wizardStep >= step.n ? '#833ab4' : '#9ca3af', textAlign: 'center' }}>{step.label}</span>
                    <span style={{ fontSize: '0.72rem', color: '#9ca3af', textAlign: 'center', marginTop: 2 }}>{step.sub}</span>
                  </div>
                  {!isMobile && i < 2 && (
                    <div style={{ height: 1, background: wizardStep > step.n ? '#833ab4' : '#d1d5db', flex: 0, width: 40, flexShrink: 0, marginTop: 17 }} />
                  )}
                </div>
              ))}
            </div>

            {/* Files kept from the free PDF tools; renders nothing when empty. */}
            <SavedFilesPanel />

            <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1.2fr 0.8fr', gap: 16, marginBottom: 20 }}>
              <div style={{ border: '1.5px solid #d1d5db', borderRadius: 12, background: 'var(--pg-bg-secondary)', padding: '14px 16px' }}>
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 10, marginBottom: 10, flexWrap: 'wrap' }}>
                  <div>
                    <div style={{ fontWeight: 700, fontSize: '0.92rem', color: 'var(--pg-text)' }}>Prompt Ops</div>
                    <div style={{ fontSize: '0.76rem', color: 'var(--pg-text-light)' }}>Approvals, versions, and audit activity for the prompt being edited.</div>
                  </div>
                  {editingPromptId !== null && (
                    <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                      <button
                        type="button"
                        onClick={handleRequestApproval}
                        disabled={isRequestApprovalDisabled}
                        style={{
                          border: '1px solid #d1d5db',
                          background: isRequestApprovalDisabled ? '#9ca3af' : 'var(--pg-bg)',
                          color: isRequestApprovalDisabled ? '#fff' : 'var(--pg-text)',
                          borderRadius: 8,
                          padding: '6px 10px',
                          cursor: isRequestApprovalDisabled ? 'not-allowed' : 'pointer',
                          fontSize: '0.78rem',
                          opacity: 1
                        }}
                      >
                        Request Approval
                      </button>
                      <button type="button" onClick={handleApprovePrompt} style={{ border: 'none', background: 'linear-gradient(135deg, #10b981, #059669)', color: '#fff', borderRadius: 8, padding: '6px 10px', cursor: 'pointer', fontSize: '0.78rem', fontWeight: 700 }}>Approve</button>
                    </div>
                  )}
                </div>
                {editingPromptId !== null && (
                  <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: 12 }}>
                    <div>
                      <div style={{ fontSize: '0.76rem', fontWeight: 700, textTransform: 'uppercase', color: 'var(--pg-text-light)', marginBottom: 8 }}>Versions</div>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: 8, maxHeight: 180, overflowY: 'auto', paddingRight: 4 }}>
                        {promptVersions.length === 0 && <div style={{ fontSize: '0.8rem', color: 'var(--pg-text-light)' }}>No versions yet.</div>}
                        {promptVersions.map(version => (
                          <div key={`${version.versionNumber}-${version.createdAt}`} style={{ border: '1px solid #e5e7eb', borderRadius: 8, padding: '8px 10px', background: 'var(--pg-bg)' }}>
                            <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8, marginBottom: 4 }}>
                              <strong style={{ fontSize: '0.8rem' }}>v{version.versionNumber}</strong>
                              <span style={{ fontSize: '0.72rem', color: 'var(--pg-text-light)' }}>{new Date(version.createdAt).toLocaleDateString()}</span>
                            </div>
                            <div style={{ fontSize: '0.75rem', color: 'var(--pg-text-secondary)' }}>{version.status || 'draft'}{version.changeNote ? ` · ${version.changeNote}` : ''}</div>
                          </div>
                        ))}
                      </div>
                    </div>
                    <div>
                      <div style={{ fontSize: '0.76rem', fontWeight: 700, textTransform: 'uppercase', color: 'var(--pg-text-light)', marginBottom: 8 }}>Audit Trail</div>
                      <div style={{ display: 'flex', flexDirection: 'column', gap: 8, maxHeight: 180, overflowY: 'auto', paddingRight: 4 }}>
                        {promptAudit.length === 0 && <div style={{ fontSize: '0.8rem', color: 'var(--pg-text-light)' }}>No audit events yet.</div>}
                        {promptAudit.map((event, index) => (
                          <div key={`${event.eventType}-${event.createdAt}-${index}`} style={{ border: '1px solid #e5e7eb', borderRadius: 8, padding: '8px 10px', background: 'var(--pg-bg)' }}>
                            <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8, marginBottom: 4 }}>
                              <strong style={{ fontSize: '0.8rem', textTransform: 'capitalize' }}>{event.eventType.replace(/_/g, ' ')}</strong>
                              <span style={{ fontSize: '0.72rem', color: 'var(--pg-text-light)' }}>{new Date(event.createdAt).toLocaleString()}</span>
                            </div>
                            <div style={{ fontSize: '0.75rem', color: 'var(--pg-text-secondary)' }}>{event.detailsJson || 'No extra metadata recorded.'}</div>
                          </div>
                        ))}
                      </div>
                    </div>
                  </div>
                )}
              </div>

              <div style={{ border: '1.5px solid #d1d5db', borderRadius: 12, background: 'var(--pg-bg-secondary)', padding: '14px 16px' }}>
                <div style={{ fontWeight: 700, fontSize: '0.92rem', color: 'var(--pg-text)', marginBottom: 6 }}>Top Performers</div>
                <div style={{ fontSize: '0.76rem', color: 'var(--pg-text-light)', marginBottom: 10 }}>Prompt analytics snapshot from recent runs.</div>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                  {topPerformers.length === 0 && <div style={{ fontSize: '0.8rem', color: 'var(--pg-text-light)' }}>No analytics available yet.</div>}
                  {topPerformers.slice(0, 5).map(prompt => (
                    <div key={prompt.id} style={{ border: '1px solid #e5e7eb', borderRadius: 8, padding: '8px 10px', background: 'var(--pg-bg)' }}>
                      <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8 }}>
                        <strong style={{ fontSize: '0.8rem' }}>{prompt.name}</strong>
                        <span style={{ fontSize: '0.72rem', color: 'var(--pg-text-light)' }}>{prompt.executionCount ?? 0} runs</span>
                      </div>
                      <div style={{ fontSize: '0.75rem', color: 'var(--pg-text-secondary)' }}>{prompt.category || 'General'}{typeof prompt.averageScore === 'number' ? ` · score ${prompt.averageScore.toFixed(2)}` : ''}</div>
                    </div>
                  ))}
                </div>
              </div>
            </div>

            {/* ── STEP 1: Describe ── */}
            {wizardStep === 1 && (
              <>
                {/* Section 1: Describe Your Request */}
                <div style={{ border: '1.5px solid #d1d5db', borderRadius: 12, padding: '18px 20px', marginBottom: 16, background: 'var(--pg-bg-secondary)' }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 16 }}>
                    <div style={{ width: 24, height: 24, borderRadius: '50%', background: '#833ab4', color: '#fff', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '0.75rem', fontWeight: 700, flexShrink: 0 }}>1</div>
                    <span style={{ fontWeight: 700, fontSize: '0.95rem', color: 'var(--pg-text)' }}>Describe Your Request</span>
                  </div>
                  {/* Prompt Name */}
                  <div style={{ marginBottom: 14 }}>
                    <label style={{ fontSize: '0.78rem', fontWeight: 600, color: 'var(--pg-text-light)', display: 'block', marginBottom: 6 }}>Prompt Name</label>
                    <input
                      type="text"
                      value={wizardName}
                      onChange={e => setWizardName(e.target.value)}
                      placeholder="e.g. support/customer-bot or My Prompt"
                      style={{
                        width: '100%', padding: '9px 12px', borderRadius: 8,
                        border: '1.5px solid #d1d5db', background: 'var(--pg-bg)',
                        color: 'var(--pg-text)', fontSize: '0.875rem', boxSizing: 'border-box', outline: 'none', fontFamily: 'inherit'
                      }}
                      onFocus={e => (e.target as HTMLInputElement).style.borderColor = '#833ab4'}
                      onBlur={e => (e.target as HTMLInputElement).style.borderColor = '#d1d5db'}
                    />
                    <p style={{ margin: '4px 0 0', fontSize: '0.72rem', color: '#833ab4' }}>
                      Tip: use folder/name (e.g. marketing/email-reply) to organise into folders
                    </p>
                  </div>

                  {/* Category + Style side by side */}
                  <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: 14, marginTop: 14 }}>
                    <div>
                      <label style={{ fontSize: '0.78rem', fontWeight: 600, color: 'var(--pg-text-light)', display: 'block', marginBottom: 6 }}>Category</label>
                      <input
                        type="text"
                        value={wizardCategory}
                        onChange={e => setWizardCategory(e.target.value)}
                        placeholder="e.g. Support"
                        style={{
                          width: '100%', padding: '9px 12px', borderRadius: 8,
                          border: '1.5px solid #d1d5db', background: 'var(--pg-bg)',
                          color: 'var(--pg-text)', fontSize: '0.85rem', boxSizing: 'border-box', outline: 'none', fontFamily: 'inherit'
                        }}
                        onFocus={e => (e.target as HTMLInputElement).style.borderColor = '#833ab4'}
                        onBlur={e => (e.target as HTMLInputElement).style.borderColor = '#d1d5db'}
                      />
                    </div>
                    <div>
                      <label style={{ fontSize: '0.78rem', fontWeight: 600, color: 'var(--pg-text-light)', display: 'block', marginBottom: 6 }}>Style</label>
                      <div style={{ position: 'relative' }}>
                        <select value={wizardStyle} onChange={e => setWizardStyle(e.target.value)} style={{
                          width: '100%', padding: '9px 32px 9px 12px', borderRadius: 8,
                          border: '1.5px solid #d1d5db', background: 'var(--pg-bg)',
                          color: 'var(--pg-text)', fontSize: '0.85rem', appearance: 'none', cursor: 'pointer'
                        }}>
                          {STYLES.map(s => <option key={s} value={s}>{s}</option>)}
                        </select>
                        <PhosphorIcon className="bi bi-chevron-down" style={{ position: 'absolute', right: 10, top: '50%', transform: 'translateY(-50%)', pointerEvents: 'none', color: 'var(--pg-text-light)', fontSize: '0.75rem' }} />
                      </div>
                      <p style={{ margin: '4px 0 0', fontSize: '0.72rem', color: 'var(--pg-text-light)' }}>
                        {wizardStyle === 'Professional' ? 'Best for business & formal content' :
                          wizardStyle === 'Creative' ? 'Best for imaginative, expressive content' :
                            wizardStyle === 'Technical' ? 'Best for precise, detailed instructions' :
                              wizardStyle === 'Academic' ? 'Best for research & academic writing' :
                                'Best for friendly, conversational content'}
                      </p>
                    </div>
                  </div>
                  <p style={{ margin: '8px 0 0', fontSize: '0.72rem', color: '#833ab4' }}>
                    <PhosphorIcon className="bi bi-stars" /> AI will suggest personas when you generate suggestions
                  </p>
                </div>

                {/* Context Files */}
                <div style={{ border: '1.5px solid #d1d5db', borderRadius: 12, padding: '14px 18px', marginBottom: 16, background: 'var(--pg-bg-secondary)' }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
                    <PhosphorIcon className="bi bi-paperclip" style={{ color: 'var(--pg-text-light)', fontSize: '0.9rem' }} />
                    <span style={{ fontSize: '0.85rem', fontWeight: 600, color: 'var(--pg-text)' }}>Context Files</span>
                    <span style={{ fontSize: '0.75rem', color: 'var(--pg-text-light)', marginLeft: 2 }}>{wizardFiles.length}/5 • Optional</span>
                  </div>
                  <div
                    onDragOver={e => { e.preventDefault(); setIsDragOver(true) }}
                    onDragLeave={() => setIsDragOver(false)}
                    onDrop={e => {
                      e.preventDefault(); setIsDragOver(false)
                      const newFiles = Array.from(e.dataTransfer.files).slice(0, 5 - wizardFiles.length)
                      setWizardFiles(prev => [...prev, ...newFiles].slice(0, 5))
                    }}
                    onClick={() => {
                      const inp = document.createElement('input'); inp.type = 'file'; inp.multiple = true
                      inp.accept = '.txt,.md,.csv,.json,.pdf'
                      inp.onchange = e => {
                        const newFiles = Array.from((e.target as HTMLInputElement).files || []).slice(0, 5 - wizardFiles.length)
                        setWizardFiles(prev => [...prev, ...newFiles].slice(0, 5))
                      }; inp.click()
                    }}
                    style={{
                      border: `2px dashed ${isDragOver ? 'var(--pg-accent)' : '#c4c9d4'}`,
                      borderRadius: 10, padding: wizardFiles.length ? '10px 14px' : '18px 14px',
                      cursor: 'pointer', textAlign: 'center', transition: 'all 0.15s',
                      background: isDragOver ? 'rgba(131,58,180,0.04)' : 'transparent'
                    }}
                  >
                    {wizardFiles.length === 0 ? (
                      <>
                        <PhosphorIcon className="bi bi-file-earmark-plus" style={{ fontSize: '1.6rem', color: 'var(--pg-text-light)', display: 'block', marginBottom: 6 }} />
                        <p style={{ margin: '0 0 2px', fontSize: '0.83rem', color: 'var(--pg-text)', fontWeight: 500 }}>Drag & drop files here</p>
                        <p style={{ margin: 0, fontSize: '0.75rem', color: 'var(--pg-text-light)' }}>or <span style={{ color: 'var(--pg-accent)' }}>click to browse</span></p>
                        <p style={{ margin: '6px 0 0', fontSize: '0.7rem', color: 'var(--pg-text-light)' }}>TXT, MD, CSV, JSON, PDF — max 5MB • Read in browser, not uploaded</p>
                      </>
                    ) : (
                      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
                        {wizardFiles.map((f, i) => (
                          <div key={i} style={{ display: 'flex', alignItems: 'center', gap: 5, padding: '4px 8px', background: 'var(--pg-bg)', border: '1px solid #d1d5db', borderRadius: 6, fontSize: '0.78rem' }}>
                            <PhosphorIcon className="bi bi-file-earmark-text" style={{ color: 'var(--pg-accent)' }} />
                            <span style={{ maxWidth: 120, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{f.name}</span>
                            <button onClick={e => { e.stopPropagation(); setWizardFiles(prev => prev.filter((_, idx) => idx !== i)) }}
                              style={{ border: 'none', background: 'none', cursor: 'pointer', color: 'var(--pg-text-light)', padding: '0 2px', lineHeight: 1 }}>
                              <PhosphorIcon className="bi bi-x" />
                            </button>
                          </div>
                        ))}
                        <span style={{ fontSize: '0.75rem', color: 'var(--pg-accent)', alignSelf: 'center', marginLeft: 4 }}>+ Add more</span>
                      </div>
                    )}
                  </div>
                </div>

                {/* Describe Your Intent */}
                <div style={{ border: '1.5px solid #d1d5db', borderRadius: 12, padding: '14px 18px', marginBottom: 18, background: 'var(--pg-bg-secondary)' }}>
                  <div style={{ display: 'flex', alignItems: isMobile ? 'flex-start' : 'center', flexDirection: isMobile ? 'column' : 'row', gap: isMobile ? 10 : 0, justifyContent: 'space-between', marginBottom: 10 }}>
                    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                      <div style={{ width: 22, height: 22, borderRadius: '50%', border: '2px solid #d1d5db', color: '#9ca3af', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '0.72rem', fontWeight: 700, flexShrink: 0 }}>2</div>
                      <span style={{ fontWeight: 700, fontSize: '0.9rem', color: 'var(--pg-text)' }}>Describe Your Intent</span>
                    </div>
                    <div style={{ display: 'flex', alignItems: isMobile ? 'flex-start' : 'center', flexDirection: isMobile ? 'column' : 'row', gap: 6, width: isMobile ? '100%' : 'auto' }}>
                      <span style={{ fontSize: '0.75rem', color: 'var(--pg-text-light)' }}>Suggestion mode:</span>
                      <div style={{ display: 'flex', alignItems: 'center', gap: 6, width: isMobile ? '100%' : 'auto' }}>
                        <div style={{ position: 'relative' }}>
                          <select
                            value={wizardSuggestionMode}
                            onChange={e => setWizardSuggestionMode(e.target.value as 'Intent' | 'Goals')}
                            style={{
                              width: isMobile ? '100%' : 280,
                              maxWidth: '100%',
                              padding: '5px 28px 5px 10px', borderRadius: 6, fontSize: '0.78rem', fontWeight: 600,
                              border: '1.5px solid #d1d5db', background: 'var(--pg-bg)',
                              color: '#833ab4', appearance: 'none', cursor: 'pointer', outline: 'none'
                            }}
                          >
                            <option value="Intent">{SUGGESTION_MODE_COPY.Intent.optionLabel}</option>
                            <option value="Goals">{SUGGESTION_MODE_COPY.Goals.optionLabel}</option>
                          </select>
                          <PhosphorIcon className="bi bi-chevron-down" style={{ position: 'absolute', right: 6, top: '50%', transform: 'translateY(-50%)', pointerEvents: 'none', color: '#833ab4', fontSize: '0.65rem' }} />
                        </div>
                      </div>
                    </div>
                  </div>
                  <div style={{ position: 'relative' }}>
                    <textarea
                      ref={intentRef}
                      value={wizardIntent}
                      onChange={e => setWizardIntent(e.target.value)}
                      placeholder={SUGGESTION_MODE_COPY[wizardSuggestionMode].placeholder}
                      rows={4}
                      style={{
                        width: '100%', padding: '10px 40px 10px 4px', borderRadius: 0,
                        border: '1.5px solid #d1d5db', outline: 'none', background: 'var(--pg-bg)',
                        color: 'var(--pg-text)', fontSize: '0.875rem', resize: 'none',
                        fontFamily: 'inherit', lineHeight: 1.6, boxSizing: 'border-box'
                      }}
                    />
                    <button style={{ position: 'absolute', right: 10, bottom: 10, border: 'none', background: 'none', cursor: 'pointer', color: 'var(--pg-text-light)', fontSize: '1rem' }} title="Voice input (coming soon)">
                      <PhosphorIcon className="bi bi-mic" />
                    </button>
                  </div>
                </div>

                {/* Generate button */}
                <button
                  onClick={handleGenerateSuggestions}
                  disabled={!wizardIntent.trim() || loadingSuggestions}
                  style={{
                    width: '100%', padding: '13px 20px', border: 'none', borderRadius: 10,
                    cursor: wizardIntent.trim() && !loadingSuggestions ? 'pointer' : 'not-allowed',
                    background: wizardIntent.trim() && !loadingSuggestions ? 'linear-gradient(135deg, #833ab4 0%, #fa7e1e 100%)' : '#d1d5db',
                    color: '#fff', fontSize: '0.95rem', fontWeight: 700,
                    display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
                    boxShadow: wizardIntent.trim() && !loadingSuggestions ? '0 4px 14px rgba(131,58,180,0.35)' : 'none', transition: 'opacity 0.15s'
                  }}
                  onMouseEnter={e => { if (!loadingSuggestions) (e.currentTarget as HTMLButtonElement).style.opacity = '0.9' }}
                  onMouseLeave={e => (e.currentTarget as HTMLButtonElement).style.opacity = '1'}
                >
                  {loadingSuggestions
                    ? <><span className="pg-pill-spinner" /> Generating Suggestions…</>
                    : <><PhosphorIcon className="bi bi-stars" /> Generate AI Suggestions</>}
                </button>
                <p style={{ textAlign: 'center', margin: '8px 0 0', fontSize: '0.75rem', color: 'var(--pg-text-light)' }}>
                  Press <kbd style={{ background: 'var(--pg-bg-secondary)', border: '1px solid #d1d5db', borderRadius: 4, padding: '1px 5px', fontSize: '0.72rem' }}>Ctrl</kbd> + <kbd style={{ background: 'var(--pg-bg-secondary)', border: '1px solid #d1d5db', borderRadius: 4, padding: '1px 5px', fontSize: '0.72rem' }}>Enter</kbd> to generate
                </p>
                {editingPromptId !== null && (
                  <div style={{ display: 'flex', justifyContent: 'flex-end', marginTop: 10 }}>
                    <button
                      type="button"
                      onClick={() => setWizardStep(2)}
                      style={{ padding: '10px 16px', border: '1.5px solid #d1d5db', borderRadius: 8, background: 'var(--pg-bg)', color: 'var(--pg-text)', fontSize: '0.85rem', fontWeight: 600, cursor: 'pointer', display: 'flex', alignItems: 'center', gap: 6 }}
                    >
                      Continue to Goals <PhosphorIcon className="bi bi-arrow-right" />
                    </button>
                  </div>
                )}
              </>
            )}

            {/* ── STEP 2: Goals ── */}
            {wizardStep === 2 && (
              <>
              {/* Persona selection box */}
              {wizardPersonaLenses.length > 0 && (
                <div style={{ border: '1.5px solid #d1d5db', borderRadius: 12, padding: '16px 20px', marginBottom: 16, background: 'var(--pg-bg-secondary)' }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
                    <PhosphorIcon className="bi bi-person-bounding-box" style={{ color: '#833ab4', fontSize: '1rem' }} />
                    <span style={{ fontWeight: 700, fontSize: '0.92rem', color: 'var(--pg-text)' }}>Choose a Persona</span>
                    <span style={{ fontSize: '0.75rem', color: 'var(--pg-text-light)', marginLeft: 4 }}>AI-suggested for your intent</span>
                  </div>
                  <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : 'repeat(auto-fill, minmax(200px, 1fr))', gap: 8 }}>
                    {/* Default option */}
                    <button
                      type="button"
                      className="pg-persona-card"
                      data-selected={wizardSelectedPersonaId === 'default'}
                      onClick={() => setWizardSelectedPersonaId('default')}
                      style={{
                        padding: '10px 12px', borderRadius: 8, cursor: 'pointer',
                        border: `1.5px solid ${wizardSelectedPersonaId === 'default' ? '#833ab4' : '#d1d5db'}`,
                        background: wizardSelectedPersonaId === 'default' ? 'rgba(131,58,180,0.07)' : 'var(--pg-bg)',
                      }}
                    >
                      <div style={{ textAlign: 'center' }}>
                        <div style={{ fontSize: '0.7rem', fontWeight: 600, color: '#833ab4', textTransform: 'uppercase', marginBottom: 3 }}>Default</div>
                        <div style={{ fontWeight: 700, fontSize: '0.85rem', color: 'var(--pg-text)', marginBottom: 4 }}>Balanced AI</div>
                      </div>
                      <div style={{ fontSize: '0.75rem', color: 'var(--pg-text-light)', lineHeight: 1.4, textAlign: 'left' }}>Let the AI infer the best approach from your intent.</div>
                    </button>
                    {wizardPersonaLenses.map(lens => (
                      <button
                        key={lens.id}
                        type="button"
                        className="pg-persona-card"
                        data-selected={wizardSelectedPersonaId === lens.id}
                        onClick={() => setWizardSelectedPersonaId(lens.id)}
                        style={{
                          padding: '10px 12px', borderRadius: 8, cursor: 'pointer',
                          border: `1.5px solid ${wizardSelectedPersonaId === lens.id ? '#833ab4' : '#d1d5db'}`,
                          background: wizardSelectedPersonaId === lens.id ? 'rgba(131,58,180,0.07)' : 'var(--pg-bg)',
                        }}
                      >
                        <div style={{ textAlign: 'center' }}>
                          <div style={{ fontSize: '0.7rem', fontWeight: 600, color: '#833ab4', textTransform: 'uppercase', marginBottom: 3 }}>{lens.category}</div>
                          <div style={{ fontWeight: 700, fontSize: '0.85rem', color: 'var(--pg-text)', marginBottom: 4 }}>{lens.title}</div>
                        </div>
                        <div style={{ fontSize: '0.75rem', color: 'var(--pg-text-light)', lineHeight: 1.4, textAlign: 'left' }}>{lens.description}</div>
                      </button>
                    ))}
                  </div>
                </div>
              )}
              <div style={{ border: '1.5px solid #d1d5db', borderRadius: 12, padding: '18px 20px', background: 'var(--pg-bg-secondary)' }}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 14 }}>
                  <div style={{ width: 24, height: 24, borderRadius: '50%', background: '#833ab4', color: '#fff', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '0.75rem', fontWeight: 700, flexShrink: 0 }}>2</div>
                  <span style={{ fontWeight: 700, fontSize: '0.95rem', color: 'var(--pg-text)' }}>Select Your Goals</span>
                  <span style={{ marginLeft: 'auto', fontSize: '0.78rem', color: 'var(--pg-text-light)' }}>{wizardSelectedGoals.length} selected</span>
                </div>
                <p style={{ margin: '0 0 14px', fontSize: '0.82rem', color: 'var(--pg-text-light)' }}>
                  Select the goals that best match what you want to achieve.
                </p>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginBottom: 16 }}>
                  {wizardGoals.map((goal, i) => {
                    const sel = wizardSelectedGoals.includes(goal)
                    return (
                      <label key={i} style={{
                        display: 'flex', alignItems: 'flex-start', gap: 10, cursor: 'pointer',
                        padding: '10px 12px', borderRadius: 8,
                        border: `1.5px solid ${sel ? '#833ab4' : '#d1d5db'}`,
                        background: sel ? 'rgba(131,58,180,0.05)' : 'var(--pg-bg)',
                        transition: 'all 0.15s'
                      }}>
                        <input type="checkbox" checked={sel} onChange={() => setWizardSelectedGoals(prev => sel ? prev.filter(g => g !== goal) : [...prev, goal])} style={{ marginTop: 2, accentColor: '#833ab4', flexShrink: 0 }} />
                        <span style={{ fontSize: '0.87rem', color: 'var(--pg-text)', lineHeight: 1.45 }}>
                          <span style={{ fontWeight: 600, color: 'var(--pg-text-light)', marginRight: 6 }}>{i + 1}.</span>{goal}
                        </span>
                      </label>
                    )
                  })}
                </div>
                <div style={{ marginBottom: 16 }}>
                  <label style={{ fontSize: '0.78rem', fontWeight: 600, color: 'var(--pg-text-light)', display: 'block', marginBottom: 6 }}>Additional Context (optional)</label>
                  <input
                    type="text"
                    value={wizardAdditionalContext}
                    onChange={e => setWizardAdditionalContext(e.target.value)}
                    placeholder="Any extra context or constraints..."
                    style={{ width: '100%', padding: '9px 12px', borderRadius: 8, border: '1.5px solid #d1d5db', background: 'var(--pg-bg)', color: 'var(--pg-text)', fontSize: '0.875rem', boxSizing: 'border-box', outline: 'none', fontFamily: 'inherit' }}
                    onFocus={e => (e.target as HTMLInputElement).style.borderColor = '#833ab4'}
                    onBlur={e => (e.target as HTMLInputElement).style.borderColor = '#d1d5db'}
                  />
                </div>
                <div style={{ display: 'flex', gap: 10, flexDirection: isMobile ? 'column' : 'row' }}>
                  <button onClick={() => setWizardStep(1)} style={{ padding: '10px 16px', border: '1.5px solid #d1d5db', borderRadius: 8, background: 'var(--pg-bg)', color: 'var(--pg-text)', fontSize: '0.85rem', fontWeight: 600, cursor: 'pointer', display: 'flex', alignItems: 'center', gap: 6 }}>
                    <PhosphorIcon className="bi bi-arrow-left" /> Back
                  </button>
                  <button
                    onClick={handleGeneratePrompt}
                    disabled={!wizardSelectedGoals.length || generatingPrompt}
                    style={{
                      flex: 1, padding: '10px 20px', border: 'none', borderRadius: 8,
                      cursor: wizardSelectedGoals.length && !generatingPrompt ? 'pointer' : 'not-allowed',
                      background: wizardSelectedGoals.length ? 'linear-gradient(135deg, #833ab4 0%, #fa7e1e 100%)' : '#d1d5db',
                      color: '#fff', fontSize: '0.9rem', fontWeight: 700,
                      display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
                      boxShadow: wizardSelectedGoals.length ? '0 4px 14px rgba(131,58,180,0.35)' : 'none',
                      opacity: generatingPrompt ? 0.8 : 1
                    }}
                  >
                    {generatingPrompt ? <><span className="pg-pill-spinner" /> Generating Prompt…</> : <><PhosphorIcon className="bi bi-magic" /> Generate Prompt →</>}
                  </button>
                </div>
              </div>
              </>
            )}

            {/* ── STEP 3: Prompt ── */}
            {wizardStep === 3 && (
              <>
                {/* Name + Category */}
                <div style={{ border: '1.5px solid #d1d5db', borderRadius: 12, padding: '16px 20px', marginBottom: 16, background: 'var(--pg-bg-secondary)' }}>
                  <div style={{ display: 'grid', gridTemplateColumns: isMobile ? '1fr' : '1fr 1fr', gap: 14 }}>
                    <div>
                      <label style={{ fontSize: '0.78rem', fontWeight: 600, color: 'var(--pg-text-light)', display: 'block', marginBottom: 6 }}>Prompt Name *</label>
                      <input
                        type="text"
                        value={wizardName}
                        onChange={e => setWizardName(e.target.value)}
                        placeholder="e.g. support/customer-bot"
                        style={{
                          width: '100%', padding: '9px 12px', borderRadius: 8,
                          border: `1.5px solid ${!wizardName.trim() ? '#f87171' : '#d1d5db'}`,
                          background: 'var(--pg-bg)', color: 'var(--pg-text)', fontSize: '0.875rem',
                          boxSizing: 'border-box', outline: 'none', fontFamily: 'inherit'
                        }}
                        onFocus={e => (e.target as HTMLInputElement).style.borderColor = '#833ab4'}
                        onBlur={e => (e.target as HTMLInputElement).style.borderColor = !wizardName.trim() ? '#f87171' : '#d1d5db'}
                      />
                    </div>
                    <div>
                      <label style={{ fontSize: '0.78rem', fontWeight: 600, color: 'var(--pg-text-light)', display: 'block', marginBottom: 6 }}>Category</label>
                      <input
                        type="text"
                        value={wizardCategory}
                        onChange={e => setWizardCategory(e.target.value)}
                        placeholder="e.g. Support"
                        style={{
                          width: '100%', padding: '9px 12px', borderRadius: 8,
                          border: '1.5px solid #d1d5db', background: 'var(--pg-bg)',
                          color: 'var(--pg-text)', fontSize: '0.875rem', boxSizing: 'border-box', outline: 'none', fontFamily: 'inherit'
                        }}
                        onFocus={e => (e.target as HTMLInputElement).style.borderColor = '#833ab4'}
                        onBlur={e => (e.target as HTMLInputElement).style.borderColor = '#d1d5db'}
                      />
                    </div>
                  </div>
                </div>

                {/* Generated prompt */}
                <div style={{ border: '1.5px solid #d1d5db', borderRadius: 12, padding: '16px 20px', marginBottom: 16, background: 'var(--pg-bg-secondary)' }}>
                  <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 10 }}>
                    <div style={{ width: 24, height: 24, borderRadius: '50%', background: '#833ab4', color: '#fff', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '0.75rem', fontWeight: 700, flexShrink: 0 }}>3</div>
                    <span style={{ fontWeight: 700, fontSize: '0.95rem', color: 'var(--pg-text)' }}>{editingPromptId !== null ? 'Edit Your Prompt' : 'Your Generated Prompt'}</span>
                    <button
                      onClick={async () => { try { await navigator.clipboard.writeText(wizardGeneratedContent); showToast('Copied!') } catch { showToast('Failed to copy') } }}
                      style={{ marginLeft: 'auto', border: '1px solid #d1d5db', background: 'var(--pg-bg)', borderRadius: 6, padding: '4px 10px', fontSize: '0.78rem', cursor: 'pointer', color: 'var(--pg-text-light)', display: 'flex', alignItems: 'center', gap: 5 }}
                    >
                      <PhosphorIcon className="bi bi-clipboard" /> Copy
                    </button>
                  </div>
                  <textarea
                    value={wizardGeneratedContent}
                    onChange={e => setWizardGeneratedContent(e.target.value)}
                    rows={12}
                    style={{
                      width: '100%', padding: '10px 12px', borderRadius: 8,
                      border: '1.5px solid #d1d5db', background: 'var(--pg-bg)',
                      color: 'var(--pg-text)', fontSize: '0.875rem', resize: 'vertical',
                      fontFamily: 'inherit', lineHeight: 1.6, boxSizing: 'border-box', outline: 'none'
                    }}
                    onFocus={e => (e.target as HTMLTextAreaElement).style.borderColor = '#833ab4'}
                    onBlur={e => (e.target as HTMLTextAreaElement).style.borderColor = '#d1d5db'}
                  />
                  <p style={{ margin: '6px 0 0', fontSize: '0.75rem', color: 'var(--pg-text-light)' }}>
                    {wizardGeneratedContent.length} chars · ~{Math.ceil(wizardGeneratedContent.trim().split(/\s+/).filter(Boolean).length * 1.3)} tokens
                  </p>
                </div>

                {/* Actions */}
                <div style={{ display: 'flex', gap: 10, flexDirection: isMobile ? 'column' : 'row' }}>
                  <button onClick={() => setWizardStep(2)} style={{ padding: '10px 16px', border: '1.5px solid #d1d5db', borderRadius: 8, background: 'var(--pg-bg)', color: 'var(--pg-text)', fontSize: '0.85rem', fontWeight: 600, cursor: 'pointer', display: 'flex', alignItems: 'center', gap: 6 }}>
                    <PhosphorIcon className="bi bi-arrow-left" /> Back
                  </button>
                  <button onClick={resetWizard} style={{ padding: '10px 16px', border: '1.5px solid #d1d5db', borderRadius: 8, background: 'var(--pg-bg)', color: 'var(--pg-text)', fontSize: '0.85rem', fontWeight: 600, cursor: 'pointer' }}>
                    {editingPromptId !== null ? 'Cancel Edit' : 'Start Over'}
                  </button>
                  <button
                    onClick={handleSaveWizardPrompt}
                    disabled={!wizardName.trim() || !wizardGeneratedContent.trim()}
                    style={{
                      flex: 1, padding: '10px 20px', border: 'none', borderRadius: 8,
                      cursor: wizardName.trim() && wizardGeneratedContent.trim() ? 'pointer' : 'not-allowed',
                      background: wizardName.trim() && wizardGeneratedContent.trim() ? 'linear-gradient(135deg, #10b981 0%, #059669 100%)' : '#d1d5db',
                      color: '#fff', fontSize: '0.9rem', fontWeight: 700,
                      display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 8,
                      boxShadow: wizardName.trim() && wizardGeneratedContent.trim() ? '0 4px 14px rgba(16,185,129,0.35)' : 'none'
                    }}
                  >
                    <PhosphorIcon className="bi bi-floppy" /> {editingPromptId !== null ? 'Update Prompt' : 'Save Prompt'}
                  </button>
                </div>
              </>
            )}
          </div>
        </div>
      </div>

      {/* ══ MODALS ══ */}

      {showRecycler && (
        <div className="modal-overlay" onClick={() => setShowRecycler(false)}>
          <div className="modal-content" onClick={e => e.stopPropagation()}>
            <div className="modal-header"><h2><PhosphorIcon className="bi bi-recycle" /> Recycler</h2>
              <button className="modal-close" onClick={() => setShowRecycler(false)}><PhosphorIcon className="bi bi-x-lg" /></button>
            </div>
            <div className="modal-body">
              {deletedPrompts.length === 0
                ? <p style={{ textAlign: 'center', padding: '2rem', color: '#65676b' }}>No deleted prompts</p>
                : <div className="recycler-list">{deletedPrompts.map(p => (
                  <div key={p.id} className="recycler-item">
                    <div className="recycler-info"><h3>{p.name}</h3>{p.category && <span className="prompt-category">{p.category}</span>}<p style={{ fontSize: '0.85rem', color: '#888', marginTop: '0.5rem' }}>Deleted: {new Date(p.updatedAt).toLocaleString()}</p></div>
                    <div className="recycler-actions">
                      <button className="btn btn-success" onClick={() => handleRestore(p.id)} style={{ fontSize: '0.9rem' }}><PhosphorIcon className="bi bi-arrow-counterclockwise" /> Restore</button>
                      <button className="btn btn-danger" onClick={() => handlePermanentDelete(p.id)} style={{ fontSize: '0.9rem' }}><PhosphorIcon className="bi bi-trash" /> Delete Forever</button>
                    </div>
                  </div>
                ))}</div>}
            </div>
          </div>
        </div>
      )}

      {showImportModal && (
        <div className="modal-overlay" onClick={() => !importing && setShowImportModal(false)}>
          <div className="modal-content modal-wide" onClick={e => e.stopPropagation()}>
            <div className="modal-header">
              <h2><PhosphorIcon className="bi bi-upload" /> {t('common.upload')}</h2>
              <button type="button" className="btn-close" onClick={() => setShowImportModal(false)} disabled={importing}><PhosphorIcon className="bi bi-x-lg" /></button>
            </div>
            <div className="modal-body">
              {!importFile ? (
                <div style={{ textAlign: 'center', padding: '2rem' }}>
                  <div style={{ border: '2px dashed #d1d5db', borderRadius: 8, padding: '3rem 2rem', cursor: 'pointer' }}
                    onDragOver={e => { e.preventDefault(); e.currentTarget.style.borderColor = 'var(--pg-accent)' }}
                    onDragLeave={e => { e.currentTarget.style.borderColor = '#d1d5db' }}
                    onDrop={e => { e.preventDefault(); e.currentTarget.style.borderColor = '#d1d5db'; const f = e.dataTransfer.files[0]; if (f) handleImportFile(f) }}
                    onClick={() => { const i = document.createElement('input'); i.type = 'file'; i.accept = '.json,.csv'; i.onchange = e => { const f = (e.target as HTMLInputElement).files?.[0]; if (f) handleImportFile(f) }; i.click() }}
                  >
                    <PhosphorIcon className="bi bi-cloud-upload" style={{ fontSize: '3rem', color: 'var(--pg-accent)', marginBottom: '1rem', display: 'block' }} />
                    <h3 style={{ margin: '0 0 0.5rem 0' }}>Drop your file here or click to browse</h3>
                    <p style={{ margin: 0, color: 'var(--pg-text-light)', fontSize: '0.9rem' }}>Supports JSON and CSV formats</p>
                  </div>
                </div>
              ) : (
                <div>
                  <div style={{ padding: '1rem', background: 'var(--pg-bg-secondary)', borderRadius: 8, marginBottom: '1rem', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                    <div><PhosphorIcon className="bi bi-file-earmark-text" style={{ marginRight: '0.5rem', color: 'var(--pg-accent)' }} /><strong>{importFile.name}</strong><span style={{ marginLeft: '1rem', color: 'var(--pg-text-light)', fontSize: '0.9rem' }}>{importPreview.length} prompt(s) found</span></div>
                    <button type="button" className="btn btn-secondary btn-sm" onClick={() => { setImportFile(null); setImportPreview([]) }} disabled={importing}><PhosphorIcon className="bi bi-x" /> Remove</button>
                  </div>
                  <div style={{ maxHeight: 400, overflow: 'auto' }}>
                    {importPreview.map((p, i) => (
                      <div key={i} style={{ padding: '1rem', marginBottom: '0.75rem', border: '1px solid #d1d5db', borderRadius: 8 }}>
                        <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: '0.5rem' }}>
                          <strong>{p.name}</strong>
                          {p.category && <span style={{ padding: '0.25rem 0.75rem', background: 'var(--pg-accent)', color: '#fff', borderRadius: 12, fontSize: '0.75rem' }}>{p.category}</span>}
                        </div>
                        <p style={{ margin: 0, fontSize: '0.85rem', color: 'var(--pg-text-light)', overflow: 'hidden', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical' }}>{p.content}</p>
                      </div>
                    ))}
                  </div>
                </div>
              )}
            </div>
            <div className="modal-footer">
              <button className="btn btn-secondary" onClick={() => { setShowImportModal(false); setImportFile(null); setImportPreview([]) }} disabled={importing}>{t('common.cancel')}</button>
              {importFile && (
                <button className="btn btn-primary" onClick={handleConfirmImport} disabled={importing || !importPreview.length}>
                  {importing ? <><span className="pg-pill-spinner" /> Importing…</> : <><PhosphorIcon className="bi bi-check-lg" /> Import {importPreview.length} Prompt{importPreview.length !== 1 ? 's' : ''}</>}
                </button>
              )}
            </div>
          </div>
        </div>
      )}

      {toast.visible && <div className="toast-notification">{toast.message}</div>}

      {showExecutionModal && executionPrompt && (
        <ExecutionModal
          isOpen={showExecutionModal}
          onClose={() => { setShowExecutionModal(false); setExecutionPrompt(null) }}
          prompt={{ id: String(executionPrompt.id), name: executionPrompt.name, content: executionPrompt.content, isTemplate: executionPrompt.isTemplate }}
        />
      )}
    </>
  )
}

export default Dashboard
