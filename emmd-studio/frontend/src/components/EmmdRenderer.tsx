import { forwardRef, useMemo } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import rehypeRaw from 'rehype-raw'
import rehypeSanitize, { defaultSchema } from 'rehype-sanitize'
import { parseEmmd } from '../utils/emmd'
import { buildEmmdCss, getTheme } from '../utils/emmdThemes'
import { rehypeSafeLinks } from '../utils/emmdSafeLinks'
import { rehypeHeadingIds } from '../utils/emmdHeadingIds'

// A strict allowlist. We permit structural/semantic HTML and class/id hooks so
// the (sanitised, scoped) style block can target elements — but NOT inline
// `style`, event handlers, <script>, <style>, <iframe>, forms, etc. Image src
// may be http(s) or a self-contained data: URI.
const schema = {
  ...defaultSchema,
  tagNames: [
    ...(defaultSchema.tagNames || []),
    'div', 'span', 'section', 'article', 'header', 'footer', 'aside',
    'figure', 'figcaption', 'mark', 'small', 'sup', 'sub', 'u', 'ins',
    'details', 'summary', 'abbr', 'kbd', 'samp', 'time', 'dl', 'dt', 'dd',
  ],
  attributes: {
    ...defaultSchema.attributes,
    '*': [...(defaultSchema.attributes?.['*'] || []), 'className', 'id'],
    a: [...(defaultSchema.attributes?.a || []), 'target', 'rel'],
    img: [...(defaultSchema.attributes?.img || []), 'width', 'height', 'loading'],
    td: [...(defaultSchema.attributes?.td || []), 'colSpan', 'rowSpan', 'align'],
    th: [...(defaultSchema.attributes?.th || []), 'colSpan', 'rowSpan', 'align', 'scope'],
  },
  protocols: {
    ...defaultSchema.protocols,
    src: [...(defaultSchema.protocols?.src || []), 'data'],
  },
}

const remarkPlugins = [remarkGfm]
const rehypePlugins: never[] = [rehypeRaw, [rehypeSanitize, schema], rehypeSafeLinks, rehypeHeadingIds] as never[]

interface Props {
  /** EMMD source (frontmatter + style + body). A trailing seal comment is ignored. */
  content: string
  /** Force a theme instead of the one named in the frontmatter. */
  themeOverride?: string
  /** Render a title/subtitle/author header from the frontmatter. */
  showHeader?: boolean
  className?: string
}

/** Renders EMMD to themed, sanitised HTML. The forwarded ref points at the
 *  `.emmd-doc` element so callers can read its innerHTML for export. */
const EmmdRenderer = forwardRef<HTMLDivElement, Props>(function EmmdRenderer(
  { content, themeOverride, showHeader = true, className },
  ref,
) {
  const parsed = useMemo(() => parseEmmd(content || ''), [content])
  const css = useMemo(() => buildEmmdCss(parsed.styleCss), [parsed.styleCss])
  const themeId = getTheme(themeOverride || parsed.frontmatter.theme).id

  const fm = parsed.frontmatter
  const hasHeader = showHeader && (fm.title || fm.subtitle || fm.author)

  return (
    <>
      <style>{css}</style>
      <div ref={ref} className={`emmd-doc emmd-theme-${themeId}${className ? ' ' + className : ''}`}>
        {hasHeader && (
          <header className="emmd-dochead">
            {fm.title && <h1>{fm.title}</h1>}
            {fm.subtitle && <p className="emmd-subtitle">{fm.subtitle}</p>}
            {(fm.author || fm.created) && (
              <p className="emmd-byline">
                {fm.author}{fm.author && fm.created ? ' · ' : ''}{fm.created}
              </p>
            )}
          </header>
        )}
        <ReactMarkdown remarkPlugins={remarkPlugins} rehypePlugins={rehypePlugins}>
          {parsed.body}
        </ReactMarkdown>
      </div>
    </>
  )
})

export default EmmdRenderer
