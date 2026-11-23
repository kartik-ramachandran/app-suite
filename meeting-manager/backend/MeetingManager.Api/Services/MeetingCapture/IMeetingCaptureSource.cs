using MeetingManager.Api.Models;

namespace MeetingManager.Api.Services.MeetingCapture;

/// <summary>
/// How audio gets from a meeting to us. Everything downstream -- transcript
/// assembly, the Signal tools, the meeting workspace -- is identical whichever
/// source is used, so a source only has to answer three questions: can I take
/// this meeting, start, and stop.
///
/// Sources are deliberately allowed to be unavailable at runtime (no vendor key
/// configured, no bot built yet). <see cref="IsConfigured"/> reports that, and
/// the arming endpoint refuses clearly rather than failing at meeting time.
/// </summary>
public interface IMeetingCaptureSource
{
    /// <summary>One of <see cref="MeetingCaptureSources"/>.</summary>
    string Key { get; }

    /// <summary>Shown in the picker.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Whether this source can run for a particular person. Availability is not
    /// a property of the deployment: one user may have their own meeting-bot
    /// account while another has none, so this has to be asked per user rather
    /// than read once from configuration.
    /// </summary>
    Task<SourceAvailability> AvailabilityAsync(string userId, string meetingPlatform = "*", CancellationToken ct = default);

    /// <summary>
    /// Whether this source can attend a given meeting. Tab capture can only take
    /// meetings that open in a browser; a dial-in bot needs a join link it
    /// recognises.
    /// </summary>
    bool CanAttend(MeetingSession session);

    /// <summary>
    /// Bring the capture up. Tab capture has nothing to do here -- the extension
    /// pushes chunks when the user is in the meeting -- while a bot source has
    /// to actually dispatch a bot to the join link.
    /// </summary>
    Task<CaptureStartResult> StartAsync(MeetingSession session, CancellationToken ct = default);

    /// <summary>Tear the capture down. Must be safe to call more than once.</summary>
    Task StopAsync(MeetingSession session, CancellationToken ct = default);
}

/// <param name="Available">Whether this source will work for this user right now.</param>
/// <param name="Reason">Why not, when it will not. Shown next to the disabled option.</param>
public readonly record struct SourceAvailability(bool Available, string? Reason = null)
{
    public static SourceAvailability Yes => new(true);
    public static SourceAvailability No(string reason) => new(false, reason);
}

/// <param name="Started">False when the source declined; Message says why.</param>
/// <param name="Message">Human-readable detail, surfaced on the meeting.</param>
/// <param name="ExternalId">Vendor-side handle, when the source has one.</param>
public readonly record struct CaptureStartResult(bool Started, string? Message = null, string? ExternalId = null)
{
    public static CaptureStartResult Ok(string? externalId = null) => new(true, null, externalId);
    public static CaptureStartResult Declined(string message) => new(false, message);
}
