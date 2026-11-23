namespace Prism.Api.Models;

// ── Prism (autonomous data engineering) result shapes ───────────────
// Mirrors the Interaction Intelligence render contract (headline / metrics /
// sections) so one generic frontend renderer presents every tool, plus a
// table preview and an optional downloadable output file.

public class PrismMetric
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Tone { get; set; } = "neutral";   // good | warn | bad | neutral
}

public class PrismSection
{
    public string Heading { get; set; } = string.Empty;
    public List<string> Items { get; set; } = new();
}

public class PrismPreview
{
    public List<string> Headers { get; set; } = new();
    public List<List<string>> Rows { get; set; } = new();
    public int TotalRows { get; set; }
}

public class PrismDownload
{
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "text/csv";
    public string Base64 { get; set; } = string.Empty;
}

public class PrismResult
{
    public string Headline { get; set; } = string.Empty;
    public List<PrismMetric> Metrics { get; set; } = new();
    public List<PrismSection> Sections { get; set; } = new();
    public PrismPreview? Preview { get; set; }
    public PrismDownload? Download { get; set; }
}
