import PhosphorIcon from './PhosphorIcon'
import { useEffect, useState } from 'react'

// Mermaid is heavy (~0.5 MB gz) — load it only when a diagram is rendered.
// CRITICAL: a failed import must NOT stay cached (a chunk 404 after a rolling
// deploy would otherwise poison every diagram until a full page reload).
let mermaidPromise: Promise<typeof import('mermaid')['default']> | null = null
const loadMermaid = () => {
  mermaidPromise ??= import('mermaid')
    .then(m => {
      m.default.initialize({
        startOnLoad: false,
        securityLevel: 'strict',
        theme: 'base',
        themeVariables: {
          primaryColor: '#fff5fb',
          primaryBorderColor: '#d62976',
          primaryTextColor: '#0f172a',
          lineColor: '#833ab4',
          fontFamily: 'Inter, -apple-system, sans-serif',
          fontSize: '14px',
        },
      })
      return m.default
    })
    .catch(err => {
      mermaidPromise = null   // allow the next attempt to re-import
      throw err
    })
  return mermaidPromise
}

// Mindmap/flowchart parsers choke on punctuation inside node labels — colons,
// commas, quotes. Strip those for a retry attempt, replacing with spaces so
// LEADING indentation (which defines mindmap hierarchy) is intact. Other
// diagram types (sequence, journey, gantt, class, er…) NEED colons as syntax,
// so they are returned unchanged — a retry can't help them.
const sanitizeMermaid = (code: string): string => {
  const lines = code.split('\n')
  const kind = (lines[0] ?? '').trim().split(/\s/)[0]
  if (kind !== 'mindmap' && kind !== 'flowchart' && kind !== 'graph') return code
  return lines
    .map((line, i) => {
      if (i === 0) return line.trim()
      // Preserve structural keywords untouched.
      if (/^\s*(subgraph\b|end\s*$)/.test(line) && kind !== 'mindmap') {
        return line.replace(/[:;,"'`#&]/g, ' ')
      }
      const indent = line.match(/^\s*/)?.[0] ?? ''
      let text = line.slice(indent.length)
      // Keep structural tokens: shape brackets/parens, arrows, edge-label pipes.
      text = text.replace(/[:;,"'`#&]/g, ' ')
      return indent + text
    })
    .join('\n')
}

// Render a mindmap outline as an indented list when all else fails —
// never show raw mermaid syntax to the user.
const outlineFromMindmap = (code: string): { text: string; depth: number }[] | null => {
  const lines = code.split('\n').map(l => l.replace(/\r$/, ''))
  if (!/^\s*mindmap/.test(lines[0] ?? '')) return null
  const items: { text: string; depth: number }[] = []
  for (const line of lines.slice(1)) {
    if (!line.trim()) continue
    const indent = (line.match(/^\s*/)?.[0] ?? '').length
    const text = line.trim()
      .replace(/^root\s*/, '')
      .replace(/^\(\((.*)\)\)$/, '$1')
      .replace(/^\[(.*)\]$/, '$1')
      .replace(/^\((.*)\)$/, '$1')
    if (text) items.push({ text, depth: Math.max(0, Math.round((indent - 2) / 2)) })
  }
  return items.length ? items : null
}

let renderSeq = 0

// Flowchart fallback: turn "A[Label] -->|No| B{Question}" lines into readable
// steps — raw mermaid syntax must never be what the user reads.
const outlineFromFlowchart = (code: string): string[] | null => {
  const lines = code.split('\n').map(l => l.trim()).filter(Boolean)
  if (!/^(flowchart|graph)\b/.test(lines[0] ?? '')) return null
  const clean = (part: string) => part
    .replace(/[A-Za-z0-9_]+\[([^\]]*)\]/g, '$1')
    .replace(/[A-Za-z0-9_]+\{([^}]*)\}/g, '$1?')
    .replace(/[A-Za-z0-9_]+\(\(([^)]*)\)\)/g, '$1')
    .replace(/[A-Za-z0-9_]+\(([^)]*)\)/g, '$1')
    .trim()
  const steps: string[] = []
  for (const line of lines.slice(1)) {
    if (/^(subgraph|end$|classDef|style|click)/.test(line)) continue
    const withLabels = line.replace(/--?>\|([^|]*)\|/g, ' → ($1) ').replace(/--?>/g, ' → ')
    const readable = withLabels.split(/\s*→\s*/).map(clean).filter(Boolean).join(' → ')
    if (readable) steps.push(readable)
  }
  return steps.length ? steps : null
}

/** Renders mermaid code to inline SVG; sanitizes + retries on parse errors,
 *  auto-retries once (covers transient chunk-load failures after deploys), and
 *  offers a manual "Try drawing again" on the fallback.
 *  `sketch` draws with mermaid v11's hand-drawn look. */
export default function MermaidDiagram({ code, sketch = false }: { code: string; sketch?: boolean }) {
  const [svg, setSvg] = useState('')
  const [failed, setFailed] = useState(false)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    let cancelled = false
    setSvg('')
    setFailed(false)
    const withLook = (c: string) =>
      sketch && !c.startsWith('---') ? `---\nconfig:\n  look: handDrawn\n---\n${c}` : c
    const fail = () => {
      if (cancelled) return
      // One silent automatic retry (fresh import + fresh render) before giving up.
      if (attempt === 0) setTimeout(() => { if (!cancelled) setAttempt(1) }, 700)
      else setFailed(true)
    }
    loadMermaid()
      .then(async mermaid => {
        for (const candidate of [...new Set([code, sanitizeMermaid(code)])]) {
          for (const finalCode of [...new Set([withLook(candidate), candidate])]) {
            try {
              const { svg } = await mermaid.render(`ss-mmd-${++renderSeq}`, finalCode)
              if (!cancelled) setSvg(svg)
              return
            } catch (e) {
              console.warn('mermaid render failed, retrying:', e)
            }
          }
        }
        fail()
      })
      .catch(fail)
    return () => { cancelled = true }
  }, [code, sketch, attempt])

  if (failed) {
    const retryBtn = (
      <button type="button" className="ss-mermaid-retry" onClick={() => { setFailed(false); setAttempt(a => a + 1) }}>
        <PhosphorIcon className="bi bi-arrow-repeat" /> Try drawing again
      </button>
    )
    const mindmapOutline = outlineFromMindmap(code)
    if (mindmapOutline) {
      return (
        <div>
          {retryBtn}
          <div className="ss-outline">
            {mindmapOutline.map((item, i) => (
              <div key={i} className={`ss-outline-item ss-outline-d${Math.min(item.depth, 3)}`}>
                {item.text}
              </div>
            ))}
          </div>
        </div>
      )
    }
    const flowSteps = outlineFromFlowchart(code)
    if (flowSteps) {
      return (
        <div>
          {retryBtn}
          <div className="ss-outline">
            {flowSteps.map((s, i) => (
              <div key={i} className="ss-outline-item ss-outline-d1">{s}</div>
            ))}
          </div>
        </div>
      )
    }
    return (
      <div className="ss-mermaid-fallback">
        <p>The diagram could not be drawn.</p>
        {retryBtn}
        <pre>{code}</pre>
      </div>
    )
  }
  if (!svg) return <div className="ss-mermaid-loading">Drawing diagram…</div>
  return <div className="ss-mermaid" dangerouslySetInnerHTML={{ __html: svg }} />
}
