import PhosphorIcon from './PhosphorIcon'
import { useState, useEffect, useRef, useCallback } from 'react'
import { useTranslation } from 'react-i18next'
import api from '../services/api'

interface FileAttachment {
  id: number
  fileName: string
  contentType: string
  fileSize: number
  source: string
  googleDriveFileId?: string
  createdAt: string
}

interface FileUploadProps {
  entityType: 'persona' | 'prompt'
  entityId: string | number | null // null means "not saved yet"
  onFilesChange?: (files: FileAttachment[]) => void
}

const FILE_ICONS: Record<string, string> = {
  'application/pdf': 'bi-file-earmark-pdf',
  'application/msword': 'bi-file-earmark-word',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': 'bi-file-earmark-word',
  'text/plain': 'bi-file-earmark-text',
  'text/markdown': 'bi-file-earmark-text',
  'text/csv': 'bi-file-earmark-spreadsheet',
  'application/json': 'bi-file-earmark-code',
  'application/xml': 'bi-file-earmark-code',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': 'bi-file-earmark-excel',
  'application/vnd.ms-excel': 'bi-file-earmark-excel',
  'application/vnd.openxmlformats-officedocument.presentationml.presentation': 'bi-file-earmark-ppt',
  'application/vnd.ms-powerpoint': 'bi-file-earmark-ppt',
}

function formatFileSize(bytes: number): string {
  if (bytes < 1024) return bytes + ' B'
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB'
  return (bytes / (1024 * 1024)).toFixed(1) + ' MB'
}

// Google Drive Picker API
declare global {
  interface Window {
    gapi: any
    google: any
  }
}

const CONTEXT_EXAMPLES = [
  {
    icon: 'bi-file-earmark-spreadsheet',
    color: '#16a34a',
    title: 'Product Catalog',
    file: 'products.csv',
    download: '/examples/products.csv',
    desc: 'Let the AI answer questions about your inventory, pricing, or specs.',
  },
  {
    icon: 'bi-file-earmark-text',
    color: '#dc2626',
    title: 'Company FAQ',
    file: 'faq.txt',
    download: '/examples/faq.txt',
    desc: 'Ground the AI in your official policies so answers stay on-brand.',
  },
  {
    icon: 'bi-file-earmark-text',
    color: '#c13584',
    title: 'Brand Guidelines',
    file: 'brand-voice.txt',
    download: '/examples/brand-voice.txt',
    desc: 'Keep tone, style, and terminology consistent across all outputs.',
  },
  {
    icon: 'bi-file-earmark-code',
    color: '#833ab4',
    title: 'API / Schema Docs',
    file: 'openapi.json',
    download: '/examples/openapi.json',
    desc: 'Have the AI generate code or queries that match your exact schema.',
  },
  {
    icon: 'bi-file-earmark-text',
    color: '#c13584',
    title: 'Contract Template',
    file: 'contract-template.txt',
    download: '/examples/contract-template.txt',
    desc: 'Provide a reference doc so the AI drafts clauses in your format.',
  },
  {
    icon: 'bi-file-earmark-spreadsheet',
    color: '#b45309',
    title: 'Customer Data (sample)',
    file: 'customers.csv',
    download: '/examples/customers.csv',
    desc: 'Analyse trends or generate summaries from tabular data.',
  },
]

