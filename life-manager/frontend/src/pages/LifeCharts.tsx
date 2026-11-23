import { useEffect, useRef } from 'react'
import * as d3 from 'd3'

// ── Types ─────────────────────────────────────────────────────────────────────
interface LifeGoal {
  id: number
  title: string
  area: string
  targetDate?: string
  progress: number
  status: 'active' | 'paused' | 'done'
  notes: string
}

interface LifeRelationship {
  id: number
  name: string
  circle: string
  lastContactedAt?: string
  importance: number
  notes: string
}

// ── helpers ───────────────────────────────────────────────────────────────────
function useDraw(ref: React.RefObject<SVGSVGElement | null>, deps: unknown[], fn: (W: number, H: number) => void | (() => void)) {
  useEffect(() => {
    const el = ref.current
    if (!el) return
    let cleanup: void | (() => void)
    const run = () => {
      cleanup?.()
      const W = el.clientWidth || el.getBoundingClientRect().width || 340
      const H = el.clientHeight || el.getBoundingClientRect().height || 340
      d3.select(el).selectAll('*').remove()
      cleanup = fn(W, H)
    }
    run()
    const ro = new ResizeObserver(run)
    ro.observe(el)
    return () => {
      ro.disconnect()
      cleanup?.()
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps)
}

// ── Radial Arc Ring Chart — Life Areas Balance Wheel ─────────────────────────
interface AreaScore {
  label: string
  score: number
  color: string
}

export function RadialAreaChart({ areas }: { areas: AreaScore[] }) {
  const ref = useRef<SVGSVGElement>(null)

  useDraw(ref, [areas], (W, H) => {
    if (!areas.length) return
    const svg = d3.select(ref.current!)
    const cx = W / 2
    const cy = H / 2
    const outerR = Math.min(cx, cy) - 16
    const innerR = outerR * 0.22
    const gap = 3
    const ringW = (outerR - innerR - gap * areas.length) / areas.length

    const defs = svg.append('defs')
    areas.forEach((_, i) => {
      const f = defs.append('filter').attr('id', `glow-r${i}`)
      f.append('feGaussianBlur').attr('stdDeviation', 4).attr('result', 'blur')
      const m = f.append('feMerge')
      m.append('feMergeNode').attr('in', 'blur')
      m.append('feMergeNode').attr('in', 'SourceGraphic')
    })

    const g = svg.append('g').attr('transform', `translate(${cx},${cy})`)

    areas.forEach((area, i) => {
      const rInner = innerR + i * (ringW + gap)
      const rOuter = rInner + ringW
      const rMid = (rInner + rOuter) / 2

      // Track ring (full circle, dim)
      g.append('circle')
        .attr('r', rMid)
        .attr('fill', 'none')
        .attr('stroke', 'rgba(31, 30, 27, 0.08)')
        .attr('stroke-width', ringW)

      const endAngle = -Math.PI / 2 + (area.score / 10) * Math.PI * 2

      // Glow arc
      const arcGlow = d3.arc<unknown>().innerRadius(rInner).outerRadius(rOuter)
        .startAngle(-Math.PI / 2).endAngle(endAngle).cornerRadius(3)
      g.append('path').attr('d', arcGlow(null)!).attr('fill', area.color).attr('opacity', 0.2)
        .attr('filter', `url(#glow-r${i})`)

      // Main arc
      const arc = d3.arc<unknown>().innerRadius(rInner).outerRadius(rOuter)
        .startAngle(-Math.PI / 2).endAngle(endAngle).cornerRadius(3)
      g.append('path').attr('d', arc(null)!).attr('fill', area.color).attr('opacity', 0.88)

      // Dot at arc end
      const dx = Math.cos(endAngle) * (rOuter + 5)
      const dy = Math.sin(endAngle) * (rOuter + 5)
      g.append('circle').attr('cx', dx).attr('cy', dy).attr('r', 3.5).attr('fill', area.color)

      // Label left side
      g.append('text')
        .attr('x', -(rOuter + 6))
        .attr('y', rInner + ringW / 2 - innerR - i * (ringW + gap) + (i - (areas.length - 1) / 2) * (ringW + gap))
        .attr('text-anchor', 'end')
        .attr('dominant-baseline', 'middle')
        .attr('fill', area.color)
        .attr('font-size', 10)
        .attr('font-weight', 700)
        .attr('letter-spacing', '0.04em')
        .attr('y', (rInner + ringW / 2) - cy + cy - rMid * 0 + (-(areas.length - 1) / 2 + i) * 0)

      // Use a simpler positioning for labels — at the top of each arc
      g.selectAll(`text.lbl-${i}`).remove()
      g.append('text')
        .attr('class', `lbl-${i}`)
        .attr('x', 0)
        .attr('y', -(rMid))
        .attr('text-anchor', 'middle')
        .attr('dominant-baseline', 'middle')
        .attr('fill', area.color)
        .attr('font-size', 9)
        .attr('font-weight', 700)
        .attr('letter-spacing', '0.06em')
        .attr('transform', `rotate(${((-Math.PI / 2 + endAngle) / 2) * (180 / Math.PI) + 90})`)
    })

    // Redraw labels cleanly — place each label at midpoint of its arc
    g.selectAll('[class^="lbl-"]').remove()
    areas.forEach((area, i) => {
      const rInner = innerR + i * (ringW + gap)
      const rOuter = rInner + ringW
      const midAngle = -Math.PI / 2 + (area.score / 10) * Math.PI

      const lx = Math.cos(midAngle) * (rOuter + 14)
      const ly = Math.sin(midAngle) * (rOuter + 14)

      g.append('text')
        .attr('x', lx)
        .attr('y', ly)
        .attr('text-anchor', midAngle > Math.PI / 2 || midAngle < -Math.PI / 2 ? 'end' : 'start')
        .attr('dominant-baseline', 'middle')
        .attr('fill', area.color)
        .attr('font-size', 9.5)
        .attr('font-weight', 700)
        .attr('letter-spacing', '0.05em')
        .text(`${area.label.toUpperCase()} ${area.score}`)
    })

    // Center
    const avg = (areas.reduce((s, a) => s + a.score, 0) / areas.length).toFixed(1)
    g.append('text').attr('text-anchor', 'middle').attr('y', -9)
      .attr('fill', '#1f1e1b').attr('font-size', 28).attr('font-weight', 900).text(avg)
    g.append('text').attr('text-anchor', 'middle').attr('y', 13)
      .attr('fill', 'rgba(31, 30, 27, 0.5)').attr('font-size', 9).attr('letter-spacing', '0.12em').text('BALANCE')
  })

  return <svg ref={ref} style={{ width: '100%', height: '340px', display: 'block', overflow: 'visible' }} />
}

// ── Bubble Chart — Goals by Area ──────────────────────────────────────────────
// Matches LifeBalanceWheel's palette so an area is the same colour wherever it
// appears on the page. Muted and warm to sit with the editorial paper, and one
// hue per area — Work and Growth previously shared a purple, which made two
// different parts of someone's life look identical.
export const AREA_COLORS: Record<string, string> = {
  Health: '#6f9440',
  Work: '#4d6f8a',
  Relationships: '#e4573d',
  Money: '#c99433',
  Growth: '#a05c8a',
  Home: '#8a6244',
}

export function GoalBubbleChart({ goals }: { goals: LifeGoal[] }) {
  const ref = useRef<SVGSVGElement>(null)

  useDraw(ref, [goals], (W, H) => {
    const svg = d3.select(ref.current!)

    if (!goals.length) {
      svg.append('text').attr('x', W / 2).attr('y', H / 2)
        .attr('text-anchor', 'middle').attr('fill', 'rgba(31, 30, 27, 0.45)').attr('font-size', 13)
        .text('Add a goal to see your progress map')
      return
    }

    const rScale = d3.scaleSqrt().domain([0, 100]).range([16, 54])

    const nodes = goals.map(g => ({
      ...g,
      r: rScale(Math.max(5, g.progress)),
      color: AREA_COLORS[g.area] ?? '#777269',
      x: W / 2 + (Math.random() - 0.5) * 80,
      y: H / 2 + (Math.random() - 0.5) * 40,
    }))

    const sim = d3.forceSimulation(nodes as any)
      .force('charge', d3.forceManyBody().strength(8))
      .force('center', d3.forceCenter(W / 2, H / 2 - 16))
      .force('collision', d3.forceCollide().radius((d: any) => d.r + 5))
      .stop()
    for (let i = 0; i < 200; i++) sim.tick()

    const defs = svg.append('defs')
    const glow = defs.append('filter').attr('id', 'bub-glow')
    glow.append('feGaussianBlur').attr('stdDeviation', 5).attr('result', 'blur')
    const merge = glow.append('feMerge')
    merge.append('feMergeNode').attr('in', 'blur')
    merge.append('feMergeNode').attr('in', 'SourceGraphic')

    const grp = svg.append('g')

    nodes.forEach((node: any) => {
      const ng = grp.append('g').attr('transform', `translate(${node.x},${node.y})`)
      const dim = node.status === 'done'

      ng.append('circle').attr('r', node.r + 8).attr('fill', node.color).attr('opacity', dim ? 0.04 : 0.14)
      ng.append('circle').attr('r', node.r)
        .attr('fill', node.color).attr('opacity', dim ? 0.22 : 0.78)
        .attr('stroke', node.color).attr('stroke-width', 1.5).attr('stroke-opacity', 0.5)

      // Progress arc
      if (node.progress > 0 && !dim) {
        const a = d3.arc<unknown>()
          .innerRadius(node.r - 4).outerRadius(node.r)
          .startAngle(-Math.PI / 2)
          .endAngle(-Math.PI / 2 + (node.progress / 100) * Math.PI * 2)
          .cornerRadius(2)
        ng.append('path').attr('d', a(null)!).attr('fill', 'white').attr('opacity', 0.5)
      }

      const fontSize = Math.max(8, Math.min(11, node.r / 2.6))
      const words = node.title.split(' ')
      const lines: string[] = []
      let cur = ''
      words.forEach((w: string) => {
        const test = cur ? `${cur} ${w}` : w
        if (test.length > 11 && cur) { lines.push(cur); cur = w }
        else cur = test
      })
      if (cur) lines.push(cur)
      const lineH = fontSize + 1.5
      const startY = -((lines.length - 1) * lineH) / 2

      lines.forEach((line, idx) => {
        ng.append('text')
          .attr('text-anchor', 'middle').attr('y', startY + idx * lineH)
          .attr('fill', dim ? 'rgba(31, 30, 27, 0.5)' : 'white')
          .attr('font-size', fontSize).attr('font-weight', 700).text(line)
      })

      ng.append('text').attr('text-anchor', 'middle')
        .attr('y', ((lines.length - 1) * lineH) / 2 + fontSize + 2)
        .attr('fill', dim ? 'rgba(31, 30, 27, 0.4)' : 'rgba(255, 255, 255, 0.92)')
        .attr('font-size', 8)
        .text(dim ? '✓ done' : `${node.progress}%`)
    })

    // Legend
    const areas = [...new Set(goals.map(g => g.area))]
    const leg = svg.append('g').attr('transform', `translate(8,${H - 18})`)
    areas.forEach((area, i) => {
      leg.append('circle').attr('cx', i * 88).attr('r', 4.5).attr('fill', AREA_COLORS[area] ?? '#777269')
      leg.append('text').attr('x', i * 88 + 8).attr('y', 4)
        .attr('fill', 'rgba(31, 30, 27, 0.6)').attr('font-size', 9).text(area)
    })
  })

  return <svg ref={ref} style={{ width: '100%', height: '320px', display: 'block', overflow: 'visible' }} />
}

// ── Force Network Graph — Relationships ───────────────────────────────────────
const CIRCLE_COLORS: Record<string, string> = {
  Family: '#e4573d',
  Friends: '#c99433',
  Work: '#4d6f8a',
  Community: '#6f9440',
}

interface NetworkNode extends d3.SimulationNodeDatum {
  id: string; label: string; type: 'hub' | 'person'
  circle?: string; importance?: number; color: string; r: number
}
interface NetworkLink extends d3.SimulationLinkDatum<NetworkNode> { color: string }

export function RelationshipNetwork({ relationships }: { relationships: LifeRelationship[] }) {
  const ref = useRef<SVGSVGElement>(null)

  useDraw(ref, [relationships], (W, H) => {
    const svg = d3.select(ref.current!)

    if (!relationships.length) {
      svg.append('text').attr('x', W / 2).attr('y', H / 2)
        .attr('text-anchor', 'middle').attr('fill', 'rgba(31, 30, 27, 0.45)').attr('font-size', 13)
        .text('Add people to see your network')
      return
    }

    const circles = [...new Set(relationships.map(r => r.circle))]

    const nodes: NetworkNode[] = [
      { id: '__you__', label: 'YOU', type: 'hub', color: '#bd3e2b', r: 22 },
      ...circles.map(c => ({ id: `hub-${c}`, label: c, type: 'hub' as const, circle: c, color: CIRCLE_COLORS[c] ?? '#777269', r: 14 })),
      ...relationships.map(r => ({
        id: `p-${r.id}`, label: r.name, type: 'person' as const, circle: r.circle,
        importance: r.importance, color: CIRCLE_COLORS[r.circle] ?? '#777269', r: 6 + (r.importance ?? 5) * 0.85,
      })),
    ]

    const links: NetworkLink[] = [
      ...circles.map(c => ({ source: '__you__', target: `hub-${c}`, color: CIRCLE_COLORS[c] ?? '#777269' })),
      ...relationships.map(r => ({ source: `hub-${r.circle}`, target: `p-${r.id}`, color: CIRCLE_COLORS[r.circle] ?? '#777269' })),
    ]

    const sim = d3.forceSimulation(nodes)
      .force('link', d3.forceLink(links).id((d: any) => d.id)
        .distance((l: any) => (l.target as NetworkNode).type === 'hub' ? 85 : 60).strength(0.6))
      .force('charge', d3.forceManyBody().strength(-130))
      .force('center', d3.forceCenter(W / 2, H / 2 - 14))
      .force('collision', d3.forceCollide().radius((d: any) => (d as NetworkNode).r + 7))

    const linkSel = svg.append('g').selectAll('line').data(links).join('line')
      .attr('stroke', d => d.color).attr('stroke-opacity', 0.22).attr('stroke-width', 1.5)

    const nodeSel = svg.append('g').selectAll('g').data(nodes).join('g')

    nodeSel.append('circle').attr('r', d => d.r + 6).attr('fill', d => d.color).attr('opacity', 0.1)
    nodeSel.append('circle').attr('r', d => d.r)
      .attr('fill', d => d.type === 'hub' ? d.color : 'transparent')
      .attr('fill-opacity', 0.78)
      .attr('stroke', d => d.color).attr('stroke-width', d => d.type === 'person' ? 1.8 : 0)
      .attr('stroke-opacity', 0.85)

    nodeSel.append('text')
      .attr('text-anchor', 'middle').attr('dominant-baseline', 'middle')
      .attr('fill', d => d.type === 'hub' ? 'white' : d.color)
      .attr('font-size', d => d.id === '__you__' ? 10 : d.type === 'hub' ? 8.5 : 7.5)
      .attr('font-weight', 800)
      .attr('dy', d => d.type === 'person' ? d.r + 9 : 0)
      .text(d => d.label.length > 10 ? d.label.slice(0, 9) + '…' : d.label)

    const clamp = (v: number, lo: number, hi: number) => Math.max(lo, Math.min(hi, v))

    sim.on('tick', () => {
      linkSel
        .attr('x1', d => (d.source as NetworkNode).x!)
        .attr('y1', d => (d.source as NetworkNode).y!)
        .attr('x2', d => (d.target as NetworkNode).x!)
        .attr('y2', d => (d.target as NetworkNode).y!)
      nodeSel.attr('transform', d =>
        `translate(${clamp(d.x!, d.r, W - d.r)},${clamp(d.y!, d.r, H - 24 - d.r)})`)
    })

    // Legend
    const leg = svg.append('g').attr('transform', `translate(8,${H - 18})`)
    circles.forEach((c, i) => {
      leg.append('circle').attr('cx', i * 90).attr('r', 4.5).attr('fill', CIRCLE_COLORS[c] ?? '#777269')
      leg.append('text').attr('x', i * 90 + 8).attr('y', 4)
        .attr('fill', 'rgba(31, 30, 27, 0.6)').attr('font-size', 9).text(c)
    })

    return () => sim.stop()
  })

  return <svg ref={ref} style={{ width: '100%', height: '320px', display: 'block', overflow: 'visible' }} />
}
