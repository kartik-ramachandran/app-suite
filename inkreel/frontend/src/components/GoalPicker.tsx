import PhosphorIcon from './PhosphorIcon'
import { useEffect, useState } from 'react'
import api from '../services/api'

interface Props {
  /** Current topic (topic mode) — used to ground the suggestions. */
  topic?: string | null
  /** Current pasted text (source mode) — takes precedence over topic. */
  content?: string | null
  /** Receives the composed intent string whenever the selection changes. */
  onIntentChange: (intent: string) => void
}

/**
 * CreatePrompt-style goal capture: AI suggests what the artifact could aim
 * for, the user ticks the ones that matter (plus optional free text), and the
 * composed goal steers generation.
 */
export default function GoalPicker({ topic, content, onIntentChange }: Props) {
  const [options, setOptions] = useState<string[]>([])
  const [selected, setSelected] = useState<string[]>([])
  const [custom, setCustom] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  // When the source material changes, previously suggested/selected goals no
  // longer apply — clear them so a stale goal can't silently steer a new topic.
  const sourceSig = `${(topic ?? '').trim()}|${(content ?? '').trim().slice(0, 80)}`
  const [lastSig, setLastSig] = useState(sourceSig)
  useEffect(() => {
    if (sourceSig === lastSig) return
    setLastSig(sourceSig)
    if (options.length || selected.length) { setOptions([]); setSelected([]) }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sourceSig])

  useEffect(() => {
    const parts = [...selected]
    if (custom.trim()) parts.push(custom.trim())
    onIntentChange(parts.length ? (parts.length === 1 ? parts[0] : 'Focus on: ' + parts.join('; ')) : '')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selected, custom])

  const suggest = async () => {
    const hasSource = (content ?? '').trim().length >= 40 || (topic ?? '').trim().length >= 3
    if (!hasSource) { setError('Add your topic or text first — goals are suggested from it.'); return }
    setError(''); setLoading(true)
    try {
      const res = await api.post('/ink/suggest-goals', {
        topic: topic?.trim() || null,
        content: content?.trim() || null,
        existing: options,
      })
      const fresh: string[] = [...new Set<string>(res.data?.goals ?? [])].filter(g => !options.includes(g))
      setOptions(prev => [...prev, ...fresh])
    } catch {
      setError('Could not suggest goals — try again.')
    } finally {
      setLoading(false)
    }
  }

  const toggle = (g: string) =>
    setSelected(prev => prev.includes(g) ? prev.filter(x => x !== g) : [...prev, g])

  return (
    <div className="gp-goals">
      <div className="gp-goals-row">
        <input
          className="ss-topic-input ss-intent-input"
          style={{ marginTop: 0 }}
          value={custom}
          onChange={e => setCustom(e.target.value)}
          placeholder="Optional: what should this achieve? — or let AI suggest goals →"
        />
        <button type="button" className="gp-goals-suggest" onClick={suggest} disabled={loading}>
          {loading ? <span className="ss-spin" /> : <PhosphorIcon className="bi bi-bullseye" />}
          {options.length ? 'More goals' : 'Suggest goals'}
        </button>
      </div>
      {error && <div className="gp-goals-error">{error}</div>}
      {options.length > 0 && (
        <div className="gp-goals-cloud">
          {options.map(g => (
            <button
              key={g}
              type="button"
              className={`ss-level-chip${selected.includes(g) ? ' active' : ''}`}
              onClick={() => toggle(g)}
            >
              {selected.includes(g) && <PhosphorIcon className="bi bi-check-lg" />} {g}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
