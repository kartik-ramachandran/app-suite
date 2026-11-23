using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;
using MeetingManager.Api.Models;

namespace MeetingManager.Api.Services.MeetingCapture;

/// <summary>
/// Owns the life of a meeting session: arming it from a calendar event, taking
/// audio while it runs, and handing the finished transcript to the Signal tools.
///
/// Transcription is incremental. Each chunk is transcribed on arrival and
/// appended, so the transcript grows while the meeting is still going rather
/// than appearing all at once at the end. That is the whole point of the live
/// layer -- an office floor watching a meeting is only interesting if it has
/// something to react to before the meeting is over.
/// </summary>
public class MeetingSessionService
{
    private readonly PromptDbContext _db;
    private readonly InteractionService _interaction;
    private readonly MeetingCaptureRegistry _registry;
    private readonly MeetingIntelligenceService _intelligence;
    private readonly ILogger<MeetingSessionService> _logger;

    public MeetingSessionService(
        PromptDbContext db,
        InteractionService interaction,
        MeetingCaptureRegistry registry,
        MeetingIntelligenceService intelligence,
        ILogger<MeetingSessionService> logger)
    {
        _db = db;
        _interaction = interaction;
        _registry = registry;
        _intelligence = intelligence;
        _logger = logger;
    }

    /// <summary>Guess the platform from the join link, for the UI and for bot routing.</summary>
    public static string PlatformFor(string? meetingLink)
    {
        var link = meetingLink?.ToLowerInvariant() ?? string.Empty;
        if (link.Contains("meet.google.")) return "google";
        if (link.Contains("teams.microsoft.") || link.Contains("teams.live.")) return "microsoft";
        if (link.Contains("zoom.us") || link.Contains("zoom.com")) return "zoom";
        if (link.Contains("webex.")) return "webex";
        return "other";
    }

