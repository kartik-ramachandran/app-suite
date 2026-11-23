import { useEffect, useRef } from 'react'
import * as d3 from 'd3'
import type { LifeScenario } from '../services/api'

/**
 * Branching timeline of possible futures.
 *
 * A bar chart of likelihoods would answer the wrong question. What someone
 * wants to see is that their life forks from *today* — one trunk, several
 * paths, arriving at different places at different times. So the drawing is a
 * fan of curves leaving a single "now" node:
 *
 *   - vertical position  = how good/bad the branch is (opportunity high, risk low)
 *   - horizontal position = when it resolves, inside the chosen horizon
 *   - stroke opacity/width = likelihood, so probable futures read as solid and
 *     unlikely ones as faint. Encoding it visually avoids printing a spurious
 *     "68%" next to a projection about someone's life.
 *
 * Shares the resize/cleanup pattern with LifeCharts so both behave identically.
 */

function useDraw(
  ref: React.RefObject<SVGSVGElement | null>,
  deps: unknown[],
  fn: (W: number, H: number) => void | (() => void),
) {
  useEffect(() => {
    const el = ref.current
    if (!el) return
    let cleanup: void | (() => void)
    const run = () => {
      cleanup?.()
      const W = el.clientWidth || el.getBoundingClientRect().width || 640
      const H = el.clientHeight || el.getBoundingClientRect().height || 380
      d3.select(el).selectAll('*').remove()
      cleanup = fn(W, H)
    }
    run()
    const ro = new ResizeObserver(run)
    ro.observe(el)
    return () => { ro.disconnect(); cleanup?.() }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps)
}

const KIND_COLOR: Record<string, string> = {
  'current-path': '#94a3b8',
  opportunity: '#22c55e',
  risk: '#ef4444',
  wildcard: '#a855f7',
}

// Where a branch sits vertically. Opportunities rise, risks fall, the current
// path runs level — the shape alone should tell the story before any reading.
const KIND_LIFT: Record<string, number> = {
  opportunity: -1,
  'current-path': 0,
  wildcard: 0.55,
  risk: 1,
}

const HORIZON_LABEL: Record<string, string> = {
  '3-months': '3 months',
  '1-year': '1 year',
  '3-years': '3 years',
}

type Props = {
  scenarios: LifeScenario[]
  horizon: string
  selectedId?: number | null
  onSelect?: (s: LifeScenario) => void
}

export default function ScenarioTimeline({ scenarios, horizon, selectedId, onSelect }: Props) {
  const ref = useRef<SVGSVGElement>(null)

  useDraw(ref, [scenarios, horizon, selectedId], (W, H) => {
    if (!scenarios.length) return

    const svg = d3.select(ref.current!)
    const M = { top: 26, right: 150, bottom: 34, left: 74 }
    const iw = Math.max(120, W - M.left - M.right)
    const ih = Math.max(100, H - M.top - M.bottom)
    const g = svg.append('g').attr('transform', `translate(${M.left},${M.top})`)

    // Group by kind so several branches of the same type fan out instead of
    // stacking on one line.
    const byKind = d3.group(scenarios, d => d.kind)
    const positioned = scenarios.map(s => {
      const peers = byKind.get(s.kind)!
      const i = peers.indexOf(s)
      const spread = peers.length > 1 ? (i - (peers.length - 1) / 2) * 0.42 : 0
      const lift = (KIND_LIFT[s.kind] ?? 0) + spread
      return { s, lift }
    })

    const yScale = d3.scaleLinear()
      .domain([d3.min(positioned, p => p.lift)! - 0.5, d3.max(positioned, p => p.lift)! + 0.5])
      .range([12, ih - 12])

    const nowX = 0
    const endX = iw

    // ── Time axis ────────────────────────────────────────────────────────
    const ticks = horizon === '3-months'
      ? ['now', '1 mo', '2 mo', '3 mo']
      : horizon === '3-years'
      ? ['now', '1 yr', '2 yr', '3 yr']
      : ['now', '3 mo', '6 mo', '1 yr']

    const tickX = d3.scalePoint<string>().domain(ticks).range([nowX, endX])

    g.append('line')
      .attr('x1', nowX).attr('x2', endX)
      .attr('y1', ih + 12).attr('y2', ih + 12)
      .attr('stroke', 'rgba(148,163,184,0.28)')

    g.selectAll('.tick-label')
      .data(ticks).enter().append('text')
      .attr('x', d => tickX(d)!)
      .attr('y', ih + 27)
      .attr('text-anchor', (_d, i) => (i === 0 ? 'start' : i === ticks.length - 1 ? 'end' : 'middle'))
      .attr('fill', '#64748b')
      .attr('font-size', 10)
      .attr('letter-spacing', '0.08em')
      .text(d => d.toUpperCase())

    // Faint guides so "when" is readable without a heavy grid.
    g.selectAll('.guide')
      .data(ticks.slice(1)).enter().append('line')
      .attr('x1', d => tickX(d)!).attr('x2', d => tickX(d)!)
      .attr('y1', 0).attr('y2', ih + 12)
      .attr('stroke', 'rgba(148,163,184,0.09)')
      .attr('stroke-dasharray', '2 5')

    // ── Branches ─────────────────────────────────────────────────────────
    const trunkY = yScale(0)

    // Longer horizons resolve later; a 3-month scenario should not sprawl
    // across a 3-year canvas.
    const reach = (likelihood: number) => 0.55 + (likelihood / 100) * 0.45

    const link = d3.linkHorizontal<unknown, [number, number]>()
      .source(d => (d as { s: [number, number] }).s)
      .target(d => (d as { t: [number, number] }).t)

    const branch = g.selectAll('.branch')
      .data(positioned).enter().append('g')
      .attr('class', 'branch')
      .style('cursor', onSelect ? 'pointer' : 'default')
      .on('click', (_e, d) => onSelect?.(d.s))

    branch.append('path')
      .attr('d', d => {
        const x2 = endX * reach(d.s.likelihood)
        const y2 = yScale(d.lift)
        return link({ s: [nowX, trunkY], t: [x2, y2] } as never)
      })
      .attr('fill', 'none')
      .attr('stroke', d => KIND_COLOR[d.s.kind] ?? '#64748b')
      // Likelihood as visual weight rather than a printed number.
      .attr('stroke-width', d => (selectedId === d.s.id ? 4 : 1.5 + (d.s.likelihood / 100) * 2.5))
      .attr('stroke-opacity', d => (selectedId && selectedId !== d.s.id ? 0.22 : 0.35 + (d.s.likelihood / 100) * 0.6))
      .attr('stroke-linecap', 'round')
      .attr('stroke-dasharray', d => (d.s.kind === 'wildcard' ? '6 5' : null))
      .each(function () {
        // Draw each path on so the fan "grows" out of today.
        const len = (this as SVGPathElement).getTotalLength()
        d3.select(this)
          .attr('stroke-dashoffset', len)
          .attr('stroke-dasharray', `${len} ${len}`)
          .transition().duration(850).ease(d3.easeCubicOut)
          .attr('stroke-dashoffset', 0)
          .on('end', function () {
            const d = d3.select(this).datum() as { s: LifeScenario }
            d3.select(this).attr('stroke-dasharray', d.s.kind === 'wildcard' ? '6 5' : null)
          })
      })

    // Endpoint marker + label.
    const end = branch.append('g')
      .attr('transform', d => `translate(${endX * reach(d.s.likelihood)},${yScale(d.lift)})`)
      .attr('opacity', 0)

    end.transition().delay(600).duration(400).attr('opacity', 1)

    end.append('circle')
      .attr('r', d => (selectedId === d.s.id ? 7 : 5))
      .attr('fill', d => KIND_COLOR[d.s.kind] ?? '#64748b')
      .attr('stroke', '#0f172a')
      .attr('stroke-width', 2)

    end.append('circle')
      .attr('r', d => (selectedId === d.s.id ? 13 : 10))
      .attr('fill', 'none')
      .attr('stroke', d => KIND_COLOR[d.s.kind] ?? '#64748b')
      .attr('stroke-opacity', 0.28)

    end.append('text')
      .attr('x', 15).attr('dy', '0.32em')
      .attr('fill', d => (selectedId && selectedId !== d.s.id ? '#64748b' : '#e2e8f0'))
      .attr('font-size', 11.5)
      .attr('font-weight', d => (selectedId === d.s.id ? 700 : 500))
      .text(d => (d.s.title.length > 26 ? d.s.title.slice(0, 25) + '…' : d.s.title))
      .append('title')
      .text(d => `${d.s.title}\n${d.s.summary}`)

    // ── "Today" trunk node, drawn last so it sits on top ─────────────────
    const now = g.append('g').attr('transform', `translate(${nowX},${trunkY})`)

    now.append('circle').attr('r', 16).attr('fill', 'rgba(249,115,22,0.14)')
    now.append('circle').attr('r', 9).attr('fill', 'rgba(249,115,22,0.28)')
    now.append('circle').attr('r', 5).attr('fill', '#f97316')

    now.append('circle')
      .attr('r', 9).attr('fill', 'none')
      .attr('stroke', '#f97316').attr('stroke-width', 1.5)
      .append('animate')
      .attr('attributeName', 'r').attr('values', '9;20;9')
      .attr('dur', '3s').attr('repeatCount', 'indefinite')

    now.append('text')
      .attr('x', -14).attr('dy', '0.32em')
      .attr('text-anchor', 'end')
      .attr('fill', '#f8fafc').attr('font-size', 11.5).attr('font-weight', 700)
      .text('Today')

    // Horizon caption, top-right.
    g.append('text')
      .attr('x', endX).attr('y', -10)
      .attr('text-anchor', 'end')
      .attr('fill', '#475569').attr('font-size', 10)
      .attr('letter-spacing', '0.1em')
      .text(`NEXT ${(HORIZON_LABEL[horizon] ?? horizon).toUpperCase()}`)
  })

  if (!scenarios.length) return null

  return (
    <div className="st-wrap">
      <svg ref={ref} className="st-svg" role="img"
           aria-label={`Branching timeline of ${scenarios.length} possible futures over the next ${HORIZON_LABEL[horizon] ?? horizon}`} />
      <div className="st-legend">
        {Object.entries({
          'current-path': 'Current path',
          opportunity: 'Opportunity',
          risk: 'Risk',
          wildcard: 'Wildcard',
        }).map(([k, label]) => (
          <span key={k}><i style={{ background: KIND_COLOR[k] }} />{label}</span>
        ))}
        <span className="st-hint">Thicker line = more likely</span>
      </div>
    </div>
  )
}
