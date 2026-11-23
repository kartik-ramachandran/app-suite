import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { compressImage } from '../utils/imageCompress'
import SnapButton from '../components/SnapButton'
import ModuleVoiceChat from '../components/ModuleVoiceChat'
import './snaptools.css'

interface Plant {
  id: number; name: string; nickname?: string | null; species?: string | null
  health?: string | null; diagnosis?: string | null; light?: string | null
  careTips?: string | null; waterEveryDays?: number | null; lastWatered?: string | null; imageData?: string | null
}

function waterStatus(p: Plant): { label: string; due: boolean } {
  if (!p.lastWatered || !p.waterEveryDays) return { label: 'water tracking off', due: false }
  const last = new Date(p.lastWatered + 'T00:00:00')
  const next = new Date(last.getTime() + p.waterEveryDays * 86400000)
  const days = Math.ceil((next.getTime() - Date.now()) / 86400000)
  if (days <= 0) return { label: 'water now', due: true }
  return { label: `water in ${days}d`, due: false }
}

export default function PlantDoctor() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('plant-doctor')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Plant Doctor' }])
    return () => setBreadcrumbs([])
  }, [])

  const [plants, setPlants] = useState<Plant[]>([])
  const [loading, setLoading] = useState(true)
  const [adding, setAdding] = useState(false)
  const [error, setError] = useState('')

  const load = async () => {
    try { const { data } = await api.get('/plants'); setPlants(data) } catch { setError('Could not load your plants.') }
    finally { setLoading(false) }
  }
  useEffect(() => { load() }, [])

  const onFile = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError(''); setAdding(true)
    try {
      const imageData = await compressImage(files[0])
      const { data } = await api.post('/plants', { imageData })
      setPlants(p => [data, ...p])
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not analyze that photo.')
    } finally { setAdding(false) }
  }

  const water = async (p: Plant) => {
    try { const { data } = await api.post(`/plants/${p.id}/water`); setPlants(list => list.map(x => x.id === p.id ? { ...x, lastWatered: data.lastWatered } : x)) } catch { /* ignore */ }
  }
  const remove = async (p: Plant) => {
    if (!confirm(`Remove ${p.name}?`)) return
    try { await api.delete(`/plants/${p.id}`); setPlants(list => list.filter(x => x.id !== p.id)) } catch { setError('Could not remove the plant.') }
  }


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Plant Doctor',
    busy: adding,
    error,
    count: plants.length,
    noun: 'plants',
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-flower1" /> Plant Doctor</h1>
        <p>Snap a plant to identify it, spot problems, and get a care plan — then track watering so nothing dries out.</p>
      </div>
      {/* Ask instead of scanning the list — answers come from what this
          module already holds. */}
      <ModuleVoiceChat module="plant" />

      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      <AgentStage {...stageProps} model={field} />

      <div className="st-card" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <div><strong>{plants.length}</strong> plant{plants.length === 1 ? '' : 's'} in your collection</div>
        <SnapButton onPick={onFile} busy={adding} label="Add a plant" busyLabel="Diagnosing…" />
      </div>

      {loading ? (
        <div className="st-empty"><span className="st-spin" style={{ borderTopColor: '#d62976' }} /> Loading…</div>
      ) : plants.length === 0 ? (
        <div className="st-empty"><PhosphorIcon className="bi bi-flower2" /><p>No plants yet — snap your first one and I'll identify it and check its health.</p></div>
      ) : (
        <div className="pl-grid">
          {plants.map(p => {
            const ws = waterStatus(p)
            const tips = (p.careTips || '').split('\n').filter(Boolean)
            return (
              <div key={p.id} className="pl-card">
                <div className="pl-img">
                  {p.imageData ? <img src={p.imageData} alt={p.name} loading="lazy" /> : null}
                  {p.health && p.health !== 'unknown' && <span className={`pl-health ${p.health}`}>{p.health.replace('-', ' ')}</span>}
                </div>
                <div className="pl-body">
                  <strong>{p.name}</strong>
                  {p.species && <div className="pl-species">{p.species}</div>}
                  {p.diagnosis && <p className="pl-diag">{p.diagnosis}</p>}
                  <div className="pl-meta">
                    {p.light && <span><PhosphorIcon className="bi bi-brightness-high" /> {p.light}</span>}
                    {p.waterEveryDays ? <span><PhosphorIcon className="bi bi-droplet" /> every {p.waterEveryDays}d</span> : null}
                  </div>
                  {tips.length > 0 && <ul className="pl-tips">{tips.slice(0, 4).map((t, i) => <li key={i}>{t}</li>)}</ul>}
                  <div className="pl-water-row">
                    <span className={`pl-water-due${ws.due ? ' now' : ''}`}><PhosphorIcon className="bi bi-droplet-half" /> {ws.label}</span>
                    <div className="pl-actions">
                      <button className="pl-icon-btn" title="Mark watered today" onClick={() => water(p)}><PhosphorIcon className="bi bi-droplet-fill" /></button>
                      <button className="pl-icon-btn" title="Remove" onClick={() => remove(p)}><PhosphorIcon className="bi bi-trash" /></button>
                    </div>
                  </div>
                </div>
              </div>
            )
          })}
        </div>
      )}

      <p className="st-disclaimer"><PhosphorIcon className="bi bi-info-circle" /> Identifications and care advice are AI estimates — double-check for toxic or sensitive species.</p>
    </div>
  )
}
