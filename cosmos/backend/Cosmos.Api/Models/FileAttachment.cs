namespace Cosmos.Api.Models;

public class FileAttachment
{
    public int Id { get; set; }
    public required string FileName { get; set; }
    public required string StoredFileName { get; set; } // Unique name on disk
    public required string ContentType { get; set; }
    public long FileSize { get; set; }

    // Source: "upload" or "google-drive"
    public string Source { get; set; } = "upload";
    public string? GoogleDriveFileId { get; set; }

    // Polymorphic attachment: can belong to a persona, prompt, or knowledge node.
    public string EntityType { get; set; } = string.Empty; // "persona", "prompt", or "knowledge"
    public string EntityId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
