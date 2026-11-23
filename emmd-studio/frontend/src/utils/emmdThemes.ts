// EMMD themes + a CSS sanitiser for the document's own <!--emmd:style--> block.
//
// Author-supplied themes (below) are trusted and written fully-qualified under
// `.emmd-doc.emmd-theme-<id>`. The per-document style block is UNTRUSTED and is
// passed through sanitizeCss(): every selector is scoped under `.emmd-doc`, and
// declarations/at-rules that could escape the document or reach the network are
// stripped. This keeps a shared EMMD file safe to open.

export interface EmmdTheme {
  id: string
  label: string
  description: string
  css: string
}

// Applied to every document regardless of theme.
export const EMMD_BASE_CSS = `
.emmd-doc { color:#0f172a; font-family: Inter, system-ui, Arial, sans-serif; line-height:1.65; font-size:15px; }
.emmd-doc :first-child { margin-top:0; }
.emmd-doc h1,.emmd-doc h2,.emmd-doc h3,.emmd-doc h4 { line-height:1.25; font-weight:700; }
.emmd-doc h1 { font-size:1.9em; margin:0 0 .5em; }
.emmd-doc h2 { font-size:1.4em; margin:1.4em 0 .5em; }
.emmd-doc h3 { font-size:1.15em; margin:1.2em 0 .4em; }
.emmd-doc p { margin:.7em 0; }
.emmd-doc ul,.emmd-doc ol { margin:.6em 0; padding-left:1.5em; }
.emmd-doc li { margin:.25em 0; }
.emmd-doc a { color:#2563eb; text-decoration:underline; }
.emmd-doc img { max-width:100%; height:auto; border-radius:8px; }
.emmd-doc hr { border:0; border-top:1px solid #e2e8f0; margin:1.6em 0; }
.emmd-doc table { width:100%; border-collapse:collapse; margin:1em 0; font-size:.95em; }
.emmd-doc th,.emmd-doc td { padding:.55em .7em; border:1px solid #e2e8f0; text-align:left; vertical-align:top; }
.emmd-doc th { background:#f1f5f9; font-weight:600; }
.emmd-doc code { padding:.12em .4em; border-radius:4px; background:rgba(15,23,42,.06); font-size:.9em; font-family:ui-monospace,Menlo,Consolas,monospace; }
.emmd-doc pre { overflow-x:auto; padding:1em; border-radius:10px; background:#0f172a; color:#e2e8f0; }
.emmd-doc pre code { background:none; padding:0; color:inherit; }
.emmd-doc blockquote { margin:1em 0; padding:.5em 1em; border-left:3px solid #cbd5e1; color:#475569; background:#f8fafc; }
`

