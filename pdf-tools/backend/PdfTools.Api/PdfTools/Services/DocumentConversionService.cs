using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using XDR = DocumentFormat.OpenXml.Drawing.Spreadsheet;
using DocumentFormat.OpenXml.Packaging;
using P = DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace PdfAI.API.Services;

public class DocumentConversionService
{
    private readonly DocumentExtractionService _extractionService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocumentConversionService> _logger;

    public DocumentConversionService(DocumentExtractionService extractionService, IConfiguration configuration, ILogger<DocumentConversionService> logger)
    {
        _extractionService = extractionService;
        _configuration = configuration;
        _logger = logger;
    }

    public byte[] ConvertPdfToWord(byte[] pdfBytes)
    {
        var pageImages = RenderPdfPages(pdfBytes, "png", 220).GetAwaiter().GetResult();

        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var mainPart = document.AddMainDocumentPart();
            mainPart.Document = new Document(new Body());
            var body = mainPart.Document.Body!;

            for (var index = 0; index < pageImages.Count; index++)
            {
                var imageSize = GetImageSize(pageImages[index]);
                var pageLayout = GetWordPageLayout(imageSize.Width, imageSize.Height);
                var imagePart = mainPart.AddImagePart(ImagePartType.Png);
                using (var imageStream = new MemoryStream(pageImages[index]))
                {
                    imagePart.FeedData(imageStream);
                }

                var imageId = mainPart.GetIdOfPart(imagePart);
                var drawing = CreateWordImageDrawing(imageId, imageSize.Width, imageSize.Height, pageLayout.ContentWidthEmu, pageLayout.ContentHeightEmu, (uint)index + 1);
                body.AppendChild(new Paragraph(
                    new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
                    new DocumentFormat.OpenXml.Wordprocessing.Run(drawing)));

                if (index < pageImages.Count - 1)
                {
                    body.AppendChild(new Paragraph(new ParagraphProperties(
                        new SectionProperties(
                            new PageSize { Width = pageLayout.PageWidthTwips, Height = pageLayout.PageHeightTwips, Orient = pageLayout.IsLandscape ? PageOrientationValues.Landscape : PageOrientationValues.Portrait },
                            new PageMargin { Top = 0, Right = 0, Bottom = 0, Left = 0, Header = 0, Footer = 0, Gutter = 0 }))));
                }
                else
                {
                    body.AppendChild(new SectionProperties(
                        new PageSize { Width = pageLayout.PageWidthTwips, Height = pageLayout.PageHeightTwips, Orient = pageLayout.IsLandscape ? PageOrientationValues.Landscape : PageOrientationValues.Portrait },
                        new PageMargin { Top = 0, Right = 0, Bottom = 0, Left = 0, Header = 0, Footer = 0, Gutter = 0 }));
                }
            }

            mainPart.Document.Save();
        }

