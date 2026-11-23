using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Research.Api.Data;
using Research.Api.Models;
using Research.Api.Services;

namespace Research.Api.Controllers;

/// <summary>
/// Study Studio: turn a topic (or the user's own notes/report text) into one of
/// 20 student-friendly learning formats — study notes, sticky boards, mind maps,
/// flashcards, quizzes, and more. The model returns structured JSON per renderer
/// archetype; the frontend does all the visuals.
/// </summary>
[Authorize]
[ApiController]
[Route("api/study")]
public class StudyController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly AzureVisionService _ai;
    private readonly WebSearchService _search;
    private readonly JobLogService _jobLog;
    private readonly KnowledgeWeaverService _weaver;
    private readonly BookStorageService _books;
    private readonly ILogger<StudyController> _logger;

    public StudyController(PromptDbContext db, AzureVisionService ai, WebSearchService search, JobLogService jobLog, KnowledgeWeaverService weaver, BookStorageService books, ILogger<StudyController> logger)
    { _db = db; _ai = ai; _search = search; _jobLog = jobLog; _weaver = weaver; _books = books; _logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    private static bool _ensured;
    private async Task EnsureAsync()
    {
        if (_ensured) return;
        try
        {
            var sqlite = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
            var pk = sqlite ? "INTEGER PRIMARY KEY AUTOINCREMENT" : "SERIAL PRIMARY KEY";
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""StudyDocs"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""StyleId"" TEXT NOT NULL, ""Topic"" TEXT,
                ""Title"" TEXT, ""DataJson"" TEXT NOT NULL, ""CreatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_StudyDocs_UserId"" ON ""StudyDocs"" (""UserId"", ""Id"")");
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""InklingDocs"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Title"" TEXT,
                ""ContentJson"" TEXT NOT NULL, ""CreatedAt"" TEXT, ""UpdatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_InklingDocs_UserId"" ON ""InklingDocs"" (""UserId"", ""Id"")");
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""StudyReviews"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""DocId"" INTEGER NOT NULL,
                ""StateJson"" TEXT NOT NULL, ""DueAt"" TEXT, ""UpdatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_StudyReviews_User_Doc"" ON ""StudyReviews"" (""UserId"", ""DocId"")");
            _ensured = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EnsureStudyDocs failed"); }
    }

    // ── Catalog ──────────────────────────────────────────────────────────────

    [HttpGet("styles")]
    public IActionResult Styles() =>
        Ok(StudyStyleCatalog.All.Select(s => new { id = s.Id, label = s.Label, group = s.Group, icon = s.Icon, archetype = s.Archetype, blurb = s.Blurb }));

    // ── Generation ───────────────────────────────────────────────────────────

    public class GenerateRequest
    {
        public string? StyleId { get; set; }
        /// <summary>Topic mode: the subject to teach (the AI supplies the content).</summary>
        public string? Topic { get; set; }
        /// <summary>Source mode: the user's own notes/report text to transform.</summary>
        public string? Content { get; set; }
        /// <summary>Optional audience hint: school | university | professional.</summary>
        public string? Level { get; set; }
        /// <summary>Topic mode only: ground the material with a live web search (default true).</summary>
        public bool UseWebSearch { get; set; } = true;
        /// <summary>Optional learner goal, e.g. "pass Friday's exam" — prioritised across the output.</summary>
        public string? Intent { get; set; }
        /// <summary>Mind maps only: visual style — "creative" | "clean" | null (surprise).</summary>
        public string? Look { get; set; }
        /// <summary>Voice: professional | casual | playful | academic.</summary>
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
            return BadRequest(new { error = "Give a topic to learn, or paste your own material." });
        if (content is { Length: > 60_000 }) content = content[..60_000];

        if (!_ai.IsAvailable) return StatusCode(503, new { error = "AI generation is not available right now." });

        var level = (req.Level ?? "").Trim().ToLowerInvariant() switch
        {
            "school" => "a secondary-school student",
            "professional" => "a busy professional upskilling",
            _ => "a university student",
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
            "You are an expert learning designer creating study material for " + level + ". " +
            "Be accurate, specific and information-dense — every item must teach something real; never pad. " +
            (tone == null ? "" : "TONE: write every label and sentence in a " + tone + " voice. ") +
            (string.IsNullOrEmpty(intent) ? "" :
                "THE LEARNER'S STATED GOAL (prioritise whatever serves it): \"" + intent + "\". ") +
            "STYLE BRIEF: " + style.Instructions + "\n\n" +
            "OUTPUT FORMAT: Return ONLY a minified JSON object with EXACTLY this shape:\n" +
            ArchetypeSchema(style.Archetype) +
            "\nNo commentary, no markdown fences. All strings plain text (no markdown) except '**' item-prefixes where the brief allows.";

        var user = !string.IsNullOrEmpty(content)
            ? (string.IsNullOrEmpty(topic) ? "" : $"Topic: {topic}\n\n") + "Create it from THIS source material (stay faithful to it):\n\n" + content
            : $"Topic to teach: {topic}";

        // Topic mode: ground the material with live web results so facts, figures
        // and recent developments are current — the model's cutoff isn't.
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
                _logger.LogWarning(ex, "Study web search failed — generating without web context");
            }
        }

        var job = await _jobLog.StartAsync(UserId, "study", style.Id, $"{style.Label}: {topic ?? Truncate(content!, 60)}");
        try
        {
            var raw = await _ai.CompleteTextAsync(system, user, maxTokens: 7000);
            var json = ParseStrict(raw, style.Archetype);
            if (json == null)
            {
                await _jobLog.FailAsync(job, "The result did not parse — please try again.");
                return StatusCode(502, new { error = "The AI returned an unusable result — please try again." });
            }

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
            await EnsureAsync();
            var now = DateTime.UtcNow.ToString("o");
            // RETURNING keeps insert + id atomic — MAX(Id) raced under concurrent
            // generations and could hand back another request's doc id.
            // ToListAsync (NOT FirstAsync): LINQ operators compose a SELECT around
            // the SQL, which is invalid for INSERT…RETURNING and throws at runtime.
            var idRows = await _db.Database.SqlQuery<int>($@"
                INSERT INTO ""StudyDocs"" (""UserId"", ""StyleId"", ""Topic"", ""Title"", ""DataJson"", ""CreatedAt"")
                VALUES ({UserId}, {style.Id}, {topic ?? ""}, {title}, {json}, {now})
                RETURNING ""Id"" AS ""Value""").ToListAsync();
            var id = idRows[0];

            await _weaver.CaptureAsync(UserId, title, json, "study", "created", $"study-doc:{id}");

            await _jobLog.CompleteAsync(job, title);
            return Ok(new { id, styleId = style.Id, title, data = JsonSerializer.Deserialize<JsonElement>(json) });
        }
        catch (Exception ex)
        {
            await _jobLog.FailAsync(job, ex.Message);
            _logger.LogError(ex, "Study generation failed for style {Style}", style.Id);
            return StatusCode(502, new { error = "Generation failed — please try again." });
        }
    }

    // ── Import from an uploaded book (Book Reader hand-off) ──────────────────

    /// <summary>
    /// Turns an already-uploaded study/exam guide into study documents.
    ///
    /// The mirror of Recipes.ImportFromBook: opt-in behind the Book Reader's
    /// offer banner, and the book is marked imported so accepting twice cannot
    /// double the library. Each chapter-sized topic becomes its own StudyDoc
    /// in the "notes" archetype — the shape every Study Studio surface
    /// (viewer, reviews, decks) already renders.
    ///
    /// ?mode=cram upgrades the import to a study pack: per topic, one further
    /// AI pass adds flashcards (cards), a practice quiz (worksheet) and — when
    /// the topic is structural — a mermaid diagram. Cards land in the SM-2
    /// review flow automatically because reviews hang off StudyDocs by id.
    /// A topic whose cram pass fails still keeps its notes; cram never turns
    /// a working import into a failed one.
    /// </summary>
    [HttpPost("import-from-book/{bookId:guid}")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> ImportFromBook(Guid bookId, [FromQuery] string? mode, CancellationToken ct)
    {
        var book = await _db.ReaderBooks
            .FirstOrDefaultAsync(b => b.Id == bookId && b.UserId == UserId && b.DeletedAt == null, ct);

        if (book is null) return NotFound(new { error = "Book not found." });
        if (book.Status != "ready")
            return BadRequest(new { error = "This book is still being prepared." });
        if (book.ImportedAt is not null)
            return Conflict(new
            {
                error = $"Already imported {book.ImportedCount} study topics from this book.",
                importedCount = book.ImportedCount,
            });

        var text = await _books.ReadTextAsync($"{bookId}/text.txt", ct);
        if (string.IsNullOrWhiteSpace(text))
            return BadRequest(new { error = "The text for this book is no longer available." });

        if (!_ai.IsAvailable) return StatusCode(503, new { error = "AI generation is not available right now." });

        // One pass over the whole book. 60k chars covers a typical exam guide's
        // teaching content; beyond that the tail is appendices and answer keys.
        if (text.Length > 60_000) text = text[..60_000];

        var system =
            "You are an expert learning designer. The user uploaded a study/exam guide; break it into " +
            "self-contained study topics a learner would revise separately — one per chapter or major theme, " +
            "4 to 10 of them. Stay faithful to the book: teach what IT teaches, never pad with outside filler. " +
            "Every item must carry a real fact, definition, formula, date or distinction from the text. " +
            "OUTPUT FORMAT: Return ONLY minified JSON with EXACTLY this shape:\n" +
            "{\"topics\":[{\"title\":string (the topic, specific, max 60 chars)," +
            "\"sections\":[{\"heading\":string,\"cue\":string (short margin keyword/question),\"items\":string[]}]," +
            "\"summary\":string (2-3 sentences)}]}\n" +
            "No commentary, no markdown fences. All strings plain text.";

        var job = await _jobLog.StartAsync(UserId, "study", "book-import", $"Study import: {book.Title}");
        try
        {
            var raw = await _ai.CompleteTextAsync(system, "THE BOOK TEXT:\n\n" + text, maxTokens: 7000);
            var parsed = ParseTopics(raw);
            if (parsed.Count == 0)
            {
                await _jobLog.FailAsync(job, "No study topics could be read from this book.");
                return Ok(new { imported = 0, message = "No usable study topics could be found in this book." });
            }

            await EnsureAsync();

            // Skip titles the user already has — importing the guide's second
            // edition must not double the library. Same contract as recipes.
            var existing = (await _db.Database.SqlQuery<string>(
                    $@"SELECT ""Title"" AS ""Value"" FROM ""StudyDocs"" WHERE ""UserId""={UserId}").ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var now = DateTime.UtcNow.ToString("o");
            var titles = new List<string>();
            var cram = string.Equals(mode, "cram", StringComparison.OrdinalIgnoreCase);
            var cramDocs = 0;
            foreach (var (title, json) in parsed)
            {
                if (existing.Contains(title)) continue;
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO ""StudyDocs"" (""UserId"", ""StyleId"", ""Topic"", ""Title"", ""DataJson"", ""CreatedAt"")
                    VALUES ({UserId}, {"notebook"}, {book.Title}, {title}, {json}, {now})", ct);
                titles.Add(title);

                // The cram pack: flashcards + quiz (+ diagram) per topic. Each
                // failure is logged and skipped — the notes doc above already
                // landed, and a pack missing one quiz beats an import that
                // rolled back five topics.
                if (!cram) continue;
                foreach (var (styleId, docTitle, docJson) in await BuildCramPackAsync(title, json, ct))
                {
                    if (existing.Contains(docTitle)) continue;
                    await _db.Database.ExecuteSqlInterpolatedAsync($@"
                        INSERT INTO ""StudyDocs"" (""UserId"", ""StyleId"", ""Topic"", ""Title"", ""DataJson"", ""CreatedAt"")
                        VALUES ({UserId}, {styleId}, {book.Title}, {docTitle}, {docJson}, {now})", ct);
                    cramDocs++;
                }
            }

            book.ImportedCount = titles.Count;
            book.ImportedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);

            await _jobLog.CompleteAsync(job, $"{titles.Count} topics from {book.Title}");
            _logger.LogInformation("Imported {Added} study topics from book {BookId} ({Skipped} already present)",
                titles.Count, bookId, parsed.Count - titles.Count);

            return Ok(new
            {
                imported = titles.Count,
                skipped = parsed.Count - titles.Count,
                cramDocs,
                titles = titles.Take(8),
            });
        }
        catch (Exception ex)
        {
            await _jobLog.FailAsync(job, ex.Message);
            _logger.LogError(ex, "Study import failed for book {BookId}", bookId);
            return StatusCode(502, new { error = "The import failed — please try again." });
        }
    }

    /// <summary>
    /// The recall half of a study pack for one topic: flashcards, a practice
    /// quiz, and a diagram when the topic has real structure. Built FROM the
    /// notes doc (not the raw book text) so pack and notes can never disagree
    /// about a fact. Every doc goes through the same ParseStrict gate as
    /// generated docs; anything unusable is dropped, never stored broken.
    /// </summary>
    private async Task<List<(string StyleId, string Title, string Json)>> BuildCramPackAsync(
        string topicTitle, string notesJson, CancellationToken ct)
    {
        var results = new List<(string, string, string)>();

        var system =
            "You are an expert learning designer building recall material from study notes. " +
            "Use ONLY facts present in the notes — invent nothing. " +
            "OUTPUT FORMAT: Return ONLY minified JSON with EXACTLY this shape:\n" +
            "{\"cards\":[{\"front\":string (a question or prompt),\"back\":string (the answer)}] (8-14 of them)," +
            "\"quiz\":[{\"q\":string,\"options\":string[] (4 options, or empty for fill-in-the-blank),\"answer\":string,\"explain\":string}] (5-8 of them)," +
            "\"mermaid\":string (a valid mermaid flowchart/graph capturing the topic's structure, e.g. 'graph TD; A-->B;' — or \"\" when the topic has no real structure to draw)}\n" +
            "Mermaid rules: node labels in square brackets, no parentheses inside labels, \\n for line breaks. " +
            "No commentary, no markdown fences.";

        string raw;
        try
        {
            raw = await _ai.CompleteTextAsync(system, "THE NOTES:\n\n" + notesJson, maxTokens: 4000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cram pass failed for topic {Topic}; keeping notes only", topicTitle);
            return results;
        }

        try
        {
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return results;
            using var doc = JsonDocument.Parse(Sanitize(raw.Substring(start, end - start + 1)), Lenient);
            var root = doc.RootElement;

            if (root.TryGetProperty("cards", out var cards)
                && cards.ValueKind == JsonValueKind.Array && cards.GetArrayLength() >= 3)
            {
                results.Add(("flashcards", $"{topicTitle} — flashcards",
                    JsonSerializer.Serialize(new { title = $"{topicTitle} — flashcards", cards })));
            }

            if (root.TryGetProperty("quiz", out var quiz)
                && quiz.ValueKind == JsonValueKind.Array && quiz.GetArrayLength() >= 3)
            {
                results.Add(("quiz", $"{topicTitle} — practice quiz",
                    JsonSerializer.Serialize(new { title = $"{topicTitle} — practice quiz", questions = quiz })));
            }

            if (root.TryGetProperty("mermaid", out var mermaid)
                && mermaid.ValueKind == JsonValueKind.String
                && mermaid.GetString() is { Length: > 20 } code)
            {
                results.Add(("flowchart", $"{topicTitle} — diagram",
                    JsonSerializer.Serialize(new { title = $"{topicTitle} — diagram", mermaid = code })));
            }
        }
        catch (JsonException)
        {
            _logger.LogWarning("Cram reply for topic {Topic} did not parse; keeping notes only", topicTitle);
        }

        return results;
    }

    /// <summary>
    /// The model's topic list as (title, canonical notes-archetype JSON) pairs.
    /// Each topic is reshaped to the exact document the "notes" viewer renders,
    /// so an imported topic and a generated one are indistinguishable downstream.
    /// </summary>
    private static List<(string Title, string Json)> ParseTopics(string raw)
    {
        var results = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(raw)) return results;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return results;

        try
        {
            using var doc = JsonDocument.Parse(Sanitize(raw.Substring(start, end - start + 1)), Lenient);
            if (!doc.RootElement.TryGetProperty("topics", out var topics)
                || topics.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var topic in topics.EnumerateArray())
            {
                if (!topic.TryGetProperty("title", out var t) || t.ValueKind != JsonValueKind.String) continue;
                var title = t.GetString()!.Trim();
                if (title.Length == 0 || !topic.TryGetProperty("sections", out var s)
                    || s.ValueKind != JsonValueKind.Array || s.GetArrayLength() == 0)
                {
                    continue;
                }

                var docJson = JsonSerializer.Serialize(new
                {
                    title,
                    sections = s,
                    summary = topic.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.String
                        ? sm.GetString() : "",
                });
                results.Add((title, docJson));
            }
        }
        catch (JsonException) { /* an unusable reply imports nothing */ }

        return results;
    }

    // ── Inkling: research + write the source document ────────────────────────
    public class ComposeRequest
    {
        public string? Topic { get; set; }
        public string? Intent { get; set; }
        public string? Tone { get; set; }
        public string? Level { get; set; }
    }

    /// <summary>
    /// Research a topic (live web) and write a substantive Markdown explainer —
    /// the document Inkling users then select passages from to embed visuals.
    /// </summary>
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

        var job = await _jobLog.StartAsync(UserId, "study", "compose", $"Inkling research: {topic}");
        try
        {
            var raw = await _ai.CompleteTextAsync(system, user, maxTokens: 7000);
            var json = ParseStrict(raw, "compose");
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                var title = doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var markdown = doc.RootElement.TryGetProperty("markdown", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                if (!string.IsNullOrWhiteSpace(markdown))
                {
                    await _jobLog.CompleteAsync(job, title ?? topic);
                    return Ok(new { title = title ?? topic, markdown });
                }
            }
            // Model wrote prose instead of JSON — salvage it as the document body.
            if (raw.Trim().Length > 300 && !raw.TrimStart().StartsWith("{"))
            {
                await _jobLog.CompleteAsync(job, topic);
                return Ok(new { title = topic, markdown = raw.Trim() });
            }
            await _jobLog.FailAsync(job, "Unusable result");
            return StatusCode(502, new { error = "The research came back unusable — please try again." });
        }
        catch (Exception ex)
        {
            await _jobLog.FailAsync(job, ex.Message);
            _logger.LogError(ex, "Inkling compose failed for topic {Topic}", topic);
            return StatusCode(502, new { error = "Research failed — please try again." });
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

        var job = await _jobLog.StartAsync(UserId, "study", "deck", $"Inkling deck: {topic ?? Truncate(content!, 60)}");
        try
        {
            var raw = await _ai.CompleteTextAsync(system, user, maxTokens: 8000);
            var json = ParseStrict(raw, "deck");
            if (json == null)
            {
                await _jobLog.FailAsync(job, "The deck did not parse — please try again.");
                return StatusCode(502, new { error = "The deck came back unusable — please try again." });
            }

            var deck = ShapeDeck(json, topic);
            if (deck.Slides.Count == 0)
            {
                await _jobLog.FailAsync(job, "No usable slides");
                return StatusCode(502, new { error = "The deck came back empty — please try again." });
            }

            await _jobLog.CompleteAsync(job, deck.Title);
            return Ok(new { title = deck.Title, subtitle = deck.Subtitle, slides = deck.Slides });
        }
        catch (Exception ex)
        {
            await _jobLog.FailAsync(job, ex.Message);
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

    // ── Inkling saved documents (article + embedded visuals, max 100/user) ───

    public class SaveInklingRequest
    {
        public int? Id { get; set; }
        public string? Title { get; set; }
        /// <summary>Serialized document: {blocks: string[], visuals: {blockIdx: StudyDoc[]}, tone}.</summary>
        public string? Content { get; set; }
    }

    private const int MaxInklingDocs = 100;

    [HttpPost("inkling-docs")]
    public async Task<IActionResult> SaveInklingDoc([FromBody] SaveInklingRequest req)
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
            if (updated > 0)
            {
                await _weaver.CaptureAsync(UserId, title, content, "inkling", "created", $"inkling-doc:{existingId}");
                return Ok(new { id = existingId, savedAt = now });
            }
        }

        var idRows = await _db.Database.SqlQuery<int>($@"
            INSERT INTO ""InklingDocs"" (""UserId"", ""Title"", ""ContentJson"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({UserId}, {title}, {content}, {now}, {now})
            RETURNING ""Id"" AS ""Value""").ToListAsync();

        // Keep the newest 100 per user; older documents roll off.
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM ""InklingDocs"" WHERE ""UserId""={UserId} AND ""Id"" NOT IN (
                SELECT ""Id"" FROM ""InklingDocs"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT {MaxInklingDocs})");

        await _weaver.CaptureAsync(UserId, title, content, "inkling", "created", $"inkling-doc:{idRows[0]}");

        return Ok(new { id = idRows[0], savedAt = now });
    }

    private sealed record InklingListRow(int Id, string? Title, string? UpdatedAt);
    private sealed record InklingFullRow(int Id, string? Title, string ContentJson, string? UpdatedAt);

    [HttpGet("inkling-docs")]
    public async Task<IActionResult> InklingDocs()
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<InklingListRow>($@"
            SELECT ""Id"", ""Title"", ""UpdatedAt"" FROM ""InklingDocs""
            WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT {MaxInklingDocs}").ToListAsync();
        return Ok(rows);
    }

    [HttpGet("inkling-docs/{id:int}")]
    public async Task<IActionResult> InklingDoc(int id)
    {
        await EnsureAsync();
        var row = await _db.Database.SqlQuery<InklingFullRow>($@"
            SELECT ""Id"", ""Title"", ""ContentJson"", ""UpdatedAt"" FROM ""InklingDocs""
            WHERE ""Id""={id} AND ""UserId""={UserId}").FirstOrDefaultAsync();
        if (row == null) return NotFound(new { error = "Not found." });
        return Ok(new { id = row.Id, title = row.Title, updatedAt = row.UpdatedAt, content = JsonSerializer.Deserialize<JsonElement>(row.ContentJson) });
    }

    [HttpDelete("inkling-docs/{id:int}")]
    public async Task<IActionResult> DeleteInklingDoc(int id)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""InklingDocs"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return NoContent();
    }

    // ── Goal suggestions (CreatePrompt-style picker) ─────────────────────────

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
            var json = ParseStrict(raw, "goals");
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
    private static readonly string[] VisualizableStyles =
    {
        "mind-map", "concept-map", "flowchart", "timeline", "venn", "matrix", "infographic",
        "sticky-board", "cheat-sheet", "learning-roadmap", "sequence-diagram", "swimlane",
        "class-diagram", "er-diagram", "state-machine", "architecture", "user-journey", "gantt", "git-flow",
        "cycle", "hierarchy", "pyramid", "funnel", "cause-effect", "decision-tree",
        "relationship-map", "process-steps", "swot", "pros-cons", "kpi-dashboard", "stakeholder-map",
    };

    public class SuggestVisualsRequest { public string? Text { get; set; } }

    /// <summary>
    /// Given a selected piece of text, rank the 6 visual formats that would
    /// represent it best (the "✨ Visualize" flow available across modules).
    /// </summary>
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
            var json = ParseStrict(raw, "suggest");
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

    // ── History ──────────────────────────────────────────────────────────────

    private sealed record DocRow(int Id, string StyleId, string? Topic, string? Title, string? CreatedAt);
    private sealed record DocFullRow(int Id, string StyleId, string? Topic, string? Title, string DataJson, string? CreatedAt);

    [HttpGet("docs")]
    public async Task<IActionResult> Docs()
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<DocRow>($@"
            SELECT ""Id"", ""StyleId"", ""Topic"", ""Title"", ""CreatedAt""
            FROM ""StudyDocs"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 30").ToListAsync();
        return Ok(rows);
    }

    [HttpGet("docs/{id:int}")]
    public async Task<IActionResult> Doc(int id)
    {
        await EnsureAsync();
        var row = await _db.Database.SqlQuery<DocFullRow>($@"
            SELECT ""Id"", ""StyleId"", ""Topic"", ""Title"", ""DataJson"", ""CreatedAt""
            FROM ""StudyDocs"" WHERE ""Id""={id} AND ""UserId""={UserId}").FirstOrDefaultAsync();
        if (row == null) return NotFound(new { error = "Not found." });
        return Ok(new { id = row.Id, styleId = row.StyleId, topic = row.Topic, title = row.Title, createdAt = row.CreatedAt, data = JsonSerializer.Deserialize<JsonElement>(row.DataJson) });
    }

    [HttpDelete("docs/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""StudyDocs"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""StudyReviews"" WHERE ""DocId""={id} AND ""UserId""={UserId}");
        return NoContent();
    }

    // ── Spaced-repetition review state (SM-2 lite; one row per user+doc) ─────

    private sealed record ReviewRow(int DocId, string StateJson, string? DueAt, string? Title, string? StyleId);

    /// <summary>All review states for the user, joined with the doc's title/style
    /// so the frontend can render a "due for review" strip without extra fetches.</summary>
    [HttpGet("reviews")]
    public async Task<IActionResult> Reviews()
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<ReviewRow>($@"
            SELECT r.""DocId"", r.""StateJson"", r.""DueAt"", d.""Title"", d.""StyleId""
            FROM ""StudyReviews"" r
            JOIN ""StudyDocs"" d ON d.""Id"" = r.""DocId"" AND d.""UserId"" = r.""UserId""
            WHERE r.""UserId""={UserId} ORDER BY r.""DueAt"" LIMIT 100").ToListAsync();
        return Ok(rows.Select(r => new
        {
            docId = r.DocId,
            dueAt = r.DueAt,
            title = r.Title,
            styleId = r.StyleId,
            state = TryParse(r.StateJson),
        }));
    }

    public class SaveReviewRequest
    {
        /// <summary>Serialized {items: {cardKey: {e, i, r, due}}} — the SM-2 state per card.</summary>
        public string? StateJson { get; set; }
        /// <summary>Earliest next-due timestamp (ISO), for cheap "what's due" ordering.</summary>
        public string? DueAt { get; set; }
    }

    [HttpPut("reviews/{docId:int}")]
    public async Task<IActionResult> SaveReview(int docId, [FromBody] SaveReviewRequest req)
    {
        var state = req?.StateJson;
        if (string.IsNullOrWhiteSpace(state)) return BadRequest(new { error = "Nothing to save." });
        if (state.Length > 200_000) return BadRequest(new { error = "Review state too large." });
        try { using var _ = JsonDocument.Parse(state); }
        catch (JsonException) { return BadRequest(new { error = "Invalid review state." }); }

        await EnsureAsync();

        // The doc must exist and belong to the caller.
        var owns = await _db.Database.SqlQuery<int>($@"
            SELECT ""Id"" AS ""Value"" FROM ""StudyDocs"" WHERE ""Id""={docId} AND ""UserId""={UserId}").ToListAsync();
        if (owns.Count == 0) return NotFound(new { error = "Not found." });

        var dueAt = req!.DueAt is { Length: > 0 and <= 40 } d ? d : null;
        var now = DateTime.UtcNow.ToString("o");
        var updated = await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""StudyReviews"" SET ""StateJson""={state}, ""DueAt""={dueAt}, ""UpdatedAt""={now}
            WHERE ""UserId""={UserId} AND ""DocId""={docId}");
        if (updated == 0)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""StudyReviews"" (""UserId"", ""DocId"", ""StateJson"", ""DueAt"", ""UpdatedAt"")
                VALUES ({UserId}, {docId}, {state}, {dueAt}, {now})");
        }
        return Ok(new { docId, savedAt = now });
    }

    private static JsonElement? TryParse(string json)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return null; }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ArchetypeSchema(string archetype) => archetype switch
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
    private static string? ParseStrict(string raw, string archetype)
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
                // Canonicalize so stored JSON is always strictly valid.
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

    private static string? TitleOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch { return null; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
