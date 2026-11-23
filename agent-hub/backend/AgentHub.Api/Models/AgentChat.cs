namespace AgentHub.Api.Models;

public class AgentChatSession
{
    public string SessionId { get; set; } = string.Empty;
    public string Title { get; set; } = "New chat";
    public string? UserId { get; set; }
    public string? BotType { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<AgentChatMessage> Messages { get; set; } = new List<AgentChatMessage>();
}

public class AgentChatMessage
{
    public int Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string Role { get; set; } = "user"; // user | assistant
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public AgentChatSession Session { get; set; } = null!;
}
