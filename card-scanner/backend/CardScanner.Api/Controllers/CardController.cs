using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using CardScanner.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace CardScanner.Api.Controllers;

/// <summary>
/// Business Card → Contact: snap a business card and get the structured contact fields
/// back, ready to save as a vCard. Zero-storage — analyzed in memory, never persisted.
/// </summary>
[Authorize]
[ApiController]
[Route("api/cards")]
public class CardController : ControllerBase
{
    private readonly AzureVisionService _vision;
    private readonly ILogger<CardController> _logger;

    public CardController(AzureVisionService vision, ILogger<CardController> logger)
    {
        _vision = vision;_logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    [HttpPost("scan")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Scan([FromBody] CardRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData)) return BadRequest(new { message = "Add a photo of the business card." });
        if (req.ImageData.Length > 3_000_000) return BadRequest(new { message = "That photo is too large — try again." });
        if (!_vision.IsAvailable) return StatusCode(503, new { message = "Image analysis is not available right now." });

        try
        {
            var raw = await _vision.AnalyzeAsync(req.ImageData,
                @"You extract contact details from a photo of a business card. Return ONLY JSON (no fences):
{
  ""name"": ""full name"",
  ""title"": ""job title"",
  ""company"": ""company name"",
  ""phone"": ""primary phone"",
  ""mobile"": ""mobile if separate"",
  ""email"": ""email"",
  ""website"": ""website"",
  ""address"": ""postal address on one line""
}
Use empty strings for anything not present. If it isn't a business card, return {""error"":""not-card""}.",
                "Extract the contact details from this business card.", 500);

            var json = raw.Trim();
            if (json.StartsWith("```")) json = string.Join("\n", json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            var s = json.IndexOf('{'); var e = json.LastIndexOf('}');
            if (s >= 0 && e > s) json = json.Substring(s, e - s + 1);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("error", out var er) && er.GetString() == "not-card")
                return BadRequest(new { message = "That doesn't look like a business card — try a clear, flat photo." });
            string S(string p) => r.TryGetProperty(p, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
            var contactName = S("name");
            return Ok(new
            {
                name = contactName, title = S("title"), company = S("company"),
                phone = S("phone"), mobile = S("mobile"), email = S("email"),
                website = S("website"), address = S("address"),
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Card scan failed");
            return StatusCode(500, new { message = "Could not read that card — try a clearer photo." });
        }
    }
}

public class CardRequest
{
    public string? ImageData { get; set; }
}
