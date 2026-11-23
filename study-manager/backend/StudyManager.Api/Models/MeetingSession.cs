namespace StudyManager.Api.Models;

/// <summary>
/// A meeting the notetaker is attending, or has attended.
///
/// This is the bridge between three things that already existed separately: a
/// <c>CalendarEvent</c> (when the meeting is and how to join it), a capture
/// source (how the audio actually reaches us), and an
/// <c>InteractionSession</c> (the transcript and the eight Signal analyses run
/// over it). Nothing here knows how audio is captured -- that is the capture
/// source's job -- so a meeting recorded from a browser tab and one recorded by
/// a bot that dialled in are the same row with a different <c>Source</c>.
/// </summary>
public class MeetingSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;

    /// <summary>The calendar event this was armed from, when there is one.</summary>
    public int? CalendarEventId { get; set; }

    /// <summary>Where the transcript and analyses live once we have them.</summary>
    public string? InteractionSessionId { get; set; }

    public string Title { get; set; } = string.Empty;
    /// <summary>Join URL, so a bot source knows where to go.</summary>
    public string? MeetingLink { get; set; }
    /// <summary>google | microsoft | zoom | other -- derived from the link.</summary>
    public string Platform { get; set; } = "other";

    /// <summary>Which capture source is responsible. See MeetingCaptureSources.</summary>
    public string Source { get; set; } = MeetingCaptureSources.TabCapture;

    /// <summary>
    /// The capture source's own handle for this meeting -- a provider's bot id.
    /// Also how an inbound webhook finds the session it belongs to, since the
    /// provider knows nothing about our ids.
    /// </summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// Lets our own bot post this meeting's audio without a user session. Scoped
    /// to this meeting alone, so a bot that leaks can only append to the call it
    /// was sent to. Cleared when the meeting closes.
    /// </summary>
    public string? BotToken { get; set; }

    /// <summary>
    /// armed    -- scheduled, waiting for the start time
    /// live     -- audio is arriving
    /// closing  -- capture ended, transcript being finalised
    /// analysing-- Signal tools running over the transcript
    /// done | failed | cancelled
    /// </summary>
    public string Status { get; set; } = MeetingSessionStatus.Armed;
    public string? ErrorMessage { get; set; }

    /// <summary>Growing transcript while live; copied to the InteractionSession at the end.</summary>
    public string Transcript { get; set; } = string.Empty;
    /// <summary>Number of audio chunks accepted, for progress and for spotting a stalled capture.</summary>
    public int ChunkCount { get; set; }

    /// <summary>
    /// Who is in the room, as the bot last saw it: a JSON array of
    /// { name, speaking }. Drives the meeting view, and is the only reason we
    /// know a meeting has people in it rather than just audio.
    /// </summary>
    public string ParticipantsJson { get; set; } = "[]";

    /// <summary>Tool keys to run automatically once the meeting ends, JSON array.</summary>
    public string AutoToolsJson { get; set; } = "[]";

    /// <summary>
    /// The recurring meeting this instance belongs to, matched on close. Null
    /// for a one-off, or for a meeting we could not confidently place in a
    /// series -- a wrong link is worse than no link, because it makes the
    /// "what changed since last week" view lie.
    /// </summary>
    public string? MeetingSeriesId { get; set; }

    public DateTime ScheduledStart { get; set; }
    public DateTime ScheduledEnd { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    /// <summary>Last time a chunk arrived; a live session that goes quiet is stale.</summary>
    public DateTime? LastChunkAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class MeetingSessionStatus
{
    public const string Armed = "armed";
    public const string Live = "live";
    public const string Closing = "closing";
    public const string Analysing = "analysing";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static readonly string[] Active = [Armed, Live, Closing, Analysing];
}

public static class MeetingCaptureSources
{
    /// <summary>The browser extension captures the meeting tab. Needs you present.</summary>
    public const string TabCapture = "tab";

    /// <summary>
    /// Our own bot joins the call and records it. Renting one from a provider
    /// was removed: it meant paying per meeting-minute for something we give
    /// away, and sending every customer's meeting audio through a third party.
    /// </summary>
    public const string NativeBot = "native-bot";
}
