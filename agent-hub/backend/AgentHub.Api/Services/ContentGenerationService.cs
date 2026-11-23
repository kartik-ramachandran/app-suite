using System.Text.RegularExpressions;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public record ContentPhaseUpdate(string Phase, string Content, bool IsFinal = false);

public class ContentGenerationService
{
    private readonly IAIService _aiService;
    private readonly WebSearchService _webSearchService;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ContentGenerationService> _logger;

    public ContentGenerationService(
        IAIService aiService,
        WebSearchService webSearchService,
        IHttpClientFactory httpFactory,
        ILogger<ContentGenerationService> logger)
    {
        _aiService = aiService;
        _webSearchService = webSearchService;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async IAsyncEnumerable<ContentPhaseUpdate> GenerateAsync(
        string topic,
        string contentType,
        string tone,
        int targetLength,
        bool includeWebSearch,
        string? userApiKey = null,
        string? userModel = null,
        IAIService? serviceOverride = null)
    {
        var service = GetService(userApiKey, userModel, serviceOverride);
        var searchContext = "";

        // Phase 1: Web Research
        if (includeWebSearch)
        {
            yield return new ContentPhaseUpdate("search", "Searching the web for up-to-date context...");
            string searchResult = "";
            try
            {
                searchResult = await _webSearchService.SearchAsync(topic, 5);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Web search failed during content generation");
                searchResult = "";
            }

            if (!string.IsNullOrWhiteSpace(searchResult))
            {
                searchContext = searchResult;
                yield return new ContentPhaseUpdate("search", "Web context retrieved.");
            }
            else
            {
                yield return new ContentPhaseUpdate("search", "Web search unavailable, proceeding without live context.");
            }
        }

        // Phase 2: Research Synthesis
        yield return new ContentPhaseUpdate("research", "Synthesising key points...");
        var researchResp = await service.TestPromptAsync(new PromptTestRequest
        {
            PromptContent =
                "You are a research analyst. Extract the 5 most important points to cover in the content. " +
                "Output a numbered list only.",
            UserInput = $"Topic: {topic}\n\n{searchContext}"
        });
        var keyPoints = researchResp.Result.Trim();
        yield return new ContentPhaseUpdate("research", keyPoints);

        // Phase 3: Outline
        yield return new ContentPhaseUpdate("outline", "Creating structured outline...");
        var outlineResp = await service.TestPromptAsync(new PromptTestRequest
        {
            PromptContent =
                $"Create a clear outline for a {contentType} on the topic. Target ~{targetLength} words. " +
                "Output ONLY the outline with headings and bullet sub-points.",
            UserInput = $"Topic: {topic}\n\nKey points:\n{keyPoints}"
        });
        var outline = outlineResp.Result.Trim();
        yield return new ContentPhaseUpdate("outline", outline);

        // Phase 4: Full Content (streaming)
        yield return new ContentPhaseUpdate("writing", "Writing full content...");

        var writingSystem =
            $"You are an expert {contentType} writer with a {tone} tone. " +
            $"Write a complete {contentType} of ~{targetLength} words following the outline. " +
            $"Use {tone} language throughout.{(string.IsNullOrWhiteSpace(searchContext) ? "" : " " + searchContext)}";

        var writingUser = $"Topic: {topic}\n\nOutline:\n{outline}";

        await foreach (var token in service.StreamExecuteAsync(
            writingSystem,
            new List<ConversationMessage> { new("user", writingUser) },
            userModel))
        {
            yield return new ContentPhaseUpdate("writing", token);
        }

        yield return new ContentPhaseUpdate("done", "", true);
    }

    // Platform rules for the multi-platform "Social Pack" generator.
    public static readonly IReadOnlyDictionary<string, string> SocialPlatformRules = new Dictionary<string, string>
    {
        ["linkedin"]  = "LinkedIn: up to 1500 characters, professional narrative with a strong hook line, short paragraphs, one clear takeaway, 3-5 relevant hashtags at the end.",
        ["twitter"]   = "twitter: under 280 characters TOTAL (this is a hard limit), punchy and direct, 2-3 hashtags inline.",
        ["instagram"] = "instagram: 150-300 characters, warm and visual language, line breaks for readability, a call to engage, 8-12 hashtags at the end.",
        ["facebook"]  = "facebook: 1-3 short conversational paragraphs, friendly and relatable, end with a question that invites comments, 0-2 hashtags only.",
        ["threads"]   = "threads: under 500 characters, casual and conversational like a text to a friend, at most 1-2 hashtags.",
        ["tiktok"]     = "tiktok: a short video caption under 150 characters with a strong scroll-stopping hook, plus 3-5 trending-style hashtags.",
        ["newsletter"] = "newsletter: a short email newsletter. The FIRST LINE must be 'Subject: <a compelling subject line>'. Then 2-4 short paragraphs in a warm, informative tone with one clear takeaway and a soft call to action at the end. Plain text, no hashtags.",
    };

    public static readonly string[] AllSocialPlatforms = { "linkedin", "twitter", "instagram", "facebook", "threads", "tiktok", "newsletter" };

    /// <summary>
    /// Generates ready-to-post copy for the requested platforms from a topic or source content.
    /// Returns a platform-keyed dictionary (keys from <see cref="AllSocialPlatforms"/>).
    /// </summary>
    public async Task<Dictionary<string, string>> GenerateSocialPackAsync(
        string sourceContent,
        string tone,
        IEnumerable<string>? platforms = null,
        string? userApiKey = null,
        string? userModel = null,
        IAIService? serviceOverride = null,
        bool includeWebSearch = true)
    {
        var service = GetService(userApiKey, userModel, serviceOverride);

        var wanted = (platforms ?? AllSocialPlatforms)
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(SocialPlatformRules.ContainsKey)
            .Distinct()
            .ToList();
        if (wanted.Count == 0) wanted = AllSocialPlatforms.ToList();

        // Ground the copy in reality: read any links the user pasted, and (optionally) web-search the topic.
        var augmentedSource = sourceContent;
        try
        {
            var grounding = await GatherGroundingAsync(sourceContent, includeWebSearch);
            if (!string.IsNullOrWhiteSpace(grounding))
                augmentedSource = sourceContent +
                    "\n\n--- Reference material (use for accurate, current facts; write original copy, do not copy verbatim) ---\n" + grounding;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Social pack grounding failed"); }

        var rules = string.Join("\n", wanted.Select(p => "- " + SocialPlatformRules[p]));
        var shape = string.Join(",\n", wanted.Select(p => $"  \"{p}\": \"...\""));

        var systemPrompt = $$"""
            You are a social media expert. Given source content and a tone, write ready-to-post copy tailored to each platform's style and audience.
            Return ONLY valid JSON (no markdown, no code blocks) with exactly these fields:
            {
            {{shape}}
            }

            Per-platform rules:
            {{rules}}

            Write natural, human copy — no "as an AI", no meta commentary. Respect each length limit strictly.
            """;

        var messages = new List<ConversationMessage>
        {
            new("user", $"Tone: {tone}\n\nSource content / topic:\n{augmentedSource}")
        };

        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in service.StreamExecuteAsync(systemPrompt, messages, userModel))
            sb.Append(chunk);

        var json = sb.ToString().Trim();
        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('\n') + 1;
            var end = json.LastIndexOf("```");
            if (start > 0 && end > start) json = json[start..end].Trim();
        }
        var brace = json.IndexOf('{');
        var lastBrace = json.LastIndexOf('}');
        if (brace >= 0 && lastBrace > brace) json = json[brace..(lastBrace + 1)];

        var result = new Dictionary<string, string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var p in wanted)
                if (doc.RootElement.TryGetProperty(p, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String)
                    result[p] = el.GetString() ?? "";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Social pack JSON parse failed");
        }
        return result;
    }

    // Reads pasted links (fetches + strips the page) and optionally web-searches the topic.
    private async Task<string> GatherGroundingAsync(string source, bool includeWebSearch)
    {
        var sb = new System.Text.StringBuilder();

        var urls = Regex.Matches(source, @"https?://[^\s""'<>)]+")
            .Select(m => m.Value.TrimEnd('.', ',', ')'))
            .Distinct()
            .Take(2)
            .ToList();

        foreach (var url in urls)
        {
            var text = await FetchUrlTextAsync(url);
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine($"[Content of {url}]");
                sb.AppendLine(text[..Math.Min(text.Length, 3500)]);
                sb.AppendLine();
            }
        }

        if (includeWebSearch)
        {
            var query = Regex.Replace(source, @"https?://[^\s""'<>)]+", "").Trim();
            if (query.Length is > 2 and < 220)
            {
                try
                {
                    var results = await _webSearchService.SearchAsync(query, 5);
                    if (!string.IsNullOrWhiteSpace(results))
                    {
                        sb.AppendLine("[Recent web search results]");
                        sb.AppendLine(results);
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Social pack web search failed"); }
            }
        }

        return sb.ToString().Trim();
    }

    private async Task<string> FetchUrlTextAsync(string url)
    {
        try
        {
            var client = _httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 GenPrompt-SocialPack/1.0");
            var html = await client.GetStringAsync(url);
            return ExtractTextFromHtml(html);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Social pack failed to fetch {Url}", url);
            return "";
        }
    }

    private static string ExtractTextFromHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        html = Regex.Replace(html, @"<script[\s\S]*?</script>", " ", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<style[\s\S]*?</style>", " ", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<[^>]+>", " ");
        html = System.Net.WebUtility.HtmlDecode(html);
        html = Regex.Replace(html, @"\s+", " ").Trim();
        return html;
    }

    public async Task<(string LinkedIn, string Twitter, string Instagram)> GenerateSocialPostsAsync(
        string sourceContent,
        string tone,
        string? userApiKey = null,
        string? userModel = null,
        IAIService? serviceOverride = null)
    {
        var service = GetService(userApiKey, userModel, serviceOverride);

        const string systemPrompt = """
            You are a social media expert. Given source content and a tone, generate platform-optimized posts.
            Return ONLY valid JSON (no markdown, no code blocks) with exactly these three fields:
            { "linkedin": "...", "twitter": "...", "instagram": "..." }

            Rules:
            - LinkedIn: up to 1500 chars, professional narrative, 3-5 hashtags at the end
            - twitter: under 280 chars total, punchy and direct, 2-3 hashtags inline
            - instagram: 150-300 chars, visual/engaging language, 8-12 hashtags at the end
            """;

        var messages = new List<ConversationMessage>
        {
            new("user", $"Tone: {tone}\n\nSource content:\n{sourceContent}")
        };

        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in service.StreamExecuteAsync(systemPrompt, messages, userModel))
            sb.Append(chunk);

        var json = sb.ToString().Trim();
        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('\n') + 1;
            var end = json.LastIndexOf("```");
            if (start > 0 && end > start)
                json = json[start..end].Trim();
        }

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        return (
            root.GetProperty("linkedin").GetString() ?? "",
            root.GetProperty("twitter").GetString() ?? "",
            root.GetProperty("instagram").GetString() ?? ""
        );
    }

    private IAIService GetService(string? userApiKey, string? userModel, IAIService? serviceOverride = null)
    {
        if (serviceOverride != null) return serviceOverride;
        if (!string.IsNullOrWhiteSpace(userApiKey))
        {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenAI:ApiKey"] = userApiKey,
                    ["OpenAI:DefaultModel"] = userModel ?? "gpt-4o"
                }).Build();
            return new OpenAIService(cfg, Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAIService>.Instance);
        }
        return _aiService;
    }
}
