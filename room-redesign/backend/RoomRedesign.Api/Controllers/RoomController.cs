using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using RoomRedesign.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace RoomRedesign.Api.Controllers;

/// <summary>Room Redesign: snap a room and get restyling ideas plus a shopping list in a chosen style.</summary>
[Authorize]
[ApiController]
[Route("api/room")]
public class RoomController : ControllerBase
{
    private readonly AzureVisionService _vision;
    private readonly ILogger<RoomController> _logger;

    public RoomController(AzureVisionService vision, ILogger<RoomController> logger)
    { _vision = vision;_logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    [HttpPost("redesign")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Redesign([FromBody] RoomRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData)) return BadRequest(new { message = "Add a photo of the room." });
        if (req.ImageData.Length > 4_000_000) return BadRequest(new { message = "That photo is too large — try again." });
        if (!_vision.IsAvailable) return StatusCode(503, new { message = "Image analysis is not available right now." });

        var style = string.IsNullOrWhiteSpace(req.Style) ? "a fresh, modern look" : req.Style!;
        var budget = string.IsNullOrWhiteSpace(req.Budget) ? "any budget" : req.Budget!;
        try
        {
            var raw = await _vision.AnalyzeAsync(req.ImageData,
                $@"You are an interior designer. Restyle the room in the photo toward: {style}. Budget: {budget}. Return ONLY JSON (no fences):
{{ ""assessment"":""2 sentences on the room as-is and its potential"", ""ideas"":[""5-7 specific changes: paint colours, layout, lighting, textiles, plants, decor — reference what's actually in the photo""], ""keepItems"":[""things worth keeping""], ""shopping"":[""6-8 specific items to buy to achieve the look""] }}
Be concrete and realistic for the budget. If it's not a room/interior, return {{""error"":""not-room""}}.",
                "Redesign this room.", 1500);
            var j = raw.Trim();
            if (j.StartsWith("```")) j = string.Join("\n", j.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            var s = j.IndexOf('{'); var e = j.LastIndexOf('}'); if (s >= 0 && e > s) j = j.Substring(s, e - s + 1);
            using var doc = JsonDocument.Parse(j);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var er) && er.GetString() == "not-room")
                return BadRequest(new { message = "That doesn't look like a room — try a wider interior photo." });
            string Str(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            string[] Arr(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.Array ? el.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray() : Array.Empty<string>();
            var result = new { assessment = Str("assessment"), ideas = Arr("ideas"), keepItems = Arr("keepItems"), shopping = Arr("shopping") };
            return Ok(result);
        }
        catch (Exception ex) { _logger.LogError(ex, "Room redesign failed"); return StatusCode(500, new { message = "Could not analyze that room." }); }
    }
}

public class RoomRequest
{
    public string? ImageData { get; set; }
    public string? Style { get; set; }
    public string? Budget { get; set; }
}
