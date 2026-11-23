import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { compressImage } from '../utils/imageCompress'
import SnapButton from '../components/SnapButton'
import ModuleVoiceChat from '../components/ModuleVoiceChat'
import './snaptools.css'

interface Warranty { id: number; product: string; brand?: string | null; retailer?: string | null; purchaseDate?: string | null; warrantyMonths?: number | null; expiryDate?: string | null; notes?: string | null; imageData?: string | null }

function status(d: string | null | undefined, now: number): { label: string; cls: string } | null {
  if (!d) return null
  const ms = new Date(d).getTime() - now
  if (isNaN(ms)) return null
  if (ms <= 0) return { label: 'expired', cls: 'expired' }
  const hours = ms / 3600000
  if (hours <= 48) { // final 48h — count down in days + hours so a page left open stays live
    const dd = Math.floor(hours / 24)
    const hh = Math.floor(hours % 24)
    return { label: dd > 0 ? `${dd}d ${hh}h left` : hh > 0 ? `${hh}h left` : '<1h left', cls: 'soon' }
  }
  const days = Math.round(hours / 24)
  if (days <= 60) return { label: `${days}d left`, cls: 'soon' }
  const months = Math.round(days / 30.44)
  return { label: `${months}mo left`, cls: 'ok' }
}

// Percentage of the warranty period already elapsed (0–100), or null if we can't tell.
function elapsedPct(purchase: string | null | undefined, expiry: string | null | undefined, now: number): number | null {
  if (!purchase || !expiry) return null
  const start = new Date(purchase).getTime()
  const end = new Date(expiry).getTime()
  if (isNaN(start) || isNaN(end) || end <= start) return null
  return Math.round(Math.min(1, Math.max(0, (now - start) / (end - start))) * 100)
}

export default function WarrantyVault() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('warranty')
  useLayoutEffect(() => { setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Warranty Vault' }]); return () => setBreadcrumbs([]) }, [])

  const [items, setItems] = useState<Warranty[]>([])
  const [loading, setLoading] = useState(true)
  const [adding, setAdding] = useState(false)
  const [error, setError] = useState('')
  const [now, setNow] = useState(() => Date.now())

  const load = async () => { try { const { data } = await api.get('/warranty'); setItems(data) } catch { setError('Could not load your vault.') } finally { setLoading(false) } }
  useEffect(() => { load() }, [])

  // Keep countdowns live: re-tick every minute and whenever the tab regains focus.
  useEffect(() => {
    const tick = () => setNow(Date.now())
    const id = window.setInterval(tick, 60000)
    window.addEventListener('focus', tick)
    document.addEventListener('visibilitychange', tick)
    return () => { window.clearInterval(id); window.removeEventListener('focus', tick); document.removeEventListener('visibilitychange', tick) }
  }, [])

  const onFile = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError(''); setAdding(true)
    try { const imageData = await compressImage(files[0]); const { data } = await api.post('/warranty', { imageData }); setItems(x => [...x, data].sort((a, b) => (a.expiryDate || '').localeCompare(b.expiryDate || ''))) }
    catch (e: unknown) { setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not read that photo.') }
    finally { setAdding(false) }
  }
  const remove = async (w: Warranty) => { try { await api.delete(`/warranty/${w.id}`); setItems(l => l.filter(x => x.id !== w.id)) } catch { setError('Could not remove.') } }


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Warranty Vault',
    busy: adding,
    error,
    count: items.length,
    noun: 'warranties',
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-shield-check" /> Warranty Vault</h1>
        <p>Snap a product or its receipt and I'll log the warranty and count down to when it expires.</p>
      </div>
      {/* Ask instead of scanning the list — answers come from what this
          module already holds. */}
      <ModuleVoiceChat module="warranty" />
      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
      <AgentStage {...stageProps} model={field} />

      <div className="st-card" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <div><strong>{items.length}</strong> item{items.length === 1 ? '' : 's'} tracked</div>
        <SnapButton onPick={onFile} busy={adding} label="Add item" />
      </div>

      {loading ? <div className="st-empty"><span className="st-spin" style={{ borderTopColor: '#d62976' }} /> Loading…</div>
        : items.length === 0 ? <div className="st-empty"><PhosphorIcon className="bi bi-box-seam" /><p>Nothing tracked yet — snap a product or receipt to start a warranty countdown.</p></div>
        : (
          <div className="st-list">
            {items.map(w => { const s = status(w.expiryDate, now); const pct = elapsedPct(w.purchaseDate, w.expiryDate, now); return (
              <div key={w.id} className="st-row">
                {w.imageData ? <img className="st-thumb" src={w.imageData} alt="" /> : <span className="st-thumb st-thumb-empty"><PhosphorIcon className="bi bi-box" /></span>}
                <div className="st-row-main">
                  <strong>{w.product}{w.brand ? ` · ${w.brand}` : ''}</strong>
                  <small>{[w.purchaseDate && `bought ${w.purchaseDate}`, w.retailer, w.expiryDate && `expires ${w.expiryDate}`].filter(Boolean).join(' · ')}</small>
                  {pct != null && (
                    <div className="wv-bar" title={`${pct}% of warranty period elapsed`}>
                      <span className={`wv-bar-fill ${s?.cls || 'ok'}`} style={{ width: `${pct}%` }} />
                    </div>
                  )}
                </div>
                {s && <span className={`st-badge ${s.cls}`}>{s.label}</span>}
                <button className="st-del" onClick={() => remove(w)}><PhosphorIcon className="bi bi-trash" /></button>
              </div>
            )})}
          </div>
        )}
    </div>
  )
}
