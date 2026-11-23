namespace PromptStudio.Api.Models;

public class PromptAuditEvent
{
    public int Id { get; set; }
    public string PromptId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Prompt? Prompt { get; set; }
    public User? User { get; set; }
}