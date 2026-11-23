import PhosphorIcon from '../components/PhosphorIcon'
import '../components/ContentHandoff.css'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import { useEffect, useRef, useState, useMemo } from 'react'
import { useSearchParams } from 'react-router-dom'
import api from '../services/api'
import ExportButtons from '../components/ExportButtons'
import ConfirmDialog from '../components/ConfirmDialog'
import GoalPicker from '../components/GoalPicker'
import { StudyRenderer, STUDY_EXPORT_CSS, type StudyDoc } from '../components/StudyRender'
import StudyReview, { reviewableItems, dueCount, type ReviewState } from '../components/StudyReview'
import StyleThumb from '../components/StudyThumb'
import { STUDY_GROUPS, STUDY_STYLES, studyStyleById } from '../data/studyStyles'
import { elementToPng, downloadDataUri, safePngName } from '../utils/imageExport'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import { takeHandoff, type Handoff } from '../services/handoff'
import ModuleVoiceChat from '../components/ModuleVoiceChat'
import './StudyStudio.css'

interface DocListItem {
  id: number
  styleId: string
  topic?: string
  title?: string
  createdAt?: string
}

interface ReviewRow { state: ReviewState; title?: string; styleId?: string; dueAt?: string }

/** One click → a complete revision kit: read it, map it, drill it, test it. */
const PACK_STYLES = ['handwritten', 'mind-map', 'flashcards', 'quiz']

const apiErrorMessage = (error: unknown, fallback: string) => {
  const response = (error as { response?: { data?: { error?: unknown } } } | null)?.response
  return typeof response?.data?.error === 'string' ? response.data.error : fallback
}

