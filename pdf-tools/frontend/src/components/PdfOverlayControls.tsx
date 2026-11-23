import { useState, type ReactNode } from 'react'

// Mirrors the backend PdfOverlayOptions contract (PdfOverlayModels.cs).
interface OverlaySegmentDto {
  text?: string
  imageIndex?: number
  imageMaxHeight?: number
}
interface BandDto {
  enabled: boolean
  left?: OverlaySegmentDto
  center?: OverlaySegmentDto
  right?: OverlaySegmentDto
  skipFirstPage: boolean
  fontFamily: string
  fontSize: number
  bold: boolean
  color: string
}
interface PageNumbersDto {
  enabled: boolean
  format: string
  position: string
  startAt: number
  skipFirstPage: boolean
  fontFamily: string
  fontSize: number
  bold: boolean
  color: string
}
interface OverlayOptionsDto {
  pageNumbers?: PageNumbersDto
  header?: BandDto
  footer?: BandDto
  marginPoints: number
}

type LogoPosition = 'left' | 'center' | 'right'

interface PageNumberState {
  enabled: boolean
  format: string
  position: string
  startAt: number
  skipFirstPage: boolean
  fontSize: number
  bold: boolean
  color: string
}
interface BandState {
  enabled: boolean
  left: string
  center: string
  right: string
  logo: File | null
  logoPosition: LogoPosition
  logoHeight: number
  skipFirstPage: boolean
  fontSize: number
  bold: boolean
  color: string
}

const TOKENS = '{page} {pages} {date} {time} {filename} {title}'

const POSITIONS: Array<{ value: string; label: string }> = [
  { value: 'top-left', label: 'Top left' },
  { value: 'top-center', label: 'Top center' },
  { value: 'top-right', label: 'Top right' },
  { value: 'bottom-left', label: 'Bottom left' },
  { value: 'bottom-center', label: 'Bottom center' },
  { value: 'bottom-right', label: 'Bottom right' },
]

const defaultBand = (): BandState => ({
  enabled: false,
  left: '',
  center: '',
  right: '',
  logo: null,
  logoPosition: 'left',
  logoHeight: 28,
  skipFirstPage: false,
  fontSize: 10,
  bold: false,
  color: '#333333',
})

const defaultPageNumbers = (): PageNumberState => ({
  enabled: false,
  format: '{page} / {pages}',
  position: 'bottom-center',
  startAt: 1,
  skipFirstPage: false,
  fontSize: 10,
  bold: false,
  color: '#333333',
})

export interface UseOverlayResult {
  /** The controls UI to render in the tool panel. */
  node: ReactNode
  /**
   * Appends the overlay options (and any logo files) to a FormData under the
   * given field names. Returns an error message to surface, or null on success
   * (including the no-op case where nothing is enabled).
   */
  append: (form: FormData, fields: { optionsField: string; imagesField: string }, required: boolean) => string | null
}

/**
 * Reusable page-number / header / footer controls.
 * Used standalone (the "Header, Footer & Page Numbers" tool) and as an optional
 * collapsible block on the convert-to-PDF tools.
 */
