// Tier-3 floor for the snap tools. Those pages are a single round trip, but it
// is a real three-stage one — compress the photo, call the vision model, file
// the result — so the floor shows that crew rather than one avatar blinking.

import { useEffect, useMemo, useRef, useState } from 'react'
import type { FieldEvent, FieldMessage, FieldModel } from './types'

export interface SnapFieldInput {
  /** Wall sign, e.g. 'Plant Doctor'. */
  title: string
  /** True while a snap is in flight. */
  busy: boolean
  /** Set when the last snap failed. */
  error?: string
  /** How many things the collection holds, shown on the filing clerk's desk. */
  count?: number
  /** Plural noun for `count`, e.g. 'plants'. */
  noun?: string
  /** Optional extra line under the title. */
  subtitle?: string
  /**
   * Rename the three desks when 'Intake / Analyst / Filing' would misdescribe
   * the work, e.g. a travel guide is researched rather than scanned.
   */
  stages?: [StageLabel, StageLabel, StageLabel]
}

export interface StageLabel {
  name: string
  role: string
}

const STAGES = [
  { id: 'intake', name: 'Intake', role: 'compress & upload', ms: 900 },
  { id: 'vision', name: 'Analyst', role: 'vision model', ms: 9000 },
  { id: 'filing', name: 'Filing', role: 'saves to your shelf', ms: 700 },
] as const

/**
 * Builds a live model for a snap tool. Stage timings are nominal — the analyst
 * simply stays at the desk until `busy` clears, so a slow model reads as a slow
 * analyst rather than a desk that finishes early.
 */
export function useSnapFieldModel(input: SnapFieldInput): FieldModel {
  const { title, busy, error, count, noun = 'items', subtitle, stages } = input
  const [tick, setTick] = useState(0)
  const startedRef = useRef<number | null>(null)
  const runRef = useRef(0)
  const [log, setLog] = useState<FieldEvent[]>([])
  const [messages, setMessages] = useState<FieldMessage[]>([])

  // Drive the stage clock only while something is actually happening.
  useEffect(() => {
    if (!busy) return
    startedRef.current = Date.now()
    runRef.current += 1
    const run = runRef.current
    setLog(l => [...l.slice(-20), { id: `s${run}-start`, text: 'New job in at the front desk.', tone: 'info' }])
    setMessages([{ id: `m${run}-in`, from: 'user', to: 'intake' }])
    const t = window.setInterval(() => setTick(n => n + 1), 200)
    return () => window.clearInterval(t)
  }, [busy])

  useEffect(() => {
    if (busy) return
    const run = runRef.current
    if (!startedRef.current) return
    startedRef.current = null
    if (error) {
      setLog(l => [...l.slice(-20), { id: `s${run}-err`, text: error, tone: 'bad' }])
    } else {
      setLog(l => [...l.slice(-20), { id: `s${run}-ok`, text: 'Filed. Your records are up to date.', tone: 'good' }])
      setMessages(m => [...m, { id: `m${run}-file`, from: 'vision', to: 'filing' }])
    }
  }, [busy, error])

  return useMemo<FieldModel>(() => {
    const elapsed = startedRef.current ? Date.now() - startedRef.current : 0
    const intakeDone = elapsed > STAGES[0].ms

    const label = (i: number) => stages?.[i] ?? { name: STAGES[i].name, role: STAGES[i].role }

    const agents = STAGES.map((s, si) => {
      const { name, role: roleLabel } = label(si)
      if (!busy) {
        return {
          id: s.id,
          name,
          role: s.id === 'filing' && typeof count === 'number' ? `${count} ${noun} on file` : roleLabel,
          status: (error && s.id === 'vision' ? 'failed' : 'idle') as 'failed' | 'idle',
          detail: error && s.id === 'vision' ? error : undefined,
          boss: s.id === 'vision',
        }
      }
      if (s.id === 'intake') {
        return {
          id: s.id,
          name,
          role: roleLabel,
          status: (intakeDone ? 'done' : 'working') as 'done' | 'working',
          detail: intakeDone ? undefined : 'Getting it ready…',
          progress: Math.min(1, elapsed / STAGES[0].ms),
        }
      }
      if (s.id === 'vision') {
        return {
          id: s.id,
          name,
          role: roleLabel,
          status: (intakeDone ? 'working' : 'idle') as 'working' | 'idle',
          detail: intakeDone ? 'Working on it…' : undefined,
          boss: true,
        }
      }
      return {
        id: s.id,
        name,
        role: typeof count === 'number' ? `${count} ${noun} on file` : roleLabel,
        status: 'idle' as const,
      }
    })

    return {
      title,
      subtitle,
      running: busy,
      agents,
      messages: intakeDone && busy
        ? [...messages, { id: `m${runRef.current}-hand`, from: 'intake', to: 'vision' }]
        : messages,
      events: log,
    }
    // `tick` is the clock that re-derives elapsed time while busy.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [title, subtitle, busy, error, count, noun, stages, log, messages, tick])
}
