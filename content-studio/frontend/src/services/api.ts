import axios from 'axios'
import { isLocalMode, startLocalSession } from './localMode'

// In local mode there is no sign-in page: renew the owner session instead.
function goToSignIn() {
  if (isLocalMode()) {
    void startLocalSession().then(ok => { if (ok) window.location.reload() })
    return
  }
  window.location.href = '/signin'
}

const api = axios.create({
  baseURL: import.meta.env.VITE_API_BASE_URL || '/api',
  headers: {
    'Content-Type': 'application/json'
  }
})

// CSRF token management - token is fetched from the backend, not hardcoded
let csrfToken: string | null = null
let csrfFetchPromise: Promise<string | null> | null = null

async function fetchCsrfToken(): Promise<string | null> {
  try {
    const response = await axios.get(
      `${import.meta.env.VITE_API_BASE_URL || '/api'}/auth/csrf-token`
    )
    csrfToken = response.data.token
    return csrfToken
  } catch {
    console.warn('Failed to fetch CSRF token')
    return null
  }
}

// Fetch CSRF token once on startup (deduped)
function ensureCsrfToken(): Promise<string | null> {
  if (csrfToken) return Promise.resolve(csrfToken)
  if (!csrfFetchPromise) {
    csrfFetchPromise = fetchCsrfToken().finally(() => {
      csrfFetchPromise = null
    })
  }
  return csrfFetchPromise
}

// Refresh the CSRF token (called when token expires)
async function refreshCsrfToken(): Promise<string | null> {
  csrfToken = null
  csrfFetchPromise = null
  return fetchCsrfToken()
}

// Helper to decode JWT and check expiration
function getTokenExpiration(token: string): number | null {
  try {
    const payload = JSON.parse(atob(token.split('.')[1]))
    return payload.exp ? payload.exp * 1000 : null // Convert to milliseconds
  } catch {
    return null
  }
}

// Refresh when the token is within 7 days of expiring OR has already expired
// (the backend accepts an expired token within its grace window, so we still try
// — this is what keeps a returning user signed in instead of bounced to /signin).
function shouldRefreshToken(token: string): boolean {
  const expiration = getTokenExpiration(token)
  if (!expiration) return false

  const sevenDaysInMs = 7 * 24 * 60 * 60 * 1000
  const timeUntilExpiry = expiration - Date.now()

  return timeUntilExpiry < sevenDaysInMs
}

let isRefreshing = false

// Add token to requests if available
api.interceptors.request.use(async (config) => {
  // Add CSRF token to non-auth requests
  if (!config.url?.includes('/auth/')) {
    const token = await ensureCsrfToken()
    if (token) {
      config.headers['X-CSRF-Token'] = token
    }
  }

  const authToken = localStorage.getItem('authToken')
  if (authToken) {
    config.headers.Authorization = `Bearer ${authToken}`

    // Proactively refresh token if it's close to expiring
    if (shouldRefreshToken(authToken) && !isRefreshing && !config.url?.includes('/auth/refresh-token')) {
      isRefreshing = true
      try {
        const response = await axios.post(
          `${config.baseURL}/auth/refresh-token`,
          {},
          { headers: { Authorization: `Bearer ${authToken}` } }
        )
        const newToken = response.data.token
        localStorage.setItem('authToken', newToken)
        localStorage.setItem('user', JSON.stringify(response.data.user))
        config.headers.Authorization = `Bearer ${newToken}`
      } catch {
        // If refresh fails, continue with existing token
      } finally {
        isRefreshing = false
      }
    }
  }
  // FormData must not carry the instance's default JSON content type: the
  // browser has to set multipart/form-data itself so it can add the boundary.
  // Leaving it as application/json makes ASP.NET refuse the upload with 415
  // before the controller is ever reached.
  if (typeof FormData !== 'undefined' && config.data instanceof FormData) {
    delete config.headers['Content-Type']
  }

  return config
})

