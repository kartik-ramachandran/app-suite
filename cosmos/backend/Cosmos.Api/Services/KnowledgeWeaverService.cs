using Microsoft.EntityFrameworkCore;
using Cosmos.Api.Data;
using Cosmos.Api.Models;
using System.Text.RegularExpressions;

namespace Cosmos.Api.Services;

/// <summary>
/// Weaves information into the user's knowledge cosmos. Every module calls
/// <see cref="CaptureAsync"/> when it produces or saves user content; the call
/// NEVER throws — a capture failure must never break the host feature.
/// Wiki-links and #tags are parsed synchronously; AI concept extraction runs
/// in the background on a fresh DI scope.
/// </summary>
public class KnowledgeWeaverService
{
    private const int MaxNodesPerUser = 5000;
    private const int EnrichMinLength = 200;

    private static readonly Regex WikiLinkRegex = new(@"\[\[([^\[\]\n]{1,120})\]\]", RegexOptions.Compiled);
    private static readonly Regex HashtagRegex = new(@"(?<=^|\s)#([\p{L}\p{N}_\-]{2,60})", RegexOptions.Compiled);

    /// <summary>Module slug → hub-node display label. Unlisted slugs are humanized.</summary>
    private static readonly Dictionary<string, string> ModuleLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["plant"] = "Plant Doctor",
        ["whiteboard"] = "Whiteboard",
        ["card-scanner"] = "Card Scanner",
        ["medicine"] = "Medicine Cabinet",
        ["warranty"] = "Warranty Vault",
        ["bookshelf"] = "Bookshelf",
        ["label"] = "Label Scanner",
        ["bills"] = "Bill Tracker",
        ["email-tasks"] = "Email Tasks",
        ["price"] = "Price Checker",
        ["gift"] = "Gift Finder",
        ["event-planner"] = "Event Planner",
        ["proposal"] = "Proposals",
        ["room"] = "Room Redesign",
        ["wardrobe"] = "Wardrobe",
        ["kitchen"] = "Kitchen",
        ["travel"] = "Travel",
        ["study"] = "Study Studio",
        ["inkling"] = "Inkling",
        ["emmd"] = "Enhanced MMD",
        ["social"] = "Social Pack",
        ["invoice"] = "Invoice Agent",
        ["expense"] = "Expense Snap",
        ["research"] = "Research",
        ["prompts"] = "Prompts",
        ["chains"] = "Workflows",
        ["personas"] = "Personas",
        ["life-tracker"] = "Life Tracker",
        ["signal"] = "Signal",
        ["prism"] = "Prism",
        ["content-studio"] = "Content Studio",
        ["cosmos"] = "Cosmos",
    };

    private readonly PromptDbContext _context;
    private readonly IAIService _aiService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<KnowledgeWeaverService> _logger;

    public KnowledgeWeaverService(
        PromptDbContext context,
        IAIService aiService,
        IServiceScopeFactory scopeFactory,
        ILogger<KnowledgeWeaverService> logger)
    {
        _context = context;
        _aiService = aiService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Capture a piece of information into the user's cosmos. Safe to call from
    /// any module — swallows all failures. Dedups by (module, sourceRef) when a
    /// sourceRef is given, else by (module, title). Links the node to a module
    /// hub, parses [[wiki]]/#tags, and queues background AI concept extraction.
    /// </summary>
    /// <param name="source">dumped | fetched | created</param>
    /// <param name="enrich">Queue background AI concept extraction (default). Pass false
    /// for bulk backfills, which schedule their own bounded enrichment.</param>
    /// <returns>The captured node's id, or null when nothing was captured.</returns>
    public async Task<Guid?> CaptureAsync(
        string userId,
        string? title,
        string? content,
        string module,
        string source = "created",
        string? sourceRef = null,
        bool enrich = true)
    {
        try
        {
            if (string.IsNullOrEmpty(userId)) return null;
            content = content?.Trim();
            title = title?.Trim();
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(content)) return null;

            if (string.IsNullOrEmpty(title))
            {
                var firstLine = content!.Split('\n')[0].Trim();
                title = firstLine.Length > 80 ? firstLine.Substring(0, 80) + "…" : firstLine;
                if (string.IsNullOrEmpty(title)) title = "Untitled";
            }
            if (title.Length > 200) title = title.Substring(0, 200) + "…";
            if (content is { Length: > 20000 }) content = content.Substring(0, 20000) + "…";

            // Dedup: same origin updates the existing star instead of adding a twin.
            KnowledgeNode? node = null;
            if (!string.IsNullOrEmpty(sourceRef))
            {
                node = await _context.KnowledgeNodes.FirstOrDefaultAsync(n =>
                    n.UserId == userId && n.SourceModule == module && n.SourceRef == sourceRef);
            }
            node ??= await _context.KnowledgeNodes.FirstOrDefaultAsync(n =>
                n.UserId == userId && n.Kind == "note" && n.SourceModule == module &&
                n.Title.ToLower() == title.ToLower());

            if (node == null)
            {
                var count = await _context.KnowledgeNodes.CountAsync(n => n.UserId == userId);
                if (count >= MaxNodesPerUser)
                {
                    _logger.LogInformation("Cosmos capture skipped for {UserId} — node limit reached", userId);
                    return null;
                }
                node = new KnowledgeNode { Title = title, UserId = userId };
                _context.KnowledgeNodes.Add(node);
            }

            node.Title = title;
            node.Content = content;
            node.Kind = "note";
            node.Source = source;
            node.SourceModule = module;
            node.SourceRef = sourceRef;
            node.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            // Module hub keeps each tool's stars clustered.
            var hub = await FindOrCreateNodeAsync(userId, LabelFor(module), "tag");
            await EnsureLinkAsync(userId, node.Id, hub.Id, "tag");

            await WeaveParsedLinksAsync(node, userId);

            if (enrich && content is { Length: >= EnrichMinLength })
                QueueConceptEnrichment(node.Id, userId);

            return node.Id;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cosmos capture failed for module {Module}", module);
            return null;
        }
    }

    public static string LabelFor(string module) =>
        ModuleLabels.TryGetValue(module, out var label)
            ? label
            : string.Join(' ', module.Split('-', '_')
                .Where(p => p.Length > 0)
                .Select(p => char.ToUpperInvariant(p[0]) + p.Substring(1)));

    /// <summary>Parse [[wiki-links]] and #hashtags from the node and connect them.</summary>
    public async Task WeaveParsedLinksAsync(KnowledgeNode node, string userId)
    {
        // Parse the content alone when it exists — the title is often a truncated
        // slice of the content, and a hashtag cut mid-word would spawn junk tags.
        var text = string.IsNullOrWhiteSpace(node.Content) ? node.Title : node.Content;

        foreach (Match m in WikiLinkRegex.Matches(text))
        {
            var linkedTitle = m.Groups[1].Value.Trim();
            if (string.IsNullOrEmpty(linkedTitle)) continue;
            var target = await FindOrCreateNodeAsync(userId, linkedTitle, "note");
            await EnsureLinkAsync(userId, node.Id, target.Id, "wiki");
        }

        foreach (Match m in HashtagRegex.Matches(text))
        {
            var tag = m.Groups[1].Value.Trim();
            var target = await FindOrCreateNodeAsync(userId, tag, "tag");
            await EnsureLinkAsync(userId, node.Id, target.Id, "tag");
        }
    }

    /// <summary>Ask the AI for concepts and connect them. Synchronous variant (cosmos dumps).</summary>
    public async Task WeaveConceptsAsync(KnowledgeNode node, string userId)
    {
        if (string.IsNullOrWhiteSpace(node.Content) || node.Content.Length <= 40) return;
        try
        {
            var concepts = await ExtractConceptsAsync(_aiService, node.Content);
            foreach (var concept in concepts)
            {
                var target = await FindOrCreateNodeAsync(userId, concept, "concept");
                await EnsureLinkAsync(userId, node.Id, target.Id, "concept");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Concept extraction failed for node {NodeId}", node.Id);
        }
    }

    /// <summary>
    /// Background concept extraction on a fresh DI scope, so module requests
    /// return without waiting on the AI.
    /// </summary>
    public void QueueConceptEnrichment(Guid nodeId, string userId) =>
        QueueSequentialEnrichment(new[] { nodeId }, userId);

    /// <summary>
    /// Enrich many nodes on ONE background task, sequentially — used by backfills
    /// so a bulk import doesn't fan out hundreds of parallel AI calls.
    /// </summary>
    public void QueueSequentialEnrichment(IReadOnlyList<Guid> nodeIds, string userId)
    {
        if (nodeIds.Count == 0) return;
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<PromptDbContext>();
            var aiService = scope.ServiceProvider.GetRequiredService<IAIService>();
            foreach (var nodeId in nodeIds)
            {
                try
                {
                    await EnrichNodeAsync(context, aiService, nodeId, userId);
                }
                catch (Exception ex)
                {
                    // Best-effort — the note itself is already woven in.
                    _logger.LogWarning(ex, "Background concept enrichment failed for node {NodeId}", nodeId);
                }
            }
        });
    }

    private static async Task EnrichNodeAsync(PromptDbContext context, IAIService aiService, Guid nodeId, string userId)
    {
        var node = await context.KnowledgeNodes.FirstOrDefaultAsync(n => n.Id == nodeId && n.UserId == userId);
        if (node == null || string.IsNullOrWhiteSpace(node.Content)) return;

        var concepts = await ExtractConceptsAsync(aiService, node.Content);
        foreach (var concept in concepts)
        {
            var existing = await context.KnowledgeNodes.FirstOrDefaultAsync(n =>
                n.UserId == userId && n.Kind == "concept" && n.Title.ToLower() == concept.ToLower());
            if (existing == null)
            {
                existing = new KnowledgeNode
                {
                    Title = concept,
                    Kind = "concept",
                    Source = "created",
                    SourceModule = "cosmos",
                    UserId = userId
                };
                context.KnowledgeNodes.Add(existing);
                await context.SaveChangesAsync();
            }
            var linkExists = await context.KnowledgeLinks.AnyAsync(l =>
                l.UserId == userId && l.Kind == "concept" &&
                ((l.SourceId == nodeId && l.TargetId == existing.Id) ||
                 (l.SourceId == existing.Id && l.TargetId == nodeId)));
            if (!linkExists)
            {
                context.KnowledgeLinks.Add(new KnowledgeLink
                {
                    SourceId = nodeId,
                    TargetId = existing.Id,
                    Kind = "concept",
                    UserId = userId
                });
                await context.SaveChangesAsync();
            }
        }
    }

    private static async Task<List<string>> ExtractConceptsAsync(IAIService aiService, string content)
    {
        var request = new PromptTestRequest
        {
            PromptContent =
                "Extract 3 to 6 short concept labels (1-3 words each) that best categorize the text. " +
                "Prefer general, reusable concepts over one-off phrases. " +
                "Respond with ONLY the labels, comma-separated, no numbering, no explanations.",
            UserInput = content.Length > 4000 ? content.Substring(0, 4000) : content
        };
        var response = await aiService.TestPromptAsync(request);
        return (response.Result ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(c => c.Length >= 2 && c.Length <= 60 && !c.Contains('\n'))
            .Take(6)
            .ToList();
    }

    public async Task<KnowledgeNode> FindOrCreateNodeAsync(string userId, string title, string kind)
    {
        var existing = await _context.KnowledgeNodes.FirstOrDefaultAsync(n =>
            n.UserId == userId && n.Kind == kind && n.Title.ToLower() == title.ToLower());
        if (existing != null) return existing;

        var node = new KnowledgeNode
        {
            Title = title,
            Kind = kind,
            Source = "created",
            SourceModule = "cosmos",
            UserId = userId
        };
        _context.KnowledgeNodes.Add(node);
        await _context.SaveChangesAsync();
        return node;
    }

    public async Task EnsureLinkAsync(string userId, Guid sourceId, Guid targetId, string kind)
    {
        if (sourceId == targetId) return;
        var exists = await _context.KnowledgeLinks.AnyAsync(l =>
            l.UserId == userId && l.Kind == kind &&
            ((l.SourceId == sourceId && l.TargetId == targetId) ||
             (l.SourceId == targetId && l.TargetId == sourceId)));
        if (exists) return;
        _context.KnowledgeLinks.Add(new KnowledgeLink { SourceId = sourceId, TargetId = targetId, Kind = kind, UserId = userId });
        await _context.SaveChangesAsync();
    }
}
