using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PdfAI.API.Models;

namespace PdfAI.API.Services;

/// <summary>
/// Stamps page numbers, headers and footers onto an already-rendered PDF.
///
/// This is a single post-process pass that runs regardless of how the PDF was
/// produced (Office COM, LibreOffice, Chromium, or our own PdfSharp renderers),
/// so headers/footers/page numbers behave identically across every conversion.
///
/// Known limitation: drawing is appended in media-box coordinates, so on pages
/// with a non-zero /Rotate the stamped text is not re-oriented to match the
/// viewer rotation. This matches the existing watermark/page-number behaviour.
/// </summary>
public class PdfOverlayService
{
    private readonly ILogger<PdfOverlayService> _logger;

    public PdfOverlayService(ILogger<PdfOverlayService> logger)
    {
        _logger = logger;
    }

    public byte[] ApplyOverlay(byte[] pdfBytes, PdfOverlayOptions options, IReadOnlyList<byte[]>? images = null, string? sourceFileName = null)
    {
        images ??= Array.Empty<byte[]>();

        using var inputStream = new MemoryStream(pdfBytes);
        using var document = PdfReader.Open(inputStream, PdfDocumentOpenMode.Modify);

        var pageCount = document.Pages.Count;
        var now = DateTime.Now;
        var fileName = string.IsNullOrWhiteSpace(sourceFileName) ? string.Empty : Path.GetFileName(sourceFileName);
        var title = !string.IsNullOrWhiteSpace(options.Title)
            ? options.Title!
            : Path.GetFileNameWithoutExtension(fileName);

        // Decode logo images once and reuse the XImage across every page.
        var logoCache = new Dictionary<int, XImage?>();
        XImage? ResolveImage(int? index)
        {
            if (index is not int i || i < 0 || i >= images.Count)
            {
                return null;
            }

            if (!logoCache.TryGetValue(i, out var cached))
            {
                try
                {
                    cached = XImage.FromStream(new MemoryStream(images[i], writable: false));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Overlay image at index {Index} could not be decoded; skipping", i);
                    cached = null;
                }

                logoCache[i] = cached;
            }

            return cached;
        }

        for (var index = 0; index < pageCount; index++)
        {
            var page = document.Pages[index];
            var isFirst = index == 0;

            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var pageWidth = page.Width.Point;
            var pageHeight = page.Height.Point;
            var margin = options.MarginPoints;

            string ResolveTokens(string? text, int displayNumber, int totalPages)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return string.Empty;
                }

                return text
                    .Replace("{page}", displayNumber.ToString(CultureInfo.InvariantCulture))
                    .Replace("{pages}", totalPages.ToString(CultureInfo.InvariantCulture))
                    .Replace("{date}", now.ToString("d", CultureInfo.CurrentCulture))
                    .Replace("{time}", now.ToString("t", CultureInfo.CurrentCulture))
                    .Replace("{filename}", fileName)
                    .Replace("{title}", title);
            }

            // ---- Header ----
            if (options.Header is { Enabled: true } header && !(header.SkipFirstPage && isFirst))
            {
                var font = BuildFont(header.FontFamily, header.FontSize, header.Bold);
                var brush = new XSolidBrush(ParseColor(header.Color));
                var displayNumber = index + (options.PageNumbers?.StartAt ?? 1);
                DrawBand(gfx, font, brush, margin, isTop: true, pageWidth, pageHeight, margin,
                    s => ResolveTokens(s, displayNumber, pageCount), header, ResolveImage);
            }

            // ---- Footer ----
            if (options.Footer is { Enabled: true } footer && !(footer.SkipFirstPage && isFirst))
            {
                var font = BuildFont(footer.FontFamily, footer.FontSize, footer.Bold);
                var brush = new XSolidBrush(ParseColor(footer.Color));
                var displayNumber = index + (options.PageNumbers?.StartAt ?? 1);
                DrawBand(gfx, font, brush, pageHeight - margin - footer.FontSize, isTop: false, pageWidth, pageHeight, margin,
                    s => ResolveTokens(s, displayNumber, pageCount), footer, ResolveImage);
            }

