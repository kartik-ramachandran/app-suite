import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import ContentHandoff from '../components/ContentHandoff'
import remarkGfm from 'remark-gfm'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { compressImage } from '../utils/imageCompress'
import SnapButton from '../components/SnapButton'
import './snaptools.css'

interface Digitized { id?: number; title: string; markdown: string; actionItems: string[]; decisions: string[] }
interface HistoryItem { id: number; title: string; createdAt: string }

function relTime(iso?: string): string {
  if (!iso) return ''
  const mins = Math.floor((Date.now() - new Date(iso).getTime()) / 60000)
  if (mins < 60) return `${Math.max(1, mins)}m ago`
  const hrs = Math.floor(mins / 60)
  if (hrs < 24) return `${hrs}h ago`
  const days = Math.floor(hrs / 24)
  return days < 7 ? `${days}d ago` : new Date(iso).toLocaleDateString()
}

export default function Whiteboard() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('whiteboard')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Whiteboard Digitizer' }])
    return () => setBreadcrumbs([])
  }, [])

  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [result, setResult] = useState<Digitized | null>(null)
  const [copied, setCopied] = useState(false)
  const [history, setHistory] = useState<HistoryItem[]>([])

  const loadHistory = async () => { try { const { data } = await api.get('/whiteboard/history'); setHistory(data) } catch { /* ignore */ } }
  useEffect(() => { loadHistory() }, [])

  const onFile = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError(''); setBusy(true); setResult(null)
    try {
      const imageData = await compressImage(files[0], 1800)
      const { data } = await api.post('/whiteboard/extract', { imageData })
      setResult(data)
      loadHistory()
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not digitize that image.')
    } finally { setBusy(false) }
  }

  const openBoard = async (id: number) => {
    setError('')
    try { const { data } = await api.get(`/whiteboard/history/${id}`); setResult(data) } catch { setError('Could not open that saved board.') }
  }
  const deleteBoard = async (id: number, e: React.MouseEvent) => {
    e.stopPropagation()
    try { await api.delete(`/whiteboard/history/${id}`); setHistory(h => h.filter(x => x.id !== id)); if (result?.id === id) setResult(null) } catch { /* ignore */ }
  }

  const fullText = () => {
    if (!result) return ''
    let out = result.markdown
    if (result.actionItems.length) out += '\n\n## Action items\n' + result.actionItems.map(a => `- [ ] ${a}`).join('\n')
    if (result.decisions.length) out += '\n\n## Decisions\n' + result.decisions.map(d => `- ${d}`).join('\n')
    return out
  }
  const copy = async () => { await navigator.clipboard.writeText(fullText()); setCopied(true); setTimeout(() => setCopied(false), 1800) }
  const download = () => {
    const url = URL.createObjectURL(new Blob([fullText()], { type: 'text/markdown;charset=utf-8' }))
    const a = document.createElement('a')
    a.href = url; a.download = `${(result?.title || 'notes').replace(/[^a-z0-9]+/gi, '-').toLowerCase()}.md`
    document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(url)
  }


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Whiteboard Digitizer',
    busy: busy,
    error,
    count: history.length,
    noun: 'boards',
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-easel2" /> Whiteboard Digitizer</h1>
        <p>Snap a whiteboard, sticky-note wall, or handwritten page and get clean markdown, action items, and decisions — ready to copy or download.</p>
      </div>

      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      <AgentStage {...stageProps} model={field} />

      <div className="wb-layout">
        <div className="wb-main">
          <div className="st-card" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
            <span className="st-muted">Point straight at the board for the cleanest read. Your photo is analyzed and discarded — only the digitized text is saved so you can revisit it.</span>
            <SnapButton onPick={onFile} busy={busy} label="Digitize a board" />
          </div>

          {result && (
            <>
              <div className="wb-toolbar">
                <button className="st-btn st-btn-primary" onClick={copy}>{copied ? <><PhosphorIcon className="bi bi-check-lg" /> Copied</> : <><PhosphorIcon className="bi bi-clipboard" /> Copy markdown</>}</button>
                <button className="st-btn" onClick={download}><PhosphorIcon className="bi bi-download" /> Download .md</button>
              </div>
              <div className="wb-md"><ReactMarkdown remarkPlugins={[remarkGfm]}>{result.markdown || '_No text found._'}</ReactMarkdown></div>
              {result.actionItems.length > 0 && (
                <div className="wb-actions-card"><h3><PhosphorIcon className="bi bi-check2-square" /> Action items</h3>{result.actionItems.map((a, i) => <div key={i} className="wb-check"><PhosphorIcon className="bi bi-square" /> {a}</div>)}</div>
              )}
              {result.decisions.length > 0 && (
                <div className="wb-actions-card"><h3><PhosphorIcon className="bi bi-flag" /> Decisions</h3>{result.decisions.map((d, i) => <div key={i} className="wb-check"><PhosphorIcon className="bi bi-dot" /> {d}</div>)}</div>
              )}

              {/* A digitised board is clean markdown — the same thing Study
                  Studio and Inkling take as input. Without this it dead-ends
                  at copy/download, and the expensive vision pass is used once. */}
              <ContentHandoff
                kind="markdown"
                title={result.title}
                content={fullText()}
                sourceModule="whiteboard"
                sourceLabel="Whiteboard"
                sourceId={result.id}
              />
            </>
          )}
        </div>

        {history.length > 0 && (
          <aside className="wb-history">
            <h3><PhosphorIcon className="bi bi-clock-history" /> Recent boards</h3>
            {history.map(h => (
              <button key={h.id} className={`wb-hist-item${result?.id === h.id ? ' active' : ''}`} onClick={() => openBoard(h.id)}>
                <span className="wb-hist-title">{h.title}</span>
                <span className="wb-hist-time">{relTime(h.createdAt)}</span>
                <span className="wb-hist-del" onClick={e => deleteBoard(h.id, e)} title="Delete"><PhosphorIcon className="bi bi-x" /></span>
              </button>
            ))}
          </aside>
        )}
      </div>
    </div>
  )
}
