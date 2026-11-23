using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Whiteboard.Api.Data;
using Whiteboard.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace Whiteboard.Api.Controllers;

/// <summary>
/// Whiteboard / Handwriting Digitizer: turn a photo of a whiteboard, sticky notes, or a
/// handwritten page into clean structured markdown plus action items. The photo itself is
/// analyzed in memory and never stored — only the digitized text is kept (last 10) so the
/// user can revisit past boards.
/// </summary>
[Authorize]
[ApiController]
[Route("api/whiteboard")]
public class WhiteboardController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly AzureVisionService _vision;
    private readonly ILogger<WhiteboardController> _logger;

    public WhiteboardController(PromptDbContext db, AzureVisionService vision, ILogger<WhiteboardController> logger)
    { _db = db; _vision = vision;_logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    private static bool _ensured;
    private async Task EnsureAsync()
    {
        if (_ensured) return;
        try
        {
            var sqlite = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
            var pk = sqlite ? "INTEGER PRIMARY KEY AUTOINCREMENT" : "SERIAL PRIMARY KEY";
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""WhiteboardSessions"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Title"" TEXT, ""Markdown"" TEXT,
                ""ActionItems"" TEXT, ""Decisions"" TEXT, ""CreatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_WhiteboardSessions_UserId"" ON ""WhiteboardSessions"" (""UserId"", ""Id"")");
            _ensured = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EnsureWhiteboardSessions failed"); }
    }

    [HttpPost("extract")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Extract([FromBody] WhiteboardRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData)) return BadRequest(new { message = "Add a photo of the whiteboard or notes." });
        if (req.ImageData.Length > 4_000_000) return BadRequest(new { message = "That photo is too large — try a smaller capture." });
        if (!_vision.IsAvailable) return StatusCode(503, new { message = "Image analysis is not available right now." });
        await EnsureAsync();

        try
        {
            var raw = await _vision.AnalyzeAsync(req.ImageData,
                @"You digitize a photo of a whiteboard, sticky-note wall, flipchart, or handwritten page.
Read ALL the text and structure faithfully. Return ONLY JSON (no fences):
{
  ""title"": ""a short title for the notes"",
  ""markdown"": ""the full content as clean GitHub-flavored markdown: use headings, bullet/numbered lists, and describe any diagrams in [square brackets]. Preserve grouping and arrows/relationships as text."",
  ""actionItems"": [""each explicit to-do or action, one per line, with an owner if written""],
  ""decisions"": [""any decisions or conclusions captured""]
}
If there is no readable content, return {""error"":""empty""}.",
                "Digitize this whiteboard / notes into structured markdown.", 2500);

            var json = raw.Trim();
            if (json.StartsWith("```")) json = string.Join("\n", json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            var s = json.IndexOf('{'); var e = json.LastIndexOf('}');
            if (s >= 0 && e > s) json = json.Substring(s, e - s + 1);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var er) && er.GetString() == "empty")
                return BadRequest(new { message = "Couldn't read any notes in that image — try a clearer, straight-on photo." });

            string Str(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            string[] Arr(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.Array
                ? el.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : Array.Empty<string>();

            var title = string.IsNullOrWhiteSpace(Str("title")) ? "Untitled board" : Str("title");
            var markdown = Str("markdown");
            var actions = Arr("actionItems");
            var decisions = Arr("decisions");

            // Persist the digitized TEXT (never the photo) and keep only the last 10.
            int savedId = 0;
            try
            {
                var now = DateTime.UtcNow.ToString("o");
                var actionsJson = JsonSerializer.Serialize(actions);
                var decisionsJson = JsonSerializer.Serialize(decisions);
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    INSERT INTO ""WhiteboardSessions"" (""UserId"", ""Title"", ""Markdown"", ""ActionItems"", ""Decisions"", ""CreatedAt"")
                    VALUES ({UserId}, {title}, {markdown}, {actionsJson}, {decisionsJson}, {now})");
                await _db.Database.ExecuteSqlInterpolatedAsync($@"
                    DELETE FROM ""WhiteboardSessions"" WHERE ""UserId""={UserId} AND ""Id"" NOT IN (
                        SELECT ""Id"" FROM ""WhiteboardSessions"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 10)");
                savedId = (await _db.Database.SqlQuery<int>($@"SELECT ""Id"" AS ""Value"" FROM ""WhiteboardSessions"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 1").ToListAsync()).FirstOrDefault();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Whiteboard save failed"); }


            return Ok(new { id = savedId, title, markdown, actionItems = actions, decisions });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Whiteboard extraction failed");
            return StatusCode(500, new { message = "Could not digitize that image — try a clearer capture." });
        }
    }

    [HttpGet("history")]
    public async Task<IActionResult> History()
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<HistoryRow>($@"
            SELECT ""Id"", ""Title"", ""CreatedAt"" FROM ""WhiteboardSessions""
            WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 10").ToListAsync();
        return Ok(rows);
    }

    [HttpGet("history/{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        await EnsureAsync();
        var row = (await _db.Database.SqlQuery<FullRow>($@"
            SELECT ""Id"", ""Title"", ""Markdown"", ""ActionItems"", ""Decisions"", ""CreatedAt""
            FROM ""WhiteboardSessions"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
        if (row == null) return NotFound(new { message = "Saved board not found." });
        return Ok(new
        {
            row.Id, row.Title,
            markdown = row.Markdown ?? "",
            actionItems = Deserialize(row.ActionItems),
            decisions = Deserialize(row.Decisions),
        });
    }

    [HttpDelete("history/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""WhiteboardSessions"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return Ok(new { message = "Removed." });
    }

    private static string[] Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try { return JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }

    private sealed class HistoryRow { public int Id { get; set; } public string? Title { get; set; } public string? CreatedAt { get; set; } }
    private sealed class FullRow { public int Id { get; set; } public string? Title { get; set; } public string? Markdown { get; set; } public string? ActionItems { get; set; } public string? Decisions { get; set; } public string? CreatedAt { get; set; } }
}

public class WhiteboardRequest
{
    public string? ImageData { get; set; }
}