export function useOverlay(options?: { collapsible?: boolean }): UseOverlayResult {
  const collapsible = options?.collapsible ?? false
  const [pageNumbers, setPageNumbers] = useState<PageNumberState>(defaultPageNumbers)
  const [header, setHeader] = useState<BandState>(defaultBand)
  const [footer, setFooter] = useState<BandState>(defaultBand)

  const buildBand = (band: BandState, images: File[]): BandDto | undefined => {
    if (!band.enabled) return undefined

    let logoIndex: number | undefined
    if (band.logo) {
      logoIndex = images.length
      images.push(band.logo)
    }

    const segment = (pos: LogoPosition, text: string): OverlaySegmentDto | undefined => {
      const trimmed = text.trim()
      const hasImage = logoIndex !== undefined && band.logoPosition === pos
      if (!trimmed && !hasImage) return undefined
      return {
        text: trimmed || undefined,
        imageIndex: hasImage ? logoIndex : undefined,
        imageMaxHeight: band.logoHeight,
      }
    }

    return {
      enabled: true,
      left: segment('left', band.left),
      center: segment('center', band.center),
      right: segment('right', band.right),
      skipFirstPage: band.skipFirstPage,
      fontFamily: 'Arial',
      fontSize: band.fontSize,
      bold: band.bold,
      color: band.color,
    }
  }

  const append: UseOverlayResult['append'] = (form, fields, required) => {
    const images: File[] = []
    const dto: OverlayOptionsDto = {
      pageNumbers: pageNumbers.enabled
        ? { ...pageNumbers, fontFamily: 'Arial' }
        : undefined,
      header: buildBand(header, images),
      footer: buildBand(footer, images),
      marginPoints: 36,
    }

    const nothingEnabled = !dto.pageNumbers && !dto.header && !dto.footer
    if (nothingEnabled) {
      return required ? 'Turn on page numbers, a header, or a footer first.' : null
    }

    form.append(fields.optionsField, JSON.stringify(dto))
    images.forEach(image => form.append(fields.imagesField, image))
    return null
  }

  const bandControls = (
    title: string,
    band: BandState,
    setBand: React.Dispatch<React.SetStateAction<BandState>>,
  ) => (
    <div className="pdf-overlay-section">
      <label className="pdf-overlay-toggle">
        <input
          type="checkbox"
          checked={band.enabled}
          onChange={e => setBand(b => ({ ...b, enabled: e.target.checked }))}
        />
        <span>{title}</span>
      </label>

      {band.enabled && (
        <div className="pdf-overlay-body">
          <div className="pdf-overlay-segments">
            {(['left', 'center', 'right'] as const).map(pos => (
              <label key={pos}>
                <span>{pos[0].toUpperCase() + pos.slice(1)}</span>
                <input
                  value={band[pos]}
                  placeholder={pos === 'center' ? 'e.g. {title}' : pos === 'right' ? 'e.g. {date}' : 'Text…'}
                  onChange={e => setBand(b => ({ ...b, [pos]: e.target.value }))}
                />
              </label>
            ))}
          </div>

          <div className="pdf-overlay-row">
            <label className="pdf-overlay-logo">
              <span>Logo image</span>
              <input
                type="file"
                accept="image/png,image/jpeg,.png,.jpg,.jpeg"
                onChange={e => setBand(b => ({ ...b, logo: e.target.files?.[0] ?? null }))}
              />
            </label>
            {band.logo && (
              <>
                <label>
                  <span>Logo position</span>
                  <select value={band.logoPosition} onChange={e => setBand(b => ({ ...b, logoPosition: e.target.value as LogoPosition }))}>
                    <option value="left">Left</option>
                    <option value="center">Center</option>
                    <option value="right">Right</option>
                  </select>
                </label>
                <label>
                  <span>Logo height (pt)</span>
                  <input
                    type="number" min={8} max={120}
                    value={band.logoHeight}
                    onChange={e => setBand(b => ({ ...b, logoHeight: Number(e.target.value) || 28 }))}
                  />
                </label>
              </>
            )}
          </div>

          <div className="pdf-overlay-row">
            <label>
              <span>Font size</span>
              <input type="number" min={4} max={96} value={band.fontSize} onChange={e => setBand(b => ({ ...b, fontSize: Number(e.target.value) || 10 }))} />
            </label>
            <label className="pdf-overlay-color">
              <span>Color</span>
              <input type="color" value={band.color} onChange={e => setBand(b => ({ ...b, color: e.target.value }))} />
            </label>
            <label className="pdf-overlay-check">
              <input type="checkbox" checked={band.bold} onChange={e => setBand(b => ({ ...b, bold: e.target.checked }))} />
              <span>Bold</span>
            </label>
            <label className="pdf-overlay-check">
              <input type="checkbox" checked={band.skipFirstPage} onChange={e => setBand(b => ({ ...b, skipFirstPage: e.target.checked }))} />
              <span>Skip first page</span>
            </label>
          </div>
        </div>
      )}
    </div>
  )

  const pageNumberControls = (
    <div className="pdf-overlay-section">
      <label className="pdf-overlay-toggle">
        <input
          type="checkbox"
          checked={pageNumbers.enabled}
          onChange={e => setPageNumbers(p => ({ ...p, enabled: e.target.checked }))}
        />
        <span>Page numbers</span>
      </label>

      {pageNumbers.enabled && (
        <div className="pdf-overlay-body">
          <div className="pdf-overlay-row">
            <label className="pdf-overlay-grow">
              <span>Format</span>
              <input value={pageNumbers.format} onChange={e => setPageNumbers(p => ({ ...p, format: e.target.value }))} />
            </label>
            <label>
              <span>Position</span>
              <select value={pageNumbers.position} onChange={e => setPageNumbers(p => ({ ...p, position: e.target.value }))}>
                {POSITIONS.map(pos => <option key={pos.value} value={pos.value}>{pos.label}</option>)}
              </select>
            </label>
            <label>
              <span>Start at</span>
              <input type="number" value={pageNumbers.startAt} onChange={e => setPageNumbers(p => ({ ...p, startAt: Number(e.target.value) || 1 }))} />
            </label>
          </div>
          <div className="pdf-overlay-row">
            <label>
              <span>Font size</span>
              <input type="number" min={4} max={96} value={pageNumbers.fontSize} onChange={e => setPageNumbers(p => ({ ...p, fontSize: Number(e.target.value) || 10 }))} />
            </label>
            <label className="pdf-overlay-color">
              <span>Color</span>
              <input type="color" value={pageNumbers.color} onChange={e => setPageNumbers(p => ({ ...p, color: e.target.value }))} />
            </label>
            <label className="pdf-overlay-check">
              <input type="checkbox" checked={pageNumbers.bold} onChange={e => setPageNumbers(p => ({ ...p, bold: e.target.checked }))} />
              <span>Bold</span>
            </label>
            <label className="pdf-overlay-check">
              <input type="checkbox" checked={pageNumbers.skipFirstPage} onChange={e => setPageNumbers(p => ({ ...p, skipFirstPage: e.target.checked }))} />
              <span>Skip first page</span>
            </label>
          </div>
        </div>
      )}
    </div>
  )

  const inner = (
    <div className="pdf-overlay-controls">
      {pageNumberControls}
      {bandControls('Header', header, setHeader)}
      {bandControls('Footer', footer, setFooter)}
      <p className="pdf-overlay-tokens">Tokens you can use in text: <code>{TOKENS}</code></p>
    </div>
  )

  const node = collapsible ? (
    <details className="pdf-overlay-details">
      <summary>Header, footer &amp; page numbers (optional)</summary>
      {inner}
    </details>
  ) : inner

  return { node, append }
}
