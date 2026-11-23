using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Prism.Api.Services;
using SkiaSharp;
using UglyToad.PdfPig.Content;

namespace PdfAI.API.Services;

/// <summary>
/// True-removal PDF redaction. Matches are found on the text layer (PdfPig word
/// boxes + pattern/term/AI matching); redacted pages are then rebuilt from
/// rasterized images with the matched regions painted out BEFORE the rebuild —
/// so the underlying text objects no longer exist in the output. The trade-off
/// is that redacted output pages are images (not selectable text); that is what
/// guarantees the content is unrecoverable.
/// </summary>
public class PdfRedactionService
{
    private readonly DocumentConversionService _conversion;
    private readonly AzureVisionService _ai;
    private readonly ILogger<PdfRedactionService> _logger;

    // Must match DocumentConversionService.ConvertPdfToImages' render DPI.
    private const double RenderDpi = 220.0;

    public PdfRedactionService(DocumentConversionService conversion, AzureVisionService ai, ILogger<PdfRedactionService> logger)
    {
        _conversion = conversion;
        _ai = ai;
        _logger = logger;
    }

    public record RedactionOptions(
        bool Emails, bool Phones, bool CreditCards, bool NationalIds, bool Ibans,
        IReadOnlyList<string> Terms, bool UseAiPii);

    public record RedactionResult(byte[] Pdf, int MatchCount, Dictionary<string, int> ByCategory);

    private static readonly Regex EmailRx = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex PhoneRx = new(@"(?<!\d)(\+?\d[\d\s().\-]{7,17}\d)(?!\d)", RegexOptions.Compiled);
    private static readonly Regex CardRx = new(@"(?<!\d)(?:\d[ \-]?){13,19}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex NationalIdRx = new(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex IbanRx = new(@"\b[A-Z]{2}\d{2}[A-Z0-9]{10,30}\b", RegexOptions.Compiled);

    public async Task<RedactionResult> RedactAsync(byte[] pdfBytes, RedactionOptions options)
    {
        var terms = new List<string>(options.Terms.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()));
        var byCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Page number (1-based) → rectangles to paint, in PDF points (origin bottom-left).
        var boxes = new Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>>();
        var pageSizes = new Dictionary<int, (double Width, double Height)>();

        using (var doc = UglyToad.PdfPig.PdfDocument.Open(pdfBytes))
        {
            // Optional AI pass: ask the model for PII strings the patterns can't
            // know (person names, street addresses, organisations).
            if (options.UseAiPii && _ai.IsAvailable)
            {
                var fullText = new StringBuilder();
                foreach (var page in doc.GetPages())
                {
                    fullText.Append(page.Text).Append('\n');
                    if (fullText.Length > 15000) break;
                }
                terms.AddRange(await FindAiPiiTermsAsync(fullText.ToString()));
            }

            foreach (var page in doc.GetPages())
            {
                pageSizes[page.Number] = (page.Width, page.Height);
                var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
                var pageBoxes = new List<(double, double, double, double)>();

                void Mark(IEnumerable<Word> matched, string category)
                {
                    foreach (var w in matched)
                    {
                        var b = w.BoundingBox;
                        pageBoxes.Add((b.Left, b.Bottom, b.Right, b.Top));
                    }
                    byCategory[category] = byCategory.GetValueOrDefault(category) + 1;
                }

                // Sliding windows of consecutive words catch values split across
                // tokens ("+64 21 123 4567") and multi-word terms ("Jane Doe").
                for (var i = 0; i < words.Count; i++)
                {
                    for (var span = 1; span <= 4 && i + span <= words.Count; span++)
                    {
                        var window = words.GetRange(i, span);
                        var joined = string.Join(" ", window.Select(w => w.Text));

                        if (span == 1 || window.All(w => Math.Abs(w.BoundingBox.Bottom - window[0].BoundingBox.Bottom) < 3))
                        {
                            if (options.Emails && span == 1 && EmailRx.IsMatch(joined)) { Mark(window, "emails"); break; }
                            if (options.NationalIds && span == 1 && NationalIdRx.IsMatch(joined)) { Mark(window, "nationalIds"); break; }
                            if (options.Ibans && span == 1 && IbanRx.IsMatch(joined)) { Mark(window, "ibans"); break; }
                            if (options.CreditCards && span <= 4 && IsCardNumber(joined)) { Mark(window, "creditCards"); break; }
                            if (options.Phones && span <= 4 && IsPhone(joined)) { Mark(window, "phones"); break; }
                            var term = terms.FirstOrDefault(t => string.Equals(joined, t, StringComparison.OrdinalIgnoreCase));
                            if (term != null) { Mark(window, "terms"); break; }
                        }
                    }
                }

                if (pageBoxes.Count > 0) boxes[page.Number] = pageBoxes;
            }
        }

        var total = byCategory.Values.Sum();
        if (total == 0) return new RedactionResult(pdfBytes, 0, byCategory);

        var redacted = RebuildWithRedactions(pdfBytes, boxes, pageSizes);
        return new RedactionResult(redacted, total, byCategory);
    }

