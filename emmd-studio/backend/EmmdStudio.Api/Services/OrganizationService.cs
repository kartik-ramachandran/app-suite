using Microsoft.EntityFrameworkCore;
using EmmdStudio.Api.Data;
using EmmdStudio.Api.Models;

namespace EmmdStudio.Api.Services;

/// <summary>
/// Shared organisation helpers: membership/plan lookups and the free-tier
/// usage cap. Used by OrganizationController and the usage-limit middleware.
/// </summary>
public class OrganizationService
{
    private readonly PromptDbContext _db;
    private readonly IConfiguration _config;

    public OrganizationService(PromptDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    /// <summary>
    /// Per-minute rate limit for a free individual user's metered in-app calls.
    /// Free accounts are UNLIMITED overall — only throttled to prevent abuse.
    /// </summary>
    public int FreeTierRatePerMin => _config.GetValue<int?>("FreeTier:RateLimitPerMin") ?? 15;

    /// <summary>The user's single active org membership, or null.</summary>
    public Task<OrganizationMember?> GetActiveMembershipAsync(string userId) =>
        _db.OrganizationMembers
           .FirstOrDefaultAsync(m => m.UserId == userId && m.Status == "active");

    public Task<Organization?> GetByIdAsync(string organizationId) =>
        _db.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId);

    /// <summary>Resolved plan for a user: their org's plan, or "free" if none.</summary>
    public async Task<string> GetEffectivePlanAsync(string userId)
    {
        var membership = await GetActiveMembershipAsync(userId);
        if (membership == null) return OrgPlans.Free;
        var org = await GetByIdAsync(membership.OrganizationId);
        return org?.Plan ?? OrgPlans.Free;
    }

    /// <summary>
    /// True when the user is on the free tier (no membership, or a member of a
    /// free-plan org) and should be subject to soft usage limits.
    /// </summary>
    public async Task<bool> IsFreeTierAsync(string userId) =>
        !OrgPlans.IsPaid(await GetEffectivePlanAsync(userId));

    /// <summary>
    /// The only modules that count toward the free-tier rate limit. AppUsage also
    /// records every other module for adoption reporting, and those rows must
    /// never throttle anyone.
    /// </summary>
    public static readonly string[] MeteredModules = { "pdf", "interaction", "prism" };

    /// <summary>Count of a user's metered calls in the trailing <paramref name="seconds"/> window (rate limiting).</summary>
    public Task<int> CountRecentUsageAsync(string userId, int seconds)
    {
        var since = DateTime.UtcNow.AddSeconds(-seconds);
        return _db.AppUsages.CountAsync(u =>
            u.UserId == userId && u.CreatedAt >= since && MeteredModules.Contains(u.Module));
    }

    public async Task RecordUsageAsync(string userId, string module, string endpoint)
    {
        var membership = await GetActiveMembershipAsync(userId);
        _db.AppUsages.Add(new AppUsage
        {
            UserId = userId,
            OrganizationId = membership?.OrganizationId,
            Module = module,
            Endpoint = endpoint,
        });
        await _db.SaveChangesAsync();
    }
}
