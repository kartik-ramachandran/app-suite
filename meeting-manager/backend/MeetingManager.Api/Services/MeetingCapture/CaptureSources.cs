using MeetingManager.Api.Data;
using MeetingManager.Api.Models;

namespace MeetingManager.Api.Services.MeetingCapture;

/// <summary>
/// The browser extension records the meeting tab and posts chunks to us. There
/// is nothing to dispatch: the capture exists because the user is in the
/// meeting with the tab open, so Start just marks the session ready to receive.
/// Always available -- it needs no vendor and no credentials.
/// </summary>
public class TabCaptureSource : IMeetingCaptureSource
{
    public string Key => MeetingCaptureSources.TabCapture;
    public string DisplayName => "Browser tab (extension)";

    /// <summary>Needs no account, so it is available to everyone.</summary>
    public Task<SourceAvailability> AvailabilityAsync(string userId, string meetingPlatform = "*", CancellationToken ct = default)
        => Task.FromResult(SourceAvailability.Yes);

    /// <summary>
    /// Anything that runs in a browser tab. Dial-in-only meetings have no web
    /// client to capture, but we cannot tell that from the link, so we accept
    /// and let the extension report if it cannot find the tab.
    /// </summary>
    public bool CanAttend(MeetingSession session) => true;

    public Task<CaptureStartResult> StartAsync(MeetingSession session, CancellationToken ct = default)
        => Task.FromResult(CaptureStartResult.Ok());

    public Task StopAsync(MeetingSession session, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// Our own bot: a headless Chromium that joins the call as a guest, records what
/// it hears and posts it back in segments. No provider, no per-minute fee -- the
/// cost is a container for the length of the meeting.
///
/// Joining as a guest in a browser is the only approach that covers Google Meet,
/// which has no bot API at all, and it happens to cover Teams and Zoom web
/// clients the same way. The price of that is maintenance: these are three web
/// UIs that change whenever their owners feel like it.
/// </summary>
public class NativeBotCaptureSource : IMeetingCaptureSource
{
    private readonly NativeBotLauncher _launcher;
    private readonly PromptDbContext _db;
    private readonly MeetingBotConfigResolver _settings;

    public NativeBotCaptureSource(NativeBotLauncher launcher, PromptDbContext db, MeetingBotConfigResolver settings)
    {
        _launcher = launcher;
        _db = db;
        _settings = settings;
    }

    public string Key => MeetingCaptureSources.NativeBot;
    public string DisplayName => "Send a bot to the meeting";

    public async Task<SourceAvailability> AvailabilityAsync(string userId, string meetingPlatform = "*", CancellationToken ct = default)
    {
        if (!_launcher.CanLaunch) return SourceAvailability.No(_launcher.WhyNot!);

        // A person or their team can switch the notetaker off without anyone
        // having to remove it from every meeting one at a time.
        var settings = await _settings.ResolveForUserAsync(userId, meetingPlatform, ct);
        return settings.Enabled
            ? SourceAvailability.Yes
            : SourceAvailability.No("The notetaker is switched off in your settings, or your team's.");
    }

    /// <summary>Anything with a link a browser can open.</summary>
    public bool CanAttend(MeetingSession session) => !string.IsNullOrWhiteSpace(session.MeetingLink);

    public async Task<CaptureStartResult> StartAsync(MeetingSession session, CancellationToken ct = default)
    {
        if (!CanAttend(session)) return CaptureStartResult.Declined("This meeting has no link for the bot to open.");
        if (!_launcher.CanLaunch) return CaptureStartResult.Declined(_launcher.WhyNot!);

        // One bot per meeting: a second would sit in the call recording a
        // duplicate of everything the first one hears.
        if (!string.IsNullOrWhiteSpace(session.ExternalId)) return CaptureStartResult.Ok(session.ExternalId);

        // The bot has no user session, so it gets a token good for this meeting
        // and nothing else. Cleared again the moment the meeting closes.
        session.BotToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await _db.SaveChangesAsync(ct);

        var settings = await _settings.ResolveForUserAsync(session.UserId, session.Platform, ct);

        var (started, message, handle) = await _launcher.LaunchAsync(
            session.Id, session.MeetingLink!, session.BotToken, settings.BotName, settings.MaxMinutes, ct);

        if (!started)
        {
            session.BotToken = null;
            await _db.SaveChangesAsync(ct);
            return CaptureStartResult.Declined(message ?? "The bot could not be started.");
        }

        return CaptureStartResult.Ok(handle);
    }

    /// <summary>
    /// The bot leaves on its own when the call ends or it is removed, and the
    /// container goes with it, so there is nothing to tear down from here.
    /// </summary>
    public Task StopAsync(MeetingSession session, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Finds the source responsible for a meeting.</summary>
public class MeetingCaptureRegistry
{
    private readonly Dictionary<string, IMeetingCaptureSource> _sources;

    public MeetingCaptureRegistry(IEnumerable<IMeetingCaptureSource> sources)
        => _sources = sources.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<IMeetingCaptureSource> All => _sources.Values;

    public IMeetingCaptureSource? Get(string? key)
        => key is not null && _sources.TryGetValue(key, out var s) ? s : null;

    /// <summary>Falls back to tab capture, which is always available.</summary>
    public IMeetingCaptureSource For(MeetingSession session)
        => Get(session.Source) ?? _sources[MeetingCaptureSources.TabCapture];
}
