import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useLayoutControls } from '../components/Layout'
import PackingList from '../components/PackingList'
import api from '../services/api'
import './Wardrobe.css'

interface WardrobeItem {
  id: number
  name: string | null
  category: string | null
  colors: string | null
  pattern: string | null
  style: string | null
  seasons: string | null
  formality: string | null
  condition: string | null
  careTip: string | null
  imageData: string | null
  boughtDate: string | null
  notes: string | null
  status: string | null
  createdAt: string | null
  discardedAt: string | null
}

interface Outfit {
  title: string
  itemIds?: number[]
  pieces?: string[]
  why: string
  tips?: string
  missing?: string
}

const PRESENTATIONS = [
  { value: 'feminine', label: 'Feminine', icon: 'bi-flower1' },
  { value: 'masculine', label: 'Masculine', icon: 'bi-suit-club' },
  { value: 'neutral', label: 'Neutral', icon: 'bi-circle-half' },
]

const COLOR_CHOICES = [
  { value: 'Black', swatch: '#16181d' },
  { value: 'White', swatch: '#ffffff' },
  { value: 'Grey', swatch: '#9ca3af' },
  { value: 'Navy', swatch: '#1e3a5f' },
  { value: 'Blue', swatch: '#3b82f6' },
  { value: 'Green', swatch: '#16a34a' },
  { value: 'Olive', swatch: '#708238' },
  { value: 'Beige', swatch: '#dcc9a8' },
  { value: 'Brown', swatch: '#8b5e3c' },
  { value: 'Red', swatch: '#ef4444' },
  { value: 'Burgundy', swatch: '#7f1d3a' },
  { value: 'Pink', swatch: '#ec4899' },
  { value: 'Purple', swatch: '#8b5cf6' },
  { value: 'Yellow', swatch: '#eab308' },
  { value: 'Pastels', swatch: 'linear-gradient(135deg, #fbcfe8, #bfdbfe 50%, #bbf7d0)' },
  { value: 'Earth tones', swatch: 'linear-gradient(135deg, #a16207, #78716c 50%, #4d7c0f)' },
]

const SKIN_TONES = [
  { value: 'fair', label: 'Fair', hex: '#f6ddc7' },
  { value: 'light', label: 'Light', hex: '#efc5a2' },
  { value: 'medium', label: 'Medium', hex: '#d9a06b' },
  { value: 'olive', label: 'Olive', hex: '#b98a5a' },
  { value: 'tan', label: 'Tan', hex: '#a9714b' },
  { value: 'deep', label: 'Deep', hex: '#70452c' },
]

interface SavedLook {
  id: number
  occasion: string | null
  weatherSummary: string | null
  capsule: number
  createdAt: string | null
}

interface SwatchOption { value: string; label: string; swatch?: string; icon?: string }

const OCCASION_ICONS: Record<string, string> = {
  'Everyday casual': 'bi-cup', 'Work / office': 'bi-briefcase', 'Date night': 'bi-heart',
  'Wedding / formal event': 'bi-gem', 'Party / night out': 'bi-music-note-beamed',
  'Workout': 'bi-heart-pulse', 'Travel day': 'bi-airplane', 'Interview': 'bi-person-badge',
}

// Dropdown whose options (and trigger) show a colour circle or icon next to each choice.
function SwatchDropdown({ label, options, selected, multi, onSelect }: {
  label: string
  options: SwatchOption[]
  selected: string[]
  multi: boolean
  onSelect: (value: string) => void
}) {
  const [open, setOpen] = useState(false)
  const ref = useRef<HTMLDivElement>(null)

  useEffect(() => {
    const onDoc = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', onDoc)
    return () => document.removeEventListener('mousedown', onDoc)
  }, [])

  const sel = options.filter(o => selected.includes(o.value))
  return (
    <div className="wd-dd" ref={ref}>
      <button type="button" className={`wd-dd-trigger${sel.length ? ' wd-dd-has' : ''}`} onClick={() => setOpen(o => !o)}>
        <span className="wd-dd-label">{label}</span>
        {sel.length === 0 ? (
          <span className="wd-dd-placeholder">Any</span>
        ) : !multi ? (
          <span className="wd-dd-value">
            {sel[0].swatch && <span className="wd-tone-dot" style={{ background: sel[0].swatch }} />}
            {sel[0].icon && <PhosphorIcon className={`bi ${sel[0].icon} wd-dd-opt-icon`} />}
            {sel[0].label}
          </span>
        ) : (
          <span className="wd-dd-dots">
            {sel.slice(0, 5).map(o => <span key={o.value} className="wd-tone-dot" style={{ background: o.swatch }} />)}
            {sel.length > 5 && <span className="wd-dd-more">+{sel.length - 5}</span>}
          </span>
        )}
        <PhosphorIcon className={`bi bi-chevron-${open ? 'up' : 'down'}`} />
      </button>
      {open && (
        <div className="wd-dd-menu">
          {options.map(o => {
            const active = selected.includes(o.value)
            return (
              <button
                type="button"
                key={o.value}
                className={`wd-dd-option${active ? ' wd-dd-option-active' : ''}`}
                onClick={() => { onSelect(o.value); if (!multi) setOpen(false) }}
              >
                {o.swatch && <span className="wd-tone-dot" style={{ background: o.swatch }} />}
                {o.icon && <PhosphorIcon className={`bi ${o.icon} wd-dd-opt-icon`} />}
                <span>{o.label}</span>
                {active && <PhosphorIcon className="bi bi-check-lg wd-dd-check" />}
              </button>
            )
          })}
        </div>
      )}
    </div>
  )
}

