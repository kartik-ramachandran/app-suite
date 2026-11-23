import PhosphorIcon from './PhosphorIcon'
import { useEffect, useMemo, useRef, useState } from 'react'
import MermaidDiagram from './MermaidDiagram'
import { studyStyleById } from '../data/studyStyles'
import '../pages/StudyStudio.css'

export interface StudyDoc {
  id: number
  styleId: string
  title: string
  data: any
}

// Styling that must survive the standalone HTML/PDF export (the export wrapper
// only carries generic document CSS, so the renderers' look ships inline).
export const STUDY_EXPORT_CSS = `
  @import url('https://fonts.googleapis.com/css2?family=Caveat:wght@500;700&family=Patrick+Hand&display=swap');
  .ss-render { color: #0f172a; }
  .ss-hand .ss-note-item, .ss-hand .ss-summary { font-family: 'Patrick Hand', 'Caveat', cursive; font-size: 18px; }
  .ss-hand .ss-note-section { background: repeating-linear-gradient(#fff, #fff 27px, #e8eef7 28px); border-left: 3px solid #fca5a5; }
  .ss-note-section { border: 1px solid #e2e8f0; border-radius: 12px; padding: 14px 16px 14px 18px; margin: 12px 0; page-break-inside: avoid; }
  .ss-cue { display: inline-block; font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: .06em; color: #c13584; background: #fdf2f8; border: 1px solid #f9a8d4; border-radius: 999px; padding: 2px 10px; margin-bottom: 6px; }
  .ss-note-h { margin: 0 0 8px; font-size: 16px; background: none; color: #0f172a; padding: 0; }
  .ss-note-item { margin: 5px 0 5px 18px; }
  .ss-note-item.ss-hot { background: linear-gradient(transparent 45%, #fef08a 45%); font-weight: 600; }
  .ss-summary { margin-top: 14px; padding: 12px 14px; border-radius: 10px; background: #f0fdf4; border: 1px solid #bbf7d0; }
  .ss-cornell .ss-note-section { display: grid; grid-template-columns: 150px 1fr; gap: 12px; }
  .ss-board { display: grid; grid-template-columns: repeat(auto-fill, minmax(180px, 1fr)); gap: 14px; }
  .ss-sticky { padding: 16px 14px 20px; min-height: 110px; box-shadow: 2px 4px 9px rgba(15,23,42,.14); font-family: 'Patrick Hand', 'Caveat', cursive; font-size: 17px; line-height: 1.35; page-break-inside: avoid; }
  .ss-sticky:nth-child(3n) { transform: rotate(-1.3deg); } .ss-sticky:nth-child(3n+1) { transform: rotate(1.1deg); }
  .ss-sticky.yellow { background: #fef08a; } .ss-sticky.pink { background: #fbcfe8; } .ss-sticky.blue { background: #bfdbfe; }
  .ss-sticky.green { background: #bbf7d0; } .ss-sticky.orange { background: #fed7aa; }
  .ss-sheet { column-count: 2; column-gap: 14px; }
  .ss-box { break-inside: avoid; border: 1.5px solid #e2e8f0; border-radius: 12px; padding: 12px 14px; margin: 0 0 14px; }
  .ss-box-h { margin: 0 0 8px; font-size: 13px; font-weight: 800; text-transform: uppercase; letter-spacing: .05em; color: #c13584; background: none; padding: 0; }
  .ss-box li { margin: 5px 0 5px 16px; font-size: 13.5px; }
  .ss-stats { display: flex; flex-wrap: wrap; gap: 12px; margin-bottom: 14px; }
  .ss-stat { flex: 1 1 110px; text-align: center; border: 1.5px solid #f9a8d4; background: #fdf2f8; border-radius: 12px; padding: 12px 8px; }
  .ss-stat strong { display: block; font-size: 22px; color: #c13584; }
  .ss-stat span { font-size: 11.5px; color: #64748b; }
  .ss-timeline { position: relative; margin-left: 10px; padding-left: 26px; border-left: 3px solid #d62976; }
  .ss-event { position: relative; margin: 0 0 18px; page-break-inside: avoid; }
  .ss-event::before { content: ''; position: absolute; left: -33.5px; top: 4px; width: 12px; height: 12px; border-radius: 50%; background: #d62976; border: 2.5px solid #fff; box-shadow: 0 0 0 2px #d62976; }
  .ss-event-label { font-size: 11.5px; font-weight: 800; letter-spacing: .05em; text-transform: uppercase; color: #833ab4; }
  .ss-event h3 { margin: 2px 0 4px; font-size: 15px; background: none; color: #0f172a; padding: 0; }
  .ss-event p { margin: 0; font-size: 13.5px; color: #475569; }
  .ss-venn-circles { display: flex; justify-content: center; margin: 8px 0 16px; }
  .ss-venn-circles span { width: 150px; height: 150px; border-radius: 50%; display: flex; align-items: center; justify-content: center; font-weight: 800; font-size: 13px; text-align: center; padding: 12px; }
  .ss-venn-a { background: rgba(214,41,118,.16); border: 2px solid #d62976; color: #9d174d; margin-right: -40px; }
  .ss-venn-b { background: rgba(131,58,180,.14); border: 2px solid #833ab4; color: #5b21b6; }
  .ss-venn-cols { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 12px; }
  .ss-venn-col { border: 1.5px solid #e2e8f0; border-radius: 12px; padding: 10px 12px; }
  .ss-venn-col h4 { margin: 0 0 6px; font-size: 12.5px; color: #c13584; }
  .ss-venn-col li { margin: 4px 0 4px 16px; font-size: 13px; }
  .ss-matrix { width: 100%; border-collapse: collapse; }
  .ss-matrix th { background: linear-gradient(135deg, #42115f, #c13584); color: #fff; padding: 9px 12px; text-align: left; font-size: 13px; }
  .ss-matrix td { border-bottom: 1px solid #e2e8f0; padding: 9px 12px; font-size: 13px; vertical-align: top; }
  .ss-matrix td:first-child { font-weight: 700; }
  .ss-cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 14px; }
  .ss-card { border: 1.5px solid #e9d5ff; border-radius: 14px; overflow: hidden; page-break-inside: avoid; }
  .ss-card-front { padding: 14px; font-weight: 700; background: linear-gradient(135deg, #fff5fb, #faf5ff); }
  .ss-card-back { padding: 12px 14px; font-size: 13.5px; border-top: 1.5px dashed #e9d5ff; }
  .ss-card-back::before { content: 'Answer'; display: block; font-size: 10.5px; font-weight: 800; letter-spacing: .06em; text-transform: uppercase; color: #c13584; margin-bottom: 4px; }
  .ss-flip-hint, .ss-answers-toggle, .mm-drag-hint { display: none !important; }
  .ss-q { border: 1.5px solid #e2e8f0; border-radius: 12px; padding: 13px 15px; margin: 0 0 12px; page-break-inside: avoid; }
  .ss-q-text { font-weight: 700; margin-bottom: 8px; }
  .ss-opt { margin: 4px 0 4px 8px; padding: 6px 10px; border-radius: 8px; font-size: 13.5px; }
  .ss-opt.correct { background: #f0fdf4; border: 1px solid #86efac; font-weight: 700; }
  .ss-answer { margin-top: 8px; padding: 8px 12px; border-radius: 9px; background: #f0fdf4; border: 1px solid #bbf7d0; font-size: 13px; display: block !important; }
  .ss-answer b { color: #15803d; }
  .mm-wrap { display: flex; align-items: center; justify-content: center; padding: 14px 0; }
  .mm-root { background: #1e293b; color: #fff; padding: 12px 20px; border-radius: 14px; font-weight: 800; font-size: 15px; flex-shrink: 0; margin: 0 14px; text-align: center; max-width: 170px; }
  .mm-side { display: flex; flex-direction: column; gap: 26px; }
  .mm-branch { display: flex; align-items: center; }
  .mm-left .mm-branch { flex-direction: row-reverse; }
  .mm-pill { position: relative; padding: 7px 14px; border-radius: 999px; font-size: 12.5px; font-weight: 600; color: #fff; background: var(--mm-c); max-width: 180px; line-height: 1.3; }
  .mm-branch-pill { font-weight: 800; font-size: 13px; }
  .mm-sub-pill { background: color-mix(in srgb, var(--mm-c) 22%, #fff); color: #334155; font-weight: 500; font-size: 11.5px; }
  .mm-kids { position: relative; display: flex; flex-direction: column; justify-content: center; gap: 10px; }
  .mm-right .mm-kids { margin-left: 22px; }
  .mm-left .mm-kids { margin-right: 22px; }
  .mm-right .mm-kids::before { content: ''; position: absolute; left: -11px; top: 14px; bottom: 14px; width: 2px; background: var(--mm-c); border-radius: 2px; opacity: .65; }
  .mm-left .mm-kids::before { content: ''; position: absolute; right: -11px; top: 14px; bottom: 14px; width: 2px; background: var(--mm-c); border-radius: 2px; opacity: .65; }
  .mm-kid { position: relative; display: flex; align-items: center; }
  .mm-left .mm-kid { flex-direction: row-reverse; }
  .mm-right .mm-kid > .mm-pill::before { content: ''; position: absolute; right: 100%; top: 50%; width: 11px; height: 2px; background: var(--mm-c); opacity: .65; }
  .mm-left .mm-kid > .mm-pill::before { content: ''; position: absolute; left: 100%; top: 50%; width: 11px; height: 2px; background: var(--mm-c); opacity: .65; }
  .mm-right .mm-branch > .mm-branch-pill::before { content: ''; position: absolute; right: 100%; top: 50%; width: 14px; height: 2px; background: var(--mm-c); }
  .mm-left .mm-branch > .mm-branch-pill::before { content: ''; position: absolute; left: 100%; top: 50%; width: 14px; height: 2px; background: var(--mm-c); }
  .mm-svg { width: 100%; height: auto; display: block; }
  .mm-svg text { font-family: 'Caveat', 'Patrick Hand', cursive; }
  .mm-svg-root { font-size: 24px; font-weight: 700; }
  .mm-svg-branch { font-size: 24px; font-weight: 700; letter-spacing: 0.5px; }
  .mm-svg-kid { font-size: 19px; font-weight: 600; }
  .mm-svg-sub { font-size: 16px; }

  .ss-notebook { position: relative; padding: 6px 10px 6px 34px; border-radius: 14px; background: #fffdf8; border: 1px solid #eee9dd; }
  .ss-notebook::before { content: ''; position: absolute; left: 9px; top: 12px; bottom: 12px; width: 14px; background: radial-gradient(circle at 7px 11px, #cbd5e1 3.5px, #f8fafc 4px, transparent 5px); background-size: 14px 24px; background-repeat: repeat-y; }
  .ss-notebook .ss-note-section { border: none; padding: 8px 12px 10px; margin: 6px 0; }
  .ss-notebook .ss-note-h { display: inline-block; padding: 0 8px; font-family: 'Patrick Hand', 'Caveat', cursive; font-size: 20px; }
  .ss-notebook .ss-note-section:nth-of-type(5n+1) .ss-note-h { background: linear-gradient(transparent 35%, #fef08a 35%); }
  .ss-notebook .ss-note-section:nth-of-type(5n+2) .ss-note-h { background: linear-gradient(transparent 35%, #fbcfe8 35%); }
  .ss-notebook .ss-note-section:nth-of-type(5n+3) .ss-note-h { background: linear-gradient(transparent 35%, #bbf7d0 35%); }
  .ss-notebook .ss-note-section:nth-of-type(5n+4) .ss-note-h { background: linear-gradient(transparent 35%, #bfdbfe 35%); }
  .ss-notebook .ss-note-section:nth-of-type(5n)   .ss-note-h { background: linear-gradient(transparent 35%, #fed7aa 35%); }
  .ss-doodle { float: right; font-size: 30px; line-height: 1; margin: 2px 4px 4px 12px; }
  .ss-notebook .ss-note-item.ss-example { display: inline-block; background: #ecfdf5; border-radius: 7px; padding: 1px 9px; color: #047857; }
  .ss-notebook .ss-summary { background: #fefce8; border: 1.5px dashed #f59e0b; border-radius: 12px; }
  .ss-notebook .ss-summary strong { color: #b45309; }
  .ss-notebook .ss-summary::before { content: '📌 '; }
`

