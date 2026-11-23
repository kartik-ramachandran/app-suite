namespace PromptStudio.Api.Models;

public class PromptCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Emoji { get; set; } = "📁";
    public bool IsPublic { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }

    public ICollection<CollectionItem> Items { get; set; } = new List<CollectionItem>();
}

public class CollectionItem
{
    public int Id { get; set; }
    public string CollectionId { get; set; } = string.Empty;
    public PromptCollection? Collection { get; set; }

    public string PromptId { get; set; } = string.Empty;
    public Prompt? Prompt { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
