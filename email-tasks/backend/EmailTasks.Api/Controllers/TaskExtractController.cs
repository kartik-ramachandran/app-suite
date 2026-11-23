using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using EmailTasks.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace EmailTasks.Api.Controllers;

/// <summary>Email → Tasks &amp; Events: screenshot an email/message and pull out to-dos and events.</summary>
[Authorize]
[ApiController]
[Route("api/tasks")]
public class TaskExtractController : ControllerBase
{
    private readonly AzureVisionService _vision;
    private readonly ILogger<TaskExtractController> _logger;

    public TaskExtractController(AzureVisionService vision, ILogger<TaskExtractController> logger)
    { _vision = vision;_logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    [HttpPost("extract")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Extract([FromBody] ImageOnlyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData)) return BadRequest(new { message = "Add a screenshot of the email or message." });
        if (req.ImageData.Length > 4_000_000) return BadRequest(new { message = "That image is too large — try again." });
        if (!_vision.IsAvailable) return StatusCode(503, new { message = "Image analysis is not available right now." });

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var raw = await _vision.AnalyzeAsync(req.ImageData,
                $@"You read a screenshot of an email, chat, or message and pull out anything actionable. Today is {today}. Return ONLY JSON (no fences):
{{ ""summary"":""one-line summary of the message"", ""tasks"":[""each to-do / request, imperative, one per line""], ""events"":[{{""title"":""..."",""date"":""yyyy-MM-dd"",""time"":""HH:mm or empty"",""location"":""or empty""}}] }}
Resolve relative dates against today. If nothing actionable, return empty arrays.",
                "Extract the tasks and events.", 1200);
            var j = raw.Trim();
            if (j.StartsWith("```")) j = string.Join("\n", j.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            var s = j.IndexOf('{'); var e = j.LastIndexOf('}'); if (s >= 0 && e > s) j = j.Substring(s, e - s + 1);
            using var doc = JsonDocument.Parse(j);
            var r = doc.RootElement;
            string Str(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            string[] Arr(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.Array ? el.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : Array.Empty<string>();
            var events = r.TryGetProperty("events", out var ev) ? ev.Clone() : (JsonElement?)null;
            var summary = Str("summary");
            return Ok(new { summary, tasks = Arr("tasks"), events });
        }
        catch (Exception ex) { _logger.LogError(ex, "Task extract failed"); return StatusCode(500, new { message = "Could not read that screenshot." }); }
    }
}
