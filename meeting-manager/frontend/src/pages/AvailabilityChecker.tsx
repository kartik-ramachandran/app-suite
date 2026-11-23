import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useLayoutEffect } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import api from '../services/api'
import '../App.css'

interface Slot {
  start: string; end: string; decision: string; score: number; reason: string
}

const DECISION_COLOR: Record<string, string> = {
  Available: '#22c55e',
  TentativelyAvailable: '#f59e0b',
  NotRecommended: '#f97316',
  Unavailable: '#ef4444',
}

const DECISION_ICON: Record<string, string> = {
  Available: 'bi-check-circle-fill',
  TentativelyAvailable: 'bi-exclamation-circle-fill',
  NotRecommended: 'bi-x-octagon',
  Unavailable: 'bi-x-circle-fill',
}

const DECISION_LABEL: Record<string, string> = {
  Available: 'Available',
  TentativelyAvailable: 'Tentatively Available',
  NotRecommended: 'Not Recommended',
  Unavailable: 'Unavailable',
}

export default function AvailabilityChecker() {
  const { setBreadcrumbs } = useLayoutControls()
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Calendar', path: '/calendar' }, { label: 'Availability Checker' }])
    return () => setBreadcrumbs([])
  }, [])

  const [mode, setMode] = useState<'check' | 'suggest'>('suggest')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')
  const [suggestedSlots, setSuggestedSlots] = useState<Slot[] | null>(null)
  const [checkResult, setCheckResult] = useState<any | null>(null)
  const [copied, setCopied] = useState(false)

  // Check specific slot
  const [checkStart, setCheckStart] = useState('')
  const [checkEnd, setCheckEnd] = useState('')
  const [meetingType, setMeetingType] = useState('')
  const [requiresPrep, setRequiresPrep] = useState(false)
  const [bufferMins, setBufferMins] = useState(15)

  // Suggest slots
  const [duration, setDuration] = useState(30)
  const [dateFrom, setDateFrom] = useState(() => new Date().toISOString().slice(0, 10))
  const [dateTo, setDateTo] = useState(() => {
    const d = new Date(); d.setDate(d.getDate() + 5); return d.toISOString().slice(0, 10)
  })

  const checkSlot = async () => {
    if (!checkStart || !checkEnd) { setError('Select a start and end time'); return }
    setLoading(true)
    setError('')
    setCheckResult(null)
    try {
      const tz = Intl.DateTimeFormat().resolvedOptions().timeZone
      const res = await api.post('/availability/check', {
        start: new Date(checkStart).toISOString(),
        end: new Date(checkEnd).toISOString(),
        timezone: tz, meetingType, requiresPrep, minimumBufferMinutes: bufferMins,
      })
      setCheckResult(res.data)
    } catch (e: any) { setError(e.response?.data?.message ?? 'Check failed') }
    finally { setLoading(false) }
  }

  const suggestSlots = async () => {
    setLoading(true)
    setError('')
    setSuggestedSlots(null)
    try {
      const tz = Intl.DateTimeFormat().resolvedOptions().timeZone
      const res = await api.post('/availability/suggest-slots', {
        durationMinutes: duration, dateFrom, dateTo,
        timezone: tz, meetingType, requiresPrep, minimumBufferMinutes: bufferMins, maxResults: 5,
      })
      setSuggestedSlots(res.data.recommendedSlots)
    } catch (e: any) { setError(e.response?.data?.message ?? 'Suggestion failed') }
    finally { setLoading(false) }
  }

  const copyReply = (text: string) => {
    navigator.clipboard.writeText(text)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }


  return (
    <div style={{ padding: '24px', maxWidth: 800, margin: '0 auto' }}>

      {/* Header */}
      <div style={{ marginBottom: 20 }}>
        <h1 style={{ fontSize: 22, fontWeight: 700, margin: '0 0 4px', display: 'flex', alignItems: 'center', gap: 8 }}>
          <PhosphorIcon className="bi bi-clock" style={{ color: 'var(--primary)' }} />Availability Checker
        </h1>
        <p style={{ color: 'var(--text-secondary)', fontSize: 13, margin: 0 }}>
          Smart slot checking that considers your meetings, buffer time, and working patterns.
        </p>
      </div>

      {/* Mode switch */}
      <div style={{ display: 'flex', gap: 4, marginBottom: 20, background: 'var(--bg-secondary)', borderRadius: 10, padding: 4, width: 'fit-content' }}>
        {(['suggest', 'check'] as const).map(m => (
          <button key={m} onClick={() => setMode(m)} style={{
            padding: '7px 18px', fontSize: 13, fontWeight: 600, borderRadius: 7, border: 'none', cursor: 'pointer',
            background: mode === m ? 'var(--bg-primary)' : 'transparent',
            color: mode === m ? 'var(--text-primary)' : 'var(--text-muted)',
            boxShadow: mode === m ? '0 1px 4px rgba(0,0,0,0.1)' : 'none',
          }}>
            {m === 'suggest' ? 'Find Best Slots' : 'Check Specific Slot'}
          </button>
        ))}
      </div>

      <div className="card" style={{ padding: '20px 24px', marginBottom: 20 }}>

        {/* Shared options */}
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 14, marginBottom: 16 }}>
          <div>
            <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', display: 'block', marginBottom: 4 }}>MEETING TYPE</label>
            <input className="form-input" style={{ fontSize: 13 }} placeholder="e.g. Architecture Review, 1:1, Sprint Planning" value={meetingType} onChange={e => setMeetingType(e.target.value)} />
          </div>
          <div>
            <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', display: 'block', marginBottom: 4 }}>BUFFER TIME (minutes)</label>
            <select className="form-select" style={{ fontSize: 13 }} value={bufferMins} onChange={e => setBufferMins(+e.target.value)}>
              {[0, 5, 10, 15, 20, 30].map(v => <option key={v} value={v}>{v} min</option>)}
            </select>
          </div>
        </div>

        <label style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 13, cursor: 'pointer', marginBottom: 16 }}>
          <input type="checkbox" checked={requiresPrep} onChange={e => setRequiresPrep(e.target.checked)} style={{ width: 14, height: 14 }} />
          <span style={{ fontWeight: 500 }}>This meeting requires preparation time</span>
        </label>

        {mode === 'check' && (
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 14, marginBottom: 16 }}>
            <div>
              <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', display: 'block', marginBottom: 4 }}>START</label>
              <input type="datetime-local" className="form-input" style={{ fontSize: 13 }} value={checkStart} onChange={e => setCheckStart(e.target.value)} />
            </div>
            <div>
              <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', display: 'block', marginBottom: 4 }}>END</label>
              <input type="datetime-local" className="form-input" style={{ fontSize: 13 }} value={checkEnd} onChange={e => setCheckEnd(e.target.value)} />
            </div>
          </div>
        )}

        {mode === 'suggest' && (
          <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 14, marginBottom: 16 }}>
            <div>
              <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', display: 'block', marginBottom: 4 }}>DURATION</label>
              <select className="form-select" style={{ fontSize: 13 }} value={duration} onChange={e => setDuration(+e.target.value)}>
                {[15, 30, 45, 60, 90, 120].map(v => <option key={v} value={v}>{v} min</option>)}
              </select>
            </div>
            <div>
              <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', display: 'block', marginBottom: 4 }}>FROM</label>
              <input type="date" className="form-input" style={{ fontSize: 13 }} value={dateFrom} onChange={e => setDateFrom(e.target.value)} />
            </div>
            <div>
              <label style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', display: 'block', marginBottom: 4 }}>TO</label>
              <input type="date" className="form-input" style={{ fontSize: 13 }} value={dateTo} onChange={e => setDateTo(e.target.value)} />
            </div>
          </div>
        )}

        {error && (
          <div style={{ padding: '8px 12px', background: '#ef444410', border: '1px solid #ef444430', borderRadius: 8, color: '#ef4444', fontSize: 13, marginBottom: 12 }}>
            <PhosphorIcon className="bi bi-exclamation-circle" style={{ marginRight: 6 }} />{error}
          </div>
        )}

        <button className="btn btn-primary" style={{ height: 38, fontSize: 14 }} onClick={mode === 'check' ? checkSlot : suggestSlots} disabled={loading}>
          {loading
            ? <><div className="spinner" />Analysing your calendar…</>
            : mode === 'check' ? <><PhosphorIcon className="bi bi-search" />Check This Slot</> : <><PhosphorIcon className="bi bi-stars" />Find Best Slots</>
          }
        </button>
      </div>

      {/* Check result */}
      {checkResult && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
          <div className="card" style={{ padding: '18px 20px', borderLeft: `4px solid ${DECISION_COLOR[checkResult.decision] ?? '#888'}` }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
              <PhosphorIcon className={`bi ${DECISION_ICON[checkResult.decision] ?? 'bi-info-circle'}`} style={{ color: DECISION_COLOR[checkResult.decision], fontSize: 18 }} />
              <span style={{ fontSize: 16, fontWeight: 700, color: DECISION_COLOR[checkResult.decision] }}>{DECISION_LABEL[checkResult.decision]}</span>
              <span style={{ marginLeft: 'auto', fontSize: 12, color: 'var(--text-muted)' }}>Score: {checkResult.score}/100</span>
            </div>
            <div style={{ fontSize: 13, color: 'var(--text-secondary)', lineHeight: 1.6 }}>{checkResult.reason}</div>
          </div>

          {checkResult.alternatives?.length > 0 && (
            <div>
              <div style={{ fontSize: 12, fontWeight: 700, color: 'var(--text-muted)', marginBottom: 8 }}>BETTER ALTERNATIVES</div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                {checkResult.alternatives.map((a: Slot, i: number) => (
                  <SlotCard key={i} slot={a} rank={i + 1} />
                ))}
              </div>
            </div>
          )}

          {checkResult.suggestedReply && (
            <div className="card" style={{ padding: '14px 16px' }}>
              <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 8 }}>
                <div style={{ fontSize: 12, fontWeight: 700, color: 'var(--text-muted)' }}>SUGGESTED REPLY</div>
                <button className="btn btn-secondary" style={{ fontSize: 11, height: 26 }} onClick={() => copyReply(checkResult.suggestedReply)}>
                  {copied ? <><PhosphorIcon className="bi bi-check" />Copied!</> : <><PhosphorIcon className="bi bi-clipboard" />Copy</>}
                </button>
              </div>
              <p style={{ fontSize: 13, color: 'var(--text-secondary)', margin: 0, lineHeight: 1.6, fontStyle: 'italic' }}>"{checkResult.suggestedReply}"</p>
            </div>
          )}
        </div>
      )}

      {/* Suggested slots */}
      {suggestedSlots && (
        <div>
          <div style={{ fontSize: 14, fontWeight: 700, marginBottom: 12 }}>
            {suggestedSlots.length > 0
              ? `${suggestedSlots.length} best slot${suggestedSlots.length !== 1 ? 's' : ''} found`
              : 'No suitable slots found in this range'
            }
          </div>
          <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {suggestedSlots.map((slot, i) => <SlotCard key={i} slot={slot} rank={i + 1} showCopy onCopy={copyReply} copied={copied && i === 0} />)}
          </div>
        </div>
      )}
    </div>
  )
}

