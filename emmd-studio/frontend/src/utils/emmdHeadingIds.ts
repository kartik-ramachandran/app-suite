// A tiny rehype plugin (no external deps) that gives every heading a stable,
// slugified id so in-document links — a Table of Contents — actually work.
// Runs AFTER rehype-sanitize, so the ids it assigns are not clobber-prefixed.
// The same ids end up in the exported HTML/PDF twin (the export captures the
// rendered DOM), so TOC links keep working outside the app too.

interface HastNode {
  type?: string
  tagName?: string
  properties?: Record<string, unknown>
  children?: HastNode[]
  value?: string
}

const HEADINGS = new Set(['h1', 'h2', 'h3', 'h4', 'h5', 'h6'])

/** "Objectives and success metrics" → "objectives-and-success-metrics" */
export function slugifyHeading(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
}

function textOf(node: HastNode): string {
  if (node.type === 'text') return node.value ?? ''
  return (node.children ?? []).map(textOf).join('')
}

export function addHeadingIds(node: HastNode, used = new Map<string, number>()): void {
  if (node.type === 'element' && node.tagName && HEADINGS.has(node.tagName)) {
    if (!node.properties?.id) {
      const base = slugifyHeading(textOf(node)) || 'section'
      const count = used.get(base) ?? 0
      used.set(base, count + 1)
      node.properties = { ...node.properties, id: count ? `${base}-${count}` : base }
    }
  }
  if (Array.isArray(node.children)) for (const c of node.children) addHeadingIds(c, used)
}

export function rehypeHeadingIds() {
  return (tree: unknown) => { addHeadingIds(tree as HastNode) }
}
