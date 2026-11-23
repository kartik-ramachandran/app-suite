// The Gamefield view. three.js paints the deck; everything textual — nameplates,
// speech bubbles, the activity ticker, the inspector — is DOM on top of it, so
// it stays crisp, selectable and themed with the rest of the site.

import { useEffect, useMemo, useRef, useState } from 'react'
import PhosphorIcon from '../components/PhosphorIcon'
import type { FieldAction, FieldAgent, FieldModel } from './types'
import type { FloorHandle, FloorSlot } from './OfficeFloor'
import './GameField.css'

const STATUS_LABEL: Record<FieldAgent['status'], string> = {
  idle: 'Idle',
  walking: 'Heading over',
  working: 'Working',
  blocked: 'Needs you',
  done: 'Done',
  failed: 'Failed',
}

const STATUS_ICON: Record<FieldAgent['status'], string> = {
  idle: 'bi-cup-hot',
  walking: 'bi-person-walking',
  working: 'bi-keyboard',
  blocked: 'bi-hand-index-thumb',
  done: 'bi-check-circle-fill',
  failed: 'bi-x-circle-fill',
}

function useDarkMode(): boolean {
  const [dark, setDark] = useState(() => document.body.classList.contains('dark-mode'))
  useEffect(() => {
    const obs = new MutationObserver(() => setDark(document.body.classList.contains('dark-mode')))
    obs.observe(document.body, { attributes: true, attributeFilter: ['class'] })
    return () => obs.disconnect()
  }, [])
  return dark
}

export interface GameFieldProps {
  model: FieldModel
  /** Height of the floor viewport. Defaults to a comfortable desktop size. */
  height?: number
  /**
   * The page's primary controls, docked into the floor's toolbar. Without these
   * the field would be a view you can only watch, so pages that hide a panel
   * behind the toggle should pass whatever that panel let you press.
   */
  actions?: FieldAction[]
  /** Rendered under the floor, e.g. a page's own inline form. */
  children?: React.ReactNode
}

function ActionButton({ action }: { action: FieldAction }) {
  return (
    <button
      type="button"
      className={`gf-action gf-action-${action.tone || 'default'}`}
      onClick={action.onClick}
      disabled={action.disabled}
    >
      {action.icon && <PhosphorIcon className={`bi ${action.icon}`} />}
      {action.label}
    </button>
  )
}

