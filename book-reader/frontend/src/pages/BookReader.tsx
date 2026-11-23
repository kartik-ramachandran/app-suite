import PhosphorIcon from '../components/PhosphorIcon'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import CharacterAvatar from '../components/CharacterAvatar'
import FigureImage from '../components/FigureImage'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import { StudyRenderer, type StudyDoc } from '../components/StudyRender'
import api from '../services/api'
import './BookReader.css'

/**
 * Upload a book, let AI cast its characters, then listen with a different
 * voice per speaker.
 *
 * One page, three states — library, uploading, reading — rather than three
 * routes, because the whole flow is "pick a book and press play" and a router
 * hop between each step makes it feel like paperwork.
 */

interface BookSummary {
  id: string
  title: string
  author?: string
  format?: string
  status: string
  statusMessage?: string
  wordCount?: number
  currentLineIndex: number
  completionPercentage: number
  lastReadAt?: string
  uploadedAt: string
  genre?: string
  source?: string
  subject?: string
  publishedYear?: string
  importedCount?: number
  /** What the import extracted, named per genre: 'study topics', 'recipes'. */
  importedLabel?: string
  importedAt?: string
  characterCount: number
  lineCount: number
}

interface Character {
  id: number
  name: string
  description?: string
  gender: string
  assignedVoiceId?: string
  lineCount: number
  traits: string[]
}

interface Line {
  lineIndex: number
  speaker: string
  text: string
  isNarration: boolean
  page?: number
  /** What the model judged this block to be: heading | body | caption. */
  kind?: string | null
  /** URLs for figures printed on this line's page, when it is a caption. */
  figures?: string[] | null
}

interface BookDetail {
  id: string
  title: string
  author?: string
  status: string
  currentLineIndex: number
  completionPercentage: number
  wordCount?: number
  totalLines: number
  neuralVoices: boolean
  genre: string
  genreConfidence: number
  importedCount: number
  importedAt?: string
  destination?: {
    genre: string
    label: string
    module: string
    moduleLabel: string
    route: string
    icon: string
    extractLabel: string
    importable: boolean
    /** API path for this genre's import, e.g. '/study/import-from-book'. */
    importEndpoint?: string | null
    imported: boolean
  } | null
  characters: Character[]
  lines: Line[]
}

const PAGE = 200

/** Colour per speaker so the eye can follow who is talking without reading names. */
const SPEAKER_COLOURS = [
  '#d62976', '#0369a1', '#15803d', '#b45309',
  '#7c3aed', '#be185d', '#0f766e', '#c2410c',
]

// ── Book-styled rendering for non-fiction ──────────────────────────────────
// A reference book's lines are not all prose: some are chapter headings, some
// are NOTE/IMPORTANT banners, some carry URLs. Fiction keeps plain text; a
// tech book gets typeset like the page it came from.

const URL_RE = /https?:\/\/[^\s)]+/g

/** Classifies a line so CSS can typeset it: heading, callout, or body. */
function lineKind(text: string): string {
  const t = text.trim()
  // "Chapter 2 Implement and manage storage" / "Skill 1.3: Manage subscriptions"
  if (/^(chapter|part|appendix)\s+\d+/i.test(t) && t.length < 90) return 'br-h1'
  if (/^(skill|objective|lesson|module)\s+[\d.]+/i.test(t) && t.length < 110) return 'br-h2'
  // Short line, no sentence punctuation, title-cased start — a section heading.
  if (t.length < 60 && !/[.!?:;]$/.test(t) && /^[A-Z]/.test(t) && t.split(' ').length <= 8
      && !URL_RE.test(t)) return 'br-h3'
  if (/^(important|note|warning|caution|exam tip|real world)\b/i.test(t)) return 'br-callout'
  // "FIGURE 1-43 Moving resources diagram" — a picture existed here in print.
  if (/^figure\s+\d+[-.]\d+/i.test(t) && t.length < 120) return 'br-figure'
  return ''
}

/**
 * Prose with URLs as tappable chips instead of 80 characters of read-aloud
 * slash-soup. The chip opens in a new tab; the click must not also jump
 * playback to the line, hence stopPropagation.
 */
function renderBookText(text: string): React.ReactNode {
  const parts = text.split(URL_RE)
  const urls = text.match(URL_RE)
  if (!urls) return text
  const out: React.ReactNode[] = []
  parts.forEach((part, i) => {
    out.push(part)
    if (urls[i]) {
      let label: string
      try { label = new URL(urls[i]).hostname.replace(/^www\./, '') } catch { label = 'link' }
      out.push(
        <a key={i} className="br-url" href={urls[i]} target="_blank" rel="noreferrer"
           onClick={e => e.stopPropagation()}>
          {label} ↗
        </a>,
      )
    }
  })
  return out
}

