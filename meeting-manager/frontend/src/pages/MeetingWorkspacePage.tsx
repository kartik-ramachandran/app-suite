import PhosphorIcon from '../components/PhosphorIcon'
import { useState, useEffect, useLayoutEffect } from 'react'
import { useParams, useNavigate } from 'react-router-dom'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import api from '../services/api'
import '../App.css'

interface Attendee { name: string; email: string; responseStatus: string }
interface ActionItem { title: string; owner: string; dueDate?: string; priority: string }
interface PrepPack {
  objective: string; agenda: string
  talkingPoints: string[]; questions: string[]; risks: string[]; decisionsNeeded: string[]
  followUpItems: string[]; openingStatement: string; closingStatement: string
}
interface FollowUpPack {
  summary: string; decisions: string[]
  actions: ActionItem[]; openQuestions: string[]; risks: string[]
  followUpEmail: string; nextMeetingAgenda: string
}
interface Workspace {
  id: number; providerEventId?: string; eventTitle: string
  eventStart?: string; eventEnd?: string; attendees: Attendee[]
  objective?: string; notes?: string; summary?: string; followUpDraft?: string
  prepPack?: PrepPack; decisions: string[]; risks: string[]; actions: ActionItem[]
  createdAt: string; updatedAt: string
}

type Tab = 'prep' | 'notes' | 'follow-up' | 'tasks'

const PRIORITY_COLOR: Record<string, string> = { high: '#ef4444', medium: '#f59e0b', low: '#22c55e' }

