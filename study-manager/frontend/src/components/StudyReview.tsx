import PhosphorIcon from './PhosphorIcon'
import { useMemo, useState } from 'react'
import { studyStyleById } from '../data/studyStyles'
import type { StudyDoc } from './StudyRender'

// Spaced-repetition review for flashcard and quiz docs — a lightweight SM-2:
// grade a card Again/Hard/Good/Easy and it schedules the next look. State is
// persisted per (user, doc) by the parent via onPersist.

export interface ReviewItemState { e: number; i: number; r: number; due: string }
export interface ReviewState { items: Record<string, ReviewItemState> }

export interface ReviewItem { key: string; front: string; options?: string[]; back: string }

const S = (v: unknown): string => (typeof v === 'string' ? v : v == null ? '' : String(v))

/** Extract the reviewable Q/A pairs from a doc (flashcards + all worksheet styles). */
export const reviewableItems = (doc: StudyDoc): ReviewItem[] => {
  const arch = studyStyleById(doc.styleId)?.archetype
  const data = doc.data ?? {}
  if (arch === 'cards') {
    return (Array.isArray(data.cards) ? data.cards : [])
      .map((c: any, i: number) => ({ key: String(i), front: S(c?.front), back: S(c?.back) }))
      .filter((c: ReviewItem) => c.front && c.back)
  }
  if (arch === 'worksheet') {
    return (Array.isArray(data.questions) ? data.questions : [])
      .map((q: any, i: number) => ({
        key: String(i),
        front: S(q?.q),
        options: Array.isArray(q?.options) ? q.options.map(S).filter(Boolean) : undefined,
        back: S(q?.answer) + (q?.explain ? ` — ${S(q.explain)}` : ''),
      }))
      .filter((c: ReviewItem) => c.front && c.back)
  }
  return []
}

export const dueCount = (items: ReviewItem[], state: ReviewState | undefined): number => {
  const now = Date.now()
  return items.filter(it => {
    const s = state?.items?.[it.key]
    return !s || Date.parse(s.due) <= now
  }).length
}

type Grade = 0 | 3 | 4 | 5

const gradeItem = (prev: ReviewItemState | undefined, g: Grade): ReviewItemState => {
  let { e, i, r } = prev ?? { e: 2.5, i: 0, r: 0 }
  if (g === 0) {
    r = 0; i = 0
    e = Math.max(1.3, e - 0.2)
  } else {
    e = Math.max(1.3, e + (0.1 - (5 - g) * (0.08 + (5 - g) * 0.02)))
    r += 1
    i = r === 1 ? 1 : r === 2 ? 3 : Math.max(1, Math.round(i * e))
    if (g === 3) i = Math.max(1, Math.round(i * 0.8))
  }
  const due = new Date(Date.now() + (g === 0 ? 10 * 60_000 : i * 86_400_000)).toISOString()
  return { e, i, r, due }
}

const intervalLabel = (prev: ReviewItemState | undefined, g: Grade): string => {
  if (g === 0) return '10 min'
  const next = gradeItem(prev, g)
  return next.i === 1 ? '1 day' : `${next.i} days`
}

interface Props {
  doc: StudyDoc
  initial: ReviewState | undefined
  onPersist: (state: ReviewState) => void
  onClose: () => void
}

export default function StudyReview({ doc, initial, onPersist, onClose }: Props) {
  const items = useMemo(() => reviewableItems(doc), [doc])
  const [state, setState] = useState<ReviewState>({ items: { ...(initial?.items ?? {}) } })
  const [queue, setQueue] = useState<string[]>(() => {
    const now = Date.now()
    return items
      .filter(it => {
        const s = initial?.items?.[it.key]
        return !s || Date.parse(s.due) <= now
      })
      .map(it => it.key)
  })
  const [revealed, setRevealed] = useState(false)
  const [done, setDone] = useState(0)

  const current = items.find(it => it.key === queue[0])

  const grade = (g: Grade) => {
    if (!current) return
    const next = gradeItem(state.items[current.key], g)
    const newState: ReviewState = { items: { ...state.items, [current.key]: next } }
    setState(newState)
    onPersist(newState)
    setRevealed(false)
    setQueue(q => {
      const rest = q.slice(1)
      // "Again" comes back around at the end of this session.
      return g === 0 ? [...rest, current.key] : rest
    })
    if (g !== 0) setDone(d => d + 1)
  }

  const nextDueText = () => {
    const dues = items
      .map(it => state.items[it.key]?.due)
      .filter(Boolean)
      .map(d => Date.parse(d as string))
      .filter(t => t > Date.now())
    if (!dues.length) return null
    const days = Math.max(1, Math.round((Math.min(...dues) - Date.now()) / 86_400_000))
    return days === 1 ? 'tomorrow' : `in ${days} days`
  }

  return (
    <div className="ss-review-overlay" role="dialog" aria-modal="true" aria-label="Spaced-repetition review">
      <div className="ss-review">
        <div className="ss-review-head">
          <div>
            <span className="ss-review-eyebrow"><PhosphorIcon className="bi bi-arrow-repeat" /> Spaced review</span>
            <strong>{doc.title}</strong>
          </div>
          <button type="button" className="vz-close" onClick={onClose} aria-label="Close review">
            <PhosphorIcon className="bi bi-x-lg" />
          </button>
        </div>

        {current ? (
          <>
            <div className="ss-review-progress">
              <span>{queue.length} to go</span>
              <span>{done} done</span>
            </div>
            <div className="ss-review-card">
              <div className="ss-review-q">{current.front}</div>
              {current.options && current.options.length > 0 && (
                <ul className="ss-review-opts">
                  {current.options.map((o, i) => <li key={i}>{String.fromCharCode(65 + i)}. {o}</li>)}
                </ul>
              )}
              {revealed && <div className="ss-review-a"><b>Answer</b>{current.back}</div>}
            </div>
            {!revealed ? (
              <button type="button" className="ss-generate ss-review-reveal" onClick={() => setRevealed(true)}>
                <PhosphorIcon className="bi bi-eye" /> Reveal answer
              </button>
            ) : (
              <div className="ss-review-grades">
                <button type="button" className="ss-grade ss-grade-again" onClick={() => grade(0)}>
                  Again<small>{intervalLabel(state.items[current.key], 0)}</small>
                </button>
                <button type="button" className="ss-grade ss-grade-hard" onClick={() => grade(3)}>
                  Hard<small>{intervalLabel(state.items[current.key], 3)}</small>
                </button>
                <button type="button" className="ss-grade ss-grade-good" onClick={() => grade(4)}>
                  Good<small>{intervalLabel(state.items[current.key], 4)}</small>
                </button>
                <button type="button" className="ss-grade ss-grade-easy" onClick={() => grade(5)}>
                  Easy<small>{intervalLabel(state.items[current.key], 5)}</small>
                </button>
              </div>
            )}
          </>
        ) : (
          <div className="ss-review-done">
            <PhosphorIcon className="bi bi-patch-check" />
            <strong>All caught up!</strong>
            <p>
              {done > 0 ? `You reviewed ${done} card${done === 1 ? '' : 's'}.` : 'Nothing is due right now.'}
              {nextDueText() ? ` Next review ${nextDueText()}.` : ''}
            </p>
            <button type="button" className="ss-generate" onClick={onClose}>Done</button>
          </div>
        )}
      </div>
    </div>
  )
}
