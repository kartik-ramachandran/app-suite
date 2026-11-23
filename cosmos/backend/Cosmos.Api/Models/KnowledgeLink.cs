namespace Cosmos.Api.Models;

/// <summary>
/// A typed edge between two knowledge nodes.
/// wiki = explicit [[link]] in content, tag = #hashtag, concept = AI-extracted,
/// manual = drawn by the user in the cosmos view.
/// </summary>
public class KnowledgeLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public KnowledgeNode? SourceNode { get; set; }
    public Guid TargetId { get; set; }
    public KnowledgeNode? TargetNode { get; set; }
    /// <summary>wiki | tag | concept | manual</summary>
    public string Kind { get; set; } = "manual";
    public string? Label { get; set; }
    public double Weight { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string UserId { get; set; } = string.Empty;
}
