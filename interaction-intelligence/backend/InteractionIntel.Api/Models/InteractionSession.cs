namespace InteractionIntel.Api.Models;

/// <summary>
/// A user's working state for the Interaction Intelligence page: the transcript of
/// the last processed recording plus the analyses produced for each tool. Persisted
/// so the page can be restored after navigating away or switching devices.
/// </summary>
public class InteractionSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string SourceLabel { get; set; } = string.Empty; // file name or link
    public string Transcript { get; set; } = string.Empty;
    public string AnalysesJson { get; set; } = "{}";        // { toolKey: analysisObject }
    // What the user wants out of this recording (their goal/intent). Applied as the
    // priority lens across every tool so the analysis is tailored to it.
    public string? Intent { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
