import { useEffect, useRef } from 'react'
import * as d3 from 'd3'

/**
 * Life balance wheel.
 *
 * Replaces a rotating 3D constellation that looked striking and communicated
 * nothing: the nodes were unlabelled, two of the five areas shared a colour,
 * the connecting lines were decorative rather than meaningful, and the score
 * only moved a node's radius by 0.2 units — invisible at any real size.
 *
 * Same underlying data, drawn so it can actually be read:
 *   - one labelled spoke per life area, with its score
 *   - petal length AND colour intensity both encode the score, so it survives
 *     a glance and a greyscale screenshot
 *   - a faint reference ring at 5/10 to show what "average" looks like
 *   - the overall life score in the middle, where the eye lands first
 *
 * Deliberately still bold — glow, saturation, animated draw-on — just bold in
 * service of the number rather than instead of it.
 */

export interface AreaScore {
  label: string
  score: number
  color: string
}

type Props = {
  areas: AreaScore[]
  lifeScore?: number
  /** Today's check-in, when there is one. */
  mood?: number
  energy?: number
  focus?: number
  /** Days since the last check-in, used to say so plainly when data is stale. */
  staleDays?: number | null
}

// Distinct hue per area. The old palette repeated purple for both Work and
// Growth, which made two different parts of someone's life look identical.
//
// Muted and warm rather than saturated, so the wheel belongs to the editorial
// palette without collapsing into six shades of vermilion — a categorical chart
// still has to be readable, and that needs hues that differ.
const AREA_COLOR: Record<string, string> = {
  Health: '#6f9440',
  Work: '#4d6f8a',
  Relationships: '#e4573d',
  Money: '#c99433',
  Growth: '#a05c8a',
  Home: '#8a6244',
}

const colorFor = (a: AreaScore, i: number) =>
  AREA_COLOR[a.label] ?? a.color ?? d3.schemeTableau10[i % 10]

