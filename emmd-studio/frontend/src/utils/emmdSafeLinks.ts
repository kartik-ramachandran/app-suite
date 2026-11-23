// A tiny rehype plugin (no external deps) that runs AFTER sanitising: it forces
// every external link to open in a new tab with rel="noopener noreferrer
// nofollow". target=_blank without noopener is a reverse-tabnabbing vector, so
// this closes it for any HTML an author embeds in a document.

interface HastNode {
  type?: string
  tagName?: string
  properties?: Record<string, unknown>
  children?: HastNode[]
}

export function hardenLinks(node: HastNode): void {
  if (node.type === 'element' && node.tagName === 'a') {
    const href = node.properties?.href
    if (typeof href === 'string' && /^https?:/i.test(href)) {
      node.properties = { ...node.properties, target: '_blank', rel: 'noopener noreferrer nofollow' }
    }
  }
  if (Array.isArray(node.children)) for (const c of node.children) hardenLinks(c)
}

export function rehypeSafeLinks() {
  return (tree: unknown) => { hardenLinks(tree as HastNode) }
}