export default function GameField({ model, height, actions, children }: GameFieldProps) {
  const hostRef = useRef<HTMLDivElement>(null)
  const floorRef = useRef<FloorHandle | null>(null)
  const [slots, setSlots] = useState<FloorSlot[]>([])
  const [roomHeight, setRoomHeight] = useState<number | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)
  const [ready, setReady] = useState(false)
  const dark = useDarkMode()

  // Keep the newest model available to the async boot without re-running it.
  const modelRef = useRef(model)
  modelRef.current = model

  // A phone gets a taller budget: two columns of legible desks beats six rows
  // of one-pixel people.
  const narrow = typeof window !== 'undefined' && window.innerWidth <= 768
  const maxHeightRef = useRef(height ?? (narrow ? 780 : 560))
  maxHeightRef.current = height ?? (narrow ? 780 : 560)

  useEffect(() => {
    let disposed = false
    const host = hostRef.current
    if (!host) return

    import('./OfficeFloor')
      .then(({ createFloor }) => createFloor(host, {
        dark,
        maxHeight: maxHeightRef.current,
        onLayout: (next, contentHeight) => {
          setSlots(next)
          setRoomHeight(contentHeight)
        },
        shouldAbort: () => disposed,
      }))
      .then(handle => {
        if (!handle) return
        if (disposed) {
          handle.destroy()
          return
        }
        floorRef.current = handle
        handle.update(modelRef.current)
        lastSig.current = null
        setReady(true)
      })
      .catch(err => {
        console.error('Gamefield could not start', err)
        if (!disposed) setFailed(true)
      })

    return () => {
      disposed = true
      floorRef.current?.destroy()
      floorRef.current = null
    }
    // The floor is created once; theme changes go through setTheme below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Pages rebuild their model object on every render, but the floor only cares
  // about a few fields of it. Driving the scene from an unchanged model was the bulk
  // of the work this component did: desk text and bubbles are DOM and re-render
  // regardless, so the scene only needs waking when the scene actually changed.
  const sceneSig = useMemo(() => {
    const desks = model.agents
      .map(a => `${a.id}:${a.status}:${a.progress ?? ''}:${a.boss ? 1 : 0}:${a.accent ?? ''}`)
      .join('|')
    const posts = (model.messages || []).map(m => m.id).join(',')
    return `${desks}#${posts}#${model.running ? 1 : 0}`
  }, [model.agents, model.messages, model.running])

  const lastSig = useRef<string | null>(null)
  useEffect(() => {
    if (lastSig.current === sceneSig) return
    lastSig.current = sceneSig
    floorRef.current?.update(model)
    // `model` is read through the signature; adding it here would defeat the gate.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sceneSig])

  useEffect(() => {
    floorRef.current?.setTheme(dark)
  }, [dark])

  // Selection is drawn in the scene, not as an overlay box — see the note on
  // .gf-hit in GameField.css.
  useEffect(() => {
    floorRef.current?.setSelected(selected)
  }, [selected, ready])

  const byId = useMemo(() => new Map(model.agents.map(a => [a.id, a])), [model.agents])

  // Bubbles are the first thing to overcrowd a narrow floor, so only the most
  // interesting few get one: whoever needs you first, then whatever failed,
  // then whoever is still typing.
  const bubbleIds = useMemo(() => {
    const rank = (s2: FieldAgent['status']) => (s2 === 'blocked' ? 0 : s2 === 'failed' ? 1 : 2)
    return new Set(
      model.agents
        .filter(a => a.detail && (a.status === 'working' || a.status === 'blocked' || a.status === 'failed'))
        .sort((a, b) => rank(a.status) - rank(b.status))
        .slice(0, 3)
        .map(a => a.id),
    )
  }, [model.agents])
  const chosen = selected ? byId.get(selected) : undefined
  const events = useMemo(() => (model.events || []).slice(-40).reverse(), [model.events])

  const counts = useMemo(() => {
    const c = { working: 0, blocked: 0, done: 0, failed: 0 }
    for (const a of model.agents) {
      if (a.status === 'working' || a.status === 'walking') c.working++
      else if (a.status === 'blocked') c.blocked++
      else if (a.status === 'done') c.done++
      else if (a.status === 'failed') c.failed++
    }
    return c
  }, [model.agents])

  // The renderer reports the height the room actually needs, already inside the
  // budget it was given.
  const floorHeight = Math.round(Math.max(240, roomHeight ?? Math.min(maxHeightRef.current, 420)))

  if (failed) {
    return (
      <div className="gf-shell gf-shell-error">
        <PhosphorIcon className="bi bi-exclamation-triangle" />
        <div>
          <strong>The floor could not open.</strong>
          <p>This view needs WebGL, which this browser or device has turned off. Switch back to the panel view to keep working.</p>
        </div>
      </div>
    )
  }

  return (
    <div className="gf-shell">
      <div className="gf-topbar">
        <div className="gf-title">
          <PhosphorIcon className="bi bi-buildings" />
          <div>
            <strong>{model.title}</strong>
            {model.subtitle && <span>{model.subtitle}</span>}
          </div>
        </div>
        <div className="gf-tallies">
          {actions?.map(a => <ActionButton key={a.id} action={a} />)}
          {counts.working > 0 && <span className="gf-tally gf-tally-working"><i /> {counts.working} working</span>}
          {counts.blocked > 0 && <span className="gf-tally gf-tally-blocked"><i /> {counts.blocked} waiting on you</span>}
          {counts.done > 0 && <span className="gf-tally gf-tally-done"><i /> {counts.done} done</span>}
          {counts.failed > 0 && <span className="gf-tally gf-tally-failed"><i /> {counts.failed} failed</span>}
          {!model.agents.length && <span className="gf-tally">Nobody in yet</span>}
        </div>
      </div>

      <div className="gf-floor" style={{ height: floorHeight }}>
        <div ref={hostRef} className="gf-canvas" />

        {!ready && (
          <div className="gf-booting">
            <span className="gf-boot-dot" />
            Opening the floor…
          </div>
        )}

        <div className="gf-overlay">
          {slots.map(slot => {
            const a = byId.get(slot.id)
            if (!a) return null
            // A desk narrower than this cannot carry a readable bubble.
            const bubble = bubbleIds.has(a.id) && slot.w >= 104
            return (
              <div key={slot.id}>
                {bubble && (
                  <div
                    className={`gf-bubble gf-bubble-${a.status}`}
                    style={{ left: `clamp(88px, ${slot.x}px, calc(100% - 88px))`, top: slot.headY - 6 }}
                  >
                    {a.detail}
                  </div>
                )}
                <div className="gf-plate" style={{ left: slot.x, top: slot.plateY, maxWidth: slot.w - 8 }}>
                  <span className="gf-plate-name">{a.name}</span>
                  {a.role && <span className="gf-plate-role">{a.role}</span>}
                  {(a.status === 'done' || a.status === 'failed' || a.status === 'blocked') && (
                    <span className={`gf-plate-state gf-plate-state-${a.status}`}>
                      <PhosphorIcon className={`bi ${STATUS_ICON[a.status]}`} />
                      {STATUS_LABEL[a.status]}
                    </span>
                  )}
                </div>
                <button
                  type="button"
                  className={`gf-hit ${selected === a.id ? 'is-selected' : ''}`}
                  style={{ left: slot.x - slot.w / 2, top: slot.top, width: slot.w, height: slot.h }}
                  onClick={() => setSelected(id => (id === a.id ? null : a.id))}
                  aria-label={`${a.name} — ${STATUS_LABEL[a.status]}${a.actions?.length ? ', has actions' : ''}`}
                />
                {a.actions && a.actions.length > 0 && (
                  <span className="gf-actionable" style={{ left: slot.x + slot.w * 0.32, top: slot.headY + 14 }} aria-hidden>
                    <PhosphorIcon className="bi bi-hand-index-thumb" />
                  </span>
                )}
              </div>
            )
          })}
        </div>

        {chosen && (
          <aside className="gf-inspector">
            <div className="gf-inspector-head">
              <div>
                <strong>{chosen.name}</strong>
                {chosen.role && <span>{chosen.role}</span>}
              </div>
              <button type="button" onClick={() => setSelected(null)} aria-label="Close">
                <PhosphorIcon className="bi bi-x-lg" />
              </button>
            </div>
            <div className={`gf-inspector-status gf-status-${chosen.status}`}>
              <PhosphorIcon className={`bi ${STATUS_ICON[chosen.status]}`} />
              {STATUS_LABEL[chosen.status]}
              {typeof chosen.progress === 'number' && chosen.status === 'working' && (
                <em>{Math.round(chosen.progress * 100)}%</em>
              )}
            </div>
            {chosen.detail && <p className="gf-inspector-detail">{chosen.detail}</p>}
            {chosen.output
              ? <pre className="gf-inspector-output">{chosen.output}</pre>
              : <p className="gf-inspector-empty">Nothing on this desk yet.</p>}
            {chosen.actions && chosen.actions.length > 0 && (
              <div className="gf-inspector-actions">
                {chosen.actions.map(a => <ActionButton key={a.id} action={a} />)}
              </div>
            )}
          </aside>
        )}
      </div>

      {events.length > 0 && (
        <ol className="gf-ticker">
          {events.map(e => (
            <li key={e.id} className={`gf-ev gf-ev-${e.tone || 'info'}`}>
              <i />
              {e.text}
            </li>
          ))}
        </ol>
      )}

      {children && <div className="gf-controls">{children}</div>}
    </div>
  )
}
