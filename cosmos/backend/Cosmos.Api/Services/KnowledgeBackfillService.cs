using Microsoft.EntityFrameworkCore;
using Cosmos.Api.Data;

namespace Cosmos.Api.Services;

/// <summary>
/// One-shot import of a user's EXISTING content into the knowledge cosmos —
/// everything created before live capture shipped (prompts, chains, personas,
/// study/inkling docs, garments, receipts, plants, …). Uses the same module
/// slugs and sourceRef formats as the live capture calls, so re-running is
/// idempotent and future live captures update the same nodes.
/// </summary>
public class KnowledgeBackfillService
{
    private const int MaxRowsPerSource = 300;
    private const int MaxEnrichPerRun = 40;

    private readonly PromptDbContext _context;
    private readonly KnowledgeWeaverService _weaver;
    private readonly ILogger<KnowledgeBackfillService> _logger;

    public KnowledgeBackfillService(PromptDbContext context, KnowledgeWeaverService weaver, ILogger<KnowledgeBackfillService> logger)
    {
        _context = context;
        _weaver = weaver;
        _logger = logger;
    }

    public class BackfillRow
    {
        public string Id { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Content { get; set; }
    }

    /// <summary>
    /// Raw-SQL module table. Title columns are tried in order (first non-empty);
    /// content columns are concatenated. Identifier quoting is retried both ways
    /// because older tables were created unquoted (lower-cased on Postgres) and
    /// newer ones quoted.
    /// </summary>
    private sealed record RawSource(string Module, string Table, string[] TitleCols, string[] ContentCols, string RefPrefix, string Source);

    private static readonly RawSource[] RawSources =
    {
        new("plant",      "Plants",             new[] { "Nickname", "Name", "Species" }, new[] { "Health", "Diagnosis", "CareTips" }, "plant", "fetched"),
        new("wardrobe",   "WardrobeItems",      new[] { "Name", "Category" },            new[] { "Category", "Colors", "Style", "Notes", "CareTip" }, "garment", "fetched"),
        new("wardrobe",   "WardrobeOutfits",    new[] { "Occasion" },                    new[] { "WeatherSummary", "OutfitsJson" }, "outfit", "fetched"),
        new("kitchen",    "MealLog",            new[] { "Title" },                       new[] { "HealthNote", "ItemsJson" }, "meal", "fetched"),
        new("expense",    "Receipts",           new[] { "Merchant" },                    new[] { "Category", "PurchaseDate", "Total", "ItemsJson" }, "receipt", "fetched"),
        new("medicine",   "Medicines",          new[] { "Name" },                        new[] { "Form", "Strength", "Purpose", "Notes" }, "medicine", "fetched"),
        new("warranty",   "Warranties",         new[] { "Product", "Brand" },            new[] { "Brand", "Retailer", "ExpiryDate", "Notes" }, "warranty", "fetched"),
        new("bookshelf",  "ReaderBooks",        new[] { "Title" },                       new[] { "Author", "Subject" }, "book", "fetched"),
        new("bills",      "Bills",              new[] { "Biller" },                      new[] { "Category", "Amount", "DueDate", "Notes" }, "bill", "fetched"),
        new("whiteboard", "WhiteboardSessions", new[] { "Title" },                       new[] { "Markdown", "ActionItems", "Decisions" }, "whiteboard", "fetched"),
        new("travel",     "TravelGuides",       new[] { "Destination" },                 new[] { "GuideJson" }, "travel-guide", "fetched"),
        new("study",      "StudyDocs",          new[] { "Title", "Topic" },              new[] { "DataJson" }, "study-doc", "created"),
        new("inkling",    "InklingDocs",        new[] { "Title" },                       new[] { "ContentJson" }, "inkling-doc", "created"),
    };

