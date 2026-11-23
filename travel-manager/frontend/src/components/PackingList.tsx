import { useState } from 'react'
import PhosphorIcon from './PhosphorIcon'
import api from '../services/api'
import './PackingList.css'

interface PackItem {
  item: { id: number; name: string; category: string; colors: string; formality: string }
  why: string
}

interface PackingResult {
  destination: string
  nights: number
  note: string
  pack: PackItem[]
  missing: string[]
}

/**
 * A packing list built from the clothes the user actually owns.
 *
 * Lives in Travel but reads Wardrobe: a generic list ("5 shirts, 2 trousers")
 * is something any search can give you. Naming the specific garments already
 * hanging in someone's wardrobe — and what they are missing for the forecast —
 * needs both modules at once, which is the whole argument for them being in the
 * same app.
 */
export default function PackingList({
  destination,
  nights,
  month,
}: {
  destination: string
  nights: number
  month?: string
}) {
  const [result, setResult] = useState<PackingResult | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [occasions, setOccasions] = useState('')

  const build = async () => {
    setBusy(true); setError(''); setResult(null)
    try {
      const { data } = await api.post('/wardrobe/packing-list', {
        destination, nights, month, occasions: occasions.trim() || null,
      })
      setResult(data)
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message
      setError(msg || 'Could not build a packing list.')
    } finally {
      setBusy(false)
    }
  }

  const byCategory = (result?.pack ?? []).reduce<Record<string, PackItem[]>>((acc, p) => {
    const k = p.item.category || 'other'
    ;(acc[k] ||= []).push(p)
    return acc
  }, {})

  return (
    <div className="pk-card">
      <div className="pk-head">
        <h3><PhosphorIcon className="bi bi-suitcase" /> Pack from your wardrobe</h3>
        <p>Uses the clothes you've catalogued — not a generic list.</p>
      </div>

      {!result && (
        <div className="pk-form">
          <input
            value={occasions}
            onChange={e => setOccasions(e.target.value)}
            placeholder="Anything planned? e.g. two dinners out, a hike, one client meeting"
            onKeyDown={e => { if (e.key === 'Enter') void build() }}
          />
          <button className="pk-go" onClick={() => void build()} disabled={busy}>
            {busy ? <><span className="pk-spin" /> Building…</> : <><PhosphorIcon className="bi bi-magic" /> Build my packing list</>}
          </button>
        </div>
      )}

      {error && <p className="pk-error">{error}</p>}

      {result && (
        <>
          {result.note && <p className="pk-note"><PhosphorIcon className="bi bi-cloud-sun" /> {result.note}</p>}

          {Object.entries(byCategory).map(([cat, list]) => (
            <div key={cat} className="pk-group">
              <h4>{cat}</h4>
              {list.map(p => (
                <div key={p.item.id} className="pk-item">
                  <PhosphorIcon className="bi bi-check2-square" />
                  <div>
                    <div className="pk-item-name">{p.item.name}</div>
                    {p.why && <div className="pk-item-why">{p.why}</div>}
                  </div>
                </div>
              ))}
            </div>
          ))}

          {result.missing.length > 0 && (
            <div className="pk-missing">
              <h4><PhosphorIcon className="bi bi-cart" /> You don't have</h4>
              {result.missing.map((m, i) => <div key={i} className="pk-miss">{m}</div>)}
            </div>
          )}

          <button className="pk-again" onClick={() => setResult(null)}>Build a different list</button>
        </>
      )}
    </div>
  )
}
