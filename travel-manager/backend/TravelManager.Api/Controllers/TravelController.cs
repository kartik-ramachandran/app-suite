using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using TravelManager.Api.Data;
using TravelManager.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace TravelManager.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/travel")]
public class TravelController : ControllerBase
{
    private readonly TravelIntelligenceService _travel;
    private readonly FlightPriceService _flights;
    private readonly AppDbContext _db;
    private readonly ILogger<TravelController> _logger;

    public TravelController(TravelIntelligenceService travel, FlightPriceService flights, AppDbContext db, ILogger<TravelController> logger)
    {
        _travel = travel;
        _flights = flights;
        _db = db;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Dietary / food preferences (surfaced in Settings) ─────────────────────

    [HttpGet("preferences")]
    public async Task<IActionResult> GetPreferences()
    {
        var rows = await _db.Database.SqlQuery<string?>(
            $@"SELECT ""DietaryPreferences"" AS ""Value"" FROM ""UserTravelPrefs"" WHERE ""UserId""={UserId}").ToListAsync();
        return Ok(new { dietaryPreferences = rows.FirstOrDefault() ?? "" });
    }

    [HttpPut("preferences")]
    public async Task<IActionResult> SavePreferences([FromBody] TravelPrefsRequest req)
    {
        var prefs = (req.DietaryPreferences ?? "").Trim();
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""UserTravelPrefs"" (""UserId"", ""DietaryPreferences"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({UserId}, {prefs}, {now}, {now})
            ON CONFLICT (""UserId"") DO UPDATE SET ""DietaryPreferences""=excluded.""DietaryPreferences"", ""UpdatedAt""=excluded.""UpdatedAt""");
        return Ok(new { message = "Preferences saved." });
    }

    // ── Travel guide ──────────────────────────────────────────────────────────

    [HttpPost("guide")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> GenerateGuide([FromBody] TravelGuideRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Destination))
            return BadRequest(new { message = "Enter a destination." });
        if (req.EndDate < req.StartDate)
            return BadRequest(new { message = "The return date can't be before the start date." });
        if ((req.EndDate - req.StartDate).TotalDays > 60)
            return BadRequest(new { message = "Please keep the trip to 60 days or fewer." });

        // Use the traveler's saved dietary preferences unless overridden in the request.
        var dietary = req.DietaryPreferences;
        if (string.IsNullOrWhiteSpace(dietary))
        {
            var rows = await _db.Database.SqlQuery<string?>(
                $@"SELECT ""DietaryPreferences"" AS ""Value"" FROM ""UserTravelPrefs"" WHERE ""UserId""={UserId}").ToListAsync();
            dietary = rows.FirstOrDefault();
        }

        try
        {
            var guide = await _travel.BuildGuideAsync(
                req.Destination.Trim(), req.StartDate, req.EndDate,
                req.TripType, dietary, req.Notes);
            await SaveGuideAsync(guide);
            return Ok(guide);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Travel guide generation failed for {Destination}", req.Destination);
            return StatusCode(500, new { message = "Could not build the travel guide. Please try again." });
        }
    }

    // ── Saved plans (last 5 per user) ─────────────────────────────────────────

    private static readonly JsonSerializerOptions GuideJsonOpts = new(JsonSerializerDefaults.Web);
    private static bool _guidesTableEnsured;

    // Startup table creation silently fails for this table on the prod Postgres
    // connection, so ensure it here on the (working) request connection. Idempotent.
    private async Task EnsureGuidesTableAsync()
    {
        if (_guidesTableEnsured) return;
        try
        {
            var isSqlite = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
            if (isSqlite)
                await _db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""TravelGuides"" (""Id"" INTEGER PRIMARY KEY AUTOINCREMENT, ""UserId"" TEXT NOT NULL, ""Destination"" TEXT, ""StartDate"" TEXT, ""EndDate"" TEXT, ""GuideJson"" TEXT, ""CreatedAt"" TEXT)");
            else
                await _db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""TravelGuides"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Destination"" TEXT, ""StartDate"" TEXT, ""EndDate"" TEXT, ""GuideJson"" TEXT, ""CreatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_TravelGuides_UserId"" ON ""TravelGuides"" (""UserId"", ""Id"")");
            _guidesTableEnsured = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EnsureGuidesTable failed"); }
    }

    private async Task SaveGuideAsync(TravelGuide guide)
    {
        await EnsureGuidesTableAsync();
        var json = JsonSerializer.Serialize(guide, GuideJsonOpts);
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""TravelGuides"" (""UserId"", ""Destination"", ""StartDate"", ""EndDate"", ""GuideJson"", ""CreatedAt"")
            VALUES ({UserId}, {guide.Destination}, {guide.StartDate}, {guide.EndDate}, {json}, {now})");
        // Keep only the 5 most recent per user.
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM ""TravelGuides"" WHERE ""UserId""={UserId} AND ""Id"" NOT IN (
                SELECT ""Id"" FROM ""TravelGuides"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 5)");
    }

    [HttpGet("guides")]
    public async Task<IActionResult> GetGuides()
    {
        await EnsureGuidesTableAsync();
        var rows = await _db.Database.SqlQuery<GuideSummaryRow>($@"
            SELECT ""Id"", ""Destination"", ""StartDate"", ""EndDate"", ""CreatedAt""
            FROM ""TravelGuides"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 5").ToListAsync();
        return Ok(rows);
    }

    [HttpGet("guides/{id:int}")]
    public async Task<IActionResult> GetGuide(int id)
    {
        await EnsureGuidesTableAsync();
        var rows = await _db.Database.SqlQuery<string?>($@"
            SELECT ""GuideJson"" AS ""Value"" FROM ""TravelGuides"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync();
        var json = rows.FirstOrDefault();
        if (string.IsNullOrEmpty(json)) return NotFound(new { message = "Saved plan not found." });
        return Content(json, "application/json");
    }

    [HttpDelete("guides/{id:int}")]
    public async Task<IActionResult> DeleteGuide(int id)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""TravelGuides"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Deleted." });
    }

    // ── Best time to fly (historical fares via Travelpayouts) ─────────────────

    [HttpPost("flight-prices")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> FlightPrices([FromBody] FlightPricesRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Origin) || string.IsNullOrWhiteSpace(req.Destination))
            return BadRequest(new { message = "Enter where you're flying from and to." });

        var insight = await _flights.GetInsightAsync(req.Origin.Trim(), req.Destination.Trim(), req.Currency);
        return Ok(insight);
    }
}

public class FlightPricesRequest
{
    public required string Origin { get; set; }
    public required string Destination { get; set; }
    public string? Currency { get; set; }
}

public class TravelPrefsRequest
{
    public string? DietaryPreferences { get; set; }
}

public class GuideSummaryRow
{
    public int Id { get; set; }
    public string Destination { get; set; } = "";
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public string CreatedAt { get; set; } = "";
}

public class TravelGuideRequest
{
    public required string Destination { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public string? TripType { get; set; }
    public string? Notes { get; set; }
    // Optional per-request override; falls back to the saved preference.
    public string? DietaryPreferences { get; set; }
}
