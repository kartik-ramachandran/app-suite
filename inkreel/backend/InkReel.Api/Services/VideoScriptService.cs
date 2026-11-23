using System.Text.Json;

namespace InkReel.Api.Services;

/// <summary>
/// Turns source text into a timed scene script. The target length drives the
/// scene count and narration word budget (~150 spoken words/minute), matching
/// the "pick the time, the slides follow it" behaviour in the enhancement spec.
/// </summary>
public class VideoScriptService
{
    private readonly AzureVisionService _ai;
    private readonly ILogger<VideoScriptService> _logger;

    public VideoScriptService(AzureVisionService ai, ILogger<VideoScriptService> logger)
    { _ai = ai; _logger = logger; }

    private const double WordsPerSecond = 2.5; // ~150 wpm narration pace

    public async Task<List<VideoScene>> WriteScriptAsync(VideoRequest req, CancellationToken ct = default)
    {
        if (!_ai.IsAvailable) throw new InvalidOperationException("AI is not configured.");

        // Scene budget: ~12-20s per scene, clamped 3..14 scenes.
        var sceneCount = Math.Clamp((int)Math.Round(req.TargetSeconds / 16.0), 3, 14);
        var totalWords = (int)(req.TargetSeconds * WordsPerSecond);
        var wordsPerScene = Math.Max(18, totalWords / sceneCount);

        var narrationVoice = req.NarrationStyle.ToLowerInvariant() switch
        {
            "storyteller" => "a warm storyteller — narrative hooks, concrete scenes, momentum",
            "energetic" => "an energetic presenter — short punchy sentences, enthusiasm without hype",
            "calm" => "a calm, measured explainer — unhurried, reassuring, precise",
            _ => "a friendly, conversational explainer (YouTube explainer style)",
        };

        var imagesNote = req.Images.Count == 0
            ? "There are no document images available; every scene must set imageIndex to -1."
            : $"The document has {req.Images.Count} extracted figures/images, indexed 0 to {req.Images.Count - 1} in document order. " +
              "Where a scene discusses something a figure likely shows, set its imageIndex to that figure; otherwise -1. Never invent an index.";

        var system =
            "You are a video scriptwriter turning source material into a narrated slide video. " +
            $"Audience: {req.Audience}. Narrator persona: {narrationVoice}. " +
            $"Produce EXACTLY {sceneCount} scenes. Each scene: a short slide title (max 8 words), " +
            "2-4 ultra-short bullets (max 10 words each, specific facts — never filler), and narration of " +
            $"ABOUT {wordsPerScene} words that a voice actor reads aloud over the slide. " +
            "The narration must flow scene to scene as one continuous piece: open with a hook, end with a takeaway. " +
            "Narration is spoken text only — no headings, no 'Slide 1', no stage directions, no markdown. " +
            imagesNote + " " +
            "OUTPUT: ONLY minified JSON {\"scenes\":[{\"title\":string,\"bullets\":[string],\"narration\":string,\"imageIndex\":number}]}. No fences.";

        var source = req.SourceText.Length > 24_000 ? req.SourceText[..24_000] : req.SourceText;
        var user = $"Video title: {req.Title}\n\nSOURCE MATERIAL (stay faithful to it):\n\n{source}";

        var raw = await _ai.CompleteTextAsync(system, user, maxTokens: 6000);
        var scenes = Parse(raw, req.Images.Count);
        if (scenes.Count == 0)
            throw new InvalidOperationException("The script came back unusable — please try again.");
        return scenes;
    }

    private List<VideoScene> Parse(string raw, int imageCount)
    {
        var scenes = new List<VideoScene>();
        try
        {
            var json = InkReel.Api.Controllers.InklingController.ParseStrict(raw);
            if (json == null) return scenes;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("scenes", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return scenes;

            foreach (var s in arr.EnumerateArray())
            {
                var title = Str(s, "title");
                var narration = Str(s, "narration");
                if (string.IsNullOrWhiteSpace(narration)) continue;

                var bullets = new List<string>();
                if (s.TryGetProperty("bullets", out var b) && b.ValueKind == JsonValueKind.Array)
                    bullets = b.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => (x.GetString() ?? "").Trim())
                        .Where(x => x.Length > 0)
                        .Take(4)
                        .ToList();

                var imageIndex = -1;
                if (s.TryGetProperty("imageIndex", out var ii) && ii.ValueKind == JsonValueKind.Number
                    && ii.TryGetInt32(out var idx) && idx >= 0 && idx < imageCount)
                    imageIndex = idx;

                scenes.Add(new VideoScene
                {
                    Title = string.IsNullOrWhiteSpace(title) ? "—" : title.Trim(),
                    Bullets = bullets,
                    Narration = narration.Trim(),
                    ImageIndex = imageIndex,
                });
                if (scenes.Count == 14) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scene parse failed");
        }
        return scenes;
    }

    private static string Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