// Model JSON is untrusted: any element may be null, a number, or an object.
// Coerce to strings at the boundary so a single malformed item can't white-screen.
const S = (v: any): string => (typeof v === 'string' ? v : v == null ? '' : typeof v === 'object' ? JSON.stringify(v) : String(v))
const hot = (item: any) => typeof item === 'string' && item.startsWith('**')
const strip = (item: any) => S(item).replace(/^\*\*\s*/, '')
const arr = (v: any): any[] => (Array.isArray(v) ? v.filter(x => x != null) : [])

const isExample = (item: any) => /^example[:\-\s]/i.test(strip(item))

function NotesRenderer({ data, styleId }: { data: any; styleId: string }) {
  const cls = styleId === 'handwritten' ? 'ss-hand'
    : styleId === 'cornell' ? 'ss-cornell'
    : styleId === 'notebook' ? 'ss-hand ss-notebook'
    : ''
  return (
    <div className={cls}>
      {arr(data.sections).map((s: any, i: number) => (
        <div className="ss-note-section" key={i}>
          {styleId === 'cornell' ? (
            <>
              <div>{s.cue && <span className="ss-cue">{S(s.cue)}</span>}</div>
              <div>
                <h3 className="ss-note-h">{S(s.heading)}</h3>
                {arr(s.items).map((it: any, j: number) => (
                  <div key={j} className={`ss-note-item${hot(it) ? ' ss-hot' : ''}`}>• {strip(it)}</div>
                ))}
              </div>
            </>
          ) : (
            <>
              {s.cue && (styleId === 'notebook'
                ? <span className="ss-doodle" aria-hidden="true">{S(s.cue)}</span>
                : <span className="ss-cue">{S(s.cue)}</span>)}
              <h3 className="ss-note-h">{S(s.heading)}</h3>
              {arr(s.items).map((it: any, j: number) => (
                <div key={j} className={`ss-note-item${hot(it) ? ' ss-hot' : ''}${styleId === 'notebook' && isExample(it) ? ' ss-example' : ''}`}>
                  {styleId === 'notebook' && isExample(it) ? '☑ ' : '• '}{strip(it)}
                </div>
              ))}
            </>
          )}
        </div>
      ))}
      {data.summary && <div className="ss-summary"><strong>Summary — </strong>{S(data.summary)}</div>}
    </div>
  )
}

