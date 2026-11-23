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

// CSRF token management — token is fetched from the backend, not hardcoded
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

function ensureCsrfToken(): Promise<string | null> {
  if (csrfToken) return Promise.resolve(csrfToken)
  if (!csrfFetchPromise) {
    csrfFetchPromise = fetchCsrfToken().finally(() => {
      csrfFetchPromise = null
    })
  }
  return csrfFetchPromise
}

async function refreshCsrfToken(): Promise<string | null> {
  csrfToken = null
  csrfFetchPromise = null
  return fetchCsrfToken()
}

function getTokenExpiration(token: string): number | null {
  try {
    const payload = JSON.parse(atob(token.split('.')[1]))
    return payload.exp ? payload.exp * 1000 : null
  } catch {
    return null
  }
}

// Refresh when the token is within 7 days of expiring OR has already expired
// (the backend accepts an expired token within its grace window — this is what
// keeps a returning user signed in instead of bounced to /signin).
function shouldRefreshToken(token: string): boolean {
  const expiration = getTokenExpiration(token)
  if (!expiration) return false

  const sevenDaysInMs = 7 * 24 * 60 * 60 * 1000
  return expiration - Date.now() < sevenDaysInMs
}

let isRefreshing = false

api.interceptors.request.use(async (config) => {
  if (!config.url?.includes('/auth/')) {
    const token = await ensureCsrfToken()
    if (token) {
      config.headers['X-CSRF-Token'] = token
    }
  }

  const authToken = localStorage.getItem('authToken')
  if (authToken) {
    config.headers.Authorization = `Bearer ${authToken}`

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
  return config
})

api.interceptors.response.use(
  (response) => response,
  async (error) => {
    const originalRequest = error.config

    // 401 — try a token refresh before signing out
    if (error.response?.status === 401 && !(originalRequest as any)._retry) {
      (originalRequest as any)._retry = true

      if (originalRequest.url?.includes('/auth/login') ||
          originalRequest.url?.includes('/auth/register') ||
          originalRequest.url?.includes('/auth/refresh-token')) {
        return Promise.reject(error)
      }

      const authToken = localStorage.getItem('authToken')
      if (authToken) {
        try {
          const response = await axios.post(
            `${originalRequest.baseURL}/auth/refresh-token`,
            {},
            { headers: { Authorization: `Bearer ${authToken}` } }
          )

          const newToken = response.data.token
          localStorage.setItem('authToken', newToken)
          localStorage.setItem('user', JSON.stringify(response.data.user))

          originalRequest.headers.Authorization = `Bearer ${newToken}`
          return api.request(originalRequest)
        } catch (refreshError) {
          localStorage.removeItem('authToken')
          localStorage.removeItem('user')
          goToSignIn()
          return Promise.reject(refreshError)
        }
      } else {
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
// so the expiry window resets on every visit.
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
    // Keep the existing token; a truly-expired one is handled by the 401 interceptor.
  } finally {
    isRefreshing = false
  }
}
slideSessionOnLoad()

export default api
