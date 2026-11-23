import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useRef, useState, useMemo } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import ExportButtons from '../components/ExportButtons'
import api from '../services/api'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import './SocialPack.css'

interface Platform { key: string; label: string; icon: string; limit: number; open: string; intent?: boolean }

const PLATFORMS: Platform[] = [
  { key: 'linkedin', label: 'LinkedIn', icon: 'bi-linkedin', limit: 1500, open: 'https://www.linkedin.com/feed/' },
  { key: 'twitter', label: 'X (Twitter)', icon: 'bi-twitter-x', limit: 280, open: 'https://twitter.com/intent/tweet?text=', intent: true },
  { key: 'instagram', label: 'Instagram', icon: 'bi-instagram', limit: 2200, open: 'https://www.instagram.com/' },
  { key: 'facebook', label: 'Facebook', icon: 'bi-facebook', limit: 63206, open: 'https://www.facebook.com/' },
  { key: 'threads', label: 'Threads', icon: 'bi-at', limit: 500, open: 'https://www.threads.net/' },
  { key: 'tiktok', label: 'TikTok', icon: 'bi-tiktok', limit: 2200, open: 'https://www.tiktok.com/upload' },
  { key: 'newsletter', label: 'Newsletter', icon: 'bi-envelope-paper', limit: 0, open: 'mailto:?body=', intent: true },
]

const TONES = ['professional', 'casual', 'enthusiastic', 'witty', 'inspirational', 'bold']

