import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useLayoutEffect, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { consumeCosmosHandoff } from '../utils/cosmosHandoff'
import './snaptools.css'

export default function ProposalGenerator() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('proposal')
  useLayoutEffect(() => { setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Proposal Generator' }]); return () => setBreadcrumbs([]) }, [])

  const [form, setForm] = useState({ from: '', client: '', project: '', scope: '', price: '', timeline: '', tone: 'professional' })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [markdown, setMarkdown] = useState('')
  const [copied, setCopied] = useState(false)
  const set = (k: keyof typeof form, v: string) => setForm(f => ({ ...f, [k]: v }))

  // Consume a pending Cosmos handoff (AI-routed from the knowledge graph). Prefill only.
  useEffect(() => {
    const handoff = consumeCosmosHandoff('/proposal')
    if (handoff?.input) setForm(f => ({ ...f, project: handoff.input }))
  }, [])

  const go = async () => {
    if (!form.project.trim()) { setError('Describe the project or service.'); return }
    setBusy(true); setError(''); setMarkdown('')
    try { const { data } = await api.post('/planner/proposal', form); setMarkdown(data.markdown || '') }
    catch (e: unknown) { setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not generate the proposal.') }
    finally { setBusy(false) }
  }
  const copy = async () => { await navigator.clipboard.writeText(markdown); setCopied(true); setTimeout(() => setCopied(false), 1800) }
  const printPdf = () => {
    // Print the rendered proposal (the browser's print dialog offers "Save as PDF").
    const rendered = document.querySelector('.wb-md')?.innerHTML
    if (!rendered) return
    const w = window.open('', '_blank'); if (!w) return
    w.document.write(`<!doctype html><meta charset="utf-8"><title>Proposal</title><body style="font-family:Inter,Arial,sans-serif;max-width:820px;margin:24px auto;padding:0 24px;line-height:1.65;color:#0f172a">${rendered}</body>`)
    w.document.close()
    setTimeout(() => { w.focus(); w.print() }, 300)
  }


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Proposal Generator',
    busy: busy,
    error,
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-file-earmark-ruled" /> Proposal &amp; Quote Generator</h1>
        <p>Fill in the essentials and get a clean, client-ready proposal you can copy or print to PDF.</p>
      </div>
      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}
      <AgentStage {...stageProps} model={field} />

      <div className="st-card">
        <div className="st-form">
          <div className="st-field2"><label>From (you / company)</label><input className="st-input" value={form.from} onChange={e => set('from', e.target.value)} placeholder="Your name or business" /></div>
          <div className="st-field2"><label>Client</label><input className="st-input" value={form.client} onChange={e => set('client', e.target.value)} placeholder="Client name" /></div>
          <div className="st-field2 full"><label>Project / service</label><input className="st-input" value={form.project} onChange={e => set('project', e.target.value)} placeholder="e.g. Redesign of marketing website" /></div>
          <div className="st-field2 full"><label>Scope / deliverables</label><textarea className="st-textarea" value={form.scope} onChange={e => set('scope', e.target.value)} placeholder="What's included…" /></div>
          <div className="st-field2"><label>Price</label><input className="st-input" value={form.price} onChange={e => set('price', e.target.value)} placeholder="$5,000 fixed / $120/hr" /></div>
          <div className="st-field2"><label>Timeline</label><input className="st-input" value={form.timeline} onChange={e => set('timeline', e.target.value)} placeholder="e.g. 6 weeks" /></div>
        </div>
        <button className="st-btn st-btn-primary" style={{ marginTop: 14 }} onClick={go} disabled={busy}>{busy ? <><span className="st-spin" /> Writing…</> : <><PhosphorIcon className="bi bi-magic" /> Generate proposal</>}</button>
      </div>

      {markdown && (
        <>
          <div className="wb-toolbar">
            <button className="st-btn st-btn-primary" onClick={copy}>{copied ? <><PhosphorIcon className="bi bi-check-lg" /> Copied</> : <><PhosphorIcon className="bi bi-clipboard" /> Copy</>}</button>
            <button className="st-btn" onClick={printPdf}><PhosphorIcon className="bi bi-printer" /> Print / PDF</button>
          </div>
          <div className="wb-md"><ReactMarkdown remarkPlugins={[remarkGfm]}>{markdown}</ReactMarkdown></div>
        </>
      )}
    </div>
  )
}
