import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useRef, useCallback } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { usePageMeta } from '../utils/usePageMeta'
import SiteFooter from '../components/SiteFooter'
import AdSense from '../components/AdSense'
import PromptPicker from '../components/PromptPicker'
import GenPromptLogoMark from '../components/GenPromptLogoMark'
import authService from '../services/authService'
import api from '../services/api'
import { PREBUILT_SKILLS, SKILL_CATEGORIES, generateSkillMarkdown, type Skill } from '../data/skills'
import '../App.css'
import './LandingPage.css'

interface ApiSkill {
  id: string
  name: string
  instruction: string
  category: string | null
  description: string | null
  tags: string[]
  usageCount: number
  likeCount: number
  createdAt: string
  authorName: string
  authorPicture: string | null
  isOwner: boolean
}

function apiSkillToSkill(s: ApiSkill): Skill {
  return {
    id: s.id,
    name: s.name,
    description: s.description ?? s.name,
    category: s.category ?? 'community',
    categoryIcon: SKILL_CATEGORIES.find(c => c.id === s.category)?.icon ?? 'bi-people',
    icon: 'bi-person-gear',
    tags: s.tags,
    instruction: s.instruction,
    exampleIntent: '',
  }
}

function Skills() {
  usePageMeta(
    'AI Skills Library – 40+ Free Prompt Skill Packs | GenPrompt',
    'Browse 40+ free AI skill packs built by domain experts. Apply instantly in the Prompt Builder or download as Claude-compatible .md files for Claude Desktop, Cursor, and VS Code Copilot. Covers Marketing, Engineering, Legal, Finance, HR, and more.',
    `${window.location.origin}/skills`
  )

  const navigate = useNavigate()
  const isAuthenticated = authService.isAuthenticated()
  const panelRef = useRef<HTMLDivElement>(null)

  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)
  const [activeCategory, setActiveCategory] = useState<string>('all')
  const [search, setSearch] = useState('')
  const [downloadedId, setDownloadedId] = useState<string | null>(null)
  const [showCreate, setShowCreate] = useState(false)

  // Community skills from API
  const [communitySkills, setCommunitySkills] = useState<ApiSkill[]>([])
  const [loadingCommunity, setLoadingCommunity] = useState(true)

  // Create form
  const [pasteContent, setPasteContent] = useState('')
  const [skillName, setSkillName] = useState('')
  const [skillDesc, setSkillDesc] = useState('')
  const [skillCategory, setSkillCategory] = useState('marketing')
  const [skillTags, setSkillTags] = useState('')
  const [creating, setCreating] = useState(false)
  const [createDone, setCreateDone] = useState(false)
  const [createError, setCreateError] = useState<string | null>(null)
  const [showHowTo, setShowHowTo] = useState(false)

  const fetchCommunitySkills = useCallback(async () => {
    try {
      setLoadingCommunity(true)
      const res = await api.get<ApiSkill[]>('/skills/public')
      setCommunitySkills(res.data)
    } catch {
      // silently fail — pre-built skills still show
    } finally {
      setLoadingCommunity(false)
    }
  }, [])

  useEffect(() => { fetchCommunitySkills() }, [fetchCommunitySkills])

  // Close panel on outside click
  useEffect(() => {
    if (!showCreate) return
    const handler = (e: MouseEvent) => {
      if (panelRef.current && !panelRef.current.contains(e.target as Node)) setShowCreate(false)
    }
    document.addEventListener('mousedown', handler)
    return () => document.removeEventListener('mousedown', handler)
  }, [showCreate])

  // Auto-detect name + desc from pasted content
  const handlePaste = (val: string) => {
    setPasteContent(val)
    if (!skillName && val.trim()) {
      const firstLine = val.trim().split('\n')[0].replace(/^#+\s*/, '').slice(0, 60)
      setSkillName(firstLine)
    }
    if (!skillDesc && val.trim()) {
      const lines = val.trim().split('\n').filter(l => l.trim() && !l.startsWith('#'))
      if (lines[0]) setSkillDesc(lines[0].slice(0, 120))
    }
  }

  // Combine pre-built + community
  const communityAsSkills = communitySkills.map(apiSkillToSkill)
  const allSkills: (Skill & { _isOwner?: boolean; _apiId?: string; _likeCount?: number; _usageCount?: number; _authorName?: string })[] = [
    ...communitySkills.map(s => ({ ...apiSkillToSkill(s), _isOwner: s.isOwner, _apiId: s.id, _likeCount: s.likeCount, _usageCount: s.usageCount, _authorName: s.authorName })),
    ...PREBUILT_SKILLS,
  ]

  const filteredSkills = allSkills.filter(skill => {
    const isCommunity = communityAsSkills.some(c => c.id === skill.id)
    if (activeCategory === 'community') return isCommunity
    const matchesCategory = activeCategory === 'all' || skill.category === activeCategory
    const q = search.toLowerCase()
    const matchesSearch = !q || skill.name.toLowerCase().includes(q) || skill.description.toLowerCase().includes(q) || skill.tags.some(t => t.toLowerCase().includes(q))
    return matchesCategory && matchesSearch
  })

  const handleDownload = (skill: Skill) => {
    const md = generateSkillMarkdown(skill)
    const blob = new Blob([md], { type: 'text/markdown' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url; a.download = `${skill.id}-skill.md`
    document.body.appendChild(a); a.click(); document.body.removeChild(a)
    URL.revokeObjectURL(url)
    setDownloadedId(skill.id)
    setTimeout(() => setDownloadedId(null), 2000)
    // Track usage
    if (communitySkills.some(s => s.id === skill.id)) {
      api.post(`/skills/public/${skill.id}/use`).catch(() => {})
    }
  }

  const handleUseInPlayground = (skill: Skill) => {
    if (communitySkills.some(s => s.id === skill.id)) {
      api.post(`/skills/public/${skill.id}/use`).catch(() => {})
    }
    navigate(isAuthenticated ? '/create-prompt' : '/signup?next=%2Fskills', { state: { skill } })
  }

  const handleDelete = async (apiId: string) => {
    try {
      await api.delete(`/skills/${apiId}`)
      setCommunitySkills(prev => prev.filter(s => s.id !== apiId))
    } catch {
      // ignore
    }
  }

  const handleLike = async (apiId: string) => {
    if (!isAuthenticated) { navigate('/signin'); return }
    try {
      const res = await api.post<{ likeCount: number }>(`/skills/public/${apiId}/like`)
      setCommunitySkills(prev => prev.map(s => s.id === apiId ? { ...s, likeCount: res.data.likeCount } : s))
    } catch {
      // ignore
    }
  }

  const handleCreateSkill = async (download: boolean) => {
    if (!skillName.trim() || !pasteContent.trim()) return
    setCreating(true)
    setCreateError(null)
    const tags = skillTags.split(',').map(t => t.trim()).filter(Boolean)

    if (!isAuthenticated) {
      // Not logged in — download only, no API
      const cat = SKILL_CATEGORIES.find(c => c.id === skillCategory)
      const localSkill: Skill = {
        id: `local-${Date.now()}`, name: skillName.trim(),
        description: skillDesc.trim() || skillName.trim(),
        category: skillCategory, categoryIcon: cat?.icon ?? 'bi-stars',
        icon: 'bi-person-gear', tags, instruction: pasteContent.trim(), exampleIntent: '',
      }
      if (download) handleDownload(localSkill)
      setCreating(false)
      setCreateDone(true)
      setTimeout(() => resetForm(), 1500)
      return
    }

    try {
      const res = await api.post<ApiSkill>('/skills', {
        name: skillName.trim(),
        instruction: pasteContent.trim(),
        category: skillCategory,
        description: skillDesc.trim() || null,
        tags,
      })
      setCommunitySkills(prev => [res.data, ...prev])
      if (download) handleDownload(apiSkillToSkill(res.data))
      setCreateDone(true)
      setTimeout(() => resetForm(), 1500)
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message
      setCreateError(msg ?? 'Failed to save skill. Please try again.')
    } finally {
      setCreating(false)
    }
  }

  const resetForm = () => {
    setCreateDone(false); setShowCreate(false)
    setPasteContent(''); setSkillName(''); setSkillDesc(''); setSkillTags(''); setSkillCategory('marketing')
  }

  const isCreateValid = skillName.trim().length > 0 && pasteContent.trim().length > 0
  const communityCount = communitySkills.length

  const categoriesAndGrid = (
    <>
      {/* Search + Create bar (authenticated view) */}
      {isAuthenticated && (
        <div className="skills-auth-toolbar" style={{ display: 'flex', alignItems: 'center', gap: '10px', padding: '24px 24px 8px' }}>
          <div className="skills-search-wrap" style={{ width: '280px' }}>
            <PhosphorIcon className="bi bi-search skills-search-icon"></PhosphorIcon>
            <input type="text" className="skills-search" placeholder="Search skills..." value={search} onChange={e => setSearch(e.target.value)} />
            {search && <button className="skills-search-clear" onClick={() => setSearch('')}><PhosphorIcon className="bi bi-x"></PhosphorIcon></button>}
          </div>
          <button className="skill-create-btn" onClick={() => setShowCreate(true)}>
            <PhosphorIcon className="bi bi-plus-lg"></PhosphorIcon> Create Skill
          </button>
          <button onClick={() => setShowHowTo(true)} title="How to use skills" style={{ width: '25px', height: '25px', borderRadius: '50%', background: '#d62976', border: 'none', color: '#fff', fontWeight: 700, fontSize: '12px', fontStyle: 'italic', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>i</button>
        </div>
      )}

      {/* Category pills */}
      <div className="skills-categories-bar">
        <div className="skills-categories-inner">
          <button className={`skill-cat-pill ${activeCategory === 'all' ? 'active' : ''}`} onClick={() => setActiveCategory('all')}>
            <PhosphorIcon className="bi bi-grid"></PhosphorIcon> All
          </button>
          {communityCount > 0 && (
            <button className={`skill-cat-pill ${activeCategory === 'community' ? 'active' : ''}`} onClick={() => setActiveCategory('community')}>
              <PhosphorIcon className="bi bi-people"></PhosphorIcon> Community <span className="skill-pill-count">{communityCount}</span>
            </button>
          )}
          {SKILL_CATEGORIES.map(cat => (
            <button key={cat.id} className={`skill-cat-pill ${activeCategory === cat.id ? 'active' : ''}`} onClick={() => setActiveCategory(cat.id)}>
              <PhosphorIcon className={`bi ${cat.icon}`}></PhosphorIcon> {cat.label}
            </button>
          ))}
        </div>
      </div>

      {/* Skills grid */}
      <main className="skills-main">
        {loadingCommunity && communityCount === 0 && activeCategory === 'community' ? (
          <div className="skills-empty"><span className="pg-pill-spinner"></span></div>
        ) : filteredSkills.length === 0 ? (
          <div className="skills-empty">
            <PhosphorIcon className="bi bi-search" style={{ fontSize: '2rem', opacity: 0.3 }}></PhosphorIcon>
            <p>{search ? `No skills found for "${search}"` : 'No skills in this category yet'}</p>
            <button className="skill-create-btn" onClick={() => setShowCreate(true)}>
              <PhosphorIcon className="bi bi-plus-lg"></PhosphorIcon> Be the first to contribute
            </button>
          </div>
        ) : (
          <>
            <p className="skills-count">{filteredSkills.length} skill{filteredSkills.length !== 1 ? 's' : ''}</p>
            <div className="skills-grid">
              {filteredSkills.map(skill => {
                const cat = SKILL_CATEGORIES.find(c => c.id === skill.category)
                const apiEntry = communitySkills.find(s => s.id === skill.id)
                const isCommunity = !!apiEntry
                const isOwner = apiEntry?.isOwner ?? false
                return (
                  <div key={skill.id} className={`skill-card ${isCommunity ? 'skill-card-community' : ''}`}>
                    <div className="skill-card-header">
                      <div className="skill-card-icon"><PhosphorIcon className={`bi ${skill.icon}`}></PhosphorIcon></div>
                      <div style={{ display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
                        <span className="skill-cat-label">
                          <PhosphorIcon className={`bi ${cat?.icon ?? 'bi-people'}`}></PhosphorIcon> {cat?.label ?? 'Community'}
                        </span>
                        {isOwner && (
                          <button className="skill-card-delete" onClick={() => handleDelete(skill.id)} title="Delete your skill">
                            <PhosphorIcon className="bi bi-trash3"></PhosphorIcon>
                          </button>
                        )}
                      </div>
                    </div>
                    {isCommunity && (
                      <div className="skill-card-author">
                        <PhosphorIcon className="bi bi-person-circle"></PhosphorIcon> {apiEntry!.authorName}
                      </div>
                    )}
                    <h3 className="skill-card-name">{skill.name}</h3>
                    <p className="skill-card-desc">{skill.description}</p>
                    {skill.tags.length > 0 && (
                      <div className="skill-card-tags">
                        {skill.tags.map(tag => <span key={tag} className="skill-tag">{tag}</span>)}
                      </div>
                    )}
                    {isCommunity && (
                      <div className="skill-card-stats">
                        <button className="skill-stat-btn" onClick={() => handleLike(skill.id)} title="Like">
                          <PhosphorIcon className="bi bi-heart"></PhosphorIcon> {apiEntry!.likeCount}
                        </button>
                        <span className="skill-stat">
                          <PhosphorIcon className="bi bi-play-circle"></PhosphorIcon> {apiEntry!.usageCount} uses
                        </span>
                      </div>
                    )}
                    <div className="skill-card-actions">
                      <button className="skill-btn-use" onClick={() => handleUseInPlayground(skill)}>
                        <PhosphorIcon className="bi bi-play-fill"></PhosphorIcon> Use in Prompt Builder
                      </button>
                      <button className="skill-btn-download" onClick={() => handleDownload(skill)}>
                        <PhosphorIcon className={`bi ${downloadedId === skill.id ? 'bi-check-lg' : 'bi-download'}`}></PhosphorIcon>
                        {downloadedId === skill.id ? 'Downloaded' : 'Download Skill'}
                      </button>
                    </div>
                  </div>
                )
              })}
            </div>
          </>
        )}
        <AdSense slot="3087599544" style={{ marginTop: '2rem' }} />
      </main>

      {/* How to use skills modal */}
      {showHowTo && (
        <div className="skill-panel-overlay" style={{ justifyContent: 'center', alignItems: 'center' }} onClick={() => setShowHowTo(false)}>
          <div className="skill-panel" style={{ maxWidth: '500px', height: 'auto', maxHeight: '90vh', borderRadius: '12px' }} onClick={e => e.stopPropagation()}>
            <div className="skill-panel-header" style={{ borderRadius: '12px 12px 0 0' }}>
              <div>
                <h2 className="skill-panel-title" style={{ color: 'white', margin: '0 0 0.2rem' }}>How to use Skills</h2>
                <p className="skill-panel-subtitle" style={{ margin: 0 }}>4 ways to put skills to work</p>
              </div>
              <button className="skill-panel-close" onClick={() => setShowHowTo(false)}><PhosphorIcon className="bi bi-x-lg"></PhosphorIcon></button>
            </div>
            <div style={{ padding: '20px 24px', display: 'flex', flexDirection: 'column', gap: '20px' }}>
              <div>
                <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '8px' }}>
                  <span style={{ width: '28px', height: '28px', borderRadius: '50%', background: '#d6297618', display: 'flex', alignItems: 'center', justifyContent: 'center' }}><PhosphorIcon className="bi bi-play-circle-fill" style={{ color: '#d62976' }}></PhosphorIcon></span>
                  <strong>In the Prompt Builder</strong>
                </div>
                <p style={{ margin: 0, fontSize: '0.875rem', color: 'var(--text-secondary)', lineHeight: 1.6 }}>Click <strong>Use in Prompt Builder</strong> on any skill card. The skill's instruction is injected as the system context for your next prompt generation.</p>
              </div>
              <div>
                <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '8px' }}>
                  <span style={{ width: '28px', height: '28px', borderRadius: '50%', background: '#833ab418', display: 'flex', alignItems: 'center', justifyContent: 'center' }}><PhosphorIcon className="bi bi-download" style={{ color: '#833ab4' }}></PhosphorIcon></span>
                  <strong>In Claude Desktop, Cursor or Windsurf</strong>
                </div>
                <p style={{ margin: 0, fontSize: '0.875rem', color: 'var(--text-secondary)', lineHeight: 1.6 }}>Click <strong>Download .md</strong> to save the skill as a Markdown file. In Claude Desktop go to <code style={{ background: 'var(--bg-secondary)', padding: '1px 5px', borderRadius: '4px' }}>Settings → Custom Instructions</code> and paste the file contents, or drop it into your Cursor / Windsurf project rules.</p>
              </div>
              <div>
                <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '8px' }}>
                  <span style={{ width: '28px', height: '28px', borderRadius: '50%', background: '#833ab418', display: 'flex', alignItems: 'center', justifyContent: 'center' }}><PhosphorIcon className="bi bi-cpu" style={{ color: '#833ab4' }}></PhosphorIcon></span>
                  <strong>Via MCP (Claude Desktop / Cursor / VS Code)</strong>
                </div>
                <p style={{ margin: 0, fontSize: '0.875rem', color: 'var(--text-secondary)', lineHeight: 1.6 }}>If you have the GenPrompt MCP server connected, your entire skills library is already available inside your AI client. Run <code style={{ background: 'var(--bg-secondary)', padding: '1px 5px', borderRadius: '4px' }}>search_prompts</code> or ask your AI to "use the [skill name] skill from GenPrompt".</p>
              </div>
              <div>
                <div style={{ display: 'flex', alignItems: 'center', gap: '8px', marginBottom: '8px' }}>
                  <span style={{ width: '28px', height: '28px', borderRadius: '50%', background: '#f9731618', display: 'flex', alignItems: 'center', justifyContent: 'center' }}><PhosphorIcon className="bi bi-puzzle-fill" style={{ color: '#f97316' }}></PhosphorIcon></span>
                  <strong>Via Chrome Extension</strong>
                </div>
                <p style={{ margin: 0, fontSize: '0.875rem', color: 'var(--text-secondary)', lineHeight: 1.6 }}>Open the GenPrompt extension popup while on ChatGPT, Claude, or Gemini. Browse your skills, click one, and it injects directly into the chat input — no copy-paste needed.</p>
              </div>
            </div>
          </div>
        </div>
      )}

      {/* Create Skill panel */}
      {showCreate && (
        <div className="skill-panel-overlay">
          <div className="skill-panel" ref={panelRef}>
            <div className="skill-panel-header">
              <div>
                <h2 className="skill-panel-title">Create a Skill</h2>
                <p className="skill-panel-subtitle">
                  {isAuthenticated ? 'Paste your content — it will be shared with the community' : 'Paste your content to generate a .md skill file'}
                </p>
              </div>
              <button className="skill-panel-close" onClick={() => setShowCreate(false)}>
                <PhosphorIcon className="bi bi-x-lg"></PhosphorIcon>
              </button>
            </div>
            <div className="skill-panel-body">
              <div className="skill-panel-field">
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 6 }}>
                  <label className="skill-panel-label" style={{ margin: 0 }}>
                    <PhosphorIcon className="bi bi-clipboard-fill"></PhosphorIcon> Paste your content <span className="skill-panel-required">*</span>
                  </label>
                  <PromptPicker onSelect={p => handlePaste(p.content)} />
                </div>
                <textarea className="skill-panel-paste" placeholder={"Paste your instructions, guidelines, documentation, or any text here...\n\nGenPrompt will turn it into a reusable AI skill."} value={pasteContent} onChange={e => handlePaste(e.target.value)} rows={10} autoFocus />
                {pasteContent && <span className="skill-panel-char-count">{pasteContent.length.toLocaleString()} characters</span>}
              </div>
              <div className="skill-panel-row">
                <div className="skill-panel-field" style={{ flex: 1 }}>
                  <label className="skill-panel-label">Skill name <span className="skill-panel-required">*</span></label>
                  <input type="text" className="skill-panel-input" placeholder="e.g. Brand Guidelines Enforcer" value={skillName} onChange={e => setSkillName(e.target.value)} maxLength={60} />
                </div>
                <div className="skill-panel-field" style={{ width: '180px' }}>
                  <label className="skill-panel-label">Category</label>
                  <select className="skill-panel-select" value={skillCategory} onChange={e => setSkillCategory(e.target.value)}>
                    {SKILL_CATEGORIES.map(c => <option key={c.id} value={c.id}>{c.label}</option>)}
                  </select>
                </div>
              </div>
              <div className="skill-panel-field">
                <label className="skill-panel-label">Short description</label>
                <input type="text" className="skill-panel-input" placeholder="One sentence describing what this skill does" value={skillDesc} onChange={e => setSkillDesc(e.target.value)} maxLength={120} />
              </div>
              <div className="skill-panel-field">
                <label className="skill-panel-label">Tags <span className="skill-panel-hint">(comma separated)</span></label>
                <input type="text" className="skill-panel-input" placeholder="e.g. branding, tone, content" value={skillTags} onChange={e => setSkillTags(e.target.value)} />
              </div>
              {!isAuthenticated && (
                <div className="skill-panel-notice">
                  <PhosphorIcon className="bi bi-info-circle"></PhosphorIcon>
                  <span><Link to="/signin">Sign in</Link> to publish your skill to the community library. Or download as a .md file without an account.</span>
                </div>
              )}
              {createError && <div className="skill-panel-error"><PhosphorIcon className="bi bi-exclamation-circle"></PhosphorIcon> {createError}</div>}
            </div>
            <div className="skill-panel-footer">
              {createDone ? (
                <div className="skill-panel-success">
                  <PhosphorIcon className="bi bi-check-circle-fill"></PhosphorIcon>
                  {isAuthenticated ? 'Skill published to the community library!' : 'Skill file downloaded!'}
                </div>
              ) : (
                <>
                  <button className="skill-panel-btn-save" onClick={() => handleCreateSkill(false)} disabled={!isCreateValid || creating}>
                    {creating ? <span className="pg-pill-spinner"></span> : <PhosphorIcon className="bi bi-globe"></PhosphorIcon>}
                    {isAuthenticated ? 'Publish to Library' : 'Save Locally'}
                  </button>
                  <button className="skill-panel-btn-download" onClick={() => handleCreateSkill(true)} disabled={!isCreateValid || creating}>
                    <PhosphorIcon className="bi bi-download"></PhosphorIcon> Download .md
                  </button>
                  <button className="skill-panel-btn-cancel" onClick={() => setShowCreate(false)}>Cancel</button>
                </>
              )}
            </div>
          </div>
        </div>
      )}
    </>
  )

  if (isAuthenticated) {
    return (
      <div className="skills-page skills-page-auth">
        <section className="skills-auth-hero">
          <div>
            <span className="skills-auth-eyebrow">
              <PhosphorIcon className="bi bi-lightning-charge-fill"></PhosphorIcon> Skills Library
            </span>
            <h1>Reusable AI skills for every workflow</h1>
            <p>Browse, apply, download, and publish prompt skill packs from your workspace.</p>
          </div>
          <div className="skills-auth-stats" aria-label="Skills library stats">
            <div>
              <strong>{filteredSkills.length}</strong>
              <span>Visible skills</span>
            </div>
            <div>
              <strong>{communityCount}</strong>
              <span>Community skills</span>
            </div>
          </div>
        </section>
        {categoriesAndGrid}
      </div>
    )
  }

  return (
    <div className="skills-page">
      {/* Header — matches LandingPage nav */}
      <nav className="landing-nav">
        <div className="nav-container">
          <Link to="/" className="nav-brand">
            <GenPromptLogoMark className="landing-header-logo-image" title="GenPrompt" />
            <span className="brand-name">GenPrompt</span>
          </Link>
          <button className="hamburger-menu" onClick={() => setMobileMenuOpen(!mobileMenuOpen)} aria-label="Toggle menu">
            <span></span><span></span><span></span>
          </button>
          <div className={`nav-links ${mobileMenuOpen ? 'mobile-open' : ''}`}>
            <Link to="/features"   className="nav-link" onClick={() => setMobileMenuOpen(false)}>Features</Link>
            <Link to="/library"    className="nav-link" onClick={() => setMobileMenuOpen(false)}>Library</Link>
            <Link to="/skills"     className="nav-link skills-nav-active" onClick={() => setMobileMenuOpen(false)}>Skills</Link>
            <Link to="/contact"    className="nav-link" onClick={() => setMobileMenuOpen(false)}>Contact Us</Link>
            {isAuthenticated ? (
              <Link to="/dashboard" className="btn-nav-cta" onClick={() => setMobileMenuOpen(false)}>Dashboard</Link>
            ) : (
              <>
                <Link to="/signin" className="nav-link" onClick={() => setMobileMenuOpen(false)}>Sign In</Link>
                <Link to="/signup" className="btn-nav-cta" onClick={() => setMobileMenuOpen(false)}>Get Started</Link>
              </>
            )}
          </div>
        </div>
      </nav>

      {/* Hero */}
      <section className="skills-hero">
        <div className="skills-hero-inner">
          <div className="skills-hero-badge">
            <PhosphorIcon className="bi bi-lightning-charge-fill"></PhosphorIcon> {40 + communityCount} Skills
          </div>
          <h1 className="skills-hero-title">
            Expert-Level AI Skills,<br />Ready to Use
          </h1>
          <p className="skills-hero-subtitle">
            Browse pre-built instruction sets or share your own with the community.
            Use in the Prompt Builder or download as Claude-compatible <code>.md</code> files.
          </p>
          <div className="skills-hero-actions">
            <div className="skills-search-wrap">
              <PhosphorIcon className="bi bi-search skills-search-icon"></PhosphorIcon>
              <input type="text" className="skills-search" placeholder="Search skills..." value={search} onChange={e => setSearch(e.target.value)} />
              {search && <button className="skills-search-clear" onClick={() => setSearch('')}><PhosphorIcon className="bi bi-x"></PhosphorIcon></button>}
            </div>
            <button className="skill-create-btn" onClick={() => setShowCreate(true)}>
              <PhosphorIcon className="bi bi-plus-lg"></PhosphorIcon> Create Skill
            </button>
            <button onClick={() => setShowHowTo(true)} title="How to use skills" style={{ width: '25px', height: '25px', borderRadius: '50%', background: '#d62976', border: 'none', color: '#fff', fontWeight: 700, fontSize: '12px', fontStyle: 'italic', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }}>i</button>
          </div>
        </div>
      </section>

      {/* Static explainer — improves crawlability and contextualises the page */}
      <section style={{ maxWidth: 900, margin: '0 auto', padding: '0 1.5rem 2rem' }}>
        <div style={{ background: '#f8fafc', border: '1px solid #e2e8f0', borderRadius: 12, padding: '1.5rem 2rem' }}>
          <h2 style={{ fontSize: '1.1rem', fontWeight: 700, marginBottom: '0.75rem', color: '#0f172a' }}>What are AI Skills?</h2>
          <p style={{ margin: '0 0 0.75rem', fontSize: '0.92rem', lineHeight: 1.7, color: '#475569' }}>
            AI Skills are reusable instruction sets — domain-specific guidelines, personas, or constraints
            that you attach to any prompt to get consistently expert-level output. Instead of re-explaining
            your brand voice, technical standards, or writing style every time, you define it once as a skill
            and apply it with one click.
          </p>
          <p style={{ margin: '0 0 0.75rem', fontSize: '0.92rem', lineHeight: 1.7, color: '#475569' }}>
            GenPrompt's library includes 40+ pre-built skills covering <strong>Marketing</strong>, <strong>Engineering</strong>,{' '}
            <strong>Legal</strong>, <strong>Finance</strong>, <strong>HR</strong>, <strong>Education</strong>, and more —
            each authored by domain experts. Community members can also contribute their own skills to share
            with other users.
          </p>
          <p style={{ margin: 0, fontSize: '0.92rem', lineHeight: 1.7, color: '#475569' }}>
            Skills can be applied directly in the GenPrompt Prompt Builder, downloaded as Claude-compatible{' '}
            <code style={{ background: '#e2e8f0', padding: '1px 5px', borderRadius: 4 }}>.md</code> files for
            use in Claude Desktop, Cursor, or VS Code, or accessed through the GenPrompt MCP server or
            Chrome extension — no account required for downloading.
          </p>
        </div>
      </section>

      {categoriesAndGrid}
      <SiteFooter />
    </div>
  )
}

export default Skills
