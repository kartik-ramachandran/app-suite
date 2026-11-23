namespace AgentHub.Api.Models;

public class AgentRun
{
    public int Id { get; set; }
    public int AgentId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;
    public string? FinalOutput { get; set; }
    public string StepsJson { get; set; } = "[]";   // JSON array of AgentRunStep
    public string Status { get; set; } = "pending"; // pending | running | completed | failed
    public int DurationMs { get; set; }
    public int TokensUsed { get; set; }
    public int Iterations { get; set; }
    public string? ModelUsed { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public string SafetyFlagsJson { get; set; } = "[]";
    public bool ApprovalRequired { get; set; }
    public string? BenchmarkGroupId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Agent? Agent { get; set; }
}
