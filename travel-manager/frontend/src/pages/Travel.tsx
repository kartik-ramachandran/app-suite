import PhosphorIcon from '../components/PhosphorIcon'
import PackingList from '../components/PackingList'
import { useEffect, useLayoutEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useLayoutControls } from '../components/Layout'
import api from '../services/api'
import './Travel.css'

interface WeatherDay {
  date: string
  tempMaxC: number
  tempMinC: number
  precipMm: number
  summary: string
}
interface TravelGuide {
  destination: string
  country?: string | null
  startDate: string
  endDate: string
  weatherKind: string
  weatherHeadline: string
  weather: WeatherDay[]
  overview: string
  thingsToDo: string[]
  neighborhoods: string[]
  packing: string[]
  localDress: string[]
  food: string[]
  localTips: string[]
  gettingAround: string
  disclaimer: string
}

interface FlightMonth { month: string; monthLabel: string; price: number; airline?: string | null }
interface FlightInsight {
  available: boolean
  message?: string | null
  originCode?: string | null
  originName?: string | null
  destinationCode?: string | null
  destinationName?: string | null
  currency: string
  months: FlightMonth[]
  cheapest?: FlightMonth | null
  priciest?: FlightMonth | null
  savingsPercent?: number | null
  suggestion?: string | null
}

interface SavedGuideSummary { id: number; destination: string; startDate?: string; endDate?: string; createdAt: string }

const TRIP_TYPES = ['Leisure', 'Business', 'Family', 'Adventure / outdoors', 'Romantic', 'Backpacking', 'Foodie']