            // ---- Page numbers (independent layer, own anchor) ----
            if (options.PageNumbers is { Enabled: true } pn && !(pn.SkipFirstPage && isFirst))
            {
                var displayNumber = index + pn.StartAt;
                var label = ResolveTokens(pn.Format, displayNumber, pageCount);
                if (!string.IsNullOrWhiteSpace(label))
                {
                    var font = BuildFont(pn.FontFamily, pn.FontSize, pn.Bold);
                    var brush = new XSolidBrush(ParseColor(pn.Color));
                    DrawAnchoredText(gfx, font, brush, label, pn.Position, pageWidth, pageHeight, margin);
                }
            }
        }

        using var outputStream = new MemoryStream();
        document.Save(outputStream);
        return outputStream.ToArray();
    }

    // Draws the three segments (left/center/right) of a header or footer band.
    private void DrawBand(
        XGraphics gfx, XFont font, XBrush brush, double y, bool isTop,
        double pageWidth, double pageHeight, double margin,
        Func<string?, string> resolve, HeaderFooterOptions band, Func<int?, XImage?> resolveImage)
    {
        DrawSegment(gfx, font, brush, band.Left, "left", y, pageWidth, margin, resolve, resolveImage);
        DrawSegment(gfx, font, brush, band.Center, "center", y, pageWidth, margin, resolve, resolveImage);
        DrawSegment(gfx, font, brush, band.Right, "right", y, pageWidth, margin, resolve, resolveImage);
    }

    private void DrawSegment(
        XGraphics gfx, XFont font, XBrush brush, OverlaySegment? segment, string horizontal,
        double y, double pageWidth, double margin, Func<string?, string> resolve, Func<int?, XImage?> resolveImage)
    {
        if (segment is null)
        {
            return;
        }

        var text = resolve(segment.Text);
        var image = resolveImage(segment.ImageIndex);
        var hasText = !string.IsNullOrWhiteSpace(text);
        if (!hasText && image is null)
        {
            return;
        }

        var lineHeight = font.GetHeight();
        var contentLeft = margin;
        var contentRight = pageWidth - margin;

        // Image is scaled to the segment's max height; width preserves aspect ratio.
        double imageWidth = 0, imageHeight = 0;
        if (image is not null)
        {
            imageHeight = Math.Min(segment.ImageMaxHeight, image.PixelHeight);
            imageWidth = image.PixelWidth * (imageHeight / Math.Max(1, image.PixelHeight));
        }

        var gap = image is not null && hasText ? 6d : 0d;
        var textWidth = hasText ? gfx.MeasureString(text, font).Width : 0d;
        var totalWidth = imageWidth + gap + textWidth;

        double startX = horizontal switch
        {
            "right" => contentRight - totalWidth,
            "center" => (contentLeft + contentRight) / 2 - totalWidth / 2,
            _ => contentLeft,
        };

        var x = startX;
        if (image is not null)
        {
            // Vertically center the image against the text baseline band.
            var imageY = y + (lineHeight - imageHeight) / 2;
            gfx.DrawImage(image, x, imageY, imageWidth, imageHeight);
            x += imageWidth + gap;
        }

        if (hasText)
        {
            gfx.DrawString(text, font, brush, new XRect(x, y, textWidth, lineHeight), XStringFormats.TopLeft);
        }
    }

    private static void DrawAnchoredText(
        XGraphics gfx, XFont font, XBrush brush, string text, string position,
        double pageWidth, double pageHeight, double margin)
    {
        var parts = position.Split('-', 2);
        var vertical = parts.Length > 0 ? parts[0] : "bottom";
        var horizontal = parts.Length > 1 ? parts[1] : "center";

        var lineHeight = font.GetHeight();
        var y = vertical == "top" ? margin : pageHeight - margin - font.Size;
        var rect = new XRect(margin, y, pageWidth - margin * 2, lineHeight);

        var format = horizontal switch
        {
            "left" => XStringFormats.TopLeft,
            "right" => XStringFormats.TopRight,
            _ => XStringFormats.TopCenter,
        };

        gfx.DrawString(text, font, brush, rect, format);
    }

    private static XFont BuildFont(string family, double size, bool bold)
    {
        var fam = string.IsNullOrWhiteSpace(family) ? "Arial" : family;
        var clampedSize = Math.Clamp(size <= 0 ? 10 : size, 4, 96);
        return new XFont(fam, clampedSize, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
    }

    // Accepts #RGB, #RRGGBB or #AARRGGBB. Falls back to dark gray on bad input.
    private static XColor ParseColor(string? value)
    {
        var fallback = XColor.FromArgb(51, 51, 51);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 3)
        {
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }

        try
        {
            if (hex.Length == 6)
            {
                return XColor.FromArgb(
                    int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber),
                    int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber),
                    int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber));
            }

            if (hex.Length == 8)
            {
                return XColor.FromArgb(
                    int.Parse(hex.Substring(0, 2), NumberStyles.HexNumber),
                    int.Parse(hex.Substring(2, 2), NumberStyles.HexNumber),
                    int.Parse(hex.Substring(4, 2), NumberStyles.HexNumber),
                    int.Parse(hex.Substring(6, 2), NumberStyles.HexNumber));
            }
        }
        catch (FormatException)
        {
            // fall through to default
        }

        return fallback;
    }
}
