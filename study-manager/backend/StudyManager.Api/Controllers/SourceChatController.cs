using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using DocumentFormat.OpenXml.Packaging;
using OpenAI.Chat;
using UglyToad.PdfPig;
using YoutubeExplode;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.ClosedCaptions;
using StudyManager.Api.Data;
using StudyManager.Api.Services;

namespace StudyManager.Api.Controllers;

/// <summary>
/// Chat with a source: upload a PDF / Word / text file or paste a YouTube link,
/// then ask questions about it. Answers come only from the source and point back
/// to where they came from (page number, or video timestamp).
///
/// How it works, without a vector database:
///   1. The source is split into passages of ~1,200 characters, each tagged with
///      its page (documents) or start time (videos), and stored per user.
///   2. For each question, passages are scored by keyword overlap (BM25-style)
///      and the best few — plus the opening passage, for context — go to the
///      model with the question and the recent conversation.
///   3. Small sources (under ~24k characters) are sent whole instead, which is
///      both simpler and more accurate.
/// Works on any configured provider, including a local Ollama model.
/// </summary>
[Authorize]
[ApiController]
[Route("api/sources")]
public class SourceChatController : ControllerBase
{
    private const int MaxUploadBytes = 30 * 1024 * 1024;
    private const int MaxSourceChars = 1_500_000;   // ~600 pages of text
    private const int ChunkChars = 1200;
    private const int WholeSourceChars = 24_000;    // below this, send everything
    private const int PassagesPerAnswer = 8;
    private const int MaxSourcesPerUser = 50;
    private const int HistoryTurns = 6;