function BoardRenderer({ data }: { data: any }) {
  return (
    <div className="ss-board">
      {arr(data.notes).map((n: any, i: number) => (
        <div key={i} className={`ss-sticky ${['yellow', 'pink', 'blue', 'green', 'orange'].includes(n.color) ? n.color : 'yellow'}`}>
          {S(n.text)}
        </div>
      ))}
    </div>
  )
}

function SheetRenderer({ data, styleId }: { data: any; styleId: string }) {
  const boxes: any[] = arr(data.boxes)
  const hasHeadlineStats = styleId === 'infographic' || styleId === 'kpi-dashboard'
  const statsBox = hasHeadlineStats ? boxes.find(b => /key numbers/i.test(S(b.heading))) : null
  const rest = statsBox ? boxes.filter(b => b !== statsBox) : boxes
  return (
    <div>
      {statsBox && (
        <div className="ss-stats">
          {arr(statsBox.items).map((raw: any, i: number) => {
            const [value, ...labelParts] = S(raw).split('—')
            return (
              <div className="ss-stat" key={i}>
                <strong>{value.trim()}</strong>
                <span>{labelParts.join('—').trim()}</span>
              </div>
            )
          })}
        </div>
      )}
      <div className={`ss-sheet ss-sheet-${styleId}`}>
        {rest.map((b: any, i: number) => (
          <div className="ss-box" key={i}>
            <h3 className="ss-box-h">{S(b.heading)}</h3>
            <ul>{arr(b.items).map((it: any, j: number) => <li key={j}>{S(it)}</li>)}</ul>
          </div>
        ))}
      </div>
    </div>
  )
}

