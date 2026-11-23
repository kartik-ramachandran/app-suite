import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import ExportButtons from '../components/ExportButtons'
import './snaptools.css'

interface Idea { name: string; why: string; priceHint?: string; whereToBuy?: string }

export default function GiftFinder() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('gift-finder')
  useLayoutEffect(() => { setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Gift Finder' }]); return () => setBreadcrumbs([]) }, [])

  const [recipient, setRecipient] = useState('')
  const [occasion, setOccasion] = useState('')
  const [budget, setBudget] = useState('')
  const [interests, setInterests] = useState('')
  const [stores, setStores] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [ideas, setIdeas] = useState<Idea[]>([])
  // Ideas are not stored server-side — export is the only way to keep the shortlist.
  const resultRef = useRef<HTMLDivElement>(null)

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph). Prefill only.
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/gift-finder')
    if (handoff?.input) setInterests(handoff.input)
  }, [])

  const go = async () => {
    if (!recipient.trim() && !interests.trim()) { setError('Tell me who it\'s for or their interests.'); return }
    setBusy(true); setError(''); setIdeas([])
    try { const { data } = await api.post('/planner/gifts', { recipient, occasion, budget, interests, stores }); setIdeas(data.ideas || []) }
    catch (e: unknown) { setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not build gift ideas.') }
    finally { setBusy(false) }
  }


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Gift Finder',
    busy: busy,
    error,
    count: ideas.length,
    noun: 'ideas',
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-gift" /> Gift Finder</h1>
        <p>Describe who it's for and I'll suggest thoughtful gift ideas with rough prices and where to buy them.</p>
      </div>
      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
      <AgentStage {...stageProps} model={field} />

      <div className="st-card">
        <div className="st-form">
          <div className="st-field2"><label>Who is it for?</label><input className="st-input" value={recipient} onChange={e => setRecipient(e.target.value)} placeholder="e.g. my dad, a coworker" /></div>
          <div className="st-field2"><label>Occasion</label><input className="st-input" value={occasion} onChange={e => setOccasion(e.target.value)} placeholder="birthday, anniversary…" /></div>
          <div className="st-field2"><label>Budget</label><input className="st-input" value={budget} onChange={e => setBudget(e.target.value)} placeholder="$50, under £30…" /></div>
          <div className="st-field2"><label>Interests</label><input className="st-input" value={interests} onChange={e => setInterests(e.target.value)} placeholder="cooking, cycling, sci-fi…" /></div>
        </div>
        <div className="st-field2" style={{ marginTop: 12 }}>
          <label>Preferred stores <span style={{ fontWeight: 400, opacity: 0.65 }}>(optional)</span></label>
          <textarea className="st-input st-textarea" rows={2} value={stores} onChange={e => setStores(e.target.value)} placeholder="Name shops you like and I'll point you there — e.g. Michael Hill, Zamel's, local Wellington boutiques, Etsy" />
        </div>
        <button className="st-btn st-btn-primary" style={{ marginTop: 14 }} onClick={go} disabled={busy}>{busy ? <><span className="st-spin" /> Finding gifts…</> : <><PhosphorIcon className="bi bi-stars" /> Find gifts</>}</button>
      </div>

      {ideas.length > 0 && (
        <>
          <div className="st-card-actions" style={{ marginTop: 16 }}>
            <ExportButtons
              title={`Gift ideas${recipient ? ` for ${recipient}` : ''}`}
              filenameBase="gift-ideas"
              subtitle={[occasion, budget].filter(Boolean).join(' · ') || undefined}
              size="sm"
              getBodyHtml={() => resultRef.current?.innerHTML}
            />
          </div>
          <div className="st-ideas" ref={resultRef}>
            {ideas.map((g, i) => (
              <div key={i} className="st-idea">
                <strong>{g.name}</strong>
                <p>{g.why}</p>
                <div className="meta">{[g.priceHint, g.whereToBuy].filter(Boolean).join(' · ')}</div>
              </div>
            ))}
          </div>
        </>
      )}
    </div>
  )
}
