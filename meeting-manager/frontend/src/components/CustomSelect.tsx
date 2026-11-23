import PhosphorIcon from './PhosphorIcon'
import { useState, useRef, useEffect, useCallback } from 'react'

export interface SelectOption {
  value: string
  label: string
  description?: string
  icon?: string
}

interface CustomSelectProps {
  value: string
  onChange: (value: string) => void
  options: SelectOption[]
  id?: string
  disabled?: boolean
  className?: string
  style?: React.CSSProperties
  placeholder?: string
}

export default function CustomSelect({
  value,
  onChange,
  options,
  id,
  disabled,
  className,
  style,
  placeholder,
}: CustomSelectProps) {
  const [open, setOpen] = useState(false)
  const [focusedIndex, setFocusedIndex] = useState(-1)
  const containerRef = useRef<HTMLDivElement>(null)
  const listRef = useRef<HTMLUListElement>(null)

  const selected = options.find(o => o.value === value)

  const close = useCallback(() => {
    setOpen(false)
    setFocusedIndex(-1)
  }, [])

  useEffect(() => {
    const handler = (e: MouseEvent) => {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        close()
      }
    }
    document.addEventListener('mousedown', handler)
    return () => document.removeEventListener('mousedown', handler)
  }, [close])

  useEffect(() => {
    if (open && focusedIndex >= 0) {
      const item = listRef.current?.querySelectorAll('li')[focusedIndex] as HTMLElement
      item?.scrollIntoView({ block: 'nearest' })
    }
  }, [open, focusedIndex])

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (disabled) return
    if (!open) {
      if (e.key === 'Enter' || e.key === ' ' || e.key === 'ArrowDown') {
        e.preventDefault()
        setOpen(true)
        setFocusedIndex(options.findIndex(o => o.value === value))
      }
      return
    }
    if (e.key === 'Escape') { close(); return }
    if (e.key === 'ArrowDown') { e.preventDefault(); setFocusedIndex(i => Math.min(i + 1, options.length - 1)) }
    if (e.key === 'ArrowUp') { e.preventDefault(); setFocusedIndex(i => Math.max(i - 1, 0)) }
    if (e.key === 'Enter' && focusedIndex >= 0) {
      e.preventDefault()
      onChange(options[focusedIndex].value)
      close()
    }
  }

  return (
    <div
      ref={containerRef}
      className={`cs-root${open ? ' cs-open' : ''}${disabled ? ' cs-disabled' : ''} ${className || ''}`}
      style={style}
    >
      <button
        type="button"
        id={id}
        disabled={disabled}
        className="cs-trigger"
        aria-haspopup="listbox"
        aria-expanded={open}
        onClick={() => !disabled && setOpen(o => !o)}
        onKeyDown={handleKeyDown}
      >
        <span className="cs-trigger-label">
          {selected?.icon && <PhosphorIcon className={`bi ${selected.icon}`} style={{ marginRight: '0.4rem' }} />}
          {selected?.label ?? placeholder ?? ''}
        </span>
        <PhosphorIcon className="bi bi-chevron-down cs-arrow" />
      </button>

      {open && (
        <ul ref={listRef} className="cs-dropdown" role="listbox">
          {options.map((opt, i) => (
            <li
              key={opt.value}
              role="option"
              aria-selected={opt.value === value}
              className={`cs-option${opt.value === value ? ' cs-selected' : ''}${i === focusedIndex ? ' cs-focused' : ''}`}
              onMouseEnter={() => setFocusedIndex(i)}
              onMouseDown={(e) => {
                e.preventDefault()
                onChange(opt.value)
                close()
              }}
            >
              <span className="cs-option-content">
                {opt.icon && <PhosphorIcon className={`bi ${opt.icon}`} style={{ marginRight: '0.4rem', opacity: 0.7 }} />}
                <span className="cs-option-text">
                  <span className="cs-option-label">{opt.label}</span>
                  {opt.description && <span className="cs-option-desc">{opt.description}</span>}
                </span>
              </span>
              {opt.value === value && <PhosphorIcon className="bi bi-check2 cs-check" />}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
