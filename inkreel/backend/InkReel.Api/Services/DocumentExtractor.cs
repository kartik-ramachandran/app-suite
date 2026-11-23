using System.Text;
using DocumentFormat.OpenXml.Packaging;
using UglyToad.PdfPig;

namespace InkReel.Api.Services;

public record ExtractedDocument(string Text, int PageCount, List<byte[]> Images);

/// <summary>
/// Pulls text AND embedded images out of an uploaded document so the video can
/// show the document's own figures, not just generated slides. PDF via PdfPig
/// (text layer + page images), DOCX via OpenXml (paragraphs + image parts),
/// MD/TXT as plain text. Page selection applies to PDFs only — the other
/// formats have no page concept worth honouring.
/// </summary>
public class DocumentExtractor
{
    private readonly ILogger<DocumentExtractor> _logger;
    private const int MaxImages = 20;
    private const int MinImageBytes = 6_000; // skip icons/bullets/decorations

    public DocumentExtractor(ILogger<DocumentExtractor> logger) { _logger = logger; }

    public ExtractedDocument Extract(byte[] fileBytes, string fileName, int? fromPage, int? toPage)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => ExtractPdf(fileBytes, fromPage, toPage),
            ".docx" => ExtractDocx(fileBytes),
            ".md" or ".txt" => new ExtractedDocument(Encoding.UTF8.GetString(fileBytes), 1, new()),
            _ => throw new InvalidOperationException("Unsupported file type — use PDF, DOCX, MD, or TXT."),
        };
    }

    private ExtractedDocument ExtractPdf(byte[] bytes, int? fromPage, int? toPage)
    {
        using var doc = PdfDocument.Open(new MemoryStream(bytes));
        var total = doc.NumberOfPages;
        var from = Math.Clamp(fromPage ?? 1, 1, total);
        var to = Math.Clamp(toPage ?? total, from, total);

        var text = new StringBuilder();
        var images = new List<byte[]>();

        foreach (var page in doc.GetPages())
        {
            if (page.Number < from || page.Number > to) continue;
            text.AppendLine(page.Text);
            text.AppendLine();

            try
            {
                foreach (var img in page.GetImages())
                {
                    if (images.Count >= MaxImages) break;
                    // Prefer the PNG-convertible form; fall back to raw bytes
                    // (JPEG streams come through RawBytes directly).
                    if (img.TryGetPng(out var png) && png.Length >= MinImageBytes)
                        images.Add(png);
                    else if (img.RawBytes.Count >= MinImageBytes)
                        images.Add(img.RawBytes.ToArray());
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Image extraction failed on page {Page}", page.Number);
            }
        }

        return new ExtractedDocument(text.ToString(), total, images);
    }

    private ExtractedDocument ExtractDocx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(stream, isEditable: false);

        var body = doc.MainDocumentPart?.Document?.Body;
        var text = body == null ? "" : string.Join("\n\n",
            body.ChildElements.Select(e => e.InnerText).Where(t => !string.IsNullOrWhiteSpace(t)));

        var images = new List<byte[]>();
        try
        {
            foreach (var part in doc.MainDocumentPart!.ImageParts)
            {
                if (images.Count >= MaxImages) break;
                using var s = part.GetStream();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                if (ms.Length >= MinImageBytes) images.Add(ms.ToArray());
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DOCX image extraction failed");
        }

        return new ExtractedDocument(text, 1, images);
    }
}
