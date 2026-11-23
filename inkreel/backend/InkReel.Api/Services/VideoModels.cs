namespace InkReel.Api.Services;

/// <summary>One narrated slide in the generated video.</summary>
public class VideoScene
{
    public string Title { get; set; } = "";
    public List<string> Bullets { get; set; } = new();
    /// <summary>What the voice says over this slide.</summary>
    public string Narration { get; set; } = "";
    /// <summary>Index into the document's extracted images (-1 = none).</summary>
    public int ImageIndex { get; set; } = -1;
}

/// <summary>Everything the pipeline needs to build one video.</summary>
public class VideoRequest
{
    public string Title { get; set; } = "";
    /// <summary>Source text (from the uploaded document's selected pages, or a prompt).</summary>
    public string SourceText { get; set; } = "";
    /// <summary>Extracted document images (page order) the scenes may reference.</summary>
    public List<byte[]> Images { get; set; } = new();
    /// <summary>Target length in seconds (30, 60, 90, 120, 180, 240).</summary>
    public int TargetSeconds { get; set; } = 120;
    /// <summary>Slide style id from VideoStyleCatalog.</summary>
    public string StyleId { get; set; } = "white-on-navy";
    /// <summary>Brand palette id from VideoStyleCatalog, or 'custom'.</summary>
    public string PaletteId { get; set; } = "trust-blue";
    /// <summary>Custom palette hexes when PaletteId == 'custom' (primary, accent).</summary>
    public string? CustomPrimary { get; set; }
    public string? CustomAccent { get; set; }
    /// <summary>Voice id from SpeechService.Voices.</summary>
    public string VoiceId { get; set; } = "friendly-male";
    /// <summary>Narration style: explainer | storyteller | energetic | calm.</summary>
    public string NarrationStyle { get; set; } = "explainer";
    public string Audience { get; set; } = "General audience";
    /// <summary>Optional logo (PNG/WebP bytes) drawn top-left on every slide.</summary>
    public byte[]? Logo { get; set; }
}

public class VideoJobStatus
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>queued | scripting | narrating | rendering | assembling | done | failed</summary>
    public string Status { get; set; } = "queued";
    public string? Error { get; set; }
    public int SceneCount { get; set; }
    public int ScenesDone { get; set; }
    public double DurationSeconds { get; set; }
    public string? CreatedAt { get; set; }
    public bool HasAudio { get; set; }
}
