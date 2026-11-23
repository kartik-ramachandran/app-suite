using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using BookReader.Api.Data;
using BookReader.Api.Models;
using BookReader.Api.Services;

namespace BookReader.Api.Controllers;

/// <summary>
/// The recipe library, and the route a cookbook takes into it.
///
/// Kitchen previously had no recipe storage at all — it generated suggestions
/// from the pantry and discarded them. That is why importing a cookbook is
/// worth doing: it turns a one-shot suggestion tool into a library the user
/// chose, and "what can I cook tonight?" can finally answer from real recipes.
/// </summary>
[ApiController]
[Route("api/recipes")]
[Authorize]
public class RecipesController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly BookExtractionService _extraction;
    private readonly BookStorageService _storage;
    private readonly IngredientNormaliser _normaliser;
    private readonly IAIService _ai;
    private readonly ILogger<RecipesController> _logger;

    public RecipesController(
        PromptDbContext db,
        BookExtractionService extraction,
        BookStorageService storage,
        IngredientNormaliser normaliser,
        IAIService ai,
        ILogger<RecipesController> logger)
    {
        _db = db;
        _extraction = extraction;
        _storage = storage;
        _normaliser = normaliser;
        _ai = ai;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Library ───────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? search, [FromQuery] string? course)
    {
        var query = _db.Recipes.Where(r => r.UserId == UserId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            query = query.Where(r =>
                r.Title.ToLower().Contains(s) ||
                r.IngredientNames.Contains(s) ||
                (r.Cuisine != null && r.Cuisine.ToLower().Contains(s)));
        }

        if (!string.IsNullOrWhiteSpace(course))
            query = query.Where(r => r.Course == course);

        // Counting newlines cannot be translated to SQL, so the rows come back
        // first and the count is done in memory. Capped at 300, so this is a
        // trivial amount of work — and far cheaper than a column that would
        // have to be kept in step with IngredientNames on every write.
        var rows = await query
            .OrderByDescending(r => r.IsFavourite)
            .ThenByDescending(r => r.CreatedAt)
            .Take(300)
            .Select(r => new
            {
                r.Id, r.Title, r.Description, r.Cuisine, r.Course,
                r.Servings, r.TotalMinutes, r.SourceBookTitle,
                r.IsFavourite, r.TimesCooked, r.LastCookedAt,
                r.IngredientNames,
            })
            .ToListAsync();

        var recipes = rows.Select(r => new
        {
            r.Id, r.Title, r.Description, r.Cuisine, r.Course,
            r.Servings, r.TotalMinutes, r.SourceBookTitle,
            r.IsFavourite, r.TimesCooked, r.LastCookedAt,
            IngredientCount = string.IsNullOrEmpty(r.IngredientNames)
                ? 0
                : r.IngredientNames.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
        });

        return Ok(recipes);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var r = await _db.Recipes.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (r is null) return NotFound(new { error = "Recipe not found." });

        return Ok(new
        {
            r.Id, r.Title, r.Description, r.Cuisine, r.Course,
            r.Servings, r.TotalMinutes, r.SourceBookTitle, r.SourceBookId,
            r.IsFavourite, r.TimesCooked, r.LastCookedAt,
            Ingredients = Parse<List<RecipeIngredient>>(r.IngredientsJson) ?? new(),
            Steps = Parse<List<string>>(r.StepsJson) ?? new(),
            Dietary = Parse<List<string>>(r.DietaryJson) ?? new(),
        });
    }

    // ── Import from a book ────────────────────────────────────────────────────

    /// <summary>
    /// Turns an already-uploaded cookbook into recipes.
    ///
    /// Opt-in: nothing is imported until the user accepts the offer. Silently
    /// filling someone's Kitchen from a misclassified book would be worse than
    /// not offering at all, and this is an expensive AI pass either way.
    /// </summary>
    [HttpPost("import-from-book/{bookId:guid}")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> ImportFromBook(Guid bookId, CancellationToken ct)
    {
        var book = await _db.ReaderBooks
            .FirstOrDefaultAsync(b => b.Id == bookId && b.UserId == UserId && b.DeletedAt == null, ct);

        if (book is null) return NotFound(new { error = "Book not found." });
        if (book.Status != "ready")
            return BadRequest(new { error = "This book is still being prepared." });

        // Re-importing the same book would duplicate the whole library.
        if (book.ImportedAt is not null)
            return Conflict(new
            {
                error = $"Already imported {book.ImportedCount} recipes from this book.",
                importedCount = book.ImportedCount,
            });

        var text = await _storage.ReadTextAsync($"{bookId}/text.txt", ct);
        if (string.IsNullOrWhiteSpace(text))
            return BadRequest(new { error = "The text for this book is no longer available." });

        var extracted = await _extraction.ExtractRecipesAsync(text, ct);
        if (extracted.Count == 0)
            return Ok(new { imported = 0, message = "No complete recipes could be found in this book." });

        // Skip titles the user already has, so importing a second cookbook by
        // the same author does not double up the classics.
        var existing = (await _db.Recipes
                .Where(r => r.UserId == UserId)
                .Select(r => r.Title)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Normalise every ingredient in the book in one batch, so matching can
        // stay free arithmetic later. One pass for the whole import, and almost
        // everything resolves from the seed table without an AI call.
        var allNames = extracted
            .SelectMany(r => r.IngredientNames.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        var canonical = await _normaliser.NormaliseAsync(allNames, ct);

        var toAdd = new List<Recipe>();
        foreach (var recipe in extracted)
        {
            if (existing.Contains(recipe.Title)) continue;
            recipe.IngredientNames = Canonicalise(recipe.IngredientNames, canonical);
            recipe.UserId = UserId;
            recipe.SourceBookId = bookId;
            recipe.SourceBookTitle = book.Title;
            recipe.Source = "book";
            toAdd.Add(recipe);
        }

        _db.Recipes.AddRange(toAdd);

        book.ImportedCount = toAdd.Count;
        book.ImportedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Imported {Added} recipes from book {BookId} ({Skipped} already present)",
            toAdd.Count, bookId, extracted.Count - toAdd.Count);

        return Ok(new
        {
            imported = toAdd.Count,
            skipped = extracted.Count - toAdd.Count,
            titles = toAdd.Take(8).Select(r => r.Title),
        });
    }

    // ── From a photo ──────────────────────────────────────────────────────────

    /// <summary>Roughly 4 MB of base64, matching Kitchen's own photo limit.</summary>
    private const int MaxImageChars = 5_600_000;

    /// <summary>
    /// Reads a recipe from a photograph and returns it as a DRAFT.
    ///
    /// Nothing is saved here on purpose. Vision misreads handwriting, and a
    /// recipe silently added with "2 tsp" where the card said "2 tbsp" is worse
    /// than one the user was asked to check. The client shows the draft, the
    /// user corrects it, and POST /api/recipes saves it.
    /// </summary>
    [HttpPost("from-photo")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> FromPhoto([FromBody] PhotoRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData))
            return BadRequest(new { error = "Add a photo of the recipe." });

        if (req.ImageData.Length > MaxImageChars)
            return BadRequest(new { error = "That photo is too large — try a smaller one." });

        if (!_extraction.VisionAvailable)
            return StatusCode(503, new { error = "Reading photos is not available on this server yet." });

        var recipes = await _extraction.ExtractRecipesFromImageAsync(req.ImageData, ct);

        if (recipes.Count == 0)
            return Ok(new
            {
                found = 0,
                message = "No recipe could be read from that photo. A straight-on shot of the whole page usually works best.",
            });

        return Ok(new
        {
            found = recipes.Count,
            // Drafts, not rows: the client saves them after the user has looked.
            drafts = recipes.Select(r => new
            {
                r.Title, r.Description, r.Cuisine, r.Course, r.Servings, r.TotalMinutes,
                Ingredients = Parse<List<RecipeIngredient>>(r.IngredientsJson) ?? new(),
                Steps = Parse<List<string>>(r.StepsJson) ?? new(),
                Dietary = Parse<List<string>>(r.DietaryJson) ?? new(),
            }),
        });
    }

    /// <summary>
    /// Saves a recipe the user typed or corrected after a photo read.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveRecipeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            return BadRequest(new { error = "Give the recipe a name." });

        var ingredients = (req.Ingredients ?? new())
            .Where(i => !string.IsNullOrWhiteSpace(i.Name))
            .ToList();

        if (ingredients.Count == 0)
            return BadRequest(new { error = "Add at least one ingredient." });

        var names = ingredients.Select(i => i.Name.Trim()).ToList();
        var canonical = await _normaliser.NormaliseAsync(names);

        var recipe = new Recipe
        {
            UserId = UserId,
            Title = req.Title.Trim(),
            Description = req.Description,
            Cuisine = req.Cuisine,
            Course = req.Course,
            Servings = req.Servings,
            TotalMinutes = req.TotalMinutes,
            IngredientsJson = JsonSerializer.Serialize(ingredients),
            IngredientNames = string.Join("\n", ingredients.Select(i =>
                canonical.TryGetValue(i.Name.Trim(), out var c) ? c : i.Name.Trim().ToLowerInvariant())),
            StepsJson = JsonSerializer.Serialize(req.Steps ?? new()),
            DietaryJson = JsonSerializer.Serialize(req.Dietary ?? new()),
            SourceBookTitle = req.SourceLabel,
            Source = req.Source ?? "photo",
        };

        _db.Recipes.Add(recipe);
        await _db.SaveChangesAsync();

        return Ok(new { recipe.Id, recipe.Title });
    }

    public class PhotoRequest
    {
        /// <summary>Base64 data URL, as the other photo endpoints take.</summary>
        public string? ImageData { get; set; }
    }

    public class SaveRecipeRequest
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? Cuisine { get; set; }
        public string? Course { get; set; }
        public int? Servings { get; set; }
        public int? TotalMinutes { get; set; }
        public List<RecipeIngredient>? Ingredients { get; set; }
        public List<string>? Steps { get; set; }
        public List<string>? Dietary { get; set; }
        /// <summary>Where it came from, shown on the card: "Nonna's card", "Instagram"…</summary>
        public string? SourceLabel { get; set; }
        public string? Source { get; set; }
    }

    // ── Cook tonight ──────────────────────────────────────────────────────────

    /// <summary>
    /// What the user can actually cook right now, from their own recipes and
    /// their own pantry.
    ///
    /// This is the point of the whole import: two datasets the user already
    /// owns, meeting. It is deliberately arithmetic rather than an AI call —
    /// matching ingredients against a pantry does not need a model, and running
    /// one here would cost money on every page load.
    /// </summary>
    [HttpGet("cook-tonight")]
    public async Task<IActionResult> CookTonight()
    {
        // The pantry lives in a raw table that KitchenController creates lazily
        // the first time someone opens Kitchen. A user who imported a cookbook
        // but has never opened Kitchen has no such table, so this must treat a
        // missing table exactly like an empty pantry rather than throwing.
        List<string> pantry;
        try
        {
            pantry = await _db.Database
                .SqlQuery<string>($@"SELECT ""Name"" FROM ""PantryItems"" WHERE ""UserId""={UserId}")
                .ToListAsync();
        }
        catch (DbException ex)
        {
            _logger.LogInformation(ex, "Pantry table not available yet; treating as an empty pantry");
            pantry = new List<string>();
        }

        if (pantry.Count == 0)
            return Ok(new { pantryEmpty = true, matches = Array.Empty<object>() });

        // The pantry is normalised the same way, otherwise a canonical recipe
        // ingredient ("spring onion") would fail against a raw pantry entry
        // ("scallions") and the whole exercise would be pointless.
        var pantryNames = pantry.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        var pantryCanonical = await _normaliser.NormaliseAsync(pantryNames);
        var have = pantryNames
            .Select(p => pantryCanonical.TryGetValue(p, out var c) ? c : p.ToLowerInvariant().Trim())
            .Distinct()
            .ToList();

        var recipes = await _db.Recipes
            .Where(r => r.UserId == UserId && r.IngredientNames != "")
            .Select(r => new { r.Id, r.Title, r.Description, r.TotalMinutes, r.Servings, r.SourceBookTitle, r.IngredientNames })
            .ToListAsync();

        var matches = new List<PantryMatch>();
        foreach (var r in recipes)
        {
            var needed = r.IngredientNames.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (needed.Length == 0) continue;

            // Substring both ways: a pantry "tomatoes" should satisfy a recipe
            // calling for "tomato", and a pantry "smoked paprika" should satisfy
            // "paprika". Imperfect, but wrong in the generous direction, which
            // is the right way to be wrong when suggesting dinner.
            var missing = needed
                .Where(n => !have.Any(h => h.Contains(n) || n.Contains(h)))
                .ToList();

            var ratio = (double)(needed.Length - missing.Count) / needed.Length;
            if (ratio < 0.5) continue;

            matches.Add(new PantryMatch(
                r.Id, r.Title, r.Description, r.TotalMinutes, r.Servings, r.SourceBookTitle,
                Have: needed.Length - missing.Count,
                Needs: needed.Length,
                Missing: missing.Take(6).ToList(),
                CanCookNow: missing.Count == 0,
                MatchPercent: (int)Math.Round(ratio * 100)));
        }

        return Ok(new
        {
            pantryEmpty = false,
            pantryCount = have.Count,
            // Fully-cookable first, then closest to it.
            matches = matches
                .OrderByDescending(m => m.CanCookNow)
                .ThenByDescending(m => m.MatchPercent)
                .Take(20),
        });
    }

    // ── Substitutions ─────────────────────────────────────────────────────────

    /// <summary>
    /// Asks whether the missing ingredients can be worked around with what the
    /// user already has.
    ///
    /// This is where a model earns its cost: substitution is judgment, not
    /// lookup — no table can say "you have milk and lemon, that makes
    /// buttermilk". Unlike matching, it runs only when the user asks about one
    /// specific recipe, so the cost is bounded by intent rather than by page
    /// views.
    /// </summary>
    [HttpGet("{id:int}/substitutions")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Substitutions(int id, CancellationToken ct)
    {
        var recipe = await _db.Recipes.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId, ct);
        if (recipe is null) return NotFound(new { error = "Recipe not found." });

        List<string> pantry;
        try
        {
            pantry = await _db.Database
                .SqlQuery<string>($@"SELECT ""Name"" FROM ""PantryItems"" WHERE ""UserId""={UserId}")
                .ToListAsync(ct);
        }
        catch (DbException)
        {
            pantry = new List<string>();
        }

        if (pantry.Count == 0)
            return Ok(new { substitutions = Array.Empty<object>(), message = "Add what is in your kitchen first." });

        var canonical = await _normaliser.NormaliseAsync(pantry, ct);
        var have = pantry.Select(p => canonical.TryGetValue(p, out var c) ? c : p.ToLowerInvariant()).ToList();

        var needed = recipe.IngredientNames.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var missing = needed.Where(n => !have.Any(h => h.Contains(n) || n.Contains(h))).ToList();

        if (missing.Count == 0)
            return Ok(new { substitutions = Array.Empty<object>(), message = "You have everything for this one." });

        var prompt =
            "A cook is missing some ingredients. Say which can be substituted with what they " +
            "already have, and which cannot.\n\n" +
            "Return ONLY JSON:\n" +
            "{\"substitutions\":[{\"missing\":\"buttermilk\",\"use\":\"milk + lemon juice\"," +
            "\"how\":\"one line on what to do\",\"confidence\":\"good|acceptable|last-resort\"}]," +
            "\"cannotSubstitute\":[\"...\"]}\n\n" +
            "Rules:\n" +
            "- Only suggest substitutes made from the ingredients they actually have.\n" +
            "- Be honest: if a swap changes the dish materially, say last-resort. If there is " +
            "no reasonable substitute, put it in cannotSubstitute rather than inventing one.\n" +
            "- Never suggest anything unsafe for an allergy: a nut substitute for a nut is not a swap.";

        try
        {
            var response = await _ai.TestPromptAsync(new PromptTestRequest
            {
                PromptContent = prompt,
                UserInput = $"RECIPE: {recipe.Title}\nMISSING: {string.Join(", ", missing)}\nTHEY HAVE: {string.Join(", ", have)}",
            });

            using var doc = JsonDocument.Parse(response.Result ?? "{}");
            var subs = doc.RootElement.TryGetProperty("substitutions", out var sv) && sv.ValueKind == JsonValueKind.Array
                ? sv.EnumerateArray().Select(x => new
                {
                    missing = x.TryGetProperty("missing", out var m) ? m.GetString() : null,
                    use = x.TryGetProperty("use", out var u) ? u.GetString() : null,
                    how = x.TryGetProperty("how", out var h) ? h.GetString() : null,
                    confidence = x.TryGetProperty("confidence", out var c) ? c.GetString() : "acceptable",
                }).Where(x => x.missing is not null && x.use is not null).ToList<object>()
                : new List<object>();

            var cannot = doc.RootElement.TryGetProperty("cannotSubstitute", out var cv) && cv.ValueKind == JsonValueKind.Array
                ? cv.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList()
                : new List<string?>();

            return Ok(new { substitutions = subs, cannotSubstitute = cannot, missing });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not work out substitutions for recipe {Id}", id);
            return Ok(new { substitutions = Array.Empty<object>(), missing, message = "Could not work out substitutions just now." });
        }
    }

    // ── Housekeeping ──────────────────────────────────────────────────────────

    [HttpPost("{id:int}/cooked")]
    public async Task<IActionResult> Cooked(int id)
    {
        var r = await _db.Recipes.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (r is null) return NotFound(new { error = "Recipe not found." });
        r.TimesCooked++;
        r.LastCookedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { r.TimesCooked, r.LastCookedAt });
    }

    [HttpPost("{id:int}/favourite")]
    public async Task<IActionResult> Favourite(int id)
    {
        var r = await _db.Recipes.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (r is null) return NotFound(new { error = "Recipe not found." });
        r.IsFavourite = !r.IsFavourite;
        await _db.SaveChangesAsync();
        return Ok(new { r.IsFavourite });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _db.Recipes.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (r is null) return NotFound(new { error = "Recipe not found." });
        _db.Recipes.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    /// <summary>Removes every recipe that came from one book, so an import can be undone.</summary>
    [HttpDelete("from-book/{bookId:guid}")]
    public async Task<IActionResult> DeleteFromBook(Guid bookId)
    {
        var rows = await _db.Recipes.Where(r => r.UserId == UserId && r.SourceBookId == bookId).ToListAsync();
        _db.Recipes.RemoveRange(rows);

        var book = await _db.ReaderBooks.FirstOrDefaultAsync(b => b.Id == bookId && b.UserId == UserId);
        if (book is not null)
        {
            book.ImportedAt = null;
            book.ImportedCount = 0;
        }

        await _db.SaveChangesAsync();
        return Ok(new { deleted = rows.Count });
    }

    /// <summary>A recipe scored against what is actually in the pantry.</summary>
    private record PantryMatch(
        int Id, string Title, string? Description, int? TotalMinutes, int? Servings,
        string? SourceBookTitle, int Have, int Needs, List<string> Missing,
        bool CanCookNow, int MatchPercent);

    /// <summary>Rewrites a stored ingredient list to canonical names.</summary>
    private static string Canonicalise(string names, Dictionary<string, string> map) =>
        string.Join("\n", names
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(n => map.TryGetValue(n, out var c) ? c : n)
            .Distinct());

    private static T? Parse<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return default; }
    }
}
