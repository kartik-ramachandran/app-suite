using Microsoft.AspNetCore.Mvc;
using PdfAI.API.Services;
using PdfSharp.Pdf.IO;
using PdfTools.Api.Services;

namespace PdfTools.Api.Controllers;

/// <summary>
/// PDF tools usable WITHOUT an account.
///
/// This is the acquisition funnel. The free tool pages rank for high-volume
/// queries ("merge pdf", "compress pdf") but previously could only show
/// instructions and a signup link — the visitor had to create an account before
/// converting anything. iLovePDF and Smallpdf do the opposite: the tool works
/// immediately, and signup is asked for AFTER the file is delivered, to unlock
/// batching, history and the API. That ordering is the whole mechanic.
///
/// DELIBERATELY SEPARATE from PdfController rather than relaxing its
/// class-level [Authorize]. Keeping them apart means:
///   - the paid/authenticated path is untouched and cannot regress
///   - only an explicit allowlist of cheap tools is ever reachable anonymously
///   - quota logic lives in one place instead of being sprinkled with `if`s
///
/// Every tool here is CPU-only. Nothing that costs third-party money (OCR at
/// scale, AI redaction, template generation) is exposed. See ANONYMOUS-ACCESS.md.
/// </summary>
[ApiController]
[Route("api/free-pdf")]
public class AnonymousPdfController : ControllerBase
{
    private readonly PdfProcessingService _pdf;
    private readonly AnonymousPdfPolicy _policy;
    private readonly ILogger<AnonymousPdfController> _logger;

    public AnonymousPdfController(
        PdfProcessingService pdf,
        AnonymousPdfPolicy policy,
        ILogger<AnonymousPdfController> logger)
    {
        _pdf = pdf;
        _policy = policy;
        _logger = logger;
    }

    /// <summary>
    /// Tools reachable without an account, and how many files each accepts.
    ///
    /// An allowlist, never a blocklist: a new tool added to PdfController must
    /// be opted in here explicitly, so the default for anything new is "closed".
    ///
    /// Excluded on purpose:
    ///   ocr       — CPU-heavy per page and the obvious bulk-abuse target
    ///   redact    — AI-detected PII, real per-call cost
    ///   generate  — template + JSON, a paid B2B feature
    ///   pdfa      — archival conversion, a business feature
    ///   unlock    — abuse and legal exposure regardless of cost
    ///   compare / fill-form — low search volume, high complexity
    /// </summary>
    private static readonly Dictionary<string, (bool Multi, string Label)> AllowedTools = new()
    {
        ["merge"] = (true, "Merge PDF"),
        ["split"] = (false, "Split PDF"),
        ["compress"] = (false, "Compress PDF"),
        ["rotate"] = (false, "Rotate PDF"),
        ["page-numbers"] = (false, "Add page numbers"),
        ["remove-pages"] = (false, "Remove pages"),
        ["organize"] = (false, "Organise pages"),
        ["watermark"] = (false, "Watermark PDF"),
        ["repair"] = (false, "Repair PDF"),
        ["pdf-to-jpg"] = (false, "PDF to JPG"),
        ["pdf-to-word"] = (false, "PDF to Word"),
        ["pdf-to-excel"] = (false, "PDF to Excel"),
        ["pdf-to-powerpoint"] = (false, "PDF to PowerPoint"),
        ["pdf-to-html"] = (false, "PDF to HTML"),
        ["pdf-to-markdown"] = (false, "PDF to Markdown"),
        ["pdf-to-text"] = (false, "PDF to text"),
        ["word-to-pdf"] = (false, "Word to PDF"),
        ["excel-to-pdf"] = (false, "Excel to PDF"),
        ["powerpoint-to-pdf"] = (false, "PowerPoint to PDF"),
        ["html-to-pdf"] = (false, "HTML to PDF"),
        ["markdown-to-pdf"] = (false, "Markdown to PDF"),
        ["text-to-pdf"] = (false, "Text to PDF"),
        ["images-to-pdf"] = (true, "Images to PDF"),
    };

    private string ClientIp =>
        HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Quota state for this visitor, so the UI can show "2 of 3 left" before
    /// they pick a file rather than failing them after an upload.
    /// </summary>
    [HttpGet("quota")]
    public IActionResult Quota() => Ok(new
    {
        remaining = _policy.RemainingFor(ClientIp),
        dailyLimit = AnonymousPdfPolicy.DailyQuotaPerIp,
        maxBytes = AnonymousPdfPolicy.MaxBytes,
        maxPages = AnonymousPdfPolicy.MaxPages,
        tools = AllowedTools.Keys.OrderBy(k => k).ToArray(),
    });

