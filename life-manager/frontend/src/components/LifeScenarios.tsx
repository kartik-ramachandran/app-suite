import { useCallback, useEffect, useState } from 'react'
import PhosphorIcon from './PhosphorIcon'
import {
  generateLifeScenarios, getLifeScenarios, pinLifeScenario, getLifeGuidance,
  type LifeScenario, type LifeGuidance,
} from '../services/api'
import ScenarioTimeline from './ScenarioTimeline'
import './LifeVoice.css'

type Horizon = '3-months' | '1-year' | '3-years'

const KIND: Record<LifeScenario['kind'], { label: string; icon: string }> = {
  'current-path': { label: 'The path you\'re on', icon: 'bi-signpost-2' },
  opportunity: { label: 'Opportunity', icon: 'bi-stars' },
  risk: { label: 'Risk', icon: 'bi-exclamation-triangle' },
  wildcard: { label: 'Wildcard', icon: 'bi-dice-5' },
}

/**
 * Likelihood shown as a band, never a bare number.
 *
 * The model is estimating from a handful of journal entries. Printing "68%"
 * implies a precision that does not exist, and people anchor hard on numbers
 * about their own future.
 */
const band = (n: number) =>
  n >= 70 ? 'Likely' : n >= 45 ? 'Plausible' : n >= 20 ? 'Possible' : 'Unlikely'

