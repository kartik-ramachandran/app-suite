import PhosphorIcon from './PhosphorIcon'
import { useState, useEffect } from 'react'
import '../features.css'

interface Props {
  content: string
  size?: 'sm' | 'md' | 'lg'
  exclude?: ('chatgpt' | 'claude' | 'gemini' | 'perplexity')[]
}

const AI_PLATFORMS = [
  { key: 'chatgpt',    label: 'ChatGPT',     initial: 'AI', url: 'https://chatgpt.com/',              cls: 'ai-btn-chatgpt' },
  { key: 'claude',     label: 'Claude',      initial: 'C',  url: 'https://claude.ai/new',             cls: 'ai-btn-claude'  },
  { key: 'gemini',     label: 'Gemini',      initial: 'G',  url: 'https://gemini.google.com/',        cls: 'ai-btn-gemini'  },
  { key: 'perplexity', label: 'Perplexity',  initial: 'P',  url: 'https://www.perplexity.ai/',        cls: 'ai-btn-perplexity' },
] as const

export default function OpenInAIButtons({ content, size = 'sm', exclude = [] }: Props) {
  const [toast, setToast] = useState<string | null>(null)

  useEffect(() => {
    if (!toast) return
    const t = setTimeout(() => setToast(null), 3500)
    return () => clearTimeout(t)
  }, [toast])

  const handleOpen = async (key: string, label: string, url: string) => {
    try { await navigator.clipboard.writeText(content) } catch {}

    // Perplexity supports ?q= to prefill the search box
    const finalUrl = key === 'perplexity'
      ? `${url}?q=${encodeURIComponent(content.slice(0, 500))}`
      : url

    window.open(finalUrl, '_blank')
    setToast(label)
  }

  const platforms = AI_PLATFORMS.filter(p => !exclude.includes(p.key as any))

  return (
    <>
      <div className="ai-buttons-row">
        {platforms.map(p => (
          <button
            key={p.key}
            type="button"
            className={`ai-btn ${p.cls} ${size}`}
            onClick={() => handleOpen(p.key, p.label, p.url)}
            title={`Copy & open in ${p.label}`}
            data-tooltip={`Open in ${p.label}`}
          >
            <span className="ai-btn-initial">{p.initial}</span>
            <span className="ai-btn-label">{p.label}</span>
          </button>
        ))}
      </div>

      {toast && (
        <div className="ai-open-toast">
          <PhosphorIcon className="bi bi-clipboard-check" />
          <span>
            Prompt copied — press <kbd>Ctrl+V</kbd> to paste in {toast}
          </span>
        </div>
      )}
    </>
  )
}
