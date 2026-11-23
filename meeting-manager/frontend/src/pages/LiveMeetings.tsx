// Live Meetings — a module in its own right.
//
// This started as a panel wedged into Signal's sidebar, which made the most
// autonomous thing the product does look like a footnote to the recording
// analyser. It is the other way round: a notetaker joins the meeting, records
// it, and hands the transcript to Signal. So it gets a page, a place on the
// dashboard, and a row in the nav like every other module.
//
// The panel itself is unchanged — same component, same polling, same arming —
// only given room. Signal still owns the analyses, and a recorded meeting links
// straight back there with the session it wants opened.

import { useLayoutEffect } from 'react'
import { useNavigate } from 'react-router-dom'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import PhosphorIcon from '../components/PhosphorIcon'
import MeetingsPanel from '../components/MeetingsPanel'
import ModuleVoiceChat from '../components/ModuleVoiceChat'
import './LiveMeetings.css'

const API = import.meta.env.VITE_API_URL
  ?? (window.location.hostname === 'localhost' ? 'http://localhost:5152' : window.location.origin)

const authHeaders = (): Record<string, string> => {
  const token = localStorage.getItem('authToken')
  return token ? { Authorization: `Bearer ${token}` } : {}
}

export default function LiveMeetings() {
  const { setBreadcrumbs } = useLayoutControls()
  const navigate = useNavigate()

  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Live Meetings' }])
    return () => setBreadcrumbs([])
  }, [])

  return (
    <div className="lm-page">
      <header className="lm-head">
        <div className="lm-eyebrow">
          <span className="lm-mark"><PhosphorIcon className="bi bi-record-circle" /></span>
          Live Meetings
        </div>
        <h1>Send a notetaker, <em>not yourself.</em></h1>
        <p>
          Arm a meeting from your calendar and a bot joins it, records it, and writes it
          up — no one has to be at a keyboard. The transcript builds while the meeting is
          still going, and the analysis lands in Signal when it ends.
        </p>
      </header>

      {/* Ask about what was said instead of reading transcripts. */}
      <ModuleVoiceChat module="meetings" />

      <div className="lm-body">
        <MeetingsPanel
          api={API}
          authHeaders={authHeaders}
          variant="page"
          onOpenSession={id => navigate(`/interaction-intelligence?session=${encodeURIComponent(id)}`)}
        />

        <aside className="lm-aside">
          <h2>Before the first one</h2>
          <ol className="lm-steps">
            <li>
              <strong>Connect a calendar.</strong>
              <span>Meetings with a join link are the ones a notetaker can be put on.</span>
              <a href="/calendar">Calendar settings →</a>
            </li>
            <li>
              <strong>Choose how it records.</strong>
              <span>Our own bot joins the call on its own. Browser-tab capture needs the extension and you in the meeting.</span>
            </li>
            <li>
              <strong>Decide what runs afterwards.</strong>
              <span>Summary, wrap-up, quality, compliance — per person or for the whole team.</span>
              <a href="/settings">Notetaker settings →</a>
            </li>
          </ol>

          <p className="lm-legal">
            <PhosphorIcon className="bi bi-shield-exclamation" />
            The bot joins as a visible participant. Recording people has legal requirements
            that differ by country — make sure everyone knows before you record.
          </p>
        </aside>
      </div>
    </div>
  )
}
