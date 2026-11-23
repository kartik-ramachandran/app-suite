using System.ComponentModel.DataAnnotations;

namespace MeetingManager.Api.Models;

/// <summary>
/// The recurring meeting a session belongs to — the standup, the weekly 1:1.
///
/// A meeting nobody links to anything is a transcript. A meeting linked to the
/// twelve before it is a record of whether the team does what it says it will.
/// Sessions are matched into a series by normalised title plus participants, so
/// this fills in without anyone maintaining it.
/// </summary>
public class MeetingSeries
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;

    [Required, MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    /// <summary>Lowercased title with dates/ordinals stripped — the matching key.</summary>
    [MaxLength(300)]
    public string NormalizedTitle { get; set; } = string.Empty;

    /// <summary>Calendar recurrence id when the provider gave us one; far more reliable than the title.</summary>
    [MaxLength(300)]
    public string? RecurrenceKey { get; set; }

    public int SessionCount { get; set; }
    public DateTime? FirstMeetingAt { get; set; }
    public DateTime? LastMeetingAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The part of a meeting a person actually wants: what was decided, what was
/// agreed, and who owes what.
///
/// Generated on close rather than on request. A summary you have to go and ask
/// for is a summary nobody reads.
/// </summary>
public class MeetingOutcome
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MeetingSessionId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    public string? Summary { get; set; }

    /// <summary>JSON array of { text, context }.</summary>
    public string DecisionsJson { get; set; } = "[]";

    /// <summary>JSON array of { text, owner, dueDate, status, carriedFromSessionId }.</summary>
    public string ActionItemsJson { get; set; } = "[]";

    /// <summary>JSON array of { text } — raised, not resolved.</summary>
    public string OpenQuestionsJson { get; set; } = "[]";

    /// <summary>JSON array of { name, talkTimeSeconds, wordCount, sharePct, commitments }.</summary>
    public string SpeakerStatsJson { get; set; } = "[]";

    /// <summary>What moved since the previous session in the same series.</summary>
    public string? ChangesSinceLast { get; set; }

    /// <summary>Draft follow-up email, ready to send from the Follow-Up Hub.</summary>
    public string? FollowUpSubject { get; set; }
    public string? FollowUpBody { get; set; }

    /// <summary>pending | ready | failed</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "pending";
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public MeetingSession? Session { get; set; }
}

/// <summary>
/// One commitment made out loud, tracked across meetings.
///
/// Split out of the outcome JSON because these need to be queried ("what is
/// open for me?"), carried forward into the next session in the series, and
/// closed independently of the meeting that created them.
/// </summary>
public class MeetingActionItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string MeetingSessionId { get; set; } = string.Empty;
    public string? MeetingSeriesId { get; set; }
    public string UserId { get; set; } = string.Empty;

    [Required, MaxLength(1000)]
    public string Text { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Owner { get; set; }

    public DateTime? DueDate { get; set; }

    /// <summary>open | done | dropped</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "open";

    /// <summary>Set when this item was still open at the end of an earlier session.</summary>
    public string? CarriedFromSessionId { get; set; }

    /// <summary>How many sessions this has survived without being closed. The number that starts arguments.</summary>
    public int CarryOverCount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}
