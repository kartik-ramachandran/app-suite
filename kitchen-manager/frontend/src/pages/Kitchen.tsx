import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import RecipeLibrary from '../components/RecipeLibrary'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import ModuleVoiceChat from '../components/ModuleVoiceChat'
import './Kitchen.css'

interface PantryItem {
  id: number; name: string; category: string
  caloriesPer100g?: number | null; macros?: string | null; servingNote?: string | null; quantity?: string | null
}
interface FoodLookup {
  name: string; category: string; caloriesPer100g: number
  protein: number; carbs: number; fat: number; servingNote: string; healthNote: string
}
interface Recipe {
  title: string; cuisine?: string; minutes?: number; spice?: string; calories?: number
  healthNote?: string; uses?: string[]; missing?: string[]; steps?: string[]
}
interface PlateResult {
  id?: number | null; title: string; calories: number; protein: number; carbs: number; fat: number
  confidence: string; healthNote?: string; dishesJson?: string; logged?: boolean
}
interface Meal {
  id: number; loggedDate: string; title: string; calories: number; protein: number
  carbs: number; fat: number; itemsJson: string; healthNote: string; imageData: string | null
}
interface ShoppingItem { id: number; name: string; checked: number }

const CUISINES = ['Italian', 'Indian', 'Chinese', 'Thai', 'Japanese', 'Mexican', 'Mediterranean', 'American', 'French', 'Korean', 'Middle Eastern', 'Vietnamese']
const SPICE_LEVELS = [
  { value: 'mild', label: 'Mild', icon: '🌶️' },
  { value: 'medium', label: 'Medium', icon: '🌶️🌶️' },
  { value: 'hot', label: 'Hot', icon: '🌶️🌶️🌶️' },
]
const DIET_GOALS = ['balanced', 'high-protein', 'low-carb', 'weight-loss', 'vegetarian', 'vegan', 'keto']

