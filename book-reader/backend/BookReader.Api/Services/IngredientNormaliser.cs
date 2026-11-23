using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using BookReader.Api.Data;
using BookReader.Api.Models;

namespace BookReader.Api.Services;

/// <summary>
/// Turns an ingredient name into its canonical form, so pantry matching can
/// stay free arithmetic.
///
/// Three tiers, in order:
///   1. Seed table  - ~180 common pairs, in memory, free
///   2. Alias table - anything resolved before, one indexed read
///   3. AI          - once per genuinely unknown name, then written to tier 2
///
/// The point is that tier 3 fires rarely and never on a read path. Cookbooks
/// reuse the same few hundred ingredients, so after the first import almost
/// everything hits tier 1 or 2.
/// </summary>
public class IngredientNormaliser
{
    private readonly PromptDbContext _db;
    private readonly IAIService _ai;
    private readonly ILogger<IngredientNormaliser> _logger;

    /// <summary>
    /// Process-wide cache of resolved names. Ingredients are global, so this is
    /// safe to share, and it keeps a bulk import from hitting the database once
    /// per line.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public IngredientNormaliser(PromptDbContext db, IAIService ai, ILogger<IngredientNormaliser> logger)
    {
        _db = db;
        _ai = ai;
        _logger = logger;
    }

    /// <summary>
    /// Normalises a batch, which is how it is always used in practice — a
    /// recipe's ingredient list, or a pantry snapshot. Batching means one AI
    /// call for everything unknown rather than one per name.
    /// </summary>
    public async Task<Dictionary<string, string>> NormaliseAsync(
        IEnumerable<string> names, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();

        foreach (var raw in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = Clean(raw);
            if (key.Length == 0) continue;

            if (Seed.TryGetValue(key, out var seeded)) { result[raw] = seeded; continue; }
            if (Cache.TryGetValue(key, out var cached)) { result[raw] = cached; continue; }

            unresolved.Add(raw);
        }

        if (unresolved.Count == 0) return result;

        // Tier 2: anything resolved on a previous request.
        var keys = unresolved.Select(Clean).ToList();
        var known = await _db.IngredientAliases
            .Where(a => keys.Contains(a.Alias))
            .ToDictionaryAsync(a => a.Alias, a => a.Canonical, ct);

        var stillUnknown = new List<string>();
        foreach (var raw in unresolved)
        {
            var key = Clean(raw);
            if (known.TryGetValue(key, out var canonical))
            {
                Cache[key] = canonical;
                result[raw] = canonical;
            }
            else stillUnknown.Add(raw);
        }

        if (stillUnknown.Count == 0) return result;

        // Tier 3: ask once, remember forever.
        var learned = await AskAsync(stillUnknown, ct);
        foreach (var raw in stillUnknown)
        {
            var key = Clean(raw);
            var canonical = learned.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
                ? Clean(v)
                : key;   // Unresolvable: the name is its own canonical form.

            Cache[key] = canonical;
            result[raw] = canonical;

            // Only store a real mapping. Most names come back unchanged
            // ("salt" -> "salt"), and persisting those fills the table with
            // rows that teach nothing — the in-process cache already stops
            // them being asked about twice in a run.
            if (!string.Equals(key, canonical, StringComparison.OrdinalIgnoreCase))
            {
                _db.IngredientAliases.Add(new IngredientAlias
                {
                    Alias = key,
                    Canonical = canonical,
                    Source = "ai",
                });
            }
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Two imports racing on the same new ingredient both try to insert
            // it. The cache is already correct, so the write losing is fine.
            _logger.LogInformation(ex, "Alias already inserted by a concurrent request");
        }

        return result;
    }

    /// <summary>Convenience for a single name.</summary>
    public async Task<string> NormaliseOneAsync(string name, CancellationToken ct = default)
    {
        var map = await NormaliseAsync(new[] { name }, ct);
        return map.TryGetValue(name, out var v) ? v : Clean(name);
    }

