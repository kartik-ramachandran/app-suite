import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import type { StageLabel } from '../gamefield/SnapField'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import { RadialAreaChart, GoalBubbleChart, RelationshipNetwork, AREA_COLORS } from './LifeCharts'
import LifeBalanceWheel from '../components/LifeBalanceWheel'
import UpcomingDates from '../components/UpcomingDates'
import LifeVoiceRecorder from '../components/LifeVoiceRecorder'
import LifeScenarios from '../components/LifeScenarios'
import {
  createLifeDecision,
  createLifeGoal,
  askLifeCoach,
  deleteLifeDecision,
  deleteLifeCloudLink,
  deleteLifeEvidence,
  deleteLifeGoal,
  deleteLifeRelationship,
  downloadLifeEvidence,
  getLifeEvidence,
  getLifeTracker,
  getLifeWeeklySummary,
  markLifeRelationshipContacted,
  saveLifeCheckIn,
  saveLifeCloudLink,
  saveLifeRelationship,
  syncLifeCloudLink,
  updateLifeDecision,
  uploadLifeEvidence,
  updateLifeGoal,
  updateLifeRelationship,
} from '../services/api'
import './LifeTracker.css'

interface LifeCheckIn {
  id: number
  date: string
  mood: number
  energy: number
  focus: number
  sleepHours: number
  exerciseMinutes: number
  screenMinutes: number
  moneySpent: number
  topTasks: string
  gratitude: string
  notes: string
}

interface LifeGoal {
  id: number
  title: string
  area: string
  targetDate?: string
  progress: number
  status: 'active' | 'paused' | 'done'
  notes: string
}

interface LifeDecision {
  id: number
  date: string
  title: string
  reasoning: string
  outcome: string
  reviewDate?: string
}

interface LifeRelationship {
  id: number
  name: string
  circle: string
  lastContactedAt?: string
  importance: number
  notes: string
}

interface LifeEvidence {
  id: number
  fileName: string
  contentType: string
  fileSize: number
  evidenceType: string
  notes: string
  createdAt: string
}

interface LifeEvidenceDetail extends LifeEvidence {
  extractedText: string
}

interface LifeCloudLink {
  id: number
  provider: 'Google Drive' | 'OneDrive' | string
  displayName: string
  url: string
  notes: string
  syncStatus: string
  lastSyncedAt?: string
  extractedPreview: string
  createdAt: string
  updatedAt: string
}

interface LifeStats {
  avgMood: number
  avgEnergy: number
  avgFocus: number
  avgSleep: number
  exerciseMinutes: number
  screenMinutes: number
  activeGoals: number
  completedGoals: number
  staleRelationships: number
  lifeScore: number
}

interface LifeInsight {
  kind: string
  text: string
  value?: number
}

interface RelationshipNudge {
  id: number
  name: string
  circle: string
  importance: number
  lastContactedAt?: string
  daysSinceContact?: number
  thresholdDays: number
}

interface DecisionReview {
  id: number
  title: string
  reviewDate: string
  daysUntil: number
  hasOutcome: boolean
}

interface LifeInsights {
  trends: LifeInsight[]
  relationshipNudges: RelationshipNudge[]
  decisionReviews: DecisionReview[]
}

interface LifeSummary {
  checkIns: LifeCheckIn[]
  goals: LifeGoal[]
  decisions: LifeDecision[]
  relationships: LifeRelationship[]
  evidence: LifeEvidence[]
  cloudLinks: LifeCloudLink[]
  stats: LifeStats
  insights: LifeInsights
}

const today = () => new Date().toISOString().slice(0, 10)
const toDateInput = (value?: string | null) => value ? new Date(value).toISOString().slice(0, 10) : ''

