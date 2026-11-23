import PhosphorIcon from '../components/PhosphorIcon'
import { Fragment, useEffect, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import api from '../services/api'
import CustomSelect from '../components/CustomSelect'
import ExportButtons from '../components/ExportButtons'
import GoalPicker from '../components/GoalPicker'
import DeckBuilder, { type Deck } from '../components/DeckBuilder'
import { StudyRenderer, STUDY_EXPORT_CSS, type StudyDoc } from '../components/StudyRender'
import { STUDY_STYLES, studyStyleById } from '../data/studyStyles'
import { svgToPng, elementToPng, downloadDataUri, safePngName } from '../utils/imageExport'
import '../components/VisualizeSelection.css'
import './StudyStudio.css'

interface Suggestion { styleId: string; reason: string }
interface Article { title: string; blocks: string[] }

/**
 * One embedded visual "slot": the passage it was drawn from plus every variant
 * generated for it (restyles / tone changes). The user flips between variants;
 * only the active one renders and exports.
 */
interface VisualSlot { sourceText?: string; variants: StudyDoc[]; active: number }

const TONES: [string, string][] = [
  ['professional', 'Professional'], ['casual', 'Casual'], ['sketch', 'Sketch'], ['playful', 'Playful'], ['academic', 'Academic'],
]

const apiErrorMessage = (error: unknown, fallback: string) => {
  const response = (error as { response?: { data?: { error?: unknown } } } | null)?.response
  return typeof response?.data?.error === 'string' ? response.data.error : fallback
}

const toBlocks = (markdown: string): string[] =>
  markdown.split(/\n{2,}/).map(b => b.trim()).filter(Boolean)

// Saved docs come in two shapes: legacy (plain StudyDoc arrays) and current
// (VisualSlot arrays). Normalize both so old documents keep opening.
const toSlots = (v: unknown): Record<number, VisualSlot[]> => {
  const out: Record<number, VisualSlot[]> = {}
  if (!v || typeof v !== 'object') return out
  for (const [k, list] of Object.entries(v as Record<string, unknown>)) {
    if (!Array.isArray(list)) continue
    const slots = list.filter(Boolean).map((item: any): VisualSlot =>
      Array.isArray(item?.variants)
        ? {
            sourceText: typeof item.sourceText === 'string' ? item.sourceText : undefined,
            variants: item.variants.filter(Boolean),
            active: Math.min(Math.max(0, Number(item.active) || 0), item.variants.filter(Boolean).length - 1),
          }
        : { variants: [item], active: 0 },
    ).filter(s => s.variants.length > 0)
    if (slots.length) out[Number(k)] = slots
  }
  return out
}

// Styling the exported document needs beyond the shared study CSS.
const INK_EXPORT_CSS = `
  .ink-block { font-size: 15px; line-height: 1.75; }
  .ink-inline-visual { margin: 16px 0 20px; border: 1.5px solid #e9d5ff; border-radius: 14px; padding: 14px 16px; page-break-inside: avoid; background: #fdfcff; }
  .ink-inline-head { display: flex; align-items: center; gap: 7px; font-size: 11px; font-weight: 800; letter-spacing: .06em; text-transform: uppercase; color: #c13584; margin-bottom: 10px; }
  .ink-remove, .ink-vis-tools, .ink-edit-btn { display: none !important; }
`

/** UI-only chrome stripped from every export (Word, EMMD, PNG, HTML/PDF). */
const EXPORT_CLEANUP = '.ink-remove, .ink-vis-tools, .ink-edit-btn, .mm-drag-hint'

/**
 * Inkling — napkin-style visual documents. Topic mode researches the web and
 * writes a substantive explainer; paste mode opens the user's own text. Either
 * way, the result is a DOCUMENT: select any passage and AI draws a visual that
 * embeds inline right there, building a mixed text-and-diagrams artifact that
 * exports as one piece. Every passage is editable in place, and every embedded
 * visual can be restyled, re-toned, flipped between variants, or downloaded as
 * a PNG on its own.
 */
export default function Visualizer() {
  const [mode, setMode] = useState<'topic' | 'paste' | 'deck'>('topic')
  const [deck, setDeck] = useState<Deck | null>(null)
  const [topic, setTopic] = useState('')
  const [text, setText] = useState('')
  const [tone, setTone] = useState('professional')
  const [intent, setIntent] = useState('')
  const [composing, setComposing] = useState(false)
  const [error, setError] = useState('')

  const [article, setArticle] = useState<Article | null>(null)
  const [inlineVisuals, setInlineVisuals] = useState<Record<number, VisualSlot[]>>({})

  // In-place editing of one article block at a time.
  const [editing, setEditing] = useState<{ block: number; text: string } | null>(null)

  // Persistence: up to 100 saved documents per user; Start over auto-saves.
  const [savedId, setSavedId] = useState<number | null>(null)
  const [saving, setSaving] = useState(false)
  const [savedFlash, setSavedFlash] = useState(false)
  const [savedDocs, setSavedDocs] = useState<{ id: number; title?: string; updatedAt?: string }[]>([])

  const loadSavedDocs = async () => {
    try { setSavedDocs((await api.get('/ink/docs')).data ?? []) } catch { /* ignore */ }
  }
  useEffect(() => { loadSavedDocs() }, [])

  const saveDoc = async (): Promise<boolean> => {
    if (!article || saving) return false
    setSaving(true)
    try {
      const res = await api.post('/ink/docs', {
        id: savedId,
        title: article.title,
        content: JSON.stringify({ blocks: article.blocks, visuals: inlineVisuals, tone, v: 2 }),
      })
      setSavedId(res.data.id)
      setSavedFlash(true)
      setTimeout(() => setSavedFlash(false), 2000)
      loadSavedDocs()
      return true
    } catch (e: unknown) {
      setError(apiErrorMessage(e, 'Could not save the document — try again.'))
      return false
    } finally {
      setSaving(false)
    }
  }

  const openSavedDoc = async (id: number) => {
    try {
      const res = await api.get(`/ink/docs/${id}`)
      const c = res.data?.content ?? {}
      setArticle({ title: res.data?.title || 'Untitled document', blocks: Array.isArray(c.blocks) ? c.blocks : [] })
      setInlineVisuals(toSlots(c.visuals))
      if (typeof c.tone === 'string') setTone(c.tone)
      setSavedId(id)
      setEditing(null)
      setError('')
      window.scrollTo({ top: 0, behavior: 'smooth' })
    } catch { /* ignore */ }
  }

  const deleteSavedDoc = async (id: number) => {
    try { await api.delete(`/ink/docs/${id}`) } catch { /* ignore */ }
    setSavedDocs(d => d.filter(x => x.id !== id))
    if (savedId === id) setSavedId(null)
  }

  // In-document selection → floating visualize button → suggestion popover.
  const [inkSel, setInkSel] = useState<{ block: number; text: string; x: number; y: number } | null>(null)
  const [popOpen, setPopOpen] = useState(false)
  const [sugs, setSugs] = useState<Suggestion[] | null>(null)
  const [loadingSugs, setLoadingSugs] = useState(false)
  const [generating, setGenerating] = useState<string | null>(null)
  const [popError, setPopError] = useState('')
  const docRef = useRef<HTMLDivElement>(null)
  const popOpenRef = useRef(false)
  popOpenRef.current = popOpen

  useEffect(() => {
    const onMouseUp = () => {
      if (popOpenRef.current) return
      setTimeout(() => {
        const sel = window.getSelection()
        const selText = sel?.toString().trim() ?? ''
        if (!sel || sel.isCollapsed || selText.length < 25 || selText.length > 12000) { setInkSel(null); return }
        const anchor = sel.anchorNode instanceof Element ? sel.anchorNode : sel.anchorNode?.parentElement
        const blockEl = anchor?.closest('[data-ink-block]')
        if (!blockEl || !docRef.current?.contains(blockEl)) { setInkSel(null); return }
        // Selections inside the block editor are just text editing.
        if (blockEl.classList.contains('ink-block-editing')) { setInkSel(null); return }
        const rect = sel.getRangeAt(0).getBoundingClientRect()
        setInkSel({
          block: Number(blockEl.getAttribute('data-ink-block')),
          text: selText,
          x: Math.min(window.innerWidth - 150, Math.max(10, rect.left + rect.width / 2 - 65)),
          y: Math.max(8, rect.top - 46),
        })
      }, 10)
    }
    const onDown = (e: MouseEvent) => {
      if ((e.target as Element)?.closest?.('.vz-fab, .ink-pop')) return
      setInkSel(null)
      setPopOpen(false)
    }
    document.addEventListener('mouseup', onMouseUp)
    document.addEventListener('mousedown', onDown)
    return () => {
      document.removeEventListener('mouseup', onMouseUp)
      document.removeEventListener('mousedown', onDown)
    }
  }, [])

  const compose = async () => {
    if (composing) return
    setError('')

    // Present mode: the same prompt, built as slides instead of paragraphs. The
    // writer nominates a visual per slide; nothing is drawn until asked, so a
    // deck arrives fast and gains pictures where they earn their place.
    if (mode === 'deck') {
      const seed = topic.trim()
      const pasted = text.trim()
      if (seed.length < 3 && pasted.length < 40) {
        setError('Give a topic to present, or paste the material to build it from.')
        return
      }
      setComposing(true)
      try {
        const res = await api.post('/ink/deck', {
          topic: seed || null,
          content: pasted.length >= 40 ? pasted : null,
          intent: intent || null,
          tone,
        })
        setDeck({
          title: res.data.title,
          subtitle: res.data.subtitle ?? '',
          slides: (res.data.slides ?? []).map((sl: {
            title: string; bullets?: string[]; notes?: string
            visual?: { styleId?: string; brief?: string }
          }) => ({
            title: sl.title,
            bullets: sl.bullets ?? [],
            notes: sl.notes ?? '',
            visual: { styleId: sl.visual?.styleId ?? '', brief: sl.visual?.brief ?? '' },
            variants: [],
            active: 0,
            showVisual: false,
          })),
        })
        setArticle(null)
        setInlineVisuals({})
        setEditing(null)
      } catch (e: unknown) {
        setError(apiErrorMessage(e, 'The deck could not be built — please try again.'))
      } finally {
        setComposing(false)
      }
      return
    }

    if (mode === 'topic') {
      if (topic.trim().length < 3) { setError('Give a topic to research.'); return }
      setComposing(true)
      try {
        const res = await api.post('/ink/compose', { topic: topic.trim(), intent: intent || null, tone })
        setArticle({ title: res.data.title, blocks: toBlocks(res.data.markdown) })
        setInlineVisuals({})
        setEditing(null)
      } catch (e: unknown) {
        setError(apiErrorMessage(e, 'Research failed — please try again.'))
      } finally {
        setComposing(false)
      }
    } else {
      if (text.trim().length < 40) { setError('Paste a bit more text — at least a few sentences.'); return }
      setArticle({ title: topic.trim() || 'My document', blocks: toBlocks(text) })
      setInlineVisuals({})
      setEditing(null)
    }
  }

  const startOver = async () => {
    // Never lose work: the current document is saved before clearing.
    if (article && article.blocks.length > 0) await saveDoc()
    setArticle(null); setDeck(null); setInlineVisuals({}); setInkSel(null); setPopOpen(false); setError(''); setSavedId(null); setEditing(null)
  }

  // ── Block editing ──
  const startEdit = (block: number) => {
    if (!article) return
    setEditing({ block, text: article.blocks[block] ?? '' })
    setInkSel(null)
  }

  const saveEdit = () => {
    if (!editing || !article) return
    const next = editing.text.trim()
    if (next) setArticle({ ...article, blocks: article.blocks.map((b, i) => (i === editing.block ? next : b)) })
    setEditing(null)
  }

  const openPop = async () => {
    if (!inkSel) return
    setPopOpen(true); setSugs(null); setPopError(''); setLoadingSugs(true)
    try {
      const res = await api.post('/ink/suggest-visuals', { text: inkSel.text })
      setSugs(res.data?.suggestions ?? [])
    } catch {
      setSugs([])
    } finally {
      setLoadingSugs(false)
    }
  }

  const closePop = () => {
    setPopOpen(false)
    setSugs(null)
    setPopError('')
  }

  useEffect(() => {
    if (!popOpen) return
    const onEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        setPopOpen(false)
        setSugs(null)
        setPopError('')
      }
    }
    document.addEventListener('keydown', onEscape)
    return () => document.removeEventListener('keydown', onEscape)
  }, [popOpen])

  /** Shared generation call for first embeds and variant regenerations. */
  const generateStudyDoc = async (styleId: string, source: string, useTone: string): Promise<StudyDoc> => {
    const res = await api.post('/ink/generate', {
      styleId,
      content: source,
      topic: article?.title ?? null,
      level: 'professional',
      useWebSearch: false,
      tone: useTone,
      intent: intent || null,
      look: styleId === 'mind-map' ? (useTone === 'professional' || useTone === 'academic' ? 'clean' : 'creative') : null,
    })
    return res.data
  }

  const embed = async (styleId: string) => {
    if (!inkSel || generating) return
    setGenerating(styleId); setPopError('')
    try {
      const doc = await generateStudyDoc(styleId, inkSel.text, tone)
      const block = inkSel.block
      const slot: VisualSlot = { sourceText: inkSel.text, variants: [doc], active: 0 }
      setInlineVisuals(prev => ({ ...prev, [block]: [...(prev[block] ?? []), slot] }))
      setPopOpen(false); setInkSel(null)
      window.getSelection()?.removeAllRanges()
    } catch (e: unknown) {
      setPopError(apiErrorMessage(e, 'Could not draw that — try again.'))
    } finally {
      setGenerating(null)
    }
  }

  // ── Per-visual actions: restyle / re-tone (new variant), flip, PNG, remove ──
  const [slotBusy, setSlotBusy] = useState<string | null>(null)
  const slotKey = (block: number, idx: number) => `${block}:${idx}`

  const generateVariant = async (block: number, idx: number, opts: { styleId?: string; tone?: string }) => {
    const slot = inlineVisuals[block]?.[idx]
    if (!slot || slotBusy) return
    const current = slot.variants[slot.active]
    const source = slot.sourceText ?? article?.blocks[block] ?? ''
    if (!source.trim() || !current) return
    setSlotBusy(slotKey(block, idx))
    setError('')
    try {
      const doc = await generateStudyDoc(opts.styleId ?? current.styleId, source, opts.tone ?? tone)
      setInlineVisuals(prev => {
        const slots = [...(prev[block] ?? [])]
        const s = slots[idx]
        if (!s) return prev
        slots[idx] = { ...s, variants: [...s.variants, doc], active: s.variants.length }
        return { ...prev, [block]: slots }
      })
    } catch (e: unknown) {
      setError(apiErrorMessage(e, 'Could not redraw that visual — try again.'))
    } finally {
      setSlotBusy(null)
    }
  }

  const flipVariant = (block: number, idx: number, dir: number) =>
    setInlineVisuals(prev => {
      const slots = [...(prev[block] ?? [])]
      const s = slots[idx]
      if (!s || s.variants.length < 2) return prev
      slots[idx] = { ...s, active: (s.active + dir + s.variants.length) % s.variants.length }
      return { ...prev, [block]: slots }
    })

  const downloadVisualPng = async (block: number, idx: number) => {
    const el = docRef.current?.querySelector<HTMLElement>(`[data-vis="${block}-${idx}"] .ink-vis-body`)
    const slot = inlineVisuals[block]?.[idx]
    if (!el || !slot) return
    try {
      const png = await elementToPng(el, STUDY_EXPORT_CSS + INK_EXPORT_CSS)
      downloadDataUri(png, safePngName(slot.variants[slot.active]?.title, 'inkling-visual'))
    } catch {
      setError('Could not export that visual as an image — try again.')
    }
  }

  const removeVisual = (block: number, idx: number) =>
    setInlineVisuals(prev => ({ ...prev, [block]: (prev[block] ?? []).filter((_, i) => i !== idx) }))

  // ── Word (.doc) + Enhanced MMD (.emmd) export ──
  const [exporting, setExporting] = useState<'word' | 'mmd' | null>(null)

  const escapeHtml = (s: string) => s.replace(/[<>&]/g, c => ({ '<': '&lt;', '>': '&gt;', '&': '&amp;' }[c] as string))
  const safeFilename = () => (article?.title ?? 'inkling-doc').replace(/[^\w\- ]+/g, '').trim().replace(/\s+/g, '-').slice(0, 60) || 'inkling-doc'

  const downloadBlob = (blob: Blob, filename: string) => {
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url; a.download = filename
    document.body.appendChild(a); a.click(); a.remove()
    setTimeout(() => URL.revokeObjectURL(url), 5000)
  }

  /** Clone the rendered document with every SVG swapped for a PNG img. */
  const rasterizedDocHtml = async (): Promise<string | null> => {
    const src = docRef.current
    if (!src) return null
    const clone = src.cloneNode(true) as HTMLElement
    const srcSvgs = Array.from(src.querySelectorAll('svg'))
    const cloneSvgs = Array.from(clone.querySelectorAll('svg'))
    for (let i = 0; i < srcSvgs.length; i++) {
      try {
        const png = await svgToPng(srcSvgs[i] as SVGSVGElement)
        const img = document.createElement('img')
        img.src = png
        img.setAttribute('width', '100%')
        cloneSvgs[i]?.replaceWith(img)
      } catch { /* keep the svg — better than losing it */ }
    }
    clone.querySelectorAll(EXPORT_CLEANUP).forEach(el => el.remove())
    return clone.innerHTML
  }

  const exportWord = async () => {
    if (!article || exporting) return
    setExporting('word')
    try {
      const body = await rasterizedDocHtml()
      if (!body) return
      const doc =
        `<html xmlns:o="urn:schemas-microsoft-com:office:office" xmlns:w="urn:schemas-microsoft-com:office:word">` +
        `<head><meta charset="utf-8"><title>${escapeHtml(article.title)}</title>` +
        `<style>body{font-family:Calibri,Arial,sans-serif;line-height:1.6;} ${STUDY_EXPORT_CSS}${INK_EXPORT_CSS}</style></head>` +
        `<body><h1>${escapeHtml(article.title)}</h1>${body}</body></html>`
      downloadBlob(new Blob(['﻿', doc], { type: 'application/msword' }), `${safeFilename()}.doc`)
    } finally {
      setExporting(null)
    }
  }

  const exportMmd = async () => {
    if (!article || exporting) return
    setExporting('mmd')
    try {
      // Visuals in render order (blocks ascending, then insertion order).
      const src = docRef.current
      const visualEls = src ? Array.from(src.querySelectorAll('.ink-inline-visual')) : []
      const visualHtml: string[] = []
      for (const el of visualEls) {
        const clone = el.cloneNode(true) as HTMLElement
        const srcSvg = el.querySelector('svg')
        const cloneSvg = clone.querySelector('svg')
        if (srcSvg && cloneSvg) {
          try {
            const png = await svgToPng(srcSvg as SVGSVGElement)
            const img = document.createElement('img')
            img.src = png
            img.className = 'ink-visual-img'
            cloneSvg.replaceWith(img)
          } catch { cloneSvg.remove() }
        }
        clone.querySelectorAll(EXPORT_CLEANUP).forEach(x => x.remove())
        visualHtml.push(clone.outerHTML)
      }
      const parts: string[] = []
      let k = 0
      article.blocks.forEach((b, i) => {
        parts.push(b)
        for (let j = 0; j < (inlineVisuals[i] ?? []).length; j++) {
          if (visualHtml[k]) parts.push(visualHtml[k])
          k++
        }
      })
      const css = (STUDY_EXPORT_CSS + INK_EXPORT_CSS).replace(/@import[^;]+;/g, '').trim()
      const emmd =
        `---\ntitle: ${article.title.replace(/\n/g, ' ')}\ntheme: clean\n---\n\n` +
        `<!--emmd:style\n${css}\n.ink-visual-img { max-width: 100%; }\n-->\n\n` +
        parts.join('\n\n') + '\n'
      downloadBlob(new Blob([emmd], { type: 'text/markdown;charset=utf-8' }), `${safeFilename()}.emmd`)
    } finally {
      setExporting(null)
    }
  }

  const visualCount = Object.values(inlineVisuals).reduce((n, v) => n + v.length, 0)

  const styleOptions = STUDY_STYLES.map(style => ({
    value: style.id,
    label: style.label,
    description: style.blurb,
    icon: style.icon,
  }))

  return (
    <div className="study-page inkling-page">
      <div className="ss-main">
        <header className="ss-page-head">
          <div className="inkling-eyebrow">
            <span className="inkling-mark"><PhosphorIcon className="bi bi-bezier2" /></span>
            InkReel · Visual documents
          </div>
          <h1>Write it. Then <em>draw inside it.</em></h1>
          <p>Research a topic (or bring your own text), then highlight any passage — AI embeds the visual that explains it, right where it belongs.</p>
        </header>

        <div className={`inkling-workspace${savedDocs.length > 0 ? ' has-history' : ''}`}>
          <div className="inkling-primary">
        {!article && !deck && (
          <section className="ss-input-card">
            <div className="inkling-editor-label">
              <span>01</span>
              <div>
                <strong>Start your document</strong>
                <small>Let AI research and write it — or paste what you already have.</small>
              </div>
            </div>
            <div className="ss-mode-tabs" role="tablist" aria-label="Source type">
              <button type="button" role="tab" aria-selected={mode === 'topic'} className={mode === 'topic' ? 'active' : ''} onClick={() => setMode('topic')}>
                <PhosphorIcon className="bi bi-lightbulb" /> Research a topic
              </button>
              <button type="button" role="tab" aria-selected={mode === 'paste'} className={mode === 'paste' ? 'active' : ''} onClick={() => setMode('paste')}>
                <PhosphorIcon className="bi bi-text-paragraph" /> Paste my own text
              </button>
              <button type="button" role="tab" aria-selected={mode === 'deck'} className={mode === 'deck' ? 'active' : ''} onClick={() => setMode('deck')}>
                <PhosphorIcon className="bi bi-easel" /> Build a deck
              </button>
            </div>
            {mode === 'deck' ? (
              <>
                <textarea
                  className="ss-topic-input"
                  value={topic}
                  onChange={e => setTopic(e.target.value)}
                  onKeyDown={e => { if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') compose() }}
                  placeholder="What is the talk about?&#10;&#10;e.g. Why our onboarding drops 40% at step three, How OAuth login works…"
                  rows={4}
                />
                <textarea
                  className="ss-source-input"
                  value={text}
                  onChange={e => setText(e.target.value)}
                  placeholder="Optional — paste the material to build the slides from (notes, a spec, a report). Leave empty to research the topic."
                  rows={5}
                />
              </>
            ) : mode === 'topic' ? (
              <textarea
                className="ss-topic-input"
                value={topic}
                onChange={e => setTopic(e.target.value)}
                onKeyDown={e => { if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') compose() }}
                placeholder="What should the document explain?&#10;&#10;e.g. How OAuth login works, CRISPR gene editing, our customer onboarding process…"
                rows={5}
              />
            ) : (
              <textarea
                className="ss-source-input"
                style={{ marginTop: 0 }}
                value={text}
                onChange={e => setText(e.target.value)}
                onKeyDown={e => { if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') compose() }}
                placeholder="Paste meeting notes, a spec, an article, a chapter…"
                rows={7}
              />
            )}

            {/* Deck mode uses both boxes — a topic to present and, optionally,
                the material to build it from — so it feeds the picker both.
                Passing null for either made Suggest goals claim nothing had
                been typed. */}
            <GoalPicker
              topic={mode === 'paste' ? null : topic}
              content={mode === 'topic' ? null : text}
              onIntentChange={setIntent}
            />

            <div className="inkling-section-divider">
              <span>02</span>
              <div>
                <strong>Set the character</strong>
                <small>How the writing and visuals should feel.</small>
              </div>
            </div>
            <div className="ss-level-row">
              <span className="inkling-style-label">Visual tone</span>
              {TONES.map(([id, label]) => (
                <button key={id} type="button" className={`ss-level-chip${tone === id ? ' active' : ''}`} onClick={() => setTone(id)}>
                  {label}
                </button>
              ))}
            </div>
            {error && <div className="ss-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
            <button type="button" className="ss-generate" onClick={compose} disabled={composing}>
              {composing
                ? <><span className="ss-spin" /> {mode === 'deck' ? 'Building the deck — ~30 seconds…' : 'Researching & writing — ~30 seconds…'}</>
                : mode === 'deck'
                  ? <><PhosphorIcon className="bi bi-easel" /> Build the deck</>
                  : mode === 'topic'
                    ? <><PhosphorIcon className="bi bi-stars" /> Research &amp; write the document</>
                    : <><PhosphorIcon className="bi bi-file-earmark-text" /> Open as a document</>}
            </button>
          </section>
        )}

        {deck && (
          <DeckBuilder
            deck={deck}
            onChange={setDeck}
            tone={tone}
            intent={intent}
            onStartOver={startOver}
          />
        )}

        {article && (
          <>
            <div className="ink-toolbar" role="toolbar" aria-label="Document actions">
                <ExportButtons
                  title={article.title}
                  filenameBase="inkling-doc"
                  subtitle="InkReel"
                  getBodyHtml={() => {
                    const body = docRef.current?.innerHTML
                    return body ? `<style>${STUDY_EXPORT_CSS}${INK_EXPORT_CSS}</style><div class="ss-render">${body}</div>` : null
                  }}
                  size="sm"
                />
                <button type="button" className="ink-btn" onClick={exportWord} disabled={exporting !== null} title="Download as a Word document">
                  {exporting === 'word' ? <span className="ss-spin" /> : <PhosphorIcon className="bi bi-file-earmark-word" />} Word
                </button>
                <button type="button" className="ink-btn" onClick={exportMmd} disabled={exporting !== null} title="Download as Enhanced MMD (.emmd) — open in the EMMD Studio">
                  {exporting === 'mmd' ? <span className="ss-spin" /> : <PhosphorIcon className="bi bi-file-earmark-richtext" />} MMD
                </button>
                <button type="button" className="ink-btn ink-btn-save" onClick={saveDoc} disabled={saving}>
                  {saving ? <span className="ss-spin" /> : <PhosphorIcon className={`bi ${savedFlash ? 'bi-check-lg' : 'bi-floppy'}`} />}
                  {savedFlash ? 'Saved' : 'Save'}
                </button>
                <button type="button" className="ink-btn" onClick={startOver} disabled={saving} title="Saves this document, then starts a new one">
                  <PhosphorIcon className="bi bi-arrow-counterclockwise" /> Start over
                </button>
            </div>
            <section className="ss-result-card">
              <div className="ss-result-head">
                <div>
                  <span className="ss-result-style"><PhosphorIcon className="bi bi-bezier2" /> Inkling document · {visualCount} visual{visualCount === 1 ? '' : 's'} embedded</span>
                  <h2>{article.title}</h2>
                </div>
              </div>
              <div className="ink-doc-hint">
                <PhosphorIcon className="bi bi-cursor-text" /> Highlight any passage below — AI draws the visual that explains it and embeds it right there. Hover a paragraph to edit it.
              </div>
              <div ref={docRef} className="ss-render ink-article">
                {article.blocks.map((block, i) => (
                  <Fragment key={i}>
                    {editing?.block === i ? (
                      <div className="ink-block ink-block-editing" data-ink-block={i}>
                        <textarea
                          className="ink-block-editor"
                          value={editing.text}
                          onChange={e => setEditing(ed => (ed ? { ...ed, text: e.target.value } : ed))}
                          onKeyDown={e => {
                            if ((e.metaKey || e.ctrlKey) && e.key === 'Enter') saveEdit()
                            if (e.key === 'Escape') setEditing(null)
                          }}
                          rows={Math.min(16, Math.max(4, editing.text.split('\n').length + 2))}
                          autoFocus
                        />
                        <div className="ink-edit-actions">
                          <button type="button" className="ink-btn ink-btn-save" onClick={saveEdit}>
                            <PhosphorIcon className="bi bi-check-lg" /> Save passage
                          </button>
                          <button type="button" className="ink-btn" onClick={() => setEditing(null)}>Cancel</button>
                        </div>
                      </div>
                    ) : (
                      <div className="ink-block" data-ink-block={i}>
                        <button type="button" className="ink-edit-btn" onClick={() => startEdit(i)} title="Edit this passage" aria-label="Edit this passage">
                          <PhosphorIcon className="bi bi-pencil" />
                        </button>
                        <ReactMarkdown>{block}</ReactMarkdown>
                      </div>
                    )}
                    {(inlineVisuals[i] ?? []).map((slot, si) => {
                      const v = slot.variants[slot.active]
                      if (!v) return null
                      const busy = slotBusy === slotKey(i, si)
                      return (
                        <div className="ink-inline-visual" key={`${i}-${si}-${v.id}-${slot.active}`} data-vis={`${i}-${si}`}>
                          <div className="ink-inline-head">
                            <PhosphorIcon className={`bi ${studyStyleById(v.styleId)?.icon ?? 'bi-easel'}`} />
                            {studyStyleById(v.styleId)?.label ?? 'Visual'}
                            <span className="ink-vis-tools">
                              {slot.variants.length > 1 && (
                                <span className="ink-flip">
                                  <button type="button" onClick={() => flipVariant(i, si, -1)} aria-label="Previous variant"><PhosphorIcon className="bi bi-chevron-left" /></button>
                                  {slot.active + 1}/{slot.variants.length}
                                  <button type="button" onClick={() => flipVariant(i, si, 1)} aria-label="Next variant"><PhosphorIcon className="bi bi-chevron-right" /></button>
                                </span>
                              )}
                              {busy ? (
                                <span className="ink-vis-busy"><span className="ss-spin" /> drawing…</span>
                              ) : (
                                <>
                                  <CustomSelect
                                    className="ink-vis-select"
                                    value=""
                                    disabled={slotBusy !== null}
                                    placeholder="Restyle"
                                    options={styleOptions}
                                    onChange={sid => generateVariant(i, si, { styleId: sid })}
                                  />
                                  <CustomSelect
                                    className="ink-vis-select"
                                    value=""
                                    disabled={slotBusy !== null}
                                    placeholder="Tone"
                                    options={TONES.map(([id, label]) => ({ value: id, label }))}
                                    onChange={t => generateVariant(i, si, { tone: t })}
                                  />
                                </>
                              )}
                              <button type="button" className="ink-tool-btn" onClick={() => downloadVisualPng(i, si)} title="Download this visual as a PNG image" aria-label="Download as PNG">
                                <PhosphorIcon className="bi bi-image" />
                              </button>
                              <button type="button" className="ink-tool-btn ink-tool-del" onClick={() => removeVisual(i, si)} aria-label="Remove visual">
                                <PhosphorIcon className="bi bi-x-lg" />
                              </button>
                            </span>
                          </div>
                          <div className="ink-vis-body">
                            <StudyRenderer key={`${v.id}-${slot.active}`} doc={v} />
                          </div>
                        </div>
                      )
                    })}
                  </Fragment>
                ))}
              </div>
            </section>
          </>
        )}
          </div>

          {savedDocs.length > 0 && (
            <aside className="ss-history inkling-history" aria-label="Saved Inkling documents">
              <h3><PhosphorIcon className="bi bi-journals" /> Saved documents <span>{savedDocs.length}/100</span></h3>
              <div className="ss-history-list">
                {savedDocs.map(d => (
                  <div key={d.id} className={`ss-history-item${savedId === d.id ? ' active' : ''}`}>
                    <button type="button" className="ss-history-open" onClick={() => openSavedDoc(d.id)}>
                      <PhosphorIcon className="bi bi-file-earmark-richtext" />
                      <span>
                        <strong>{d.title || 'Untitled document'}</strong>
                        <small>{d.updatedAt ? new Date(d.updatedAt).toLocaleDateString() : 'Inkling document'}</small>
                      </span>
                    </button>
                    <button type="button" className="ss-history-del" onClick={() => deleteSavedDoc(d.id)} aria-label={`Delete ${d.title || 'document'}`}>
                      <PhosphorIcon className="bi bi-trash" />
                    </button>
                  </div>
                ))}
              </div>
            </aside>
          )}
        </div>
      </div>

      {/* Floating visualize button over the selection */}
      {inkSel && !popOpen && (
        <button type="button" className="vz-fab" style={{ left: inkSel.x, top: inkSel.y }} onClick={openPop}>
          <PhosphorIcon className="bi bi-stars" /> Visualize
        </button>
      )}

      {/* Suggestion popover anchored near the selection */}
      {popOpen && inkSel && (
        <div className="ink-pop-overlay" onMouseDown={closePop}>
          <div
            className="ink-pop"
            role="dialog"
            aria-modal="true"
            aria-labelledby="ink-pop-title"
            onMouseDown={event => event.stopPropagation()}
          >
            <div className="ink-pop-head">
              <div>
                <span>Visualise selection</span>
                <strong id="ink-pop-title">Choose a visual direction</strong>
              </div>
              <button type="button" className="vz-close" onClick={closePop} aria-label="Close visual selector">
                <PhosphorIcon className="bi bi-x-lg" />
              </button>
            </div>
            <div className="ink-pop-body">
              <p className="vz-quote">“{inkSel.text.slice(0, 160)}{inkSel.text.length > 160 ? '…' : ''}”</p>
              {loadingSugs && <div className="vz-loading"><span className="ss-spin" /> Finding the visuals that fit…</div>}
              {!loadingSugs && sugs && (
                <>
                  <div className="vz-recommendation-meta">
                    <strong>Best matches</strong>
                    <span>{sugs.length} recommendations · {STUDY_STYLES.length} formats available</span>
                  </div>
                  <div className="vz-suggestions">
                    {sugs.map(s => {
                      const meta = studyStyleById(s.styleId)
                      if (!meta) return null
                      return (
                        <button key={s.styleId} type="button" className="vz-suggestion" disabled={generating !== null} onClick={() => embed(s.styleId)}>
                          <PhosphorIcon className={`bi ${meta.icon}`} />
                          <span><strong>{meta.label}</strong><small>{s.reason || meta.blurb}</small></span>
                          {generating === s.styleId && <span className="ss-spin" />}
                        </button>
                      )
                    })}
                    <div className="vz-more">
                      <CustomSelect
                        className="vz-style-select"
                        value=""
                        disabled={generating !== null}
                        placeholder={`Browse all ${STUDY_STYLES.length} visual styles`}
                        options={styleOptions}
                        onChange={embed}
                      />
                      {generating && !sugs.some(s => s.styleId === generating) && <span className="ss-spin" />}
                    </div>
                  </div>
                </>
              )}
              {popError && <div className="vz-error">{popError}</div>}
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
