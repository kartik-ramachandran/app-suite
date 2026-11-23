namespace AgentHub.Api.Models;

public class PdfApiUsage
{
    public int Id { get; set; }
    public int ApiKeyId { get; set; }
    public ApiKey ApiKey { get; set; } = null!;
    public string Endpoint { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