function relTime(iso: string | null): string {
  if (!iso) return ''
  const ms = Date.now() - new Date(iso).getTime()
  const mins = Math.floor(ms / 60000)
  if (mins < 60) return `${Math.max(1, mins)}m ago`
  const hrs = Math.floor(mins / 60)
  if (hrs < 24) return `${hrs}h ago`
  const days = Math.floor(hrs / 24)
  return days < 7 ? `${days}d ago` : new Date(iso).toLocaleDateString()
}

interface WeatherUsed { location: string; summary: string }

function getPosition(timeoutMs = 8000): Promise<{ lat: number; lon: number }> {
  return new Promise((resolve, reject) => {
    if (!navigator.geolocation) return reject(new Error('unsupported'))
    navigator.geolocation.getCurrentPosition(
      pos => resolve({ lat: pos.coords.latitude, lon: pos.coords.longitude }),
      err => reject(err),
      { timeout: timeoutMs, maximumAge: 10 * 60 * 1000 }
    )
  })
}

const OCCASIONS = ['Everyday casual', 'Work / office', 'Date night', 'Wedding / formal event', 'Party / night out', 'Workout', 'Travel day', 'Interview']

const CATEGORY_ICONS: Record<string, string> = {
  top: 'bi-person-arms-up', bottom: 'bi-rulers', dress: 'bi-gem', outerwear: 'bi-cloud-rain',
  shoes: 'bi-slash-square', accessory: 'bi-smartwatch', bag: 'bi-bag', other: 'bi-tag',
}

// Downscale a photo client-side so uploads stay small (~100-300KB JPEG).
function compressDataUrl(dataUrl: string, maxDim = 900, quality = 0.82): Promise<string> {
  return new Promise((resolve, reject) => {
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
    img.src = dataUrl
  })
}

function compressImage(file: File, maxDim = 900, quality = 0.82): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => compressDataUrl(reader.result as string, maxDim, quality).then(resolve, reject)
    reader.onerror = () => reject(new Error('read'))
    reader.readAsDataURL(file)
  })
}

const IS_MOBILE = typeof navigator !== 'undefined' && /Android|iPhone|iPad|iPod/i.test(navigator.userAgent)

function ageOf(boughtDate: string | null): { label: string; months: number | null } {
  if (!boughtDate) return { label: 'age unknown', months: null }
  const d = new Date(boughtDate)
  if (isNaN(d.getTime())) return { label: 'age unknown', months: null }
  const months = Math.max(0, Math.floor((Date.now() - d.getTime()) / (1000 * 60 * 60 * 24 * 30.44)))
  if (months < 1) return { label: 'new this month', months }
  if (months < 12) return { label: `${months} mo old`, months }
  const y = Math.floor(months / 12), m = months % 12
  return { label: m ? `${y}y ${m}m old` : `${y}y old`, months }
}

