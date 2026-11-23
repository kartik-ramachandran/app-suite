import PhosphorIcon from './PhosphorIcon'
import { useState } from 'react'
import { downloadHtml, printToPdf, exportStamp } from '../utils/exportDoc'
import './ExportButtons.css'

interface Props {
  /** Document title shown at the top of the export. */
  title: string
  /** Base filename (no extension); a timestamp is appended. */
  filenameBase: string
  /** Returns the inner HTML to export (e.g. () => resultRef.current?.innerHTML). */
  getBodyHtml: () => string | null | undefined
  /** Optional line under the title (e.g. a date or source). */
  subtitle?: string
  className?: string
  /** Compact variant (icon-only-ish) for tight toolbars. */
  size?: 'sm' | 'md'
}

/** Reusable "Download PDF / HTML" pair for any AI result view. */
export default function ExportButtons({ title, filenameBase, getBodyHtml, subtitle, className, size = 'md' }: Props) {
  const [pdfBusy, setPdfBusy] = useState(false)

  const asHtml = () => {
    const body = getBodyHtml()
    if (body && body.trim()) downloadHtml(title, body, `${filenameBase}-${exportStamp()}.html`, subtitle)
  }
  const asPdf = async () => {
    const body = getBodyHtml()
    if (!body || !body.trim() || pdfBusy) return
    setPdfBusy(true)
    try {
      // No server-side PDF engine in InkReel — the browser's print-to-PDF is the path.
      printToPdf(title, body, subtitle)
    } finally {
      setPdfBusy(false)
    }
  }
  return (
    <div className={`export-btns${size === 'sm' ? ' export-btns-sm' : ''}${className ? ' ' + className : ''}`}>
      <button type="button" className="export-btn" onClick={asPdf} disabled={pdfBusy} title="Download as PDF">
        {pdfBusy
          ? <><span className="export-spin" aria-hidden="true" /> PDF…</>
          : <><PhosphorIcon className="bi bi-file-earmark-pdf" aria-hidden="true" /> PDF</>}
      </button>
      <button type="button" className="export-btn" onClick={asHtml} title="Download as HTML">
        <PhosphorIcon className="bi bi-filetype-html" aria-hidden="true" /> HTML
      </button>
    </div>
  )
}
