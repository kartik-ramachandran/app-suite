using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Models;
using PromptStudio.Api.Services;
using System.Security.Claims;

namespace PromptStudio.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/teams")]
public class TeamsController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly TeamService _teams;
    private readonly IEmailService _email;
    private readonly ILogger<TeamsController> _logger;

    public TeamsController(PromptDbContext context, TeamService teams, IEmailService email, ILogger<TeamsController> logger)
    {
        _context = context;
        _teams = teams;
        _email = email;
        _logger = logger;
    }

    private string GetCurrentUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    [HttpGet]
    public async Task<ActionResult> GetTeams()
    {
        var userId = GetCurrentUserId();
        var teams = await _teams.TeamsForAsync(userId);

        return Ok(teams.Select(t => new
        {
            t.Team.Id,
            t.Team.Name,
            t.Team.Description,
            t.Team.RequireApproval,
            t.Team.OrganizationId,
            role = t.Role,
            memberCount = t.MemberCount,
            t.Team.CreatedAt
        }));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult> GetTeam(string id)
    {
        var userId = GetCurrentUserId();
        var role = await _teams.RoleOfAsync(id, userId);
        if (role == null) return NotFound();

        var team = await _context.Teams.FirstAsync(t => t.Id == id);

        var members = await (
            from m in _context.TeamMembers
            join u in _context.Users on m.UserId equals u.Id
            where m.TeamId == id
            select new { m.Id, m.UserId, u.Name, u.Email, m.Role, m.Status, m.JoinedAt }
        ).ToListAsync();

        var promptCount = await _context.Prompts.CountAsync(p => p.TeamId == id && p.IsActive);
        var pendingApprovals = await _context.PromptApprovals.CountAsync(a => a.TeamId == id && a.Status == "pending");

        return Ok(new
        {
            team.Id, team.Name, team.Description, team.RequireApproval, team.OrganizationId,
            team.OwnerId, team.CreatedAt,
            yourRole = role,
            members,
            promptCount,
            pendingApprovals
        });
    }

    [HttpPost]
    public async Task<ActionResult> CreateTeam([FromBody] SaveTeamRequest request)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "The team needs a name." });

        var team = new Team
        {
            Name = request.Name,
            Description = request.Description,
            OrganizationId = request.OrganizationId,
            OwnerId = userId,
            RequireApproval = request.RequireApproval
        };
        _context.Teams.Add(team);

        // The owner is also a member row, so member queries do not have to
        // special-case them everywhere.
        _context.TeamMembers.Add(new TeamMember
        {
            TeamId = team.Id,
            UserId = userId,
            Role = TeamRoles.Owner,
            Status = "active"
        });

        await _context.SaveChangesAsync();
        return Ok(new { team.Id, team.Name, role = TeamRoles.Owner });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> UpdateTeam(string id, [FromBody] SaveTeamRequest request)
    {
        var userId = GetCurrentUserId();
        var role = await _teams.RoleOfAsync(id, userId);
        if (!TeamRoles.CanManage(role)) return Forbid();

        var team = await _context.Teams.FirstAsync(t => t.Id == id);
        team.Name = request.Name ?? team.Name;
        team.Description = request.Description;
        team.RequireApproval = request.RequireApproval;
        team.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(new { team.Id, team.Name, team.RequireApproval });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteTeam(string id)
    {
        var userId = GetCurrentUserId();
        var role = await _teams.RoleOfAsync(id, userId);
        if (!TeamRoles.CanManage(role)) return Forbid();

        var team = await _context.Teams.FirstAsync(t => t.Id == id);

        // Prompts survive the team and revert to their author. Deleting a team
        // must never delete work.
        var prompts = await _context.Prompts.Where(p => p.TeamId == id).ToListAsync();
        foreach (var p in prompts) p.TeamId = null;

        _context.Teams.Remove(team);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // ── Members ─────────────────────────────────────────────────────────────

    [HttpPost("{id}/members")]
    public async Task<ActionResult> AddMember(string id, [FromBody] AddTeamMemberRequest request)
    {
        var userId = GetCurrentUserId();
        var role = await _teams.RoleOfAsync(id, userId);
        if (!TeamRoles.CanManage(role)) return Forbid();

        if (!TeamRoles.All.Contains(request.Role))
            return BadRequest(new { error = $"Role must be one of: {string.Join(", ", TeamRoles.All)}." });

        var invitee = await _context.Users.FirstOrDefaultAsync(u => u.Email == request.Email);
        if (invitee == null)
            return BadRequest(new { error = "No account with that email address. They need to sign up first." });

        var existing = await _context.TeamMembers.FirstOrDefaultAsync(m => m.TeamId == id && m.UserId == invitee.Id);
        if (existing != null)
        {
            existing.Role = request.Role;
            existing.Status = "active";
            await _context.SaveChangesAsync();
            return Ok(new { existing.Id, existing.Role, updated = true });
        }

        var member = new TeamMember
        {
            TeamId = id,
            UserId = invitee.Id,
            Role = request.Role,
            InvitedByUserId = userId,
            Status = "active"
        };
        _context.TeamMembers.Add(member);
        await _context.SaveChangesAsync();

        try
        {
            var team = await _context.Teams.FirstAsync(t => t.Id == id);
            await _email.SendEmailAsync(invitee.Email,
                $"You've been added to {team.Name}",
                $"<p>You now have <strong>{request.Role}</strong> access to the team \"{System.Net.WebUtility.HtmlEncode(team.Name)}\" on GenPrompt.</p>");
        }
        catch (Exception ex)
        {
            // The membership is the point; a failed email must not undo it.
            _logger.LogWarning(ex, "Team invite email failed for {Email}", invitee.Email);
        }

        return Ok(new { member.Id, member.Role });
    }

    [HttpPut("{id}/members/{memberId}")]
    public async Task<ActionResult> UpdateMemberRole(string id, string memberId, [FromBody] AddTeamMemberRequest request)
    {
        var userId = GetCurrentUserId();
        var role = await _teams.RoleOfAsync(id, userId);
        if (!TeamRoles.CanManage(role)) return Forbid();

        var member = await _context.TeamMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.TeamId == id);
        if (member == null) return NotFound();

        var team = await _context.Teams.FirstAsync(t => t.Id == id);
        if (member.UserId == team.OwnerId)
            return BadRequest(new { error = "The team owner's role cannot be changed. Transfer ownership instead." });

        if (!TeamRoles.All.Contains(request.Role))
            return BadRequest(new { error = "Unknown role." });

        member.Role = request.Role;
        await _context.SaveChangesAsync();
        return Ok(new { member.Id, member.Role });
    }

    [HttpDelete("{id}/members/{memberId}")]
    public async Task<ActionResult> RemoveMember(string id, string memberId)
    {
        var userId = GetCurrentUserId();
        var role = await _teams.RoleOfAsync(id, userId);

        var member = await _context.TeamMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.TeamId == id);
        if (member == null) return NotFound();

        // Managers can remove anyone; anyone can remove themselves.
        if (!TeamRoles.CanManage(role) && member.UserId != userId) return Forbid();

        var team = await _context.Teams.FirstAsync(t => t.Id == id);
        if (member.UserId == team.OwnerId)
            return BadRequest(new { error = "The owner cannot be removed from their own team." });

        _context.TeamMembers.Remove(member);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // ── Team prompts ────────────────────────────────────────────────────────

    [HttpGet("{id}/prompts")]
    public async Task<ActionResult> GetTeamPrompts(string id)
    {
        var userId = GetCurrentUserId();
        var role = await _teams.RoleOfAsync(id, userId);
        if (role == null) return Forbid();

        var prompts = await _context.Prompts
            .Where(p => p.TeamId == id && p.IsActive)
            .OrderByDescending(p => p.UpdatedAt)
            .Select(p => new
            {
                p.Id, p.Name, p.Category, p.Description, p.CurrentVersion,
                p.PreferredModel, p.UpdatedAt, p.UserId,
                pendingApprovals = _context.PromptApprovals.Count(a => a.PromptId == p.Id && a.Status == "pending")
            })
            .ToListAsync();

        return Ok(new { yourRole = role, canEdit = TeamRoles.CanEdit(role), prompts });
    }

    [HttpPost("{id}/prompts/{promptId}")]
    public async Task<ActionResult> AddPromptToTeam(string id, string promptId)
    {
        var userId = GetCurrentUserId();
        var role = await _teams.RoleOfAsync(id, userId);
        if (!TeamRoles.CanEdit(role)) return Forbid();

        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == promptId && p.UserId == userId);
        if (prompt == null) return NotFound(new { error = "That prompt does not exist or is not yours to share." });

        prompt.TeamId = id;
        prompt.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new { prompt.Id, prompt.TeamId });
    }

    [HttpDelete("{id}/prompts/{promptId}")]
    public async Task<ActionResult> RemovePromptFromTeam(string id, string promptId)
    {
        var userId = GetCurrentUserId();
        var role = await _teams.RoleOfAsync(id, userId);
        if (!TeamRoles.CanApprove(role)) return Forbid();

        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == promptId && p.TeamId == id);
        if (prompt == null) return NotFound();

        prompt.TeamId = null;
        prompt.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // ── Approval queue ──────────────────────────────────────────────────────

    [HttpGet("{id}/approvals")]
    public async Task<ActionResult> GetApprovals(string id, [FromQuery] string status = "pending")
    {
        var userId = GetCurrentUserId();
        var role = await _teams.RoleOfAsync(id, userId);
        if (role == null) return Forbid();

        var approvals = await (
            from a in _context.PromptApprovals
            join p in _context.Prompts on a.PromptId equals p.Id
            join u in _context.Users on a.RequestedByUserId equals u.Id
            where a.TeamId == id && a.Status == status
            orderby a.CreatedAt descending
            select new
            {
                a.Id, a.PromptId, promptName = p.Name, a.Status,
                requestedBy = u.Name, requestedByEmail = u.Email,
                a.ChangeNote, a.CreatedAt, a.ReviewedAt, a.ReviewComment,
                a.BaseVersion
            }
        ).Take(100).ToListAsync();

        return Ok(new { yourRole = role, canApprove = TeamRoles.CanApprove(role), approvals });
    }

    [HttpGet("approvals/{approvalId}")]
    public async Task<ActionResult> GetApproval(string approvalId)
    {
        var userId = GetCurrentUserId();
        var approval = await _context.PromptApprovals.FirstOrDefaultAsync(a => a.Id == approvalId);
        if (approval == null) return NotFound();

        var role = await _teams.RoleOfAsync(approval.TeamId, userId);
        if (role == null) return Forbid();

        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == approval.PromptId);

        return Ok(new
        {
            approval.Id, approval.PromptId, approval.TeamId, approval.Status,
            approval.RequestedByUserId, approval.ChangeNote, approval.ReviewComment,
            approval.CreatedAt, approval.ReviewedAt, approval.BaseVersion,
            currentContent = prompt?.Content,
            proposedContent = approval.ProposedContent,
            proposedName = approval.ProposedName,
            // Diff against what the prompt looks like NOW, not what it looked
            // like when proposed — that is what the reviewer is deciding on.
            diff = TextDiff.Compute(prompt?.Content ?? approval.BaseContent, approval.ProposedContent),
            stats = TextDiff.Stats(prompt?.Content ?? approval.BaseContent, approval.ProposedContent),
            // Warn the reviewer when the prompt moved after this was proposed.
            baseIsStale = prompt != null && prompt.CurrentVersion != approval.BaseVersion,
            canApprove = TeamRoles.CanApprove(role)
        });
    }

    [HttpPost("approvals/{approvalId}/approve")]
    public async Task<ActionResult> Approve(string approvalId, [FromBody] ReviewApprovalRequest? request)
    {
        var userId = GetCurrentUserId();
        var approval = await _context.PromptApprovals.FirstOrDefaultAsync(a => a.Id == approvalId);
        if (approval == null) return NotFound();
        if (approval.Status != "pending") return BadRequest(new { error = $"This request was already {approval.Status}." });

        var role = await _teams.RoleOfAsync(approval.TeamId, userId);
        if (!TeamRoles.CanApprove(role)) return Forbid();

        // You cannot wave through your own change — that would make the whole
        // gate decorative.
        if (approval.RequestedByUserId == userId)
            return BadRequest(new { error = "You cannot approve your own change." });

        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == approval.PromptId);
        if (prompt == null) return NotFound(new { error = "The prompt no longer exists." });

        // Snapshot the version being replaced so the approval is reversible.
        //
        // PromptVersions is uniquely keyed on (PromptId, VersionNumber) and an
        // ordinary save already writes a row for the current version, so
        // inserting one unconditionally here fails the constraint and 500s the
        // approval. Only snapshot when that version is not already recorded.
        var alreadySnapshotted = await _context.PromptVersions
            .AnyAsync(v => v.PromptId == prompt.Id && v.VersionNumber == prompt.CurrentVersion);

        if (!alreadySnapshotted)
        {
            _context.PromptVersions.Add(new PromptVersion
            {
                PromptId = prompt.Id,
                UserId = prompt.UserId,
                VersionNumber = prompt.CurrentVersion,
                Name = prompt.Name,
                Content = prompt.Content,
                Category = prompt.Category,
                Description = prompt.Description,
                Status = prompt.ApprovalStatus,
                ChangeNote = "Snapshot before approved change"
            });
        }

        prompt.Name = approval.ProposedName;
        prompt.Content = approval.ProposedContent;
        prompt.Category = approval.ProposedCategory;
        prompt.Description = approval.ProposedDescription;
        prompt.CurrentVersion += 1;
        prompt.ApprovalStatus = "approved";
        prompt.ApprovedAt = DateTime.UtcNow;
        prompt.UpdatedAt = DateTime.UtcNow;

        // Record the approved content under its new version number.
        _context.PromptVersions.Add(new PromptVersion
        {
            PromptId = prompt.Id,
            UserId = approval.RequestedByUserId,
            VersionNumber = prompt.CurrentVersion,
            Name = prompt.Name,
            Content = prompt.Content,
            Category = prompt.Category,
            Description = prompt.Description,
            Status = "approved",
            ChangeNote = approval.ChangeNote ?? "Approved team change"
        });

        approval.Status = "approved";
        approval.ReviewedByUserId = userId;
        approval.ReviewComment = request?.Comment;
        approval.ReviewedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return Ok(new { approval.Id, approval.Status, prompt.CurrentVersion });
    }

    [HttpPost("approvals/{approvalId}/reject")]
    public async Task<ActionResult> Reject(string approvalId, [FromBody] ReviewApprovalRequest? request)
    {
        var userId = GetCurrentUserId();
        var approval = await _context.PromptApprovals.FirstOrDefaultAsync(a => a.Id == approvalId);
        if (approval == null) return NotFound();
        if (approval.Status != "pending") return BadRequest(new { error = $"This request was already {approval.Status}." });

        var role = await _teams.RoleOfAsync(approval.TeamId, userId);
        if (!TeamRoles.CanApprove(role)) return Forbid();

        approval.Status = "rejected";
        approval.ReviewedByUserId = userId;
        approval.ReviewComment = request?.Comment;
        approval.ReviewedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(new { approval.Id, approval.Status });
    }

    [HttpPost("approvals/{approvalId}/withdraw")]
    public async Task<ActionResult> Withdraw(string approvalId)
    {
        var userId = GetCurrentUserId();
        var approval = await _context.PromptApprovals.FirstOrDefaultAsync(a => a.Id == approvalId);
        if (approval == null) return NotFound();
        if (approval.RequestedByUserId != userId) return Forbid();
        if (approval.Status != "pending") return BadRequest(new { error = "Only a pending request can be withdrawn." });

        approval.Status = "withdrawn";
        approval.ReviewedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return Ok(new { approval.Id, approval.Status });
    }

    /// <summary>Everything waiting on this user across every team they can approve in.</summary>
    //
    // Declared with an explicit Order so it always beats approvals/{approvalId}.
    // Without it the literal "inbox" is captured as an approval id and this
    // endpoint is unreachable.
    [HttpGet("approvals/inbox", Order = -1)]
    public async Task<ActionResult> ApprovalInbox()
    {
        var userId = GetCurrentUserId();
        var teamIds = await _teams.AccessibleTeamIdsAsync(userId);

        var approvable = new List<string>();
        foreach (var tid in teamIds)
        {
            if (TeamRoles.CanApprove(await _teams.RoleOfAsync(tid, userId)))
                approvable.Add(tid);
        }

        if (approvable.Count == 0) return Ok(new { count = 0, items = Array.Empty<object>() });

        var items = await (
            from a in _context.PromptApprovals
            join p in _context.Prompts on a.PromptId equals p.Id
            join t in _context.Teams on a.TeamId equals t.Id
            where approvable.Contains(a.TeamId) && a.Status == "pending" && a.RequestedByUserId != userId
            orderby a.CreatedAt
            select new { a.Id, a.PromptId, promptName = p.Name, teamName = t.Name, a.ChangeNote, a.CreatedAt }
        ).Take(50).ToListAsync();

        return Ok(new { count = items.Count, items });
    }
}

public class SaveTeamRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? OrganizationId { get; set; }
    public bool RequireApproval { get; set; } = true;
}

public class AddTeamMemberRequest
{
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = TeamRoles.Viewer;
}

public class ReviewApprovalRequest
{
    public string? Comment { get; set; }
}
