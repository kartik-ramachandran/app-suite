namespace AgentHub.Api.Models;

public class ApiKey
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// The person this key belongs to. Null for org keys: a licence is sold to
    /// an organisation, not a person, so there is no user row to point at. This
    /// was a required FK, which meant every attempt to mint an org licence
    /// violated it and failed 500 -- the reason no organisation ever existed.
    /// </summary>
    public string? UserId { get; set; }
    public User? User { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public bool IsActive { get; set; } = true;
    public int RequestCount { get; set; } = 0;

    // ── Org licence fields (Type = "org") ────────────────────────────────────
    public string Type { get; set; } = "user";           // "user" | "org"
    public string? KeyHash { get; set; }                 // SHA-256 for fast org-key lookup
    public string? OrgEmail { get; set; }
    public string? BillingModel { get; set; }            // "monthly" | "annual" | "credits"
    public string? Tier { get; set; }                    // "starter" | "business" | "enterprise"
    public int RateLimitPerMin { get; set; } = 60;
    public int MonthlyLimit { get; set; } = 0;           // 0 = unlimited
    public int TotalCredits { get; set; } = 0;           // 0 = not credit-based
    public int CreditsUsed { get; set; } = 0;
    public DateTime? ExpiresAt { get; set; }
    public string? Notes { get; set; }

    public ICollection<PdfApiUsage> PdfUsage { get; set; } = [];
}
