using Azure;
using Azure.AI.OpenAI;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenAI.Chat;
using MoneyManager.Api.Data;
using MoneyManager.Api.Services;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace MoneyManager.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/docagent")]
public class DocAgentController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocAgentController> _logger;
    private readonly IHttpClientFactory _http;
    private readonly PromptDbContext _db;

    private static readonly HashSet<string> AllowedMime = new(StringComparer.OrdinalIgnoreCase)
    {
        // Images → Azure Vision
        "image/jpeg", "image/png", "image/webp", "image/gif",
        // PDF → PdfPig (text) + Azure Vision (image pages)
        "application/pdf",
        // Plain text
        "text/plain",
        // Word documents
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/msword"
    };

    private static readonly Dictionary<string, string> AnalysisSystemPrompts = new()
    {
        ["receipts"] = """
            You are an expert financial analyst specialising in receipt and expense analysis.
            The user will provide one or more receipts or invoices (as images, PDFs, or text).
            For each document extract: merchant name, date, total amount, currency, item breakdown if visible, payment method if shown, and category (meals, transport, software, office supplies, etc.).
            Then produce:
            1. A structured per-receipt summary table (merchant | date | amount | category)
            2. Totals grouped by category as a markdown table with columns: Category | Total | Notes
            3. Any anomalies, duplicates, or items that look unusual
            4. Flag any anomalies or duplicates
            Formatting rules:
            - Use markdown tables for all repeated financial rows and grouped totals.
            - Do not present category totals, merchant totals, invoice totals, or transaction summaries as bullet lists.
            - Use bullet points only for narrative observations and flags.
            Optional non-advisory calculations when the provided data supports them or the user asks:
            - Analyse spending patterns from the provided data.
            - Show budgeting framework breakdowns such as 50/30/20 as informational comparisons, not prescriptions.
            - If debt balances, interest rates, minimum payments, or payment amounts are provided, calculate payoff timelines, snowball vs avalanche comparisons, and extra-payment what-if scenarios.
            - Summarise general strategies neutrally without recommending which one the user should choose.
            Be precise with amounts. Use clear headings. Do not repeat any personal card numbers or account details.
            IMPORTANT: Do NOT provide financial advice, tax advice, investment recommendations, or personalised budgeting prescriptions. Only extract, calculate, compare, and explain using user-provided data.
            If asked for financial advice, respond: "I only extract and display document data. For financial advice, please consult a qualified professional."
            """,

        ["bank"] = """
            You are an expert financial analyst specialising in bank statement analysis.
            The user will provide bank statements as images, PDFs, or text.
            Extract: account period, opening/closing balance, all transactions (date, description, debit, credit).
            Then produce:
            1. Period overview as a markdown table with columns: Metric | Amount | Notes
            2. Spending breakdown by category as a markdown table with columns: Category | Total | Transaction count | Notes
            3. Largest transactions flagged as a markdown table with columns: Date | Description | Amount | Category
            4. Recurring charges identified as a markdown table with columns: Merchant | Amount | Frequency | Notes
            Formatting rules:
            - Use markdown tables for all repeated financial rows and grouped totals.
            - Do not present category totals, merchant totals, or transaction summaries as bullet lists.
            - Use bullet points only for narrative observations and flags.
            Optional non-advisory calculations when the provided data supports them or the user asks:
            - Analyse spending patterns from the provided data.
            - Show budgeting framework breakdowns such as 50/30/20 as informational comparisons, not prescriptions.
            - If debt balances, interest rates, minimum payments, or payment amounts are provided, calculate payoff timelines, snowball vs avalanche comparisons, and extra-payment what-if scenarios.
            - Show how extra payments change estimated total interest paid when the required rate and balance data are available.
            - Summarise general strategies neutrally without recommending which one the user should choose.
            Redact or omit full account numbers — mention only the last 4 digits if needed.
            IMPORTANT: Do NOT provide financial advice, investment recommendations, personalised budgeting prescriptions, or tax guidance. Only extract, calculate, compare, and explain using user-provided data.
            If asked for financial advice, respond: "I only extract and display document data. For financial advice, please consult a qualified professional."
            """,

        ["invoices"] = """
            You are an expert financial analyst specialising in invoice processing.
            The user will provide invoices as images, PDFs, or text.
            For each invoice extract: vendor, invoice number, invoice date, due date, line items, subtotal, tax, total, payment terms, and currency.
            Then produce:
            1. Invoice summary per document as a markdown table with columns: Document | Vendor | Invoice number | Due date | Total | Payment terms
            2. Combined totals as a markdown table with columns: Metric | Amount | Notes
            3. Flag any invoices with missing information or unusual terms
            4. Flag any invoices with missing information or unusual terms
            Formatting rules:
            - Use markdown tables for all repeated financial rows and totals.
            - Do not present invoice totals or grouped totals as bullet lists.
            - Use bullet points only for narrative observations and flags.
            Optional non-advisory calculations when the provided data supports them or the user asks:
            - Analyse payment timing, totals, outstanding balances, and scenario calculations from the provided invoices.
            - If debt balances, interest rates, minimum payments, or payment amounts are provided, calculate payoff timelines, snowball vs avalanche comparisons, and extra-payment what-if scenarios.
            - Summarise general strategies neutrally without recommending which one the user should choose.
            IMPORTANT: Do NOT provide financial advice or payment recommendations beyond what is explicitly stated in the document. Only extract, calculate, compare, and explain using user-provided data.
            If asked for financial advice, respond: "I only extract and display document data. For financial advice, please consult a qualified professional."
            """,

        ["general"] = """
            You are an expert document analyst. The user will provide one or more documents (images, PDFs, Word files, or text).
            Analyse each document and produce:
            1. Document type and purpose
            2. Key facts, figures, and dates extracted
            3. Important obligations, deadlines, or action items
            4. A plain-English summary of what the document means and what the user should do next
            Be clear, concise, and structured. Use headings and bullet points.
            """
    };

    public DocAgentController(IConfiguration configuration, ILogger<DocAgentController> logger, IHttpClientFactory http, PromptDbContext db)
    {
        _configuration = configuration;
        _logger = logger;
        _http = http;
        _db = db;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    [HttpPost("analyse")]
    [RequestSizeLimit(104_857_600)] // 100 MB total
    public async Task Analyse([FromForm] string analysisType, [FromForm] string? cloudUrl, IFormFileCollection files)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        await using var writer = new StreamWriter(Response.Body, Encoding.UTF8, leaveOpen: true);

        async Task Send(string eventName, object data)
        {
            await writer.WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(data)}\n\n");
            await writer.FlushAsync();
        }

        try
        {
            var hasFiles = files != null && files.Count > 0;
            var hasCloud = !string.IsNullOrWhiteSpace(cloudUrl);

            if (!hasFiles && !hasCloud)
            {
                await Send("error", new { message = "Please upload a file or provide a cloud document link." });
                return;
            }

            if (hasFiles && files!.Count > 10)
            {
                await Send("error", new { message = "Maximum 10 files per request." });
                return;
            }

            var type = AnalysisSystemPrompts.ContainsKey(analysisType) ? analysisType : "general";
            var systemPrompt = AnalysisSystemPrompts[type];

            var contentParts = new List<ChatMessageContentPart>();
            contentParts.Add(ChatMessageContentPart.CreateTextPart(
                "Please analyse the document(s) I am providing and give me a detailed, structured report."));

            int loaded = 0;

            // ── Cloud URL import (Google Drive / OneDrive) ────────────────────
            if (hasCloud)
            {
                await Send("progress", new { message = "Fetching cloud document…" });
                var (cloudText, cloudLabel) = await FetchCloudUrlAsync(cloudUrl!);
                if (!string.IsNullOrWhiteSpace(cloudText))
                {
                    contentParts.Add(ChatMessageContentPart.CreateTextPart(
                        $"[Cloud document: {cloudLabel}]\n{cloudText[..Math.Min(cloudText.Length, 24_000)]}"));
                    loaded++;
                    await Send("progress", new { message = $"Loaded {cloudLabel}" });
                }
                else
                {
                    await Send("warning", new { message = "Could not fetch the cloud document. Check the link is publicly shared." });
                }
            }

            foreach (var file in files ?? new FormFileCollection())
            {
                var mime = file.ContentType?.ToLowerInvariant() ?? string.Empty;

                if (!AllowedMime.Contains(mime))
                {
                    await Send("warning", new { message = $"Skipped '{file.FileName}' — unsupported type ({mime})." });
                    continue;
                }

                if (file.Length > 104_857_600)
                {
                    await Send("warning", new { message = $"Skipped '{file.FileName}' — exceeds 100 MB limit." });
                    continue;
                }

                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var bytes = ms.ToArray();
                // bytes live in RAM only — never written to disk or DB

                int partsBefore = contentParts.Count;

                if (mime.StartsWith("image/"))
                {
                    // Image → send straight to Azure Vision
                    contentParts.Add(ChatMessageContentPart.CreateImagePart(
                        BinaryData.FromBytes(bytes), mime));
                }
                else if (mime == "application/pdf")
                {
                    // PDF → PdfPig per page
                    // Text pages → text part
                    // Image-only pages → extract embedded images → Azure Vision
                    ExtractPdf(bytes, file.FileName, contentParts);
                }
                else if (mime == "text/plain")
                {
                    // TXT → read as UTF-8, send as text
                    var text = Encoding.UTF8.GetString(bytes);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        await Send("warning", new { message = $"'{file.FileName}' is empty." });
                        continue;
                    }
                    contentParts.Add(ChatMessageContentPart.CreateTextPart(
                        $"[Text file: {file.FileName}]\n{text}"));
                }
                else if (mime == "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                      || mime == "application/msword")
                {
                    // DOCX → OpenXml: extract text + embedded images
                    ExtractDocx(bytes, file.FileName, contentParts);
                }

                if (contentParts.Count > partsBefore)
                {
                    loaded++;
                    await Send("progress", new { message = $"Loaded {file.FileName} ({file.Length / 1024} KB)" });
                }
                else
                {
                    await Send("warning", new { message = $"'{file.FileName}' — nothing could be extracted." });
                }
            }

            if (loaded == 0)
            {
                await Send("error", new { message = "No valid content could be extracted from the provided files." });
                return;
            }

            await Send("progress", new { message = $"Analysing {loaded} document(s) with Azure OpenAI…" });

            var sb = new StringBuilder();
            await StreamAzureOpenAI(systemPrompt, contentParts, Send, sb);

            if (sb.Length > 0)
            {
                var firstFile = files?.FirstOrDefault()?.FileName;
                var analysisTitle = string.IsNullOrWhiteSpace(firstFile)
                    ? $"Document analysis ({type})"
                    : $"{firstFile} — {type} analysis";
            }

            await Send("done", new { totalLength = sb.Length });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DocAgent analysis failed");
            var detail = ex is Azure.RequestFailedException rfe
                ? $"Azure OpenAI error ({rfe.Status}): {rfe.Message.Split('\n')[0]}"
                : ex.Message.Length < 200 ? ex.Message : "Analysis failed. Please try again.";
            await Send("error", new { message = detail });
        }
    }

    // ── Receipt & Expense Snap (persistent tracker, unlike the zero-storage analysis) ──

    private const int MaxImageChars = 3_000_000;
    private static readonly string[] ReceiptCategories =
        { "groceries", "dining", "transport", "fuel", "utilities", "shopping", "health", "entertainment", "travel", "office", "other" };

    private static bool _receiptsTableEnsured;

    private async Task EnsureReceiptsTableAsync()
    {
        if (_receiptsTableEnsured) return;
        try
        {
            var isSqlite = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
            var pk = isSqlite ? "INTEGER PRIMARY KEY AUTOINCREMENT" : "SERIAL PRIMARY KEY";
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""Receipts"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Merchant"" TEXT, ""PurchaseDate"" TEXT, ""Currency"" TEXT,
                ""Total"" REAL, ""Tax"" REAL, ""PaymentMethod"" TEXT, ""Category"" TEXT, ""ItemsJson"" TEXT,
                ""ImageData"" TEXT, ""CreatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_Receipts_UserId"" ON ""Receipts"" (""UserId"", ""PurchaseDate"")");
            _receiptsTableEnsured = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EnsureReceiptsTable failed"); }
    }

    [HttpGet("receipts")]
    public async Task<IActionResult> GetReceipts([FromQuery] string? month)
    {
        await EnsureReceiptsTableAsync();
        List<ReceiptRow> rows;
        if (!string.IsNullOrWhiteSpace(month) && Regex.IsMatch(month, @"^\d{4}-\d{2}$"))
        {
            var like = month + "%";
            rows = await _db.Database.SqlQuery<ReceiptRow>($@"
                SELECT ""Id"", ""Merchant"", ""PurchaseDate"", ""Currency"", ""Total"", ""Tax"", ""PaymentMethod"", ""Category"", ""ItemsJson"", ""ImageData"", ""CreatedAt""
                FROM ""Receipts"" WHERE ""UserId""={UserId} AND ""PurchaseDate"" LIKE {like}
                ORDER BY ""PurchaseDate"" DESC, ""Id"" DESC").ToListAsync();
        }
        else
        {
            rows = await _db.Database.SqlQuery<ReceiptRow>($@"
                SELECT ""Id"", ""Merchant"", ""PurchaseDate"", ""Currency"", ""Total"", ""Tax"", ""PaymentMethod"", ""Category"", ""ItemsJson"", ""ImageData"", ""CreatedAt""
                FROM ""Receipts"" WHERE ""UserId""={UserId}
                ORDER BY ""PurchaseDate"" DESC, ""Id"" DESC LIMIT 200").ToListAsync();
        }
        return Ok(rows);
    }

    [HttpPost("receipts")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> AddReceipt([FromBody] ReceiptSnapRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData))
            return BadRequest(new { message = "Add a photo of the receipt." });
        if (req.ImageData.Length > MaxImageChars)
            return BadRequest(new { message = "That photo is too large — please try again." });

        var endpoint = _configuration["AzureOpenAI:Endpoint"];
        var apiKey = _configuration["AzureOpenAI:ApiKey"];
        if (!AiChatClients.IsConfigured(_configuration))
            return StatusCode(503, new { message = "Receipt analysis is not available right now." });

        await EnsureReceiptsTableAsync();

        // Decode the data URI
        var comma = req.ImageData.IndexOf(',');
        var mime = "image/jpeg";
        if (req.ImageData.StartsWith("data:") && comma > 5)
        {
            var header = req.ImageData[5..comma];
            var semi = header.IndexOf(';');
            if (semi > 0) mime = header[..semi];
        }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(comma >= 0 ? req.ImageData[(comma + 1)..] : req.ImageData); }
        catch { return BadRequest(new { message = "That image could not be read — try again." }); }

        string raw;
        try
        {
            var deployment = _configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4o";
            var client = AiChatClients.Create(_configuration, deployment);
            var messages = new List<ChatMessage>
            {
                new SystemChatMessage($@"You extract structured data from a photo of a single receipt or simple invoice.
Return ONLY JSON (no markdown fences):
{{
  ""merchant"": ""store/vendor name"",
  ""date"": ""purchase date as yyyy-MM-dd, or """" if not visible"",
  ""currency"": ""3-letter code if determinable (NZD, USD, EUR...), else the symbol shown"",
  ""total"": 0.00,
  ""tax"": 0.00,
  ""paymentMethod"": ""card | cash | other | """",
  ""category"": ""one of: {string.Join(" | ", ReceiptCategories)}"",
  ""items"": [{{ ""name"": ""item"", ""qty"": 1, ""price"": 0.00 }}]
}}
Rules: amounts are plain numbers (no symbols). Max 30 items; collapse duplicates. Never include card numbers.
If the photo is not a receipt or invoice, return {{""error"": ""not-receipt""}}."),
                new UserChatMessage(new[]
                {
                    ChatMessageContentPart.CreateTextPart("Extract this receipt."),
                    ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(bytes), mime)
                })
            };
            var result = await client.CompleteChatAsync(messages, AzureChatOptions.For(deployment, maxOutputTokens: 1500));
            raw = result.Value.Content[0].Text ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receipt extraction failed");
            return StatusCode(500, new { message = "Could not read the receipt — try a clearer photo." });
        }

        // Parse the model's JSON
        string merchant = "Receipt", date = "", currency = "", payment = "", category = "other", itemsJson = "[]";
        double total = 0, tax = 0;
        try
        {
            var json = raw.Trim();
            if (json.StartsWith("```")) json = string.Join("\n", json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            var s = json.IndexOf('{'); var e = json.LastIndexOf('}');
            if (s >= 0 && e > s) json = json.Substring(s, e - s + 1);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var errEl) && errEl.GetString() == "not-receipt")
                return BadRequest(new { message = "That doesn't look like a receipt — try a clear photo of one receipt." });
            string Str(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            double Num(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0;
            merchant = string.IsNullOrWhiteSpace(Str("merchant")) ? "Receipt" : Str("merchant");
            date = Str("date");
            currency = Str("currency");
            payment = Str("paymentMethod");
            var cat = Str("category").ToLowerInvariant();
            category = ReceiptCategories.Contains(cat) ? cat : "other";
            total = Math.Round(Num("total"), 2);
            tax = Math.Round(Num("tax"), 2);
            if (r.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                itemsJson = itemsEl.GetRawText();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Receipt JSON parse failed — saving defaults"); }

        if (!Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}$")) date = DateTime.UtcNow.ToString("yyyy-MM-dd");

        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Receipts"" (""UserId"", ""Merchant"", ""PurchaseDate"", ""Currency"", ""Total"", ""Tax"", ""PaymentMethod"", ""Category"", ""ItemsJson"", ""ImageData"", ""CreatedAt"")
            VALUES ({UserId}, {merchant}, {date}, {currency}, {total}, {tax}, {payment}, {category}, {itemsJson}, {req.ImageData}, {now})");

        var row = (await _db.Database.SqlQuery<ReceiptRow>($@"
            SELECT ""Id"", ""Merchant"", ""PurchaseDate"", ""Currency"", ""Total"", ""Tax"", ""PaymentMethod"", ""Category"", ""ItemsJson"", ""ImageData"", ""CreatedAt""
            FROM ""Receipts"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 1").ToListAsync()).FirstOrDefault();

        var receiptContent =
            $"Merchant: {merchant}\nDate: {date}\nTotal: {currency} {total}\nTax: {tax}\nPayment: {payment}\nCategory: {category}\nItems: {itemsJson}";
        return Ok(row);
    }

    [HttpPut("receipts/{id:int}")]
    public async Task<IActionResult> UpdateReceipt(int id, [FromBody] ReceiptUpdateRequest req)
    {
        await EnsureReceiptsTableAsync();
        var category = !string.IsNullOrWhiteSpace(req.Category) && ReceiptCategories.Contains(req.Category.ToLowerInvariant())
            ? req.Category.ToLowerInvariant() : null;
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""Receipts"" SET
                ""Merchant"" = COALESCE({req.Merchant}, ""Merchant""),
                ""Category"" = COALESCE({category}, ""Category""),
                ""PurchaseDate"" = COALESCE({req.PurchaseDate}, ""PurchaseDate""),
                ""Total"" = COALESCE({req.Total}, ""Total"")
            WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Updated." });
    }

    [HttpDelete("receipts/{id:int}")]
    public async Task<IActionResult> DeleteReceipt(int id)
    {
        await EnsureReceiptsTableAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""Receipts"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Deleted." });
    }

    public sealed class ReceiptRow
    {
        public int Id { get; set; }
        public string? Merchant { get; set; }
        public string? PurchaseDate { get; set; }
        public string? Currency { get; set; }
        public double? Total { get; set; }
        public double? Tax { get; set; }
        public string? PaymentMethod { get; set; }
        public string? Category { get; set; }
        public string? ItemsJson { get; set; }
        public string? ImageData { get; set; }
        public string? CreatedAt { get; set; }
    }

    public class ReceiptSnapRequest { public string? ImageData { get; set; } }

    public class ReceiptUpdateRequest
    {
        public string? Merchant { get; set; }
        public string? Category { get; set; }
        public string? PurchaseDate { get; set; }
        public double? Total { get; set; }
    }

    // ── File handlers ─────────────────────────────────────────────────────────

    private async Task<(string text, string label)> FetchCloudUrlAsync(string url)
    {
        try
        {
            var fetchUrl = url;
            var gdMatch = Regex.Match(url, @"drive\.google\.com/file/d/([^/]+)");
            if (gdMatch.Success)
                fetchUrl = $"https://drive.google.com/uc?export=download&id={gdMatch.Groups[1].Value}";

            var client = _http.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 GenPrompt-DocAgent/1.0");
            client.Timeout = TimeSpan.FromSeconds(30);

            using var resp = await client.GetAsync(fetchUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            var mime = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "text/plain";
            var docBytes = await resp.Content.ReadAsByteArrayAsync();

            string text;
            if (mime == "application/pdf")
            {
                using var ms = new MemoryStream(docBytes);
                var parts = new List<ChatMessageContentPart>();
                ExtractPdf(docBytes, url, parts);
                text = string.Join("\n", parts.Where(p => p.Kind == ChatMessageContentPartKind.Text).Select(p => p.Text));
            }
            else if (mime.Contains("wordprocessingml"))
            {
                var parts = new List<ChatMessageContentPart>();
                ExtractDocx(docBytes, url, parts);
                text = string.Join("\n", parts.Where(p => p.Kind == ChatMessageContentPartKind.Text).Select(p => p.Text));
            }
            else
            {
                text = System.Text.Encoding.UTF8.GetString(docBytes);
            }

            var label = gdMatch.Success ? "Google Drive document"
                : url.Contains("1drv.ms") || url.Contains("onedrive") ? "OneDrive document"
                : $"Cloud document ({new Uri(url).Host})";

            return (text, label);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch cloud URL {Url}", url);
            return (string.Empty, string.Empty);
        }
    }

    private void ExtractPdf(byte[] bytes, string fileName, List<ChatMessageContentPart> parts)
    {
        try
        {
            using var doc = PdfDocument.Open(bytes);
            var textSb = new StringBuilder();

            foreach (var page in doc.GetPages())
            {
                var pageText = page.Text?.Trim() ?? string.Empty;

                if (pageText.Length >= 20)
                {
                    // Text page — extract as text
                    textSb.AppendLine(pageText);
                }
                else
                {
                    // Image-only page — extract embedded images for Azure Vision
                    foreach (var img in page.GetImages())
                    {
                        if (img.TryGetPng(out var pngBytes) && pngBytes?.Length > 0)
                        {
                            parts.Add(ChatMessageContentPart.CreateImagePart(
                                BinaryData.FromBytes(pngBytes), "image/png"));
                        }
                    }
                }
            }

            var extracted = textSb.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(extracted))
                parts.Add(ChatMessageContentPart.CreateTextPart($"[PDF: {fileName}]\n{extracted}"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PdfPig failed on {File}", fileName);
        }
    }

    private void ExtractDocx(byte[] bytes, string fileName, List<ChatMessageContentPart> parts)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var wordDoc = WordprocessingDocument.Open(stream, false);

            var mainPart = wordDoc.MainDocumentPart;
            if (mainPart == null) return;

            // Extract all text
            var text = mainPart.Document?.Body?.InnerText?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
                parts.Add(ChatMessageContentPart.CreateTextPart($"[Word document: {fileName}]\n{text}"));

            // Extract embedded images → Azure Vision
            foreach (var imgPart in mainPart.ImageParts)
            {
                try
                {
                    using var imgStream = imgPart.GetStream();
                    using var imgMs = new MemoryStream();
                    imgStream.CopyTo(imgMs);
                    var imgBytes = imgMs.ToArray();
                    var imgMime = imgPart.ContentType ?? "image/png";

                    if (imgBytes.Length > 0 && imgMime.StartsWith("image/"))
                        parts.Add(ChatMessageContentPart.CreateImagePart(
                            BinaryData.FromBytes(imgBytes), imgMime));
                }
                catch { /* skip unreadable image parts */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenXml failed on {File}", fileName);
        }
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    private async Task StreamAzureOpenAI(
        string systemPrompt,
        List<ChatMessageContentPart> contentParts,
        Func<string, object, Task> send,
        StringBuilder sb)
    {
        var endpoint = _configuration["AzureOpenAI:Endpoint"];
        var apiKey   = _configuration["AzureOpenAI:ApiKey"];

        if (!AiChatClients.IsConfigured(_configuration))
        {
            await send("error", new { message = "No AI provider is configured on this server." });
            return;
        }

        var deployment = _configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4o";
        var client = AiChatClients.Create(_configuration, deployment);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(contentParts)
        };

        await foreach (var update in client.CompleteChatStreamingAsync(messages,
            AzureChatOptions.For(deployment, maxOutputTokens: 8192)))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                {
                    sb.Append(part.Text);
                    await send("chunk", new { text = part.Text });
                }
            }
        }
    }
}
