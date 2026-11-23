using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MeetingManager.Api.Services;
using System.Security.Claims;

namespace MeetingManager.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/availability")]
[EnableRateLimiting("ai-generation")]
public class AvailabilityController : ControllerBase
{
    private readonly AvailabilityService _svc;
    private readonly ILogger<AvailabilityController> _logger;

    public AvailabilityController(AvailabilityService svc, ILogger<AvailabilityController> logger)
    {
        _svc = svc;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>Check whether a specific slot should be booked.</summary>
    [HttpPost("check")]
    public async Task<IActionResult> CheckSlot([FromBody] CheckSlotRequest req)
    {
        if (!DateTime.TryParse(req.Start, out var start) || !DateTime.TryParse(req.End, out var end))
            return BadRequest(new { message = "Invalid start or end date" });

        try
        {
            var result = await _svc.CheckSlotAsync(
                UserId, start, end,
                req.Timezone ?? "UTC",
                req.MeetingType,
                req.RequiresPrep,
                req.MinimumBufferMinutes,
                req.WorkStartHour,
                req.WorkEndHour);

            return Ok(new
            {
                requestedStart = result.RequestedStart,
                requestedEnd = result.RequestedEnd,
                decision = result.Decision.ToString(),
                score = result.Score,
                reason = result.Reason,
                alternatives = result.Alternatives.Select(a => new
                {
                    start = a.Start,
                    end = a.End,
                    decision = a.Decision.ToString(),
                    score = a.Score,
                    reason = a.Reason,
                }),
                suggestedReply = result.SuggestedReply,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Availability check failed");
            return StatusCode(500, new { message = "Check failed. Please try again." });
        }
    }

    /// <summary>Suggest the best available slots for a given duration and date range.</summary>
    [HttpPost("suggest-slots")]
    public async Task<IActionResult> SuggestSlots([FromBody] SuggestSlotsRequest req)
    {
        if (req.DurationMinutes <= 0)
            return BadRequest(new { message = "Duration must be positive" });

        if (!DateTime.TryParse(req.DateFrom, out var from))
            from = DateTime.UtcNow.Date;
        if (!DateTime.TryParse(req.DateTo, out var to))
            to = from.AddDays(5);

        try
        {
            var slots = await _svc.FindBestSlotsAsync(
                UserId, from, to, req.DurationMinutes,
                req.Timezone ?? "UTC",
                req.RequiresPrep,
                req.MinimumBufferMinutes,
                req.WorkStartHour,
                req.WorkEndHour,
                req.MaxResults);

            return Ok(new
            {
                recommendedSlots = slots.Select(s => new
                {
                    start = s.Start,
                    end = s.End,
                    decision = s.Decision.ToString(),
                    score = s.Score,
                    reason = s.Reason,
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Slot suggestion failed");
            return StatusCode(500, new { message = "Suggestion failed. Please try again." });
        }
    }
}

public class CheckSlotRequest
{
    public required string Start { get; set; }
    public required string End { get; set; }
    public string? Timezone { get; set; }
    public string? MeetingType { get; set; }
    public bool RequiresPrep { get; set; } = false;
    public int MinimumBufferMinutes { get; set; } = 15;
    public int WorkStartHour { get; set; } = 9;
    public int WorkEndHour { get; set; } = 18;
}

public class SuggestSlotsRequest
{
    public int DurationMinutes { get; set; } = 30;
    public string? DateFrom { get; set; }
    public string? DateTo { get; set; }
    public string? Timezone { get; set; }
    public string? MeetingType { get; set; }
    public bool RequiresPrep { get; set; } = false;
    public int MinimumBufferMinutes { get; set; } = 15;
    public int WorkStartHour { get; set; } = 9;
    public int WorkEndHour { get; set; } = 18;
    public int MaxResults { get; set; } = 5;
}
