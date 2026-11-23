import PhosphorIcon from '../components/PhosphorIcon'
import { Link } from 'react-router-dom'
import { useState, type ReactNode } from 'react'
import { usePageMeta } from '../utils/usePageMeta'
import { useJsonLd } from '../utils/useJsonLd'
import SiteFooter from '../components/SiteFooter'
import GenPromptLogoMark from '../components/GenPromptLogoMark'
import { PDF_TOOL_GROUPS, PDF_TOOLS } from '../data/pdfTools'
import './LandingPage.css'
import './Pricing.css'

const TOOL_GROUPS = PDF_TOOL_GROUPS
const ALL_TOOLS = PDF_TOOLS

const FAQS: Array<{ q: string; a: string; answerNode?: ReactNode }> = [
  {
    q: 'Are the GenPrompt PDF tools free to use?',
    a: 'Yes. Most tools — merge, split, compress, rotate, remove pages, organise pages, and every format conversion — run right in your browser with no account at all, up to 3 files a day. There is never a watermark and no per-file charge. A free account removes the daily limit and adds batch processing, file history and API access.',
  },
  {
    q: 'Is it safe to convert my PDF files online?',
    a: 'Your files are processed securely over HTTPS and are not shared with third parties. For organisations with strict data-residency or compliance needs, GenPrompt also offers an in-house PDF engine you can run entirely inside your own infrastructure so no documents leave your network.',
  },
  {
    q: 'Do I need to install any software?',
    a: 'No. All GenPrompt PDF tools run in your browser. There is nothing to download or install — open the tool, upload your file, and download the result.',
  },
  {
    q: 'What is the best free way to convert PDF to Word?',
    a: 'Use the GenPrompt PDF to Word tool. It converts your PDF into an editable .docx document while preserving the original page styling, so headings, tables, and layout stay intact. It is free and runs in your browser.',
  },
  {
    q: 'How do I merge several PDFs into one file?',
    a: 'Open the Merge PDF tool, upload two or more PDFs, drag them into the order you want, and download a single combined document. It is free and takes a few seconds.',
  },
  {
    q: 'Can I reduce the size of a large PDF?',
    a: 'Yes. The Compress PDF tool reduces file size so your PDF is easy to email or upload, while keeping the document readable.',
  },
  {
    q: 'Can I convert PDFs to Markdown for AI tools?',
    a: 'Yes. GenPrompt includes a PDF to Markdown converter, which is ideal for feeding documents into ChatGPT, Claude, and other AI tools, or for publishing to docs and wikis.',
  },
  {
    q: 'Is there a PDF API for developers and businesses?',
    a: 'Yes. The same conversion engine is available as a hosted PDF API with API keys, or as an in-house Docker licence for on-premises deployment. See the PDF API page for pricing.',
    answerNode: (
      <>
        Yes. The same conversion engine is available as a hosted PDF API with API keys, or as an
        in-house Docker licence for on-premises deployment. See the{' '}
        <Link to="/enterprise" style={{ fontWeight: 700, color: '#d62976' }}>PDF API page</Link> for pricing.
      </>
    ),
  },
]