    private async Task<Dictionary<string, string>> AskAsync(List<string> names, CancellationToken ct)
    {
        var prompt =
            "Give the canonical supermarket name for each ingredient, so that regional " +
            "and descriptive variants collapse to one term.\n\n" +
            "Rules:\n" +
            "- Use the plain everyday name: \"scallions\" -> \"spring onion\", " +
            "\"cilantro\" -> \"coriander\", \"caster sugar\" -> \"sugar\".\n" +
            "- Singular, lower case, no brand names, no preparation, no quantity.\n" +
            "- Keep a genuinely distinct ingredient distinct: smoked paprika is NOT paprika, " +
            "double cream is NOT milk, self-raising flour is NOT plain flour.\n" +
            "- If it is already canonical, repeat it unchanged.\n\n" +
            "Return ONLY JSON: {\"map\":{\"input\":\"canonical\", ...}}";

        try
        {
            var response = await _ai.TestPromptAsync(new PromptTestRequest
            {
                PromptContent = prompt,
                UserInput = string.Join("\n", names),
            });

            using var doc = JsonDocument.Parse(response.Result ?? "{}");
            if (!doc.RootElement.TryGetProperty("map", out var map) || map.ValueKind != JsonValueKind.Object)
                return new();

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in map.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String)
                    result[Clean(prop.Name)] = prop.Value.GetString() ?? prop.Name;
            }

