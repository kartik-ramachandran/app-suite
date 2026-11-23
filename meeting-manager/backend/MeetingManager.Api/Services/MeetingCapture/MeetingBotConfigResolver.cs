using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;
using MeetingManager.Api.Models;

namespace MeetingManager.Api.Services.MeetingCapture;

/// <summary>
/// Works out how the notetaker should behave for a given meeting.
///
/// Order is the person, then their team, then the site default, and within each
/// the settings pinned to that meeting's platform beat the catch-all. Falling
/// off the end is fine: there is a sane default, because running the bot needs
/// no account from anyone.
/// </summary>
public class MeetingBotConfigResolver
{
    private readonly PromptDbContext _db;
    private readonly ILogger<MeetingBotConfigResolver> _logger;

    public MeetingBotConfigResolver(PromptDbContext db, ILogger<MeetingBotConfigResolver> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Used when nobody has set anything, at any level.</summary>
    public static MeetingBotConfig Default => new();

    /// <summary>The organisation this user belongs to, if any.</summary>
    public async Task<string?> OrganizationIdFor(string userId, CancellationToken ct = default)
    {
        try
        {
            return await _db.OrganizationMembers
                .Where(m => m.UserId == userId && m.Status == "active")
                .Select(m => m.OrganizationId)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Could not read organisation membership for {User}", userId);
            return null;
        }
    }

    /// <summary>
    /// The settings that apply to this meeting. Never null -- an unconfigured
    /// user gets the defaults rather than nothing.
    /// </summary>
    public async Task<MeetingBotConfig> ResolveAsync(
        string userId,
        string? organizationId,
        string meetingPlatform = "*",
        CancellationToken ct = default)
    {
        List<MeetingBotConfig> candidates;
        try
        {
            candidates = await _db.MeetingBotConfigs
                .Where(c => (
                    (c.Scope == MeetingBotScopes.User && c.OwnerId == userId) ||
                    (c.Scope == MeetingBotScopes.Organization && organizationId != null && c.OwnerId == organizationId) ||
                    c.Scope == MeetingBotScopes.Platform))
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            // Table not created yet on a database that has not restarted.
            _logger.LogInformation(ex, "Meeting bot settings unavailable; using defaults");
            return Default;
        }

        foreach (var scope in MeetingBotScopes.Precedence)
        {
            var atScope = candidates
                .Where(c => string.Equals(c.Scope, scope, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (atScope.Count == 0) continue;

            var exact = atScope.FirstOrDefault(c =>
                string.Equals(c.MeetingPlatform, meetingPlatform, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;

            var any = atScope.FirstOrDefault(c => c.MeetingPlatform == "*");
            if (any is not null) return any;
        }

        return Default;
    }

    /// <summary>Convenience for callers that only have a user.</summary>
    public async Task<MeetingBotConfig> ResolveForUserAsync(
        string userId, string meetingPlatform = "*", CancellationToken ct = default)
    {
        var orgId = await OrganizationIdFor(userId, ct);
        return await ResolveAsync(userId, orgId, meetingPlatform, ct);
    }
}
