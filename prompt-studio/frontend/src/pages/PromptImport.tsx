import PhosphorIcon from '../components/PhosphorIcon'
import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import api from '../services/api'
import { usePageMeta } from '../utils/usePageMeta'
import '../App.css'

interface ExtractedPrompt {
  name: string
  prompt: string
  category: string
  difficulty: string
  description: string
}

function PromptImport() {
  usePageMeta('Import Prompt from Conversation – GenPrompt', 'Paste an AI conversation and extract a reusable prompt automatically.')
  const navigate = useNavigate()

  const [conversationText, setConversationText] = useState('')
  const [extracting, setExtracting] = useState(false)
  const [extracted, setExtracted] = useState<ExtractedPrompt | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [toast, setToast] = useState('')

  const showToast = (msg: string) => {
    setToast(msg)
    setTimeout(() => setToast(''), 2500)
  }

  const handleExtract = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!conversationText.trim()) return
    setExtracting(true)
    setError(null)
    setExtracted(null)
    try {
      const res = await api.post<ExtractedPrompt>('/prompts/import-from-conversation', {
        conversationText
      })
      setExtracted(res.data)
    } catch {
      setError('Failed to extract prompt. Make sure the conversation text is clear.')
    } finally {
      setExtracting(false)
    }
  }

  const handleSave = async () => {
    if (!extracted) return
    setSaving(true)
    try {
      await api.post('/prompts', {
        name: extracted.name,
        content: extracted.prompt,
        category: extracted.category,
        difficulty: extracted.difficulty || null,
        description: extracted.description,
      })
      showToast('Saved to dashboard!')
      setTimeout(() => navigate('/dashboard'), 800)
    } catch {
      showToast('Failed to save')
    } finally {
      setSaving(false)
    }
  }

  const handleCopy = () => {
    if (!extracted) return
    navigator.clipboard.writeText(extracted.prompt)
    showToast('Copied!')
  }

  return (
    <div className="app-container prompt-import-page">
      {toast && <div className="toast-notification toast-visible">{toast}</div>}

      <div className="prompt-import-hero">
        <div className="prompt-import-hero-copy">
          <span className="prompt-import-eyebrow">
            <PhosphorIcon className="bi bi-chat-square-text" /> Conversation Import
          </span>
          <h2>Import from Conversation</h2>
          <p>Paste a conversation with any AI and we'll extract a clean, reusable prompt from it.</p>
        </div>
        <div className="prompt-import-steps" aria-label="Import workflow">
          <span><strong>1</strong> Paste</span>
          <span><strong>2</strong> Extract</span>
          <span><strong>3</strong> Save</span>
        </div>
      </div>

      <div className="prompt-import-shell">
        <form onSubmit={handleExtract}>
          <div className="form-group prompt-import-card">
            <label className="prompt-import-label">
              Paste your conversation
            </label>
            <textarea
              className="prompt-import-textarea"
              value={conversationText}
              onChange={e => setConversationText(e.target.value)}
              placeholder={"Paste a conversation here — e.g.:\n\nUser: Can you help me write a product description for a SaaS tool?\nAI: Sure! Here's a description...\n\nWe'll extract the core prompt from the user side."}
            />
            <p className="prompt-import-hint">
              Up to 4,000 characters processed. Longer conversations will be trimmed from the end.
            </p>

            {error && (
              <div className="error-message prompt-import-error">{error}</div>
            )}

            <button type="submit" className="btn btn-primary prompt-import-submit" disabled={extracting || !conversationText.trim()}>
              {extracting ? <><PhosphorIcon className="bi bi-arrow-repeat spin" /> Extracting...</> : <><PhosphorIcon className="bi bi-magic" /> Extract Prompt</>}
            </button>
          </div>
        </form>

        {extracted && (
          <div className="prompt-import-result">
            <h3>
              <PhosphorIcon className="bi bi-check-circle-fill" /> Extracted Prompt
            </h3>

            <div style={{ display: 'flex', gap: '0.5rem', flexWrap: 'wrap', marginBottom: '1rem' }}>
              {extracted.category && (
                <span className="prompt-category">{extracted.category}</span>
              )}
              {extracted.difficulty && (
                <span className={`difficulty-badge difficulty-${extracted.difficulty}`}>
                  {extracted.difficulty.charAt(0).toUpperCase() + extracted.difficulty.slice(1)}
                </span>
              )}
            </div>

            <div className="form-group" style={{ marginBottom: '1rem' }}>
              <label style={{ fontWeight: 600, fontSize: '0.85rem', color: '#475569' }}>NAME</label>
              <input
                className="form-control"
                value={extracted.name}
                onChange={e => setExtracted({ ...extracted, name: e.target.value })}
                style={{ marginTop: '0.25rem' }}
              />
            </div>

            <div className="form-group" style={{ marginBottom: '1rem' }}>
              <label style={{ fontWeight: 600, fontSize: '0.85rem', color: '#475569' }}>PROMPT</label>
              <textarea
                className="prompt-import-result-textarea"
                value={extracted.prompt}
                onChange={e => setExtracted({ ...extracted, prompt: e.target.value })}
              />
            </div>

            {extracted.description && (
              <p style={{ margin: '0 0 1.25rem', color: '#475569', fontSize: '0.875rem' }}>
                <strong>Description:</strong> {extracted.description}
              </p>
            )}

            <div style={{ display: 'flex', gap: '0.75rem', flexWrap: 'wrap' }}>
              <button className="btn btn-primary" onClick={handleSave} disabled={saving}>
                {saving ? 'Saving...' : <><PhosphorIcon className="bi bi-floppy" /> Save to Dashboard</>}
              </button>
              <button className="btn btn-secondary" onClick={handleCopy}>
                <PhosphorIcon className="bi bi-clipboard" /> Copy Prompt
              </button>
              <button className="btn btn-secondary" onClick={() => setExtracted(null)}>
                <PhosphorIcon className="bi bi-arrow-left" /> Try Again
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

export default PromptImport
