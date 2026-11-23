using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using InkReel.Api.Data;
using InkReel.Api.Services;

namespace InkReel.Api.Controllers;

/// <summary>
/// InkReel documents — the napkin-style core carried over from the parent app:
/// research a topic (or paste text) into a Markdown document, highlight any
/// passage to get ranked visual suggestions, and generate structured JSON per
/// renderer archetype that the frontend draws inline. Saved documents keep the
/// article blocks and every embedded visual together.
/// </summary>
[Authorize]
[ApiController]
[Route("api/ink")]
public class InklingController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AzureVisionService _ai;
    private readonly WebSearchService _search;
    private readonly ILogger<InklingController> _logger;

    public InklingController(AppDbContext db, AzureVisionService ai, WebSearchService search, ILogger<InklingController> logger)
    { _db = db; _ai = ai; _search = search; _logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    private static bool _ensured;
    private async Task EnsureAsync()
    {
        if (_ensured) return;
        try
        {
            var sqlite = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
            var pk = sqlite ? "INTEGER PRIMARY KEY AUTOINCREMENT" : "SERIAL PRIMARY KEY";
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""InklingDocs"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Title"" TEXT,
                ""ContentJson"" TEXT NOT NULL, ""CreatedAt"" TEXT, ""UpdatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_InklingDocs_UserId"" ON ""InklingDocs"" (""UserId"", ""Id"")");
            _ensured = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EnsureInklingDocs failed"); }
    }

    // ── Catalog ──────────────────────────────────────────────────────────────

    [HttpGet("styles")]
    public IActionResult Styles() =>
        Ok(StudyStyleCatalog.All.Select(s => new { id = s.Id, label = s.Label, group = s.Group, icon = s.Icon, archetype = s.Archetype, blurb = s.Blurb }));

    // ── Compose: research + write the source document ────────────────────────

    public class ComposeRequest
    {
        public string? Topic { get; set; }
        public string? Intent { get; set; }
        public string? Tone { get; set; }
        public string? Level { get; set; }
    }

    [HttpPost("compose")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Compose([FromBody] ComposeRequest req)
    {
        var topic = req?.Topic?.Trim();
        if (string.IsNullOrEmpty(topic)) return BadRequest(new { error = "Give a topic to research." });
        if (topic.Length > 300) topic = topic[..300];
        if (!_ai.IsAvailable) return StatusCode(503, new { error = "AI is not available right now." });

        var intent = req!.Intent?.Trim();
        if (intent is { Length: > 300 }) intent = intent[..300];
        var tone = (req.Tone ?? "").Trim().ToLowerInvariant() switch
        {
            "casual" => "relaxed, friendly and conversational",
            "sketch" => "loose whiteboard style — direct and punchy",
            "playful" => "vivid, energetic and playful (while staying accurate)",
            "academic" => "precise and academic, with correct formal terminology",
            _ => "clear, polished and professional",
        };
        var level = (req.Level ?? "").Trim().ToLowerInvariant() switch
        {
            "school" => "a secondary-school student",
            "university" => "a university student",
            _ => "an intelligent general reader",
        };

        // Deep-ish research: two angles merged so the writing is grounded.
        var webContext = "";
        try
        {
            var a = await _search.SearchAsync(topic, 6);
            var b = await _search.SearchAsync($"{topic} explained in depth key concepts", 5);
            webContext = Truncate(a + "\n\n" + b, 9000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Compose web search failed — writing from model knowledge");
        }

        var system =
            "You are an expert researcher and writer producing the definitive short explainer on a topic for " + level + ". " +
            "Write in a " + tone + " voice. " +
            (string.IsNullOrEmpty(intent) ? "" : "THE READER'S GOAL (prioritise it): \"" + intent + "\". ") +
            "Write 900-1300 words of Markdown: open with a strong one-paragraph overview (no heading), then 4-7 '## ' sections " +
            "with substantive paragraphs (occasional bullet lists are fine) covering how it works, concrete examples with real " +
            "numbers/names where they exist, comparisons or trade-offs, and common misconceptions or pitfalls. Every paragraph " +
            "must teach something specific — no filler, no generic advice. " +
            "OUTPUT FORMAT: Return ONLY minified JSON {\"title\": string, \"markdown\": string} with \\n escapes in the markdown. No fences.";

        var user = $"Topic: {topic}" + (string.IsNullOrEmpty(webContext) ? "" :
            "\n\nLIVE WEB CONTEXT (use for accuracy and freshness; ignore irrelevant results; do NOT cite URLs):\n\n" + webContext);

        try
        {
            var raw = await _ai.CompleteTextAsync(system, user, maxTokens: 7000);
            var json = ParseStrict(raw);
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                var title = doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var markdown = doc.RootElement.TryGetProperty("markdown", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                if (!string.IsNullOrWhiteSpace(markdown))
                    return Ok(new { title = title ?? topic, markdown });
            }
            // Model wrote prose instead of JSON — salvage it as the document body.
            if (raw.Trim().Length > 300 && !raw.TrimStart().StartsWith("{"))
                return Ok(new { title = topic, markdown = raw.Trim() });

            return StatusCode(502, new { error = "The research came back unusable — please try again." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inkling compose failed for topic {Topic}", topic);
            return StatusCode(502, new { error = "Research failed — please try again." });
        }
    }

    // ── Generate one visual (structured JSON per archetype) ──────────────────

    public class GenerateRequest
    {
        public string? StyleId { get; set; }
        public string? Topic { get; set; }
        public string? Content { get; set; }
        public string? Level { get; set; }
        public bool UseWebSearch { get; set; } = false;
        public string? Intent { get; set; }
        public string? Look { get; set; }
        public string? Tone { get; set; }
    }

    [HttpPost("generate")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Generate([FromBody] GenerateRequest req)
    {
        var style = StudyStyleCatalog.Find(req?.StyleId);
        if (style == null) return BadRequest(new { error = "Pick a valid style." });

        var topic = req!.Topic?.Trim();
        var content = req.Content?.Trim();
        if (string.IsNullOrEmpty(topic) && string.IsNullOrEmpty(content))
            return BadRequest(new { error = "Give a topic, or paste your own material." });
        if (content is { Length: > 60_000 }) content = content[..60_000];

        if (!_ai.IsAvailable) return StatusCode(503, new { error = "AI generation is not available right now." });

        var level = (req.Level ?? "").Trim().ToLowerInvariant() switch
        {
            "school" => "a secondary-school student",
            "professional" => "a busy professional upskilling",
            _ => "an intelligent general reader",
        };

        var intent = req.Intent?.Trim();
        if (intent is { Length: > 300 }) intent = intent[..300];

        var tone = (req.Tone ?? "").Trim().ToLowerInvariant() switch
        {
            "casual" => "relaxed, friendly and conversational",
            "sketch" => "loose whiteboard style — short punchy phrases, like quick marker notes",
            "playful" => "vivid, energetic and playful (while staying accurate)",
            "academic" => "precise and academic, with correct formal terminology",
            "professional" => "polished, concise and businesslike",
            _ => null,
        };

        var system =
            "You are an expert visual-communication designer creating material for " + level + ". " +
            "Be accurate, specific and information-dense — every item must teach something real; never pad. " +
            (tone == null ? "" : "TONE: write every label and sentence in a " + tone + " voice. ") +
            (string.IsNullOrEmpty(intent) ? "" :
                "THE READER'S STATED GOAL (prioritise whatever serves it): \"" + intent + "\". ") +
            "STYLE BRIEF: " + style.Instructions + "\n\n" +
            "OUTPUT FORMAT: Return ONLY a minified JSON object with EXACTLY this shape:\n" +
            ArchetypeSchema(style.Archetype) +
            "\nNo commentary, no markdown fences. All strings plain text (no markdown) except '**' item-prefixes where the brief allows.";

        var user = !string.IsNullOrEmpty(content)
            ? (string.IsNullOrEmpty(topic) ? "" : $"Topic: {topic}\n\n") + "Create it from THIS source material (stay faithful to it):\n\n" + content
            : $"Topic: {topic}";

        if (string.IsNullOrEmpty(content) && req.UseWebSearch)
        {
            try
            {
                var webContext = await _search.SearchAsync($"{topic} explained key facts", 6);
                if (!string.IsNullOrWhiteSpace(webContext) && !webContext.StartsWith("No results"))
                {
                    user += "\n\nLIVE WEB CONTEXT (use to keep facts, figures and recent developments accurate; " +
                            "ignore anything irrelevant; do NOT put URLs in the output):\n\n" + Truncate(webContext, 8000);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Generate web search failed — generating without web context");
            }
        }

        try
        {
            var raw = await _ai.CompleteTextAsync(system, user, maxTokens: 7000);
            var json = ParseStrict(raw);
            if (json == null)
                return StatusCode(502, new { error = "The AI returned an unusable result — please try again." });

            // Persist presentation choices inside the doc so history redraws it the same way.
            var toneKey = tone != null ? (req.Tone ?? "").Trim().ToLowerInvariant() : null;
            if ((style.Archetype == "mindmap" && req.Look is "creative" or "clean" or "interactive") || toneKey != null)
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(json)!;
                if (style.Archetype == "mindmap" && req.Look is "creative" or "clean" or "interactive") node["look"] = req.Look;
                if (toneKey != null) node["tone"] = toneKey;
                json = node.ToJsonString();
            }

            var title = TitleOf(json) ?? topic ?? style.Label;
            using var parsed = JsonDocument.Parse(json);
            return Ok(new { id = Random.Shared.Next(1, int.MaxValue), styleId = style.Id, title, data = parsed.RootElement.Clone() });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inkling generate failed for style {Style}", style.Id);
            return StatusCode(502, new { error = "Generation failed — please try again." });
        }
    }

    // ── Inkling: build a deck ────────────────────────────────────────────────

    public class DeckRequest
    {
        public string? Topic { get; set; }
        /// <summary>Own material to turn into slides, instead of researching a topic.</summary>
        public string? Content { get; set; }
        public string? Intent { get; set; }
        public string? Tone { get; set; }
        public string? Level { get; set; }
        /// <summary>How many content slides to aim for. Clamped to 5-20.</summary>
        public int? Slides { get; set; }
    }

    /// <summary>
    /// Turn the same prompt Inkling researches a document from into a deck.
    ///
    /// The slides come back as text, deliberately. Every slide also carries a
    /// nominated visual -- what a diagram of this slide would show, and which
    /// style would suit it -- so swapping a slide's bullets for a drawn visual
    /// is one click and reuses exactly the same generator the inline visuals
    /// use. A deck where every slide is a picture is as unreadable as one where
    /// none is, so the model nominates visuals rather than insisting on them.
    /// </summary>
    [HttpPost("deck")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Deck([FromBody] DeckRequest req)
    {
        var topic = req?.Topic?.Trim();
        var content = req?.Content?.Trim();
        if (string.IsNullOrEmpty(topic) && string.IsNullOrEmpty(content))
            return BadRequest(new { error = "Give a topic to present, or paste your own material." });
        if (topic is { Length: > 300 }) topic = topic[..300];
        if (content is { Length: > 60_000 }) content = content[..60_000];
        if (!_ai.IsAvailable) return StatusCode(503, new { error = "AI is not available right now." });

        var count = Math.Clamp(req?.Slides ?? 10, 5, 20);

        var intent = req?.Intent?.Trim();
        if (intent is { Length: > 300 }) intent = intent[..300];

        var tone = (req?.Tone ?? "").Trim().ToLowerInvariant() switch
        {
            "casual" => "relaxed and conversational",
            "sketch" => "loose whiteboard style — short punchy phrases",
            "playful" => "vivid and energetic (while staying accurate)",
            "academic" => "precise and academic, with correct formal terminology",
            _ => "clear, polished and businesslike",
        };
        var level = (req?.Level ?? "").Trim().ToLowerInvariant() switch
        {
            "school" => "a secondary-school audience",
            "university" => "a university audience",
            _ => "an informed professional audience",
        };

        // Same grounding as the document path: a deck of stale facts is worse
        // than a document of them, because nobody fact-checks a slide.
        var webContext = "";
        if (string.IsNullOrEmpty(content) && !string.IsNullOrEmpty(topic))
        {
            try
            {
                var a = await _search.SearchAsync(topic, 6);
                var b = await _search.SearchAsync($"{topic} key facts figures examples", 5);
                webContext = Truncate(a + "\n\n" + b, 9000);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deck web search failed — building from model knowledge");
            }
        }

        var styleMenu = string.Join(", ", StudyStyleCatalog.All
            .Where(x => VisualizableStyles.Contains(x.Id))
            .Select(x => x.Id));

        var system =
            "You are a senior presentation designer building a talk for " + level + ". " +
            "Write in a " + tone + " voice. " +
            (string.IsNullOrEmpty(intent) ? "" : "THE PRESENTER'S GOAL (prioritise it): \"" + intent + "\". ") +
            $"Produce exactly {count} content slides after the title slide. " +
            "RULES: each slide makes ONE point, stated in its title as a full claim, not a label like 'Overview'. " +
            "3-5 bullets per slide, each under 14 words, each carrying a specific fact, number, name or consequence — " +
            "never filler, never a bullet that only restates the title. Speaker notes are 2-3 full sentences the " +
            "presenter could say aloud. " +
            "For every slide also nominate the visual that would replace its bullets best: pick styleId from this list — " +
            styleMenu + " — and write a one-line brief of what that visual should show. If a slide is genuinely better " +
            "as words, set styleId to \"\". Aim for roughly half the slides nominating a visual. " +
            "OUTPUT FORMAT: Return ONLY minified JSON with EXACTLY this shape: " +
            "{\"title\":string,\"subtitle\":string,\"slides\":[{\"title\":string,\"bullets\":[string],\"notes\":string," +
            "\"visual\":{\"styleId\":string,\"brief\":string}}]}. No commentary, no markdown fences.";

        var user = !string.IsNullOrEmpty(content)
            ? (string.IsNullOrEmpty(topic) ? "" : $"Working title: {topic}\n\n") +
              "Build the deck from THIS material (stay faithful to it):\n\n" + content
            : $"Topic to present: {topic}" + (string.IsNullOrEmpty(webContext) ? "" :
                "\n\nLIVE WEB CONTEXT (use for accuracy and freshness; ignore irrelevant results; do NOT cite URLs):\n\n" + webContext);

        try
        {
            var raw = await _ai.CompleteTextAsync(system, user, maxTokens: 8000);
            var json = ParseStrict(raw);
            if (json == null)
            {
                return StatusCode(502, new { error = "The deck came back unusable — please try again." });
            }

            var deck = ShapeDeck(json, topic);
            if (deck.Slides.Count == 0)
            {
                return StatusCode(502, new { error = "The deck came back empty — please try again." });
            }

            return Ok(new { title = deck.Title, subtitle = deck.Subtitle, slides = deck.Slides });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inkling deck failed for {Topic}", topic);
            return StatusCode(502, new { error = "Building the deck failed — please try again." });
        }
    }

    private sealed record DeckShape(string Title, string Subtitle, List<object> Slides);

    /// <summary>
    /// Take only what we asked for. A model that invents a styleId, returns a
    /// bullet as a paragraph, or writes twelve of them should not reach the deck
    /// editor — the slide has no room for any of it.
    /// </summary>
    private static DeckShape ShapeDeck(string json, string? fallbackTitle)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        static string Text(JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";

        var slides = new List<object>();
        if (root.TryGetProperty("slides", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in arr.EnumerateArray())
            {
                var title = Text(s, "title");
                if (string.IsNullOrWhiteSpace(title)) continue;

                var bullets = new List<string>();
                if (s.TryGetProperty("bullets", out var b) && b.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in b.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        var t = (item.GetString() ?? "").Trim();
                        if (t.Length == 0) continue;
                        bullets.Add(t.Length > 220 ? t[..220] : t);
                        if (bullets.Count == 6) break;
                    }
                }

                string styleId = "", brief = "";
                if (s.TryGetProperty("visual", out var v) && v.ValueKind == JsonValueKind.Object)
                {
                    styleId = Text(v, "styleId");
                    brief = Text(v, "brief");
                    if (!VisualizableStyles.Contains(styleId)) { styleId = ""; brief = ""; }
                }

                slides.Add(new
                {
                    title = title.Length > 160 ? title[..160] : title,
                    bullets,
                    notes = Text(s, "notes"),
                    visual = new { styleId, brief },
                });
                if (slides.Count == 20) break;
            }
        }

        var deckTitle = Text(root, "title");
        if (string.IsNullOrWhiteSpace(deckTitle)) deckTitle = fallbackTitle ?? "Untitled deck";
        return new DeckShape(deckTitle, Text(root, "subtitle"), slides);
    }

    public class SuggestGoalsRequest
    {
        public string? Topic { get; set; }
        public string? Content { get; set; }
        public List<string>? Existing { get; set; }
    }

    /// <summary>
    /// Suggest ~12 goal options for what the visual/study artifact should achieve,
    /// grounded in the topic or pasted text — same UX as the prompt builder's
    /// goal picker. Pass already-shown goals in `existing` for a fresh batch.
    /// </summary>
    [HttpPost("suggest-goals")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> SuggestGoals([FromBody] SuggestGoalsRequest req)
    {
        var source = !string.IsNullOrWhiteSpace(req?.Content) ? req!.Content!.Trim()
                   : req?.Topic?.Trim();
        if (string.IsNullOrEmpty(source)) return BadRequest(new { error = "Give a topic or some text first." });
        if (source.Length > 5000) source = source[..5000];
        if (!_ai.IsAvailable) return StatusCode(503, new { error = "AI is not available right now." });

        var existing = (req!.Existing ?? new List<string>()).Take(40).ToList();
        var system =
            "You help someone decide what they want a generated visual or study artifact to achieve. " +
            "Based on the input, suggest 12 SHORT goal options (max 8 words each, action-oriented, SPECIFIC to this input — " +
            "e.g. 'Highlight the decision points', 'Make it exam-ready', 'Compare the two approaches', 'Focus on the risks'). " +
            (existing.Count > 0 ? "Do NOT repeat any of these already-shown goals: " + string.Join(" | ", existing) + ". " : "") +
            "Return ONLY minified JSON: {\"goals\": string[]}. No commentary.";

        try
        {
            var raw = await _ai.CompleteTextAsync(system, source, maxTokens: 600);
            var json = ParseStrict(raw);
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("goals", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    var goals = arr.EnumerateArray()
                        .Where(g => g.ValueKind == JsonValueKind.String)
                        .Select(g => g.GetString()!.Trim())
                        .Where(g => g.Length is > 0 and <= 80)
                        .Take(12)
                        .ToList();
                    if (goals.Count > 0) return Ok(new { goals });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "suggest-goals failed — returning defaults");
        }
        return Ok(new
        {
            goals = new[]
            {
                "Keep it simple and scannable", "Highlight the key decisions", "Make it presentation-ready",
                "Focus on what beginners miss", "Show how the pieces connect", "Make it exam-ready",
            },
        });
    }

    // ── Napkin-style visual suggestions ──────────────────────────────────────

    /// <summary>Diagram-capable styles the suggester may recommend for a text selection.</summary>
    internal static readonly string[] VisualizableStyles =
    {
        "mind-map", "concept-map", "flowchart", "timeline", "venn", "matrix", "infographic",
        "sticky-board", "cheat-sheet", "learning-roadmap", "sequence-diagram", "swimlane",
        "class-diagram", "er-diagram", "state-machine", "architecture", "user-journey", "gantt", "git-flow",
        "cycle", "hierarchy", "pyramid", "funnel", "cause-effect", "decision-tree",
        "relationship-map", "process-steps", "swot", "pros-cons", "kpi-dashboard", "stakeholder-map",
    };

    public class SuggestVisualsRequest { public string? Text { get; set; } }

    [HttpPost("suggest-visuals")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> SuggestVisuals([FromBody] SuggestVisualsRequest req)
    {
        var text = req?.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return BadRequest(new { error = "Select some text first." });
        if (text.Length > 6000) text = text[..6000];
        if (!_ai.IsAvailable) return StatusCode(503, new { error = "AI is not available right now." });

        var menu = string.Join("\n", StudyStyleCatalog.All
            .Where(s => VisualizableStyles.Contains(s.Id))
            .Select(s => $"- {s.Id}: {s.Blurb}"));

        var system =
            "You match a piece of text to the visual formats that would represent it best. Available formats:\n" + menu +
            "\n\nPick exactly 6 formats whose structure genuinely fits the text's content (processes → flowchart/swimlane/process-steps, " +
            "comparisons → venn/matrix/pros-cons, repeating systems → cycle, causes → cause-effect, strategy → swot, " +
            "sequences/history → timeline, components → mind-map/architecture/hierarchy, data models → er-diagram, etc.). " +
            "Rank best first. Return ONLY minified JSON: {\"suggestions\":[{\"styleId\": string (one of the ids above), " +
            "\"reason\": string (max 8 words, e.g. 'step-by-step process with decisions')}]}. No commentary.";

        try
        {
            var raw = await _ai.CompleteTextAsync(system, text, maxTokens: 400);
            var json = ParseStrict(raw);
            if (json == null) return Ok(new { suggestions = FallbackSuggestions() });

            using var doc = JsonDocument.Parse(json);
            var list = new List<object>();
            if (doc.RootElement.TryGetProperty("suggestions", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in arr.EnumerateArray())
                {
                    var id = s.TryGetProperty("styleId", out var i) ? i.GetString() : null;
                    if (id == null || !VisualizableStyles.Contains(id)) continue;
                    var reason = s.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
                    list.Add(new { styleId = id, reason });
                    if (list.Count == 6) break;
                }
            }
            return Ok(new { suggestions = list.Count > 0 ? list : FallbackSuggestions() });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "suggest-visuals failed — returning defaults");
            return Ok(new { suggestions = FallbackSuggestions() });
        }
    }

    private static List<object> FallbackSuggestions() => new()
    {
        new { styleId = "mind-map", reason = "key ideas at a glance" },
        new { styleId = "flowchart", reason = "as a process" },
        new { styleId = "cycle", reason = "as a repeating loop" },
        new { styleId = "cause-effect", reason = "causes leading to outcomes" },
        new { styleId = "sticky-board", reason = "one idea per note" },
        new { styleId = "timeline", reason = "in sequence" },
    };

    // ── Saved documents (article + embedded visuals, max 100/user) ───────────

    public class SaveDocRequest
    {
        public int? Id { get; set; }
        public string? Title { get; set; }
        /// <summary>Serialized document: {blocks: string[], visuals: {blockIdx: slots}, tone}.</summary>
        public string? Content { get; set; }
    }

    private const int MaxDocs = 100;

    [HttpPost("docs")]
    public async Task<IActionResult> SaveDoc([FromBody] SaveDocRequest req)
    {
        var content = req?.Content;
        if (string.IsNullOrWhiteSpace(content)) return BadRequest(new { error = "Nothing to save." });
        if (content.Length > 2_000_000) return BadRequest(new { error = "This document is too large to save." });
        try { using var _ = JsonDocument.Parse(content); }
        catch (JsonException) { return BadRequest(new { error = "Invalid document payload." }); }

        var title = (req!.Title ?? "").Trim();
        if (title.Length == 0) title = "Untitled document";
        if (title.Length > 200) title = title[..200];

        await EnsureAsync();
        var now = DateTime.UtcNow.ToString("o");

        if (req.Id is int existingId)
        {
            var updated = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE ""InklingDocs"" SET ""Title""={title}, ""ContentJson""={content}, ""UpdatedAt""={now}
                WHERE ""Id""={existingId} AND ""UserId""={UserId}");
            if (updated > 0) return Ok(new { id = existingId });
        }

        var count = (await _db.Database.SqlQuery<int>(
            $@"SELECT COUNT(*) AS ""Value"" FROM ""InklingDocs"" WHERE ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
        if (count >= MaxDocs)
            return BadRequest(new { error = $"You have {MaxDocs} saved documents — delete some to save new ones." });

        var ids = await _db.Database.SqlQuery<int>($@"
            INSERT INTO ""InklingDocs"" (""UserId"", ""Title"", ""ContentJson"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({UserId}, {title}, {content}, {now}, {now})
            RETURNING ""Id"" AS ""Value""").ToListAsync();
        return Ok(new { id = ids[0] });
    }

    private sealed record InkDocRow(int Id, string? Title, string? UpdatedAt);

    [HttpGet("docs")]
    public async Task<IActionResult> Docs()
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<InkDocRow>($@"
            SELECT ""Id"", ""Title"", ""UpdatedAt""
            FROM ""InklingDocs"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT {MaxDocs}").ToListAsync();
        return Ok(rows.Select(r => new { id = r.Id, title = r.Title, updatedAt = r.UpdatedAt }));
    }

    [HttpGet("docs/{id:int}")]
    public async Task<IActionResult> Doc(int id)
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<string?>($@"
            SELECT ""ContentJson"" AS ""Value"" FROM ""InklingDocs"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync();
        var contentJson = rows.FirstOrDefault();
        if (string.IsNullOrEmpty(contentJson)) return NotFound(new { error = "Document not found." });
        var titleRows = await _db.Database.SqlQuery<string?>($@"
            SELECT ""Title"" AS ""Value"" FROM ""InklingDocs"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync();
        using var doc = JsonDocument.Parse(contentJson);
        return Ok(new { id, title = titleRows.FirstOrDefault(), content = doc.RootElement.Clone() });
    }

    [HttpDelete("docs/{id:int}")]
    public async Task<IActionResult> DeleteDoc(int id)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""InklingDocs"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Deleted." });
    }

    // ── Shared parsing helpers (same rules as the parent app) ────────────────

    internal static string ArchetypeSchema(string archetype) => archetype switch
    {
        "notes" => "{\"title\": string, \"sections\": [{\"heading\": string, \"cue\": string (short margin keyword/question), \"items\": string[]}], \"summary\": string}",
        "board" => "{\"title\": string, \"notes\": [{\"text\": string, \"color\": \"yellow\"|\"pink\"|\"blue\"|\"green\"|\"orange\"}]}",
        "sheet" => "{\"title\": string, \"boxes\": [{\"heading\": string, \"items\": string[]}]}",
        "diagram" => "{\"title\": string, \"mermaid\": string (valid mermaid code as instructed, with \\n newlines)}",
        "mindmap" => "{\"title\": string, \"root\": string (2-4 words), \"branches\": [{\"label\": string, \"children\": [{\"label\": string, \"subitems\": string[] (empty array if none)}]}]}",
        "timeline" => "{\"title\": string, \"events\": [{\"label\": string (date or stage), \"heading\": string, \"detail\": string}]}",
        "venn" => "{\"title\": string, \"aLabel\": string, \"bLabel\": string, \"aOnly\": string[], \"bOnly\": string[], \"shared\": string[]}",
        "matrix" => "{\"title\": string, \"columns\": string[], \"rows\": [{\"label\": string, \"cells\": string[] (same length as columns)}]}",
        "cards" => "{\"title\": string, \"cards\": [{\"front\": string, \"back\": string}]}",
        "worksheet" => "{\"title\": string, \"questions\": [{\"q\": string, \"options\": string[] (empty for fill-in-the-blank), \"answer\": string, \"explain\": string}]}",
        _ => "{\"title\": string, \"sections\": [{\"heading\": string, \"items\": string[]}]}",
    };

    private static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Extract, sanitize, and validate the model's JSON; returns canonical JSON or null.</summary>
    internal static string? ParseStrict(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var json = raw.Substring(start, end - start + 1);

        foreach (var candidate in new[] { json, Sanitize(json) })
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate, Lenient);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                return JsonSerializer.Serialize(doc.RootElement);
            }
            catch (JsonException) { /* next candidate */ }
        }
        return null;
    }

    private static string Sanitize(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length);
        foreach (var ch in json)
        {
            if (ch == '“' || ch == '”') sb.Append('"');
            else if (ch == '‘' || ch == '’') sb.Append('\'');
            else if (ch == '\n' || ch == '\r' || ch == '\t') sb.Append(' ');
            else if (ch < ' ') sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    internal static string? TitleOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch { return null; }
    }

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
