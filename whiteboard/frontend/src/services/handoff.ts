/**
 * Cross-module handoff.
 *
 * Several modules end by producing clean text: Whiteboard digitises a photo into
 * markdown, Book Reader extracts a book, Live Meetings produces a transcript.
 * Until now each of those dead-ended at its own screen, so the expensive part —
 * turning something messy into structured text — was used exactly once.
 *
 * This carries that output to a module that consumes it. Deliberately in
 * sessionStorage rather than a query string: the payload is a whole document,
 * and it should not survive a browser restart or leak into a shared URL.
 */

export type HandoffKind = 'markdown' | 'text' | 'recipes' | 'topics'

export interface Handoff {
  /** Where it came from, for the receiving screen to explain itself. */
  sourceModule: string
  sourceLabel: string
  kind: HandoffKind
  title: string
  content: string
  /** Set when the source row still exists, so the receiver can link back. */
  sourceId?: string | number
}

const KEY = 'gp-handoff'

export function putHandoff(h: Handoff) {
  sessionStorage.setItem(KEY, JSON.stringify(h))
}

/**
 * Reads and CLEARS the handoff: it is a one-shot delivery, not a store.
 * Leaving it in place would re-import the same whiteboard every time the user
 * opened Study Studio.
 */
export function takeHandoff(): Handoff | null {
  const raw = sessionStorage.getItem(KEY)
  if (!raw) return null
  sessionStorage.removeItem(KEY)
  try {
    return JSON.parse(raw) as Handoff
  } catch {
    return null
  }
}

/** Peek without consuming — for showing a banner before the user commits. */
export function peekHandoff(): Handoff | null {
  const raw = sessionStorage.getItem(KEY)
  if (!raw) return null
  try {
    return JSON.parse(raw) as Handoff
  } catch {
    return null
  }
}

export function clearHandoff() {
  sessionStorage.removeItem(KEY)
}

/**
 * Where a given kind of content can usefully go.
 *
 * A registry rather than buttons hard-coded into each page: adding
 * "meeting transcript -> Study Studio" should be one entry here, not a new
 * feature in two modules.
 */
export interface Destination {
  id: string
  label: string
  icon: string
  path: string
  /** Why this is worth doing, shown under the button. */
  hint: string
  accepts: HandoffKind[]
}

export const DESTINATIONS: Destination[] = [
  {
    id: 'study',
    label: 'Make study notes',
    icon: 'bi-mortarboard',
    path: '/study',
    hint: 'Turn this into revision notes and flashcards',
    accepts: ['markdown', 'text'],
  },
  {
    id: 'inkling',
    label: 'Write it up',
    icon: 'bi-pencil',
    path: '/inkling',
    hint: 'Draft a document from this',
    accepts: ['markdown', 'text'],
  },
  {
    id: 'content-studio',
    label: 'Turn into content',
    icon: 'bi-megaphone',
    path: '/content-studio',
    hint: 'A post, thread, or newsletter from this',
    accepts: ['markdown', 'text'],
  },
]

export function destinationsFor(kind: HandoffKind): Destination[] {
  return DESTINATIONS.filter(d => d.accepts.includes(kind))
}
