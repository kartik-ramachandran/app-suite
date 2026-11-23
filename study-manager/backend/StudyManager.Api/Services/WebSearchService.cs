using System.Net.Http;
using System.Text.Json;
using System.Web;

namespace StudyManager.Api.Services;

public class WebSearchService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<WebSearchService> _logger;

    public WebSearchService(IConfiguration configuration, ILogger<WebSearchService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string> SearchAsync(string query, int maxResults = 5)
    {
        var braveKey = _configuration["BraveSearch:ApiKey"];

        if (!string.IsNullOrWhiteSpace(braveKey))
            return await SearchWithBraveAsync(query, braveKey, maxResults);

        return await SearchWithDuckDuckGoAsync(query, maxResults);
    }

    private async Task<string> SearchWithBraveAsync(string query, string apiKey, int maxResults)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var encodedQuery = HttpUtility.UrlEncode(query);
        var url = $"https://api.search.brave.com/res/v1/web/search?q={encodedQuery}&count={maxResults}";

        client.DefaultRequestHeaders.Add("Accept", "application/json");
        client.DefaultRequestHeaders.Add("X-Subscription-Token", apiKey);

        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return await SearchWithDuckDuckGoAsync(query, maxResults);

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var results = new List<string>();
        if (doc.RootElement.TryGetProperty("web", out var web) &&
            web.TryGetProperty("results", out var items))
        {
            foreach (var item in items.EnumerateArray().Take(maxResults))
            {
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : "";
                var desc = item.TryGetProperty("description", out var d) ? d.GetString() : "";
                var urlStr = item.TryGetProperty("url", out var u) ? u.GetString() : "";
                results.Add($"**{title}**\n{desc}\n{urlStr}");
            }
        }

        return results.Count > 0
            ? $"Search results for \"{query}\":\n\n" + string.Join("\n\n---\n\n", results)
            : $"No results found for \"{query}\".";
    }

    private async Task<string> SearchWithDuckDuckGoAsync(string query, int maxResults)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("User-Agent", "GenPrompt/1.0");

        var encodedQuery = HttpUtility.UrlEncode(query);
        var url = $"https://api.duckduckgo.com/?q={encodedQuery}&format=json&no_html=1&skip_disambig=1";

        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode)
            return $"Search unavailable for \"{query}\". Configure BraveSearch:ApiKey in settings for full web search.";

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        var parts = new List<string>();

        if (doc.RootElement.TryGetProperty("Answer", out var answer) && !string.IsNullOrWhiteSpace(answer.GetString()))
            parts.Add($"**Answer:** {answer.GetString()}");

        if (doc.RootElement.TryGetProperty("AbstractText", out var abs) && !string.IsNullOrWhiteSpace(abs.GetString()))
            parts.Add($"**Summary:** {abs.GetString()}");

        if (doc.RootElement.TryGetProperty("RelatedTopics", out var topics))
        {
            var topicResults = new List<string>();
            foreach (var topic in topics.EnumerateArray().Take(maxResults))
            {
                if (topic.TryGetProperty("Text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                    topicResults.Add($"• {text.GetString()}");
            }
            if (topicResults.Count > 0)
                parts.Add("**Related:**\n" + string.Join("\n", topicResults));
        }

        return parts.Count > 0
            ? $"Search results for \"{query}\":\n\n" + string.Join("\n\n", parts)
            : $"No instant results for \"{query}\". Configure BraveSearch:ApiKey in appsettings for full web search.";
    }
}
