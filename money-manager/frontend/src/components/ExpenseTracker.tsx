import PhosphorIcon from './PhosphorIcon'
import { useEffect, useRef, useState } from 'react'
import api from '../services/api'

interface Receipt {
  id: number
  merchant: string | null
  purchaseDate: string | null
  currency: string | null
  total: number | null
  tax: number | null
  paymentMethod: string | null
  category: string | null
  itemsJson: string | null
  imageData: string | null
  createdAt: string | null
}

interface ReceiptItem { name?: string; qty?: number; price?: number }

const CATEGORIES = ['groceries', 'dining', 'transport', 'fuel', 'utilities', 'shopping', 'health', 'entertainment', 'travel', 'office', 'other']

const CATEGORY_ICONS: Record<string, string> = {
  groceries: 'bi-cart3', dining: 'bi-cup-hot', transport: 'bi-bus-front', fuel: 'bi-fuel-pump',
  utilities: 'bi-lightning-charge', shopping: 'bi-bag', health: 'bi-heart-pulse',
  entertainment: 'bi-controller', travel: 'bi-airplane', office: 'bi-briefcase', other: 'bi-tag',
}

function compressImage(file: File, maxDim = 1400, quality = 0.85): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => {
      const img = new Image()
      img.onload = () => {
        const scale = Math.min(1, maxDim / Math.max(img.width, img.height))
        const canvas = document.createElement('canvas')
        canvas.width = Math.round(img.width * scale)
        canvas.height = Math.round(img.height * scale)
        const ctx = canvas.getContext('2d')
        if (!ctx) return reject(new Error('canvas'))
        ctx.drawImage(img, 0, 0, canvas.width, canvas.height)
        resolve(canvas.toDataURL('image/jpeg', quality))
      }
      img.onerror = () => reject(new Error('image'))
      img.src = reader.result as string
    }
    reader.onerror = () => reject(new Error('read'))
    reader.readAsDataURL(file)
  })
}

const money = (n: number | null | undefined, currency?: string | null) =>
  `${currency ? currency + ' ' : ''}${(n ?? 0).toFixed(2)}`

const parseItems = (json: string | null): ReceiptItem[] => {
  try { const v = JSON.parse(json || '[]'); return Array.isArray(v) ? v : [] } catch { return [] }
}