function FreePdfTools() {
  usePageMeta(
    'Free PDF Tools – Convert, Merge, Split & Compress | GenPrompt',
    'Convert PDF to Word, Excel, JPG and more. Merge, split, compress, rotate and watermark PDFs free in your browser — most tools need no account at all.',
    `${window.location.origin}/free-pdf-tools`
  )

  useJsonLd(
    {
      '@context': 'https://schema.org',
      '@type': 'BreadcrumbList',
      itemListElement: [
        { '@type': 'ListItem', position: 1, name: 'Home', item: `${window.location.origin}/` },
        { '@type': 'ListItem', position: 2, name: 'Free PDF Tools', item: `${window.location.origin}/free-pdf-tools` },
      ],
    },
    {
      '@context': 'https://schema.org',
      '@type': 'SoftwareApplication',
      name: 'GenPrompt PDF Tools',
      description:
        'Free online PDF tools to convert, create, merge, split, compress, rotate, watermark, OCR, protect, and compare PDF files in your browser.',
      url: `${window.location.origin}/free-pdf-tools`,
      applicationCategory: 'BusinessApplication',
      operatingSystem: 'Web',
      inLanguage: 'en-US',
      offers: { '@type': 'Offer', price: '0', priceCurrency: 'USD' },
      featureList: ALL_TOOLS.map((t) => t.name),
      publisher: { '@type': 'Organization', name: 'GenPrompt', url: `${window.location.origin}` },
    },
    {
      '@context': 'https://schema.org',
      '@type': 'ItemList',
      name: 'GenPrompt PDF Tools',
      itemListElement: ALL_TOOLS.map((t, i) => ({
        '@type': 'ListItem',
        position: i + 1,
        name: t.name,
        url: `${window.location.origin}/free-pdf-tools/${t.key}`,
      })),
    },
    {
      '@context': 'https://schema.org',
      '@type': 'FAQPage',
      mainEntity: FAQS.map((f) => ({
        '@type': 'Question',
        name: f.q,
        acceptedAnswer: { '@type': 'Answer', text: f.a },
      })),
    }
  )

  const [mobileMenuOpen, setMobileMenuOpen] = useState(false)

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
            <Link to="/features" className="nav-link" onClick={() => setMobileMenuOpen(false)}>Features</Link>
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
          <div className="hero-badge">
            <PhosphorIcon className="bi bi-file-earmark-pdf"></PhosphorIcon> Free PDF tools
          </div>
          <h1 className="section-title" style={{ fontSize: '2.75rem' }}>
            Free PDF tools, all in one place.
          </h1>
          <p className="section-subtitle" style={{ maxWidth: '660px', margin: '0.75rem auto 0' }}>
            Convert, create, merge, split, compress, and secure PDFs right in your browser — no install,
            no watermark. {ALL_TOOLS.length} tools covering every common document job — most work with no account at all.
          </p>
          <div className="cta-actions" style={{ marginTop: '1.5rem', justifyContent: 'center' }}>
            <Link to="/pdf-tools" className="btn-cta-primary">
              <PhosphorIcon className="bi bi-magic"></PhosphorIcon> Open PDF Tools
            </Link>
            <Link to="/enterprise" className="btn-cta-secondary">APIs for businesses</Link>
          </div>
        </div>
        <div className="donate-proof-row" aria-label="PDF tools promise">
          <span><PhosphorIcon className="bi bi-check-circle"></PhosphorIcon> No watermarks</span>
          <span><PhosphorIcon className="bi bi-shield-check"></PhosphorIcon> Secure HTTPS processing</span>
          <span><PhosphorIcon className="bi bi-browser-chrome"></PhosphorIcon> Works in your browser</span>
        </div>
      </section>

      {/* Tool groups */}
      {TOOL_GROUPS.map((group) => (
        <section className="pricing-faq" key={group.group} style={{ paddingTop: '1.5rem', paddingBottom: '1.5rem' }}>
          <div className="section-header">
            <h2 className="section-title" style={{ fontSize: '1.8rem' }}>{group.group}</h2>
            <p className="section-subtitle" style={{ maxWidth: '640px' }}>{group.blurb}</p>
          </div>
          <div
            className="pricing-faq-grid"
            style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(260px, 1fr))', maxWidth: '1100px', margin: '0 auto' }}
          >
            {group.tools.map((tool) => (
              <Link
                key={tool.key}
                to={`/free-pdf-tools/${tool.key}`}
                className="pricing-faq-item"
                style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem', textDecoration: 'none', color: 'inherit' }}
              >
                <h3 style={{ margin: 0, display: 'flex', alignItems: 'center', gap: '0.5rem', fontSize: '1.1rem' }}>
                  <PhosphorIcon className={`bi ${tool.icon}`} style={{ color: '#d62976' }}></PhosphorIcon> {tool.name}
                </h3>
                <p style={{ margin: 0, fontSize: '0.92rem' }}>{tool.short}</p>
              </Link>
            ))}
          </div>
        </section>
      ))}

      {/* Why GenPrompt */}
      <section className="pricing-faq" style={{ paddingTop: 0 }}>
        <div className="section-header">
          <h2 className="section-title">Why use GenPrompt PDF tools?</h2>
        </div>
        <div className="pricing-faq-grid" style={{ maxWidth: '900px', margin: '0 auto' }}>
          <div className="pricing-faq-item">
            <h4><PhosphorIcon className="bi bi-cash-coin"></PhosphorIcon> Free, no watermarks</h4>
            <p>Most tools run with no account at all. Your output never carries a watermark.</p>
          </div>
          <div className="pricing-faq-item">
            <h4><PhosphorIcon className="bi bi-layout-text-window"></PhosphorIcon> Layout preserved</h4>
            <p>Conversions keep the original styling — headings, tables, and page layout stay intact.</p>
          </div>
          <div className="pricing-faq-item">
            <h4><PhosphorIcon className="bi bi-hdd-network"></PhosphorIcon> Built for scale</h4>
            <p>The same engine powers our hosted PDF API and an in-house licence for compliance-bound teams.</p>
          </div>
          <div className="pricing-faq-item">
            <h4><PhosphorIcon className="bi bi-stars"></PhosphorIcon> More than PDFs</h4>
            <p>GenPrompt is also a full AI prompt workspace — prompts, skills, agents, and workflows in one place.</p>
          </div>
        </div>
      </section>

      {/* FAQ */}
      <section className="pricing-faq" style={{ paddingTop: 0 }}>
        <div className="section-header">
          <h2 className="section-title">Frequently asked questions</h2>
        </div>
        <div className="pricing-faq-grid" style={{ maxWidth: '900px', margin: '0 auto' }}>
          {FAQS.map((f) => (
            <div className="pricing-faq-item" key={f.q}>
              <h4>{f.q}</h4>
              <p>{f.answerNode ?? f.a}</p>
            </div>
          ))}
        </div>
      </section>

      {/* CTA */}
      <section className="cta-section">
        <div className="cta-content">
          <h2>Get every PDF tool for free</h2>
          <p>Convert, merge, split and compress PDFs in seconds — no account needed. Create one free to remove the daily limit and unlock batch processing and the API.</p>
          <div className="cta-actions">
            <Link to="/signup" className="btn-cta-primary">
              <PhosphorIcon className="bi bi-rocket-takeoff"></PhosphorIcon> Get Started Free
            </Link>
            <Link to="/pdf-tools" className="btn-cta-secondary">Open PDF Tools</Link>
          </div>
        </div>
      </section>

      <SiteFooter />
    </div>
  )
}

export default FreePdfTools
