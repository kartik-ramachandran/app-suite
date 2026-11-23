import PhosphorIcon from './PhosphorIcon'
import { useEffect, useRef, useState } from 'react'

interface CopyTextDialogProps {
  open: boolean
  title: string
  description?: string
  value: string
  onClose: () => void
}

export default function CopyTextDialog({ open, title, description, value, onClose }: CopyTextDialogProps) {
  const inputRef = useRef<HTMLInputElement>(null)
  const [copied, setCopied] = useState(false)

  useEffect(() => {
    if (!open) return
    function onKeyDown(e: KeyboardEvent) {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKeyDown)
    setTimeout(() => inputRef.current?.select(), 0)
    return () => window.removeEventListener('keydown', onKeyDown)
  }, [open, onClose])

  useEffect(() => {
    if (!open) setCopied(false)
  }, [open])

  if (!open) return null

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      setTimeout(() => setCopied(false), 1800)
    } catch {
      inputRef.current?.select()
    }
  }

  return (
    <div className="modal-overlay pd-dialog-overlay" onClick={onClose}>
      <div
        className="modal-content pd-copy-dialog"
        role="dialog"
        aria-modal="true"
        aria-labelledby="pd-copy-title"
        onClick={e => e.stopPropagation()}
      >
        <div className="pd-copy-header">
          <div className="pd-copy-icon"><PhosphorIcon className="bi bi-link-45deg" /></div>
          <button className="modal-close" onClick={onClose} aria-label="Close">
            <PhosphorIcon className="bi bi-x-lg" />
          </button>
        </div>
        <div className="pd-copy-body">
          <h2 id="pd-copy-title">{title}</h2>
          {description && <p>{description}</p>}
          <div className="pd-copy-row">
            <input ref={inputRef} value={value} readOnly />
            <button className="btn btn-primary" onClick={copy}>
              <PhosphorIcon className={`bi ${copied ? 'bi-check-lg' : 'bi-clipboard'}`} />
              {copied ? 'Copied' : 'Copy'}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}
