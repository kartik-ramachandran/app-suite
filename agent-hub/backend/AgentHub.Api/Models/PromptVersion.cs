namespace AgentHub.Api.Models;

public class PromptVersion
{
    public int Id { get; set; }
    public string PromptId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string Status { get; set; } = "draft";
    public string? ChangeNote { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Prompt? Prompt { get; set; }
    public User? User { get; set; }
}