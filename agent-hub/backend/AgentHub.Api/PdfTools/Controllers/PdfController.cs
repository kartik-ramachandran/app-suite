using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using PdfAI.API.Services;
using PdfAI.API.Models;

namespace PdfAI.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[Consumes("multipart/form-data")]
public class PdfController : ControllerBase
{
    private readonly PdfProcessingService _pdfService;
    private readonly PdfOverlayService _overlayService;
    private readonly PdfCompareService _compareService;
    private readonly ValidationAgentService _validationAgentService;
    private readonly PdfFormService _formService;
    private readonly PdfRedactionService _redactionService;
    private readonly PdfArchiveService _archiveService;
    private readonly ILogger<PdfController> _logger;

    private static readonly System.Text.Json.JsonSerializerOptions OverlayJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public PdfController(
        PdfProcessingService pdfService,
        PdfOverlayService overlayService,
        PdfCompareService compareService,
        ValidationAgentService validationAgentService,
        PdfFormService formService,
        PdfRedactionService redactionService,
        PdfArchiveService archiveService,
        ILogger<PdfController> logger)
    {
        _pdfService = pdfService;
        _overlayService = overlayService;
        _compareService = compareService;
        _validationAgentService = validationAgentService;
        _formService = formService;
        _redactionService = redactionService;
        _archiveService = archiveService;
        _logger = logger;
    }