const MM_COLORS = ['#f59e0b', '#14b8a6', '#f97316', '#8b5cf6', '#ec4899', '#22c55e', '#0ea5e9']

// Seeded PRNG so each mind map gets its own organic geometry, but a given doc
// always redraws identically (including in the PDF export).
const mulberry32 = (a: number) => () => {
  a |= 0; a = (a + 0x6D2B79F5) | 0
  let t = Math.imul(a ^ (a >>> 15), 1 | a)
  t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296
}
const hashStr = (s: string) => { let h = 0; for (const c of s) h = (h * 31 + c.charCodeAt(0)) | 0; return h }

// Buzan-style organic radial mind map: curved tapering branches radiating from
// the centre at seeded angles, handwriting labels, a different layout per doc.
function MindMapOrganic({ data, seed }: { data: any; seed: number }) {
  const rnd = mulberry32(seed)
  const W = 1000, H = 660, cx = W / 2, cy = H / 2
  const branches: any[] = data.branches ?? []
  const n = branches.length
  const clampX = (x: number) => Math.max(70, Math.min(W - 70, x))
  const clampY = (y: number) => Math.max(30, Math.min(H - 24, y))
  const colorOffset = Math.floor(rnd() * MM_COLORS.length)
  const rotation = rnd() * Math.PI * 2

  const els: React.ReactNode[] = []
  branches.forEach((b: any, i: number) => {
    const color = MM_COLORS[(i + colorOffset) % MM_COLORS.length]
    const a = rotation + (i / n) * Math.PI * 2 + (rnd() - 0.5) * 0.45
    const r1 = 165 + rnd() * 45
    const bx = clampX(cx + Math.cos(a) * r1)
    const by = clampY(cy + Math.sin(a) * r1)
    // Bowed trunk: control point pushed perpendicular for an organic curve.
    const bow = (rnd() - 0.5) * 90
    const mx = (cx + bx) / 2 - Math.sin(a) * bow
    const my = (cy + by) / 2 + Math.cos(a) * bow
    els.push(<path key={`b${i}`} d={`M ${cx} ${cy} Q ${mx} ${my} ${bx} ${by}`} stroke={color} strokeWidth={7.5} fill="none" strokeLinecap="round" />)
    const anchor = Math.cos(a) >= 0 ? 'start' : 'end'
    els.push(
      <text key={`bt${i}`} x={bx + (anchor === 'start' ? 10 : -10)} y={by - 8} textAnchor={anchor} className="mm-svg-branch" fill={color}
        transform={`rotate(${(rnd() - 0.5) * 10} ${bx} ${by})`}>
        {String(b.label ?? '').toUpperCase()}
      </text>
    )
    const kids: any[] = b.children ?? []
    kids.forEach((c: any, j: number) => {
      const ca = a + (j - (kids.length - 1) / 2) * (0.42 + rnd() * 0.1) + (rnd() - 0.5) * 0.12
      const r2 = r1 + 100 + rnd() * 35
      const kx = clampX(cx + Math.cos(ca) * r2)
      const ky = clampY(cy + Math.sin(ca) * r2)
      const kbow = (rnd() - 0.5) * 46
      const kmx = (bx + kx) / 2 - Math.sin(ca) * kbow
      const kmy = (by + ky) / 2 + Math.cos(ca) * kbow
      const kAnchor = Math.cos(ca) >= 0 ? 'start' : 'end'
      els.push(<path key={`k${i}-${j}`} d={`M ${bx} ${by} Q ${kmx} ${kmy} ${kx} ${ky}`} stroke={color} strokeWidth={3.6} fill="none" strokeLinecap="round" />)
      els.push(<circle key={`kc${i}-${j}`} cx={kx} cy={ky} r={4} fill={color} />)
      els.push(
        <text key={`kt${i}-${j}`} x={kx + (kAnchor === 'start' ? 8 : -8)} y={ky + 4} textAnchor={kAnchor} className="mm-svg-kid" fill="#334155">
          {String(c.label ?? '')}
        </text>
      )
      const subs: string[] = c.subitems ?? []
      subs.forEach((s: string, k: number) => {
        const sa = ca + (k - (subs.length - 1) / 2) * 0.3 + (rnd() - 0.5) * 0.1
        const r3 = r2 + 72 + rnd() * 20
        const sx = clampX(cx + Math.cos(sa) * r3)
        const sy = clampY(cy + Math.sin(sa) * r3)
        const sAnchor = Math.cos(sa) >= 0 ? 'start' : 'end'
        els.push(<path key={`s${i}-${j}-${k}`} d={`M ${kx} ${ky} L ${sx} ${sy}`} stroke={color} strokeWidth={2} fill="none" strokeLinecap="round" opacity={0.75} />)
        els.push(
          <text key={`st${i}-${j}-${k}`} x={sx + (sAnchor === 'start' ? 6 : -6)} y={sy + 4} textAnchor={sAnchor} className="mm-svg-sub" fill="#64748b">
            {s}
          </text>
        )
      })
    })
  })

  const root = String(data.root || data.title || '')
  const rootW = Math.max(110, root.length * 10 + 36)
  return (
    <svg className="mm-svg" viewBox={`0 0 ${W} ${H}`} role="img" aria-label={`Mind map: ${root}`}>
      {els}
      <rect x={cx - rootW / 2} y={cy - 26} width={rootW} height={52} rx={26} fill="#1e293b" />
      <text x={cx} y={cy + 6} textAnchor="middle" className="mm-svg-root" fill="#fff">{root}</text>
    </svg>
  )
}

