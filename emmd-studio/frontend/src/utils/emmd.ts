// Enhanced MMD (EMMD) — a backward-compatible Markdown superset.
//
// An .emmd file is plain Markdown plus three OPTIONAL, comment-based layers, so
// it still renders as ordinary Markdown in any reader (VS Code, GitHub, Obsidian):
//
//   1. YAML frontmatter        --- title / theme / author … ---
//   2. A scoped style block     <!--emmd:style  h1 { color:#123 }  -->
//   3. An integrity seal        <!--emmd:seal  {"token":"GPSEAL1…"}  -->
//
// This module only parses/serialises the format. Rendering (theme + sanitised
// HTML) lives in EmmdRenderer; sealing/verification is done by the backend,
// which holds the signing key.

export const EMMD_VERSION = '1.0'
export const EMMD_FILE_EXT = '.emmd'

export interface EmmdFrontmatter {
  emmd?: string
  title?: string
  subtitle?: string
  author?: string
  theme?: string
  created?: string
  [key: string]: string | undefined
}

export interface EmmdSealInfo {
  token: string
  sealedBy?: string
  sealedAt?: string
}

export interface ParsedEmmd {
  frontmatter: EmmdFrontmatter
  /** Raw CSS from the emmd:style block ('' if none). */
  styleCss: string
  /** Markdown body with frontmatter, style block and seal removed. */
  body: string
  seal: EmmdSealInfo | null
  /** The document source with ONLY the seal comment removed — the exact bytes
   *  that are hashed and sealed. Keep this verbatim for verification. */
  contentWithoutSeal: string
}

const SEAL_OPEN = '<!--emmd:seal'
const STYLE_OPEN = '<!--emmd:style'
const HTML_CLOSE = '-->'

function normalizeEol(s: string): string {
  return s.replace(/\r\n/g, '\n').replace(/\r/g, '\n')
}

// ── Seal surgery (pure string ops, must mirror the backend) ─────────────────

/** Split a document into its content (seal removed) and the seal, if present. */
export function stripSeal(raw: string): { contentWithoutSeal: string; seal: EmmdSealInfo | null } {
  const s = normalizeEol(raw)
  const open = s.lastIndexOf(SEAL_OPEN)
  if (open < 0) return { contentWithoutSeal: s.replace(/\s+$/, ''), seal: null }
  const close = s.indexOf(HTML_CLOSE, open)
  if (close < 0) return { contentWithoutSeal: s.replace(/\s+$/, ''), seal: null }

  // Only treat it as a trailing seal if nothing but whitespace follows it.
  const after = s.slice(close + HTML_CLOSE.length)
  if (after.trim().length !== 0) return { contentWithoutSeal: s.replace(/\s+$/, ''), seal: null }

  const json = s.slice(open + SEAL_OPEN.length, close).trim()
  let seal: EmmdSealInfo | null = null
  try {
    const obj = JSON.parse(json)
    if (obj && typeof obj.token === 'string') {
      seal = { token: obj.token, sealedBy: obj.sealedBy, sealedAt: obj.sealedAt }
    }
  } catch {
    seal = null
  }
  const content = s.slice(0, open).replace(/\s+$/, '')
  return { contentWithoutSeal: content, seal }
}

/** Render a seal comment to append to a document for export. */
export function sealComment(seal: EmmdSealInfo): string {
  return `${SEAL_OPEN} ${JSON.stringify(seal)} ${HTML_CLOSE}`
}

// ── Frontmatter (a small, safe YAML subset: key: value) ─────────────────────

function parseFrontmatter(block: string): EmmdFrontmatter {
  const fm: EmmdFrontmatter = {}
  for (const line of block.split('\n')) {
    const trimmed = line.trim()
    if (!trimmed || trimmed.startsWith('#')) continue
    const idx = trimmed.indexOf(':')
    if (idx < 0) continue
    const key = trimmed.slice(0, idx).trim()
    let value = trimmed.slice(idx + 1).trim()
    if ((value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'"))) {
      value = value.slice(1, -1).replace(/\\"/g, '"')
    }
    if (key) fm[key] = value
  }
  return fm
}

function serializeFrontmatter(fm: EmmdFrontmatter): string {
  const order = ['emmd', 'title', 'subtitle', 'author', 'theme', 'created']
  const keys = [...order.filter(k => fm[k] != null && fm[k] !== ''),
    ...Object.keys(fm).filter(k => !order.includes(k) && fm[k] != null && fm[k] !== '')]
  const lines = keys.map(k => {
    const raw = String(fm[k])
    // Quote when the value could be misread as YAML structure.
    const needsQuote = /[:#]|^\s|\s$|^['"]|^\d{4}-\d{2}-\d{2}/.test(raw)
    const value = needsQuote ? `"${raw.replace(/"/g, '\\"')}"` : raw
    return `${k}: ${value}`
  })
  return lines.join('\n')
}

// ── Style block ─────────────────────────────────────────────────────────────

function extractStyle(content: string): { styleCss: string; rest: string } {
  const open = content.indexOf(STYLE_OPEN)
  if (open < 0) return { styleCss: '', rest: content }
  const close = content.indexOf(HTML_CLOSE, open)
  if (close < 0) return { styleCss: '', rest: content }
  const css = content.slice(open + STYLE_OPEN.length, close).trim()
  const rest = (content.slice(0, open) + content.slice(close + HTML_CLOSE.length)).trim()
  return { styleCss: css, rest }
}

// ── Parse / build ───────────────────────────────────────────────────────────

export function parseEmmd(raw: string): ParsedEmmd {
  const { contentWithoutSeal, seal } = stripSeal(raw)

  let frontmatter: EmmdFrontmatter = {}
  let afterFm = contentWithoutSeal
  const fmMatch = /^---\n([\s\S]*?)\n---\s*\n?/.exec(contentWithoutSeal)
  if (fmMatch) {
    frontmatter = parseFrontmatter(fmMatch[1])
    afterFm = contentWithoutSeal.slice(fmMatch[0].length)
  }

  const { styleCss, rest } = extractStyle(afterFm)

  return {
    frontmatter,
    styleCss,
    body: rest.replace(/^\n+/, ''),
    seal,
    contentWithoutSeal,
  }
}

/** Build the stored document source (frontmatter + style + body), WITHOUT a seal. */
export function buildContent(frontmatter: EmmdFrontmatter, styleCss: string, body: string): string {
  const fm: EmmdFrontmatter = { emmd: EMMD_VERSION, ...frontmatter }
  const parts: string[] = []
  parts.push(`---\n${serializeFrontmatter(fm)}\n---`)
  if (styleCss.trim()) parts.push(`${STYLE_OPEN}\n${styleCss.trim()}\n${HTML_CLOSE}`)
  parts.push(body.trim())
  return parts.join('\n\n') + '\n'
}

/** Build a full .emmd file, appending the seal comment when the document is sealed. */
export function buildEmmdFile(content: string, seal: EmmdSealInfo | null): string {
  const base = normalizeEol(content).replace(/\s+$/, '')
  if (!seal) return base + '\n'
  return `${base}\n\n${sealComment(seal)}\n`
}

/** Heuristic: does this text look like an EMMD document (vs. plain markdown)? */
export function isEmmd(raw: string): boolean {
  return /^---\n[\s\S]*?\bemmd\s*:/.test(normalizeEol(raw)) ||
    raw.includes(STYLE_OPEN) || raw.includes(SEAL_OPEN)
}
