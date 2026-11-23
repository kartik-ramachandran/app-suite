namespace AgentHub.Api.Models;

/// <summary>
/// One goal, planned into a graph of tasks and executed over time. Runs are
/// durable: the executor is a background service that picks up whatever is
/// runnable, so a restart mid-run resumes rather than loses the work.
/// </summary>
public class OrchestrationRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }
    public string Goal { get; set; } = string.Empty;
    /// planning | running | waiting_approval | completed | failed | cancelled
    public string Status { get; set; } = "planning";
    /// Plain-language summary of the approach, written by the planner.
    public string PlanSummary { get; set; } = string.Empty;
    /// Final assembled artifact (markdown).
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    /// Hard ceiling on task executions — a runaway plan must not bill forever.
    public int MaxSteps { get; set; } = 40;
    public int StepsUsed { get; set; }
    /// Knowledge node this run was launched from, and the one it wrote back to.
    public Guid? SourceNodeId { get; set; }
    public Guid? ResultNodeId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

/// <summary>
/// A single planned step. DependsOn is a JSON array of sibling task Keys, which
/// is what makes the plan a DAG rather than a list: everything whose
/// dependencies are satisfied runs in parallel.
/// </summary>
public class OrchestrationTask
{
    public int Id { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    /// Stable key within the run, e.g. "research_market". Referenced by DependsOn.
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// Tool id from the registry, e.g. "ai.generate" or "mcp:<connectionId>:<tool>".
    public string Tool { get; set; } = string.Empty;
    /// JSON arguments for the tool. {{task_key}} placeholders are substituted
    /// with upstream outputs before execution.
    public string ArgumentsJson { get; set; } = "{}";
    public string DependsOnJson { get; set; } = "[]";
    /// pending | ready | running | waiting_approval | done | failed | skipped
    public string Status { get; set; } = "pending";
    public string? Output { get; set; }
    public string? ErrorMessage { get; set; }
    public int Attempts { get; set; }
    /// Set when a write-capable tool needs a human before it may run.
    public bool NeedsApproval { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public int OrderIndex { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int DurationMs { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
