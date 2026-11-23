import PhosphorIcon from '../components/PhosphorIcon'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import EmmdRenderer from '../components/EmmdRenderer'
import { buildContent, parseEmmd } from '../utils/emmd'
import { EMMD_THEMES } from '../utils/emmdThemes'
import { EMMD_SAMPLE } from '../data/emmdSample'
import { downloadEmmdHtml, downloadEmmdPdf, downloadEmmdSource, exportStamp, printEmmd } from '../utils/emmdExport'
import {
  createEmmd, deleteEmmd, getEmmd, listEmmd, sealEmmd, unsealEmmd, updateEmmd,
  type EmmdDoc, type EmmdDocSummary, type EmmdSealStatus,
} from '../services/emmdApi'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import './Emmd.css'

const STARTER_BODY = `## Overview

Write in **Markdown** — it stays readable in any editor. Add tables, lists, and
even a little HTML when you need structure:

| Item      | Qty | Price |
|-----------|-----|-------|
| Widget    | 2   | 40.00 |
| Assembly  | 1   | 15.00 |

> Pick a theme on the left, add optional custom CSS, then **Seal** the document
> to stamp it "sealed by GenPrompt, content unmodified".
`

function slugify(s: string): string {
  return (s || 'document').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40) || 'document'
}

function today(): string {
  return new Date().toISOString().slice(0, 10)
}