    public async Task<MeetingSession> ArmAsync(
        string userId,
        CalendarEventLite ev,
        string source,
        IEnumerable<string> autoTools,
        CancellationToken ct = default)
    {
        // Re-arming a meeting that is already armed should not create a second one.
        var existing = await _db.MeetingSessions
            .FirstOrDefaultAsync(m => m.UserId == userId
                && m.CalendarEventId == ev.Id
                && MeetingSessionStatus.Active.Contains(m.Status), ct);

        var session = existing ?? new MeetingSession
        {
            UserId = userId,
            CalendarEventId = ev.Id,
            CreatedAt = DateTime.UtcNow,
        };

        session.Title = ev.Title;
        session.MeetingLink = ev.MeetingLink;
        session.Platform = PlatformFor(ev.MeetingLink);
        session.Source = source;
        session.ScheduledStart = ev.Start;
        session.ScheduledEnd = ev.End;
        session.AutoToolsJson = JsonSerializer.Serialize(autoTools.ToArray());
        session.Status = MeetingSessionStatus.Armed;
        session.ErrorMessage = null;
        session.UpdatedAt = DateTime.UtcNow;

        if (existing is null) _db.Add(session);
        await _db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>Capture is starting. Asks the source to bring itself up first.</summary>
    public async Task<CaptureStartResult> StartAsync(MeetingSession session, CancellationToken ct = default)
    {
        var source = _registry.For(session);
        var result = await source.StartAsync(session, ct);

        if (!result.Started)
        {
            session.Status = MeetingSessionStatus.Failed;
            session.ErrorMessage = result.Message;
        }
        else
        {
            session.Status = MeetingSessionStatus.Live;
            session.StartedAt ??= DateTime.UtcNow;
            session.ErrorMessage = null;
            // A bot source hands back its own id; the webhook needs it to find
            // this session again.
            if (!string.IsNullOrWhiteSpace(result.ExternalId)) session.ExternalId = result.ExternalId;
        }

        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>
    /// Take one slice of the meeting. Audio is transcribed here and appended;
    /// a source that already produces text (most bot providers do) can pass it
    /// straight through as <paramref name="text"/>.
    /// </summary>
    public async Task<MeetingSession> AppendChunkAsync(
        MeetingSession session,
        string? audioPath,
        string? text,
        CancellationToken ct = default)
    {
        var piece = text;

        if (string.IsNullOrWhiteSpace(piece) && !string.IsNullOrWhiteSpace(audioPath))
        {
            try
            {
                piece = await _interaction.TranscribeAsync(audioPath);
            }
            catch (Exception ex)
            {
                // One unreadable slice must not end the meeting; the next chunk
                // may be fine, and a gap is better than a lost recording.
                _logger.LogWarning(ex, "Meeting {Id}: chunk could not be transcribed", session.Id);
                piece = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(piece))
        {
            session.Transcript = string.IsNullOrEmpty(session.Transcript)
                ? piece.Trim()
                : session.Transcript + "\n" + piece.Trim();
        }

        session.ChunkCount++;
        session.LastChunkAt = DateTime.UtcNow;
        if (session.Status == MeetingSessionStatus.Armed)
        {
            session.Status = MeetingSessionStatus.Live;
            session.StartedAt ??= DateTime.UtcNow;
        }
        session.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return session;
    }

    /// <summary>
    /// The meeting is over. Stops the capture, files the transcript as an
    /// InteractionSession so it appears in Signal's history like any other
    /// analysis, and runs whichever tools were armed.
    /// </summary>
    public async Task<MeetingSession> CloseAsync(MeetingSession session, CancellationToken ct = default)
    {
        await _registry.For(session).StopAsync(session, ct);

        session.Status = MeetingSessionStatus.Closing;
        session.EndedAt ??= DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (string.IsNullOrWhiteSpace(session.Transcript))
        {
            session.Status = MeetingSessionStatus.Failed;
            session.ErrorMessage = "Nothing was captured from this meeting.";
            session.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return session;
        }

        var interaction = new InteractionSession
        {
            UserId = session.UserId,
            SourceLabel = session.Title,
            Transcript = session.Transcript,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Add(interaction);
        await _db.SaveChangesAsync(ct);

        session.InteractionSessionId = interaction.Id;
        session.Status = MeetingSessionStatus.Analysing;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        string[] tools;
        try { tools = JsonSerializer.Deserialize<string[]>(session.AutoToolsJson) ?? []; }
        catch { tools = []; }

        var analyses = new Dictionary<string, object>();
        foreach (var tool in tools)
        {
            try
            {
                analyses[tool] = await _interaction.AnalyseAsync(session.Transcript, tool);
            }
            catch (Exception ex)
            {
                // A failing tool should not cost us the transcript or the tools
                // that did work.
                _logger.LogWarning(ex, "Meeting {Id}: tool {Tool} failed", session.Id, tool);
            }
        }

        if (analyses.Count > 0)
        {
            interaction.AnalysesJson = JsonSerializer.Serialize(analyses);
            interaction.UpdatedAt = DateTime.UtcNow;
        }

        // The payoff layer: place this meeting in its recurring series, then
        // build the summary, decisions and action items. Generated here rather
        // than on request because a summary you have to go and ask for is a
        // summary nobody reads.
        //
        // Wrapped because none of it is worth losing the transcript over: if
        // the extraction model is down, the meeting still closes and the
        // outcome can be rebuilt later from /generate.
        try
        {
            await _intelligence.AttachToSeriesAsync(session, ct);
            await _intelligence.GenerateOutcomeAsync(session, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Meeting {Id}: outcome generation failed; transcript is safe", session.Id);
        }

        session.Status = MeetingSessionStatus.Done;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return session;
    }
}

/// <summary>The bits of a calendar event arming needs, so the service does not depend on the calendar model.</summary>
public record CalendarEventLite(int Id, string Title, string? MeetingLink, DateTime Start, DateTime End);