    private static readonly Regex YouTubeUrl = new(
        @"^(https?://)?(www\.|m\.|music\.)?(youtube\.com/(watch\?|shorts/|embed/|live/)|youtu\.be/)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly PromptDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<SourceChatController> _logger;

    public SourceChatController(PromptDbContext db, IConfiguration config, ILogger<SourceChatController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Storage (raw SQL tables, created on first use) ───────────────────────

    private static bool _ensured;

    private async Task EnsureAsync()
    {
        if (_ensured) return;
        var sqlite = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
        var pk = sqlite ? "INTEGER PRIMARY KEY AUTOINCREMENT" : "SERIAL PRIMARY KEY";
        await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""ChatSources"" (
            ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Kind"" TEXT NOT NULL, ""Title"" TEXT NOT NULL,
            ""Origin"" TEXT, ""CharCount"" INTEGER NOT NULL, ""ChunksJson"" TEXT NOT NULL, ""CreatedAt"" TEXT NOT NULL)");
        await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_ChatSources_UserId"" ON ""ChatSources"" (""UserId"", ""Id"")");
        await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""ChatSourceMessages"" (
            ""Id"" {pk}, ""SourceId"" INTEGER NOT NULL, ""UserId"" TEXT NOT NULL, ""Role"" TEXT NOT NULL,
            ""Content"" TEXT NOT NULL, ""CitationsJson"" TEXT, ""CreatedAt"" TEXT NOT NULL)");
        await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_ChatSourceMessages_Source"" ON ""ChatSourceMessages"" (""SourceId"", ""Id"")");
        _ensured = true;
    }

    /// <summary>One passage of a source. Page for documents, seconds for videos.</summary>
    public sealed record Chunk(int Index, string Text, int? Page, double? Seconds);

    private sealed record SourceRow(int Id, string Kind, string Title, string? Origin, int CharCount, string CreatedAt);
    private sealed record SourceFullRow(int Id, string Kind, string Title, string? Origin, string ChunksJson);
    private sealed record MessageRow(int Id, string Role, string Content, string? CitationsJson, string CreatedAt);

    // ── Add a source ─────────────────────────────────────────────────────────

    /// <summary>Upload a PDF, Word (.docx), text or Markdown file.</summary>
    [HttpPost("file")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> AddFile(IFormFile file)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "No file was received." });
        if (file.Length > MaxUploadBytes) return BadRequest(new { error = "That file is over 30 MB." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        List<(string Text, int? Page)> pages;
        try
        {
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream);
            stream.Position = 0;
            pages = ext switch
            {
                ".pdf" => ReadPdf(stream),
                ".docx" => ReadDocx(stream),
                ".txt" or ".md" or ".markdown" or ".csv" => new() { (Encoding.UTF8.GetString(stream.ToArray()), null) },
                _ => throw new NotSupportedException("Use a PDF, Word (.docx), text or Markdown file."),
            };
        }
        catch (NotSupportedException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read uploaded source {Name}", file.FileName);
            return BadRequest(new { error = "That file couldn't be read. Is it password-protected or damaged?" });
        }

        var chunks = ChunkPages(pages);
        if (chunks.Count == 0)
            return BadRequest(new { error = "No text was found. Scanned PDFs need OCR first (Document Suite can do that)." });

        var title = Path.GetFileNameWithoutExtension(file.FileName);
        return Ok(await SaveAsync(ext == ".pdf" ? "pdf" : "document", string.IsNullOrWhiteSpace(title) ? "Untitled" : title, null, chunks));
    }

    public sealed record YouTubeRequest(string? Url);

    /// <summary>Paste a YouTube link: its captions become the source.</summary>
    [HttpPost("youtube")]
    public async Task<IActionResult> AddYouTube([FromBody] YouTubeRequest req, CancellationToken ct)
    {
        var url = req?.Url?.Trim() ?? "";
        if (!YouTubeUrl.IsMatch(url)) return BadRequest(new { error = "That doesn't look like a YouTube link." });

        VideoId videoId;
        try { videoId = VideoId.Parse(url); }
        catch { return BadRequest(new { error = "That doesn't look like a YouTube video link." }); }

        try
        {
            var yt = new YoutubeClient();
            var video = await yt.Videos.GetAsync(videoId, ct);
            var manifest = await yt.Videos.ClosedCaptions.GetManifestAsync(videoId, ct);
            if (manifest.Tracks.Count == 0)
                return BadRequest(new { error = "This video has no captions, so there's nothing to read. Try another video." });

            // Prefer English captions written by a person, then auto-generated English, then anything.
            var track = manifest.Tracks
                .OrderBy(t => t.Language.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(t => t.IsAutoGenerated ? 1 : 0)
                .First();
            var captions = await yt.Videos.ClosedCaptions.GetAsync(track, ct);

            var chunks = ChunkCaptions(captions.Captions);
            if (chunks.Count == 0) return BadRequest(new { error = "The captions for this video were empty." });

            var origin = $"https://www.youtube.com/watch?v={videoId.Value}";
            return Ok(await SaveAsync("youtube", $"{video.Title} — {video.Author.ChannelTitle}", origin, chunks));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "YouTube captions failed for {Url}", url);
            return StatusCode(502, new { error = "Couldn't fetch that video's captions. It may be private, age-restricted or region-locked." });
        }
    }

    private async Task<object> SaveAsync(string kind, string title, string? origin, List<Chunk> chunks)
    {
        await EnsureAsync();
        var chars = chunks.Sum(c => c.Text.Length);
        if (title.Length > 200) title = title[..200];
        var json = JsonSerializer.Serialize(chunks);
        var now = DateTime.UtcNow.ToString("o");
        var ids = await _db.Database.SqlQuery<int>($@"
            INSERT INTO ""ChatSources"" (""UserId"", ""Kind"", ""Title"", ""Origin"", ""CharCount"", ""ChunksJson"", ""CreatedAt"")
            VALUES ({UserId}, {kind}, {title}, {origin}, {chars}, {json}, {now})
            RETURNING ""Id"" AS ""Value""").ToListAsync();

        // Keep the newest sources; older ones (and their chats) roll off.
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM ""ChatSourceMessages"" WHERE ""UserId""={UserId} AND ""SourceId"" NOT IN (
                SELECT ""Id"" FROM ""ChatSources"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT {MaxSourcesPerUser})");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            DELETE FROM ""ChatSources"" WHERE ""UserId""={UserId} AND ""Id"" NOT IN (
                SELECT ""Id"" FROM ""ChatSources"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT {MaxSourcesPerUser})");

        return new { id = ids[0], kind, title, origin, charCount = chars, chunks = chunks.Count, createdAt = now };
    }

    // ── Read back ────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List()
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<SourceRow>($@"
            SELECT ""Id"", ""Kind"", ""Title"", ""Origin"", ""CharCount"", ""CreatedAt"" FROM ""ChatSources""
            WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC").ToListAsync();
        return Ok(rows.Select(r => new { id = r.Id, kind = r.Kind, title = r.Title, origin = r.Origin, charCount = r.CharCount, createdAt = r.CreatedAt }));
    }

    [HttpGet("{id:int}/messages")]
    public async Task<IActionResult> Messages(int id)
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<MessageRow>($@"
            SELECT ""Id"", ""Role"", ""Content"", ""CitationsJson"", ""CreatedAt"" FROM ""ChatSourceMessages""
            WHERE ""SourceId""={id} AND ""UserId""={UserId} ORDER BY ""Id""").ToListAsync();
        return Ok(rows.Select(r => new
        {
            id = r.Id, role = r.Role, content = r.Content, createdAt = r.CreatedAt,
            citations = string.IsNullOrEmpty(r.CitationsJson) ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(r.CitationsJson),
        }));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""ChatSourceMessages"" WHERE ""SourceId""={id} AND ""UserId""={UserId}");
        var n = await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""ChatSources"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return n > 0 ? NoContent() : NotFound();
    }

    [HttpDelete("{id:int}/messages")]
    public async Task<IActionResult> ClearChat(int id)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""ChatSourceMessages"" WHERE ""SourceId""={id} AND ""UserId""={UserId}");
        return NoContent();
    }

