import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import ExportButtons from '../components/ExportButtons'
import api from '../services/api'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import './snaptools.css'

interface Plan {
  overview: string
  checklist: string[]
  timeline: { when: string; what: string }[]
  budget: { item: string; estimate: string }[]
  tips: string[]
}

export default function EventPlanner() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('event-planner')
  useLayoutEffect(() => { setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Event Planner' }]); return () => setBreadcrumbs([]) }, [])

  const [eventType, setEventType] = useState('')
  const [date, setDate] = useState('')
  const [guests, setGuests] = useState('')
  const [budget, setBudget] = useState('')
  const [notes, setNotes] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [plan, setPlan] = useState<Plan | null>(null)
  const exportRef = useRef<HTMLDivElement>(null)

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph). Prefill only.
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/event-planner')
    if (handoff?.input) setEventType(handoff.input)
  }, [])

  const go = async () => {
    if (!eventType.trim()) { setError('What kind of event are you planning?'); return }
    setBusy(true); setError(''); setPlan(null)
    try { const { data } = await api.post('/planner/event', { eventType, date, guests, budget, notes }); setPlan(data) }
    catch (e: unknown) { setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not build the plan.') }
    finally { setBusy(false) }
  }


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Event Planner',
    busy: busy,
    error,
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-calendar2-heart" /> Event Planner</h1>
        <p>Tell me about your event and I'll build a checklist, a timeline, and a rough budget to keep it on track.</p>
      </div>
      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
      <AgentStage {...stageProps} model={field} />

      <div className="st-card">
        <div className="st-form">
          <div className="st-field2 full"><label>Event</label><input className="st-input" value={eventType} onChange={e => setEventType(e.target.value)} placeholder="birthday party, wedding, team offsite…" /></div>
          <div className="st-field2"><label>Date</label><input className="st-input" value={date} onChange={e => setDate(e.target.value)} placeholder="e.g. Sep 20, or 6 weeks out" /></div>
          <div className="st-field2"><label>Guests</label><input className="st-input" value={guests} onChange={e => setGuests(e.target.value)} placeholder="e.g. 25" /></div>
          <div className="st-field2"><label>Budget</label><input className="st-input" value={budget} onChange={e => setBudget(e.target.value)} placeholder="e.g. $1,000" /></div>
          <div className="st-field2"><label>Notes</label><input className="st-input" value={notes} onChange={e => setNotes(e.target.value)} placeholder="theme, must-haves…" /></div>
        </div>
        <button className="st-btn st-btn-primary" style={{ marginTop: 14 }} onClick={go} disabled={busy}>{busy ? <><span className="st-spin" /> Planning…</> : <><PhosphorIcon className="bi bi-stars" /> Build the plan</>}</button>
      </div>

      {plan && (
        <div className="st-card">
          <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 10 }}>
            <ExportButtons
              title={eventType.trim() || 'Event Plan'}
              filenameBase="event-plan"
              subtitle="GenPrompt Event Planner"
              getBodyHtml={() => exportRef.current?.innerHTML}
              size="sm"
            />
          </div>
          <div ref={exportRef}>
            {plan.overview && <p className="st-muted" style={{ marginTop: 0 }}>{plan.overview}</p>}
            {plan.checklist?.length > 0 && <div className="st-section"><h3><PhosphorIcon className="bi bi-check2-square" /> Checklist</h3>{plan.checklist.map((c, i) => <div key={i} className="wb-check"><PhosphorIcon className="bi bi-square" /> {c}</div>)}</div>}
            {plan.timeline?.length > 0 && <div className="st-section"><h3><PhosphorIcon className="bi bi-clock-history" /> Timeline</h3>{plan.timeline.map((t, i) => <div key={i} className="st-timeline-row"><span>{t.when}</span><span>{t.what}</span></div>)}</div>}
            {plan.budget?.length > 0 && <div className="st-section"><h3><PhosphorIcon className="bi bi-cash-coin" /> Budget</h3>{plan.budget.map((b, i) => <div key={i} className="st-timeline-row"><span>{b.item}</span><span>{b.estimate}</span></div>)}</div>}
            {plan.tips?.length > 0 && <div className="st-section"><h3><PhosphorIcon className="bi bi-lightbulb" /> Tips</h3><ul>{plan.tips.map((t, i) => <li key={i}>{t}</li>)}</ul></div>}
          </div>
        </div>
      )}
    </div>
  )
}
