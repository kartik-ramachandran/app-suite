import PhosphorIcon from './PhosphorIcon'
import { useCallback, useEffect, useRef, useState } from 'react'
import api from '../services/api'
import './RecipeLibrary.css'

/**
 * The recipe library: what the user can cook, from books they imported and
 * photos they took.
 *
 * "Cook tonight" leads deliberately. A list of recipes is a reference; a list
 * of things you can make right now, from what is already in the kitchen, is a
 * reason to open the app at 6pm.
 */

interface RecipeSummary {
  id: number
  title: string
  description?: string
  cuisine?: string
  course?: string
  servings?: number
  totalMinutes?: number
  sourceBookTitle?: string
  isFavourite: boolean
  timesCooked: number
  ingredientCount: number
}

interface Ingredient { name: string; quantity?: string; unit?: string }

interface RecipeDetail extends RecipeSummary {
  ingredients: Ingredient[]
  steps: string[]
  dietary: string[]
}

interface Match {
  id: number
  title: string
  description?: string
  totalMinutes?: number
  servings?: number
  sourceBookTitle?: string
  have: number
  needs: number
  missing: string[]
  canCookNow: boolean
  matchPercent: number
}

interface Draft {
  title: string
  description?: string
  cuisine?: string
  course?: string
  servings?: number
  totalMinutes?: number
  ingredients: Ingredient[]
  steps: string[]
  dietary: string[]
}

