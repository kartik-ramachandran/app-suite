using System.ComponentModel.DataAnnotations;

namespace PromptStudio.Api.Models;

/// <summary>
/// A set of assertions about what a prompt should do, runnable on demand, from
/// CI, or on a schedule.
///
/// The value is not the suite — it is the run history. A prompt that passed
/// yesterday and fails today, with nothing on our side changed, means the model
/// provider moved underneath us. That is the thing nobody else tells you.
/// </summary>
public class TestSuite
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The prompt under test. Null means the suite carries its own inline prompt.</summary>
    public string? PromptId { get; set; }

    /// <summary>Snapshot prompt content, used when PromptId is null or the prompt was deleted.</summary>
    public string? PromptContent { get; set; }

    /// <summary>Which model to test against. Null uses the prompt's preferred model or the default.</summary>
    [MaxLength(100)]
    public string? Model { get; set; }

    /// <summary>Re-run automatically. The regression alert is the point of this flag.</summary>
    public bool ScheduleEnabled { get; set; } = false;

    /// <summary>daily | weekly — checked by the scheduler sweep.</summary>
    [MaxLength(20)]
    public string ScheduleCadence { get; set; } = "daily";

    public int ScheduleHour { get; set; } = 3;
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }

    /// <summary>Pass rate of the most recent run, 0-100. Used to detect a regression against the next one.</summary>
    public double? LastPassRate { get; set; }

    /// <summary>Email/notify when pass rate drops versus the previous run.</summary>
    public bool AlertOnRegression { get; set; } = true;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Prompt? Prompt { get; set; }
    public User? User { get; set; }
    public ICollection<TestCase> Cases { get; set; } = new List<TestCase>();
    public ICollection<TestSuiteRun> Runs { get; set; } = new List<TestSuiteRun>();
}

/// <summary>One assertion: given this input, the output must look like this.</summary>
public class TestCase
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TestSuiteId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string Input { get; set; } = string.Empty;

    /// <summary>JSON array of substrings that must all appear (case-insensitive).</summary>
    public string ExpectedContainsJson { get; set; } = "[]";

    /// <summary>JSON array of substrings that must not appear.</summary>
    public string ExpectedNotContainsJson { get; set; } = "[]";

    /// <summary>Optional regex the output must match.</summary>
    public string? ExpectedRegex { get; set; }

    /// <summary>When set, an LLM grades the output 1-10 and it must reach this score.</summary>
    public int? MinScore { get; set; }

    /// <summary>What "good" means for the grader, when MinScore is used.</summary>
    public string? GradingCriteria { get; set; }

    /// <summary>Output must parse as JSON, and validate against this schema when set.</summary>
    public string? JsonSchema { get; set; }

    /// <summary>Fail the case if it takes longer than this. 0 = no limit.</summary>
    public int MaxLatencyMs { get; set; } = 0;

    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TestSuite? TestSuite { get; set; }
}

/// <summary>One execution of a whole suite.</summary>
public class TestSuiteRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TestSuiteId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    /// <summary>manual | scheduled | ci — who asked for this run.</summary>
    [MaxLength(20)]
    public string Trigger { get; set; } = "manual";

    [MaxLength(20)]
    public string Status { get; set; } = "running"; // running | passed | failed | error

    [MaxLength(100)]
    public string? Model { get; set; }

    public int TotalCases { get; set; }
    public int PassedCases { get; set; }
    public int FailedCases { get; set; }
    public double PassRate { get; set; }

    public int TotalTokens { get; set; }
    public decimal TotalCostUsd { get; set; }
    public long TotalLatencyMs { get; set; }

    /// <summary>Pass rate of the run before this one, so a regression is visible in the row itself.</summary>
    public double? PreviousPassRate { get; set; }
    public bool IsRegression { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public TestSuite? TestSuite { get; set; }
    public ICollection<TestCaseResult> Results { get; set; } = new List<TestCaseResult>();
}

/// <summary>The outcome of one case within one run.</summary>
public class TestCaseResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string TestSuiteRunId { get; set; } = string.Empty;
    public string TestCaseId { get; set; } = string.Empty;

    [MaxLength(200)]
    public string CaseName { get; set; } = string.Empty;

    public bool Passed { get; set; }

    /// <summary>Which assertions failed, as a JSON array of human-readable strings.</summary>
    public string FailureReasonsJson { get; set; } = "[]";

    public string? Output { get; set; }
    public int? Score { get; set; }
    public int TokensUsed { get; set; }
    public long LatencyMs { get; set; }
    public decimal CostUsd { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TestSuiteRun? Run { get; set; }
}