        return stream.ToArray();
    }

    public byte[] ConvertPdfToExcel(byte[] pdfBytes)
    {
        var pageImages = RenderPdfPages(pdfBytes, "png", 220).GetAwaiter().GetResult();

        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());

            for (var index = 0; index < pageImages.Count; index++)
            {
                var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
                worksheetPart.Worksheet = new Worksheet(new SheetViews(new SheetView { WorkbookViewId = 0U }), new SheetData());
                AddImageToWorksheet(worksheetPart, pageImages[index], (uint)index + 1);
                worksheetPart.Worksheet.Save();

                sheets.Append(new Sheet
                {
                    Id = workbookPart.GetIdOfPart(worksheetPart),
                    SheetId = (uint)index + 1,
                    Name = $"Page {index + 1}"
                });
            }

            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    public byte[] ConvertPdfToPowerPoint(byte[] pdfBytes)
    {
        var pageImages = RenderPdfPages(pdfBytes, "png", 220).GetAwaiter().GetResult();
        using var stream = new MemoryStream();
        using (var document = PresentationDocument.Create(stream, PresentationDocumentType.Presentation, true))
        {
            var presentationPart = document.AddPresentationPart();
            var slideIdList = new P.SlideIdList();
            var firstPageSize = pageImages.Count > 0 ? GetImageSize(pageImages[0]) : (Width: 1200, Height: 1600);
            var slideSize = GetPresentationSlideSize(firstPageSize.Width, firstPageSize.Height);

            for (var index = 0; index < pageImages.Count; index++)
            {
                var slidePart = presentationPart.AddNewPart<SlidePart>();
                var imagePart = slidePart.AddImagePart(ImagePartType.Png);
                using (var imageStream = new MemoryStream(pageImages[index]))
                {
                    imagePart.FeedData(imageStream);
                }

                var imageId = slidePart.GetIdOfPart(imagePart);
                var imageSize = GetImageSize(pageImages[index]);
                slidePart.Slide = CreateImageSlide(imageId, slideSize.WidthEmu, slideSize.HeightEmu, imageSize.Width, imageSize.Height, (uint)index + 1);
                slidePart.Slide.Save();

                slideIdList.Append(new P.SlideId
                {
                    Id = 256U + (uint)index,
                    RelationshipId = presentationPart.GetIdOfPart(slidePart)
                });
            }

            presentationPart.Presentation = new P.Presentation(
                slideIdList,
                new P.SlideSize { Cx = (int)slideSize.WidthEmu, Cy = (int)slideSize.HeightEmu, Type = P.SlideSizeValues.Custom },
                new P.NotesSize { Cx = 6858000, Cy = 9144000 });
            presentationPart.Presentation.Save();
        }

        return stream.ToArray();
    }

    public async Task<List<byte[]>> ConvertPdfToImages(byte[] pdfBytes, string format)
    {
        return await RenderPdfPages(pdfBytes, format.Equals("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg", 220);
    }

    public async Task<List<byte[]>> RenderPdfPreviewImages(byte[] pdfBytes)
    {
        return await RenderPdfPages(pdfBytes, "jpg", 72);
    }

    public byte[] ConvertPdfToHtml(byte[] pdfBytes)
    {
        var pageImages = RenderPdfPages(pdfBytes, "png", 220).GetAwaiter().GetResult();
        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><title>Converted PDF</title><style>");
        html.AppendLine("body{margin:0;background:#e5e7eb;color:#111827;font-family:Arial,sans-serif}.page{display:block;width:min(100%,var(--page-width));margin:24px auto;background:white;box-shadow:0 10px 30px rgba(15,23,42,.18)}img{display:block;width:100%;height:auto}");
        html.AppendLine("@media print{body{background:white}.page{margin:0 auto;box-shadow:none;break-after:page}.page:last-child{break-after:auto}}");
        html.AppendLine("</style></head><body>");

        for (var index = 0; index < pageImages.Count; index++)
        {
            var imageSize = GetImageSize(pageImages[index]);
            var data = Convert.ToBase64String(pageImages[index]);
            html.AppendLine($"<main class=\"page\" style=\"--page-width:{imageSize.Width}px\" aria-label=\"Page {index + 1}\"><img src=\"data:image/png;base64,{data}\" width=\"{imageSize.Width}\" height=\"{imageSize.Height}\" alt=\"Page {index + 1}\"></main>");
        }

        html.AppendLine("</body></html>");
        return Encoding.UTF8.GetBytes(html.ToString());
    }

    // Returns a ZIP containing a text-first "converted.md" plus an "images/" folder.
    // The Markdown references images by relative path (e.g. images/page-2-img-1.png)
    // instead of inlining base64 — keeping the .md small and editor-friendly.
    public byte[] ConvertPdfToMarkdown(byte[] pdfBytes)
    {
        var pageTexts = ExtractPdfTextByPage(pdfBytes);
        var imagesByPage = ExtractPdfImagesByPage(pdfBytes);
        var hasText = pageTexts.Any(t => !string.IsNullOrWhiteSpace(t));

        var markdown = new StringBuilder();
        markdown.AppendLine("# Converted PDF");
        markdown.AppendLine();

        // Relative path -> bytes; written into the ZIP alongside the Markdown.
        var imageFiles = new List<(string Path, byte[] Bytes)>();

        if (!hasText && imagesByPage.Count == 0)
        {
            // Scanned PDF (no selectable text, no embedded images): render each page to
            // an image file and reference it, so the output is never empty.
            var pageImages = RenderPdfPages(pdfBytes, "png", 180).GetAwaiter().GetResult();
            markdown.AppendLine("> No selectable text was found in this PDF (it may be scanned). Pages are included as images.");
            markdown.AppendLine();
            for (var index = 0; index < pageImages.Count; index++)
            {
                var rel = $"images/page-{index + 1}.png";
                imageFiles.Add((rel, pageImages[index]));
                markdown.AppendLine($"## Page {index + 1}");
                markdown.AppendLine();
                markdown.AppendLine($"![Page {index + 1}]({rel})");
                markdown.AppendLine();
            }
        }
        else
        {
            var pageCount = Math.Max(pageTexts.Count, imagesByPage.Keys.DefaultIfEmpty(0).Max());
            for (var page = 1; page <= pageCount; page++)
            {
                markdown.AppendLine($"## Page {page}");
                markdown.AppendLine();

                var text = page <= pageTexts.Count ? pageTexts[page - 1] : string.Empty;
                var formatted = FormatTextAsMarkdown(text);
                if (!string.IsNullOrWhiteSpace(formatted))
                {
                    markdown.AppendLine(formatted);
                    markdown.AppendLine();
                }

                if (imagesByPage.TryGetValue(page, out var images))
                {
                    for (var i = 0; i < images.Count; i++)
                    {
                        var rel = $"images/page-{page}-img-{i + 1}.{images[i].MimeSubtype}";
                        imageFiles.Add((rel, images[i].Bytes));
                        markdown.AppendLine($"![Page {page} image {i + 1}]({rel})");
                        markdown.AppendLine();
                    }
                }
            }
        }

        // Bundle converted.md + images/ into a ZIP.
        using var zipStream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            var mdEntry = archive.CreateEntry("converted.md", System.IO.Compression.CompressionLevel.Optimal);
            using (var entryStream = mdEntry.Open())
            {
                var mdBytes = Encoding.UTF8.GetBytes(markdown.ToString());
                entryStream.Write(mdBytes, 0, mdBytes.Length);
            }

            foreach (var (path, data) in imageFiles)
            {
                var entry = archive.CreateEntry(path, System.IO.Compression.CompressionLevel.Fastest);
                using var entryStream = entry.Open();
                entryStream.Write(data, 0, data.Length);
            }
        }

        return zipStream.ToArray();
    }

    // Runs pdftotext and returns one string per page, split on the form-feed page break.
    private List<string> ExtractPdfTextByPage(byte[] pdfBytes)
    {
        var executable = _configuration["Tools:Poppler:PdfToTextPath"] ?? GetSiblingToolPath("pdftotext");
        var tempDirectory = Directory.CreateTempSubdirectory("pdfforge-md-text-");
        var inputPath = Path.Combine(tempDirectory.FullName, "input.pdf");
        var outputPath = Path.Combine(tempDirectory.FullName, "converted.txt");

        try
        {
            File.WriteAllBytes(inputPath, pdfBytes);
            RunTool(executable, $"-enc UTF-8 \"{inputPath}\" \"{outputPath}\"").GetAwaiter().GetResult();
            var text = File.ReadAllText(outputPath, Encoding.UTF8);
            // pdftotext separates pages with a form-feed (\f) character.
            return text.Split('\f').ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "pdftotext extraction failed during Markdown conversion");
            return new List<string>();
        }
        finally
        {
            tempDirectory.Delete(true);
        }
    }

    // Runs pdfimages to extract every embedded raster image as PNG, keyed by page number.
    private Dictionary<int, List<(byte[] Bytes, string MimeSubtype)>> ExtractPdfImagesByPage(byte[] pdfBytes)
    {
        var result = new Dictionary<int, List<(byte[], string)>>();
        var executable = GetSiblingToolPath("pdfimages");
        var tempDirectory = Directory.CreateTempSubdirectory("pdfforge-md-img-");
        var inputPath = Path.Combine(tempDirectory.FullName, "input.pdf");
        var outputPrefix = Path.Combine(tempDirectory.FullName, "img");

        try
        {
            File.WriteAllBytes(inputPath, pdfBytes);
            // -p prepends the page number to each filename (img-<page>-<num>.png);
            // -png writes every image as PNG so it renders in any Markdown viewer.
            RunTool(executable, $"-png -p \"{inputPath}\" \"{outputPrefix}\"").GetAwaiter().GetResult();

            foreach (var path in Directory.GetFiles(tempDirectory.FullName, "img-*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                // Filename format: img-<page>-<imageIndex>.png
                var name = Path.GetFileNameWithoutExtension(path);
                var parts = name.Split('-');
                if (parts.Length < 3 || !int.TryParse(parts[^2], out var page))
                {
                    continue;
                }

                var bytes = File.ReadAllBytes(path);
                // Skip tiny artifacts (separators, 1px spacers) that add noise.
                if (bytes.Length < 1024)
                {
                    continue;
                }

                if (!result.TryGetValue(page, out var list))
                {
                    list = new List<(byte[], string)>();
                    result[page] = list;
                }

                list.Add((bytes, "png"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "pdfimages extraction failed during Markdown conversion");
        }
        finally
        {
            tempDirectory.Delete(true);
        }

        return result;
    }

    // Turns raw pdftotext output into readable Markdown: collapses runaway blank lines and
    // treats blank-line-separated blocks as paragraphs while preserving intentional breaks.
    private static string FormatTextAsMarkdown(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        // Collapse 3+ consecutive newlines down to a paragraph break.
        normalized = Regex.Replace(normalized, "\n{3,}", "\n\n");
        return normalized.Trim();
    }

    public byte[] ConvertPdfToText(byte[] pdfBytes)
    {
        var executable = _configuration["Tools:Poppler:PdfToTextPath"] ?? GetSiblingToolPath("pdftotext");
        var tempDirectory = Directory.CreateTempSubdirectory("pdfforge-text-");
        var inputPath = Path.Combine(tempDirectory.FullName, "input.pdf");
        var outputPath = Path.Combine(tempDirectory.FullName, "converted.txt");

        try
        {
            File.WriteAllBytes(inputPath, pdfBytes);
            RunTool(executable, $"-layout -enc UTF-8 \"{inputPath}\" \"{outputPath}\"").GetAwaiter().GetResult();
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            tempDirectory.Delete(true);
        }
    }

    public Task<byte[]> ConvertWordToPdf(byte[] docxBytes, string extension = ".docx", CancellationToken cancellationToken = default)
    {
        return ConvertOfficeToPdfAsync(docxBytes, NormalizeOfficeExtension(extension, ".docx", ".doc"), ExportWordToPdf, cancellationToken);
    }

    public Task<byte[]> ConvertExcelToPdf(byte[] xlsxBytes, string extension = ".xlsx", CancellationToken cancellationToken = default)
    {
        return ConvertOfficeToPdfAsync(xlsxBytes, NormalizeOfficeExtension(extension, ".xlsx", ".xls"), ExportExcelToPdf, cancellationToken);
    }

    public Task<byte[]> ConvertPowerPointToPdf(byte[] pptxBytes, string extension = ".pptx", CancellationToken cancellationToken = default)
    {
        return ConvertOfficeToPdfAsync(pptxBytes, NormalizeOfficeExtension(extension, ".pptx", ".ppt"), ExportPowerPointToPdf, cancellationToken);
    }

    public byte[] ConvertHtmlToPdf(byte[] htmlBytes)
    {
        var browser = GetBrowserPath();
        var tempDirectory = Directory.CreateTempSubdirectory("pdfforge-htmltopdf-");
        var inputPath = Path.Combine(tempDirectory.FullName, "input.html");
        var outputPath = Path.Combine(tempDirectory.FullName, "converted.pdf");

        try
        {
            File.WriteAllBytes(inputPath, htmlBytes);
            // --print-to-pdf-no-header suppresses Chromium's default print header/footer
            // (the date at the top and the source file:// URL + page number at the bottom),
            // which would otherwise leak the temp input path into the generated PDF.
            RunTool(browser, $"--headless --no-sandbox --disable-gpu --disable-dev-shm-usage --print-to-pdf-no-header --no-pdf-header-footer --print-to-pdf=\"{outputPath}\" \"file:///{inputPath.Replace("\\", "/")}\"").GetAwaiter().GetResult();
            if (!File.Exists(outputPath))
            {
                throw new InvalidOperationException("Browser PDF export did not produce an output file.");
            }

            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            tempDirectory.Delete(true);
        }
    }

    public byte[] ConvertMarkdownToPdf(byte[] markdownBytes)
    {
        var markdown = Encoding.UTF8.GetString(markdownBytes);
        // Render via Chromium (full CSS: wrapping, fixed-layout tables, code blocks).
        // Fall back to the built-in PdfSharp renderer only if Chromium is unavailable.
        try
        {
            var html = MarkdownToHtml(markdown);
            return ConvertHtmlToPdf(Encoding.UTF8.GetBytes(html));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Chromium markdown->PDF failed; falling back to PdfSharp renderer");
            return RenderMarkdownToPdf(markdown);
        }
    }

    public byte[] ConvertTextToPdf(byte[] textBytes)
    {
        var text = Encoding.UTF8.GetString(textBytes);
        return CreateTextPdf(text);
    }

    public byte[] ConvertImagesToPdf(IReadOnlyList<byte[]> imageBytes)
    {
        if (imageBytes.Count == 0)
        {
            throw new ArgumentException("At least one image is required.", nameof(imageBytes));
        }

        using var document = new PdfDocument();
        foreach (var bytes in imageBytes)
        {
            if (bytes.Length == 0)
            {
                throw new ArgumentException("One of the selected images is empty.");
            }

            using var stream = new MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true);
            using var image = XImage.FromStream(stream) ?? throw new InvalidOperationException("One of the selected images could not be opened. Please use a valid JPG or PNG file.");
            var page = document.AddPage();
            page.Width = XUnit.FromPoint(image.PointWidth);
            page.Height = XUnit.FromPoint(image.PointHeight);

            using var graphics = XGraphics.FromPdfPage(page);
            graphics.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
        }

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static IEnumerable<string> SplitLines(string text)
    {
        return text.Replace("\r\n", "\n").Split('\n');
    }

    private static string ExtractWordText(byte[] docxBytes)
    {
        using var stream = new MemoryStream(docxBytes);
        using var document = WordprocessingDocument.Open(stream, false);
        return document.MainDocumentPart?.Document?.Body?.InnerText?.Trim() ?? string.Empty;
    }

    private static string ExtractExcelText(byte[] xlsxBytes)
    {
        using var stream = new MemoryStream(xlsxBytes);
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbookPart = document.WorkbookPart;
        var output = new StringBuilder();

        if (workbookPart?.Workbook?.Sheets is not Sheets sheets)
        {
            return string.Empty;
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;

        foreach (var sheet in sheets.Elements<Sheet>())
        {
            var sheetRelationshipId = sheet.Id?.Value;
            if (string.IsNullOrWhiteSpace(sheetRelationshipId))
            {
                continue;
            }

            var worksheetPart = (WorksheetPart?)workbookPart.GetPartById(sheetRelationshipId);
            if (worksheetPart?.Worksheet is null)
            {
                continue;
            }

            output.AppendLine(sheet.Name?.Value ?? "Sheet");
            foreach (var row in worksheetPart.Worksheet.Descendants<Row>())
            {
                var values = row.Elements<Cell>()
                    .Select(cell => GetCellText(cell, sharedStrings))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();

                if (values.Length > 0)
                {
                    output.AppendLine(string.Join("    ", values));
                }
            }

            output.AppendLine();
        }

        return output.ToString().Trim();
    }

    private static string GetCellText(Cell cell, SharedStringTable? sharedStrings)
    {
        var rawValue = cell.CellValue?.Text ?? cell.InnerText ?? string.Empty;
        if (cell.DataType?.Value == CellValues.SharedString && int.TryParse(rawValue, out var sharedStringIndex))
        {
            return sharedStrings?.Elements<SharedStringItem>().ElementAtOrDefault(sharedStringIndex)?.InnerText ?? string.Empty;
        }

        return rawValue;
    }

    private static string ExtractPowerPointText(byte[] pptxBytes)
    {
        using var stream = new MemoryStream(pptxBytes);
        using var document = PresentationDocument.Open(stream, false);
        var output = new StringBuilder();
        var slideNumber = 1;

        var slideParts = document.PresentationPart?.SlideParts ?? Enumerable.Empty<SlidePart>();
        foreach (var slidePart in slideParts)
        {
            if (slidePart.Slide is null)
            {
                continue;
            }

            var lines = slidePart.Slide.Descendants<A.Text>()
                .Select(text => text.Text)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();

            output.AppendLine($"Slide {slideNumber}");
            foreach (var line in lines)
            {
                output.AppendLine(line);
            }

            output.AppendLine();
            slideNumber++;
        }

        return output.ToString().Trim();
    }

    private static string HtmlToPlainText(string html)
    {
        var withoutScripts = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var withBreaks = Regex.Replace(withoutScripts, @"</?(p|div|section|article|header|footer|h[1-6]|li|tr|br)[^>]*>", "\n", RegexOptions.IgnoreCase);
        var withoutTags = Regex.Replace(withBreaks, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"[ \t]+", " ").Trim();
    }

    private static string MarkdownToHtml(string markdown)
    {
        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .Build();

        var html = new StringBuilder();
        html.AppendLine("<!doctype html><html><head><meta charset=\"utf-8\"><style>");
        html.AppendLine("@page{margin:28mm 22mm}*{box-sizing:border-box}body{max-width:920px;margin:0 auto;color:#111827;font-family:Arial,Helvetica,sans-serif;font-size:14px;line-height:1.55;overflow-wrap:break-word}");
        html.AppendLine("h1,h2,h3,h4,h5,h6{color:#0f172a;line-height:1.2;margin:1.3em 0 .55em;font-weight:700;white-space:normal}h1{font-size:32px;border-bottom:1px solid #e2e8f0;padding-bottom:10px}h2{font-size:24px;border-bottom:1px solid #e2e8f0;padding-bottom:8px}h3{font-size:19px}p{margin:.75em 0;white-space:pre-wrap}");
        html.AppendLine("a{color:#2563eb;text-decoration:underline}strong{font-weight:700}em{font-style:italic}hr{border:0;border-top:1px solid #e2e8f0;margin:24px 0}ul,ol{padding-left:28px;margin:.75em 0}li{margin:.25em 0}li>p{margin:.35em 0}");
        html.AppendLine("blockquote{border-left:4px solid #cbd5e1;margin:16px 0;padding:2px 0 2px 16px;color:#475569;background:#f8fafc;white-space:pre-wrap}code,pre{font-family:Consolas,'Liberation Mono',Menlo,monospace;background:#f1f5f9;border-radius:6px}code{padding:2px 5px;font-size:.92em;overflow-wrap:anywhere;word-break:break-word}pre{padding:14px;font-size:12px;line-height:1.45;overflow-wrap:anywhere;word-break:break-word;white-space:pre-wrap;page-break-inside:avoid;max-width:100%}pre code{padding:0;background:transparent;white-space:pre-wrap;word-break:break-word}");
        html.AppendLine("table{width:100%;table-layout:fixed;border-collapse:collapse;margin:18px 0;page-break-inside:auto;white-space:normal}thead{display:table-header-group}tr{page-break-inside:avoid;page-break-after:auto}th,td{border:1px solid #cbd5e1;padding:8px 10px;vertical-align:top;text-align:left;overflow-wrap:anywhere;word-break:break-word}th{background:#f1f5f9;color:#0f172a;font-weight:700}tbody tr:nth-child(even){background:#f8fafc}");
        html.AppendLine("img{max-width:100%;height:auto}input[type=checkbox]{margin-right:6px}.contains-task-list{list-style:none;padding-left:8px}");
        html.AppendLine("</style></head><body>");
        html.AppendLine(Markdown.ToHtml(markdown, pipeline));
        html.AppendLine("</body></html>");
        return html.ToString();
    }

    private async Task<byte[]> ConvertOfficeToPdfAsync(byte[] inputBytes, string extension, Action<string, string> export, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return ConvertViaCom(inputBytes, extension, export);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("not installed", StringComparison.OrdinalIgnoreCase))
            {
            }
        }

        try
        {
            return await ConvertViaLibreOffice(inputBytes, extension, cancellationToken);
        }
        catch (Exception libreEx) when (!OperatingSystem.IsWindows() && libreEx is not OperationCanceledException)
        {
            _logger.LogWarning(libreEx, "LibreOffice conversion failed for {Extension}; falling back to text extraction", extension);

            var text = extension switch
            {
                ".docx" or ".doc" => ExtractWordText(inputBytes),
                ".xlsx" or ".xls" => ExtractExcelText(inputBytes),
                ".pptx" or ".ppt" => ExtractPowerPointText(inputBytes),
                _ => string.Empty
            };
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException(
                    $"LibreOffice failed ({libreEx.Message}) and no text could be extracted. " +
                    "The file may be image-only, password-protected, or corrupted.");
            return CreateTextPdf(text);
        }
    }

    private static byte[] ConvertViaCom(byte[] inputBytes, string extension, Action<string, string> export)
    {
        var tempDirectory = Directory.CreateTempSubdirectory("pdfforge-office-");
        var inputPath = Path.Combine(tempDirectory.FullName, $"input{extension}");
        var outputPath = Path.Combine(tempDirectory.FullName, "converted.pdf");

        try
        {
            File.WriteAllBytes(inputPath, inputBytes);
            export(inputPath, outputPath);
            if (!File.Exists(outputPath))
                throw new InvalidOperationException("Office PDF export did not produce an output file.");

            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            try { tempDirectory.Delete(true); } catch { }
        }
    }

    private async Task<byte[]> ConvertViaLibreOffice(byte[] inputBytes, string extension, CancellationToken cancellationToken)
    {
        var soffice = FindLibreOffice();
        var tempDirectory = Directory.CreateTempSubdirectory("pdfforge-libre-");
        var inputPath = Path.Combine(tempDirectory.FullName, $"input{extension}");
        // Isolated HOME per request so LibreOffice creates a fresh profile and cannot
        // conflict with concurrent or previously crashed conversions.
        var homeDir = Path.Combine(tempDirectory.FullName, "home");
        Directory.CreateDirectory(homeDir);

        try
        {
            File.WriteAllBytes(inputPath, inputBytes);

            // Use ArgumentList (not Arguments string) so paths are passed verbatim to execv()
            // without shell-quote stripping ambiguity on Linux.
            // xvfb-run provides a minimal virtual X11 display so LibreOffice's X11 init
            // succeeds even in headless containers (LibreOffice connects to X11 at startup
            // before the --headless flag suppresses the UI).
            var xvfbRun = "/usr/bin/xvfb-run";
            var startInfo = new ProcessStartInfo
            {
                FileName = File.Exists(xvfbRun) ? xvfbRun : soffice,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (File.Exists(xvfbRun))
            {
                startInfo.ArgumentList.Add("--auto-servernum");
                startInfo.ArgumentList.Add("--server-args=-screen 0 1x1x8");
                startInfo.ArgumentList.Add(soffice);
            }
            startInfo.ArgumentList.Add("--headless");
            startInfo.ArgumentList.Add("--norestore");
            startInfo.ArgumentList.Add("--nolockcheck");
            startInfo.ArgumentList.Add("--convert-to");
            startInfo.ArgumentList.Add("pdf");
            startInfo.ArgumentList.Add("--outdir");
            startInfo.ArgumentList.Add(tempDirectory.FullName);
            startInfo.ArgumentList.Add(inputPath);
            startInfo.Environment["HOME"] = homeDir;
            startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = "/dev/null";

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start LibreOffice.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            _logger.LogInformation("LibreOffice exit={Code} stdout={Out} stderr={Err}",
                process.ExitCode, stdout.Trim(), stderr.Trim());

            // LibreOffice names output after the input stem — look for "input.pdf"
            var outputPath = Path.Combine(tempDirectory.FullName, "input.pdf");
            if (!File.Exists(outputPath))
            {
                // Fall back: any PDF in the temp dir
                var anyPdf = Directory.GetFiles(tempDirectory.FullName, "*.pdf").FirstOrDefault();
                if (anyPdf != null) return File.ReadAllBytes(anyPdf);

                throw new InvalidOperationException(
                    $"LibreOffice (exit {process.ExitCode}) produced no output. " +
                    $"{(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
            }

            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            try { tempDirectory.Delete(true); } catch { }
        }
    }

    private string FindLibreOffice()
    {
        var configured = _configuration["Tools:LibreOffice:SofficePath"] ?? _configuration["Tools:LibreOffice:Path"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var candidates = OperatingSystem.IsWindows()
            ? new[]
            {
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe"
            }
            : new[]
            {
                "/usr/bin/soffice",
                "/usr/local/bin/soffice",
                "/opt/libreoffice/program/soffice"
            };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException(
                "Neither Microsoft Office nor LibreOffice is available. " +
                "Install LibreOffice or set Tools:LibreOffice:SofficePath in appsettings.");
    }

    private static string NormalizeOfficeExtension(string extension, params string[] allowedExtensions)
    {
        var normalized = Path.GetExtension(extension);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = extension;
        }

        normalized = normalized.StartsWith('.') ? normalized.ToLowerInvariant() : $".{normalized.ToLowerInvariant()}";
        return allowedExtensions.Contains(normalized) ? normalized : allowedExtensions[0];
    }

#pragma warning disable CA1416
    private static void ExportWordToPdf(string inputPath, string outputPath)
    {
        dynamic? app = null;
        dynamic? document = null;
        try
        {
            var wordType = Type.GetTypeFromProgID("Word.Application") ?? throw new InvalidOperationException("Microsoft Word is not installed.");
            app = Activator.CreateInstance(wordType) ?? throw new InvalidOperationException("Could not start Microsoft Word.");
            app.Visible = false;
            app.DisplayAlerts = 0;
            document = app.Documents.Open(inputPath, ReadOnly: true, Visible: false);
            document.ExportAsFixedFormat(outputPath, 17);
        }
        finally
        {
            if (document is not null)
            {
                try { document.Close(false); } catch { }
                Marshal.FinalReleaseComObject(document);
            }
            if (app is not null)
            {
                try { app.Quit(false); } catch { }
                Marshal.FinalReleaseComObject(app);
            }
        }
    }

    private static void ExportExcelToPdf(string inputPath, string outputPath)
    {
        dynamic? app = null;
        dynamic? workbook = null;
        try
        {
            var excelType = Type.GetTypeFromProgID("Excel.Application") ?? throw new InvalidOperationException("Microsoft Excel is not installed.");
            app = Activator.CreateInstance(excelType) ?? throw new InvalidOperationException("Could not start Microsoft Excel.");
            app.Visible = false;
            app.DisplayAlerts = false;
            workbook = app.Workbooks.Open(inputPath, ReadOnly: true);
            workbook.ExportAsFixedFormat(0, outputPath);
        }
        finally
        {
            if (workbook is not null)
            {
                try { workbook.Close(false); } catch { }
                Marshal.FinalReleaseComObject(workbook);
            }
            if (app is not null)
            {
                try { app.Quit(); } catch { }
                Marshal.FinalReleaseComObject(app);
            }
        }
    }

    private static void ExportPowerPointToPdf(string inputPath, string outputPath)
    {
        dynamic? app = null;
        dynamic? presentation = null;
        try
        {
            var powerPointType = Type.GetTypeFromProgID("PowerPoint.Application") ?? throw new InvalidOperationException("Microsoft PowerPoint is not installed.");
            app = Activator.CreateInstance(powerPointType) ?? throw new InvalidOperationException("Could not start Microsoft PowerPoint.");
            app.Visible = -1;

            // PowerPoint COM is sensitive to optional/named arguments. Use the native
            // positional order: FileName, ReadOnly, Untitled, WithWindow.
            presentation = app.Presentations.Open(inputPath, -1, 0, -1);
            presentation.SaveAs(outputPath, 32);
        }
        catch (COMException ex)
        {
            throw new InvalidOperationException("PowerPoint could not open this deck. Close any PowerPoint dialogs, make sure the file is a valid .pptx/.ppt file, and try again.", ex);
        }
        finally
        {
            if (presentation is not null)
            {
                try { presentation.Close(); } catch { }
                Marshal.FinalReleaseComObject(presentation);
            }
            if (app is not null)
            {
                try { app.Quit(); } catch { }
                Marshal.FinalReleaseComObject(app);
            }
        }
    }
#pragma warning restore CA1416

    private async Task<List<byte[]>> RenderPdfPages(byte[] pdfBytes, string format, int dpi)
    {
        var executable = _configuration["Tools:Poppler:PdfToPpmPath"] ?? "pdftoppm";
        var normalizedFormat = format.Equals("jpg", StringComparison.OrdinalIgnoreCase) ? "jpeg" : "png";
        var extension = normalizedFormat == "jpeg" ? "jpg" : "png";
        var tempDirectory = Directory.CreateTempSubdirectory("pdfforge-render-");
        var inputPath = Path.Combine(tempDirectory.FullName, "input.pdf");
        var outputPrefix = Path.Combine(tempDirectory.FullName, "page");

        try
        {
            await File.WriteAllBytesAsync(inputPath, pdfBytes);
            await RunTool(executable, $"-{normalizedFormat} -r {dpi} \"{inputPath}\" \"{outputPrefix}\"");

            return Directory.GetFiles(tempDirectory.FullName, $"page-*.{extension}")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(File.ReadAllBytes)
                .ToList();
        }
        finally
        {
            tempDirectory.Delete(true);
        }
    }

    private string GetBrowserPath()
    {
        var configured = _configuration["Tools:Browser:Path"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var candidates = new[]
        {
            "/usr/bin/chromium",
            "/usr/bin/chromium-browser",
            "/usr/bin/google-chrome",
            "/usr/bin/microsoft-edge",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
            @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
        };

        return candidates.FirstOrDefault(File.Exists) ?? (OperatingSystem.IsWindows() ? "msedge" : "chromium");
    }

    private string GetSiblingToolPath(string toolName)
    {
        var pdfToPpm = _configuration["Tools:Poppler:PdfToPpmPath"];
        if (!string.IsNullOrWhiteSpace(pdfToPpm))
        {
            var directory = Path.GetDirectoryName(pdfToPpm);
            var siblingPath = directory is null ? null : Path.Combine(directory, $"{toolName}.exe");
            if (!string.IsNullOrWhiteSpace(siblingPath) && File.Exists(siblingPath))
            {
                return siblingPath;
            }
        }

        return toolName;
    }

    private static (int Width, int Height) GetImageSize(byte[] imageBytes)
    {
        using var bitmap = SKBitmap.Decode(imageBytes);
        return bitmap is null ? (1200, 1600) : (bitmap.Width, bitmap.Height);
    }

    private static (uint PageWidthTwips, uint PageHeightTwips, long ContentWidthEmu, long ContentHeightEmu, bool IsLandscape) GetWordPageLayout(int pixelWidth, int pixelHeight)
    {
        var isLandscape = pixelWidth > pixelHeight;
        const uint a4ShortTwips = 11906U;
        const uint a4LongTwips = 16838U;
        var pageWidthTwips = isLandscape ? a4LongTwips : a4ShortTwips;
        var pageHeightTwips = isLandscape ? a4ShortTwips : a4LongTwips;
        var pageWidthEmu = TwipsToEmu(pageWidthTwips);
        var pageHeightEmu = TwipsToEmu(pageHeightTwips);
        var fitted = FitInside(pixelWidth, pixelHeight, pageWidthEmu, pageHeightEmu);

        return (pageWidthTwips, pageHeightTwips, fitted.WidthEmu, fitted.HeightEmu, isLandscape);
    }

    private static (long WidthEmu, long HeightEmu) GetPresentationSlideSize(int pixelWidth, int pixelHeight)
    {
        const long baseLongEdgeEmu = 10_058_400L;
        var ratio = pixelHeight / (double)Math.Max(pixelWidth, 1);
        if (pixelWidth >= pixelHeight)
        {
            return (baseLongEdgeEmu, (long)(baseLongEdgeEmu * ratio));
        }

        return ((long)(baseLongEdgeEmu / ratio), baseLongEdgeEmu);
    }

    private static (long WidthEmu, long HeightEmu, long OffsetXEmu, long OffsetYEmu) FitInside(int pixelWidth, int pixelHeight, long maxWidthEmu, long maxHeightEmu)
    {
        var widthRatio = maxWidthEmu / (double)Math.Max(pixelWidth, 1);
        var heightRatio = maxHeightEmu / (double)Math.Max(pixelHeight, 1);
        var scale = Math.Min(widthRatio, heightRatio);
        var widthEmu = (long)(pixelWidth * scale);
        var heightEmu = (long)(pixelHeight * scale);

        return (widthEmu, heightEmu, (maxWidthEmu - widthEmu) / 2, (maxHeightEmu - heightEmu) / 2);
    }

    private static long TwipsToEmu(uint twips) => twips * 635L;

    private static DocumentFormat.OpenXml.Wordprocessing.Drawing CreateWordImageDrawing(string relationshipId, int pixelWidth, int pixelHeight, long maxWidthEmu, long maxHeightEmu, uint imageNumber)
    {
        var fitted = FitInside(pixelWidth, pixelHeight, maxWidthEmu, maxHeightEmu);

        return new DocumentFormat.OpenXml.Wordprocessing.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = fitted.WidthEmu, Cy = fitted.HeightEmu },
                new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
                new DW.DocProperties { Id = imageNumber, Name = $"Page {imageNumber}" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(
                    new A.GraphicData(
                        new PIC.Picture(
                            new PIC.NonVisualPictureProperties(
                                new PIC.NonVisualDrawingProperties { Id = imageNumber, Name = $"Page {imageNumber}.png" },
                                new PIC.NonVisualPictureDrawingProperties()),
                            new PIC.BlipFill(
                                new A.Blip { Embed = relationshipId },
                                new A.Stretch(new A.FillRectangle())),
                            new PIC.ShapeProperties(
                                new A.Transform2D(
                                    new A.Offset { X = 0L, Y = 0L },
                                    new A.Extents { Cx = fitted.WidthEmu, Cy = fitted.HeightEmu }),
                                new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                    { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" })));
    }

    private static P.Slide CreateImageSlide(string relationshipId, long slideWidthEmu, long slideHeightEmu, int pixelWidth, int pixelHeight, uint slideNumber)
    {
        var fitted = FitInside(pixelWidth, pixelHeight, slideWidthEmu, slideHeightEmu);

        return new P.Slide(
            new P.CommonSlideData(
                new P.ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                        new P.NonVisualGroupShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.GroupShapeProperties(new A.TransformGroup()),
                    new P.Picture(
                        new P.NonVisualPictureProperties(
                            new P.NonVisualDrawingProperties { Id = slideNumber + 1, Name = $"Page {slideNumber}" },
                            new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                            new P.ApplicationNonVisualDrawingProperties()),
                        new P.BlipFill(
                            new A.Blip { Embed = relationshipId },
                            new A.Stretch(new A.FillRectangle())),
                        new P.ShapeProperties(
                            new A.Transform2D(
                                new A.Offset { X = fitted.OffsetXEmu, Y = fitted.OffsetYEmu },
                                new A.Extents { Cx = fitted.WidthEmu, Cy = fitted.HeightEmu }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))));
    }

    private static void AddImageToWorksheet(WorksheetPart worksheetPart, byte[] imageBytes, uint imageNumber)
    {
        var drawingsPart = worksheetPart.AddNewPart<DrawingsPart>();
        drawingsPart.WorksheetDrawing = new XDR.WorksheetDrawing();

        var imagePart = drawingsPart.AddImagePart(ImagePartType.Png);
        using (var imageStream = new MemoryStream(imageBytes))
        {
            imagePart.FeedData(imageStream);
        }

        var size = GetImageSize(imageBytes);
        var relationshipId = drawingsPart.GetIdOfPart(imagePart);
        var widthEmu = size.Width * 9525L;
        var heightEmu = size.Height * 9525L;

        drawingsPart.WorksheetDrawing.Append(
            new XDR.AbsoluteAnchor(
                new XDR.Position { X = 0L, Y = 0L },
                new XDR.Extent { Cx = widthEmu, Cy = heightEmu },
                new XDR.Picture(
                    new XDR.NonVisualPictureProperties(
                        new XDR.NonVisualDrawingProperties { Id = imageNumber, Name = $"Page {imageNumber}.png" },
                        new XDR.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true })),
                    new XDR.BlipFill(
                        new A.Blip { Embed = relationshipId },
                        new A.Stretch(new A.FillRectangle())),
                    new XDR.ShapeProperties(
                        new A.Transform2D(
                            new A.Offset { X = 0L, Y = 0L },
                            new A.Extents { Cx = widthEmu, Cy = heightEmu }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })),
                new XDR.ClientData()));

        worksheetPart.Worksheet ??= new Worksheet(new SheetData());
        worksheetPart.Worksheet.Append(new DocumentFormat.OpenXml.Spreadsheet.Drawing { Id = worksheetPart.GetIdOfPart(drawingsPart) });
        drawingsPart.WorksheetDrawing.Save();
    }

    private static byte[] CreateTextPdf(string text)
    {
        using var document = new PdfDocument();
        var font = new XFont("Arial", 11);
        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        var graphics = XGraphics.FromPdfPage(page);
        var margin = 42d;
        var y = margin;
        var lineHeight = 16d;
        var maxChars = 96;

        foreach (var rawLine in SplitLines(text))
        {
            foreach (var line in WrapLine(rawLine, maxChars))
            {
                if (y > page.Height.Point - margin)
                {
                    graphics.Dispose();
                    page = document.AddPage();
                    page.Size = PdfSharp.PageSize.A4;
                    graphics = XGraphics.FromPdfPage(page);
                    y = margin;
                }

                graphics.DrawString(line, font, XBrushes.Black, new XRect(margin, y, page.Width.Point - margin * 2, lineHeight), XStringFormats.TopLeft);
                y += lineHeight;
            }
        }

        graphics.Dispose();
        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static IEnumerable<string> WrapLine(string line, int maxChars)
    {
        if (string.IsNullOrEmpty(line))
        {
            yield return string.Empty;
            yield break;
        }

        for (var i = 0; i < line.Length; i += maxChars)
        {
            yield return line.Substring(i, Math.Min(maxChars, line.Length - i));
        }
    }

    private static byte[] RenderMarkdownToPdf(string markdown)
    {
        using var document = new PdfDocument();
        const double margin = 50;

        var fontBody = new XFont("Arial", 11);
        var fontH1 = new XFont("Arial", 22, XFontStyleEx.Bold);
        var fontH2 = new XFont("Arial", 17, XFontStyleEx.Bold);
        var fontH3 = new XFont("Arial", 13, XFontStyleEx.Bold);
        var fontMono = new XFont("Courier New", 10);

        PdfPage? page = null;
        XGraphics? gfx = null;
        double y = margin;
        double pageHeight = 0;
        double contentWidth = 0;

        void NewPage()
        {
            gfx?.Dispose();
            page = document.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            gfx = XGraphics.FromPdfPage(page);
            pageHeight = page.Height.Point;
            contentWidth = page.Width.Point - margin * 2;
            y = margin;
        }

        void EnsureSpace(double needed)
        {
            if (page is null || y + needed > pageHeight - margin)
                NewPage();
        }

        void DrawWrapped(string text, XFont font, double indent = 0, double spaceBefore = 0, double spaceAfter = 6, XBrush? brush = null)
        {
            brush ??= XBrushes.Black;
            y += spaceBefore;
            var effectiveWidth = contentWidth - indent;
            var lineH = font.GetHeight() + 3;

            foreach (var word in WrapWords(text, font, gfx!, effectiveWidth))
            {
                EnsureSpace(lineH);
                gfx!.DrawString(word, font, brush, new XRect(margin + indent, y, effectiveWidth, lineH), XStringFormats.TopLeft);
                y += lineH;
            }

            y += spaceAfter;
        }

        NewPage();

        var inCodeBlock = false;
        var codeLines = new List<string>();
        var tableRows = new List<string>();
        var fontBoldBody = new XFont("Arial", 11, XFontStyleEx.Bold);

        void FlushCode()
        {
            if (codeLines.Count == 0) return;
            var lineH = fontMono.GetHeight() + 2;
            var blockH = codeLines.Count * lineH + 12;
            EnsureSpace(blockH);
            gfx!.DrawRectangle(new XSolidBrush(XColor.FromArgb(241, 245, 249)), margin, y, contentWidth, blockH);
            y += 6;
            foreach (var cl in codeLines)
            {
                EnsureSpace(lineH);
                gfx!.DrawString(cl.Length > 90 ? cl[..90] : cl, fontMono, XBrushes.Black,
                    new XRect(margin + 8, y, contentWidth - 16, lineH), XStringFormats.TopLeft);
                y += lineH;
            }
            y += 6;
            codeLines.Clear();
        }

        void FlushTable()
        {
            if (tableRows.Count == 0) return;

            var parsedRows = tableRows
                .Select(r => r.Trim().Trim('|').Split('|')
                    .Select(c => StripInlineMarkdown(c.Trim())).ToArray())
                .ToList();
            tableRows.Clear();

            // Find separator row (cells like ---, :---:, :--)
            var sepIdx = parsedRows.FindIndex(r => r.All(c => Regex.IsMatch(c, @"^:?-+:?$")));
            var colCount = parsedRows.Max(r => r.Length);
            if (colCount == 0) return;

            var colW = contentWidth / colCount;
            const double pad = 5;
            var lineH = fontBody.GetHeight() + 2;
            var headerBg = new XSolidBrush(XColor.FromArgb(235, 240, 248));
            var borderPen = new XPen(XColor.FromArgb(203, 213, 225));

            for (var ri = 0; ri < parsedRows.Count; ri++)
            {
                if (ri == sepIdx) continue;
                var isHeader = sepIdx > 0 && ri < sepIdx;
                var row = parsedRows[ri];
                var font = isHeader ? fontBoldBody : fontBody;

                // Pre-wrap each cell to compute the row height needed
                var cellLines = Enumerable.Range(0, colCount)
                    .Select(ci => WrapWords(ci < row.Length ? row[ci] : string.Empty, font, gfx!, colW - pad * 2).ToList())
                    .ToList();
                var rowH = cellLines.Max(l => Math.Max(1, l.Count)) * lineH + pad * 2;

                EnsureSpace(rowH);

                for (var ci = 0; ci < colCount; ci++)
                {
                    var x = margin + ci * colW;
                    if (isHeader)
                        gfx!.DrawRectangle(headerBg, x, y, colW, rowH);
                    gfx!.DrawRectangle(borderPen, x, y, colW, rowH);

                    var ty = y + pad;
                    foreach (var l in cellLines[ci])
                    {
                        gfx!.DrawString(l, font, XBrushes.Black,
                            new XRect(x + pad, ty, colW - pad * 2, lineH), XStringFormats.TopLeft);
                        ty += lineH;
                    }
                }

                y += rowH;
            }

            y += 8;
        }

        foreach (var rawLine in SplitLines(markdown))
        {
            var line = rawLine.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                FlushTable();
                if (inCodeBlock) { FlushCode(); inCodeBlock = false; }
                else inCodeBlock = true;
                continue;
            }

            if (inCodeBlock) { codeLines.Add(line); continue; }

            var t = line.Trim();

            // Accumulate table rows
            if (t.StartsWith("|"))
            {
                tableRows.Add(t);
                continue;
            }

            // Non-table line — flush any pending table first
            FlushTable();

            if (string.IsNullOrWhiteSpace(t)) { y += 5; continue; }

            if (t.StartsWith("# "))
                DrawWrapped(StripInlineMarkdown(t[2..]), fontH1, spaceBefore: 14, spaceAfter: 6);
            else if (t.StartsWith("## "))
                DrawWrapped(StripInlineMarkdown(t[3..]), fontH2, spaceBefore: 10, spaceAfter: 4);
            else if (t.StartsWith("### "))
                DrawWrapped(StripInlineMarkdown(t[4..]), fontH3, spaceBefore: 8, spaceAfter: 3);
            else if (t is "---" or "***" or "___")
            {
                EnsureSpace(12);
                gfx!.DrawLine(new XPen(XColor.FromArgb(203, 213, 225)), margin, y + 5, margin + contentWidth, y + 5);
                y += 12;
            }
            else if (t.StartsWith("- ") || t.StartsWith("* "))
                DrawWrapped("•  " + StripInlineMarkdown(t[2..]), fontBody, indent: 10, spaceAfter: 2);
            else if (Regex.IsMatch(t, @"^\d+\. "))
            {
                var dot = t.IndexOf(". ", StringComparison.Ordinal);
                DrawWrapped(t[..(dot + 2)] + StripInlineMarkdown(t[(dot + 2)..]), fontBody, indent: 10, spaceAfter: 2);
            }
            else if (t.StartsWith("> "))
                DrawWrapped(StripInlineMarkdown(t[2..]), fontBody, indent: 16, spaceAfter: 4,
                    brush: new XSolidBrush(XColor.FromArgb(71, 85, 105)));
            else
                DrawWrapped(StripInlineMarkdown(t), fontBody, spaceAfter: 4);
        }

        FlushTable();
        if (inCodeBlock) FlushCode();
        gfx?.Dispose();

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private static string StripInlineMarkdown(string text)
    {
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.+?)__", "$1");
        text = Regex.Replace(text, @"\*(.+?)\*", "$1");
        text = Regex.Replace(text, @"_(.+?)_", "$1");
        text = Regex.Replace(text, @"`(.+?)`", "$1");
        text = Regex.Replace(text, @"\[(.+?)\]\(.+?\)", "$1");
        return text;
    }

    private static IEnumerable<string> WrapWords(string text, XFont font, XGraphics gfx, double maxWidth)
    {
        if (string.IsNullOrEmpty(text)) { yield return string.Empty; yield break; }

        var words = text.Split(' ');
        var line = new StringBuilder();

        foreach (var word in words)
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (gfx.MeasureString(candidate, font).Width > maxWidth && line.Length > 0)
            {
                yield return line.ToString();
                line.Clear();
                line.Append(word);
            }
            else
            {
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
        }

        if (line.Length > 0) yield return line.ToString();
    }

    private static async Task RunTool(string executable, string arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} failed with exit code {process.ExitCode}. {error}");
        }
    }
}
