// Rasterize rendered study/Inkling visuals to PNG.
//
// Two paths: a bare inline SVG (mermaid, mind maps) rasterizes directly for
// crisp output; arbitrary HTML (sticky boards, sheets, timelines…) is wrapped
// in an SVG <foreignObject> with the export CSS inlined. External resources
// (Google-font @import) are stripped — the browser won't fetch them inside an
// image anyway and they would taint the canvas; handwriting styles fall back
// to the generic cursive stack in the PNG.

const SCALE = 2

/** UI-only bits that must never appear in an exported image. */
const CLEANUP_SELECTOR = '.ink-remove, .ink-vis-tools, .ink-edit-btn, .mm-drag-hint, .ss-answers-toggle, .ss-flip-hint'

export const svgToPng = (svg: SVGSVGElement): Promise<string> => new Promise((resolve, reject) => {
  const rect = svg.getBoundingClientRect()
  const w = Math.max(320, Math.round(rect.width) || 900)
  const h = Math.max(200, Math.round(rect.height) || 600)
  const cloned = svg.cloneNode(true) as SVGSVGElement
  cloned.setAttribute('width', String(w))
  cloned.setAttribute('height', String(h))
  const xml = new XMLSerializer().serializeToString(cloned)
  rasterize('data:image/svg+xml;charset=utf-8,' + encodeURIComponent(xml), w, h).then(resolve, reject)
})

export const elementToPng = (el: HTMLElement, css: string): Promise<string> => new Promise((resolve, reject) => {
  const rect = el.getBoundingClientRect()
  const w = Math.max(320, Math.ceil(rect.width) || 900)
  const h = Math.max(80, Math.ceil(el.scrollHeight || rect.height) || 600) + 32
  const clone = el.cloneNode(true) as HTMLElement
  clone.querySelectorAll(CLEANUP_SELECTOR).forEach(x => x.remove())
  // Quiz answers render collapsed on screen; keep whatever state the DOM has.
  const wrapper = document.createElement('div')
  wrapper.appendChild(clone)
  const xhtml = new XMLSerializer().serializeToString(wrapper)
  const cleanCss = css.replace(/@import[^;]+;/g, '')
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}">` +
    `<foreignObject width="100%" height="100%">` +
    `<div xmlns="http://www.w3.org/1999/xhtml">` +
    `<style><![CDATA[ body,div{margin:0} ${cleanCss} ]]></style>` +
    `<div class="ss-render" style="background:#ffffff;padding:16px;width:${w - 32}px;box-sizing:content-box;color:#0f172a;` +
    `font-family:Inter,-apple-system,'Segoe UI',Roboto,sans-serif;">${xhtml}</div>` +
    `</div></foreignObject></svg>`
  rasterize('data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svg), w, h).then(resolve, reject)
})

const rasterize = (src: string, w: number, h: number): Promise<string> => new Promise((resolve, reject) => {
  const img = new Image()
  img.onload = () => {
    try {
      const canvas = document.createElement('canvas')
      canvas.width = w * SCALE
      canvas.height = h * SCALE
      const ctx = canvas.getContext('2d')
      if (!ctx) { reject(new Error('canvas unavailable')); return }
      ctx.fillStyle = '#ffffff'
      ctx.fillRect(0, 0, canvas.width, canvas.height)
      ctx.drawImage(img, 0, 0, canvas.width, canvas.height)
      resolve(canvas.toDataURL('image/png'))
    } catch (e) { reject(e as Error) }
  }
  img.onerror = () => reject(new Error('rasterize failed'))
  img.src = src
})

export const downloadDataUri = (dataUri: string, filename: string) => {
  const a = document.createElement('a')
  a.href = dataUri
  a.download = filename
  document.body.appendChild(a)
  a.click()
  a.remove()
}

export const safePngName = (title: string | null | undefined, fallback: string) =>
  ((title ?? '').replace(/[^\w\- ]+/g, '').trim().replace(/\s+/g, '-').slice(0, 60) || fallback) + '.png'
