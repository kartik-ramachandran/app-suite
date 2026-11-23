// Turning an Inkling deck into a real .pptx.
//
// pptxgenjs is ~370 KB, and most people who open Present mode never export, so
// it is imported on click rather than bundled — the same treatment the 3D
// gamefield gets. Nothing here runs until someone actually asks for a file.
//
// Diagrams go in as PNGs. PowerPoint has no way to represent a mind map drawn
// as SVG-in-HTML, and a half-translated shape tree would be worse than a clean
// picture: this way what you saw in the editor is exactly what lands on the
// slide, and the text around it stays editable.
//
// Two things this file does itself rather than asking the library to:
//
//   Image fitting. pptxgenjs's sizing:{type:'contain'} does not shrink the
//     picture frame — it keeps the frame at the declared w×h and writes a
//     srcRect, so a tall diagram in a wide box comes out stretched. So the PNG
//     is measured, scaled to fit inside its box on the long edge, and centred.
//
//   Text fitting. The library's fit:'shrink' maps to PowerPoint's autofit,
//     which by its own documentation "only takes effect after editing
//     text/resize shape" — it does nothing on first open. So the font size is
//     computed here from how much text there is and how much room it has.
//
// Between them those are why text and pictures used to run off the slide.

import type { DeckTheme } from '../data/deckThemes'

export interface PptxSlide {
  title: string
  bullets: string[]
  notes: string
  /** A rendered visual for this slide, as a PNG data URI, when one is showing. */
  imageDataUri?: string | null
}

export interface PptxDeck {
  title: string
  subtitle: string
  slides: PptxSlide[]
}

/** Widescreen, in inches — every measurement below is in the same units. */
const W = 13.333
const H = 7.5
const MARGIN = 0.7

/** Where the body of a content slide lives, once the title has taken its share. */
const BODY_TOP = 2.0
const BODY_H = H - BODY_TOP - 0.8
const BODY_W = W - MARGIN * 2

// ── Fitting ─────────────────────────────────────────────────────────────────

/** Natural pixel size of a PNG data URI. */
const measure = (dataUri: string): Promise<{ w: number; h: number } | null> =>
  new Promise(resolve => {
    const img = new Image()
    img.onload = () => resolve({ w: img.naturalWidth, h: img.naturalHeight })
    img.onerror = () => resolve(null)
    img.src = dataUri
  })

/** Largest box with the image's aspect ratio that fits, centred in the space. */
function containBox(
  img: { w: number; h: number },
  x: number, y: number, boxW: number, boxH: number,
) {
  const scale = Math.min(boxW / img.w, boxH / img.h)
  const w = img.w * scale
  const h = img.h * scale
  return { x: x + (boxW - w) / 2, y: y + (boxH - h) / 2, w, h }
}

/**
 * A font size at which these lines fit the box.
 *
 * Deliberately an estimate: laying out text properly needs font metrics we do
 * not have in the browser without measuring every string. Average glyph width
 * is close enough to about half the point size for the faces used here, and
 * erring small costs a little whitespace while erring large costs a slide that
 * runs off the edge.
 */
function fitFontSize(
  lines: string[],
  boxW: number,
  boxH: number,
  maxPt: number,
  minPt: number,
  lineSpacing: number,
): number {
  // PowerPoint insets text about 0.1in each side by default, and the estimate
  // is biased narrow on purpose: too small costs whitespace, too large costs a
  // slide that runs off the edge. 0.58 rather than 0.5 of the point size covers
  // the wider faces (Georgia, Arial Black) as well as the narrow ones.
  const boxWpt = (boxW - 0.2) * 72
  const boxHpt = (boxH - 0.1) * 72

  for (let pt = maxPt; pt > minPt; pt -= 1) {
    const charsPerLine = Math.max(8, Math.floor(boxWpt / (pt * 0.58)))
    let rows = 0
    for (const line of lines) rows += Math.max(1, Math.ceil(line.length / charsPerLine))
    if (rows * pt * lineSpacing <= boxHpt) return pt
  }
  return minPt
}

// ── Export ──────────────────────────────────────────────────────────────────

