using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenAI.Chat;
using MoneyManager.Api.Services;

namespace MoneyManager.Api.Controllers;

/// <summary>
/// The Invoice Agent as a product other businesses can buy.
///
/// This is deliberately a separate controller from DocAgentController rather
/// than an [AllowAnonymous] hole in it. The two have different jobs:
///
///   DocAgentController serves the logged-in UI. It streams Server-Sent Events
///     so the page can show progress, and it writes rows into the caller's own
///     receipt tracker. Neither behaviour belongs in a machine-to-machine API.
///
///   This returns one JSON object, synchronously, and persists nothing. A
///     customer's documents are theirs; we extract and hand back.
///
/// It sits at /api/invoices rather than /api/external/invoices on purpose.
/// ApiKeyAuthMiddleware owns /api/external and authenticates *user* keys by
/// bcrypt against ApiKey.Key -- but org keys are minted with Key = "" and
/// UserId = "", so they are rejected 401 there before any metering runs.
/// Org traffic belongs on the prefixes PdfApiKeyMiddleware meters, which is
/// where Snappdf, Signal and Prism already live.
/// </summary>
[ApiController]
[Route("api/invoices")]
[Authorize]
public class InvoiceApiController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<InvoiceApiController> _logger;

    public InvoiceApiController(IConfiguration configuration, ILogger<InvoiceApiController> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>Roughly 2 MB of image once base64 overhead is removed.</summary>
    private const int MaxImageChars = 3_000_000;

    private static readonly string[] Categories =
        { "groceries", "dining", "transport", "fuel", "utilities", "shopping",
          "health", "entertainment", "travel", "office", "other" };

    private static readonly HashSet<string> AllowedMime = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/webp", "image/gif",
    };

    public class ExtractRequest
    {
        /// <summary>A data URI ("data:image/jpeg;base64,...") or bare base64.</summary>
        public string? ImageData { get; set; }
    }

    /// <summary>
    /// Extract structured data from one invoice or receipt.
    ///
    /// One document per call, which is also one billing unit — a caller can see
    /// exactly what they are charged for, and a partial batch never leaves an
    /// ambiguous bill.
    /// </summary>
    [HttpPost("extract")]
    [RequestSizeLimit(8_000_000)]
    public async Task<IActionResult> Extract([FromBody] ExtractRequest request)
    {
        var imageData = request?.ImageData;
        if (string.IsNullOrWhiteSpace(imageData))
            return BadRequest(new { error = "imageData is required — a data URI or base64 image of the document." });

        if (imageData.Length > MaxImageChars)
            return BadRequest(new { error = "That image is too large. Send at most ~2 MB of image data." });

        var endpoint = _configuration["AzureOpenAI:Endpoint"];
        var apiKey = _configuration["AzureOpenAI:ApiKey"];
        if (!AiChatClients.IsConfigured(_configuration))
        {
            // 503 rather than 500: the metering middleware refunds the credit on
            // any 4xx/5xx, so a customer is not charged for our outage.
            return StatusCode(503, new { error = "Extraction is unavailable right now. Please retry." });
        }

        // ── Decode ───────────────────────────────────────────────────────────
        var comma = imageData.IndexOf(',');
        var mime = "image/jpeg";
        if (imageData.StartsWith("data:") && comma > 5)
        {
            var header = imageData[5..comma];
            var semi = header.IndexOf(';');
            if (semi > 0) mime = header[..semi];
        }

        if (!AllowedMime.Contains(mime))
            return BadRequest(new { error = $"Unsupported image type '{mime}'. Send JPEG, PNG, WebP or GIF." });

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(comma >= 0 ? imageData[(comma + 1)..] : imageData);
        }
        catch
        {
            return BadRequest(new { error = "imageData is not valid base64." });
        }

        if (bytes.Length == 0)
            return BadRequest(new { error = "imageData decoded to nothing." });

        // ── Extract ──────────────────────────────────────────────────────────
        string raw;
        try
        {
            var deployment = _configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4o";
            var client = AiChatClients.Create(_configuration, deployment);

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage($@"You extract structured data from a photograph or scan of a single
invoice or receipt. Return ONLY JSON, no markdown fences:
{{
  ""merchant"": ""vendor or supplier name"",
  ""invoiceNumber"": ""invoice/receipt number, or """" if none"",
  ""date"": ""issue date as yyyy-MM-dd, or """" if not visible"",
  ""dueDate"": ""due date as yyyy-MM-dd, or """" if not shown"",
  ""currency"": ""3-letter code if determinable (NZD, USD, EUR...), else the symbol shown"",
  ""subtotal"": 0.00,
  ""tax"": 0.00,
  ""total"": 0.00,
  ""paymentMethod"": ""card | cash | bank-transfer | other | """",
  ""category"": ""one of: {string.Join(" | ", Categories)}"",
  ""lineItems"": [{{ ""description"": ""item"", ""qty"": 1, ""unitPrice"": 0.00, ""amount"": 0.00 }}]
}}
Rules: amounts are plain numbers with no symbols. Maximum 50 line items; collapse
duplicates. Never return card numbers, bank account numbers or any full payment
credential — omit them entirely. If a field is not visible, use """" or 0 rather
than guessing. If the image is not an invoice or receipt, return
{{""error"": ""not-an-invoice""}}."),
                new UserChatMessage(new[]
                {
                    ChatMessageContentPart.CreateTextPart("Extract this document."),
                    ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(bytes), mime),
                }),
            };

            var result = await client.CompleteChatAsync(
                messages, AzureChatOptions.For(deployment, maxOutputTokens: 2000));
            raw = result.Value.Content[0].Text ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invoice extraction failed");
            return StatusCode(502, new { error = "The document could not be processed. Please retry." });
        }

        // ── Shape ────────────────────────────────────────────────────────────
        var json = StripFences(raw);
        JsonElement parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            _logger.LogWarning("Invoice extraction returned unparseable output");
            return StatusCode(502, new { error = "The document could not be read. Try a clearer photo." });
        }

        // The model's own refusal is a client error, not a server one — the
        // caller sent something that is not an invoice.
        if (parsed.ValueKind == JsonValueKind.Object
            && parsed.TryGetProperty("error", out var modelError)
            && modelError.ValueKind == JsonValueKind.String)
        {
            return BadRequest(new { error = "That image does not look like an invoice or receipt." });
        }

        return Ok(new { document = parsed });
    }

    /// <summary>What this API can do, for a caller checking their key works.</summary>
    [HttpGet("capabilities")]
    public IActionResult Capabilities() => Ok(new
    {
        service = "Invoice Agent",
        version = "1.0",
        billingUnit = "one document per call",
        maxImageBytes = 2_000_000,
        acceptedTypes = AllowedMime.OrderBy(m => m).ToArray(),
        categories = Categories,
        endpoints = new[]
        {
            new { method = "POST", path = "/api/invoices/extract", description = "Extract structured data from one invoice or receipt." },
            new { method = "GET", path = "/api/invoices/capabilities", description = "This document." },
        },
    });

    /// <summary>Models still fence JSON occasionally despite being told not to.</summary>
    private static string StripFences(string s)
    {
        var t = s.Trim();
        if (!t.StartsWith("```")) return t;
        var firstNewline = t.IndexOf('\n');
        if (firstNewline < 0) return t;
        t = t[(firstNewline + 1)..];
        var fence = t.LastIndexOf("```", StringComparison.Ordinal);
        return (fence >= 0 ? t[..fence] : t).Trim();
    }
}
