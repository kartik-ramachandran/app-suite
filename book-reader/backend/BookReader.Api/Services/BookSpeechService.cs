using System.Text;
using System.Xml.Linq;

namespace BookReader.Api.Services;

/// <summary>
/// Speaks a book line in a character's voice using Azure Speech.
///
/// The REST endpoint is used rather than the Speech SDK: it needs no native
/// binaries, works the same in a Linux container, and returns an mp3 the
/// browser can play directly.
///
/// If no subscription key is configured this reports itself unavailable rather
/// than throwing. The reader then falls back to the browser's own speech
/// synthesis — worse voices, but it works for free and on day one, which is
/// what the free tier needs.
/// </summary>
public class BookSpeechService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<BookSpeechService> _logger;

    public BookSpeechService(IHttpClientFactory http, IConfiguration cfg, ILogger<BookSpeechService> logger)
    {
        _http = http;
        _cfg = cfg;
        _logger = logger;
    }

    private string? Key => _cfg["AzureSpeech:SubscriptionKey"];
    private string Region => _cfg["AzureSpeech:Region"] ?? "eastus";

    /// <summary>
    /// Whether server-side neural speech can run at all. The client asks this
    /// before playing so it can switch to browser speech without a failed
    /// request per line.
    /// </summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(Key);

    /// <summary>
    /// Renders one line to mp3. Returns null when speech is unavailable or the
    /// request fails — the caller falls back rather than surfacing an error,
    /// because a book that stops reading mid-chapter is worse than one that
    /// changes voice.
    /// </summary>
    public async Task<byte[]?> SynthesiseAsync(string text, string? voiceId, CancellationToken ct = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(text)) return null;

        var voice = string.IsNullOrWhiteSpace(voiceId) ? BookVoices.Narrator : voiceId;

        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);

            using var req = new HttpRequestMessage(
                HttpMethod.Post, $"https://{Region}.tts.speech.microsoft.com/cognitiveservices/v1");

            req.Headers.Add("Ocp-Apim-Subscription-Key", Key);
            req.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
            req.Headers.Add("User-Agent", "GenPrompt-BookReader");

            req.Content = new StringContent(BuildSsml(text, voice), Encoding.UTF8, "application/ssml+xml");

            using var res = await client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                _logger.LogWarning("Azure TTS returned {Status} for voice {Voice}", res.StatusCode, voice);
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

    /// <summary>
    /// Builds SSML with the text as a value, never as markup. Book text is
    /// full of ampersands and angle brackets, and pasting it raw into SSML
    /// produces either a 400 or, worse, injected markup.
    /// </summary>
    private static string BuildSsml(string text, string voice)
    {
        var speak = new XElement(
            XName.Get("speak", "http://www.w3.org/2001/10/synthesis"),
            new XAttribute("version", "1.0"),
            new XAttribute(XNamespace.Xml + "lang", "en-US"),
            new XElement(
                XName.Get("voice", "http://www.w3.org/2001/10/synthesis"),
                new XAttribute("name", voice),
                text));

        return speak.ToString(SaveOptions.DisableFormatting);
    }
}
