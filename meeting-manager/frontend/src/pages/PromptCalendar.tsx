import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useLayoutEffect, useRef } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import AvatarPanel from '../components/AvatarPanel'
import api from '../services/api'
import '../App.css'

interface CalendarEvent {
  id: number
  provider: string
  providerEventId: string
  title: string
  description?: string
  location?: string
  meetingLink?: string
  startDateTime: string
  endDateTime: string
  timezone: string
  organizerEmail?: string
  organizerName?: string
  busyStatus: string
  isAllDay: boolean
  attendees: { name: string; email: string; responseStatus: string }[]
  workspaceId?: number
  reminderMinutes?: number
}

interface Connection {
  id: number
  provider: string
  calendarEmail: string
  displayName: string
}

interface DailyBriefing {
  summary: string
  meetingsNeedingPrep: string[]
  pendingFollowUps: string[]
  bestFocusBlock?: string
  risks: string[]
  suggestedPriorities: string[]
}

const PROVIDER_COLOR: Record<string, string> = {
  genprompt: '#d62976',
  google: '#4285f4',
  microsoft: '#0078d4',
}

const PROVIDER_ICON: Record<string, string> = {
  genprompt: 'bi-stars',
  google: 'bi-google',
  microsoft: 'bi-microsoft',
}

interface ExtractedEvent {
  title?: string; date?: string; startTime?: string; endTime?: string
  allDay?: boolean; location?: string; meetingLink?: string; description?: string
}

type View = 'today' | 'week' | 'agenda'
type CalendarProvider = 'google' | 'microsoft'

const PROVIDER_LABEL: Record<CalendarProvider, string> = {
  google: 'Google Calendar',
  microsoft: 'Outlook Calendar',
}

const defaultEventForm = () => {
  const start = new Date()
  start.setMinutes(start.getMinutes() < 30 ? 30 : 60, 0, 0)
  const end = new Date(start)
  end.setHours(end.getHours() + 1)
  return {
    title: '',
    description: '',
    location: '',
    meetingLink: '',
    startDateTime: toDateTimeLocal(start),
    endDateTime: toDateTimeLocal(end),
    isAllDay: false,
    reminderMinutes: '15',
  }
}

