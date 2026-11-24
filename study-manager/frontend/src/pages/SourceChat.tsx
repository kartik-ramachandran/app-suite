import { useCallback, useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import api from '../services/api'
import PhosphorIcon from '../components/PhosphorIcon'
import './SourceChat.css'

/**
 * Chat with a source: a PDF, a Word file or a YouTube video.
 * Answers come only from the source, with clickable citations back to the
 * page (documents) or the moment in the video.
 */

interface Source {
  id: number
  kind: 'pdf' | 'document' | 'youtube'
  title: string
  origin?: string | null
  charCount: number
  createdAt: string
}

interface Citation {
  n: number
  label: string
  page?: number | null
  seconds?: number | null
  link?: string | null
  excerpt: string
}

interface Message {
  id?: number
  role: 'user' | 'assistant'
  content: string
  citations?: Citation[] | null
}

const SUGGESTIONS: Record<Source['kind'], string[]> = {
  youtube: ['Summarise this video in 5 bullet points', 'What are the key takeaways?', 'Make a short quiz from this video'],
  pdf: ['Summarise this document', 'What are the main points?', 'List anything I need to act on'],
  document: ['Summarise this document', 'What are the main points?', 'List anything I need to act on'],
}

const errorText = (e: unknown, fallback: string) => {
  const ax = e as { response?: { data?: { error?: string } } }
  return ax.response?.data?.error ?? fallback
}

const icon = (k: Source['kind']) => (k === 'youtube' ? 'bi-youtube' : k === 'pdf' ? 'bi-file-earmark-pdf' : 'bi-file-earmark-text')

/** Turns "[3]" markers in an answer into small numbered chips. */
function AnswerText({ text, citations, onCite }: { text: string; citations?: Citation[] | null; onCite: (c: Citation) => void }) {
  const byN = new Map((citations ?? []).map(c => [c.n, c]))
  const parts = text.split(/(\[\d{1,4}\])/g)
  return (
    <div className="sc-answer-text">
      {parts.map((p, i) => {
        const m = /^\[(\d{1,4})\]$/.exec(p)
        const c = m ? byN.get(Number(m[1])) : undefined
        if (c) {
          return (
            <button key={i} className="sc-cite" title={`${c.label}: ${c.excerpt}`} onClick={() => onCite(c)}>
              {c.n}
            </button>
          )
        }
        return <span key={i}>{p}</span>
      })}
    </div>
  )
}

export default function SourceChat() {
  const [sources, setSources] = useState<Source[]>([])
  const [active, setActive] = useState<Source | null>(null)
  const [messages, setMessages] = useState<Message[]>([])
  const [question, setQuestion] = useState('')
  const [url, setUrl] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [focus, setFocus] = useState<Citation | null>(null)
  const fileRef = useRef<HTMLInputElement>(null)
  const endRef = useRef<HTMLDivElement>(null)

  const loadSources = useCallback(async () => {
    try {
      const res = await api.get<Source[]>('/sources')
      setSources(res.data)
      return res.data
    } catch { return [] }
  }, [])

  useEffect(() => { loadSources() }, [loadSources])
  useEffect(() => { endRef.current?.scrollIntoView({ behavior: 'smooth', block: 'end' }) }, [messages, busy])

  const open = async (s: Source) => {
    setActive(s); setFocus(null); setError(null); setMessages([])
    try {
      const res = await api.get<Message[]>(`/sources/${s.id}/messages`)
      setMessages(res.data)
    } catch { /* new source, no history */ }
  }

  const added = async (s: Source) => {
    await loadSources()
    await open(s)
  }

  const upload = async (file: File) => {
    setBusy(`Reading ${file.name}…`); setError(null)
    const form = new FormData()
    form.append('file', file)
    try {
      const res = await api.post<Source>('/sources/file', form, { timeout: 180000 })
      await added(res.data)
    } catch (e) { setError(errorText(e, 'That file could not be added.')) }
    finally { setBusy(null); if (fileRef.current) fileRef.current.value = '' }
  }

  const addVideo = async (e: FormEvent) => {
    e.preventDefault()
    if (!url.trim()) return
    setBusy('Fetching the video’s captions…'); setError(null)
    try {
      const res = await api.post<Source>('/sources/youtube', { url: url.trim() }, { timeout: 120000 })
      setUrl('')
      await added(res.data)
    } catch (err) { setError(errorText(err, 'That video could not be added.')) }
    finally { setBusy(null) }
  }

  const ask = async (q?: string) => {
    const text = (q ?? question).trim()
    if (!text || !active || busy) return
    setQuestion(''); setError(null)
    setMessages(m => [...m, { role: 'user', content: text }])
    setBusy('Thinking…')
    try {
      const res = await api.post<{ answer: string; citations: Citation[] }>(`/sources/${active.id}/ask`, { question: text }, { timeout: 600000 })
      setMessages(m => [...m, { role: 'assistant', content: res.data.answer, citations: res.data.citations }])
    } catch (e) {
      setError(errorText(e, 'No answer came back. Try again.'))
      setMessages(m => m.slice(0, -1))
      setQuestion(text)
    } finally { setBusy(null) }
  }

  const remove = async (s: Source) => {
    if (!confirm(`Remove “${s.title}” and its chat?`)) return
    await api.delete(`/sources/${s.id}`).catch(() => {})
    if (active?.id === s.id) { setActive(null); setMessages([]) }
    await loadSources()
  }

  const clearChat = async () => {
    if (!active) return
    await api.delete(`/sources/${active.id}/messages`).catch(() => {})
    setMessages([])
  }

  return (
    <div className="sc-page">
      <header className="sc-head">
        <h1><PhosphorIcon className="bi bi-chat-square-quote" /> Chat with a source</h1>
        <p>Add a PDF, a Word file or a YouTube video, then ask it anything. Every answer points back to the page or the moment it came from.</p>
      </header>

      <div className="sc-layout">
        <aside className="sc-side">
          <div className="sc-add">
            <button className="sc-btn sc-btn-hot" onClick={() => fileRef.current?.click()} disabled={!!busy}>
              <PhosphorIcon className="bi bi-upload" /> Add PDF or Word
            </button>
            <input ref={fileRef} type="file" hidden accept=".pdf,.docx,.txt,.md"
              onChange={e => { const f = e.target.files?.[0]; if (f) upload(f) }} />
            <form className="sc-yt" onSubmit={addVideo}>
              <PhosphorIcon className="bi bi-youtube" />
              <input value={url} onChange={e => setUrl(e.target.value)} placeholder="Paste a YouTube link" disabled={!!busy} />
              <button className="sc-btn" type="submit" disabled={!url.trim() || !!busy}>Add</button>
            </form>
          </div>

          <h2>Your sources</h2>
          {sources.length === 0 && <p className="sc-empty-side">Nothing yet. Add a file or a video to start.</p>}
          <ul className="sc-list">
            {sources.map(s => (
              <li key={s.id} className={active?.id === s.id ? 'sc-on' : undefined}>
                <button className="sc-item" onClick={() => open(s)}>
                  <span className={`sc-kind sc-kind-${s.kind}`}><PhosphorIcon className={`bi ${icon(s.kind)}`} /></span>
                  <span className="sc-item-title">{s.title}</span>
                </button>
                <button className="sc-del" onClick={() => remove(s)} aria-label={`Remove ${s.title}`}>
                  <PhosphorIcon className="bi bi-trash" />
                </button>
              </li>
            ))}
          </ul>
        </aside>

        <section className="sc-chat">
          {!active ? (
            <div className="sc-welcome">
              <div className="sc-welcome-art">
                <span><PhosphorIcon className="bi bi-file-earmark-pdf" /></span>
                <span><PhosphorIcon className="bi bi-youtube" /></span>
                <span><PhosphorIcon className="bi bi-chat-dots" /></span>
              </div>
              <h2>Pick a source, then ask away</h2>
              <p>Long lecture? Dense report? Ask for a summary, a quiz, or the one thing you need, and jump straight to where it’s said.</p>
            </div>
          ) : (
            <>
              <div className="sc-chat-head">
                <span className={`sc-kind sc-kind-${active.kind}`}><PhosphorIcon className={`bi ${icon(active.kind)}`} /></span>
                <div>
                  <h2>{active.title}</h2>
                  {active.origin
                    ? <a href={active.origin} target="_blank" rel="noreferrer">Open on YouTube ↗</a>
                    : <span>{Math.round(active.charCount / 1000)}k characters</span>}
                </div>
                {messages.length > 0 && (
                  <button className="sc-btn sc-btn-ghost" onClick={clearChat}><PhosphorIcon className="bi bi-eraser" /> Clear chat</button>
                )}
              </div>

              <div className="sc-messages">
                {messages.length === 0 && !busy && (
                  <div className="sc-suggest">
                    {SUGGESTIONS[active.kind].map(s => (
                      <button key={s} onClick={() => ask(s)}>{s}</button>
                    ))}
                  </div>
                )}
                {messages.map((m, i) => (
                  <div key={i} className={`sc-msg sc-${m.role}`}>
                    {m.role === 'assistant'
                      ? <AnswerText text={m.content} citations={m.citations} onCite={setFocus} />
                      : <div className="sc-answer-text">{m.content}</div>}
                    {m.role === 'assistant' && m.citations && m.citations.length > 0 && (
                      <div className="sc-sources">
                        {m.citations.map(c => (
                          c.link
                            ? <a key={c.n} href={c.link} target="_blank" rel="noreferrer" className="sc-chip">▶ {c.label}</a>
                            : <button key={c.n} className="sc-chip" onClick={() => setFocus(c)}>{c.label}</button>
                        ))}
                      </div>
                    )}
                  </div>
                ))}
                {busy && <div className="sc-msg sc-assistant sc-typing"><span /><span /><span /></div>}
                <div ref={endRef} />
              </div>

              {focus && (
                <div className="sc-focus">
                  <div className="sc-focus-head">
                    <strong>[{focus.n}] {focus.label}</strong>
                    {focus.link && <a href={focus.link} target="_blank" rel="noreferrer">Watch from here ↗</a>}
                    <button onClick={() => setFocus(null)} aria-label="Close"><PhosphorIcon className="bi bi-x-lg" /></button>
                  </div>
                  <p>{focus.excerpt}</p>
                </div>
              )}

              <form className="sc-composer" onSubmit={e => { e.preventDefault(); ask() }}>
                <textarea value={question} rows={2} placeholder="Ask about this source…"
                  onChange={e => setQuestion(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); ask() } }} />
                <button className="sc-btn sc-btn-hot" type="submit" disabled={!question.trim() || !!busy}>
                  <PhosphorIcon className="bi bi-send" /> Ask
                </button>
              </form>
            </>
          )}
          {busy && !active && <div className="sc-busy"><span className="sc-spin" />{busy}</div>}
          {error && <div className="sc-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
        </section>
      </div>
    </div>
  )
}