export async function exportDeckPptx(
  deck: PptxDeck,
  filenameBase: string,
  theme: DeckTheme,
): Promise<void> {
  const { default: PptxGenJS } = await import('pptxgenjs')
  const pptx = new PptxGenJS()

  pptx.layout = 'LAYOUT_16x9'
  pptx.author = 'GenPrompt Inkling'
  pptx.title = deck.title

  // ── Cover ──
  const cover = pptx.addSlide()
  const flood = theme.cover === 'flood'
  cover.background = { color: flood ? theme.accent : theme.bg }
  if (theme.cover === 'bar') {
    cover.addShape('rect', { x: 0, y: 0, w: 0.26, h: H, fill: { color: theme.accent } })
  }

  const coverInk = flood ? (theme.coverTitle ?? 'FFFFFF') : theme.title
  const coverW = W - MARGIN * 2 - (theme.cover === 'bar' ? 0.3 : 0)
  const coverX = MARGIN + (theme.cover === 'bar' ? 0.3 : 0)

  cover.addText(deck.title, {
    x: coverX, y: 2.2, w: coverW, h: 2.0,
    fontSize: fitFontSize([deck.title], coverW, 2.0, 40, 20, 1.2),
    bold: true, color: coverInk, fontFace: theme.titleFont,
    valign: 'bottom', wrap: true, margin: 4,
  })
  if (deck.subtitle) {
    cover.addText(deck.subtitle, {
      x: coverX, y: 4.35, w: coverW, h: 1.0,
      fontSize: fitFontSize([deck.subtitle], coverW, 1.0, 18, 11, 1.3),
      color: flood ? coverInk : theme.muted, fontFace: theme.bodyFont,
      valign: 'top', wrap: true, margin: 4,
    })
  }

  // ── Content ──
  // Planned as a flat list first so numbering counts the slides that actually
  // exist, including the retained text ones.
  type Planned = { slide: PptxSlide; kind: 'visual' | 'text'; retained?: boolean }
  const planned: Planned[] = []
  for (const slide of deck.slides) {
    if (slide.imageDataUri) {
      planned.push({ slide, kind: 'visual' })
      // The words behind the picture, kept so the download is complete.
      if (slide.bullets.length > 0) planned.push({ slide, kind: 'text', retained: true })
    } else {
      planned.push({ slide, kind: 'text' })
    }
  }

  // Measured up front: addSlide is synchronous and ordering must be exact.
  const sizes = await Promise.all(
    planned.map(p => (p.kind === 'visual' && p.slide.imageDataUri
      ? measure(p.slide.imageDataUri)
      : Promise.resolve(null))),
  )

  planned.forEach(({ slide, kind, retained }, index) => {
    const s = pptx.addSlide()
    s.background = { color: theme.bg }

    let titleTop = 0.5
    if (retained) {
      // A quiet marker so a repeated title reads as deliberate, and is easy to
      // find and delete.
      s.addText('IN WORDS', {
        x: MARGIN, y: 0.42, w: BODY_W, h: 0.3,
        fontSize: 10, bold: true, charSpacing: 2,
        color: theme.muted, fontFace: theme.bodyFont,
      })
      titleTop = 0.78
    }

    const titleH = 1.05
    s.addText(slide.title, {
      x: MARGIN, y: titleTop, w: BODY_W, h: titleH,
      fontSize: fitFontSize([slide.title], BODY_W, titleH, 26, 14, 1.25),
      bold: true, color: theme.title, fontFace: theme.titleFont,
      valign: 'top', wrap: true, margin: 4,
    })
    s.addShape('rect', { x: MARGIN, y: 1.72, w: 1.1, h: 0.05, fill: { color: theme.accent } })

    const image = sizes[index]
    if (kind === 'visual' && slide.imageDataUri && image) {
      const box = containBox(image, MARGIN, BODY_TOP, BODY_W, BODY_H)
      s.addImage({ data: slide.imageDataUri, x: box.x, y: box.y, w: box.w, h: box.h })
    } else if (kind === 'visual' && slide.imageDataUri) {
      // Could not be measured — fall back to the full box rather than dropping it.
      s.addImage({ data: slide.imageDataUri, x: MARGIN, y: BODY_TOP, w: BODY_W, h: BODY_H })
    } else if (slide.bullets.length > 0) {
      s.addText(
        slide.bullets.map(text => ({
          text,
          options: { bullet: { characterCode: '25CF' }, breakLine: true },
        })),
        {
          x: MARGIN + 0.12, y: BODY_TOP, w: BODY_W - 0.12, h: BODY_H,
          // The bullet glyph and its indent eat roughly three characters a line.
          fontSize: fitFontSize(
            slide.bullets.map(b => `___${b}`), BODY_W - 0.12, BODY_H, 18, 10, 1.45,
          ),
          color: theme.body, fontFace: theme.bodyFont,
          lineSpacingMultiple: 1.3, valign: 'top', wrap: true, margin: 4,
        },
      )
    }

    s.addText(`${index + 1}`, {
      x: W - MARGIN - 0.8, y: H - 0.62, w: 0.8, h: 0.32,
      fontSize: 11, color: theme.muted, fontFace: theme.bodyFont, align: 'right',
    })

    if (slide.notes?.trim()) s.addNotes(slide.notes.trim())
  })

  await pptx.writeFile({ fileName: `${safeFileName(filenameBase)}.pptx` })
}

const safeFileName = (name: string) =>
  (name || 'deck').replace(/[^\w\s-]/g, '').trim().replace(/\s+/g, '-').slice(0, 80) || 'deck'
