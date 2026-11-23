using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// Team membership and the approval gate.
///
/// Kept out of the controllers because "can this person edit this prompt" is
/// asked from several places, and an authorisation rule that is written twice
/// is an authorisation rule that will eventually disagree with itself.
/// </summary>
public class TeamService
{
    private readonly PromptDbContext _db;

    public TeamService(PromptDbContext db) => _db = db;

    /// <summary>The user's role in a team, or null if they are not a member.</summary>
    public async Task<string?> RoleOfAsync(string teamId, string userId, CancellationToken ct = default)
    {
        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct);
        if (team == null) return null;
        if (team.OwnerId == userId) return TeamRoles.Owner;

        return await _db.TeamMembers
            .Where(m => m.TeamId == teamId && m.UserId == userId && m.Status == "active")
            .Select(m => m.Role)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Teams the user can see, with their role in each.</summary>
    public async Task<List<(Team Team, string Role, int MemberCount)>> TeamsForAsync(string userId, CancellationToken ct = default)
    {
        var memberships = await _db.TeamMembers
            .Where(m => m.UserId == userId && m.Status == "active")
            .Select(m => new { m.TeamId, m.Role })
            .ToListAsync(ct);

        var ids = memberships.Select(m => m.TeamId).ToList();

        var teams = await _db.Teams
            .Where(t => t.OwnerId == userId || ids.Contains(t.Id))
            .ToListAsync(ct);

        var counts = await _db.TeamMembers
            .Where(m => teams.Select(t => t.Id).Contains(m.TeamId) && m.Status == "active")
            .GroupBy(m => m.TeamId)
            .Select(g => new { TeamId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TeamId, x => x.Count, ct);

        return teams.Select(t =>
        {
            var role = t.OwnerId == userId
                ? TeamRoles.Owner
                : memberships.FirstOrDefault(m => m.TeamId == t.Id)?.Role ?? TeamRoles.Viewer;
            return (t, role, counts.GetValueOrDefault(t.Id, 0));
        }).ToList();
    }

    /// <summary>
    /// Whether a save should be applied directly or queued for review.
    /// Personal prompts always apply directly; only a team with approval turned
    /// on, being edited by an editor, produces a request.
    /// </summary>
    public async Task<bool> RequiresApprovalAsync(Prompt prompt, string userId, CancellationToken ct = default)
    {
        if (prompt.TeamId == null) return false;

        var team = await _db.Teams.FirstOrDefaultAsync(t => t.Id == prompt.TeamId, ct);
        if (team is not { RequireApproval: true }) return false;

        var role = await RoleOfAsync(prompt.TeamId, userId, ct);
        return TeamRoles.RequiresApproval(role);
    }

    /// <summary>Prompt ids the user can reach through team membership.</summary>
    public async Task<List<string>> AccessibleTeamIdsAsync(string userId, CancellationToken ct = default)
    {
        var owned = await _db.Teams.Where(t => t.OwnerId == userId).Select(t => t.Id).ToListAsync(ct);
        var joined = await _db.TeamMembers
            .Where(m => m.UserId == userId && m.Status == "active")
            .Select(m => m.TeamId)
            .ToListAsync(ct);
        return owned.Concat(joined).Distinct().ToList();
    }
}