// Handle 401 and 403 responses
api.interceptors.response.use(
  (response) => response,
  async (error) => {
    const originalRequest = error.config

    // Handle 401 Unauthorized - try to refresh token first
    if (error.response?.status === 401 && !(originalRequest as any)._retry) {
      (originalRequest as any)._retry = true

      // Don't try to refresh if the 401 came from auth endpoints themselves
      if (originalRequest.url?.includes('/auth/login') || 
          originalRequest.url?.includes('/auth/register') ||
          originalRequest.url?.includes('/auth/refresh-token')) {
        return Promise.reject(error)
      }

      // Try to refresh the token
      const authToken = localStorage.getItem('authToken')
      if (authToken) {
        try {
          const response = await axios.post(
            `${originalRequest.baseURL}/auth/refresh-token`,
            {},
            { 
              headers: { Authorization: `Bearer ${authToken}` },
              ...(({ _retry: true } as any))
            }
          )
          
          const newToken = response.data.token
          localStorage.setItem('authToken', newToken)
          localStorage.setItem('user', JSON.stringify(response.data.user))
          
          // Retry the original request with new token
          originalRequest.headers.Authorization = `Bearer ${newToken}`
          return api.request(originalRequest)
        } catch (refreshError) {
          // Refresh failed - now we logout
          localStorage.removeItem('authToken')
          localStorage.removeItem('user')
          goToSignIn()
          return Promise.reject(refreshError)
        }
      } else {
        // No token to refresh - logout
        localStorage.removeItem('authToken')
        localStorage.removeItem('user')
        goToSignIn()
      }
    }

    // If CSRF token expired, refresh it and retry
    if (error.response?.status === 403 && error.config && !(error.config as any)._csrfRetried) {
      (error.config as any)._csrfRetried = true
      const newToken = await refreshCsrfToken()
      if (newToken) {
        error.config.headers['X-CSRF-Token'] = newToken
        return api.request(error.config)
      }
    }

    return Promise.reject(error)
  }
)

// Kick off CSRF token fetch immediately
ensureCsrfToken()

// On app load, slide the session forward: if a token is present, mint a fresh one
// so the 30-day expiry window resets on every visit. Any user who opens the app
// at least once within (token lifetime + grace) never gets logged out. Failures
// are non-fatal — the response interceptor signs out only a token beyond grace.
async function slideSessionOnLoad(): Promise<void> {
  const authToken = localStorage.getItem('authToken')
  if (!authToken || isRefreshing) return
  isRefreshing = true
  try {
    const baseUrl = import.meta.env.VITE_API_BASE_URL || '/api'
    const response = await axios.post(
      `${baseUrl}/auth/refresh-token`,
      {},
      { headers: { Authorization: `Bearer ${authToken}` } }
    )
    if (response.data?.token) {
      localStorage.setItem('authToken', response.data.token)
      if (response.data.user) localStorage.setItem('user', JSON.stringify(response.data.user))
    }
  } catch {
    // Keep the existing token; a truly-expired (beyond-grace) one is handled by
    // the 401 response interceptor on the next request.
  } finally {
    isRefreshing = false
  }
}
slideSessionOnLoad()

// --- Execution API helpers ---

export interface StreamExecutionParams {
  systemPrompt: string
  messages: { role: string; content: string }[]
  model: string
  userApiKey?: string
  language?: string
}

export async function streamExecution(
  params: StreamExecutionParams,
  onToken: (token: string) => void,
  onDone: (tokensUsed: number) => void,
  onError: (error: string) => void
): Promise<void> {
  const baseUrl = import.meta.env.VITE_API_BASE_URL || '/api'
  const authToken = localStorage.getItem('authToken')

  const response = await fetch(`${baseUrl}/executions/stream`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      ...(authToken ? { Authorization: `Bearer ${authToken}` } : {})
    },
    body: JSON.stringify(params)
  })

  if (!response.ok) {
    onError('Failed to connect to execution service.')
    return
  }

  const reader = response.body!.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  while (true) {
    const { done, value } = await reader.read()
    if (done) break

    buffer += decoder.decode(value, { stream: true })
    const lines = buffer.split('\n')
    buffer = lines.pop() || ''

    for (const line of lines) {
      const trimmed = line.trim()
      if (!trimmed.startsWith('data: ')) continue
      try {
        const data = JSON.parse(trimmed.slice(6))
        if (data.type === 'token') onToken(data.content)
        else if (data.type === 'done') onDone(data.tokensUsed ?? 0)
        else if (data.type === 'error') onError(data.message)
      } catch {
        // skip malformed SSE lines
      }
    }
  }
}

