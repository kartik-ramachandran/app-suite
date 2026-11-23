using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MeetingManager.Api.Services;
using System.Security.Claims;

namespace MeetingManager.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/calendar")]
[EnableRateLimiting("general")]
public class CalendarController : ControllerBase
{
    private readonly CalendarSyncService _sync;
    private readonly IConfiguration _config;
    private readonly AzureVisionService _vision;
    private readonly ILogger<CalendarController> _logger;

    public CalendarController(CalendarSyncService sync, IConfiguration config, AzureVisionService vision, ILogger<CalendarController> logger)
    {
        _sync = sync;
        _config = config;
        _vision = vision;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Connections ───────────────────────────────────────────────────────────

    [HttpGet("connections")]
    public async Task<IActionResult> GetConnections()
    {
        var conns = await _sync.GetConnectionsAsync(UserId);
        return Ok(conns.Select(c => new
        {
            c.Id, c.Provider, c.CalendarEmail, c.DisplayName, c.IsActive, c.CreatedAt
        }));
    }

    [HttpDelete("connections/{id}")]
    public async Task<IActionResult> Disconnect(int id)
    {
        await _sync.DisconnectAsync(id, UserId);
        return Ok(new { message = "Calendar disconnected" });
    }

    // ── Google OAuth ──────────────────────────────────────────────────────────

    [HttpGet("connect/google")]
    public IActionResult ConnectGoogle()
    {
        var clientId = _config["GoogleCalendar:ClientId"] ?? "";
        if (string.IsNullOrWhiteSpace(clientId))
            return BadRequest(new { message = "Google Calendar sync isn't available right now — please try again later." });

        var redirectUri = _config["GoogleCalendar:RedirectUri"] ?? "http://localhost:5173/calendar/callback/google";
        var authUrl = _sync.BuildGoogleAuthUrl(UserId, redirectUri);
        return Ok(new { authUrl });
    }

    [HttpPost("callback/google")]
    public async Task<IActionResult> GoogleCallback([FromBody] OAuthCallbackRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(new { message = "Authorization code is required" });

        var clientSecret = _config["GoogleCalendar:ClientSecret"] ?? "";
        if (string.IsNullOrWhiteSpace(clientSecret))
            return BadRequest(new { message = "Google Calendar sync isn't available right now — please try again later." });

        try
        {
            var redirectUri = _config["GoogleCalendar:RedirectUri"] ?? "http://localhost:5173/calendar/callback/google";
            var connectionId = await _sync.HandleGoogleCallbackAsync(req.Code, UserId, redirectUri);
            return Ok(new { connectionId, message = "Google Calendar connected successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google Calendar OAuth callback failed");
            return BadRequest(new { message = $"Connection failed: {ex.Message}" });
        }
    }

    // ── Microsoft OAuth ───────────────────────────────────────────────────────

    [HttpGet("connect/microsoft")]
    public IActionResult ConnectMicrosoft()
    {
        var clientId = _config["MicrosoftGraph:ClientId"] ?? "";
        if (string.IsNullOrWhiteSpace(clientId))
            return BadRequest(new { message = "Outlook calendar sync isn't available right now — please try again later." });

        var redirectUri = _config["MicrosoftGraph:RedirectUri"] ?? "http://localhost:5173/calendar/callback/microsoft";
        var authUrl = _sync.BuildMicrosoftAuthUrl(UserId, redirectUri);
        return Ok(new { authUrl });
    }

    [HttpPost("callback/microsoft")]
    public async Task<IActionResult> MicrosoftCallback([FromBody] OAuthCallbackRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Code))
            return BadRequest(new { message = "Authorization code is required" });

        var clientSecret = _config["MicrosoftGraph:ClientSecret"] ?? "";
        if (string.IsNullOrWhiteSpace(clientSecret))
            return BadRequest(new { message = "Outlook calendar sync isn't available right now — please try again later." });

        try
        {
            var redirectUri = _config["MicrosoftGraph:RedirectUri"] ?? "http://localhost:5173/calendar/callback/microsoft";
            var connectionId = await _sync.HandleMicrosoftCallbackAsync(req.Code, UserId, redirectUri);
            return Ok(new { connectionId, message = "Microsoft Calendar connected successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Microsoft Calendar OAuth callback failed");
            return BadRequest(new { message = $"Connection failed: {ex.Message}" });
        }
    }

    // ── Events ────────────────────────────────────────────────────────────────

    [HttpGet("events")]
    public async Task<IActionResult> GetEvents([FromQuery] string? from, [FromQuery] string? to)
    {
        var fromDate = string.IsNullOrEmpty(from) ? DateTime.UtcNow.Date : DateTime.Parse(from);
        var toDate = string.IsNullOrEmpty(to) ? fromDate.AddDays(7) : DateTime.Parse(to);

        var events = await _sync.GetEventsAsync(UserId, fromDate, toDate);
        return Ok(events);
    }

    [HttpGet("events/today")]
    public async Task<IActionResult> GetTodayEvents()
    {
        var today = DateTime.UtcNow.Date;
        var events = await _sync.GetEventsAsync(UserId, today, today.AddDays(1));
        return Ok(events);
    }

    [HttpPost("events/native")]
    public async Task<IActionResult> CreateNativeEvent([FromBody] NativeCalendarEventRequest req)
    {
        var validation = ValidateNativeEvent(req);
        if (validation is not null) return BadRequest(new { message = validation });

        try
        {
            var evt = await _sync.CreateNativeEventAsync(UserId, ToInput(req), req.TargetConnectionId);
            return Ok(evt);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("events/native/{id:int}")]
    public async Task<IActionResult> UpdateNativeEvent(int id, [FromBody] NativeCalendarEventRequest req)
    {
        var validation = ValidateNativeEvent(req);
        if (validation is not null) return BadRequest(new { message = validation });

        var evt = await _sync.UpdateNativeEventAsync(UserId, id, ToInput(req));
        return evt is null ? NotFound(new { message = "Native calendar event not found" }) : Ok(evt);
    }

    [HttpDelete("events/native/{id:int}")]
    public async Task<IActionResult> DeleteNativeEvent(int id)
    {
        var deleted = await _sync.DeleteNativeEventAsync(UserId, id);
        return deleted ? Ok(new { message = "Appointment deleted" }) : NotFound(new { message = "Native calendar event not found" });
    }

    // ── Extract events from a screenshot / shared-screen frame ─────────────────

    [HttpPost("extract-events")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> ExtractEvents([FromBody] ExtractEventsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData))
            return BadRequest(new { message = "No image was provided." });
        if (req.ImageData.Length > 4_000_000)
            return BadRequest(new { message = "That image is too large — try a smaller capture." });
        if (!_vision.IsAvailable)
            return StatusCode(503, new { message = "Screen reading is not available right now." });

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        try
        {
            var raw = await _vision.AnalyzeAsync(req.ImageData,
                @"You read calendar events and appointments from a screenshot — it may be an email, a meeting invite, a message, a poster, or an on-screen calendar/schedule.
Return ONLY JSON (no markdown fences):
{
  ""events"": [
    {
      ""title"": ""short event name"",
      ""date"": ""yyyy-MM-dd"",
      ""startTime"": ""HH:mm 24-hour, or empty if unknown"",
      ""endTime"": ""HH:mm 24-hour, or empty"",
      ""allDay"": false,
      ""location"": ""place or empty"",
      ""meetingLink"": ""video/meeting URL or empty"",
      ""description"": ""one-line context or empty""
    }
  ]
}
Resolve relative dates (""tomorrow"", ""next Tuesday"", ""Fri"") using TODAY. If no year is shown, choose the nearest FUTURE date. If a screenshot shows several scheduled events (e.g. a week view), return each one. If nothing calendar-like is visible, return {""events"": []}.",
                $"Today is {today}. Extract every event you can see.", 1200);

            var json = raw.Trim();
            if (json.StartsWith("```")) json = string.Join("\n", json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            var s = json.IndexOf('{'); var e = json.LastIndexOf('}');
            if (s >= 0 && e > s) json = json.Substring(s, e - s + 1);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var events = doc.RootElement.TryGetProperty("events", out var evEl) ? evEl.Clone() : (System.Text.Json.JsonElement?)null;
            return Ok(new { events });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Calendar event extraction failed");
            return StatusCode(500, new { message = "Could not read events from that image — try a clearer capture." });
        }
    }

    // ── Sync ──────────────────────────────────────────────────────────────────

    [HttpPost("sync")]
    public async Task<IActionResult> Sync()
    {
        try
        {
            await _sync.SyncAllForUserAsync(UserId);
            return Ok(new { message = "Calendar sync complete" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Calendar sync failed");
            return StatusCode(500, new { message = "Sync failed. Please try again." });
        }
    }

    private static NativeCalendarEventInput ToInput(NativeCalendarEventRequest req) => new(
        req.Title.Trim(),
        string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim(),
        string.IsNullOrWhiteSpace(req.Location) ? null : req.Location.Trim(),
        string.IsNullOrWhiteSpace(req.MeetingLink) ? null : req.MeetingLink.Trim(),
        req.StartDateTime,
        req.EndDateTime,
        string.IsNullOrWhiteSpace(req.Timezone) ? "UTC" : req.Timezone.Trim(),
        req.IsAllDay,
        req.ReminderMinutes
    );

    private static string? ValidateNativeEvent(NativeCalendarEventRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Title)) return "Title is required.";
        if (req.EndDateTime <= req.StartDateTime) return "End time must be after start time.";
        if (req.ReminderMinutes is < 0 or > 10080) return "Reminder must be between 0 minutes and 7 days.";
        return null;
    }
}

public class OAuthCallbackRequest
{
    public required string Code { get; set; }
}

public class ExtractEventsRequest
{
    public string? ImageData { get; set; }
}

public class NativeCalendarEventRequest
{
    public required string Title { get; set; }
    public string? Description { get; set; }
    public string? Location { get; set; }
    public string? MeetingLink { get; set; }
    public DateTime StartDateTime { get; set; }
    public DateTime EndDateTime { get; set; }
    public string Timezone { get; set; } = "UTC";
    public bool IsAllDay { get; set; }
    public int? ReminderMinutes { get; set; }
    // When set, the appointment is created on the connected Google/Outlook calendar
    // (that connection id) so it appears on the user's real devices. Null = local-only.
    public int? TargetConnectionId { get; set; }
}
