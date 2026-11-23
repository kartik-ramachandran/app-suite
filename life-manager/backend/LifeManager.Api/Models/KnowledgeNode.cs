namespace LifeManager.Api.Models;

/// <summary>
/// A single piece of information in the user's knowledge cosmos.
/// Notes are dumped/fetched/created content; concepts and tags are
/// hub nodes auto-extracted from note content (Obsidian-style).
/// </summary>
public class KnowledgeNode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public string? Content { get; set; }
    /// <summary>note | concept | tag</summary>
    public string Kind { get; set; } = "note";
    /// <summary>dumped | fetched | created</summary>
    public string Source { get; set; } = "dumped";
    /// <summary>Module that produced it (research, cosmos, prompt-builder…)</summary>
    public string? SourceModule { get; set; }
    /// <summary>Optional origin reference (URL, entity id…)</summary>
    public string? SourceRef { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
}
