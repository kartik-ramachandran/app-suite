import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useState } from 'react'
import { useNavigate, useLocation } from 'react-router-dom'
import api from '../services/api'

export default function CalendarOAuthCallback() {
  const location = useLocation()
  const navigate = useNavigate()
  const [status, setStatus] = useState<'loading' | 'success' | 'error'>('loading')
  const [message, setMessage] = useState('')

  useEffect(() => {
    const params = new URLSearchParams(location.search)
    const code = params.get('code')
    const error = params.get('error')

    if (error) {
      setStatus('error')
      setMessage(error === 'access_denied' ? 'Calendar access was denied.' : `OAuth error: ${error}`)
      return
    }

    if (!code) {
      setStatus('error')
      setMessage('No authorization code received.')
      return
    }

    const provider = location.pathname.includes('microsoft') ? 'microsoft' : 'google'
    exchangeCode(provider, code)
  }, [])

  const exchangeCode = async (provider: 'google' | 'microsoft', code: string) => {
    try {
      await api.post(`/calendar/callback/${provider}`, { code })
      setStatus('success')
      setTimeout(() => navigate('/calendar', { replace: true }), 1500)
    } catch (e: any) {
      setStatus('error')
      setMessage(e.response?.data?.message ?? 'Failed to connect calendar.')
    }
  }

  return (
    <div style={{ minHeight: '100vh', display: 'flex', alignItems: 'center', justifyContent: 'center', background: 'var(--bg-primary)' }}>
      <div style={{ textAlign: 'center', padding: '40px 32px', maxWidth: 360 }}>
        {status === 'loading' && (
          <>
            <div className="spinner" style={{ width: 36, height: 36, borderWidth: 3, margin: '0 auto 16px' }} />
            <div style={{ fontSize: 16, fontWeight: 600, marginBottom: 6 }}>Connecting your calendar…</div>
            <div style={{ fontSize: 13, color: 'var(--text-muted)' }}>Exchanging tokens and syncing events.</div>
          </>
        )}
        {status === 'success' && (
          <>
            <div style={{ width: 56, height: 56, borderRadius: '50%', background: '#22c55e15', display: 'flex', alignItems: 'center', justifyContent: 'center', margin: '0 auto 16px' }}>
              <PhosphorIcon className="bi bi-check-circle-fill" style={{ fontSize: 28, color: '#22c55e' }} />
            </div>
            <div style={{ fontSize: 16, fontWeight: 600, marginBottom: 6 }}>Calendar connected!</div>
            <div style={{ fontSize: 13, color: 'var(--text-muted)' }}>Redirecting to your calendar…</div>
          </>
        )}
        {status === 'error' && (
          <>
            <div style={{ width: 56, height: 56, borderRadius: '50%', background: '#ef444415', display: 'flex', alignItems: 'center', justifyContent: 'center', margin: '0 auto 16px' }}>
              <PhosphorIcon className="bi bi-x-circle-fill" style={{ fontSize: 28, color: '#ef4444' }} />
            </div>
            <div style={{ fontSize: 16, fontWeight: 600, marginBottom: 8 }}>Connection failed</div>
            <div style={{ fontSize: 13, color: 'var(--text-muted)', marginBottom: 20 }}>{message}</div>
            <button className="btn btn-primary" style={{ fontSize: 13 }} onClick={() => navigate('/calendar', { replace: true })}>
              Back to Calendar
            </button>
          </>
        )}
      </div>
    </div>
  )
}
