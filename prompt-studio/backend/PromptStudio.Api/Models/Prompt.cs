namespace PromptStudio.Api.Models;

public class Prompt
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public required string Content { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;

    // Template support - prompts can have {{placeholders}}
    public bool IsTemplate { get; set; } = false;

    // Public sharing support
    public bool IsPublic { get; set; } = false;
    public int UsageCount { get; set; } = 0;
    public int LikeCount { get; set; } = 0;
    public string ApprovalStatus { get; set; } = "draft";
    public bool RequiresApproval { get; set; }
    public int CurrentVersion { get; set; } = 1;
    public DateTime? ApprovedAt { get; set; }

    // Difficulty level for library discoverability
    public string? Difficulty { get; set; } // "beginner", "intermediate", "advanced"

    // ── Model selection ─────────────────────────────────────────────────────

    /// <summary>
    /// The model this prompt should run on, chosen after a comparison. Null
    /// means "whatever the provider order gives us", which is the old behaviour.
    /// </summary>
    public string? PreferredModel { get; set; }

    /// <summary>
    /// Ordered JSON array of model ids to try when the preferred one errors or
    /// times out. Empty means no fallback — fail loudly rather than silently
    /// answering on a model the user did not choose.
    /// </summary>
    public string FallbackModelsJson { get; set; } = "[]";

    /// <summary>
    /// Pick the cheapest model that scored at or above <see cref="RoutingMinScore"/>
    /// in the last comparison, instead of always using PreferredModel.
    /// </summary>
    public bool CostOptimalRouting { get; set; } = false;

    /// <summary>Quality floor (1-10) a model must clear to be eligible for cost-optimal routing.</summary>
    public int RoutingMinScore { get; set; } = 8;

    // ── Structured output ───────────────────────────────────────────────────

    /// <summary>
    /// JSON Schema the response must validate against. When set, the provider is
    /// asked for JSON mode and the result is checked before it is returned.
    /// </summary>
    public string? OutputSchema { get; set; }

    /// <summary>Reject rather than return a response that does not match OutputSchema.</summary>
    public bool EnforceSchema { get; set; } = false;

    // ── Team ownership ──────────────────────────────────────────────────────

    /// <summary>The team this prompt belongs to. Null means it is personal.</summary>
    public string? TeamId { get; set; }

    // User relationship
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
    public Team? Team { get; set; }
}
