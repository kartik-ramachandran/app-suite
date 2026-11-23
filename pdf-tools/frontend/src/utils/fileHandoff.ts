/**
 * Hands a just-converted file from the free PDF tools to another module.
 *
 * The moment after a conversion is the one point where an anonymous visitor
 * holds a file they care about and is looking at buttons. These helpers let
 * "Summarise with AI" and "Track as expense" carry the ACTUAL file across
 * navigation instead of asking the user to download it and re-upload it on the
 * next screen — that re-upload is where the intent dies.
 *
 * The file is held in module memory, not sessionStorage: blobs do not survive
 * JSON serialisation, and base64-encoding a 10MB PDF into storage would cost
 * more than it gives. The trade-off is that the hand-off only survives a
 * client-side navigation (Link), not a full reload — which is exactly the path
 * the buttons take.
 */

export interface HandoffFile {
  blob: Blob
  name: string
  /** Where the file came from, e.g. "merge" — used for analytics naming only. */
  sourceTool: string
  /** What the receiving page should do with it. */
  intent: 'chat-summarise' | 'chat-tables' | 'expense' | 'keep'
  /** Pre-filled message for chat intents. */
  prompt?: string
}

let pending: HandoffFile | null = null

export function setHandoff(file: HandoffFile): void {
  pending = file
}

/**
 * Claims the pending file. Single-shot by design: consuming clears it, so a
 * revisit to the page cannot silently re-attach a stale document.
 */
export function takeHandoff(intent?: HandoffFile['intent']): HandoffFile | null {
  if (!pending) return null
  if (intent && pending.intent !== intent) return null
  const f = pending
  pending = null
  return f
}

/** Non-destructive peek, for pages that need to know before mount effects run. */
export function hasHandoff(intent?: HandoffFile['intent']): boolean {
  return pending !== null && (!intent || pending.intent === intent)
}
