import { useCallback, useEffect, useLayoutEffect, useMemo, useState } from 'react'
import PhosphorIcon from '../components/PhosphorIcon'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import api from '../services/api'
import { GameField } from '../gamefield'
import type { FieldAgent, FieldModel } from '../gamefield'

/**
 * The Colony — every coding-agent thread on this machine, as a crew.
 *
 * Where the other Gamefield pages show the agents inside one run, this one
 * shows the whole estate: it reads the sessions Claude Code has already written
 * to disk and gives each thread a desk.
 *
 * The page deliberately owns no header of its own. Breadcrumbs carry the page
 * identity like every other page, and the floor's own title bar names the room,
 * so the title is not printed twice.
 */

interface ColonyThread {
  id: string
  title: string
  preview: string
  project: string
  gitBranch: string
  model: string
  lastActivityAt: number
  sizeBytes: number
  running: boolean
  hasError: boolean
  source: string
}

interface ColonyResponse {
  available: boolean
  reason?: string | null
  threadCount: number
  projectCount: number
  threads: ColonyThread[]
}

/** Threads untouched for this long are asleep rather than merely idle. */
const STALE_MS = 3 * 24 * 60 * 60 * 1000

/** Desks stop being readable long before this; showing every thread is worse. */
const MAX_DESKS = 24

function relativeTime(ms: number): string {
  if (!ms) return 'unknown'
  const mins = Math.round((Date.now() - ms) / 60000)
  if (mins < 1) return 'just now'
  if (mins < 60) return `${mins}m ago`
  const hours = Math.round(mins / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.round(hours / 24)}d ago`
}

function formatSize(bytes: number): string {
  if (bytes > 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)}MB`
  if (bytes > 1024) return `${Math.round(bytes / 1024)}KB`
  return `${bytes}B`
}

/**
 * A stable colour per repo, so every crew member on the same project shares a
 * desk lamp. Hashing the name keeps the mapping across restarts without
 * storing anything.
 */
function accentFor(project: string): string {
  let h = 2166136261
  for (let i = 0; i < project.length; i++) {
    h ^= project.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  const palette = [
    '#e4573d', '#3f8f6d', '#4a7fb5', '#d9930b', '#7668b4',
    '#c2557f', '#2f8f8f', '#b5834a', '#5965d8', '#8f6f3f',
  ]
  return palette[Math.abs(h) % palette.length]
}

/**
 * The floor should fill what is left of the window under the app chrome. A
 * fixed height left two thirds of a 1440p screen empty, which is the one thing
 * a room full of desks cannot afford.
 */
function useFloorHeight(): number {
  const measure = () =>
    typeof window === 'undefined'
      ? 640
      : Math.max(420, Math.round(window.innerHeight - 190))

  const [height, setHeight] = useState(measure)

  useEffect(() => {
    const onResize = () => setHeight(measure())
    window.addEventListener('resize', onResize)
    return () => window.removeEventListener('resize', onResize)
  }, [])

  return height
}

export default function Colony() {
  const { setBreadcrumbs } = useLayoutControls()
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'The Colony' }])
    return () => setBreadcrumbs([])
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const [data, setData] = useState<ColonyResponse | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [projectFilter, setProjectFilter] = useState<string>('all')
  const floorHeight = useFloorHeight()

  const load = useCallback(async () => {
    try {
      const res = await api.get<ColonyResponse>('/colony/threads')
      setData(res.data)
      setError(null)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not reach the colony endpoint.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    load()
    // Threads change while you work, but nothing on the deck needs sub-minute
    // freshness — and this reads files.
    const timer = setInterval(load, 30000)
    return () => clearInterval(timer)
  }, [load])

  const projects = useMemo(() => {
    if (!data?.threads) return []
    const counts = new Map<string, number>()
    for (const t of data.threads) counts.set(t.project, (counts.get(t.project) || 0) + 1)
    return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
  }, [data])

  const visible = useMemo(() => {
    const all = data?.threads || []
    return projectFilter === 'all' ? all : all.filter(t => t.project === projectFilter)
  }, [data, projectFilter])

  const field = useMemo<FieldModel>(() => {
    const now = Date.now()
    const shown = [...visible]
      .sort((a, b) => b.lastActivityAt - a.lastActivityAt)
      .slice(0, MAX_DESKS)

    const agents: FieldAgent[] = shown.map(t => {
      const stale = now - t.lastActivityAt > STALE_MS
      const status: FieldAgent['status'] =
        t.hasError ? 'failed'
        : t.running ? 'working'
        : stale ? 'idle'
        : 'done'

      return {
        id: t.id,
        name: t.title,
        role: t.gitBranch ? `${t.project} · ${t.gitBranch}` : t.project,
        status,
        detail: t.running ? t.preview || 'Working…' : undefined,
        output: [
          t.preview && `“${t.preview}”`,
          `Project: ${t.project}`,
          t.gitBranch && `Branch: ${t.gitBranch}`,
          t.model && `Model: ${t.model}`,
          `Transcript: ${formatSize(t.sizeBytes)}`,
          `Last active: ${relativeTime(t.lastActivityAt)}`,
        ].filter(Boolean).join('\n'),
        accent: accentFor(t.project),
      }
    })

    const runningCount = visible.filter(t => t.running).length
    const scope = projectFilter === 'all'
      ? `${visible.length} thread${visible.length === 1 ? '' : 's'} across ${projects.length} repo${projects.length === 1 ? '' : 's'}`
      : `${visible.length} thread${visible.length === 1 ? '' : 's'} in ${projectFilter}`

    return {
      title: 'The Colony',
      subtitle: data?.available
        ? scope
          + (shown.length < visible.length ? ` — newest ${shown.length} at desks` : '')
          + (runningCount ? ` · ${runningCount} working now` : '')
        : 'No sessions found on this machine.',
      running: runningCount > 0,
      agents,
    }
  }, [visible, projects.length, projectFilter, data?.available])

  if (loading) {
    return (
      <div className="container-fluid py-5 text-center">
        <div className="spinner-border text-primary" role="status" />
        <p className="text-muted mt-3 mb-0">Reading the session files…</p>
      </div>
    )
  }

  const unavailable = error || (data && !data.available)

  if (unavailable) {
    return (
      <div className="container-fluid py-4">
        <div className="alert alert-info d-flex gap-2 align-items-start mb-0">
          <PhosphorIcon className="bi bi-info-circle-fill mt-1" />
          <div>
            <strong className="d-block">Nothing to show here yet.</strong>
            <span className="small">{error || data?.reason}</span>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="container-fluid px-0">
      {/* The repo filter rides in the floor's own toolbar rather than in a
          second header above it, so the page has one title, not two. */}
      <GameField
        model={field}
        height={floorHeight}
        actions={[
          {
            id: 'rescan',
            label: 'Rescan',
            icon: 'bi-arrow-clockwise',
            onClick: load,
            tone: 'primary',
          },
        ]}
      >
        {projects.length > 1 && (
          <div className="d-flex align-items-center gap-2 px-3 py-2">
            <label className="form-label small text-muted mb-0" htmlFor="colony-repo">
              Repo
            </label>
            <select
              id="colony-repo"
              className="form-select form-select-sm"
              style={{ maxWidth: 280 }}
              value={projectFilter}
              onChange={e => setProjectFilter(e.target.value)}
            >
              <option value="all">All repos ({data?.threadCount ?? 0})</option>
              {projects.map(([name, count]) => (
                <option key={name} value={name}>{name} ({count})</option>
              ))}
            </select>
          </div>
        )}
      </GameField>
    </div>
  )
}