export interface SaveExecutionData {
  id?: string
  promptId?: string
  promptName: string
  systemPrompt: string
  model?: string
  messagesJson: string
  totalTokensUsed: number
}

export const getExecutions = (promptId?: string, page = 1, pageSize = 20) =>
  api.get('/executions', { params: { promptId, page, pageSize } })

export const getExecution = (id: string) =>
  api.get(`/executions/${id}`)

export const saveExecution = (data: SaveExecutionData) =>
  api.post('/executions', data)

export const deleteExecution = (id: string) =>
  api.delete(`/executions/${id}`)

// --- Memory API ---
export const getMemories = () => api.get('/memory')
export const addMemory = (key: string, value: string) => api.post('/memory', { key, value })
export const deleteMemory = (id: number) => api.delete(`/memory/${id}`)
export const clearAllMemories = () => api.delete('/memory')
export const consolidateMemory = () => api.post('/memory/consolidate')

// --- Jobs API ---
export const getJobs = () => api.get('/jobs')
export const getJobRuns = (jobId: string) => api.get(`/jobs/${jobId}/runs`)
export const createJob = (data: object) => api.post('/jobs', data)
export const updateJob = (id: string, data: object) => api.put(`/jobs/${id}`, data)
export const deleteJob = (id: string) => api.delete(`/jobs/${id}`)
export const triggerJob = (id: string) => api.post(`/jobs/${id}/trigger`)

// --- User AI Settings (BYOK) ---
export const getAiSettings = () => api.get('/usersettings/ai')
export const saveAiSettings = (data: object) => api.put('/usersettings/ai', data)

// --- Agents API ---
export const getAgents = () => api.get('/agents')
export const getPublicAgents = () => api.get('/agents/public')
export const getAgentTools = () => api.get('/agents/tools')
export const getAgent = (id: number) => api.get(`/agents/${id}`)
export const createAgent = (data: object) => api.post('/agents', data)
export const updateAgent = (id: number, data: object) => api.put(`/agents/${id}`, data)
export const deleteAgent = (id: number) => api.delete(`/agents/${id}`)
export const runAgent = (id: number, input: string) => api.post(`/agents/${id}/run`, { input })
export const createAgentFromPrompt = (promptId: string) => api.post(`/agents/from-prompt/${promptId}`)
export const createAgentFromPersona = (personaId: number) => api.post(`/agents/from-persona/${personaId}`)

// --- Prompt Ops API ---
export const getPromptVersions = (id: string) => api.get(`/prompts/${id}/versions`)
export const getPromptAudit = (id: string) => api.get(`/prompts/${id}/audit`)
export const requestPromptApproval = (id: string) => api.post(`/prompts/${id}/request-approval`)
export const approvePrompt = (id: string) => api.post(`/prompts/${id}/approve`)
export const getTopPromptPerformers = () => api.get('/prompts/analytics/top-performers')

// --- Life Tracker API ---
export const getLifeTracker = (days = 30) => api.get('/lifetracker', { params: { days } })
export const saveLifeCheckIn = (data: object) => api.post('/lifetracker/checkins', data)
export const createLifeGoal = (data: object) => api.post('/lifetracker/goals', data)
export const updateLifeGoal = (id: number, data: object) => api.put(`/lifetracker/goals/${id}`, data)
export const deleteLifeGoal = (id: number) => api.delete(`/lifetracker/goals/${id}`)
export const createLifeDecision = (data: object) => api.post('/lifetracker/decisions', data)
export const updateLifeDecision = (id: number, data: object) => api.put(`/lifetracker/decisions/${id}`, data)
export const deleteLifeDecision = (id: number) => api.delete(`/lifetracker/decisions/${id}`)
export const saveLifeRelationship = (data: object) => api.post('/lifetracker/relationships', data)
export const updateLifeRelationship = (id: number, data: object) => api.put(`/lifetracker/relationships/${id}`, data)
export const deleteLifeRelationship = (id: number) => api.delete(`/lifetracker/relationships/${id}`)
export const markLifeRelationshipContacted = (id: number) => api.post(`/lifetracker/relationships/${id}/contacted`)
export const getLifeWeeklySummary = (days = 7) => api.post('/lifetracker/weekly-summary', { days })
export const uploadLifeEvidence = (formData: FormData) => api.post('/lifetracker/evidence', formData, {
  headers: { 'Content-Type': 'multipart/form-data' }
})
export const getLifeEvidence = (id: number) => api.get(`/lifetracker/evidence/${id}`)
export const downloadLifeEvidence = (id: number) => api.get(`/lifetracker/evidence/${id}/download`, { responseType: 'blob' })
export const deleteLifeEvidence = (id: number) => api.delete(`/lifetracker/evidence/${id}`)
export const askLifeCoach = (question: string) => api.post('/lifetracker/coach', { question })
export const saveLifeCloudLink = (data: object) => api.post('/lifetracker/cloud-links', data)
export const syncLifeCloudLink = (id: number) => api.post(`/lifetracker/cloud-links/${id}/sync`)
export const deleteLifeCloudLink = (id: number) => api.delete(`/lifetracker/cloud-links/${id}`)

