import PhosphorIcon from '../components/PhosphorIcon'
import { useLayoutEffect, useState } from 'react'
import { useLayoutControls } from '../components/AuthenticatedLayout'
import { AgentStage, useFieldView, useSnapFieldModel } from '../gamefield'
import api from '../services/api'
import { compressImage } from '../utils/imageCompress'
import SnapButton from '../components/SnapButton'
import './snaptools.css'

interface Contact {
  name: string; title: string; company: string; phone: string
  mobile: string; email: string; website: string; address: string
}
const EMPTY: Contact = { name: '', title: '', company: '', phone: '', mobile: '', email: '', website: '', address: '' }

const FIELDS: { key: keyof Contact; label: string }[] = [
  { key: 'name', label: 'Name' }, { key: 'title', label: 'Title' }, { key: 'company', label: 'Company' },
  { key: 'phone', label: 'Phone' }, { key: 'mobile', label: 'Mobile' }, { key: 'email', label: 'Email' },
  { key: 'website', label: 'Website' }, { key: 'address', label: 'Address' },
]

export default function CardScanner() {
  const { setBreadcrumbs } = useLayoutControls()
  const { stageProps } = useFieldView('card-scanner')
  useLayoutEffect(() => {
    setBreadcrumbs([{ label: 'Dashboard', path: '/dashboard' }, { label: 'Card Scanner' }])
    return () => setBreadcrumbs([])
  }, [])

  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [contact, setContact] = useState<Contact | null>(null)

  const onFile = async (files: FileList | null) => {
    if (!files || files.length === 0) return
    setError(''); setBusy(true); setContact(null)
    try {
      const imageData = await compressImage(files[0])
      const { data } = await api.post('/cards/scan', { imageData })
      setContact({ ...EMPTY, ...data })
    } catch (e: unknown) {
      setError((e as { response?: { data?: { message?: string } } })?.response?.data?.message || 'Could not read that card.')
    } finally { setBusy(false) }
  }

  const set = (k: keyof Contact, v: string) => setContact(c => c ? { ...c, [k]: v } : c)

  const vcard = (c: Contact) => {
    const lines = ['BEGIN:VCARD', 'VERSION:3.0']
    if (c.name) lines.push(`FN:${c.name}`, `N:${c.name.split(' ').reverse().join(';')}`)
    if (c.company || c.title) lines.push(`ORG:${c.company}`, `TITLE:${c.title}`)
    if (c.phone) lines.push(`TEL;TYPE=WORK,VOICE:${c.phone}`)
    if (c.mobile) lines.push(`TEL;TYPE=CELL:${c.mobile}`)
    if (c.email) lines.push(`EMAIL;TYPE=WORK:${c.email}`)
    if (c.website) lines.push(`URL:${c.website}`)
    if (c.address) lines.push(`ADR;TYPE=WORK:;;${c.address};;;;`)
    lines.push('END:VCARD')
    return lines.join('\r\n')
  }
  const download = () => {
    if (!contact) return
    const url = URL.createObjectURL(new Blob([vcard(contact)], { type: 'text/vcard;charset=utf-8' }))
    const a = document.createElement('a')
    a.href = url; a.download = `${(contact.name || 'contact').replace(/[^a-z0-9]+/gi, '-').toLowerCase()}.vcf`
    document.body.appendChild(a); a.click(); a.remove(); URL.revokeObjectURL(url)
  }
  const copy = async () => { if (contact) await navigator.clipboard.writeText(FIELDS.map(f => contact[f.key]).filter(Boolean).join('\n')) }


  // Gamefield: intake -> vision model -> filing, the three stages a snap
  // actually goes through.
  const field = useSnapFieldModel({
    title: 'Card Scanner',
    busy: busy,
    error,
  })

  return (
    <div className="st-page">
      <div className="st-hero">
        <h1><PhosphorIcon className="bi bi-person-vcard" /> Card Scanner</h1>
        <p>Snap a business card and get the contact details back, ready to save to your phone as a vCard. Nothing is stored.</p>
      </div>

      {error && <div className="st-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

      <AgentStage {...stageProps} model={field} />

      <div className="st-card" style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <span className="st-muted">Lay the card flat and fill the frame for the best read.</span>
        <SnapButton onPick={onFile} busy={busy} label="Scan a card" />
      </div>

      {contact && (
        <div className="st-card">
          <div className="cs-fields">
            {FIELDS.map(f => (
              <div key={f.key} className="cs-field">
                <label>{f.label}</label>
                <input value={contact[f.key]} onChange={e => set(f.key, e.target.value)} placeholder={`No ${f.label.toLowerCase()} found`} />
              </div>
            ))}
          </div>
          <div className="wb-toolbar" style={{ marginTop: 16, marginBottom: 0 }}>
            <button className="st-btn st-btn-primary" onClick={download}><PhosphorIcon className="bi bi-download" /> Save as vCard</button>
            <button className="st-btn" onClick={copy}><PhosphorIcon className="bi bi-clipboard" /> Copy details</button>
          </div>
        </div>
      )}
    </div>
  )
}