export default function MeetingWorkspacePage() {
  const { id } = useParams<{ id: string }>()
  const navigate = useNavigate()
  const { setBreadcrumbs } = useLayoutControls()

  const [workspace, setWorkspace] = useState<Workspace | null>(null)
  const [activeTab, setActiveTab] = useState<Tab>('prep')
  const [loading, setLoading] = useState(true)
  const [generatingPrep, setGeneratingPrep] = useState(false)
  const [generatingFollowUp, setGeneratingFollowUp] = useState(false)
  const [extractingTasks, setExtractingTasks] = useState(false)
  const [notes, setNotes] = useState('')
  const [notesDirty, setNotesDirty] = useState(false)
  const [savingNotes, setSavingNotes] = useState(false)
  const [followUpDraft, setFollowUpDraft] = useState<FollowUpPack | null>(null)
  const [tasks, setTasks] = useState<any[]>([])
  const [copied, setCopied] = useState(false)
  const [error, setError] = useState('')

  useLayoutEffect(() => {
    setBreadcrumbs([
      { label: 'Calendar', path: '/calendar' },
      { label: 'Meeting Workspace' }
    ])
    return () => setBreadcrumbs([])
  }, [])

  useEffect(() => { if (id) loadWorkspace() }, [id])
  useEffect(() => { if (id) loadTasks() }, [id])

  const loadWorkspace = async () => {
    setLoading(true)
    try {
      const res = await api.get(`/meetings/workspaces/${id}`)
      setWorkspace(res.data)
      setNotes(res.data.notes ?? '')
    } catch { setError('Workspace not found') }
    finally { setLoading(false) }
  }

  const loadTasks = async () => {
    try {
      const res = await api.get(`/meetings/tasks?workspaceId=${id}`)
      setTasks(res.data)
    } catch { /* non-fatal */ }
  }

  const generatePrep = async () => {
    if (!workspace) return
    setGeneratingPrep(true)
    setError('')
    try {
      const res = await api.post(`/meetings/workspaces/${id}/generate-prep`, {
        description: workspace.eventTitle,
        previousNotes: workspace.notes
      })
      setWorkspace(w => w ? { ...w, prepPack: res.data.prep, objective: res.data.prep.objective } : w)
    } catch (e: any) { setError(e.response?.data?.message ?? 'Generation failed') }
    finally { setGeneratingPrep(false) }
  }

  const generateFollowUp = async () => {
    if (!notes.trim()) { setError('Add meeting notes before generating follow-up'); return }
    setGeneratingFollowUp(true)
    setError('')
    try {
      const res = await api.post(`/meetings/workspaces/${id}/generate-follow-up`, { notes })
      setFollowUpDraft(res.data.followUp)
      setWorkspace(w => w ? { ...w, followUpDraft: res.data.followUp.followUpEmail, summary: res.data.followUp.summary } : w)
      setActiveTab('follow-up')
    } catch (e: any) { setError(e.response?.data?.message ?? 'Generation failed') }
    finally { setGeneratingFollowUp(false) }
  }

  const extractTasks = async () => {
    if (!notes.trim()) { setError('Add meeting notes before extracting tasks'); return }
    setExtractingTasks(true)
    setError('')
    try {
      await api.post(`/meetings/workspaces/${id}/extract-tasks`, { notes })
      await loadTasks()
      setActiveTab('tasks')
    } catch (e: any) { setError(e.response?.data?.message ?? 'Extraction failed') }
    finally { setExtractingTasks(false) }
  }

  const saveNotes = async () => {
    setSavingNotes(true)
    await api.patch(`/meetings/workspaces/${id}/notes`, { notes })
    setSavingNotes(false)
    setNotesDirty(false)
  }

  const copyEmail = () => {
    const text = followUpDraft?.followUpEmail ?? workspace?.followUpDraft ?? ''
    navigator.clipboard.writeText(text)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  const updateTaskStatus = async (taskId: number, status: string) => {
    await api.patch(`/meetings/tasks/${taskId}/status`, { status })
    setTasks(t => t.map(x => x.id === taskId ? { ...x, status } : x))
  }

  if (loading) return (
    <div style={{ padding: 32, display: 'flex', alignItems: 'center', gap: 12, color: 'var(--text-secondary)' }}>
      <div className="spinner" /><span>Loading workspace…</span>
    </div>
  )

  if (!workspace) return (
    <div style={{ padding: 32, textAlign: 'center' }}>
      <div style={{ color: '#ef4444', marginBottom: 12 }}>{error || 'Workspace not found'}</div>
      <button className="btn btn-secondary" onClick={() => navigate('/calendar')}>← Back to Calendar</button>
    </div>
  )

  const prep = workspace.prepPack
  const displayFollowUp = followUpDraft ?? (workspace.followUpDraft ? { followUpEmail: workspace.followUpDraft, summary: workspace.summary ?? '', decisions: workspace.decisions, actions: workspace.actions, openQuestions: [], risks: workspace.risks, nextMeetingAgenda: '' } : null)

  return (
    <div style={{ padding: '24px', maxWidth: 1000, margin: '0 auto' }}>

      {/* Meeting header */}
      <div className="card" style={{ padding: '18px 22px', marginBottom: 20 }}>
        <div style={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', gap: 12, flexWrap: 'wrap' }}>
          <div style={{ flex: 1 }}>
            <h1 style={{ fontSize: 20, fontWeight: 700, margin: '0 0 6px' }}>{workspace.eventTitle}</h1>
            <div style={{ display: 'flex', gap: 14, fontSize: 13, color: 'var(--text-secondary)', flexWrap: 'wrap' }}>
              {workspace.eventStart && (
                <span><PhosphorIcon className="bi bi-calendar3" style={{ marginRight: 4 }} />
                  {new Date(workspace.eventStart).toLocaleDateString('en-US', { weekday: 'long', month: 'long', day: 'numeric' })}
                </span>
              )}
              {workspace.eventStart && workspace.eventEnd && (
                <span><PhosphorIcon className="bi bi-clock" style={{ marginRight: 4 }} />
                  {new Date(workspace.eventStart).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}
                  {' — '}
                  {new Date(workspace.eventEnd).toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' })}
                </span>
              )}
              {workspace.attendees.length > 0 && (
                <span><PhosphorIcon className="bi bi-people" style={{ marginRight: 4 }} />{workspace.attendees.length} attendee{workspace.attendees.length !== 1 ? 's' : ''}</span>
              )}
            </div>
            {workspace.attendees.length > 0 && (
              <div style={{ marginTop: 8, display: 'flex', flexWrap: 'wrap', gap: 4 }}>
                {workspace.attendees.map((a, i) => (
                  <span key={i} style={{ fontSize: 11, background: 'var(--bg-secondary)', border: '1px solid var(--border)', borderRadius: 12, padding: '2px 8px', color: 'var(--text-secondary)' }}>
                    {a.name || a.email}
                  </span>
                ))}
              </div>
            )}
          </div>
          {workspace.objective && (
            <div style={{ fontSize: 12, color: 'var(--text-muted)', maxWidth: 260, background: 'var(--bg-secondary)', padding: '8px 12px', borderRadius: 8, lineHeight: 1.5 }}>
              <strong style={{ color: 'var(--text-secondary)' }}>Objective:</strong> {workspace.objective}
            </div>
          )}
        </div>
      </div>

      {error && (
        <div style={{ padding: '10px 14px', background: '#ef444410', border: '1px solid #ef444430', borderRadius: 8, color: '#ef4444', fontSize: 13, marginBottom: 16, display: 'flex', gap: 8 }}>
          <PhosphorIcon className="bi bi-exclamation-circle" />{error}
          <button onClick={() => setError('')} style={{ marginLeft: 'auto', background: 'none', border: 'none', cursor: 'pointer', color: '#ef4444' }}><PhosphorIcon className="bi bi-x" /></button>
        </div>
      )}

      {/* Action buttons */}
      <div style={{ display: 'flex', gap: 8, marginBottom: 16, flexWrap: 'wrap' }}>
        <button className="btn btn-primary" style={{ fontSize: 12 }} onClick={generatePrep} disabled={generatingPrep}>
          {generatingPrep ? <><div className="spinner" style={{ width: 14, height: 14 }} />Generating…</> : <><PhosphorIcon className="bi bi-stars" />Generate Prep Pack</>}
        </button>
        <button className="btn btn-secondary" style={{ fontSize: 12 }} onClick={generateFollowUp} disabled={generatingFollowUp}>
          {generatingFollowUp ? <><div className="spinner" style={{ width: 14, height: 14 }} />Generating…</> : <><PhosphorIcon className="bi bi-envelope" />Generate Follow-up</>}
        </button>
        <button className="btn btn-secondary" style={{ fontSize: 12 }} onClick={extractTasks} disabled={extractingTasks}>
          {extractingTasks ? <><div className="spinner" style={{ width: 14, height: 14 }} />Extracting…</> : <><PhosphorIcon className="bi bi-check2-square" />Extract Tasks</>}
        </button>
      </div>

      {/* Tabs */}
      <div style={{ display: 'flex', gap: 0, borderBottom: '1px solid var(--border)', marginBottom: 20 }}>
        {([
          { key: 'prep', icon: 'bi-journal-text', label: 'Preparation' },
          { key: 'notes', icon: 'bi-pencil', label: 'Notes' },
          { key: 'follow-up', icon: 'bi-envelope', label: 'Follow-up' },
          { key: 'tasks', icon: 'bi-check2-square', label: `Tasks${tasks.length > 0 ? ` (${tasks.length})` : ''}` },
        ] as const).map(t => (
          <button key={t.key} onClick={() => setActiveTab(t.key)} style={{
            padding: '8px 16px', fontSize: 13, fontWeight: 600, border: 'none', cursor: 'pointer',
            background: 'none', borderBottom: activeTab === t.key ? '2px solid var(--primary)' : '2px solid transparent',
            color: activeTab === t.key ? 'var(--primary)' : 'var(--text-secondary)',
            marginBottom: -1,
          }}>
            <PhosphorIcon className={`bi ${t.icon}`} style={{ marginRight: 5 }} />{t.label}
          </button>
        ))}
      </div>

      {/* ── Prep tab ── */}
      {activeTab === 'prep' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          {!prep && (
            <div className="card" style={{ padding: '32px 24px', textAlign: 'center' }}>
              <PhosphorIcon className="bi bi-journal-text" style={{ fontSize: 36, color: 'var(--primary)', display: 'block', marginBottom: 12 }} />
              <div style={{ fontSize: 15, fontWeight: 600, marginBottom: 8 }}>No preparation pack yet</div>
              <div style={{ fontSize: 13, color: 'var(--text-secondary)', marginBottom: 16 }}>Generate a prep pack to get talking points, questions, risks, and decisions needed.</div>
              <button className="btn btn-primary" onClick={generatePrep} disabled={generatingPrep}>
                {generatingPrep ? <><div className="spinner" />Generating…</> : <><PhosphorIcon className="bi bi-stars" />Generate Prep Pack</>}
              </button>
            </div>
          )}
          {prep && (
            <>
              {prep.objective && <PrepSection title="Meeting Objective" icon="bi-bullseye" content={prep.objective} />}
              {prep.agenda && <PrepSection title="Suggested Agenda" icon="bi-list-ol" content={prep.agenda} />}
              {prep.openingStatement && <PrepSection title="Opening Statement" icon="bi-chat-quote" content={prep.openingStatement} />}
              {prep.talkingPoints.length > 0 && <PrepList title="Talking Points" icon="bi-megaphone" items={prep.talkingPoints} />}
              {prep.questions.length > 0 && <PrepList title="Questions to Ask" icon="bi-question-circle" items={prep.questions} />}
              {prep.risks.length > 0 && <PrepList title="Risks to Raise" icon="bi-exclamation-triangle" items={prep.risks} color="#f59e0b" />}
              {prep.decisionsNeeded.length > 0 && <PrepList title="Decisions Needed" icon="bi-check-circle" items={prep.decisionsNeeded} color="#833ab4" />}
              {prep.closingStatement && <PrepSection title="Closing Statement" icon="bi-chat-right-quote" content={prep.closingStatement} />}
            </>
          )}
        </div>
      )}

      {/* ── Notes tab ── */}
      {activeTab === 'notes' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
          <div style={{ fontSize: 13, color: 'var(--text-secondary)' }}>
            Enter rough notes, bullet points, or a transcript. The AI will generate a follow-up email and extract action items.
          </div>
          <textarea
            className="form-textarea"
            style={{ minHeight: 280, fontSize: 14, lineHeight: 1.7, fontFamily: 'inherit' }}
            placeholder="e.g. Kevin agreed API changes needed. Lee wants revised timeline by Friday. Need to confirm metadata JSON impact. Firm filtering should happen in Core."
            value={notes}
            onChange={e => { setNotes(e.target.value); setNotesDirty(true) }}
          />
          <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
            {notesDirty && (
              <button className="btn btn-secondary" style={{ fontSize: 12 }} onClick={saveNotes} disabled={savingNotes}>
                {savingNotes ? <><div className="spinner" style={{ width: 14, height: 14 }} />Saving…</> : <><PhosphorIcon className="bi bi-floppy" />Save Notes</>}
              </button>
            )}
            <button className="btn btn-primary" style={{ fontSize: 12 }} onClick={generateFollowUp} disabled={generatingFollowUp || !notes.trim()}>
              {generatingFollowUp ? <><div className="spinner" style={{ width: 14, height: 14 }} />Generating…</> : <><PhosphorIcon className="bi bi-envelope" />Generate Follow-up</>}
            </button>
            <button className="btn btn-secondary" style={{ fontSize: 12 }} onClick={extractTasks} disabled={extractingTasks || !notes.trim()}>
              {extractingTasks ? <><div className="spinner" style={{ width: 14, height: 14 }} />Extracting…</> : <><PhosphorIcon className="bi bi-check2-square" />Extract Tasks</>}
            </button>
          </div>
        </div>
      )}

      {/* ── Follow-up tab ── */}
      {activeTab === 'follow-up' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
          {!displayFollowUp && (
            <div className="card" style={{ padding: '32px 24px', textAlign: 'center' }}>
              <PhosphorIcon className="bi bi-envelope" style={{ fontSize: 36, color: 'var(--primary)', display: 'block', marginBottom: 12 }} />
              <div style={{ fontSize: 15, fontWeight: 600, marginBottom: 8 }}>No follow-up generated yet</div>
              <div style={{ fontSize: 13, color: 'var(--text-secondary)', marginBottom: 16 }}>Add meeting notes in the Notes tab, then generate a follow-up email and action items.</div>
              <button className="btn btn-secondary" onClick={() => setActiveTab('notes')}>Go to Notes →</button>
            </div>
          )}
          {displayFollowUp && (
            <>
              {displayFollowUp.summary && (
                <div className="card" style={{ padding: '14px 16px' }}>
                  <div style={{ fontSize: 12, fontWeight: 700, color: 'var(--text-muted)', marginBottom: 6 }}>SUMMARY</div>
                  <p style={{ fontSize: 13, color: 'var(--text-secondary)', margin: 0, lineHeight: 1.6 }}>{displayFollowUp.summary}</p>
                </div>
              )}
              {displayFollowUp.decisions.length > 0 && <PrepList title="Decisions Made" icon="bi-check-all" items={displayFollowUp.decisions} color="#22c55e" />}
              {displayFollowUp.actions.length > 0 && (
                <div className="card" style={{ padding: '14px 16px' }}>
                  <div style={{ fontSize: 12, fontWeight: 700, color: 'var(--text-muted)', marginBottom: 8 }}>ACTION ITEMS</div>
                  {displayFollowUp.actions.map((a, i) => (
                    <div key={i} style={{ display: 'flex', alignItems: 'flex-start', gap: 8, marginBottom: 8, paddingBottom: 8, borderBottom: i < displayFollowUp.actions.length - 1 ? '1px solid var(--border)' : 'none' }}>
                      <div style={{ width: 6, height: 6, borderRadius: '50%', background: PRIORITY_COLOR[a.priority] ?? '#888', marginTop: 6, flexShrink: 0 }} />
                      <div style={{ flex: 1 }}>
                        <div style={{ fontSize: 13, fontWeight: 600 }}>{a.title}</div>
                        <div style={{ fontSize: 11, color: 'var(--text-muted)' }}>
                          {a.owner && <><PhosphorIcon className="bi bi-person" style={{ marginRight: 3 }} />{a.owner}</>}
                          {a.dueDate && <><PhosphorIcon className="bi bi-calendar3" style={{ marginLeft: 8, marginRight: 3 }} />Due: {a.dueDate}</>}
                        </div>
                      </div>
                      <span style={{ fontSize: 10, background: `${PRIORITY_COLOR[a.priority] ?? '#888'}20`, color: PRIORITY_COLOR[a.priority] ?? '#888', padding: '2px 6px', borderRadius: 8, fontWeight: 600 }}>{a.priority}</span>
                    </div>
                  ))}
                </div>
              )}
              {displayFollowUp.openQuestions.length > 0 && <PrepList title="Open Questions" icon="bi-question-lg" items={displayFollowUp.openQuestions} />}
              {/* Follow-up email */}
              <div className="card" style={{ padding: '14px 16px' }}>
                <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 10 }}>
                  <div style={{ fontSize: 12, fontWeight: 700, color: 'var(--text-muted)' }}>FOLLOW-UP EMAIL DRAFT</div>
                  <button className="btn btn-secondary" style={{ fontSize: 11, height: 26 }} onClick={copyEmail}>
                    {copied ? <><PhosphorIcon className="bi bi-check" />Copied!</> : <><PhosphorIcon className="bi bi-clipboard" />Copy</>}
                  </button>
                </div>
                <pre style={{ fontSize: 12, lineHeight: 1.7, whiteSpace: 'pre-wrap', wordBreak: 'break-word', color: 'var(--text-primary)', background: 'var(--bg-secondary)', padding: '12px 14px', borderRadius: 8, margin: 0, fontFamily: 'inherit' }}>
                  {displayFollowUp.followUpEmail}
                </pre>
              </div>
            </>
          )}
        </div>
      )}

      {/* ── Tasks tab ── */}
      {activeTab === 'tasks' && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          {tasks.length === 0
            ? (
              <div className="card" style={{ padding: '32px 24px', textAlign: 'center' }}>
                <PhosphorIcon className="bi bi-check2-square" style={{ fontSize: 36, color: 'var(--primary)', display: 'block', marginBottom: 12 }} />
                <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 8 }}>No tasks yet</div>
                <div style={{ fontSize: 13, color: 'var(--text-secondary)', marginBottom: 16 }}>Add meeting notes, then extract tasks automatically.</div>
                <button className="btn btn-secondary" onClick={() => setActiveTab('notes')}>Go to Notes →</button>
              </div>
            )
            : tasks.map(t => (
              <div key={t.id} className="card" style={{ padding: '12px 14px', display: 'flex', alignItems: 'flex-start', gap: 10 }}>
                <button onClick={() => updateTaskStatus(t.id, t.status === 'done' ? 'open' : 'done')} style={{
                  width: 18, height: 18, borderRadius: 4, border: '2px solid var(--border)', cursor: 'pointer',
                  background: t.status === 'done' ? 'var(--primary)' : 'transparent',
                  display: 'flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0, marginTop: 2,
                }}>
                  {t.status === 'done' && <PhosphorIcon className="bi bi-check" style={{ color: '#fff', fontSize: 11 }} />}
                </button>
                <div style={{ flex: 1, textDecoration: t.status === 'done' ? 'line-through' : 'none', opacity: t.status === 'done' ? 0.55 : 1 }}>
                  <div style={{ fontSize: 13, fontWeight: 600, marginBottom: 2 }}>{t.title}</div>
                  <div style={{ fontSize: 11, color: 'var(--text-muted)', display: 'flex', gap: 10 }}>
                    {t.owner && <span><PhosphorIcon className="bi bi-person" style={{ marginRight: 2 }} />{t.owner}</span>}
                    {t.dueDate && <span><PhosphorIcon className="bi bi-calendar3" style={{ marginRight: 2 }} />{t.dueDate}</span>}
                  </div>
                </div>
                <span style={{ fontSize: 10, background: `${PRIORITY_COLOR[t.priority] ?? '#888'}20`, color: PRIORITY_COLOR[t.priority] ?? '#888', padding: '2px 6px', borderRadius: 8, fontWeight: 600 }}>{t.priority}</span>
              </div>
            ))
          }
        </div>
      )}
    </div>
  )
}

