using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PDFtoImage;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig;
using VersOne.Epub;

namespace BookReader.Api.Services;

/// <summary>
/// Extracts plain text from PDF, EPUB, Word, HTML and text files.
///
/// Ported from the standalone Bookreader app. Every branch falls back to a
/// plain-text read rather than throwing: a partially-readable book is worth
/// more to someone than an error, and the AI step downstream degrades
/// gracefully on messy input.
/// </summary>
public class BookParserService
{
    private readonly ILogger<BookParserService> _logger;
    private readonly AzureVisionService _vision;

    private static readonly Regex StripHtml = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex CollapseWhitespace = new(@"\s{3,}", RegexOptions.Compiled);

    public BookParserService(ILogger<BookParserService> logger, AzureVisionService vision)
    {
        _logger = logger;
        _vision = vision;
    }

    public static readonly string[] SupportedExtensions =
        { ".pdf", ".epub", ".docx", ".doc", ".html", ".htm", ".txt", ".md",
          ".mobi", ".azw", ".azw3", ".prc",
          ".jpg", ".jpeg", ".png", ".webp" };

    /// <summary>A photographed or scanned page has to be read by a model, not parsed.</summary>
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };

    /// <summary>Kindle containers. All are Palm Database files underneath.</summary>
    private static readonly HashSet<string> KindleExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mobi", ".azw", ".azw3", ".prc" };

    public async Task<string> ExtractTextAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        try
        {
            return ext switch
            {
                ".pdf" => ExtractPdf(stream),
                ".epub" => await ExtractEpubAsync(stream),
                ".docx" => ExtractDocx(stream),
                ".html" or ".htm" => ExtractHtml(stream),
                _ when ImageExtensions.Contains(ext) => await ExtractImageAsync(stream, ext, ct),
                _ when KindleExtensions.Contains(ext) => ExtractKindle(stream),
                _ => await ReadAsTextAsync(stream, ct),
            };
        }
        // A deliberate refusal is not a parse failure. Falling back to a plain
        // text read for an encrypted or unsupported-compression file would feed
        // the AI a few kilobytes of binary and hand the reader a nonsense book
        // with no explanation of why — the failure has to reach the surface.
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse {FileName} as {Ext}; falling back to plain text", fileName, ext);
            stream.Position = 0;
            return await ReadAsTextAsync(stream, ct);
        }
    }

    private string ExtractPdf(Stream stream)
    {
        using var doc = PdfDocument.Open(stream);
        var sb = new StringBuilder();

        foreach (var page in doc.GetPages())
        {
            // A page marker is written into the text so a figure caption can
            // later be matched to the images that were on the same page.
            // Without it there is no way back from a line of text to a
            // picture, because extraction flattens the whole book to a string.
            sb.AppendLine($"{PageMarker}{page.Number}");

            // GetWords() preserves reading order better than page.Text on
            // multi-column layouts, which most books have.
            //
            // The words are then regrouped into VISUAL LINES by their y
            // position. Joining them with spaces instead - which is what this
            // used to do - flattens a whole page into one string, destroying
            // every paragraph break, heading and figure caption. That is why
            // the reader showed running text with "FIGURE 1-50 ..." buried
            // mid-paragraph and no way to anchor its picture.
            var words = page.GetWords().ToList();

            if (words.Count == 0)
            {
                sb.AppendLine(page.Text);
                sb.AppendLine();
                continue;
            }

            foreach (var line in GroupIntoLines(words))
                sb.AppendLine(line);

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Rebuilds visual lines from positioned words.
    ///
    /// A PDF has no concept of a line: it has words at coordinates. Words
    /// sharing a baseline are one line, and a larger-than-usual vertical gap
    /// between lines is a paragraph break. Recovering this is what lets a
    /// figure caption be its own line instead of being swallowed by the
    /// paragraph before it.
    /// </summary>
    private static IEnumerable<string> GroupIntoLines(
        IReadOnlyList<UglyToad.PdfPig.Content.Word> words)
    {
        // Group by baseline WITHIN a column. Doing this across the whole page
        // width merges side-by-side columns into one line, which is how a
        // credits page came out as "All rights reserved. This publication is
        // protected by copyright, and permission Loretta Yates must be
        // obtained" - the editor names read straight through the legal text.
        var rows = new List<(double Y, double Height, List<UglyToad.PdfPig.Content.Word> Words)>();

        foreach (var column in SplitIntoColumns(words))
        {
            var columnRows = new List<(double Y, double Height, List<UglyToad.PdfPig.Content.Word> Words)>();

            foreach (var word in column.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
            {
                var y = word.BoundingBox.Bottom;
                var h = Math.Max(word.BoundingBox.Height, 1);
                var row = columnRows.FirstOrDefault(r => Math.Abs(r.Y - y) <= Math.Max(r.Height, h) * 0.5);

                if (row.Words is null)
                    columnRows.Add((y, h, new List<UglyToad.PdfPig.Content.Word> { word }));
                else
                    row.Words.Add(word);
            }

            // Each column is read top to bottom before the next one starts,
            // which is how a person reads them.
            rows.AddRange(columnRows.OrderByDescending(r => r.Y));
        }

        // Already ordered: column by column, each top to bottom. Re-sorting
        // by y here would undo the column split.
        var ordered = rows;
        if (ordered.Count == 0) yield break;

        // The usual line spacing on this page, used to spot paragraph breaks.
        var gaps = new List<double>();
        for (var i = 1; i < ordered.Count; i++)
        {
            var g = ordered[i - 1].Y - ordered[i].Y;
            if (g > 0) gaps.Add(g);   // skip the jump back up to a new column
        }

        var typicalGap = gaps.Count > 0
            ? gaps.OrderBy(g => g).ElementAt(gaps.Count / 2)
            : 0;

        var paragraph = new StringBuilder();

        for (var i = 0; i < ordered.Count; i++)
        {
            var text = string.Join(" ", ordered[i].Words
                .OrderBy(w => w.BoundingBox.Left)
                .Select(w => w.Text));

            if (string.IsNullOrWhiteSpace(text)) continue;

            // A figure caption is its own block, always. It is the anchor the
            // image is hung from, so it must never be merged into the prose
            // around it.
            var isCaption = CaptionStart.IsMatch(text);

            var gapBefore = i > 0 ? ordered[i - 1].Y - ordered[i].Y : 0;
            // A negative gap means the previous row was BELOW this one: the
            // start of a new column. That is always a new paragraph.
            var isNewParagraph = i > 0 && (gapBefore < 0 || (typicalGap > 0 && gapBefore > typicalGap * 1.6));

            if ((isCaption || isNewParagraph) && paragraph.Length > 0)
            {
                yield return paragraph.ToString().Trim();
                paragraph.Clear();
            }

            if (isCaption)
            {
                yield return text.Trim();
                continue;
            }

            // Rejoin a word the print layout hyphen-split across the break.
            if (paragraph.Length > 0)
            {
                if (paragraph[^1] == '-')
                {
                    paragraph.Length -= 1;
                }
                else
                {
                    paragraph.Append(' ');
                }
            }

            paragraph.Append(text);
        }

        if (paragraph.Length > 0) yield return paragraph.ToString().Trim();
    }

    /// <summary>
    /// Splits a page's words into columns.
    ///
    /// Works on the vertical gaps in the horizontal profile: a page is sliced
    /// into narrow x-bands, and a run of empty bands wide enough to be a
    /// gutter separates two columns. This is deliberately conservative -
    /// wrongly splitting ordinary prose would be worse than missing a column,
    /// so a gutter must be genuinely wide and both sides must hold real text.
    /// </summary>
    private static List<List<UglyToad.PdfPig.Content.Word>> SplitIntoColumns(
        IReadOnlyList<UglyToad.PdfPig.Content.Word> words)
    {
        var single = new List<List<UglyToad.PdfPig.Content.Word>> { words.ToList() };
        if (words.Count < 40) return single;   // too little text to judge

        var left = words.Min(w => w.BoundingBox.Left);
        var right = words.Max(w => w.BoundingBox.Right);
        var width = right - left;
        if (width <= 0) return single;

        const int bands = 100;
        var bandWidth = width / bands;
        var occupied = new bool[bands];

        foreach (var w in words)
        {
            var from = (int)Math.Floor((w.BoundingBox.Left - left) / bandWidth);
            var to = (int)Math.Ceiling((w.BoundingBox.Right - left) / bandWidth);
            for (var b = Math.Max(0, from); b < Math.Min(bands, to); b++) occupied[b] = true;
        }

        // A gutter is an empty run of at least 4% of the page width. Narrower
        // than that and it is word spacing or a ragged right edge.
        var gutters = new List<(int Start, int End)>();
        var runStart = -1;
        for (var b = 0; b < bands; b++)
        {
            if (!occupied[b])
            {
                if (runStart < 0) runStart = b;
            }
            else if (runStart >= 0)
            {
                if (b - runStart >= 4) gutters.Add((runStart, b));
                runStart = -1;
            }
        }

        // Ignore gutters at the very edges: those are margins, not columns.
        gutters = gutters.Where(g => g.Start > 8 && g.End < bands - 8).ToList();
        if (gutters.Count == 0) return single;

        // Cut at the middle of each gutter.
        var cuts = gutters.Select(g => left + (g.Start + g.End) / 2.0 * bandWidth).ToList();
        var columns = new List<List<UglyToad.PdfPig.Content.Word>>();
        var bounds = new List<double> { double.MinValue };
        bounds.AddRange(cuts);
        bounds.Add(double.MaxValue);

        for (var i = 0; i < bounds.Count - 1; i++)
        {
            var lo = bounds[i];
            var hi = bounds[i + 1];
            // Assign by the word's centre, so a word straddling a cut lands on
            // the side it mostly sits on.
            var inColumn = words
                .Where(w =>
                {
                    var centre = (w.BoundingBox.Left + w.BoundingBox.Right) / 2;
                    return centre >= lo && centre < hi;
                })
                .ToList();

            if (inColumn.Count > 0) columns.Add(inColumn);
        }

        // A split that leaves a nearly-empty side was a false positive - an
        // indented block or a figure, not a column layout.
        if (columns.Count < 2 || columns.Any(c => c.Count < 15)) return single;

        return columns;
    }

    /// <summary>
    /// A figure caption at the start of a line: "FIGURE 1-50 ...", "Figure 3.2",
    /// "TABLE 2-1". Anchored, so "see Figure 1-50" mid-sentence is left alone -
    /// that is a cross-reference, not the figure itself.
    /// </summary>
    private static readonly Regex CaptionStart = new(
        // Requires a real title after the number, so a stranded cross-reference
        // fragment ("Figure 1-7." or "Figure 1-9)." left behind when a sentence
        // wraps) is not mistaken for a caption and does not steal the image
        // belonging to the actual caption on that page.
        @"^(FIGURE|Figure|FIG\.|Fig\.|TABLE|Table)\s*\d+([.\-]\d+)?\s+\p{L}[\p{L}\s]{3,}",
        RegexOptions.Compiled);

    /// <summary>
    /// Marks a page boundary in extracted text. Stripped before display; the
    /// reader uses it only to work out which page a line came from.
    /// </summary>
    public const string PageMarker = "\u241E\u241EPAGE";

    /// <summary>One picture lifted out of a PDF.</summary>
    public record ExtractedImage(int PageNumber, int Index, byte[] Bytes, string ContentType);

    /// <summary>
    /// Pulls the embedded images out of a PDF so figures can be shown next to
    /// the text that refers to them.
    ///
    /// Text extraction alone drops every diagram, which on a technical book is
    /// most of the meaning: the reader was showing "FIGURE 1-47 Delete an Azure
    /// resource with dependencies" as a bare line with nothing beside it.
    ///
    /// Deliberately bounded: tiny images are rules and logos rather than
    /// figures, and a 400-page book can carry thousands of them.
    /// </summary>
    public IEnumerable<ExtractedImage> ExtractPdfImages(
        Stream stream, int maxImages = 200, int minPixels = 20_000)
    {
        var results = new List<ExtractedImage>();

        // PDFium renders from bytes, and the stream is consumed by PdfPig,
        // so it is buffered once here rather than re-read per page.
        byte[] fileBytes;
        try
        {
            if (stream.CanSeek) stream.Position = 0;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            fileBytes = buffer.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not buffer the PDF for rendering");
            return results;
        }

        PdfDocument doc;
        try
        {
            doc = PdfDocument.Open(new MemoryStream(fileBytes));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reopen the PDF to read its images");
            return results;
        }

        using (doc)
        {
            foreach (var page in doc.GetPages())
            {
                if (results.Count >= maxImages) break;

                // Rendered lazily: a page with no figures never pays for it.
                SKBitmap? pageBitmap = null;

                IEnumerable<UglyToad.PdfPig.Content.IPdfImage> images;
                try
                {
                    images = page.GetImages();
                }
                catch (Exception ex)
                {
                    // One unreadable page must not lose the rest of the book.
                    _logger.LogDebug(ex, "Skipped images on page {Page}", page.Number);
                    continue;
                }

                var index = 0;
                foreach (var image in images)
                {
                    if (results.Count >= maxImages) break;

                    // Filter on PIXELS, not bytes. A well-compressed
                    // screenshot can be smaller on disk than a decorative
                    // icon, so a byte threshold throws away real figures and
                    // keeps bullets. Measured on a 432-page Azure exam guide:
                    // a 6KB byte filter kept 0 of 83 figures.
                    if ((long)image.WidthInSamples * image.HeightInSamples < minPixels) continue;

                    byte[]? bytes = null;
                    var contentType = "image/png";

                    try
                    {
                        // Render the page once, then crop each figure from it.
                        pageBitmap ??= RenderPage(fileBytes, page.Number);

                        if (pageBitmap is not null)
                            bytes = CropFigure(pageBitmap, image.Bounds, page.Width, page.Height);

                        // Rendering can fail on a malformed page; the embedded
                        // bytes are still better than nothing for RGB images.
                        if (bytes is null && image.TryGetPng(out var png) && png is not null)
                            bytes = png;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Could not render an image on page {Page}", page.Number);
                        continue;
                    }

                    if (bytes is null || bytes.Length < 1_000) continue;

                    results.Add(new ExtractedImage(page.Number, index++, bytes, contentType));
                }

                // A rendered page is several megabytes; a 400-page book
                // would hold gigabytes if these were kept.
                pageBitmap?.Dispose();
            }
        }

        _logger.LogInformation("Extracted {Count} figures from the PDF", results.Count);
        return results;
    }

    private async Task<string> ExtractEpubAsync(Stream stream)
    {
        var book = await EpubReader.ReadBookAsync(stream);
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(book.Title)) sb.AppendLine($"Title: {book.Title}");
        if (book.AuthorList?.Count > 0) sb.AppendLine($"Author: {string.Join(", ", book.AuthorList)}");
        sb.AppendLine();

        foreach (var file in book.ReadingOrder)
        {
            if (string.IsNullOrWhiteSpace(file.Content)) continue;
            var plain = CollapseWhitespace.Replace(
                System.Net.WebUtility.HtmlDecode(StripHtml.Replace(file.Content, " ")), "\n\n").Trim();
            if (plain.Length > 0) { sb.AppendLine(plain); sb.AppendLine(); }
        }

        return sb.ToString();
    }

    private string ExtractDocx(Stream stream)
    {
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var para in body.Elements<Paragraph>())
        {
            var line = para.InnerText;
            if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private string ExtractHtml(Stream stream)
    {
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var html = reader.ReadToEnd();
        return CollapseWhitespace.Replace(
            System.Net.WebUtility.HtmlDecode(StripHtml.Replace(html, " ")), "\n\n").Trim();
    }

    private static async Task<string> ReadAsTextAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// Splits raw text into readable lines when AI analysis is unavailable or
    /// disabled, so a book is still playable as straight narration. Paragraph
    /// breaks are respected, and long paragraphs are split on sentence
    /// boundaries — a single 900-word TTS request would time out.
    /// </summary>
    public static List<string> SplitIntoLines(string text, int maxChars = 400) =>
        SplitIntoLinesWithPages(text, maxChars).Select(l => l.Text).ToList();

    /// <summary>A display line, and the PDF page it was extracted from (0 if unknown).</summary>
    public record PagedLine(string Text, int Page);

    /// <summary>
    /// As SplitIntoLines, but keeps the page number each line came from.
    ///
    /// The page is what lets a caption like "FIGURE 1-47 ..." be paired with
    /// the images extracted from that same page. Without it the reader can
    /// only show the caption text, which is what a technical book looked like
    /// before: every diagram silently dropped.
    /// </summary>
    public static List<PagedLine> SplitIntoLinesWithPages(string text, int maxChars = 400)
    {
        var lines = new List<PagedLine>();
        var paragraphs = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var page = 0;

        foreach (var raw in paragraphs)
        {
            var para = raw.Trim();
            if (para.Length == 0) continue;

            // Page markers are bookkeeping, never shown and never spoken.
            if (para.StartsWith(PageMarker, StringComparison.Ordinal))
            {
                if (int.TryParse(para[PageMarker.Length..].Trim(), out var n)) page = n;
                continue;
            }

            if (para.Length <= maxChars) { lines.Add(new PagedLine(para, page)); continue; }

            var sentences = Regex.Split(para, @"(?<=[.!?])\s+");
            var buffer = new StringBuilder();
            foreach (var sentence in sentences)
            {
                if (buffer.Length + sentence.Length > maxChars && buffer.Length > 0)
                {
                    lines.Add(new PagedLine(buffer.ToString().Trim(), page));
                    buffer.Clear();
                }
                buffer.Append(sentence).Append(' ');
            }
            if (buffer.Length > 0) lines.Add(new PagedLine(buffer.ToString().Trim(), page));
        }

        return lines;
    }

    // ── Images ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders one PDF page to a bitmap with PDFium.
    ///
    /// Used instead of reading the embedded image streams because print PDFs
    /// store figures as CMYK JPEGs with Adobe's inverted samples: browsers
    /// cannot display those at all, and converting them afterwards loses the
    /// colour (a blue button came back pure white). Rendering produces exactly
    /// what a PDF viewer shows.
    /// </summary>
    private SKBitmap? RenderPage(byte[] fileBytes, int pageNumber)
    {
        try
        {
            // 200 DPI keeps small print legible when a figure is cropped out
            // and shown at full width, without making each page huge.
            return Conversion.ToImage(
                fileBytes,
                page: pageNumber - 1,          // PDFtoImage counts from 0
                options: new RenderOptions(Dpi: FigureRenderDpi));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not render page {Page}", pageNumber);
            return null;
        }
    }

    /// <summary>
    /// Cuts a figure out of a rendered page, given its position in PDF points.
    /// </summary>
    private static byte[]? CropFigure(
        SKBitmap pageBitmap, PdfRectangle bounds, double pageWidth, double pageHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0) return null;

        // Scale between the two coordinate systems rather than assuming the DPI
        // landed exactly: PDFium rounds the bitmap to whole pixels.
        var scaleX = pageBitmap.Width / pageWidth;
        var scaleY = pageBitmap.Height / pageHeight;

        // PDF coordinates start bottom-left, bitmaps top-left.
        var left = (int)Math.Floor(bounds.Left * scaleX);
        var top = (int)Math.Floor((pageHeight - bounds.Top) * scaleY);
        var width = (int)Math.Ceiling(bounds.Width * scaleX);
        var height = (int)Math.Ceiling(bounds.Height * scaleY);

        left = Math.Clamp(left, 0, pageBitmap.Width - 1);
        top = Math.Clamp(top, 0, pageBitmap.Height - 1);
        width = Math.Clamp(width, 1, pageBitmap.Width - left);
        height = Math.Clamp(height, 1, pageBitmap.Height - top);

        using var crop = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(crop))
        {
            canvas.DrawBitmap(
                pageBitmap,
                new SKRect(left, top, left + width, top + height),
                new SKRect(0, 0, width, height));
        }

        using var image = SKImage.FromBitmap(crop);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data?.ToArray();
    }

    /// <summary>
    /// Render resolution for figures: high enough that UI labels in a
    /// screenshot stay readable, low enough to keep a long book manageable.
    /// </summary>
    private const int FigureRenderDpi = 200;

    /// <summary>
    /// A photograph or scan of a page. There is nothing to parse, so the page is
    /// transcribed by the vision model instead.
    ///
    /// Deliberately not OCR-with-cleanup: the model is asked for the text only,
    /// in reading order, with nothing added. A transcription that invents a
    /// sentence is worse than one that drops a word, because the reader has no
    /// way to tell which happened.
    /// </summary>
    private async Task<string> ExtractImageAsync(Stream stream, string ext, CancellationToken ct)
    {
        if (!_vision.IsAvailable)
            throw new InvalidOperationException("Reading text from images needs the AI service, which is not configured.");

        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        var base64 = Convert.ToBase64String(ms.ToArray());

        var text = await _vision.AnalyzeAsync(
            base64,
            "You transcribe photographed or scanned pages of books. Return ONLY the text "
            + "you can actually read, in reading order, preserving paragraph breaks and "
            + "dialogue punctuation exactly as printed. Do not summarise, correct, "
            + "complete or translate anything. Do not describe the image. If a word is "
            + "illegible write [illegible] rather than guessing it. If there is no "
            + "readable text at all, return nothing.",
            "Transcribe this page.",
            maxTokens: 4000);

        return CollapseWhitespace.Replace(text ?? string.Empty, "\n\n").Trim();
    }

    // ── Kindle (MOBI / AZW / AZW3 / PRC) ─────────────────────────────────────

    /// <summary>
    /// Kindle files are Palm Database (PDB) containers. The layout is: a 78-byte
    /// header, a record-offset table, then the records themselves. Record 0 holds
    /// the PalmDOC header, whose first field says how the text is compressed and
    /// how many records the text spans.
    ///
    /// Two compression schemes are handled: none (1) and PalmDOC/LZ77 (2), which
    /// together cover most non-Amazon files. HUFF/CDIC (17480) is not, and neither
    /// is DRM — a book bought from Amazon is encrypted and cannot be read here at
    /// all. Both cases raise a message that says which it is, because "could not
    /// parse" would send someone hunting for a corrupt file that is fine.
    /// </summary>
    private string ExtractKindle(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var data = ms.ToArray();

        if (data.Length < 78)
            throw new InvalidOperationException("That Kindle file is too small to be valid.");

        int BE16(int at) => (data[at] << 8) | data[at + 1];
        int BE32(int at) => (data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3];

        var recordCount = BE16(76);
        if (recordCount < 2) throw new InvalidOperationException("That Kindle file has no readable records.");

        // Offset table: 8 bytes per record, starting at 78. Offset is the first 4.
        var offsets = new int[recordCount + 1];
        for (var i = 0; i < recordCount; i++)
        {
            var at = 78 + i * 8;
            if (at + 4 > data.Length) throw new InvalidOperationException("That Kindle file is truncated.");
            offsets[i] = BE32(at);
        }
        offsets[recordCount] = data.Length;

        var rec0 = offsets[0];
        var compression = BE16(rec0);
        var textRecordCount = BE16(rec0 + 8);

        // Encryption type sits at +12 in the PalmDOC header. Non-zero means DRM.
        var encryption = BE16(rec0 + 12);
        if (encryption != 0)
            throw new InvalidOperationException(
                "That Kindle book is DRM-protected, so its text cannot be read. Books bought from "
                + "Amazon are encrypted. An EPUB or PDF of the same book will work.");

        if (compression != 1 && compression != 2)
            throw new InvalidOperationException(
                "That Kindle file uses a compression format this reader cannot open (HUFF/CDIC). "
                + "Converting it to EPUB will work.");

        var sb = new StringBuilder();
        var last = Math.Min(textRecordCount, recordCount - 1);

        for (var i = 1; i <= last; i++)
        {
            var start = offsets[i];
            var end = Math.Min(offsets[i + 1], data.Length);
            if (start >= end || start < 0) continue;

            var chunk = new byte[end - start];
            Array.Copy(data, start, chunk, 0, chunk.Length);
            sb.Append(compression == 2
                ? Encoding.UTF8.GetString(PalmDocDecompress(chunk))
                : Encoding.UTF8.GetString(chunk));
        }

        // The payload is HTML, so it goes through the same stripping as an HTML book.
        var plain = StripHtml.Replace(sb.ToString(), " ");
        return CollapseWhitespace.Replace(System.Net.WebUtility.HtmlDecode(plain), "\n\n").Trim();
    }

    /// <summary>
    /// PalmDOC decompression: a byte-oriented LZ77 variant.
    ///
    ///   0x00        literal
    ///   0x01..0x08  that many literal bytes follow
    ///   0x09..0x7f  literal
    ///   0x80..0xbf  two-byte back-reference: 11-bit distance, 3-bit length + 3
    ///   0xc0..0xff  space plus the low 7 bits
    /// </summary>
    private static byte[] PalmDocDecompress(byte[] input)
    {
        var output = new List<byte>(input.Length * 4);

        for (var i = 0; i < input.Length;)
        {
            int b = input[i++];

            if (b == 0 || (b >= 0x09 && b <= 0x7f))
            {
                output.Add((byte)b);
            }
            else if (b >= 0x01 && b <= 0x08)
            {
                for (var j = 0; j < b && i < input.Length; j++) output.Add(input[i++]);
            }
            else if (b >= 0x80 && b <= 0xbf)
            {
                if (i >= input.Length) break;
                var pair = (b << 8) | input[i++];
                var distance = (pair >> 3) & 0x07ff;
                var length = (pair & 0x0007) + 3;
                if (distance == 0 || distance > output.Count) continue;

                var from = output.Count - distance;
                // Overlapping copies are legal and common; copy one byte at a time.
                for (var j = 0; j < length; j++) output.Add(output[from + j]);
            }
            else
            {
                output.Add((byte)' ');
                output.Add((byte)(b & 0x7f));
            }
        }

        return output.ToArray();
    }
}