function SlotCard({ slot, rank, showCopy, onCopy, copied }: { slot: Slot; rank: number; showCopy?: boolean; onCopy?: (t: string) => void; copied?: boolean }) {
  const color = DECISION_COLOR[slot.decision] ?? '#888'
  const icon = DECISION_ICON[slot.decision] ?? 'bi-circle'
  const d = new Date(slot.start)
  const end = new Date(slot.end)

  return (
    <div className="card" style={{ padding: '12px 16px', display: 'flex', alignItems: 'flex-start', gap: 12 }}>
      <div style={{ width: 28, height: 28, borderRadius: 8, background: `${color}15`, border: `1px solid ${color}40`, display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0, color, fontWeight: 700, fontSize: 13 }}>
        {rank}
      </div>
      <div style={{ flex: 1 }}>
        <div style={{ fontSize: 13, fontWeight: 700, marginBottom: 2 }}>
          {d.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric' })}
          {' '}
          {d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}
          {' — '}
          {end.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginBottom: 4 }}>
          <PhosphorIcon className={`bi ${icon}`} style={{ color, fontSize: 12 }} />
          <span style={{ fontSize: 11, fontWeight: 600, color }}>{DECISION_LABEL[slot.decision]}</span>
          <span style={{ fontSize: 11, color: 'var(--text-muted)' }}>· Score: {slot.score}</span>
        </div>
        <div style={{ fontSize: 12, color: 'var(--text-secondary)' }}>{slot.reason}</div>
      </div>
      {showCopy && onCopy && (
        <button className="btn btn-secondary" style={{ fontSize: 11, height: 26, flexShrink: 0 }} onClick={() => onCopy(`${d.toLocaleDateString('en-US', { weekday: 'long', month: 'long', day: 'numeric' })} at ${d.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })} works well — ${slot.reason}`)}>
          {copied ? <><PhosphorIcon className="bi bi-check" />Copied</> : <><PhosphorIcon className="bi bi-clipboard" />Copy reply</>}
        </button>
      )}
    </div>
  )
}
