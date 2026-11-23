import PhosphorIcon from '../components/PhosphorIcon'
import { useLayoutEffect, useRef, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { compressImage } from '../utils/imageCompress'
import SnapButton from '../components/SnapButton'
import ExportButtons from '../components/ExportButtons'
import './snaptools.css'

interface Option { retailer: string; price?: string; note?: string }
interface Result { product: string; priceRange?: string; options: Option[]; tip?: string }

export default function PriceChecker() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('price-checker')
  useLayoutEffect(() => { setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Price Checker' }]); return () => setBreadcrumbs([]) }, [])

  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [identified, setIdentified] = useState('')
  const [result, setResult] = useState<Result | null>(null)
  // Nothing is stored server-side here, so the export is the only way to keep a result.
  const resultRef = useRef<HTMLDivElement>(null)

  const onFile = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError(''); setBusy(true); setResult(null); setIdentified('')
    try {
      const imageData = await compressImage(files[0])
      const { data } = await api.post('/price/check', { imageData })
      setIdentified(data.identified || '')
      if (data.result) setResult(data.result); else if (data.message) setError(data.message)
    } catch (e: unknown) { setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not check that product.') }
    finally { setBusy(false) }
  }


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Price Checker',
    busy: busy,
    error,
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-tags" /> Price &amp; Deal Checker</h1>
        <p>Snap a product and I'll identify it and search the web for where to buy it and the best deals.</p>
      </div>
      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
      <AgentStage {...stageProps} model={field} />

      <div className="st-card" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <span className="st-muted">Point at the product or its box/label for the best match.</span>
        <SnapButton onPick={onFile} busy={busy} label="Check a product" busyLabel="Searching…" />
      </div>

      {(identified || result) && (
        <div className="st-card">
          <div className="st-card-actions">
            <ExportButtons
              title={identified || result?.product || 'Price check'}
              filenameBase="price-check"
              subtitle={result?.priceRange}
              size="sm"
              getBodyHtml={() => resultRef.current?.innerHTML}
            />
          </div>
          <div ref={resultRef}>
          {identified && <div style={{ marginBottom: 8 }}><span className="st-muted">Identified:</span> <strong>{identified}</strong></div>}
          {result?.priceRange && <div style={{ marginBottom: 10 }}><span className="st-badge ok">{result.priceRange}</span></div>}
          {result && result.options?.length > 0 && (
            <div className="st-list">
              {result.options.map((o, i) => (
                <div key={i} className="st-row">
                  <span className="st-thumb st-thumb-empty"><PhosphorIcon className="bi bi-shop" /></span>
                  <div className="st-row-main"><strong>{o.retailer}</strong><small>{o.note}</small></div>
                  {o.price && <span className="st-badge ok">{o.price}</span>}
                </div>
              ))}
            </div>
          )}
          {result?.tip && <p className="st-muted" style={{ marginTop: 12 }}><PhosphorIcon className="bi bi-lightbulb" /> {result.tip}</p>}
          </div>
        </div>
      )}
      <p className="st-disclaimer"><PhosphorIcon className="bi bi-info-circle" /> Prices come from live web results and can change — always confirm on the retailer's site.</p>
    </div>
  )
}
