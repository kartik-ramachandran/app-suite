import PhosphorIcon from './PhosphorIcon'
import { useState, useMemo } from 'react'
import '../features.css'

interface Props {
  isOpen: boolean
  content: string
  onClose: () => void
}

function encodeShare(text: string): string {
  try { return btoa(encodeURIComponent(text)) } catch { return btoa(text.slice(0, 2000)) }
}

export default function SharePromptModal({ isOpen, content, onClose }: Props) {
  const [copied, setCopied] = useState(false)

  const shareUrl = useMemo(() => {
    if (typeof window === 'undefined') return ''
    const encoded = encodeShare(content)
    return `${window.location.origin}/share?p=${encoded}`
  }, [content])

  const handleCopyLink = async () => {
    await navigator.clipboard.writeText(shareUrl)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  const handleShareX = () => {
    const text = encodeURIComponent('Check out this AI prompt I created with GenPrompt ✦')
    const url = encodeURIComponent(shareUrl)
    window.open(`https://x.com/intent/tweet?text=${text}&url=${url}`, '_blank')
  }

  const handleShareLinkedIn = () => {
    const url = encodeURIComponent(shareUrl)
    window.open(`https://www.linkedin.com/sharing/share-offsite/?url=${url}`, '_blank')
  }

  if (!isOpen) return null

  return (
    <div className="share-overlay" onClick={e => e.target === e.currentTarget && onClose()}>
      <div className="share-modal">
        <div className="share-modal-header">
          <h3><PhosphorIcon className="bi bi-share" style={{ marginRight: '0.5rem', color: '#d62976' }} />Share Prompt</h3>
          <button type="button" className="feat-btn-close" onClick={onClose}><PhosphorIcon className="bi bi-x-lg" /></button>
        </div>

        <div className="share-modal-body">
          {/* Preview card */}
          <div className="share-preview-card">
            <div className="share-preview-card-top">
              <PhosphorIcon className="bi bi-stars" style={{ color: '#fff' }} />
              <span>GenPrompt · AI Prompt</span>
            </div>
            <div className="share-preview-card-body">{content}</div>
          </div>

          {/* URL row */}
          <div>
            <div className="varfill-preview-label" style={{ marginBottom: '0.4rem' }}>Share link</div>
            <div className="share-url-row">
              <input
                className="share-url-input"
                readOnly
                value={shareUrl}
                onFocus={e => e.target.select()}
              />
              <button
                type="button"
                className={`feat-btn-copy${copied ? ' copied' : ''}`}
                onClick={handleCopyLink}
                style={{ flexShrink: 0 }}
              >
                <PhosphorIcon className={`bi ${copied ? 'bi-check-lg' : 'bi-clipboard'}`} />
                {copied ? 'Copied!' : 'Copy'}
              </button>
            </div>
          </div>

          {/* Social sharing */}
          <div>
            <div className="varfill-preview-label" style={{ marginBottom: '0.4rem' }}>Share to</div>
            <div className="share-platforms">
              <button type="button" className="btn-share-platform btn-share-x" onClick={handleShareX}>
                <PhosphorIcon className="bi bi-twitter-x" /> Post on X
              </button>
              <button type="button" className="btn-share-platform btn-share-linkedin" onClick={handleShareLinkedIn}>
                <PhosphorIcon className="bi bi-linkedin" /> Share on LinkedIn
              </button>
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