export default function RecipeLibrary() {
  const [recipes, setRecipes] = useState<RecipeSummary[]>([])
  const [matches, setMatches] = useState<Match[]>([])
  const [pantryCount, setPantryCount] = useState(0)
  const [pantryEmpty, setPantryEmpty] = useState(true)
  const [open, setOpen] = useState<RecipeDetail | null>(null)
  const [subs, setSubs] = useState<{ missing: string; use: string; how?: string; confidence: string }[] | null>(null)
  const [cannotSub, setCannotSub] = useState<string[]>([])
  const [subsLoading, setSubsLoading] = useState(false)
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  // Photo capture
  const fileRef = useRef<HTMLInputElement>(null)
  const [reading, setReading] = useState(false)
  const [drafts, setDrafts] = useState<Draft[]>([])
  const [saving, setSaving] = useState(false)

  const load = useCallback(async (q?: string) => {
    try {
      const [list, tonight] = await Promise.all([
        api.get('/recipes', { params: q ? { search: q } : {} }),
        api.get('/recipes/cook-tonight'),
      ])
      setRecipes(list.data ?? [])
      setPantryEmpty(!!tonight.data.pantryEmpty)
      setPantryCount(tonight.data.pantryCount ?? 0)
      setMatches(tonight.data.matches ?? [])
    } catch {
      setError('Could not load your recipes.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => { void load() }, [load])

  const openRecipe = async (id: number) => {
    setSubs(null); setCannotSub([])
    try {
      const res = await api.get(`/recipes/${id}`)
      setOpen(res.data)
    } catch {
      setError('Could not open that recipe.')
    }
  }

  // Photo -> draft. Never saves straight away: vision misreads handwriting,
  // and a wrong quantity discovered mid-cook is worse than a quick check.
  const onPhoto = async (file: File) => {
    setReading(true); setError(''); setDrafts([])
    try {
      const dataUrl = await new Promise<string>((resolve, reject) => {
        const fr = new FileReader()
        fr.onload = () => resolve(String(fr.result))
        fr.onerror = () => reject(new Error('read failed'))
        fr.readAsDataURL(file)
      })
      const res = await api.post('/recipes/from-photo', { imageData: dataUrl })
      if (!res.data.found) setError(res.data.message || 'No recipe found in that photo.')
      else setDrafts(res.data.drafts ?? [])
    } catch (e: any) {
      setError(e?.response?.data?.error || 'Could not read that photo.')
    } finally {
      setReading(false)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  const saveDraft = async (draft: Draft) => {
    setSaving(true)
    try {
      await api.post('/recipes', { ...draft, source: 'photo', sourceLabel: 'From a photo' })
      setDrafts(d => d.filter(x => x !== draft))
      await load(search)
    } catch (e: any) {
      setError(e?.response?.data?.error || 'Could not save that recipe.')
    } finally {
      setSaving(false)
    }
  }

  // On demand only. Substitution is judgment worth paying a model for, but
  // not on every page load — the user asks about one recipe they want to cook.
  const askSubs = async (id: number) => {
    setSubsLoading(true)
    try {
      const res = await api.get(`/recipes/${id}/substitutions`)
      setSubs(res.data.substitutions ?? [])
      setCannotSub((res.data.cannotSubstitute ?? []).filter(Boolean))
    } catch {
      setError('Could not work out substitutions.')
    } finally {
      setSubsLoading(false)
    }
  }

  const cooked = async (id: number) => {
    await api.post(`/recipes/${id}/cooked`)
    await load(search)
    setOpen(null)
  }

  const favourite = async (id: number) => {
    await api.post(`/recipes/${id}/favourite`)
    await load(search)
  }

  const remove = async (id: number) => {
    if (!confirm('Delete this recipe?')) return
    await api.delete(`/recipes/${id}`)
    setOpen(null)
    await load(search)
  }

  // ── Draft review ──────────────────────────────────────────────────────────

  if (drafts.length > 0) {
    return (
      <div className="rc-tab">
        <div className="rc-review-head">
          <div>
            <h2>Check this before saving</h2>
            <p className="rc-muted">Read from your photo — correct anything that came out wrong.</p>
          </div>
          <button className="rc-ghost" onClick={() => setDrafts([])}>Discard</button>
        </div>

        {drafts.map((d, i) => (
          <div className="rc-draft" key={i}>
            <input
              className="rc-draft-title"
              value={d.title}
              onChange={e => setDrafts(list => list.map((x, j) => j === i ? { ...x, title: e.target.value } : x))}
            />
            <div className="rc-draft-meta">
              {d.cuisine && <span>{d.cuisine}</span>}
              {d.course && <span>{d.course}</span>}
              {d.servings ? <span>{d.servings} serves</span> : null}
              {d.totalMinutes ? <span>{d.totalMinutes} min</span> : null}
            </div>

            <h4>Ingredients</h4>
            <ul className="rc-draft-ing">
              {d.ingredients.map((ing, k) => (
                <li key={k}>
                  <span className="rc-qty">{[ing.quantity, ing.unit].filter(Boolean).join(' ')}</span>
                  {ing.name}
                </li>
              ))}
            </ul>

            <h4>Method</h4>
            <ol className="rc-draft-steps">
              {d.steps.map((s, k) => <li key={k}>{s}</li>)}
            </ol>

            <button className="rc-primary" onClick={() => saveDraft(d)} disabled={saving}>
              {saving ? 'Saving…' : 'Save to my recipes'}
            </button>
          </div>
        ))}
      </div>
    )
  }

  // ── Recipe detail ─────────────────────────────────────────────────────────

  if (open) {
    return (
      <div className="rc-tab">
        <button className="rc-back" onClick={() => setOpen(null)}>
          <PhosphorIcon className="bi bi-arrow-left" /> All recipes
        </button>

        <div className="rc-detail-head">
          <h2>{open.title}</h2>
          <button className="rc-icon" onClick={() => favourite(open.id)} title="Favourite">
            <PhosphorIcon className={open.isFavourite ? 'bi bi-star-fill' : 'bi bi-star'} />
          </button>
        </div>

        {open.description && <p className="rc-lead">{open.description}</p>}

        <div className="rc-detail-meta">
          {open.cuisine && <span>{open.cuisine}</span>}
          {open.course && <span>{open.course}</span>}
          {open.servings ? <span>{open.servings} serves</span> : null}
          {open.totalMinutes ? <span>{open.totalMinutes} min</span> : null}
          {open.dietary?.map(t => <span key={t} className="rc-diet">{t}</span>)}
        </div>

        {open.sourceBookTitle && <p className="rc-source">from {open.sourceBookTitle}</p>}

        <div className="rc-detail-cols">
          <div>
            <h3>Ingredients</h3>
            <ul className="rc-ing">
              {open.ingredients.map((i, k) => (
                <li key={k}>
                  <span className="rc-qty">{[i.quantity, i.unit].filter(Boolean).join(' ')}</span>
                  {i.name}
                </li>
              ))}
            </ul>
          </div>
          <div>
            <h3>Method</h3>
            <ol className="rc-steps">
              {open.steps.map((s, k) => <li key={k}>{s}</li>)}
            </ol>
          </div>
        </div>

        {/* Only offered when something is actually missing. */}
        {matches.some(m => m.id === open.id && !m.canCookNow) && (
          <div className="rc-subs">
            {!subs && (
              <button className="rc-ghost" onClick={() => askSubs(open.id)} disabled={subsLoading}>
                <PhosphorIcon className="bi bi-lightbulb" />
                {subsLoading ? 'Thinking…' : 'Can I substitute what I\'m missing?'}
              </button>
            )}

            {subs && subs.length > 0 && (
              <>
                <h3>You could use instead</h3>
                {subs.map((sb, i) => (
                  <div className="rc-sub" key={i}>
                    <div className="rc-sub-line">
                      <strong>{sb.missing}</strong>
                      <PhosphorIcon className="bi bi-arrow-right" />
                      <span>{sb.use}</span>
                      <em className={`rc-conf rc-conf-${sb.confidence}`}>{sb.confidence}</em>
                    </div>
                    {sb.how && <p>{sb.how}</p>}
                  </div>
                ))}
              </>
            )}

            {subs && subs.length === 0 && <p className="rc-muted">No good substitutes from what you have.</p>}

            {cannotSub.length > 0 && (
              <p className="rc-muted">You'll need to buy: {cannotSub.join(', ')}</p>
            )}
          </div>
        )}

        <div className="rc-detail-actions">
          <button className="rc-primary" onClick={() => cooked(open.id)}>
            <PhosphorIcon className="bi bi-check-circle" /> I cooked this
            {open.timesCooked > 0 && <em> · {open.timesCooked}×</em>}
          </button>
          <button className="rc-ghost rc-danger" onClick={() => remove(open.id)}>Delete</button>
        </div>
      </div>
    )
  }

  // ── Library ───────────────────────────────────────────────────────────────

  return (
    <div className="rc-tab">
      <div className="rc-head">
        <div>
          <h2>Recipes</h2>
          <p className="rc-muted">
            From cookbooks you've imported and photos you've taken.
          </p>
        </div>
        <input
          ref={fileRef}
          type="file"
          accept="image/*"
          hidden
          onChange={e => { const f = e.target.files?.[0]; if (f) void onPhoto(f) }}
        />
        <button className="rc-primary" onClick={() => fileRef.current?.click()} disabled={reading}>
          <PhosphorIcon className="bi bi-camera" />
          {reading ? 'Reading the photo…' : 'Snap a recipe'}
        </button>
      </div>

      {error && <p className="rc-error">{error}</p>}

      {/* Leads on purpose: what you can cook right now beats a list of what exists. */}
      {!pantryEmpty && matches.length > 0 && (
        <section className="rc-tonight">
          <div className="rc-tonight-head">
            <h3>Cook tonight</h3>
            <span className="rc-muted">from {pantryCount} things in your kitchen</span>
          </div>
          <div className="rc-tonight-row">
            {matches.map(m => (
              <button className={`rc-match ${m.canCookNow ? 'rc-match-now' : ''}`} key={m.id} onClick={() => openRecipe(m.id)}>
                <div className="rc-match-top">
                  {m.canCookNow
                    ? <span className="rc-badge-now">Everything in stock</span>
                    : <span className="rc-badge-near">{m.matchPercent}% there</span>}
                  {m.totalMinutes ? <span className="rc-muted">{m.totalMinutes} min</span> : null}
                </div>
                <strong>{m.title}</strong>
                {!m.canCookNow && (
                  <p className="rc-missing">needs {m.missing.slice(0, 3).join(', ')}</p>
                )}
              </button>
            ))}
          </div>
        </section>
      )}

      {pantryEmpty && recipes.length > 0 && (
        <p className="rc-hint">
          <PhosphorIcon className="bi bi-lightbulb" />
          Snap your fridge in <a href="/kitchen">Kitchen</a> and this page will tell you which of these you can cook tonight.
        </p>
      )}

      <div className="rc-search">
        <PhosphorIcon className="bi bi-search" />
        <input
          value={search}
          placeholder="Search by name, ingredient, or cuisine…"
          onChange={e => { setSearch(e.target.value); void load(e.target.value) }}
        />
      </div>

      {loading && <p className="rc-muted">Loading…</p>}

      {!loading && recipes.length === 0 && (
        <div className="rc-empty">
          <PhosphorIcon className="bi bi-egg-fried" />
          <h3>No recipes yet</h3>
          <p>Photograph a recipe page, or upload a cookbook in Book Reader and import it here.</p>
        </div>
      )}

      <div className="rc-grid">
        {recipes.map(r => (
          <button className="rc-card" key={r.id} onClick={() => openRecipe(r.id)}>
            <div className="rc-card-top">
              <span className="rc-course">{r.course || 'recipe'}</span>
              {r.isFavourite && <PhosphorIcon className="bi bi-star-fill rc-fav" />}
            </div>
            <strong>{r.title}</strong>
            {r.description && <p className="rc-card-desc">{r.description}</p>}
            <div className="rc-card-meta">
              <span>{r.ingredientCount} ingredients</span>
              {r.totalMinutes ? <span>{r.totalMinutes} min</span> : null}
              {r.timesCooked > 0 && <span>cooked {r.timesCooked}×</span>}
            </div>
            {r.sourceBookTitle && <span className="rc-card-src">{r.sourceBookTitle}</span>}
          </button>
        ))}
      </div>
    </div>
  )
}
