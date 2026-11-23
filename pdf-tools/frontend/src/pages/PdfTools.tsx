import PhosphorIcon from '../components/PhosphorIcon'
import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { AgentStage, useFieldView, bubble } from '../gamefield'
import type { FieldModel } from '../gamefield'
import { useOverlay } from '../components/PdfOverlayControls'
import './PdfTools.css'

const API = import.meta.env.VITE_API_URL ??
  (typeof window !== 'undefined' && window.location.hostname !== 'localhost'
    ? window.location.origin
    : 'http://localhost:5220')

type ToolKey =
  | 'merge'
  | 'split'
  | 'compress'
  | 'pdf-to-word'
  | 'pdf-to-excel'
  | 'pdf-to-powerpoint'
  | 'pdf-to-jpg'
  | 'pdf-to-html'
  | 'pdf-to-markdown'
  | 'pdf-to-text'
  | 'word-to-pdf'
  | 'excel-to-pdf'
  | 'powerpoint-to-pdf'
  | 'html-to-pdf'
  | 'markdown-to-pdf'
  | 'text-to-pdf'
  | 'images-to-pdf'
  | 'watermark'
  | 'rotate'
  | 'page-numbers'
  | 'overlay'
  | 'repair'
  | 'unlock'
  | 'protect'
  | 'ocr'
  | 'compare'
  | 'fill-form'
  | 'generate'
  | 'redact'
  | 'pdfa'
  | 'pdfa-validate'
  | 'clean-metadata'

interface ToolInfo {
  name: string
  endpoint: string
  allowMultiple: boolean
  description: string
  category: string
  group: string
  icon: string
  accept: string
  inputLabel: string
  outputExtension: string
}

interface ValidationResult {
  status: 'pass' | 'fail' | 'needs-review'
  confidence: number
  explanation: string
  checks: Array<{ name: string; status: string; detail: string }>
  remediationSuggestions?: string[]
  warnings?: string[]
}

interface CompareResult {
  summary: {
    leftPageCount: number
    rightPageCount: number
    comparedPageCount: number
    changedPageCount: number
    addedLineCount: number
    removedLineCount: number
    changedImageCount: number
    averageVisualDifferencePercent: number
  }
  pages: Array<{
    pageNumber: number
    status: string
    addedLineCount: number
    removedLineCount: number
    changedImageCount: number
    visualDifferencePercent?: number
    leftPreviewDataUrl?: string
    rightPreviewDataUrl?: string
    diffPreviewDataUrl?: string
    lines: Array<{ type: string; leftLineNumber?: number; rightLineNumber?: number; leftText: string; rightText: string }>
  }>
  warnings?: string[]
}

interface SplitInspection {
  pageCount: number
  pages: Array<{ pageNumber: number; previewDataUrl: string }>
}

interface PdfFormField {
  name: string
  type: string
  value?: string
  options: string[]
  readOnly: boolean
}

interface PdfAReport {
  claimsPdfA: boolean
  part?: string
  conformance?: string
  hasOutputIntent: boolean
  isEncrypted: boolean
  pageCount: number
  pdfVersion: string
  warnings: string[]
  note: string
}

const SAMPLE_TEMPLATE = `<html>
<head><style>
  body { font-family: Arial, sans-serif; margin: 40px; color: #111; }
  h1 { border-bottom: 3px solid #111; padding-bottom: 8px; }
  table { width: 100%; border-collapse: collapse; margin-top: 16px; }
  th { text-align: left; background: #f3f4f6; }
  th, td { padding: 8px 10px; border-bottom: 1px solid #e5e7eb; }
  .total { text-align: right; font-size: 1.2em; font-weight: bold; margin-top: 12px; }
</style></head>
<body>
  <h1>Invoice {{number}}</h1>
  <p>Billed to: <strong>{{customer.name}}</strong><br>{{customer.email}}</p>
  <table>
    <tr><th>#</th><th>Item</th><th>Amount</th></tr>
    {{#each items}}<tr><td>{{@index}}</td><td>{{name}}</td><td>{{amount}}</td></tr>{{/each}}
  </table>
  <p class="total">Total: {{total}}</p>
</body>
</html>`

const SAMPLE_DATA = `{
  "number": "INV-0042",
  "customer": { "name": "Acme Ltd", "email": "accounts@acme.example" },
  "items": [
    { "name": "Design work", "amount": "$1,200.00" },
    { "name": "Hosting (12 months)", "amount": "$300.00" }
  ],
  "total": "$1,500.00"
}`

const pdfAccept = 'application/pdf,.pdf'
const docxAccept = 'application/msword,application/vnd.openxmlformats-officedocument.wordprocessingml.document,.doc,.docx'
const xlsxAccept = 'application/vnd.ms-excel,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet,.xls,.xlsx'
const pptxAccept = 'application/vnd.ms-powerpoint,application/vnd.openxmlformats-officedocument.presentationml.presentation,.ppt,.pptx'
const htmlAccept = 'text/html,.html,.htm'
const markdownAccept = 'text/markdown,text/plain,.md,.markdown'
const textAccept = 'text/plain,.txt'
const imageAccept = 'image/jpeg,image/png,.jpg,.jpeg,.png'

const makeTool = (tool: Omit<ToolInfo, 'accept' | 'inputLabel' | 'outputExtension'> & Partial<Pick<ToolInfo, 'accept' | 'inputLabel' | 'outputExtension'>>): ToolInfo => ({
  accept: pdfAccept,
  inputLabel: tool.allowMultiple ? 'Choose PDF files' : 'Choose a PDF',
  outputExtension: 'pdf',
  ...tool,
})

