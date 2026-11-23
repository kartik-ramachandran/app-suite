namespace Research.Api.Models;

public class PromptChain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public bool IsPublic { get; set; } = false;
    public int UsageCount { get; set; } = 0;
    public int InstallCount { get; set; } = 0;
    public bool IsFeatured { get; set; } = false;
    public string? Publisher { get; set; }
    public DateTime? FeaturedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
    public ICollection<ChainNode> Nodes { get; set; } = new List<ChainNode>();
    public ICollection<ChainEdge> Edges { get; set; } = new List<ChainEdge>();
}
