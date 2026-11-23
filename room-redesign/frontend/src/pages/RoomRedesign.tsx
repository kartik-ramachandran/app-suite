import PhosphorIcon from '../components/PhosphorIcon'
import { useLayoutEffect, useRef, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { compressImage } from '../utils/imageCompress'
import SnapButton from '../components/SnapButton'
import ExportButtons from '../components/ExportButtons'
import './snaptools.css'

interface Result { assessment: string; ideas: string[]; keepItems: string[]; shopping: string[] }
const STYLES = ['Modern', 'Scandinavian', 'Minimalist', 'Boho', 'Industrial', 'Mid-century', 'Japandi', 'Cozy / Hygge', 'Coastal']

export default function RoomRedesign() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('room-redesign')
  useLayoutEffect(() => { setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Room Redesign' }]); return () => setBreadcrumbs([]) }, [])

  const [style, setStyle] = useState('Modern')
  const [budget, setBudget] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [result, setResult] = useState<Result | null>(null)
  const [preview, setPreview] = useState('')
  // Zero-storage tool — the export is the only way to take the plan shopping.
  const resultRef = useRef<HTMLDivElement>(null)

  const onFile = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError(''); setBusy(true); setResult(null)
    try {
      const imageData = await compressImage(files[0], 1600)
      setPreview(imageData)
      const { data } = await api.post('/room/redesign', { imageData, style, budget })
      setResult(data)
    } catch (e: unknown) { setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not analyze that room.') }
    finally { setBusy(false) }
  }


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Room Redesign',
    busy: busy,
    error,
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-house-heart" /> Room Redesign</h1>
        <p>Snap a room and get specific restyling ideas plus a shopping list to bring the look together.</p>
      </div>
      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
      <AgentStage {...stageProps} model={field} />

      <div className="st-card">
        <div className="st-form">
          <div className="st-field2"><label>Style</label>
            <select className="st-select" value={style} onChange={e => setStyle(e.target.value)}>{STYLES.map(s => <option key={s}>{s}</option>)}</select>
          </div>
          <div className="st-field2"><label>Budget (optional)</label><input className="st-input" value={budget} onChange={e => setBudget(e.target.value)} placeholder="e.g. under $500" /></div>
        </div>
        <div style={{ marginTop: 14 }}><SnapButton onPick={onFile} busy={busy} label="Redesign my room" busyLabel="Designing…" /></div>
      </div>

      {result && (
        <div className="st-card">
          <div className="st-card-actions">
            <ExportButtons
              title={`${style} room redesign`}
              filenameBase="room-redesign"
              subtitle={budget || undefined}
              size="sm"
              getBodyHtml={() => resultRef.current?.innerHTML}
            />
          </div>
          {preview && <img src={preview} alt="room" style={{ width: '100%', maxHeight: 320, objectFit: 'cover', borderRadius: 12, marginBottom: 14 }} />}
          <div ref={resultRef}>
          {result.assessment && <p className="st-muted" style={{ marginTop: 0 }}>{result.assessment}</p>}
          {result.ideas?.length > 0 && <div className="st-section"><h3><PhosphorIcon className="bi bi-brush" /> Ideas ({style})</h3><ul>{result.ideas.map((x, i) => <li key={i}>{x}</li>)}</ul></div>}
          {result.keepItems?.length > 0 && <div className="st-section"><h3><PhosphorIcon className="bi bi-hand-thumbs-up" /> Worth keeping</h3><ul>{result.keepItems.map((x, i) => <li key={i}>{x}</li>)}</ul></div>}
          {result.shopping?.length > 0 && <div className="st-section"><h3><PhosphorIcon className="bi bi-bag" /> Shopping list</h3>{result.shopping.map((x, i) => <div key={i} className="wb-check"><PhosphorIcon className="bi bi-cart" /> {x}</div>)}</div>}
          </div>
        </div>
      )}
    </div>
  )
}
