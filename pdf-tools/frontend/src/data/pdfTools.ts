// Single source of truth for the public PDF tool landing pages.
// Used by the /free-pdf-tools hub, the per-tool detail pages, and the
// prerender route list. Mirrors the live tool set in pages/PdfTools.tsx.

export interface PdfTool {
  key: string
  name: string
  group: string
  icon: string
  /** Short card description used on the hub grid. */
  short: string
  /** 1–2 sentence keyword-rich intro shown on the tool's own page. */
  intro: string
  /** Comma-separated keywords for the page <meta name="keywords">. */
  keywords: string
}

export interface PdfToolGroup {
  group: string
  blurb: string
  tools: PdfTool[]
}

export const PDF_TOOL_GROUPS: PdfToolGroup[] = [
  {
    group: 'Convert from PDF',
    blurb: 'Turn any PDF into an editable file while keeping the original page layout.',
    tools: [
      { key: 'pdf-to-word', name: 'PDF to Word', group: 'Convert from PDF', icon: 'bi-file-earmark-word', short: 'Convert PDF to an editable Word (.docx) document that keeps the original styling.', intro: 'Convert PDF to Word online free. Get an editable .docx that keeps the original layout, fonts and tables — no watermark, no signup.', keywords: 'pdf to word, convert pdf to word, pdf to docx, pdf to word converter free, pdf to editable word' },
      { key: 'pdf-to-excel', name: 'PDF to Excel', group: 'Convert from PDF', icon: 'bi-file-earmark-excel', short: 'Convert PDF tables and pages into an Excel (.xlsx) spreadsheet.', intro: 'Convert PDF to Excel online for free. Turn PDF tables and pages into an editable .xlsx spreadsheet you can sort, filter, and calculate with.', keywords: 'pdf to excel, convert pdf to excel, pdf to xlsx, pdf table to excel, pdf to spreadsheet' },
      { key: 'pdf-to-powerpoint', name: 'PDF to PowerPoint', group: 'Convert from PDF', icon: 'bi-file-earmark-slides', short: 'Convert PDF pages into editable PowerPoint (.pptx) slides.', intro: 'Convert PDF to PowerPoint online for free. Each PDF page becomes an editable slide so you can reuse a document as a presentation.', keywords: 'pdf to powerpoint, convert pdf to ppt, pdf to pptx, pdf to slides' },
      { key: 'pdf-to-jpg', name: 'PDF to JPG', group: 'Convert from PDF', icon: 'bi-file-earmark-image', short: 'Convert a PDF page into a high-quality JPG image.', intro: 'Convert PDF to JPG online for free. Save a PDF page as a high-quality JPG image for sharing, embedding, or thumbnails.', keywords: 'pdf to jpg, convert pdf to image, pdf to jpeg, pdf to png, pdf to image converter' },
      { key: 'pdf-to-html', name: 'PDF to HTML', group: 'Convert from PDF', icon: 'bi-filetype-html', short: 'Convert a PDF into positioned HTML that preserves the page layout.', intro: 'Convert PDF to HTML online for free. Turn a PDF into positioned HTML that keeps the original page layout — ideal for the web.', keywords: 'pdf to html, convert pdf to html, pdf to web page' },
      { key: 'pdf-to-markdown', name: 'PDF to Markdown', group: 'Convert from PDF', icon: 'bi-markdown', short: 'Convert a PDF into Markdown for docs, wikis, and AI tools.', intro: 'Convert PDF to Markdown online free. Get clean .md with headings, lists and tables preserved — ideal for docs, wikis and AI context.', keywords: 'pdf to markdown, convert pdf to markdown, pdf to md, pdf for ai, pdf to text for chatgpt' },
      { key: 'pdf-to-text', name: 'PDF to Text', group: 'Convert from PDF', icon: 'bi-body-text', short: 'Extract plain text from a PDF with layout spacing preserved.', intro: 'Convert PDF to text online for free. Extract the plain text from any PDF with layout spacing preserved as far as plain text allows.', keywords: 'pdf to text, extract text from pdf, pdf to txt, copy text from pdf' },
    ],
  },
  {
    group: 'Create PDF',
    blurb: 'Make a polished PDF from the documents and images you already have.',
    tools: [
      { key: 'word-to-pdf', name: 'Word to PDF', group: 'Create PDF', icon: 'bi-file-earmark-word', short: 'Convert Word documents to PDF; bulk uploads merge into one file.', intro: 'Convert Word to PDF online for free. Export .doc and .docx files to a polished PDF — upload several and they merge into one document in order.', keywords: 'word to pdf, convert word to pdf, docx to pdf, doc to pdf' },
      { key: 'excel-to-pdf', name: 'Excel to PDF', group: 'Create PDF', icon: 'bi-file-earmark-excel', short: 'Convert Excel workbooks to PDF in upload order.', intro: 'Convert Excel to PDF online for free. Turn .xls and .xlsx workbooks into a clean, shareable PDF.', keywords: 'excel to pdf, convert excel to pdf, xlsx to pdf, spreadsheet to pdf' },
      { key: 'powerpoint-to-pdf', name: 'PowerPoint to PDF', group: 'Create PDF', icon: 'bi-file-earmark-slides', short: 'Convert PowerPoint decks to a shareable PDF.', intro: 'Convert PowerPoint to PDF online for free. Turn .ppt and .pptx decks into a PDF anyone can open without PowerPoint.', keywords: 'powerpoint to pdf, convert ppt to pdf, pptx to pdf, slides to pdf' },
      { key: 'html-to-pdf', name: 'HTML to PDF', group: 'Create PDF', icon: 'bi-filetype-html', short: 'Convert HTML files or web pages to PDF.', intro: 'Convert HTML to PDF online for free. Turn HTML files into a print-ready PDF that keeps your formatting.', keywords: 'html to pdf, convert html to pdf, web page to pdf' },
      { key: 'markdown-to-pdf', name: 'Markdown to PDF', group: 'Create PDF', icon: 'bi-markdown', short: 'Convert Markdown files into a clean, formatted PDF.', intro: 'Convert Markdown to PDF online for free. Turn .md files into a clean, formatted PDF with headings, lists, and code blocks intact.', keywords: 'markdown to pdf, convert markdown to pdf, md to pdf' },
      { key: 'text-to-pdf', name: 'Text to PDF', group: 'Create PDF', icon: 'bi-file-earmark-text', short: 'Convert plain text files into a PDF document.', intro: 'Convert text to PDF online for free. Turn .txt files into a tidy PDF document in seconds.', keywords: 'text to pdf, convert txt to pdf, txt to pdf' },
      { key: 'images-to-pdf', name: 'Image to PDF', group: 'Create PDF', icon: 'bi-images', short: 'Combine JPG or PNG images into a single PDF.', intro: 'Convert images to PDF online for free. Combine JPG and PNG photos or scans into a single PDF in the order you choose.', keywords: 'image to pdf, jpg to pdf, png to pdf, convert image to pdf, photo to pdf' },
    ],
  },
  {
    group: 'Edit & organise PDF',
    blurb: 'Merge, split, compress, and tidy up your PDFs in seconds.',
    tools: [
      { key: 'merge', name: 'Merge PDF', group: 'Edit & organise PDF', icon: 'bi-intersect', short: 'Combine multiple PDFs into one document in any order.', intro: 'Merge PDF files online for free. Combine multiple PDFs into a single document and drag them into any order before you download.', keywords: 'merge pdf, combine pdf, join pdf, merge pdf files free, pdf merger' },
      { key: 'split', name: 'Split PDF', group: 'Edit & organise PDF', icon: 'bi-scissors', short: 'Extract pages or split a PDF into separate files with page previews.', intro: 'Split PDF files online for free. Pick the pages you need with live previews, then extract them into one PDF or split each page into its own file.', keywords: 'split pdf, extract pdf pages, separate pdf, pdf splitter, delete pages from pdf' },
      { key: 'compress', name: 'Compress PDF', group: 'Edit & organise PDF', icon: 'bi-arrows-collapse', short: 'Reduce PDF file size for easy email and upload.', intro: 'Compress PDF files online for free. Shrink a large PDF so it is easy to email and upload, while keeping the document readable.', keywords: 'compress pdf, reduce pdf size, shrink pdf, make pdf smaller, pdf compressor' },
      { key: 'rotate', name: 'Rotate PDF', group: 'Edit & organise PDF', icon: 'bi-arrow-clockwise', short: 'Rotate every page by 90, 180, or 270 degrees.', intro: 'Rotate PDF pages online for free. Turn every page by 90, 180, or 270 degrees and save the corrected file.', keywords: 'rotate pdf, turn pdf pages, fix pdf orientation' },
      { key: 'watermark', name: 'Watermark PDF', group: 'Edit & organise PDF', icon: 'bi-droplet', short: 'Add a text watermark to every page of a PDF.', intro: 'Add a watermark to a PDF online for free. Stamp a text watermark such as DRAFT or CONFIDENTIAL across every page.', keywords: 'watermark pdf, add watermark to pdf, stamp pdf' },
      { key: 'page-numbers', name: 'Add Page Numbers', group: 'Edit & organise PDF', icon: 'bi-123', short: 'Insert page numbers at the bottom of every page.', intro: 'Add page numbers to a PDF online for free. Insert clean page numbers at the bottom of every page.', keywords: 'add page numbers to pdf, number pdf pages, pdf page numbering' },
      { key: 'overlay', name: 'Header & Footer', group: 'Edit & organise PDF', icon: 'bi-layout-text-window-reverse', short: 'Add headers, footers, and page numbers — type text, upload a logo, or both.', intro: 'Add headers, footers and page numbers to a PDF. Place text or a logo in any corner, with tokens like {page}, {date} and {title}.', keywords: 'add header to pdf, add footer to pdf, pdf header footer, add logo to pdf, page numbers in pdf, pdf bates numbering' },
      { key: 'remove-pages', name: 'Remove Pages', group: 'Organise PDF', icon: 'bi-file-earmark-minus', short: 'Delete the pages you do not want and keep the rest in order.', intro: 'Remove pages from a PDF online free. Type the pages to delete — single pages, ranges, or a mix like 1,3,5-8 — and download the result.', keywords: 'remove pages from pdf, delete pages from pdf, pdf page remover, trim pdf pages, extract pages from pdf' },
      { key: 'organize', name: 'Organise PDF', group: 'Organise PDF', icon: 'bi-arrows-move', short: 'Reorder, duplicate, or drop pages by listing the order you want.', intro: 'Organise PDF pages online free. Give the order you want — for example 3,1,2 — to rearrange, repeat or drop pages. No account needed.', keywords: 'organize pdf, rearrange pdf pages, reorder pdf pages, sort pdf pages, move pdf pages' },
      { key: 'repair', name: 'Repair PDF', group: 'Edit & organise PDF', icon: 'bi-wrench', short: 'Rebuild a corrupted PDF so it opens cleanly again.', intro: 'Repair a damaged PDF online for free. Rebuild a clean copy of a PDF that opens poorly or shows errors.', keywords: 'repair pdf, fix corrupted pdf, recover pdf, pdf repair tool' },
    ],
  },
  {
    group: 'Protect PDF',
    blurb: 'Lock, unlock, and secure documents before you share them.',
    tools: [
      { key: 'protect', name: 'Protect PDF', group: 'Protect PDF', icon: 'bi-lock', short: 'Add password protection and encryption to a PDF.', intro: 'Password-protect a PDF online for free. Add encryption and a password so only the right people can open your document.', keywords: 'protect pdf, password protect pdf, encrypt pdf, add password to pdf, secure pdf' },
      { key: 'unlock', name: 'Unlock PDF', group: 'Protect PDF', icon: 'bi-unlock', short: 'Remove password protection from a PDF you own.', intro: 'Unlock a PDF online for free. Remove password protection from a PDF when you know the password.', keywords: 'unlock pdf, remove pdf password, decrypt pdf, remove password from pdf' },
    ],
  },
  {
    group: 'Check PDF',
    blurb: 'Read text from scans and compare two documents line by line.',
    tools: [
      { key: 'ocr', name: 'OCR PDF', group: 'Check PDF', icon: 'bi-text-paragraph', short: 'Extract searchable text from scanned PDFs and images.', intro: 'OCR a PDF online for free. Extract searchable, copyable text from scanned PDFs and image-based documents.', keywords: 'ocr pdf, pdf ocr, scanned pdf to text, extract text from scanned pdf, make pdf searchable' },
      { key: 'compare', name: 'Compare PDFs', group: 'Check PDF', icon: 'bi-layout-split', short: 'Compare two PDFs and highlight text and visual differences.', intro: 'Compare two PDFs online for free. See the text and visual differences between two versions of a document, page by page.', keywords: 'compare pdf, pdf diff, compare two pdfs, pdf comparison tool, difference between pdfs' },
    ],
  },
  {
    group: 'Business tools',
    blurb: 'Fill forms, generate documents at scale, redact for compliance, and archive to PDF/A.',
    tools: [
      { key: 'fill-form', name: 'Fill PDF Form', group: 'Business tools', icon: 'bi-input-cursor-text', short: 'Fill the fields of any fillable PDF form and download the completed copy.', intro: 'Fill a PDF form online free. Type into form fields in your browser and download the completed document, no software needed.', keywords: 'fill pdf form, pdf form filler, fill out pdf online, complete pdf form, acroform filler, flatten pdf form' },
      { key: 'generate', name: 'PDF Document Generator', group: 'Business tools', icon: 'bi-braces-asterisk', short: 'Generate invoices, contracts, and certificates from an HTML template plus JSON data.', intro: 'Generate a professional PDF from a prompt or your own content — invoices, reports, letters and contracts, styled and ready to send.', keywords: 'pdf generation api, generate pdf from template, html template to pdf, invoice generator api, document generation, json to pdf' },
      { key: 'redact', name: 'Redact PDF', group: 'Business tools', icon: 'bi-eraser-fill', short: 'Permanently remove emails, numbers, names, and phrases — true redaction, not a black box.', intro: 'Redact a PDF online free. Black out names, numbers and addresses so the text is removed from the file, not just hidden behind a box.', keywords: 'redact pdf, pdf redaction tool, remove text from pdf permanently, black out pdf, redact pdf online, gdpr redaction, pii removal' },
      { key: 'pdfa', name: 'PDF to PDF/A', group: 'Business tools', icon: 'bi-archive', short: 'Convert any PDF to the PDF/A archival standard for compliance and long-term storage.', intro: 'Convert PDF to PDF/A online free. Produce the archival format required for long-term storage and legal or government submissions.', keywords: 'pdf to pdfa, convert pdf to pdf/a, pdfa converter, pdf/a-2b, archival pdf, iso 19005, pdfa validation' },
      { key: 'clean-metadata', name: 'Remove PDF Metadata', group: 'Business tools', icon: 'bi-shield-slash', short: 'Strip author, title, and hidden XMP metadata before sharing a document.', intro: 'Remove metadata from a PDF online for free. Strip the author, title, creator software, and hidden XMP metadata from a PDF before it leaves your organisation.', keywords: 'remove pdf metadata, strip pdf metadata, pdf metadata remover, anonymize pdf, clean pdf properties' },
    ],
  },
]

export const PDF_TOOLS: PdfTool[] = PDF_TOOL_GROUPS.flatMap((g) => g.tools)

export const getPdfTool = (key: string): PdfTool | undefined =>
  PDF_TOOLS.find((t) => t.key === key)

export const getRelatedPdfTools = (tool: PdfTool): PdfTool[] =>
  PDF_TOOLS.filter((t) => t.group === tool.group && t.key !== tool.key)
