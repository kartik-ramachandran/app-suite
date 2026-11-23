import { useState } from 'react'
import PhosphorIcon from './PhosphorIcon'
import api from '../services/api'
import './NextActions.css'

/**
 * "What should I do about this?" for a scanned item.
 *
 * The scanner modules stop at extraction — here are the fields we read off your
 * photo. This is the other half: the thing you can actually do about it.
 *
 * The disclaimer is rendered from the server's response and is never optional.
 * It is appended in backend code rather than requested in the model prompt,
 * because a prompt is a request and the model can drop it exactly when the
 * answer is most alarming.
 */

interface NextAction {
  action: string
  why: string
  urgency: 'now' | 'soon' | 'whenever'
}

interface Response {
  actions: NextAction[]
  disclaimer: string
  withheld: boolean
  withheldReason?: string | null
}

const URGENCY_LABEL: Record<string, string> = {
  now: 'Do now',
  soon: 'Soon',
  whenever: 'When you can',
}

interface Props {
  /** warranty | medicine | bill */
  kind: string
  id: number
  /** Health/food/plant items get a visibly stronger caution treatment. */
  sensitive?: boolean
}

export default function NextActions({ kind, id, sensitive = false }: Props) {
  const [data, setData] = useState<Response | null>(null)
  const [loading, setLoading] = useState(false)
  const [failed, setFailed] = useState(false)

  const load = async () => {
    setLoading(true); setFailed(false)
    try {
      const { data } = await api.get(`/next-action/${kind}/${id}`)
      setData(data)
    } catch {
      setFailed(true)
    } finally {
      setLoading(false)
    }
  }

  // Opt-in: every call costs an AI request, so it runs when asked for, not on
  // render of a long list.
  if (!data && !loading && !failed) {
    return (
      <button className="na-ask" onClick={load}>
        <PhosphorIcon className="bi bi-lightbulb" /> What should I do about this?
      </button>
    )
  }

  if (loading) {
    return <div className="na-loading"><span className="na-spin" /> Thinking…</div>
  }

  if (failed) {
    return (
      <button className="na-ask" onClick={load}>
        <PhosphorIcon className="bi bi-arrow-clockwise" /> Try again
      </button>
    )
  }

  if (!data) return null

  return (
    <div className={`na ${sensitive ? 'na-sensitive' : ''}`}>
      {data.actions.length > 0 ? (
        <ul className="na-list">
          {data.actions.map((a, i) => (
            <li key={i} className={`na-item na-${a.urgency}`}>
              <span className="na-urgency">{URGENCY_LABEL[a.urgency] ?? 'Soon'}</span>
              <span className="na-body">
                <strong>{a.action}</strong>
                {a.why && <small>{a.why}</small>}
              </span>
            </li>
          ))}
        </ul>
      ) : (
        <p className="na-none">
          {data.withheldReason === 'screened'
            // Deliberately explicit rather than pretending nothing happened: a
            // silent empty state would hide that a safety rule fired.
            ? 'We held back the suggestions for this one. Please check with a professional instead.'
            : data.withheldReason === 'unavailable'
              ? 'Suggestions are not available right now.'
              : 'Nothing needs doing about this right now.'}
        </p>
      )}

      {/* Always rendered. Server-enforced, not model-generated. */}
      <p className="na-disclaimer">
        <PhosphorIcon className="bi bi-info-circle" /> {data.disclaimer}
      </p>
    </div>
  )
}
