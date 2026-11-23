using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PlantDoctor.Api.Data;
using PlantDoctor.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace PlantDoctor.Api.Controllers;

/// <summary>
/// Plant Doctor: snap a plant to identify it, diagnose problems, and get a care plan
/// with a watering interval. Care advice is general guidance, not a guarantee.
/// </summary>
[Authorize]
[ApiController]
[Route("api/plants")]
public class PlantController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly AzureVisionService _vision;
    private readonly ILogger<PlantController> _logger;

    private const int MaxImageChars = 3_000_000;

    public PlantController(PromptDbContext db, AzureVisionService vision, ILogger<PlantController> logger)
    {
        _db = db; _vision = vision;_logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    private Task EnsureTableAsync() => LifestyleTables.EnsurePlantsAsync(_db, _logger);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        await EnsureTableAsync();
        var rows = await _db.Database.SqlQuery<PlantRow>($@"
            SELECT ""Id"", ""Name"", ""Nickname"", ""Species"", ""Health"", ""Diagnosis"", ""Light"", ""CareTips"",
                   ""WaterEveryDays"", ""LastWatered"", ""ImageData"", ""CreatedAt""
            FROM ""Plants"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC").ToListAsync();
        return Ok(rows);
    }

    [HttpPost]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Add([FromBody] PlantAddRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData)) return BadRequest(new { message = "Add a photo of your plant." });
        if (req.ImageData.Length > MaxImageChars) return BadRequest(new { message = "That photo is too large — try again." });
        if (!_vision.IsAvailable) return StatusCode(503, new { message = "Image analysis is not available right now." });

        await EnsureTableAsync();

        string name = "Plant", species = "", health = "unknown", diagnosis = "", light = "", careTips = "";
        int waterDays = 7;
        try
        {
            var raw = await _vision.AnalyzeAsync(req.ImageData,
                @"You are a botanist. Identify the plant in the photo and assess its health. Return ONLY JSON (no fences):
{
  ""commonName"": ""common name"",
  ""species"": ""scientific name or best guess"",
  ""health"": ""one of: healthy | needs-attention | unhealthy"",
  ""diagnosis"": ""one sentence on what you see (yellowing, pests, overwatering, etc.) — 'Looks healthy' if fine"",
  ""light"": ""light needs, e.g. 'bright indirect'"",
  ""wateringDays"": 7,
  ""careTips"": [""3-5 short, specific care tips""]
}
If it is not a plant, return {""error"":""not-plant""}.",
                "Identify and diagnose this plant.", 700);
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var er) && er.GetString() == "not-plant")
                return BadRequest(new { message = "That doesn't look like a plant — try a clear photo of one." });
            string S(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            name = string.IsNullOrWhiteSpace(S("commonName")) ? "Plant" : S("commonName");
            species = S("species"); diagnosis = S("diagnosis"); light = S("light");
            var h = S("health").ToLowerInvariant();
            health = h is "healthy" or "needs-attention" or "unhealthy" ? h : "unknown";
            if (r.TryGetProperty("wateringDays", out var wd) && wd.ValueKind == JsonValueKind.Number) waterDays = Math.Clamp(wd.GetInt32(), 1, 60);
            if (r.TryGetProperty("careTips", out var ct) && ct.ValueKind == JsonValueKind.Array)
                careTips = string.Join("\n", ct.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plant analysis failed");
            return StatusCode(500, new { message = "Could not read that photo — try a clearer one." });
        }

        var now = DateTime.UtcNow.ToString("o");
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""Plants"" (""UserId"", ""Name"", ""Nickname"", ""Species"", ""Health"", ""Diagnosis"", ""Light"", ""CareTips"", ""WaterEveryDays"", ""LastWatered"", ""ImageData"", ""CreatedAt"")
            VALUES ({UserId}, {name}, {req.Nickname ?? ""}, {species}, {health}, {diagnosis}, {light}, {careTips}, {waterDays}, {today}, {req.ImageData}, {now})");
        var row = (await _db.Database.SqlQuery<PlantRow>($@"
            SELECT ""Id"", ""Name"", ""Nickname"", ""Species"", ""Health"", ""Diagnosis"", ""Light"", ""CareTips"",
                   ""WaterEveryDays"", ""LastWatered"", ""ImageData"", ""CreatedAt""
            FROM ""Plants"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 1").ToListAsync()).FirstOrDefault();
        return Ok(row);
    }

    [HttpPost("{id:int}/water")]
    public async Task<IActionResult> Water(int id)
    {
        await EnsureTableAsync();
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""Plants"" SET ""LastWatered""={today} WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { lastWatered = today });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await EnsureTableAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""Plants"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Removed." });
    }

    private static string ExtractJson(string raw)
    {
        var j = raw.Trim();
        if (j.StartsWith("```")) j = string.Join("\n", j.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
        var s = j.IndexOf('{'); var e = j.LastIndexOf('}');
        if (s >= 0 && e > s) j = j.Substring(s, e - s + 1);
        using var _ = JsonDocument.Parse(j);
        return j;
    }

    public sealed class PlantRow
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Nickname { get; set; }
        public string? Species { get; set; }
        public string? Health { get; set; }
        public string? Diagnosis { get; set; }
        public string? Light { get; set; }
        public string? CareTips { get; set; }
        public int? WaterEveryDays { get; set; }
        public string? LastWatered { get; set; }
        public string? ImageData { get; set; }
        public string? CreatedAt { get; set; }
    }
}

public class PlantAddRequest
{
    public string? ImageData { get; set; }
    public string? Nickname { get; set; }
}
