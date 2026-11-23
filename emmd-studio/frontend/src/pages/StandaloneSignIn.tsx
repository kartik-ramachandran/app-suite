import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import axios from 'axios'
import PhosphorIcon from '../components/PhosphorIcon'

/**
 * Email/password sign-in and registration against the standalone API.
 * Raw axios (not the api instance): auth endpoints skip CSRF, and a 401 here
 * must not trigger the interceptor's refresh-and-redirect loop.
 */
export default function StandaloneSignIn() {
  const navigate = useNavigate()
  const [mode, setMode] = useState<'signin' | 'register'>('signin')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [name, setName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const baseUrl = import.meta.env.VITE_API_BASE_URL || '/api'

  const submit = async () => {
    setBusy(true); setError('')
    try {
      const url = mode === 'signin' ? `${baseUrl}/auth/login` : `${baseUrl}/auth/register`
      const body = mode === 'signin' ? { email, password } : { email, password, name }
      const { data } = await axios.post(url, body)
      localStorage.setItem('authToken', data.token)
      localStorage.setItem('user', JSON.stringify(data.user))
      navigate('/emmd')
    } catch (e: unknown) {
      const err = e as { response?: { data?: { error?: string; message?: string } } }
      setError(err.response?.data?.error || err.response?.data?.message || 'Something went wrong — try again.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="si-page">
      <div className="si-card">
        <div className="si-brand">
          <PhosphorIcon className="bi bi-file-earmark-richtext" />
          <h1>EMMD Studio</h1>
        </div>
        <p className="si-tag">Write, seal, and verify Enhanced Markdown documents.</p>

        <div className="si-tabs">
          <button className={mode === 'signin' ? 'si-tab-active' : ''} onClick={() => { setMode('signin'); setError('') }}>Sign in</button>
          <button className={mode === 'register' ? 'si-tab-active' : ''} onClick={() => { setMode('register'); setError('') }}>Create account</button>
        </div>

        {mode === 'register' && (
          <label className="si-field">
            <span>Name</span>
            <input value={name} onChange={e => setName(e.target.value)} placeholder="Your name" autoComplete="name" />
          </label>
        )}
        <label className="si-field">
          <span>Email</span>
          <input type="email" value={email} onChange={e => setEmail(e.target.value)} placeholder="you@example.com" autoComplete="email" />
        </label>
        <label className="si-field">
          <span>Password</span>
          <input
            type="password" value={password} onChange={e => setPassword(e.target.value)}
            placeholder={mode === 'register' ? 'At least 8 characters' : 'Your password'}
            autoComplete={mode === 'register' ? 'new-password' : 'current-password'}
            onKeyDown={e => { if (e.key === 'Enter') void submit() }}
          />
        </label>

        {error && <p className="si-error"><PhosphorIcon className="bi bi-exclamation-circle" /> {error}</p>}

        <button className="si-go" onClick={() => void submit()} disabled={busy}>
          {busy ? 'Working…' : mode === 'signin' ? 'Sign in' : 'Create account'}
        </button>
      </div>
    </div>
  )
}
