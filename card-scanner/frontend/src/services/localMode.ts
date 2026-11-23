import axios from 'axios'

/**
 * Local mode (no sign-in). When the backend runs with LocalMode:Enabled, it
 * answers /auth/mode with localMode=true and hands out a session for its single
 * owner account at /auth/local-session — so the app opens straight into it.
 * When local mode is off this does nothing and the normal sign-in page is used.
 *
 * Raw axios, not the api instance: these run before any session exists, and a
 * failure here must not trigger the api interceptor's redirect to /signin.
 */
const baseUrl = import.meta.env.VITE_API_BASE_URL || '/api'

let localMode: boolean | null = null

export function isLocalMode(): boolean {
  return localMode === true
}

/** Ask the backend once at startup. A backend that is down counts as "not local". */
export async function detectLocalMode(): Promise<boolean> {
  if (localMode !== null) return localMode
  try {
    const { data } = await axios.get(`${baseUrl}/auth/mode`, { timeout: 5000 })
    localMode = data?.localMode === true
  } catch {
    localMode = false
  }
  return localMode
}

/** Store a fresh owner session. Returns false if the backend refused. */
export async function startLocalSession(): Promise<boolean> {
  try {
    const { data } = await axios.post(`${baseUrl}/auth/local-session`, null, { timeout: 10000 })
    if (!data?.token) return false
    localStorage.setItem('authToken', data.token)
    localStorage.setItem('user', JSON.stringify(data.user))
    return true
  } catch {
    return false
  }
}

/** Startup: in local mode, make sure an owner session is stored before the first render. */
export async function bootstrapLocalMode(): Promise<void> {
  if (!(await detectLocalMode())) return
  if (!localStorage.getItem('authToken')) await startLocalSession()
}
