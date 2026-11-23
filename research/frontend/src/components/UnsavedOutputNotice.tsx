import PhosphorIcon from './PhosphorIcon'
import { useEffect } from 'react'

const LEAVE_WARNING = 'Generated content on this page is not saved. Copy it somewhere else before leaving, or it will be lost.'

export function useUnsavedOutputWarning(shouldWarn: boolean) {
  useEffect(() => {
    if (!shouldWarn) return

    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = LEAVE_WARNING
      return LEAVE_WARNING
    }

    window.addEventListener('beforeunload', handleBeforeUnload)
    return () => window.removeEventListener('beforeunload', handleBeforeUnload)
  }, [shouldWarn])
}

export default function UnsavedOutputNotice() {
  return (
    <div style={{
      display: 'flex',
      alignItems: 'flex-start',
      gap: 10,
      padding: '10px 14px',
      border: '1px solid rgba(245,158,11,0.28)',
      borderRadius: 8,
      background: 'rgba(245,158,11,0.08)',
      color: 'var(--text-primary)',
      fontSize: 13,
      lineHeight: 1.5
    }}>
      <PhosphorIcon className="bi bi-exclamation-triangle" style={{ color: '#f59e0b', marginTop: 1, flexShrink: 0 }} />
      <span>
        This generated content is not saved. Copy it somewhere else before leaving this page, or it will be lost.
      </span>
    </div>
  )
}