export const EMMD_THEMES: EmmdTheme[] = [
  {
    id: 'clean',
    label: 'Clean',
    description: 'Neutral, readable defaults — good for notes and general docs.',
    css: '',
  },
  {
    id: 'proposal',
    label: 'Proposal',
    description: 'Branded gradient headings for client-facing proposals.',
    css: `
.emmd-doc.emmd-theme-proposal { color:#0f172a; }
.emmd-doc.emmd-theme-proposal h1 { padding-bottom:.3em; border-bottom:3px solid #c13584; }
.emmd-doc.emmd-theme-proposal h2 { color:#fff; padding:.4em .7em; border-radius:10px; background:linear-gradient(135deg,#0f172a 0%,#42115f 58%,#c13584 100%); }
.emmd-doc.emmd-theme-proposal h3 { color:#42115f; }
.emmd-doc.emmd-theme-proposal th { background:linear-gradient(135deg,#0f172a,#42115f); color:#fff; border-color:#42115f; }
.emmd-doc.emmd-theme-proposal blockquote { border-left-color:#c13584; background:rgba(193,53,132,.06); color:#42115f; }
.emmd-doc.emmd-theme-proposal a { color:#c13584; }
`,
  },
  {
    id: 'invoice',
    label: 'Invoice',
    description: 'Tight, monospaced numerics and ruled tables for invoices/quotes.',
    css: `
.emmd-doc.emmd-theme-invoice { font-size:14px; }
.emmd-doc.emmd-theme-invoice h1 { font-size:1.6em; color:#0f766e; }
.emmd-doc.emmd-theme-invoice h2 { font-size:1.05em; text-transform:uppercase; letter-spacing:.06em; color:#0f766e; border-bottom:2px solid #ccfbf1; padding-bottom:.2em; }
.emmd-doc.emmd-theme-invoice table { font-variant-numeric:tabular-nums; }
.emmd-doc.emmd-theme-invoice th { background:#0f766e; color:#fff; border-color:#0f766e; }
.emmd-doc.emmd-theme-invoice td:last-child,.emmd-doc.emmd-theme-invoice th:last-child { text-align:right; }
.emmd-doc.emmd-theme-invoice tr:nth-child(even) td { background:#f0fdfa; }
`,
  },
  {
    id: 'letter',
    label: 'Letter',
    description: 'Serif body with generous spacing — formal letters and memos.',
    css: `
.emmd-doc.emmd-theme-letter { font-family:Georgia,'Times New Roman',serif; font-size:16px; line-height:1.7; }
.emmd-doc.emmd-theme-letter h1 { font-size:1.7em; text-align:center; }
.emmd-doc.emmd-theme-letter h2 { font-size:1.2em; font-style:italic; color:#334155; }
.emmd-doc.emmd-theme-letter blockquote { font-style:italic; }
`,
  },
  {
    id: 'contract',
    label: 'Contract',
    description: 'Numbered-clause styling and dense tables for agreements.',
    css: `
.emmd-doc.emmd-theme-contract { font-family:Georgia,serif; font-size:14px; }
.emmd-doc.emmd-theme-contract h1 { text-align:center; text-transform:uppercase; letter-spacing:.05em; font-size:1.5em; }
.emmd-doc.emmd-theme-contract h2 { font-size:1.05em; text-transform:uppercase; letter-spacing:.03em; border-bottom:1px solid #cbd5e1; padding-bottom:.2em; }
.emmd-doc.emmd-theme-contract ol { counter-reset:none; }
.emmd-doc.emmd-theme-contract p { text-align:justify; }
`,
  },
  {
    id: 'technical',
    label: 'Technical',
    description: 'Monospace accents and strong code blocks for specs/READMEs.',
    css: `
.emmd-doc.emmd-theme-technical { font-size:15px; }
.emmd-doc.emmd-theme-technical h1,.emmd-doc.emmd-theme-technical h2,.emmd-doc.emmd-theme-technical h3 { font-family:ui-monospace,Menlo,Consolas,monospace; }
.emmd-doc.emmd-theme-technical h2 { border-left:4px solid #2563eb; padding-left:.5em; }
.emmd-doc.emmd-theme-technical code { background:rgba(37,99,235,.1); color:#1d4ed8; }
.emmd-doc.emmd-theme-technical th { background:#1e293b; color:#fff; border-color:#1e293b; }
`,
  },
  {
    id: 'dark',
    label: 'Dark',
    description: 'Light-on-dark surface for on-screen reading.',
    css: `
.emmd-doc.emmd-theme-dark { color:#e2e8f0; background:#0f172a; }
.emmd-doc.emmd-theme-dark h1,.emmd-doc.emmd-theme-dark h2,.emmd-doc.emmd-theme-dark h3,.emmd-doc.emmd-theme-dark h4 { color:#f8fafc; }
.emmd-doc.emmd-theme-dark a { color:#7dd3fc; }
.emmd-doc.emmd-theme-dark th,.emmd-doc.emmd-theme-dark td { border-color:#334155; }
.emmd-doc.emmd-theme-dark th { background:#1e293b; }
.emmd-doc.emmd-theme-dark code { background:rgba(148,163,184,.16); color:#e2e8f0; }
.emmd-doc.emmd-theme-dark blockquote { border-left-color:#475569; background:#1e293b; color:#cbd5e1; }
.emmd-doc.emmd-theme-dark hr { border-top-color:#334155; }
`,
  },
]

export const DEFAULT_THEME = 'clean'

export function getTheme(id: string | undefined): EmmdTheme {
  return EMMD_THEMES.find(t => t.id === id) || EMMD_THEMES[0]
}

/** Full CSS for a rendered EMMD document: base + all themes + the sanitised
 *  per-document style block. Shared by the live renderer and PDF/HTML export. */
export function buildEmmdCss(styleCss: string): string {
  return `${EMMD_BASE_CSS}\n${allThemeCss()}\n${sanitizeCss(styleCss)}`
}

/** All author theme CSS concatenated — every theme is present, activated by the
 *  document's `emmd-theme-<id>` class, so switching themes needs no re-inject. */
export function allThemeCss(): string {
  return EMMD_THEMES.map(t => t.css).join('\n')
}

// ── CSS sanitiser for the untrusted per-document style block ─────────────────

