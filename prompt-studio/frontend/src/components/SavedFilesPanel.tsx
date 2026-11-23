import { useEffect, useState } from 'react'
import PhosphorIcon from './PhosphorIcon'

/**
 * The files a user chose to keep — today that means converted PDFs saved via
 * "Keep this file" on the free tools.
 *
 * This panel exists because the save button promises "it will be waiting in
 * your files": without somewhere that lists them, that promise is false and
 * the save is a write-only hole. Renders nothing while empty so the dashboard
 * of a user who never kept a file is untouched.
 */

interface SavedFile {
  id: number
  fileName: string
  fileSize: number
  createdAt: string
}

function prettySize(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
  if (bytes >= 1024) return `${Math.round(bytes / 1024)} KB`
  return `${bytes} B`
}

export default function SavedFilesPanel() {
  const [files, setFiles] = useState<SavedFile[]>([])
  const token = localStorage.getItem('authToken')

  useEffect(() => {
    if (!token) return
    fetch('/api/files?entityType=vault&entityId=converted', {
      headers: { Authorization: `Bearer ${token}` },
    })
      .then(r => (r.ok ? r.json() : []))
      .then(d => setFiles(Array.isArray(d) ? d : []))
      .catch(() => { /* an empty panel is the correct failure mode here */ })
  }, [token])

  const remove = async (id: number) => {
    // Optimistic: the row disappears immediately, and a failed delete brings
    // it back on the next load rather than blocking the click.
    setFiles(prev => prev.filter(f => f.id !== id))
    try {
      await fetch(`/api/files/${id}`, {
        method: 'DELETE',
        headers: { Authorization: `Bearer ${token}` },
      })
    } catch { /* see above */ }
  }

  if (files.length === 0) return null

  return (
    <div style={{
      border: '1px solid var(--pg-border, rgba(0,0,0,0.1))',
      borderRadius: 12,
      padding: '14px 16px',
      marginBottom: 20,
    }}>
      <h2 style={{ margin: '0 0 10px', fontSize: '1.05rem', fontWeight: 700, color: 'var(--pg-text)' }}>
        <PhosphorIcon className="bi bi-folder-check" /> Saved files
      </h2>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {files.map(f => (
          <div key={f.id} style={{ display: 'flex', alignItems: 'center', gap: 10, fontSize: '0.9rem' }}>
            <PhosphorIcon className="bi bi-file-earmark-pdf" />
            <span style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
              {f.fileName}
            </span>
            <span style={{ opacity: 0.6, fontSize: '0.8rem' }}>{prettySize(f.fileSize)}</span>
            <a
              href={`/api/files/${f.id}/download`}
              onClick={async e => {
                // The download endpoint needs the bearer token, so a plain
                // href will 401 — fetch it and hand the browser a blob instead.
                e.preventDefault()
                const res = await fetch(`/api/files/${f.id}/download`, {
                  headers: { Authorization: `Bearer ${token}` },
                })
                if (!res.ok) return
                const blob = await res.blob()
                const url = URL.createObjectURL(blob)
                const a = document.createElement('a')
                a.href = url
                a.download = f.fileName
                a.click()
                URL.revokeObjectURL(url)
              }}
              title="Download"
              style={{ display: 'inline-flex' }}
            >
              <PhosphorIcon className="bi bi-download" />
            </a>
            <button
              onClick={() => remove(f.id)}
              title="Delete"
              style={{ background: 'none', border: 'none', cursor: 'pointer', color: 'inherit', display: 'inline-flex', padding: 2 }}
            >
              <PhosphorIcon className="bi bi-trash" />
            </button>
          </div>
        ))}
      </div>
    </div>
  )
}
