using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using EventPlanner.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace EventPlanner.Api.Controllers;

/// <summary>Text-in generators: Gift Finder, Event Planner, Proposal Generator.</summary>
[Authorize]
[ApiController]
[Route("api/planner")]
[EnableRateLimiting("ai-generation")]
public class PlannerController : ControllerBase
{
    private readonly AzureVisionService _ai;
    private readonly WebSearchService _search;
    private readonly ILogger<PlannerController> _logger;

    public PlannerController(AzureVisionService ai, WebSearchService search, ILogger<PlannerController> logger)
    { _ai = ai; _search = search;_logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Gift Finder ──
    [HttpPost("gifts")]
    public async Task<IActionResult> Gifts([FromBody] GiftRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Recipient) && string.IsNullOrWhiteSpace(req.Interests))
            return BadRequest(new { message = "Tell me who it's for or their interests." });
        var web = "";
        try { web = await _search.SearchAsync($"best gift ideas {req.Interests} {req.Occasion} {req.Budget}", 5); } catch { }
        try
        {
            var storesLine = string.IsNullOrWhiteSpace(req.Stores) ? "" : $"\nPreferred stores: {req.Stores}";
            var storesRule = string.IsNullOrWhiteSpace(req.Stores)
                ? ""
                : "\nThe user named preferred stores — for each idea, set whereToBuy to one of THOSE stores when it plausibly stocks the item; only fall back to a general retailer type if none of them fit.";
            var json = await Gen(
                @"You are a thoughtful gift concierge. Suggest gift ideas tailored to the recipient. Return ONLY JSON (no fences):
{ ""ideas"":[{""name"":""gift"",""why"":""why they'd love it"",""priceHint"":""rough price"",""whereToBuy"":""a type of store or retailer""}] }
Give 6 varied ideas within budget. Use the web results for current/real products where helpful." + storesRule,
                $"Recipient: {req.Recipient}\nOccasion: {req.Occasion}\nBudget: {req.Budget}\nInterests: {req.Interests}{storesLine}\n\nWeb ideas:\n{web}", 1200);
            var giftFor = string.IsNullOrWhiteSpace(req.Recipient) ? req.Interests!.Trim() : req.Recipient!.Trim();
            var giftTitle = string.IsNullOrWhiteSpace(req.Occasion) ? $"Gift ideas — {giftFor}" : $"Gift ideas — {giftFor} ({req.Occasion!.Trim()})";
            return Content(json, "application/json");
        }
        catch (Exception ex) { _logger.LogError(ex, "Gifts failed"); return StatusCode(500, new { message = "Could not build gift ideas — try again." }); }
    }

    // ── Event Planner ──
    [HttpPost("event")]
    public async Task<IActionResult> Event([FromBody] EventReq req)
    {
        if (string.IsNullOrWhiteSpace(req.EventType)) return BadRequest(new { message = "What kind of event are you planning?" });
        try
        {
            var json = await Gen(
                @"You are an expert event planner. Build a practical plan. Return ONLY JSON (no fences):
{ ""overview"":""2 sentences"", ""checklist"":[""to-do items in a sensible order""], ""timeline"":[{""when"":""e.g. 6 weeks before"",""what"":""task""}], ""budget"":[{""item"":""category"",""estimate"":""rough cost""}], ""tips"":[""2-3 tips""] }
Tailor to the guest count and budget. Be realistic.",
                $"Event: {req.EventType}\nDate: {req.Date}\nGuests: {req.Guests}\nBudget: {req.Budget}\nNotes: {req.Notes}", 1600);
            return Content(json, "application/json");
        }
        catch (Exception ex) { _logger.LogError(ex, "Event plan failed"); return StatusCode(500, new { message = "Could not build the plan — try again." }); }
    }

    // ── Proposal / Quote Generator ──
    [HttpPost("proposal")]
    public async Task<IActionResult> Proposal([FromBody] ProposalReq req)
    {
        if (string.IsNullOrWhiteSpace(req.Project)) return BadRequest(new { message = "Describe the project or service." });
        try
        {
            var md = await _ai.CompleteTextAsync(
                @"You are a business proposal writer. Write a clean, professional proposal in MARKDOWN (headings, bullets, a simple pricing table). Sections: Overview, Scope of work, Deliverables, Timeline, Pricing, Terms, Next steps. Be concise and client-ready. Return only the markdown, no preamble.",
                $"From: {req.From}\nTo (client): {req.Client}\nProject: {req.Project}\nScope: {req.Scope}\nPrice: {req.Price}\nTimeline: {req.Timeline}\nTone: {req.Tone}", 2200);
            var propTitle = string.IsNullOrWhiteSpace(req.Client)
                ? $"Proposal — {req.Project!.Trim()}"
                : $"Proposal — {req.Project!.Trim()} for {req.Client!.Trim()}";
            return Ok(new { markdown = md });
        }
        catch (Exception ex) { _logger.LogError(ex, "Proposal failed"); return StatusCode(500, new { message = "Could not generate the proposal — try again." }); }
    }

    private async Task<string> Gen(string system, string user, int max)
    {
        var raw = await _ai.CompleteTextAsync(system, user, max);
        var j = raw.Trim();
        if (j.StartsWith("```")) j = string.Join("\n", j.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
        var s = j.IndexOf('{'); var e = j.LastIndexOf('}'); if (s >= 0 && e > s) j = j.Substring(s, e - s + 1);
        using var _ = JsonDocument.Parse(j);
        return j;
    }
}

public class GiftRequest { public string? Recipient { get; set; } public string? Occasion { get; set; } public string? Budget { get; set; } public string? Interests { get; set; } public string? Stores { get; set; } }
public class EventReq { public string? EventType { get; set; } public string? Date { get; set; } public string? Guests { get; set; } public string? Budget { get; set; } public string? Notes { get; set; } }
public class ProposalReq { public string? From { get; set; } public string? Client { get; set; } public string? Project { get; set; } public string? Scope { get; set; } public string? Price { get; set; } public string? Timeline { get; set; } public string? Tone { get; set; } }