export default function ExpenseTracker() {
  const [month, setMonth] = useState(new Date().toISOString().slice(0, 7))
  const [receipts, setReceipts] = useState<Receipt[]>([])
  const [loading, setLoading] = useState(true)
  const [adding, setAdding] = useState(0)
  const [error, setError] = useState('')
  const [chooserOpen, setChooserOpen] = useState(false)
  const [viewImg, setViewImg] = useState<string | null>(null)
  const [expanded, setExpanded] = useState<number | null>(null)
  const uploadRef = useRef<HTMLInputElement>(null)
  const cameraRef = useRef<HTMLInputElement>(null)

  const load = async (m: string) => {
    setLoading(true)
    try { const { data } = await api.get(`/docagent/receipts?month=${m}`); setReceipts(data) }
    catch { setError('Could not load receipts.') }
    finally { setLoading(false) }
  }
  useEffect(() => { load(month) }, [month])

  const onFiles = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError('')
    const list = Array.from(files).slice(0, 6)
    setAdding(a => a + list.length)
    for (const f of list) {
      try {
        const imageData = await compressImage(f)
        const { data } = await api.post('/docagent/receipts', { imageData })
        if ((data.purchaseDate || '').startsWith(month)) setReceipts(prev => [data, ...prev])
        else setError(`Saved "${data.merchant}" under ${data.purchaseDate?.slice(0, 7)} — switch months to see it.`)
      } catch (e: unknown) {
        const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message
        setError(msg || `Could not read ${f.name}.`)
      } finally { setAdding(a => a - 1) }
    }
    if (uploadRef.current) uploadRef.current.value = ''
    if (cameraRef.current) cameraRef.current.value = ''
  }

  // A receipt for an appliance already holds the merchant, date and total a
  // warranty record needs. Filing it costs one API call and no AI at all.
  const [filing, setFiling] = useState<number | null>(null)
  const [filed, setFiled] = useState<Set<number>>(new Set())

  const fileAsWarranty = async (r: Receipt) => {
    setFiling(r.id)
    try {
      await api.post(`/warranty/from-receipt/${r.id}`, { warrantyMonths: 12 })
      setFiled(prev => new Set(prev).add(r.id))
    } catch {
      /* leave the button available to retry rather than reporting a wall */
    } finally {
      setFiling(null)
    }
  }

  const setCategory = async (r: Receipt, category: string) => {
    setReceipts(prev => prev.map(x => x.id === r.id ? { ...x, category } : x))
    try { await api.put(`/docagent/receipts/${r.id}`, { category }) } catch { /* keep optimistic */ }
  }

  const remove = async (r: Receipt) => {
    if (!confirm(`Delete the ${r.merchant} receipt?`)) return
    try { await api.delete(`/docagent/receipts/${r.id}`); setReceipts(prev => prev.filter(x => x.id !== r.id)) }
    catch { setError('Could not delete the receipt.') }
  }

  const exportCsv = () => {
    const esc = (v: unknown) => `"${String(v ?? '').replace(/"/g, '""')}"`
    const lines = ['Date,Merchant,Category,Currency,Total,Tax,Payment,Items']
    receipts.forEach(r => {
      const items = parseItems(r.itemsJson).map(i => `${i.qty && i.qty > 1 ? i.qty + 'x ' : ''}${i.name} ${i.price ?? ''}`.trim()).join('; ')
      lines.push([r.purchaseDate, esc(r.merchant), r.category, r.currency, r.total ?? 0, r.tax ?? 0, r.paymentMethod, esc(items)].join(','))
    })
    const url = URL.createObjectURL(new Blob([lines.join('\n')], { type: 'text/csv;charset=utf-8' }))
    const a = document.createElement('a')
    a.href = url
    a.download = `expenses-${month}.csv`
    document.body.appendChild(a)
    a.click()
    a.remove()
    URL.revokeObjectURL(url)
  }

  // Totals per currency + per category (a month can legitimately mix currencies when travelling)
  const byCurrency = new Map<string, number>()
  const byCategory = new Map<string, number>()
  receipts.forEach(r => {
    const cur = r.currency || ''
    byCurrency.set(cur, (byCurrency.get(cur) || 0) + (r.total || 0))
    byCategory.set(r.category || 'other', (byCategory.get(r.category || 'other') || 0) + (r.total || 0))
  })
  const catRows = [...byCategory.entries()].sort((a, b) => b[1] - a[1])
  const maxCat = catRows.length ? catRows[0][1] : 0

  const monthLabel = new Date(month + '-01T00:00:00').toLocaleDateString('en-US', { month: 'long', year: 'numeric' })

  return (
    <div className="exp-wrap">
      <div className="exp-toolbar">
        <div className="exp-month">
          <button onClick={() => { const d = new Date(month + '-01T00:00:00'); d.setMonth(d.getMonth() - 1); setMonth(d.toISOString().slice(0, 7)) }} aria-label="Previous month"><PhosphorIcon className="bi bi-chevron-left" /></button>
          <input type="month" value={month} max={new Date().toISOString().slice(0, 7)} onChange={e => e.target.value && setMonth(e.target.value)} />
          <button onClick={() => { const d = new Date(month + '-01T00:00:00'); d.setMonth(d.getMonth() + 1); setMonth(d.toISOString().slice(0, 7)) }} aria-label="Next month"><PhosphorIcon className="bi bi-chevron-right" /></button>
        </div>
        <div className="exp-toolbar-actions">
          <button className="exp-btn" onClick={exportCsv} disabled={receipts.length === 0}><PhosphorIcon className="bi bi-filetype-csv" /> Export CSV</button>
          <button className="exp-btn exp-btn-primary" onClick={() => setChooserOpen(true)}>
            {adding > 0 ? <><span className="doc-spinner" /> Reading {adding}…</> : <><PhosphorIcon className="bi bi-camera" /> Snap receipt</>}
          </button>
        </div>
        <input ref={uploadRef} type="file" accept="image/*" multiple hidden onChange={e => onFiles(e.target.files)} />
        <input ref={cameraRef} type="file" accept="image/*" capture="environment" hidden onChange={e => onFiles(e.target.files)} />
      </div>

      <p className="exp-note"><PhosphorIcon className="bi bi-info-circle" /> Snapped receipts are <strong>saved to your account</strong> (photo + extracted data) until you delete them — unlike the zero-storage document analysis tab.</p>

      {error && <div className="doc-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      <div className="exp-summary">
        <div className="exp-tile">
          <span>{monthLabel}</span>
          <strong>{[...byCurrency.entries()].map(([c, t]) => money(t, c)).join(' · ') || '0.00'}</strong>
          <small>{receipts.length} receipt{receipts.length === 1 ? '' : 's'}</small>
        </div>
        <div className="exp-cats">
          {catRows.length === 0 && <span className="exp-cats-empty">Category breakdown appears here.</span>}
          {catRows.slice(0, 6).map(([cat, total]) => (
            <div key={cat} className="exp-cat-row">
              <PhosphorIcon className={`bi ${CATEGORY_ICONS[cat] || 'bi-tag'}`} />
              <span className="exp-cat-name">{cat}</span>
              <div className="exp-cat-bar"><div style={{ width: `${maxCat ? Math.max(6, (total / maxCat) * 100) : 0}%` }} /></div>
              <span className="exp-cat-total">{total.toFixed(2)}</span>
            </div>
          ))}
        </div>
      </div>

      {loading ? (
        <div className="exp-loading"><span className="doc-spinner" /> Loading receipts…</div>
      ) : receipts.length === 0 ? (
        <div className="exp-empty">
          <PhosphorIcon className="bi bi-receipt-cutoff" />
          <p>No receipts for {monthLabel} — snap your first one and it's itemised, categorised, and totted up automatically.</p>
        </div>
      ) : (
        <div className="exp-list">
          {receipts.map(r => {
            const items = parseItems(r.itemsJson)
            const open = expanded === r.id
            return (
              <div key={r.id} className="exp-receipt">
                <div className="exp-receipt-row">
                  {r.imageData
                    ? <img src={r.imageData} alt="" className="exp-thumb" onClick={() => setViewImg(r.imageData)} />
                    : <span className="exp-thumb exp-thumb-empty"><PhosphorIcon className="bi bi-receipt" /></span>}
                  <div className="exp-receipt-main">
                    <strong>{r.merchant}</strong>
                    <small>{r.purchaseDate}{r.paymentMethod ? ` · ${r.paymentMethod}` : ''}</small>
                  </div>
                  <select className="exp-cat-select" value={r.category || 'other'} onChange={e => setCategory(r, e.target.value)}>
                    {CATEGORIES.map(c => <option key={c} value={c}>{c}</option>)}
                  </select>
                  <span className="exp-total">{money(r.total, r.currency)}</span>
                  {items.length > 0 && (
                    <button className="exp-icon-btn" title="Items" onClick={() => setExpanded(open ? null : r.id)}>
                      <PhosphorIcon className={`bi bi-chevron-${open ? 'up' : 'down'}`} />
                    </button>
                  )}
                  <button
                    className="exp-icon-btn"
                    title={filed.has(r.id) ? 'Filed under warranties' : 'File as a warranty'}
                    disabled={filing === r.id || filed.has(r.id)}
                    onClick={() => fileAsWarranty(r)}
                  >
                    <PhosphorIcon className={`bi ${filed.has(r.id) ? 'bi-shield-check' : 'bi-shield-plus'}`} />
                  </button>
                  <button className="exp-icon-btn exp-del" title="Delete" onClick={() => remove(r)}><PhosphorIcon className="bi bi-trash" /></button>
                </div>
                {open && items.length > 0 && (
                  <ul className="exp-items">
                    {items.map((it, i) => (
                      <li key={i}>
                        <span>{it.qty && it.qty > 1 ? `${it.qty}× ` : ''}{it.name}</span>
                        <span>{it.price != null ? it.price.toFixed(2) : ''}</span>
                      </li>
                    ))}
                    {(r.tax ?? 0) > 0 && <li className="exp-item-tax"><span>Tax</span><span>{(r.tax ?? 0).toFixed(2)}</span></li>}
                  </ul>
                )}
              </div>
            )
          })}
        </div>
      )}

      {/* Snap chooser — camera or upload */}
      {chooserOpen && (
        <div className="exp-modal-backdrop" onClick={() => setChooserOpen(false)}>
          <div className="exp-modal" onClick={e => e.stopPropagation()}>
            <button className="exp-modal-close" aria-label="Close" onClick={() => setChooserOpen(false)}><PhosphorIcon className="bi bi-x-lg" /></button>
            <h3><PhosphorIcon className="bi bi-receipt" /> Add receipts</h3>
            <p className="exp-note" style={{ margin: '0 0 12px' }}>One receipt per photo works best — totals, items, and category are extracted automatically.</p>
            <div className="exp-choices">
              <button className="exp-choice" onClick={() => { cameraRef.current?.click(); setChooserOpen(false) }}>
                <PhosphorIcon className="bi bi-camera-fill" /><strong>Take a photo</strong><span>Use your camera</span>
              </button>
              <button className="exp-choice" onClick={() => { uploadRef.current?.click(); setChooserOpen(false) }}>
                <PhosphorIcon className="bi bi-folder2-open" /><strong>Upload</strong><span>Choose from your device</span>
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Receipt image viewer */}
      {viewImg && (
        <div className="exp-modal-backdrop" onClick={() => setViewImg(null)}>
          <div className="exp-modal exp-modal-img" onClick={e => e.stopPropagation()}>
            <button className="exp-modal-close" aria-label="Close" onClick={() => setViewImg(null)}><PhosphorIcon className="bi bi-x-lg" /></button>
            <img src={viewImg} alt="Receipt" />
          </div>
        </div>
      )}
    </div>
  )
}
