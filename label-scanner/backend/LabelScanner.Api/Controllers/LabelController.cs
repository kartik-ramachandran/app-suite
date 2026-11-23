using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LabelScanner.Api.Data;
using LabelScanner.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace LabelScanner.Api.Controllers;

/// <summary>
/// Label Scanner: snap a food/product label and get a verdict against the user's saved
/// dietary preferences and allergies. Not medical advice.
/// </summary>
[Authorize]
[ApiController]
[Route("api/label")]
public class LabelController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly AzureVisionService _vision;
    private readonly ILogger<LabelController> _logger;

    public LabelController(PromptDbContext db, AzureVisionService vision, ILogger<LabelController> logger)
    { _db = db; _vision = vision;_logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    [HttpPost("scan")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Scan([FromBody] ImageOnlyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData)) return BadRequest(new { message = "Add a photo of the label." });
        if (req.ImageData.Length > 3_000_000) return BadRequest(new { message = "That photo is too large — try again." });
        if (!_vision.IsAvailable) return StatusCode(503, new { message = "Image analysis is not available right now." });

        // Pull the user's dietary preferences (kitchen allergies first, then travel dietary prefs).
        string diet = "";
        try { diet = (await _db.Database.SqlQuery<string?>($@"SELECT ""Allergies"" AS ""Value"" FROM ""KitchenPrefs"" WHERE ""UserId""={UserId}").ToListAsync()).FirstOrDefault() ?? ""; } catch { }
        if (string.IsNullOrWhiteSpace(diet))
            try { diet = (await _db.Database.SqlQuery<string?>($@"SELECT ""DietaryPreferences"" AS ""Value"" FROM ""UserTravelPrefs"" WHERE ""UserId""={UserId}").ToListAsync()).FirstOrDefault() ?? ""; } catch { }

        try
        {
            var raw = await _vision.AnalyzeAsync(req.ImageData,
                $@"You read a food/product label (ingredients + nutrition). The user's dietary needs/allergies: {(string.IsNullOrWhiteSpace(diet) ? "none stated" : diet)}.
Return ONLY JSON (no fences):
{{ ""product"":""product name if visible"", ""verdict"":""one of: ok | caution | avoid"", ""reasons"":[""short reasons for the verdict tied to their diet/allergies and general healthiness""], ""flagged"":[""specific ingredients that conflict with their needs, if any""], ""healthNote"":""one short general note (sugar/sodium/additives)"" }}
If the user has an allergy present in the ingredients, verdict MUST be 'avoid'. If not a label, return {{""error"":""not-label""}}.",
                "Assess this label for me.", 700);
            var j = raw.Trim();
            if (j.StartsWith("```")) j = string.Join("\n", j.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            var s = j.IndexOf('{'); var e = j.LastIndexOf('}'); if (s >= 0 && e > s) j = j.Substring(s, e - s + 1);
            using var doc = JsonDocument.Parse(j);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var er) && er.GetString() == "not-label")
                return BadRequest(new { message = "That doesn't look like a product label — try a clear photo of the ingredients." });
            string S(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            string[] A(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.Array ? el.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : Array.Empty<string>();
            var verdict = S("verdict").ToLowerInvariant(); if (verdict is not ("ok" or "caution" or "avoid")) verdict = "caution";
            var product = S("product");
            return Ok(new { product, verdict, reasons = A("reasons"), flagged = A("flagged"), healthNote = S("healthNote"), hasDiet = !string.IsNullOrWhiteSpace(diet) });
        }
        catch (Exception ex) { _logger.LogError(ex, "Label scan failed"); return StatusCode(500, new { message = "Could not read that label — try a clearer photo." }); }
    }
}