const CATEGORY_ICONS: Record<string, string> = {
  produce: 'bi-flower3', dairy: 'bi-egg', meat: 'bi-egg-fried', seafood: 'bi-water',
  grains: 'bi-basket', legumes: 'bi-circle', condiment: 'bi-droplet', spice: 'bi-fire',
  baking: 'bi-cookie', frozen: 'bi-snow', beverage: 'bi-cup-straw', other: 'bi-bag',
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

const spiceDots = (s?: string) => s === 'hot' ? '🌶️🌶️🌶️' : s === 'medium' ? '🌶️🌶️' : s === 'mild' ? '🌶️' : ''

// Compact pill dropdown (single or multi select), optionally with an "add your own" field.
function KitDropdown({ label, options, selected, multi, onSelect, allowCustom, placeholder = 'Any' }: {
  label: string; options: string[]; selected: string[]; multi: boolean
  onSelect: (v: string) => void; allowCustom?: boolean; placeholder?: string
}) {
  const [open, setOpen] = useState(false)
  const [custom, setCustom] = useState('')
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const onDoc = (e: MouseEvent) => { if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false) }
    document.addEventListener('mousedown', onDoc)
    return () => document.removeEventListener('mousedown', onDoc)
  }, [])
  const allOpts = Array.from(new Set([...options, ...selected]))
  const addCustom = () => { const v = custom.trim(); if (v) { onSelect(v); setCustom('') } }
  const summary = selected.length === 0
    ? <span className="kit-dd-any">{placeholder}</span>
    : multi
      ? (selected.length <= 2 ? selected.join(', ') : `${selected.slice(0, 2).join(', ')} +${selected.length - 2}`)
      : selected[0]
  return (
    <div className="kit-dd" ref={ref}>
      <button type="button" className={`kit-dd-trigger${selected.length ? ' has' : ''}`} onClick={() => setOpen(o => !o)}>
        <span className="kit-dd-label">{label}</span>
        <span className="kit-dd-value">{summary}</span>
        <PhosphorIcon className={`bi bi-chevron-${open ? 'up' : 'down'}`} />
      </button>
      {open && (
        <div className="kit-dd-menu">
          {allOpts.map(o => {
            const active = selected.includes(o)
            return (
              <button key={o} type="button" className={`kit-dd-opt${active ? ' active' : ''}`} onClick={() => { onSelect(o); if (!multi) setOpen(false) }}>
                <span>{o}</span>{active && <PhosphorIcon className="bi bi-check-lg" />}
              </button>
            )
          })}
          {allowCustom && (
            <div className="kit-dd-custom">
              <input placeholder="Add a specific style…" value={custom} onChange={e => setCustom(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addCustom() } }} />
              <button type="button" onClick={addCustom} aria-label="Add"><PhosphorIcon className="bi bi-plus-lg" /></button>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

export default function Kitchen() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('kitchen')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Kitchen Intelligence' }])
    return () => setBreadcrumbs([])
  }, [])

  // 'recipes' is deep-linkable (?tab=recipes) because the cookbook import
  // banner in Book Reader links straight here, and /recipes redirects in.
  // Suggestions are generated fresh each time and lost on refresh. Saving one
  // moves it into the real library, where it persists and can be matched
  // against the pantry later.
  const [savedIdx, setSavedIdx] = useState<Set<number>>(new Set())
  const [savingIdx, setSavingIdx] = useState<number | null>(null)

  const saveSuggestion = async (r: Recipe, i: number) => {
    setSavingIdx(i)
    try {
      await api.post('/recipes', {
        title: r.title,
        cuisine: r.cuisine,
        totalMinutes: r.minutes,
        // A suggestion names what it uses and what is missing; together those
        // are its ingredient list.
        ingredients: [...(r.uses ?? []), ...(r.missing ?? [])].map(name => ({ name })),
        steps: r.steps ?? [],
        source: 'generated',
        sourceLabel: 'Suggested from your pantry',
      })
      setSavedIdx(prev => new Set(prev).add(i))
      flash('Saved to your recipes.')
    } catch {
      setError('Could not save that recipe.')
    } finally {
      setSavingIdx(null)
    }
  }

  const [view, setView] = useState<'cook' | 'plate' | 'recipes'>(
    () => (new URLSearchParams(window.location.search).get('tab') === 'recipes' ? 'recipes' : 'cook'))
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const flash = (m: string) => { setNotice(m); setTimeout(() => setNotice(''), 3500) }

  // Preferences
  const [cuisines, setCuisines] = useState<string[]>([])
  const [spiceLevel, setSpiceLevel] = useState('')
  const [dietGoal, setDietGoal] = useState('')
  const [allergies, setAllergies] = useState('')

  // Pantry + recipes
  const [pantry, setPantry] = useState<PantryItem[]>([])
  const [manualItem, setManualItem] = useState('')
  const [snapping, setSnapping] = useState(false)

  // Add-item modal with nutrition lookup
  const [addOpen, setAddOpen] = useState(false)
  const [lookupQuery, setLookupQuery] = useState('')
  const [looking, setLooking] = useState(false)
  const [lookup, setLookup] = useState<FoodLookup | null>(null)
  const [lookupError, setLookupError] = useState('')
  const [addingItem, setAddingItem] = useState(false)
  const [qtyInput, setQtyInput] = useState('')
  const [recipes, setRecipes] = useState<Recipe[]>([])
  const [recipeLoading, setRecipeLoading] = useState(false)
  const [openRecipe, setOpenRecipe] = useState<number | null>(null)

  // Shopping list
  const [shopping, setShopping] = useState<ShoppingItem[]>([])
  const [shopItem, setShopItem] = useState('')

  // Plate check
  const [logMeal, setLogMeal] = useState(true)
  const [plateLoading, setPlateLoading] = useState(false)
  const [plate, setPlate] = useState<PlateResult | null>(null)
  const [mealDate, setMealDate] = useState(new Date().toISOString().slice(0, 10))
  const [meals, setMeals] = useState<Meal[]>([])

  // Photo chooser
  const [chooser, setChooser] = useState<'pantry' | 'plate' | null>(null)
  const pantryUpload = useRef<HTMLInputElement>(null)
  const pantryCamera = useRef<HTMLInputElement>(null)
  const plateUpload = useRef<HTMLInputElement>(null)
  const plateCamera = useRef<HTMLInputElement>(null)

  useEffect(() => {
    (async () => {
      try {
        const [pr, pan, shop] = await Promise.all([api.get('/kitchen/prefs'), api.get('/kitchen/pantry'), api.get('/kitchen/shopping')])
        setCuisines((pr.data.cuisines || '').split(',').map((c: string) => c.trim()).filter(Boolean))
        setSpiceLevel(pr.data.spiceLevel || '')
        setDietGoal(pr.data.dietGoal || '')
        setAllergies(pr.data.allergies || '')
        setPantry(pan.data)
        setShopping(shop.data)
      } catch { /* ignore */ }
    })()
  }, [])

  useEffect(() => { loadMeals(mealDate) }, [mealDate])
  const loadMeals = async (d: string) => {
    try { const { data } = await api.get(`/kitchen/meals?date=${d}`); setMeals(data) } catch { /* ignore */ }
  }

  const savePrefs = (next: Partial<{ cuisines: string[]; spiceLevel: string; dietGoal: string; allergies: string }>) => {
    const body = {
      cuisines: (next.cuisines ?? cuisines).join(', '),
      spiceLevel: next.spiceLevel ?? spiceLevel,
      dietGoal: next.dietGoal ?? dietGoal,
      allergies: next.allergies ?? allergies,
    }
    api.put('/kitchen/prefs', body).catch(() => {})
  }

  const toggleCuisine = (c: string) => {
    const next = cuisines.includes(c) ? cuisines.filter(x => x !== c) : [...cuisines, c]
    setCuisines(next); savePrefs({ cuisines: next })
  }
  const pickSpice = (s: string) => { const v = spiceLevel === s ? '' : s; setSpiceLevel(v); savePrefs({ spiceLevel: v }) }
  const pickGoal = (g: string) => { const v = dietGoal === g ? '' : g; setDietGoal(v); savePrefs({ dietGoal: v }) }

  const onPantryFiles = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError(''); setSnapping(true)
    try {
      for (const f of Array.from(files).slice(0, 4)) {
        const imageData = await compressImage(f, 1300)
        const { data } = await api.post('/kitchen/pantry/snap', { imageData })
        if (data.pantry) setPantry(data.pantry)
        flash(data.added?.length ? `Added ${data.added.length} item${data.added.length === 1 ? '' : 's'} to your pantry.` : (data.message || 'No new items found.'))
      }
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not read that photo.')
    } finally {
      setSnapping(false)
      if (pantryUpload.current) pantryUpload.current.value = ''
      if (pantryCamera.current) pantryCamera.current.value = ''
    }
  }

  // "Add" opens the lookup modal (prefilled + auto-searched when something was typed).
  const openAdd = () => {
    const q = manualItem.trim()
    setLookup(null); setLookupError(''); setLookupQuery(q); setQtyInput(''); setAddOpen(true)
    if (q) runLookup(q)
  }

  const runLookup = async (q?: string) => {
    const name = (q ?? lookupQuery).trim()
    if (!name) return
    setLooking(true); setLookup(null); setLookupError('')
    try {
      const { data } = await api.post('/kitchen/pantry/lookup', { name })
      setLookup(data)
    } catch (e: unknown) {
      setLookupError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Lookup failed — you can still add it without details.')
    } finally { setLooking(false) }
  }

  const confirmAdd = async (withDetails: boolean) => {
    const name = withDetails && lookup ? lookup.name : lookupQuery.trim()
    if (!name) return
    setAddingItem(true)
    try {
      const quantity = qtyInput.trim() || null
      const body = withDetails && lookup
        ? {
            name: lookup.name, category: lookup.category, caloriesPer100g: lookup.caloriesPer100g,
            macros: `P ${lookup.protein}g · C ${lookup.carbs}g · F ${lookup.fat}g per 100g`,
            servingNote: lookup.servingNote, quantity,
          }
        : { name, quantity }
      const { data } = await api.post('/kitchen/pantry', body)
      setPantry(prev => [...prev, data])
      setManualItem(''); setAddOpen(false)
      flash(`Added ${name} to your pantry.`)
    } catch { setLookupError('Could not add that item.') }
    finally { setAddingItem(false) }
  }

  const removeItem = async (id: number) => {
    setPantry(prev => prev.filter(p => p.id !== id))
    try { await api.delete(`/kitchen/pantry/${id}`) } catch { /* keep optimistic */ }
  }

  const clearPantry = async () => {
    if (!confirm('Clear your whole pantry?')) return
    try { await api.delete('/kitchen/pantry'); setPantry([]); setRecipes([]) } catch { setError('Could not clear the pantry.') }
  }

  const suggestRecipes = async () => {
    setRecipeLoading(true); setError(''); setRecipes([])
    try {
      const { data } = await api.post('/kitchen/recipes')
      // ready-to-cook (nothing missing) first
      const list: Recipe[] = (data.recipes || []).slice().sort((a: Recipe, b: Recipe) => (a.missing?.length ?? 0) - (b.missing?.length ?? 0))
      setRecipes(list)
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not build recipes — try again.')
    } finally { setRecipeLoading(false) }
  }

  const onPlateFiles = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError(''); setPlateLoading(true); setPlate(null)
    try {
      const imageData = await compressImage(files[0], 1400)
      const { data } = await api.post('/kitchen/plate', { imageData, log: logMeal, date: mealDate })
      setPlate(data)
      if (data.logged) loadMeals(mealDate)
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not read that plate.')
    } finally {
      setPlateLoading(false)
      if (plateUpload.current) plateUpload.current.value = ''
      if (plateCamera.current) plateCamera.current.value = ''
    }
  }

  const deleteMeal = async (id: number) => {
    setMeals(prev => prev.filter(m => m.id !== id))
    try { await api.delete(`/kitchen/meals/${id}`) } catch { /* ignore */ }
  }

  // Shopping list
  const addShopping = async (names: string[]) => {
    const clean = names.map(n => n.trim()).filter(Boolean)
    if (!clean.length) return
    try { const { data } = await api.post('/kitchen/shopping', { names: clean }); setShopping(data) } catch { setError('Could not add to the list.') }
  }
  const addShopManual = async () => { const n = shopItem.trim(); if (!n) return; await addShopping([n]); setShopItem('') }
  const toggleShopping = async (it: ShoppingItem) => {
    const checked = it.checked ? 0 : 1
    setShopping(prev => prev.map(s => s.id === it.id ? { ...s, checked } : s).sort((a, b) => a.checked - b.checked || a.id - b.id))
    try { await api.put(`/kitchen/shopping/${it.id}`, { checked: !!checked }) } catch { /* keep optimistic */ }
  }
  const removeShopping = async (id: number) => {
    setShopping(prev => prev.filter(s => s.id !== id))
    try { await api.delete(`/kitchen/shopping/${id}`) } catch { /* ignore */ }
  }
  const clearShopping = async (all: boolean) => {
    try { await api.post(`/kitchen/shopping/clear?all=${all}`); setShopping(prev => all ? [] : prev.filter(s => !s.checked)) } catch { /* ignore */ }
  }
  const shopHas = (name: string) => shopping.some(s => s.name.toLowerCase() === name.toLowerCase())

  const dayTotals = meals.reduce((a, m) => ({
    cal: a.cal + (m.calories || 0), p: a.p + (m.protein || 0), c: a.c + (m.carbs || 0), f: a.f + (m.fat || 0),
  }), { cal: 0, p: 0, c: 0, f: 0 })

  // Group pantry by category for display
  const grouped = pantry.reduce((acc, it) => {
    (acc[it.category] ||= []).push(it); return acc
  }, {} as Record<string, PantryItem[]>)

  const readyCount = recipes.filter(r => !r.missing?.length).length


  // Gamefield: the crew this page's work actually passes through.
  const field = useSnapFieldModel({
    title: 'Kitchen Intelligence',
    busy: snapping || addingItem,
    error,
    count: pantry.length,
    noun: 'pantry items',
  })

  return (
    <div className="kit-page">
      <div className="kit-hero">
        <div>
          <h1><PhosphorIcon className="bi bi-egg-fried" /> Kitchen Intelligence</h1>
          <p>Snap your fridge or groceries and cook from what you already have — tuned to your cuisines, spice level, and health goal. Snap a plate to estimate its calories and macros.</p>
        </div>
        <div className="kit-stats">
          <div><strong>{pantry.length}</strong><span>in pantry</span></div>
          <div><strong>{Math.round(dayTotals.cal)}</strong><span>kcal today</span></div>
        </div>
      </div>
        {/* Ask a question instead of clicking through forms — the assistant
            answers from what this module already knows about you. */}
        <ModuleVoiceChat module="kitchen" />

      <AgentStage {...stageProps} model={field} />

      <div className="kit-tabs">
        <button className={`kit-tab${view === 'cook' ? ' active' : ''}`} onClick={() => setView('cook')}><PhosphorIcon className="bi bi-basket3" /> Cook from my kitchen</button>
        <button className={`kit-tab${view === 'recipes' ? ' active' : ''}`} onClick={() => setView('recipes')}><PhosphorIcon className="bi bi-journal-richtext" /> My recipes</button>
        <button className={`kit-tab${view === 'plate' ? ' active' : ''}`} onClick={() => setView('plate')}><PhosphorIcon className="bi bi-camera" /> Plate check</button>
      </div>

      {notice && <div className="kit-notice"><PhosphorIcon className="bi bi-check-circle" /> {notice}</div>}
      {error && <div className="kit-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      {view === 'cook' && (
        <>
          {/* Preferences */}
          <section className="kit-card">
            <h2><PhosphorIcon className="bi bi-sliders" /> Taste &amp; health</h2>
            <div className="kit-pref-row">
              <KitDropdown label="Cuisines" options={CUISINES} selected={cuisines} multi allowCustom onSelect={toggleCuisine} />
              <KitDropdown label="Health goal" options={DIET_GOALS} selected={dietGoal ? [dietGoal] : []} multi={false} onSelect={pickGoal} placeholder="Balanced" />
            </div>
            <div className="kit-pref-group">
              <span className="kit-pref-label">Spice level</span>
              <div className="kit-chips">
                {SPICE_LEVELS.map(s => (
                  <button key={s.value} className={`kit-chip${spiceLevel === s.value ? ' on' : ''}`} onClick={() => pickSpice(s.value)}>{s.icon} {s.label}</button>
                ))}
              </div>
            </div>
            {cuisines.length > 0 && (
              <div className="kit-pref-group">
                <span className="kit-pref-label">Picked</span>
                <div className="kit-chips">
                  {cuisines.map(c => (
                    <span key={c} className="kit-item-chip">{c}<button onClick={() => toggleCuisine(c)} aria-label="Remove"><PhosphorIcon className="bi bi-x" /></button></span>
                  ))}
                </div>
              </div>
            )}
            <div className="kit-pref-group">
              <span className="kit-pref-label">Allergies / avoid</span>
              <input className="kit-input" placeholder="e.g. peanuts, shellfish, gluten (never suggested)" value={allergies}
                onChange={e => setAllergies(e.target.value)} onBlur={() => savePrefs({})} />
            </div>
          </section>

          {/* Pantry */}
          <section className="kit-card">
            <div className="kit-card-head">
              <h2><PhosphorIcon className="bi bi-basket3" /> My pantry <span className="kit-count">{pantry.length}</span></h2>
              <div className="kit-head-actions">
                {pantry.length > 0 && <button className="kit-btn kit-btn-ghost" onClick={clearPantry}>Clear</button>}
                <button className="kit-btn kit-btn-primary" onClick={() => setChooser('pantry')} disabled={snapping}>
                  {snapping ? <><span className="kit-spin" /> Reading…</> : <><PhosphorIcon className="bi bi-camera" /> Snap groceries</>}
                </button>
              </div>
            </div>

            <div className="kit-manual">
              <input className="kit-input" placeholder="Add an item by hand — I'll fetch its calories too…" value={manualItem}
                onChange={e => setManualItem(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') openAdd() }} />
              <button className="kit-btn" onClick={openAdd}><PhosphorIcon className="bi bi-plus-lg" /> Add</button>
            </div>

            {pantry.length === 0 ? (
              <div className="kit-empty"><PhosphorIcon className="bi bi-basket" /><p>Your pantry is empty — snap your fridge or groceries and I'll list everything I can see.</p></div>
            ) : (
              <div className="kit-pantry">
                {Object.keys(grouped).sort().map(cat => (
                  <div key={cat} className="kit-pantry-cat">
                    <span className="kit-cat-label"><PhosphorIcon className={`bi ${CATEGORY_ICONS[cat] || 'bi-bag'}`} /> {cat}</span>
                    <div className="kit-item-chips">
                      {grouped[cat].map(it => (
                        <span key={it.id} className="kit-item-chip" title={it.servingNote || (it.caloriesPer100g ? `~${Math.round(it.caloriesPer100g)} kcal per 100g` : '')}>
                          {it.name}
                          {it.quantity ? <em className="kit-chip-qty">{it.quantity}</em> : null}
                          {it.caloriesPer100g ? <em className="kit-chip-kcal">{Math.round(it.caloriesPer100g)} kcal</em> : null}
                          <button onClick={() => removeItem(it.id)} aria-label="Remove"><PhosphorIcon className="bi bi-x" /></button>
                        </span>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            )}

            <button className="kit-btn kit-btn-primary kit-suggest" onClick={suggestRecipes} disabled={recipeLoading || pantry.length < 2}>
              {recipeLoading ? <><span className="kit-spin" /> Finding recipes…</> : <><PhosphorIcon className="bi bi-stars" /> What can I make?</>}
            </button>
            {pantry.length < 2 && <p className="kit-muted">Add at least a couple of ingredients to get recipe ideas.</p>}
          </section>

          {/* Shopping list */}
          <section className="kit-card">
            <div className="kit-card-head">
              <h2><PhosphorIcon className="bi bi-cart3" /> Shopping list {shopping.length > 0 && <span className="kit-count">{shopping.filter(s => !s.checked).length} to buy</span>}</h2>
              {shopping.length > 0 && (
                <div className="kit-head-actions">
                  {shopping.some(s => s.checked) && <button className="kit-btn kit-btn-ghost" onClick={() => clearShopping(false)}>Clear ticked</button>}
                  <button className="kit-btn kit-btn-ghost" onClick={() => clearShopping(true)}>Clear all</button>
                </div>
              )}
            </div>
            <div className="kit-manual">
              <input className="kit-input" placeholder="Add something to buy…" value={shopItem}
                onChange={e => setShopItem(e.target.value)} onKeyDown={e => { if (e.key === 'Enter') addShopManual() }} />
              <button className="kit-btn" onClick={addShopManual}><PhosphorIcon className="bi bi-plus-lg" /> Add</button>
            </div>
            {shopping.length === 0 ? (
              <p className="kit-muted">Nothing on your list yet — add items here, or tap "need to buy" ingredients on any recipe below.</p>
            ) : (
              <ul className="kit-shop-list">
                {shopping.map(s => (
                  <li key={s.id} className={s.checked ? 'done' : ''}>
                    <label>
                      <input type="checkbox" checked={!!s.checked} onChange={() => toggleShopping(s)} />
                      <span>{s.name}</span>
                    </label>
                    <button className="kit-shop-del" onClick={() => removeShopping(s.id)} aria-label="Remove"><PhosphorIcon className="bi bi-x" /></button>
                  </li>
                ))}
              </ul>
            )}
          </section>

          {/* Recipes */}
          {recipes.length > 0 && (
            <section className="kit-card">
              <h2><PhosphorIcon className="bi bi-journal-richtext" /> Recipes {readyCount > 0 && <span className="kit-ready-badge">{readyCount} ready with what you have</span>}</h2>
              <div className="kit-recipes">
                {recipes.map((r, i) => {
                  const ready = !r.missing?.length
                  const open = openRecipe === i
                  return (
                    <div key={i} className={`kit-recipe${ready ? ' ready' : ''}`}>
                      <div className="kit-recipe-head" onClick={() => setOpenRecipe(open ? null : i)}>
                        <div>
                          <strong>{r.title}</strong>
                          <div className="kit-recipe-meta">
                            {r.cuisine && <span>{r.cuisine}</span>}
                            {r.minutes ? <span><PhosphorIcon className="bi bi-clock" /> {r.minutes}m</span> : null}
                            {r.spice && <span>{spiceDots(r.spice)}</span>}
                            {r.calories ? <span><PhosphorIcon className="bi bi-fire" /> ~{r.calories} kcal</span> : null}
                          </div>
                        </div>
                        {ready ? <span className="kit-tag-ready">Ready</span> : <span className="kit-tag-need">Need {r.missing!.length}</span>}
                      </div>
                      {r.healthNote && <p className="kit-recipe-health"><PhosphorIcon className="bi bi-heart-pulse" /> {r.healthNote}</p>}
                      {!ready && (
                        <div className="kit-need-row">
                          <span>Need to buy:</span>
                          {r.missing!.map((m, k) => (
                            <button key={k} className={`kit-need-chip${shopHas(m) ? ' added' : ''}`} onClick={() => addShopping([m])} title={shopHas(m) ? 'On your list' : 'Add to shopping list'}>
                              {shopHas(m) ? <PhosphorIcon className="bi bi-check2" /> : <PhosphorIcon className="bi bi-plus" />} {m}
                            </button>
                          ))}
                          <button className="kit-add-all" onClick={() => addShopping(r.missing!)}><PhosphorIcon className="bi bi-cart-plus" /> Add all</button>
                        </div>
                      )}
                      {open && (
                        <div className="kit-recipe-body">
                          {r.uses?.length ? <p className="kit-uses">Uses: {r.uses.join(', ')}</p> : null}
                          <ol className="kit-steps">{r.steps?.map((s, k) => <li key={k}>{s}</li>)}</ol>
                          {/* Without this the suggestion vanishes on refresh. */}
                          <button
                            className="kit-save-recipe"
                            onClick={() => saveSuggestion(r, i)}
                            disabled={savedIdx.has(i) || savingIdx === i}
                          >
                            <PhosphorIcon className={savedIdx.has(i) ? 'bi bi-check-circle' : 'bi bi-bookmark'} />
                            {savedIdx.has(i) ? 'Saved to my recipes' : savingIdx === i ? 'Saving…' : 'Keep this recipe'}
                          </button>
                        </div>
                      )}
                    </div>
                  )
                })}
              </div>
            </section>
          )}
        </>
      )}

      {view === 'recipes' && <RecipeLibrary />}

      {view === 'plate' && (
        <>
          <section className="kit-card">
            <h2><PhosphorIcon className="bi bi-camera" /> Plate check</h2>
            <p className="kit-muted">Snap your meal and get an estimated calorie and macro breakdown. <strong>Estimates only — not medical or dietary advice.</strong></p>
            <label className="kit-log-toggle">
              <input type="checkbox" checked={logMeal} onChange={e => setLogMeal(e.target.checked)} /> Add to my food diary ({mealDate === new Date().toISOString().slice(0, 10) ? 'today' : mealDate})
            </label>
            <button className="kit-btn kit-btn-primary" onClick={() => setChooser('plate')} disabled={plateLoading}>
              {plateLoading ? <><span className="kit-spin" /> Analysing…</> : <><PhosphorIcon className="bi bi-camera-fill" /> Snap my plate</>}
            </button>

            {plate && (
              <div className="kit-plate-result">
                <div className="kit-plate-cal">
                  <strong>{Math.round(plate.calories)}</strong><span>kcal · {plate.title}</span>
                  <em className={`kit-conf kit-conf-${plate.confidence}`}>{plate.confidence} confidence</em>
                </div>
                <div className="kit-macros">
                  {([['Protein', plate.protein, '#16a34a'], ['Carbs', plate.carbs, '#f59e0b'], ['Fat', plate.fat, '#d62976']] as const).map(([label, g, col]) => (
                    <div key={label} className="kit-macro">
                      <span className="kit-macro-top"><span>{label}</span><strong>{Math.round(g)}g</strong></span>
                      <div className="kit-macro-bar"><div style={{ width: `${Math.min(100, (g / Math.max(plate.protein, plate.carbs, plate.fat, 1)) * 100)}%`, background: col }} /></div>
                    </div>
                  ))}
                </div>
                {plate.dishesJson && (() => {
                  try {
                    const dishes = JSON.parse(plate.dishesJson) as { name?: string; calories?: number }[]
                    return dishes.length ? <ul className="kit-dishes">{dishes.map((d, i) => <li key={i}><span>{d.name}</span><span>{d.calories ? `~${Math.round(d.calories)} kcal` : ''}</span></li>)}</ul> : null
                  } catch { return null }
                })()}
                {plate.healthNote && <p className="kit-recipe-health"><PhosphorIcon className="bi bi-lightbulb" /> {plate.healthNote}</p>}
                {plate.logged && <p className="kit-logged"><PhosphorIcon className="bi bi-check-circle-fill" /> Added to your diary.</p>}
              </div>
            )}
          </section>

          <section className="kit-card">
            <div className="kit-card-head">
              <h2><PhosphorIcon className="bi bi-journal-check" /> Food diary</h2>
              <input type="date" className="kit-input kit-date" value={mealDate} max={new Date().toISOString().slice(0, 10)} onChange={e => e.target.value && setMealDate(e.target.value)} />
            </div>
            {meals.length > 0 && (
              <div className="kit-day-total">
                <strong>{Math.round(dayTotals.cal)} kcal</strong>
                <span>P {Math.round(dayTotals.p)}g · C {Math.round(dayTotals.c)}g · F {Math.round(dayTotals.f)}g</span>
              </div>
            )}
            {meals.length === 0 ? (
              <div className="kit-empty"><PhosphorIcon className="bi bi-clipboard-heart" /><p>No meals logged for this day.</p></div>
            ) : (
              <div className="kit-meals">
                {meals.map(m => (
                  <div key={m.id} className="kit-meal">
                    {m.imageData ? <img src={m.imageData} alt="" /> : <span className="kit-meal-noimg"><PhosphorIcon className="bi bi-egg-fried" /></span>}
                    <div className="kit-meal-main">
                      <strong>{m.title}</strong>
                      <small>P {Math.round(m.protein)}g · C {Math.round(m.carbs)}g · F {Math.round(m.fat)}g</small>
                    </div>
                    <span className="kit-meal-cal">{Math.round(m.calories)} kcal</span>
                    <button className="kit-icon-btn" onClick={() => deleteMeal(m.id)} aria-label="Delete"><PhosphorIcon className="bi bi-trash" /></button>
                  </div>
                ))}
              </div>
            )}
          </section>
        </>
      )}

      {/* Add-item modal with nutrition lookup */}
      {addOpen && (
        <div className="kit-modal-backdrop" onClick={() => setAddOpen(false)}>
          <div className="kit-modal" onClick={e => e.stopPropagation()}>
            <button className="kit-modal-close" aria-label="Close" onClick={() => setAddOpen(false)}><PhosphorIcon className="bi bi-x-lg" /></button>
            <h3><PhosphorIcon className="bi bi-plus-circle" /> Add to pantry</h3>
            <div className="kit-manual" style={{ marginBottom: 12 }}>
              <input
                className="kit-input"
                placeholder="e.g. rolled oats, cheddar, chicken breast…"
                value={lookupQuery}
                autoFocus
                onChange={e => setLookupQuery(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') runLookup() }}
              />
              <button className="kit-btn kit-btn-primary" onClick={() => runLookup()} disabled={looking || !lookupQuery.trim()}>
                {looking ? <><span className="kit-spin" /> Looking…</> : <><PhosphorIcon className="bi bi-search" /> Search</>}
              </button>
            </div>

            {lookupError && <div className="kit-error" style={{ marginBottom: 12 }}><PhosphorIcon className="bi bi-exclamation-triangle" /> {lookupError}</div>}

            {lookup && (
              <div className="kit-lookup-card">
                <div className="kit-lookup-head">
                  <strong>{lookup.name}</strong>
                  <span className="kit-tag-ready" style={{ textTransform: 'capitalize' }}>{lookup.category}</span>
                </div>
                <div className="kit-lookup-kcal"><strong>{Math.round(lookup.caloriesPer100g)}</strong> kcal per 100g</div>
                <div className="kit-lookup-macros">
                  <span>Protein <strong>{lookup.protein}g</strong></span>
                  <span>Carbs <strong>{lookup.carbs}g</strong></span>
                  <span>Fat <strong>{lookup.fat}g</strong></span>
                </div>
                {lookup.servingNote && <p className="kit-muted" style={{ margin: '8px 0 0' }}><PhosphorIcon className="bi bi-cup" /> {lookup.servingNote}</p>}
                {lookup.healthNote && <p className="kit-recipe-health"><PhosphorIcon className="bi bi-heart-pulse" /> {lookup.healthNote}</p>}
              </div>
            )}

            {(lookup || (!looking && lookupQuery.trim())) && (
              <div className="kit-qty-row">
                <label>How much do you have? <span className="kit-muted" style={{ margin: 0 }}>(optional)</span></label>
                <input
                  className="kit-input"
                  placeholder="e.g. 500 g, 2 L, 6 eggs, half a pack"
                  value={qtyInput}
                  onChange={e => setQtyInput(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter' && lookup) confirmAdd(true) }}
                />
              </div>
            )}

            <div className="kit-lookup-actions">
              {lookup && (
                <button className="kit-btn kit-btn-primary" onClick={() => confirmAdd(true)} disabled={addingItem}>
                  {addingItem ? <><span className="kit-spin" /> Adding…</> : <><PhosphorIcon className="bi bi-check-lg" /> Add to pantry</>}
                </button>
              )}
              {!lookup && !looking && lookupQuery.trim() && (
                <button className="kit-btn" onClick={() => confirmAdd(false)} disabled={addingItem}>
                  <PhosphorIcon className="bi bi-plus-lg" /> Add "{lookupQuery.trim()}" without details
                </button>
              )}
            </div>
          </div>
        </div>
      )}

      {/* Photo chooser modal */}
      {chooser && (
        <div className="kit-modal-backdrop" onClick={() => setChooser(null)}>
          <div className="kit-modal" onClick={e => e.stopPropagation()}>
            <button className="kit-modal-close" aria-label="Close" onClick={() => setChooser(null)}><PhosphorIcon className="bi bi-x-lg" /></button>
            <h3><PhosphorIcon className="bi bi-camera" /> {chooser === 'pantry' ? 'Snap groceries' : 'Snap your plate'}</h3>
            <p className="kit-muted" style={{ margin: '0 0 14px' }}>
              {chooser === 'pantry' ? 'Point at your open fridge, pantry shelf, or grocery haul — I list everything I can see.' : 'One clear photo of your meal works best.'}
            </p>
            <div className="kit-choices">
              <button className="kit-choice" onClick={() => { (chooser === 'pantry' ? pantryCamera : plateCamera).current?.click(); setChooser(null) }}>
                <PhosphorIcon className="bi bi-camera-fill" /><strong>Take a photo</strong><span>Use your camera</span>
              </button>
              <button className="kit-choice" onClick={() => { (chooser === 'pantry' ? pantryUpload : plateUpload).current?.click(); setChooser(null) }}>
                <PhosphorIcon className="bi bi-folder2-open" /><strong>Upload</strong><span>Choose from your device</span>
              </button>
            </div>
          </div>
        </div>
      )}

      <input ref={pantryUpload} type="file" accept="image/*" multiple hidden onChange={e => onPantryFiles(e.target.files)} />
      <input ref={pantryCamera} type="file" accept="image/*" capture="environment" hidden onChange={e => onPantryFiles(e.target.files)} />
      <input ref={plateUpload} type="file" accept="image/*" hidden onChange={e => onPlateFiles(e.target.files)} />
      <input ref={plateCamera} type="file" accept="image/*" capture="environment" hidden onChange={e => onPlateFiles(e.target.files)} />
    </div>
  )
}
