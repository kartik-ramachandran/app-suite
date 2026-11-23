import PhosphorIcon from './PhosphorIcon'
import { useEffect } from 'react'
import { createPortal } from 'react-dom'

interface ConfirmDialogProps {
  open: boolean
  title?: string
  message: string
  confirmLabel?: string
  cancelLabel?: string
  variant?: 'danger' | 'primary'
  icon?: string
  busy?: boolean
  onConfirm: () => void
  onCancel: () => void
}

export default function ConfirmDialog({
  open,
  title = 'Confirm',
  message,
  confirmLabel = 'Confirm',
  cancelLabel = 'Cancel',
  variant = 'danger',
  icon = 'bi-exclamation-triangle',
  busy = false,
  onConfirm,
  onCancel,
}: ConfirmDialogProps) {
  useEffect(() => {
    if (!open) return
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape' && !busy) onCancel()
    }
    window.addEventListener('keydown', onKeyDown)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [open, busy, onCancel])

  if (!open) return null

  // Portal to <body>: ancestors using backdrop-filter/transform create a new
  // containing block, which would pin the fixed overlay to that ancestor
  // instead of the viewport.
  return createPortal(
    <div className="modal-overlay pd-dialog-overlay" onClick={() => !busy && onCancel()}>
      <div
        className="modal-content pd-confirm-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="pd-confirm-title"
        onClick={e => e.stopPropagation()}
      >
        <div className={`pd-confirm-icon pd-confirm-icon--${variant}`}>
          <PhosphorIcon className={`bi ${busy ? 'bi-arrow-repeat pd-dialog-spin' : icon}`} />
        </div>
        <div className="pd-confirm-copy">
          <h2 id="pd-confirm-title">{title}</h2>
          <p>{message}</p>
        </div>
        <div className="pd-confirm-actions">
          <button className="btn btn-secondary" onClick={onCancel} disabled={busy}>
            {cancelLabel}
          </button>
          <button className={`btn ${variant === 'danger' ? 'btn-danger' : 'btn-primary'}`} onClick={onConfirm} disabled={busy}>
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>,
    document.body
  )
}
