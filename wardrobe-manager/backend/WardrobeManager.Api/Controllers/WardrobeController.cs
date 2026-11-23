using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WardrobeManager.Api.Data;
using WardrobeManager.Api.Models;
using WardrobeManager.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace WardrobeManager.Api.Controllers;

/// <summary>
/// Wardrobe Intelligence: photograph your clothes, get them auto-catalogued by AI,
/// ask "what should I wear", track garment age from purchase date, and get
/// responsible-discard guidance (repair / donate / recycle) for tired pieces.
/// Body photos are analyzed in-memory for styling notes and NEVER stored.
/// </summary>
[Authorize]
[ApiController]
[Route("api/wardrobe")]
public class WardrobeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AzureVisionService _vision;
    private readonly IAIService _ai;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<WardrobeController> _logger;

    private const int MaxImageChars = 3_000_000; // ~2.2MB binary — client downscales before upload
    private const int MaxActiveItems = 300;

    public WardrobeController(AppDbContext db, AzureVisionService vision, IAIService ai, IHttpClientFactory http, ILogger<WardrobeController> logger)
    {
        _db = db;
        _vision = vision;
        _ai = ai;
        _http = http;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Table bootstrap (lazy, on the request connection — same pattern as TravelGuides) ──

    private static bool _tablesEnsured;

    private async Task EnsureTablesAsync()
    {
        if (_tablesEnsured) return;
        try
        {
            var isSqlite = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
            var pk = isSqlite ? "INTEGER PRIMARY KEY AUTOINCREMENT" : "SERIAL PRIMARY KEY";
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""WardrobeItems"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Name"" TEXT, ""Category"" TEXT, ""Colors"" TEXT,
                ""Pattern"" TEXT, ""Style"" TEXT, ""Seasons"" TEXT, ""Formality"" TEXT, ""Condition"" TEXT,
                ""CareTip"" TEXT, ""ImageData"" TEXT, ""BoughtDate"" TEXT, ""Notes"" TEXT,
                ""Status"" TEXT NOT NULL DEFAULT 'active', ""CreatedAt"" TEXT, ""DiscardedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_WardrobeItems_UserId"" ON ""WardrobeItems"" (""UserId"", ""Status"", ""Id"")");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""WardrobeProfiles"" (
                ""UserId"" TEXT PRIMARY KEY, ""BodyAdvice"" TEXT, ""UpdatedAt"" TEXT)");
            // Style preference columns (added after initial rollout — SQLite has no ADD COLUMN IF NOT EXISTS)
            foreach (var col in new[] { "Presentation", "FavColors", "AvoidColors", "SkinTone" })
            {
                try { await _db.Database.ExecuteSqlRawAsync($@"ALTER TABLE ""WardrobeProfiles"" ADD COLUMN ""{col}"" TEXT"); }
                catch { /* column already exists */ }
            }
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""WardrobeOutfits"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Occasion"" TEXT, ""WeatherSummary"" TEXT,
                ""Capsule"" INTEGER NOT NULL DEFAULT 0, ""OutfitsJson"" TEXT, ""CreatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_WardrobeOutfits_UserId"" ON ""WardrobeOutfits"" (""UserId"", ""Id"")");
            _tablesEnsured = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EnsureWardrobeTables failed"); }
    }

    // ── Items ──────────────────────────────────────────────────────────────────

    [HttpGet("items")]
    public async Task<IActionResult> GetItems([FromQuery] string? status)
    {
        await EnsureTablesAsync();
        var wanted = status == "discarded" ? "discarded" : "active";
        var rows = await _db.Database.SqlQuery<WardrobeItemRow>($@"
            SELECT ""Id"", ""Name"", ""Category"", ""Colors"", ""Pattern"", ""Style"", ""Seasons"", ""Formality"",
                   ""Condition"", ""CareTip"", ""ImageData"", ""BoughtDate"", ""Notes"", ""Status"", ""CreatedAt"", ""DiscardedAt""
            FROM ""WardrobeItems"" WHERE ""UserId""={UserId} AND ""Status""={wanted} ORDER BY ""Id"" DESC").ToListAsync();
        return Ok(rows);
    }

    [HttpPost("items")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> AddItem([FromBody] WardrobeAddRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData))
            return BadRequest(new { message = "Add a photo of the garment." });
        if (req.ImageData.Length > MaxImageChars)
            return BadRequest(new { message = "That photo is too large — please try again (it should compress automatically)." });
        if (!_vision.IsAvailable)
            return StatusCode(503, new { message = "Image analysis is not available right now." });

        await EnsureTablesAsync();

        var count = (await _db.Database.SqlQuery<int>(
            $@"SELECT COUNT(*) AS ""Value"" FROM ""WardrobeItems"" WHERE ""UserId""={UserId} AND ""Status""='active'").ToListAsync()).FirstOrDefault();
        if (count >= MaxActiveItems)
            return BadRequest(new { message = $"Wardrobe limit of {MaxActiveItems} items reached — discard some pieces first." });

        // One photo can hold several garments (a t-shirt under an open jacket,
        // shoes in frame, a laid-out outfit) — catalogue EVERY distinct item.
        var analyses = new List<WardrobeAnalysis>();
        try
        {
            var raw = await _vision.AnalyzeAsync(req.ImageData,
                @"You are a fashion cataloguer. Identify EVERY distinct clothing item, shoe, bag, or accessory visible in this photo — including layered pieces (e.g. a t-shirt worn under an open jacket) and items only partly visible. Return ONLY JSON (no markdown fences):
{
  ""items"": [
    {
      ""name"": ""short specific name, e.g. 'Navy slim-fit chinos' or 'White crew-neck t-shirt'"",
      ""category"": ""one of: top | bottom | dress | outerwear | shoes | accessory | bag | other"",
      ""colors"": [""main colours, lowercase""],
      ""pattern"": ""solid | striped | checked | floral | graphic | other"",
      ""style"": ""one short style word: casual | classic | streetwear | sporty | bohemian | elegant | edgy"",
      ""seasons"": [""which of: summer, winter, spring, autumn, all-season""],
      ""formality"": ""casual | smart-casual | business | formal | athletic"",
      ""condition"": ""what the photo shows: like-new | good | worn | heavily-worn"",
      ""careTip"": ""one practical care tip for this fabric/garment type""
    }
  ]
}
List each distinct garment exactly once, most prominent first (max 6). Ignore backgrounds, furniture, and people's features. If nothing in the photo is a clothing item, shoe, bag, or accessory, return {""error"": ""not-clothing""}.",
                "Catalogue every garment in this photo.");
            analyses = ParseAnalyses(raw);

            if (analyses.Count == 0)
            {
                _logger.LogWarning(
                    "Wardrobe analysis produced no items. Parse error: {Error}. Model said: {Raw}",
                    ParseFailure ?? "(none)", raw.Length > 600 ? raw[..600] : raw);

                // One retry. The same photo has been observed returning
                // nothing on one attempt and a full correct list on the next,
                // so failing the upload on a single hiccup is premature.
                raw = await _vision.AnalyzeAsync(req.ImageData, RetryPrompt,
                    "List every garment you can see, as JSON.");
                analyses = ParseAnalyses(raw);

                if (analyses.Count > 0)
                    _logger.LogInformation("Wardrobe analysis succeeded on retry");
            }
            if (analyses.Count > 0 && analyses[0].Error == "not-clothing")
                return BadRequest(new { message = "That doesn't look like clothing — try a clear photo of your garments." });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wardrobe item analysis failed — saving with defaults");
        }

        analyses = analyses.Where(a => string.IsNullOrEmpty(a.Error)).Take(6).ToList();

        // Saving a blank placeholder when analysis failed produced rows like
        // "Clothing item / other / no colours": useless for outfit matching,
        // counted against the wardrobe cap, and unexplained. Ask for a better
        // photo instead - the failure is usually the photo, and when it is a
        // transient model hiccup, retrying costs the user one tap.
        if (analyses.Count == 0)
        {
            return BadRequest(new
            {
                message = "Could not make out the clothes in that photo. " +
                          "A clear, well-lit shot of the garment laid flat or hung up works best - " +
                          "several pieces in one photo is fine.",
                retryable = true,
            });
        }

        // Respect the wardrobe cap across the whole batch.
        var capacity = Math.Max(0, MaxActiveItems - count);
        var toSave = analyses.Take(capacity).ToList();

        var now = DateTime.UtcNow.ToString("o");
        var bought = string.IsNullOrWhiteSpace(req.BoughtDate) ? null : req.BoughtDate!.Trim();
        var savedRows = new List<WardrobeItemRow>();
        foreach (var analysis in toSave)
        {
            var idRows = await _db.Database.SqlQuery<int>($@"
                INSERT INTO ""WardrobeItems"" (""UserId"", ""Name"", ""Category"", ""Colors"", ""Pattern"", ""Style"", ""Seasons"", ""Formality"", ""Condition"", ""CareTip"", ""ImageData"", ""BoughtDate"", ""Notes"", ""Status"", ""CreatedAt"")
                VALUES ({UserId}, {analysis.Name}, {analysis.Category}, {analysis.Colors}, {analysis.Pattern}, {analysis.Style}, {analysis.Seasons}, {analysis.Formality}, {analysis.Condition}, {analysis.CareTip}, {req.ImageData}, {bought}, {req.Notes ?? ""}, {"active"}, {now})
                RETURNING ""Id"" AS ""Value""").ToListAsync();
            var id = idRows[0];

            var row = (await _db.Database.SqlQuery<WardrobeItemRow>($@"
                SELECT ""Id"", ""Name"", ""Category"", ""Colors"", ""Pattern"", ""Style"", ""Seasons"", ""Formality"",
                       ""Condition"", ""CareTip"", ""ImageData"", ""BoughtDate"", ""Notes"", ""Status"", ""CreatedAt"", ""DiscardedAt""
                FROM ""WardrobeItems"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
            if (row != null) savedRows.Add(row);
        }

        return Ok(new
        {
            items = savedRows,
            detected = analyses.Count,
            skipped = analyses.Count - toSave.Count, // over the wardrobe cap
        });
    }

    [HttpPut("items/{id:int}")]
    public async Task<IActionResult> UpdateItem(int id, [FromBody] WardrobeUpdateRequest req)
    {
        await EnsureTablesAsync();
        var bought = string.IsNullOrWhiteSpace(req.BoughtDate) ? null : req.BoughtDate!.Trim();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""WardrobeItems"" SET
                ""Name"" = COALESCE({req.Name}, ""Name""),
                ""BoughtDate"" = {bought},
                ""Notes"" = COALESCE({req.Notes}, ""Notes"")
            WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Updated." });
    }

    [HttpDelete("items/{id:int}")]
    public async Task<IActionResult> DeleteItem(int id)
    {
        await EnsureTablesAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""WardrobeItems"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Deleted." });
    }

    // ── Responsible discard ────────────────────────────────────────────────────

    [HttpPost("items/{id:int}/discard-advice")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> DiscardAdvice(int id)
    {
        await EnsureTablesAsync();
        var item = (await _db.Database.SqlQuery<WardrobeItemRow>($@"
            SELECT ""Id"", ""Name"", ""Category"", ""Colors"", ""Pattern"", ""Style"", ""Seasons"", ""Formality"",
                   ""Condition"", ""CareTip"", ""ImageData"", ""BoughtDate"", ""Notes"", ""Status"", ""CreatedAt"", ""DiscardedAt""
            FROM ""WardrobeItems"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
        if (item == null) return NotFound(new { message = "Item not found." });

        var age = DescribeAge(item.BoughtDate);
        try
        {
            var discardSystem = @"You are a sustainable-fashion advisor. The user wants to responsibly let go of a garment.
Give 3-5 short, practical bullet points (plain text, one per line, starting with '- ') tailored to the item's condition and age:
- if it's in good shape: resale platforms and donation (charity shops, clothing banks, shelters)
- if it's worn: repair/upcycle ideas worth considering first
- if it's beyond saving: textile recycling (in-store take-back schemes like H&M/Zara, council textile banks) — NEVER general waste
Keep it friendly and specific to the garment type. No preamble, just the bullets.";
            var discardUser = $"Item: {item.Name} ({item.Category}). Condition from photo: {item.Condition}. Age: {age}. Notes: {item.Notes}";

            var advice = _vision.IsAvailable
                ? await _vision.CompleteTextAsync(discardSystem, discardUser, 500)
                : (await _ai.TestPromptAsync(new PromptTestRequest { PromptContent = discardSystem, UserInput = discardUser })).Result;
            return Ok(new { advice });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discard advice failed");
            return Ok(new { advice = "- If it's still wearable, donate it to a charity shop or clothing bank.\n- If it's worn out, take it to a textile recycling point (many clothing stores have take-back bins) — don't put it in general waste.\n- Good-condition pieces can be sold on resale platforms." });
        }
    }

    [HttpPost("items/{id:int}/discard")]
    public async Task<IActionResult> MarkDiscarded(int id)
    {
        await EnsureTablesAsync();
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""WardrobeItems"" SET ""Status""='discarded', ""DiscardedAt""={now}, ""ImageData""=NULL
            WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Marked as discarded." });
    }

    // ── Outfit suggestions ─────────────────────────────────────────────────────

    [HttpPost("outfits")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> SuggestOutfits([FromBody] WardrobeOutfitRequest req)
    {
        await EnsureTablesAsync();
        var items = await _db.Database.SqlQuery<WardrobeInventoryRow>($@"
            SELECT ""Id"", ""Name"", ""Category"", ""Colors"", ""Pattern"", ""Style"", ""Seasons"", ""Formality"", ""Condition"", ""BoughtDate"", ""Notes""
            FROM ""WardrobeItems"" WHERE ""UserId""={UserId} AND ""Status""='active' ORDER BY ""Id"" DESC").ToListAsync();

        var profile = (await _db.Database.SqlQuery<ProfileRow>(
            $@"SELECT ""BodyAdvice"", ""Presentation"", ""FavColors"", ""AvoidColors"", ""SkinTone"" FROM ""WardrobeProfiles"" WHERE ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
        var bodyAdvice = profile?.BodyAdvice;

        var prefsText = "";
        if (!string.IsNullOrWhiteSpace(profile?.Presentation))
            prefsText += $"\nDRESS STYLE: {profile.Presentation} (style presentation the user prefers to dress in — honour it regardless of garment gender labels)";
        if (!string.IsNullOrWhiteSpace(profile?.FavColors))
            prefsText += $"\nCOLOURS THEY LOVE WEARING: {profile.FavColors}";
        if (!string.IsNullOrWhiteSpace(profile?.AvoidColors))
            prefsText += $"\nCOLOURS TO AVOID: {profile.AvoidColors}";
        if (!string.IsNullOrWhiteSpace(profile?.SkinTone))
            prefsText += $"\nSKIN TONE: {profile.SkinTone} — pick garment colours that complement this skin tone (mention in 'why' when a colour was chosen for it)";

        // Live weather: from browser coordinates if shared, else from a typed city; free-text override wins.
        var weatherText = req.Weather;
        WardrobeWeather? weatherUsed = null;
        if (string.IsNullOrWhiteSpace(weatherText))
        {
            weatherUsed = await GetLiveWeatherAsync(req.Lat, req.Lon, req.City);
            if (weatherUsed != null) weatherText = weatherUsed.Summary;
        }

        var capsuleMode = items.Count < 2;

        var common = $@"OCCASION: {(string.IsNullOrWhiteSpace(req.Occasion) ? "everyday casual" : req.Occasion)}
WEATHER RIGHT NOW (dress for this — pick season-appropriate layers): {(string.IsNullOrWhiteSpace(weatherText) ? "not specified" : weatherText)}
EXTRA NOTES: {(string.IsNullOrWhiteSpace(req.Notes) ? "none" : req.Notes)}{prefsText}
{(string.IsNullOrWhiteSpace(bodyAdvice) ? "" : $"\nPERSONAL STYLING NOTES (from the user's body-shape profile — apply them):\n{bodyAdvice}")}";

        string systemPrompt;
        string user;
        if (capsuleMode)
        {
            // No (or barely any) catalogued clothes — style from preferences instead of blocking.
            systemPrompt = @"You are a personal stylist. The user hasn't catalogued their clothes yet, so suggest complete outfit ideas made of common garments they likely own or can easily buy.
Return ONLY JSON (no markdown fences):
{
  ""outfits"": [
    { ""title"": ""short outfit name"", ""pieces"": [""specific garment with colour, e.g. 'Olive chinos', 'White linen shirt'""], ""why"": ""1-2 sentences: why this works for the occasion and weather (colour pairing, silhouette, formality)"", ""tips"": ""one styling tip"" }
  ]
}
Give 2-3 outfits. STRICTLY honour the user's dress style (feminine / masculine / gender-neutral) and colour preferences if given, the occasion, and the CURRENT WEATHER (layers for cold, breathable for heat, rain-ready for rain).";
            user = common;
        }
        else
        {
            var inventory = string.Join("\n", items.Select(i =>
                $"#{i.Id}: {i.Name} | {i.Category} | colours: {i.Colors} | {i.Pattern} | {i.Style} | seasons: {i.Seasons} | {i.Formality} | condition: {i.Condition}"));
            systemPrompt = @"You are a personal stylist. Build outfits ONLY from the user's own wardrobe items (referenced by # id).
Return ONLY JSON (no markdown fences):
{
  ""outfits"": [
    { ""title"": ""short outfit name"", ""itemIds"": [1,2,3], ""why"": ""1-2 sentences: why these pieces work together for the occasion (colour pairing, silhouette, formality)"", ""tips"": ""one styling tip (tuck, roll, layer, accessorise)"" }
  ]
}
Give 2-3 outfits. Respect the occasion, the CURRENT WEATHER (layers for cold, breathable pieces for heat, rain-ready outerwear/shoes for rain), the user's dress style and colour preferences, and the personal styling notes. Pair colours deliberately (say why they work). If the wardrobe is missing something essential for the occasion or weather, add a ""missing"" string field to that outfit naming the one piece worth buying.";
            user = $@"{common}

WARDROBE (only use these items, referenced by their # id):
{inventory}";
        }

        try
        {
            var completion = _vision.IsAvailable
                ? await _vision.CompleteTextAsync(systemPrompt, user, 1800)
                : (await _ai.TestPromptAsync(new PromptTestRequest { PromptContent = systemPrompt, UserInput = user })).Result;

            var json = ExtractJson(completion);
            using var doc = JsonDocument.Parse(json);
            var outfits = doc.RootElement.TryGetProperty("outfits", out var o) ? o.Clone() : (JsonElement?)null;

            // Keep the last 5 suggestions per user so a closed tab doesn't lose a good look.
            try
            {
                var raw = outfits?.GetRawText() ?? "[]";
                var now = DateTime.UtcNow.ToString("o");
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO ""WardrobeOutfits"" (""UserId"", ""Occasion"", ""WeatherSummary"", ""Capsule"", ""OutfitsJson"", ""CreatedAt"")
                    VALUES ({UserId}, {req.Occasion ?? ""}, {weatherUsed?.Summary ?? req.Weather ?? ""}, {(capsuleMode ? 1 : 0)}, {raw}, {now})");
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    DELETE FROM ""WardrobeOutfits"" WHERE ""UserId""={UserId} AND ""Id"" NOT IN (
                        SELECT ""Id"" FROM ""WardrobeOutfits"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 5)");
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Saving outfit history failed"); }

            return Ok(new { outfits, weather = weatherUsed, capsule = capsuleMode });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Outfit suggestion failed");
            return StatusCode(500, new { message = "Could not build outfits right now — try again." });
        }
    }

    // ── Live weather (Open-Meteo, no key) ──────────────────────────────────────

    public sealed record WardrobeWeather(string Location, string Summary);

    private async Task<WardrobeWeather?> GetLiveWeatherAsync(double? lat, double? lon, string? city)
    {
        try
        {
            using var client = _http.CreateClient();
            var label = "your location";

            if ((lat == null || lon == null) && !string.IsNullOrWhiteSpace(city))
            {
                var geoUrl = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(city.Trim())}&count=1&language=en&format=json";
                using var geoDoc = JsonDocument.Parse(await client.GetStringAsync(geoUrl));
                if (!geoDoc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                    return null;
                var g = results[0];
                lat = g.GetProperty("latitude").GetDouble();
                lon = g.GetProperty("longitude").GetDouble();
                label = g.TryGetProperty("name", out var n) ? n.GetString() ?? city : city;
                if (g.TryGetProperty("country", out var c) && !string.IsNullOrEmpty(c.GetString())) label += $", {c.GetString()}";
            }
            else if (lat != null && lon != null && !string.IsNullOrWhiteSpace(city))
            {
                label = city.Trim();
            }

            if (lat == null || lon == null) return null;

            var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                      "&current=temperature_2m,apparent_temperature,precipitation,weather_code,wind_speed_10m" +
                      "&daily=temperature_2m_max,temperature_2m_min,precipitation_sum&forecast_days=1&timezone=auto";
            using var doc = JsonDocument.Parse(await client.GetStringAsync(url));
            var root = doc.RootElement;
            var cur = root.GetProperty("current");
            var daily = root.GetProperty("daily");

            var temp = Math.Round(cur.GetProperty("temperature_2m").GetDouble());
            var feels = Math.Round(cur.GetProperty("apparent_temperature").GetDouble());
            var code = cur.GetProperty("weather_code").GetInt32();
            var wind = Math.Round(cur.GetProperty("wind_speed_10m").GetDouble());
            var tmax = Math.Round(daily.GetProperty("temperature_2m_max")[0].GetDouble());
            var tmin = Math.Round(daily.GetProperty("temperature_2m_min")[0].GetDouble());
            var rain = daily.GetProperty("precipitation_sum")[0].GetDouble();

            var summary = $"{label}: now {temp}°C (feels {feels}°C), {WeatherCodeText(code)}, wind {wind} km/h. Today {tmin}–{tmax}°C" +
                          (rain >= 1 ? $", {Math.Round(rain, 1)}mm rain expected." : ", little to no rain expected.");
            return new WardrobeWeather(label, summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Wardrobe weather fetch failed");
            return null;
        }
    }

    private static string WeatherCodeText(int code) => code switch
    {
        0 => "clear",
        1 or 2 => "partly cloudy",
        3 => "overcast",
        45 or 48 => "foggy",
        51 or 53 or 55 or 56 or 57 => "drizzle",
        61 or 63 or 65 or 66 or 67 => "rain",
        71 or 73 or 75 or 77 => "snow",
        80 or 81 or 82 => "rain showers",
        85 or 86 => "snow showers",
        95 or 96 or 99 => "thunderstorms",
        _ => "mixed conditions"
    };

    // ── Outfit history (last 5 per user) ───────────────────────────────────────

    /// <summary>
    /// Builds a packing list for a trip FROM THE CLOTHES THE USER ACTUALLY OWNS.
    ///
    /// The point of doing this here rather than in Travel: a generic packing
    /// list ("5 shirts, 2 trousers") is a search result. Naming the specific
    /// garments in someone's wardrobe, and telling them what they are missing
    /// for the forecast, is something only this app can do - it needs the
    /// catalogue and the trip together.
    /// </summary>
    [HttpPost("packing-list")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> PackingList([FromBody] PackingListRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Destination))
            return BadRequest(new { message = "Where are you going?" });

        var nights = Math.Clamp(req.Nights <= 0 ? 3 : req.Nights, 1, 60);

        await EnsureTablesAsync();

        var items = await _db.Database.SqlQuery<WardrobeItemRow>($@"
            SELECT ""Id"", ""Name"", ""Category"", ""Colors"", ""Pattern"", ""Style"", ""Seasons"",
                   ""Formality"", ""Condition"", ""CareTip"", '' AS ""ImageData"", ""BoughtDate"",
                   ""Notes"", ""Status"", ""CreatedAt"", ""DiscardedAt""
            FROM ""WardrobeItems"" WHERE ""UserId""={UserId} AND ""Status""='active'
            ORDER BY ""Category""").ToListAsync(ct);

        if (items.Count == 0)
            return BadRequest(new { message = "Your wardrobe is empty - add a few pieces first." });

        if (!_ai.IsAvailable)
            return StatusCode(503, new { message = "AI is not available right now." });

        // Ids are given to the model so it can refer to real garments; anything
        // it names outside this list is dropped below.
        var catalogue = string.Join("\n", items.Select(i =>
            $"[{i.Id}] {i.Name} | {i.Category} | {i.Colors} | {i.Formality} | {i.Seasons}"));

        var system =
            "You are a practical packing assistant. Build a packing list for the trip using ONLY the " +
            "garments listed - refer to each by its [id]. Account for the destination's likely weather " +
            "in that month, the trip length, and any stated occasions. Do not invent garments. " +
            "Separately list what the wardrobe is MISSING for this trip, as plain descriptions.";

        var prompt =
            $"Trip: {req.Destination.Trim()}\n" +
            $"Nights: {nights}\n" +
            (string.IsNullOrWhiteSpace(req.Month) ? "" : $"Month: {req.Month!.Trim()}\n") +
            (string.IsNullOrWhiteSpace(req.Occasions) ? "" : $"Planned: {req.Occasions!.Trim()}\n") +
            $"\nWardrobe:\n{catalogue}\n\n" +
            "Return ONLY JSON:\n" +
            "{\"pack\":[{\"id\":123,\"why\":\"one short reason\"}]," +
            "\"missing\":[\"what to buy or borrow\"]," +
            "\"note\":\"one sentence on the weather and what it means for packing\"}";

        string raw;
        try
        {
            raw = (await _ai.TestPromptAsync(new PromptTestRequest
            {
                PromptContent = system,
                UserInput = prompt,
            })).Result ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Packing list generation failed for {Destination}", req.Destination);
            return StatusCode(500, new { message = "Could not build a packing list just now." });
        }

        var json = ExtractJson(raw);
        List<PackChoice> pack = new();
        List<string> missing = new();
        var note = "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("pack", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    if (!el.TryGetProperty("id", out var idEl)) continue;
                    if (!idEl.TryGetInt32(out var id)) continue;
                    pack.Add(new PackChoice(id, el.TryGetProperty("why", out var w) ? w.GetString() ?? "" : ""));
                }
            }

            if (root.TryGetProperty("missing", out var miss) && miss.ValueKind == JsonValueKind.Array)
                missing = miss.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();

            if (root.TryGetProperty("note", out var n)) note = n.GetString() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse the packing list response");
            return StatusCode(500, new { message = "Could not build a packing list just now." });
        }

        // Keep only real garments: a hallucinated id would render as a blank row.
        var byId = items.ToDictionary(i => i.Id);
        var chosen = pack
            .Where(p => byId.ContainsKey(p.Id))
            .Select(p => new
            {
                item = byId[p.Id],
                why = p.Why,
            })
            .ToList();

        if (chosen.Count == 0)
            return StatusCode(500, new { message = "Could not match any of your clothes to that trip." });

        return Ok(new
        {
            destination = req.Destination.Trim(),
            nights,
            note,
            pack = chosen,
            missing,
        });
    }

    [HttpGet("outfits/history")]
    public async Task<IActionResult> OutfitHistory()
    {
        await EnsureTablesAsync();
        var rows = await _db.Database.SqlQuery<OutfitHistoryRow>($@"
            SELECT ""Id"", ""Occasion"", ""WeatherSummary"", ""Capsule"", ""CreatedAt""
            FROM ""WardrobeOutfits"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 5").ToListAsync();
        return Ok(rows);
    }

    [HttpGet("outfits/history/{id:int}")]
    public async Task<IActionResult> OutfitHistoryDetail(int id)
    {
        await EnsureTablesAsync();
        var row = (await _db.Database.SqlQuery<OutfitHistoryDetailRow>($@"
            SELECT ""Id"", ""Occasion"", ""WeatherSummary"", ""Capsule"", ""OutfitsJson"", ""CreatedAt""
            FROM ""WardrobeOutfits"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
        if (row == null) return NotFound(new { message = "Saved look not found." });
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(row.OutfitsJson) ? "[]" : row.OutfitsJson!);
        return Ok(new
        {
            outfits = doc.RootElement.Clone(),
            weather = string.IsNullOrWhiteSpace(row.WeatherSummary) ? null : new { location = "", summary = row.WeatherSummary },
            capsule = row.Capsule == 1,
            occasion = row.Occasion
        });
    }

    [HttpDelete("outfits/history/{id:int}")]
    public async Task<IActionResult> DeleteOutfitHistory(int id)
    {
        await EnsureTablesAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""WardrobeOutfits"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Deleted." });
    }

    private sealed class OutfitHistoryRow
    {
        public int Id { get; set; }
        public string? Occasion { get; set; }
        public string? WeatherSummary { get; set; }
        public int Capsule { get; set; }
        public string? CreatedAt { get; set; }
    }

    private sealed class OutfitHistoryDetailRow
    {
        public int Id { get; set; }
        public string? Occasion { get; set; }
        public string? WeatherSummary { get; set; }
        public int Capsule { get; set; }
        public string? OutfitsJson { get; set; }
        public string? CreatedAt { get; set; }
    }

    // ── Body profile (photo analyzed, never stored) ────────────────────────────

    [HttpPost("body-analysis")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> AnalyzeBody([FromBody] WardrobeBodyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData))
            return BadRequest(new { message = "Add a photo first." });
        if (req.ImageData.Length > MaxImageChars)
            return BadRequest(new { message = "That photo is too large — please try again." });
        if (!_vision.IsAvailable)
            return StatusCode(503, new { message = "Image analysis is not available right now." });

        await EnsureTablesAsync();
        try
        {
            var advice = await _vision.AnalyzeAsync(req.ImageData,
                @"You are a kind, body-positive personal stylist. From this full-body photo, write 4-6 short styling notes (plain text, one per line, starting with '- ') to help the person dress well:
- silhouette and fit guidance (which cuts and fits will flatter — e.g. high-waisted, tailored, relaxed, structured shoulders)
- colour guidance based on visible skin/hair tone (which colour families complement them)
- proportion tips (where to add volume or definition, hem lengths)
STRICT RULES: be entirely positive and practical. NEVER comment on weight, size, attractiveness, or health. NEVER estimate measurements. No preamble — just the '- ' bullets.
If the photo does not show a person, return exactly: NOT-A-PERSON",
                "Give me styling notes for my body shape.");

            if (advice.Contains("NOT-A-PERSON"))
                return BadRequest(new { message = "That photo doesn't look like a full-body picture — try again." });

            var now = DateTime.UtcNow.ToString("o");
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""WardrobeProfiles"" (""UserId"", ""BodyAdvice"", ""UpdatedAt"") VALUES ({UserId}, {advice}, {now})
                ON CONFLICT (""UserId"") DO UPDATE SET ""BodyAdvice""=excluded.""BodyAdvice"", ""UpdatedAt""=excluded.""UpdatedAt""");
            return Ok(new { bodyAdvice = advice });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Body analysis failed");
            return StatusCode(500, new { message = "Could not analyze the photo — try again." });
        }
    }

    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        await EnsureTablesAsync();
        var p = (await _db.Database.SqlQuery<ProfileRow>(
            $@"SELECT ""BodyAdvice"", ""Presentation"", ""FavColors"", ""AvoidColors"", ""SkinTone"" FROM ""WardrobeProfiles"" WHERE ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
        return Ok(new
        {
            bodyAdvice = p?.BodyAdvice ?? "",
            presentation = p?.Presentation ?? "",
            favColors = p?.FavColors ?? "",
            avoidColors = p?.AvoidColors ?? "",
            skinTone = p?.SkinTone ?? ""
        });
    }

    [HttpPut("style")]
    public async Task<IActionResult> SaveStyle([FromBody] WardrobeStyleRequest req)
    {
        await EnsureTablesAsync();
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""WardrobeProfiles"" (""UserId"", ""Presentation"", ""FavColors"", ""AvoidColors"", ""SkinTone"", ""UpdatedAt"")
            VALUES ({UserId}, {req.Presentation ?? ""}, {req.FavColors ?? ""}, {req.AvoidColors ?? ""}, {req.SkinTone ?? ""}, {now})
            ON CONFLICT (""UserId"") DO UPDATE SET
                ""Presentation""=excluded.""Presentation"",
                ""FavColors""=excluded.""FavColors"",
                ""AvoidColors""=excluded.""AvoidColors"",
                ""SkinTone""=excluded.""SkinTone"",
                ""UpdatedAt""=excluded.""UpdatedAt""");
        return Ok(new { message = "Style preferences saved." });
    }

    private sealed class ProfileRow
    {
        public string? BodyAdvice { get; set; }
        public string? Presentation { get; set; }
        public string? FavColors { get; set; }
        public string? AvoidColors { get; set; }
        public string? SkinTone { get; set; }
    }

    [HttpDelete("profile")]
    public async Task<IActionResult> ClearProfile()
    {
        await EnsureTablesAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""WardrobeProfiles"" WHERE ""UserId""={UserId}");
        return Ok(new { message = "Styling profile cleared." });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string DescribeAge(string? boughtDate)
    {
        if (string.IsNullOrWhiteSpace(boughtDate) || !DateTime.TryParse(boughtDate, out var d)) return "unknown";
        var months = Math.Max(0, (int)((DateTime.UtcNow - d).TotalDays / 30.44));
        return months < 12 ? $"{months} months" : $"{months / 12} years {months % 12} months";
    }

    private static string ExtractJson(string raw)
    {
        var json = raw.Trim();
        if (json.StartsWith("```")) json = string.Join("\n", json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
        var s = json.IndexOf('{');
        var e = json.LastIndexOf('}');
        if (s >= 0 && e > s) json = json.Substring(s, e - s + 1);
        using var _ = JsonDocument.Parse(json); // validate before returning raw
        return json;
    }

    /// <summary>
    /// Parse the model's response into one analysis per garment. Accepts the
    /// current {"items":[…]} shape and the legacy single-object shape.
    /// </summary>
    /// <summary>Why the last parse failed, for diagnosis. Null when it worked.</summary>
    [ThreadStatic] private static string? ParseFailure;

    /// <summary>
    /// Second attempt, deliberately shorter than the first. When a long
    /// prompt yields unparseable output, trimming it to the essential fields
    /// is more effective than sending the same instructions again.
    /// </summary>
    private const string RetryPrompt = @"List every clothing item, shoe, bag or accessory in this photo.
Return ONLY JSON, no markdown fences:
{""items"":[{""name"":""short specific name"",""category"":""top|bottom|dress|outerwear|shoes|accessory|bag|other"",""colors"":[""lowercase colours""],""pattern"":""solid|striped|checked|floral|graphic|other"",""style"":""casual|classic|streetwear|sporty|bohemian|elegant|edgy"",""seasons"":[""summer|winter|spring|autumn|all-season""],""formality"":""casual|smart-casual|business|formal|athletic"",""condition"":""like-new|good|worn|heavily-worn"",""careTip"":""one care tip""}]}
Max 6 items, most prominent first. If there is no clothing, return {""error"":""not-clothing""}.";

    private static List<WardrobeAnalysis> ParseAnalyses(string raw)
    {
        var results = new List<WardrobeAnalysis>();
        try
        {
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Object) results.Add(AnalysisFrom(el));
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                results.Add(AnalysisFrom(root));
            }
        }
        catch (Exception ex)
        {
            // Swallowing this silently made a failure indistinguishable from a
            // photo with no clothes in it: the user got an empty "Clothing item"
            // row with no colour, category or care tip, and nothing to explain it.
            ParseFailure = ex.Message;
        }
        return results;
    }

    private static WardrobeAnalysis AnalysisFrom(JsonElement r)
    {
        string Str(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
        string Arr(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.Array
            ? string.Join(", ", el.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)))
            : "";
        return new WardrobeAnalysis
        {
            Error = Str("error"),
            Name = string.IsNullOrWhiteSpace(Str("name")) ? "Clothing item" : Str("name"),
            Category = string.IsNullOrWhiteSpace(Str("category")) ? "other" : Str("category"),
            Colors = Arr("colors"),
            Pattern = Str("pattern"),
            Style = Str("style"),
            Seasons = Arr("seasons"),
            Formality = Str("formality"),
            Condition = Str("condition"),
            CareTip = Str("careTip")
        };
    }

    private sealed class WardrobeAnalysis
    {
        public string Error { get; set; } = "";
        public string Name { get; set; } = "Clothing item";
        public string Category { get; set; } = "other";
        public string Colors { get; set; } = "";
        public string Pattern { get; set; } = "";
        public string Style { get; set; } = "";
        public string Seasons { get; set; } = "";
        public string Formality { get; set; } = "";
        public string Condition { get; set; } = "";
        public string CareTip { get; set; } = "";
    }

    public sealed class WardrobeItemRow
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Colors { get; set; }
        public string? Pattern { get; set; }
        public string? Style { get; set; }
        public string? Seasons { get; set; }
        public string? Formality { get; set; }
        public string? Condition { get; set; }
        public string? CareTip { get; set; }
        public string? ImageData { get; set; }
        public string? BoughtDate { get; set; }
        public string? Notes { get; set; }
        public string? Status { get; set; }
        public string? CreatedAt { get; set; }
        public string? DiscardedAt { get; set; }
    }

    public sealed class WardrobeInventoryRow
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Colors { get; set; }
        public string? Pattern { get; set; }
        public string? Style { get; set; }
        public string? Seasons { get; set; }
        public string? Formality { get; set; }
        public string? Condition { get; set; }
        public string? BoughtDate { get; set; }
        public string? Notes { get; set; }
    }
}

public class PackingListRequest
{
    public string? Destination { get; set; }
    public int Nights { get; set; }
    /// <summary>Month of travel, so the model can reason about the season.</summary>
    public string? Month { get; set; }
    /// <summary>Free text: "two dinners out, a hike, one client meeting".</summary>
    public string? Occasions { get; set; }
}

internal record PackChoice(int Id, string Why);

public class WardrobeAddRequest
{
    public string? ImageData { get; set; }
    public string? BoughtDate { get; set; }
    public string? Notes { get; set; }
}

public class WardrobeUpdateRequest
{
    public string? Name { get; set; }
    public string? BoughtDate { get; set; }
    public string? Notes { get; set; }
}

public class WardrobeOutfitRequest
{
    public string? Occasion { get; set; }
    public string? Weather { get; set; }
    public string? Notes { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public string? City { get; set; }
}

public class WardrobeBodyRequest
{
    public string? ImageData { get; set; }
}

public class WardrobeStyleRequest
{
    public string? Presentation { get; set; }
    public string? FavColors { get; set; }
    public string? AvoidColors { get; set; }
    public string? SkinTone { get; set; }
}