    public async Task<(int Imported, Dictionary<string, int> ByModule)> BackfillAsync(string userId)
    {
        var byModule = new Dictionary<string, int>();
        var captured = new List<Guid>();

        async Task Capture(string module, string? title, string? content, string source, string sourceRef)
        {
            var nodeId = await _weaver.CaptureAsync(userId, title, content, module, source, sourceRef, enrich: false);
            if (nodeId == null) return;
            captured.Add(nodeId.Value);
            byModule[module] = byModule.GetValueOrDefault(module) + 1;
        }

        // ── EF-modelled sources ──────────────────────────────────────────────
        try
        {
            var prompts = await _context.Prompts
                .Where(p => p.UserId == userId && p.IsActive)
                .OrderByDescending(p => p.UpdatedAt).Take(MaxRowsPerSource).ToListAsync();
            foreach (var p in prompts)
                await Capture("prompts", p.Name, $"{p.Content}\n\n{p.Description}".Trim(), "created", $"prompt:{p.Id}");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Backfill: prompts failed"); }

        try
        {
            var chains = await _context.PromptChains
                .Include(c => c.Nodes)
                .Where(c => c.UserId == userId)
                .OrderByDescending(c => c.UpdatedAt).Take(MaxRowsPerSource).ToListAsync();
            foreach (var c in chains)
            {
                var nodeLabels = string.Join(", ", c.Nodes.Take(30).Select(n => n.Label ?? n.NodeType));
                await Capture("chains", c.Name, $"{c.Description}\nNodes: {nodeLabels}".Trim(), "created", $"chain:{c.Id}");
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Backfill: chains failed"); }

        try
        {
            var personas = await _context.Personas
                .Where(p => p.UserId == userId && !p.IsTemplate)
                .OrderByDescending(p => p.UpdatedAt).Take(MaxRowsPerSource).ToListAsync();
            foreach (var p in personas)
                await Capture("personas", p.Name, $"{p.Description}\n\n{p.SystemPrompt}".Trim(), "created", $"persona:{p.Id}");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Backfill: personas failed"); }

        try
        {
            var docs = await _context.EmmdDocuments
                .Where(d => d.UserId == userId)
                .Take(MaxRowsPerSource).ToListAsync();
            foreach (var d in docs)
                await Capture("emmd", d.Title, d.Content, "created", $"emmd:{d.Id}");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Backfill: emmd failed"); }

        try
        {
            var goals = await _context.LifeGoals.Where(g => g.UserId == userId).Take(MaxRowsPerSource).ToListAsync();
            foreach (var g in goals)
                await Capture("life-tracker", g.Title, g.Notes, "created", $"life-goal:{g.Id}");
            var decisions = await _context.LifeDecisions.Where(d => d.UserId == userId).Take(MaxRowsPerSource).ToListAsync();
            foreach (var d in decisions)
                await Capture("life-tracker", d.Title, d.Reasoning, "created", $"life-decision:{d.Id}");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Backfill: life-tracker failed"); }

        // ── Raw-SQL module tables ────────────────────────────────────────────
        foreach (var source in RawSources)
        {
            var rows = await QueryRawSourceAsync(source, userId);
            foreach (var row in rows)
                await Capture(source.Module, row.Title, row.Content, source.Source, $"{source.RefPrefix}:{row.Id}");
        }

        // Bounded background concept enrichment — only nodes that have content
        // worth enriching and no concept links yet (idempotent across re-runs).
        try
        {
            var candidates = await _context.KnowledgeNodes
                .Where(n => n.UserId == userId && captured.Contains(n.Id)
                            && n.Content != null && n.Content.Length >= 200)
                .Select(n => n.Id)
                .ToListAsync();
            var alreadyEnriched = await _context.KnowledgeLinks
                .Where(l => l.UserId == userId && l.Kind == "concept" &&
                            (candidates.Contains(l.SourceId) || candidates.Contains(l.TargetId)))
                .Select(l => new { l.SourceId, l.TargetId })
                .ToListAsync();
            var enrichedSet = alreadyEnriched.SelectMany(l => new[] { l.SourceId, l.TargetId }).ToHashSet();
            var toEnrich = candidates.Where(id => !enrichedSet.Contains(id)).Take(MaxEnrichPerRun).ToList();
            _weaver.QueueSequentialEnrichment(toEnrich, userId);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Backfill: enrichment scheduling failed"); }

        return (byModule.Values.Sum(), byModule);
    }

    private async Task<List<BackfillRow>> QueryRawSourceAsync(RawSource source, string userId)
    {
        // Try quoted identifiers first (newer tables), then unquoted (older tables,
        // lower-cased by Postgres). Either failing just means that variant's table
        // doesn't exist — the module may never have been used.
        foreach (var quote in new[] { true, false })
        {
            try
            {
                var sql = BuildSql(source, quote);
                return await _context.Database.SqlQueryRaw<BackfillRow>(sql, userId).ToListAsync();
            }
            catch
            {
                // fall through to the other quoting variant
            }
        }
        return new List<BackfillRow>();
    }

    private static string BuildSql(RawSource source, bool quote)
    {
        string Q(string identifier) => quote ? $"\"{identifier}\"" : identifier;

        var titleExpr = "COALESCE(" + string.Join(", ", source.TitleCols.Select(c => $"NULLIF(CAST({Q(c)} AS TEXT), '')")) + ")";
        var contentExpr = string.Join(" || ' ' || ", source.ContentCols.Select(c => $"COALESCE(CAST({Q(c)} AS TEXT), '')"));

        return $"SELECT CAST({Q(source.Table)}.{Q("Id")} AS TEXT) AS \"Id\", {titleExpr} AS \"Title\", {contentExpr} AS \"Content\" " +
               $"FROM {Q(source.Table)} WHERE {Q("UserId")} = {{0}} LIMIT {MaxRowsPerSource}";
    }
}
