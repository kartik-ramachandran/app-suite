namespace ContentStudio.Api.Models;

public class ScheduledJob
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AgentType { get; set; } = "prompt"; // prompt | chain | content
    public Guid? ChainId { get; set; }
    public string? PromptId { get; set; }
    public string Input { get; set; } = string.Empty;
    public string Schedule { get; set; } = "daily"; // daily | weekly | monthly | once
    public int Hour { get; set; } = 9; // 0-23
    public int DayOfWeek { get; set; } = 1; // 1=Monday … 7=Sunday
    public int DayOfMonth { get; set; } = 1; // 1-28
    public DateTime? OneOffAt { get; set; }
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunStatus { get; set; }
    public string? DeliverToEmail { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public User? User { get; set; }
    public ICollection<JobRun> Runs { get; set; } = new List<JobRun>();
}

public class JobRun
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Status { get; set; } = "pending"; // pending | running | success | failed
    public string? ResultText { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ScheduledJob? Job { get; set; }
}
