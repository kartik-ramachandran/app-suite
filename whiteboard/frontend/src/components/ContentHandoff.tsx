import { useNavigate } from 'react-router-dom'
import PhosphorIcon from './PhosphorIcon'
import { putHandoff, destinationsFor, type HandoffKind } from '../services/handoff'
import './ContentHandoff.css'

/**
 * "Do more with this" — the strip that carries one module's output into another.
 *
 * The same component in every producing module, driven by the destination
 * registry, so adding a route is a registry entry rather than new UI in two
 * places.
 *
 * Every destination is an explicit button. Nothing runs automatically: each one
 * costs an AI call, and someone digitising ten whiteboards should not silently
 * trigger ten study-note generations.
 */
export default function ContentHandoff({
  kind,
  title,
  content,
  sourceModule,
  sourceLabel,
  sourceId,
}: {
  kind: HandoffKind
  title: string
  content: string
  sourceModule: string
  sourceLabel: string
  sourceId?: string | number
}) {
  const navigate = useNavigate()
  const options = destinationsFor(kind)

  if (options.length === 0 || !content?.trim()) return null

  const send = (path: string) => {
    putHandoff({ sourceModule, sourceLabel, kind, title, content, sourceId })
    navigate(path)
  }

  return (
    <div className="ch-strip">
      <span className="ch-label">
        <PhosphorIcon className="bi bi-arrow-right-circle" /> Do more with this
      </span>

      <div className="ch-options">
        {options.map(d => (
          <button key={d.id} className="ch-btn" onClick={() => send(d.path)} title={d.hint}>
            <PhosphorIcon className={`bi ${d.icon}`} />
            <span className="ch-btn-label">{d.label}</span>
            <span className="ch-btn-hint">{d.hint}</span>
          </button>
        ))}
      </div>
    </div>
  )
}