const toDateInput = (d: Date) => {
  const p = (n: number) => n.toString().padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`
}
const defaultStart = () => { const d = new Date(); d.setDate(d.getDate() + 14); return toDateInput(d) }
const defaultEnd = () => { const d = new Date(); d.setDate(d.getDate() + 18); return toDateInput(d) }

const dayLabel = (iso: string) => {
  const d = new Date(iso + 'T00:00:00')
  return d.toLocaleDateString('en-US', { weekday: 'short', day: 'numeric', month: 'short' })
}


export default function Travel() {
  const { setBreadcrumbs } = useLayoutControls()
  const [params] = useSearchParams()

  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Travel Manager', path: '/travel' }, { label: 'Travel Intelligence' }])
    return () => setBreadcrumbs([])
  }, [])

  const [destination, setDestination] = useState(params.get('destination') ?? '')
  const [startDate, setStartDate] = useState(params.get('from') ?? defaultStart())
  const [endDate, setEndDate] = useState(params.get('to') ?? defaultEnd())
  const [tripType, setTripType] = useState('Leisure')
  const [dietary, setDietary] = useState('')
  const [notes, setNotes] = useState('')

  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [guide, setGuide] = useState<TravelGuide | null>(null)
  const [savedGuides, setSavedGuides] = useState<SavedGuideSummary[]>([])

  // Best time to fly (part of the same form — "Flying from")
  const [origin, setOrigin] = useState('')
  const [fpLoading, setFpLoading] = useState(false)
  const [fpError, setFpError] = useState('')
  const [fp, setFp] = useState<FlightInsight | null>(null)

  const loadSaved = async () => {
    try { const { data } = await api.get('/travel/guides'); setSavedGuides(data) } catch { /* ignore */ }
  }

  const openSaved = async (id: number) => {
    try {
      const { data } = await api.get(`/travel/guides/${id}`)
      setGuide(data); setError('')
    } catch { setError('Could not open that saved plan.') }
  }

  // Prefill dietary preferences + load saved plans.
  useEffect(() => {
    api.get('/travel/preferences')
      .then(r => setDietary(r.data.dietaryPreferences ?? ''))
      .catch(() => {})
    void loadSaved()
  }, [])

  const generate = async () => {
    if (!destination.trim()) { setError('Enter a destination.'); return }
    setLoading(true); setError(''); setGuide(null); setFp(null); setFpError('')
    const dest = destination.trim()

    // If they've said where they're flying from, look up fares for that route in parallel.
    const flightsP = origin.trim()
      ? (async () => {
          setFpLoading(true)
          try {
            const { data } = await api.post('/travel/flight-prices', { origin: origin.trim(), destination: dest })
            setFp(data)
            if (!data.available && data.message) setFpError(data.message)
          } catch { /* fares are a bonus — never block the guide */ }
          finally { setFpLoading(false) }
        })()
      : Promise.resolve()

    try {
      const { data } = await api.post('/travel/guide', {
        destination: dest, startDate, endDate, tripType,
        notes: notes.trim() || null, dietaryPreferences: dietary.trim() || null,
      })
      setGuide(data)
      void loadSaved()
    } catch (e: any) {
      setError(e.response?.data?.message ?? 'Could not build the travel guide. Please try again.')
    } finally {
      setLoading(false)
    }
    await flightsP
  }

  const slug = (s: string) => (s || 'travel').replace(/[^a-z0-9]+/gi, '-').replace(/^-|-$/g, '').toLowerCase()

  const buildGuideHtml = (g: TravelGuide): string => {
    const esc = (s?: string | null) => (s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    const list = (title: string, items: string[]) => items?.length
      ? `<h2>${esc(title)}</h2><ul>${items.map(i => `<li>${esc(i)}</li>`).join('')}</ul>` : ''
    const weather = g.weather?.length
      ? `<h2>Weather (${esc(g.weatherKind)})</h2><p>${esc(g.weatherHeadline)}</p><table><thead><tr><th>Date</th><th>High</th><th>Low</th><th>Conditions</th><th>Rain</th></tr></thead><tbody>${g.weather.map(w => `<tr><td>${esc(w.date)}</td><td>${Math.round(w.tempMaxC)}°C</td><td>${Math.round(w.tempMinC)}°C</td><td>${esc(w.summary)}</td><td>${w.precipMm}mm</td></tr>`).join('')}</tbody></table>` : ''
    return `<!doctype html><html><head><meta charset="utf-8"><title>Travel guide — ${esc(g.destination)}</title><style>
      body{font-family:Arial,Helvetica,sans-serif;max-width:820px;margin:0 auto;color:#111827;line-height:1.55;padding:24px}
      h1{font-size:28px;margin:0 0 4px}
      .sub{color:#64748b;margin:0 0 18px}
      h2{font-size:18px;margin:22px 0 8px;color:#0f172a;border-bottom:1px solid #e2e8f0;padding-bottom:4px}
      ul{padding-left:20px;margin:6px 0}li{margin:3px 0}
      table{width:100%;table-layout:fixed;border-collapse:collapse;margin:8px 0}
      th,td{border:1px solid #cbd5e1;padding:6px 9px;text-align:left;font-size:13px;word-break:break-word}th{background:#f1f5f9}
      .disc{margin-top:22px;font-size:12px;color:#64748b;border:1px dashed #cbd5e1;border-radius:8px;padding:12px}
    </style></head><body>
      <h1>${esc(g.destination)}</h1>
      <p class="sub">${esc(g.startDate)} → ${esc(g.endDate)}</p>
      ${g.overview ? `<p>${esc(g.overview)}</p>` : ''}
      ${weather}
      ${list('Things to do', g.thingsToDo)}
      ${list('Where to explore', g.neighborhoods)}
      ${list('What to pack', g.packing)}
      ${list('Local dress &amp; colours', g.localDress)}
      ${list('Food to try', g.food)}
      ${list('Local tips', g.localTips)}
      ${g.gettingAround ? `<h2>Getting around</h2><p>${esc(g.gettingAround)}</p>` : ''}
      <div class="disc">${esc(g.disclaimer)}</div>
    </body></html>`
  }

  const downloadHtml = () => {
    if (!guide) return
    const blob = new Blob([buildGuideHtml(guide)], { type: 'text/html' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url; a.download = `${slug(guide.destination)}-travel-guide.html`
    a.click(); URL.revokeObjectURL(url)
  }

  return (
    <div className="travel-page">
      <div className="travel-hero">
        <div className="travel-hero-icon"><PhosphorIcon className="bi bi-compass" /></div>
        <div>
          <h1>Travel Intelligence</h1>
          <p>Turn any trip into a complete guide — real weather, what to pack, places to go, and food tuned to your tastes.</p>
        </div>
      </div>

      {savedGuides.length > 0 && (
        <div className="travel-saved">
          <span className="travel-saved-label"><PhosphorIcon className="bi bi-bookmark-star" /> Recent plans</span>
          {savedGuides.map(g => (
            <button key={g.id} className="travel-saved-chip" onClick={() => openSaved(g.id)} title={`Open ${g.destination}`}>
              <strong>{g.destination}</strong>
              {g.startDate && <span>{new Date(g.startDate + 'T00:00:00').toLocaleDateString('en-US', { month: 'short', day: 'numeric' })}</span>}
            </button>
          ))}
        </div>
      )}

      <div className="travel-card travel-form">
        <div className="travel-form-grid">
          <label className="travel-field">
            <span>Flying from <em>(optional — for fares)</em></span>
            <input value={origin} onChange={e => setOrigin(e.target.value)}
              placeholder="Auckland, London…" onKeyDown={e => e.key === 'Enter' && generate()} />
          </label>
          <label className="travel-field">
            <span>Destination</span>
            <input value={destination} onChange={e => setDestination(e.target.value)}
              placeholder="Tokyo, Lisbon…" onKeyDown={e => e.key === 'Enter' && generate()} />
          </label>
          <label className="travel-field">
            <span>From</span>
            <input type="date" value={startDate} onChange={e => setStartDate(e.target.value)} />
          </label>
          <label className="travel-field">
            <span>To</span>
            <input type="date" value={endDate} onChange={e => setEndDate(e.target.value)} />
          </label>
          <label className="travel-field">
            <span>Trip type</span>
            <select value={tripType} onChange={e => setTripType(e.target.value)}>
              {TRIP_TYPES.map(t => <option key={t} value={t}>{t}</option>)}
            </select>
          </label>
          <label className="travel-field travel-col-3">
            <span>Dietary / food preferences <em>(remembered for next time)</em></span>
            <input value={dietary} onChange={e => setDietary(e.target.value)}
              placeholder="e.g. vegetarian, no pork, loves seafood, gluten-free" />
          </label>
          <label className="travel-field travel-col-4">
            <span>Anything specific? <em>(optional)</em></span>
            <input value={notes} onChange={e => setNotes(e.target.value)}
              placeholder="Traveling with kids, on a budget, first time there…" />
          </label>
        </div>
        {error && <div className="travel-error"><PhosphorIcon className="bi bi-exclamation-circle" /> {error}</div>}
        <button className="travel-btn" onClick={generate} disabled={loading}>
          {loading ? <><span className="travel-spinner" /> Building your guide…</> : <><PhosphorIcon className="bi bi-stars" /> Generate travel guide</>}
        </button>
        <p className="travel-form-hint">
          <PhosphorIcon className="bi bi-airplane-engines" /> Add <strong>Flying from</strong> to also get the cheapest months to fly this route.
        </p>
      </div>

      {loading && (
        <div className="travel-card travel-loading">
          <span className="travel-spinner travel-spinner-lg" />
          <div>{fpLoading ? 'Checking weather, fares, and mapping out ' : 'Checking the weather and mapping out '}{destination.trim() || 'your destination'}…</div>
        </div>
      )}

      {fpError && !fpLoading && (
        <div className="travel-card"><div className="travel-error"><PhosphorIcon className="bi bi-exclamation-circle" /> {fpError}</div></div>
      )}

      {fp && fp.available && fp.cheapest && (
        <div className="travel-card travel-fp-result-card">
          <h2 className="travel-fp-rh"><PhosphorIcon className="bi bi-airplane-engines" /> Best time to fly</h2>
          <div className="travel-fp-result">
            <div className="travel-fp-route">
              {fp.originName} ({fp.originCode}) <PhosphorIcon className="bi bi-arrow-right" /> {fp.destinationName} ({fp.destinationCode})
            </div>
            <div className="travel-fp-months">
              {fp.months.map(m => {
                const max = Math.max(...fp.months.map(x => x.price))
                const isCheapest = m.month === fp.cheapest!.month
                return (
                  <div key={m.month} className={`travel-fp-month${isCheapest ? ' cheapest' : ''}`}>
                    <div className="travel-fp-bar-wrap"><div className="travel-fp-bar" style={{ height: `${Math.max(10, (m.price / max) * 100)}%` }} /></div>
                    <div className="travel-fp-price">{fp.currency} {m.price}</div>
                    <div className="travel-fp-mlabel">{m.monthLabel}</div>
                  </div>
                )
              })}
            </div>
            {fp.suggestion && <p className="travel-fp-suggestion"><PhosphorIcon className="bi bi-lightbulb" /> {fp.suggestion}</p>}
            <p className="travel-fp-note">Cached fares from Travelpayouts — indicative, not live bookable prices. Verify on the airline/booking site.</p>
          </div>
        </div>
      )}

      {guide && (
        <div className="travel-guide">
          <div className="travel-card travel-guide-head">
            <div>
              <h2>{guide.destination}</h2>
              <div className="travel-meta">
                <span><PhosphorIcon className="bi bi-calendar3" /> {dayLabel(guide.startDate)} – {dayLabel(guide.endDate)}</span>
                {guide.weatherKind !== 'unavailable' && (
                  <span className={`travel-badge travel-badge-${guide.weatherKind}`}>
                    <PhosphorIcon className="bi bi-cloud-sun" /> {guide.weatherKind === 'forecast' ? 'Forecast' : 'Seasonal estimate'}
                  </span>
                )}
              </div>
              <p className="travel-weather-headline">{guide.weatherHeadline}</p>
            </div>
            <div className="travel-guide-actions">
              <button className="travel-guide-dl" onClick={downloadHtml}>
                <PhosphorIcon className="bi bi-filetype-html" /> HTML
              </button>
            </div>
          </div>

          {guide.weather.length > 0 && (
            <div className="travel-weather-strip">
              {guide.weather.map(w => (
                <div key={w.date} className="travel-weather-day">
                  <div className="travel-weather-date">{dayLabel(w.date)}</div>
                  <div className="travel-weather-temp">{Math.round(w.tempMaxC)}°<span>/{Math.round(w.tempMinC)}°</span></div>
                  <div className="travel-weather-sum">{w.summary}</div>
                  {w.precipMm >= 1 && <div className="travel-weather-rain"><PhosphorIcon className="bi bi-droplet" /> {w.precipMm}mm</div>}
                </div>
              ))}
            </div>
          )}

          {guide.overview && (
            <div className="travel-card">
              <p className="travel-overview">{guide.overview}</p>
            </div>
          )}

          <div className="travel-columns">
            <Section icon="bi-geo-alt" title="Things to do" items={guide.thingsToDo} />
            <Section icon="bi-signpost-2" title="Where to explore" items={guide.neighborhoods} />
            <Section icon="bi-bag-check" title="What to pack" items={guide.packing} accent />
            <Section icon="bi-palette" title="Local dress & colours" items={guide.localDress} accent
              footer={<span className="travel-food-note"><PhosphorIcon className="bi bi-info-circle" /> Colours & styles locals consider appropriate — dress rules can be stricter at religious/formal sites.</span>} />

            <Section icon="bi-cup-hot" title="Food to try" items={guide.food} accent
              footer={<span className="travel-food-note"><PhosphorIcon className="bi bi-info-circle" /> Based on your dietary preferences{dietary.trim() ? `: ${dietary.trim()}` : ' (add some for better picks)'}.</span>} />
            <Section icon="bi-lightbulb" title="Local tips" items={guide.localTips} />
            {guide.gettingAround && (
              <div className="travel-card travel-section">
                <h3><PhosphorIcon className="bi bi-signpost-split" /> Getting around</h3>
                <p>{guide.gettingAround}</p>
              </div>
            )}
          </div>

          <div className="travel-disclaimer">
            <PhosphorIcon className="bi bi-shield-exclamation" /> {guide.disclaimer}
          </div>

          {/* Travel knows where and when; Wardrobe knows what you own.
              Neither can build a real packing list alone. */}
          <PackingList
            destination={guide.destination}
            nights={Math.max(1, Math.round(
              (new Date(guide.endDate).getTime() - new Date(guide.startDate).getTime()) / 86400000))}
            month={new Date(guide.startDate).toLocaleString('en', { month: 'long' })}
          />
        </div>
      )}
    </div>
  )
}

function Section({ icon, title, items, accent, footer }: {
  icon: string; title: string; items: string[]; accent?: boolean; footer?: React.ReactNode
}) {
  if (!items || items.length === 0) return null
  return (
    <div className={`travel-card travel-section${accent ? ' travel-section-accent' : ''}`}>
      <h3><PhosphorIcon className={`bi ${icon}`} /> {title}</h3>
      <ul>{items.map((it, i) => <li key={i}><SectionItem text={it} /></li>)}</ul>
      {footer}
    </div>
  )
}

// Items arrive as "Name — detail"; bold the name so the list scans like a mini-directory.
function SectionItem({ text }: { text: string }) {
  const sep = text.includes(' — ') ? ' — ' : text.includes(' - ') ? ' - ' : null
  if (!sep) return <>{text}</>
  const idx = text.indexOf(sep)
  const name = text.slice(0, idx)
  if (name.length > 60) return <>{text}</>
  return <><strong>{name}</strong>{' — '}{text.slice(idx + sep.length)}</>
}
