namespace AgentHub.Api.Models;

public class Skill
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public required string Instruction { get; set; }
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? Tags { get; set; } // comma-separated
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsActive { get; set; } = true;
    public bool IsPublic { get; set; } = true;
    public int UsageCount { get; set; } = 0;
    public int LikeCount { get; set; } = 0;

    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
}
