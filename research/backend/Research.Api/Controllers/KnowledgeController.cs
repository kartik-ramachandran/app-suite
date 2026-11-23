using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Research.Api.Data;
using Research.Api.Models;
using Research.Api.Services;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace Research.Api.Controllers;

/// <summary>
/// The knowledge cosmos: every piece of information a user dumps, fetches
/// (research/search results), or creates becomes a node. Wiki-links, hashtags
/// and AI-extracted concepts weave the nodes into a graph, and users can draw
/// manual connections on top.
/// </summary>
[Authorize]
[ApiController]
[Route("api/knowledge")]
public class KnowledgeController : ControllerBase
{
    private const int MaxNodesPerUser = 5000;

    private readonly PromptDbContext _context;
    private readonly KnowledgeWeaverService _weaver;
    private readonly KnowledgeBackfillService _backfill;
    private readonly ILogger<KnowledgeController> _logger;
    private readonly IWebHostEnvironment _env;

    public KnowledgeController(
        PromptDbContext context,
        KnowledgeWeaverService weaver,
        KnowledgeBackfillService backfill,
        ILogger<KnowledgeController> logger,
        IWebHostEnvironment env)
    {
        _context = context;
        _weaver = weaver;
        _backfill = backfill;
        _logger = logger;
        _env = env;
    }

    public class RouteRequest
    {
        public Guid NodeId { get; set; }
        /// <summary>Optional user intent, e.g. "research this deeper".</summary>
        public string? Intent { get; set; }
    }

    // Every module is a destination. Text-input modules get the brief prefilled
    // via the cosmos handoff; camera/file-first modules are navigated to directly,
    // landing ready for a snap or upload.
    // Kind: "text" modules take the brief as prefilled input; "app" modules are
    // navigation-only and open ready for a snap, upload, or their own flow.
    private static readonly Dictionary<string, (string Path, string Label, string Kind, string Description)> RouteTargets = new()
    {
        ["research"] = ("/research", "Research", "text", "Deep multi-agent investigation — sourced findings and a written report."),
        ["swarm"] = ("/swarm", "Agent Swarm", "text", "Parallel agents attack one open-ended goal and report back together."),
        ["coordinator"] = ("/coordinator", "Agent Coordinator", "text", "One goal split across coordinated agents working to a shared plan."),
        ["prompts"] = ("/create-prompt", "Prompt Builder", "text", "Turn this into a reusable, tested AI prompt you can save and share."),
        ["study"] = ("/study", "Study Studio", "text", "Learning material — handwritten notes, flashcards, mind maps and 17 more styles."),
        ["social"] = ("/social-pack", "Social Pack", "text", "Ready-to-post copy for LinkedIn, X, Instagram, Facebook, Threads and TikTok."),
        ["agent-chat"] = ("/agent-chat", "Agent Chat", "text", "Open-ended conversation and analysis with an AI agent."),
        ["inkling"] = ("/inkling", "Inkling", "text", "A visual document with AI-drawn diagrams inline in the writing."),
        ["emmd"] = ("/emmd", "Enhanced MMD", "text", "A styled, themed document you can export, seal and verify."),
        ["content-studio"] = ("/content-studio", "Content Studio", "text", "Long-form drafting — articles, structured pieces, publish-ready content."),
        ["chains"] = ("/chain-builder", "Workflow Builder", "text", "Build a repeatable multi-step AI workflow around this."),
        ["travel"] = ("/travel", "Travel", "text", "Trip planning — destinations, guides, itineraries and packing lists."),
        ["gift"] = ("/gift-finder", "Gift Finder", "text", "Gift ideas matched to a person, occasion and budget."),
        ["event-planner"] = ("/event-planner", "Event Planner", "text", "Event checklist, timeline, budget and vendor plan."),
        ["proposal"] = ("/proposal", "Proposal Generator", "text", "A client-ready proposal — scope, deliverables, pricing."),
        ["life-tracker"] = ("/life-tracker", "Life Tracker", "text", "Track this as a personal goal, habit or decision over time."),
        ["media-studio"] = ("/media-studio", "Media Studio", "app", "Generate images and audio. Opens ready for a new creation."),
        ["calendar"] = ("/calendar", "Calendar", "app", "Schedule it — two-way Google and Outlook sync."),
        ["kit-builder"] = ("/kit-builder", "Kit Builder", "app", "Package prompts and personas into a shareable kit."),
        ["prism"] = ("/prism", "Prism", "app", "Clean, reshape and map a data file (CSV, JSON, XLSX, PDF)."),
        ["signal"] = ("/interaction-intelligence", "Signal", "app", "Analyse a call or meeting recording — themes, sentiment, actions."),
        ["invoice"] = ("/invoice-agent", "Invoice Agent", "app", "Read invoices and receipts into itemised, categorised data."),
        ["pdf-tools"] = ("/pdf-tools", "SnapPDF", "app", "Convert, merge, split and compress PDFs."),
        ["plant"] = ("/plant-doctor", "Plant Doctor", "app", "Diagnose a plant from a photo and get a care plan."),
        ["whiteboard"] = ("/whiteboard", "Whiteboard", "app", "Turn a whiteboard photo into structured, editable notes."),
        ["card-scanner"] = ("/card-scanner", "Card Scanner", "app", "Business card photo into saved contact details."),
        ["medicine"] = ("/medicine", "Medicine Cabinet", "app", "Track medicines, doses and expiry from the pack."),
        ["warranty"] = ("/warranty", "Warranty Vault", "app", "Store warranties and receipts with expiry reminders."),
        ["bookshelf"] = ("/bookshelf", "Bookshelf", "app", "Catalogue books from a shelf photo and get reading picks."),
        ["label"] = ("/label-scanner", "Label Scanner", "app", "Read a product label — ingredients, additives, warnings."),
        ["bills"] = ("/bills", "Bill Tracker", "app", "Track bills and due dates from a photo of the bill."),
        ["email-tasks"] = ("/email-tasks", "Email to Tasks", "app", "Turn an email thread into actionable, dated tasks."),
        ["price"] = ("/price-checker", "Price Checker", "app", "Check what a product should cost before you buy."),
        ["room"] = ("/room-redesign", "Room Redesign", "app", "Redesign a room from a photo of the space."),
        ["wardrobe"] = ("/wardrobe", "Wardrobe", "app", "Catalogue clothes and get outfits from what you own."),
        ["kitchen"] = ("/kitchen", "Kitchen", "app", "Fridge or grocery snap into pantry, recipes and a shopping list."),
    };