const TOOLS: Record<ToolKey, ToolInfo> = {
  'pdf-to-word': makeTool({ name: 'PDF to Word', endpoint: '/api/pdf/convert/word', allowMultiple: false, description: 'Turn a PDF into a Word document that keeps the original page styling.', category: 'Convert from PDF', group: 'Turn PDF into...', icon: 'bi-file-earmark-word', outputExtension: 'docx' }),
  'pdf-to-excel': makeTool({ name: 'PDF to Excel', endpoint: '/api/pdf/convert/excel', allowMultiple: false, description: 'Turn each styled PDF page into an Excel sheet.', category: 'Convert from PDF', group: 'Turn PDF into...', icon: 'bi-file-earmark-excel', outputExtension: 'xlsx' }),
  'pdf-to-powerpoint': makeTool({ name: 'PDF to PowerPoint', endpoint: '/api/pdf/convert/powerpoint', allowMultiple: false, description: 'Turn PDF pages into slides while preserving the visual layout.', category: 'Convert from PDF', group: 'Turn PDF into...', icon: 'bi-file-earmark-slides', outputExtension: 'pptx' }),
  'pdf-to-jpg': makeTool({ name: 'PDF to JPG', endpoint: '/api/pdf/convert/images', allowMultiple: false, description: 'Convert every PDF page to an image. A multi-page PDF downloads as a ZIP of images.', category: 'Convert from PDF', group: 'Turn PDF into...', icon: 'bi-file-earmark-image', outputExtension: 'jpg' }),
  'pdf-to-html': makeTool({ name: 'PDF to HTML', endpoint: '/api/pdf/convert/html', allowMultiple: false, description: 'Turn a PDF into positioned HTML that keeps the page layout.', category: 'Convert from PDF', group: 'Turn PDF into...', icon: 'bi-filetype-html', outputExtension: 'html' }),
  'pdf-to-markdown': makeTool({ name: 'PDF to Markdown', endpoint: '/api/pdf/convert/markdown', allowMultiple: false, description: 'Extract a PDF to clean, text-first Markdown. Downloads as a ZIP: converted.md plus an images/ folder it references. Great for feeding documents to AI/LLMs.', category: 'Convert from PDF', group: 'Turn PDF into...', icon: 'bi-markdown', outputExtension: 'zip' }),
  'pdf-to-text': makeTool({ name: 'PDF to Text', endpoint: '/api/pdf/convert/text', allowMultiple: false, description: 'Extract text with layout spacing preserved as much as plain text allows.', category: 'Convert from PDF', group: 'Turn PDF into...', icon: 'bi-body-text', outputExtension: 'txt' }),
  'word-to-pdf': makeTool({ name: 'Word to PDF', endpoint: '/api/pdf/word-to-pdf', allowMultiple: true, description: 'Export Word documents to PDF; multiple files are merged in upload order.', category: 'Create PDF', group: 'Make a PDF from...', icon: 'bi-file-earmark-word', accept: docxAccept, inputLabel: 'Choose Word files', outputExtension: 'pdf' }),
  'excel-to-pdf': makeTool({ name: 'Excel to PDF', endpoint: '/api/pdf/excel-to-pdf', allowMultiple: true, description: 'Export Excel workbooks to PDF; multiple files are merged in upload order.', category: 'Create PDF', group: 'Make a PDF from...', icon: 'bi-file-earmark-excel', accept: xlsxAccept, inputLabel: 'Choose Excel files', outputExtension: 'pdf' }),
  'powerpoint-to-pdf': makeTool({ name: 'PowerPoint to PDF', endpoint: '/api/pdf/powerpoint-to-pdf', allowMultiple: true, description: 'Export PowerPoint decks to PDF; multiple files are merged in upload order.', category: 'Create PDF', group: 'Make a PDF from...', icon: 'bi-file-earmark-slides', accept: pptxAccept, inputLabel: 'Choose PowerPoint files', outputExtension: 'pdf' }),
  'html-to-pdf': makeTool({ name: 'HTML to PDF', endpoint: '/api/pdf/html-to-pdf', allowMultiple: true, description: 'Print HTML files to PDF; multiple files are merged in upload order.', category: 'Create PDF', group: 'Make a PDF from...', icon: 'bi-filetype-html', accept: htmlAccept, inputLabel: 'Choose HTML files', outputExtension: 'pdf' }),
  'markdown-to-pdf': makeTool({ name: 'Markdown to PDF', endpoint: '/api/pdf/markdown-to-pdf', allowMultiple: true, description: 'Turn Markdown files into PDF; multiple files are merged in upload order.', category: 'Create PDF', group: 'Make a PDF from...', icon: 'bi-markdown', accept: markdownAccept, inputLabel: 'Choose Markdown files', outputExtension: 'pdf' }),
  'text-to-pdf': makeTool({ name: 'Text to PDF', endpoint: '/api/pdf/text-to-pdf', allowMultiple: true, description: 'Turn text files into PDF; multiple files are merged in upload order.', category: 'Create PDF', group: 'Make a PDF from...', icon: 'bi-file-earmark-text', accept: textAccept, inputLabel: 'Choose text files', outputExtension: 'pdf' }),
  'images-to-pdf': makeTool({ name: 'Image to PDF', endpoint: '/api/pdf/images-to-pdf', allowMultiple: true, description: 'Turn JPG or PNG images into one PDF.', category: 'Create PDF', group: 'Make a PDF from...', icon: 'bi-images', accept: imageAccept, inputLabel: 'Choose image files', outputExtension: 'pdf' }),
  merge: makeTool({ name: 'Merge PDF', endpoint: '/api/pdf/merge', allowMultiple: true, description: 'Combine multiple PDFs into one document.', category: 'Edit PDF', group: 'Edit PDF', icon: 'bi-intersect' }),
  split: makeTool({ name: 'Split PDF', endpoint: '/api/pdf/split', allowMultiple: false, description: 'Pick pages to extract into one PDF, or split each selected page into its own file.', category: 'Edit PDF', group: 'Edit PDF', icon: 'bi-scissors' }),
  compress: makeTool({ name: 'Compress PDF', endpoint: '/api/pdf/compress', allowMultiple: false, description: 'Reduce PDF file size.', category: 'Edit PDF', group: 'Edit PDF', icon: 'bi-arrows-collapse' }),
  rotate: makeTool({ name: 'Rotate PDF', endpoint: '/api/pdf/rotate', allowMultiple: false, description: 'Rotate every page by 90, 180, or 270 degrees.', category: 'Edit PDF', group: 'Edit PDF', icon: 'bi-arrow-clockwise' }),
  watermark: makeTool({ name: 'Watermark PDF', endpoint: '/api/pdf/watermark', allowMultiple: false, description: 'Apply a text watermark to every page.', category: 'Edit PDF', group: 'Edit PDF', icon: 'bi-droplet' }),
  'page-numbers': makeTool({ name: 'Page Numbers', endpoint: '/api/pdf/page-numbers', allowMultiple: false, description: 'Add page numbers to the bottom of every page.', category: 'Edit PDF', group: 'Edit PDF', icon: 'bi-123' }),
  overlay: makeTool({ name: 'Header & Footer', endpoint: '/api/pdf/overlay', allowMultiple: false, description: 'Add page numbers, headers, and footers — type text, upload a logo, or both, anywhere on the page.', category: 'Edit PDF', group: 'Edit PDF', icon: 'bi-layout-text-window-reverse' }),
  repair: makeTool({ name: 'Repair PDF', endpoint: '/api/pdf/repair', allowMultiple: false, description: 'Rebuild a PDF copy when a file opens poorly.', category: 'Edit PDF', group: 'Edit PDF', icon: 'bi-wrench' }),
  unlock: makeTool({ name: 'Unlock PDF', endpoint: '/api/pdf/unlock', allowMultiple: false, description: 'Remove password protection when you know the password.', category: 'Protect PDF', group: 'Protect PDF', icon: 'bi-unlock' }),
  protect: makeTool({ name: 'Protect PDF', endpoint: '/api/pdf/protect', allowMultiple: false, description: 'Add password protection to a PDF.', category: 'Protect PDF', group: 'Protect PDF', icon: 'bi-lock' }),
  compare: makeTool({ name: 'Compare PDFs', endpoint: '/api/pdf/compare', allowMultiple: true, description: 'Compare two PDFs with text and visual differences.', category: 'Check PDF', group: 'Check PDF', icon: 'bi-layout-split', inputLabel: 'Choose two PDFs' }),
  ocr: makeTool({ name: 'OCR / Extract Text', endpoint: '/api/pdf/ocr', allowMultiple: false, description: 'Extract embedded text from a PDF.', category: 'Check PDF', group: 'Check PDF', icon: 'bi-text-paragraph', outputExtension: 'txt' }),
  'fill-form': makeTool({ name: 'Fill PDF Form', endpoint: '/api/pdf/form/fill', allowMultiple: false, description: 'Upload a fillable PDF (AcroForm), type into its fields, and download the filled copy — optionally flattened so the values become permanent.', category: 'Business', group: 'Business', icon: 'bi-input-cursor-text' }),
  generate: makeTool({ name: 'Doc Generator', endpoint: '/api/pdf/generate', allowMultiple: false, description: 'Turn an HTML template with {{placeholders}} plus JSON data into a polished PDF — invoices, contracts, and certificates at scale. Save templates for reuse.', category: 'Business', group: 'Business', icon: 'bi-braces-asterisk' }),
  redact: makeTool({ name: 'Redact PDF', endpoint: '/api/pdf/redact', allowMultiple: false, description: 'True content removal — not a black box over selectable text. Match emails, phones, card numbers, IDs, your own terms, or let AI find names and addresses.', category: 'Business', group: 'Business', icon: 'bi-eraser-fill' }),
  pdfa: makeTool({ name: 'Convert to PDF/A', endpoint: '/api/pdf/pdfa', allowMultiple: false, description: 'Convert a PDF to the PDF/A archival standard (2b or 3b) for long-term storage and compliance submissions.', category: 'Business', group: 'Business', icon: 'bi-archive' }),
  'pdfa-validate': makeTool({ name: 'Check PDF/A', endpoint: '/api/pdf/pdfa/validate', allowMultiple: false, description: 'Quick structural conformance report: does this file identify as PDF/A, carry an OutputIntent, and avoid encryption?', category: 'Business', group: 'Business', icon: 'bi-patch-check' }),
  'clean-metadata': makeTool({ name: 'Remove Metadata', endpoint: '/api/pdf/clean-metadata', allowMultiple: false, description: 'Strip author, title, and hidden XMP metadata from a PDF before sharing it externally.', category: 'Business', group: 'Business', icon: 'bi-shield-slash' }),
}

