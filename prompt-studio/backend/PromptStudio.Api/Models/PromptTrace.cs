using System.ComponentModel.DataAnnotations;

namespace PromptStudio.Api.Models;

/// <summary>
/// One prompt execution, recorded for operations rather than for the user's
/// history.
///
/// <see cref="PromptExecution"/> stores conversations a person had in the UI.
/// This stores every call that went through the platform — API keys, chains,
/// agents, scheduled jobs, test suites — with the numbers you need to answer
/// "why did last month cost that much" and "which prompt got slow". Kept
/// separate so the user-facing history stays small and readable while traces
/// can be aggressively pruned.
/// </summary>
public class PromptTrace
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }

    public string? PromptId { get; set; }

    [MaxLength(200)]
    public string PromptName { get; set; } = string.Empty;

    /// <summary>api | ui | chain | agent | job | suite | compare — where the call came from.</summary>
    [MaxLength(20)]
    public string Source { get; set; } = "api";

    /// <summary>The API key used, when the call came in over the external API.</summary>
    public int? ApiKeyId { get; set; }

    [MaxLength(100)]
    public string Model { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? Provider { get; set; }

    /// <summary>ok | error | timeout | filtered — filtered means content moderation blocked it.</summary>
    [MaxLength(20)]
    public string Status { get; set; } = "ok";

    public string? ErrorMessage { get; set; }

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public decimal CostUsd { get; set; }
    public long LatencyMs { get; set; }

    /// <summary>Set when a fallback chain had to move past its first choice.</summary>
    [MaxLength(100)]
    public string? FellBackFrom { get; set; }

    public int RetryCount { get; set; }

    /// <summary>Truncated input/output — enough to debug, not enough to be a second copy of the data.</summary>
    [MaxLength(2000)]
    public string? InputPreview { get; set; }

    [MaxLength(2000)]
    public string? OutputPreview { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Prompt? Prompt { get; set; }
    public User? User { get; set; }
}

/// <summary>
/// A cost or error-rate threshold that, once crossed, is worth waking someone
/// for. Evaluated by the scheduler sweep against the trace table.
/// </summary>
public class TraceAlertRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>cost | error_rate | latency_p95 | volume</summary>
    [MaxLength(20)]
    public string Metric { get; set; } = "cost";

    /// <summary>Scope to one prompt, or null for everything the user runs.</summary>
    public string? PromptId { get; set; }

    /// <summary>Window in hours the metric is computed over.</summary>
    public int WindowHours { get; set; } = 24;

    /// <summary>Fire when the metric exceeds this. USD for cost, 0-100 for error_rate, ms for latency.</summary>
    public double Threshold { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Don't re-fire within this many hours of the last alert.</summary>
    public int CooldownHours { get; set; } = 6;

    public DateTime? LastFiredAt { get; set; }
    public double? LastValue { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? User { get; set; }
}
