import { useCallback, useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import PhosphorIcon from './PhosphorIcon'
import { setHandoff, takeHandoff } from '../utils/fileHandoff'
import './FreePdfConverter.css'

/**
 * A PDF tool that actually works without an account.
 *
 * The free tool pages rank for high-volume queries ("merge pdf", "compress
 * pdf") but used to link straight to the signed-in app, so a visitor arriving
 * from search hit a signup wall before converting anything. That is backwards
 * from how iLovePDF and Smallpdf acquire users: the tool works first, and the
 * account is asked for AFTER the file is delivered — to unlock batching,
 * history and the API.
 *
 * The signup prompt in this component therefore renders only once `result` is
 * set, i.e. once the user already has their file. Moving it earlier would
 * recreate the exact wall this replaces.
 */

/** Tools the backend allows anonymously. Mirrors AnonymousPdfController.AllowedTools. */
const ANON_TOOLS = new Set([
  'merge', 'split', 'compress', 'rotate', 'page-numbers', 'watermark', 'repair',
  'remove-pages', 'organize',
  'pdf-to-jpg', 'pdf-to-word', 'pdf-to-excel', 'pdf-to-powerpoint', 'pdf-to-html',
  'pdf-to-markdown', 'pdf-to-text', 'word-to-pdf', 'excel-to-pdf',
  'powerpoint-to-pdf', 'html-to-pdf', 'markdown-to-pdf', 'text-to-pdf',
  'images-to-pdf',
])

const MULTI_FILE_TOOLS = new Set(['merge', 'images-to-pdf'])

/**
 * Tools that need a value from the user before they can run. Without this the
 * page-selection tools would silently no-op, which reads as a broken tool.
 */
const OPTION_FIELD: Record<string, { label: string; placeholder: string; required: boolean }> = {
  'remove-pages': { label: 'Pages to remove', placeholder: 'e.g. 1,3,5-8', required: true },
  organize: { label: 'Page order', placeholder: 'e.g. 3,1,2', required: true },
  watermark: { label: 'Watermark text', placeholder: 'CONFIDENTIAL', required: false },
  rotate: { label: 'Rotation', placeholder: '90, 180 or 270', required: false },
}

/** Accept hints per tool. Advisory only — the server sniffs actual bytes. */
const ACCEPT: Record<string, string> = {
  'word-to-pdf': '.doc,.docx',
  'excel-to-pdf': '.xls,.xlsx',
  'powerpoint-to-pdf': '.ppt,.pptx',
  'html-to-pdf': '.html,.htm',
  'markdown-to-pdf': '.md,.markdown',
  'text-to-pdf': '.txt',
  'images-to-pdf': 'image/*',
}

interface Props {
  toolKey: string
  toolName: string
}

interface Quota {
  remaining: number
  dailyLimit: number
  maxBytes: number
  maxPages: number
}

export default function FreePdfConverter({ toolKey, toolName }: Props) {
  const navigate = useNavigate()
  const [files, setFiles] = useState<File[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [needsAccount, setNeedsAccount] = useState(false)
  const [result, setResult] = useState<{ url: string; name: string; blob: Blob } | null>(null)
  const [quota, setQuota] = useState<Quota | null>(null)
  const [dragging, setDragging] = useState(false)
  const [options, setOptions] = useState('')
  const [keeping, setKeeping] = useState(false)
  const [kept, setKept] = useState(false)
  const [keepError, setKeepError] = useState('')
  const inputRef = useRef<HTMLInputElement>(null)

  const signedIn = !!localStorage.getItem('authToken')

  const supported = ANON_TOOLS.has(toolKey)
  const multi = MULTI_FILE_TOOLS.has(toolKey)
  const optionField = OPTION_FIELD[toolKey]
  const optionMissing = !!optionField?.required && !options.trim()

  useEffect(() => {
    if (!supported) return
    fetch('/api/free-pdf/quota')
      .then(r => (r.ok ? r.json() : null))
      .then(d => d && setQuota(d))
      .catch(() => { /* quota display is a nicety, never block the tool on it */ })
  }, [supported])

  // Revoke the object URL when it is replaced or the component unmounts.
  // Without this every conversion leaks the whole file into memory.
  useEffect(() => () => { if (result) URL.revokeObjectURL(result.url) }, [result])

  // Completes a "keep this file" that started signed-out: the visitor clicked
  // save, went through signup, and landed back here. Their file is in the
  // hand-off store; finish the job and show it as done.
  useEffect(() => {
    if (!signedIn) return
    const stash = takeHandoff('keep')
    if (!stash) return
    setResult({ url: URL.createObjectURL(stash.blob), name: stash.name, blob: stash.blob })
    void uploadToVault(stash.blob, stash.name)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [signedIn])

  const pick = useCallback((picked: FileList | null) => {
    if (!picked || picked.length === 0) return
    setError(''); setNeedsAccount(false)
    setFiles(multi ? Array.from(picked) : [picked[0]])
  }, [multi])

  const convert = async () => {
    if (files.length === 0) return
    setBusy(true); setError(''); setNeedsAccount(false)

    if (result) { URL.revokeObjectURL(result.url); setResult(null) }

    try {
      const body = new FormData()
      files.forEach(f => body.append('files', f))
      if (options.trim()) body.append('options', options.trim())

      const res = await fetch(`/api/free-pdf/${toolKey}`, { method: 'POST', body })

      if (!res.ok) {
        // The server sends a JSON message plus requiresAccount when the limit
        // itself is the reason — that distinction drives the CTA below.
        let message = 'Could not process that file.'
        try {
          const j = await res.json()
          if (j?.message) message = j.message
          if (j?.requiresAccount) setNeedsAccount(true)
        } catch { /* non-JSON error body; keep the default message */ }
        setError(message)
        return
      }

      const blob = await res.blob()
      const disposition = res.headers.get('Content-Disposition') || ''
      const match = /filename="?([^";]+)"?/i.exec(disposition)
      setResult({ url: URL.createObjectURL(blob), name: match?.[1] || `${toolKey}-result`, blob })

      const left = res.headers.get('X-Free-Conversions-Remaining')
      if (left !== null && quota) setQuota({ ...quota, remaining: Number(left) })
    } catch {
      setError('Something went wrong. Check your connection and try again.')
    } finally {
      setBusy(false)
    }
  }

  const reset = () => {
    if (result) URL.revokeObjectURL(result.url)
    setResult(null); setFiles([]); setError(''); setNeedsAccount(false); setOptions('')
    setKept(false); setKeepError('')
    if (inputRef.current) inputRef.current.value = ''
  }

  // ── Post-conversion bridges ───────────────────────────────────────────────
  // These fire at the one moment the visitor demonstrably cares about this
  // file. Each carries the blob across navigation via the hand-off store, so
  // nobody is asked to re-upload something we are already holding.

  const isPdfResult = !!result && /\.pdf$/i.test(result.name)

  const toChat = (kind: 'chat-summarise' | 'chat-tables') => {
    if (!result) return
    setHandoff({
      blob: result.blob,
      name: result.name,
      sourceTool: toolKey,
      intent: kind,
      prompt: kind === 'chat-summarise'
        ? 'Summarise this PDF. Give me the key points, any dates or amounts, and anything unusual.'
        : 'Extract every table in this PDF as clean markdown tables. Preserve headers and units.',
    })
    // Signed-out visitors go through signin and land back in chat; the handoff
    // store survives that because it is a client-side navigation both ways.
    navigate(signedIn ? '/agent-chat' : `/signin?next=${encodeURIComponent('/agent-chat')}`)
  }

  const toExpenses = () => {
    if (!result) return
    setHandoff({ blob: result.blob, name: result.name, sourceTool: toolKey, intent: 'expense' })
    navigate(signedIn ? '/invoice-agent' : `/signin?next=${encodeURIComponent('/invoice-agent')}`)
  }

  // "Keep this file" — the converted output saved to the user's account. For
  // signed-out visitors this is the signup hook; for signed-in users it is one
  // click straight into their vault.
  const keepFile = async () => {
    if (!result) return
    if (!signedIn) {
      // Stash the blob so the promise "it will be waiting in your files" is
      // actually true: after signup the visitor lands back here and the
      // mount effect below finishes the save without another click.
      setHandoff({ blob: result.blob, name: result.name, sourceTool: toolKey, intent: 'keep' })
      navigate(`/signup?next=${encodeURIComponent(window.location.pathname)}`)
      return
    }
    await uploadToVault(result.blob, result.name)
  }

  const uploadToVault = async (blob: Blob, name: string) => {
    setKeeping(true); setKeepError('')
    try {
      const body = new FormData()
      body.append('files', new File([blob], name, { type: blob.type || 'application/pdf' }))
      body.append('entityType', 'vault')
      body.append('entityId', 'converted')
      const res = await fetch('/api/files/upload', {
        method: 'POST',
        headers: { Authorization: `Bearer ${localStorage.getItem('authToken')}` },
        body,
      })
      if (!res.ok) {
        const j = await res.json().catch(() => null)
        throw new Error(j?.error || 'Could not save the file.')
      }
      setKept(true)
    } catch (e) {
      setKeepError(e instanceof Error ? e.message : 'Could not save the file.')
    } finally {
      setKeeping(false)
    }
  }

  // Tools kept behind sign-in (ocr, redact, generate, pdfa, unlock…) are honest
  // about it rather than failing after an upload.
  if (!supported) {
    return (
      <div className="fpc-card fpc-locked">
        <PhosphorIcon className="bi bi-lock" />
        <p>{toolName} needs a free account — it uses more processing than the open tools.</p>
        <Link to={`/signup?next=${encodeURIComponent(`/pdf-tools/${toolKey}`)}`} className="fpc-btn">
          Create a free account
        </Link>
      </div>
    )
  }

  return (
    <div className="fpc-card">
      {!result && (
        <>
          <div
            className={`fpc-drop ${dragging ? 'fpc-drop-active' : ''}`}
            onDragOver={e => { e.preventDefault(); setDragging(true) }}
            onDragLeave={() => setDragging(false)}
            onDrop={e => { e.preventDefault(); setDragging(false); pick(e.dataTransfer.files) }}
            onClick={() => inputRef.current?.click()}
            role="button"
            tabIndex={0}
            onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') inputRef.current?.click() }}
            aria-label={`Choose a file for ${toolName}`}
          >
            <input
              ref={inputRef}
              type="file"
              hidden
              multiple={multi}
              accept={ACCEPT[toolKey] ?? '.pdf'}
              onChange={e => pick(e.target.files)}
            />
            <PhosphorIcon className="bi bi-cloud-arrow-up" />
            {files.length === 0 ? (
              <>
                <strong>Drop your file here</strong>
                <span>or click to choose{multi ? ' — several files allowed' : ''}</span>
              </>
            ) : (
              <>
                <strong>{files.length === 1 ? files[0].name : `${files.length} files ready`}</strong>
                <span>Click to change</span>
              </>
            )}
          </div>

          {optionField && (
            <label className="fpc-option">
              <span>{optionField.label}</span>
              <input
                type="text"
                value={options}
                placeholder={optionField.placeholder}
                onChange={e => setOptions(e.target.value)}
              />
            </label>
          )}

          <button className="fpc-btn fpc-go" onClick={convert} disabled={busy || files.length === 0 || optionMissing}>
            {busy
              ? <><span className="fpc-spin" /> Working…</>
              : <><PhosphorIcon className="bi bi-lightning-charge" /> {toolName}</>}
          </button>

          {quota && (
            <p className="fpc-quota">
              {quota.remaining} of {quota.dailyLimit} free conversions left today · up to{' '}
              {Math.round(quota.maxBytes / (1024 * 1024))}MB · {quota.maxPages} pages · no account needed
            </p>
          )}
        </>
      )}

      {error && (
        <div className="fpc-error">
          <PhosphorIcon className="bi bi-exclamation-triangle" /> {error}
          {needsAccount && (
            <Link to="/signup" className="fpc-btn fpc-btn-inline">Create a free account</Link>
          )}
        </div>
      )}

      {/* Success. The file is ALREADY delivered before anything is asked for —
          that ordering is the entire acquisition mechanic. */}
      {result && (
        <div className="fpc-done">
          <div className="fpc-done-icon"><PhosphorIcon className="bi bi-check-circle-fill" /></div>
          <strong>Done — your file is ready</strong>
          <a className="fpc-btn fpc-download" href={result.url} download={result.name}>
            <PhosphorIcon className="bi bi-download" /> Download {result.name}
          </a>
          <button className="fpc-link" onClick={reset}>Convert another file</button>

          {/* Bridges into the rest of the app, offered while the file is in
              hand. PDF-only: summarising a JPG or an XLSX through the PDF chat
              path would fail downstream. */}
          {isPdfResult && (
            <div className="fpc-next">
              <span className="fpc-next-label">Do more with this file</span>
              <div className="fpc-next-row">
                <button className="fpc-btn fpc-btn-ghost" onClick={() => toChat('chat-summarise')}>
                  <PhosphorIcon className="bi bi-stars" /> Summarise with AI
                </button>
                <button className="fpc-btn fpc-btn-ghost" onClick={() => toChat('chat-tables')}>
                  <PhosphorIcon className="bi bi-table" /> Extract tables
                </button>
                <button className="fpc-btn fpc-btn-ghost" onClick={toExpenses}>
                  <PhosphorIcon className="bi bi-receipt" /> Track as expense
                </button>
              </div>
            </div>
          )}

          <div className="fpc-upsell">
            {kept ? (
              <p><PhosphorIcon className="bi bi-check-circle" /> Saved to your files.</p>
            ) : (
              <>
                <p>
                  <strong>Keep this file?</strong>{' '}
                  {signedIn
                    ? 'Save it to your GenPrompt files so it is there when you need it.'
                    : 'Create a free account and it will be waiting in your files — plus batch processing and no daily limit.'}
                </p>
                <button className="fpc-btn fpc-btn-ghost" onClick={keepFile} disabled={keeping}>
                  {keeping
                    ? <><span className="fpc-spin" /> Saving…</>
                    : signedIn ? 'Save to my files' : 'Create a free account'}
                </button>
                {keepError && <p className="fpc-keep-error">{keepError}</p>}
              </>
            )}
          </div>
        </div>
      )}

      <p className="fpc-privacy">
        <PhosphorIcon className="bi bi-shield-check" /> Files are processed and deleted immediately — never stored or shared.
      </p>
    </div>
  )
}