const TOOL_ORDER = Object.keys(TOOLS) as ToolKey[]
const TOOL_GROUPS = Array.from(new Set(TOOL_ORDER.map(key => TOOLS[key].group)))

// Create-PDF tools that accept an optional header/footer/page-number overlay,
// applied as a post-conversion pass on the server.
const OVERLAY_CAPABLE_CONVERTERS: ToolKey[] = [
  'word-to-pdf', 'excel-to-pdf', 'powerpoint-to-pdf', 'html-to-pdf', 'markdown-to-pdf', 'text-to-pdf', 'images-to-pdf',
]
const groupsState = (openGroup: string) => Object.fromEntries(TOOL_GROUPS.map(group => [group, group === openGroup]))

const formatSize = (bytes: number) => {
  if (!bytes) return '0 KB'
  const units = ['B', 'KB', 'MB', 'GB']
  const index = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1)
  const value = bytes / (1024 ** index)
  return `${value.toFixed(value >= 10 || index === 0 ? 0 : 1)} ${units[index]}`
}

const extensionFor = (toolName: string) => TOOLS[toolName as ToolKey]?.outputExtension ?? 'pdf'

const friendlyPdfError = (message: string) => {
  // Show the real error — LibreOffice is installed, so a generic "install it" message is misleading
  return message
}

const fileMatchesTool = (file: File, tool: ToolInfo) => {
  const name = file.name.toLowerCase()
  return tool.accept.split(',').some(rawAccept => {
    const accept = rawAccept.trim().toLowerCase()
    if (!accept) return false
    if (accept.startsWith('.')) return name.endsWith(accept)
    if (accept.endsWith('/*')) return file.type.toLowerCase().startsWith(accept.slice(0, -1))
    return file.type.toLowerCase() === accept
  })
}