            _logger.LogInformation("Learned {Count} new ingredient aliases", result.Count);
            return result;
        }
        catch (Exception ex)
        {
            // Never block a save on this. An unnormalised name still matches
            // itself; it just will not match its synonyms.
            _logger.LogWarning(ex, "Could not normalise ingredients; using them as written");
            return new();
        }
    }

    private static string Clean(string s) => s.Trim().ToLowerInvariant();

    /// <summary>
    /// Common variants, shipped so tier 3 rarely fires on a fresh install.
    /// UK/US spelling, tinned/canned, and the descriptive forms that recipes
    /// use but pantries do not.
    /// </summary>
    private static readonly Dictionary<string, string> Seed = new(StringComparer.OrdinalIgnoreCase)
    {
        // US -> UK produce
        ["scallion"] = "spring onion", ["scallions"] = "spring onion",
        ["green onion"] = "spring onion", ["green onions"] = "spring onion",
        ["cilantro"] = "coriander", ["fresh cilantro"] = "coriander",
        ["eggplant"] = "aubergine", ["zucchini"] = "courgette",
        ["arugula"] = "rocket", ["snow peas"] = "mangetout",
        ["bell pepper"] = "pepper", ["capsicum"] = "pepper",
        ["red bell pepper"] = "red pepper", ["green bell pepper"] = "green pepper",
        ["garbanzo beans"] = "chickpeas", ["garbanzos"] = "chickpeas",
        ["fava beans"] = "broad beans", ["rutabaga"] = "swede",
        ["beets"] = "beetroot", ["endive"] = "chicory",

        // Tinned / canned / fresh
        ["canned tomatoes"] = "tinned tomatoes", ["can of tomatoes"] = "tinned tomatoes",
        ["chopped tomatoes"] = "tinned tomatoes", ["plum tomatoes"] = "tinned tomatoes",
        ["crushed tomatoes"] = "tinned tomatoes", ["diced tomatoes"] = "tinned tomatoes",
        ["canned chickpeas"] = "chickpeas", ["tinned chickpeas"] = "chickpeas",
        ["tins chickpeas"] = "chickpeas", ["canned beans"] = "beans",
        ["canned coconut milk"] = "coconut milk",

        // Sugar and flour: the descriptive form is the same shopping item,
        // but a genuinely different product stays separate (see AskAsync).
        ["caster sugar"] = "sugar", ["granulated sugar"] = "sugar",
        ["superfine sugar"] = "sugar", ["white sugar"] = "sugar",
        ["confectioners sugar"] = "icing sugar", ["powdered sugar"] = "icing sugar",
        ["all-purpose flour"] = "plain flour", ["all purpose flour"] = "plain flour",
        ["ap flour"] = "plain flour", ["white flour"] = "plain flour",

        // Dairy
        ["heavy cream"] = "double cream", ["whipping cream"] = "double cream",
        ["light cream"] = "single cream", ["half and half"] = "single cream",
        ["whole milk"] = "milk", ["full fat milk"] = "milk", ["semi-skimmed milk"] = "milk",
        ["unsalted butter"] = "butter", ["salted butter"] = "butter",
        ["greek yoghurt"] = "greek yogurt", ["natural yoghurt"] = "yogurt",
        ["plain yogurt"] = "yogurt", ["plain yoghurt"] = "yogurt",

        // Oils and vinegars
        ["extra virgin olive oil"] = "olive oil", ["evoo"] = "olive oil",
        ["vegetable oil"] = "oil", ["sunflower oil"] = "oil", ["canola oil"] = "oil",
        ["rapeseed oil"] = "oil", ["neutral oil"] = "oil",
        ["white wine vinegar"] = "wine vinegar", ["red wine vinegar"] = "wine vinegar",

        // Aromatics
        ["garlic cloves"] = "garlic", ["clove garlic"] = "garlic",
        ["cloves garlic"] = "garlic", ["garlic clove"] = "garlic",
        ["fresh garlic"] = "garlic", ["minced garlic"] = "garlic",
        ["yellow onion"] = "onion", ["brown onion"] = "onion", ["white onion"] = "onion",
        ["fresh ginger"] = "ginger", ["ginger root"] = "ginger", ["root ginger"] = "ginger",

        // Herbs (fresh/dried collapse: the pantry holds one or the other)
        ["fresh basil"] = "basil", ["dried basil"] = "basil",
        ["fresh parsley"] = "parsley", ["flat-leaf parsley"] = "parsley",
        ["flat leaf parsley"] = "parsley", ["italian parsley"] = "parsley",
        ["fresh thyme"] = "thyme", ["dried thyme"] = "thyme",
        ["fresh rosemary"] = "rosemary", ["fresh mint"] = "mint",
        ["fresh dill"] = "dill", ["bay leaves"] = "bay leaf",

        // Spices
        ["ground cumin"] = "cumin", ["cumin seeds"] = "cumin",
        ["ground coriander"] = "coriander seed", ["coriander seeds"] = "coriander seed",
        ["sweet paprika"] = "paprika", ["ground cinnamon"] = "cinnamon",
        ["cinnamon sticks"] = "cinnamon", ["ground turmeric"] = "turmeric",
        ["chili flakes"] = "chilli flakes", ["chilli flakes"] = "chilli flakes",
        ["red pepper flakes"] = "chilli flakes", ["crushed red pepper"] = "chilli flakes",
        ["kosher salt"] = "salt", ["sea salt"] = "salt", ["flaky salt"] = "salt",
        ["table salt"] = "salt", ["fine salt"] = "salt",
        ["black pepper"] = "pepper", ["freshly ground black pepper"] = "pepper",
        ["ground black pepper"] = "pepper", ["salt and black pepper"] = "salt",
        ["salt and pepper"] = "salt",

        // Stock
        ["chicken broth"] = "chicken stock", ["beef broth"] = "beef stock",
        ["vegetable broth"] = "vegetable stock", ["veg stock"] = "vegetable stock",
        ["stock cube"] = "stock",

        // Bread and grains
        ["stale bread"] = "bread", ["sourdough"] = "bread", ["stale sourdough"] = "bread",
        ["breadcrumbs"] = "breadcrumbs", ["panko"] = "breadcrumbs",
        ["basmati rice"] = "rice", ["long grain rice"] = "rice", ["white rice"] = "rice",
        ["spaghetti"] = "pasta", ["penne"] = "pasta", ["linguine"] = "pasta",
        ["tagliatelle"] = "pasta", ["fusilli"] = "pasta", ["rigatoni"] = "pasta",

        // Eggs, nuts
        ["large eggs"] = "eggs", ["free range eggs"] = "eggs", ["medium eggs"] = "eggs",
        ["egg"] = "eggs", ["ground almonds"] = "almonds", ["almond flour"] = "almonds",
        ["flaked almonds"] = "almonds",

        // Citrus
        ["lemons"] = "lemon", ["limes"] = "lime", ["lemon juice"] = "lemon",
        ["lemon zest"] = "lemon", ["lime juice"] = "lime",

        // Fish and meat
        ["anchovy fillets"] = "anchovies", ["anchovy"] = "anchovies",
        ["chicken breasts"] = "chicken", ["chicken thighs"] = "chicken",
        ["minced beef"] = "beef mince", ["ground beef"] = "beef mince",
        ["minced lamb"] = "lamb mince", ["ground lamb"] = "lamb mince",
        ["streaky bacon"] = "bacon", ["bacon rashers"] = "bacon",
    };

    /// <summary>Exposed so a startup task can report how many pairs ship by default.</summary>
    public static int SeedCount => Seed.Count;
}
