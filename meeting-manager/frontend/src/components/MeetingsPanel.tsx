// Signal's live-meeting layer.
//
// Shows the meetings on your calendar that could be recorded, lets you put a
// notetaker on one, and tracks it while it runs. Everything after the audio is
// Signal's existing machinery: when a meeting ends its transcript is filed as a
// normal analysis session, which is why a finished meeting just opens like any
// other item in the history.

import { useCallback, useEffect, useMemo, useState } from 'react'
import PhosphorIcon from './PhosphorIcon'
import ConfirmDialog from './ConfirmDialog'
import CustomSelect from './CustomSelect'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import './MeetingsPanel.css'

export interface CaptureSource {
  key: string
  name: string
  available: boolean
  reason?: string | null
}

export interface MeetingSession {
  id: string
  calendarEventId?: number | null
  interactionSessionId?: string | null
  title: string
  platform: string
  source: string
  status: 'armed' | 'live' | 'closing' | 'analysing' | 'done' | 'failed' | 'cancelled'
  error?: string | null
  chunkCount: number
  transcriptLength: number
  participants: { name: string; speaking?: boolean }[]
  tools: string[]
  scheduledStart: string
  scheduledEnd: string
}

export interface UpcomingMeeting {
  calendarEventId: number
  title: string
  meetingLink?: string | null
  platform: string
  start: string
  end: string
  joinable: boolean
  session?: MeetingSession | null
}

const PLATFORM_ICON: Record<string, string> = {
  google: 'bi-camera-video',
  microsoft: 'bi-microsoft-teams',
  zoom: 'bi-camera-video-fill',
  webex: 'bi-camera-video',
  other: 'bi-calendar-event',
}

const STATUS_LABEL: Record<MeetingSession['status'], string> = {
  armed: 'Notetaker armed',
  live: 'Recording',
  closing: 'Wrapping up',
  analysing: 'Analysing',
  done: 'Done',
  failed: 'Failed',
  cancelled: 'Off',
}

/** A live meeting is worth re-checking often; an idle list is not. */
const POLL_LIVE_MS = 5000
const POLL_IDLE_MS = 60000

export interface MeetingsPanelProps {
  /** Base API URL, so this matches however the host page talks to the server. */
  api: string
  authHeaders: () => Record<string, string>
  /** Open a finished meeting's analysis in the host page. */
  onOpenSession: (interactionSessionId: string) => void
  /**
   * 'sidebar' is the compact column this started as; 'page' is the standalone
   * module, where the same groups get room to breathe side by side.
   */
  variant?: 'sidebar' | 'page'
}