export default function PdfTools() {
  const { toolName = 'merge' } = useParams()
  const activeKey = (TOOLS[toolName as ToolKey] ? toolName : 'merge') as ToolKey
  const tool = TOOLS[activeKey]
  const fileInputRef = useRef<HTMLInputElement>(null)
  const requestIdRef = useRef(0)
  const [files, setFiles] = useState<File[]>([])
  const [dragging, setDragging] = useState(false)
  const [processing, setProcessing] = useState(false)
  const [progress, setProgress] = useState(0)
  const [error, setError] = useState('')
  const [resultUrl, setResultUrl] = useState('')
  const [resultFilename, setResultFilename] = useState('')
  const [resultExtension, setResultExtension] = useState('')
  const [validation, setValidation] = useState<ValidationResult | null>(null)
  const [compareResult, setCompareResult] = useState<CompareResult | null>(null)
  const [splitInspection, setSplitInspection] = useState<SplitInspection | null>(null)
  const [selectedSplitPages, setSelectedSplitPages] = useState<number[]>([])
  const [splitSeparate, setSplitSeparate] = useState(false)
  const [inspectingSplit, setInspectingSplit] = useState(false)
  const [watermarkText, setWatermarkText] = useState('GenPrompt')
  const [degrees, setDegrees] = useState('90')
  const [password, setPassword] = useState('')
  const [openGroups, setOpenGroups] = useState<Record<string, boolean>>(() => groupsState(tool.group))

  // ── Business tools state ──
  const [formFields, setFormFields] = useState<PdfFormField[] | null>(null)
  const [formValues, setFormValues] = useState<Record<string, string>>({})
  const [inspectingForm, setInspectingForm] = useState(false)
  const [flattenForm, setFlattenForm] = useState(false)
  const [redactOpts, setRedactOpts] = useState({ emails: true, phones: true, creditCards: true, nationalIds: false, ibans: false, useAiPii: false })
  const [redactTerms, setRedactTerms] = useState('')
  const [redactNote, setRedactNote] = useState('')
  const [pdfaLevel, setPdfaLevel] = useState('2b')
  const [pdfaReport, setPdfaReport] = useState<PdfAReport | null>(null)
  const [genHtml, setGenHtml] = useState(SAMPLE_TEMPLATE)
  const [genData, setGenData] = useState(SAMPLE_DATA)
  const [templates, setTemplates] = useState<Array<{ id: number; name: string }>>([])
  const [templateId, setTemplateId] = useState<number | null>(null)
  const [templateName, setTemplateName] = useState('')
  const [savingTemplate, setSavingTemplate] = useState(false)

  const isOverlayTool = activeKey === 'overlay'
  const showsOverlay = isOverlayTool || OVERLAY_CAPABLE_CONVERTERS.includes(activeKey)
  // Standalone tool shows the controls inline; converters tuck them behind a collapsible block.
  const overlay = useOverlay({ collapsible: !isOverlayTool })

  const selectedGroup = useMemo(() => TOOL_ORDER.filter(key => TOOLS[key].category === tool.category), [tool.category])

  const fileLimit = activeKey === 'compare' ? 2 : tool.allowMultiple ? Number.POSITIVE_INFINITY : 1

  useEffect(() => {
    setOpenGroups(current => ({ ...current, [tool.group]: true }))
  }, [tool.group])

  const toggleGroup = (group: string) => {
    setOpenGroups(current => ({ ...current, [group]: !current[group] }))
  }

  useEffect(() => {
    requestIdRef.current += 1
    setProcessing(false)
    setProgress(0)
    setError('')
    setResultUrl(current => {
      if (current) URL.revokeObjectURL(current)
      return ''
    })
    setResultFilename('')
    setCompareResult(null)
    setValidation(null)
    setSplitInspection(null)
    setSelectedSplitPages([])
    setSplitSeparate(false)
    setPassword('')
    setFormFields(null)
    setFormValues({})
    setFlattenForm(false)
    setRedactNote('')
    setPdfaReport(null)
    setFiles(current => current.slice(0, Number.isFinite(fileLimit) ? fileLimit : current.length))
    if (fileInputRef.current) fileInputRef.current.value = ''
  }, [activeKey, fileLimit])

  // Fill-form: inspect the uploaded PDF's fields as soon as it lands.
  useEffect(() => {
    if (activeKey !== 'fill-form' || files.length !== 1) {
      setFormFields(null)
      setFormValues({})
      setInspectingForm(false)
      return
    }
    const requestId = ++requestIdRef.current
    const inspect = async () => {
      setInspectingForm(true)
      setError('')
      try {
        const token = localStorage.getItem('authToken')
        const form = new FormData()
        form.append('file', files[0])
        const response = await fetch(`${API}/api/pdf/form/fields`, {
          method: 'POST',
          headers: token ? { Authorization: `Bearer ${token}` } : undefined,
          body: form,
        })
        if (!response.ok) {
          const payload = await response.json().catch(() => null)
          throw new Error(payload?.error ?? `Server error ${response.status}`)
        }
        const payload = await response.json()
        if (requestId !== requestIdRef.current) return
        const fields: PdfFormField[] = payload.fields ?? []
        setFormFields(fields)
        setFormValues(Object.fromEntries(fields.map(f => [f.name, f.value && f.value !== 'false' ? f.value : ''])))
      } catch (err: any) {
        if (requestId === requestIdRef.current) setError(err.message ?? 'Could not inspect the form fields.')
      } finally {
        if (requestId === requestIdRef.current) setInspectingForm(false)
      }
    }
    inspect()
  }, [activeKey, files])

  // Doc generator: load the user's saved templates.
  useEffect(() => {
    if (activeKey !== 'generate') return
    const token = localStorage.getItem('authToken')
    fetch(`${API}/api/pdf/templates`, { headers: token ? { Authorization: `Bearer ${token}` } : undefined })
      .then(r => (r.ok ? r.json() : []))
      .then(rows => setTemplates(Array.isArray(rows) ? rows : []))
      .catch(() => { /* list is a convenience — ignore */ })
  }, [activeKey])

  const openTemplate = async (id: number) => {
    try {
      const token = localStorage.getItem('authToken')
      const r = await fetch(`${API}/api/pdf/templates/${id}`, { headers: token ? { Authorization: `Bearer ${token}` } : undefined })
      if (!r.ok) return
      const t = await r.json()
      setTemplateId(t.id)
      setTemplateName(t.name ?? '')
      setGenHtml(t.html ?? '')
      if (t.sampleDataJson) setGenData(t.sampleDataJson)
    } catch { /* ignore */ }
  }

  const saveTemplate = async () => {
    if (savingTemplate || !genHtml.trim()) return
    setSavingTemplate(true)
    try {
      const token = localStorage.getItem('authToken')
      const r = await fetch(`${API}/api/pdf/templates`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
        body: JSON.stringify({ id: templateId, name: templateName || 'Untitled template', html: genHtml, sampleDataJson: genData || null }),
      })
      if (r.ok) {
        const saved = await r.json()
        setTemplateId(saved.id)
        const list = await fetch(`${API}/api/pdf/templates`, { headers: token ? { Authorization: `Bearer ${token}` } : undefined })
        if (list.ok) setTemplates(await list.json())
      }
    } catch { /* ignore */ }
    finally { setSavingTemplate(false) }
  }

  const acceptFiles = (incoming: FileList | File[]) => {
    const pdfs = Array.from(incoming).filter(file => fileMatchesTool(file, tool))
    const limit = Number.isFinite(fileLimit) ? fileLimit : pdfs.length
    setFiles(pdfs.slice(0, limit))
    setError(pdfs.length ? '' : `Please choose the right file type for ${tool.name}.`)
    setResultUrl(current => {
      if (current) URL.revokeObjectURL(current)
      return ''
    })
    setResultFilename('')
    setCompareResult(null)
    setValidation(null)
    setSplitInspection(null)
    setSelectedSplitPages([])
    setPdfaReport(null)
    setRedactNote('')
  }

  const removeFile = (index: number) => setFiles(current => current.filter((_, i) => i !== index))

  // Fully reset the workspace so the next document starts from a clean slate.
  const startOver = () => {
    requestIdRef.current += 1
    setFiles([])
    setError('')
    setProcessing(false)
    setProgress(0)
    setResultUrl(current => {
      if (current) URL.revokeObjectURL(current)
      return ''
    })
    setResultFilename('')
    setCompareResult(null)
    setValidation(null)
    setSplitInspection(null)
    setSelectedSplitPages([])
    setSplitSeparate(false)
    setPassword('')
    setPdfaReport(null)
    setRedactNote('')
    setFormFields(null)
    setFormValues({})
    if (fileInputRef.current) fileInputRef.current.value = ''
  }

  useEffect(() => {
    if (activeKey !== 'split' || files.length !== 1) {
      setSplitInspection(null)
      setSelectedSplitPages([])
      setInspectingSplit(false)
      return
    }

    const requestId = ++requestIdRef.current
    const inspect = async () => {
      setInspectingSplit(true)
      setError('')
      try {
        const token = localStorage.getItem('authToken')
        const form = new FormData()
        form.append('file', files[0])
        const response = await fetch(`${API}/api/pdf/split/inspect`, {
          method: 'POST',
          headers: token ? { Authorization: `Bearer ${token}` } : undefined,
          body: form,
        })
        if (!response.ok) {
          const payload = await response.json().catch(() => null)
          throw new Error(payload?.error ?? `Server error ${response.status}`)
        }

        const payload = await response.json() as SplitInspection
        if (requestId !== requestIdRef.current) return
        setSplitInspection(payload)
        setSelectedSplitPages(payload.pages.map(page => page.pageNumber))
      } catch (err: any) {
        if (requestId === requestIdRef.current) setError(err.message ?? 'Could not inspect the PDF pages.')
      } finally {
        if (requestId === requestIdRef.current) setInspectingSplit(false)
      }
    }

    inspect()
  }, [activeKey, files])

  const toggleSplitPage = (pageNumber: number) => {
    setSelectedSplitPages(current =>
      current.includes(pageNumber)
        ? current.filter(page => page !== pageNumber)
        : [...current, pageNumber].sort((a, b) => a - b)
    )
  }

  const setAllSplitPages = () => setSelectedSplitPages(splitInspection?.pages.map(page => page.pageNumber) ?? [])
  const clearSplitPages = () => setSelectedSplitPages([])

  const appendOptions = (form: FormData) => {
    if (activeKey === 'watermark') {
      if (!watermarkText.trim()) {
        setError('Please enter watermark text.')
        return false
      }
      form.append('text', watermarkText.trim())
    }
    if (activeKey === 'rotate') form.append('degrees', degrees)
    if (activeKey === 'split') {
      if (!selectedSplitPages.length) {
        setError('Select at least one page to split into the output PDF.')
        return false
      }
      form.append('pages', selectedSplitPages.join(','))
      form.append('separate', String(splitSeparate))
    }
    if (activeKey === 'protect') {
      if (!password) {
        setError('Please enter a password for the protected PDF.')
        return false
      }
      form.append('userPassword', password)
      form.append('ownerPassword', password)
    }
    if (activeKey === 'unlock' && password) form.append('password', password)
    if (activeKey === 'fill-form') {
      const filled = Object.fromEntries(Object.entries(formValues).filter(([, v]) => v !== ''))
      if (!Object.keys(filled).length) {
        setError('Fill in at least one field first.')
        return false
      }
      form.append('fields', JSON.stringify(filled))
      form.append('flatten', String(flattenForm))
    }
    if (activeKey === 'redact') {
      const terms = redactTerms.split(/[,\n]/).map(t => t.trim()).filter(Boolean)
      if (!terms.length && !redactOpts.emails && !redactOpts.phones && !redactOpts.creditCards && !redactOpts.nationalIds && !redactOpts.ibans && !redactOpts.useAiPii) {
        setError('Pick at least one thing to redact.')
        return false
      }
      form.append('options', JSON.stringify({ ...redactOpts, terms }))
    }
    if (activeKey === 'pdfa') form.append('level', pdfaLevel)
    if (isOverlayTool) {
      const overlayError = overlay.append(form, { optionsField: 'options', imagesField: 'images' }, true)
      if (overlayError) {
        setError(overlayError)
        return false
      }
    } else if (OVERLAY_CAPABLE_CONVERTERS.includes(activeKey)) {
      overlay.append(form, { optionsField: 'overlayOptions', imagesField: 'overlayImages' }, false)
    }
    return true
  }

  const buildValidationOptions = () => ({
    watermarkText,
    degrees,
    hasPassword: Boolean(password),
  })

  const validateOutcome = async (
    operation: ToolKey,
    sourceFiles: File[],
    outputBlob: Blob | null,
    outputFilename: string,
    resultPayload?: CompareResult
  ) => {
    const token = localStorage.getItem('authToken')
    const form = new FormData()
    form.append('operation', operation)
    sourceFiles.forEach(file => form.append('originalFiles', file))
    form.append('optionsJson', JSON.stringify(buildValidationOptions()))
    form.append('warningsJson', JSON.stringify(resultPayload?.warnings ?? []))
    if (resultPayload) form.append('resultJson', JSON.stringify(resultPayload))
    if (outputBlob) {
      form.append('outputFile', new File([outputBlob], outputFilename, {
        type: outputBlob.type || 'application/octet-stream',
      }))
    }

    const response = await fetch(`${API}/api/pdf/validate`, {
      method: 'POST',
      headers: token ? { Authorization: `Bearer ${token}` } : undefined,
      body: form,
    })
    if (response.ok) return await response.json()
    return null
  }

  // Doc generator posts JSON (no file upload) and downloads the rendered PDF.
  const runGenerate = async () => {
    setError('')
    if (!genHtml.trim()) {
      setError('Add template HTML first.')
      return
    }
    let data: unknown
    if (genData.trim()) {
      try { data = JSON.parse(genData) }
      catch { setError('The data must be valid JSON.'); return }
    }
    const token = localStorage.getItem('authToken')
    const requestId = ++requestIdRef.current
    setProcessing(true)
    setProgress(15)
    setResultUrl(current => { if (current) URL.revokeObjectURL(current); return '' })
    setResultFilename('')
    const timer = window.setInterval(() => setProgress(value => Math.min(90, value + 15)), 500)
    try {
      const response = await fetch(`${API}/api/pdf/generate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
        body: JSON.stringify({ html: genHtml, data }),
      })
      if (!response.ok) {
        const payload = await response.json().catch(() => null)
        throw new Error(payload?.error ?? `Server error ${response.status}`)
      }
      const blob = await response.blob()
      const url = URL.createObjectURL(blob)
      if (requestId !== requestIdRef.current) { URL.revokeObjectURL(url); return }
      setResultFilename(templateName.trim() || 'generated-document')
      setResultExtension('pdf')
      setResultUrl(url)
      setProgress(100)
    } catch (err: any) {
      setError(err.message ?? 'Generation failed. Please try again.')
    } finally {
      window.clearInterval(timer)
      if (requestId === requestIdRef.current) setProcessing(false)
    }
  }

  const processFiles = async () => {
    setError('')
    if (activeKey === 'generate') {
      await runGenerate()
      return
    }
    if (!files.length) {
      setError(`Please add a file for ${tool.name} first.`)
      return
    }
    if (activeKey === 'compare' && files.length !== 2) {
      setError('Please add exactly two PDF files to compare.')
      return
    }

    const operation = activeKey
    const sourceFiles = files.slice(0, Number.isFinite(fileLimit) ? fileLimit : files.length)
    const form = new FormData()
    if (activeKey === 'compare') {
      form.append('leftFile', sourceFiles[0])
      form.append('rightFile', sourceFiles[1])
    } else if (tool.allowMultiple) {
      sourceFiles.forEach(file => form.append('files', file))
    } else {
      form.append('file', sourceFiles[0])
    }
    if (!appendOptions(form)) return

    const token = localStorage.getItem('authToken')
    const requestId = ++requestIdRef.current
    setProcessing(true)
    setProgress(15)
    setResultUrl(current => {
      if (current) URL.revokeObjectURL(current)
      return ''
    })
    setResultFilename('')
    setCompareResult(null)
    setValidation(null)

    const timer = window.setInterval(() => setProgress(value => Math.min(90, value + 15)), 500)
    try {
      const response = await fetch(`${API}${tool.endpoint}`, {
        method: 'POST',
        headers: token ? { Authorization: `Bearer ${token}` } : undefined,
        body: form,
      })
      if (!response.ok) {
        const payload = await response.json().catch(() => null)
        throw new Error(payload?.error ?? `Server error ${response.status}`)
      }

      if (activeKey === 'compare') {
        const payload = await response.json()
        if (requestId !== requestIdRef.current) return
        setCompareResult(payload)
        setProgress(100)
        const validationPayload = await validateOutcome(operation, sourceFiles, null, '', payload)
        if (requestId === requestIdRef.current && validationPayload) setValidation(validationPayload)
        return
      }

      if (activeKey === 'pdfa-validate') {
        const payload = await response.json()
        if (requestId !== requestIdRef.current) return
        setPdfaReport(payload)
        setProgress(100)
        return
      }

      if (activeKey === 'redact') {
        const count = response.headers.get('X-Redaction-Count')
        const summary = response.headers.get('X-Redaction-Summary')
        setRedactNote(count != null
          ? `${count} match${count === '1' ? '' : 'es'} redacted${summary ? ` — ${summary}` : ''}`
          : '')
      }

      const blob = await response.blob()
      const isZip = (response.headers.get('Content-Type') || '').includes('application/zip')
      const extension = isZip ? 'zip' : (activeKey === 'split' && splitSeparate ? 'zip' : extensionFor(activeKey))
      const baseName = (sourceFiles?.[0]?.name ?? 'document').replace(/\.[^./\\]+$/, '').trim() || 'document'
      const filename = `${baseName}.${extension}`
      const url = URL.createObjectURL(blob)
      if (requestId !== requestIdRef.current) {
        URL.revokeObjectURL(url)
        return
      }
      setResultFilename(baseName)
      setResultExtension(extension)
      setResultUrl(url)
      setProgress(100)
      const validationPayload = await validateOutcome(operation, sourceFiles, blob, filename)
      if (requestId === requestIdRef.current && validationPayload) setValidation(validationPayload)
    } catch (err: any) {
      setError(friendlyPdfError(err.message ?? 'Error processing file. Please try again.'))
    } finally {
      window.clearInterval(timer)
      if (requestId === requestIdRef.current) setProcessing(false)
    }
  }

  const { stageProps } = useFieldView('snappdf')

  // Gamefield: the print room. An intake clerk takes the files, the press runs
  // the chosen tool, and the finishing desk hands back the result.
  const field = useMemo<FieldModel>(() => {
    const desks = files.slice(0, 10).map((f, i) => ({
      id: `file-${i}`,
      name: f.name,
      role: `${(f.size / 1024).toFixed(0)} KB`,
      status: (error ? 'failed' : resultUrl ? 'done' : processing ? 'working' : 'idle') as 'failed' | 'done' | 'working' | 'idle',
      detail: processing ? 'On the press…' : undefined,
    }))

    return {
      title: 'Snappdf',
      subtitle: tool.name,
      running: processing || inspectingSplit,
      agents: [
        ...desks,
        {
          id: 'press',
          name: 'The Press',
          role: tool.name.toLowerCase(),
          status: (error ? 'failed' : processing ? 'working' : resultUrl ? 'done' : 'idle') as 'failed' | 'working' | 'done' | 'idle',
          detail: error ? bubble(error)
            : processing ? `${tool.name}${progress ? ` — ${progress}%` : '…'}`
            : resultUrl ? `Ready: ${resultFilename}` : undefined,
          progress: processing && progress ? progress / 100 : undefined,
          output: resultUrl ? `${resultFilename}${resultExtension}` : undefined,
          boss: true,
        },
      ],
      messages: resultUrl ? desks.map(d => ({ id: `out-${d.id}`, from: d.id, to: 'press' })) : [],
      events: [
        ...(files.length ? [{ id: 'in', text: `${files.length} file${files.length === 1 ? '' : 's'} in the tray.`, tone: 'info' as const }] : []),
        ...(resultUrl ? [{ id: 'out', text: `${tool.name} finished — ${resultFilename}`, tone: 'good' as const }] : []),
        ...(error ? [{ id: 'err', text: error, tone: 'bad' as const }] : []),
      ],
    }
  }, [files, processing, progress, resultUrl, resultFilename, resultExtension, error, tool, inspectingSplit])

  return (
    <div className="pdf-tools-page">
      <AgentStage {...stageProps} model={field} />
      <aside className="pdf-tool-rail">
        <div className="pdf-tool-rail-title">
          <PhosphorIcon className="bi bi-file-earmark-pdf" />
          <span>SnapPDF</span>
        </div>
        <div className="pdf-tool-links">
          {TOOL_GROUPS.map(group => (
            <div className="pdf-tool-group" key={group}>
              <button
                type="button"
                className="pdf-tool-group-label"
                aria-expanded={Boolean(openGroups[group])}
                onClick={() => toggleGroup(group)}
              >
                <span>{group}</span>
                <PhosphorIcon className={`bi ${openGroups[group] ? 'bi-chevron-up' : 'bi-chevron-down'}`} />
              </button>
              {openGroups[group] && (
                <div className="pdf-tool-group-items">
                  {TOOL_ORDER.filter(key => TOOLS[key].group === group).map(key => (
                    <Link key={key} to={`/pdf-tools/${key}`} className={`pdf-tool-link${key === activeKey ? ' active' : ''}`}>
                      <PhosphorIcon className={`bi ${TOOLS[key].icon}`} />
                      <span>{TOOLS[key].name}</span>
                    </Link>
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>
      </aside>

      <main className="pdf-tool-workspace">
        <section className="pdf-tool-header">
          <div>
            <span className="pdf-tool-kicker">{tool.category}</span>
            <h1>{tool.name}</h1>
            <p>{tool.description}</p>
          </div>
          <div className="pdf-tool-neighbors">
            {selectedGroup.map(key => (
              <Link key={key} to={`/pdf-tools/${key}`} className={key === activeKey ? 'active' : ''}>{TOOLS[key].name}</Link>
            ))}
          </div>
        </section>

        <section className="pdf-tool-panel">
          {activeKey !== 'generate' && (
          <div
            className={`pdf-drop-zone${dragging ? ' dragging' : ''}`}
            onDragOver={event => { event.preventDefault(); setDragging(true) }}
            onDragLeave={() => setDragging(false)}
            onDrop={event => { event.preventDefault(); setDragging(false); acceptFiles(event.dataTransfer.files) }}
            onClick={() => fileInputRef.current?.click()}
          >
            <input
              ref={fileInputRef}
              type="file"
              multiple={tool.allowMultiple}
              accept={tool.accept}
              hidden
              onChange={event => {
                if (event.target.files?.length) acceptFiles(event.target.files)
                // Clear so choosing the SAME file again still fires onChange —
                // otherwise a second conversion of one file needs a page refresh.
                event.target.value = ''
              }}
            />
            <PhosphorIcon className={`bi ${tool.icon}`} />
            <strong>{tool.inputLabel}</strong>
            <span>Drag files here or browse from your computer.</span>
          </div>
          )}

          {files.length > 0 && (
            <div className="pdf-file-list">
              {files.map((file, index) => (
                <div className="pdf-file-row" key={`${file.name}-${index}`}>
                  <PhosphorIcon className={`bi ${tool.icon}`} />
                  <div>
                    <strong>{file.name}</strong>
                    <span>{formatSize(file.size)}</span>
                  </div>
                  <button className="pdf-file-remove" type="button" onClick={() => removeFile(index)} aria-label="Remove file">
                    <PhosphorIcon className="bi bi-x-lg" />
                  </button>
                </div>
              ))}
            </div>
          )}

          {activeKey === 'split' && files.length === 1 && (
            <div className="pdf-split-picker">
              <div className="pdf-split-header">
                <div>
                  <span>Pages</span>
                  <strong>
                    {inspectingSplit
                      ? 'Inspecting PDF...'
                      : splitInspection
                        ? `${splitInspection.pageCount} pages found`
                        : 'Upload a PDF to inspect pages'}
                  </strong>
                </div>
                {splitInspection && (
                  <div className="pdf-split-actions">
                    <button type="button" onClick={setAllSplitPages}>Select all</button>
                    <button type="button" onClick={clearSplitPages}>Clear</button>
                  </div>
                )}
              </div>

              {inspectingSplit && (
                <div className="pdf-split-loading">
                  <span className="pdf-spinner dark" />
                  Preparing page previews...
                </div>
              )}

              {splitInspection && (
                <>
                  <div className="pdf-split-summary">
                    {selectedSplitPages.length} of {splitInspection.pageCount} pages selected
                    {splitSeparate ? ' — each saved as a separate PDF in a ZIP.' : ' for the output PDF.'}
                  </div>
                  <label className="pdf-split-mode">
                    <input
                      type="checkbox"
                      checked={splitSeparate}
                      onChange={event => setSplitSeparate(event.target.checked)}
                    />
                    <span>Save each selected page as a separate file (ZIP)</span>
                  </label>
                  <div className="pdf-page-grid">
                    {splitInspection.pages.map(page => {
                      const selected = selectedSplitPages.includes(page.pageNumber)
                      return (
                        <button
                          type="button"
                          className={`pdf-page-tile${selected ? ' selected' : ''}`}
                          key={page.pageNumber}
                          aria-pressed={selected}
                          onClick={() => toggleSplitPage(page.pageNumber)}
                        >
                          <img src={page.previewDataUrl} alt={`Page ${page.pageNumber} preview`} />
                          <span>
                            <PhosphorIcon className={`bi ${selected ? 'bi-check-circle-fill' : 'bi-circle'}`} />
                            Page {page.pageNumber}
                          </span>
                        </button>
                      )
                    })}
                  </div>
                </>
              )}
            </div>
          )}

          {(activeKey === 'watermark' || activeKey === 'rotate' || activeKey === 'protect' || activeKey === 'unlock') && (
            <div className="pdf-options-grid">
              {activeKey === 'watermark' && (
                <label>
                  <span>Watermark text</span>
                  <input value={watermarkText} onChange={event => setWatermarkText(event.target.value)} />
                </label>
              )}
              {activeKey === 'rotate' && (
                <label>
                  <span>Rotation</span>
                  <select value={degrees} onChange={event => setDegrees(event.target.value)}>
                    <option value="90">90 degrees clockwise</option>
                    <option value="180">180 degrees</option>
                    <option value="270">270 degrees clockwise</option>
                  </select>
                </label>
              )}
              {(activeKey === 'protect' || activeKey === 'unlock') && (
                <label>
                  <span>{activeKey === 'protect' ? 'New password' : 'Current password'}</span>
                  <input type="password" value={password} onChange={event => setPassword(event.target.value)} />
                </label>
              )}
            </div>
          )}

          {/* ── Fill PDF Form: field editor ── */}
          {activeKey === 'fill-form' && files.length === 1 && (
            <div className="pdf-form-editor">
              {inspectingForm && (
                <div className="pdf-split-loading"><span className="pdf-spinner dark" /> Reading form fields…</div>
              )}
              {formFields && formFields.length === 0 && !inspectingForm && (
                <div className="pdf-error"><PhosphorIcon className="bi bi-info-circle" /> This PDF has no fillable form fields (AcroForm). Try the Header &amp; Footer tool to stamp text instead.</div>
              )}
              {formFields && formFields.length > 0 && (
                <>
                  <div className="pdf-split-summary">{formFields.length} fillable field{formFields.length === 1 ? '' : 's'} found — leave a field empty to keep its current value.</div>
                  <div className="pdf-options-grid pdf-form-fields">
                    {formFields.map(field => (
                      <label key={field.name}>
                        <span>{field.name}{field.readOnly ? ' (read-only)' : ''} <em className="pdf-field-type">{field.type}</em></span>
                        {field.type === 'checkbox' ? (
                          <select value={formValues[field.name] ?? ''} onChange={e => setFormValues(v => ({ ...v, [field.name]: e.target.value }))} disabled={field.readOnly}>
                            <option value="">(leave as is)</option>
                            <option value="true">Checked</option>
                            <option value="false">Unchecked</option>
                          </select>
                        ) : field.options.length > 0 ? (
                          <select value={formValues[field.name] ?? ''} onChange={e => setFormValues(v => ({ ...v, [field.name]: e.target.value }))} disabled={field.readOnly}>
                            <option value="">(leave as is)</option>
                            {field.options.map(opt => <option key={opt} value={opt}>{opt}</option>)}
                          </select>
                        ) : (
                          <input value={formValues[field.name] ?? ''} onChange={e => setFormValues(v => ({ ...v, [field.name]: e.target.value }))} disabled={field.readOnly || field.type === 'signature' || field.type === 'button'} placeholder={field.type === 'signature' ? 'signature fields cannot be filled here' : ''} />
                        )}
                      </label>
                    ))}
                  </div>
                  <label className="pdf-split-mode">
                    <input type="checkbox" checked={flattenForm} onChange={e => setFlattenForm(e.target.checked)} />
                    <span>Flatten the result (values become permanent page content — no longer editable)</span>
                  </label>
                </>
              )}
            </div>
          )}

          {/* ── Redact: what to remove ── */}
          {activeKey === 'redact' && (
            <div className="pdf-redact-options">
              <div className="pdf-split-summary">Choose what to find and permanently remove. Redacted pages are rebuilt from images, so the matched text no longer exists in the file.</div>
              <div className="pdf-check-row">
                {([['emails', 'Email addresses'], ['phones', 'Phone numbers'], ['creditCards', 'Card numbers'], ['nationalIds', 'SSN-style IDs'], ['ibans', 'IBANs']] as const).map(([key, label]) => (
                  <label key={key} className="pdf-check">
                    <input type="checkbox" checked={redactOpts[key]} onChange={e => setRedactOpts(o => ({ ...o, [key]: e.target.checked }))} />
                    <span>{label}</span>
                  </label>
                ))}
                <label className="pdf-check pdf-check-ai">
                  <input type="checkbox" checked={redactOpts.useAiPii} onChange={e => setRedactOpts(o => ({ ...o, useAiPii: e.target.checked }))} />
                  <span><PhosphorIcon className="bi bi-stars" /> AI: find names &amp; addresses</span>
                </label>
              </div>
              <label className="pdf-terms-label">
                <span>Specific words or phrases (comma or newline separated)</span>
                <textarea value={redactTerms} onChange={e => setRedactTerms(e.target.value)} rows={2} placeholder="e.g. Project Nightingale, Jane Doe" />
              </label>
            </div>
          )}

          {/* ── PDF/A level ── */}
          {activeKey === 'pdfa' && (
            <div className="pdf-options-grid">
              <label>
                <span>Conformance level</span>
                <select value={pdfaLevel} onChange={e => setPdfaLevel(e.target.value)}>
                  <option value="2b">PDF/A-2b (recommended)</option>
                  <option value="3b">PDF/A-3b (allows embedded files)</option>
                </select>
              </label>
            </div>
          )}

          {/* ── Doc generator ── */}
          {activeKey === 'generate' && (
            <div className="pdf-generator">
              <div className="pdf-gen-toolbar">
                <input
                  className="pdf-gen-name"
                  value={templateName}
                  onChange={e => setTemplateName(e.target.value)}
                  placeholder="Template name"
                />
                <button type="button" className="pdf-gen-save" onClick={saveTemplate} disabled={savingTemplate}>
                  {savingTemplate ? <span className="pdf-spinner dark" /> : <PhosphorIcon className="bi bi-floppy" />} {templateId ? 'Update template' : 'Save template'}
                </button>
                {templates.length > 0 && (
                  <select
                    className="pdf-gen-open"
                    value=""
                    onChange={e => { if (e.target.value) openTemplate(Number(e.target.value)) }}
                  >
                    <option value="">Open saved template…</option>
                    {templates.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}
                  </select>
                )}
              </div>
              <div className="pdf-gen-grid">
                <label>
                  <span>Template HTML — use {'{{name}}'}, {'{{customer.name}}'}, {'{{#each items}}…{{/each}}'}</span>
                  <textarea value={genHtml} onChange={e => setGenHtml(e.target.value)} rows={14} spellCheck={false} />
                </label>
                <label>
                  <span>Data (JSON)</span>
                  <textarea value={genData} onChange={e => setGenData(e.target.value)} rows={14} spellCheck={false} />
                </label>
              </div>
            </div>
          )}

          {showsOverlay && overlay.node}

          {error && <div className="pdf-error"><PhosphorIcon className="bi bi-exclamation-triangle" /> {error}</div>}

          <button
            className="pdf-run-button"
            type="button"
            disabled={processing || (activeKey !== 'generate' && !files.length) || inspectingSplit || inspectingForm || (activeKey === 'split' && !selectedSplitPages.length)}
            onClick={processFiles}
          >
            {processing ? <><span className="pdf-spinner" /> Processing...</> : <><PhosphorIcon className="bi bi-play-fill" /> Run {tool.name}</>}
          </button>

          {processing && (
            <div className="pdf-progress">
              <div style={{ width: `${progress}%` }} />
            </div>
          )}
        </section>

        {resultUrl && (
          <section className="pdf-result-card">
            <PhosphorIcon className="bi bi-check-circle-fill" />
            <div>
              <h2>Your file is ready</h2>
              <p>{activeKey === 'redact' && redactNote ? redactNote : 'Rename it if you like, then download.'}</p>
              <div className="pdf-result-name">
                <input
                  value={resultFilename}
                  onChange={e => setResultFilename(e.target.value)}
                  placeholder="File name"
                  aria-label="Download file name"
                  spellCheck={false}
                />
                <span className="pdf-result-ext">.{resultExtension}</span>
              </div>
            </div>
            <div className="pdf-result-actions">
              <a
                href={resultUrl}
                download={`${(resultFilename.trim() || 'document').replace(/\.[^./\\]+$/, '')}.${resultExtension}`}
              >
                <PhosphorIcon className="bi bi-download" /> Download
              </a>
              <button type="button" className="pdf-start-over" onClick={startOver}>
                <PhosphorIcon className="bi bi-arrow-repeat" /> Convert another
              </button>
            </div>
          </section>
        )}

        {pdfaReport && (
          <section className={`pdf-validation ${pdfaReport.claimsPdfA && pdfaReport.hasOutputIntent && pdfaReport.warnings.length === 0 ? 'pass' : pdfaReport.claimsPdfA ? 'needs-review' : 'fail'}`}>
            <div className="pdf-validation-head">
              <div>
                <span>PDF/A check</span>
                <h2>
                  {pdfaReport.claimsPdfA
                    ? `Identifies as PDF/A-${pdfaReport.part ?? '?'}${pdfaReport.conformance ?? ''}`
                    : 'Not a PDF/A document'}
                </h2>
                <p>{pdfaReport.note}</p>
              </div>
              <strong>{pdfaReport.pageCount} pg</strong>
            </div>
            <div className="pdf-validation-checks">
              <div className={pdfaReport.claimsPdfA ? 'pass' : 'fail'}>
                <strong>XMP identification</strong>
                <p>{pdfaReport.claimsPdfA ? `pdfaid:part ${pdfaReport.part}, conformance ${pdfaReport.conformance ?? 'n/a'}` : 'No PDF/A identification in XMP metadata.'}</p>
              </div>
              <div className={pdfaReport.hasOutputIntent ? 'pass' : 'fail'}>
                <strong>OutputIntent</strong>
                <p>{pdfaReport.hasOutputIntent ? 'Colour OutputIntent present.' : 'Missing the required colour OutputIntent.'}</p>
              </div>
              <div className={pdfaReport.isEncrypted ? 'fail' : 'pass'}>
                <strong>Encryption</strong>
                <p>{pdfaReport.isEncrypted ? 'Encrypted — PDF/A forbids encryption.' : 'Not encrypted.'}</p>
              </div>
              {pdfaReport.warnings.map((w, i) => (
                <div key={i} className="needs-review"><strong>Warning</strong><p>{w}</p></div>
              ))}
            </div>
            <div className="pdf-result-actions" style={{ justifyContent: 'flex-end', marginTop: 12 }}>
              <button type="button" className="pdf-start-over" onClick={startOver}>
                <PhosphorIcon className="bi bi-arrow-repeat" /> Check another file
              </button>
            </div>
          </section>
        )}

        {compareResult && (
          <section className="pdf-compare">
            <div className="pdf-result-actions" style={{ justifyContent: 'flex-end' }}>
              <button type="button" className="pdf-start-over" onClick={startOver}>
                <PhosphorIcon className="bi bi-arrow-repeat" /> Compare other files
              </button>
            </div>
            <div className="pdf-metrics">
              <Metric label="Pages" value={`${compareResult.summary.changedPageCount}/${compareResult.summary.comparedPageCount}`} />
              <Metric label="Added" value={compareResult.summary.addedLineCount} />
              <Metric label="Removed" value={compareResult.summary.removedLineCount} />
              <Metric label="Images" value={compareResult.summary.changedImageCount} />
              <Metric label="Visual" value={`${compareResult.summary.averageVisualDifferencePercent}%`} />
            </div>
            {compareResult.pages.slice(0, 12).map(page => (
              <article className="pdf-compare-page" key={page.pageNumber}>
                <header>
                  <h2>Page {page.pageNumber}</h2>
                  <span>{page.status}</span>
                </header>
                {(page.leftPreviewDataUrl || page.rightPreviewDataUrl || page.diffPreviewDataUrl) && (
                  <div className="pdf-preview-grid">
                    {page.leftPreviewDataUrl && <img src={page.leftPreviewDataUrl} alt="Original page preview" />}
                    {page.rightPreviewDataUrl && <img src={page.rightPreviewDataUrl} alt="Revised page preview" />}
                    {page.diffPreviewDataUrl && <img src={page.diffPreviewDataUrl} alt="Visual difference preview" />}
                  </div>
                )}
                <div className="pdf-lines">
                  {page.lines.filter(line => line.type !== 'unchanged').slice(0, 80).map((line, index) => (
                    <div className={`pdf-line ${line.type}`} key={index}>
                      <span>{line.leftLineNumber ?? line.rightLineNumber ?? ''}</span>
                      <code>{line.leftText || line.rightText}</code>
                    </div>
                  ))}
                </div>
              </article>
            ))}
          </section>
        )}

        {validation && (
          <section className={`pdf-validation ${validation.status}`}>
            <div className="pdf-validation-head">
              <div>
                <span>Validation agent</span>
                <h2>{validation.status === 'pass' ? 'Outcome passed validation' : validation.status === 'fail' ? 'Outcome failed validation' : 'Outcome needs review'}</h2>
                <p>{validation.explanation}</p>
              </div>
              <strong>{Math.round(validation.confidence * 100)}%</strong>
            </div>
            <div className="pdf-validation-checks">
              {validation.checks.map(check => (
                <div key={`${check.name}-${check.detail}`} className={check.status}>
                  <strong>{check.name}</strong>
                  <p>{check.detail}</p>
                </div>
              ))}
            </div>
          </section>
        )}
      </main>
    </div>
  )
}

function Metric({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="pdf-metric">
      <span>{label}</span>
      <strong>{value}</strong>
    </div>
  )
}