function PrepSection({ title, icon, content }: { title: string; icon: string; content: string }) {
  return (
    <div className="card" style={{ padding: '14px 16px' }}>
      <div style={{ fontSize: 12, fontWeight: 700, color: 'var(--text-muted)', marginBottom: 6, display: 'flex', alignItems: 'center', gap: 5 }}>
        <PhosphorIcon className={`bi ${icon}`} />{title.toUpperCase()}
      </div>
      <p style={{ fontSize: 13, color: 'var(--text-secondary)', margin: 0, lineHeight: 1.65, whiteSpace: 'pre-wrap' }}>{content}</p>
    </div>
  )
}

function PrepList({ title, icon, items, color }: { title: string; icon: string; items: string[]; color?: string }) {
  return (
    <div className="card" style={{ padding: '14px 16px' }}>
      <div style={{ fontSize: 12, fontWeight: 700, color: color ?? 'var(--text-muted)', marginBottom: 8, display: 'flex', alignItems: 'center', gap: 5 }}>
        <PhosphorIcon className={`bi ${icon}`} />{title.toUpperCase()}
      </div>
      {items.map((item, i) => (
        <div key={i} style={{ display: 'flex', gap: 8, marginBottom: i < items.length - 1 ? 8 : 0, fontSize: 13, color: 'var(--text-secondary)', lineHeight: 1.5 }}>
          <span style={{ color: color ?? 'var(--primary)', flexShrink: 0 }}>•</span>
          <span>{item}</span>
        </div>
      ))}
    </div>
  )
}