export default function MeetingsPanel({ api, authHeaders, onOpenSession, variant = 'sidebar' }: MeetingsPanelProps) {
  const [sources, setSources] = useState<CaptureSource[]>([])
  const [upcoming, setUpcoming] = useState<UpcomingMeeting[]>([])
  const [sessions, setSessions] = useState<MeetingSession[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [busyId, setBusyId] = useState<number | string | null>(null)
  const [disarmTarget, setDisarmTarget] = useState<MeetingSession | null>(null)

  const [source, setSource] = useState('tab')
  const [showHelp, setShowHelp] = useState(false)

  const get = useCallback(async (path: string) => {
    const res = await fetch(`${api}/api/meetings${path}`, { headers: authHeaders() })
    if (!res.ok) throw new Error(String(res.status))
    return res.json()
  }, [api, authHeaders])

  const load = useCallback(async () => {
    try {
      const [s, u, l] = await Promise.all([
        get('/sources').catch(() => []),
        get('/upcoming').catch(() => []),
        get('').catch(() => []),
      ])
      setSources(s)
      setUpcoming(u)
      setSessions(l)
      setError('')
    } catch {
      setError('Could not load your meetings.')
    } finally {
      setLoading(false)
    }
  }, [get])

  useEffect(() => { load() }, [load])

  const live = useMemo(
    () => sessions.filter(s => s.status === 'live' || s.status === 'closing' || s.status === 'analysing'),
    [sessions],
  )

  // Poll faster while something is actually running.
  useEffect(() => {
    const every = live.length ? POLL_LIVE_MS : POLL_IDLE_MS
    const t = window.setInterval(load, every)
    return () => window.clearInterval(t)
  }, [live.length, load])

  const post = async (path: string, body?: unknown) => {
    const res = await fetch(`${api}/api/meetings${path}`, {
      method: 'POST',
      headers: { ...authHeaders(), 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined,
    })
    const data = await res.json().catch(() => ({}))
    if (!res.ok) throw new Error(data?.error || 'That did not work.')
    return data
  }

  const arm = async (meeting: UpcomingMeeting) => {
    setBusyId(meeting.calendarEventId)
    setError('')
    try {
      await post('/arm', { calendarEventId: meeting.calendarEventId, source, tools: ['summaries'] })
      await load()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusyId(null)
    }
  }

  const confirmDisarm = async () => {
    const target = disarmTarget
    setDisarmTarget(null)
    if (!target) return
    setBusyId(target.id)
    try {
      await post(`/${target.id}/disarm`)
      await load()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusyId(null)
    }
  }

  // The office floor, but the desks are the people actually in the call. The
  // bot reports the room with every audio segment, so this is who it can see
  // rather than a guess from the calendar invite.
  const { stageProps } = useFieldView('signal-meeting')

  const field = useMemo<FieldModel>(() => {
    const current = live[0]
    const people = current?.participants ?? []

    return {
      title: current ? current.title : 'Meeting room',
      subtitle: current
        ? `${people.length || 'no'} in the room · ${current.transcriptLength.toLocaleString()} characters`
        : 'Nothing being recorded right now.',
      running: !!current && current.status === 'live',
      agents: [
        ...people.map((p, i) => ({
          id: `person-${i}-${p.name}`,
          name: p.name,
          role: p.speaking ? 'speaking' : 'listening',
          status: (p.speaking ? 'working' : 'idle') as 'working' | 'idle',
        })),
        {
          id: 'notetaker',
          name: 'Notetaker',
          role: current ? STATUS_LABEL[current.status].toLowerCase() : 'idle',
          status: (current?.status === 'live' ? 'working'
            : current?.status === 'failed' ? 'failed'
            : current ? 'done' : 'idle') as 'working' | 'failed' | 'done' | 'idle',
          detail: current?.error
            ? bubble(current.error)
            : current?.status === 'live' ? 'Listening and writing it down…' : undefined,
          boss: true,
        },
      ],
      messages: people.filter(p => p.speaking).map((p, i) => ({
        id: `said-${i}-${current?.chunkCount ?? 0}`,
        from: `person-${people.indexOf(p)}-${p.name}`,
        to: 'notetaker',
      })),
      events: current
        ? [{ id: 'live', text: `${current.title} — ${STATUS_LABEL[current.status]}`, tone: 'info' as const }]
        : [],
    }
  }, [live])

  const chosen = sources.find(s => s.key === source)
  const finished = sessions.filter(s => s.status === 'done' || s.status === 'failed')

  return (
    <div className={`mp-panel${variant === 'page' ? ' mp-panel--page' : ''}`}>
      <ConfirmDialog
        open={!!disarmTarget}
        title="Take the notetaker off?"
        message={`"${disarmTarget?.title ?? 'This meeting'}" will not be recorded.`}
        confirmLabel="Take it off"
        onConfirm={confirmDisarm}
        onCancel={() => setDisarmTarget(null)}
      />

      <div className="mp-head">
        <h3><PhosphorIcon className="bi bi-record-circle" /> Live meetings</h3>
        <button
          type="button"
          className="mp-help-toggle"
          onClick={() => setShowHelp(v => !v)}
          aria-expanded={showHelp}
          aria-label="How the notetaker works"
        >
          <PhosphorIcon className="bi bi-question-circle" />
        </button>
      </div>

      {showHelp && (
        <div className="mp-help">
          <ol>
            <li><strong>Arm it.</strong> Press Record next to a meeting below.</li>
            <li>
              <strong>It starts itself.</strong>{' '}
              {source === 'tab'
                ? 'When the meeting begins, the extension badge turns into a prompt on the meeting tab — one click and it is recording. Chrome only releases a tab’s audio after a click, which is why that step exists.'
                : 'A bot joins the call and records it. Nobody needs to be at a keyboard.'}
            </li>
            <li><strong>It writes itself up.</strong> The transcript builds during the meeting, and when it ends the analysis appears in History.</li>
          </ol>
          {source === 'tab' && (
            <p className="mp-help-note">
              Needs the GenPrompt extension (1.8.0+), signed in, and you in the meeting.
              Browser meetings only.
            </p>
          )}
          <a href="/faq" target="_blank" rel="noreferrer">More on how this works →</a>
        </div>
      )}

      {sources.length > 0 && (
        <div className="mp-source">
          <label id="mp-source-label">Record with</label>
          <CustomSelect
            className="mp-source-select"
            value={source}
            onChange={setSource}
            options={sources.map(s => ({
              value: s.key,
              label: s.available ? s.name : `${s.name} — unavailable`,
              description: s.available ? undefined : (s.reason ?? undefined),
              icon: s.key === 'tab' ? 'bi-window' : 'bi-robot',
            }))}
          />
          {chosen && !chosen.available && <p className="mp-note">{chosen.reason}</p>}
        </div>
      )}

      {error && <div className="mp-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      {/* Fixed launcher and modal; nothing of this renders in the panel flow. */}
      <AgentStage {...stageProps} model={field} />

      {live.length > 0 && (
        <div className="mp-group">
          <span className="mp-group-title">Happening now</span>
          {live.map(s => (
            <div key={s.id} className="mp-live">
              <div className="mp-live-dot" aria-hidden />
              <div className="mp-live-body">
                <strong>{s.title}</strong>
                <small>
                  {STATUS_LABEL[s.status]}
                  {s.chunkCount > 0 && ` · ${s.transcriptLength.toLocaleString()} characters so far`}
                </small>
              </div>
            </div>
          ))}
        </div>
      )}

      <div className="mp-group">
        <span className="mp-group-title">Coming up</span>
        {loading ? (
          <p className="mp-empty">Loading…</p>
        ) : upcoming.length === 0 ? (
          <p className="mp-empty">
            Nothing scheduled with a join link. Connect a calendar to put a notetaker on your meetings.
          </p>
        ) : upcoming.map(m => {
          const armed = m.session && m.session.status !== 'cancelled'
          return (
            <div key={m.calendarEventId} className={`mp-item${armed ? ' is-armed' : ''}`}>
              <PhosphorIcon className={`bi ${PLATFORM_ICON[m.platform] || PLATFORM_ICON.other} mp-item-icon`} />
              <div className="mp-item-body">
                <strong>{m.title}</strong>
                <small>{new Date(m.start).toLocaleString()}</small>
              </div>
              {!m.joinable ? (
                <span className="mp-tag">No link</span>
              ) : armed ? (
                <button
                  className="mp-btn mp-btn-off"
                  disabled={busyId === m.session!.id}
                  onClick={() => setDisarmTarget(m.session!)}
                >
                  Armed
                </button>
              ) : (
                <button
                  className="mp-btn"
                  disabled={busyId === m.calendarEventId || (chosen ? !chosen.available : false)}
                  onClick={() => arm(m)}
                >
                  {busyId === m.calendarEventId ? '…' : 'Record'}
                </button>
              )}
            </div>
          )
        })}
      </div>

      {finished.length > 0 && (
        <div className="mp-group">
          <span className="mp-group-title">Recorded</span>
          {finished.slice(0, 10).map(s => (
            <button
              key={s.id}
              className="mp-item mp-item-button"
              disabled={!s.interactionSessionId}
              onClick={() => s.interactionSessionId && onOpenSession(s.interactionSessionId)}
            >
              <PhosphorIcon className={`bi ${s.status === 'failed' ? 'bi-x-circle' : 'bi-file-earmark-text'} mp-item-icon`} />
              <div className="mp-item-body">
                <strong>{s.title}</strong>
                <small>{s.status === 'failed' ? (s.error || 'Failed') : new Date(s.scheduledStart).toLocaleDateString()}</small>
              </div>
            </button>
          ))}
        </div>
      )}
    </div>
  )
}
