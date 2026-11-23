// Inkling's deck editor.
//
// Same prompt, same visual engine, different artifact. A slide starts as words
// and can be swapped for a drawing of those exact words — /study/suggest-visuals
// reads the slide's own bullets, /study/generate draws them, and StudyRenderer
// paints the result. That is the same path an inline visual in an Inkling
// document takes, which is the point: nothing here is a second implementation
// of drawing.
//
// The swap is a toggle, not a replacement. The bullets stay behind the visual,
// so flipping back costs nothing and a slide is never one regeneration away
// from having lost its content.

import { useMemo, useRef, useState, type CSSProperties } from 'react'
import PhosphorIcon from './PhosphorIcon'
import api from '../services/api'
import { StudyRenderer, STUDY_EXPORT_CSS, type StudyDoc } from './StudyRender'
import { STUDY_STYLES, studyStyleById } from '../data/studyStyles'
import { elementToPng, downloadDataUri, safePngName } from '../utils/imageExport'
import { exportDeckPptx } from '../utils/deckExport'
import { DECK_THEMES, deckThemeById, hex } from '../data/deckThemes'
import ExportButtons from './ExportButtons'
import CustomSelect from './CustomSelect'
import './DeckBuilder.css'

export interface DeckSlide {
  title: string
  bullets: string[]
  notes: string
  /** What the writer thought this slide should be drawn as. May be empty. */
  visual: { styleId: string; brief: string }
  /** Every visual generated for this slide; the user flips between them. */
  variants: StudyDoc[]
  active: number
  /** Whether the visual or the bullets are on the slide right now. */
  showVisual: boolean
}

export interface Deck {
  title: string
  subtitle: string
  slides: DeckSlide[]
}

interface Suggestion { styleId: string; reason: string }

export interface DeckBuilderProps {
  deck: Deck
  onChange: (deck: Deck) => void
  /** Carried into every generation so slides look like the rest of the deck. */
  tone: string
  intent: string
  onStartOver: () => void
  saving?: boolean
  savedFlash?: boolean
  onSave?: () => void
}

const DECK_EXPORT_CSS = `
  .dk-slide { page-break-after: always; border: 1px solid #e5e7eb; border-radius: 14px; padding: 28px 32px; margin-bottom: 22px; background: #fff; }
  .dk-slide-title { font-size: 24px; font-weight: 800; color: #1f2937; margin: 0 0 6px; }
  .dk-rule { width: 74px; height: 3px; background: #c13584; border-radius: 2px; margin-bottom: 18px; }
  .dk-bullets { margin: 0; padding-left: 22px; }
  .dk-bullets li { font-size: 16px; line-height: 1.8; color: #1f2937; }
  .dk-notes { margin-top: 16px; font-size: 12px; color: #6b7280; font-style: italic; }
  .dk-chrome, .dk-slide-tools, .dk-rail, .dk-toolbar, .dk-picker { display: none !important; }
`

/** The slide's own words — what a visual of it should be drawn from. */
const slideText = (s: DeckSlide) => [s.title, ...s.bullets].join('\n')

