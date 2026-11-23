using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using KitchenManager.Api.Data;
using KitchenManager.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace KitchenManager.Api.Controllers;

/// <summary>
/// Kitchen Intelligence: snap your fridge/groceries to build a pantry, get recipes you can
/// cook from what you have (tuned to cuisine, spice level, diet goal, and allergies), and
/// snap a plate for an estimated calorie/macro breakdown logged by day.
/// Calorie figures are rough estimates, NOT medical or dietary advice.
/// </summary>
[Authorize]
[ApiController]
[Route("api/kitchen")]
public class KitchenController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly AzureVisionService _vision;
    private readonly IAIService _ai;
    private readonly ILogger<KitchenController> _logger;

    private const int MaxImageChars = 3_000_000;
    private const int MaxPantryItems = 400;

    private static readonly string[] PantryCategories =
        { "produce", "dairy", "meat", "seafood", "grains", "legumes", "condiment", "spice", "baking", "frozen", "beverage", "other" };

    public KitchenController(PromptDbContext db, AzureVisionService vision, IAIService ai, ILogger<KitchenController> logger)
    {
        _db = db;
        _vision = vision;
        _ai = ai;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Table bootstrap (lazy, on the request connection — same pattern as Wardrobe/Receipts) ──

    private static bool _tablesEnsured;

    private async Task EnsureTablesAsync()
    {
        if (_tablesEnsured) return;
        try
        {
            var isSqlite = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
            var pk = isSqlite ? "INTEGER PRIMARY KEY AUTOINCREMENT" : "SERIAL PRIMARY KEY";
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""PantryItems"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Name"" TEXT, ""Category"" TEXT, ""CreatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_PantryItems_UserId"" ON ""PantryItems"" (""UserId"", ""Id"")");
            // Nutrition columns (added after initial rollout — SQLite has no ADD COLUMN IF NOT EXISTS)
            foreach (var (col, type) in new[] { ("CaloriesPer100g", "REAL"), ("Macros", "TEXT"), ("ServingNote", "TEXT"), ("Quantity", "TEXT") })
            {
                try { await _db.Database.ExecuteSqlRawAsync($@"ALTER TABLE ""PantryItems"" ADD COLUMN ""{col}"" {type}"); }
                catch { /* column already exists */ }
            }
            await _db.Database.ExecuteSqlRawAsync(@"CREATE TABLE IF NOT EXISTS ""KitchenPrefs"" (
                ""UserId"" TEXT PRIMARY KEY, ""Cuisines"" TEXT, ""SpiceLevel"" TEXT, ""DietGoal"" TEXT, ""Allergies"" TEXT, ""UpdatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""MealLog"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""LoggedDate"" TEXT, ""Title"" TEXT,
                ""Calories"" REAL, ""Protein"" REAL, ""Carbs"" REAL, ""Fat"" REAL,
                ""ItemsJson"" TEXT, ""HealthNote"" TEXT, ""ImageData"" TEXT, ""CreatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_MealLog_UserId"" ON ""MealLog"" (""UserId"", ""LoggedDate"")");
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""ShoppingList"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Name"" TEXT, ""Checked"" INTEGER NOT NULL DEFAULT 0, ""CreatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_ShoppingList_UserId"" ON ""ShoppingList"" (""UserId"", ""Id"")");
            _tablesEnsured = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EnsureKitchenTables failed"); }
    }

    // ── Preferences ────────────────────────────────────────────────────────────

    [HttpGet("prefs")]
    public async Task<IActionResult> GetPrefs()
    {
        await EnsureTablesAsync();
        var p = (await _db.Database.SqlQuery<KitchenPrefsRow>(
            $@"SELECT ""Cuisines"", ""SpiceLevel"", ""DietGoal"", ""Allergies"" FROM ""KitchenPrefs"" WHERE ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
        return Ok(new
        {
            cuisines = p?.Cuisines ?? "",
            spiceLevel = p?.SpiceLevel ?? "",
            dietGoal = p?.DietGoal ?? "",
            allergies = p?.Allergies ?? ""
        });
    }

    [HttpPut("prefs")]
    public async Task<IActionResult> SavePrefs([FromBody] KitchenPrefsRequest req)
    {
        await EnsureTablesAsync();
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""KitchenPrefs"" (""UserId"", ""Cuisines"", ""SpiceLevel"", ""DietGoal"", ""Allergies"", ""UpdatedAt"")
            VALUES ({UserId}, {req.Cuisines ?? ""}, {req.SpiceLevel ?? ""}, {req.DietGoal ?? ""}, {req.Allergies ?? ""}, {now})
            ON CONFLICT (""UserId"") DO UPDATE SET
                ""Cuisines""=excluded.""Cuisines"",
                ""SpiceLevel""=excluded.""SpiceLevel"",
                ""DietGoal""=excluded.""DietGoal"",
                ""Allergies""=excluded.""Allergies"",
                ""UpdatedAt""=excluded.""UpdatedAt""");
        return Ok(new { message = "Saved." });
    }

    // ── Pantry ─────────────────────────────────────────────────────────────────

    [HttpGet("pantry")]
    public async Task<IActionResult> GetPantry()
    {
        await EnsureTablesAsync();
        var rows = await _db.Database.SqlQuery<PantryRow>($@"
            SELECT ""Id"", ""Name"", ""Category"", ""CaloriesPer100g"", ""Macros"", ""ServingNote"", ""Quantity"", ""CreatedAt"" FROM ""PantryItems""
            WHERE ""UserId""={UserId} ORDER BY ""Category"", ""Name""").ToListAsync();
        return Ok(rows);
    }

    [HttpPost("pantry/snap")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> SnapPantry([FromBody] KitchenImageRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData))
            return BadRequest(new { message = "Add a photo of your fridge, pantry, or groceries." });
        if (req.ImageData.Length > MaxImageChars)
            return BadRequest(new { message = "That photo is too large — please try again." });
        if (!_vision.IsAvailable)
            return StatusCode(503, new { message = "Image analysis is not available right now." });

        await EnsureTablesAsync();

        string raw;
        try
        {
            raw = await _vision.AnalyzeAsync(req.ImageData,
                $@"You identify food ingredients in a photo of a fridge, pantry, counter, or groceries.
Return ONLY JSON (no markdown fences):
{{ ""items"": [ {{ ""name"": ""generic food name, lowercase (e.g. eggs, tomatoes, cheddar cheese, olive oil)"", ""category"": ""one of: {string.Join(" | ", PantryCategories)}"" }} ] }}
List each distinct visible food item once. Use simple generic names, not brands. Max 40 items. Ignore non-food objects.
If no food is visible, return {{ ""items"": [] }}.",
                "List the food items you can see.", 1200);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pantry snap analysis failed");
            return StatusCode(500, new { message = "Could not read that photo — try a clearer one." });
        }

        var existing = (await _db.Database.SqlQuery<string>(
            $@"SELECT LOWER(""Name"") AS ""Value"" FROM ""PantryItems"" WHERE ""UserId""={UserId}").ToListAsync())
            .ToHashSet();

        var added = new List<object>();
        var now = DateTime.UtcNow.ToString("o");
        try
        {
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                var count = existing.Count;
                foreach (var el in items.EnumerateArray())
                {
                    if (count >= MaxPantryItems) break;
                    var name = (el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "").Trim();
                    if (string.IsNullOrWhiteSpace(name) || existing.Contains(name.ToLowerInvariant())) continue;
                    var cat = (el.TryGetProperty("category", out var c) ? c.GetString() ?? "other" : "other").ToLowerInvariant();
                    if (!PantryCategories.Contains(cat)) cat = "other";
                    await _db.Database.ExecuteSqlInterpolatedAsync($@"
                        INSERT INTO ""PantryItems"" (""UserId"", ""Name"", ""Category"", ""CreatedAt"") VALUES ({UserId}, {name}, {cat}, {now})");
                    existing.Add(name.ToLowerInvariant());
                    added.Add(new { name, category = cat });
                    count++;
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Pantry snap parse failed"); }

        if (added.Count == 0)
            return Ok(new { added = Array.Empty<object>(), message = "No new food items spotted — try a clearer photo or add items manually." });

        var all = await _db.Database.SqlQuery<PantryRow>($@"
            SELECT ""Id"", ""Name"", ""Category"", ""CaloriesPer100g"", ""Macros"", ""ServingNote"", ""Quantity"", ""CreatedAt"" FROM ""PantryItems""
            WHERE ""UserId""={UserId} ORDER BY ""Category"", ""Name""").ToListAsync();
        return Ok(new { added, pantry = all });
    }

    /// <summary>
    /// Reads a grocery receipt: stocks the pantry AND records what it cost.
    ///
    /// One photo, two answers. A receipt already carries both facts, so making
    /// the user photograph it once for Kitchen and again for Bill Tracker is
    /// asking them to do the app's work. The spend is returned rather than
    /// written straight to Bills: shopping is not a recurring bill, and silently
    /// creating financial records from a pantry action would be a surprise.
    /// </summary>
    [HttpPost("pantry/receipt")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> ScanReceipt([FromBody] KitchenImageRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData))
            return BadRequest(new { message = "No photo supplied." });
        if (!_vision.IsAvailable)
            return StatusCode(503, new { message = "Image analysis is not available right now." });

        await EnsureTablesAsync();

        string raw;
        try
        {
            raw = await _vision.AnalyzeAsync(req.ImageData,
                @"This is a grocery receipt. Return ONLY JSON (no markdown fences):
{
  ""merchant"": ""shop name, or empty"",
  ""total"": 0.00,
  ""currency"": ""code or symbol as printed, or empty"",
  ""date"": ""yyyy-MM-dd if shown, else empty"",
  ""items"": [{ ""name"": ""food item, generic name without brand or size"", ""quantity"": ""as printed, or empty"" }]
}
List only FOOD and DRINK items - skip bags, loyalty lines, discounts and totals. Use the generic name (""tomatoes"", not ""ORG VINE TOM 400G""). If this is not a receipt, return {""error"":""not-receipt""}.",
                "Read this grocery receipt.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receipt scan failed");
            return StatusCode(500, new { message = "Could not read that receipt." });
        }

        var json = ExtractJson(raw);
        string merchant = "", currency = "", date = "";
        double total = 0;
        var names = new List<(string Name, string Qty)>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var err) && err.GetString() == "not-receipt")
                return BadRequest(new { message = "That doesn't look like a receipt - try a clear photo of the whole slip." });

            if (root.TryGetProperty("merchant", out var m)) merchant = m.GetString() ?? "";
            if (root.TryGetProperty("currency", out var c)) currency = c.GetString() ?? "";
            if (root.TryGetProperty("date", out var d)) date = d.GetString() ?? "";
            if (root.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number)
                total = t.GetDouble();

            if (root.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var name = el.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
                    if (name.Length == 0) continue;
                    var qty = el.TryGetProperty("quantity", out var q) ? (q.GetString() ?? "").Trim() : "";
                    names.Add((name, qty));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse the receipt response");
            return StatusCode(500, new { message = "Could not read that receipt." });
        }

        if (names.Count == 0)
            return BadRequest(new { message = "No food items were readable on that receipt." });

        // Stock the pantry, skipping what is already there: a weekly shop
        // repeats most of the same items, and duplicate rows make the
        // "what can I cook?" match noisier without adding anything.
        var existing = (await _db.Database.SqlQuery<string>(
            $@"SELECT ""Name"" AS ""Value"" FROM ""PantryItems"" WHERE ""UserId""={UserId}")
            .ToListAsync(ct))
            .Select(x => x.ToLowerInvariant())
            .ToHashSet();

        var now = DateTime.UtcNow.ToString("o");
        var added = new List<string>();

        foreach (var (name, qty) in names)
        {
            if (existing.Contains(name.ToLowerInvariant())) continue;
            await _db.Database.ExecuteSqlAsync(
                $@"INSERT INTO ""PantryItems"" (""UserId"", ""Name"", ""Quantity"", ""CreatedAt"")
                   VALUES ({UserId}, {name}, {qty}, {now})", ct);
            existing.Add(name.ToLowerInvariant());
            added.Add(name);
        }

        return Ok(new
        {
            added,
            skipped = names.Count - added.Count,
            // Offered, not recorded. The client asks before writing to Bills.
            spend = total > 0 ? new { merchant, total, currency, date } : null,
        });
    }

    [HttpPost("pantry")]
    public async Task<IActionResult> AddPantryItem([FromBody] PantryAddRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest(new { message = "Enter an item." });
        await EnsureTablesAsync();
        var cat = (req.Category ?? "other").ToLowerInvariant();
        if (!PantryCategories.Contains(cat)) cat = "other";
        var name = req.Name.Trim();
        var now = DateTime.UtcNow.ToString("o");
        var qty = string.IsNullOrWhiteSpace(req.Quantity) ? null : req.Quantity!.Trim();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""PantryItems"" (""UserId"", ""Name"", ""Category"", ""CaloriesPer100g"", ""Macros"", ""ServingNote"", ""Quantity"", ""CreatedAt"")
            VALUES ({UserId}, {name}, {cat}, {req.CaloriesPer100g}, {req.Macros}, {req.ServingNote}, {qty}, {now})");
        var row = (await _db.Database.SqlQuery<PantryRow>($@"
            SELECT ""Id"", ""Name"", ""Category"", ""CaloriesPer100g"", ""Macros"", ""ServingNote"", ""Quantity"", ""CreatedAt"" FROM ""PantryItems""
            WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 1").ToListAsync()).FirstOrDefault();
        return Ok(row);
    }

    // Look up a food by name: canonical name, category, and nutrition facts for the add-item modal.
    [HttpPost("pantry/lookup")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> LookupFood([FromBody] PantryLookupRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Type a food or ingredient to look up." });
        if (!_vision.IsAvailable)
            return StatusCode(503, new { message = "Lookup is not available right now." });

        try
        {
            var raw = await _vision.CompleteTextAsync(
                $@"You are a nutrition reference for common foods and ingredients. Return ONLY JSON (no markdown fences):
{{
  ""name"": ""canonical item name, lowercase (e.g. 'rolled oats', 'cheddar cheese')"",
  ""category"": ""one of: {string.Join(" | ", PantryCategories)}"",
  ""caloriesPer100g"": 0,
  ""protein"": 0,
  ""carbs"": 0,
  ""fat"": 0,
  ""servingNote"": ""typical serving and its calories, e.g. '1 medium banana (118g) ≈ 105 kcal'"",
  ""healthNote"": ""one short, factual note (fibre, sugar, sodium, good source of...)""
}}
Macros are grams per 100g; calories are kcal per 100g. Use typical published nutrition values.
If the input is not a food or drink, return {{""error"": ""not-food""}}.",
                $"Look up: {req.Name.Trim()}", 500);

            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var errEl) && errEl.GetString() == "not-food")
                return BadRequest(new { message = $"\"{req.Name.Trim()}\" doesn't look like a food item." });

            string Str(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            double Num(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0;
            var cat = Str("category").ToLowerInvariant();
            return Ok(new
            {
                name = string.IsNullOrWhiteSpace(Str("name")) ? req.Name.Trim().ToLowerInvariant() : Str("name"),
                category = PantryCategories.Contains(cat) ? cat : "other",
                caloriesPer100g = Math.Round(Num("caloriesPer100g")),
                protein = Math.Round(Num("protein"), 1),
                carbs = Math.Round(Num("carbs"), 1),
                fat = Math.Round(Num("fat"), 1),
                servingNote = Str("servingNote"),
                healthNote = Str("healthNote")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pantry lookup failed for {Name}", req.Name);
            return StatusCode(500, new { message = "Lookup failed — you can still add the item without details." });
        }
    }

    [HttpDelete("pantry/{id:int}")]
    public async Task<IActionResult> DeletePantryItem(int id)
    {
        await EnsureTablesAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""PantryItems"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Removed." });
    }

    [HttpDelete("pantry")]
    public async Task<IActionResult> ClearPantry()
    {
        await EnsureTablesAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""PantryItems"" WHERE ""UserId""={UserId}");
        return Ok(new { message = "Pantry cleared." });
    }

    // ── Recipe suggestions ─────────────────────────────────────────────────────

    [HttpPost("recipes")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Recipes()
    {
        await EnsureTablesAsync();
        var pantry = await _db.Database.SqlQuery<PantryRow>($@"
            SELECT ""Id"", ""Name"", ""Category"", ""CaloriesPer100g"", ""Macros"", ""ServingNote"", ""Quantity"", ""CreatedAt"" FROM ""PantryItems"" WHERE ""UserId""={UserId} ORDER BY ""Name""").ToListAsync();
        if (pantry.Count < 2)
            return BadRequest(new { message = "Add a few ingredients first — snap your fridge or groceries." });

        var prefs = (await _db.Database.SqlQuery<KitchenPrefsRow>(
            $@"SELECT ""Cuisines"", ""SpiceLevel"", ""DietGoal"", ""Allergies"" FROM ""KitchenPrefs"" WHERE ""UserId""={UserId}").ToListAsync()).FirstOrDefault();

        // Reuse the traveller's saved dietary preferences as a hint when kitchen allergies are empty.
        var dietaryHint = prefs?.Allergies ?? "";
        if (string.IsNullOrWhiteSpace(dietaryHint))
        {
            try
            {
                dietaryHint = (await _db.Database.SqlQuery<string?>(
                    $@"SELECT ""DietaryPreferences"" AS ""Value"" FROM ""UserTravelPrefs"" WHERE ""UserId""={UserId}").ToListAsync()).FirstOrDefault() ?? "";
            }
            catch { /* table may not exist yet */ }
        }

        var ingredients = string.Join(", ", pantry.Select(p =>
            string.IsNullOrWhiteSpace(p.Quantity) ? p.Name : $"{p.Name} ({p.Quantity})"));
        var user = $@"INGREDIENTS I HAVE: {ingredients}
CUISINE PREFERENCES: {(string.IsNullOrWhiteSpace(prefs?.Cuisines) ? "any" : prefs!.Cuisines)}
SPICE TOLERANCE: {(string.IsNullOrWhiteSpace(prefs?.SpiceLevel) ? "no preference" : prefs!.SpiceLevel)}
DIET / HEALTH GOAL: {(string.IsNullOrWhiteSpace(prefs?.DietGoal) ? "balanced" : prefs!.DietGoal)}
ALLERGIES / MUST AVOID: {(string.IsNullOrWhiteSpace(dietaryHint) ? "none" : dietaryHint)}";

        var system = @"You are a practical home-cooking assistant. Suggest real recipes the user can cook from the ingredients they have.
Return ONLY JSON (no markdown fences):
{
  ""recipes"": [
    {
      ""title"": ""dish name"",
      ""cuisine"": ""cuisine label"",
      ""minutes"": 30,
      ""spice"": ""mild | medium | hot"",
      ""calories"": 550,
      ""healthNote"": ""one short note tied to their diet goal (e.g. 'high in protein, light on carbs')"",
      ""uses"": [""ingredients they have that this recipe uses""],
      ""missing"": [""ingredients they'd need to buy — empty array if none""],
      ""steps"": [""brief step"", ""brief step""]
    }
  ]
}
Rules:
- Give 4-6 recipes. List recipes that need NO extra shopping (empty missing[]) FIRST, then ones needing 1-3 common extras.
- STRICTLY respect allergies/must-avoid — never include those ingredients.
- Match the spice tolerance and the diet/health goal (adjust portions/ingredients to fit the goal and note it in healthNote).
- Honour cuisine preferences when possible. Keep steps to 4-7 concise lines. Calories are a rough per-serving estimate.";

        try
        {
            var completion = await _vision.CompleteTextAsync(system, user, 2200);
            var json = ExtractJson(completion);
            using var doc = JsonDocument.Parse(json);
            var recipes = doc.RootElement.TryGetProperty("recipes", out var r) ? r.Clone() : (JsonElement?)null;
            string? firstDish = recipes is JsonElement rEl && rEl.ValueKind == JsonValueKind.Array && rEl.GetArrayLength() > 0
                && rEl[0].TryGetProperty("title", out var t0) ? t0.GetString() : null;
            var recipeTitle = string.IsNullOrWhiteSpace(firstDish)
                ? "Recipe ideas from my pantry"
                : $"Recipe ideas — {firstDish} & more";
            return Ok(new { recipes });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recipe suggestion failed");
            return StatusCode(500, new { message = "Could not build recipes right now — try again." });
        }
    }

    // ── Shopping list ──────────────────────────────────────────────────────────

    [HttpGet("shopping")]
    public async Task<IActionResult> GetShopping()
    {
        await EnsureTablesAsync();
        var rows = await _db.Database.SqlQuery<ShoppingRow>($@"
            SELECT ""Id"", ""Name"", ""Checked"", ""CreatedAt"" FROM ""ShoppingList""
            WHERE ""UserId""={UserId} ORDER BY ""Checked"", ""Id""").ToListAsync();
        return Ok(rows);
    }

    [HttpPost("shopping")]
    public async Task<IActionResult> AddShopping([FromBody] ShoppingAddRequest req)
    {
        await EnsureTablesAsync();
        // Accept a single name or a batch (e.g. a recipe's missing ingredients).
        var names = (req.Names != null && req.Names.Count > 0)
            ? req.Names
            : (string.IsNullOrWhiteSpace(req.Name) ? new List<string>() : new List<string> { req.Name! });
        var cleaned = names.Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cleaned.Count == 0) return BadRequest(new { message = "Enter an item." });

        var existing = (await _db.Database.SqlQuery<string>(
            $@"SELECT LOWER(""Name"") AS ""Value"" FROM ""ShoppingList"" WHERE ""UserId""={UserId}").ToListAsync()).ToHashSet();
        var now = DateTime.UtcNow.ToString("o");
        foreach (var name in cleaned)
        {
            if (existing.Contains(name.ToLowerInvariant())) continue;
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""ShoppingList"" (""UserId"", ""Name"", ""Checked"", ""CreatedAt"") VALUES ({UserId}, {name}, {0}, {now})");
            existing.Add(name.ToLowerInvariant());
        }

        var all = await _db.Database.SqlQuery<ShoppingRow>($@"
            SELECT ""Id"", ""Name"", ""Checked"", ""CreatedAt"" FROM ""ShoppingList""
            WHERE ""UserId""={UserId} ORDER BY ""Checked"", ""Id""").ToListAsync();
        return Ok(all);
    }

    [HttpPut("shopping/{id:int}")]
    public async Task<IActionResult> ToggleShopping(int id, [FromBody] ShoppingToggleRequest req)
    {
        await EnsureTablesAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""ShoppingList"" SET ""Checked""={(req.Checked ? 1 : 0)} WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Updated." });
    }

    [HttpDelete("shopping/{id:int}")]
    public async Task<IActionResult> DeleteShopping(int id)
    {
        await EnsureTablesAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""ShoppingList"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Removed." });
    }

    // Clear all ticked-off items (or everything when all=true).
    [HttpPost("shopping/clear")]
    public async Task<IActionResult> ClearShopping([FromQuery] bool all = false)
    {
        await EnsureTablesAsync();
        if (all)
            await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""ShoppingList"" WHERE ""UserId""={UserId}");
        else
            await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""ShoppingList"" WHERE ""UserId""={UserId} AND ""Checked""={1}");
        return Ok(new { message = "Cleared." });
    }

    // ── Plate check (calorie/macro estimate) ───────────────────────────────────

    [HttpPost("plate")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Plate([FromBody] PlateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData))
            return BadRequest(new { message = "Add a photo of your plate." });
        if (req.ImageData.Length > MaxImageChars)
            return BadRequest(new { message = "That photo is too large — please try again." });
        if (!_vision.IsAvailable)
            return StatusCode(503, new { message = "Image analysis is not available right now." });

        await EnsureTablesAsync();

        string raw;
        try
        {
            raw = await _vision.AnalyzeAsync(req.ImageData,
                @"You estimate the nutrition of a meal from a photo. Return ONLY JSON (no markdown fences):
{
  ""title"": ""short meal name"",
  ""dishes"": [ { ""name"": ""component"", ""calories"": 0 } ],
  ""calories"": 0,
  ""protein"": 0,
  ""carbs"": 0,
  ""fat"": 0,
  ""confidence"": ""low | medium | high"",
  ""healthNote"": ""one friendly, non-judgmental tip (a healthier swap or a balance observation)""
}
Estimate typical portion sizes from the photo. Macros (protein/carbs/fat) are in grams; calories in kcal.
If the photo is not food, return { ""error"": ""not-food"" }.
These are rough estimates, not medical or dietary advice.",
                "Estimate the calories and macros of this meal.", 900);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plate analysis failed");
            return StatusCode(500, new { message = "Could not read that plate — try a clearer photo." });
        }

        string title = "Meal", itemsJson = "[]", healthNote = "", confidence = "low";
        double calories = 0, protein = 0, carbs = 0, fat = 0;
        try
        {
            var json = ExtractJson(raw);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var errEl) && errEl.GetString() == "not-food")
                return BadRequest(new { message = "That doesn't look like food — try a clear photo of your plate." });
            string Str(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            double Num(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetDouble() : 0;
            title = string.IsNullOrWhiteSpace(Str("title")) ? "Meal" : Str("title");
            healthNote = Str("healthNote");
            confidence = string.IsNullOrWhiteSpace(Str("confidence")) ? "low" : Str("confidence");
            calories = Math.Round(Num("calories"));
            protein = Math.Round(Num("protein"));
            carbs = Math.Round(Num("carbs"));
            fat = Math.Round(Num("fat"));
            if (r.TryGetProperty("dishes", out var dishes) && dishes.ValueKind == JsonValueKind.Array)
                itemsJson = dishes.GetRawText();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Plate parse failed"); }

        int? savedId = null;
        if (req.Log)
        {
            var date = System.Text.RegularExpressions.Regex.IsMatch(req.Date ?? "", @"^\d{4}-\d{2}-\d{2}$")
                ? req.Date! : DateTime.UtcNow.ToString("yyyy-MM-dd");
            var now = DateTime.UtcNow.ToString("o");
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""MealLog"" (""UserId"", ""LoggedDate"", ""Title"", ""Calories"", ""Protein"", ""Carbs"", ""Fat"", ""ItemsJson"", ""HealthNote"", ""ImageData"", ""CreatedAt"")
                VALUES ({UserId}, {date}, {title}, {calories}, {protein}, {carbs}, {fat}, {itemsJson}, {healthNote}, {req.ImageData}, {now})");
            savedId = (await _db.Database.SqlQuery<int>($@"
                SELECT ""Id"" AS ""Value"" FROM ""MealLog"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 1").ToListAsync()).FirstOrDefault();
        }


        return Ok(new { id = savedId, title, calories, protein, carbs, fat, confidence, healthNote, dishesJson = itemsJson, logged = req.Log });
    }

    [HttpGet("meals")]
    public async Task<IActionResult> Meals([FromQuery] string? date)
    {
        await EnsureTablesAsync();
        var day = System.Text.RegularExpressions.Regex.IsMatch(date ?? "", @"^\d{4}-\d{2}-\d{2}$")
            ? date! : DateTime.UtcNow.ToString("yyyy-MM-dd");
        var rows = await _db.Database.SqlQuery<MealRow>($@"
            SELECT ""Id"", ""LoggedDate"", ""Title"", ""Calories"", ""Protein"", ""Carbs"", ""Fat"", ""ItemsJson"", ""HealthNote"", ""ImageData"", ""CreatedAt""
            FROM ""MealLog"" WHERE ""UserId""={UserId} AND ""LoggedDate""={day} ORDER BY ""Id"" DESC").ToListAsync();
        return Ok(rows);
    }

    [HttpDelete("meals/{id:int}")]
    public async Task<IActionResult> DeleteMeal(int id)
    {
        await EnsureTablesAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""MealLog"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Deleted." });
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string ExtractJson(string raw)
    {
        var json = raw.Trim();
        if (json.StartsWith("```")) json = string.Join("\n", json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
        var s = json.IndexOf('{');
        var e = json.LastIndexOf('}');
        if (s >= 0 && e > s) json = json.Substring(s, e - s + 1);
        using var _ = JsonDocument.Parse(json);
        return json;
    }

    public sealed class PantryRow
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Category { get; set; }
        public double? CaloriesPer100g { get; set; }
        public string? Macros { get; set; }
        public string? ServingNote { get; set; }
        public string? Quantity { get; set; }
        public string? CreatedAt { get; set; }
    }

    public sealed class MealRow
    {
        public int Id { get; set; }
        public string? LoggedDate { get; set; }
        public string? Title { get; set; }
        public double? Calories { get; set; }
        public double? Protein { get; set; }
        public double? Carbs { get; set; }
        public double? Fat { get; set; }
        public string? ItemsJson { get; set; }
        public string? HealthNote { get; set; }
        public string? ImageData { get; set; }
        public string? CreatedAt { get; set; }
    }

    private sealed class KitchenPrefsRow
    {
        public string? Cuisines { get; set; }
        public string? SpiceLevel { get; set; }
        public string? DietGoal { get; set; }
        public string? Allergies { get; set; }
    }

    public sealed class ShoppingRow
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public int Checked { get; set; }
        public string? CreatedAt { get; set; }
    }
}

public class KitchenImageRequest { public string? ImageData { get; set; } }

public class PantryAddRequest
{
    public string? Name { get; set; }
    public string? Category { get; set; }
    public double? CaloriesPer100g { get; set; }
    public string? Macros { get; set; }
    public string? ServingNote { get; set; }
    public string? Quantity { get; set; }
}

public class PantryLookupRequest { public string? Name { get; set; } }

public class KitchenPrefsRequest
{
    public string? Cuisines { get; set; }
    public string? SpiceLevel { get; set; }
    public string? DietGoal { get; set; }
    public string? Allergies { get; set; }
}

public class PlateRequest
{
    public string? ImageData { get; set; }
    public bool Log { get; set; }
    public string? Date { get; set; }
}

public class ShoppingAddRequest
{
    public string? Name { get; set; }
    public List<string>? Names { get; set; }
}

public class ShoppingToggleRequest { public bool Checked { get; set; } }