    [HttpPost("merge")]
    [RequestSizeLimit(104857600)] // 100 MB
    public async Task<IActionResult> MergePdfs(List<IFormFile> files)
    {
        try
        {
            var pdfBytes = new List<byte[]>();
            foreach (var file in files)
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                pdfBytes.Add(ms.ToArray());
            }

            var result = await _pdfService.MergePdfs(pdfBytes);
            return File(result, "application/pdf", "merged.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error merging PDFs");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("split")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> SplitPdf(IFormFile file, [FromForm] string? pages = null, [FromForm] bool separate = false)
    {
        try
        {
            var pdfBytes = await ReadBytes(file);
            var selectedPages = ParsePageSelection(pages);

            // When `separate` is set, each selected page is returned as its own PDF
            // inside a ZIP; otherwise the selected pages are combined into one PDF.
            if (separate)
            {
                var zip = await _pdfService.ExtractPdfPagesToZip(pdfBytes, selectedPages);
                return File(zip, "application/zip", "split_pages.zip");
            }

            var result = await _pdfService.ExtractPdfPages(pdfBytes, selectedPages);
            return File(result, "application/pdf", selectedPages.Count == 1 ? $"page_{selectedPages[0]}.pdf" : "selected_pages.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error splitting PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("split/inspect")]
    public async Task<IActionResult> InspectSplitPdf(IFormFile file)
    {
        try
        {
            var pdfBytes = await ReadBytes(file);
            var result = await _pdfService.InspectPdf(pdfBytes);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inspecting PDF for split");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("compress")]
    public async Task<IActionResult> CompressPdf(IFormFile file, [FromForm] string quality = "recommended")
    {
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var pdfBytes = ms.ToArray();

            var result = await _pdfService.CompressPdf(pdfBytes, quality);
            return File(result, "application/pdf", "compressed.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error compressing PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("convert/word")]
    public async Task<IActionResult> ConvertToWord(IFormFile file)
    {
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var pdfBytes = ms.ToArray();

            var result = await _pdfService.ConvertPdfToWord(pdfBytes);
            return File(result, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "converted.docx");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting to Word");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("convert/excel")]
    public async Task<IActionResult> ConvertToExcel(IFormFile file)
    {
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var pdfBytes = ms.ToArray();

            var result = await _pdfService.ConvertPdfToExcel(pdfBytes);
            return File(result, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "converted.xlsx");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting to Excel");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("convert/powerpoint")]
    public async Task<IActionResult> ConvertToPowerPoint(IFormFile file)
    {
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var pdfBytes = ms.ToArray();

            var result = await _pdfService.ConvertPdfToPowerPoint(pdfBytes);
            return File(result, "application/vnd.openxmlformats-officedocument.presentationml.presentation", "converted.pptx");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting to PowerPoint");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("convert/images")]
    public async Task<IActionResult> ConvertToImages(IFormFile file, [FromForm] string format = "jpg")
    {
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var pdfBytes = ms.ToArray();

            var results = await _pdfService.ConvertPdfToImages(pdfBytes, format);
            if (results.Count == 0)
                return BadRequest(new { error = "No pages could be rendered from this PDF." });

            var ext = format.Equals("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
            var mimeType = ext == "png" ? "image/png" : "image/jpeg";

            // Single page → return the image directly. Multiple pages → bundle every page into a ZIP.
            if (results.Count == 1)
                return File(results[0], mimeType, $"page_1.{ext}");

            using var zipStream = new MemoryStream();
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, true))
            {
                for (var i = 0; i < results.Count; i++)
                {
                    var entry = archive.CreateEntry($"page_{i + 1}.{ext}", System.IO.Compression.CompressionLevel.Fastest);
                    using var entryStream = entry.Open();
                    entryStream.Write(results[i], 0, results[i].Length);
                }
            }
            return File(zipStream.ToArray(), "application/zip", "pages.zip");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting to images");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("convert/html")]
    public async Task<IActionResult> ConvertToHtml(IFormFile file)
    {
        try
        {
            var pdfBytes = await ReadBytes(file);
            var result = _pdfService.ConvertPdfToHtml(pdfBytes);
            return File(result, "text/html; charset=utf-8", "converted.html");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting to HTML");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("convert/markdown")]
    public async Task<IActionResult> ConvertToMarkdown(IFormFile file)
    {
        try
        {
            var pdfBytes = await ReadBytes(file);
            var result = _pdfService.ConvertPdfToMarkdown(pdfBytes);
            // Text-first Markdown plus an images/ folder, bundled as a ZIP.
            return File(result, "application/zip", "converted-markdown.zip");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting to Markdown");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("convert/text")]
    public async Task<IActionResult> ConvertToText(IFormFile file)
    {
        try
        {
            var pdfBytes = await ReadBytes(file);
            var result = _pdfService.ConvertPdfToText(pdfBytes);
            return File(result, "text/plain; charset=utf-8", "converted.txt");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting to Text");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("word-to-pdf")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ConvertWordToPdf(List<IFormFile> files, [FromForm] string? overlayOptions, List<IFormFile>? overlayImages)
    {
        try
        {
            var ct = HttpContext.RequestAborted;
            var result = await ConvertFilesToSinglePdf(files, async file =>
                await _pdfService.ConvertWordToPdf(await ReadBytes(file), Path.GetExtension(file.FileName), ct));
            result = await ApplyOverlayIfRequested(result, overlayOptions, overlayImages, files.FirstOrDefault()?.FileName);
            return File(result, "application/pdf", files.Count > 1 ? "converted-merged.pdf" : "converted.pdf");
        }
        catch (OperationCanceledException) { return StatusCode(499); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting Word to PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("excel-to-pdf")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ConvertExcelToPdf(List<IFormFile> files, [FromForm] string? overlayOptions, List<IFormFile>? overlayImages)
    {
        try
        {
            var ct = HttpContext.RequestAborted;
            var result = await ConvertFilesToSinglePdf(files, async file =>
                await _pdfService.ConvertExcelToPdf(await ReadBytes(file), Path.GetExtension(file.FileName), ct));
            result = await ApplyOverlayIfRequested(result, overlayOptions, overlayImages, files.FirstOrDefault()?.FileName);
            return File(result, "application/pdf", files.Count > 1 ? "converted-merged.pdf" : "converted.pdf");
        }
        catch (OperationCanceledException) { return StatusCode(499); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting Excel to PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("powerpoint-to-pdf")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ConvertPowerPointToPdf(List<IFormFile> files, [FromForm] string? overlayOptions, List<IFormFile>? overlayImages)
    {
        try
        {
            var ct = HttpContext.RequestAborted;
            var result = await ConvertFilesToSinglePdf(files, async file =>
                await _pdfService.ConvertPowerPointToPdf(await ReadBytes(file), Path.GetExtension(file.FileName), ct));
            result = await ApplyOverlayIfRequested(result, overlayOptions, overlayImages, files.FirstOrDefault()?.FileName);
            return File(result, "application/pdf", files.Count > 1 ? "converted-merged.pdf" : "converted.pdf");
        }
        catch (OperationCanceledException) { return StatusCode(499); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting PowerPoint to PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("html-to-pdf")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ConvertHtmlToPdf(List<IFormFile> files, [FromForm] string? overlayOptions, List<IFormFile>? overlayImages)
    {
        try
        {
            var result = await ConvertFilesToSinglePdf(files, async file =>
                _pdfService.ConvertHtmlToPdf(await ReadBytes(file)));
            result = await ApplyOverlayIfRequested(result, overlayOptions, overlayImages, files.FirstOrDefault()?.FileName);
            return File(result, "application/pdf", files.Count > 1 ? "converted-merged.pdf" : "converted.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting HTML to PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("markdown-to-pdf")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ConvertMarkdownToPdf(List<IFormFile> files, [FromForm] string? overlayOptions, List<IFormFile>? overlayImages)
    {
        try
        {
            var result = await ConvertFilesToSinglePdf(files, async file =>
                _pdfService.ConvertMarkdownToPdf(await ReadBytes(file)));
            result = await ApplyOverlayIfRequested(result, overlayOptions, overlayImages, files.FirstOrDefault()?.FileName);
            return File(result, "application/pdf", files.Count > 1 ? "converted-merged.pdf" : "converted.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting Markdown to PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("text-to-pdf")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ConvertTextToPdf(List<IFormFile> files, [FromForm] string? overlayOptions, List<IFormFile>? overlayImages)
    {
        try
        {
            var result = await ConvertFilesToSinglePdf(files, async file =>
                _pdfService.ConvertTextToPdf(await ReadBytes(file)));
            result = await ApplyOverlayIfRequested(result, overlayOptions, overlayImages, files.FirstOrDefault()?.FileName);
            return File(result, "application/pdf", files.Count > 1 ? "converted-merged.pdf" : "converted.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting text to PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("images-to-pdf")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ConvertImagesToPdf(List<IFormFile> files, [FromForm] string? overlayOptions, List<IFormFile>? overlayImages)
    {
        try
        {
            var imageBytes = new List<byte[]>();
            foreach (var file in files)
            {
                imageBytes.Add(await ReadBytes(file));
            }

            var result = _pdfService.ConvertImagesToPdf(imageBytes);
            result = await ApplyOverlayIfRequested(result, overlayOptions, overlayImages, files.FirstOrDefault()?.FileName);
            return File(result, "application/pdf", "images.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting images to PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("page-numbers")]
    public async Task<IActionResult> AddPageNumbers(IFormFile file)
    {
        try
        {
            var pdfBytes = await ReadBytes(file);
            var result = _pdfService.AddPageNumbers(pdfBytes);
            return File(result, "application/pdf", "numbered.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding page numbers");
            return BadRequest(new { error = ex.Message });
        }
    }

    // Stamps configurable page numbers, headers and footers onto an uploaded PDF.
    // `options` is a JSON-serialized PdfOverlayOptions; `images` are optional logos
    // referenced by zero-based index from header/footer segments.
    [HttpPost("overlay")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ApplyOverlay(IFormFile file, [FromForm] string options, List<IFormFile>? images)
    {
        try
        {
            var pdfBytes = await ReadBytes(file);

            PdfOverlayOptions parsed;
            try
            {
                parsed = System.Text.Json.JsonSerializer.Deserialize<PdfOverlayOptions>(options ?? string.Empty, OverlayJsonOptions)
                    ?? throw new ArgumentException("Overlay options are required.");
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest(new { error = "Overlay options were not valid JSON." });
            }

            var logos = new List<byte[]>();
            if (images is not null)
            {
                foreach (var image in images)
                {
                    logos.Add(await ReadBytes(image));
                }
            }

            var result = _overlayService.ApplyOverlay(pdfBytes, parsed, logos, file.FileName);
            return File(result, "application/pdf", "overlay.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying PDF overlay");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("repair")]
    public async Task<IActionResult> RepairPdf(IFormFile file)
    {
        try
        {
            var pdfBytes = await ReadBytes(file);
            var result = _pdfService.RepairPdf(pdfBytes);
            return File(result, "application/pdf", "repaired.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error repairing PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("watermark")]
    public async Task<IActionResult> AddWatermark(IFormFile file, [FromForm] string text)
    {
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var pdfBytes = ms.ToArray();

            var result = await _pdfService.AddWatermark(pdfBytes, text);
            return File(result, "application/pdf", "watermarked.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding watermark");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("rotate")]
    public async Task<IActionResult> RotatePdf(IFormFile file, [FromForm] int degrees = 90)
    {
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var pdfBytes = ms.ToArray();

            var result = await _pdfService.RotatePdf(pdfBytes, degrees);
            return File(result, "application/pdf", "rotated.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rotating PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("ocr")]
    public async Task<IActionResult> OcrPdf(IFormFile file)
    {
        try
        {
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var pdfBytes = ms.ToArray();

            var result = await _pdfService.OcrPdf(pdfBytes);
            return File(System.Text.Encoding.UTF8.GetBytes(result), "text/plain", "extracted-text.txt");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing OCR");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("compare")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ComparePdfs(IFormFile leftFile, IFormFile rightFile)
    {
        try
        {
            if (leftFile.Length == 0 || rightFile.Length == 0)
            {
                return BadRequest(new { error = "Both PDF files are required." });
            }

            using var leftStream = new MemoryStream();
            using var rightStream = new MemoryStream();
            await leftFile.CopyToAsync(leftStream);
            await rightFile.CopyToAsync(rightStream);

            var result = await _compareService.CompareAsync(leftStream.ToArray(), rightStream.ToArray());
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error comparing PDFs");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("validate")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ValidateOutcome(
        [FromForm] string operation,
        List<IFormFile> originalFiles,
        IFormFile? outputFile = null,
        [FromForm] string? optionsJson = null,
        [FromForm] string? warningsJson = null,
        [FromForm] string? resultJson = null)
    {
        try
        {
            var originals = new List<UploadedDocument>();
            foreach (var file in originalFiles)
            {
                originals.Add(await ReadUploadedDocument(file));
            }

            var output = outputFile is null ? null : await ReadUploadedDocument(outputFile);
            var result = _validationAgentService.Validate(operation, originals, output, optionsJson, warningsJson, resultJson);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating PDF outcome");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("unlock")]
    public IActionResult UnlockPdf(IFormFile file, [FromForm] string? password = null)
    {
        try
        {
            using var ms = new MemoryStream();
            file.CopyTo(ms);
            var pdfBytes = ms.ToArray();

            var result = _pdfService.UnlockPdf(pdfBytes, password);
            return File(result, "application/pdf", "unlocked.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unlocking PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("protect")]
    public IActionResult ProtectPdf(
        IFormFile file, 
        [FromForm] string userPassword, 
        [FromForm] string ownerPassword)
    {
        try
        {
            using var ms = new MemoryStream();
            file.CopyTo(ms);
            var pdfBytes = ms.ToArray();

            var result = _pdfService.ProtectPdf(pdfBytes, userPassword, ownerPassword);
            return File(result, "application/pdf", "protected.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error protecting PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Forms: inspect + fill (business doc-workflow features) ──────────────

    [HttpPost("form/fields")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ListFormFields(IFormFile file)
    {
        try
        {
            var fields = _formService.ListFields(await ReadBytes(file));
            return Ok(new { count = fields.Count, fields });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing form fields");
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Fill AcroForm fields from JSON ({"fieldName": "value", …}). With
    /// flatten=true the filled PDF is rebuilt from rendered pages so values
    /// become permanent, non-editable page content.
    /// </summary>
    [HttpPost("form/fill")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> FillForm(IFormFile file, [FromForm] string fields, [FromForm] bool flatten = false)
    {
        try
        {
            Dictionary<string, string>? values;
            try
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(fields ?? "");
                values = parsed?.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? kv.Value.GetString() ?? ""
                        : kv.Value.GetRawText());
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest(new { error = "fields must be a JSON object of name → value." });
            }
            if (values == null || values.Count == 0)
                return BadRequest(new { error = "Provide at least one field value." });

            var (pdf, filled, skipped) = _formService.FillForm(await ReadBytes(file), values);

            if (flatten)
            {
                var pages = await _pdfService.ConvertPdfToImages(pdf, "jpg");
                if (pages.Count > 0) pdf = _pdfService.ConvertImagesToPdf(pages);
            }

            Response.Headers["X-Fields-Filled"] = filled.Count.ToString();
            Response.Headers["X-Fields-Skipped"] = skipped.Count == 0 ? "0" : $"{skipped.Count} ({string.Join(", ", skipped.Take(8))})";
            return File(pdf, "application/pdf", flatten ? "filled-flattened.pdf" : "filled.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error filling PDF form");
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Redaction (true content removal) ─────────────────────────────────────

    public class RedactRequestOptions
    {
        public bool Emails { get; set; } = true;
        public bool Phones { get; set; }
        public bool CreditCards { get; set; }
        public bool NationalIds { get; set; }
        public bool Ibans { get; set; }
        public List<string>? Terms { get; set; }
        public bool UseAiPii { get; set; }
    }

    /// <summary>
    /// Redact by pattern category, explicit terms, and/or AI-detected PII.
    /// Matched regions are painted out on rasterized pages before the PDF is
    /// rebuilt — the underlying text is removed, not covered.
    /// </summary>
    [HttpPost("redact")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> RedactPdf(IFormFile file, [FromForm] string? options = null)
    {
        try
        {
            RedactRequestOptions parsed;
            try
            {
                parsed = string.IsNullOrWhiteSpace(options)
                    ? new RedactRequestOptions()
                    : System.Text.Json.JsonSerializer.Deserialize<RedactRequestOptions>(options, OverlayJsonOptions) ?? new RedactRequestOptions();
            }
            catch (System.Text.Json.JsonException)
            {
                return BadRequest(new { error = "Redaction options were not valid JSON." });
            }

            var result = await _redactionService.RedactAsync(await ReadBytes(file), new PdfRedactionService.RedactionOptions(
                parsed.Emails, parsed.Phones, parsed.CreditCards, parsed.NationalIds, parsed.Ibans,
                parsed.Terms ?? new List<string>(), parsed.UseAiPii));

            Response.Headers["X-Redaction-Count"] = result.MatchCount.ToString();
            Response.Headers["X-Redaction-Summary"] = string.Join(", ", result.ByCategory.Select(kv => $"{kv.Key}:{kv.Value}"));
            return File(result.Pdf, "application/pdf", result.MatchCount > 0 ? "redacted.pdf" : "no-matches.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error redacting PDF");
            return BadRequest(new { error = ex.Message });
        }
    }

    // ── Archival compliance: PDF/A + metadata scrubbing ─────────────────────

    [HttpPost("pdfa")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ConvertToPdfA(IFormFile file, [FromForm] string level = "2b")
    {
        try
        {
            var result = await _archiveService.ConvertToPdfAAsync(await ReadBytes(file), level);
            return File(result, "application/pdf", $"pdfa-{(level.StartsWith('3') ? "3b" : "2b")}.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error converting to PDF/A");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("pdfa/validate")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> ValidatePdfA(IFormFile file)
    {
        try
        {
            return Ok(_archiveService.ValidatePdfA(await ReadBytes(file)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating PDF/A");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("clean-metadata")]
    [RequestSizeLimit(104857600)]
    public async Task<IActionResult> CleanMetadata(IFormFile file)
    {
        try
        {
            var result = _archiveService.StripMetadata(await ReadBytes(file));
            return File(result, "application/pdf", "clean.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stripping PDF metadata");
            return BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<UploadedDocument> ReadUploadedDocument(IFormFile file)
    {
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        return new UploadedDocument(file.FileName, file.ContentType, stream.ToArray());
    }

    private static async Task<byte[]> ReadBytes(IFormFile file)
    {
        using var stream = new MemoryStream();
        await file.CopyToAsync(stream);
        return stream.ToArray();
    }

    // Optional post-conversion pass: if the caller supplied overlayOptions JSON, stamp
    // page numbers/header/footer onto the freshly converted PDF. Malformed options are
    // ignored so a bad overlay never fails an otherwise successful conversion.
    private async Task<byte[]> ApplyOverlayIfRequested(byte[] pdfBytes, string? overlayOptions, List<IFormFile>? overlayImages, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(overlayOptions))
        {
            return pdfBytes;
        }

        PdfOverlayOptions? parsed;
        try
        {
            parsed = System.Text.Json.JsonSerializer.Deserialize<PdfOverlayOptions>(overlayOptions, OverlayJsonOptions);
        }
        catch (System.Text.Json.JsonException ex)
        {
            _logger.LogWarning(ex, "Ignoring invalid overlayOptions on conversion");
            return pdfBytes;
        }

        if (parsed is null)
        {
            return pdfBytes;
        }

        var logos = new List<byte[]>();
        if (overlayImages is not null)
        {
            foreach (var image in overlayImages)
            {
                logos.Add(await ReadBytes(image));
            }
        }

        return _overlayService.ApplyOverlay(pdfBytes, parsed, logos, fileName);
    }

    private static List<int> ParsePageSelection(string? pages)
    {
        if (string.IsNullOrWhiteSpace(pages))
        {
            throw new ArgumentException("Select at least one page to split.");
        }

        var selected = new SortedSet<int>();
        foreach (var part in pages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Contains('-', StringComparison.Ordinal))
            {
                var range = part.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (range.Length != 2 || !int.TryParse(range[0], out var start) || !int.TryParse(range[1], out var end) || start <= 0 || end < start)
                {
                    throw new ArgumentException($"Invalid page range '{part}'.");
                }

                for (var page = start; page <= end; page++)
                {
                    selected.Add(page);
                }
            }
            else if (int.TryParse(part, out var page) && page > 0)
            {
                selected.Add(page);
            }
            else
            {
                throw new ArgumentException($"Invalid page number '{part}'.");
            }
        }

        return selected.ToList();
    }

    private async Task<byte[]> ConvertFilesToSinglePdf(List<IFormFile> files, Func<IFormFile, Task<byte[]>> convert)
    {
        if (files.Count == 0)
        {
            throw new ArgumentException("At least one file is required.", nameof(files));
        }

        var convertedPdfs = new List<byte[]>();
        foreach (var file in files)
        {
            convertedPdfs.Add(await convert(file));
        }

        return convertedPdfs.Count == 1
            ? convertedPdfs[0]
            : await _pdfService.MergePdfs(convertedPdfs);
    }
}
