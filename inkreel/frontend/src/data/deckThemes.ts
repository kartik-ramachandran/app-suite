// Deck templates.
//
// One definition drives both the on-screen slides and the exported .pptx, so
// what you pick in the editor is what opens in PowerPoint. Colours are stored
// without the leading '#' because that is the form pptxgenjs wants; the editor
// adds it back.
//
// Fonts are named, not embedded. Every face here ships with Office on Windows
// and macOS, so a deck opens looking the way it was built rather than falling
// back to something arbitrary on the first machine that opens it.

export interface DeckTheme {
  id: string
  label: string
  blurb: string
  /** Slide background. */
  bg: string
  /** Title text. */
  title: string
  /** Body text. */
  body: string
  /** Rules, bullets, the cover bar. */
  accent: string
  /** Muted text: subtitle, slide numbers. */
  muted: string
  titleFont: string
  bodyFont: string
  /** Cover treatment: a bar down the left, a full bleed, or nothing. */
  cover: 'bar' | 'flood' | 'plain'
  /** Title text on a flood cover, when the background goes dark. */
  coverTitle?: string
}

export const DECK_THEMES: DeckTheme[] = [
  {
    id: 'editorial',
    label: 'Editorial',
    blurb: 'Warm paper, ink black, a vermilion rule.',
    bg: 'FBF9F5',
    title: '1A1A1A',
    body: '2E2A26',
    accent: 'DF5439',
    muted: '8A8078',
    titleFont: 'Georgia',
    bodyFont: 'Calibri',
    cover: 'bar',
  },
  {
    id: 'minimal',
    label: 'Minimal',
    blurb: 'White, black, and nothing else.',
    bg: 'FFFFFF',
    title: '111111',
    body: '333333',
    accent: '111111',
    muted: '999999',
    titleFont: 'Calibri Light',
    bodyFont: 'Calibri',
    cover: 'plain',
  },
  {
    id: 'corporate',
    label: 'Corporate',
    blurb: 'Navy and slate — the safe one for a board.',
    bg: 'FFFFFF',
    title: '0F2740',
    body: '2C3E50',
    accent: '1F6FEB',
    muted: '7A8CA0',
    titleFont: 'Calibri Light',
    bodyFont: 'Calibri',
    cover: 'flood',
    coverTitle: 'FFFFFF',
  },
  {
    id: 'midnight',
    label: 'Midnight',
    blurb: 'Dark slides for a dark room.',
    bg: '11161D',
    title: 'F1F5F9',
    body: 'CBD5E1',
    accent: 'F0ABFC',
    muted: '7C8899',
    titleFont: 'Calibri Light',
    bodyFont: 'Calibri',
    cover: 'flood',
    coverTitle: 'F1F5F9',
  },
  {
    id: 'bold',
    label: 'Bold',
    blurb: 'Heavy type, magenta accent, high contrast.',
    bg: 'FFFFFF',
    title: '18181B',
    body: '27272A',
    accent: 'C13584',
    muted: '8A8A94',
    titleFont: 'Arial Black',
    bodyFont: 'Arial',
    cover: 'bar',
  },
  {
    id: 'academic',
    label: 'Academic',
    blurb: 'Serif throughout, forest green, quiet.',
    bg: 'FDFDFB',
    title: '1C2B22',
    body: '32403A',
    accent: '2F6B4F',
    muted: '7E8B84',
    titleFont: 'Georgia',
    bodyFont: 'Georgia',
    cover: 'bar',
  },
]

export const deckThemeById = (id: string | null | undefined): DeckTheme =>
  DECK_THEMES.find(t => t.id === id) ?? DECK_THEMES[0]

/** '1A1A1A' → '#1A1A1A', for the CSS side of the same definition. */
export const hex = (c: string) => `#${c}`
