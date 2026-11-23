import PhosphorIcon from '../components/PhosphorIcon'
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import EmmdRenderer from '../components/EmmdRenderer'
import { parseEmmd, stripSeal, type EmmdSealInfo } from '../utils/emmd'
import { downloadEmmdHtml, downloadEmmdPdf, downloadEmmdSource, exportStamp, printEmmd } from '../utils/emmdExport'
import { getEmmd, verifyEmmd, type EmmdSealStatus } from '../services/emmdApi'
import './Emmd.css'

function slugify(s: string): string {
  return (s || 'document').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40) || 'document'
}

function SealBadge({ seal }: { seal: EmmdSealStatus | null }) {
  if (!seal || !seal.sealed) {
    return <div className="emmd-seal emmd-seal-none"><PhosphorIcon className="bi bi-shield" /> <span>Not sealed</span></div>
  }
  if (seal.valid) {
    return (
      <div className="emmd-seal emmd-seal-ok">
        <PhosphorIcon className="bi bi-patch-check-fill" />
        <span>
          <strong>Sealed &amp; verified</strong> — {seal.sealedBy}{seal.sealedAt ? ` · ${new Date(seal.sealedAt).toLocaleString()}` : ''}
          <em>Content unmodified since sealing.</em>
        </span>
      </div>
    )
  }
  return (
    <div className="emmd-seal emmd-seal-bad">
      <PhosphorIcon className="bi bi-patch-exclamation-fill" />
      <span><strong>Seal invalid</strong> — {seal.reason || 'This document has changed since it was sealed.'}</span>
    </div>
  )
}

export default function EmmdViewer() {
  const { setBreadcrumbs } = useLayoutControls()
  const navigate = useNavigate()
  const { id } = useParams()

  const [title, setTitle] = useState('Document')
  const [theme, setTheme] = useState('clean')
  const [content, setContent] = useState('')
  const [seal, setSeal] = useState<EmmdSealStatus | null>(null)
  const [sealToken, setSealToken] = useState<string | null>(null)
  const [source, setSource] = useState<'stored' | 'file'>('stored')
  const [loading, setLoading] = useState(true)
  const [pdfBusy, setPdfBusy] = useState(false)
  const [error, setError] = useState('')

  const previewRef = useRef<HTMLDivElement>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)

  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Enhanced MMD', path: '/emmd' }, { label: 'Reader' }])
    return () => setBreadcrumbs([])
  }, [])

  const loadStored = useCallback(async (docId: string) => {
    setLoading(true); setError('')
    try {
      const doc = await getEmmd(docId)
      setSource('stored')
      setTitle(doc.title)
      setTheme(doc.theme)
      setContent(doc.content)
      setSeal(doc.seal)
      setSealToken(doc.sealToken || null)
    } catch {
      setError('Could not open that document.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { if (id) loadStored(id) }, [id, loadStored])

  const onVerifyFile = async (file: File) => {
    if (file.size > 3 * 1024 * 1024) {
      setError(`That file is too large (${Math.round(file.size / 1024)} KB). The limit is 3072 KB.`)
      return
    }
    setLoading(true); setError('')
    try {
      const text = await file.text()
      const { contentWithoutSeal, seal: fileSeal } = stripSeal(text)
      const parsed = parseEmmd(text)
      setSource('file')
      setTitle(parsed.frontmatter.title || file.name)
      setTheme(parsed.frontmatter.theme || 'clean')
      setContent(contentWithoutSeal)
      setSealToken(fileSeal?.token || null)
      if (fileSeal?.token) {
        setSeal(await verifyEmmd(contentWithoutSeal, fileSeal.token))
      } else {
        setSeal({ sealed: false, valid: false, tampered: false })
      }
    } catch {
      setError('Could not read that file.')
    } finally {
      setLoading(false)
    }
  }

  const filenameBase = () => `${slugify(title)}-${exportStamp()}`
  const sealInfo: EmmdSealInfo | null = seal?.valid && sealToken
    ? { token: sealToken, sealedBy: seal.sealedBy, sealedAt: seal.sealedAt }
    : null

  const exportPdf = async () => {
    const bodyHtml = previewRef.current?.innerHTML
    if (!bodyHtml || pdfBusy) return
    setPdfBusy(true)
    try {
      await downloadEmmdPdf(content, bodyHtml, filenameBase(), theme)
    } catch {
      printEmmd(content, bodyHtml, theme)
    } finally { setPdfBusy(false) }
  }
  const exportHtml = () => {
    const bodyHtml = previewRef.current?.innerHTML
    if (bodyHtml) downloadEmmdHtml(content, bodyHtml, filenameBase(), theme)
  }

  return (
    <div className="emmd-viewer">
      <div className="emmd-viewer-bar">
        <div className="emmd-viewer-bar-left">
          <button className="emmd-btn" onClick={() => navigate('/emmd')}><PhosphorIcon className="bi bi-arrow-left" /> Studio</button>
          <button className="emmd-btn" onClick={() => fileInputRef.current?.click()}><PhosphorIcon className="bi bi-shield-check" /> Verify a file</button>
          <input
            ref={fileInputRef}
            type="file"
            accept=".emmd,.md,.markdown,text/markdown,text/plain"
            style={{ display: 'none' }}
            onChange={e => { const f = e.target.files?.[0]; if (f) onVerifyFile(f); e.target.value = '' }}
          />
        </div>
        <div className="emmd-viewer-bar-right">
          <button className="emmd-btn" onClick={exportPdf} disabled={pdfBusy}>
            {pdfBusy ? <><span className="emmd-spin" /> PDF…</> : <><PhosphorIcon className="bi bi-file-earmark-pdf" /> PDF</>}
          </button>
          <button className="emmd-btn" onClick={exportHtml}><PhosphorIcon className="bi bi-filetype-html" /> HTML</button>
          {sealInfo && <button className="emmd-btn" onClick={() => downloadEmmdSource(content, sealInfo, filenameBase())}><PhosphorIcon className="bi bi-filetype-md" /> .emmd</button>}
        </div>
      </div>

      {error && <div className="emmd-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      <SealBadge seal={seal} />
      {source === 'file' && <p className="emmd-viewer-note"><PhosphorIcon className="bi bi-info-circle" /> Verifying an uploaded file locally against GenPrompt's public seal key.</p>}

      <div className="emmd-viewer-surface">
        {loading ? (
          <div className="emmd-loading"><span className="emmd-spin" /> Loading…</div>
        ) : (
          <EmmdRenderer ref={previewRef} content={content} themeOverride={theme} />
        )}
      </div>
    </div>
  )
}
