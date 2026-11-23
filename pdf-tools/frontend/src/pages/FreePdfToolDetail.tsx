import PhosphorIcon from '../components/PhosphorIcon'
import { Link, useParams, Navigate } from 'react-router-dom'
import { useState } from 'react'
import { usePageMeta } from '../utils/usePageMeta'
import { useJsonLd } from '../utils/useJsonLd'
import SiteFooter from '../components/SiteFooter'
import GenPromptLogoMark from '../components/GenPromptLogoMark'
import FreePdfConverter from '../components/FreePdfConverter'
import { getPdfTool, getRelatedPdfTools } from '../data/pdfTools'
import './LandingPage.css'
import './Pricing.css'

function FreePdfToolDetail() {
  const { toolKey = '' } = useParams<{ toolKey: string }>()
  const tool = getPdfTool(toolKey)
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)

  // Hooks must run unconditionally — compute safe values when the tool is unknown.
  const name = tool?.name ?? 'PDF Tool'
  const url = `${window.location.origin}/free-pdf-tools/${toolKey}`
  const related = tool ? getRelatedPdfTools(tool) : []

  const steps = [
    { name: 'Upload your file', text: `Drag your file onto the box at the top of this page, or click to choose one. No account needed.` },
    { name: `Run ${name}`, text: `Click the ${name} button and GenPrompt processes your file in seconds, right here in the browser.` },
    { name: 'Download the result', text: 'Download your finished file straight away. There are no watermarks and your file is processed securely.' },
  ]

  const faqs = [
    { q: `Is ${name} really free?`, a: `Yes. You can run ${name} right on this page without creating an account — no watermark, no per-file charge. A free account removes the daily limit and adds batch processing, file history and API access.` },
    { q: `Is it safe to use ${name} online?`, a: 'Your files are processed securely over HTTPS and are not shared with third parties. Organisations with strict data-residency needs can also run the same engine in-house via a private licence.' },
    { q: `Do I need an account or software for ${name}?`, a: `Neither. ${name} runs in your browser with no download and no sign-up — upload a file above and download the result. Files are processed and deleted immediately.` },
  ]

  // "Merge PDF – Free Online Merge PDF Tool" repeated the tool name twice and
  // pushed most pages past Google's ~60-char cut. One mention, plus the words
  // people actually search alongside it.
  usePageMeta(
    `${name} – Free Online Tool | GenPrompt`,
    tool?.intro ?? 'Free online PDF tools by GenPrompt.',
    url
  )

  useJsonLd(
    {
      '@context': 'https://schema.org',
      '@type': 'BreadcrumbList',
      itemListElement: [
        { '@type': 'ListItem', position: 1, name: 'Home', item: `${window.location.origin}/` },
        { '@type': 'ListItem', position: 2, name: 'Free PDF Tools', item: `${window.location.origin}/free-pdf-tools` },
        { '@type': 'ListItem', position: 3, name, item: url },
      ],
    },
    {
      '@context': 'https://schema.org',
      '@type': 'HowTo',
      name: `How to use ${name}`,
      description: tool?.intro ?? '',
      totalTime: 'PT1M',
      step: steps.map((s) => ({ '@type': 'HowToStep', name: s.name, text: s.text })),
      tool: { '@type': 'HowToTool', name: `GenPrompt ${name}`, url },
    },
    {
      '@context': 'https://schema.org',
      '@type': 'SoftwareApplication',
      name: `GenPrompt ${name}`,
      description: tool?.intro ?? '',
      url,
      applicationCategory: 'BusinessApplication',
      operatingSystem: 'Web',
      inLanguage: 'en-US',
      offers: { '@type': 'Offer', price: '0', priceCurrency: 'USD' },
      publisher: { '@type': 'Organization', name: 'GenPrompt', url: `${window.location.origin}` },
    },
    {
      '@context': 'https://schema.org',
      '@type': 'FAQPage',
      mainEntity: faqs.map((f) => ({
        '@type': 'Question',
        name: f.q,
        acceptedAnswer: { '@type': 'Answer', text: f.a },
      })),
    }
  )

  // Unknown tool key → send users to the hub. (Runs after hooks, so hook order is stable.)
  if (!tool) return <Navigate to="/free-pdf-tools" replace />

  return (
    <div className="landing-page pricing-page pdf-api-page">
      <nav className="landing-nav">
        <div className="nav-container">
          <div className="nav-brand">
            <Link to="/" style={{ display: 'flex', alignItems: 'center', gap: '0.5rem', textDecoration: 'none' }}>
              <GenPromptLogoMark className="landing-header-logo-image" title="GenPrompt" />
              <span className="brand-name">GenPrompt</span>
            </Link>
          </div>
          <button className="hamburger-menu" onClick={() => setMobileMenuOpen(!mobileMenuOpen)} aria-label="Toggle menu">
            <span></span><span></span><span></span>
          </button>
          <div className={`nav-links ${mobileMenuOpen ? 'mobile-open' : ''}`}>
            <Link to="/free-pdf-tools" className="nav-link" onClick={() => setMobileMenuOpen(false)}>All PDF Tools</Link>
            <Link to="/enterprise" className="nav-link" onClick={() => setMobileMenuOpen(false)}>For Business</Link>
            <Link to="/pricing" className="nav-link" onClick={() => setMobileMenuOpen(false)}>Pricing</Link>
            <Link to="/signin" className="nav-link" onClick={() => setMobileMenuOpen(false)}>Sign In</Link>
            <Link to="/signup" className="btn-nav-cta" onClick={() => setMobileMenuOpen(false)}>Get Started</Link>
          </div>
        </div>
      </nav>

      {/* Hero */}
      <section className="pricing-hero pdf-api-hero">
        <div className="section-header">
          <nav aria-label="Breadcrumb" style={{ fontSize: '0.85rem', marginBottom: '0.5rem' }}>
            <Link to="/free-pdf-tools" style={{ color: '#d62976', textDecoration: 'none' }}>Free PDF Tools</Link>
            <span style={{ opacity: 0.5 }}> / {name}</span>
          </nav>
          <div className="hero-badge">
            <PhosphorIcon className={`bi ${tool.icon}`}></PhosphorIcon> {tool.group}
          </div>
          <h1 className="section-title" style={{ fontSize: '2.6rem' }}>{name} — free & online</h1>
          <p className="section-subtitle" style={{ maxWidth: '660px', margin: '0.75rem auto 0' }}>{tool.intro}</p>

          {/* The tool itself, above the fold. This page ranks for "{name}" —
              a visitor who searched for it should be able to do it here, not be
              sent to a signup wall first. */}
          <FreePdfConverter toolKey={tool.key} toolName={name} />

          <div className="cta-actions" style={{ marginTop: '1.25rem', justifyContent: 'center' }}>
            <Link to="/free-pdf-tools" className="btn-cta-secondary">See all PDF tools</Link>
          </div>
        </div>
        <div className="donate-proof-row" aria-label="Promise">
          <span><PhosphorIcon className="bi bi-check-circle"></PhosphorIcon> Free, no watermark</span>
          <span><PhosphorIcon className="bi bi-person-x"></PhosphorIcon> No account needed</span>
          <span><PhosphorIcon className="bi bi-shield-check"></PhosphorIcon> Files deleted immediately</span>
        </div>
      </section>

      {/* How to */}
      <section className="pricing-faq">
        <div className="section-header">
          <h2 className="section-title">How to use {name}</h2>
        </div>
        <div className="pricing-faq-grid" style={{ maxWidth: '900px', margin: '0 auto' }}>
          {steps.map((s, i) => (
            <div className="pricing-faq-item" key={s.name}>
              <h4><PhosphorIcon className={`bi bi-${i + 1}-circle`}></PhosphorIcon> {s.name}</h4>
              <p>{s.text}</p>
            </div>
          ))}
        </div>
      </section>

      {/* Related tools */}
      {related.length > 0 && (
        <section className="pricing-faq" style={{ paddingTop: 0 }}>
          <div className="section-header">
            <h2 className="section-title">Related PDF tools</h2>
          </div>
          <div
            className="pricing-faq-grid"
            style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', maxWidth: '1000px', margin: '0 auto' }}
          >
            {related.map((t) => (
              <Link
                key={t.key}
                to={`/free-pdf-tools/${t.key}`}
                className="pricing-faq-item"
                style={{ display: 'flex', flexDirection: 'column', gap: '0.4rem', textDecoration: 'none', color: 'inherit' }}
              >
                <h3 style={{ margin: 0, display: 'flex', alignItems: 'center', gap: '0.5rem', fontSize: '1.05rem' }}>
                  <PhosphorIcon className={`bi ${t.icon}`} style={{ color: '#d62976' }}></PhosphorIcon> {t.name}
                </h3>
                <p style={{ margin: 0, fontSize: '0.9rem' }}>{t.short}</p>
              </Link>
            ))}
          </div>
        </section>
      )}

      {/* FAQ */}
      <section className="pricing-faq" style={{ paddingTop: 0 }}>
        <div className="section-header">
          <h2 className="section-title">{name} — frequently asked questions</h2>
        </div>
        <div className="pricing-faq-grid" style={{ maxWidth: '900px', margin: '0 auto' }}>
          {faqs.map((f) => (
            <div className="pricing-faq-item" key={f.q}>
              <h4>{f.q}</h4>
              <p>{f.a}</p>
            </div>
          ))}
        </div>
      </section>

      {/* CTA */}
      <section className="cta-section">
        <div className="cta-content">
          <h2>Use {name} free</h2>
          <p>Create a free GenPrompt account and get {name} plus every other PDF tool.</p>
          <div className="cta-actions">
            <Link to="/signup" className="btn-cta-primary">
              <PhosphorIcon className="bi bi-rocket-takeoff"></PhosphorIcon> Get Started Free
            </Link>
            <Link to={`/pdf-tools/${tool.key}`} className="btn-cta-secondary">Open {name}</Link>
          </div>
        </div>
      </section>

      <SiteFooter />
    </div>
  )
}

export default FreePdfToolDetail
