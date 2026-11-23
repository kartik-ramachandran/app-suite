using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using System.Text.Json;

namespace AgentHub.Api.Services;

/// <summary>
/// Syncs the browsable MCP catalogue from the official registry
/// (registry.modelcontextprotocol.io). The registry returns one row per server
/// VERSION, so this keeps only the latest active version per server name —
/// otherwise the picker shows the same server three times.
///
/// Servers that only ship a stdio package (npx/uvx) are stored but marked
/// IsConnectable = false: running arbitrary packages inside the container is
/// arbitrary code execution, so we only call remote (streamable-http) servers.
/// </summary>
public class McpRegistryService
{
    private const string RegistryBase = "https://registry.modelcontextprotocol.io/v0/servers";
    private const int MaxPages = 40; // ~4000 rows, well past the whole registry today

    private readonly PromptDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<McpRegistryService> _logger;

    public McpRegistryService(PromptDbContext db, IHttpClientFactory http, ILogger<McpRegistryService> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public record SyncResult(int Fetched, int Servers, int Connectable, int Added, int Updated);

    public async Task<SyncResult> SyncAsync(CancellationToken ct = default)
    {
        var client = _http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);

        // name -> newest row seen, so later versions overwrite earlier ones.
        var latest = new Dictionary<string, McpCatalogServer>(StringComparer.OrdinalIgnoreCase);
        string? cursor = null;
        var fetched = 0;

        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{RegistryBase}?limit=100" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var response = await client.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("MCP registry returned {Status} on page {Page}; keeping what we have.", response.StatusCode, page);
                break;
            }

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array) break;
            foreach (var row in servers.EnumerateArray())
            {
                fetched++;
                var parsed = ParseRow(row);
                if (parsed != null) latest[parsed.Name] = parsed;
            }

            cursor = root.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("nextCursor", out var next)
                ? next.GetString()
                : null;
            if (string.IsNullOrEmpty(cursor)) break;
        }

        if (latest.Count == 0)
        {
            _logger.LogWarning("MCP registry sync produced no servers — leaving the existing catalogue untouched.");
            return new SyncResult(fetched, 0, 0, 0, 0);
        }

        var existing = await _db.McpCatalogServers.ToDictionaryAsync(s => s.Name, StringComparer.OrdinalIgnoreCase, ct);
        int added = 0, updated = 0;
        foreach (var (name, incoming) in latest)
        {
            if (existing.TryGetValue(name, out var current))
            {
                current.Title = incoming.Title;
                current.Description = incoming.Description;
                current.Version = incoming.Version;
                current.Category = incoming.Category;
                current.Transport = incoming.Transport;
                current.RemoteUrl = incoming.RemoteUrl;
                current.Publisher = incoming.Publisher;
                current.WebsiteUrl = incoming.WebsiteUrl;
                current.IsConnectable = incoming.IsConnectable;
                current.RegistryUpdatedAt = incoming.RegistryUpdatedAt;
                current.SyncedAt = DateTime.UtcNow;
                updated++;
            }
            else
            {
                _db.McpCatalogServers.Add(incoming);
                added++;
            }
        }

        await _db.SaveChangesAsync(ct);
        var connectable = latest.Values.Count(s => s.IsConnectable);
        _logger.LogInformation(
            "MCP registry sync: {Fetched} rows → {Servers} servers ({Connectable} connectable); {Added} new, {Updated} updated.",
            fetched, latest.Count, connectable, added, updated);
        return new SyncResult(fetched, latest.Count, connectable, added, updated);
    }

    private static McpCatalogServer? ParseRow(JsonElement row)
    {
        if (!row.TryGetProperty("server", out var s)) return null;

        // Skip anything not current: the registry keeps every version, and
        // deleted/deprecated servers stay queryable.
        if (row.TryGetProperty("_meta", out var meta) &&
            meta.TryGetProperty("io.modelcontextprotocol.registry/official", out var official))
        {
            if (official.TryGetProperty("isLatest", out var isLatest) && !isLatest.GetBoolean()) return null;
            if (official.TryGetProperty("status", out var status) &&
                !string.Equals(status.GetString(), "active", StringComparison.OrdinalIgnoreCase)) return null;
        }

        var name = s.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return null;

        var description = s.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var title = s.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(title)) title = PrettyName(name!);

        // Prefer a streamable-http remote; fall back to sse. Package-only servers
        // are catalogued but not connectable.
        var remoteUrl = "";
        if (s.TryGetProperty("remotes", out var remotes) && remotes.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in remotes.EnumerateArray())
            {
                var type = r.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                var u = r.TryGetProperty("url", out var uu) ? uu.GetString() : null;
                if (string.IsNullOrWhiteSpace(u)) continue;
                if (string.Equals(type, "streamable-http", StringComparison.OrdinalIgnoreCase)) { remoteUrl = u!; break; }
                if (string.IsNullOrEmpty(remoteUrl)) remoteUrl = u!;
            }
        }

        var updatedAt = DateTime.UtcNow;
        if (row.TryGetProperty("_meta", out var m2) &&
            m2.TryGetProperty("io.modelcontextprotocol.registry/official", out var o2) &&
            o2.TryGetProperty("updatedAt", out var ua) &&
            DateTime.TryParse(ua.GetString(), out var parsedUpdated))
        {
            updatedAt = parsedUpdated.ToUniversalTime();
        }

        return new McpCatalogServer
        {
            Name = name!,
            Title = title,
            Description = description,
            Version = s.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
            Category = Categorise(name!, title, description),
            Transport = string.IsNullOrEmpty(remoteUrl) ? "package" : "remote",
            RemoteUrl = remoteUrl,
            Publisher = name!.Contains('/') ? name!.Split('/')[0] : name!,
            WebsiteUrl = s.TryGetProperty("websiteUrl", out var w) ? w.GetString() : null,
            IsConnectable = !string.IsNullOrEmpty(remoteUrl),
            RegistryUpdatedAt = updatedAt,
            SyncedAt = DateTime.UtcNow,
        };
    }

    private static string PrettyName(string name)
    {
        var last = name.Contains('/') ? name.Split('/').Last() : name;
        last = last.Replace('-', ' ').Replace('_', ' ').Replace("mcp", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(last)) return name;
        return char.ToUpperInvariant(last[0]) + last.Substring(1);
    }

    // Keyword buckets so the picker can be filtered by what a tool is FOR.
    private static readonly (string Category, string[] Keywords)[] Buckets =
    {
        ("Developer",      new[] { "github", "gitlab", "git ", "jira", "linear", "sentry", "ci/cd", "devops", "code", "repo", "docker", "kubernetes", "terraform" }),
        ("Data",           new[] { "postgres", "mysql", "sql", "database", "snowflake", "bigquery", "duckdb", "mongo", "redis", "warehouse", "analytics", "dataset" }),
        ("Communication",  new[] { "slack", "discord", "teams", "email", "gmail", "outlook", "sms", "twilio", "whatsapp", "telegram", "chat" }),
        ("Productivity",   new[] { "notion", "airtable", "asana", "trello", "todoist", "calendar", "docs", "sheets", "drive", "confluence", "task" }),
        ("Search & web",   new[] { "search", "browser", "scrape", "crawl", "web", "fetch", "perplexity", "wikipedia", "news" }),
        ("Cloud",          new[] { "aws", "azure", "gcp", "cloudflare", "vercel", "netlify", "s3", "lambda", "storage" }),
        ("Finance",        new[] { "stripe", "payment", "invoice", "accounting", "quickbooks", "xero", "crypto", "trading", "market", "bank" }),
        ("Sales & CRM",    new[] { "salesforce", "hubspot", "crm", "pipedrive", "zendesk", "intercom", "support", "customer" }),
        ("Design & media", new[] { "figma", "image", "video", "audio", "design", "canva", "photo", "render", "3d", "music" }),
        ("AI & models",    new[] { "llm", "model", "openai", "anthropic", "inference", "embedding", "vector", "rag", "agent" }),
        ("Security",       new[] { "security", "vault", "secret", "auth", "scan", "vulnerab", "compliance", "audit" }),
        ("Maps & travel",  new[] { "maps", "location", "travel", "flight", "hotel", "weather", "geo" }),
    };

    private static string Categorise(string name, string title, string description)
    {
        var haystack = $"{name} {title} {description}".ToLowerInvariant();
        foreach (var (category, keywords) in Buckets)
            if (keywords.Any(haystack.Contains)) return category;
        return "Other";
    }
}

/// <summary>Refreshes the MCP catalogue on boot and daily thereafter.</summary>
public class McpCatalogSyncHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<McpCatalogSyncHostedService> _logger;

    public McpCatalogSyncHostedService(IServiceProvider services, ILogger<McpCatalogSyncHostedService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting (and tables get created) before the first sync.
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); } catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _services.CreateScope();
                var sync = scope.ServiceProvider.GetRequiredService<McpRegistryService>();
                await sync.SyncAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MCP catalogue sync failed; retrying tomorrow.");
            }

            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