// ── Life tracker: voice journal & scenario projection ───────────────────────

export interface VoiceExtraction {
  headline: string
  narrative: string
  sentiment: string | null
  checkIn: {
    mood: number | null; energy: number | null; focus: number | null
    sleepHours: number | null; exerciseMinutes: number | null
    topTasks: string | null; gratitude: string | null; notes: string | null
  } | null
  goals: { title: string; area: string | null; notes: string | null; progress: number | null }[]
  decisions: { title: string; reasoning: string | null; outcome: string | null }[]
  relationships: { name: string; circle: string | null; notes: string | null }[]
  themes: string[]
  openLoops: string[]
}

export interface LifeVoiceEntry {
  id: number
  entryDate: string
  headline: string
  narrative: string
  sentiment: string | null
  durationSeconds: number
  status: 'pending' | 'ready' | 'failed'
  errorMessage: string | null
  applied: boolean
  appliedAt: string | null
  createdAt: string
  transcript?: string
  extraction: VoiceExtraction | null
}

export interface LifeScenario {
  id: number
  batchId: string
  title: string
  kind: 'current-path' | 'opportunity' | 'risk' | 'wildcard'
  horizon: string
  summary: string
  likelihood: number
  isPinned: boolean
  createdAt: string
  drivers: string[]
  assumptions: string[]
  actions: string[]
  signals: string[]
  areas: string[]
}

export interface LifeGuidance {
  read: string
  nextActions: string[]
  questions: string[]
  patterns: string[]
  caution: string | null
}

/** Upload a spoken entry. The blob comes straight from MediaRecorder. */
export const recordLifeVoiceEntry = (audio: Blob, durationSeconds: number, entryDate?: string) => {
  const fd = new FormData()
  // The filename matters: Whisper infers the codec from the extension.
  fd.append('audio', audio, 'entry.webm')
  fd.append('durationSeconds', String(Math.round(durationSeconds)))
  if (entryDate) fd.append('entryDate', entryDate)
  return api.post<LifeVoiceEntry>('/lifetracker/voice', fd, {
    headers: { 'Content-Type': 'multipart/form-data' },
    timeout: 180000, // transcription + extraction on a long entry
  })
}

export const writeLifeVoiceEntry = (text: string, entryDate?: string) =>
  api.post<LifeVoiceEntry>('/lifetracker/voice/text', { text, entryDate }, { timeout: 120000 })

export const getLifeVoiceEntries = (take = 30, skip = 0) =>
  api.get<{ total: number; totalMinutes: number; items: LifeVoiceEntry[] }>(
    '/lifetracker/voice', { params: { take, skip } })

export const getLifeVoiceEntry = (id: number) =>
  api.get<LifeVoiceEntry>(`/lifetracker/voice/${id}`)

export const reprocessLifeVoiceEntry = (id: number) =>
  api.post<LifeVoiceEntry>(`/lifetracker/voice/${id}/reprocess`, {}, { timeout: 120000 })

export const deleteLifeVoiceEntry = (id: number) =>
  api.delete(`/lifetracker/voice/${id}`)

/** Commit chosen items into the tracker. Omit an index list to accept all of that type. */
export const applyLifeVoiceEntry = (id: number, selection?: {
  applyCheckIn?: boolean
  goalIndexes?: number[]
  decisionIndexes?: number[]
  relationshipIndexes?: number[]
}) => api.post<{ id: number; applied: boolean; created: string[] }>(
  `/lifetracker/voice/${id}/apply`, selection ?? {})