    /// <summary>
    /// AI routing: given a star and an optional intent, pick which module should
    /// handle it and what input to hand over. The star talks to the app.
    /// </summary>
    [HttpPost("route")]
    public async Task<ActionResult> Route([FromBody] RouteRequest request, [FromServices] IAIService aiService)
    {
        var userId = GetCurrentUserId();
        var node = await _context.KnowledgeNodes.FirstOrDefaultAsync(n => n.Id == request.NodeId && n.UserId == userId);
        if (node == null) return NotFound();

        var content = string.IsNullOrWhiteSpace(node.Content) ? node.Title : node.Content!;
        if (content.Length > 3000) content = content.Substring(0, 3000);

        var moduleKey = "agent-chat";
        var handoffInput = content;
        var goal = string.IsNullOrWhiteSpace(request.Intent) ? node.Title : request.Intent!.Trim();
        var reason = "";
        var mode = "topic";
        try
        {
            var aiResponse = await aiService.TestPromptAsync(new PromptTestRequest
            {
                PromptContent =
                    "You route a piece of the user's saved knowledge to the best module of this app and phrase the work brief for it.\n" +
                    "TEXT modules (the brief is prefilled there):\n" +
                    "- research: investigate a question deeply with multi-agent research\n" +
                    "- swarm: attack an open-ended goal with parallel agents\n" +
                    "- coordinator: assign a shared goal across multiple coordinated agents\n" +
                    "- prompts: turn it into a reusable AI prompt\n" +
                    "- study: turn it into learning material (notes, flashcards, mind maps)\n" +
                    "- social: turn it into social media posts\n" +
                    "- agent-chat: general analysis or discussion\n" +
                    "- inkling: turn it into a visual document with inline AI-drawn diagrams\n" +
                    "- emmd: write it up as a styled, sealable document\n" +
                    "- content-studio: draft an article or structured long-form content\n" +
                    "- chains: build a repeatable multi-step AI workflow around it\n" +
                    "- travel: plan a trip (destinations, packing, guides)\n" +
                    "- gift: find gift ideas for someone\n" +
                    "- event-planner: plan an event with checklist, timeline, budget\n" +
                    "- proposal: turn it into a client-ready proposal\n" +
                    "- life-tracker: track it as a personal goal or decision\n" +
                    "APP modules (navigation only — the module opens ready for a snap, upload, or its own flow):\n" +
                    "- media-studio (create images/audio), calendar (schedule it), kit-builder (package prompts/personas),\n" +
                    "- prism (clean/map a data file), signal (analyse a recording), invoice (analyse invoices/receipts),\n" +
                    "- pdf-tools (convert/merge/split PDFs), plant (diagnose a plant photo), whiteboard (extract notes from a photo),\n" +
                    "- card-scanner, medicine, warranty, bookshelf, label, bills, email-tasks, price, room, wardrobe, kitchen\n\n" +
                    "Prefer TEXT modules when the knowledge itself is the work input; pick an APP module only when the user's intent clearly needs that tool (e.g. \"scan the receipt for this\").\n" +
                    "When the intent is vague or missing, the user almost always wants MORE substance on this star — default to the module that produces the most useful new content for it (research, content-studio, study, inkling), not a bare chat.\n\n" +
                    "Respond with ONLY minified JSON, no code fences:\n" +
                    "{\"module\":\"<key>\",\"goal\":\"<one sentence: the objective of this handoff>\",\"input\":\"<the full brief to prefill in that module, phrased for it — the user will refine this before sending>\",\"reason\":\"<max 12 words: why this module>\",\"mode\":\"<topic if this is a timeless deep-dive, forecast if it is about future scenarios over time>\"}",
                UserInput = $"Knowledge title: {node.Title}\nKnowledge content: {content}\nUser intent: {(string.IsNullOrWhiteSpace(request.Intent) ? "(none given — infer the most useful destination)" : request.Intent)}"
            });
            var raw = (aiResponse.Result ?? "").Trim();
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(raw.Substring(start, end - start + 1));
                if (parsed != null)
                {
                    if (parsed.TryGetValue("module", out var m) && RouteTargets.ContainsKey(m.Trim().ToLowerInvariant()))
                        moduleKey = m.Trim().ToLowerInvariant();
                    if (parsed.TryGetValue("input", out var i) && !string.IsNullOrWhiteSpace(i))
                        handoffInput = i.Trim();
                    if (parsed.TryGetValue("goal", out var g) && !string.IsNullOrWhiteSpace(g))
                        goal = g.Trim();
                    if (parsed.TryGetValue("reason", out var r) && !string.IsNullOrWhiteSpace(r))
                        reason = r.Trim();
                    if (parsed.TryGetValue("mode", out var md) && md.Trim().ToLowerInvariant() == "forecast")
                        mode = "forecast";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cosmos route AI call failed — falling back to agent-chat");
        }

        var (path, label, _, _) = RouteTargets[moduleKey];
        // All valid targets — with what each module does — so the user can
        // override the AI's pick in the refine step and know what they're picking.
        var targets = RouteTargets.Select(t => new
        {
            module = t.Key,
            path = t.Value.Path,
            label = t.Value.Label,
            kind = t.Value.Kind,
            description = t.Value.Description
        });
        return Ok(new { module = moduleKey, path, label, goal, reason, mode, input = handoffInput, title = node.Title, nodeId = node.Id, targets });
    }

    /// <summary>
    /// Import the user's existing content from every module (prompts, chains,
    /// personas, study/inkling docs, garments, receipts, plants, …) into the
    /// cosmos. Idempotent — re-runs update rather than duplicate.
    /// </summary>
    [HttpPost("backfill")]
    public async Task<ActionResult> Backfill()
    {
        var userId = GetCurrentUserId();
        var (imported, byModule) = await _backfill.BackfillAsync(userId);
        return Ok(new { imported, byModule });
    }

    private string GetCurrentUserId() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    // ---------- Graph ----------

    [HttpGet("graph")]
    public async Task<ActionResult> GetGraph()
    {
        var userId = GetCurrentUserId();
        var nodes = await _context.KnowledgeNodes
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.UpdatedAt)
            .Select(n => new
            {
                n.Id,
                n.Title,
                // Trim content for the graph payload; full text comes from GET nodes/{id}
                Content = n.Content == null ? null : (n.Content.Length > 400 ? n.Content.Substring(0, 400) + "…" : n.Content),
                n.Kind,
                n.Source,
                n.SourceModule,
                n.CreatedAt,
                n.UpdatedAt
            })
            .ToListAsync();

        var links = await _context.KnowledgeLinks
            .Where(l => l.UserId == userId)
            .Select(l => new { l.Id, l.SourceId, l.TargetId, l.Kind, l.Label, l.Weight })
            .ToListAsync();

        return Ok(new { nodes, links });
    }