    [HttpPost("{tool}")]
    [RequestSizeLimit(AnonymousPdfPolicy.MaxBytes + 1024 * 512)] // + slack for multipart framing
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> Convert(string tool, List<IFormFile> files, [FromForm] string? options = null)
    {
        tool = (tool ?? "").Trim().ToLowerInvariant();

        if (!AllowedTools.TryGetValue(tool, out var spec))
            return NotFound(new { message = "That tool needs a free account.", requiresAccount = true });

        if (files is null || files.Count == 0)
            return BadRequest(new { message = "Choose a file first." });

        if (!spec.Multi && files.Count > 1)
            return BadRequest(new { message = "This tool takes one file at a time." });

        // Cap file COUNT as well as size — 500 x 1MB files is 500MB of work that
        // each individually passes a per-file size check.
        if (files.Count > 20)
            return BadRequest(new { message = "Up to 20 files at a time without an account." });

        var totalBytes = files.Sum(f => f.Length);
        if (totalBytes > AnonymousPdfPolicy.MaxBytes)
            return BadRequest(new
            {
                message = $"Files must total under {AnonymousPdfPolicy.MaxBytes / (1024 * 1024)}MB without an account. Create a free account for larger files.",
                requiresAccount = true,
            });

        var decision = _policy.TryAcquire(ClientIp);
        if (!decision.Allowed)
        {
            if (decision.RetryAfterSeconds is int retry)
                Response.Headers["Retry-After"] = retry.ToString();
            // 429 rather than 403: this is a rate decision, and the message is a
            // conversion opportunity, not a dead end.
            return StatusCode(429, new { message = decision.Reason, requiresAccount = true });
        }

        var succeeded = false;
        try
        {
            var buffers = new List<byte[]>(files.Count);
            foreach (var f in files)
            {
                using var ms = new MemoryStream();
                await f.CopyToAsync(ms);
                buffers.Add(ms.ToArray());
            }

            // Page-count guard. Bytes are a poor proxy for work: a small,
            // heavily compressed PDF can carry thousands of pages. Only applies
            // to inputs that actually are PDFs.
            if (tool is not ("word-to-pdf" or "excel-to-pdf" or "powerpoint-to-pdf"
                             or "html-to-pdf" or "markdown-to-pdf" or "text-to-pdf" or "images-to-pdf"))
            {
                var pages = 0;
                foreach (var b in buffers)
                {
                    try
                    {
                        // PdfSharp Import mode: parses structure to count pages
                        // without rendering. InspectPdf() would also work but it
                        // renders preview images for every page — far too much
                        // work just to enforce a limit.
                        using var ms2 = new MemoryStream(b);
                        using var doc = PdfReader.Open(ms2, PdfDocumentOpenMode.Import);
                        pages += doc.PageCount;
                    }
                    catch
                    {
                        // Unreadable input is a client error, not a crash. Also
                        // catches "renamed .jpg to .pdf" without trusting MIME.
                        return BadRequest(new { message = "That file isn't a readable PDF." });
                    }
                }

                if (pages > AnonymousPdfPolicy.MaxPages)
                    return BadRequest(new
                    {
                        message = $"Up to {AnonymousPdfPolicy.MaxPages} pages without an account (this has {pages}). Create a free account for longer documents.",
                        requiresAccount = true,
                    });
            }

            // Hard timeout so pathological input cannot pin a worker.
            using var cts = new CancellationTokenSource(AnonymousPdfPolicy.MaxDuration);

            var (bytes, contentType, filename) = await RunTool(tool, buffers, options, cts.Token);

            succeeded = true;
            Response.Headers["X-Free-Conversions-Remaining"] = _policy.RemainingFor(ClientIp).ToString();
            return File(bytes, contentType, filename);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Anonymous PDF {Tool} timed out", tool);
            return StatusCode(408, new { message = "That took too long. Try a smaller file, or create a free account." });
        }
        catch (ArgumentException ex)
        {
            // Bad page selections and empty inputs are user error, not server
            // error — surfacing them as 500 would both mislead the user and
            // pollute error monitoring.
            _logger.LogInformation("Anonymous PDF {Tool} rejected input: {Message}", tool, ex.Message);
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            // Never surface exception detail to an anonymous caller: it leaks
            // library versions and file paths.
            _logger.LogError(ex, "Anonymous PDF {Tool} failed", tool);
            return StatusCode(500, new { message = "Could not process that file." });
        }
        finally
        {
            // Must always run: a leaked concurrency slot wedges the tool shut
            // for this IP until the process restarts. `succeeded` false also
            // refunds the daily quota — charging a user for our failure is how
            // a first impression becomes a last one.
            _policy.Release(ClientIp, succeeded);
        }
    }