export default function EmmdStudio() {
  const { setBreadcrumbs } = useLayoutControls()
  const navigate = useNavigate()
  const { id: routeId } = useParams()

  const [docId, setDocId] = useState<string | null>(null)
  const [version, setVersion] = useState(0)
  const [sealToken, setSealToken] = useState<string | null>(null)
  const [canEdit, setCanEdit] = useState(true)
  const [title, setTitle] = useState('Untitled document')
  const [subtitle, setSubtitle] = useState('')
  const [author, setAuthor] = useState('')
  const [created, setCreated] = useState(today())
  const [theme, setTheme] = useState('clean')
  const [body, setBody] = useState(STARTER_BODY)
  const [styleCss, setStyleCss] = useState('')

  const [seal, setSeal] = useState<EmmdSealStatus | null>(null)
  const [docs, setDocs] = useState<EmmdDocSummary[]>([])
  const [tab, setTab] = useState<'markdown' | 'style' | 'meta'>('markdown')
  const [saving, setSaving] = useState(false)
  const [sealing, setSealing] = useState(false)
  const [pdfBusy, setPdfBusy] = useState(false)
  const [error, setError] = useState('')
  const [savedSnapshot, setSavedSnapshot] = useState('')

  const previewRef = useRef<HTMLDivElement>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)

  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Enhanced MMD' }])
    return () => setBreadcrumbs([])
  }, [])

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph) —
  // its prepared text replaces the starter body. Prefill only, never auto-run.
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/emmd')
    if (handoff?.input) setBody(handoff.input)
  }, [])

  const content = useMemo(
    () => buildContent({ title, subtitle, author, theme, created }, styleCss, body),
    [title, subtitle, author, theme, created, styleCss, body],
  )
  const dirty = content !== savedSnapshot
  const wasSealed = !!seal?.valid

  // Warn before a full page unload (refresh / tab close / back) with unsaved work.
  useEffect(() => {
    if (!dirty) return
    const handler = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', handler)
    return () => window.removeEventListener('beforeunload', handler)
  }, [dirty])

  // Confirm before any in-app action that would discard unsaved edits.
  const confirmDiscard = () =>
    !dirty || window.confirm('You have unsaved changes. Discard them?')

  const refreshList = useCallback(async () => {
    try { setDocs(await listEmmd()) } catch { /* non-fatal */ }
  }, [])

  const applyDoc = useCallback((doc: EmmdDoc) => {
    const parsed = parseEmmd(doc.content)
    setTitle(doc.title)
    setTheme(doc.theme)
    setSubtitle(parsed.frontmatter.subtitle || '')
    setAuthor(parsed.frontmatter.author || '')
    setCreated(parsed.frontmatter.created || today())
    setBody(parsed.body)
    setStyleCss(parsed.styleCss)
    setSeal(doc.seal)
    setSealToken(doc.sealToken || null)
    setCanEdit(doc.canEdit)
    setVersion(doc.version)
    setSavedSnapshot(doc.content)
  }, [])

  const loadDoc = useCallback(async (id: string) => {
    setError('')
    try {
      const doc = await getEmmd(id)
      setDocId(doc.id)
      applyDoc(doc)
    } catch {
      setError('Could not open that document.')
      navigate('/emmd', { replace: true })
    }
  }, [applyDoc, navigate])

  useEffect(() => { refreshList() }, [refreshList])
  useEffect(() => {
    if (routeId && routeId !== docId) loadDoc(routeId)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [routeId])

  const resetToNew = () => {
    setDocId(null)
    setVersion(0)
    setSealToken(null)
    setCanEdit(true)
    setTitle('Untitled document')
    setSubtitle(''); setAuthor(''); setCreated(today())
    setTheme('clean'); setBody(STARTER_BODY); setStyleCss('')
    setSeal(null); setSavedSnapshot(''); setError('')
    setTab('markdown')
  }

  const newDoc = () => {
    if (!confirmDiscard()) return
    resetToNew()
    navigate('/emmd')
  }

  // Load the bundled showcase document (TOC, tables, HTML, custom CSS) as a
  // fresh unsaved draft the user can explore, tweak, save, or seal.
  const loadSample = () => {
    if (!confirmDiscard()) return
    resetToNew()
    setTitle(EMMD_SAMPLE.title)
    setSubtitle(EMMD_SAMPLE.subtitle)
    setAuthor(EMMD_SAMPLE.author)
    setTheme(EMMD_SAMPLE.theme)
    setBody(EMMD_SAMPLE.body)
    setStyleCss(EMMD_SAMPLE.styleCss)
    navigate('/emmd')
  }

  // Open an existing document from the sidebar, guarding unsaved work.
  const openDoc = (id: string) => {
    if (id === docId) return
    if (!confirmDiscard()) return
    navigate(`/emmd/doc/${id}`)
  }

  const save = async (): Promise<string | null> => {
    setSaving(true); setError('')
    try {
      const payload = { title, theme, content, expectedVersion: docId ? version : undefined }
      const doc = docId ? await updateEmmd(docId, payload) : await createEmmd(payload)
      setDocId(doc.id)
      setSeal(doc.seal)
      setSealToken(doc.sealToken || null)
      setCanEdit(doc.canEdit)
      setVersion(doc.version)
      setSavedSnapshot(doc.content)
      await refreshList()
      if (!routeId || routeId !== doc.id) navigate(`/emmd/doc/${doc.id}`, { replace: true })
      return doc.id
    } catch (e: unknown) {
      const err = e as { response?: { status?: number; data?: { message?: string } } }
      if (err?.response?.status === 409) {
        setError(err.response.data?.message || 'This document was changed elsewhere. Reload before saving.')
      } else {
        setError(err?.response?.data?.message || 'Could not save the document.')
      }
      return null
    } finally {
      setSaving(false)
    }
  }

  const doSeal = async () => {
    setSealing(true); setError('')
    try {
      const id = await save()
      if (!id) return
      const doc = await sealEmmd(id)
      setSeal(doc.seal)
      setSealToken(doc.sealToken || null)
      setVersion(doc.version)
      setSavedSnapshot(doc.content)
      await refreshList()
    } catch {
      setError('Could not seal the document.')
    } finally {
      setSealing(false)
    }
  }

  const doUnseal = async () => {
    if (!docId) return
    setSealing(true); setError('')
    try {
      const doc = await unsealEmmd(docId)
      setSeal(doc.seal)
      setSealToken(null)
      setVersion(doc.version)
      setSavedSnapshot(doc.content)
      await refreshList()
    } catch {
      setError('Could not remove the seal.')
    } finally {
      setSealing(false)
    }
  }

  const remove = async (id: string, docTitle: string) => {
    if (!window.confirm(`Delete "${docTitle}"? This cannot be undone.`)) return
    try {
      await deleteEmmd(id)
      await refreshList()
      if (id === docId) { resetToNew(); navigate('/emmd') }
    } catch {
      setError('Could not delete that document.')
    }
  }

  const MAX_IMPORT_BYTES = 3 * 1024 * 1024
  const onImport = async (file: File) => {
    if (!confirmDiscard()) return
    if (file.size > MAX_IMPORT_BYTES) {
      setError(`That file is too large (${Math.round(file.size / 1024)} KB). The limit is ${MAX_IMPORT_BYTES / 1024} KB.`)
      return
    }
    let text: string
    try { text = await file.text() }
    catch { setError('Could not read that file.'); return }
    const parsed = parseEmmd(text)
    setVersion(0)
    setSealToken(null)
    setDocId(null)
    setCanEdit(true)
    setTitle(parsed.frontmatter.title || file.name.replace(/\.(emmd|md|markdown)$/i, '') || 'Imported document')
    setSubtitle(parsed.frontmatter.subtitle || '')
    setAuthor(parsed.frontmatter.author || '')
    setCreated(parsed.frontmatter.created || today())
    setTheme(parsed.frontmatter.theme || 'clean')
    setBody(parsed.body)
    setStyleCss(parsed.styleCss)
    setSeal(null); setSavedSnapshot(''); setError('')
    navigate('/emmd')
  }

  const filenameBase = () => `${slugify(title)}-${exportStamp()}`
  // Only offer a sealed .emmd when we hold the real token AND the current content
  // still matches the seal (not dirty) — otherwise the downloaded file's seal
  // wouldn't verify. Export the source unsealed in that case.
  const sealInfo = seal?.valid && sealToken && !dirty
    ? { token: sealToken, sealedBy: seal.sealedBy, sealedAt: seal.sealedAt }
    : null

  const exportPdf = async () => {
    const bodyHtml = previewRef.current?.innerHTML
    if (!bodyHtml || pdfBusy) return
    setPdfBusy(true)
    try {
      await downloadEmmdPdf(content, bodyHtml, filenameBase(), theme)
    } catch {
      // Engine unavailable → fall back to the browser's print-to-PDF dialog.
      printEmmd(content, bodyHtml, theme)
    } finally { setPdfBusy(false) }
  }
  const exportHtml = () => {
    const bodyHtml = previewRef.current?.innerHTML
    if (bodyHtml) downloadEmmdHtml(content, bodyHtml, filenameBase(), theme)
  }
  const exportSource = () => downloadEmmdSource(content, sealInfo, filenameBase())

  return (
    <div className="emmd-studio">
      {/* Sidebar: document library */}
      <aside className="emmd-side">
        <button className="emmd-btn emmd-btn-primary emmd-new" onClick={newDoc}>
          <PhosphorIcon className="bi bi-file-earmark-plus" /> New document
        </button>
        <button className="emmd-btn emmd-sample" onClick={loadSample} title="Open a full example — TOC, tables, custom CSS, HTML blocks">
          <PhosphorIcon className="bi bi-stars" /> Load example
        </button>
        <div className="emmd-side-head">My documents</div>
        <div className="emmd-doclist">
          {docs.length === 0 && <p className="emmd-empty">No documents yet.</p>}
          {docs.map(d => (
            <div key={d.id} className={`emmd-docitem${d.id === docId ? ' active' : ''}`}>
              <button className="emmd-docitem-main" onClick={() => openDoc(d.id)}>
                <span className="emmd-docitem-title">{d.title}</span>
                <span className="emmd-docitem-meta">
                  <span className={`emmd-chip emmd-chip-${d.theme}`}>{d.theme}</span>
                  {d.sealed && <span className="emmd-chip emmd-chip-sealed"><PhosphorIcon className="bi bi-patch-check-fill" /> sealed</span>}
                  {!d.mine && <span className="emmd-chip">team</span>}
                </span>
              </button>
              {d.mine && (
                <button className="emmd-docitem-del" title="Delete" onClick={() => remove(d.id, d.title)}>
                  <PhosphorIcon className="bi bi-trash" />
                </button>
              )}
            </div>
          ))}
        </div>
      </aside>

      {/* Main editor + preview */}
      <div className="emmd-main">
        <div className="emmd-hero">
          <h1><PhosphorIcon className="bi bi-file-earmark-richtext" /> Enhanced MMD</h1>
          <p>A styled, sealable document format that's still plain Markdown underneath. Write, theme, seal, and export a PDF twin.</p>
        </div>

        {error && <div className="emmd-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

        {/* Toolbar */}
        <div className="emmd-toolbar">
          <input
            className="emmd-title-input"
            value={title}
            onChange={e => setTitle(e.target.value)}
            placeholder="Document title"
            disabled={!canEdit}
          />
          <select className="emmd-theme-select" value={theme} onChange={e => setTheme(e.target.value)} disabled={!canEdit}>
            {EMMD_THEMES.map(t => <option key={t.id} value={t.id}>{t.label}</option>)}
          </select>
          <div className="emmd-toolbar-actions">
            <button className="emmd-btn emmd-btn-primary" onClick={save} disabled={saving || !canEdit}>
              {saving ? <><span className="emmd-spin" /> Saving…</> : <><PhosphorIcon className="bi bi-save" /> Save</>}
            </button>
            {wasSealed ? (
              <button className="emmd-btn" onClick={doUnseal} disabled={sealing || !canEdit} title="Remove the seal">
                <PhosphorIcon className="bi bi-patch-check-fill" /> Unseal
              </button>
            ) : (
              <button className="emmd-btn emmd-btn-seal" onClick={doSeal} disabled={sealing || !canEdit}>
                {sealing ? <><span className="emmd-spin" /> Sealing…</> : <><PhosphorIcon className="bi bi-patch-check" /> Seal</>}
              </button>
            )}
            <span className="emmd-toolbar-sep" />
            <button className="emmd-btn" onClick={exportPdf} disabled={pdfBusy} title="Download a PDF twin">
              {pdfBusy ? <><span className="emmd-spin" /> PDF…</> : <><PhosphorIcon className="bi bi-file-earmark-pdf" /> PDF</>}
            </button>
            <button className="emmd-btn" onClick={exportHtml} title="Download self-contained HTML"><PhosphorIcon className="bi bi-filetype-html" /> HTML</button>
            <button className="emmd-btn" onClick={exportSource} title="Download the .emmd source"><PhosphorIcon className="bi bi-filetype-md" /> .emmd</button>
            <button className="emmd-btn" onClick={() => fileInputRef.current?.click()} title="Import a .emmd or .md file"><PhosphorIcon className="bi bi-upload" /> Import</button>
            {docId && <button className="emmd-btn" onClick={() => { if (confirmDiscard()) navigate(`/emmd/view/${docId}`) }} title="Open reader view"><PhosphorIcon className="bi bi-eye" /> View</button>}
            <input
              ref={fileInputRef}
              type="file"
              accept=".emmd,.md,.markdown,text/markdown,text/plain"
              style={{ display: 'none' }}
              onChange={e => { const f = e.target.files?.[0]; if (f) onImport(f); e.target.value = '' }}
            />
          </div>
        </div>

        {/* Status line */}
        <div className="emmd-status">
          {!canEdit && <span className="emmd-status-ro"><PhosphorIcon className="bi bi-lock" /> Read-only (a teammate owns this document)</span>}
          {canEdit && wasSealed && !dirty && (
            <span className="emmd-status-sealed"><PhosphorIcon className="bi bi-patch-check-fill" /> Sealed by {seal?.sealedBy}{seal?.sealedAt ? ` · ${new Date(seal.sealedAt).toLocaleString()}` : ''}</span>
          )}
          {canEdit && wasSealed && dirty && (
            <span className="emmd-status-warn"><PhosphorIcon className="bi bi-exclamation-triangle" /> Edited since sealing — saving will break the seal.</span>
          )}
          {canEdit && !wasSealed && dirty && docId && <span className="emmd-status-dirty">Unsaved changes</span>}
          {canEdit && !wasSealed && !dirty && docId && <span className="emmd-status-ok"><PhosphorIcon className="bi bi-check2" /> Saved</span>}
        </div>

        {/* Split: editor | preview */}
        <div className="emmd-split">
          <div className="emmd-editor">
            <div className="emmd-tabs">
              <button className={tab === 'markdown' ? 'on' : ''} onClick={() => setTab('markdown')}><PhosphorIcon className="bi bi-markdown" /> Markdown</button>
              <button className={tab === 'style' ? 'on' : ''} onClick={() => setTab('style')}><PhosphorIcon className="bi bi-palette" /> Custom CSS</button>
              <button className={tab === 'meta' ? 'on' : ''} onClick={() => setTab('meta')}><PhosphorIcon className="bi bi-info-circle" /> Details</button>
            </div>

            {tab === 'markdown' && (
              <textarea
                className="emmd-code"
                value={body}
                onChange={e => setBody(e.target.value)}
                placeholder="# Write your document in Markdown…"
                spellCheck={false}
                disabled={!canEdit}
              />
            )}
            {tab === 'style' && (
              <>
                <p className="emmd-hint">Optional CSS, scoped to this document. <code>&lt;style&gt;</code>, JavaScript, external URLs and fixed positioning are stripped for safety. Target elements with tags or classes, e.g. <code>h2 &#123; color:#0a7 &#125;</code>.</p>
                <textarea
                  className="emmd-code"
                  value={styleCss}
                  onChange={e => setStyleCss(e.target.value)}
                  placeholder="h2 { color: #0f766e; }"
                  spellCheck={false}
                  disabled={!canEdit}
                />
              </>
            )}
            {tab === 'meta' && (
              <div className="emmd-meta">
                <label>Subtitle<input value={subtitle} onChange={e => setSubtitle(e.target.value)} disabled={!canEdit} placeholder="Optional subtitle" /></label>
                <label>Author<input value={author} onChange={e => setAuthor(e.target.value)} disabled={!canEdit} placeholder="Your name or company" /></label>
                <label>Date<input value={created} onChange={e => setCreated(e.target.value)} disabled={!canEdit} placeholder="YYYY-MM-DD" /></label>
                <p className="emmd-hint">
                  <PhosphorIcon className="bi bi-info-circle" /> These become YAML frontmatter. Any Markdown reader shows it as a header block or ignores it — that's what keeps EMMD backward-compatible.
                </p>
              </div>
            )}
          </div>

          <div className="emmd-preview">
            <div className="emmd-preview-label"><PhosphorIcon className="bi bi-eye" /> Live preview</div>
            <div className="emmd-preview-surface">
              <EmmdRenderer ref={previewRef} content={content} themeOverride={theme} />
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
