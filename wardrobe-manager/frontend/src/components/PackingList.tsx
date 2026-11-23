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

const MONTHS = [
  'January', 'February', 'March', 'April', 'May', 'June',
  'July', 'August', 'September', 'October', 'November', 'December',
]

/**
 * A packing list built from the clothes the user actually owns.
 *
 * In the travel-manager app this card lived inside a generated travel guide and
 * received the trip as props. Standalone, the user types the trip in directly:
 * destination, nights, and travel month — the backend endpoint is identical.
 * A generic list ("5 shirts, 2 trousers") is something any search can give you;
 * naming the specific garments hanging in someone's wardrobe — and what they are
 * missing for the season — needs the catalogue and the trip together.
 */
export default function PackingList() {
  const [destination, setDestination] = useState('')
  const [nights, setNights] = useState(3)
  const [month, setMonth] = useState('')
  const [occasions, setOccasions] = useState('')
  const [result, setResult] = useState<PackingResult | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const build = async () => {
    if (!destination.trim()) { setError('Where are you going?'); return }
    setBusy(true); setError(''); setResult(null)
    try {
      const { data } = await api.post('/wardrobe/packing-list', {
        destination: destination.trim(),
        nights,
        month: month || null,
        occasions: occasions.trim() || null,
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
    <div className="pk-card" id="pack-for-trip">
      <div className="pk-head">
        <h3><PhosphorIcon className="bi bi-suitcase" /> Pack for a trip</h3>
        <p>A packing list built from the clothes you've catalogued — not a generic list.</p>
      </div>

      {!result && (
        <>
          <div className="pk-trip">
            <label className="pk-field pk-field-wide">
              <span>Destination</span>
              <input
                value={destination}
                onChange={e => setDestination(e.target.value)}
                placeholder="e.g. Queenstown, Tokyo, London"
                onKeyDown={e => { if (e.key === 'Enter') void build() }}
              />
            </label>
            <label className="pk-field">
              <span>Nights</span>
              <input
                type="number" min={1} max={60} value={nights}
                onChange={e => setNights(Math.max(1, Math.min(60, Number(e.target.value) || 1)))}
              />
            </label>
            <label className="pk-field">
              <span>Month</span>
              <select value={month} onChange={e => setMonth(e.target.value)}>
                <option value="">Not sure</option>
                {MONTHS.map(m => <option key={m} value={m}>{m}</option>)}
              </select>
            </label>
          </div>
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
        </>
      )}

      {error && <p className="pk-error">{error}</p>}

      {result && (
        <>
          <p className="pk-trip-line"><PhosphorIcon className="bi bi-geo-alt" /> {result.destination} · {result.nights} night{result.nights === 1 ? '' : 's'}{month ? ` · ${month}` : ''}</p>
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
