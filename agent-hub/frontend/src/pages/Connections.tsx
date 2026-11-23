import PhosphorIcon from '../components/PhosphorIcon'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import api from '../services/api'
import './Connections.css'

interface CatalogServer {
  name: string
  title: string
  description: string
  category: string
  publisher: string
  transport: string
  isConnectable: boolean
  websiteUrl?: string | null
  version: string
}

interface ConnectionTool {
  id: number
  toolName: string
  description: string
  enabled: boolean
  approvalMode: 'auto' | 'ask'
  isWrite: boolean
}

interface Connection {
  id: string
  serverName: string
  label: string
  remoteUrl: string
  authType: string
  status: 'connected' | 'error' | 'disabled' | 'needs_auth'
  lastError?: string | null
  toolCount: number
  lastCheckedAt?: string | null
  hasSecret: boolean
  tools: ConnectionTool[]
}

const PAGE_SIZE = 60

const STATUS_LABEL: Record<Connection['status'], { text: string; tone: string }> = {
  connected: { text: 'Connected', tone: 'ok' },
  error: { text: 'Error', tone: 'bad' },
  disabled: { text: 'Paused', tone: 'muted' },
  needs_auth: { text: 'Needs credentials', tone: 'warn' },
}

export default function Connections() {
  const { setBreadcrumbs } = useLayoutControls()
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Connections' }])
    return () => setBreadcrumbs([])
  }, [setBreadcrumbs])

  const [connections, setConnections] = useState<Connection[]>([])
  const [loading, setLoading] = useState(true)
  const [toast, setToast] = useState('')
  const [pickerOpen, setPickerOpen] = useState(false)
  const [expanded, setExpanded] = useState<string | null>(null)

  const showToast = (message: string) => {
    setToast(message)
    window.setTimeout(() => setToast(''), 3200)
  }

  const loadConnections = useCallback(async () => {
    try {
      const { data } = await api.get('/mcp/connections')
      setConnections(data)
    } catch {
      showToast('Could not load your connections.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { loadConnections() }, [loadConnections])

  const toggleTool = async (tool: ConnectionTool, patch: Partial<Pick<ConnectionTool, 'enabled' | 'approvalMode'>>) => {
    // Optimistic — a toggle that lags feels broken.
    setConnections(current => current.map(c => ({
      ...c,
      tools: c.tools.map(t => (t.id === tool.id ? { ...t, ...patch } : t)),
    })))
    try {
      await api.put(`/mcp/tools/${tool.id}`, patch)
    } catch {
      showToast('Could not save that change.')
      loadConnections()
    }
  }

  const refresh = async (connection: Connection) => {
    try {
      const { data } = await api.post(`/mcp/connections/${connection.id}/refresh`)
      showToast(data.ok ? `${connection.label}: ${data.toolCount} tools available` : (data.error || 'Could not reach that server.'))
      loadConnections()
    } catch {
      showToast('Could not reach that server.')
    }
  }

  const remove = async (connection: Connection) => {
    if (!window.confirm(`Disconnect ${connection.label}? Its tools stop being available to your agents.`)) return
    try {
      await api.delete(`/mcp/connections/${connection.id}`)
      showToast(`Disconnected ${connection.label}`)
      loadConnections()
    } catch {
      showToast('Could not disconnect.')
    }
  }

  const totalTools = connections.reduce((sum, c) => sum + c.tools.filter(t => t.enabled).length, 0)

  return (
    <div className="cx-page">
      <div className="cx-hero">
        <div>
          <h1><PhosphorIcon className="bi bi-plug" /> Connections</h1>
          <p>
            Connect any MCP server and its tools become available to your agents and workflows.
            {totalTools > 0 && <> <strong>{totalTools} tool{totalTools === 1 ? '' : 's'}</strong> enabled right now.</>}
          </p>
        </div>
        <button className="cx-btn cx-btn-primary" onClick={() => setPickerOpen(true)}>
          <PhosphorIcon className="bi bi-plus-lg" /> Add connection
        </button>
      </div>

      {loading ? (
        <div className="cx-empty"><span className="cx-spin" /> Loading connections…</div>
      ) : connections.length === 0 ? (
        <div className="cx-empty">
          <PhosphorIcon className="bi bi-plug" />
          <h3>No connections yet</h3>
          <p>Browse hundreds of MCP servers — GitHub, Slack, databases, search, payments — and pick the ones you want your agents to use.</p>
          <button className="cx-btn cx-btn-primary" onClick={() => setPickerOpen(true)}>
            <PhosphorIcon className="bi bi-search" /> Browse integrations
          </button>
        </div>
      ) : (
        <div className="cx-list">
          {connections.map(connection => {
            const status = STATUS_LABEL[connection.status] ?? STATUS_LABEL.error
            const isOpen = expanded === connection.id
            return (
              <div className="cx-card" key={connection.id}>
                <div className="cx-card-head">
                  <button
                    className="cx-card-main"
                    onClick={() => setExpanded(isOpen ? null : connection.id)}
                    aria-expanded={isOpen}
                  >
                    <PhosphorIcon className={`bi ${isOpen ? 'bi-chevron-down' : 'bi-chevron-right'}`} />
                    <div>
                      <strong>{connection.label}</strong>
                      <small>{connection.serverName}</small>
                    </div>
                  </button>
                  <span className={`cx-status ${status.tone}`}>{status.text}</span>
                  <span className="cx-toolcount">
                    {connection.tools.filter(t => t.enabled).length}/{connection.tools.length} tools
                  </span>
                  <div className="cx-card-actions">
                    <button onClick={() => refresh(connection)} title="Re-check and refresh tools"><PhosphorIcon className="bi bi-arrow-repeat" /></button>
                    <button onClick={() => remove(connection)} title="Disconnect"><PhosphorIcon className="bi bi-trash" /></button>
                  </div>
                </div>

                {connection.lastError && <div className="cx-card-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {connection.lastError}</div>}

                {isOpen && (
                  <div className="cx-tools">
                    {connection.tools.length === 0 ? (
                      <p className="cx-muted">No tools discovered. Try refreshing.</p>
                    ) : connection.tools.map(tool => (
                      <div className="cx-tool" key={tool.id}>
                        <label className="cx-switch">
                          <input
                            type="checkbox"
                            checked={tool.enabled}
                            onChange={e => toggleTool(tool, { enabled: e.target.checked })}
                          />
                          <span />
                        </label>
                        <div className="cx-tool-main">
                          <strong>{tool.toolName}</strong>
                          {tool.description && <small>{tool.description}</small>}
                        </div>
                        {tool.isWrite && <span className="cx-write" title="This tool can change or send data">writes</span>}
                        <select
                          value={tool.approvalMode}
                          onChange={e => toggleTool(tool, { approvalMode: e.target.value as 'auto' | 'ask' })}
                          disabled={!tool.enabled}
                          title="Whether a run may use this tool without asking"
                        >
                          <option value="ask">Ask first</option>
                          <option value="auto">Run automatically</option>
                        </select>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            )
          })}
        </div>
      )}

      {pickerOpen && (
        <IntegrationPicker
          connectedNames={connections.map(c => c.serverName)}
          onClose={() => setPickerOpen(false)}
          onConnected={message => { showToast(message); loadConnections() }}
        />
      )}

      {toast && <div className="cx-toast">{toast}</div>}
    </div>
  )
}

// ── Picker modal ────────────────────────────────────────────────────────────

function IntegrationPicker({
  connectedNames,
  onClose,
  onConnected,
}: {
  connectedNames: string[]
  onClose: () => void
  onConnected: (message: string) => void
}) {
  const [query, setQuery] = useState('')
  const [category, setCategory] = useState('All')
  const [servers, setServers] = useState<CatalogServer[]>([])
  const [categories, setCategories] = useState<{ category: string; count: number }[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [selected, setSelected] = useState<CatalogServer | null>(null)
  const connected = useMemo(() => new Set(connectedNames), [connectedNames])
  const searchRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') onClose() }
    window.addEventListener('keydown', onKey)
    searchRef.current?.focus()
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  // Debounced search so typing doesn't fire a request per keystroke.
  useEffect(() => {
    setLoading(true)
    const timer = window.setTimeout(async () => {
      try {
        const { data } = await api.get('/mcp/catalog', { params: { q: query || undefined, category, take: PAGE_SIZE, skip: 0 } })
        setServers(data.servers)
        setCategories(data.categories)
        setTotal(data.total)
      } catch {
        setServers([])
      } finally {
        setLoading(false)
      }
    }, 250)
    return () => window.clearTimeout(timer)
  }, [query, category])

  const loadMore = async () => {
    setLoadingMore(true)
    try {
      const { data } = await api.get('/mcp/catalog', { params: { q: query || undefined, category, take: PAGE_SIZE, skip: servers.length } })
      setServers(current => [...current, ...data.servers])
    } finally {
      setLoadingMore(false)
    }
  }

  return (
    <div className="cx-modal-backdrop" onClick={onClose}>
      <div className="cx-modal" onClick={e => e.stopPropagation()} role="dialog" aria-label="Add a connection">
        {selected ? (
          <ConnectForm
            server={selected}
            onBack={() => setSelected(null)}
            onDone={message => { onConnected(message); onClose() }}
          />
        ) : (
          <>
            <div className="cx-modal-head">
              <div>
                <h2>Add a connection</h2>
                <p>{total.toLocaleString()} MCP servers available{category !== 'All' ? ` in ${category}` : ''}</p>
              </div>
              <button className="cx-modal-close" onClick={onClose} aria-label="Close"><PhosphorIcon className="bi bi-x-lg" /></button>
            </div>

            <div className="cx-search">
              <PhosphorIcon className="bi bi-search" />
              <input
                ref={searchRef}
                value={query}
                onChange={e => setQuery(e.target.value)}
                placeholder="Search GitHub, Slack, Postgres, Stripe…"
              />
            </div>

            <div className="cx-cats">
              <button className={category === 'All' ? 'active' : ''} onClick={() => setCategory('All')}>All</button>
              {categories.map(c => (
                <button key={c.category} className={category === c.category ? 'active' : ''} onClick={() => setCategory(c.category)}>
                  {c.category} <small>{c.count}</small>
                </button>
              ))}
            </div>

            <div className="cx-grid">
              {loading ? (
                <div className="cx-grid-note"><span className="cx-spin" /> Loading catalogue…</div>
              ) : servers.length === 0 ? (
                <div className="cx-grid-note">
                  Nothing matched “{query}”. The catalogue syncs daily from the official MCP registry.
                </div>
              ) : servers.map(server => {
                const already = connected.has(server.name)
                return (
                  <button
                    key={server.name}
                    className={`cx-server${already ? ' connected' : ''}`}
                    onClick={() => !already && setSelected(server)}
                    disabled={already}
                  >
                    <div className="cx-server-head">
                      <span className="cx-server-icon"><PhosphorIcon className="bi bi-box" /></span>
                      <strong>{server.title}</strong>
                      {already && <span className="cx-badge ok">Connected</span>}
                    </div>
                    <p>{server.description || 'No description provided.'}</p>
                    <div className="cx-server-foot">
                      <span className="cx-cat">{server.category}</span>
                      <span className="cx-pub">{server.publisher}</span>
                    </div>
                  </button>
                )
              })}
            </div>

            {!loading && servers.length < total && (
              <div className="cx-more">
                <button className="cx-btn" onClick={loadMore} disabled={loadingMore}>
                  {loadingMore ? <><span className="cx-spin" /> Loading…</> : `Show more (${total - servers.length} left)`}
                </button>
              </div>
            )}
          </>
        )}
      </div>
    </div>
  )
}

// ── Connect step ────────────────────────────────────────────────────────────

function ConnectForm({
  server,
  onBack,
  onDone,
}: {
  server: CatalogServer
  onBack: () => void
  onDone: (message: string) => void
}) {
  const [authType, setAuthType] = useState<'none' | 'bearer' | 'header'>('none')
  const [secret, setSecret] = useState('')
  const [headerName, setHeaderName] = useState('X-Api-Key')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const connect = async () => {
    setBusy(true)
    setError('')
    try {
      const { data } = await api.post('/mcp/connections', {
        serverName: server.name,
        authType,
        secret: authType === 'none' ? undefined : secret,
        headerName: authType === 'header' ? headerName : undefined,
      })
      if (data.ok) {
        onDone(`Connected ${server.title} — ${data.toolCount} tool${data.toolCount === 1 ? '' : 's'} available`)
      } else {
        // The connection row exists but could not list tools; say so precisely.
        setError(data.error || 'Connected, but the server did not return any tools.')
      }
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not connect to that server.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <div className="cx-modal-head">
        <div>
          <button className="cx-back" onClick={onBack}><PhosphorIcon className="bi bi-arrow-left" /> All integrations</button>
          <h2>{server.title}</h2>
          <p>{server.description}</p>
        </div>
      </div>

      <div className="cx-connect">
        <div className="cx-field">
          <label>Authentication</label>
          <select value={authType} onChange={e => setAuthType(e.target.value as typeof authType)}>
            <option value="none">None — public server</option>
            <option value="bearer">Bearer token</option>
            <option value="header">Custom header (API key)</option>
          </select>
          <small>
            Most servers need a token from the provider. Credentials are encrypted before storage and never shown again.
          </small>
        </div>

        {authType === 'header' && (
          <div className="cx-field">
            <label>Header name</label>
            <input value={headerName} onChange={e => setHeaderName(e.target.value)} placeholder="X-Api-Key" />
          </div>
        )}

        {authType !== 'none' && (
          <div className="cx-field">
            <label>{authType === 'bearer' ? 'Token' : 'Header value'}</label>
            <input type="password" value={secret} onChange={e => setSecret(e.target.value)} placeholder="Paste your credential" autoComplete="off" />
          </div>
        )}

        <div className="cx-endpoint">
          <PhosphorIcon className="bi bi-hdd-network" /> {server.name}
          {server.websiteUrl && <> · <a href={server.websiteUrl} target="_blank" rel="noreferrer">docs</a></>}
        </div>

        {error && <div className="cx-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

        <div className="cx-connect-actions">
          <button className="cx-btn cx-btn-primary" onClick={connect} disabled={busy || (authType !== 'none' && !secret.trim())}>
            {busy ? <><span className="cx-spin" /> Connecting…</> : <><PhosphorIcon className="bi bi-plug" /> Connect</>}
          </button>
          <button className="cx-btn" onClick={onBack} disabled={busy}>Cancel</button>
        </div>

        <p className="cx-note">
          <PhosphorIcon className="bi bi-shield-check" /> Tools that can change or send data are set to <strong>ask first</strong> —
          a run pauses for your approval before using them. You can change that per tool after connecting.
        </p>
      </div>
    </>
  )
}