    /// <summary>
    /// Parse a human page selection: "1,3,5-8" -> [1,3,5,6,7,8].
    ///
    /// Order is preserved rather than sorted, because "organise" depends on the
    /// caller's sequence being honoured exactly ("3,1,2" must not become
    /// "1,2,3"). Bounded at 500 entries so a range like "1-999999999" cannot be
    /// expanded into an allocation attack before page validation ever runs.
    /// Invalid fragments are skipped rather than throwing — a stray character in
    /// a page box should not lose someone's upload.
    /// </summary>
    private static List<int> ParsePageList(string? spec)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(spec)) return result;

        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (result.Count >= 500) break;

            var dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part[..dash].Trim(), out var from) &&
                    int.TryParse(part[(dash + 1)..].Trim(), out var to) &&
                    from >= 1 && to >= from && to - from < 500)
                {
                    for (var p = from; p <= to && result.Count < 500; p++) result.Add(p);
                }
            }
            else if (int.TryParse(part, out var single) && single >= 1)
            {
                result.Add(single);
            }
        }

        return result;
    }

    private async Task<(byte[] Bytes, string ContentType, string Name)> RunTool(
        string tool, List<byte[]> input, string? options, CancellationToken ct)
    {
        // Output names are fixed constants, never derived from the upload —
        // echoing a user filename into Content-Disposition invites header
        // injection and path traversal.
        switch (tool)
        {
            case "merge":
                return (await _pdf.MergePdfs(input), "application/pdf", "merged.pdf");

            case "split":
            {
                var parts = await _pdf.SplitPdf(input[0], "all");
                // Return the first part directly; batching/zip is a signed-in feature.
                return (parts.First(), "application/pdf", "split.pdf");
            }

            case "compress":
                return (await _pdf.CompressPdf(input[0]), "application/pdf", "compressed.pdf");

            case "rotate":
            {
                var degrees = int.TryParse(options, out var d) && d is 90 or 180 or 270 ? d : 90;
                return (await _pdf.RotatePdf(input[0], degrees), "application/pdf", "rotated.pdf");
            }

            case "page-numbers":
                return (_pdf.AddPageNumbers(input[0]), "application/pdf", "numbered.pdf");

            case "remove-pages":
            {
                var pages = ParsePageList(options);
                if (pages.Count == 0)
                    throw new ArgumentException("Tell us which pages to remove, e.g. 1,3,5-8.");
                return (await _pdf.RemovePdfPages(input[0], pages), "application/pdf", "pages-removed.pdf");
            }

            case "organize":
            {
                var order = ParsePageList(options);
                if (order.Count == 0)
                    throw new ArgumentException("Give the page order you want, e.g. 3,1,2.");
                return (await _pdf.ReorderPdfPages(input[0], order), "application/pdf", "organised.pdf");
            }

            case "watermark":
            {
                // Bound the watermark text: it is drawn on every page.
                var text = string.IsNullOrWhiteSpace(options) ? "CONFIDENTIAL" : options.Trim();
                if (text.Length > 60) text = text[..60];
                return (await _pdf.AddWatermark(input[0], text), "application/pdf", "watermarked.pdf");
            }

            case "repair":
                return (_pdf.RepairPdf(input[0]), "application/pdf", "repaired.pdf");

            case "pdf-to-jpg":
            {
                var images = await _pdf.ConvertPdfToImages(input[0], "jpg");
                return (images.First(), "image/jpeg", "page-1.jpg");
            }

            case "pdf-to-word":
                return (await _pdf.ConvertPdfToWord(input[0]),
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "converted.docx");

            case "pdf-to-excel":
                return (await _pdf.ConvertPdfToExcel(input[0]),
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "converted.xlsx");

            case "pdf-to-powerpoint":
                return (await _pdf.ConvertPdfToPowerPoint(input[0]),
                    "application/vnd.openxmlformats-officedocument.presentationml.presentation", "converted.pptx");

            case "pdf-to-html":
                return (_pdf.ConvertPdfToHtml(input[0]), "text/html", "converted.html");

            case "pdf-to-markdown":
                return (_pdf.ConvertPdfToMarkdown(input[0]), "text/markdown", "converted.md");

            case "pdf-to-text":
                return (_pdf.ConvertPdfToText(input[0]), "text/plain", "converted.txt");

            case "word-to-pdf":
                return (await _pdf.ConvertWordToPdf(input[0], ".docx", ct), "application/pdf", "converted.pdf");

            case "excel-to-pdf":
                return (await _pdf.ConvertExcelToPdf(input[0], ".xlsx", ct), "application/pdf", "converted.pdf");

            case "powerpoint-to-pdf":
                return (await _pdf.ConvertPowerPointToPdf(input[0], ".pptx", ct), "application/pdf", "converted.pdf");

            case "html-to-pdf":
                return (_pdf.ConvertHtmlToPdf(input[0]), "application/pdf", "converted.pdf");

            case "markdown-to-pdf":
                return (_pdf.ConvertMarkdownToPdf(input[0]), "application/pdf", "converted.pdf");

            case "text-to-pdf":
                return (_pdf.ConvertTextToPdf(input[0]), "application/pdf", "converted.pdf");

            case "images-to-pdf":
                return (_pdf.ConvertImagesToPdf(input), "application/pdf", "converted.pdf");

            default:
                // Unreachable: the allowlist is checked before this is called.
                throw new InvalidOperationException($"Unmapped tool '{tool}'.");
        }
    }
}