export default function LifeScenarios() {
  const [scenarios, setScenarios] = useState<LifeScenario[]>([])
  const [guidance, setGuidance] = useState<LifeGuidance | null>(null)
  const [horizon, setHorizon] = useState<Horizon>('1-year')
  const [focus, setFocus] = useState('')
  const [busy, setBusy] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [open, setOpen] = useState<number | null>(null)

  const load = useCallback(async () => {
    try {
      const res = await getLifeScenarios()
      setScenarios(res.data.scenarios)
    } catch { /* none yet */ }
  }, [])

  useEffect(() => { load() }, [load])

  const project = async () => {
    setBusy('Reading your life and projecting forward…')
    setError(null)
    try {
      const res = await generateLifeScenarios(horizon, focus.trim() || undefined)
      setScenarios(res.data.scenarios)
      setOpen(res.data.scenarios[0]?.id ?? null)
    } catch (e) {
      const ax = e as { response?: { data?: { error?: string } } }
      setError(ax.response?.data?.error ?? 'Could not project scenarios right now.')
    } finally { setBusy(null) }
  }

  const advise = async () => {
    setBusy('Working out what matters next…')
    setError(null)
    try {
      const res = await getLifeGuidance(focus.trim() || undefined)
      setGuidance(res.data)
    } catch (e) {
      const ax = e as { response?: { data?: { error?: string } } }
      setError(ax.response?.data?.error ?? 'Could not generate guidance right now.')
    } finally { setBusy(null) }
  }

  const pin = async (s: LifeScenario) => {
    try {
      const res = await pinLifeScenario(s.id)
      setScenarios(prev => prev.map(p => p.id === s.id ? { ...p, isPinned: res.data.isPinned } : p))
    } catch { /* non-critical */ }
  }

  return (
    <div className="lv-root">
      <div className="ls-controls">
        <div className="ls-horizons" role="group" aria-label="Time horizon">
          {(['3-months', '1-year', '3-years'] as Horizon[]).map(h => (
            <button key={h} className={horizon === h ? 'ls-h ls-on' : 'ls-h'} onClick={() => setHorizon(h)}>
              {h.replace('-', ' ')}
            </button>
          ))}
        </div>

        <input
          className="ls-focus"
          value={focus}
          onChange={e => setFocus(e.target.value)}
          placeholder="Anything specific on your mind? (optional)"
        />

        <div className="ls-actions">
          <button className="lv-rec" onClick={project} disabled={!!busy}>
            <PhosphorIcon className="bi bi-diagram-3" /> Show me the possibilities
          </button>
          <button className="lv-ghost" onClick={advise} disabled={!!busy}>
            <PhosphorIcon className="bi bi-compass" /> What's next?
          </button>
        </div>
      </div>

      {busy && <div className="lv-busy"><span className="lv-spin" />{busy}</div>}
      {error && <div className="lv-error"><PhosphorIcon className="bi bi-exclamation-triangle" />{error}</div>}

      {/* ── Guidance ─────────────────────────────────────────────────── */}
      {guidance && (
        <div className="ls-guide">
          <h4><PhosphorIcon className="bi bi-compass" /> Where you stand</h4>
          <p className="ls-read">{guidance.read}</p>

          {guidance.nextActions.length > 0 && (
            <>
              <h5>Do this next</h5>
              <ol className="ls-actions-list">
                {guidance.nextActions.map((a, i) => <li key={i}>{a}</li>)}
              </ol>
            </>
          )}

          {guidance.patterns.length > 0 && (
            <>
              <h5>Patterns worth noticing</h5>
              <ul className="ls-bullets">{guidance.patterns.map((p, i) => <li key={i}>{p}</li>)}</ul>
            </>
          )}

          {guidance.questions.length > 0 && (
            <>
              <h5>Only you can answer</h5>
              <ul className="ls-bullets ls-q">{guidance.questions.map((q, i) => <li key={i}>{q}</li>)}</ul>
            </>
          )}

          {guidance.caution && (
            <div className="ls-caution"><PhosphorIcon className="bi bi-shield-exclamation" />{guidance.caution}</div>
          )}
        </div>
      )}

      {/* ── Timeline: the fork at a glance, before any reading ───────── */}
      {scenarios.length > 0 && (
        <ScenarioTimeline
          scenarios={scenarios}
          horizon={scenarios[0]?.horizon ?? horizon}
          selectedId={open}
          onSelect={s => setOpen(open === s.id ? null : s.id)}
        />
      )}

      {/* ── Scenarios ────────────────────────────────────────────────── */}
      {scenarios.length > 0 && (
        <div className="ls-grid">
          {scenarios.map(s => {
            const meta = KIND[s.kind] ?? KIND.opportunity
            const isOpen = open === s.id
            return (
              <article key={s.id} className={`ls-card ls-${s.kind}${isOpen ? ' ls-open' : ''}`}>
                <header onClick={() => setOpen(isOpen ? null : s.id)}>
                  <div className="ls-kind"><PhosphorIcon className={`bi ${meta.icon}`} />{meta.label}</div>
                  <h4>{s.title}</h4>
                  <div className="ls-likely">
                    <span className="ls-band">{band(s.likelihood)}</span>
                    <div className="ls-bar"><i style={{ width: `${s.likelihood}%` }} /></div>
                  </div>
                </header>

                <p className="ls-summary">{s.summary}</p>

                {s.areas.length > 0 && (
                  <div className="lv-tags">{s.areas.map((a, i) => <span key={i} className="lv-tag">{a}</span>)}</div>
                )}

                {isOpen && (
                  <div className="ls-detail">
                    {s.drivers.length > 0 && (
                      <section>
                        <h5>Why this is on the table</h5>
                        <ul>{s.drivers.map((d, i) => <li key={i}>{d}</li>)}</ul>
                      </section>
                    )}
                    {s.actions.length > 0 && (
                      <section>
                        <h5>What moves it</h5>
                        <ul>{s.actions.map((a, i) => <li key={i}>{a}</li>)}</ul>
                      </section>
                    )}
                    {s.signals.length > 0 && (
                      <section>
                        <h5>Early signs</h5>
                        <ul>{s.signals.map((x, i) => <li key={i}>{x}</li>)}</ul>
                      </section>
                    )}
                    {s.assumptions.length > 0 && (
                      <section>
                        <h5>Assuming</h5>
                        <ul className="ls-assume">{s.assumptions.map((a, i) => <li key={i}>{a}</li>)}</ul>
                      </section>
                    )}
                  </div>
                )}

                <footer>
                  <button className="lv-link" onClick={() => setOpen(isOpen ? null : s.id)}>
                    {isOpen ? 'Less' : 'What would make this happen?'}
                  </button>
                  <button className={`ls-pin${s.isPinned ? ' ls-pinned' : ''}`} onClick={() => pin(s)}
                          aria-label={s.isPinned ? 'Unpin' : 'Pin'}>
                    <PhosphorIcon className={`bi ${s.isPinned ? 'bi-pin-angle-fill' : 'bi-pin-angle'}`} />
                  </button>
                </footer>
              </article>
            )
          })}
        </div>
      )}

      {scenarios.length === 0 && !busy && !guidance && (
        <div className="ls-empty">
          <PhosphorIcon className="bi bi-diagram-3" />
          <p>Record a few voice entries, then project them forward to see where
             the paths in front of you actually lead.</p>
        </div>
      )}
    </div>
  )
}