const FORBIDDEN_VALUE = /(expression\s*\(|javascript\s*:|behaviou?r\s*:|-moz-binding|@import|@charset|<\/?\s*style)/i

function stripComments(css: string): string {
  return css.replace(/\/\*[\s\S]*?\*\//g, '')
}

/** url(...) is only allowed when it is a data: URI (self-contained, no network). */
function urlIsSafe(value: string): boolean {
  const urls = value.match(/url\(\s*([^)]*)\)/gi)
  if (!urls) return true
  return urls.every(u => /url\(\s*['"]?\s*data:/i.test(u))
}

function sanitizeDeclarations(body: string): string {
  const out: string[] = []
  for (const decl of body.split(';')) {
    const d = decl.trim()
    if (!d) continue
    const colon = d.indexOf(':')
    if (colon < 0) continue
    const prop = d.slice(0, colon).trim().toLowerCase()
    const value = d.slice(colon + 1).trim()
    if (FORBIDDEN_VALUE.test(d) || !urlIsSafe(value)) continue
    // Block viewport-escaping positioning from untrusted CSS.
    if (prop === 'position' && /\b(fixed|sticky)\b/i.test(value)) continue
    if (/^-?[a-z][a-z-]*$/i.test(prop)) out.push(`${prop}: ${value}`)
  }
  return out.join('; ')
}

function scopeSelectorList(selectors: string, scope: string): string {
  return selectors
    .split(',')
    .map(sel => {
      const s = sel.trim()
      if (!s) return ''
      // Selectors that could break out of the document are dropped.
      if (/[<>{}]|javascript:/i.test(s)) return ''
      if (/^(:root|html|body)\b/i.test(s)) return s.replace(/^(:root|html|body)/i, scope)
      return `${scope} ${s}`
    })
    .filter(Boolean)
    .join(', ')
}

interface CssBlock { prelude: string; body: string; statement?: boolean }

function splitTopLevel(css: string): CssBlock[] {
  const blocks: CssBlock[] = []
  let prelude = ''
  for (let i = 0; i < css.length; i++) {
    const c = css[i]
    if (c === '{') {
      let depth = 1
      let j = i + 1
      let body = ''
      while (j < css.length && depth > 0) {
        if (css[j] === '{') depth++
        else if (css[j] === '}') { depth--; if (depth === 0) break }
        body += css[j]
        j++
      }
      blocks.push({ prelude: prelude.trim(), body })
      prelude = ''
      i = j
    } else if (c === ';') {
      // A top-level, block-less statement (e.g. @import/@charset). Capture it
      // separately so it doesn't swallow the selector of the following rule.
      const stmt = prelude.trim()
      if (stmt) blocks.push({ prelude: stmt, body: '', statement: true })
      prelude = ''
    } else {
      prelude += c
    }
  }
  return blocks
}

/** Scope and clean an untrusted stylesheet so it can only affect `.emmd-doc`. */
export function sanitizeCss(input: string, scope = '.emmd-doc'): string {
  if (!input || !input.trim()) return ''
  const css = stripComments(input)
  const out: string[] = []

  for (const { prelude, body, statement } of splitTopLevel(css)) {
    if (!prelude) continue
    // Top-level block-less statements (@import / @charset / @namespace / junk)
    // are always dropped — none are needed and @import would fetch remotely.
    if (statement) continue

    if (prelude.startsWith('@')) {
      const at = (/^@([a-z-]+)/i.exec(prelude)?.[1] || '').toLowerCase()
      if (at === 'media' || at === 'supports' || at === 'container' || at === 'document') {
        // Nested rules — scope each inner rule, keep the at-rule prelude.
        const inner = splitTopLevel(body)
          .map(b => b.prelude ? `${scopeSelectorList(b.prelude, scope)} { ${sanitizeDeclarations(b.body)} }` : '')
          .filter(Boolean)
          .join('\n')
        if (inner) out.push(`${prelude} {\n${inner}\n}`)
      } else if (at === 'keyframes' || at === '-webkit-keyframes') {
        const inner = splitTopLevel(body)
          .map(b => b.prelude ? `${b.prelude} { ${sanitizeDeclarations(b.body)} }` : '')
          .filter(Boolean)
          .join(' ')
        if (inner) out.push(`${prelude} { ${inner} }`)
      } else if (at === 'font-face' || at === 'page') {
        const decls = sanitizeDeclarations(body)
        if (decls) out.push(`${prelude} { ${decls} }`)
      }
      // @import / @charset / @namespace and anything unknown are dropped.
      continue
    }

    const sel = scopeSelectorList(prelude, scope)
    const decls = sanitizeDeclarations(body)
    if (sel && decls) out.push(`${sel} { ${decls} }`)
  }

  return out.join('\n')
}
