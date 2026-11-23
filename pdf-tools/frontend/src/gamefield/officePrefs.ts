// Per-page preferences for the office, kept out of the component file so fast
// refresh still works on the component itself.

const autoKey = (page: string) => `gamefield-dream:${page}`

/** Whether the dream opens by itself when this page starts working. */
export function autoOpensHere(pageKey: string): boolean {
  try { return localStorage.getItem(autoKey(pageKey)) !== 'off' } catch { return true }
}

export function setAutoOpensHere(pageKey: string, on: boolean) {
  try { localStorage.setItem(autoKey(pageKey), on ? 'on' : 'off') } catch { /* private mode */ }
}
