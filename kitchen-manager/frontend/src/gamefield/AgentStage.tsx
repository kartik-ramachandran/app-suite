// The Agent Stage: how a page shows what its agents are up to.
//
// Nothing renders inline — the page keeps its own layout, untouched. Instead:
//
//   * a floating pixel-character button sits in the corner of the screen; it
//     comes alive while work is happening and badges anything waiting on you,
//   * clicking it opens the dream: a modal where the AI pictures its office,
//     with the page's real controls docked in so it stays usable,
//   * and the dream opens by itself, once, a beat after work actually starts.

import { useEffect, useMemo, useState } from 'react'
import { createPortal } from 'react-dom'
import PhosphorIcon from '../components/PhosphorIcon'
import GameField from './GameField'
import { autoOpensHere, setAutoOpensHere } from './officePrefs'
import { drawAvatar, lookFor } from './sprites'
import type { FieldAction, FieldModel } from './types'
import './AgentStage.css'

/** Work shorter than this never opens a modal — a flash would just be noise. */
const WAKE_DELAY_MS = 900

export interface AgentStageProps {
  model: FieldModel
  /** Page slug — remembers whether the dream opens by itself here. */
  pageKey: string
  /** The page's primary controls, docked into the floor so it stays usable. */
  actions?: FieldAction[]
  /** Height budget for the floor inside the modal. */
  height?: number
}

/** A pixel character for the launcher, drawn from the same sprite sheet as the floor. */
function useLauncherFace(pageKey: string): string {
  return useMemo(() => {
    try {
      return drawAvatar(lookFor(`launcher:${pageKey}`), 'stand').toDataURL()
    } catch {
      return ''
    }
  }, [pageKey])
}

export default function AgentStage({ model, pageKey, actions, height }: AgentStageProps) {
  const [open, setOpen] = useState(false)
  const [autoOpen, setAutoOpen] = useState(() => autoOpensHere(pageKey))
  const face = useLauncherFace(pageKey)

  const running = !!model.running

  // Re-arms on every false -> true transition of `running`, so dismissing the
  // dream hides it for this run and the next run still gets one.
  useEffect(() => {
    if (!running || !autoOpen) return
    const t = window.setTimeout(() => setOpen(true), WAKE_DELAY_MS)
    return () => window.clearTimeout(t)
  }, [running, autoOpen])

  useEffect(() => {
    if (!open) return
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false) }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [open])

  const counts = useMemo(() => {
    let working = 0
    let waiting = 0
    for (const a of model.agents) {
      if (a.status === 'working' || a.status === 'walking') working++
      else if (a.status === 'blocked') waiting++
    }
    return { working, waiting }
  }, [model.agents])

  // Whatever the desks are muttering becomes the drifting fragments.
  const fragments = useMemo(() => {
    const lines = [
      ...model.agents.filter(a => a.status === 'working' && a.detail).map(a => `${a.name}: ${a.detail}`),
      ...(model.events || []).slice(-4).reverse().map(e => e.text),
    ]
    return Array.from(new Set(lines)).slice(0, 4)
  }, [model.agents, model.events])

  const toggleAuto = () => {
    const next = !autoOpen
    setAutoOpen(next)
    setAutoOpensHere(pageKey, next)
  }

  // Portalled to <body> for two reasons: a page ancestor with transform or
  // overflow would otherwise trap position:fixed, and the `.ag-stage` wrapper
  // gives these controls enough specificity to survive the app-wide
  // `html.theme-editorial button { border-radius: 4px }` override, which
  // otherwise squares off the launcher and every pill in here.
  return createPortal(
    <div className="ag-stage">
      <button
        type="button"
        className={`ag-launcher${running ? ' is-busy' : ''}${counts.waiting ? ' is-waiting' : ''}`}
        onClick={() => setOpen(true)}
        aria-label={
          counts.waiting ? `Open the office — ${counts.waiting} waiting on you`
          : running ? `Open the office — ${counts.working} working`
          : 'Open the office'
        }
        title="See what the agents are doing"
      >
        <span className="ag-launcher-ring" aria-hidden />
        <span className="ag-launcher-window" aria-hidden>
          {face && <img src={face} alt="" />}
        </span>
        {counts.waiting > 0
          ? <span className="ag-launcher-badge" aria-hidden>{counts.waiting}</span>
          : running && <span className="ag-launcher-dot" aria-hidden />}
      </button>

      {open && (
        <div
          className="ds-backdrop"
          role="dialog"
          aria-modal="true"
          aria-label={`${model.title} — the office floor`}
          onClick={() => setOpen(false)}
        >
          <div className="ds-bloom ds-bloom-a" aria-hidden />
          <div className="ds-bloom ds-bloom-b" aria-hidden />

          <div className="ds-cloud" onClick={e => e.stopPropagation()}>
            <div className="ds-head">
              <span className="ds-kicker">
                <PhosphorIcon className="bi bi-moon-stars" />
                {running ? 'Daydreaming' : 'The office, as imagined'}
              </span>
              <h2>
                {running ? `${model.title} is picturing the office` : `Inside ${model.title}`}
              </h2>
              {model.subtitle && <p>{model.subtitle}</p>}
              <button type="button" className="ds-close" onClick={() => setOpen(false)} aria-label="Close">
                <PhosphorIcon className="bi bi-x-lg" />
              </button>
            </div>

            <div className={`ds-vision${running ? ' is-running' : ''}`}>
              <GameField model={model} actions={actions} height={height} />
            </div>

            {fragments.length > 0 && (
              <ul className="ds-fragments">
                {fragments.map((f, i) => (
                  <li key={f} style={{ animationDelay: `${i * 0.9}s` }}>{f}</li>
                ))}
              </ul>
            )}

            <div className="ds-foot">
              <button type="button" className="ds-btn ds-btn-primary" onClick={() => setOpen(false)}>
                <PhosphorIcon className="bi bi-box-arrow-right" /> Back to work
              </button>
              <button
                type="button"
                className={`ds-auto${autoOpen ? ' is-on' : ''}`}
                onClick={toggleAuto}
                aria-pressed={autoOpen}
              >
                <PhosphorIcon className={`bi ${autoOpen ? 'bi-check-square' : 'bi-square'}`} />
                Open this automatically when work starts
              </button>
            </div>
          </div>
        </div>
      )}
    </div>,
    document.body,
  )
}
