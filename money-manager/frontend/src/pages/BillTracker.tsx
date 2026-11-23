import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { compressImage } from '../utils/imageCompress'
import SnapButton from '../components/SnapButton'
import UpcomingDates from '../components/UpcomingDates'
import NextActions from '../components/NextActions'
import ModuleVoiceChat from '../components/ModuleVoiceChat'
import './snaptools.css'

interface Bill { id: number; biller: string; category?: string | null; amount?: number | null; currency?: string | null; dueDate?: string | null; recurring?: string | null; notes?: string | null }

// Must match BillController.BillCategories. Fetched from the API at mount so a
// change on the server can't leave the dropdown offering a category the backend
// will reject; this list is only the fallback if that call fails.
const FALLBACK_CATEGORIES = ['utilities', 'internet', 'phone', 'insurance', 'rent', 'subscription', 'loan', 'other']

function due(d?: string | null): { label: string; cls: string } | null {
  if (!d) return null
  const days = Math.round((new Date(d).getTime() - Date.now()) / 86400000)
  if (isNaN(days)) return null
  if (days < 0) return { label: 'overdue', cls: 'expired' }
  if (days <= 7) return { label: days === 0 ? 'due today' : `due in ${days}d`, cls: 'soon' }
  return { label: `in ${days}d`, cls: 'ok' }
}

export default function BillTracker() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('bills')
  useLayoutEffect(() => { setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Bill Tracker' }]); return () => setBreadcrumbs([]) }, [])

  const [bills, setBills] = useState<Bill[]>([])
  const [loading, setLoading] = useState(true)
  const [adding, setAdding] = useState(false)
  const [error, setError] = useState('')

  const load = async () => { try { const { data } = await api.get('/bills'); setBills(data) } catch { setError('Could not load your bills.') } finally { setLoading(false) } }
  useEffect(() => { load() }, [])

  const [categories, setCategories] = useState<string[]>(FALLBACK_CATEGORIES)
  useEffect(() => {
    api.get('/bills/categories').then(({ data }) => { if (Array.isArray(data) && data.length) setCategories(data) }).catch(() => { /* fallback list stands */ })
  }, [])

  // The vision model guesses the category from a photo and is regularly wrong —
  // broadband filed as "utilities", a gym membership as "other". Optimistic
  // update because the correction should feel instant; on failure we put the
  // old value back rather than leaving the row lying about what was saved.
  const setCategory = async (b: Bill, category: string) => {
    const previous = b.category
    setBills(list => list.map(x => x.id === b.id ? { ...x, category } : x))
    try {
      await api.put(`/bills/${b.id}`, { category })
    } catch {
      setBills(list => list.map(x => x.id === b.id ? { ...x, category: previous } : x))
      setError('Could not save that category.')
    }
  }

  const onFile = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError(''); setAdding(true)
    try { const imageData = await compressImage(files[0]); const { data } = await api.post('/bills', { imageData }); setBills(x => [...x, data].sort((a, b) => (a.dueDate || '~').localeCompare(b.dueDate || '~'))) }
    catch (e: unknown) { setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not read that bill.') }
    finally { setAdding(false) }
  }
  const remove = async (b: Bill) => { try { await api.delete(`/bills/${b.id}`); setBills(l => l.filter(x => x.id !== b.id)) } catch { setError('Could not remove.') } }

  const monthlyTotal = bills.filter(b => b.recurring === 'monthly').reduce((a, b) => a + (b.amount || 0), 0)


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Bill Tracker',
    busy: adding,
    error,
    count: bills.length,
    noun: 'bills',
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-receipt-cutoff" /> Bill &amp; Subscription Tracker</h1>
        <p>Snap your bills and subscriptions to track amounts and due dates in one place — never miss a payment or forget a subscription.</p>
      </div>
      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
      <AgentStage {...stageProps} model={field} />

      <div className="st-card" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <div><strong>{bills.length}</strong> tracked{monthlyTotal > 0 ? ` · ~${monthlyTotal.toFixed(2)}/mo recurring` : ''}</div>
        <SnapButton onPick={onFile} busy={adding} label="Add a bill" />
      </div>

      {/* Ask instead of scanning the list. */}
      <ModuleVoiceChat module="bills" />

      {/* Warranties and medicines expiring soon. Bills are excluded: the
          module's own list is directly below and would repeat every row. */}
      <UpcomingDates exclude={['bill']} title="Also coming up" />

      {loading ? <div className="st-empty"><span className="st-spin" style={{ borderTopColor: '#d62976' }} /> Loading…</div>
        : bills.length === 0 ? <div className="st-empty"><PhosphorIcon className="bi bi-file-earmark-text" /><p>No bills yet — snap a utility bill, invoice, or subscription statement to start tracking.</p></div>
        : (
          <div className="st-list">
            {bills.map(b => { const d = due(b.dueDate); return (
              <div key={b.id} className="st-row-group">
              <div className="st-row">
                <span className="st-thumb st-thumb-empty"><PhosphorIcon className="bi bi-lightning-charge" /></span>
                <div className="st-row-main">
                  <strong>{b.biller}{b.amount ? ` · ${b.currency || ''}${b.amount.toFixed(2)}` : ''}</strong>
                  <small>
                    {/* Editable because the AI's guess is a starting point, not
                        a verdict — filing is the user's call. */}
                    <select
                      className="st-cat-select"
                      value={b.category || 'other'}
                      onChange={e => setCategory(b, e.target.value)}
                      aria-label={`Category for ${b.biller}`}
                    >
                      {categories.map(c => <option key={c} value={c}>{c}</option>)}
                    </select>
                    {[b.recurring, b.dueDate && `due ${b.dueDate}`].filter(Boolean).join(' · ')}
                  </small>
                </div>
                {d && <span className={`st-badge ${d.cls}`}>{d.label}</span>}
                <button className="st-del" onClick={() => remove(b)}><PhosphorIcon className="bi bi-trash" /></button>
              </div>
              {/* Extraction is only half the job — this is what to do about it. */}
              <NextActions kind="bill" id={b.id} />
              </div>
            )})}
          </div>
        )}
    </div>
  )
}
