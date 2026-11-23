import PhosphorIcon from './PhosphorIcon'
import { useRef, useState } from 'react'
import api from '../services/api'

interface UploadedFile {
  name: string
  content: string
}

interface AgentFileUploadProps {
  onFilesChange: (fileContext: string, files: UploadedFile[]) => void
  disabled?: boolean
}

const TEXT_EXTENSIONS = new Set([
  'txt', 'md', 'markdown', 'csv', 'json', 'yaml', 'yml', 'xml',
  'html', 'htm', 'js', 'ts', 'tsx', 'jsx', 'py', 'rb', 'go',
  'java', 'cs', 'cpp', 'c', 'rs', 'sql', 'sh', 'bat', 'rtf',
])

const MAX_FILE_SIZE = 10 * 1024 * 1024 // 10MB

function getFileIcon(name: string): string {
  const ext = name.split('.').pop()?.toLowerCase() ?? ''
  if (ext === 'pdf') return 'bi-file-earmark-pdf'
  if (['doc', 'docx', 'rtf'].includes(ext)) return 'bi-file-earmark-word'
  if (['xls', 'xlsx', 'csv'].includes(ext)) return 'bi-file-earmark-spreadsheet'
  if (['json', 'xml', 'yaml', 'yml'].includes(ext)) return 'bi-file-earmark-code'
  return 'bi-file-earmark-text'
}

function readFileAsText(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = e => resolve(e.target?.result as string ?? '')
    reader.onerror = reject
    reader.readAsText(file)
  })
}

function buildContext(files: UploadedFile[]): string {
  return files.map(f => `--- File: ${f.name} ---\n${f.content}`).join('\n\n')
}

export default function AgentFileUpload({ onFilesChange, disabled }: AgentFileUploadProps) {
  const [files, setFiles] = useState<UploadedFile[]>([])
  const [uploading, setUploading] = useState(false)
  const [dragOver, setDragOver] = useState(false)
  const [error, setError] = useState('')
  const inputRef = useRef<HTMLInputElement>(null)

  const processFiles = async (selected: FileList) => {
    setError('')
    const newFiles: UploadedFile[] = []

    for (const file of Array.from(selected)) {
      if (file.size > MAX_FILE_SIZE) {
        setError(`"${file.name}" exceeds 10 MB limit`)
        continue
      }

      const ext = file.name.split('.').pop()?.toLowerCase() ?? ''

      if (TEXT_EXTENSIONS.has(ext)) {
        const content = await readFileAsText(file)
        newFiles.push({ name: file.name, content })
      } else if (ext === 'pdf' || ext === 'docx') {
        setUploading(true)
        try {
          const form = new FormData()
          form.append('file', file)
          const res = await api.post<{ text: string }>('/files/extract-text', form, {
            headers: { 'Content-Type': 'multipart/form-data' },
          })
          newFiles.push({ name: file.name, content: res.data.text })
        } catch {
          setError(`Failed to extract text from "${file.name}"`)
        } finally {
          setUploading(false)
        }
      } else {
        setError(`Unsupported type: .${ext}. Try txt, md, csv, json, pdf, or docx.`)
      }
    }

    if (newFiles.length === 0) return

    setFiles(prev => {
      const merged = [...prev, ...newFiles]
      onFilesChange(buildContext(merged), merged)
      return merged
    })
  }

  const removeFile = (name: string) => {
    setFiles(prev => {
      const next = prev.filter(f => f.name !== name)
      onFilesChange(buildContext(next), next)
      return next
    })
  }

  return (
    <div style={{ marginTop: 8 }}>
      {/* Drop zone */}
      <div
        style={{
          border: `1.5px dashed ${dragOver ? 'var(--primary)' : 'var(--border)'}`,
          borderRadius: 8,
          padding: '10px 14px',
          background: dragOver ? 'rgba(99,102,241,0.05)' : 'transparent',
          cursor: disabled || uploading ? 'default' : 'pointer',
          transition: 'border-color 0.15s, background 0.15s',
        }}
        onDragOver={e => { e.preventDefault(); if (!disabled) setDragOver(true) }}
        onDragLeave={() => setDragOver(false)}
        onDrop={e => {
          e.preventDefault()
          setDragOver(false)
          if (!disabled && e.dataTransfer.files.length) processFiles(e.dataTransfer.files)
        }}
        onClick={() => { if (!disabled && !uploading) inputRef.current?.click() }}
      >
        <input
          ref={inputRef}
          type="file"
          multiple
          accept=".txt,.md,.csv,.json,.yaml,.yml,.xml,.html,.pdf,.docx,.js,.ts,.tsx,.jsx,.py"
          style={{ display: 'none' }}
          onChange={e => { if (e.target.files) { processFiles(e.target.files); e.target.value = '' } }}
        />

        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          {uploading
            ? <><div className="spinner" style={{ width: 14, height: 14, margin: 0 }} /><span style={{ fontSize: 12, color: 'var(--text-secondary)' }}>Extracting text…</span></>
            : <>
                <PhosphorIcon className="bi bi-cloud-upload" style={{ fontSize: 15, color: 'var(--text-muted)' }} />
                <span style={{ fontSize: 12, color: 'var(--text-secondary)' }}>
                  Drop files or <span style={{ color: 'var(--primary)', textDecoration: 'underline' }}>click to upload</span>
                </span>
                <span style={{ fontSize: 11, color: 'var(--text-muted)', marginLeft: 2 }}>
                  txt, md, csv, json, pdf, docx…
                </span>
              </>
          }
        </div>

        {/* File chips */}
        {files.length > 0 && (
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginTop: 8 }}>
            {files.map(f => (
              <span key={f.name} style={{
                display: 'inline-flex', alignItems: 'center', gap: 4,
                background: 'var(--bg-secondary)', border: '1px solid var(--border)',
                borderRadius: 20, padding: '2px 8px 2px 6px', fontSize: 11, fontWeight: 500,
                color: 'var(--text-secondary)', maxWidth: 220,
              }}>
                <PhosphorIcon className={`bi ${getFileIcon(f.name)}`} style={{ fontSize: 11, flexShrink: 0 }} />
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{f.name}</span>
                <button
                  onClick={e => { e.stopPropagation(); removeFile(f.name) }}
                  disabled={disabled}
                  style={{
                    background: 'none', border: 'none', cursor: 'pointer',
                    padding: 0, display: 'flex', color: 'var(--text-muted)', flexShrink: 0,
                  }}
                  title="Remove file"
                >
                  <PhosphorIcon className="bi bi-x" style={{ fontSize: 13 }} />
                </button>
              </span>
            ))}
          </div>
        )}
      </div>

      {error && (
        <p style={{ color: '#ef4444', fontSize: 11, margin: '4px 0 0', display: 'flex', alignItems: 'center', gap: 4 }}>
          <PhosphorIcon className="bi bi-exclamation-circle" />
          {error}
          <button
            onClick={() => setError('')}
            style={{ background: 'none', border: 'none', cursor: 'pointer', color: '#ef4444', padding: 0, marginLeft: 2, display: 'flex' }}
          >
            <PhosphorIcon className="bi bi-x" />
          </button>
        </p>
      )}
    </div>
  )
}