    // ---------- Nodes ----------

    public class IngestRequest
    {
        public string? Title { get; set; }
        public string? Content { get; set; }
        /// <summary>dumped | fetched | created</summary>
        public string? Source { get; set; }
        public string? SourceModule { get; set; }
        public string? SourceRef { get; set; }
        /// <summary>Skip AI concept extraction when false.</summary>
        public bool Enrich { get; set; } = true;
    }

    [HttpPost("nodes")]
    public async Task<ActionResult> Ingest([FromBody] IngestRequest request)
    {
        var userId = GetCurrentUserId();
        var content = (request.Content ?? string.Empty).Trim();
        var title = (request.Title ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(content) && string.IsNullOrEmpty(title))
            return BadRequest(new { message = "Nothing to save — provide content or a title." });

        var nodeCount = await _context.KnowledgeNodes.CountAsync(n => n.UserId == userId);
        if (nodeCount >= MaxNodesPerUser)
            return BadRequest(new { message = $"Cosmos limit of {MaxNodesPerUser} nodes reached." });

        if (string.IsNullOrEmpty(title))
        {
            var firstLine = content.Split('\n')[0].Trim();
            title = firstLine.Length > 80 ? firstLine.Substring(0, 80) + "…" : firstLine;
            if (string.IsNullOrEmpty(title)) title = "Untitled";
        }

        var source = request.Source?.ToLowerInvariant() switch
        {
            "fetched" => "fetched",
            "created" => "created",
            _ => "dumped"
        };

        // If a note with this exact title exists as a stub (created by a wiki-link),
        // fill it instead of duplicating.
        var node = await _context.KnowledgeNodes.FirstOrDefaultAsync(n =>
            n.UserId == userId && n.Kind == "note" && n.Title.ToLower() == title.ToLower() &&
            (n.Content == null || n.Content == ""));

        if (node == null)
        {
            node = new KnowledgeNode { Title = title, UserId = userId };
            _context.KnowledgeNodes.Add(node);
        }
        node.Content = content;
        node.Kind = "note";
        node.Source = source;
        node.SourceModule = request.SourceModule;
        node.SourceRef = request.SourceRef;
        node.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await _weaver.WeaveParsedLinksAsync(node, userId);
        if (request.Enrich) await _weaver.WeaveConceptsAsync(node, userId);

        return Ok(await BuildNodeResponseAsync(node, userId));
    }