export default function LifeBalanceWheel({
  areas, lifeScore, mood, energy, focus, staleDays,
}: Props) {
  const ref = useRef<SVGSVGElement>(null)

  useEffect(() => {
    const el = ref.current
    if (!el || !areas.length) return

    const draw = () => {
      const W = el.clientWidth || 420
      const H = el.clientHeight || 420
      d3.select(el).selectAll('*').remove()

      const svg = d3.select(el)
      const cx = W / 2
      const cy = H / 2
      const outer = Math.min(cx, cy) - 62      // room for labels outside the ring
      const inner = Math.max(46, outer * 0.34) // hole for the score
      if (outer <= inner) return

      const g = svg.append('g').attr('transform', `translate(${cx},${cy})`)
      const n = areas.length
      const step = (Math.PI * 2) / n
      const pad = step * 0.13

      const r = d3.scaleLinear().domain([0, 10]).range([inner, outer])

      // Soft glow so the petals read as lit rather than flat.
      const defs = svg.append('defs')
      const blur = defs.append('filter').attr('id', 'lbw-glow')
        .attr('x', '-50%').attr('y', '-50%').attr('width', '200%').attr('height', '200%')
      blur.append('feGaussianBlur').attr('stdDeviation', 3.2).attr('result', 'b')
      const merge = blur.append('feMerge')
      merge.append('feMergeNode').attr('in', 'b')
      merge.append('feMergeNode').attr('in', 'SourceGraphic')

      // ── Reference rings ────────────────────────────────────────────────
      // Without these a petal length means nothing — is 6 good? Now 5/10 is
      // visible as the mid ring, so every petal is read against it.
      ;[2.5, 5, 7.5, 10].forEach(v => {
        g.append('circle')
          .attr('r', r(v))
          .attr('fill', 'none')
          .attr('stroke', v === 5 ? 'rgba(148,163,184,0.34)' : 'rgba(148,163,184,0.14)')
          .attr('stroke-dasharray', v === 5 ? '4 4' : '2 6')
      })

      g.append('text')
        .attr('x', 4).attr('y', -r(5) - 4)
        .attr('fill', '#777269').attr('font-size', 9).attr('letter-spacing', '0.06em')
        .text('5')

      // ── Petals ─────────────────────────────────────────────────────────
      const arc = d3.arc<{ a: AreaScore; i: number }>()
        .innerRadius(inner)
        .outerRadius(d => r(Math.max(0.4, Math.min(10, d.a.score))))
        .startAngle(d => d.i * step + pad)
        .endAngle(d => (d.i + 1) * step - pad)
        .cornerRadius(7)

      const data = areas.map((a, i) => ({ a, i }))

      // Faint full-length track, so a low score reads as a gap rather than as
      // missing data.
      const track = d3.arc<{ a: AreaScore; i: number }>()
        .innerRadius(inner).outerRadius(outer)
        .startAngle(d => d.i * step + pad)
        .endAngle(d => (d.i + 1) * step - pad)
        .cornerRadius(7)

      g.selectAll('.track').data(data).enter().append('path')
        .attr('d', track as never)
        .attr('fill', 'rgba(148,163,184,0.06)')

      g.selectAll('.petal').data(data).enter().append('path')
        .attr('fill', d => colorFor(d.a, d.i))
        // Intensity carries the score too: a weak area looks weak even in a
        // screenshot with no numbers.
        .attr('fill-opacity', d => 0.34 + (Math.min(10, d.a.score) / 10) * 0.56)
        .attr('filter', 'url(#lbw-glow)')
        .attr('d', d => arc({ ...d, a: { ...d.a, score: 0.4 } } as never))
        .transition().duration(760).delay((_d, i) => i * 70).ease(d3.easeCubicOut)
        .attrTween('d', function (d) {
          const it = d3.interpolate(0.4, Math.max(0.4, Math.min(10, d.a.score)))
          return (t: number) => arc({ ...d, a: { ...d.a, score: it(t) } } as never)!
        })

      // ── Labels ─────────────────────────────────────────────────────────
      // The single biggest fix: you can now tell which area is which.
      const labels = g.selectAll('.lbl').data(data).enter().append('g')
        .attr('transform', d => {
          const mid = d.i * step + step / 2 - Math.PI / 2
          const lr = outer + 26
          return `translate(${Math.cos(mid) * lr},${Math.sin(mid) * lr})`
        })
        .attr('opacity', 0)

      labels.transition().delay(500).duration(360).attr('opacity', 1)

      labels.append('text')
        .attr('text-anchor', d => {
          const mid = d.i * step + step / 2 - Math.PI / 2
          const x = Math.cos(mid)
          return Math.abs(x) < 0.28 ? 'middle' : x > 0 ? 'start' : 'end'
        })
        .attr('dy', '-0.15em')
        .attr('fill', '#d4d0c5')
        .attr('font-size', 11.5)
        .attr('font-weight', 600)
        .text(d => d.a.label)

      labels.append('text')
        .attr('text-anchor', d => {
          const mid = d.i * step + step / 2 - Math.PI / 2
          const x = Math.cos(mid)
          return Math.abs(x) < 0.28 ? 'middle' : x > 0 ? 'start' : 'end'
        })
        .attr('dy', '1.05em')
        .attr('fill', d => colorFor(d.a, d.i))
        .attr('font-size', 12.5)
        .attr('font-weight', 700)
        .attr('font-variant-numeric', 'tabular-nums')
        .text(d => `${Math.round(d.a.score)}/10`)

      // ── Centre ─────────────────────────────────────────────────────────
      const hasScore = typeof lifeScore === 'number' && lifeScore > 0

      g.append('circle')
        .attr('r', inner - 6)
        .attr('fill', 'rgba(2,6,23,0.55)')
        .attr('stroke', 'rgba(148,163,184,0.18)')

      if (hasScore) {
        g.append('text')
          .attr('text-anchor', 'middle').attr('dy', '-0.05em')
          .attr('fill', '#fbfaf5').attr('font-size', 30).attr('font-weight', 800)
          .attr('font-variant-numeric', 'tabular-nums')
          .text(0)
          .transition().duration(900).ease(d3.easeCubicOut)
          .tween('t', function () {
            const node = this as SVGTextElement
            const it = d3.interpolateNumber(0, lifeScore!)
            return (t: number) => { node.textContent = String(Math.round(it(t))) }
          })

        g.append('text')
          .attr('text-anchor', 'middle').attr('dy', '1.5em')
          .attr('fill', '#777269').attr('font-size', 9).attr('letter-spacing', '0.14em')
          .text('LIFE SCORE')
      } else {
        // Say why it's empty instead of showing "--" and leaving the user to guess.
        g.append('text')
          .attr('text-anchor', 'middle').attr('dy', '-0.2em')
          .attr('fill', '#aba69b').attr('font-size', 11).attr('font-weight', 600)
          .text('No score')
        g.append('text')
          .attr('text-anchor', 'middle').attr('dy', '1.3em')
          .attr('fill', '#777269').attr('font-size', 9)
          .text('check in first')
      }
    }

    draw()
    const ro = new ResizeObserver(draw)
    ro.observe(el)
    return () => ro.disconnect()
  }, [areas, lifeScore])

  const today = [
    { k: 'Mood', v: mood }, { k: 'Energy', v: energy }, { k: 'Focus', v: focus },
  ]
  const hasToday = today.some(t => typeof t.v === 'number')

  return (
    <div className="lbw-root">
      <svg ref={ref} className="lbw-svg" role="img"
           aria-label={`Life balance: ${areas.map(a => `${a.label} ${Math.round(a.score)} out of 10`).join(', ')}`} />

      <div className="lbw-readout">
        {hasToday ? (
          today.map(t => (
            <span key={t.k} className={typeof t.v === 'number' ? undefined : 'lbw-muted'}>
              {t.k}<b>{typeof t.v === 'number' ? t.v : '—'}</b>
            </span>
          ))
        ) : (
          // The old HUD showed "MOOD --" with no explanation. Be explicit.
          <span className="lbw-empty">
            {staleDays && staleDays > 0
              ? `Last check-in ${staleDays} day${staleDays === 1 ? '' : 's'} ago`
              : 'No check-in yet today'}
          </span>
        )}
      </div>
    </div>
  )
}
