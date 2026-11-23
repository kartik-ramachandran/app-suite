using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;
using MeetingManager.Api.Models;
using MeetingManager.Api.Services.MeetingCapture;

namespace MeetingManager.Api.Controllers;

/// <summary>
/// Where our own meeting bot posts the call it was sent to.
///
/// The bot runs in a throwaway container with no user session, so it carries a
/// token scoped to exactly one meeting instead. That token can do three things
/// to that one meeting and nothing else: add audio, close it, or report that it
/// could not join. A leaked token is worth one transcript, not an account.
/// </summary>
[ApiController]
[Route("api/meetings/bot")]
[AllowAnonymous]
public class MeetingBotIngestController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly MeetingSessionService _meetings;
    private readonly ILogger<MeetingBotIngestController> _logger;

    public MeetingBotIngestController(
        PromptDbContext db,
        MeetingSessionService meetings,
        ILogger<MeetingBotIngestController> logger)
    {
        _db = db;
        _meetings = meetings;
        _logger = logger;
    }

    /// <summary>
    /// The session this request's token belongs to, or null. Compared in
    /// constant time so the token cannot be guessed a character at a time.
    /// </summary>
    private async Task<MeetingSession?> Authorise(string id)
    {
        var header = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;

        var supplied = header["Bearer ".Length..].Trim();
        if (string.IsNullOrWhiteSpace(supplied)) return null;

        var session = await _db.MeetingSessions.FirstOrDefaultAsync(m => m.Id == id);
        if (session is null || string.IsNullOrWhiteSpace(session.BotToken)) return null;

        var a = System.Text.Encoding.UTF8.GetBytes(supplied);
        var b = System.Text.Encoding.UTF8.GetBytes(session.BotToken);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(a, b) ? session : null;
    }

    /// <summary>One segment of the meeting.</summary>
    [HttpPost("{id}/chunk")]
    [RequestSizeLimit(64_000_000)]
    public async Task<IActionResult> Chunk(string id, IFormFile? audio = null, [FromForm] string? participants = null)
    {
        var session = await Authorise(id);
        if (session is null) return Unauthorized();

        // Who the bot can see. Sent with the audio rather than on its own so the
        // room and the transcript never drift apart.
        if (!string.IsNullOrWhiteSpace(participants))
        {
            session.ParticipantsJson = participants;
            session.UpdatedAt = DateTime.UtcNow;
        }

        if (session.Status is MeetingSessionStatus.Done or MeetingSessionStatus.Cancelled)
            return BadRequest(new { error = "This meeting has already finished." });

        if (audio is null || audio.Length == 0) return BadRequest(new { error = "No audio." });

        string? tempPath = null;
        try
        {
            tempPath = Path.Combine(Path.GetTempPath(), $"botseg-{session.Id}-{Guid.NewGuid():N}.webm");
            await using (var stream = System.IO.File.Create(tempPath))
                await audio.CopyToAsync(stream);

            await _meetings.AppendChunkAsync(session, tempPath, null);
            return Ok(new { transcriptLength = session.Transcript.Length });
        }
        finally
        {
            if (tempPath is not null)
                try { System.IO.File.Delete(tempPath); } catch { /* best effort */ }
        }
    }

    /// <summary>The bot has left; file the transcript and run the armed tools.</summary>
    [HttpPost("{id}/stop")]
    public async Task<IActionResult> Stop(string id)
    {
        var session = await Authorise(id);
        if (session is null) return Unauthorized();

        // The token is spent: nothing more should be accepted for this meeting.
        session.BotToken = null;
        await _db.SaveChangesAsync();

        await _meetings.CloseAsync(session);
        return Ok(new { ok = true });
    }

    /// <summary>The bot could not join, or stopped unexpectedly.</summary>
    [HttpPost("{id}/fail")]
    public async Task<IActionResult> Fail(string id, [FromBody] FailRequest? request)
    {
        var session = await Authorise(id);
        if (session is null) return Unauthorized();

        _logger.LogWarning("Meeting {Id}: bot reported failure: {Reason}", id, request?.Reason);

        // Anything already captured is worth keeping, so a failure after some
        // audio arrived still gets filed rather than thrown away.
        if (!string.IsNullOrWhiteSpace(session.Transcript))
        {
            session.BotToken = null;
            session.ErrorMessage = request?.Reason;
            await _db.SaveChangesAsync();
            await _meetings.CloseAsync(session);
            return Ok(new { ok = true, kept = true });
        }

        session.Status = MeetingSessionStatus.Failed;
        session.ErrorMessage = request?.Reason ?? "The bot could not record this meeting.";
        session.BotToken = null;
        session.EndedAt = DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, kept = false });
    }

    public class FailRequest
    {
        public string? Reason { get; set; }
    }
}
