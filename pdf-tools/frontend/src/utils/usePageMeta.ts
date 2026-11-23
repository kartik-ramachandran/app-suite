import { useEffect } from 'react'

export function usePageMeta(title: string, description: string, canonical?: string, robots = 'index, follow, max-image-preview:large, max-snippet:-1, max-video-preview:-1') {
  useEffect(() => {
    // Title
    document.title = title

    // Meta description
    const setMeta = (selector: string, attr: string, value: string) => {
      const el = document.querySelector(selector)
      if (el) el.setAttribute(attr, value)
    }

    setMeta('meta[name="description"]', 'content', description)
    setMeta('meta[name="title"]', 'content', title)
    setMeta('meta[property="og:title"]', 'content', title)
    setMeta('meta[property="og:description"]', 'content', description)
    setMeta('meta[property="twitter:title"]', 'content', title)
    setMeta('meta[property="twitter:description"]', 'content', description)
    setMeta('meta[name="robots"]', 'content', robots)
    setMeta('meta[name="googlebot"]', 'content', robots)

    // Canonical
    if (canonical) {
      const link = document.querySelector('link[rel="canonical"]')
      if (link) link.setAttribute('href', canonical)
      setMeta('meta[property="og:url"]', 'content', canonical)
      setMeta('meta[property="twitter:url"]', 'content', canonical)
    }
  }, [title, description, canonical, robots])
}
