using System.ComponentModel.DataAnnotations;

namespace LifeManager.Api.Models;

/// <summary>
/// A spoken life journal entry.
///
/// The premise of the voice layer is that people will say things out loud that
/// they would never sit down and type into a form. A two-minute ramble on the
/// drive home carries mood, worries, decisions and relationships all at once —
/// so the transcript is kept verbatim and the structured data is *derived* from
/// it, never the other way round. The user's own words remain the record.
/// </summary>
public class LifeVoiceEntry
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;

    /// <summary>When the entry is about — usually today, but a user can backdate.</summary>
    public DateTime EntryDate { get; set; } = DateTime.UtcNow.Date;

    /// <summary>Raw speech-to-text output, kept unedited as the source of truth.</summary>
    public string Transcript { get; set; } = string.Empty;

    /// <summary>A tidied second-person retelling, for reading back later.</summary>
    public string Narrative { get; set; } = string.Empty;

    /// <summary>One-line gist, used in lists and timelines.</summary>
    [MaxLength(300)]
    public string Headline { get; set; } = string.Empty;

    /// <summary>Dominant feeling the speaker expressed, in their register.</summary>
    [MaxLength(60)]
    public string? Sentiment { get; set; }

    /// <summary>Seconds of audio, for "you journalled 14 minutes this week".</summary>
    public int DurationSeconds { get; set; }

    /// <summary>
    /// What the model pulled out of the transcript, as JSON:
    /// { checkIn, goals[], decisions[], relationships[], themes[], openLoops[] }
    ///
    /// Held as JSON rather than rows because extraction is a *proposal*. Nothing
    /// touches the user's real tracker until they accept it — an AI that
    /// silently rewrites your life log is not a journal, it is a liability.
    /// </summary>
    public string ExtractionJson { get; set; } = "{}";

    /// <summary>pending | ready | failed — extraction state, not audio state.</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "pending";
    public string? ErrorMessage { get; set; }

    /// <summary>Set once the user has accepted the extracted items into the tracker.</summary>
    public bool Applied { get; set; }
    public DateTime? AppliedAt { get; set; }

    /// <summary>Check-in row created when this entry was applied, so it can be undone.</summary>
    public int? CreatedCheckInId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}

/// <summary>
/// One plausible way the user's life could unfold from here.
///
/// Deliberately plural and non-prescriptive: the product promise is "show me
/// the possibilities", not "tell me what to do". Scenarios are generated as a
/// set so the user sees a spread — the path they are currently on, the ones
/// available if something changes, and the one they are drifting toward without
/// meaning to.
/// </summary>
public class LifeScenario
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;

    /// <summary>Groups scenarios generated together, so a set can be shown or discarded as one.</summary>
    public string BatchId { get; set; } = Guid.NewGuid().ToString();

    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// current-path  — where today's habits lead if nothing changes
    /// opportunity   — a better branch that is genuinely reachable
    /// risk          — a plausible bad turn worth naming early
    /// wildcard      — low-likelihood, high-impact
    /// </summary>
    [MaxLength(20)]
    public string Kind { get; set; } = "opportunity";

    /// <summary>Horizon this plays out over: 3-months | 1-year | 3-years</summary>
    [MaxLength(20)]
    public string Horizon { get; set; } = "1-year";

    /// <summary>The scenario itself, in second person.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// How likely, 0-100, given the user's *observed* behaviour.
    ///
    /// Explicitly a rough read rather than a forecast. It is shown as a band in
    /// the UI, because presenting a life projection as a precise percentage
    /// would be false confidence about something nobody can compute.
    /// </summary>
    public int Likelihood { get; set; } = 50;

    /// <summary>JSON array of the evidence in their data pointing this way.</summary>
    public string DriversJson { get; set; } = "[]";

    /// <summary>JSON array of what would have to hold true.</summary>
    public string AssumptionsJson { get; set; } = "[]";

    /// <summary>JSON array of concrete moves that make this more (or less) likely.</summary>
    public string ActionsJson { get; set; } = "[]";

    /// <summary>JSON array of early warning signs that this is becoming real.</summary>
    public string SignalsJson { get; set; } = "[]";

    /// <summary>Which life areas this touches, JSON array: Health, Career, Money, Relationships…</summary>
    public string AreasJson { get; set; } = "[]";

    /// <summary>User can pin the scenarios worth steering toward or away from.</summary>
    public bool IsPinned { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}