function formatFileSize(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

const emptySummary: LifeSummary = {
  checkIns: [],
  goals: [],
  decisions: [],
  relationships: [],
  evidence: [],
  cloudLinks: [],
  stats: {
    avgMood: 0,
    avgEnergy: 0,
    avgFocus: 0,
    avgSleep: 0,
    exerciseMinutes: 0,
    screenMinutes: 0,
    activeGoals: 0,
    completedGoals: 0,
    staleRelationships: 0,
    lifeScore: 0,
  },
  insights: {
    trends: [],
    relationshipNudges: [],
    decisionReviews: [],
  },
}

function normalizeLifeSummary(data: Partial<LifeSummary> | null | undefined): LifeSummary {
  return {
    ...emptySummary,
    ...data,
    checkIns: data?.checkIns ?? [],
    goals: data?.goals ?? [],
    decisions: data?.decisions ?? [],
    relationships: data?.relationships ?? [],
    evidence: data?.evidence ?? [],
    cloudLinks: data?.cloudLinks ?? [],
    stats: {
      ...emptySummary.stats,
      ...(data?.stats ?? {}),
    },
    insights: {
      ...emptySummary.insights,
      ...(data?.insights ?? {}),
      trends: data?.insights?.trends ?? [],
      relationshipNudges: data?.insights?.relationshipNudges ?? [],
      decisionReviews: data?.insights?.decisionReviews ?? [],
    },
  }
}


// The three desks a run on this page actually passes through.
const LIFE_TRACKER_STAGES: [StageLabel, StageLabel, StageLabel] = [
  { name: 'Intake', role: 'your entry' },
  { name: 'Coach', role: 'reads the week' },
  { name: 'Journal', role: 'saves to your log' },
]

type LifeTab = 'today' | 'trends' | 'goals' | 'people' | 'coach' | 'evidence'

const LIFE_TABS: { id: LifeTab; label: string; icon: string }[] = [
  { id: 'today',    label: 'Today',        icon: 'bi-sun' },
  { id: 'trends',   label: 'Trends',       icon: 'bi-graph-up' },
  { id: 'goals',    label: 'Goals',        icon: 'bi-bullseye' },
  { id: 'people',   label: 'People',       icon: 'bi-people' },
  { id: 'coach',    label: 'Coach',        icon: 'bi-stars' },
  { id: 'evidence', label: 'Evidence',     icon: 'bi-folder2-open' },
]

export default function LifeTracker() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('life-tracker')
  const [summary, setSummary] = useState<LifeSummary>(emptySummary)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')
  const [range, setRange] = useState(30)

  const [checkIn, setCheckIn] = useState({
    date: today(),
    mood: 7,
    energy: 7,
    focus: 7,
    sleepHours: 7.5,
    exerciseMinutes: 30,
    screenMinutes: 240,
    moneySpent: 0,
    topTasks: '',
    gratitude: '',
    notes: '',
  })

  const [tab, setTab] = useState<LifeTab>('today')
  const [goal, setGoal] = useState({ title: '', area: 'Health', targetDate: '', progress: 20, notes: '' })
  const [goalStatus, setGoalStatus] = useState<'active' | 'paused' | 'done'>('active')
  const [editingGoalId, setEditingGoalId] = useState<number | null>(null)
  const [decision, setDecision] = useState({ date: today(), title: '', reasoning: '', outcome: '', reviewDate: '' })
  const [editingDecisionId, setEditingDecisionId] = useState<number | null>(null)
  const [relationship, setRelationship] = useState({ name: '', circle: 'Friends', lastContactedAt: today(), importance: 7, notes: '' })
  const [editingRelationshipId, setEditingRelationshipId] = useState<number | null>(null)
  const [evidenceNotes, setEvidenceNotes] = useState('')
  const [selectedEvidence, setSelectedEvidence] = useState<LifeEvidenceDetail | null>(null)
  const [evidenceLoadingId, setEvidenceLoadingId] = useState<number | null>(null)
  const [cloudLink, setCloudLink] = useState({ provider: 'Google Drive', displayName: '', url: '', notes: '' })
  const [syncingCloudId, setSyncingCloudId] = useState<number | null>(null)
  const [uploadingEvidence, setUploadingEvidence] = useState(false)
  const [dragOverEvidence, setDragOverEvidence] = useState(false)
  const evidenceInputRef = useRef<HTMLInputElement>(null)
  const [coachQuestion, setCoachQuestion] = useState('')
  const [coachLoading, setCoachLoading] = useState(false)
  const [coachMessages, setCoachMessages] = useState<{ role: 'user' | 'coach'; content: string }[]>([
    { role: 'coach', content: 'Bring me a decision, a dilemma, or a pattern you are noticing. I will use your tracker and uploaded evidence to help you think it through.' }
  ])
  const [weeklySummary, setWeeklySummary] = useState('')
  const [weeklyLoading, setWeeklyLoading] = useState(false)

  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Life Tracker' }])
    return () => setBreadcrumbs([])
  }, [setBreadcrumbs])

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph): the
  // star becomes a draft goal — title from the star, brief in the notes. Prefill
  // only; nothing is saved until the user presses Add.
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/life-tracker')
    if (!handoff?.input) return
    setGoal(current => ({
      ...current,
      title: handoff.title?.slice(0, 120) || handoff.input.split('\n')[0].slice(0, 120),
      notes: handoff.input,
    }))
  }, [])

  const load = async () => {
    setLoading(true)
    setError('')
    try {
      const res = await getLifeTracker(range)
      const normalized = normalizeLifeSummary(res.data)
      setSummary(normalized)
      const todayCheckIn = normalized.checkIns.find((x: LifeCheckIn) => x.date.slice(0, 10) === today())
      if (todayCheckIn) {
        setCheckIn({
          date: today(),
          mood: todayCheckIn.mood,
          energy: todayCheckIn.energy,
          focus: todayCheckIn.focus,
          sleepHours: todayCheckIn.sleepHours,
          exerciseMinutes: todayCheckIn.exerciseMinutes,
          screenMinutes: todayCheckIn.screenMinutes,
          moneySpent: Number(todayCheckIn.moneySpent ?? 0),
          topTasks: todayCheckIn.topTasks ?? '',
          gratitude: todayCheckIn.gratitude ?? '',
          notes: todayCheckIn.notes ?? '',
        })
      }
    } catch (e: any) {
      setError(e.response?.data?.message ?? 'Life tracker could not load.')
    } finally {
      setLoading(false)
    }
  }

  useEffect(() => {
    load()
  }, [range])

  const latest = summary.checkIns[summary.checkIns.length - 1]
  const trends = useMemo(() => buildTrends(summary.checkIns), [summary.checkIns])
  const areaScores = useMemo(() => buildAreaScores(summary), [summary])

  const submitCheckIn = async () => {
    setSaving(true)
    setError('')
    try {
      await saveLifeCheckIn({ ...checkIn, date: new Date(checkIn.date).toISOString() })
      await load()
    } catch (e: any) {
      setError(e.response?.data?.message ?? 'Check-in could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  const markContacted = async (id: number) => {
    try {
      await markLifeRelationshipContacted(id)
      await load()
    } catch (e: any) {
      setError(e.response?.data?.message ?? 'Could not update contact date.')
    }
  }

  const runWeeklySummary = async () => {
    setWeeklyLoading(true)
    setError('')
    try {
      const res = await getLifeWeeklySummary(Math.min(range, 31))
      setWeeklySummary(res.data?.summary ?? '')
    } catch (e: any) {
      setError(e.response?.data?.message ?? 'Weekly summary could not be generated.')
    } finally {
      setWeeklyLoading(false)
    }
  }

  const submitGoal = async () => {
    if (!goal.title.trim()) return
    setSaving(true)
    setError('')
    try {
      const payload = {
        title: goal.title,
        area: goal.area,
        targetDate: goal.targetDate ? new Date(goal.targetDate).toISOString() : null,
        progress: goal.progress,
        status: goalStatus,
        notes: goal.notes,
      }
      if (editingGoalId) await updateLifeGoal(editingGoalId, payload)
      else await createLifeGoal(payload)
      resetGoalForm()
      await load()
    } catch (e: any) {
      setError(e.response?.data?.message ?? e.response?.data ?? 'Goal could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  const nudgeGoal = async (g: LifeGoal, delta: number) => {
    const nextProgress = Math.max(0, Math.min(100, g.progress + delta))
    await updateLifeGoal(g.id, { ...g, progress: nextProgress, status: nextProgress >= 100 ? 'done' : g.status })
    await load()
  }

  const editGoal = (g: LifeGoal) => {
    setEditingGoalId(g.id)
    setGoal({
      title: g.title,
      area: g.area,
      targetDate: toDateInput(g.targetDate),
      progress: g.progress,
      notes: g.notes ?? '',
    })
    setGoalStatus(g.status)
  }

  const resetGoalForm = () => {
    setEditingGoalId(null)
    setGoal({ title: '', area: 'Health', targetDate: '', progress: 20, notes: '' })
    setGoalStatus('active')
  }

  const setGoalStatusDirect = async (g: LifeGoal, status: LifeGoal['status']) => {
    await updateLifeGoal(g.id, { ...g, status, progress: status === 'done' ? 100 : g.progress })
    await load()
  }

  const submitDecision = async () => {
    if (!decision.title.trim()) return
    setSaving(true)
    setError('')
    try {
      const payload = {
        ...decision,
        date: decision.date ? new Date(decision.date).toISOString() : new Date().toISOString(),
        reviewDate: decision.reviewDate ? new Date(decision.reviewDate).toISOString() : null,
      }
      if (editingDecisionId) await updateLifeDecision(editingDecisionId, payload)
      else await createLifeDecision(payload)
      resetDecisionForm()
      await load()
    } catch (e: any) {
      setError(e.response?.data?.message ?? e.response?.data ?? 'Decision could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  const editDecision = (d: LifeDecision) => {
    setEditingDecisionId(d.id)
    setDecision({
      title: d.title,
      date: toDateInput(d.date) || today(),
      reasoning: d.reasoning ?? '',
      outcome: d.outcome ?? '',
      reviewDate: toDateInput(d.reviewDate),
    })
  }

  const resetDecisionForm = () => {
    setEditingDecisionId(null)
    setDecision({ date: today(), title: '', reasoning: '', outcome: '', reviewDate: '' })
  }

  const submitRelationship = async () => {
    if (!relationship.name.trim()) return
    setSaving(true)
    setError('')
    try {
      const payload = {
        ...relationship,
        lastContactedAt: relationship.lastContactedAt ? new Date(relationship.lastContactedAt).toISOString() : null,
      }
      if (editingRelationshipId) await updateLifeRelationship(editingRelationshipId, payload)
      else await saveLifeRelationship(payload)
      resetRelationshipForm()
      await load()
    } catch (e: any) {
      setError(e.response?.data?.message ?? e.response?.data ?? 'Relationship could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  const editRelationship = (r: LifeRelationship) => {
    setEditingRelationshipId(r.id)
    setRelationship({
      name: r.name,
      circle: r.circle,
      lastContactedAt: toDateInput(r.lastContactedAt),
      importance: r.importance,
      notes: r.notes ?? '',
    })
  }

  const resetRelationshipForm = () => {
    setEditingRelationshipId(null)
    setRelationship({ name: '', circle: 'Friends', lastContactedAt: today(), importance: 7, notes: '' })
  }

  const submitEvidence = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setUploadingEvidence(true)
    setError('')
    try {
      const form = new FormData()
      Array.from(files).forEach(file => form.append('files', file))
      form.append('notes', evidenceNotes)
      await uploadLifeEvidence(form)
      setEvidenceNotes('')
      if (evidenceInputRef.current) evidenceInputRef.current.value = ''
      await load()
    } catch (e: any) {
      setError(e.response?.data ?? e.response?.data?.message ?? 'Evidence upload failed.')
    } finally {
      setUploadingEvidence(false)
    }
  }

  const viewEvidence = async (file: LifeEvidence) => {
    setEvidenceLoadingId(file.id)
    setError('')
    try {
      const res = await getLifeEvidence(file.id)
      setSelectedEvidence(res.data)
    } catch (e: any) {
      setError(e.response?.data?.message ?? e.response?.data ?? 'Evidence could not be opened.')
    } finally {
      setEvidenceLoadingId(null)
    }
  }

  const downloadEvidence = async (file: LifeEvidence) => {
    setEvidenceLoadingId(file.id)
    setError('')
    try {
      const res = await downloadLifeEvidence(file.id)
      const url = URL.createObjectURL(res.data)
      const link = document.createElement('a')
      link.href = url
      link.download = file.fileName
      document.body.appendChild(link)
      link.click()
      link.remove()
      URL.revokeObjectURL(url)
    } catch (e: any) {
      setError(e.response?.data?.message ?? 'Evidence could not be downloaded.')
    } finally {
      setEvidenceLoadingId(null)
    }
  }

  const submitCloudLink = async () => {
    if (!cloudLink.url.trim()) return
    setSaving(true)
    setError('')
    try {
      await saveLifeCloudLink(cloudLink)
      setCloudLink({ provider: 'Google Drive', displayName: '', url: '', notes: '' })
      await load()
    } catch (e: any) {
      setError(e.response?.data ?? e.response?.data?.message ?? 'Cloud link could not be saved.')
    } finally {
      setSaving(false)
    }
  }

  const syncCloudSource = async (id: number) => {
    setSyncingCloudId(id)
    setError('')
    try {
      await syncLifeCloudLink(id)
      await load()
    } catch (e: any) {
      setError(e.response?.data?.message ?? e.response?.data ?? 'Cloud source could not be synced.')
    } finally {
      setSyncingCloudId(null)
    }
  }

  const askCoach = async (question = coachQuestion) => {
    const trimmed = question.trim()
    if (!trimmed) return
    setCoachQuestion('')
    setCoachLoading(true)
    setCoachMessages(prev => [...prev, { role: 'user', content: trimmed }])
    try {
      const res = await askLifeCoach(trimmed)
      setCoachMessages(prev => [...prev, { role: 'coach', content: res.data.answer }])
    } catch (e: any) {
      setCoachMessages(prev => [...prev, { role: 'coach', content: e.response?.data?.message ?? 'I could not answer that yet. Try again after adding a little more context.' }])
    } finally {
      setCoachLoading(false)
    }
  }


  // Gamefield: the crew this page's work actually passes through.
  const field = useSnapFieldModel({
    title: 'Life Tracker',
    busy: saving || uploadingEvidence,
    error,
    stages: LIFE_TRACKER_STAGES,
  })

  return (
    <div className="life-page">
      <section className="life-hero">
        <div className="life-hero-copy">
          <div className="life-kicker">Personal operating system</div>
          <h1>Life Tracker</h1>
          <p>Track the signals that actually run your week: mood, energy, focus, sleep, money, movement, people, and progress.</p>
          <div className="life-range">
            {[14, 30, 90].map(days => (
              <button key={days} className={range === days ? 'active' : ''} onClick={() => setRange(days)}>
                {days} days
              </button>
            ))}
          </div>
        </div>
        <div className="life-constellation">
          <LifeBalanceWheel
            areas={areaScores}
            lifeScore={summary.stats.lifeScore}
            mood={isToday(latest?.date) ? latest?.mood : undefined}
            energy={isToday(latest?.date) ? latest?.energy : undefined}
            focus={isToday(latest?.date) ? latest?.focus : undefined}
            staleDays={daysSince(latest?.date)}
          />
        </div>
      </section>

      {/* Bills, warranties and medicines all carry dates that belong in a life
          timeline. Nothing excluded here — this is the one place the whole
          picture makes sense. */}
      <UpcomingDates title="Dates coming up" limit={8} />

      <AgentStage {...stageProps} model={field} />

      {error && <div className="life-alert"><PhosphorIcon className="bi bi-exclamation-circle" />{error}</div>}
      {selectedEvidence && (
        <div className="life-modal-backdrop" onClick={() => setSelectedEvidence(null)}>
          <div className="life-modal" onClick={event => event.stopPropagation()}>
            <div className="life-panel-head compact">
              <div>
                <span>Evidence detail</span>
                <h2>{selectedEvidence.fileName}</h2>
              </div>
              <button className="life-icon-button" onClick={() => setSelectedEvidence(null)} aria-label="Close evidence detail">
                <PhosphorIcon className="bi bi-x-lg" />
              </button>
            </div>
            <div className="life-evidence-meta">
              <span>{selectedEvidence.contentType}</span>
              <span>{formatFileSize(selectedEvidence.fileSize)}</span>
              <span>{new Date(selectedEvidence.createdAt).toLocaleString()}</span>
            </div>
            {selectedEvidence.notes && <p className="life-evidence-modal-notes">{selectedEvidence.notes}</p>}
            <pre className="life-evidence-text">{selectedEvidence.extractedText || 'No extracted text was available for this file.'}</pre>
            <div className="life-modal-actions">
              <button className="life-secondary" onClick={() => downloadEvidence(selectedEvidence)}>
                <PhosphorIcon className="bi bi-download" />Download original
              </button>
              <button className="life-primary" onClick={() => setSelectedEvidence(null)}>Done</button>
            </div>
          </div>
        </div>
      )}

      <section className="life-metrics-grid">
        <Metric label="Life score" value={summary.stats.lifeScore ? `${summary.stats.lifeScore}/10` : 'Start'} icon="bi-stars" accent="#f17855" />
        <Metric label="Mood avg" value={`${summary.stats.avgMood || 0}/10`} icon="bi-emoji-smile" accent="#f59e0b" />
        <Metric label="Focus avg" value={`${summary.stats.avgFocus || 0}/10`} icon="bi-bullseye" accent="#a93727" />
        <Metric label="Exercise" value={`${Math.round(summary.stats.exerciseMinutes / 60)}h`} icon="bi-lightning-charge" accent="#bd3e2b" />
        <Metric label="Screen time" value={`${Math.round(summary.stats.screenMinutes / 60)}h`} icon="bi-phone" accent="#e4573d" />
        <Metric label="People to ping" value={summary.stats.staleRelationships.toString()} icon="bi-people" accent="#f17855" />
      </section>

      <section className="life-dashboard" data-active={tab}>
        {/* Eleven panels down one page meant scrolling past nine to reach the
            tenth. They are grouped instead, and the rail picks the group. */}
        <nav className="life-tabs" aria-label="Life Tracker sections">
          {LIFE_TABS.map(t => (
            <button
              key={t.id}
              type="button"
              className={`life-tab${tab === t.id ? ' is-active' : ''}`}
              aria-current={tab === t.id ? 'page' : undefined}
              onClick={() => setTab(t.id)}
            >
              <PhosphorIcon className={`bi ${t.icon}`} />
              <span>{t.label}</span>
            </button>
          ))}
        </nav>

        <div className="life-main">
          <div data-tab="today" className="life-panel life-checkin-panel">
            <div className="life-panel-head">
              <div>
                <span>Daily snapshot</span>
                <h2>Log today</h2>
              </div>
              <button className="life-primary" onClick={submitCheckIn} disabled={saving}>
                <PhosphorIcon className="bi bi-check2-circle" />Save
              </button>
            </div>
            <div className="life-form-grid">
              <Field label="Date"><input type="date" value={checkIn.date} onChange={e => setCheckIn({ ...checkIn, date: e.target.value })} /></Field>
              <Range label="Mood" value={checkIn.mood} onChange={mood => setCheckIn({ ...checkIn, mood })} />
              <Range label="Energy" value={checkIn.energy} onChange={energy => setCheckIn({ ...checkIn, energy })} />
              <Range label="Focus" value={checkIn.focus} onChange={focus => setCheckIn({ ...checkIn, focus })} />
              <Field label="Sleep hours"><input type="number" min="0" max="24" step="0.5" value={checkIn.sleepHours} onChange={e => setCheckIn({ ...checkIn, sleepHours: Number(e.target.value) })} /></Field>
              <Field label="Exercise minutes"><input type="number" min="0" value={checkIn.exerciseMinutes} onChange={e => setCheckIn({ ...checkIn, exerciseMinutes: Number(e.target.value) })} /></Field>
              <Field label="Screen minutes"><input type="number" min="0" value={checkIn.screenMinutes} onChange={e => setCheckIn({ ...checkIn, screenMinutes: Number(e.target.value) })} /></Field>
              <Field label="Money spent"><input type="number" min="0" step="0.01" value={checkIn.moneySpent} onChange={e => setCheckIn({ ...checkIn, moneySpent: Number(e.target.value) })} /></Field>
            </div>
            <div className="life-text-grid">
              <Field label="Top tasks"><textarea value={checkIn.topTasks} onChange={e => setCheckIn({ ...checkIn, topTasks: e.target.value })} placeholder="Top 3 things that matter today" /></Field>
              <Field label="Wins & gratitude"><textarea value={checkIn.gratitude} onChange={e => setCheckIn({ ...checkIn, gratitude: e.target.value })} placeholder="Good things worth remembering" /></Field>
              <Field label="Notes"><textarea value={checkIn.notes} onChange={e => setCheckIn({ ...checkIn, notes: e.target.value })} placeholder="Anything your future self should know" /></Field>
            </div>
          </div>

          <div data-tab="trends" className="life-panel">
            <div className="life-panel-head">
              <div>
                <span>Trend field</span>
                <h2>Horizontal signals</h2>
              </div>
              <div className="life-muted">{summary.checkIns.length} check-ins</div>
            </div>
            {loading ? <div className="life-empty">Loading your signals...</div> : <TrendBoard trends={trends} />}
          </div>

          <div data-tab="coach" className="life-panel">
            <div className="life-panel-head">
              <div>
                <span>Insights</span>
                <h2>What your data is saying</h2>
              </div>
              <button className="life-secondary" onClick={runWeeklySummary} disabled={weeklyLoading}>
                {weeklyLoading ? <><span className="life-thinking" />Summarising...</> : <><PhosphorIcon className="bi bi-stars" />Weekly summary</>}
              </button>
            </div>
            <div className="life-insight-list">
              {summary.insights.trends.length === 0
                ? <div className="life-empty">Keep logging check-ins to surface patterns.</div>
                : summary.insights.trends.map((insight, index) => (
                  <div className={`life-insight-row ${insight.kind}`} key={index}>
                    <PhosphorIcon className={`bi ${insight.kind === 'correlation' ? 'bi-graph-up-arrow' : 'bi-info-circle'}`} />
                    <span>{insight.text}</span>
                  </div>
                ))}
            </div>
            {weeklySummary && (
              <div className="life-weekly-summary">
                <div className="life-weekly-head"><PhosphorIcon className="bi bi-journal-text" /> Weekly reflection</div>
                <p>{weeklySummary}</p>
              </div>
            )}
          </div>

          <div data-tab="goals" className="life-panel">
            <div className="life-panel-head">
              <div>
                <span>Goals & milestones</span>
                <h2>Progress map</h2>
              </div>
            </div>
            <div className="life-goal-entry">
              <input value={goal.title} onChange={e => setGoal({ ...goal, title: e.target.value })} placeholder="Big thing you are building" />
              <select value={goal.area} onChange={e => setGoal({ ...goal, area: e.target.value })}>
                {['Health', 'Work', 'Relationships', 'Money', 'Growth'].map(area => <option key={area}>{area}</option>)}
              </select>
              <input type="date" value={goal.targetDate} onChange={e => setGoal({ ...goal, targetDate: e.target.value })} />
              <select value={goalStatus} onChange={e => setGoalStatus(e.target.value as LifeGoal['status'])}>
                <option value="active">Active</option>
                <option value="paused">Paused</option>
                <option value="done">Done</option>
              </select>
              <input type="number" min="0" max="100" value={goal.progress} onChange={e => setGoal({ ...goal, progress: Number(e.target.value) })} />
              <input value={goal.notes} onChange={e => setGoal({ ...goal, notes: e.target.value })} placeholder="Notes or next step" />
              <button className="life-primary" onClick={submitGoal} disabled={saving}><PhosphorIcon className={`bi ${editingGoalId ? 'bi-save' : 'bi-plus-lg'}`} />{editingGoalId ? 'Update' : 'Add'}</button>
              {editingGoalId && <button className="life-secondary" onClick={resetGoalForm}>Cancel</button>}
            </div>
            <GoalBubbleChart goals={summary.goals} />
            <div className="life-goals-controls">
              {summary.goals.map(g => (
                <div className="life-goal-row" key={g.id}>
                  <span className="life-goal-dot" style={{ background: AREA_COLORS[g.area] ?? '#777269' }} />
                  <span className="life-goal-name">{g.title}</span>
                  <div className="life-goal-actions">
                    <button onClick={() => nudgeGoal(g, -10)}><PhosphorIcon className="bi bi-dash" /></button>
                    <strong>{g.progress}%</strong>
                    <button onClick={() => nudgeGoal(g, 10)}><PhosphorIcon className="bi bi-plus" /></button>
                    <button onClick={() => editGoal(g)}><PhosphorIcon className="bi bi-pencil" /></button>
                    <button onClick={() => setGoalStatusDirect(g, g.status === 'paused' ? 'active' : 'paused')}><PhosphorIcon className={`bi ${g.status === 'paused' ? 'bi-play' : 'bi-pause'}`} /></button>
                    <button onClick={() => setGoalStatusDirect(g, 'done')}><PhosphorIcon className="bi bi-check2" /></button>
                    <button onClick={async () => { await deleteLifeGoal(g.id); await load() }}><PhosphorIcon className="bi bi-trash" /></button>
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>

        <aside className="life-side">
          {/* Voice journalling leads the sidebar: speaking is the lowest-effort
              way in, and everything below reads better once there are entries. */}
          <div data-tab="today" className="life-panel">
            <div className="life-panel-head compact">
              <div><span>Voice journal</span><h2>Say it out loud</h2></div>
              <PhosphorIcon className="bi bi-mic-fill life-coach-mark" />
            </div>
            <LifeVoiceRecorder onApplied={load} />
          </div>

          <div data-tab="coach" className="life-panel">
            <div className="life-panel-head compact">
              <div><span>Possible futures</span><h2>Where this leads</h2></div>
              <PhosphorIcon className="bi bi-diagram-3 life-coach-mark" />
            </div>
            <LifeScenarios />
          </div>

          <div data-tab="coach" className="life-panel life-coach-panel">
            <div className="life-panel-head compact">
              <div><span>Life Coach</span><h2>Decision agent</h2></div>
              <PhosphorIcon className="bi bi-stars life-coach-mark" />
            </div>
            <div className="life-coach-prompts">
              {[
                'What decision am I avoiding?',
                'What patterns do you see this month?',
                'Help me choose between my options.',
              ].map(prompt => (
                <button key={prompt} onClick={() => askCoach(prompt)} disabled={coachLoading}>{prompt}</button>
              ))}
            </div>
            <div className="life-chat-thread">
              {coachMessages.map((message, index) => (
                <div key={index} className={`life-chat-bubble ${message.role}`}>
                  {message.content}
                </div>
              ))}
              {coachLoading && <div className="life-chat-bubble coach"><span className="life-thinking" />Thinking with your life map...</div>}
            </div>
            <div className="life-chat-input">
              <textarea
                value={coachQuestion}
                onChange={e => setCoachQuestion(e.target.value)}
                onKeyDown={e => {
                  if (e.key === 'Enter' && !e.shiftKey) {
                    e.preventDefault()
                    askCoach()
                  }
                }}
                placeholder="Ask about a decision, tradeoff, goal, relationship, or pattern"
              />
              <button className="life-primary" onClick={() => askCoach()} disabled={coachLoading || !coachQuestion.trim()}>
                <PhosphorIcon className="bi bi-send" />
              </button>
            </div>
          </div>

          <div data-tab="evidence" className="life-panel">
            <div className="life-panel-head compact"><div><span>Evidence</span><h2>Upload your context</h2></div></div>
            <div
              className={`life-evidence-drop ${dragOverEvidence ? 'drag' : ''}`}
              onDragOver={e => { e.preventDefault(); setDragOverEvidence(true) }}
              onDragLeave={() => setDragOverEvidence(false)}
              onDrop={e => {
                e.preventDefault()
                setDragOverEvidence(false)
                submitEvidence(e.dataTransfer.files)
              }}
              onClick={() => evidenceInputRef.current?.click()}
            >
              <input
                ref={evidenceInputRef}
                type="file"
                multiple
                accept=".pdf,.docx,.txt,.md,.csv,.json,.rtf,.jpg,.jpeg,.png,.webp"
                onChange={e => submitEvidence(e.target.files)}
              />
              {uploadingEvidence ? <><span className="life-thinking" />Extracting context...</> : <><PhosphorIcon className="bi bi-cloud-arrow-up" />Drop files, photos, notes, or click</>}
            </div>
            <textarea
              className="life-evidence-notes"
              value={evidenceNotes}
              onChange={e => setEvidenceNotes(e.target.value)}
              placeholder="Optional note: why this file matters"
            />
            <div className="life-cloud-connect">
            <div className="life-cloud-heading">
                <strong>Cloud sources</strong>
                <span>Save shareable Drive or OneDrive links. Public/readable links are synced into coach context.</span>
              </div>
              <div className="life-cloud-tabs">
                {['Google Drive', 'OneDrive'].map(provider => (
                  <button
                    key={provider}
                    className={cloudLink.provider === provider ? 'active' : ''}
                    onClick={() => setCloudLink({ ...cloudLink, provider })}
                  >
                    <PhosphorIcon className={`bi ${provider === 'Google Drive' ? 'bi-google' : 'bi-cloud'}`} />
                    {provider}
                  </button>
                ))}
              </div>
              <input
                value={cloudLink.displayName}
                onChange={e => setCloudLink({ ...cloudLink, displayName: e.target.value })}
                placeholder="Source name, e.g. Life docs folder"
              />
              <input
                value={cloudLink.url}
                onChange={e => setCloudLink({ ...cloudLink, url: e.target.value })}
                placeholder="Paste a Google Drive or OneDrive folder/document link"
              />
              <textarea
                value={cloudLink.notes}
                onChange={e => setCloudLink({ ...cloudLink, notes: e.target.value })}
                placeholder="What should the coach remember about this source?"
              />
              <button className="life-secondary" onClick={submitCloudLink} disabled={saving || !cloudLink.url.trim()}>
                <PhosphorIcon className="bi bi-link-45deg" />Save cloud source
              </button>
            </div>
            <div className="life-mini-list">
              {summary.evidence.length === 0 && summary.cloudLinks.length === 0 && <div><span>No evidence or cloud sources saved yet.</span></div>}
              {summary.evidence.slice(0, 6).map(file => (
                <div key={file.id} className="life-evidence-row">
                  <strong><PhosphorIcon className={`bi ${file.evidenceType === 'image' ? 'bi-file-earmark-image' : 'bi-file-earmark-text'}`} />{file.fileName}</strong>
                  <span>{formatFileSize(file.fileSize)} · {new Date(file.createdAt).toLocaleDateString()}</span>
                  <div className="life-row-actions">
                    <button onClick={() => viewEvidence(file)} disabled={evidenceLoadingId === file.id}><PhosphorIcon className="bi bi-eye" /></button>
                    <button onClick={() => downloadEvidence(file)} disabled={evidenceLoadingId === file.id}><PhosphorIcon className="bi bi-download" /></button>
                    <button onClick={async () => { await deleteLifeEvidence(file.id); await load() }}><PhosphorIcon className="bi bi-trash" /></button>
                  </div>
                </div>
              ))}
              {summary.cloudLinks.map(link => (
                <div key={`cloud-${link.id}`} className="life-evidence-row life-cloud-row">
                  <strong><PhosphorIcon className={`bi ${link.provider === 'Google Drive' ? 'bi-google' : 'bi-cloud'}`} />{link.displayName || link.provider}</strong>
                  <span>{link.provider} · {link.syncStatus || 'Saved'}{link.lastSyncedAt ? ` · synced ${new Date(link.lastSyncedAt).toLocaleDateString()}` : ''}</span>
                  {link.extractedPreview && <span className="life-preview-text">{link.extractedPreview}</span>}
                  <a href={link.url} target="_blank" rel="noreferrer">Open</a>
                  <div className="life-row-actions">
                    <button onClick={() => syncCloudSource(link.id)} disabled={syncingCloudId === link.id}><PhosphorIcon className="bi bi-arrow-clockwise" /></button>
                    <button onClick={async () => { await deleteLifeCloudLink(link.id); await load() }}><PhosphorIcon className="bi bi-trash" /></button>
                  </div>
                </div>
              ))}
            </div>
          </div>

          <div data-tab="trends" className="life-panel">
            <div className="life-panel-head compact">
              <div><span>Life areas</span><h2>Balance wheel</h2></div>
            </div>
            <RadialAreaChart areas={areaScores} />
          </div>

          <div data-tab="people" className="life-panel">
            <div className="life-panel-head compact"><div><span>Relationships</span><h2>People network</h2></div></div>
            <div className="life-stack-form">
              <input value={relationship.name} onChange={e => setRelationship({ ...relationship, name: e.target.value })} placeholder="Name" />
              <div className="life-two">
                <select value={relationship.circle} onChange={e => setRelationship({ ...relationship, circle: e.target.value })}>
                  {['Family', 'Friends', 'Work', 'Community'].map(circle => <option key={circle}>{circle}</option>)}
                </select>
                <input type="date" value={relationship.lastContactedAt} onChange={e => setRelationship({ ...relationship, lastContactedAt: e.target.value })} />
              </div>
              <Range label="Importance" value={relationship.importance} onChange={importance => setRelationship({ ...relationship, importance })} />
              <textarea value={relationship.notes} onChange={e => setRelationship({ ...relationship, notes: e.target.value })} placeholder="Context, care notes, or next contact idea" />
              <button className="life-secondary" onClick={submitRelationship}><PhosphorIcon className={`bi ${editingRelationshipId ? 'bi-save' : 'bi-person-plus'}`} />{editingRelationshipId ? 'Update person' : 'Save person'}</button>
              {editingRelationshipId && <button className="life-secondary" onClick={resetRelationshipForm}>Cancel edit</button>}
            </div>
            <RelationshipNetwork relationships={summary.relationships} />
            {summary.insights.relationshipNudges.length > 0 && (
              <div className="life-nudges">
                <div className="life-nudges-head"><PhosphorIcon className="bi bi-bell" /> Time to reach out</div>
                {summary.insights.relationshipNudges.map(nudge => (
                  <div className="life-nudge-row" key={nudge.id}>
                    <div className="life-nudge-info">
                      <strong>{nudge.name}</strong>
                      <span>
                        {nudge.daysSinceContact == null
                          ? 'No contact logged yet'
                          : `${nudge.daysSinceContact} days since last contact`}
                        {' · '}{nudge.circle}
                      </span>
                    </div>
                    <button className="life-secondary" onClick={() => markContacted(nudge.id)}>
                      <PhosphorIcon className="bi bi-check2" />Reached out
                    </button>
                  </div>
                ))}
              </div>
            )}
            <div className="life-mini-list">
              {summary.relationships.map(r => (
                <div key={r.id} className="life-list-row">
                  <strong>{r.name}</strong>
                  <span>{r.circle} · importance {r.importance}/10 · last contact {r.lastContactedAt ? new Date(r.lastContactedAt).toLocaleDateString() : 'not set'}</span>
                  {r.notes && <span>{r.notes}</span>}
                  <div className="life-row-actions">
                    <button onClick={() => editRelationship(r)}><PhosphorIcon className="bi bi-pencil" /></button>
                    <button onClick={async () => { await deleteLifeRelationship(r.id); await load() }}><PhosphorIcon className="bi bi-trash" /></button>
                  </div>
                </div>
              ))}
            </div>
          </div>

          <div data-tab="goals" className="life-panel">
            <div className="life-panel-head compact"><div><span>Decisions log</span><h2>Choice memory</h2></div></div>
            {summary.insights.decisionReviews.length > 0 && (
              <div className="life-reviews">
                <div className="life-reviews-head"><PhosphorIcon className="bi bi-hourglass-split" /> Due for review</div>
                {summary.insights.decisionReviews.map(review => {
                  const overdue = review.daysUntil < 0
                  return (
                    <button className="life-review-row" key={review.id} onClick={() => { const d = summary.decisions.find(x => x.id === review.id); if (d) editDecision(d) }}>
                      <span className={`life-review-badge ${overdue ? 'overdue' : 'due'}`}>
                        {overdue ? `${Math.abs(review.daysUntil)}d overdue` : review.daysUntil === 0 ? 'today' : `in ${review.daysUntil}d`}
                      </span>
                      <span className="life-review-title">{review.title}</span>
                      {review.hasOutcome && <PhosphorIcon className="bi bi-check-circle-fill" title="Outcome recorded" />}
                    </button>
                  )
                })}
              </div>
            )}
            <div className="life-stack-form">
              <input type="date" value={decision.date} onChange={e => setDecision({ ...decision, date: e.target.value })} />
              <input value={decision.title} onChange={e => setDecision({ ...decision, title: e.target.value })} placeholder="Decision made" />
              <textarea value={decision.reasoning} onChange={e => setDecision({ ...decision, reasoning: e.target.value })} placeholder="Why this made sense" />
              <textarea value={decision.outcome} onChange={e => setDecision({ ...decision, outcome: e.target.value })} placeholder="Outcome, review notes, or what changed" />
              <input type="date" value={decision.reviewDate} onChange={e => setDecision({ ...decision, reviewDate: e.target.value })} />
              <button className="life-secondary" onClick={submitDecision}><PhosphorIcon className={`bi ${editingDecisionId ? 'bi-save' : 'bi-journal-check'}`} />{editingDecisionId ? 'Update decision' : 'Log decision'}</button>
              {editingDecisionId && <button className="life-secondary" onClick={resetDecisionForm}>Cancel edit</button>}
            </div>
            <div className="life-mini-list">
              {summary.decisions.slice(0, 4).map(d => (
                <div key={d.id} className="life-list-row">
                  <strong>{d.title}</strong>
                  <span>{new Date(d.date).toLocaleDateString()}{d.reviewDate ? ` · review ${new Date(d.reviewDate).toLocaleDateString()}` : ''}</span>
                  {d.reasoning && <span>{d.reasoning}</span>}
                  {d.outcome && <span>{d.outcome}</span>}
                  <div className="life-row-actions">
                    <button onClick={() => editDecision(d)}><PhosphorIcon className="bi bi-pencil" /></button>
                    <button onClick={async () => { await deleteLifeDecision(d.id); await load() }}><PhosphorIcon className="bi bi-trash" /></button>
                  </div>
                </div>
              ))}
            </div>
          </div>
        </aside>
      </section>
    </div>
  )
}


function Metric({ label, value, icon, accent }: { label: string; value: string; icon: string; accent: string }) {
  return (
    <div className="life-metric" style={{ '--accent': accent } as CSSProperties}>
      <PhosphorIcon className={`bi ${icon}`} />
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  )
}

function Field({ label, children }: { label: string; children: ReactNode }) {
  return <label className="life-field"><span>{label}</span>{children}</label>
}

function Range({ label, value, onChange }: { label: string; value: number; onChange: (value: number) => void }) {
  return (
    <label className="life-field">
      <span>{label} <strong>{value}</strong></span>
      <input type="range" min="1" max="10" value={value} onChange={e => onChange(Number(e.target.value))} />
    </label>
  )
}

function TrendBoard({ trends }: { trends: { label: string; values: number[]; color: string; max: number }[] }) {
  return (
    <div className="life-trends">
      {trends.map(trend => (
        <div className="life-trend" key={trend.label}>
          <div className="life-trend-label">{trend.label}</div>
          <div className="life-spark">
            {trend.values.map((value, index) => (
              <span key={index} style={{ height: `${Math.max(8, (value / trend.max) * 100)}%`, background: trend.color }} />
            ))}
          </div>
          <strong>{trend.values.at(-1) ?? 0}</strong>
        </div>
      ))}
    </div>
  )
}

function buildTrends(checkIns: LifeCheckIn[]) {
  const recent = checkIns.slice(-18)
  const fallback = Array.from({ length: 8 }, () => 0)
  return [
    { label: 'Mood', values: recent.map(x => x.mood), color: '#f59e0b', max: 10 },
    { label: 'Energy', values: recent.map(x => x.energy), color: '#f17855', max: 10 },
    { label: 'Focus', values: recent.map(x => x.focus), color: '#a93727', max: 10 },
    { label: 'Sleep', values: recent.map(x => x.sleepHours), color: '#e4573d', max: 10 },
    { label: 'Movement', values: recent.map(x => x.exerciseMinutes), color: '#bd3e2b', max: Math.max(60, ...recent.map(x => x.exerciseMinutes), 1) },
    { label: 'Screen', values: recent.map(x => Math.round(x.screenMinutes / 60)), color: '#e4573d', max: Math.max(8, ...recent.map(x => Math.round(x.screenMinutes / 60)), 1) },
  ].map(trend => ({ ...trend, values: trend.values.length ? trend.values : fallback }))
}

/** Whole days between a check-in date and now; null when there is no check-in. */
function daysSince(iso?: string): number | null {
  if (!iso) return null
  const then = new Date(iso)
  if (Number.isNaN(then.getTime())) return null
  const a = new Date(then.getFullYear(), then.getMonth(), then.getDate())
  const now = new Date()
  const b = new Date(now.getFullYear(), now.getMonth(), now.getDate())
  return Math.max(0, Math.round((b.getTime() - a.getTime()) / 86_400_000))
}

const isToday = (iso?: string) => daysSince(iso) === 0

function buildAreaScores(summary: LifeSummary) {
  const latest = summary.checkIns[summary.checkIns.length - 1]
  const activeByArea = (area: string) => summary.goals.filter(x => x.status === 'active' && x.area === area).length
  const relationshipScore = Math.max(1, Math.min(10, 10 - summary.stats.staleRelationships * 2))
  return [
    { label: 'Health', score: latest ? Math.round(((latest.sleepHours / 8) * 4 + latest.energy * 0.35 + Math.min(10, latest.exerciseMinutes / 6) * 0.25)) : 5, color: '#f17855' },
    { label: 'Work', score: latest ? latest.focus : 5, color: '#a93727' },
    { label: 'Relationships', score: relationshipScore, color: '#f17855' },
    { label: 'Money', score: latest ? Math.max(1, Math.min(10, Math.round(10 - Number(latest.moneySpent || 0) / 50))) : 6, color: '#f59e0b' },
    { label: 'Growth', score: Math.max(1, Math.min(10, 5 + activeByArea('Growth') + summary.stats.completedGoals)), color: '#a93727' },
  ]
}