// Interactive mind map: same radial idea, but every node is draggable — grab a
// pill and the connectors follow. Exports capture the arrangement as-is.
interface MMNode { id: string; label: string; x: number; y: number; color: string; kind: 'root' | 'branch' | 'kid' | 'sub'; parent?: string }

function MindMapInteractive({ data, seed }: { data: any; seed: number }) {
  const W = 1000, H = 660
  const initial = useMemo<MMNode[]>(() => {
    const rnd = mulberry32(seed)
    const cx = W / 2, cy = H / 2
    const branches: any[] = data.branches ?? []
    const n = Math.max(1, branches.length)
    const colorOffset = Math.floor(rnd() * MM_COLORS.length)
    const nodes: MMNode[] = [{ id: 'root', label: String(data.root || data.title || ''), x: cx, y: cy, color: '#1e293b', kind: 'root' }]
    const clampX = (x: number) => Math.max(80, Math.min(W - 80, x))
    const clampY = (y: number) => Math.max(30, Math.min(H - 30, y))
    branches.forEach((b: any, i: number) => {
      const color = MM_COLORS[(i + colorOffset) % MM_COLORS.length]
      const a = (i / n) * Math.PI * 2 + rnd() * 0.4
      const bid = `b${i}`
      nodes.push({ id: bid, label: String(b.label ?? ''), x: clampX(cx + Math.cos(a) * 190), y: clampY(cy + Math.sin(a) * 190), color, kind: 'branch', parent: 'root' })
      const kids: any[] = b.children ?? []
      kids.forEach((c: any, j: number) => {
        const ca = a + (j - (kids.length - 1) / 2) * 0.4
        const kid = `${bid}c${j}`
        nodes.push({ id: kid, label: String(c.label ?? ''), x: clampX(cx + Math.cos(ca) * 330), y: clampY(cy + Math.sin(ca) * 330), color, kind: 'kid', parent: bid })
        const subs: string[] = c.subitems ?? []
        subs.forEach((s: string, k: number) => {
          const sa = ca + (k - (subs.length - 1) / 2) * 0.24
          nodes.push({ id: `${kid}s${k}`, label: s, x: clampX(cx + Math.cos(sa) * 440), y: clampY(cy + Math.sin(sa) * 440), color, kind: 'sub', parent: kid })
        })
      })
    })
    return nodes
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [data, seed])

  const [pos, setPos] = useState<Record<string, { x: number; y: number }>>({})
  useEffect(() => {
    setPos(Object.fromEntries(initial.map(nd => [nd.id, { x: nd.x, y: nd.y }])))
  }, [initial])

  const svgRef = useRef<SVGSVGElement>(null)
  const dragRef = useRef<{ id: string; dx: number; dy: number } | null>(null)

  const svgPoint = (e: React.PointerEvent) => {
    const rect = svgRef.current?.getBoundingClientRect()
    if (!rect) return { x: 0, y: 0 }
    return { x: ((e.clientX - rect.left) / rect.width) * W, y: ((e.clientY - rect.top) / rect.height) * H }
  }

  const startDrag = (id: string) => (e: React.PointerEvent) => {
    e.preventDefault()
    const p = svgPoint(e)
    const cur = pos[id]
    if (!cur) return
    dragRef.current = { id, dx: p.x - cur.x, dy: p.y - cur.y }
    svgRef.current?.setPointerCapture(e.pointerId)
  }
  const onMove = (e: React.PointerEvent) => {
    const d = dragRef.current
    if (!d) return
    const p = svgPoint(e)
    setPos(prev => ({ ...prev, [d.id]: { x: p.x - d.dx, y: p.y - d.dy } }))
  }
  const endDrag = () => { dragRef.current = null }

  const at = (id: string) => pos[id] ?? { x: W / 2, y: H / 2 }
  const pillW = (nd: MMNode) => Math.min(230, Math.max(64, nd.label.length * (nd.kind === 'root' ? 9.5 : 7.2) + 28))
  const pillH = (nd: MMNode) => (nd.kind === 'root' ? 44 : nd.kind === 'sub' ? 26 : 32)
  const fontSize = (nd: MMNode) => (nd.kind === 'root' ? 17 : nd.kind === 'branch' ? 14 : nd.kind === 'kid' ? 13 : 11.5)

  return (
    <div>
      <div className="mm-drag-hint"><PhosphorIcon className="bi bi-arrows-move" /> Drag any node to arrange your map</div>
      <svg ref={svgRef} className="mm-svg mm-svg-interactive" viewBox={`0 0 ${W} ${H}`} onPointerMove={onMove} onPointerUp={endDrag} onPointerCancel={endDrag} onPointerLeave={endDrag} style={{ touchAction: 'none' }}>
        {initial.filter(nd => nd.parent).map(nd => {
          const a = at(nd.parent!)
          const b = at(nd.id)
          const mx = (a.x + b.x) / 2
          const my = (a.y + b.y) / 2 - (b.x - a.x) * 0.08
          return <path key={`e-${nd.id}`} d={`M ${a.x} ${a.y} Q ${mx} ${my} ${b.x} ${b.y}`} stroke={nd.color} strokeWidth={nd.kind === 'branch' ? 4.5 : nd.kind === 'kid' ? 2.8 : 1.8} fill="none" strokeLinecap="round" opacity={0.75} />
        })}
        {initial.map(nd => {
          const p = at(nd.id)
          const w = pillW(nd), h = pillH(nd)
          const light = nd.kind === 'sub'
          return (
            <g key={nd.id} transform={`translate(${p.x}, ${p.y})`} onPointerDown={startDrag(nd.id)} style={{ cursor: 'grab' }}>
              <rect x={-w / 2} y={-h / 2} width={w} height={h} rx={h / 2}
                fill={nd.kind === 'root' ? '#1e293b' : light ? '#fff' : nd.color}
                stroke={light ? nd.color : 'none'} strokeWidth={light ? 1.5 : 0} />
              <text x={0} y={fontSize(nd) / 3} textAnchor="middle" fontSize={fontSize(nd)} fontWeight={nd.kind === 'sub' ? 500 : 700}
                fill={nd.kind === 'root' ? '#fff' : light ? '#475569' : '#fff'} style={{ userSelect: 'none', fontFamily: 'Inter, sans-serif' }}>
                {nd.label.length > 34 ? nd.label.slice(0, 33) + '…' : nd.label}
              </text>
            </g>
          )
        })}
      </svg>
    </div>
  )
}

// Two-sided mind map: dark root in the centre, colour-coded branches splitting
// left/right, rounded pills, connector stubs — no mermaid involved.
function MindMapRenderer({ data }: { data: any }) {
  if (data.mermaid) return <MermaidDiagram code={String(data.mermaid)} />   // legacy docs
  const branches: any[] = Array.isArray(data.branches) ? data.branches : []
  if (!branches.length) return <div className="ss-mermaid-loading">No branches to draw.</div>

  // The user can pin a look (creative organic / clean pills / interactive drag);
  // otherwise each doc gets its own seeded surprise — mostly organic.
  const seed = hashStr(`${data.root ?? ''}|${data.title ?? ''}|${branches.length}|${branches[0]?.label ?? ''}`)
  const look = ['creative', 'clean', 'interactive'].includes(data.look) ? data.look : (mulberry32(seed)() > 0.3 ? 'creative' : 'clean')
  if (look === 'interactive') return <MindMapInteractive data={data} seed={seed} />
  if (look === 'creative') return <MindMapOrganic data={data} seed={seed} />

  const rightCount = Math.ceil(branches.length / 2)
  const right = branches.slice(0, rightCount)
  const left = branches.slice(rightCount)

  const renderBranch = (b: any, colorIndex: number) => (
    <div className="mm-branch" key={colorIndex} style={{ '--mm-c': MM_COLORS[colorIndex % MM_COLORS.length] } as any}>
      <div className="mm-pill mm-branch-pill">{b.label}</div>
      {Array.isArray(b.children) && b.children.length > 0 && (
        <div className="mm-kids">
          {b.children.map((c: any, j: number) => (
            <div className="mm-kid" key={j}>
              <div className="mm-pill mm-kid-pill">{c.label}</div>
              {Array.isArray(c.subitems) && c.subitems.length > 0 && (
                <div className="mm-kids">
                  {c.subitems.map((s: string, k: number) => (
                    <div className="mm-kid" key={k}><div className="mm-pill mm-sub-pill">{s}</div></div>
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  )

  return (
    <div className="mm-wrap">
      <div className="mm-side mm-left">{left.map((b, i) => renderBranch(b, rightCount + i))}</div>
      <div className="mm-root">{data.root || data.title}</div>
      <div className="mm-side mm-right">{right.map((b, i) => renderBranch(b, i))}</div>
    </div>
  )
}

function TimelineRenderer({ data }: { data: any }) {
  return (
    <div className="ss-timeline">
      {arr(data.events).map((e: any, i: number) => (
        <div className="ss-event" key={i}>
          <span className="ss-event-label">{S(e.label)}</span>
          <h3>{S(e.heading)}</h3>
          <p>{S(e.detail)}</p>
        </div>
      ))}
    </div>
  )
}

function VennRenderer({ data }: { data: any }) {
  return (
    <div>
      <div className="ss-venn-circles">
        <span className="ss-venn-a">{S(data.aLabel)}</span>
        <span className="ss-venn-b">{S(data.bLabel)}</span>
      </div>
      <div className="ss-venn-cols">
        <div className="ss-venn-col"><h4>Only {S(data.aLabel)}</h4><ul>{arr(data.aOnly).map((x: any, i: number) => <li key={i}>{S(x)}</li>)}</ul></div>
        <div className="ss-venn-col"><h4>Both</h4><ul>{arr(data.shared).map((x: any, i: number) => <li key={i}>{S(x)}</li>)}</ul></div>
        <div className="ss-venn-col"><h4>Only {S(data.bLabel)}</h4><ul>{arr(data.bOnly).map((x: any, i: number) => <li key={i}>{S(x)}</li>)}</ul></div>
      </div>
    </div>
  )
}

function MatrixRenderer({ data }: { data: any }) {
  return (
    <table className="ss-matrix">
      <thead>
        <tr><th></th>{arr(data.columns).map((c: any, i: number) => <th key={i}>{S(c)}</th>)}</tr>
      </thead>
      <tbody>
        {arr(data.rows).map((r: any, i: number) => (
          <tr key={i}>
            <td>{S(r.label)}</td>
            {arr(r.cells).map((c: any, j: number) => <td key={j}>{S(c)}</td>)}
          </tr>
        ))}
      </tbody>
    </table>
  )
}

function CardsRenderer({ data }: { data: any }) {
  const [flipped, setFlipped] = useState<Record<number, boolean>>({})
  return (
    <div className="ss-cards">
      {arr(data.cards).map((c: any, i: number) => (
        <div
          key={i}
          className={`ss-card${flipped[i] ? ' is-flipped' : ''}`}
          onClick={() => setFlipped(f => ({ ...f, [i]: !f[i] }))}
        >
          <div className="ss-card-front">{S(c.front)}<span className="ss-flip-hint"><PhosphorIcon className="bi bi-arrow-repeat" /> tap to flip</span></div>
          <div className="ss-card-back">{S(c.back)}</div>
        </div>
      ))}
    </div>
  )
}

const isCorrectOption = (option: string, index: number, answer: any): boolean => {
  const a = S(answer).trim()
  if (!a) return false
  if (a === option) return true
  const letter = String.fromCharCode(65 + index)
  const up = a.toUpperCase()
  if (up === letter || up.startsWith(letter + '.') || up.startsWith(letter + ')')) return true
  return a.replace(/^[A-Z][.)]s*/i, '') === option
}

function WorksheetRenderer({ data }: { data: any }) {
  const [showAnswers, setShowAnswers] = useState(false)
  return (
    <div className={showAnswers ? 'ss-show-answers' : ''}>
      <button type="button" className="ss-answers-toggle" onClick={() => setShowAnswers(s => !s)}>
        <PhosphorIcon className={`bi ${showAnswers ? 'bi-eye-slash' : 'bi-eye'}`} /> {showAnswers ? 'Hide answers' : 'Show answers'}
      </button>
      {arr(data.questions).map((q: any, i: number) => (
        <div className="ss-q" key={i}>
          <div className="ss-q-text">{i + 1}. {S(q.q)}</div>
          {arr(q.options).map((o: any, j: number) => (
            <div key={j} className={`ss-opt${showAnswers && isCorrectOption(S(o), j, q.answer) ? ' correct' : ''}`}>
              {String.fromCharCode(65 + j)}. {S(o)}
            </div>
          ))}
          <div className="ss-answer" style={{ display: showAnswers ? 'block' : 'none' }}>
            <b>Answer: {S(q.answer)}</b>{q.explain ? ` — ${S(q.explain)}` : ''}
          </div>
        </div>
      ))}
    </div>
  )
}

export function StudyRenderer({ doc }: { doc: StudyDoc }) {
  const meta = studyStyleById(doc.styleId)
  const data = doc.data ?? {}
  switch (meta?.archetype) {
    case 'board': return <BoardRenderer data={data} />
    case 'sheet': return <SheetRenderer data={data} styleId={doc.styleId} />
    case 'diagram': return <MermaidDiagram code={String(data.mermaid ?? '')} sketch={['sketch', 'casual', 'playful'].includes(data.tone)} />
    case 'mindmap': return <MindMapRenderer data={data} />
    case 'timeline': return <TimelineRenderer data={data} />
    case 'venn': return <VennRenderer data={data} />
    case 'matrix': return <MatrixRenderer data={data} />
    case 'cards': return <CardsRenderer data={data} />
    case 'worksheet': return <WorksheetRenderer data={data} />
    default: return <NotesRenderer data={data} styleId={doc.styleId} />
  }
}
