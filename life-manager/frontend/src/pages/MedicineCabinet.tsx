import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { compressImage } from '../utils/imageCompress'
import SnapButton from '../components/SnapButton'
import ModuleVoiceChat from '../components/ModuleVoiceChat'
import './snaptools.css'

interface Med { id: number; name: string; form?: string | null; strength?: string | null; purpose?: string | null; expiryDate?: string | null; notes?: string | null; imageData?: string | null }

function expiry(d: string | null | undefined, now: number): { label: string; cls: string } | null {
  if (!d) return null
  const iso = /^\d{4}-\d{2}$/.test(d) ? `${d}-01` : d
  const t = new Date(iso).getTime()
  if (isNaN(t)) return null
  const ms = t - now
  if (ms < 0) return { label: 'expired', cls: 'expired' }
  const hours = ms / 3600000
  if (hours <= 48) { // final 48h — count down in days + hours
    const dd = Math.floor(hours / 24)
    const hh = Math.floor(hours % 24)
    return { label: dd > 0 ? `${dd}d ${hh}h left` : hh > 0 ? `${hh}h left` : '<1h left', cls: 'soon' }
  }
  const days = Math.round(hours / 24)
  if (days <= 60) return { label: `expires in ${days}d`, cls: 'soon' }
  const months = Math.round(days / 30.44)
  return { label: `good ~${months}mo`, cls: 'ok' }
}

export default function MedicineCabinet() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('medicine')
  useLayoutEffect(() => { setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Medicine Cabinet' }]); return () => setBreadcrumbs([]) }, [])

  const [meds, setMeds] = useState<Med[]>([])
  const [loading, setLoading] = useState(true)
  const [adding, setAdding] = useState(false)
  const [error, setError] = useState('')
  const [advice, setAdvice] = useState('')
  const [checking, setChecking] = useState(false)
  const [now, setNow] = useState(() => Date.now())

  const load = async () => { try { const { data } = await api.get('/medicine'); setMeds(data) } catch { setError('Could not load your cabinet.') } finally { setLoading(false) } }
  useEffect(() => { load() }, [])

  // Keep expiry badges live: re-tick every minute and whenever the tab regains focus.
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
    try { const imageData = await compressImage(files[0]); const { data } = await api.post('/medicine', { imageData }); setMeds(m => [data, ...m]) }
    catch (e: unknown) { setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not read that photo.') }
    finally { setAdding(false) }
  }
  const remove = async (m: Med) => { try { await api.delete(`/medicine/${m.id}`); setMeds(l => l.filter(x => x.id !== m.id)); setAdvice('') } catch { setError('Could not remove.') } }
  const check = async () => { setChecking(true); setAdvice(''); try { const { data } = await api.post('/medicine/check'); setAdvice(data.advice || '') } catch { setError('Could not run the check.') } finally { setChecking(false) } }


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Medicine Cabinet',
    busy: adding,
    error,
    count: meds.length,
    noun: 'medicines',
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-capsule" /> Medicine Cabinet</h1>
        <p>Snap your medicines to catalogue them, track expiry dates, and run a general interaction check.</p>
      </div>
      {/* Ask instead of scanning the list — answers come from what this
          module already holds. */}
      <ModuleVoiceChat module="medicine" />
      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
      <AgentStage {...stageProps} model={field} />

      <div className="st-card" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <div><strong>{meds.length}</strong> item{meds.length === 1 ? '' : 's'}</div>
        <div style={{ display: 'flex', gap: 8 }}>
          {meds.length >= 2 && <button className="st-btn" onClick={check} disabled={checking}>{checking ? <><span className="st-spin" style={{ borderTopColor: '#d62976' }} /> Checking…</> : <><PhosphorIcon className="bi bi-shield-check" /> Check interactions</>}</button>}
          <SnapButton onPick={onFile} busy={adding} label="Add medicine" />
        </div>
      </div>

      {advice && <div className="st-advice">{advice.split('\n').filter(Boolean).map((l, i) => <p key={i}>{l.replace(/^- /, '• ')}</p>)}</div>}

      {loading ? <div className="st-empty"><span className="st-spin" style={{ borderTopColor: '#d62976' }} /> Loading…</div>
        : meds.length === 0 ? <div className="st-empty"><PhosphorIcon className="bi bi-capsule-pill" /><p>Nothing here yet — snap a medicine package and I'll log it with its expiry.</p></div>
        : (
          <div className="st-list">
            {meds.map(m => { const ex = expiry(m.expiryDate, now); return (
              <div key={m.id} className="st-row">
                {m.imageData ? <img className="st-thumb" src={m.imageData} alt="" /> : <span className="st-thumb st-thumb-empty"><PhosphorIcon className="bi bi-capsule" /></span>}
                <div className="st-row-main">
                  <strong>{m.name}{m.strength ? ` · ${m.strength}` : ''}</strong>
                  <small>{[m.form, m.purpose].filter(Boolean).join(' · ')}</small>
                </div>
                {ex && <span className={`st-badge ${ex.cls}`}>{ex.label}</span>}
                <button className="st-del" onClick={() => remove(m)}><PhosphorIcon className="bi bi-trash" /></button>
              </div>
            )})}
          </div>
        )}
      <p className="st-disclaimer"><PhosphorIcon className="bi bi-info-circle" /> Not medical advice — always confirm dosing and interactions with a pharmacist or doctor.</p>
    </div>
  )
}