export default function Wardrobe() {
  const { setBreadcrumbs } = useLayoutControls()
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Wardrobe Manager', path: '/wardrobe' }, { label: 'My Wardrobe' }])
    return () => setBreadcrumbs([])
  }, [])

  const [items, setItems] = useState<WardrobeItem[]>([])
  const [loading, setLoading] = useState(true)
  const [uploading, setUploading] = useState(0) // number of photos still analyzing
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const fileRef = useRef<HTMLInputElement>(null)

  // Outfit suggester — live weather via browser location, city as fallback
  const [occasion, setOccasion] = useState(OCCASIONS[0])
  const [city, setCity] = useState('')
  const [needCity, setNeedCity] = useState(false)
  const [coords, setCoords] = useState<{ lat: number; lon: number } | null>(null)
  const [weatherUsed, setWeatherUsed] = useState<WeatherUsed | null>(null)
  const [outfits, setOutfits] = useState<Outfit[]>([])
  const [outfitLoading, setOutfitLoading] = useState(false)
  const [locating, setLocating] = useState(false)
  const [capsule, setCapsule] = useState(false)

  // Style preferences — make suggestions work even with an empty closet
  const [presentation, setPresentation] = useState('')
  const [favColors, setFavColors] = useState<string[]>([])
  const [skinTone, setSkinTone] = useState('')
  const [savedLooks, setSavedLooks] = useState<SavedLook[]>([])

  // Body profile
  const [bodyAdvice, setBodyAdvice] = useState('')
  const [bodyLoading, setBodyLoading] = useState(false)
  const bodyFileRef = useRef<HTMLInputElement>(null)

  // Photo source chooser (camera vs upload) — for clothes and body photos
  const [photoTarget, setPhotoTarget] = useState<'clothes' | 'body' | null>(null)
  const [boughtWhen, setBoughtWhen] = useState('') // yyyy-MM, applies to the batch being added
  const boughtWhenRef = useRef('')
  const [camOn, setCamOn] = useState(false)
  const [camError, setCamError] = useState('')
  const [camFlash, setCamFlash] = useState('')
  const videoRef = useRef<HTMLVideoElement>(null)
  const streamRef = useRef<MediaStream | null>(null)
  const clothesCamRef = useRef<HTMLInputElement>(null)
  const bodyCamRef = useRef<HTMLInputElement>(null)

  // Edit + discard modals
  const [editItem, setEditItem] = useState<WardrobeItem | null>(null)
  const [editDate, setEditDate] = useState('')
  const [editName, setEditName] = useState('')
  const [discardItem, setDiscardItem] = useState<WardrobeItem | null>(null)
  const [discardAdvice, setDiscardAdvice] = useState('')
  const [discardLoading, setDiscardLoading] = useState(false)

  const flash = (msg: string) => { setNotice(msg); setTimeout(() => setNotice(''), 3500) }

  const loadLooks = async () => {
    try { const { data } = await api.get('/wardrobe/outfits/history'); setSavedLooks(data) } catch { /* ignore */ }
  }

  const load = async () => {
    try {
      const [it, prof] = await Promise.all([api.get('/wardrobe/items'), api.get('/wardrobe/profile')])
      setItems(it.data)
      setBodyAdvice(prof.data?.bodyAdvice || '')
      setPresentation(prof.data?.presentation || '')
      setFavColors((prof.data?.favColors || '').split(',').map((c: string) => c.trim()).filter(Boolean))
      setSkinTone(prof.data?.skinTone || '')
    } catch { setError('Could not load your wardrobe.') }
    finally { setLoading(false) }
    loadLooks()
  }
  useEffect(() => { load() }, [])

  const saveStyle = (nextPresentation: string, nextColors: string[], nextTone: string) => {
    api.put('/wardrobe/style', { presentation: nextPresentation, favColors: nextColors.join(', '), skinTone: nextTone }).catch(() => {})
  }

  const togglePresentation = (p: string) => {
    const next = presentation === p ? '' : p
    setPresentation(next)
    saveStyle(next, favColors, skinTone)
  }

  const toggleColor = (c: string) => {
    const next = favColors.includes(c) ? favColors.filter(x => x !== c) : [...favColors, c]
    setFavColors(next)
    saveStyle(presentation, next, skinTone)
  }

  const toggleSkinTone = (t: string) => {
    const next = skinTone === t ? '' : t
    setSkinTone(next)
    saveStyle(presentation, favColors, next)
  }

  const openLook = async (id: number) => {
    setError('')
    try {
      const { data } = await api.get(`/wardrobe/outfits/history/${id}`)
      setOutfits(data.outfits || [])
      setWeatherUsed(data.weather || null)
      setCapsule(!!data.capsule)
      if (data.occasion && OCCASIONS.includes(data.occasion)) setOccasion(data.occasion)
    } catch { setError('Could not open that saved look.') }
  }

  const deleteLook = async (id: number) => {
    try { await api.delete(`/wardrobe/outfits/history/${id}`); setSavedLooks(prev => prev.filter(l => l.id !== id)) }
    catch { /* ignore */ }
  }

  const addClothesData = async (imageData: string, sourceName?: string) => {
    setUploading(u => u + 1)
    try {
      const boughtDate = boughtWhenRef.current ? `${boughtWhenRef.current}-01` : null
      const { data } = await api.post('/wardrobe/items', { imageData, boughtDate })
      // One photo can yield several garments (layers, laid-out outfits).
      // The API returns { items: [...] }; tolerate the old single-object shape.
      const added = Array.isArray(data?.items) ? data.items : data ? [data] : []
      setItems(prev => [...added, ...prev])
      if (data?.skipped > 0) setError(`Added ${added.length} item${added.length === 1 ? '' : 's'} — ${data.skipped} more detected but your wardrobe is at its limit.`)
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message
      setError(msg || `Could not add ${sourceName || 'that photo'}.`)
    } finally {
      setUploading(u => u - 1)
    }
  }

  const onFiles = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError('')
    const list = Array.from(files).slice(0, 6)
    for (const f of list) {
      try {
        const imageData = await compressImage(f)
        await addClothesData(imageData, f.name)
      } catch {
        setError(`Could not read ${f.name}.`)
      }
    }
    if (fileRef.current) fileRef.current.value = ''
    if (clothesCamRef.current) clothesCamRef.current.value = ''
  }

  // ── Camera (desktop in-modal preview; mobile uses the native camera app) ──

  const openCamera = async () => {
    setCamError('')
    if (IS_MOBILE) {
      // Native camera app gives a better experience on phones.
      ;(photoTarget === 'body' ? bodyCamRef : clothesCamRef).current?.click()
      return
    }
    try {
      const stream = await navigator.mediaDevices.getUserMedia({
        video: { facingMode: 'environment', width: { ideal: 1280 } }, audio: false,
      })
      streamRef.current = stream
      setCamOn(true)
    } catch {
      setCamError('Camera access was denied or unavailable — you can upload a photo instead.')
    }
  }

  useEffect(() => {
    if (camOn && videoRef.current && streamRef.current) videoRef.current.srcObject = streamRef.current
  }, [camOn])

  const stopCamera = () => {
    streamRef.current?.getTracks().forEach(t => t.stop())
    streamRef.current = null
    setCamOn(false)
  }

  const closePhotoModal = () => { stopCamera(); setPhotoTarget(null); setCamError(''); setCamFlash('') }
  useEffect(() => () => { streamRef.current?.getTracks().forEach(t => t.stop()) }, [])

  const captureFrame = async () => {
    const v = videoRef.current
    if (!v || !v.videoWidth) return
    const canvas = document.createElement('canvas')
    canvas.width = v.videoWidth
    canvas.height = v.videoHeight
    canvas.getContext('2d')!.drawImage(v, 0, 0)
    const raw = canvas.toDataURL('image/jpeg', 0.92)
    const compressed = await compressDataUrl(raw, photoTarget === 'body' ? 1100 : 900)
    if (photoTarget === 'body') {
      closePhotoModal()
      await analyzeBodyData(compressed)
    } else {
      // Keep the camera open so several garments can be snapped in a row.
      setCamFlash('Captured — analyzing in the background')
      setTimeout(() => setCamFlash(''), 2500)
      addClothesData(compressed)
    }
  }

  const saveEdit = async () => {
    if (!editItem) return
    try {
      await api.put(`/wardrobe/items/${editItem.id}`, { name: editName || null, boughtDate: editDate || null })
      setItems(prev => prev.map(i => i.id === editItem.id ? { ...i, name: editName || i.name, boughtDate: editDate || null } : i))
      setEditItem(null)
      flash('Saved.')
    } catch { setError('Could not save changes.') }
  }

  const openDiscard = async (item: WardrobeItem) => {
    setDiscardItem(item); setDiscardAdvice(''); setDiscardLoading(true)
    try {
      const { data } = await api.post(`/wardrobe/items/${item.id}/discard-advice`)
      setDiscardAdvice(data.advice || '')
    } catch { setDiscardAdvice('- If wearable, donate to a charity shop or clothing bank.\n- If worn out, use a textile recycling point — not general waste.') }
    finally { setDiscardLoading(false) }
  }

  const confirmDiscard = async () => {
    if (!discardItem) return
    try {
      await api.post(`/wardrobe/items/${discardItem.id}/discard`)
      setItems(prev => prev.filter(i => i.id !== discardItem.id))
      setDiscardItem(null)
      flash('Marked as discarded — nice one for doing it responsibly.')
    } catch { setError('Could not mark as discarded.') }
  }

  const deleteItem = async (item: WardrobeItem) => {
    if (!confirm(`Remove "${item.name}" from your wardrobe?`)) return
    try {
      await api.delete(`/wardrobe/items/${item.id}`)
      setItems(prev => prev.filter(i => i.id !== item.id))
    } catch { setError('Could not delete the item.') }
  }

  const suggest = async () => {
    setError('')

    // Resolve a location for live weather: browser position first, typed city as fallback.
    let pos = coords
    if (!pos && !city.trim() && !needCity) {
      setLocating(true)
      try {
        pos = await getPosition()
        setCoords(pos)
      } catch {
        // Permission denied or unavailable — ask where they are instead.
        setNeedCity(true)
        setLocating(false)
        return
      }
      setLocating(false)
    }

    setOutfitLoading(true); setOutfits([]); setWeatherUsed(null)
    try {
      const { data } = await api.post('/wardrobe/outfits', {
        occasion,
        lat: pos?.lat ?? null,
        lon: pos?.lon ?? null,
        city: city.trim() || null,
      })
      setOutfits(data.outfits || [])
      setWeatherUsed(data.weather || null)
      setCapsule(!!data.capsule)
      loadLooks()
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message
      setError(msg || 'Could not build outfits — try again.')
    } finally { setOutfitLoading(false) }
  }

  const analyzeBodyData = async (imageData: string) => {
    setBodyLoading(true); setError('')
    try {
      const { data } = await api.post('/wardrobe/body-analysis', { imageData })
      setBodyAdvice(data.bodyAdvice || '')
      flash('Styling notes updated — your photo was analyzed and immediately discarded.')
    } catch (e: unknown) {
      const msg = (e as { response?: { data?: { message?: string } } })?.response?.data?.message
      setError(msg || 'Could not analyze the photo.')
    } finally {
      setBodyLoading(false)
    }
  }

  const onBodyPhoto = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    try {
      const imageData = await compressImage(files[0], 1100)
      await analyzeBodyData(imageData)
    } catch { setError('Could not read that photo.') }
    finally {
      if (bodyFileRef.current) bodyFileRef.current.value = ''
      if (bodyCamRef.current) bodyCamRef.current.value = ''
    }
  }

  const clearBody = async () => {
    try { await api.delete('/wardrobe/profile'); setBodyAdvice(''); flash('Styling profile cleared.') }
    catch { setError('Could not clear the profile.') }
  }

  const itemById = (id: number) => items.find(i => i.id === id)
  const aging = items.filter(i => (ageOf(i.boughtDate).months ?? 0) >= 36)

  return (
    <div className="wd-page">
      <div className="wd-hero">
        <div>
          <h1><PhosphorIcon className="bi bi-stars" /> Wardrobe Intelligence</h1>
          <p>Photograph your clothes — AI catalogues them, builds outfits from what you own, tracks how old each piece is, and helps you let go of tired ones responsibly.</p>
        </div>
        <div className="wd-stats">
          <div><strong>{items.length}</strong><span>items</span></div>
          <div><strong>{aging.length}</strong><span>3+ yrs old</span></div>
        </div>
      </div>

      {notice && <div className="wd-notice"><PhosphorIcon className="bi bi-check-circle" /> {notice}</div>}
      {error && <div className="wd-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      {/* What should I wear */}
      <section className="wd-card wd-outfit-card">
        <h2><PhosphorIcon className="bi bi-magic" /> What should I wear?</h2>
        <p className="wd-muted">Suggestions are matched to the live weather where you are — I'll ask for your location, or you can just tell me your city. No clothes added yet? No problem — I'll style from your preferences below.</p>

        <div className="wd-style-prefs">
          <div className="wd-style-group">
            <span className="wd-style-label">I dress</span>
            {PRESENTATIONS.map(p => (
              <button key={p.value} className={`wd-chip${presentation === p.value ? ' wd-chip-active' : ''}`} onClick={() => togglePresentation(p.value)}>
                <PhosphorIcon className={`bi ${p.icon}`} /> {p.label}
              </button>
            ))}
          </div>
          <div className="wd-style-group">
            <SwatchDropdown
              label="Skin tone"
              options={SKIN_TONES.map(t => ({ value: t.value, label: t.label, swatch: t.hex }))}
              selected={skinTone ? [skinTone] : []}
              multi={false}
              onSelect={toggleSkinTone}
            />
            <SwatchDropdown
              label="Colours I love"
              options={COLOR_CHOICES.map(c => ({ value: c.value, label: c.value, swatch: c.swatch }))}
              selected={favColors}
              multi={true}
              onSelect={toggleColor}
            />
          </div>
        </div>

        <div className="wd-outfit-form">
          <SwatchDropdown
            label="Occasion"
            options={OCCASIONS.map(o => ({ value: o, label: o, icon: OCCASION_ICONS[o] || 'bi-tag' }))}
            selected={[occasion]}
            multi={false}
            onSelect={v => setOccasion(v)}
          />
          <input
            placeholder={needCity ? 'Where are you? e.g. Auckland' : 'City (optional — otherwise I use your location)'}
            value={city}
            onChange={e => setCity(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') suggest() }}
          />
          <button className="wd-btn wd-btn-primary" onClick={suggest} disabled={outfitLoading || locating}>
            {locating ? <><span className="wd-spin" /> Locating…</>
              : outfitLoading ? <><span className="wd-spin" /> Styling…</>
              : <><PhosphorIcon className="bi bi-stars" /> Suggest outfits</>}
          </button>
        </div>
        {needCity && !city.trim() && (
          <p className="wd-need-city"><PhosphorIcon className="bi bi-geo-alt" /> I couldn't get your location — type your city above and I'll pull the weather from there. (Or press Suggest again to skip weather.)</p>
        )}
        {items.length < 2 && !loading && <p className="wd-muted">No closet yet? I'll suggest looks from your style preferences — add your clothes below to get outfits built from what you actually own.</p>}

        {savedLooks.length > 0 && (
          <div className="wd-saved-row">
            <span className="wd-style-label">Recent looks</span>
            {savedLooks.map(l => (
              <span key={l.id} className="wd-saved-chip">
                <button className="wd-saved-open" onClick={() => openLook(l.id)} title={l.weatherSummary || ''}>
                  <PhosphorIcon className="bi bi-clock-history" /> {l.occasion || 'Look'} · {relTime(l.createdAt)}
                </button>
                <button className="wd-saved-del" title="Delete" onClick={() => deleteLook(l.id)}><PhosphorIcon className="bi bi-x" /></button>
              </span>
            ))}
          </div>
        )}

        {weatherUsed && (
          <div className="wd-weather-chip"><PhosphorIcon className="bi bi-cloud-sun" /> {weatherUsed.summary}</div>
        )}

        {outfits.length > 0 && capsule && (
          <p className="wd-capsule-note"><PhosphorIcon className="bi bi-info-circle" /> These are style ideas based on your preferences — add your clothes below and I'll build outfits from what you actually own.</p>
        )}

        {outfits.length > 0 && (
          <div className="wd-outfits">
            {outfits.map((o, i) => (
              <div key={i} className="wd-outfit">
                <h3>{o.title}</h3>
                {(o.itemIds?.length ?? 0) > 0 && (
                  <div className="wd-outfit-items">
                    {o.itemIds!.map(id => {
                      const it = itemById(id)
                      return it ? (
                        <div key={id} className="wd-outfit-item" title={it.name || ''}>
                          {it.imageData ? <img src={it.imageData} alt={it.name || ''} /> : <PhosphorIcon className={`bi ${CATEGORY_ICONS[it.category || 'other']}`} />}
                          <span>{it.name}</span>
                        </div>
                      ) : null
                    })}
                  </div>
                )}
                {(o.pieces?.length ?? 0) > 0 && (
                  <ul className="wd-outfit-pieces">
                    {o.pieces!.map((p, j) => <li key={j}>{p}</li>)}
                  </ul>
                )}
                <p className="wd-outfit-why">{o.why}</p>
                {o.tips && <p className="wd-outfit-tip"><PhosphorIcon className="bi bi-lightbulb" /> {o.tips}</p>}
                {o.missing && <p className="wd-outfit-missing"><PhosphorIcon className="bi bi-cart-plus" /> Worth adding: {o.missing}</p>}
              </div>
            ))}
          </div>
        )}

        {outfits.length > 0 && !bodyAdvice && (
          <p className="wd-profile-nudge">
            <PhosphorIcon className="bi bi-person-check" /> Want these tailored to your body shape and colouring?{' '}
            <button onClick={() => setPhotoTarget('body')}>Add a styling profile</button> — one photo, analyzed and immediately discarded.
          </p>
        )}
      </section>

      {/* Closet */}
      <section className="wd-card">
        <div className="wd-closet-head">
          <h2><PhosphorIcon className="bi bi-grid-3x3-gap" /> My closet</h2>
          <button className="wd-btn wd-btn-primary" onClick={() => { setBoughtWhen(''); boughtWhenRef.current = ''; setPhotoTarget('clothes') }}>
            {uploading > 0 ? <><span className="wd-spin" /> Analyzing {uploading} photo{uploading > 1 ? 's' : ''}…</> : <><PhosphorIcon className="bi bi-plus-lg" /> Add clothes</>}
          </button>
          <input ref={fileRef} type="file" accept="image/*" multiple hidden onChange={e => onFiles(e.target.files)} />
          <input ref={clothesCamRef} type="file" accept="image/*" capture="environment" hidden onChange={e => { onFiles(e.target.files); setPhotoTarget(null) }} />
        </div>
        <p className="wd-muted">Snap or upload photos — clear, well-lit shots work best, laid flat or hung up. Several pieces in one photo is fine; I'll catalogue each separately. Add the purchase date to track its age.</p>

        {loading ? (
          <div className="wd-loading"><span className="wd-spin" /> Loading your wardrobe…</div>
        ) : items.length === 0 ? (
          <div className="wd-empty">
            <PhosphorIcon className="bi bi-door-open" />
            <p>Your closet is empty — add your first few pieces and outfit suggestions will be built from clothes you actually own.</p>
          </div>
        ) : (
          <div className="wd-grid">
            {items.map(item => {
              const age = ageOf(item.boughtDate)
              const isAging = (age.months ?? 0) >= 36
              return (
                <div key={item.id} className="wd-item">
                  <div className="wd-item-img">
                    {item.imageData
                      ? <img src={item.imageData} alt={item.name || 'garment'} loading="lazy" />
                      : <PhosphorIcon className={`bi ${CATEGORY_ICONS[item.category || 'other']}`} />}
                    <span className={`wd-age${isAging ? ' wd-age-old' : ''}`}>{age.label}</span>
                  </div>
                  <div className="wd-item-body">
                    <strong>{item.name}</strong>
                    <div className="wd-tags">
                      {item.category && <span className="wd-tag">{item.category}</span>}
                      {item.colors && item.colors.split(',').slice(0, 3).map(c => <span key={c} className="wd-tag wd-tag-color">{c.trim()}</span>)}
                      {item.formality && <span className="wd-tag">{item.formality}</span>}
                    </div>
                    {item.careTip && <p className="wd-care"><PhosphorIcon className="bi bi-droplet" /> {item.careTip}</p>}
                    <div className="wd-item-actions">
                      <button title="Edit name & purchase date" onClick={() => { setEditItem(item); setEditName(item.name || ''); setEditDate(item.boughtDate?.slice(0, 10) || '') }}><PhosphorIcon className="bi bi-pencil" /></button>
                      <button title="Discard responsibly" onClick={() => openDiscard(item)}><PhosphorIcon className="bi bi-recycle" /></button>
                      <button title="Delete" onClick={() => deleteItem(item)}><PhosphorIcon className="bi bi-trash" /></button>
                    </div>
                  </div>
                </div>
              )
            })}
          </div>
        )}
      </section>

      {/* Pack for a trip — the packing-list endpoint, fed by typed trip details */}
      <PackingList />

      {/* Body profile — the optional upgrade, below the closet since it's not a prerequisite */}
      <section className="wd-card">
        <h2><PhosphorIcon className="bi bi-person-check" /> Personal styling profile <span className="wd-optional">optional</span></h2>
        <p className="wd-muted">
          Upload one full-body photo and get fit, colour, and proportion notes that shape every outfit suggestion.
          <strong> Your photo is analyzed in memory and immediately discarded — only the styling notes are kept.</strong>
        </p>
        {bodyAdvice ? (
          <>
            <div className="wd-body-advice">
              {bodyAdvice.split('\n').filter(l => l.trim()).map((l, i) => <p key={i}>{l.replace(/^- /, '')}</p>)}
            </div>
            <div className="wd-row-gap">
              <button className="wd-btn" onClick={() => setPhotoTarget('body')} disabled={bodyLoading}>
                {bodyLoading ? 'Analyzing…' : 'Update photo'}
              </button>
              <button className="wd-btn wd-btn-ghost" onClick={clearBody}>Clear notes</button>
            </div>
          </>
        ) : (
          <button className="wd-btn" onClick={() => setPhotoTarget('body')} disabled={bodyLoading}>
            {bodyLoading ? <><span className="wd-spin" /> Analyzing…</> : <><PhosphorIcon className="bi bi-camera" /> Add a full-body photo</>}
          </button>
        )}
        <input ref={bodyFileRef} type="file" accept="image/*" hidden onChange={e => onBodyPhoto(e.target.files)} />
        <input ref={bodyCamRef} type="file" accept="image/*" capture="environment" hidden onChange={e => { onBodyPhoto(e.target.files); setPhotoTarget(null) }} />
      </section>

      {/* Edit modal */}
      {editItem && (
        <div className="wd-modal-backdrop" onClick={() => setEditItem(null)}>
          <div className="wd-modal" onClick={e => e.stopPropagation()}>
            <button className="wd-modal-close" aria-label="Close" onClick={() => setEditItem(null)}><PhosphorIcon className="bi bi-x-lg" /></button>
            <h3><PhosphorIcon className="bi bi-pencil" /> Edit item</h3>
            <label>Name</label>
            <input value={editName} onChange={e => setEditName(e.target.value)} />
            <label>Bought on <span className="wd-muted">(used to track age)</span></label>
            <input type="date" value={editDate} onChange={e => setEditDate(e.target.value)} max={new Date().toISOString().slice(0, 10)} />
            <div className="wd-row-gap">
              <button className="wd-btn wd-btn-primary" onClick={saveEdit}>Save</button>
              <button className="wd-btn wd-btn-ghost" onClick={() => setEditItem(null)}>Cancel</button>
            </div>
          </div>
        </div>
      )}

      {/* Photo source modal — camera or upload */}
      {photoTarget && (
        <div className="wd-modal-backdrop" onClick={closePhotoModal}>
          <div className="wd-modal wd-photo-modal" onClick={e => e.stopPropagation()}>
            <button className="wd-modal-close" aria-label="Close" onClick={closePhotoModal}><PhosphorIcon className="bi bi-x-lg" /></button>
            <h3><PhosphorIcon className="bi bi-camera" /> {photoTarget === 'clothes' ? 'Add clothes' : 'Full-body photo'}</h3>
            {!camOn ? (
              <>
                <p className="wd-muted">
                  {photoTarget === 'clothes'
                    ? 'Snap your garments with the camera, or upload photos you already have. A whole rail in one shot is fine — each piece is catalogued separately.'
                    : 'Take or upload one full-body photo. It is analyzed in memory and immediately discarded — only the styling notes are kept.'}
                </p>
                {camError && <p className="wd-need-city"><PhosphorIcon className="bi bi-exclamation-triangle" /> {camError}</p>}
                {photoTarget === 'clothes' && (
                  <div className="wd-bought-when">
                    <label>When did you buy it? <span className="wd-muted">(roughly is fine — powers age tracking; applies to every photo you add now)</span></label>
                    <input
                      type="month"
                      value={boughtWhen}
                      max={new Date().toISOString().slice(0, 7)}
                      onChange={e => { setBoughtWhen(e.target.value); boughtWhenRef.current = e.target.value }}
                    />
                  </div>
                )}
                <div className="wd-photo-options">
                  <button className="wd-photo-option" onClick={openCamera}>
                    <PhosphorIcon className="bi bi-camera-fill" />
                    <strong>Take a photo</strong>
                    <span>Use your camera</span>
                  </button>
                  <button className="wd-photo-option" onClick={() => { (photoTarget === 'clothes' ? fileRef : bodyFileRef).current?.click(); setPhotoTarget(null) }}>
                    <PhosphorIcon className="bi bi-folder2-open" />
                    <strong>Upload</strong>
                    <span>Choose from your device</span>
                  </button>
                </div>
              </>
            ) : (
              <>
                <video ref={videoRef} autoPlay playsInline muted className="wd-cam" />
                {camFlash && <div className="wd-cam-flash"><PhosphorIcon className="bi bi-check-circle" /> {camFlash}</div>}
                <div className="wd-row-gap">
                  <button className="wd-btn wd-btn-primary" onClick={captureFrame}>
                    <PhosphorIcon className="bi bi-record-circle" /> {photoTarget === 'clothes' ? 'Capture garment' : 'Capture'}
                  </button>
                  <button className="wd-btn wd-btn-ghost" onClick={closePhotoModal}>{photoTarget === 'clothes' ? 'Done' : 'Cancel'}</button>
                </div>
                {photoTarget === 'clothes' && <p className="wd-muted" style={{ marginTop: 8 }}>Capture as many pieces as you like — each one is added and analyzed as you go.</p>}
              </>
            )}
          </div>
        </div>
      )}

      {/* Discard modal */}
      {discardItem && (
        <div className="wd-modal-backdrop" onClick={() => setDiscardItem(null)}>
          <div className="wd-modal" onClick={e => e.stopPropagation()}>
            <button className="wd-modal-close" aria-label="Close" onClick={() => setDiscardItem(null)}><PhosphorIcon className="bi bi-x-lg" /></button>
            <h3><PhosphorIcon className="bi bi-recycle" /> Let go of "{discardItem.name}" responsibly</h3>
            {discardLoading ? (
              <div className="wd-loading"><span className="wd-spin" /> Checking the best way to discard it…</div>
            ) : (
              <div className="wd-body-advice">
                {discardAdvice.split('\n').filter(l => l.trim()).map((l, i) => <p key={i}>{l.replace(/^- /, '')}</p>)}
              </div>
            )}
            <div className="wd-row-gap">
              <button className="wd-btn wd-btn-primary" onClick={confirmDiscard} disabled={discardLoading}>
                <PhosphorIcon className="bi bi-check-lg" /> Mark as discarded
              </button>
              <button className="wd-btn wd-btn-ghost" onClick={() => setDiscardItem(null)}>Keep it</button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
