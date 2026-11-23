using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MoneyManager.Api.Data;
using MoneyManager.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace MoneyManager.Api.Controllers;

/// <summary>Warranty & Manual Vault: snap a product or its receipt to track warranty windows.</summary>
[Authorize]
[ApiController]
[Route("api/warranty")]
public class WarrantyController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly AzureVisionService _vision;
    private readonly ILogger<WarrantyController> _logger;
    private const int MaxImageChars = 3_000_000;

    public WarrantyController(PromptDbContext db, AzureVisionService vision, ILogger<WarrantyController> logger)
    { _db = db; _vision = vision;_logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    private Task EnsureAsync() => LifestyleTables.EnsureWarrantiesAsync(_db, _logger);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<WarrantyRow>($@"
            SELECT ""Id"", ""Product"", ""Brand"", ""Retailer"", ""PurchaseDate"", ""WarrantyMonths"", ""ExpiryDate"", ""Notes"", ""ImageData"", ""CreatedAt""
            FROM ""Warranties"" WHERE ""UserId""={UserId} ORDER BY ""ExpiryDate""").ToListAsync();
        return Ok(rows);
    }

    [HttpPost]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Add([FromBody] ImageOnlyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData)) return BadRequest(new { message = "Add a photo of the product or its receipt." });
        if (req.ImageData.Length > MaxImageChars) return BadRequest(new { message = "That photo is too large — try again." });
        if (!_vision.IsAvailable) return StatusCode(503, new { message = "Image analysis is not available right now." });
        await EnsureAsync();

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        string product = "Product", brand = "", retailer = "", purchase = today, notes = "";
        int months = 12;
        try
        {
            var raw = await _vision.AnalyzeAsync(req.ImageData,
                $@"You read a product photo or its purchase receipt to log a warranty. Today is {today}. Return ONLY JSON (no fences):
{{ ""product"":""product name"", ""brand"":""brand"", ""retailer"":""store if on a receipt else empty"", ""purchaseDate"":""yyyy-MM-dd if shown else today"", ""warrantyMonths"":12, ""notes"":""model/serial if visible"" }}
Estimate warrantyMonths from the product type if not stated (electronics ~12-24). If not a product/receipt, return {{""error"":""not-product""}}.",
                "Log the warranty details.", 500);
            var r = Parse(raw);
            if (r.TryGetProperty("error", out var er) && er.GetString() == "not-product")
                return BadRequest(new { message = "That doesn't look like a product or receipt — try again." });
            string S(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            product = string.IsNullOrWhiteSpace(S("product")) ? "Product" : S("product");
            brand = S("brand"); retailer = S("retailer"); notes = S("notes");
            var pd = S("purchaseDate"); if (System.Text.RegularExpressions.Regex.IsMatch(pd, @"^\d{4}-\d{2}-\d{2}$")) purchase = pd;
            if (r.TryGetProperty("warrantyMonths", out var wm) && wm.ValueKind == JsonValueKind.Number) months = Math.Clamp(wm.GetInt32(), 1, 240);
        }
        catch (Exception ex) { _logger.LogError(ex, "Warranty analysis failed"); return StatusCode(500, new { message = "Could not read that photo." }); }

        var expiry = DateTime.Parse(purchase).AddMonths(months).ToString("yyyy-MM-dd");
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Warranties"" (""UserId"", ""Product"", ""Brand"", ""Retailer"", ""PurchaseDate"", ""WarrantyMonths"", ""ExpiryDate"", ""Notes"", ""ImageData"", ""CreatedAt"")
            VALUES ({UserId}, {product}, {brand}, {retailer}, {purchase}, {months}, {expiry}, {notes}, {req.ImageData}, {now})");
        var row = (await _db.Database.SqlQuery<WarrantyRow>($@"
            SELECT ""Id"", ""Product"", ""Brand"", ""Retailer"", ""PurchaseDate"", ""WarrantyMonths"", ""ExpiryDate"", ""Notes"", ""ImageData"", ""CreatedAt""
            FROM ""Warranties"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 1").ToListAsync()).FirstOrDefault();
        return Ok(row);
    }

    /// <summary>
    /// File an existing receipt as a warranty.
    ///
    /// A receipt for an appliance already contains everything a warranty record
    /// needs — merchant, purchase date, product — but the two modules never
    /// spoke, so the user had to photograph the same receipt a second time and
    /// pay for a second vision call to extract the same facts.
    ///
    /// No AI call here at all: the receipt was already parsed once. This just
    /// copies the answer across and applies a warranty term.
    /// </summary>
    [HttpPost("from-receipt/{receiptId:int}")]
    public async Task<IActionResult> FromReceipt(int receiptId, [FromBody] FromReceiptRequest req)
    {
        await EnsureAsync();

        // Scoped by UserId so a guessed id cannot reach someone else's receipt.
        var receipt = (await _db.Database.SqlQuery<ReceiptSource>($@"
            SELECT ""Id"", ""Merchant"", ""PurchaseDate"", ""Total"", ""Currency"", ""ItemsJson""
            FROM ""Receipts"" WHERE ""Id""={receiptId} AND ""UserId""={UserId}").ToListAsync())
            .FirstOrDefault();

        if (receipt == null) return NotFound(new { message = "Receipt not found." });

        var months = req.WarrantyMonths is >= 1 and <= 240 ? req.WarrantyMonths!.Value : 12;

        var product = !string.IsNullOrWhiteSpace(req.Product)
            ? req.Product!.Trim()
            : (!string.IsNullOrWhiteSpace(receipt.Merchant) ? $"Purchase from {receipt.Merchant}" : "Purchase");

        if (product.Length > 200) product = product[..200];

        // Fall back to today when the receipt has no readable date, so a missing
        // field cannot silently produce a warranty that expired in year 1.
        var purchase = DateTime.TryParse(receipt.PurchaseDate, out var pd)
            ? pd.Date
            : DateTime.UtcNow.Date;

        var expiry = purchase.AddMonths(months);
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var amount = receipt.Total is > 0 ? $"{receipt.Currency}{receipt.Total:0.00}" : null;
        var notes = string.Join(" · ", new[] { amount, $"filed from receipt #{receipt.Id}" }
            .Where(x => !string.IsNullOrWhiteSpace(x)));

        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Warranties"" (""UserId"", ""Product"", ""Brand"", ""Retailer"", ""PurchaseDate"", ""WarrantyMonths"", ""ExpiryDate"", ""Notes"", ""ImageData"", ""CreatedAt"")
            VALUES ({UserId}, {product}, {""}, {receipt.Merchant ?? ""}, {purchase:yyyy-MM-dd}, {months}, {expiry:yyyy-MM-dd}, {notes}, {""}, {now})");

        var row = (await _db.Database.SqlQuery<WarrantyRow>($@"
            SELECT ""Id"", ""Product"", ""Brand"", ""Retailer"", ""PurchaseDate"", ""WarrantyMonths"", ""ExpiryDate"", ""Notes"", ""ImageData"", ""CreatedAt""
            FROM ""Warranties"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 1").ToListAsync())
            .FirstOrDefault();

        return Ok(row);
    }

    public sealed class FromReceiptRequest
    {
        /// <summary>Overrides the merchant-derived name. Optional.</summary>
        public string? Product { get; set; }
        /// <summary>Warranty term in months. Defaults to 12.</summary>
        public int? WarrantyMonths { get; set; }
    }

    private sealed class ReceiptSource
    {
        public int Id { get; set; }
        public string? Merchant { get; set; }
        public string? PurchaseDate { get; set; }
        public double? Total { get; set; }
        public string? Currency { get; set; }
        public string? ItemsJson { get; set; }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""Warranties"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Removed." });
    }

    private static JsonElement Parse(string raw)
    {
        var j = raw.Trim();
        if (j.StartsWith("```")) j = string.Join("\n", j.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
        var s = j.IndexOf('{'); var e = j.LastIndexOf('}');
        if (s >= 0 && e > s) j = j.Substring(s, e - s + 1);
        return JsonDocument.Parse(j).RootElement.Clone();
    }

    public sealed class WarrantyRow
    {
        public int Id { get; set; }
        public string? Product { get; set; }
        public string? Brand { get; set; }
        public string? Retailer { get; set; }
        public string? PurchaseDate { get; set; }
        public int? WarrantyMonths { get; set; }
        public string? ExpiryDate { get; set; }
        public string? Notes { get; set; }
        public string? ImageData { get; set; }
        public string? CreatedAt { get; set; }
    }
}
