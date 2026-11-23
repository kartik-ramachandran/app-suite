using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using PriceChecker.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace PriceChecker.Api.Controllers;

/// <summary>Price / Deal Checker: snap a product, identify it, and web-search for better prices.</summary>
[Authorize]
[ApiController]
[Route("api/price")]
public class PriceController : ControllerBase
{
    private readonly AzureVisionService _vision;
    private readonly WebSearchService _search;
    private readonly ILogger<PriceController> _logger;

    public PriceController(AzureVisionService vision, WebSearchService search, ILogger<PriceController> logger)
    { _vision = vision; _search = search;_logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    [HttpPost("check")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Check([FromBody] ImageOnlyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData)) return BadRequest(new { message = "Add a photo of the product." });
        if (req.ImageData.Length > 3_000_000) return BadRequest(new { message = "That photo is too large — try again." });
        if (!_vision.IsAvailable) return StatusCode(503, new { message = "Image analysis is not available right now." });

        // 1. Identify the product from the photo.
        string product;
        try
        {
            product = (await _vision.AnalyzeAsync(req.ImageData,
                "Identify the exact product in this photo (brand + model + key spec) in a few words only. Reply with just the product name, nothing else.",
                "What product is this?", 60)).Trim().Trim('"');
        }
        catch (Exception ex) { _logger.LogError(ex, "Price identify failed"); return StatusCode(500, new { message = "Could not identify the product." }); }
        if (string.IsNullOrWhiteSpace(product)) return BadRequest(new { message = "Couldn't identify the product — try a clearer photo." });

        // 2. Web-search for prices, then summarise.
        var web = "";
        try { web = await _search.SearchAsync($"{product} price buy", 6); } catch (Exception ex) { _logger.LogWarning(ex, "Price search failed"); }

        try
        {
            var summary = await _vision.CompleteTextAsync(
                @"You are a savvy shopping assistant. Given a product and web search results, summarise where to buy it and the best deals. Return ONLY JSON (no fences):
{ ""product"":""confirmed product name"", ""priceRange"":""typical price range if determinable, else 'varies'"", ""options"":[{""retailer"":""store"",""price"":""price if shown else ''"",""note"":""short note e.g. free shipping / cheapest / official""}], ""tip"":""one buying tip"" }
Only use retailers/prices supported by the search results; don't invent prices.",
                $"Product: {product}\n\nSearch results:\n{web}", 800);
            var j = summary.Trim();
            if (j.StartsWith("```")) j = string.Join("\n", j.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            var s = j.IndexOf('{'); var e = j.LastIndexOf('}'); if (s >= 0 && e > s) j = j.Substring(s, e - s + 1);
            using var doc = JsonDocument.Parse(j);
            return Ok(new { identified = product, result = doc.RootElement.Clone() });
        }
        catch (Exception ex) { _logger.LogError(ex, "Price summarise failed"); return Ok(new { identified = product, result = (object?)null, message = "Found the product but couldn't summarise deals — try again." }); }
    }
}
