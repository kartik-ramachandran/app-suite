using System.Text.Json;
using KitchenManager.Api.Models;

namespace KitchenManager.Api.Services;

/// <summary>
/// Pulls structured content out of a book, in the shape its genre implies.
///
/// The book has already been parsed by the time this runs, so the expensive
/// part is done. Genre only decides which schema the model extracts into:
/// a cookbook yields recipes, an exam guide yields study topics, a travel book
/// yields places. Same text, same parse cost, different destination.
///
/// Bounded on purpose: a 400-page cookbook could contain 200 recipes and
/// extracting all of them would be a long, expensive job with no ceiling.
/// A cap keeps the cost per import predictable, and the user can re-run for
/// more.
/// </summary>
public class BookExtractionService
{
    private readonly IAIService _ai;
    private readonly AzureVisionService _vision;
    private readonly ILogger<BookExtractionService> _logger;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Characters per extraction call. Recipes are dense, so smaller than the reader's chunks.</summary>
    private const int ChunkChars = 10000;

    /// <summary>Hard ceiling on extraction calls for one import.</summary>
    private const int MaxChunks = 15;

    public BookExtractionService(IAIService ai, AzureVisionService vision, ILogger<BookExtractionService> logger)
    {
        _ai = ai;
        _vision = vision;
        _logger = logger;
    }

    // ── Genre classification ──────────────────────────────────────────────────

