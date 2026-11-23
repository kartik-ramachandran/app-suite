// Page-side helpers: the header pill that flips a page between its normal
// panels and the office floor, plus small mappers pages use to build a model.

import { useMemo } from 'react'
import type { AgentStatus, FieldEvent } from './types'

export interface FieldViewControls {
  /** Spread onto `<AgentStage {...stageProps} model={field} />`. */
  stageProps: { pageKey: string }
}

/**
 * The office is reached from a floating launcher and shown in a modal, so a
 * page needs nothing from this hook but its own slug. It stays a hook so the
 * per-page identity is declared next to the rest of the page's state.
 *
 * @param pageKey stable slug for the page, e.g. 'swarm'.
 */
export function useFieldView(pageKey: string): FieldViewControls {
  return useMemo(() => ({ stageProps: { pageKey } }), [pageKey])
}

/** Map the status vocabularies scattered across the pages onto floor statuses. */
export function toStatus(raw: string | null | undefined, running = false): AgentStatus {
  switch ((raw || '').toLowerCase()) {
    case 'running':
    case 'executing':
    case 'in_progress':
    case 'inprogress':
    case 'processing':
    case 'planning':
    case 'streaming':
      return 'working'
    case 'waiting_approval':
    case 'waitingapproval':
    case 'needs_approval':
    case 'blocked':
    case 'paused':
      return 'blocked'
    case 'done':
    case 'completed':
    case 'complete':
    case 'succeeded':
    case 'success':
    case 'finished':
      return 'done'
    case 'failed':
    case 'error':
    case 'errored':
    case 'cancelled':
    case 'canceled':
      return 'failed'
    case 'waiting':
    case 'pending':
    case 'ready':
    case 'queued':
    case 'skipped':
    case 'idle':
      return 'idle'
    default:
      return running ? 'working' : 'idle'
  }
}

/** Trim a blob of model output down to something a speech bubble can hold. */
export function bubble(text: string | null | undefined, max = 110): string | undefined {
  if (!text) return undefined
  const flat = text.replace(/\s+/g, ' ').trim()
  if (!flat) return undefined
  return flat.length > max ? `${flat.slice(0, max - 1)}…` : flat
}

/** Turn a list of lines into ticker events with stable ids. */
export function eventsFrom(lines: (string | null | undefined)[], prefix = 'e'): FieldEvent[] {
  const out: FieldEvent[] = []
  lines.forEach((line, i) => {
    if (!line) return
    const tone: FieldEvent['tone'] =
      /fail|error|could not|unable/i.test(line) ? 'bad'
      : /done|complete|finished|saved|ready/i.test(line) ? 'good'
      : /wait|approval|review/i.test(line) ? 'warn'
      : 'info'
    out.push({ id: `${prefix}-${i}`, text: line, tone })
  })
  return out
}
