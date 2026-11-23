import { useEffect } from 'react'

/**
 * Injects one or more JSON-LD structured-data blocks into <head> for the
 * lifetime of the calling component, then removes them on unmount.
 *
 * Note: this runs client-side (after JS executes), so it is visible to
 * JS-rendering crawlers (Googlebot) immediately and will be captured in the
 * static HTML once public routes are prerendered. Pass each schema object as a
 * separate argument, or one array of objects.
 */
export function useJsonLd(...schemas: Array<object | object[]>) {
  const flat = schemas.flat()
  // Stable dependency: serialise once so the effect only re-runs on change.
  const serialised = JSON.stringify(flat)

  useEffect(() => {
    // Remove any dynamic JSON-LD already in <head> — e.g. blocks baked in by the
    // prerenderer — so hydration doesn't duplicate structured data.
    document.querySelectorAll('script[data-dynamic-jsonld]').forEach((n) => n.remove())

    const nodes = flat.map((schema) => {
      const script = document.createElement('script')
      script.type = 'application/ld+json'
      script.setAttribute('data-dynamic-jsonld', 'true')
      script.text = JSON.stringify(schema)
      document.head.appendChild(script)
      return script
    })

    return () => {
      nodes.forEach((node) => node.remove())
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [serialised])
}
