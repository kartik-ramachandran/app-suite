import PhosphorIcon from '../components/PhosphorIcon'
import { useLayoutEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { compressImage } from '../utils/imageCompress'
import SnapButton from '../components/SnapButton'
import './snaptools.css'

interface Result { product: string; verdict: 'ok' | 'caution' | 'avoid'; reasons: string[]; flagged: string[]; healthNote: string; hasDiet: boolean }
const VERDICT: Record<string, { icon: string; label: string }> = {
  ok: { icon: 'bi-check-circle-fill', label: 'Good to go' },
  caution: { icon: 'bi-exclamation-triangle-fill', label: 'Heads up' },
  avoid: { icon: 'bi-x-octagon-fill', label: 'Best avoided' },
}

export default function LabelScanner() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('label-scanner')
  useLayoutEffect(() => { setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Label Scanner' }]); return () => setBreadcrumbs([]) }, [])

  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [result, setResult] = useState<Result | null>(null)

  const onFile = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError(''); setBusy(true); setResult(null)
    try { const imageData = await compressImage(files[0]); const { data } = await api.post('/label/scan', { imageData }); setResult(data) }
    catch (e: unknown) { setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not read that label.') }
    finally { setBusy(false) }
  }

  const v = result ? VERDICT[result.verdict] : null

  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Label Scanner',
    busy: busy,
    error,
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-upc-scan" /> Label Scanner</h1>
        <p>Snap a food or product label and I'll check it against your saved dietary preferences and allergies.</p>
      </div>
      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
      <AgentStage {...stageProps} model={field} />

      <div className="st-card" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <span className="st-muted">Set your allergies/diet in <Link to="/kitchen" style={{ color: '#d62976' }}>Kitchen</Link> for a personalised verdict.</span>
        <SnapButton onPick={onFile} busy={busy} label="Scan a label" />
      </div>

      {result && v && (
        <div className={`st-verdict ${result.verdict}`}>
          <div className="st-verdict-head"><PhosphorIcon className={`bi ${v.icon}`} /> {v.label}</div>
          {result.product && <div style={{ fontWeight: 600, marginBottom: 6 }}>{result.product}</div>}
          {result.flagged.length > 0 && <div style={{ marginBottom: 8 }}>{result.flagged.map((f, i) => <span key={i} className="st-flag">{f}</span>)}</div>}
          {result.reasons.length > 0 && <ul>{result.reasons.map((r, i) => <li key={i}>{r}</li>)}</ul>}
          {result.healthNote && <p style={{ marginTop: 10, fontSize: '0.86rem' }}>{result.healthNote}</p>}
          {!result.hasDiet && <p className="st-disclaimer" style={{ marginTop: 10 }}><PhosphorIcon className="bi bi-info-circle" /> No dietary preferences on file — add them in Kitchen for allergy-aware checks.</p>}
        </div>
      )}
      <p className="st-disclaimer"><PhosphorIcon className="bi bi-info-circle" /> AI estimate from the label — not medical advice. Always read the pack for allergens.</p>
    </div>
  )
}
