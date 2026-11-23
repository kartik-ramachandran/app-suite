import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useLayoutEffect, useRef, useMemo } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import PromptPicker from '../components/PromptPicker'
import ExportButtons from '../components/ExportButtons'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import '../App.css'

type Phase = 'idle' | 'search' | 'research' | 'outline' | 'writing' | 'done' | 'error'

interface SavedArticle { id: string; topic: string; content: string; wordCount: number; savedAt: string }

function loadSaved(): SavedArticle[] {
  try { return JSON.parse(localStorage.getItem('saved_articles') ?? '[]') } catch { return [] }
}

function persistSaved(articles: SavedArticle[]) {
  localStorage.setItem('saved_articles', JSON.stringify(articles))
}

export default function ContentStudio() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('content-studio')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Content Studio' }])
    return () => setBreadcrumbs([])
  }, [])

  const [topic, setTopic] = useState('')
  const [phase, setPhase] = useState<Phase>('idle')
  const [finalContent, setFinalContent] = useState('')
  const [currentPhaseLabel, setCurrentPhaseLabel] = useState('')
  const [copied, setCopied] = useState(false)
  const [savedMsg, setSavedMsg] = useState('')
  const [errorMsg, setErrorMsg] = useState('')
  const [savedArticles, setSavedArticles] = useState<SavedArticle[]>(loadSaved)
  const [selectedArticle, setSelectedArticle] = useState<SavedArticle | null>(null)
  const abortRef = useRef<AbortController | null>(null)
  const exportRef = useRef<HTMLDivElement>(null)

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph). Prefill only.
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/content-studio')
    if (handoff?.input) setTopic(handoff.input)
  }, [])

  const PHASE_LABELS: Record<string, string> = {
    search: 'Researching the web…',
    research: 'Extracting key points…',
    outline: 'Building outline…',
    writing: 'Writing article…',
  }

  const handleGenerate = async () => {
    if (!topic.trim()) return
    abortRef.current?.abort()
    const ctrl = new AbortController()
    abortRef.current = ctrl

    setPhase('search')
    setCurrentPhaseLabel(PHASE_LABELS['search'])
    setFinalContent('')
    setErrorMsg('')
    setSavedMsg('')
    setSelectedArticle(null)

    const authToken = localStorage.getItem('authToken')
    const baseUrl = import.meta.env.VITE_API_BASE_URL || '/api'

    try {
      const resp = await fetch(`${baseUrl}/content/generate`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(authToken ? { Authorization: `Bearer ${authToken}` } : {})
        },
        body: JSON.stringify({ topic, contentType: 'article', tone: 'professional', targetLength: 800, includeWebSearch: true }),
        signal: ctrl.signal
      })

      if (!resp.ok) { setPhase('error'); setErrorMsg('Failed to start generation'); return }

      const reader = resp.body!.getReader()
      const decoder = new TextDecoder()
      let buf = ''
      let writingContent = ''

      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buf += decoder.decode(value, { stream: true })
        const lines = buf.split('\n')
        buf = lines.pop() || ''

        for (const line of lines) {
          if (!line.startsWith('data: ')) continue
          try {
            const data = JSON.parse(line.slice(6))
            if (data.type === 'error') { setPhase('error'); setErrorMsg(data.content); return }
            if (data.type === 'done') { setPhase('done'); setFinalContent(writingContent); return }
            if (data.type === 'writing') {
              writingContent += data.content
              setFinalContent(writingContent)
              setPhase('writing')
              setCurrentPhaseLabel(PHASE_LABELS['writing'])
            } else {
              setPhase(data.type as Phase)
              setCurrentPhaseLabel(PHASE_LABELS[data.type] ?? data.type)
            }
          } catch { /* skip */ }
        }
      }
    } catch (e) {
      if ((e as Error).name !== 'AbortError') {
        setPhase('error')
        setErrorMsg('Generation failed. Please try again.')
      }
    }
  }

  const handleStop = () => { abortRef.current?.abort(); setPhase('idle') }

  const copyContent = async () => {
    await navigator.clipboard.writeText(finalContent)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  const saveArticle = () => {
    const article: SavedArticle = {
      id: Date.now().toString(),
      topic: topic.slice(0, 60),
      content: finalContent,
      wordCount: finalContent.split(/\s+/).length,
      savedAt: new Date().toLocaleDateString()
    }
    const updated = [article, ...savedArticles]
    setSavedArticles(updated)
    persistSaved(updated)
    setSavedMsg('Saved!')
    setTimeout(() => setSavedMsg(''), 2000)
  }

  const deleteArticle = (id: string) => {
    const updated = savedArticles.filter(a => a.id !== id)
    setSavedArticles(updated)
    persistSaved(updated)
    if (selectedArticle?.id === id) setSelectedArticle(null)
  }

  const isRunning = phase !== 'idle' && phase !== 'done' && phase !== 'error'

  // Gamefield: the article passes down a four-desk line, editor in the corner.
  const DESK_ORDER: { id: Phase; name: string; role: string }[] = [
    { id: 'search', name: 'Search', role: 'finds the sources' },
    { id: 'research', name: 'Research', role: 'reads and notes' },
    { id: 'outline', name: 'Outline', role: 'shapes the piece' },
    { id: 'writing', name: 'Writer', role: 'drafts the article' },
  ]

  const field = useMemo<FieldModel>(() => {
    const order = DESK_ORDER.map(d => d.id)
    const at = order.indexOf(phase)
    const finished = phase === 'done'

    const desks = DESK_ORDER.map((d, i) => ({
      id: d.id,
      name: d.name,
      role: d.role,
      status: (finished || (at >= 0 && i < at) ? 'done'
        : at === i ? 'working'
        : phase === 'error' && at < 0 ? 'idle'
        : 'idle') as 'done' | 'working' | 'idle',
      detail: at === i ? (currentPhaseLabel || 'Working…') : undefined,
    }))

    return {
      title: 'Content Studio',
      subtitle: topic ? bubble(topic, 70) : 'Give it a keyword to work on.',
      running: phase !== 'idle' && phase !== 'done' && phase !== 'error',
      agents: [
        ...desks,
        {
          id: 'editor',
          name: 'Editor',
          role: finalContent ? `${finalContent.split(/\s+/).length} words` : 'signs it off',
          status: (errorMsg ? 'failed' : finished ? 'done' : phase === 'idle' ? 'idle' : 'blocked') as 'failed' | 'done' | 'idle' | 'blocked',
          detail: errorMsg || (finished ? 'Article ready.' : phase === 'idle' ? undefined : 'Waiting on the line.'),
          output: finalContent || undefined,
          boss: true,
        },
      ],
      messages: desks.filter(d => d.status === 'done').map(d => ({ id: `hand-${d.id}`, from: d.id, to: 'editor' })),
      events: [
        ...(currentPhaseLabel ? [{ id: 'phase', text: currentPhaseLabel, tone: 'info' as const }] : []),
        ...(finished ? [{ id: 'done', text: 'Draft finished and on the editor desk.', tone: 'good' as const }] : []),
        ...(errorMsg ? [{ id: 'err', text: errorMsg, tone: 'bad' as const }] : []),
      ],
    }
  }, [phase, currentPhaseLabel, finalContent, errorMsg, topic])

  return (
    <div style={{ display: 'grid', gridTemplateColumns: '1fr 220px', height: 'calc(100vh - 56px)' }}>

      {/* Main content area */}
      <div style={{ padding: '24px', overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: 20 }}>

        {/* Header */}
        <div>
          <h1 style={{ fontSize: 22, fontWeight: 700, margin: '0 0 4px' }}>Content Studio</h1>
          <p style={{ color: 'var(--text-secondary)', fontSize: 13, margin: 0 }}>
            Generate long-form SEO articles powered by AI research and publish them anywhere via webhook.
          </p>
        </div>

        <AgentStage
          {...stageProps}
          model={field}
          actions={phase !== 'idle' && phase !== 'done' && phase !== 'error'
            ? [{ id: 'stop', label: 'Stop', icon: 'bi-stop-circle', tone: 'danger', onClick: handleStop }]
            : [{ id: 'run', label: 'Write article', icon: 'bi-play-fill', tone: 'primary', disabled: !topic.trim(), onClick: handleGenerate }]}
        />

        {/* Input card */}
        <div className="card" style={{ padding: '20px 24px' }}>
          <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 10 }}>
            <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1 }}>KEYWORD OR TOPIC</label>
            <PromptPicker onSelect={p => setTopic(p.content)} />
          </div>
          <div style={{ display: 'flex', gap: 12, alignItems: 'center' }}>
            <input
              className="form-input"
              style={{ flex: 1, fontSize: 14 }}
              placeholder="e.g. best AI tools for content marketing 2025"
              value={topic}
              onChange={e => setTopic(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && handleGenerate()}
              disabled={isRunning}
            />
            {isRunning ? (
              <button className="btn btn-secondary" onClick={handleStop} style={{ whiteSpace: 'nowrap' }}>
                <PhosphorIcon className="bi bi-stop-fill" style={{ marginRight: 6 }} />Stop
              </button>
            ) : (
              <button className="btn btn-primary" onClick={handleGenerate} disabled={!topic.trim()} style={{ whiteSpace: 'nowrap' }}>
                Generate
              </button>
            )}
          </div>
          {isRunning && (
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginTop: 12, color: 'var(--primary)', fontSize: 13 }}>
              <div className="spinner" style={{ width: 14, height: 14 }} />
              {currentPhaseLabel}
            </div>
          )}
        </div>

        {/* Error */}
        {phase === 'error' && (
          <div style={{ color: '#ef4444', fontSize: 13, padding: '10px 14px', background: '#ef444410', borderRadius: 8, border: '1px solid #ef444330' }}>
            <PhosphorIcon className="bi bi-exclamation-circle" style={{ marginRight: 6 }} />{errorMsg}
          </div>
        )}

        {/* Generated article or selected saved article */}
        {selectedArticle ? (
          <div className="card" style={{ padding: '20px 24px', flex: 1 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
              <div>
                <h3 style={{ margin: 0, fontSize: 15, fontWeight: 600 }}>{selectedArticle.topic}</h3>
                <span style={{ fontSize: 12, color: 'var(--text-secondary)' }}>~{selectedArticle.wordCount} words · {selectedArticle.savedAt}</span>
              </div>
              <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={() => setSelectedArticle(null)}>
                <PhosphorIcon className="bi bi-x" style={{ marginRight: 4 }} />Close
              </button>
            </div>
            <div style={{ background: 'var(--bg-secondary)', padding: 16, borderRadius: 8, fontSize: 14, lineHeight: 1.7, whiteSpace: 'pre-wrap', maxHeight: 500, overflowY: 'auto' }}>
              {selectedArticle.content}
            </div>
          </div>
        ) : (phase === 'writing' || phase === 'done') && finalContent ? (
          <div className="card" style={{ padding: '20px 24px', flex: 1 }}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
              <div>
                <h3 style={{ margin: 0, fontSize: 15, fontWeight: 600 }}>
                  Article
                  {phase === 'writing' && <span style={{ fontSize: 12, color: 'var(--primary)', marginLeft: 10 }}>Writing…</span>}
                </h3>
                {phase === 'done' && <span style={{ fontSize: 12, color: 'var(--text-secondary)' }}>~{finalContent.split(/\s+/).length} words</span>}
              </div>
              {phase === 'done' && (
                <div style={{ display: 'flex', gap: 8 }}>
                  <button className="btn btn-secondary" style={{ fontSize: 13 }} onClick={copyContent}>
                    <PhosphorIcon className={`bi ${copied ? 'bi-check2' : 'bi-clipboard'}`} style={{ marginRight: 4 }} />
                    {copied ? 'Copied' : 'Copy'}
                  </button>
                  <button className="btn btn-primary" style={{ fontSize: 13 }} onClick={saveArticle}>
                    <PhosphorIcon className="bi bi-bookmark" style={{ marginRight: 4 }} />
                    {savedMsg || 'Save'}
                  </button>
                </div>
              )}
            </div>
            <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 10 }}>
              <ExportButtons
                title={topic || 'Content Studio'}
                filenameBase="content-studio"
                subtitle="GenPrompt Content Studio"
                getBodyHtml={() => exportRef.current?.innerHTML}
                size="sm"
              />
            </div>
            <div ref={exportRef} data-visualize style={{ background: 'var(--bg-secondary)', padding: 16, borderRadius: 8, fontSize: 14, lineHeight: 1.7, whiteSpace: 'pre-wrap', maxHeight: 560, overflowY: 'auto' }}>
              {finalContent}
              {phase === 'writing' && <span style={{ animation: 'pulse 1s infinite' }}>|</span>}
            </div>
          </div>
        ) : null}
      </div>

      {/* Saved Articles panel */}
      <div style={{ borderLeft: '1px solid var(--border)', background: 'var(--card-bg)', display: 'flex', flexDirection: 'column', overflowY: 'auto' }}>
        <div style={{ padding: '16px 16px 10px', borderBottom: '1px solid var(--border)', flexShrink: 0 }}>
          <h4 style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-secondary)', letterSpacing: 1, margin: 0 }}>SAVED ARTICLES</h4>
        </div>
        {savedArticles.length === 0 ? (
          <div style={{ flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', padding: 24, textAlign: 'center' }}>
            <PhosphorIcon className="bi bi-file-earmark-text" style={{ fontSize: 32, color: 'var(--text-muted)', marginBottom: 10 }} />
            <p style={{ fontSize: 12, color: 'var(--text-muted)', lineHeight: 1.5, margin: 0 }}>
              No saved articles yet.<br />Generate one and click Save.
            </p>
          </div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', padding: 8, gap: 4 }}>
            {savedArticles.map(a => (
              <div
                key={a.id}
                onClick={() => setSelectedArticle(a)}
                style={{
                  padding: '10px 12px', borderRadius: 8, cursor: 'pointer',
                  border: `1.5px solid ${selectedArticle?.id === a.id ? 'var(--primary)' : 'transparent'}`,
                  background: selectedArticle?.id === a.id ? 'var(--bg-secondary)' : 'transparent',
                  transition: 'background 0.15s'
                }}
              >
                <p style={{ margin: '0 0 2px', fontSize: 12, fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{a.topic}</p>
                <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                  <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>~{a.wordCount}w · {a.savedAt}</span>
                  <button
                    onClick={e => { e.stopPropagation(); deleteArticle(a.id) }}
                    style={{ background: 'none', border: 'none', cursor: 'pointer', padding: 2, color: 'var(--text-muted)', fontSize: 12 }}
                  >
                    <PhosphorIcon className="bi bi-trash" />
                  </button>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  )
}
