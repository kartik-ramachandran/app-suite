using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LifeManager.Api.Data;
using LifeManager.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace LifeManager.Api.Controllers;

/// <summary>
/// Medicine Cabinet: snap medicines to catalogue them, track expiry, and get a
/// GENERAL interaction check. NOT medical advice — always consult a pharmacist/doctor.
/// </summary>
[Authorize]
[ApiController]
[Route("api/medicine")]
public class MedicineController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly AzureVisionService _vision;
    private readonly ILogger<MedicineController> _logger;
    private const int MaxImageChars = 3_000_000;

    public MedicineController(PromptDbContext db, AzureVisionService vision, ILogger<MedicineController> logger)
    { _db = db; _vision = vision;_logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    private Task EnsureAsync() => LifestyleTables.EnsureMedicinesAsync(_db, _logger);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<MedRow>($@"
            SELECT ""Id"", ""Name"", ""Form"", ""Strength"", ""Purpose"", ""ExpiryDate"", ""Notes"", ""ImageData"", ""CreatedAt""
            FROM ""Medicines"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC").ToListAsync();
        return Ok(rows);
    }

    [HttpPost]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Add([FromBody] ImageOnlyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData)) return BadRequest(new { message = "Add a photo of the medicine." });
        if (req.ImageData.Length > MaxImageChars) return BadRequest(new { message = "That photo is too large — try again." });
        if (!_vision.IsAvailable) return StatusCode(503, new { message = "Image analysis is not available right now." });
        await EnsureAsync();

        string name = "Medicine", form = "", strength = "", purpose = "", expiry = "", notes = "";
        try
        {
            var raw = await _vision.AnalyzeAsync(req.ImageData,
                @"You read a medicine package/label. Return ONLY JSON (no fences):
{ ""name"":""product/drug name"", ""form"":""tablet|capsule|liquid|cream|spray|drops|other"", ""strength"":""e.g. 500mg"", ""purpose"":""what it's generally used for, one short phrase"", ""expiry"":""yyyy-MM or yyyy-MM-dd if visible, else empty"", ""notes"":""dosage or storage note if printed"" }
If not a medicine, return {""error"":""not-medicine""}.",
                "Read this medicine label.", 500);
            var r = Parse(raw);
            if (r.TryGetProperty("error", out var er) && er.GetString() == "not-medicine")
                return BadRequest(new { message = "That doesn't look like a medicine — try a clear photo of the package." });
            string S(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            name = string.IsNullOrWhiteSpace(S("name")) ? "Medicine" : S("name");
            form = S("form"); strength = S("strength"); purpose = S("purpose"); expiry = S("expiry"); notes = S("notes");
        }
        catch (Exception ex) { _logger.LogError(ex, "Medicine analysis failed"); return StatusCode(500, new { message = "Could not read that photo." }); }

        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Medicines"" (""UserId"", ""Name"", ""Form"", ""Strength"", ""Purpose"", ""ExpiryDate"", ""Notes"", ""ImageData"", ""CreatedAt"")
            VALUES ({UserId}, {name}, {form}, {strength}, {purpose}, {expiry}, {notes}, {req.ImageData}, {now})");
        var row = (await _db.Database.SqlQuery<MedRow>($@"
            SELECT ""Id"", ""Name"", ""Form"", ""Strength"", ""Purpose"", ""ExpiryDate"", ""Notes"", ""ImageData"", ""CreatedAt""
            FROM ""Medicines"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 1").ToListAsync()).FirstOrDefault();
        return Ok(row);
    }

    [HttpPost("check")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> CheckInteractions()
    {
        await EnsureAsync();
        var meds = await _db.Database.SqlQuery<string>($@"SELECT ""Name"" || ' ' || COALESCE(""Strength"",'') AS ""Value"" FROM ""Medicines"" WHERE ""UserId""={UserId}").ToListAsync();
        if (meds.Count < 2) return Ok(new { advice = "Add at least two medicines to check for interactions." });
        try
        {
            var resp = await _vision.CompleteTextAsync(
                @"You are a careful pharmacology reference. Given a list of medicines a person keeps, list any GENERAL, well-known interaction cautions or duplicate-ingredient risks, as short plain bullets starting with '- '. Be conservative. End with: 'This is general information, not medical advice — confirm with a pharmacist or doctor.' If nothing notable, say so plus that disclaimer.",
                "Medicines: " + string.Join(", ", meds), 700);
            return Ok(new { advice = resp });
        }
        catch (Exception ex) { _logger.LogError(ex, "Interaction check failed"); return StatusCode(500, new { message = "Could not run the check right now." }); }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""Medicines"" WHERE ""Id""={id} AND ""UserId""={UserId}");
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

    public sealed class MedRow
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Form { get; set; }
        public string? Strength { get; set; }
        public string? Purpose { get; set; }
        public string? ExpiryDate { get; set; }
        public string? Notes { get; set; }
        public string? ImageData { get; set; }
        public string? CreatedAt { get; set; }
    }
}

public class ImageOnlyRequest { public string? ImageData { get; set; } }