    // ── Ask ──────────────────────────────────────────────────────────────────

    public sealed record AskRequest(string? Question);

    [HttpPost("{id:int}/ask")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Ask(int id, [FromBody] AskRequest req, CancellationToken ct)
    {
        var question = req?.Question?.Trim() ?? "";
        if (question.Length == 0) return BadRequest(new { error = "Ask a question." });
        if (question.Length > 2000) question = question[..2000];
        if (!AiChatClients.IsConfigured(_config))
            return StatusCode(503, new { error = "No AI provider is set up. Add a Claude, Azure or OpenAI key, or turn on Ollama." });

        await EnsureAsync();
        var src = (await _db.Database.SqlQuery<SourceFullRow>($@"
            SELECT ""Id"", ""Kind"", ""Title"", ""Origin"", ""ChunksJson"" FROM ""ChatSources""
            WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
        if (src is null) return NotFound(new { error = "That source no longer exists." });

        var chunks = JsonSerializer.Deserialize<List<Chunk>>(src.ChunksJson) ?? new();
        var history = (await _db.Database.SqlQuery<MessageRow>($@"
            SELECT ""Id"", ""Role"", ""Content"", ""CitationsJson"", ""CreatedAt"" FROM ""ChatSourceMessages""
            WHERE ""SourceId""={id} AND ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT {HistoryTurns * 2}").ToListAsync())
            .OrderBy(m => m.Id).ToList();

        // Earlier turns help with follow-ups like "what about the second one?".
        var retrievalQuery = question + " " + string.Join(" ", history.Where(h => h.Role == "user").TakeLast(2).Select(h => h.Content));
        var picked = chunks.Sum(c => c.Text.Length) <= WholeSourceChars
            ? chunks
            : SelectPassages(chunks, retrievalQuery, PassagesPerAnswer);

        var video = src.Kind == "youtube";
        var context = new StringBuilder();
        foreach (var c in picked)
            context.Append('[').Append(c.Index + 1).Append("] (").Append(Locator(c, video)).Append(")\n").Append(c.Text).Append("\n\n");

        var system = $"""
            You answer questions about one {(video ? "YouTube video" : "document")}: "{src.Title}".
            Use ONLY the numbered passages below. If they don't contain the answer, say you couldn't find it
            in this {(video ? "video" : "document")} — do not guess or use outside knowledge.
            After each claim, cite the passage numbers you used in square brackets, like [3] or [2][5].
            Be clear and direct. Use short paragraphs or bullet points. Reply in the language of the question.

            PASSAGES
            {context}
            """;

        var messages = new List<ChatMessage> { new SystemChatMessage(system) };
        foreach (var h in history)
            messages.Add(h.Role == "user" ? new UserChatMessage(h.Content) : new AssistantChatMessage(h.Content));
        messages.Add(new UserChatMessage(question));

        string answer;
        try
        {
            var client = AiChatClients.Create(_config);
            var result = await client.CompleteChatAsync(messages, new ChatCompletionOptions { MaxOutputTokenCount = 1500 }, ct);
            answer = result.Value.Content.Count > 0 ? result.Value.Content[0].Text ?? "" : "";
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Source chat failed on {Provider}", AiChatClients.Active(_config));
            return StatusCode(502, new { error = "The AI provider didn't answer. Check its key (or that Ollama is running) and try again." });
        }
        if (string.IsNullOrWhiteSpace(answer)) answer = "I couldn't produce an answer for that. Try rephrasing the question.";

        // Only cite passages the model actually referenced, and that exist.
        var byNumber = picked.ToDictionary(c => c.Index + 1);
        var citations = Regex.Matches(answer, @"\[(\d{1,4})\]")
            .Select(m => int.Parse(m.Groups[1].Value)).Distinct()
            .Where(byNumber.ContainsKey)
            .Select(n =>
            {
                var c = byNumber[n];
                return new
                {
                    n,
                    label = Locator(c, video),
                    page = c.Page,
                    seconds = c.Seconds,
                    link = video && src.Origin is not null && c.Seconds is double s ? $"{src.Origin}&t={(int)s}s" : null,
                    excerpt = c.Text.Length > 280 ? c.Text[..280] + "…" : c.Text,
                };
            })
            .ToList();

        var now = DateTime.UtcNow.ToString("o");
        var citationsJson = JsonSerializer.Serialize(citations, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""ChatSourceMessages"" (""SourceId"", ""UserId"", ""Role"", ""Content"", ""CitationsJson"", ""CreatedAt"")
            VALUES ({id}, {UserId}, 'user', {question}, {(string?)null}, {now})");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""ChatSourceMessages"" (""SourceId"", ""UserId"", ""Role"", ""Content"", ""CitationsJson"", ""CreatedAt"")
            VALUES ({id}, {UserId}, 'assistant', {answer}, {citationsJson}, {now})");

        return Ok(new { answer, citations, provider = AiChatClients.Active(_config) });
    }

    // ── Extraction ───────────────────────────────────────────────────────────

    private static List<(string Text, int? Page)> ReadPdf(Stream stream)
    {
        using var pdf = PdfDocument.Open(stream);
        var pages = new List<(string, int?)>();
        foreach (var page in pdf.GetPages())
        {
            var words = page.GetWords().Select(w => w.Text);
            var text = string.Join(" ", words);
            if (!string.IsNullOrWhiteSpace(text)) pages.Add((text, page.Number));
        }
        return pages;
    }

    private static List<(string Text, int? Page)> ReadDocx(Stream stream)
    {
        using var doc = WordprocessingDocument.Open(stream, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return new();
        var paragraphs = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
            .Select(p => p.InnerText.Trim()).Where(t => t.Length > 0);
        return new() { (string.Join("\n", paragraphs), null) };
    }

    private static List<Chunk> ChunkPages(List<(string Text, int? Page)> pages)
    {
        var chunks = new List<Chunk>();
        var total = 0;
        foreach (var (raw, page) in pages)
        {
            var text = Regex.Replace(raw, @"[ \t]+", " ").Trim();
            for (var start = 0; start < text.Length && total < MaxSourceChars;)
            {
                var len = Math.Min(ChunkChars, text.Length - start);
                // Break on a sentence or word boundary where possible.
                if (start + len < text.Length)
                {
                    var cut = text.LastIndexOfAny(new[] { '.', '!', '?', '\n' }, start + len - 1, Math.Min(len, 400));
                    if (cut <= start) cut = text.LastIndexOf(' ', start + len - 1, Math.Min(len, 200));
                    if (cut > start) len = cut - start + 1;
                }
                var piece = text.Substring(start, len).Trim();
                if (piece.Length > 0) { chunks.Add(new Chunk(chunks.Count, piece, page, null)); total += piece.Length; }
                start += len;
            }
        }
        return chunks;
    }

    private static List<Chunk> ChunkCaptions(IReadOnlyList<ClosedCaption> captions)
    {
        var chunks = new List<Chunk>();
        var buf = new StringBuilder();
        double? startAt = null;
        var total = 0;
        foreach (var cap in captions)
        {
            var t = cap.Text?.Replace('\n', ' ').Trim();
            if (string.IsNullOrEmpty(t)) continue;
            startAt ??= cap.Offset.TotalSeconds;
            buf.Append(t).Append(' ');
            if (buf.Length >= ChunkChars)
            {
                chunks.Add(new Chunk(chunks.Count, buf.ToString().Trim(), null, startAt));
                total += buf.Length; buf.Clear(); startAt = null;
                if (total >= MaxSourceChars) break;
            }
        }
        if (buf.Length > 0) chunks.Add(new Chunk(chunks.Count, buf.ToString().Trim(), null, startAt));
        return chunks;
    }

    private static string Locator(Chunk c, bool video)
    {
        if (video && c.Seconds is double s)
        {
            var t = TimeSpan.FromSeconds(s);
            return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        }
        return c.Page is int p ? $"page {p}" : $"passage {c.Index + 1}";
    }

    // ── Retrieval: keyword scoring (BM25), no embeddings needed ──────────────

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the","a","an","and","or","but","of","to","in","on","for","with","is","are","was","were","be","been","it","its",
        "this","that","these","those","what","which","who","whom","how","why","when","where","does","do","did","about",
        "can","could","should","would","will","i","you","he","she","we","they","me","my","your","our","their","as","at",
        "by","from","into","than","then","there","so","if","not","no","yes","any","all","some","more","most","also","tell",
        "explain","please","video","document","pdf","say","says","said","mention","mentioned",
    };

    private static IEnumerable<string> Terms(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(m => m.Value).Where(w => !Stop.Contains(w))
            .Select(w => w.Length > 4 && w.EndsWith('s') ? w[..^1] : w);

    public static List<Chunk> SelectPassages(List<Chunk> chunks, string query, int k)
    {
        var q = Terms(query).Distinct().ToList();
        if (q.Count == 0 || chunks.Count <= k) return chunks.Take(k).ToList();

        var docs = chunks.Select(c => Terms(c.Text).ToList()).ToList();
        var avgLen = docs.Average(d => (double)Math.Max(1, d.Count));
        var df = q.ToDictionary(t => t, t => docs.Count(d => d.Contains(t)));
        const double k1 = 1.4, b = 0.75;

        var scored = chunks.Select((c, i) =>
        {
            var d = docs[i];
            double score = 0;
            foreach (var t in q)
            {
                var tf = d.Count(w => w == t);
                if (tf == 0) continue;
                var idf = Math.Log(1 + (chunks.Count - df[t] + 0.5) / (df[t] + 0.5));
                score += idf * tf * (k1 + 1) / (tf + k1 * (1 - b + b * d.Count / avgLen));
            }
            return (c, score);
        }).ToList();

        var top = scored.Where(s => s.score > 0).OrderByDescending(s => s.score).Take(k - 1).Select(s => s.c).ToList();
        // The opening passage usually says what the source is about; keep it for context.
        if (!top.Contains(chunks[0])) top.Insert(0, chunks[0]);
        // Nothing matched (a vague question): fall back to the start of the source.
        if (top.Count < 2) top = chunks.Take(k).ToList();
        return top.Distinct().OrderBy(c => c.Index).ToList();
    }
}
