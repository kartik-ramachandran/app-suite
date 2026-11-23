import { useEffect, useRef, useState } from 'react'
import api from '../services/api'

/**
 * A figure lifted out of a book's PDF.
 *
 * Fetched through axios rather than pointed at with a plain `src`, because the
 * auth token is attached by an axios request interceptor: a native `<img>`
 * request never passes through it, arrives unauthenticated, and renders as a
 * broken-image icon. The audio player already fetches blobs for the same
 * reason.
 *
 * The object URL is revoked on unmount, otherwise paging through a technical
 * book leaks a few hundred megabytes of decoded screenshots.
 */
export default function FigureImage({ src, alt }: { src: string; alt: string }) {
  const [url, setUrl] = useState<string | null>(null)
  const [failed, setFailed] = useState(false)
  const objectUrl = useRef<string | null>(null)

  useEffect(() => {
    let cancelled = false

    // `src` is an absolute API path; axios is configured with the API base.
    const path = src.replace(/^\/api/, '')

    api.get(path, { responseType: 'blob' })
      .then(res => {
        if (cancelled) return
        const next = URL.createObjectURL(res.data)
        objectUrl.current = next
        setUrl(next)
      })
      .catch(() => { if (!cancelled) setFailed(true) })

    return () => {
      cancelled = true
      if (objectUrl.current) {
        URL.revokeObjectURL(objectUrl.current)
        objectUrl.current = null
      }
    }
  }, [src])

  // A missing figure should leave the caption readable, not a broken icon.
  if (failed) return null

  if (!url) {
    return <div className="br-figure-loading" aria-label={`Loading ${alt}`} />
  }

  return <img className="br-figure-img" src={url} alt={alt} onClick={e => e.stopPropagation()} />
}
