using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MoneyManager.Api.Data;
using MoneyManager.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace MoneyManager.Api.Controllers;

/// <summary>Bill & Subscription Tracker: snap bills/statements to track amounts and due dates.</summary>
[Authorize]
[ApiController]
[Route("api/bills")]
public class BillController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly AzureVisionService _vision;
    private readonly ILogger<BillController> _logger;

    public BillController(PromptDbContext db, AzureVisionService vision, ILogger<BillController> logger)
    { _db = db; _vision = vision;_logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // The categories the vision prompt is allowed to return, and the only ones
    // a user may file a bill under. Shared by create and update so the two can
    // never drift apart.
    private static readonly string[] BillCategories =
        { "utilities", "internet", "phone", "insurance", "rent", "subscription", "loan", "other" };

    [HttpGet("categories")]
    public IActionResult Categories() => Ok(BillCategories);

    private Task EnsureAsync() => LifestyleTables.EnsureBillsAsync(_db, _logger);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<BillRow>($@"
            SELECT ""Id"", ""Biller"", ""Category"", ""Amount"", ""Currency"", ""DueDate"", ""Recurring"", ""Notes"", ""CreatedAt""
            FROM ""Bills"" WHERE ""UserId""={UserId} ORDER BY ""DueDate""").ToListAsync();
        return Ok(rows);
    }

    [HttpPost]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Add([FromBody] ImageOnlyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData)) return BadRequest(new { message = "Add a photo of the bill." });
        if (req.ImageData.Length > 3_000_000) return BadRequest(new { message = "That photo is too large — try again." });
        if (!_vision.IsAvailable) return StatusCode(503, new { message = "Image analysis is not available right now." });
        await EnsureAsync();

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        string biller = "Bill", category = "other", currency = "", due = "", recurring = "one-off", notes = "";
        double amount = 0;
        try
        {
            var raw = await _vision.AnalyzeAsync(req.ImageData,
                $@"You read a bill, invoice, or subscription statement. Today is {today}. Return ONLY JSON (no fences):
{{ ""biller"":""company"", ""category"":""one of: utilities|internet|phone|insurance|rent|subscription|loan|other"", ""amount"":0.00, ""currency"":""code or symbol"", ""dueDate"":""yyyy-MM-dd if shown else empty"", ""recurring"":""monthly|annual|one-off"", ""notes"":""account/plan if visible"" }}
If not a bill, return {{""error"":""not-bill""}}.",
                "Log this bill's key details.", 500);
            var j = raw.Trim();
            if (j.StartsWith("```")) j = string.Join("\n", j.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            var s = j.IndexOf('{'); var e = j.LastIndexOf('}'); if (s >= 0 && e > s) j = j.Substring(s, e - s + 1);
            using var doc = JsonDocument.Parse(j);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var er) && er.GetString() == "not-bill")
                return BadRequest(new { message = "That doesn't look like a bill — try a clearer photo." });
            string S(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            biller = string.IsNullOrWhiteSpace(S("biller")) ? "Bill" : S("biller");
            // Constrain the model's category to the allowlist the prompt asked
            // for. Receipts already did this; bills stored whatever came back,
            // so a hallucinated category could never be grouped or filtered.
            var rawCategory = S("category").Trim().ToLowerInvariant();
            category = BillCategories.Contains(rawCategory) ? rawCategory : "other";
            currency = S("currency"); due = S("dueDate"); recurring = S("recurring"); notes = S("notes");
            if (r.TryGetProperty("amount", out var am) && am.ValueKind == JsonValueKind.Number) amount = Math.Round(am.GetDouble(), 2);
        }
        catch (Exception ex) { _logger.LogError(ex, "Bill analysis failed"); return StatusCode(500, new { message = "Could not read that bill." }); }

        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Bills"" (""UserId"", ""Biller"", ""Category"", ""Amount"", ""Currency"", ""DueDate"", ""Recurring"", ""Notes"", ""CreatedAt"")
            VALUES ({UserId}, {biller}, {category}, {amount}, {currency}, {due}, {recurring}, {notes}, {now})");
        var row = (await _db.Database.SqlQuery<BillRow>($@"
            SELECT ""Id"", ""Biller"", ""Category"", ""Amount"", ""Currency"", ""DueDate"", ""Recurring"", ""Notes"", ""CreatedAt""
            FROM ""Bills"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 1").ToListAsync()).FirstOrDefault();
        return Ok(row);
    }

    /// <summary>
    /// Correct a bill the vision model got wrong.
    ///
    /// The model guesses the category from a photo and is sometimes wrong — a
    /// broadband bill filed as "utilities", say. Without this the only remedy
    /// was to delete the bill and re-snap it, and the AI would usually guess
    /// the same thing again. Receipts already had this; bills did not.
    ///
    /// COALESCE means every field is optional: send only what changed.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] BillUpdateRequest req)
    {
        await EnsureAsync();

        // Reject an unknown category outright rather than silently filing it as
        // "other" — a user picking from the list should never be second-guessed,
        // and anything else is a caller bug worth surfacing.
        string? category = null;
        if (!string.IsNullOrWhiteSpace(req.Category))
        {
            category = req.Category.Trim().ToLowerInvariant();
            if (!BillCategories.Contains(category))
                return BadRequest(new { message = $"Unknown category. Use one of: {string.Join(", ", BillCategories)}." });
        }

        string? recurring = null;
        if (!string.IsNullOrWhiteSpace(req.Recurring))
        {
            recurring = req.Recurring.Trim().ToLowerInvariant();
            if (recurring is not ("monthly" or "annual" or "one-off"))
                return BadRequest(new { message = "Recurring must be monthly, annual, or one-off." });
        }

        var affected = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""Bills"" SET
                ""Biller""    = COALESCE({req.Biller},   ""Biller""),
                ""Category""  = COALESCE({category},     ""Category""),
                ""Amount""    = COALESCE({req.Amount},   ""Amount""),
                ""Currency""  = COALESCE({req.Currency}, ""Currency""),
                ""DueDate""   = COALESCE({req.DueDate},  ""DueDate""),
                ""Recurring"" = COALESCE({recurring},    ""Recurring""),
                ""Notes""     = COALESCE({req.Notes},    ""Notes"")
            WHERE ""Id""={id} AND ""UserId""={UserId}");

        // Scoping the UPDATE by UserId means a mismatched id is either someone
        // else's row or a deleted one; both should read as not-found.
        if (affected == 0) return NotFound(new { message = "Bill not found." });

        var row = (await _db.Database.SqlQuery<BillRow>($@"
            SELECT ""Id"", ""Biller"", ""Category"", ""Amount"", ""Currency"", ""DueDate"", ""Recurring"", ""Notes"", ""CreatedAt""
            FROM ""Bills"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
        return Ok(row);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""Bills"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Removed." });
    }

    /// <summary>
    /// Partial update of a bill. Every field is optional — null means "leave
    /// this one alone", so the UI can send just the category the user changed.
    /// </summary>
    public sealed class BillUpdateRequest
    {
        public string? Biller { get; set; }
        public string? Category { get; set; }
        public double? Amount { get; set; }
        public string? Currency { get; set; }
        public string? DueDate { get; set; }
        public string? Recurring { get; set; }
        public string? Notes { get; set; }
    }

    public sealed class BillRow
    {
        public int Id { get; set; }
        public string? Biller { get; set; }
        public string? Category { get; set; }
        public double? Amount { get; set; }
        public string? Currency { get; set; }
        public string? DueDate { get; set; }
        public string? Recurring { get; set; }
        public string? Notes { get; set; }
        public string? CreatedAt { get; set; }
    }
}
