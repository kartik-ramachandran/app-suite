namespace EmmdStudio.Api.Models;

// ── Business / organisation accounts (Phase 1) ──────────────────────────────
// A single User table is kept; a user joins an Organization via an
// OrganizationMember row with a role. Business signup creates an Organization
// and an Owner membership for the creator. Usage of the metered suites
// (Snappdf / Signal / Prism) via the web UI is logged to AppUsage so it can be
// attributed to an org and so free individual accounts can be soft-limited.

public static class OrgRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";

    public static bool IsManager(string? role) =>
        string.Equals(role, Owner, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, Admin, StringComparison.OrdinalIgnoreCase);
}

public static class OrgPlans
{
    public const string Free = "free";
    public const string Team = "team";
    public const string Business = "business";
    public const string Enterprise = "enterprise";

    // Paid plans lift the free-tier soft limits for all members.
    public static bool IsPaid(string? plan) =>
        !string.IsNullOrEmpty(plan) && !string.Equals(plan, Free, StringComparison.OrdinalIgnoreCase);
}

public class Organization
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? Domain { get; set; }          // email domain, e.g. "acme.com"
    public string? Industry { get; set; }
    public string? Size { get; set; }            // e.g. "1-10", "11-50"
    public string? Website { get; set; }
    public string Plan { get; set; } = OrgPlans.Free;
    public int MaxMembers { get; set; } = 5;     // seat limit (free default)
    public string CreatedByUserId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class OrganizationMember
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string OrganizationId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string Role { get; set; } = OrgRoles.Member;
    public string Status { get; set; } = "active";     // active | suspended
    public string? InvitedByUserId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class OrganizationInvitation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string OrganizationId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = OrgRoles.Member;
    public string Status { get; set; } = "pending";    // pending | accepted | cancelled | expired
    public string InvitationToken { get; set; } = Guid.NewGuid().ToString("N");
    public string? InvitedByUserId { get; set; }
    public string? PersonalMessage { get; set; }
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(7);
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One row per successful in-app call to a metered suite (Snappdf / Signal /
/// Prism) made through the web UI (JWT, not an org API key). Powers per-org
/// usage attribution, the admin free-riding report, and free-tier soft limits.
/// </summary>
public class AppUsage
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string Module { get; set; } = string.Empty;   // pdf | interaction | prism
    public string Endpoint { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ── Request DTOs ────────────────────────────────────────────────────────────

public class CreateOrganizationRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Domain { get; set; }
    public string? Industry { get; set; }
    public string? Size { get; set; }
    public string? Website { get; set; }
}

public class UpdateOrganizationRequest
{
    public string? Name { get; set; }
    public string? Domain { get; set; }
    public string? Industry { get; set; }
    public string? Size { get; set; }
    public string? Website { get; set; }
}

public class InviteMemberRequest
{
    public string Email { get; set; } = string.Empty;
    public string? Role { get; set; }
    public string? PersonalMessage { get; set; }
}

public class UpdateMemberRoleRequest
{
    public string Role { get; set; } = OrgRoles.Member;
}

public class AcceptInvitationRequest
{
    public string Token { get; set; } = string.Empty;
    // Only used when the invited email has no account yet.
    public string? Name { get; set; }
    public string? Password { get; set; }
}

public class BusinessRegisterRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OrganizationName { get; set; } = string.Empty;
    public string? Industry { get; set; }
    public string? Size { get; set; }
    public string? Website { get; set; }
}
