namespace Research.Api.Models;

public class LifeCheckIn
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.UtcNow.Date;
    public int Mood { get; set; } = 5;
    public int Energy { get; set; } = 5;
    public int Focus { get; set; } = 5;
    public double SleepHours { get; set; } = 7;
    public int ExerciseMinutes { get; set; }
    public int ScreenMinutes { get; set; }
    public decimal MoneySpent { get; set; }
    public string TopTasks { get; set; } = string.Empty;
    public string Gratitude { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public User? User { get; set; }
}

public class LifeGoal
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Area { get; set; } = "Growth";
    public DateTime? TargetDate { get; set; }
    public int Progress { get; set; }
    public string Status { get; set; } = "active";
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public User? User { get; set; }
}

public class LifeDecision
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.UtcNow;
    public string Title { get; set; } = string.Empty;
    public string Reasoning { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public DateTime? ReviewDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public User? User { get; set; }
}

public class LifeRelationship
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Circle { get; set; } = "Friends";
    public DateTime? LastContactedAt { get; set; }
    public int Importance { get; set; } = 5;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public User? User { get; set; }
}

public class LifeEvidence
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string StoredFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string EvidenceType { get; set; } = "document";
    public string ExtractedText { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public User? User { get; set; }
}

public class LifeCloudLink
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Provider { get; set; } = "Google Drive";
    public string DisplayName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string ExtractedText { get; set; } = string.Empty;
    public string SyncStatus { get; set; } = "pending";
    public DateTime? LastSyncedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public User? User { get; set; }
}