    public class UpdateNodeRequest
    {
        public string? Title { get; set; }
        public string? Content { get; set; }
    }

    [HttpGet("nodes/{id}")]
    public async Task<ActionResult> GetNode(Guid id)
    {
        var userId = GetCurrentUserId();
        var node = await _context.KnowledgeNodes.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
        if (node == null) return NotFound();
        return Ok(await BuildNodeResponseAsync(node, userId));
    }

    [HttpPut("nodes/{id}")]
    public async Task<ActionResult> UpdateNode(Guid id, [FromBody] UpdateNodeRequest request)
    {
        var userId = GetCurrentUserId();
        var node = await _context.KnowledgeNodes.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
        if (node == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(request.Title)) node.Title = request.Title.Trim();
        if (request.Content != null) node.Content = request.Content;
        node.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        if (node.Kind == "note" && request.Content != null)
        {
            // Re-derive parsed links (keep concept + manual links intact).
            var stale = await _context.KnowledgeLinks
                .Where(l => l.UserId == userId && l.SourceId == node.Id && (l.Kind == "wiki" || l.Kind == "tag"))
                .ToListAsync();
            _context.KnowledgeLinks.RemoveRange(stale);
            await _context.SaveChangesAsync();
            await _weaver.WeaveParsedLinksAsync(node, userId);
        }

        return Ok(await BuildNodeResponseAsync(node, userId));
    }