export default function SocialPack() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('social-pack')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Social Pack' }])
    return () => setBreadcrumbs([])
  }, [])

  const [source, setSource] = useState('')
  const [tone, setTone] = useState('professional')
  const [webSearch, setWebSearch] = useState(true)
  const [selected, setSelected] = useState<string[]>(PLATFORMS.map(p => p.key))
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [posts, setPosts] = useState<Record<string, string>>({})
  const [copied, setCopied] = useState<string | null>(null)
  const exportRef = useRef<HTMLDivElement>(null)

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph)
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/social-pack')
    if (handoff?.input) setSource(handoff.input)
  }, [])

  // Schedule-daily
  const [scheduleHour, setScheduleHour] = useState(9)
  const [scheduling, setScheduling] = useState(false)
  const [scheduled, setScheduled] = useState(false)

  const togglePlatform = (k: string) =>
    setSelected(prev => prev.includes(k) ? prev.filter(x => x !== k) : [...prev, k])

  const generate = async () => {
    if (!source.trim()) { setError('Enter a topic or paste content to post about.'); return }
    if (selected.length === 0) { setError('Pick at least one platform.'); return }
    setLoading(true); setError(''); setPosts({}); setScheduled(false)
    try {
      const { data } = await api.post('/content/social-pack', { source: source.trim(), tone, platforms: selected, includeWebSearch: webSearch })
      setPosts(data.posts || {})
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not generate posts — try again.')
    } finally { setLoading(false) }
  }

  const copy = async (key: string, text: string) => {
    try { await navigator.clipboard.writeText(text); setCopied(key); setTimeout(() => setCopied(null), 1800) } catch { /* ignore */ }
  }

  const openPlatform = (p: Platform, text: string) => {
    if (p.key === 'newsletter') {
      const lines = text.split('\n')
      const first = (lines[0] ?? '').trim()
      let subject = '', body = text
      if (first.toLowerCase().startsWith('subject:')) {
        subject = first.slice(8).trim()
        body = lines.slice(1).join('\n').trim()
      }
      window.open(`mailto:?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`, '_blank', 'noopener')
      return
    }
    const url = p.intent ? p.open + encodeURIComponent(text) : p.open
    window.open(url, '_blank', 'noopener')
  }

  const scheduleDaily = async () => {
    setScheduling(true); setError('')
    try {
      await api.post('/jobs', {
        name: `Daily social pack: ${source.trim().slice(0, 40)}`,
        agentType: 'social',
        input: `platforms=${selected.join(',')}|${source.trim()}`,
        schedule: 'daily',
        hour: scheduleHour,
        isActive: true,
      })
      setScheduled(true)
    } catch { setError('Could not schedule the daily job.') }
    finally { setScheduling(false) }
  }

  const orderedResults = PLATFORMS.filter(p => posts[p.key] != null)

  // Gamefield: one writer per platform, the editor holding the brief.
  const field = useMemo<FieldModel>(() => {
    const desks = PLATFORMS.filter(p => selected.includes(p.key)).map(p => ({
      id: `pf-${p.key}`,
      name: p.label,
      role: `${p.limit} char limit`,
      status: (posts[p.key] ? 'done' : loading ? 'working' : 'idle') as 'done' | 'working' | 'idle',
      detail: posts[p.key] ? bubble(posts[p.key]) : loading ? 'Writing for this platform…' : undefined,
      output: posts[p.key],
    }))

    const written = Object.keys(posts).length

    return {
      title: 'Social Pack',
      subtitle: source ? bubble(source, 70) : 'Give the room something to post about.',
      running: loading,
      agents: [
        ...desks,
        {
          id: 'editor',
          name: 'Editor',
          role: `${tone}${webSearch ? ' · web' : ''}`,
          status: (error ? 'failed' : loading ? 'working' : written ? 'done' : 'idle') as 'failed' | 'working' | 'done' | 'idle',
          detail: error ? bubble(error) : loading ? 'Briefing the writers…' : written ? `${written} posts ready to copy.` : undefined,
          boss: true,
        },
      ],
      messages: desks.filter(d => d.status === 'done').map(d => ({ id: `back-${d.id}`, from: d.id, to: 'editor' })),
      events: [
        ...PLATFORMS.filter(p => posts[p.key]).map(p => ({ id: `w-${p.key}`, text: `${p.label} post written.`, tone: 'good' as const })),
        ...(scheduled ? [{ id: 'sched', text: `A fresh pack is scheduled daily at ${scheduleHour}:00.`, tone: 'info' as const }] : []),
        ...(error ? [{ id: 'err', text: error, tone: 'bad' as const }] : []),
      ],
    }
  }, [posts, loading, selected, error, source, tone, webSearch, scheduled, scheduleHour])

  return (
    <div className="sp-page">
      <div className="sp-hero">
        <h1><PhosphorIcon className="bi bi-megaphone" /> Social Pack</h1>
        <p>One topic in, ready-to-post copy out — tailored for every social platform and your email newsletter. Generate now, or schedule a fresh pack every day.</p>
      </div>

      {error && <div className="sp-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      <AgentStage
        {...stageProps}
        model={field}
        actions={[{ id: 'run', label: loading ? 'Writing…' : 'Generate pack', icon: 'bi-play-fill', tone: 'primary', disabled: loading || !source.trim(), onClick: generate }]}
      />

      <section className="sp-card">
        <label className="sp-label">What do you want to post about?</label>
        <textarea
          className="sp-textarea"
          rows={4}
          placeholder="A topic, an announcement, or paste a link — I'll read the page and post about it…"
          value={source}
          onChange={e => setSource(e.target.value)}
        />

        <label className="sp-web-toggle">
          <input type="checkbox" checked={webSearch} onChange={e => setWebSearch(e.target.checked)} />
          <span><PhosphorIcon className="bi bi-globe" /> Search the web for fresh, accurate context</span>
        </label>
        <p className="sp-hint"><PhosphorIcon className="bi bi-link-45deg" /> Paste any link and I'll open it and write posts from the actual article — no need to summarise it yourself.</p>

        <div className="sp-controls">
          <div className="sp-tone">
            <span className="sp-label">Tone</span>
            <select value={tone} onChange={e => setTone(e.target.value)}>
              {TONES.map(t => <option key={t} value={t}>{t}</option>)}
            </select>
          </div>
          <div className="sp-platforms">
            <span className="sp-label">Platforms</span>
            <div className="sp-plat-chips">
              {PLATFORMS.map(p => (
                <button key={p.key} className={`sp-plat-chip${selected.includes(p.key) ? ' on' : ''}`} onClick={() => togglePlatform(p.key)}>
                  <PhosphorIcon className={`bi ${p.icon}`} /> {p.label}
                </button>
              ))}
            </div>
          </div>
        </div>

        <button className="sp-btn sp-btn-primary sp-generate" onClick={generate} disabled={loading}>
          {loading ? <><span className="sp-spin" /> Writing your posts…</> : <><PhosphorIcon className="bi bi-stars" /> Generate posts</>}
        </button>
      </section>

      {orderedResults.length > 0 && (
        <>
          <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 10 }}>
            <ExportButtons
              title={source.trim() || 'Social Pack'}
              filenameBase="social-pack"
              subtitle="GenPrompt Social Pack"
              getBodyHtml={() => exportRef.current?.innerHTML}
              size="sm"
            />
          </div>
          <div className="sp-results" ref={exportRef}>
            {orderedResults.map(p => {
              const text = posts[p.key]
              const over = p.limit > 0 && text.length > p.limit
              const openLabel = p.key === 'newsletter' ? 'Compose email' : p.intent ? 'Post' : `Open ${p.label}`
              return (
                <div key={p.key} className={`sp-post${p.key === 'newsletter' ? ' sp-post-wide' : ''}`}>
                  <div className="sp-post-head">
                    <span className={`sp-post-title sp-${p.key}`}><PhosphorIcon className={`bi ${p.icon}`} /> {p.label}</span>
                    <span className={`sp-count${over ? ' over' : ''}`}>{p.limit > 0 ? `${text.length}/${p.limit}` : `${text.length} chars`}</span>
                  </div>
                  <div className="sp-post-body">{text}</div>
                  <div className="sp-post-actions">
                    <button className="sp-btn sp-btn-primary" onClick={() => copy(p.key, text)}>
                      {copied === p.key ? <><PhosphorIcon className="bi bi-check-lg" /> Copied</> : <><PhosphorIcon className="bi bi-clipboard" /> Copy</>}
                    </button>
                    <button className="sp-btn" onClick={() => openPlatform(p, text)}>
                      <PhosphorIcon className="bi bi-box-arrow-up-right" /> {openLabel}
                    </button>
                  </div>
                </div>
              )
            })}
          </div>

          {/* Schedule daily */}
          <section className="sp-card sp-schedule">
            {scheduled ? (
              <div className="sp-scheduled"><PhosphorIcon className="bi bi-check-circle-fill" /> Scheduled — a fresh pack for this topic will be waiting every day. Manage it on the <a href="/jobs">Jobs</a> page.</div>
            ) : (
              <>
                <div>
                  <strong><PhosphorIcon className="bi bi-calendar-check" /> Want this every day?</strong>
                  <p className="sp-muted">Schedule a daily pack for this topic and selected platforms — it lands in your Jobs activity each morning, ready to copy.</p>
                </div>
                <div className="sp-schedule-controls">
                  <label>at
                    <select value={scheduleHour} onChange={e => setScheduleHour(Number(e.target.value))}>
                      {Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00 UTC</option>)}
                    </select>
                  </label>
                  <button className="sp-btn sp-btn-primary" onClick={scheduleDaily} disabled={scheduling}>
                    {scheduling ? 'Scheduling…' : <><PhosphorIcon className="bi bi-calendar-plus" /> Schedule daily</>}
                  </button>
                </div>
              </>
            )}
          </section>
        </>
      )}
    </div>
  )
}