export const generateLifeScenarios = (horizon: '3-months' | '1-year' | '3-years' = '1-year', focus?: string) =>
  api.post<{ batchId: string; horizon: string; scenarios: LifeScenario[] }>(
    '/lifetracker/scenarios', { horizon, focus }, { timeout: 180000 })

export const getLifeScenarios = (batchId?: string, pinnedOnly = false) =>
  api.get<{ scenarios: LifeScenario[] }>('/lifetracker/scenarios', { params: { batchId, pinnedOnly } })

export const pinLifeScenario = (id: number, pinned?: boolean) =>
  api.post<{ id: number; isPinned: boolean }>(`/lifetracker/scenarios/${id}/pin`, { pinned })

export const getLifeGuidance = (question?: string) =>
  api.post<LifeGuidance>('/lifetracker/guidance', { question }, { timeout: 120000 })

/** Auto-save generated content to the Prompts module. Returns the saved prompt ID or null. */
export async function saveGeneratedPrompt(content: string, title: string): Promise<string | null> {
  try {
    const token = localStorage.getItem('authToken')
    if (!token || !content?.trim()) return null
    const res = await api.post<{ id: string }>('/prompts', {
      name: title.slice(0, 80),
      content: content.trim(),
      category: 'AI Generated',
    })
    return res.data?.id ?? null
  } catch { return null }
}

// ── Prompt operations: comparison, insights, testing, linting ──────────────

export interface ModelComparisonResult {
  model: string
  result?: string
  error?: string
  tokensUsed: number
  latencyMs: number
  estimatedCostUsd: number
  costIsEstimate: boolean
  provider?: string
  score?: number
}

export interface CompareResponse {
  results: ModelComparisonResult[]
  recommendedModel?: string
  recommendationReason?: string
}

export const getModelPricing = () => api.get('/prompts/models/pricing')

export const comparePromptModels = (data: {
  promptContent: string
  userInput?: string
  models: string[]
  scoreOutputs?: boolean
  scoringCriteria?: string
  promptId?: string
  promptName?: string
}) => api.post<CompareResponse>('/prompts/compare', data)

export const updatePromptModelSettings = (id: string, data: {
  preferredModel?: string | null
  fallbackModels?: string[]
  costOptimalRouting?: boolean
  routingMinScore?: number
}) => api.put(`/prompts/${id}/model-settings`, data)

export const runPrompt = (id: string, userInput?: string, modelOverride?: string) =>
  api.post(`/prompts/${id}/run`, { userInput, modelOverride })

// Structured output
export const updatePromptSchema = (id: string, outputSchema: string | null, enforceSchema: boolean) =>
  api.put(`/prompts/${id}/schema`, { outputSchema, enforceSchema })

export const validateAgainstSchema = (output: string, schema: string) =>
  api.post('/prompts/validate-schema', { output, schema })

// Linting
export const lintPrompt = (content: string, variables?: string[], useAi = false) =>
  api.post('/prompts/lint', { content, variables, useAi })

// Version diff
export const diffPromptVersions = (id: string, versionA: number, versionB: number) =>
  api.get(`/prompts/${id}/versions/${versionA}/diff/${versionB}`)

// Ratings loop
export const getPromptInsights = (id: string, days = 30) =>
  api.get(`/prompts/${id}/insights`, { params: { days } })

export const suggestPromptImprovement = (id: string, sampleSize = 5, additionalContext?: string) =>
  api.post(`/prompts/${id}/suggest-improvement`, { sampleSize, additionalContext })

export const applyPromptImprovement = (id: string, content: string, changeNote?: string) =>
  api.post(`/prompts/${id}/apply-improvement`, { content, changeNote })

// Conversation import
export const importConversations = (rawJson: string, maxCandidates = 50) =>
  api.post('/prompts/import-conversations', { rawJson, maxCandidates })

export const saveImportedPrompts = (prompts: Array<{
  name?: string; content: string; category?: string; description?: string
}>) => api.post('/prompts/import-conversations/save', { prompts })

// ── Test suites & regression detection ─────────────────────────────────────

