import PhosphorIcon from './PhosphorIcon'
import { useState, useMemo } from 'react'
import '../features.css'

interface Props {
  isOpen: boolean
  content: string
  onClose: () => void
  onApply: (filled: string) => void
}

function parseVariables(text: string): string[] {
  const matches = text.match(/\{\{([^}]+)\}\}/g) ?? []
  const names = matches.map(m => m.slice(2, -2).trim())
  return [...new Set(names)]
}

function fillTemplate(text: string, values: Record<string, string>): string {
  return text.replace(/\{\{([^}]+)\}\}/g, (_, name) => values[name.trim()] || `{{${name}}}`)
}

function highlightUnfilled(text: string, values: Record<string, string>): string {
  return text
    .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
    .replace(/\{\{([^}]+)\}\}/g, (_, name) => {
      const val = values[name.trim()]
      if (val) return `<mark>${val}</mark>`
      return `<span style="color:#ef4444;font-weight:600">{{${name}}}</span>`
    })
}

export default function VariableFillModal({ isOpen, content, onClose, onApply }: Props) {
  const variables = useMemo(() => parseVariables(content), [content])
  const [values, setValues] = useState<Record<string, string>>({})
  const [copied, setCopied] = useState(false)

  if (!isOpen) return null

  const filledContent = fillTemplate(content, values)
  const previewHtml = highlightUnfilled(content, values)
  const allFilled = variables.every(v => !!values[v])

  const handleCopy = async () => {
    await navigator.clipboard.writeText(filledContent)
    setCopied(true)
    setTimeout(() => setCopied(false), 1800)
  }

  return (
    <div className="varfill-overlay" onClick={e => e.target === e.currentTarget && onClose()}>
      <div className="varfill-modal">
        <div className="varfill-header">
          <h3><PhosphorIcon className="bi bi-input-cursor-text" style={{ marginRight: '0.5rem', color: '#d62976' }} />Fill in Variables</h3>
          <button type="button" className="feat-btn-close" onClick={onClose}><PhosphorIcon className="bi bi-x-lg" /></button>
        </div>

        <div className="varfill-body">
          <div className="varfill-fields">
            {variables.map(v => (
              <div className="varfill-field" key={v}>
                <label>{v.replace(/_/g, ' ')}</label>
                <input
                  type="text"
                  placeholder={`Enter ${v}…`}
                  value={values[v] ?? ''}
                  onChange={e => setValues(prev => ({ ...prev, [v]: e.target.value }))}
                />
              </div>
            ))}
          </div>

          <div>
            <div className="varfill-preview-label">Preview</div>
            <div
              className="varfill-preview"
              dangerouslySetInnerHTML={{ __html: previewHtml }}
            />
          </div>
        </div>

        <div className="varfill-footer">
          <button type="button" className="feat-btn-secondary" onClick={onClose}>Cancel</button>
          <button type="button" className={`feat-btn-copy${copied ? ' copied' : ''}`} onClick={handleCopy}>
            <PhosphorIcon className={`bi ${copied ? 'bi-check-lg' : 'bi-clipboard'}`} />
            {copied ? 'Copied!' : 'Copy Filled'}
          </button>
          <button
            type="button"
            className="feat-btn-primary"
            disabled={!allFilled}
            onClick={() => { onApply(filledContent); onClose() }}
          >
            Use Filled Prompt
          </button>
        </div>
      </div>
    </div>
  )
}
