using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;
using MeetingManager.Api.Models;
using MeetingManager.Api.Services;
using System.Security.Claims;

namespace MeetingManager.Api.Controllers;

/// <summary>
/// Everything a person wants from a meeting after it has ended.
///
/// The capture layer (MeetingsController) ends at "we have a transcript". This
/// is the half that makes the transcript worth having.
/// </summary>
[Authorize]
[ApiController]
[Route("api/meeting-intelligence")]
public class MeetingIntelligenceController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly MeetingIntelligenceService _intel;
    private readonly IEmailService _email;
    private readonly ILogger<MeetingIntelligenceController> _logger;

    public MeetingIntelligenceController(
        PromptDbContext context,
        MeetingIntelligenceService intel,
        IEmailService email,
        ILogger<MeetingIntelligenceController> logger)
    {
        _context = context;
        _intel = intel;
        _email = email;
        _logger = logger;
    }

    private string GetCurrentUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    // GET /api/meeting-intelligence/{sessionId} - the payoff view
    [HttpGet("{sessionId}")]
    public async Task<ActionResult> GetOutcome(string sessionId)
    {
        var userId = GetCurrentUserId();
        var session = await _context.MeetingSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId);
        if (session == null) return NotFound();

        var outcome = await _context.MeetingOutcomes.FirstOrDefaultAsync(o => o.MeetingSessionId == sessionId);

        var actionItems = await _context.MeetingActionItems
            .Where(a => a.MeetingSessionId == sessionId)
            .OrderByDescending(a => a.CarryOverCount)
            .ToListAsync();

        MeetingSeries? series = null;
        if (session.MeetingSeriesId != null)
            series = await _context.MeetingSeries.FirstOrDefaultAsync(s => s.Id == session.MeetingSeriesId);

        return Ok(new
        {
            sessionId = session.Id,
            title = session.Title,
            status = session.Status,
            startedAt = session.StartedAt,
            endedAt = session.EndedAt,
            outcomeStatus = outcome?.Status ?? "none",
            outcome?.ErrorMessage,
            summary = outcome?.Summary,
            decisions = Parse(outcome?.DecisionsJson),
            openQuestions = Parse(outcome?.OpenQuestionsJson),
            speakerStats = Parse(outcome?.SpeakerStatsJson),
            changesSinceLast = outcome?.ChangesSinceLast,
            followUp = outcome == null ? null : new { subject = outcome.FollowUpSubject, body = outcome.FollowUpBody },
            series = series == null ? null : new { series.Id, series.Title, series.SessionCount, series.LastMeetingAt },
            actionItems = actionItems.Select(a => new
            {
                a.Id, a.Text, a.Owner, a.DueDate, a.Status, a.CarryOverCount,
                isCarriedOver = a.CarryOverCount > 0
            })
        });
    }

    // POST /api/meeting-intelligence/{sessionId}/generate - build or rebuild
    [HttpPost("{sessionId}/generate")]
    public async Task<ActionResult> Generate(string sessionId)
    {
        var userId = GetCurrentUserId();
        var session = await _context.MeetingSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId);
        if (session == null) return NotFound();

        if (string.IsNullOrWhiteSpace(session.Transcript))
            return BadRequest(new { error = "This meeting has no transcript to work from." });

        if (session.MeetingSeriesId == null)
            await _intel.AttachToSeriesAsync(session);

        var outcome = await _intel.GenerateOutcomeAsync(session);

        return Ok(new
        {
            outcome.Status,
            outcome.Summary,
            outcome.ErrorMessage,
            decisions = Parse(outcome.DecisionsJson),
            actionItems = Parse(outcome.ActionItemsJson),
            openQuestions = Parse(outcome.OpenQuestionsJson),
            speakerStats = Parse(outcome.SpeakerStatsJson),
            changesSinceLast = outcome.ChangesSinceLast
        });
    }

    // GET /api/meeting-intelligence/search?q=...
    //
    // "When did we agree on the pricing change?" — the question that makes a
    // pile of transcripts into a resource.
    [HttpGet("search")]
    public async Task<ActionResult> Search([FromQuery] string q, [FromQuery] int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return BadRequest(new { error = "Give me at least two characters to search for." });

        var userId = GetCurrentUserId();
        var term = q.Trim();
        limit = Math.Clamp(limit, 1, 100);

        // EF.Functions.Like keeps the filter in the database rather than
        // pulling every transcript the user owns into memory.
        var pattern = $"%{term}%";

        var sessionHits = await _context.MeetingSessions
            .Where(s => s.UserId == userId &&
                        (EF.Functions.Like(s.Transcript, pattern) || EF.Functions.Like(s.Title, pattern)))
            .OrderByDescending(s => s.EndedAt ?? s.ScheduledStart)
            .Take(limit)
            .Select(s => new { s.Id, s.Title, s.EndedAt, s.ScheduledStart, s.Transcript, s.MeetingSeriesId })
            .ToListAsync();

        var outcomeHits = await _context.MeetingOutcomes
            .Where(o => o.UserId == userId &&
                        (EF.Functions.Like(o.Summary!, pattern) ||
                         EF.Functions.Like(o.DecisionsJson, pattern) ||
                         EF.Functions.Like(o.ActionItemsJson, pattern)))
            .Take(limit)
            .Select(o => new { o.MeetingSessionId, o.Summary })
            .ToListAsync();

        var actionHits = await _context.MeetingActionItems
            .Where(a => a.UserId == userId && EF.Functions.Like(a.Text, pattern))
            .OrderByDescending(a => a.CreatedAt)
            .Take(limit)
            .Select(a => new { a.Id, a.MeetingSessionId, a.Text, a.Owner, a.Status, a.DueDate })
            .ToListAsync();

        var outcomeBySession = outcomeHits.ToDictionary(o => o.MeetingSessionId, o => o.Summary);

        var results = sessionHits.Select(s => new
        {
            sessionId = s.Id,
            title = s.Title,
            at = s.EndedAt ?? s.ScheduledStart,
            summary = outcomeBySession.GetValueOrDefault(s.Id),
            // The line the term appeared on, which is what makes a search result
            // answer the question instead of just pointing at a meeting.
            excerpts = Excerpts(s.Transcript, term, 3)
        }).ToList();

        // Sessions whose outcome matched but whose transcript did not.
        foreach (var o in outcomeHits.Where(o => !sessionHits.Any(s => s.Id == o.MeetingSessionId)))
        {
            var s = await _context.MeetingSessions
                .Where(x => x.Id == o.MeetingSessionId)
                .Select(x => new { x.Id, x.Title, x.EndedAt, x.ScheduledStart })
                .FirstOrDefaultAsync();
            if (s != null)
                results.Add(new { sessionId = s.Id, title = s.Title, at = s.EndedAt ?? s.ScheduledStart, summary = o.Summary, excerpts = new List<string>() });
        }

        return Ok(new { query = term, meetings = results, actionItems = actionHits });
    }

    // ── Action items ────────────────────────────────────────────────────────

    [HttpGet("action-items")]
    public async Task<ActionResult> GetActionItems([FromQuery] string status = "open", [FromQuery] string? owner = null)
    {
        var userId = GetCurrentUserId();

        var q = _context.MeetingActionItems.Where(a => a.UserId == userId);
        if (!string.IsNullOrWhiteSpace(status) && status != "all") q = q.Where(a => a.Status == status);
        if (!string.IsNullOrWhiteSpace(owner)) q = q.Where(a => a.Owner == owner);

        var items = await (
            from a in q
            join s in _context.MeetingSessions on a.MeetingSessionId equals s.Id
            orderby a.CarryOverCount descending, a.CreatedAt descending
            select new
            {
                a.Id, a.Text, a.Owner, a.DueDate, a.Status, a.CarryOverCount,
                a.MeetingSessionId, meetingTitle = s.Title, meetingAt = s.EndedAt ?? s.ScheduledStart,
                a.CreatedAt, a.CompletedAt
            }
        ).Take(200).ToListAsync();

        return Ok(new
        {
            total = items.Count,
            // The list people actually act on: things that have survived more
            // than one meeting without being done.
            chronic = items.Count(i => i.CarryOverCount >= 2),
            overdue = items.Count(i => i.DueDate != null && i.DueDate < DateTime.UtcNow && i.Status == "open"),
            items
        });
    }

    [HttpPut("action-items/{id}")]
    public async Task<ActionResult> UpdateActionItem(string id, [FromBody] UpdateActionItemRequest request)
    {
        var userId = GetCurrentUserId();
        var item = await _context.MeetingActionItems.FirstOrDefaultAsync(a => a.Id == id && a.UserId == userId);
        if (item == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            if (!new[] { "open", "done", "dropped" }.Contains(request.Status))
                return BadRequest(new { error = "Status must be open, done or dropped." });

            item.Status = request.Status;
            item.CompletedAt = request.Status == "done" ? DateTime.UtcNow : null;
        }

        if (request.Owner != null) item.Owner = request.Owner;
        if (request.DueDate.HasValue) item.DueDate = request.DueDate;
        if (!string.IsNullOrWhiteSpace(request.Text)) item.Text = request.Text;

        await _context.SaveChangesAsync();
        return Ok(new { item.Id, item.Text, item.Owner, item.Status, item.DueDate, item.CompletedAt });
    }

    // ── Series ──────────────────────────────────────────────────────────────

    [HttpGet("series")]
    public async Task<ActionResult> GetSeries()
    {
        var userId = GetCurrentUserId();

        var series = await _context.MeetingSeries
            .Where(s => s.UserId == userId)
            .OrderByDescending(s => s.LastMeetingAt)
            .Select(s => new
            {
                s.Id, s.Title, s.SessionCount, s.FirstMeetingAt, s.LastMeetingAt,
                openActionItems = _context.MeetingActionItems.Count(a => a.MeetingSeriesId == s.Id && a.Status == "open")
            })
            .ToListAsync();

        return Ok(series);
    }

    [HttpGet("series/{id}")]
    public async Task<ActionResult> GetSeriesDetail(string id)
    {
        var userId = GetCurrentUserId();
        var series = await _context.MeetingSeries.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (series == null) return NotFound();

        var sessions = await (
            from s in _context.MeetingSessions
            where s.MeetingSeriesId == id
            orderby (s.EndedAt ?? s.ScheduledStart) descending
            select new
            {
                s.Id, s.Title, at = s.EndedAt ?? s.ScheduledStart, s.Status,
                openItems = _context.MeetingActionItems.Count(a => a.MeetingSessionId == s.Id && a.Status == "open"),
                doneItems = _context.MeetingActionItems.Count(a => a.MeetingSessionId == s.Id && a.Status == "done")
            }
        ).Take(50).ToListAsync();

        var outcomeIds = sessions.Select(s => s.Id).ToList();
        var outcomes = await _context.MeetingOutcomes
            .Where(o => outcomeIds.Contains(o.MeetingSessionId))
            .Select(o => new { o.MeetingSessionId, o.Summary, o.ChangesSinceLast })
            .ToListAsync();

        // Commitments that keep reappearing across the series — the honest
        // health metric for a recurring meeting.
        var chronic = await _context.MeetingActionItems
            .Where(a => a.MeetingSeriesId == id && a.Status == "open" && a.CarryOverCount >= 1)
            .OrderByDescending(a => a.CarryOverCount)
            .Select(a => new { a.Id, a.Text, a.Owner, a.CarryOverCount })
            .Take(20)
            .ToListAsync();

        return Ok(new
        {
            series.Id, series.Title, series.SessionCount, series.FirstMeetingAt, series.LastMeetingAt,
            sessions = sessions.Select(s => new
            {
                s.Id, s.Title, s.at, s.Status, s.openItems, s.doneItems,
                summary = outcomes.FirstOrDefault(o => o.MeetingSessionId == s.Id)?.Summary,
                changesSinceLast = outcomes.FirstOrDefault(o => o.MeetingSessionId == s.Id)?.ChangesSinceLast
            }),
            chronicItems = chronic
        });
    }

    // ── Follow-up ───────────────────────────────────────────────────────────

    [HttpPost("{sessionId}/send-follow-up")]
    public async Task<ActionResult> SendFollowUp(string sessionId, [FromBody] SendFollowUpRequest request)
    {
        var userId = GetCurrentUserId();
        var session = await _context.MeetingSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId);
        if (session == null) return NotFound();

        var outcome = await _context.MeetingOutcomes.FirstOrDefaultAsync(o => o.MeetingSessionId == sessionId);
        if (outcome == null) return BadRequest(new { error = "Generate the meeting outcome first." });

        var recipients = request.Recipients?.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList() ?? [];
        if (recipients.Count == 0)
            return BadRequest(new { error = "No recipients given." });

        if (recipients.Count > 50)
            return BadRequest(new { error = "At most 50 recipients per send." });

        var subject = request.Subject ?? outcome.FollowUpSubject ?? $"Notes — {session.Title}";
        var bodyText = request.Body ?? outcome.FollowUpBody ?? outcome.Summary ?? "";
        var html = "<div style=\"font-family:system-ui,sans-serif;line-height:1.6\">" +
                   System.Net.WebUtility.HtmlEncode(bodyText).Replace("\n", "<br>") +
                   "</div>";

        var sent = 0;
        var failed = new List<string>();
        foreach (var to in recipients)
        {
            try { await _email.SendEmailAsync(to, subject, html); sent++; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Follow-up send failed for {Recipient}", to);
                failed.Add(to);
            }
        }

        return Ok(new { sent, failed, subject });
    }

    private static object? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return null; }
    }

    /// <summary>Lines around each hit, so a search result answers the question.</summary>
    private static List<string> Excerpts(string transcript, string term, int max)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(transcript)) return found;

        foreach (var line in transcript.Split('\n'))
        {
            if (line.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                found.Add(line.Trim().Length > 300 ? line.Trim()[..300] + "…" : line.Trim());
                if (found.Count >= max) break;
            }
        }
        return found;
    }
}

public class UpdateActionItemRequest
{
    public string? Text { get; set; }
    public string? Owner { get; set; }
    public DateTime? DueDate { get; set; }
    public string? Status { get; set; }
}

public class SendFollowUpRequest
{
    public List<string>? Recipients { get; set; }
    public string? Subject { get; set; }
    public string? Body { get; set; }
}