const toDateTimeLocal = (date: Date) => {
  const pad = (value: number) => value.toString().padStart(2, '0')
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`
}

const getMonthGridRange = (month: Date) => {
  const first = new Date(month.getFullYear(), month.getMonth(), 1)
  const from = new Date(first)
  from.setDate(first.getDate() - first.getDay())
  from.setHours(0, 0, 0, 0)
  const to = new Date(from)
  to.setDate(from.getDate() + 42)
  return { from, to }
}

const buildMonthGrid = (month: Date) => {
  const { from } = getMonthGridRange(month)
  return Array.from({ length: 42 }, (_, i) => {
    const day = new Date(from)
    day.setDate(from.getDate() + i)
    return day
  })
}

const formatEventTime = (evt: CalendarEvent) =>
  evt.isAllDay
    ? 'All day'
    : new Date(evt.startDateTime).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })

export default function PromptCalendar() {
  const { setBreadcrumbs } = useLayoutControls()
  const navigate = useNavigate()

  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Calendar' }])
    return () => setBreadcrumbs([])
  }, [])

  const [connections, setConnections] = useState<Connection[]>([])
  const [events, setEvents] = useState<CalendarEvent[]>([])
  const [briefing, setBriefing] = useState<DailyBriefing | null>(null)
  const [view, setView] = useState<View>('today')
  const [weekOffset, setWeekOffset] = useState(0)
  const [loading, setLoading] = useState(true)
  const [syncing, setSyncing] = useState(false)
  const [connectingProvider, setConnectingProvider] = useState<string | null>(null)
  const [connectModalProvider, setConnectModalProvider] = useState<CalendarProvider | null>(null)
  const [showEventModal, setShowEventModal] = useState(false)
  const [showCalendarModal, setShowCalendarModal] = useState(false)
  const [calendarModalMonth, setCalendarModalMonth] = useState(() => new Date(new Date().getFullYear(), new Date().getMonth(), 1))
  const [calendarModalEvents, setCalendarModalEvents] = useState<CalendarEvent[]>([])
  const [loadingCalendarModal, setLoadingCalendarModal] = useState(false)
  const [savingEvent, setSavingEvent] = useState(false)
  const [eventForm, setEventForm] = useState(defaultEventForm)
  const [eventTarget, setEventTarget] = useState<number | null>(null)
  const [error, setError] = useState('')
  const [activeTab, setActiveTab] = useState<'calendar' | 'avatar'>('calendar')

  // Capture schedule from a screenshot / shared screen
  const [captureOpen, setCaptureOpen] = useState(false)
  const [captureBusy, setCaptureBusy] = useState(false)
  const [screenSharing, setScreenSharing] = useState(false)
  const [extracted, setExtracted] = useState<ExtractedEvent[] | null>(null)
  const [extractedPick, setExtractedPick] = useState<Record<number, boolean>>({})
  const [addingEvents, setAddingEvents] = useState(false)
  const screenshotInputRef = useRef<HTMLInputElement>(null)
  const screenVideoRef = useRef<HTMLVideoElement>(null)
  const screenStreamRef = useRef<MediaStream | null>(null)

  useEffect(() => {
    loadAll()
  }, [])

  useEffect(() => {
    loadEvents()
  }, [weekOffset, view])

  const loadAll = async () => {
    setLoading(true)
    try {
      const [connRes, briefRes] = await Promise.allSettled([
        api.get('/calendar/connections'),
        api.get(`/avatar/daily-briefing?timezone=${encodeURIComponent(Intl.DateTimeFormat().resolvedOptions().timeZone)}`),
      ])
      if (connRes.status === 'fulfilled') setConnections(connRes.value.data)
      if (briefRes.status === 'fulfilled') setBriefing(briefRes.value.data.briefing)
      await loadEvents()
    } finally {
      setLoading(false)
    }
  }

  const loadEvents = async () => {
    const { from, to } = getDateRange()
    try {
      const res = await api.get(`/calendar/events?from=${from.toISOString()}&to=${to.toISOString()}`)
      setEvents(res.data)
    } catch { /* no connections yet */ }
  }

  const getDateRange = () => {
    const now = new Date()
    if (view === 'today') {
      return { from: new Date(now.getFullYear(), now.getMonth(), now.getDate()), to: new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1) }
    }
    const weekStart = new Date(now)
    weekStart.setDate(now.getDate() - now.getDay() + 1 + weekOffset * 7) // Monday
    weekStart.setHours(0, 0, 0, 0)
    const weekEnd = new Date(weekStart)
    weekEnd.setDate(weekStart.getDate() + 7)
    return { from: weekStart, to: weekEnd }
  }

  const openConnectModal = (provider: CalendarProvider) => {
    setError('')
    setConnectModalProvider(provider)
  }

  const closeConnectModal = () => {
    if (!connectingProvider) setConnectModalProvider(null)
  }

  const handleConnect = async (provider: CalendarProvider) => {
    setConnectingProvider(provider)
    setError('')
    try {
      const res = await api.get(`/calendar/connect/${provider}`)
      window.location.href = res.data.authUrl
    } catch (e: any) {
      setError(e.response?.data?.message ?? `Failed to initiate ${provider} connection`)
      setConnectingProvider(null)
      setConnectModalProvider(null)
    }
  }

  const handleDisconnect = async (id: number) => {
    await api.delete(`/calendar/connections/${id}`)
    setConnections(c => c.filter(x => x.id !== id))
  }

  const handleSync = async () => {
    setSyncing(true)
    try {
      await api.post('/calendar/sync')
      await loadEvents()
    } catch { setError('Sync failed. Please try again.') }
    finally { setSyncing(false) }
  }

  const loadCalendarModalEvents = async (month: Date) => {
    setLoadingCalendarModal(true)
    try {
      const range = getMonthGridRange(month)
      const res = await api.get(`/calendar/events?from=${range.from.toISOString()}&to=${range.to.toISOString()}`)
      setCalendarModalEvents(res.data)
    } catch {
      setCalendarModalEvents([])
    } finally {
      setLoadingCalendarModal(false)
    }
  }

  const openCalendarModal = async () => {
    const month = new Date(new Date().getFullYear(), new Date().getMonth(), 1)
    setCalendarModalMonth(month)
    setShowCalendarModal(true)
    await loadCalendarModalEvents(month)
  }

  const moveCalendarModalMonth = async (offset: number) => {
    const next = new Date(calendarModalMonth.getFullYear(), calendarModalMonth.getMonth() + offset, 1)
    setCalendarModalMonth(next)
    await loadCalendarModalEvents(next)
  }

  const jumpCalendarModalToToday = async () => {
    const month = new Date(new Date().getFullYear(), new Date().getMonth(), 1)
    setCalendarModalMonth(month)
    await loadCalendarModalEvents(month)
  }

  const openEventModal = () => {
    setEventForm(defaultEventForm())
    // Default to the first connected calendar so the appointment syncs to the
    // user's real Google/Outlook by default — not just stored on the platform.
    setEventTarget(connections[0]?.id ?? null)
    setError('')
    setShowEventModal(true)
  }

  const createNativeEvent = async () => {
    if (!eventForm.title.trim()) {
      setError('Appointment title is required.')
      return
    }

    setSavingEvent(true)
    setError('')
    try {
      await api.post('/calendar/events/native', {
        title: eventForm.title.trim(),
        description: eventForm.description.trim() || null,
        location: eventForm.location.trim() || null,
        meetingLink: eventForm.meetingLink.trim() || null,
        startDateTime: new Date(eventForm.startDateTime).toISOString(),
        endDateTime: new Date(eventForm.endDateTime).toISOString(),
        timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
        isAllDay: eventForm.isAllDay,
        reminderMinutes: eventForm.reminderMinutes ? Number(eventForm.reminderMinutes) : null,
        targetConnectionId: eventTarget,
      })
      setShowEventModal(false)
      await loadEvents()
      if (showCalendarModal) await loadCalendarModalEvents(calendarModalMonth)
    } catch (e: any) {
      setError(e.response?.data?.message ?? 'Could not create appointment.')
    } finally {
      setSavingEvent(false)
    }
  }

  // ── Capture schedule from a screenshot / shared screen ──────────────────────

  const compressToDataUrl = (source: HTMLImageElement | HTMLVideoElement, maxDim = 1600): string => {
    const w = source instanceof HTMLVideoElement ? source.videoWidth : source.naturalWidth
    const h = source instanceof HTMLVideoElement ? source.videoHeight : source.naturalHeight
    const scale = Math.min(1, maxDim / Math.max(w, h))
    const canvas = document.createElement('canvas')
    canvas.width = Math.round(w * scale)
    canvas.height = Math.round(h * scale)
    canvas.getContext('2d')!.drawImage(source, 0, 0, canvas.width, canvas.height)
    return canvas.toDataURL('image/jpeg', 0.85)
  }

  const extractFromDataUrl = async (dataUrl: string) => {
    setCaptureBusy(true); setError('')
    try {
      const { data } = await api.post('/calendar/extract-events', { imageData: dataUrl })
      const evs: ExtractedEvent[] = data.events || []
      if (evs.length === 0) { setError('No events found in that image — try a clearer capture.'); setExtracted(null) }
      else { setExtracted(evs); setExtractedPick(Object.fromEntries(evs.map((_, i) => [i, true]))) }
    } catch (e: any) {
      setError(e.response?.data?.message ?? 'Could not read events from that image.')
    } finally { setCaptureBusy(false) }
  }

  const onScreenshotFile = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setCaptureOpen(false)
    const reader = new FileReader()
    reader.onload = () => {
      const img = new Image()
      img.onload = () => extractFromDataUrl(compressToDataUrl(img))
      img.src = reader.result as string
    }
    reader.readAsDataURL(files[0])
    if (screenshotInputRef.current) screenshotInputRef.current.value = ''
  }

  const startScreenShare = async () => {
    setError('')
    try {
      const stream = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: false })
      screenStreamRef.current = stream
      setCaptureOpen(false)
      setScreenSharing(true)
      // When the user stops sharing from the browser chrome, clean up.
      stream.getVideoTracks()[0]?.addEventListener('ended', stopScreenShare)
    } catch {
      setError('Screen share was cancelled or blocked.')
    }
  }

  useEffect(() => {
    if (screenSharing && screenVideoRef.current && screenStreamRef.current) {
      screenVideoRef.current.srcObject = screenStreamRef.current
    }
  }, [screenSharing])

  const captureScreenFrame = async () => {
    const v = screenVideoRef.current
    if (!v || !v.videoWidth) return
    const dataUrl = compressToDataUrl(v)
    await extractFromDataUrl(dataUrl)
  }

  const stopScreenShare = () => {
    screenStreamRef.current?.getTracks().forEach(t => t.stop())
    screenStreamRef.current = null
    setScreenSharing(false)
  }

  useEffect(() => () => { screenStreamRef.current?.getTracks().forEach(t => t.stop()) }, [])

  const toIso = (date: string, time: string) => new Date(`${date}T${time}:00`).toISOString()

  const addExtractedEvents = async () => {
    if (!extracted) return
    setAddingEvents(true); setError('')
    let added = 0
    try {
      for (let i = 0; i < extracted.length; i++) {
        if (!extractedPick[i]) continue
        const ev = extracted[i]
        if (!ev.title || !ev.date) continue
        let startIso: string, endIso: string, allDay = !!ev.allDay
        if (allDay || !ev.startTime) {
          allDay = true
          startIso = new Date(`${ev.date}T00:00:00`).toISOString()
          endIso = new Date(`${ev.date}T23:59:00`).toISOString()
        } else {
          startIso = toIso(ev.date, ev.startTime)
          endIso = ev.endTime ? toIso(ev.date, ev.endTime) : new Date(new Date(startIso).getTime() + 3600000).toISOString()
        }
        await api.post('/calendar/events/native', {
          title: ev.title.trim(),
          description: ev.description?.trim() || null,
          location: ev.location?.trim() || null,
          meetingLink: ev.meetingLink?.trim() || null,
          startDateTime: startIso,
          endDateTime: endIso,
          timezone: Intl.DateTimeFormat().resolvedOptions().timeZone,
          isAllDay: allDay,
          reminderMinutes: null,
          targetConnectionId: eventTarget,
        })
        added++
      }
      setExtracted(null)
      stopScreenShare()
      await loadEvents()
      if (added > 0) setError('')
    } catch (e: any) {
      setError(e.response?.data?.message ?? 'Could not add some events.')
    } finally { setAddingEvents(false) }
  }

  const openMeeting = async (evt: CalendarEvent) => {
    if (evt.workspaceId) {
      navigate(`/calendar/meeting/${evt.workspaceId}`)
      return
    }
    try {
      const res = await api.post('/meetings/workspaces', {
        providerEventId: evt.providerEventId,
        eventTitle: evt.title,
        eventStart: evt.startDateTime,
        eventEnd: evt.endDateTime,
        attendees: evt.attendees,
      })
      navigate(`/calendar/meeting/${res.data.id}`)
    } catch { navigate('/calendar/meeting/new', { state: { event: evt } }) }
  }

  const todayEvents = events.filter(e => {
    const d = new Date(e.startDateTime)
    const now = new Date()
    return d.toDateString() === now.toDateString()
  })

  const upcomingEvents = events.slice(0, 10)
  const now = new Date()

  const isEventNow = (e: CalendarEvent) => new Date(e.startDateTime) <= now && new Date(e.endDateTime) >= now
  const isPast = (e: CalendarEvent) => new Date(e.endDateTime) < now
  const calendarModalDays = buildMonthGrid(calendarModalMonth)
  const eventsForDay = (day: Date) => calendarModalEvents
    .filter(evt => new Date(evt.startDateTime).toDateString() === day.toDateString())
    .sort((a, b) => new Date(a.startDateTime).getTime() - new Date(b.startDateTime).getTime())

  if (loading) return (
    <div className="calendar-loading">
      <div className="spinner" /><span>Loading your calendar…</span>
    </div>
  )

  return (
    <div className="calendar-page">
      {connectModalProvider && (
        <div
          className="calendar-modal-backdrop"
          role="dialog"
          aria-modal="true"
          aria-labelledby="calendar-connect-title"
        >
          <div
            className="card calendar-modal-card"
          >
            <div className="calendar-modal-head">
              <div
                className="calendar-modal-provider-icon"
                style={{
                  background: PROVIDER_COLOR[connectModalProvider],
                }}
              >
                <PhosphorIcon className={`bi ${PROVIDER_ICON[connectModalProvider]}`} />
              </div>
              <div className="calendar-modal-copy">
                <h2 id="calendar-connect-title">
                  Connect {PROVIDER_LABEL[connectModalProvider]}
                </h2>
                <p>
                  This opens {PROVIDER_LABEL[connectModalProvider]} sign-in so GenPrompt can read meeting times,
                  attendees, locations, and video links for prep suggestions, availability checks, and follow-ups.
                </p>
              </div>
              <button
                className="calendar-modal-close"
                type="button"
                onClick={closeConnectModal}
                disabled={!!connectingProvider}
                aria-label="Close"
              >
                <PhosphorIcon className="bi bi-x-lg" />
              </button>
            </div>
            <div className="calendar-modal-note">
              Calendar data is used to power this workspace and the Calendar Bot. You can disconnect the calendar from this page later.
            </div>
            <div className="calendar-modal-actions">
              <button className="btn btn-secondary" onClick={closeConnectModal} disabled={!!connectingProvider}>
                Cancel
              </button>
              <button className="btn btn-primary" onClick={() => handleConnect(connectModalProvider)} disabled={!!connectingProvider}>
                {connectingProvider
                  ? <><div className="spinner" />Opening sign-in…</>
                  : <><PhosphorIcon className={`bi ${PROVIDER_ICON[connectModalProvider]}`} />Continue to {PROVIDER_LABEL[connectModalProvider]}</>}
              </button>
            </div>
          </div>
        </div>
      )}

      {showEventModal && (
        <div className="calendar-modal-backdrop" role="dialog" aria-modal="true" aria-labelledby="calendar-event-title">
          <div className="card calendar-modal-card calendar-event-modal">
            <div className="calendar-modal-head">
              <div className="calendar-modal-provider-icon" style={{ background: PROVIDER_COLOR.genprompt }}>
                <PhosphorIcon className="bi bi-calendar-plus" />
              </div>
              <div className="calendar-modal-copy">
                <h2 id="calendar-event-title">New appointment</h2>
                <p>Create a GenPrompt calendar item with reminders. It will appear beside connected Google and Outlook events.</p>
              </div>
              <button className="calendar-modal-close" type="button" onClick={() => setShowEventModal(false)} disabled={savingEvent} aria-label="Close">
                <PhosphorIcon className="bi bi-x-lg" />
              </button>
            </div>

            <div className="calendar-event-form">
              <label>
                <span>Title</span>
                <input value={eventForm.title} onChange={e => setEventForm(f => ({ ...f, title: e.target.value }))} placeholder="Strategy review, dentist, school pickup..." />
              </label>
              <label>
                <span>Save to</span>
                <select value={eventTarget ?? ''} onChange={e => setEventTarget(e.target.value ? Number(e.target.value) : null)}>
                  {connections.map(c => (
                    <option key={c.id} value={c.id}>
                      {(PROVIDER_LABEL[c.provider as CalendarProvider] ?? c.provider)} · {c.calendarEmail}
                    </option>
                  ))}
                  <option value="">GenPrompt only (this app)</option>
                </select>
                <small className="calendar-target-hint">
                  {eventTarget
                    ? <><PhosphorIcon className="bi bi-arrow-repeat" /> Added to your real calendar — syncs to your phone and other devices.</>
                    : connections.length === 0
                      ? <><PhosphorIcon className="bi bi-info-circle" /> Connect Google or Outlook to have appointments appear on your devices.</>
                      : <><PhosphorIcon className="bi bi-hdd" /> Saved in GenPrompt only — won't appear on your connected calendars.</>}
                </small>
              </label>
              <div className="calendar-event-form-grid">
                <label>
                  <span>Starts</span>
                  <input type="datetime-local" value={eventForm.startDateTime} onChange={e => setEventForm(f => ({ ...f, startDateTime: e.target.value }))} />
                </label>
                <label>
                  <span>Ends</span>
                  <input type="datetime-local" value={eventForm.endDateTime} onChange={e => setEventForm(f => ({ ...f, endDateTime: e.target.value }))} />
                </label>
              </div>
              <div className="calendar-event-form-grid">
                <label>
                  <span>Reminder</span>
                  <select value={eventForm.reminderMinutes} onChange={e => setEventForm(f => ({ ...f, reminderMinutes: e.target.value }))}>
                    <option value="">No reminder</option>
                    <option value="5">5 minutes before</option>
                    <option value="15">15 minutes before</option>
                    <option value="30">30 minutes before</option>
                    <option value="60">1 hour before</option>
                    <option value="1440">1 day before</option>
                  </select>
                </label>
                <label>
                  <span>Location or room</span>
                  <input value={eventForm.location} onChange={e => setEventForm(f => ({ ...f, location: e.target.value }))} placeholder="Office, Zoom, clinic..." />
                </label>
              </div>
              <label>
                <span>Video link</span>
                <input value={eventForm.meetingLink} onChange={e => setEventForm(f => ({ ...f, meetingLink: e.target.value }))} placeholder="https://..." />
              </label>
              <label>
                <span>Notes</span>
                <textarea value={eventForm.description} onChange={e => setEventForm(f => ({ ...f, description: e.target.value }))} placeholder="Agenda, prep notes, people to follow up with..." rows={3} />
              </label>
              <label className="calendar-check-row">
                <input type="checkbox" checked={eventForm.isAllDay} onChange={e => setEventForm(f => ({ ...f, isAllDay: e.target.checked }))} />
                <span>All-day appointment</span>
              </label>
            </div>

            <div className="calendar-modal-actions">
              <button className="btn btn-secondary" onClick={() => setShowEventModal(false)} disabled={savingEvent}>Cancel</button>
              <button className="btn btn-primary" onClick={createNativeEvent} disabled={savingEvent}>
                {savingEvent ? <><div className="spinner" />Saving...</> : <><PhosphorIcon className="bi bi-check2" />Save appointment</>}
              </button>
            </div>
          </div>
        </div>
      )}

      {showCalendarModal && (
        <div className="calendar-modal-backdrop" role="dialog" aria-modal="true" aria-labelledby="calendar-grid-title">
          <div className="card calendar-grid-modal">
            <div className="calendar-grid-head">
              <div>
                <div className="calendar-kicker">Connected calendar view</div>
                <h2 id="calendar-grid-title">
                  {calendarModalMonth.toLocaleDateString('en-US', { month: 'long', year: 'numeric' })}
                </h2>
                <p>Google, Outlook, and GenPrompt appointments in one visual calendar.</p>
              </div>
              <div className="calendar-grid-actions">
                <button className="btn btn-secondary" onClick={() => void moveCalendarModalMonth(-1)} disabled={loadingCalendarModal}>
                  <PhosphorIcon className="bi bi-chevron-left" />
                </button>
                <button className="btn btn-secondary" onClick={() => void jumpCalendarModalToToday()} disabled={loadingCalendarModal}>
                  Today
                </button>
                <button className="btn btn-secondary" onClick={() => void moveCalendarModalMonth(1)} disabled={loadingCalendarModal}>
                  <PhosphorIcon className="bi bi-chevron-right" />
                </button>
                <button className="btn btn-primary" onClick={() => { setShowCalendarModal(false); openEventModal() }}>
                  <PhosphorIcon className="bi bi-plus-lg" />New
                </button>
                <button className="calendar-modal-close" type="button" onClick={() => setShowCalendarModal(false)} aria-label="Close calendar">
                  <PhosphorIcon className="bi bi-x-lg" />
                </button>
              </div>
            </div>

            <div className="calendar-grid-provider-row">
              {[
                ['genprompt', 'GenPrompt'],
                ['google', 'Google'],
                ['microsoft', 'Outlook'],
              ].map(([provider, label]) => (
                <span key={provider}>
                  <PhosphorIcon className={`bi ${PROVIDER_ICON[provider] ?? 'bi-calendar'}`} style={{ color: PROVIDER_COLOR[provider] ?? '#64748b' }} />
                  {label}
                </span>
              ))}
              {loadingCalendarModal && <span className="calendar-grid-loading"><div className="spinner" />Loading events...</span>}
            </div>

            <div className="calendar-month-grid">
              {['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'].map(day => (
                <div key={day} className="calendar-month-weekday">{day}</div>
              ))}
              {calendarModalDays.map(day => {
                const dayEvents = eventsForDay(day)
                const isToday = day.toDateString() === new Date().toDateString()
                const isOutside = day.getMonth() !== calendarModalMonth.getMonth()
                return (
                  <div key={day.toISOString()} className={`calendar-month-cell ${isToday ? 'today' : ''} ${isOutside ? 'outside' : ''}`}>
                    <div className="calendar-month-cell-date">{day.getDate()}</div>
                    <div className="calendar-month-cell-events">
                      {dayEvents.slice(0, 4).map(evt => (
                        <button
                          key={evt.id}
                          className="calendar-month-event"
                          type="button"
                          onClick={() => openMeeting(evt)}
                          style={{ borderLeftColor: PROVIDER_COLOR[evt.provider] ?? '#64748b' }}
                          title={`${evt.title} - ${formatEventTime(evt)}`}
                        >
                          <span>{formatEventTime(evt)}</span>
                          <strong>{evt.title}</strong>
                        </button>
                      ))}
                      {dayEvents.length > 4 && (
                        <div className="calendar-month-more">+{dayEvents.length - 4} more</div>
                      )}
                    </div>
                  </div>
                )
              })}
            </div>
          </div>
        </div>
      )}

      {/* Header */}
      {/* Capture chooser */}
      {captureOpen && (
        <div className="calendar-modal-backdrop" role="dialog" aria-modal="true">
          <div className="card calendar-modal-card">
            <div className="calendar-modal-copy">
              <h2><PhosphorIcon className="bi bi-camera" /> Capture your schedule</h2>
              <p>Read events off a screenshot, or share your screen and I'll pull the schedule straight from what's on it — then add them to your calendar.</p>
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 12, marginTop: 6 }}>
              <button className="btn btn-secondary" style={{ flexDirection: 'column', height: 'auto', padding: '20px 12px', gap: 6 }} onClick={() => screenshotInputRef.current?.click()}>
                <PhosphorIcon className="bi bi-image" style={{ fontSize: '1.6rem' }} /><strong>From a screenshot</strong><span style={{ fontSize: 11, opacity: 0.7 }}>An email, invite, or poster</span>
              </button>
              <button className="btn btn-secondary" style={{ flexDirection: 'column', height: 'auto', padding: '20px 12px', gap: 6 }} onClick={startScreenShare}>
                <PhosphorIcon className="bi bi-display" style={{ fontSize: '1.6rem' }} /><strong>Share my screen</strong><span style={{ fontSize: 11, opacity: 0.7 }}>Read a calendar that's on screen</span>
              </button>
            </div>
            <div className="calendar-modal-actions" style={{ marginTop: 20, justifyContent: 'flex-end' }}>
              <button
                className="btn"
                onClick={() => setCaptureOpen(false)}
                style={{ background: '#fef2f2', color: '#dc2626', border: '1px solid #fca5a5', fontWeight: 700, padding: '9px 22px' }}
              >
                <PhosphorIcon className="bi bi-x-lg" style={{ marginRight: 6 }} />Cancel
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Screen share preview */}
      {screenSharing && (
        <div className="calendar-modal-backdrop" role="dialog" aria-modal="true">
          <div className="card calendar-modal-card" style={{ maxWidth: 720 }}>
            <div className="calendar-modal-copy">
              <h2><PhosphorIcon className="bi bi-display" /> Reading your screen</h2>
              <p>Bring the calendar or email you want onto the shared screen, then capture. Nothing is recorded — I only read the single frame you capture.</p>
            </div>
            <video ref={screenVideoRef} autoPlay playsInline muted style={{ width: '100%', borderRadius: 12, background: '#000', maxHeight: '46vh' }} />
            <div className="calendar-modal-actions">
              <button className="btn btn-secondary" onClick={stopScreenShare} disabled={captureBusy}>Stop sharing</button>
              <button className="btn btn-primary" onClick={captureScreenFrame} disabled={captureBusy}>
                {captureBusy ? <><div className="spinner" />Reading…</> : <><PhosphorIcon className="bi bi-camera" />Capture &amp; read</>}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Busy (screenshot path) */}
      {captureBusy && !screenSharing && !extracted && (
        <div className="calendar-modal-backdrop">
          <div className="card calendar-modal-card" style={{ textAlign: 'center', padding: 30 }}>
            <div className="spinner" /><p style={{ marginTop: 12 }}>Reading events…</p>
          </div>
        </div>
      )}

      {/* Extracted events review */}
      {extracted && (
        <div className="calendar-modal-backdrop" role="dialog" aria-modal="true">
          <div className="card calendar-modal-card" style={{ maxWidth: 560 }}>
            <div className="calendar-modal-copy">
              <h2><PhosphorIcon className="bi bi-calendar-check" /> {extracted.length} event{extracted.length === 1 ? '' : 's'} found</h2>
              <p>Pick which to add to your calendar.</p>
            </div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 8, maxHeight: '46vh', overflowY: 'auto' }}>
              {extracted.map((ev, i) => (
                <label key={i} style={{ display: 'flex', gap: 10, alignItems: 'flex-start', padding: '10px 12px', border: '1px solid var(--border-color,#e5e7eb)', borderRadius: 10, cursor: 'pointer' }}>
                  <input type="checkbox" checked={!!extractedPick[i]} onChange={e => setExtractedPick(p => ({ ...p, [i]: e.target.checked }))} style={{ marginTop: 3 }} />
                  <div style={{ minWidth: 0 }}>
                    <strong>{ev.title || 'Untitled event'}</strong>
                    <div style={{ fontSize: 12, color: 'var(--text-muted,#94a3b8)' }}>
                      {ev.date}{ev.startTime ? ` · ${ev.startTime}${ev.endTime ? `–${ev.endTime}` : ''}` : ev.allDay ? ' · all day' : ''}{ev.location ? ` · ${ev.location}` : ''}
                    </div>
                  </div>
                </label>
              ))}
            </div>
            <div className="calendar-modal-actions">
              <button className="btn btn-secondary" onClick={() => setExtracted(null)} disabled={addingEvents}>Cancel</button>
              <button className="btn btn-primary" onClick={addExtractedEvents} disabled={addingEvents || !Object.values(extractedPick).some(Boolean)}>
                {addingEvents ? <><div className="spinner" />Adding…</> : <><PhosphorIcon className="bi bi-check2" />Add to calendar</>}
              </button>
            </div>
          </div>
        </div>
      )}

      <input ref={screenshotInputRef} type="file" accept="image/*" hidden onChange={e => onScreenshotFile(e.target.files)} />

      <div className="calendar-hero">
        <div className="calendar-hero-copy">
          <div className="calendar-kicker">Workday command center</div>
          <h1>
            <span className="calendar-title-icon"><PhosphorIcon className="bi bi-calendar3" /></span>
            Calendar
          </h1>
          <p>
            {new Date().toLocaleDateString('en-US', { weekday: 'long', year: 'numeric', month: 'long', day: 'numeric' })}
          </p>
        </div>
        <div className="calendar-hero-actions">
          <button className="btn btn-secondary calendar-soft-btn" onClick={() => void openCalendarModal()}>
            <PhosphorIcon className="bi bi-calendar3" />Show calendar
          </button>
          <button className="btn btn-primary calendar-premium-btn" onClick={openEventModal}>
            <PhosphorIcon className="bi bi-plus-lg" />New appointment
          </button>
          <button className="btn btn-secondary calendar-soft-btn" onClick={() => { setError(''); setCaptureOpen(true) }}>
            <PhosphorIcon className="bi bi-camera" />Capture schedule
          </button>
          {connections.length > 0 && (
            <button className="btn btn-secondary calendar-soft-btn" onClick={handleSync} disabled={syncing}>
              {syncing ? <><div className="spinner" style={{ width: 14, height: 14 }} />Syncing…</> : <><PhosphorIcon className="bi bi-arrow-clockwise" />Sync</>}
            </button>
          )}
          <Link to="/calendar/availability" className="btn btn-secondary calendar-soft-btn">
            <PhosphorIcon className="bi bi-clock" />Check Availability
          </Link>
          <Link to="/calendar/follow-ups" className="btn btn-secondary calendar-soft-btn">
            <PhosphorIcon className="bi bi-list-check" />Follow-ups
          </Link>
        </div>
      </div>

      {error && (
        <div className="calendar-error">
          <PhosphorIcon className="bi bi-exclamation-circle" />{error}
          <button onClick={() => setError('')}><PhosphorIcon className="bi bi-x" /></button>
        </div>
      )}

      {/* Tab bar */}
      <div className="calendar-tabs">
        {(['calendar', 'avatar'] as const).map(tab => (
          <button key={tab} className={activeTab === tab ? 'active' : ''} onClick={() => setActiveTab(tab)}>
            <PhosphorIcon className={`bi ${tab === 'calendar' ? 'bi-calendar3' : 'bi-person-video2'}`} />
            {tab === 'calendar' ? 'Calendar' : 'AI Work Agent'}
          </button>
        ))}
      </div>

      {activeTab === 'avatar' && (
        <div className="card calendar-avatar-workspace">
          <div className="calendar-section-head">
            <span><PhosphorIcon className="bi bi-stars" />AI Work Agent</span>
            <p>Ask about your schedule, meeting prep, priorities, and follow-up reminders.</p>
          </div>
          <AvatarPanel mode="calendar" />
        </div>
      )}

      {activeTab === 'calendar' && (
        <div className="calendar-layout">

          {/* Main calendar area */}
          <div className="calendar-main-col">

            {/* No connections banner */}
            {connections.length === 0 && (
              <div className="card calendar-connect-card">
                <div className="calendar-connect-icon"><PhosphorIcon className="bi bi-calendar-plus" /></div>
                <div className="calendar-connect-title">Connect your calendars</div>
                <div className="calendar-connect-copy">
                  Connect Microsoft Outlook to see all your meetings in one place and unlock AI-powered meeting prep, follow-ups, and availability checking. Google Calendar support is coming soon.
                </div>
                <div className="calendar-connect-actions">
                  <button className="btn btn-primary calendar-google-btn" disabled title="Google Calendar is coming soon" style={{ opacity: 0.55, cursor: 'not-allowed' }}>
                    <PhosphorIcon className="bi bi-google" />Google Calendar
                    <span style={{ marginLeft: 8, fontSize: 10, fontWeight: 800, textTransform: 'uppercase', letterSpacing: '0.03em', background: 'rgba(255,255,255,0.25)', padding: '2px 7px', borderRadius: 999 }}>Coming soon</span>
                  </button>
                  <button className="btn btn-secondary calendar-outlook-btn" onClick={() => openConnectModal('microsoft')} disabled={connectingProvider === 'microsoft'}>
                    {connectingProvider === 'microsoft' ? <><div className="spinner" />Connecting…</> : <><PhosphorIcon className="bi bi-microsoft" />Connect Outlook Calendar</>}
                  </button>
                </div>
              </div>
            )}

            {/* View switcher + events */}
            <div className="calendar-viewbar">
                  {(['today', 'week', 'agenda'] as const).map(v => (
                    <button key={v} className={view === v ? 'active' : ''} onClick={() => setView(v)}>
                      {v.charAt(0).toUpperCase() + v.slice(1)}
                    </button>
                  ))}
                  {view === 'week' && (
                    <div className="calendar-week-nav">
                      <button className="btn btn-secondary" style={{ padding: '4px 8px', fontSize: 12 }} onClick={() => setWeekOffset(o => o - 1)}><PhosphorIcon className="bi bi-chevron-left" /></button>
                      <button className="btn btn-secondary" style={{ padding: '4px 8px', fontSize: 12 }} onClick={() => setWeekOffset(0)}>This week</button>
                      <button className="btn btn-secondary" style={{ padding: '4px 8px', fontSize: 12 }} onClick={() => setWeekOffset(o => o + 1)}><PhosphorIcon className="bi bi-chevron-right" /></button>
                    </div>
                  )}
                  <button className="btn btn-primary calendar-mini-add" onClick={openEventModal}>
                    <PhosphorIcon className="bi bi-plus-lg" />Add
                  </button>
                </div>

                {events.length === 0 && (
                  <div className="card calendar-empty-events">
                    <PhosphorIcon className="bi bi-calendar-check" />
                    <div>No appointments for this period</div>
                    <button className="btn btn-primary" style={{ marginTop: 12, fontSize: 12 }} onClick={openEventModal}>
                      <PhosphorIcon className="bi bi-plus-lg" />Create appointment
                    </button>
                  </div>
                )}

                {/* Events list */}
                <div className="calendar-events-list">
                  {(view === 'today' ? todayEvents : upcomingEvents).map(evt => (
                    <div
                      key={evt.id}
                      className="card calendar-event-card"
                      onClick={() => openMeeting(evt)}
                      style={{
                        padding: '12px 16px', cursor: 'pointer',
                        borderLeft: `3px solid ${PROVIDER_COLOR[evt.provider] ?? '#888'}`,
                        opacity: isPast(evt) ? 0.6 : 1,
                        transition: 'all 0.15s',
                      }}
                    >
                      <div style={{ display: 'flex', alignItems: 'flex-start', gap: 10 }}>
                        <div style={{ flexShrink: 0, marginTop: 2 }}>
                          {isEventNow(evt)
                            ? <span style={{ background: '#22c55e', color: '#fff', fontSize: 10, fontWeight: 700, padding: '2px 6px', borderRadius: 8 }}>NOW</span>
                            : <PhosphorIcon className={`bi ${PROVIDER_ICON[evt.provider] ?? 'bi-calendar'}`} style={{ color: PROVIDER_COLOR[evt.provider] ?? '#888', fontSize: 14 }} />
                          }
                        </div>
                        <div style={{ flex: 1 }}>
                          <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 2 }}>{evt.title}</div>
                          <div style={{ fontSize: 12, color: 'var(--text-secondary)', display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
                            <span>
                              <PhosphorIcon className="bi bi-clock" style={{ marginRight: 3 }} />
                              {new Date(evt.startDateTime).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}
                              {' — '}
                              {new Date(evt.endDateTime).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}
                            </span>
                            {evt.attendees.length > 0 && (
                              <span><PhosphorIcon className="bi bi-people" style={{ marginRight: 3 }} />{evt.attendees.length} attendee{evt.attendees.length !== 1 ? 's' : ''}</span>
                            )}
                            {evt.meetingLink && <span style={{ color: 'var(--primary)' }}><PhosphorIcon className="bi bi-camera-video" style={{ marginRight: 3 }} />Video link</span>}
                            {evt.provider === 'genprompt' && <span style={{ color: '#d62976', fontWeight: 600 }}><PhosphorIcon className="bi bi-stars" style={{ marginRight: 3 }} />GenPrompt</span>}
                            {evt.reminderMinutes != null && <span><PhosphorIcon className="bi bi-bell" style={{ marginRight: 3 }} />{evt.reminderMinutes === 0 ? 'At start' : `${evt.reminderMinutes} min reminder`}</span>}
                          </div>
                          {evt.location && <div style={{ fontSize: 11, color: 'var(--text-muted)', marginTop: 2 }}><PhosphorIcon className="bi bi-geo-alt" style={{ marginRight: 3 }} />{evt.location}</div>}
                        </div>
                        <div style={{ display: 'flex', gap: 6, flexShrink: 0 }}>
                          {evt.workspaceId
                            ? <span style={{ fontSize: 10, background: 'rgba(99,102,241,0.1)', color: 'var(--primary)', padding: '2px 6px', borderRadius: 8, fontWeight: 600 }}><PhosphorIcon className="bi bi-journal-text" style={{ marginRight: 2 }} />Workspace</span>
                            : <span style={{ fontSize: 10, background: 'var(--bg-secondary)', color: 'var(--text-muted)', padding: '2px 6px', borderRadius: 8 }}>Open</span>
                          }
                          <PhosphorIcon className="bi bi-chevron-right" style={{ color: 'var(--text-muted)', fontSize: 12 }} />
                        </div>
                      </div>
                    </div>
                  ))}
                </div>
          </div>

          {/* Right sidebar */}
          <div className="calendar-right-rail">

            {/* AI Daily Briefing */}
            {briefing && (
              <div className="card calendar-side-card calendar-briefing-card">
                <div className="calendar-side-title"><PhosphorIcon className="bi bi-lightning-charge" />AI Briefing</div>
                <p>{briefing.summary}</p>
                {briefing.meetingsNeedingPrep.length > 0 && (
                  <div style={{ marginBottom: 8 }}>
                    <div style={{ fontSize: 11, fontWeight: 700, color: '#f59e0b', marginBottom: 4 }}><PhosphorIcon className="bi bi-exclamation-triangle" style={{ marginRight: 4 }} />NEEDS PREP</div>
                    {briefing.meetingsNeedingPrep.map((m, i) => <div key={i} style={{ fontSize: 12, color: 'var(--text-secondary)', marginBottom: 2 }}>• {m}</div>)}
                  </div>
                )}
                {briefing.bestFocusBlock && (
                  <div className="calendar-focus-pill">
                    <PhosphorIcon className="bi bi-shield-check" style={{ marginRight: 6 }} /><strong>Focus block:</strong> {briefing.bestFocusBlock}
                  </div>
                )}
                {briefing.suggestedPriorities.length > 0 && (
                  <div style={{ marginTop: 10 }}>
                    <div style={{ fontSize: 11, fontWeight: 700, color: 'var(--text-muted)', marginBottom: 4 }}>TODAY'S PRIORITIES</div>
                    {briefing.suggestedPriorities.slice(0, 3).map((p, i) => (
                      <div key={i} style={{ fontSize: 12, color: 'var(--text-secondary)', marginBottom: 2 }}>
                        <span style={{ color: 'var(--primary)', marginRight: 4 }}>{i + 1}.</span>{p}
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )}

            {/* Avatar (compact) */}
            <div className="card calendar-side-card calendar-work-agent">
              <div className="calendar-side-title"><PhosphorIcon className="bi bi-person-video2" />Work Agent</div>
              <AvatarPanel mode="calendar" compact />
            </div>

            {/* Connected calendars */}
            <div className="card calendar-side-card">
              <div className="calendar-side-title"><PhosphorIcon className="bi bi-calendar2-week" />Calendars</div>
              {connections.length === 0
                ? <div style={{ fontSize: 12, color: 'var(--text-muted)' }}>No calendars connected</div>
                : connections.map(c => (
                  <div key={c.id} style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
                    <PhosphorIcon className={`bi ${PROVIDER_ICON[c.provider]}`} style={{ color: PROVIDER_COLOR[c.provider] }} />
                    <div style={{ flex: 1 }}>
                      <div style={{ fontSize: 12, fontWeight: 600 }}>{c.displayName || c.calendarEmail}</div>
                      <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>{c.calendarEmail}</div>
                    </div>
                    <button onClick={() => handleDisconnect(c.id)} style={{ background: 'none', border: 'none', cursor: 'pointer', color: 'var(--text-muted)', padding: 2 }} title="Disconnect"><PhosphorIcon className="bi bi-x" /></button>
                  </div>
                ))
              }
              <div style={{ display: 'flex', flexDirection: 'column', gap: 6, marginTop: connections.length > 0 ? 10 : 0 }}>
                <button className="btn btn-secondary" style={{ fontSize: 11, height: 28, opacity: 0.6 }} disabled title="Google Calendar is coming soon">
                  <PhosphorIcon className="bi bi-google" />Google Calendar (soon)
                </button>
                <button className="btn btn-secondary" style={{ fontSize: 11, height: 28 }} onClick={() => openConnectModal('microsoft')}>
                  <PhosphorIcon className="bi bi-microsoft" />Add Outlook Calendar
                </button>
              </div>
            </div>

            {/* Quick links */}
            <div className="card calendar-side-card">
              <div className="calendar-side-title"><PhosphorIcon className="bi bi-command" />Quick Links</div>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                {[
                  { to: '/calendar/availability', icon: 'bi-clock', label: 'Check Availability' },
                  { to: '/calendar/follow-ups', icon: 'bi-list-check', label: 'Follow-up Hub' },
                ].map(l => (
                  <Link key={l.to} to={l.to} style={{ display: 'flex', alignItems: 'center', gap: 8, padding: '6px 8px', borderRadius: 6, color: 'var(--text-secondary)', textDecoration: 'none', fontSize: 12, background: 'var(--bg-secondary)' }}>
                    <PhosphorIcon className={`bi ${l.icon}`} style={{ color: 'var(--primary)' }} />{l.label}
                  </Link>
                ))}
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