export default function BookReader() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('bookshelf')
  const [books, setBooks] = useState<BookSummary[]>([])
  const [neuralVoices, setNeuralVoices] = useState(false)
  const [uploadsOff, setUploadsOff] = useState('')
  const [snapping, setSnapping] = useState(false)
  const [advice, setAdvice] = useState('')
  const [recommending, setRecommending] = useState(false)
  const [quota, setQuota] = useState<{ used: number; remaining: number | null; limit: number; unlimited: boolean } | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [upgrade, setUpgrade] = useState('')

  // Upload
  const [uploading, setUploading] = useState(false)
  const fileRef = useRef<HTMLInputElement>(null)
  const shelfRef = useRef<HTMLInputElement>(null)

  // Gamefield: the same office the old Bookshelf page had — intake → vision →
  // filing. Declared with the other hooks (the library render sits below two
  // early returns, and a hook there would blow up on view changes); busy
  // covers both ways a book arrives.
  const field = useSnapFieldModel({
    title: 'Bookshelf',
    busy: snapping || uploading,
    error,
    count: books.length,
    noun: 'books',
  })

  // Reader
  const [book, setBook] = useState<BookDetail | null>(null)
  const [lines, setLines] = useState<Line[]>([])
  const [current, setCurrent] = useState(0)
  const [playing, setPlaying] = useState(false)
  const [voices, setVoices] = useState<{ id: string; label: string }[]>([])

  // Two ways to read the same lines. 'scroll' is the continuous list; 'book'
  // lays them out as paper leaves you turn. Remembered per book, because
  // someone who wants pages wants them every time they open it.
  const [mode, setMode] = useState<'scroll' | 'book'>(
    () => (localStorage.getItem('br-mode') === 'book' ? 'book' : 'scroll'))
  const [leaf, setLeaf] = useState(0)
  const [turning, setTurning] = useState<'next' | 'prev' | null>(null)
  const [showCast, setShowCast] = useState(true)
  const [importing, setImporting] = useState(false)
  const [imported, setImported] = useState<{ count: number; titles: string[] } | null>(null)

  // Recreated figures, keyed by the FIGURE line's index. Session-only cache —
  // the generated doc also lands in Study Studio history for keeps.
  const [figureDocs, setFigureDocs] = useState<Record<number, StudyDoc>>({})
  const [figureBusy, setFigureBusy] = useState<number | null>(null)

  /**
   * Redraws a print figure from the prose around its caption. The paragraphs
   * on either side describe the picture ("the VM in Resource Group 2 can be
   * moved into…"), which is exactly what the flowchart generator needs.
   */
  const recreateFigure = async (lineIndex: number) => {
    const caption = lines.find(l => l.lineIndex === lineIndex)?.text ?? ''
    const around = lines
      .filter(l => l.lineIndex >= lineIndex - 3 && l.lineIndex <= lineIndex + 3)
      .map(l => l.text).join('\n')
    setFigureBusy(lineIndex)
    try {
      const res = await api.post('/study/generate', {
        styleId: 'flowchart',
        topic: caption.slice(0, 200),
        content: around,
        useWebSearch: false,
      })
      setFigureDocs(d => ({ ...d, [lineIndex]: { id: res.data.id, styleId: 'flowchart', title: res.data.title, data: res.data.data } }))
    } catch {
      setError('Could not recreate this figure — please try again.')
    } finally {
      setFigureBusy(null)
    }
  }
  const [offerDismissed, setOfferDismissed] = useState(false)

  const audioRef = useRef<HTMLAudioElement | null>(null)
  const playingRef = useRef(false)
  const lineRefs = useRef<Record<number, HTMLDivElement | null>>({})

  useEffect(() => {
    setBreadcrumbs(id
      ? [{ label: 'Book Reader', path: '/book-reader' }, { label: book?.title || 'Reading' }]
      : [{ label: 'Book Reader' }])
  }, [setBreadcrumbs, id, book?.title])

  // ── Library ───────────────────────────────────────────────────────────────

  const loadLibrary = useCallback(async () => {
    try {
      const res = await api.get('/book-reader/books')
      setBooks(res.data.books ?? [])
      setNeuralVoices(!!res.data.neuralVoices)
      setUploadsOff(res.data.uploadsEnabled === false ? (res.data.uploadsDisabledReason || 'Uploads are unavailable.') : '')
      setQuota({
        used: res.data.booksUsed ?? 0,
        remaining: res.data.booksRemaining ?? null,
        limit: res.data.freeLimit ?? 3,
        unlimited: !!res.data.unlimited,
      })
    } catch {
      setError('Could not load your library.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { if (!id) loadLibrary() }, [id, loadLibrary])

  // Poll while anything is still being prepared. Stops as soon as everything
  // has settled so an idle library is not making requests forever.
  useEffect(() => {
    if (id) return
    const pending = books.some(b => ['uploaded', 'parsing', 'analysing'].includes(b.status))
    if (!pending) return
    const t = setInterval(loadLibrary, 3000)
    return () => clearInterval(t)
  }, [id, books, loadLibrary])

  // ── Upload ────────────────────────────────────────────────────────────────

  const upload = async (file: File) => {
    setUploading(true); setError(''); setUpgrade('')
    try {
      const form = new FormData()
      form.append('file', file)
      form.append('title', file.name.replace(/\.[^.]+$/, ''))
      await api.post('/book-reader/books', form)
      await loadLibrary()
    } catch (e: any) {
      // 402 is the free-limit answer, and it deserves an upgrade prompt rather
      // than a red error — the user did nothing wrong.
      if (e?.response?.status === 402) {
        setUpgrade(e.response.data?.detail || 'Upgrade to add more books.')
      } else {
        setError(e?.response?.data?.error || 'That upload did not work.')
      }
    } finally {
      setUploading(false)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  // Re-runs analysis on a book already in the library. Free — the user is
  // not adding a book, and the earlier result being wrong is not their fault.
  const reanalyse = async (bookId: string) => {
    try {
      await api.post(`/book-reader/books/${bookId}/reanalyse`)
      await loadLibrary()
    } catch (e: any) {
      // 429 is the daily re-analysis cap. It is not an error the user caused,
      // so it explains the limit instead of just refusing.
      if (e?.response?.status === 429) {
        const d = e.response.data
        setError(`${d?.error ?? 'Daily limit reached.'} ${d?.detail ?? ''}`.trim())
        return
      }
      setError(e?.response?.data?.error || 'Could not re-read that book.')
    }
  }

  // Photograph a shelf: catalogues books the user owns on paper. Was the
  // whole of the old Bookshelf module.
  const snapShelf = async (file: File) => {
    setSnapping(true); setError('')
    try {
      const dataUrl = await new Promise<string>((resolve, reject) => {
        const fr = new FileReader()
        fr.onload = () => resolve(String(fr.result))
        fr.onerror = () => reject(new Error('read failed'))
        fr.readAsDataURL(file)
      })
      const res = await api.post('/book-reader/snap', { imageData: dataUrl })
      if (!res.data.added) setError(res.data.message || 'No books could be read from that photo.')
      await loadLibrary()
    } catch (e: any) {
      setError(e?.response?.data?.error || 'Could not read that photo.')
    } finally {
      setSnapping(false)
      if (shelfRef.current) shelfRef.current.value = ''
    }
  }

  // Reads across everything owned - uploaded and photographed. The old
  // Bookshelf could only see snapped books, so it ignored the user's real
  // reading entirely.
  const recommend = async () => {
    setRecommending(true); setError('')
    try {
      const res = await api.post('/book-reader/recommend')
      setAdvice(res.data.advice || res.data.message || '')
    } catch {
      setError('Could not think of suggestions just now.')
    } finally {
      setRecommending(false)
    }
  }

  const remove = async (bookId: string) => {
    // Deleting frees storage, not allowance — saying otherwise would be a lie
    // the user only discovers after they have lost the book.
    const warn = quota && !quota.unlimited
      ? 'Delete this book? This will not give you another free upload.'
      : 'Delete this book?'
    if (!confirm(warn)) return
    try {
      await api.delete(`/book-reader/books/${bookId}`)
      setBooks(prev => prev.filter(b => b.id !== bookId))
      setUpgrade('')
    } catch {
      setError('Could not delete that book.')
    }
  }

  // ── Reader ────────────────────────────────────────────────────────────────

  const loadBook = useCallback(async (bookId: string, skip = 0) => {
    const res = await api.get(`/book-reader/books/${bookId}?skip=${skip}&take=${PAGE}`)
    const data: BookDetail = res.data
    setBook(data)
    setLines(prev => (skip === 0 ? data.lines : [...prev, ...data.lines]))
    if (skip === 0) setCurrent(data.currentLineIndex || 0)
    return data
  }, [])

  useEffect(() => {
    if (!id) { setBook(null); setLines([]); return }
    setLoading(true)
    loadBook(id, 0)
      .then(() => api.get('/book-reader/voices'))
      .then(r => setVoices(r.data.voices ?? []))
      .catch(() => setError('Could not open that book.'))
      .finally(() => setLoading(false))
  }, [id, loadBook])

  // Keep the active line in view during playback without yanking the page
  // while the user is reading ahead.
  useEffect(() => {
    if (!playing) return
    lineRefs.current[current]?.scrollIntoView({ behavior: 'smooth', block: 'center' })
  }, [current, playing])

  // Sends the book's content to the module its genre belongs to. Opt-in by
  // design: this is an expensive AI pass, and a misclassified book silently
  // filling someone's Kitchen would be worse than never offering.
  const importToModule = async () => {
    if (!book?.destination?.importable) return
    // The genre says where its import lands; the old hardcoded recipes path
    // sent a study guide's topics into the Kitchen. Study imports ask for the
    // full cram pack — notes plus flashcards, practice quiz and a diagram per
    // topic — because half a study aid is what the banner used to apologise for.
    const endpoint = book.destination.importEndpoint || '/recipes/import-from-book'
    const query = endpoint.includes('/study/') ? '?mode=cram' : ''
    setImporting(true); setError('')
    try {
      const res = await api.post(`${endpoint}/${book.id}${query}`)
      setImported({ count: res.data.imported ?? 0, titles: res.data.titles ?? [] })
      setBook(b => b && ({ ...b, importedCount: res.data.imported ?? 0, importedAt: new Date().toISOString() }))
    } catch (e: any) {
      setError(e?.response?.data?.error || 'Could not import from this book.')
    } finally {
      setImporting(false)
    }
  }

  const saveProgress = useCallback(async (lineIndex: number) => {
    if (!id) return
    try { await api.put(`/book-reader/books/${id}/progress`, { lineIndex }) } catch { /* progress is best-effort */ }
  }, [id])

  /**
   * Speaks one line, resolving when it finishes.
   *
   * Server-rendered neural audio when a speech key is configured; otherwise the
   * browser's own synthesiser. The fallback is what makes the free tier work on
   * day one rather than waiting on Azure configuration.
   */
  const speak = useCallback(async (line: Line): Promise<void> => {
    if (!id) return

    if (neuralVoices) {
      try {
        // The chosen voice is part of the URL on purpose. Audio is cached hard
        // (24h) because re-synthesising an unchanged line is a bill for
        // nothing — but that cache is keyed on the URL, so without this a
        // voice change replays the previously cached mp3 and looks broken.
        const voiceKey = book?.characters.find(c => c.name === line.speaker)?.assignedVoiceId ?? 'default'
        const res = await api.get(
          `/book-reader/books/${id}/lines/${line.lineIndex}/audio?v=${encodeURIComponent(voiceKey)}`,
          { responseType: 'blob' },
        )
        // 204 means the server declined to synthesise — fall through to browser.
        if (res.status !== 204 && res.data?.size > 0) {
          const url = URL.createObjectURL(res.data)
          return await new Promise<void>(resolve => {
            const audio = new Audio(url)
            audioRef.current = audio
            const done = () => { URL.revokeObjectURL(url); resolve() }
            audio.onended = done
            audio.onerror = done
            audio.play().catch(done)
          })
        }
      } catch { /* fall through to the browser voice */ }
    }

    return await new Promise<void>(resolve => {
      if (!('speechSynthesis' in window)) { resolve(); return }
      const utter = new SpeechSynthesisUtterance(line.text)
      // Rough gender match so the browser fallback still distinguishes
      // speakers, even though it cannot match the assigned neural voice.
      const available = window.speechSynthesis.getVoices()
      const character = book?.characters.find(c => c.name === line.speaker)
      if (character && available.length) {
        const wantFemale = character.gender === 'female'
        const match = available.find(v =>
          wantFemale ? /female|zira|samantha|karen/i.test(v.name) : /male|david|daniel|alex/i.test(v.name))
        if (match) utter.voice = match
      }
      utter.onend = () => resolve()
      utter.onerror = () => resolve()
      window.speechSynthesis.speak(utter)
    })
  }, [id, neuralVoices, book])

  const stop = useCallback(() => {
    playingRef.current = false
    setPlaying(false)
    audioRef.current?.pause()
    audioRef.current = null
    if ('speechSynthesis' in window) window.speechSynthesis.cancel()
  }, [])

  const play = useCallback(async (from: number) => {
    if (!book) return
    playingRef.current = true
    setPlaying(true)

    for (let i = from; i < book.totalLines; i++) {
      if (!playingRef.current) return

      // Page in more lines as playback reaches the end of what is loaded.
      let line = lines.find(l => l.lineIndex === i)
      if (!line) {
        const data = await loadBook(book.id, Math.floor(i / PAGE) * PAGE)
        line = data.lines.find(l => l.lineIndex === i)
        if (!line) break
      }

      setCurrent(i)
      await speak(line)
      if (!playingRef.current) return
      if (i % 10 === 0) void saveProgress(i)
    }

    playingRef.current = false
    setPlaying(false)
    void saveProgress(book.totalLines)
  }, [book, lines, speak, loadBook, saveProgress])

  useEffect(() => () => stop(), [stop])   // never keep talking after unmount

  // Lines are grouped into leaves by their weight, not by a fixed count: a
  // figure is worth far more vertical space than a sentence, so counting lines
  // would leave some pages half empty and overflow others.
  const leaves = useMemo(() => {
    if (mode !== 'book') return []

    const BUDGET = 1500          // roughly one printed page
    const FIGURE_COST = 900      // a screenshot eats most of a page
    const out: Line[][] = []
    let page: Line[] = []
    let used = 0

    for (const line of lines) {
      const cost = line.text.length + (line.figures?.length ? FIGURE_COST : 0)

      // A caption belongs with its figure, and a heading belongs with the text
      // it introduces - neither should be stranded at the foot of a page.
      if (used + cost > BUDGET && page.length > 0) {
        out.push(page)
        page = []
        used = 0
      }

      page.push(line)
      used += cost
    }

    if (page.length > 0) out.push(page)
    return out
  }, [lines, mode])

  // Keep the leaf in step with the line being read, so switching modes or
  // playing audio does not leave the page behind.
  useEffect(() => {
    if (mode !== 'book' || leaves.length === 0) return
    const i = leaves.findIndex(p => p.some(l => l.lineIndex === current))
    if (i >= 0 && i !== leaf) setLeaf(i)
  }, [current, leaves, mode])

  const turn = useCallback((dir: 'next' | 'prev') => {
    setLeaf(prev => {
      const next = dir === 'next' ? prev + 1 : prev - 1
      if (next < 0 || next >= leaves.length) return prev
      setTurning(dir)
      // Matches the CSS animation; clearing it early kills the flip mid-way.
      setTimeout(() => setTurning(null), 420)
      return next
    })
  }, [leaves.length])

  // Arrow keys and space are what people reach for in a paged reader.
  useEffect(() => {
    if (mode !== 'book') return
    const onKey = (e: KeyboardEvent) => {
      const el = e.target as HTMLElement
      if (el?.tagName === 'INPUT' || el?.tagName === 'SELECT' || el?.tagName === 'TEXTAREA') return
      if (e.key === 'ArrowRight' || e.key === 'PageDown') { e.preventDefault(); turn('next') }
      if (e.key === 'ArrowLeft' || e.key === 'PageUp') { e.preventDefault(); turn('prev') }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [mode, turn])

  const [loadingMore, setLoadingMore] = useState(false)

  const loadMore = useCallback(async () => {
    if (!book || loadingMore || lines.length >= book.totalLines) return
    setLoadingMore(true)
    try {
      await loadBook(book.id, lines.length)
    } finally {
      setLoadingMore(false)
    }
  }, [book, lines.length, loadingMore])

  const chooseMode = (next: 'scroll' | 'book') => {
    setMode(next)
    localStorage.setItem('br-mode', next)
  }

  const setVoice = async (characterId: number, voiceId: string) => {
    try {
      await api.put(`/book-reader/characters/${characterId}/voice`, { voiceId })
      setBook(b => b && ({
        ...b,
        characters: b.characters.map(c => c.id === characterId ? { ...c, assignedVoiceId: voiceId } : c),
      }))

      // A line already playing was synthesised in the OLD voice, so letting it
      // finish means the change appears not to have worked. Stop, and let the
      // user press play again to hear the new voice from this line on.
      if (playing) stop()
    } catch { setError('Could not change that voice.') }
  }

  const colourFor = (speaker: string) => {
    if (!book) return SPEAKER_COLOURS[0]
    const i = book.characters.findIndex(c => c.name === speaker)
    return i < 0 ? 'var(--pg-text-light, #666)' : SPEAKER_COLOURS[i % SPEAKER_COLOURS.length]
  }

  // ── Render: reader ────────────────────────────────────────────────────────

  if (id) {
    if (loading) return <div className="br-wrap"><p className="br-muted">Opening…</p></div>
    if (!book) return <div className="br-wrap"><p className="br-error">{error || 'Book not found.'}</p></div>

    const isFiction = book.genre === 'fiction'

    // One renderer for both modes: a line must look and behave the same
    // whether it is in the scrolling list or on a paper leaf.
    const renderLine = (line: Line) => {
      // The model tagged each block during structuring, so there is nothing
      // to guess from the text here.
      const kind = isFiction ? '' : (line.kind ? `br-${line.kind}` : lineKind(line.text))
        return (
        <div
          key={line.lineIndex}
          ref={el => { lineRefs.current[line.lineIndex] = el }}
          className={`br-line ${line.lineIndex === current ? 'br-line-active' : ''} ${line.isNarration ? 'br-narration' : ''} ${kind}`}
          onClick={() => { stop(); setCurrent(line.lineIndex); void saveProgress(line.lineIndex) }}
        >
          {!line.isNarration && (
            <span className="br-speaker" style={{ color: colourFor(line.speaker) }}>{line.speaker}</span>
          )}
          <span className="br-text">{isFiction ? line.text : renderBookText(line.text)}</span>

          {/* The figure as it appears in the book, lifted straight out of
              the PDF. This is the real thing, so it needs no disclaimer
              and no AI call. */}
          {line.figures?.map(src => (
            <FigureImage key={src} src={src} alt={line.text} />
          ))}

          {/* Only when the PDF carried no image for this caption: ask the
              AI to redraw it from the surrounding prose, honestly
              labelled as a recreation. */}
          {kind === 'br-figure' && !line.figures?.length && !figureDocs[line.lineIndex] && (
            <button
              className="br-figure-go"
              disabled={figureBusy === line.lineIndex}
              onClick={e => { e.stopPropagation(); void recreateFigure(line.lineIndex) }}
            >
              <PhosphorIcon className="bi bi-diagram-3" />
              {figureBusy === line.lineIndex ? 'Redrawing…' : 'Recreate this diagram'}
            </button>
          )}
          {figureDocs[line.lineIndex] && (
            <div className="br-figure-card" onClick={e => e.stopPropagation()}>
              <StudyRenderer doc={figureDocs[line.lineIndex]} />
              <p className="br-figure-note">AI recreation from the book's description — not the original figure.</p>
            </div>
          )}
        </div>
        )
    }

    const speaker = lines.find(l => l.lineIndex === current)?.speaker

    return (
      <div className="br-reader-shell">
        {/* The office launcher lives on every view of this page, not only the
            library — mounting it in one branch made it vanish the moment a
            book was opened. */}
        <AgentStage {...stageProps} model={field} />
        <div className="br-bar">
          <button className="br-icon" onClick={() => { stop(); navigate('/book-reader') }} title="Back to library">
            <PhosphorIcon className="bi bi-arrow-left" />
          </button>
          <div className="br-bar-title">
            <strong>{book.title}</strong>
            {book.author && <span className="br-muted"> · {book.author}</span>}
          </div>
          <span className="br-pct">{Math.round((current / Math.max(1, book.totalLines)) * 100)}%</span>

          {/* Read it as a list, or as a book you turn. */}
          <div className="br-mode" role="group" aria-label="Reading mode">
            <button
              className={`br-mode-btn ${mode === 'scroll' ? 'active' : ''}`}
              onClick={() => chooseMode('scroll')}
              title="Continuous scrolling text"
            >
              <PhosphorIcon className="bi bi-list-ul" /> Inkling
            </button>
            <button
              className={`br-mode-btn ${mode === 'book' ? 'active' : ''}`}
              onClick={() => chooseMode('book')}
              title="Turn the pages like a book"
            >
              <PhosphorIcon className="bi bi-book" /> Book
            </button>
          </div>
          <button className="br-icon br-panel-toggle" onClick={() => setShowCast(s => !s)} title="Toggle panel">
            <PhosphorIcon className="bi bi-people" />
          </button>
        </div>

        <div className="br-progress">
          <div className="br-progress-fill" style={{ width: `${(current / Math.max(1, book.totalLines)) * 100}%` }} />
        </div>

        {/* What this book could become. Shown once, dismissible, and never
            acted on without a click. */}
        {book.destination && !book.destination.imported && !imported && !offerDismissed && (
          <div className="br-offer">
            <PhosphorIcon className={`bi ${book.destination.icon}`} />
            <div className="br-offer-body">
              <strong>This looks like a {book.destination.label.toLowerCase()}.</strong>
              {book.destination.importable ? (
                <p>
                  Add its {book.destination.extractLabel} to your {book.destination.moduleLabel}?
                  You can undo it afterwards.
                </p>
              ) : (
                <p>
                  Reading it aloud for now — importing {book.destination.extractLabel} into{' '}
                  {book.destination.moduleLabel} is coming.
                </p>
              )}
            </div>
            {book.destination.importable && (
              <button className="br-offer-go" onClick={importToModule} disabled={importing}>
                {importing ? 'Reading the book…' : `Add to ${book.destination.moduleLabel}`}
              </button>
            )}
            <button className="br-offer-x" onClick={() => setOfferDismissed(true)} title="Not now">
              <PhosphorIcon className="bi bi-x" />
            </button>
          </div>
        )}

        {imported && (
          <div className="br-offer br-offer-done">
            <PhosphorIcon className="bi bi-check-circle" />
            <div className="br-offer-body">
              <strong>
                {imported.count > 0
                  ? `Added ${imported.count} ${book.destination?.extractLabel ?? 'items'} to your ${book.destination?.moduleLabel}.`
                  : `No complete ${book.destination?.extractLabel ?? 'items'} could be found in this book.`}
              </strong>
              {imported.titles.length > 0 && <p>{imported.titles.slice(0, 4).join(' · ')}…</p>}
            </div>
            {imported.count > 0 && book.destination && (
              <button className="br-offer-go" onClick={() => navigate(book.destination!.route)}>
                Open {book.destination.moduleLabel}
              </button>
            )}
          </div>
        )}

        <div className={`br-cols ${showCast ? '' : 'br-cols-solo'}`}>
          {/* data-visualize opts this column into the global ✨ napkin flow
              (VisualizeSelection in AuthenticatedLayout): select a passage,
              get a ranked visual, rendered by StudyRenderer and saved to
              Study Studio — the same machinery Inkling uses.
              br-book: non-fiction reads as a typeset page — paper, upright
              serif, real headings — not italic narration soup. */}
          {/* The same lines, laid out two ways. */}
          {mode === 'book' ? (
            <div className="br-paper" data-visualize>
              <div className={`br-leaf ${turning ? `br-turn-${turning}` : ''}`} key={leaf}>
                {(leaves[leaf] ?? []).map(line => renderLine(line))}
              </div>

              <div className="br-flip">
                <button
                  className="br-flip-btn"
                  onClick={() => turn('prev')}
                  disabled={leaf === 0}
                  aria-label="Previous page"
                >
                  <PhosphorIcon className="bi bi-chevron-left" />
                </button>

                <span className="br-flip-count">
                  Page {leaf + 1} of {leaves.length}
                </span>

                <button
                  className="br-flip-btn"
                  onClick={() => turn('next')}
                  disabled={leaf >= leaves.length - 1}
                  aria-label="Next page"
                >
                  <PhosphorIcon className="bi bi-chevron-right" />
                </button>
              </div>

              {lines.length < book.totalLines && leaf >= leaves.length - 2 && (
                <button className="br-more" onClick={() => void loadMore()} disabled={loadingMore}>
                  {loadingMore ? 'Loading…' : `Load more · ${lines.length} of ${book.totalLines}`}
                </button>
              )}
            </div>
          ) : (
          <div className={`br-lines ${isFiction ? '' : 'br-book'}`} data-visualize>
            {lines.map(line => renderLine(line))}

            {lines.length < book.totalLines && (
              <button className="br-more" onClick={() => void loadMore()} disabled={loadingMore}>
                {loadingMore ? 'Loading…' : `Load more · ${lines.length} of ${book.totalLines}`}
              </button>
            )}
          </div>
          )}

          {/* RHS panel. Any book with characters shows them — fiction gets its
              cast, non-fiction gets its one Narrator row (pixel avatar + the
              voice picker). The facts list stays for non-fiction below. */}
          {showCast && (
            <aside className="br-panel">
              {book.characters.length > 0 ? (
                <>
                  <div className="br-panel-head">
                    <h3>{isFiction ? 'Cast' : 'Read by'}</h3>
                    <span className="br-count">{book.characters.length}</span>
                  </div>

                  {book.characters.map((c, i) => {
                    // A narrator has no speaker tag on its lines, so match
                    // it to any narration line that is currently playing -
                    // otherwise the avatar sits mute through the whole book.
                    const isNarrator = c.name.toLowerCase() === 'narrator'
                    const isSpeaking = playing && (
                      isNarrator ? (!speaker || speaker.toLowerCase() === 'narrator') : speaker === c.name
                    )
                    return (
                      <div className={`br-char ${isSpeaking ? 'br-char-live' : ''}`} key={c.id}>
                        <CharacterAvatar
                          name={c.name}
                          gender={c.gender}
                          colour={SPEAKER_COLOURS[i % SPEAKER_COLOURS.length]}
                          speaking={isSpeaking}
                        />
                        <div className="br-char-body">
                          <div className="br-char-name" style={{ color: SPEAKER_COLOURS[i % SPEAKER_COLOURS.length] }}>
                            {c.name}
                          </div>
                          {c.description && <p className="br-char-desc">{c.description}</p>}
                          <div className="br-char-meta">
                            <span>{c.lineCount} {c.lineCount === 1 ? 'line' : 'lines'}</span>
                            {c.traits?.slice(0, 2).map(t => <em key={t}>{t}</em>)}
                          </div>
                          <select
                            value={c.assignedVoiceId || ''}
                            onChange={e => setVoice(c.id, e.target.value)}
                            disabled={!book.neuralVoices}
                            title={book.neuralVoices ? 'Change this character\'s voice' : 'Neural voices are not configured on this server'}
                          >
                            {voices.map(v => <option key={v.id} value={v.id}>{v.label}</option>)}
                          </select>
                        </div>
                      </div>
                    )
                  })}

                  {/* Non-fiction keeps its facts under the narrator card. */}
                  {!isFiction && (
                    <dl className="br-facts">
                      <div><dt>Sections</dt><dd>{book.totalLines}</dd></div>
                      {book.wordCount ? <div><dt>Words</dt><dd>{book.wordCount.toLocaleString()}</dd></div> : null}
                      <div><dt>Progress</dt><dd>{book.completionPercentage}%</dd></div>
                    </dl>
                  )}
                </>
              ) : (
                <>
                  <div className="br-panel-head">
                    <h3>This book</h3>
                  </div>
                  <p className="br-muted br-pad">
                    No named characters were found — one clear voice reads it
                    end to end.
                  </p>
                  <dl className="br-facts">
                    <div><dt>Sections</dt><dd>{book.totalLines}</dd></div>
                    {book.wordCount ? <div><dt>Words</dt><dd>{book.wordCount.toLocaleString()}</dd></div> : null}
                    <div><dt>Progress</dt><dd>{book.completionPercentage}%</dd></div>
                  </dl>
                </>
              )}
            </aside>
          )}
        </div>

        <div className="br-controls">
          <button className="br-icon" onClick={() => { stop(); setCurrent(Math.max(0, current - 1)) }} title="Previous">
            <PhosphorIcon className="bi bi-chevron-left" />
          </button>
          <button className="br-play" onClick={() => (playing ? stop() : play(current))}>
            <PhosphorIcon className={playing ? 'bi bi-pause' : 'bi bi-play-fill'} />
            {playing ? 'Pause' : 'Play'}
          </button>
          <button className="br-icon" onClick={() => { stop(); setCurrent(Math.min(book.totalLines - 1, current + 1)) }} title="Next">
            <PhosphorIcon className="bi bi-chevron-right" />
          </button>
        </div>
      </div>
    )
  }

  // ── Render: library ───────────────────────────────────────────────────────

  return (
    <div className="br-wrap">
      <div className="br-head">
        <div>
          <h1>My Books</h1>
          <p className="br-muted">
            Upload a book to hear it read aloud, or photograph a shelf to catalogue what you own.
            {quota && !quota.unlimited && (
              <> <strong>{quota.remaining} of {quota.limit} free uploads left.</strong></>
            )}
          </p>
        </div>
        <div className="br-head-actions">
          {books.length >= 2 && (
            <button className="br-secondary" onClick={recommend} disabled={recommending}>
              <PhosphorIcon className="bi bi-stars" />
              {recommending ? 'Thinking…' : 'What next?'}
            </button>
          )}
          {/* Cataloguing a paper book was the old Bookshelf module. */}
          <button className="br-secondary" onClick={() => shelfRef.current?.click()} disabled={snapping}>
            <PhosphorIcon className="bi bi-camera" />
            {snapping ? 'Reading the shelf…' : 'Snap a shelf'}
          </button>
          <button
            className="br-upload"
            onClick={() => fileRef.current?.click()}
            disabled={uploading || !!uploadsOff}
            title={uploadsOff || undefined}
          >
            <PhosphorIcon className="bi bi-upload" />
            {uploading ? 'Uploading…' : 'Add a book'}
          </button>
        </div>
        <input
          ref={shelfRef}
          type="file"
          accept="image/*"
          style={{ display: 'none' }}
          onChange={e => { const f = e.target.files?.[0]; if (f) void snapShelf(f) }}
        />
        <input
          ref={fileRef}
          type="file"
          accept=".pdf,.epub,.docx,.doc,.txt,.md,.html,.htm"
          style={{ display: 'none' }}
          onChange={e => { const f = e.target.files?.[0]; if (f) void upload(f) }}
        />
      </div>

      {error && <p className="br-error">{error}</p>}

      <AgentStage {...stageProps} model={field} />

      {advice && (
        <div className="br-advice">
          <div className="br-advice-head">
            <h3><PhosphorIcon className="bi bi-stars" /> What to read next</h3>
            <button className="br-offer-x" onClick={() => setAdvice('')}><PhosphorIcon className="bi bi-x" /></button>
          </div>
          {advice.split('\n').filter(Boolean).map((l, i) => <p key={i}>{l.replace(/^[-*]\s*/, '')}</p>)}
        </div>
      )}

      {/* Server-side storage is not configured: say so plainly rather than
          letting the upload button fail on click. */}
      {uploadsOff && <p className="br-error">{uploadsOff}</p>}

      {upgrade && (
        <div className="br-upgrade">
          <PhosphorIcon className="bi bi-stars" />
          <div>
            <strong>{upgrade}</strong>
            {/* Payment is arranged by hand for now, so this points at contact
                rather than a pricing page that cannot take money yet. */}
            <p>Accounts get {quota?.limit ?? 3} books in total. Get in touch and we'll sort out a larger library.</p>
          </div>
          <button onClick={() => navigate('/contact')}>Contact us</button>
        </div>
      )}

      {loading && <p className="br-muted">Loading…</p>}

      {!loading && books.length === 0 && (
        <div className="br-empty">
          <PhosphorIcon className="bi bi-book" />
          <h2>No books yet</h2>
          <p>PDF, EPUB, Word, or plain text — up to 100 MB. Your first {quota?.limit ?? 3} books are free.</p>
          <button onClick={() => fileRef.current?.click()}>Upload your first book</button>
        </div>
      )}

      <div className="br-grid">
        {books.map(b => {
          const busy = ['uploaded', 'parsing', 'analysing'].includes(b.status)
          const failed = b.status === 'failed'
          return (
            <div
              className={`br-card ${busy ? 'br-card-busy' : ''} ${b.status === 'ready' ? 'br-card-open' : ''}`}
              key={b.id}
              // The whole card opens the book — a card with one small button is
              // a target people miss, and everyone tries clicking the cover.
              onClick={() => { if (b.status === 'ready') navigate(`/book-reader/${b.id}`) }}
              role={b.status === 'ready' ? 'button' : undefined}
              tabIndex={b.status === 'ready' ? 0 : undefined}
              data-source={b.source || 'upload'}
              onKeyDown={e => {
                if (b.status === 'ready' && (e.key === 'Enter' || e.key === ' ')) {
                  e.preventDefault()
                  navigate(`/book-reader/${b.id}`)
                }
              }}
            >
              <div className="br-card-top">
                <PhosphorIcon className="bi bi-book" />
                {/* stopPropagation: delete must not also open the book. */}
                <button className="br-del" onClick={e => { e.stopPropagation(); remove(b.id) }} title="Delete">
                  <PhosphorIcon className="bi bi-trash" />
                </button>
              </div>
              <h3>{b.title}</h3>
              {b.author && <p className="br-muted">{b.author}</p>}

              {busy && (
                <p className="br-status">
                  <span className="br-spin" /> {b.statusMessage || 'Preparing…'}
                </p>
              )}

              {failed && <p className="br-error br-small">{b.statusMessage || 'This book could not be prepared.'}</p>}

              {b.status === 'ready' && (
                <>
                  {/* A catalogued book has no text, so it gets what a shelf
                      photo can actually tell us instead of a line count. */}
                  {b.source === 'snap' ? (
                    <p className="br-meta">
                      {[b.subject, b.publishedYear].filter(Boolean).join(' · ') || 'On your shelf'}
                    </p>
                  ) : (
                  <p className="br-meta">
                    {b.importedCount
                      ? <>{b.importedCount} {b.importedLabel || 'items'} imported · {b.lineCount.toLocaleString()} sections</>
                      : b.genre === 'fiction'
                        ? <>{b.characterCount} {b.characterCount === 1 ? 'character' : 'characters'} · {b.lineCount.toLocaleString()} lines</>
                        : <>Narrated · {b.lineCount.toLocaleString()} sections</>}
                  </p>
                  )}
                  {b.completionPercentage > 0 && (
                    <div className="br-mini-progress">
                      <div style={{ width: `${b.completionPercentage}%` }} />
                    </div>
                  )}
                  {/* You cannot listen to a paper book: a catalogued entry
                      gets a plain label where the play button would be. */}
                  {b.source === 'snap' ? (
                    <span className="br-shelf-tag">
                      <PhosphorIcon className="bi bi-bookmark" /> On your shelf
                    </span>
                  ) : (
                    <div className="br-card-actions">
                      <button className="br-open" onClick={e => { e.stopPropagation(); navigate(`/book-reader/${b.id}`) }}>
                        <PhosphorIcon className="bi bi-play-fill" />
                        {b.completionPercentage > 0 ? `Resume · ${b.completionPercentage}%` : 'Listen'}
                      </button>
                      <button
                        className="br-icon br-recheck"
                        onClick={e => { e.stopPropagation(); void reanalyse(b.id) }}
                        title="Read this book again — re-detects the type and re-casts the characters"
                      >
                        <PhosphorIcon className="bi bi-arrow-clockwise" />
                      </button>
                    </div>
                  )}
                </>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}