    /// <summary>
    /// Decides what kind of book this is, so the app can offer the right
    /// destination. One cheap call, before any expensive extraction.
    ///
    /// Samples the opening AND the middle: front matter (copyright, dedication)
    /// looks the same in every book, and a cookbook's introduction can read
    /// like a memoir. The middle of a cookbook is unmistakably recipes.
    /// </summary>
    public async Task<(string Genre, double Confidence)> ClassifyAsync(string text, CancellationToken ct = default)
    {
        var opening = text[..Math.Min(text.Length, 3500)];
        var middle = text.Length > 10000
            ? text.Substring(text.Length / 2, Math.Min(3500, text.Length - text.Length / 2))
            : string.Empty;

        var options = string.Join("\n", BookGenres.All.Select(g => $"- {g.Id}: {g.Description}"));

        var prompt =
            "Classify this book into exactly one category.\n\n" +
            options + "\n\n" +
            "Return ONLY JSON: {\"genre\":\"<id>\",\"confidence\":0.0-1.0}\n\n" +
            "Judge by PASSAGE B (the middle of the book) more than PASSAGE A — front matter " +
            "looks similar in every book. If it does not clearly fit a category, use \"general\".";

        var raw = await AskAsync(prompt, $"PASSAGE A:\n{opening}\n\nPASSAGE B:\n{middle}", ct);

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var genre = doc.RootElement.TryGetProperty("genre", out var g) ? g.GetString() : null;
            var confidence = doc.RootElement.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetDouble()
                : 0.5;

            var resolved = BookGenres.Find(genre).Id;
            _logger.LogInformation("Book classified as {Genre} ({Confidence:P0})", resolved, confidence);
            return (resolved, confidence);
        }
        catch (JsonException)
        {
            // Unreadable answer: fall back to general, which extracts nothing
            // and just reads the book. Wrong-but-harmless beats confidently
            // dumping garbage into someone's Kitchen.
            _logger.LogWarning("Genre classification did not parse; treating as general");
            return (BookGenres.General, 0);
        }
    }

    // ── Recipes ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts every recipe it can find, in reading order.
    ///
    /// Deduplicated by title because recipes cross chunk boundaries: the same
    /// dish appears at the end of one window and the start of the next, and
    /// importing it twice is immediately visible to the user.
    /// </summary>
    public async Task<List<Recipe>> ExtractRecipesAsync(string text, CancellationToken ct = default)
    {
        var chunks = Chunk(text, ChunkChars).Take(MaxChunks).ToList();
        var byTitle = new Dictionary<string, Recipe>(StringComparer.OrdinalIgnoreCase);

        var prompt =
            "Extract every complete recipe from this passage.\n\n" +
            "Return ONLY JSON:\n" +
            "{\"recipes\":[{\"title\":\"...\",\"description\":\"one line\",\"cuisine\":\"...\",\"course\":\"starter|main|dessert|side|breakfast|drink\"," +
            "\"servings\":4,\"totalMinutes\":45," +
            "\"ingredients\":[{\"name\":\"plain flour\",\"quantity\":\"200\",\"unit\":\"g\"}]," +
            "\"steps\":[\"...\"],\"dietary\":[\"vegetarian\"]}]}\n\n" +
            "Rules:\n" +
            "- Only recipes with BOTH ingredients and method. Skip a dish that is merely mentioned.\n" +
            "- ingredient `name` must be the bare ingredient: no quantity and no preparation. " +
            "\"plain flour\" not \"200g plain flour\"; \"onion\" not \"onion, sliced\"; " +
            "\"red pepper\" not \"1 red pepper, deseeded and sliced\". It is matched against a " +
            "pantry, and a pantry never holds \"onion, sliced\".\n" +
            "- Keep the book's own wording for steps; do not rewrite or shorten them.\n" +
            "- dietary: only what the text supports, e.g. vegetarian, vegan, gluten-free, contains-nuts, dairy-free.\n" +
            "- If the passage has no complete recipe, return {\"recipes\":[]}.";

        foreach (var chunk in chunks)
        {
            if (ct.IsCancellationRequested) break;

            var raw = await AskAsync(prompt, chunk, ct);
            foreach (var recipe in ParseRecipes(raw))
            {
                if (string.IsNullOrWhiteSpace(recipe.Title)) continue;
                // First occurrence wins: a recipe split across a boundary is
                // more complete in the window that starts it.
                if (!byTitle.ContainsKey(recipe.Title)) byTitle[recipe.Title] = recipe;
            }
        }

        _logger.LogInformation("Extracted {Count} recipes from {Chunks} chunks", byTitle.Count, chunks.Count);
        return byTitle.Values.ToList();
    }

    private List<Recipe> ParseRecipes(string raw)
    {
        var result = new List<Recipe>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var arr = doc.RootElement.TryGetProperty("recipes", out var r) ? r : doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array) return result;

            foreach (var item in arr.EnumerateArray())
            {
                var title = Str(item, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;

                var ingredients = new List<RecipeIngredient>();
                if (item.TryGetProperty("ingredients", out var ing) && ing.ValueKind == JsonValueKind.Array)
                {
                    foreach (var i in ing.EnumerateArray())
                    {
                        var name = Str(i, "name");
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        var clean = CleanIngredientName(name);
                        if (clean.Length == 0) continue;

                        ingredients.Add(new RecipeIngredient
                        {
                            Name = clean,
                            Quantity = Str(i, "quantity"),
                            Unit = Str(i, "unit"),
                        });
                    }
                }

                // A "recipe" with no ingredients is a mention, not a recipe.
                if (ingredients.Count == 0) continue;

                var steps = StrList(item, "steps");
                if (steps.Count == 0) continue;

                result.Add(new Recipe
                {
                    Title = title.Trim(),
                    Description = Str(item, "description"),
                    Cuisine = Str(item, "cuisine"),
                    Course = Str(item, "course"),
                    Servings = Int(item, "servings"),
                    TotalMinutes = Int(item, "totalMinutes"),
                    IngredientsJson = JsonSerializer.Serialize(ingredients),
                    IngredientNames = string.Join("\n", ingredients.Select(i => i.Name.ToLowerInvariant())),
                    StepsJson = JsonSerializer.Serialize(steps),
                    DietaryJson = JsonSerializer.Serialize(StrList(item, "dietary")),
                });
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Recipe JSON did not parse; skipping this chunk");
        }
        return result;
    }

    // ── Recipes from a photo ──────────────────────────────────────────────────

    public bool VisionAvailable => _vision.IsAvailable;

    /// <summary>
    /// Reads a recipe out of a photograph: a page from a cookbook, a magazine
    /// clipping, a handwritten card, a screenshot.
    ///
    /// This matters more than the book import, because most recipes people
    /// actually cook are not in a book they own as a file — they are a page
    /// they can photograph. Same destination, same schema, one image instead
    /// of a 300-page parse.
    ///
    /// Returns an empty list rather than throwing when the photo has no
    /// recipe in it: a blurry page is a normal thing for a user to do, not an
    /// error condition.
    /// </summary>
    public async Task<List<Recipe>> ExtractRecipesFromImageAsync(string base64Image, CancellationToken ct = default)
    {
        if (!_vision.IsAvailable)
        {
            _logger.LogWarning("Vision is not configured; cannot read a recipe photo");
            return new List<Recipe>();
        }

        var system =
            "You read recipes from photographs of cookbook pages, magazine clippings, " +
            "handwritten cards, and screenshots.\n\n" +
            "Return ONLY JSON (no markdown fences):\n" +
            "{\"recipes\":[{\"title\":\"...\",\"description\":\"one line\",\"cuisine\":\"...\"," +
            "\"course\":\"starter|main|dessert|side|breakfast|drink\",\"servings\":4,\"totalMinutes\":45," +
            "\"ingredients\":[{\"name\":\"plain flour\",\"quantity\":\"200\",\"unit\":\"g\"}]," +
            "\"steps\":[\"...\"],\"dietary\":[\"vegetarian\"]}]}\n\n" +
            "Rules:\n" +
            "- Transcribe what is written. Never invent an ingredient or a step that is not visible.\n" +
            "- ingredient `name` is the bare ingredient: no quantity, no preparation. " +
            "\"plain flour\" not \"200g plain flour\"; \"onion\" not \"onion, sliced\". " +
            "Put any preparation in the method step instead. It is matched against a pantry.\n" +
            "- If part of the recipe is cut off or unreadable, include what you can read and stop there.\n" +
            "- If the photo has no recipe in it, return {\"recipes\":[]}.";

        try
        {
            var raw = await _vision.AnalyzeAsync(base64Image, system, "Read the recipe in this photo.", 2000);
            var recipes = ParseRecipes(raw);
            _logger.LogInformation("Read {Count} recipes from a photo", recipes.Count);
            return recipes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read a recipe from the photo");
            return new List<Recipe>();
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> AskAsync(string instruction, string passage, CancellationToken ct)
    {
        var response = await _ai.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = instruction,
            UserInput = passage,
        });
        return response.Result ?? "{}";
    }

    /// <summary>
    /// Reduces an ingredient line to the thing a pantry would actually hold.
    ///
    /// The prompt asks for this, but a model will still return "onion, sliced"
    /// often enough to matter, and every such name is one that can never match
    /// a pantry — silently under-reporting what the user can cook. Cheap to
    /// enforce here, so it is enforced here rather than hoped for.
    /// </summary>
    private static string CleanIngredientName(string raw)
    {
        var name = raw.Trim();

        // "onion, sliced" / "garlic, peeled and crushed" -> "onion" / "garlic".
        var comma = name.IndexOf(',');
        if (comma > 0) name = name[..comma];

        // "coriander (chopped)" -> "coriander"
        var paren = name.IndexOf('(');
        if (paren > 0) name = name[..paren];

        // Trailing preparation without a comma: "red pepper sliced".
        foreach (var suffix in PrepWords)
        {
            if (name.EndsWith(" " + suffix, StringComparison.OrdinalIgnoreCase))
                name = name[..^(suffix.Length + 1)];
        }

        return name.Trim().TrimEnd('.', ';').Trim();
    }

    private static readonly string[] PrepWords =
    {
        "sliced", "diced", "chopped", "minced", "grated", "crushed", "peeled",
        "beaten", "melted", "softened", "drained", "rinsed", "trimmed", "halved",
        "quartered", "shredded", "cubed", "roughly chopped", "finely chopped",
        "deseeded", "zested", "juiced", "torn",
    };

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static List<string> StrList(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return new();
        return v.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()!)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }
}
