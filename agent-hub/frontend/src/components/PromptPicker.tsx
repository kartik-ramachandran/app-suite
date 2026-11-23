import PhosphorIcon from './PhosphorIcon'
import { useState, useEffect, useRef } from 'react'
import { createPortal } from 'react-dom'
import api from '../services/api'

export interface PromptPickerPrompt { id: string; name: string; content: string; category: string }

interface Props {
  onSelect: (prompt: PromptPickerPrompt) => void
  label?: string
}

export default function PromptPicker({ onSelect, label = 'My Prompts' }: Props) {
  const [open, setOpen] = useState(false)
  const [prompts, setPrompts] = useState<PromptPickerPrompt[]>([])
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(false)
  const [dropdownStyle, setDropdownStyle] = useState<React.CSSProperties>({})
  const btnRef = useRef<HTMLButtonElement>(null)
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    setLoading(true)
    api.get<PromptPickerPrompt[]>('/prompts')
      .then(r => setPrompts(r.data))
      .catch(() => {})
      .finally(() => setLoading(false))

    // Position dropdown relative to button using fixed coords so it never causes scroll
    if (btnRef.current) {
      const rect = btnRef.current.getBoundingClientRect()
      const spaceBelow = window.innerHeight - rect.bottom
      const dropH = 360
      if (spaceBelow >= dropH || spaceBelow >= 200) {
        setDropdownStyle({ top: rect.bottom + 4, left: rect.right - 320 })
      } else {
        setDropdownStyle({ bottom: window.innerHeight - rect.top + 4, left: rect.right - 320 })
      }
    }
  }, [open])

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node) &&
          btnRef.current && !btnRef.current.contains(e.target as Node)) {
        setOpen(false)
      }
    }
    document.addEventListener('mousedown', handler)
    return () => document.removeEventListener('mousedown', handler)
  }, [])

  const filtered = prompts.filter(p =>
    p.name.toLowerCase().includes(search.toLowerCase()) ||
    p.content.toLowerCase().includes(search.toLowerCase())
  )

  const handleSelect = (e: React.MouseEvent, p: PromptPickerPrompt) => {
    e.preventDefault()
    e.stopPropagation()
    onSelect(p)
    setOpen(false)
    setSearch('')
  }

  return (
    <div style={{ display: 'inline-block' }}>
      <button
        ref={btnRef}
        type="button"
        onClick={e => { e.preventDefault(); setOpen(o => !o) }}
        style={{
          display: 'flex', alignItems: 'center', gap: 5,
          background: 'var(--input-bg)', border: '1px solid var(--border-color)',
          borderRadius: 6, padding: '5px 10px', cursor: 'pointer',
          fontSize: 12, color: 'var(--text-secondary)', whiteSpace: 'nowrap'
        }}
      >
        <PhosphorIcon className="bi bi-collection" style={{ fontSize: 13 }} />
        {label}
        <PhosphorIcon className={`bi bi-chevron-${open ? 'up' : 'down'}`} style={{ fontSize: 10 }} />
      </button>

      {/* Portal to <body>: ancestors with backdrop-filter (the glass cards)
          would otherwise become the containing block for position:fixed and
          teleport the dropdown off-screen. */}
      {open && createPortal(
        <div
          ref={ref}
          style={{
            position: 'fixed',
            zIndex: 9999,
            background: 'var(--card-bg)',
            border: '1px solid var(--border-color)',
            borderRadius: 8,
            boxShadow: '0 4px 20px rgba(0,0,0,0.18)',
            width: 320,
            maxHeight: 360,
            display: 'flex',
            flexDirection: 'column',
            ...dropdownStyle
          }}
        >
          <div style={{ padding: '8px 10px', borderBottom: '1px solid var(--border-color)' }}>
            <input
              autoFocus
              placeholder="Search prompts…"
              value={search}
              onChange={e => setSearch(e.target.value)}
              onKeyDown={e => e.stopPropagation()}
              style={{
                width: '100%', padding: '5px 8px', fontSize: 12,
                background: 'var(--input-bg)', border: '1px solid var(--border-color)',
                borderRadius: 5, color: 'var(--text-primary)', outline: 'none', boxSizing: 'border-box'
              }}
            />
          </div>
          <div style={{ overflowY: 'auto', flex: 1 }}>
            {loading && <div style={{ padding: '12px', fontSize: 12, color: 'var(--text-secondary)', textAlign: 'center' }}>Loading…</div>}
            {!loading && filtered.length === 0 && (
              <div style={{ padding: '12px', fontSize: 12, color: 'var(--text-secondary)', textAlign: 'center' }}>No prompts found</div>
            )}
            {filtered.map(p => (
              <button
                key={p.id}
                type="button"
                onMouseDown={e => handleSelect(e, p)}
                style={{
                  width: '100%', textAlign: 'left', background: 'none', border: 'none',
                  padding: '9px 12px', cursor: 'pointer', borderBottom: '1px solid var(--border-color)',
                  display: 'flex', flexDirection: 'column', gap: 2
                }}
                onMouseEnter={e => (e.currentTarget.style.background = 'var(--input-bg)')}
                onMouseLeave={e => (e.currentTarget.style.background = 'none')}
              >
                <span style={{ fontSize: 13, fontWeight: 600, color: 'var(--text-primary)' }}>{p.name}</span>
                <span style={{ fontSize: 11, color: 'var(--text-secondary)', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', maxWidth: 280 }}>
                  {p.content.slice(0, 100)}
                </span>
              </button>
            ))}
          </div>
        </div>,
        document.body
      )}
    </div>
  )
}
