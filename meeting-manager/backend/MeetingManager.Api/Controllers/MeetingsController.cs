using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;
using MeetingManager.Api.Models;
using MeetingManager.Api.Services.MeetingCapture;

namespace MeetingManager.Api.Controllers;

/// <summary>
/// The live-meeting layer for Signal: arm a notetaker against a calendar
/// meeting, feed it audio while the meeting runs, and hand the finished
/// transcript to the existing Signal tools.
///
/// Capture is deliberately not this controller's business. It accepts chunks
/// from whoever is capturing -- the browser extension today, a meeting bot's
/// webhook later -- so adding a new way of getting audio does not touch
/// anything here.
/// </summary>
[ApiController]
[Route("api/meetings")]
[Authorize]
public class MeetingsController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly MeetingSessionService _meetings;
    private readonly MeetingCaptureRegistry _registry;
    private readonly ILogger<MeetingsController> _logger;

    public MeetingsController(
        PromptDbContext db,
        MeetingSessionService meetings,
        MeetingCaptureRegistry registry,
        ILogger<MeetingsController> logger)
    {
        _db = db;
        _meetings = meetings;
        _registry = registry;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    private Task<MeetingSession?> Find(string id) =>
        _db.MeetingSessions.FirstOrDefaultAsync(m => m.Id == id && m.UserId == UserId);

    /// <summary>
    /// Which ways of capturing a meeting are open to this user. Availability is
    /// per person: someone with their own meeting-bot account can send a bot
    /// whether or not anyone else has set one up.
    /// </summary>
    [HttpGet("sources")]
    public async Task<IActionResult> Sources([FromQuery] string platform = "*")
    {
        var result = new List<object>();
        foreach (var s in _registry.All)
        {
            var availability = await s.AvailabilityAsync(UserId, platform);
            result.Add(new
            {
                key = s.Key,
                name = s.DisplayName,
                available = availability.Available,
                reason = availability.Reason,
            });
        }
        return Ok(result);
    }

    /// <summary>
    /// Upcoming calendar meetings that could be recorded, each with whatever
    /// notetaker is already armed against it.
    /// </summary>
    [HttpGet("upcoming")]
    public async Task<IActionResult> Upcoming([FromQuery] int days = 7)
    {
        var from = DateTime.UtcNow.AddHours(-1);
        var to = DateTime.UtcNow.AddDays(Math.Clamp(days, 1, 30));

        List<CalendarRow> events;
        try
        {
            events = await _db.Database.SqlQuery<CalendarRow>($@"
                SELECT ""Id"", ""Title"", ""MeetingLink"", ""StartDateTime"", ""EndDateTime""
                FROM ""CalendarEvents""
                WHERE ""UserId"" = {UserId}").ToListAsync();
        }
        catch (Exception ex)
        {
            // No calendar connected yet, or the table has not been created on
            // this database. An empty list is the honest answer, not a 500.
            _logger.LogInformation(ex, "Calendar events unavailable for upcoming meetings");
            events = [];
        }

        var armed = await _db.MeetingSessions
            .Where(m => m.UserId == UserId && MeetingSessionStatus.Active.Contains(m.Status))
            .ToListAsync();

        var upcoming = events
            .Select(e => new
            {
                row = e,
                start = ParseDate(e.StartDateTime),
                end = ParseDate(e.EndDateTime),
            })
            .Where(x => x.start >= from && x.start <= to)
            .OrderBy(x => x.start)
            .Take(50)
            .Select(x => new
            {
                calendarEventId = x.row.Id,
                title = x.row.Title,
                meetingLink = x.row.MeetingLink,
                platform = MeetingSessionService.PlatformFor(x.row.MeetingLink),
                start = x.start,
                end = x.end,
                joinable = !string.IsNullOrWhiteSpace(x.row.MeetingLink),
                session = armed.FirstOrDefault(m => m.CalendarEventId == x.row.Id) is { } m
                    ? Shape(m)
                    : null,
            });

        return Ok(upcoming);
    }

    /// <summary>Every meeting session, newest first.</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var sessions = await _db.MeetingSessions
            .Where(m => m.UserId == UserId)
            .OrderByDescending(m => m.ScheduledStart)
            .Take(100)
            .ToListAsync();
        return Ok(sessions.Select(Shape));
    }

    /// <summary>One session, with its transcript so far. Polled while live.</summary>
    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var session = await Find(id);
        if (session is null) return NotFound();
        return Ok(new
        {
            session = Shape(session),
            transcript = session.Transcript,
        });
    }

    /// <summary>Put a notetaker on a meeting.</summary>
    [HttpPost("arm")]
    public async Task<IActionResult> Arm([FromBody] ArmRequest request)
    {
        if (request is null || request.CalendarEventId <= 0)
            return BadRequest(new { error = "A calendar event is required." });

        var source = _registry.Get(request.Source) ?? _registry.Get(MeetingCaptureSources.TabCapture)!;

        CalendarRow? row;
        try
        {
            row = await _db.Database.SqlQuery<CalendarRow>($@"
                SELECT ""Id"", ""Title"", ""MeetingLink"", ""StartDateTime"", ""EndDateTime""
                FROM ""CalendarEvents""
                WHERE ""UserId"" = {UserId} AND ""Id"" = {request.CalendarEventId}")
                .ToListAsync().ContinueWith(t => t.Result.FirstOrDefault());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read calendar event {Id}", request.CalendarEventId);
            return BadRequest(new { error = "That meeting could not be read from your calendar." });
        }

        if (row is null) return NotFound(new { error = "That meeting is not in your calendar." });

        var ev = new CalendarEventLite(row.Id, row.Title, row.MeetingLink,
            ParseDate(row.StartDateTime), ParseDate(row.EndDateTime));

        // Check availability against this meeting's platform: a team can point
        // Zoom at one account and everything else at another.
        var platform = MeetingSessionService.PlatformFor(ev.MeetingLink);
        var availability = await source.AvailabilityAsync(UserId, platform);
        if (!availability.Available) return BadRequest(new { error = availability.Reason });

        if (!source.CanAttend(new MeetingSession { MeetingLink = ev.MeetingLink }))
            return BadRequest(new { error = $"{source.DisplayName} cannot attend this meeting." });

        var tools = request.Tools is { Length: > 0 } ? request.Tools : ["summaries"];
        var session = await _meetings.ArmAsync(UserId, ev, source.Key, tools);
        return Ok(Shape(session));
    }

    /// <summary>Take the notetaker back off a meeting.</summary>
    [HttpPost("{id}/disarm")]
    public async Task<IActionResult> Disarm(string id)
    {
        var session = await Find(id);
        if (session is null) return NotFound();

        session.Status = MeetingSessionStatus.Cancelled;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Shape(session));
    }

    /// <summary>Capture is beginning. The capture source gets a chance to bring itself up.</summary>
    [HttpPost("{id}/start")]
    public async Task<IActionResult> Start(string id)
    {
        var session = await Find(id);
        if (session is null) return NotFound();

        var result = await _meetings.StartAsync(session);
        if (!result.Started) return BadRequest(new { error = result.Message, session = Shape(session) });
        return Ok(Shape(session));
    }

    /// <summary>
    /// One slice of the meeting: an audio blob to transcribe, or text from a
    /// source that transcribes for us. Called repeatedly while the meeting runs,
    /// which is what makes the transcript grow live.
    /// </summary>
    [HttpPost("{id}/chunk")]
    [RequestSizeLimit(64_000_000)]
    public async Task<IActionResult> Chunk(string id, IFormFile? audio = null, [FromForm] string? text = null)
    {
        var session = await Find(id);
        if (session is null) return NotFound();

        if (session.Status is MeetingSessionStatus.Done or MeetingSessionStatus.Cancelled)
            return BadRequest(new { error = "This meeting has already finished." });

        string? tempPath = null;
        try
        {
            if (audio is { Length: > 0 })
            {
                tempPath = Path.Combine(Path.GetTempPath(), $"meet-{session.Id}-{Guid.NewGuid():N}{Path.GetExtension(audio.FileName)}");
                await using var stream = System.IO.File.Create(tempPath);
                await audio.CopyToAsync(stream);
            }

            await _meetings.AppendChunkAsync(session, tempPath, text);
            return Ok(new { session = Shape(session), transcriptLength = session.Transcript.Length });
        }
        finally
        {
            if (tempPath is not null)
                try { System.IO.File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>The meeting is over: file the transcript and run the armed tools.</summary>
    [HttpPost("{id}/stop")]
    public async Task<IActionResult> Stop(string id)
    {
        var session = await Find(id);
        if (session is null) return NotFound();

        await _meetings.CloseAsync(session);
        return Ok(Shape(session));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DateTime ParseDate(string? value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var d)
            ? d
            : DateTime.MinValue;

    private static object Shape(MeetingSession m) => new
    {
        id = m.Id,
        calendarEventId = m.CalendarEventId,
        interactionSessionId = m.InteractionSessionId,
        title = m.Title,
        meetingLink = m.MeetingLink,
        platform = m.Platform,
        source = m.Source,
        status = m.Status,
        error = m.ErrorMessage,
        chunkCount = m.ChunkCount,
        participants = SafeParticipants(m.ParticipantsJson),
        transcriptLength = m.Transcript.Length,
        tools = SafeTools(m.AutoToolsJson),
        scheduledStart = m.ScheduledStart,
        scheduledEnd = m.ScheduledEnd,
        startedAt = m.StartedAt,
        endedAt = m.EndedAt,
        lastChunkAt = m.LastChunkAt,
    };

    /// <summary>Never let a malformed row take the whole listing down.</summary>
    private static object[] SafeParticipants(string json)
    {
        try { return JsonSerializer.Deserialize<object[]>(json) ?? []; }
        catch { return []; }
    }

    private static string[] SafeTools(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }

    public class ArmRequest
    {
        public int CalendarEventId { get; set; }
        public string? Source { get; set; }
        public string[]? Tools { get; set; }
    }

    /// <summary>The calendar columns arming needs. Read raw: CalendarEvents is not an EF entity.</summary>
    public class CalendarRow
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? MeetingLink { get; set; }
        public string? StartDateTime { get; set; }
        public string? EndDateTime { get; set; }
    }
}
