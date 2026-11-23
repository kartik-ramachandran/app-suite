namespace MoneyManager.Api.Models;

/// <summary>
/// A unit of work executed by any module/feature (e.g. Interaction Intelligence,
/// PDF Tools). Records the execution lifecycle so it can be logged and so the
/// originating module/user can be notified when it finishes. Reusable across the
/// app — the <see cref="Module"/> field identifies which feature owns the job.
/// </summary>
public class ModuleJob
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Module { get; set; } = string.Empty;   // e.g. "interaction-intelligence"
    public string JobType { get; set; } = string.Empty;  // e.g. "transcribe", "analyse:quality"
    public string Title { get; set; } = string.Empty;    // human-readable label
    public string Status { get; set; } = "running";      // running | completed | failed
    public string? ResultText { get; set; }              // short result/summary (truncated)
    public string? ErrorMessage { get; set; }
    public bool IsRead { get; set; }                     // notification read state
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public long DurationMs { get; set; }
}
