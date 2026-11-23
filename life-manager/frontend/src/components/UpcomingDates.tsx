import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import PhosphorIcon from './PhosphorIcon'
import api from '../services/api'
import './UpcomingDates.css'

/**
 * Everything with a date on it, from every module, in one list.
 *
 * Bills, warranties and medicines each held the same shape of fact — a thing, a
 * date, and a consequence for missing it — on three separate screens that never
 * referenced each other. The app could know your extended warranty ran out on
 * Friday and still never mention it.
 *
 * Rendered inside existing pages rather than as a new route: the point is that
 * you see the whole picture wherever you already are, not that there is one
 * more place to visit.
 */

interface UpcomingItem {
  source: 'bill' | 'warranty' | 'medicine'
  id: number
  title: string
  detail: string | null
  date: string
  daysAway: number
  urgency: 'overdue' | 'soon' | 'later'
  route: string
}

interface Counts {
  total: number
  overdue: number
  soon: number
}

const ICON: Record<string, string> = {
  bill: 'bi-receipt-cutoff',
  warranty: 'bi-shield-check',
  medicine: 'bi-capsule',
}

/** Reads the way a person would say it, not "in -3 days". */
function when(daysAway: number): string {
  if (daysAway < -1) return `${Math.abs(daysAway)} days overdue`
  if (daysAway === -1) return 'overdue since yesterday'
  if (daysAway === 0) return 'today'
  if (daysAway === 1) return 'tomorrow'
  if (daysAway <= 30) return `in ${daysAway} days`
  const months = Math.round(daysAway / 30)
  return `in ${months} month${months > 1 ? 's' : ''}`
}

interface Props {
  /** Look-ahead window. */
  days?: number
  /** Cap the rendered rows; the rest stay behind the count. */
  limit?: number
  /** Hide a module's own rows — Bill Tracker should not repeat its own list. */
  exclude?: UpcomingItem['source'][]
  title?: string
}

export default function UpcomingDates({ days = 60, limit = 6, exclude = [], title = 'Coming up' }: Props) {
  const [items, setItems] = useState<UpcomingItem[]>([])
  const [counts, setCounts] = useState<Counts | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false
    api.get(`/upcoming?days=${days}`)
      .then(({ data }) => {
        if (cancelled) return
        setItems((data.items || []).filter((i: UpcomingItem) => !exclude.includes(i.source)))
        setCounts(data.counts || null)
      })
      .catch(() => { /* a panel that fails should disappear, not shout */ })
      .finally(() => { if (!cancelled) setLoading(false) })
    return () => { cancelled = true }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [days, exclude.join(',')])

  // Render nothing at all when there is nothing to say. An empty "Coming up"
  // box is noise on every page that hosts it.
  if (loading || items.length === 0) return null

  const shown = items.slice(0, limit)
  const hidden = items.length - shown.length

  return (
    <section className="upc">
      <header className="upc-head">
        <h3><PhosphorIcon className="bi bi-calendar-check" /> {title}</h3>
        {counts && counts.overdue > 0 && (
          <span className="upc-badge upc-overdue">{counts.overdue} overdue</span>
        )}
      </header>

      <ul className="upc-list">
        {shown.map(item => (
          <li key={`${item.source}-${item.id}`} className={`upc-item upc-${item.urgency}`}>
            <Link to={item.route} className="upc-link">
              <span className="upc-icon"><PhosphorIcon className={`bi ${ICON[item.source]}`} /></span>
              <span className="upc-body">
                <strong>{item.title}</strong>
                {item.detail && <small>{item.detail}</small>}
              </span>
              <span className="upc-when">{when(item.daysAway)}</span>
            </Link>
          </li>
        ))}
      </ul>

      {hidden > 0 && <p className="upc-more">+{hidden} more in the next {days} days</p>}
    </section>
  )
}
