using System.ComponentModel.DataAnnotations;

namespace MeetingManager.Api.Models;

public static class TeamRoles
{
    public const string Owner = "owner";
    public const string Approver = "approver";
    public const string Editor = "editor";
    public const string Viewer = "viewer";

    public static readonly string[] All = [Owner, Approver, Editor, Viewer];

    /// <summary>Can change team settings and membership.</summary>
    public static bool CanManage(string? role) =>
        string.Equals(role, Owner, StringComparison.OrdinalIgnoreCase);

    /// <summary>Can approve or reject a pending change.</summary>
    public static bool CanApprove(string? role) =>
        string.Equals(role, Owner, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, Approver, StringComparison.OrdinalIgnoreCase);

    /// <summary>Can propose a change. Editors propose; approvers and owners commit.</summary>
    public static bool CanEdit(string? role) =>
        CanApprove(role) || string.Equals(role, Editor, StringComparison.OrdinalIgnoreCase);

    public static bool CanView(string? role) => !string.IsNullOrEmpty(role);

    /// <summary>An editor's save becomes a request; everyone above writes directly.</summary>
    public static bool RequiresApproval(string? role) =>
        string.Equals(role, Editor, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A shared prompt library with roles.
///
/// Distinct from <see cref="Organization"/>, which is a billing and identity
/// boundary (one company, one domain, seat limits). A team is a working group
/// inside it — a company can have "Support Prompts" and "Marketing Prompts"
/// with different members and different approval rules.
/// </summary>
public class Team
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The org this team belongs to, when it was created inside one.</summary>
    public string? OrganizationId { get; set; }

    public string OwnerId { get; set; } = string.Empty;

    /// <summary>Editors' changes queue for approval instead of saving directly.</summary>
    public bool RequireApproval { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public User? Owner { get; set; }
    public ICollection<TeamMember> Members { get; set; } = new List<TeamMember>();
}

public class TeamMember
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TeamId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    [MaxLength(20)]
    public string Role { get; set; } = TeamRoles.Viewer;

    [MaxLength(20)]
    public string Status { get; set; } = "active"; // active | suspended

    public string? InvitedByUserId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    public Team? Team { get; set; }
    public User? User { get; set; }
}

/// <summary>
/// A proposed change to a team prompt, waiting on someone with the authority to
/// commit it. Holds the full proposed content rather than a diff so an approval
/// is still meaningful if the prompt moved on underneath it.
/// </summary>
public class PromptApproval
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string PromptId { get; set; } = string.Empty;
    public string TeamId { get; set; } = string.Empty;

    public string RequestedByUserId { get; set; } = string.Empty;
    public string? ReviewedByUserId { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = "pending"; // pending | approved | rejected | withdrawn

    /// <summary>What the prompt looked like when the change was proposed.</summary>
    public string? BaseContent { get; set; }
    public int BaseVersion { get; set; }

    /// <summary>What the requester wants it to become.</summary>
    public string ProposedName { get; set; } = string.Empty;
    public string ProposedContent { get; set; } = string.Empty;
    public string? ProposedCategory { get; set; }
    public string? ProposedDescription { get; set; }

    public string? ChangeNote { get; set; }
    public string? ReviewComment { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }

    public Prompt? Prompt { get; set; }
    public Team? Team { get; set; }
}
