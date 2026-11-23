import PhosphorIcon from './PhosphorIcon'
import { useState, useEffect, useCallback } from 'react'
import api from '../services/api'
import '../features.css'

interface GenerateRequest {
  intent: string
  selectedGoals: string[]
  additionalContext?: string
  style?: string
  personaId?: number
  language?: string
}

interface VariantConfig {
  label: string
  badge: string
  badgeCls: string
  contextSuffix: string
}

const VARIANTS: VariantConfig[] = [
  { label: 'Variant 1',  badge: 'Standard', badgeCls: 'badge-standard', contextSuffix: '' },
  { label: 'Variant 2',  badge: 'Detailed',  badgeCls: 'badge-detailed',  contextSuffix: '\n\nEmphasis: Make this prompt detailed and comprehensive with concrete examples.' },
  { label: 'Variant 3',  badge: 'Concise',   badgeCls: 'badge-concise',   contextSuffix: '\n\nEmphasis: Make this prompt concise, direct, and laser-focused.' },
]

interface Props {
  isOpen: boolean
  onClose: () => void
  onUseVariant: (text: string) => void
  request: GenerateRequest
}

interface VariantState {
  status: 'loading' | 'done' | 'error'
  text: string
}

export default function VariantsModal({ isOpen, onClose, onUseVariant, request }: Props) {
  const [variants, setVariants] = useState<VariantState[]>([
    { status: 'loading', text: '' },
    { status: 'loading', text: '' },
    { status: 'loading', text: '' },
  ])
  const [copied, setCopied] = useState<number | null>(null)

  const generate = useCallback(async () => {
    setVariants(VARIANTS.map(() => ({ status: 'loading', text: '' })))

    VARIANTS.forEach(async (v, i) => {
      try {
        const body = {
          ...request,
          additionalContext: (request.additionalContext ?? '') + v.contextSuffix,
        }
        const res = await api.post<{ prompt: string }>('/prompts/generate-prompt', body)
        setVariants(prev => prev.map((s, idx) => idx === i ? { status: 'done', text: res.data.prompt } : s))
      } catch {
        setVariants(prev => prev.map((s, idx) => idx === i ? { status: 'error', text: 'Failed to generate variant.' } : s))
      }
    })
  }, [request])

  useEffect(() => {
    if (isOpen) generate()
  }, [isOpen, generate])

  const handleCopy = async (text: string, idx: number) => {
    await navigator.clipboard.writeText(text)
    setCopied(idx)
    setTimeout(() => setCopied(null), 1800)
  }

  if (!isOpen) return null

  return (
    <div className="variants-overlay" onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className="variants-modal">
        <div className="variants-header">
          <h3><PhosphorIcon className="bi bi-layout-three-columns" style={{ marginRight: '0.5rem', color: '#d62976' }} />AI Prompt Variants</h3>
          <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
            <button type="button" className="feat-btn-secondary" style={{ fontSize: '0.75rem', padding: '0.3rem 0.65rem' }} onClick={generate}>
              <PhosphorIcon className="bi bi-arrow-clockwise" style={{ marginRight: '0.3rem' }} />Regenerate
            </button>
            <button type="button" className="feat-btn-close" onClick={onClose}><PhosphorIcon className="bi bi-x-lg" /></button>
          </div>
        </div>

        <div className="variants-body">
          {VARIANTS.map((v, i) => (
            <div className="variant-card" key={i}>
              <div className="variant-card-header">
                <span className="variant-card-label">{v.label}</span>
                <span className={`variant-card-badge ${v.badgeCls}`}>{v.badge}</span>
              </div>

              {variants[i].status === 'loading' ? (
                <div className="variant-card-loading">
                  <div className="variant-spinner" />
                  Generating…
                </div>
              ) : (
                <>
                  <div className="variant-card-body">{variants[i].text}</div>
                  <div className="variant-card-footer">
                    <button
                      type="button"
                      className="btn-use-variant"
                      onClick={() => { onUseVariant(variants[i].text); onClose() }}
                    >
                      <PhosphorIcon className="bi bi-check2" style={{ marginRight: '0.25rem' }} />Use This
                    </button>
                    <button
                      type="button"
                      className="btn-copy-variant"
                      onClick={() => handleCopy(variants[i].text, i)}
                    >
                      <PhosphorIcon className={`bi ${copied === i ? 'bi-check-lg' : 'bi-clipboard'}`} />
                    </button>
                  </div>
                </>
              )}
            </div>
          ))}
        </div>
      </div>
    </div>
  )
}