export const getTestSuites = () => api.get('/test-suites')
export const getTestSuite = (id: string) => api.get(`/test-suites/${id}`)
export const createTestSuite = (data: object) => api.post('/test-suites', data)
export const updateTestSuite = (id: string, data: object) => api.put(`/test-suites/${id}`, data)
export const deleteTestSuite = (id: string) => api.delete(`/test-suites/${id}`)
export const runTestSuite = (id: string) => api.post(`/test-suites/${id}/run`)
export const getTestSuiteRuns = (id: string, limit = 50) =>
  api.get(`/test-suites/${id}/runs`, { params: { limit } })
export const getTestSuiteRun = (runId: string) => api.get(`/test-suites/runs/${runId}`)

// ── Observability ──────────────────────────────────────────────────────────

export const getTraces = (params?: {
  promptId?: string; model?: string; status?: string; source?: string
  days?: number; page?: number; pageSize?: number
}) => api.get('/traces', { params })

export const getTraceSummary = (days = 30) => api.get('/traces/summary', { params: { days } })

export const getAlertRules = () => api.get('/traces/alerts')
export const createAlertRule = (data: object) => api.post('/traces/alerts', data)
export const updateAlertRule = (id: string, data: object) => api.put(`/traces/alerts/${id}`, data)
export const deleteAlertRule = (id: string) => api.delete(`/traces/alerts/${id}`)

// ── Teams & approvals ──────────────────────────────────────────────────────

export const getTeams = () => api.get('/teams')
export const getTeam = (id: string) => api.get(`/teams/${id}`)
export const createTeam = (data: object) => api.post('/teams', data)
export const updateTeam = (id: string, data: object) => api.put(`/teams/${id}`, data)
export const deleteTeam = (id: string) => api.delete(`/teams/${id}`)

export const addTeamMember = (teamId: string, email: string, role: string) =>
  api.post(`/teams/${teamId}/members`, { email, role })
export const updateTeamMemberRole = (teamId: string, memberId: string, role: string) =>
  api.put(`/teams/${teamId}/members/${memberId}`, { role })
export const removeTeamMember = (teamId: string, memberId: string) =>
  api.delete(`/teams/${teamId}/members/${memberId}`)

export const getTeamPrompts = (teamId: string) => api.get(`/teams/${teamId}/prompts`)
export const addPromptToTeam = (teamId: string, promptId: string) =>
  api.post(`/teams/${teamId}/prompts/${promptId}`)
export const removePromptFromTeam = (teamId: string, promptId: string) =>
  api.delete(`/teams/${teamId}/prompts/${promptId}`)

export const getTeamApprovals = (teamId: string, status = 'pending') =>
  api.get(`/teams/${teamId}/approvals`, { params: { status } })
export const getApproval = (approvalId: string) => api.get(`/teams/approvals/${approvalId}`)
export const approveChange = (approvalId: string, comment?: string) =>
  api.post(`/teams/approvals/${approvalId}/approve`, { comment })
export const rejectChange = (approvalId: string, comment?: string) =>
  api.post(`/teams/approvals/${approvalId}/reject`, { comment })
export const withdrawChange = (approvalId: string) =>
  api.post(`/teams/approvals/${approvalId}/withdraw`)
export const getApprovalInbox = () => api.get('/teams/approvals/inbox')

// ── Meeting intelligence ───────────────────────────────────────────────────

export const getMeetingOutcome = (sessionId: string) =>
  api.get(`/meeting-intelligence/${sessionId}`)
export const generateMeetingOutcome = (sessionId: string) =>
  api.post(`/meeting-intelligence/${sessionId}/generate`)
export const searchMeetings = (q: string, limit = 20) =>
  api.get('/meeting-intelligence/search', { params: { q, limit } })

export const getMeetingActionItems = (status = 'open', owner?: string) =>
  api.get('/meeting-intelligence/action-items', { params: { status, owner } })
export const updateMeetingActionItem = (id: string, data: {
  text?: string; owner?: string; dueDate?: string; status?: string
}) => api.put(`/meeting-intelligence/action-items/${id}`, data)

export const getMeetingSeriesList = () => api.get('/meeting-intelligence/series')
export const getMeetingSeries = (id: string) => api.get(`/meeting-intelligence/series/${id}`)

export const sendMeetingFollowUp = (sessionId: string, data: {
  recipients: string[]; subject?: string; body?: string
}) => api.post(`/meeting-intelligence/${sessionId}/send-follow-up`, data)

export default api