    [HttpDelete("nodes/{id}")]
    public async Task<ActionResult> DeleteNode(Guid id)
    {
        var userId = GetCurrentUserId();
        var node = await _context.KnowledgeNodes.FirstOrDefaultAsync(n => n.Id == id && n.UserId == userId);
        if (node == null) return NotFound();

        var links = await _context.KnowledgeLinks
            .Where(l => l.UserId == userId && (l.SourceId == id || l.TargetId == id))
            .ToListAsync();
        var attachments = await _context.FileAttachments
            .Where(f => f.UserId == userId && f.EntityType == "knowledge" && f.EntityId == id.ToString())
            .ToListAsync();

        var uploadsRoot = Path.GetFullPath(Path.Combine(_env.ContentRootPath, "uploads"));
        foreach (var attachment in attachments)
        {
            var filePath = Path.GetFullPath(Path.Combine(uploadsRoot, attachment.StoredFileName));
            if (filePath.StartsWith(uploadsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                System.IO.File.Exists(filePath))
                System.IO.File.Delete(filePath);
        }

        _context.KnowledgeLinks.RemoveRange(links);
        _context.FileAttachments.RemoveRange(attachments);
        _context.KnowledgeNodes.Remove(node);
        await _context.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    // ---------- Manual connections ----------

    public class CreateLinkRequest
    {
        public Guid SourceId { get; set; }
        public Guid TargetId { get; set; }
        public string? Label { get; set; }
    }

    [HttpPost("links")]
    public async Task<ActionResult> CreateLink([FromBody] CreateLinkRequest request)
    {
        var userId = GetCurrentUserId();
        if (request.SourceId == request.TargetId)
            return BadRequest(new { message = "A node cannot link to itself." });

        var bothExist = await _context.KnowledgeNodes
            .CountAsync(n => n.UserId == userId && (n.Id == request.SourceId || n.Id == request.TargetId)) == 2;
        if (!bothExist) return NotFound(new { message = "Node not found." });

        var existing = await _context.KnowledgeLinks.FirstOrDefaultAsync(l =>
            l.UserId == userId &&
            ((l.SourceId == request.SourceId && l.TargetId == request.TargetId) ||
             (l.SourceId == request.TargetId && l.TargetId == request.SourceId)));
        if (existing != null)
        {
            if (!string.IsNullOrWhiteSpace(request.Label)) { existing.Label = request.Label.Trim(); await _context.SaveChangesAsync(); }
            return Ok(new { existing.Id, existing.SourceId, existing.TargetId, existing.Kind, existing.Label, existing.Weight });
        }

        var link = new KnowledgeLink
        {
            SourceId = request.SourceId,
            TargetId = request.TargetId,
            Kind = "manual",
            Label = string.IsNullOrWhiteSpace(request.Label) ? null : request.Label.Trim(),
            Weight = 2,
            UserId = userId
        };
        _context.KnowledgeLinks.Add(link);
        await _context.SaveChangesAsync();
        return Ok(new { link.Id, link.SourceId, link.TargetId, link.Kind, link.Label, link.Weight });
    }

    [HttpDelete("links/{id}")]
    public async Task<ActionResult> DeleteLink(Guid id)
    {
        var userId = GetCurrentUserId();
        var link = await _context.KnowledgeLinks.FirstOrDefaultAsync(l => l.Id == id && l.UserId == userId);
        if (link == null) return NotFound();
        _context.KnowledgeLinks.Remove(link);
        await _context.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private async Task<object> BuildNodeResponseAsync(KnowledgeNode node, string userId)
    {
        var links = await _context.KnowledgeLinks
            .Where(l => l.UserId == userId && (l.SourceId == node.Id || l.TargetId == node.Id))
            .Select(l => new { l.Id, l.SourceId, l.TargetId, l.Kind, l.Label, l.Weight })
            .ToListAsync();
        var entityId = node.Id.ToString();
        var attachments = await _context.FileAttachments
            .Where(f => f.UserId == userId && f.EntityType == "knowledge" && f.EntityId == entityId)
            .OrderBy(f => f.CreatedAt)
            .Select(f => new { f.Id, f.FileName, f.ContentType, f.FileSize, f.CreatedAt })
            .ToListAsync();
        return new
        {
            node.Id,
            node.Title,
            node.Content,
            node.Kind,
            node.Source,
            node.SourceModule,
            node.SourceRef,
            node.CreatedAt,
            node.UpdatedAt,
            links,
            attachments
        };
    }
}
