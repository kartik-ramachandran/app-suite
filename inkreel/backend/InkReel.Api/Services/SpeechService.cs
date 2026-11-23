using System.Text;

namespace InkReel.Api.Services;

public record VideoVoice(string Id, string Label, string Vibe, string Gender, string AzureVoice);

/// <summary>
/// Narration voices via the Azure Speech REST endpoint — the same keyless-SDK
/// pattern proven in the parent app's book reader (no native binaries, plain
/// HTTP). Friendly voice names with a one-word vibe, each mapped to an Azure
/// neural voice. Returns null when unconfigured or failing; the caller then
/// builds a silent video with timed slides instead of surfacing an error.
/// </summary>
public class SpeechService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<SpeechService> _logger;

    public SpeechService(IHttpClientFactory http, IConfiguration cfg, ILogger<SpeechService> logger)
    { _http = http; _cfg = cfg; _logger = logger; }

    private string? Key => _cfg["AzureSpeech:SubscriptionKey"];
    private string Region => _cfg["AzureSpeech:Region"] ?? "eastus";

    public bool IsAvailable => !string.IsNullOrWhiteSpace(Key);

    public static readonly IReadOnlyList<VideoVoice> Voices = new List<VideoVoice>
    {
        new("friendly-male",    "Achird",  "Friendly",    "male",   "en-US-GuyNeural"),
        new("gravelly-male",    "Algenib", "Gravelly",    "male",   "en-US-DavisNeural"),
        new("firm-male",        "Alnilam", "Firm",        "male",   "en-US-JasonNeural"),
        new("informative-male", "Charon",  "Informative", "male",   "en-US-TonyNeural"),
        new("friendly-female",  "Vega",    "Friendly",    "female", "en-US-JennyNeural"),
        new("bright-female",    "Mira",    "Bright",      "female", "en-US-AriaNeural"),
        new("calm-female",      "Lyra",    "Calm",        "female", "en-US-SaraNeural"),
        new("informative-female","Nashira","Informative", "female", "en-US-NancyNeural"),
    };

    public static VideoVoice Find(string? id) =>
        Voices.FirstOrDefault(v => v.Id == (id ?? "")) ?? Voices[0];

    /// <summary>Render one scene's narration to MP3 bytes, or null on any failure.</summary>
    public async Task<byte[]?> SynthesiseAsync(string text, string voiceId, string narrationStyle, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return null;
        var voice = Find(voiceId);

        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(60);

            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"https://{Region}.tts.speech.microsoft.com/cognitiveservices/v1");

            req.Headers.Add("Ocp-Apim-Subscription-Key", Key);
            req.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-96kbitrate-mono-mp3");
            req.Headers.Add("User-Agent", "InkReel-Video");

            req.Content = new StringContent(BuildSsml(text, voice.AzureVoice, narrationStyle), Encoding.UTF8, "application/ssml+xml");

            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Azure TTS returned {Status} for voice {Voice}", res.StatusCode, voice.AzureVoice);
                return null;
            }
            return await res.Content.ReadAsByteArrayAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Speech synthesis failed");
            return null;
        }
    }

    private static string BuildSsml(string text, string azureVoice, string narrationStyle)
    {
        var escaped = text
            .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
            .Replace("\"", "&quot;").Replace("'", "&apos;");

        // A touch of pacing per narration style; styles degrade gracefully on
        // voices that don't support mstts:express-as.
        var rate = narrationStyle switch
        {
            "energetic" => "+8%",
            "calm" => "-6%",
            _ => "0%",
        };

        return
            $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
            $"<voice name='{azureVoice}'><prosody rate='{rate}'>{escaped}</prosody></voice></speak>";
    }
}