    private static bool IsPhone(string joined)
    {
        var m = PhoneRx.Match(joined);
        if (!m.Success || m.Value != joined.Trim()) return false;
        var digits = joined.Count(char.IsDigit);
        return digits is >= 8 and <= 15;
    }

    private static bool IsCardNumber(string joined)
    {
        var m = CardRx.Match(joined);
        if (!m.Success || m.Value.Trim() != joined.Trim()) return false;
        var digits = joined.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        // Luhn checksum — filters invoice numbers and other digit runs.
        var sum = 0;
        var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i];
            if (alt) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            alt = !alt;
        }
        return sum % 10 == 0;
    }

    private async Task<List<string>> FindAiPiiTermsAsync(string text)
    {
        try
        {
            var raw = await _ai.CompleteTextAsync(
                "You are a PII detector for document redaction. From the text, list every occurrence of: person names, " +
                "street/postal addresses, and dates of birth. Return ONLY minified JSON {\"terms\": string[]} where each term " +
                "is the EXACT string as it appears in the text (max 60 items). No commentary.",
                text, maxTokens: 1200);
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return new List<string>();
            using var doc = JsonDocument.Parse(raw.Substring(start, end - start + 1));
            if (!doc.RootElement.TryGetProperty("terms", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new List<string>();
            return arr.EnumerateArray()
                .Where(t => t.ValueKind == JsonValueKind.String)
                .Select(t => t.GetString()!.Trim())
                .Where(t => t.Length is > 1 and <= 120)
                .Take(60)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI PII detection failed — continuing with pattern matches only");
            return new List<string>();
        }
    }

    /// <summary>
    /// Rasterize every page, paint the redaction boxes onto the images, and
    /// rebuild a PDF at the original page dimensions. Painting happens on the
    /// bitmap, so no text object from a redacted region survives into the output.
    /// </summary>
    private byte[] RebuildWithRedactions(
        byte[] pdfBytes,
        Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> boxes,
        Dictionary<int, (double Width, double Height)> pageSizes)
    {
        var pageImages = _conversion.ConvertPdfToImages(pdfBytes, "jpg").GetAwaiter().GetResult();

        using var output = new PdfDocument();
        for (var i = 0; i < pageImages.Count; i++)
        {
            var pageNumber = i + 1;
            var imageBytes = pageImages[i];

            if (boxes.TryGetValue(pageNumber, out var pageBoxes) && pageSizes.TryGetValue(pageNumber, out var size))
            {
                using var bitmap = SKBitmap.Decode(imageBytes)
                    ?? throw new InvalidOperationException($"Could not decode rendered page {pageNumber}.");
                using (var canvas = new SKCanvas(bitmap))
                using (var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill })
                {
                    var scaleX = bitmap.Width / size.Width;
                    var scaleY = bitmap.Height / size.Height;
                    foreach (var (left, bottom, right, top) in pageBoxes)
                    {
                        // PDF origin is bottom-left; bitmaps are top-left. Pad by
                        // 2pt so glyph antialiasing at the edges is covered too.
                        var rect = new SKRect(
                            (float)((left - 2) * scaleX),
                            (float)((size.Height - top - 2) * scaleY),
                            (float)((right + 2) * scaleX),
                            (float)((size.Height - bottom + 2) * scaleY));
                        canvas.DrawRect(rect, paint);
                    }
                }
                using var painted = SKImage.FromBitmap(bitmap);
                using var encoded = painted.Encode(SKEncodedImageFormat.Jpeg, 90);
                imageBytes = encoded.ToArray();
            }

            var page = output.AddPage();
            if (pageSizes.TryGetValue(pageNumber, out var dims))
            {
                page.Width = XUnit.FromPoint(dims.Width);
                page.Height = XUnit.FromPoint(dims.Height);
            }
            // publiclyVisible: PdfSharp's image importer reads the underlying buffer directly.
            using var imageStream = new MemoryStream(imageBytes, 0, imageBytes.Length, writable: false, publiclyVisible: true);
            using var ximage = XImage.FromStream(imageStream);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(ximage, 0, 0, page.Width.Point, page.Height.Point);
        }

        using var outStream = new MemoryStream();
        output.Save(outStream);
        return outStream.ToArray();
    }
}