export default function DeckBuilder({
  deck, onChange, tone, intent, onStartOver, saving, savedFlash, onSave,
}: DeckBuilderProps) {
  const [busy, setBusy] = useState<number | null>(null)
  const [picker, setPicker] = useState<number | null>(null)
  const [sugs, setSugs] = useState<Suggestion[] | null>(null)
  const [loadingSugs, setLoadingSugs] = useState(false)
  const [error, setError] = useState('')
  const [exporting, setExporting] = useState(false)
  const [editing, setEditing] = useState<number | null>(null)
  const [themeId, setThemeId] = useState('editorial')

  const deckTheme = deckThemeById(themeId)

  const deckRef = useRef<HTMLDivElement>(null)
  const slideRefs = useRef<(HTMLDivElement | null)[]>([])

  const patch = (index: number, next: Partial<DeckSlide>) =>
    onChange({ ...deck, slides: deck.slides.map((s, i) => (i === index ? { ...s, ...next } : s)) })

  // ── Drawing ──────────────────────────────────────────────────────────────

  const draw = async (index: number, styleId: string) => {
    const slide = deck.slides[index]
    if (!slide || busy !== null) return
    setBusy(index)
    setError('')
    try {
      const res = await api.post('/ink/generate', {
        styleId,
        content: slideText(slide),
        topic: deck.title || null,
        level: 'professional',
        useWebSearch: false,
        tone,
        intent: intent || null,
        look: styleId === 'mind-map' ? (tone === 'professional' || tone === 'academic' ? 'clean' : 'creative') : null,
      })
      const variants = [...slide.variants, res.data as StudyDoc]
      patch(index, { variants, active: variants.length - 1, showVisual: true })
      setPicker(null)
      setSugs(null)
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { error?: string } } })?.response?.data?.error
      setError(msg || 'That visual could not be drawn — try another style.')
    } finally {
      setBusy(null)
    }
  }

  const openPicker = async (index: number) => {
    setPicker(index)
    setSugs(null)
    setError('')
    setLoadingSugs(true)
    try {
      const res = await api.post('/ink/suggest-visuals', { text: slideText(deck.slides[index]) })
      setSugs(res.data?.suggestions ?? [])
    } catch {
      setSugs([])
    } finally {
      setLoadingSugs(false)
    }
  }

  // ── Export ───────────────────────────────────────────────────────────────

  /**
   * Rasterise whatever is on slide i, when it is a visual.
   *
   * elementToPng captures at the element's *visible* width, and a diagram wider
   * than the slide scrolls inside its box on screen — so it would be captured
   * with its right-hand side cut off. The box is widened to its full scroll
   * width for the length of the capture and put back afterwards, so the whole
   * diagram reaches the PNG and the export can then scale it down to fit.
   */
  const visualPng = async (index: number): Promise<string | null> => {
    const host = slideRefs.current[index]?.querySelector<HTMLElement>('.dk-visual-render')
    if (!host) return null

    const prevWidth = host.style.width
    const prevOverflow = host.style.overflowX
    const overflows = host.scrollWidth > host.clientWidth + 1
    if (overflows) {
      host.style.width = `${host.scrollWidth}px`
      host.style.overflowX = 'visible'
    }

    try {
      return await elementToPng(host, STUDY_EXPORT_CSS)
    } catch {
      return null
    } finally {
      if (overflows) {
        host.style.width = prevWidth
        host.style.overflowX = prevOverflow
      }
    }
  }

  const exportPptx = async () => {
    if (exporting) return
    setExporting(true)
    setError('')
    try {
      const slides = []
      for (let i = 0; i < deck.slides.length; i++) {
        const s = deck.slides[i]
        const showing = s.showVisual && s.variants.length > 0
        slides.push({
          title: s.title,
          bullets: s.bullets,
          notes: s.notes,
          imageDataUri: showing ? await visualPng(i) : null,
        })
      }
      await exportDeckPptx({ title: deck.title, subtitle: deck.subtitle, slides }, deck.title || 'inkling-deck', deckTheme)
    } catch {
      setError('The PowerPoint file could not be built — try again.')
    } finally {
      setExporting(false)
    }
  }

  const exportSlidePng = async (index: number) => {
    const el = slideRefs.current[index]
    if (!el) return
    try {
      const uri = await elementToPng(el, STUDY_EXPORT_CSS + DECK_EXPORT_CSS)
      downloadDataUri(uri, safePngName(`${deck.title}-slide-${index + 1}`, 'slide'))
    } catch {
      setError('That slide could not be saved as an image.')
    }
  }

  const jump = (index: number) =>
    slideRefs.current[index]?.scrollIntoView({ behavior: 'smooth', block: 'center' })

  const drawnCount = useMemo(
    () => deck.slides.filter(s => s.showVisual && s.variants.length > 0).length,
    [deck.slides],
  )

  return (
    <>
      <div className="dk-toolbar" role="toolbar" aria-label="Deck actions">
        <button type="button" className="ink-btn ink-btn-primary" onClick={exportPptx} disabled={exporting}>
          {exporting ? <span className="ss-spin" /> : <PhosphorIcon className="bi bi-file-earmark-slides" />}
          {exporting ? 'Building…' : 'PowerPoint'}
        </button>
        <ExportButtons
          title={deck.title}
          filenameBase="inkling-deck"
          subtitle={deck.subtitle || 'GenPrompt Inkling'}
          getBodyHtml={() => {
            const body = deckRef.current?.innerHTML
            return body ? `<style>${STUDY_EXPORT_CSS}${DECK_EXPORT_CSS}</style><div class="ss-render">${body}</div>` : null
          }}
        />
        {onSave && (
          <button type="button" className="ink-btn ink-btn-save" onClick={onSave} disabled={saving}>
            {saving ? <span className="ss-spin" /> : <PhosphorIcon className={`bi ${savedFlash ? 'bi-check-lg' : 'bi-floppy'}`} />}
            {savedFlash ? 'Saved' : 'Save'}
          </button>
        )}
        <button type="button" className="ink-btn" onClick={onStartOver} disabled={saving}>
          <PhosphorIcon className="bi bi-arrow-counterclockwise" /> Start over
        </button>

        {/* One definition drives both the slides on screen and the .pptx, so
            what is picked here is what opens in PowerPoint. */}
        <CustomSelect
          className="dk-theme-select"
          value={themeId}
          onChange={setThemeId}
          options={DECK_THEMES.map(t => ({
            value: t.id,
            label: t.label,
            description: t.blurb,
            icon: 'bi-palette',
          }))}
        />
      </div>

      <p className="dk-theme-note dk-chrome">
        <PhosphorIcon className="bi bi-info-circle" />
        {deckTheme.blurb} Slides you have drawn keep their bullets in the download,
        on a slide marked <strong>IN WORDS</strong> — delete those you do not want.
      </p>

      {error && <div className="ss-error dk-chrome"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      <div className="dk-layout">
        {/* ── Slide navigator ── */}
        <aside className="dk-rail" aria-label="Slides">
          <div className="dk-rail-head">
            <strong>{deck.slides.length} slides</strong>
            <small>{drawnCount} drawn</small>
          </div>
          <ol className="dk-rail-list">
            {deck.slides.map((s, i) => (
              <li key={i}>
                <button type="button" className="dk-rail-item" onClick={() => jump(i)}>
                  <span className="dk-rail-num">{i + 1}</span>
                  <span className="dk-rail-title">{s.title}</span>
                  {s.showVisual && s.variants.length > 0 && (
                    <PhosphorIcon className="bi bi-image dk-rail-flag" />
                  )}
                </button>
              </li>
            ))}
          </ol>
        </aside>

        {/* ── The deck ── */}
        <div
          className="dk-deck"
          ref={deckRef}
          style={{
            '--dk-bg': hex(deckTheme.bg),
            '--dk-title': hex(deckTheme.title),
            '--dk-body': hex(deckTheme.body),
            '--dk-accent': hex(deckTheme.accent),
            '--dk-muted': hex(deckTheme.muted),
            '--dk-title-font': deckTheme.titleFont,
            '--dk-body-font': deckTheme.bodyFont,
          } as CSSProperties}
        >
          <div className="dk-slide dk-slide--cover">
            <h2 className="dk-cover-title">{deck.title}</h2>
            {deck.subtitle && <p className="dk-cover-sub">{deck.subtitle}</p>}
          </div>

          {deck.slides.map((slide, i) => {
            const showing = slide.showVisual && slide.variants.length > 0
            const nominated = slide.visual.styleId ? studyStyleById(slide.visual.styleId) : null

            return (
              <div
                key={i}
                className="dk-slide"
                ref={el => { slideRefs.current[i] = el }}
                data-slide={i}
              >
                <div className="dk-slide-tools dk-chrome">
                  <span className="dk-slide-no">Slide {i + 1}</span>

                  {slide.variants.length > 0 && (
                    <div className="dk-swap" role="group" aria-label="Slide body">
                      <button
                        type="button"
                        className={!slide.showVisual ? 'active' : ''}
                        onClick={() => patch(i, { showVisual: false })}
                      >
                        <PhosphorIcon className="bi bi-text-left" /> Text
                      </button>
                      <button
                        type="button"
                        className={slide.showVisual ? 'active' : ''}
                        onClick={() => patch(i, { showVisual: true })}
                      >
                        <PhosphorIcon className="bi bi-image" /> Visual
                      </button>
                    </div>
                  )}

                  {slide.variants.length > 1 && slide.showVisual && (
                    <div className="dk-variants">
                      <button
                        type="button"
                        onClick={() => patch(i, { active: (slide.active - 1 + slide.variants.length) % slide.variants.length })}
                        aria-label="Previous variant"
                      >
                        <PhosphorIcon className="bi bi-chevron-left" />
                      </button>
                      <span>{slide.active + 1}/{slide.variants.length}</span>
                      <button
                        type="button"
                        onClick={() => patch(i, { active: (slide.active + 1) % slide.variants.length })}
                        aria-label="Next variant"
                      >
                        <PhosphorIcon className="bi bi-chevron-right" />
                      </button>
                    </div>
                  )}

                  {nominated && slide.variants.length === 0 && (
                    <button
                      type="button"
                      className="dk-nominate"
                      disabled={busy !== null}
                      onClick={() => draw(i, nominated.id)}
                      title={slide.visual.brief}
                    >
                      {busy === i ? <span className="ss-spin" /> : <PhosphorIcon className={`bi ${nominated.icon}`} />}
                      Draw as {nominated.label.toLowerCase()}
                    </button>
                  )}

                  <button type="button" className="dk-tool" disabled={busy !== null} onClick={() => openPicker(i)}>
                    <PhosphorIcon className="bi bi-easel" /> {slide.variants.length ? 'Draw another' : 'Draw this'}
                  </button>

                  <button type="button" className="dk-tool" onClick={() => setEditing(editing === i ? null : i)}>
                    <PhosphorIcon className="bi bi-pencil" /> {editing === i ? 'Done' : 'Edit'}
                  </button>

                  <button type="button" className="dk-tool" onClick={() => exportSlidePng(i)} title="Save this slide as a PNG">
                    <PhosphorIcon className="bi bi-download" />
                  </button>
                </div>

                {editing === i ? (
                  <div className="dk-edit dk-chrome">
                    <label>
                      Slide title
                      <input value={slide.title} onChange={e => patch(i, { title: e.target.value })} />
                    </label>
                    <label>
                      Bullets — one per line
                      <textarea
                        rows={5}
                        value={slide.bullets.join('\n')}
                        onChange={e => patch(i, { bullets: e.target.value.split('\n').map(b => b.trim()).filter(Boolean) })}
                      />
                    </label>
                    <label>
                      Speaker notes
                      <textarea rows={3} value={slide.notes} onChange={e => patch(i, { notes: e.target.value })} />
                    </label>
                  </div>
                ) : (
                  <>
                    <h3 className="dk-slide-title">{slide.title}</h3>
                    <div className="dk-rule" />
                    {showing ? (
                      <div className="dk-visual-render">
                        <StudyRenderer doc={slide.variants[slide.active]} />
                      </div>
                    ) : (
                      <ul className="dk-bullets">
                        {slide.bullets.map((b, k) => <li key={k}>{b}</li>)}
                      </ul>
                    )}
                    {slide.notes && (
                      <p className="dk-notes">
                        <PhosphorIcon className="bi bi-chat-left-text dk-chrome" /> {slide.notes}
                      </p>
                    )}
                  </>
                )}

                {/* ── Style picker for this slide ── */}
                {picker === i && (
                  <div className="dk-picker dk-chrome">
                    <div className="dk-picker-head">
                      <strong>Draw this slide as…</strong>
                      <button type="button" onClick={() => { setPicker(null); setSugs(null) }} aria-label="Close">
                        <PhosphorIcon className="bi bi-x-lg" />
                      </button>
                    </div>
                    {slide.visual.brief && <p className="dk-picker-brief">“{slide.visual.brief}”</p>}
                    {loadingSugs && <div className="dk-picker-loading"><span className="ss-spin" /> Finding the visuals that fit…</div>}
                    {sugs && sugs.length > 0 && (
                      <div className="dk-picker-grid">
                        {sugs.map(s => {
                          const meta = studyStyleById(s.styleId)
                          if (!meta) return null
                          return (
                            <button key={s.styleId} type="button" disabled={busy !== null} onClick={() => draw(i, s.styleId)}>
                              <PhosphorIcon className={`bi ${meta.icon}`} />
                              <span>
                                <strong>{meta.label}</strong>
                                <small>{s.reason}</small>
                              </span>
                            </button>
                          )
                        })}
                      </div>
                    )}
                    <div className="dk-picker-all">
                      <span>Or pick any style</span>
                      <CustomSelect
                        className="dk-style-select"
                        value=""
                        disabled={busy !== null}
                        placeholder="All styles"
                        options={STUDY_STYLES.map(st => ({
                          value: st.id,
                          label: st.label,
                          description: st.blurb,
                          icon: st.icon,
                        }))}
                        onChange={sid => { if (sid) draw(i, sid) }}
                      />
                    </div>
                  </div>
                )}
              </div>
            )
          })}
        </div>
      </div>
    </>
  )
}
