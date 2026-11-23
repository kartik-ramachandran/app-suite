/**
 * Handoff channel from the Cosmos graph to other modules: the AI router picks a
 * destination module, we stash the prepared input here, navigate, and the target
 * page consumes it on mount. Entries expire after 5 minutes and are single-use.
 */

const KEY = 'cosmosHandoff'
const TTL_MS = 5 * 60 * 1000

export interface CosmosHandoff {
  /** Route path of the destination page, e.g. "/research" */
  path: string
  /** Prepared input text for the destination module */
  input: string
  /** Title of the originating cosmos star */
  title?: string
  /** Id of the originating star — lets the module link its output back to it */
  sourceNodeId?: string
  /** How the brief is shaped: a timeless deep-dive or a future forecast */
  mode?: 'topic' | 'forecast'
  ts: number
}

export function setCosmosHandoff(handoff: Omit<CosmosHandoff, 'ts'>): void {
  try {
    localStorage.setItem(KEY, JSON.stringify({ ...handoff, ts: Date.now() }))
  } catch { /* storage full/blocked — handoff is best-effort */ }
}

/** Returns and clears the pending handoff if one exists for this page. */
export function consumeCosmosHandoff(path: string): CosmosHandoff | null {
  try {
    const raw = localStorage.getItem(KEY)
    if (!raw) return null
    const handoff = JSON.parse(raw) as CosmosHandoff
    if (handoff.path !== path || Date.now() - handoff.ts > TTL_MS) return null
    localStorage.removeItem(KEY)
    return handoff
  } catch {
    return null
  }
}