function FileUpload({ entityType, entityId, onFilesChange }: FileUploadProps) {
  const { t } = useTranslation()
  const [files, setFiles] = useState<FileAttachment[]>([])
  const [uploading, setUploading] = useState(false)
  const [dragOver, setDragOver] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const fileInputRef = useRef<HTMLInputElement>(null)
  const [driveLoaded, setDriveLoaded] = useState(false)
  const [showExamples, setShowExamples] = useState(false)

  const fetchFiles = useCallback(async () => {
    if (!entityId) return
    try {
      const response = await api.get('/files', {
        params: { entityType, entityId: String(entityId) }
      })
      setFiles(response.data)
      onFilesChange?.(response.data)
    } catch {
      // Silently fail — files section is not critical
    }
  }, [entityType, entityId, onFilesChange])

  useEffect(() => {
    fetchFiles()
  }, [fetchFiles])

  // Load Google Drive Picker API
  useEffect(() => {
    if (window.gapi) {
      setDriveLoaded(true)
      return
    }
    const script = document.createElement('script')
    script.src = 'https://apis.google.com/js/api.js'
    script.onload = () => {
      window.gapi.load('picker', () => setDriveLoaded(true))
    }
    document.body.appendChild(script)

    // Also load Google Identity Services for OAuth token
    if (!document.getElementById('gsi-script')) {
      const gsiScript = document.createElement('script')
      gsiScript.id = 'gsi-script'
      gsiScript.src = 'https://accounts.google.com/gsi/client'
      document.body.appendChild(gsiScript)
    }
  }, [])

  const handleFileSelect = async (selectedFiles: FileList | null) => {
    if (!selectedFiles || selectedFiles.length === 0 || !entityId) return

    setError(null)
    setUploading(true)

    const formData = new FormData()
    formData.append('entityType', entityType)
    formData.append('entityId', String(entityId))
    for (let i = 0; i < selectedFiles.length; i++) {
      formData.append('files', selectedFiles[i])
    }

    try {
      await api.post('/files/upload', formData, {
        headers: { 'Content-Type': 'multipart/form-data' }
      })
      await fetchFiles()
    } catch (err: any) {
      const msg = err.response?.data?.error || 'Upload failed'
      setError(msg)
    } finally {
      setUploading(false)
      if (fileInputRef.current) fileInputRef.current.value = ''
    }
  }

  const handleDelete = async (fileId: number) => {
    try {
      await api.delete(`/files/${fileId}`)
      await fetchFiles()
    } catch {
      setError('Failed to delete file')
    }
  }

  const handleDownload = (fileId: number, fileName: string) => {
    const token = localStorage.getItem('authToken')
    const baseUrl = import.meta.env.VITE_API_BASE_URL || '/api'
    const url = `${baseUrl}/files/${fileId}/download`

    const link = document.createElement('a')
    link.href = url
    link.download = fileName
    // Use fetch with auth header for authenticated download
    fetch(url, { headers: { Authorization: `Bearer ${token}` } })
      .then(res => res.blob())
      .then(blob => {
        const blobUrl = URL.createObjectURL(blob)
        link.href = blobUrl
        link.click()
        URL.revokeObjectURL(blobUrl)
      })
  }

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault()
    setDragOver(true)
  }

  const handleDragLeave = () => setDragOver(false)

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault()
    setDragOver(false)
    handleFileSelect(e.dataTransfer.files)
  }

  const handleGoogleDrivePick = () => {
    if (!entityId) return

    const GOOGLE_CLIENT_ID = (import.meta.env.VITE_GOOGLE_CLIENT_ID || '')
    const SCOPES = 'https://www.googleapis.com/auth/drive.readonly'

    // Use Google Identity Services to get an access token
    const tokenClient = window.google?.accounts?.oauth2?.initTokenClient({
      client_id: GOOGLE_CLIENT_ID,
      scope: SCOPES,
      callback: (tokenResponse: any) => {
        if (tokenResponse.access_token) {
          openDrivePicker(tokenResponse.access_token)
        }
      }
    })

    if (tokenClient) {
      tokenClient.requestAccessToken()
    } else {
      setError('Google Drive is not available. Please try again.')
    }
  }

  const openDrivePicker = (accessToken: string) => {
    const picker = new window.google.picker.PickerBuilder()
      .addView(window.google.picker.ViewId.DOCS)
      .setOAuthToken(accessToken)
      .setCallback(async (data: any) => {
        if (data.action === 'picked') {
          const driveFiles = data.docs.map((doc: any) => ({
            id: doc.id,
            name: doc.name,
            mimeType: doc.mimeType
          }))

          setUploading(true)
          try {
            await api.post('/files/import-google-drive', {
              accessToken,
              entityType,
              entityId: String(entityId),
              files: driveFiles
            })
            await fetchFiles()
          } catch (err: any) {
            setError(err.response?.data?.error || 'Failed to import from Google Drive')
          } finally {
            setUploading(false)
          }
        }
      })
      .setMaxItems(5)
      .build()

    picker.setVisible(true)
  }

  const getFileIcon = (contentType: string) => FILE_ICONS[contentType] || 'bi-file-earmark'

  if (!entityId) {
    return (
      <div className="file-upload-section">
        <p className="file-upload-save-hint">
          <PhosphorIcon className="bi bi-info-circle"></PhosphorIcon> {t('fileUpload.saveFirst')}
        </p>
      </div>
    )
  }

  return (
    <div className="file-upload-section">
      <label className="file-upload-label">
        <PhosphorIcon className="bi bi-paperclip"></PhosphorIcon> {t('fileUpload.label')}
        <span className="file-upload-count">({files.length}/10)</span>
      </label>

      {error && (
        <div className="file-upload-error">
          {error}
          <button onClick={() => setError(null)}><PhosphorIcon className="bi bi-x"></PhosphorIcon></button>
        </div>
      )}

      {/* Drop zone */}
      <div
        className={`file-upload-dropzone ${dragOver ? 'drag-over' : ''} ${uploading ? 'uploading' : ''}`}
        onDragOver={handleDragOver}
        onDragLeave={handleDragLeave}
        onDrop={handleDrop}
        onClick={() => !uploading && fileInputRef.current?.click()}
      >
        {uploading ? (
          <>
            <div className="btn-spinner"></div>
            <span>{t('fileUpload.uploading')}</span>
          </>
        ) : (
          <>
            <PhosphorIcon className="bi bi-cloud-arrow-up"></PhosphorIcon>
            <span>{t('fileUpload.dropzone')}</span>
            <span className="file-upload-hint">{t('fileUpload.hint')}</span>
          </>
        )}
      </div>

      <input
        ref={fileInputRef}
        type="file"
        multiple
        accept=".pdf,.doc,.docx,.txt,.md,.csv,.json,.xml,.xlsx,.xls,.pptx,.ppt,.rtf"
        onChange={(e) => handleFileSelect(e.target.files)}
        style={{ display: 'none' }}
      />

      {/* Examples */}
      <button
        type="button"
        className="file-examples-toggle"
        onClick={() => setShowExamples(v => !v)}
      >
        <span><PhosphorIcon className="bi bi-lightbulb"></PhosphorIcon> {t('fileUpload.examplesToggle')}</span>
        <PhosphorIcon className={`bi bi-chevron-down file-examples-chevron ${showExamples ? 'open' : ''}`}></PhosphorIcon>
      </button>
      {showExamples && (
        <div className="file-examples-grid">
          {CONTEXT_EXAMPLES.map(ex => (
            <div key={ex.file} className="file-example-card">
              <div className="file-example-icon" style={{ color: ex.color, background: ex.color + '18' }}>
                <PhosphorIcon className={`bi ${ex.icon}`}></PhosphorIcon>
              </div>
              <div className="file-example-body">
                <div className="file-example-title-row">
                  <span className="file-example-title">{ex.title}</span>
                  <a
                    href={ex.download}
                    download={ex.file}
                    className="file-example-download"
                    title={t('fileUpload.download')}
                    onClick={e => e.stopPropagation()}
                  >
                    <PhosphorIcon className="bi bi-download"></PhosphorIcon>
                  </a>
                </div>
                <div className="file-example-file">{ex.file}</div>
                <div className="file-example-desc">{ex.desc}</div>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Google Drive button */}
      {driveLoaded && (
        <button
          className="btn btn-secondary file-upload-drive-btn"
          onClick={handleGoogleDrivePick}
          disabled={uploading}
        >
          <PhosphorIcon className="bi bi-google"></PhosphorIcon> {t('fileUpload.importDrive')}
        </button>
      )}

      {/* File list */}
      {files.length > 0 && (
        <div className="file-upload-list">
          {files.map((file) => (
            <div key={file.id} className="file-upload-item">
              <PhosphorIcon className={`bi ${getFileIcon(file.contentType)}`}></PhosphorIcon>
              <div className="file-upload-item-info">
                <span className="file-upload-item-name" title={file.fileName}>{file.fileName}</span>
                <span className="file-upload-item-meta">
                  {formatFileSize(file.fileSize)}
                  {file.source === 'google-drive' && <> · <PhosphorIcon className="bi bi-google" style={{ fontSize: '0.65rem' }}></PhosphorIcon> Drive</>}
                </span>
              </div>
              <div className="file-upload-item-actions">
                <button
                  className="file-upload-action-btn"
                  onClick={(e) => { e.stopPropagation(); handleDownload(file.id, file.fileName) }}
                  title={t('fileUpload.download')}
                >
                  <PhosphorIcon className="bi bi-download"></PhosphorIcon>
                </button>
                <button
                  className="file-upload-action-btn delete"
                  onClick={(e) => { e.stopPropagation(); handleDelete(file.id) }}
                  title="Remove"
                >
                  <PhosphorIcon className="bi bi-trash3"></PhosphorIcon>
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

export default FileUpload