export default function StudyStudio() {
  const { stageProps } = useFieldView('study')

  const [searchParams] = useSearchParams()
  const [mode, setMode] = useState<'topic' | 'source'>('topic')
  const [topic, setTopic] = useState('')
  const [source, setSource] = useState('')
  const [level, setLevel] = useState('university')
  const [useWeb, setUseWeb] = useState(true)
  const [intent, setIntent] = useState('')
  const [tone, setTone] = useState('professional')
  const [mapLook, setMapLook] = useState<'creative' | 'clean' | 'interactive' | ''>('')
  const [styleId, setStyleId] = useState('handwritten')
  const [generating, setGenerating] = useState(false)
  const [error, setError] = useState('')
  const [doc, setDoc] = useState<StudyDoc | null>(null)
  const [docs, setDocs] = useState<DocListItem[]>([])
  const [pack, setPack] = useState<StudyDoc[]>([])
  const [packBusy, setPackBusy] = useState<string | null>(null)
  const [pngBusy, setPngBusy] = useState(false)
  const [reviewOpen, setReviewOpen] = useState(false)
  const [reviews, setReviews] = useState<Record<number, ReviewRow>>({})
  const exportRef = useRef<HTMLDivElement>(null)

  const loadDocs = async () => {
    try { setDocs((await api.get('/study/docs')).data ?? []) } catch { /* ignore */ }
  }

  const loadReviews = async () => {
    try {
      const rows = (await api.get('/study/reviews')).data ?? []
      const map: Record<number, ReviewRow> = {}
      for (const r of rows) map[r.docId] = { state: r.state ?? { items: {} }, title: r.title, styleId: r.styleId, dueAt: r.dueAt }
      setReviews(map)
    } catch { /* ignore */ }
  }

  useEffect(() => {
    loadDocs()
    loadReviews()
    const docId = searchParams.get('doc')
    if (docId) {
      api.get(`/study/docs/${docId}`)
        .then(res => { setDoc(res.data); setStyleId(res.data.styleId) })
        .catch(() => { /* stale link — ignore */ })
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchParams.get('doc')])

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph)
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/study')
    if (handoff?.input) setTopic(handoff.input)
  }, [])

  // Content sent from another module — a digitised whiteboard, a meeting
  // transcript. It arrives as the user's OWN material, so it lands in source
  // mode: the point is to transform what they captured, not to have the AI
  // fetch a fresh essay on the same subject.
  const [inbound, setInbound] = useState<Handoff | null>(null)
  useEffect(() => {
    const h = takeHandoff()
    if (!h) return
    setMode('source')
    setSource(h.content)
    setInbound(h)
  }, [])

  const validateInput = (): boolean => {
    if (mode === 'topic' && !topic.trim()) { setError('What do you want to learn? Give a topic.'); return false }
    if (mode === 'source' && !source.trim()) { setError('Paste the notes or text you want transformed.'); return false }
    return true
  }

  const generatePayload = (sid: string) => ({
    styleId: sid,
    topic: topic.trim() || null,
    content: mode === 'source' ? source.trim() : null,
    level,
    useWebSearch: mode === 'topic' && useWeb,
    intent: intent.trim() || null,
    look: mapLook || null,
    tone,
  })

  const generate = async () => {
    if (generating || packBusy) return
    setError('')
    if (!validateInput()) return
    setGenerating(true)
    setDoc(null)
    setPack([])
    setReviewOpen(false)
    try {
      const res = await api.post('/study/generate', generatePayload(styleId))
      setDoc(res.data)
      loadDocs()
    } catch (e: unknown) {
      setError(apiErrorMessage(e, 'Generation failed — please try again.'))
    } finally {
      setGenerating(false)
    }
  }

  const generatePack = async () => {
    if (generating || packBusy) return
    setError('')
    if (!validateInput()) return
    setDoc(null)
    setPack([])
    setReviewOpen(false)
    const made: StudyDoc[] = []
    for (const sid of PACK_STYLES) {
      setPackBusy(studyStyleById(sid)?.label ?? sid)
      try {
        const res = await api.post('/study/generate', generatePayload(sid))
        made.push(res.data)
        setPack([...made])
        if (made.length === 1) setDoc(res.data)
      } catch (e: unknown) {
        setError(made.length > 0
          ? `The pack stopped after ${made.length} of ${PACK_STYLES.length} formats — ${apiErrorMessage(e, 'please try again for the rest.')}`
          : apiErrorMessage(e, 'Generation failed — please try again.'))
        break
      }
    }
    setPackBusy(null)
    loadDocs()
  }

  const openDoc = async (id: number) => {
    try {
      const res = await api.get(`/study/docs/${id}`)
      setDoc(res.data)
      setStyleId(res.data.styleId)
      setPack([])
      setReviewOpen(false)
      window.scrollTo({ top: 0, behavior: 'smooth' })
      return res.data as StudyDoc
    } catch { return null }
  }

  // Same as the analysis history: deleting a study doc is not undoable, so it
  // goes through the shared confirm dialog.
  const [deleteTarget, setDeleteTarget] = useState<DocListItem | null>(null)

  const deleteDoc = async (id: number) => {
    try { await api.delete(`/study/docs/${id}`) } catch { /* ignore */ }
    setDocs(d => d.filter(x => x.id !== id))
    if (doc?.id === id) setDoc(null)
  }

  const confirmDelete = async () => {
    const target = deleteTarget
    setDeleteTarget(null)
    if (target) await deleteDoc(target.id)
  }

  const downloadPng = async () => {
    if (!exportRef.current || !doc || pngBusy) return
    setPngBusy(true)
    try {
      const png = await elementToPng(exportRef.current, STUDY_EXPORT_CSS)
      downloadDataUri(png, safePngName(doc.title, `study-${doc.styleId}`))
    } catch {
      setError('Image export failed — try again.')
    } finally {
      setPngBusy(false)
    }
  }

  const persistReview = (docId: number, st: ReviewState) => {
    setReviews(r => ({ ...r, [docId]: { ...(r[docId] ?? {}), state: st, title: doc?.title, styleId: doc?.styleId } }))
    const dues = Object.values(st.items ?? {}).map(s => s.due).sort()
    api.put(`/study/reviews/${docId}`, { stateJson: JSON.stringify(st), dueAt: dues[0] ?? null }).catch(() => { /* offline — retried next session */ })
  }

  const stateDueCount = (st: ReviewState) =>
    Object.values(st.items ?? {}).filter(s => Date.parse(s.due) <= Date.now()).length

  const dueDocs = Object.entries(reviews)
    .map(([id, r]) => ({ id: Number(id), n: stateDueCount(r.state), title: r.title, styleId: r.styleId }))
    .filter(d => d.n > 0)

  const selectedMeta = studyStyleById(styleId)
  const reviewable = doc ? reviewableItems(doc) : []
  const reviewDue = doc && reviewable.length > 0 ? dueCount(reviewable, reviews[doc.id]?.state) : 0

  // Gamefield: a renderer desk per study piece, the tutor holding the brief.
  const field = useMemo<FieldModel>(() => {
    const made = pack.length ? pack : doc ? [doc] : []
    const desks = made.map(d => ({
      id: `doc-${d.id}`,
      name: d.title,
      role: d.styleId,
      status: (packBusy === d.styleId ? 'working' : 'done') as 'working' | 'done',
      detail: bubble(d.title),
    }))

    const subject = mode === 'topic' ? topic : source

    return {
      title: 'Study Studio',
      subtitle: subject ? bubble(subject, 70) : 'Give it a topic or paste your notes.',
      running: generating || packBusy != null,
      agents: [
        ...desks,
        {
          id: 'tutor',
          name: 'Tutor',
          role: `${level}${useWeb ? ' · web' : ''}`,
          status: (error ? 'failed' : generating ? 'working' : made.length ? 'done' : 'idle') as 'failed' | 'working' | 'done' | 'idle',
          detail: error ? bubble(error) : generating ? `Building the ${styleId} version…` : undefined,
          boss: true,
        },
      ],
      messages: desks.map(d => ({ id: `made-${d.id}`, from: 'tutor', to: d.id })),
      events: [
        ...made.map(d => ({ id: `ev-${d.id}`, text: `${d.styleId} ready: ${d.title}`, tone: 'good' as const })),
        ...(error ? [{ id: 'err', text: error, tone: 'bad' as const }] : []),
      ],
    }
  }, [doc, pack, packBusy, generating, error, styleId, level, useWeb, mode, topic, source])

  return (
    <div className="study-page">
      <ConfirmDialog
        open={!!deleteTarget}
        title="Delete study doc?"
        message={`This will permanently delete "${deleteTarget?.title || deleteTarget?.topic || 'this study doc'}".`}
        confirmLabel="Delete"
        onConfirm={confirmDelete}
        onCancel={() => setDeleteTarget(null)}
      />
      <div className="ss-main">
        <header className="ss-page-head">
          <h1><PhosphorIcon className="bi bi-mortarboard" /> Study Studio</h1>
          <p>Any topic — or your own notes — turned into 50 ways to learn it faster.</p>
        </header>

        {/* Ask instead of scanning the list — answers come from the material
            already saved here. */}
        <ModuleVoiceChat module="study" />

        <AgentStage {...stageProps} model={field} />

        {/* ── Spaced-repetition: what's due today ── */}
        {dueDocs.length > 0 && (
          <section className="ss-due-strip" aria-label="Due for review">
            <span className="ss-due-label"><PhosphorIcon className="bi bi-alarm" /> Due for review</span>
            {dueDocs.map(d => (
              <button
                key={d.id}
                type="button"
                className="ss-due-chip"
                onClick={async () => { const opened = await openDoc(d.id); if (opened) setReviewOpen(true) }}
              >
                <PhosphorIcon className={`bi ${studyStyleById(d.styleId)?.icon ?? 'bi-collection'}`} />
                {d.title || studyStyleById(d.styleId)?.label || 'Study doc'}
                <b>{d.n}</b>
              </button>
            ))}
          </section>
        )}

        {/* ── Input ── */}
        <section className="ss-input-card">
          {/* Say where the pre-filled text came from: arriving at a full
              textarea with no explanation looks like a bug. */}
          {inbound && (
            <div className="ch-inbound">
              <PhosphorIcon className="bi bi-arrow-down-circle" />
              <div className="ch-inbound-body">
                <div className="ch-inbound-title">{inbound.title}</div>
                <div className="ch-inbound-from">Brought over from {inbound.sourceLabel}</div>
              </div>
              <button
                className="ch-inbound-dismiss"
                onClick={() => { setInbound(null); setSource('') }}
                aria-label="Discard this"
              >
                <PhosphorIcon className="bi bi-x-lg" />
              </button>
            </div>
          )}

          <div className="ss-mode-tabs">
            <button type="button" className={mode === 'topic' ? 'active' : ''} onClick={() => setMode('topic')}>
              <PhosphorIcon className="bi bi-mortarboard" /> Teach me a topic
            </button>
            <button type="button" className={mode === 'source' ? 'active' : ''} onClick={() => setMode('source')}>
              <PhosphorIcon className="bi bi-clipboard-plus" /> Use my own notes
            </button>
          </div>

          <input
            className="ss-topic-input"
            value={topic}
            onChange={e => setTopic(e.target.value)}
            placeholder={mode === 'topic' ? 'e.g. Photosynthesis, World War 1 causes, Binary search trees…' : 'Optional title for this material'}
            onKeyDown={e => { if (e.key === 'Enter' && mode === 'topic') generate() }}
          />
          {mode === 'source' && (
            <textarea
              className="ss-source-input"
              value={source}
              onChange={e => setSource(e.target.value)}
              placeholder="Paste your lecture notes, a textbook passage, an article, or a research report…"
              rows={6}
            />
          )}

          <GoalPicker
            topic={topic}
            content={mode === 'source' ? source : null}
            onIntentChange={setIntent}
          />

          {selectedMeta?.archetype === 'mindmap' && (
            <div className="ss-level-row">
              <span>Map look:</span>
              {([['creative', 'Creative hand-drawn'], ['clean', 'Clean & professional'], ['interactive', 'Interactive (drag)'], ['', 'Surprise me']] as const).map(([id, label]) => (
                <button key={id || 'any'} type="button" className={`ss-level-chip${mapLook === id ? ' active' : ''}`} onClick={() => setMapLook(id)}>
                  {label}
                </button>
              ))}
            </div>
          )}

          <div className="ss-level-row">
            <span>Tone:</span>
            {[['professional', 'Professional'], ['casual', 'Casual'], ['sketch', 'Sketch'], ['playful', 'Playful'], ['academic', 'Academic']].map(([id, label]) => (
              <button key={id} type="button" className={`ss-level-chip${tone === id ? ' active' : ''}`} onClick={() => setTone(id)}>
                {label}
              </button>
            ))}
          </div>

          <div className="ss-level-row">
            <span>For:</span>
            {[['school', 'School'], ['university', 'University'], ['professional', 'Professional']].map(([id, label]) => (
              <button key={id} type="button" className={`ss-level-chip${level === id ? ' active' : ''}`} onClick={() => setLevel(id)}>
                {label}
              </button>
            ))}
            {mode === 'topic' && (
              <button
                type="button"
                className={`ss-level-chip ss-web-chip${useWeb ? ' active' : ''}`}
                onClick={() => setUseWeb(w => !w)}
                title="Ground the material with a live web search so facts and recent developments are current"
              >
                <PhosphorIcon className={`bi ${useWeb ? 'bi-globe2' : 'bi-globe'}`} /> Web search {useWeb ? 'on' : 'off'}
              </button>
            )}
          </div>

          {/* ── Style gallery ── */}
          {STUDY_GROUPS.map(group => (
            <div className="ss-style-group" key={group}>
              <span className="ss-style-group-label">{group}</span>
              <div className="ss-style-grid">
                {STUDY_STYLES.filter(s => s.group === group).map(s => (
                  <button
                    key={s.id}
                    type="button"
                    className={`ss-style-card${styleId === s.id ? ' active' : ''}`}
                    onClick={() => setStyleId(s.id)}
                    title={s.blurb}
                  >
                    <StyleThumb archetype={s.archetype} />
                    <span className="ss-style-card-name"><PhosphorIcon className={`bi ${s.icon}`} /> <strong>{s.label}</strong></span>
                    <small>{s.blurb}</small>
                  </button>
                ))}
              </div>
            </div>
          ))}

          {error && <div className="ss-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

          <div className="ss-generate-row">
            <button type="button" className="ss-generate" onClick={generate} disabled={generating || packBusy !== null}>
              {generating
                ? <><span className="ss-spin" /> Creating your {selectedMeta?.label.toLowerCase()}…</>
                : <><PhosphorIcon className="bi bi-stars" /> Create {selectedMeta?.label}</>}
            </button>
            <button
              type="button"
              className="ss-generate ss-generate-pack"
              onClick={generatePack}
              disabled={generating || packBusy !== null}
              title="Notes + mind map + flashcards + practice quiz, generated in one go"
            >
              {packBusy
                ? <><span className="ss-spin" /> {packBusy}… ({pack.length}/{PACK_STYLES.length})</>
                : <><PhosphorIcon className="bi bi-collection-fill" /> Create full study pack</>}
            </button>
          </div>
        </section>

        {/* ── Study pack tabs ── */}
        {pack.length > 0 && (
          <div className="ss-pack-tabs" role="tablist" aria-label="Study pack">
            {pack.map(p => (
              <button
                key={p.id}
                type="button"
                role="tab"
                aria-selected={doc?.id === p.id}
                className={`ss-pack-tab${doc?.id === p.id ? ' active' : ''}`}
                onClick={() => setDoc(p)}
              >
                <PhosphorIcon className={`bi ${studyStyleById(p.styleId)?.icon ?? 'bi-file-text'}`} /> {studyStyleById(p.styleId)?.label}
              </button>
            ))}
            {packBusy && <span className="ss-pack-progress"><span className="ss-spin" /> {packBusy}…</span>}
          </div>
        )}

        {/* ── Result ── */}
        {doc && (
          <section className="ss-result-card">
            <div className="ss-result-head">
              <div>
                <span className="ss-result-style"><PhosphorIcon className={`bi ${studyStyleById(doc.styleId)?.icon}`} /> {studyStyleById(doc.styleId)?.label}</span>
                <h2>{doc.title}</h2>
              </div>
              <div className="ss-result-actions">
                {reviewable.length > 0 && (
                  <button type="button" className="ss-mini-btn ss-review-btn" onClick={() => setReviewOpen(true)} title="Spaced-repetition review — grade each card and it schedules the next look">
                    <PhosphorIcon className="bi bi-arrow-repeat" /> Review{reviewDue > 0 ? ` (${reviewDue} due)` : ''}
                  </button>
                )}
                <button type="button" className="ss-mini-btn" onClick={downloadPng} disabled={pngBusy} title="Download as a PNG image">
                  {pngBusy ? <span className="ss-spin" /> : <PhosphorIcon className="bi bi-image" />} PNG
                </button>
                <ExportButtons
                  title={doc.title}
                  filenameBase={`study-${doc.styleId}`}
                  subtitle={`GenPrompt Study Studio · ${studyStyleById(doc.styleId)?.label}`}
                  getBodyHtml={() => {
                    const body = exportRef.current?.innerHTML
                    return body ? `<style>${STUDY_EXPORT_CSS}</style><div class="ss-render">${body}</div>` : null
                  }}
                  size="sm"
                />
              </div>
            </div>
            <div ref={exportRef} className="ss-render">
              <StudyRenderer key={doc.id} doc={doc} />
            </div>
          </section>
        )}

        {/* ── History ── */}
        {docs.length > 0 && (
          <section className="ss-history">
            <h3><PhosphorIcon className="bi bi-clock-history" /> Recent</h3>
            <div className="ss-history-list">
              {docs.map(d => (
                <div key={d.id} className={`ss-history-item${doc?.id === d.id ? ' active' : ''}`}>
                  <button type="button" className="ss-history-open" onClick={() => openDoc(d.id)}>
                    <PhosphorIcon className={`bi ${studyStyleById(d.styleId)?.icon ?? 'bi-file-text'}`} />
                    <span>
                      <strong>{d.title || d.topic || studyStyleById(d.styleId)?.label}</strong>
                      <small>{studyStyleById(d.styleId)?.label}</small>
                    </span>
                  </button>
                  <button type="button" className="ss-history-del" onClick={() => setDeleteTarget(d)} aria-label="Delete">
                    <PhosphorIcon className="bi bi-trash" />
                  </button>
                </div>
              ))}
            </div>
          </section>
        )}
      </div>

      {reviewOpen && doc && reviewable.length > 0 && (
        <StudyReview
          doc={doc}
          initial={reviews[doc.id]?.state}
          onPersist={st => persistReview(doc.id, st)}
          onClose={() => setReviewOpen(false)}
        />
      )}
    </div>
  )
}
