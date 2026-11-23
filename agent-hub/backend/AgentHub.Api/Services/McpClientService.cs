using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using System.Text.Json;

namespace AgentHub.Api.Services;

/// <summary>
/// Talks to remote MCP servers on a user's behalf: verifies a connection,
/// discovers its tools, and calls them.
///
/// Remote (streamable-http / SSE) transports ONLY. Launching stdio packages
/// would mean running arbitrary third-party code inside the API container, so
/// package-only servers are catalogued but never connected.
/// </summary>
public class McpClientService
{
    private readonly PromptDbContext _db;
    private readonly SecretProtector _secrets;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpClientService> _logger;

    public McpClientService(PromptDbContext db, SecretProtector secrets, ILoggerFactory loggerFactory, ILogger<McpClientService> logger)
    {
        _db = db;
        _secrets = secrets;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public record DiscoveredTool(string Name, string Description, string InputSchemaJson, bool IsWrite);

    /// <summary>Opens a session, lists tools, and disposes. Throws on failure — callers translate to a status.</summary>
    public async Task<List<DiscoveredTool>> DiscoverToolsAsync(McpConnection connection, CancellationToken ct = default)
    {
        await using var client = await ConnectAsync(connection, ct);
        var tools = await client.ListToolsAsync(cancellationToken: ct);

        return tools.Select(t => new DiscoveredTool(
            t.Name,
            t.Description ?? "",
            t.JsonSchema.ValueKind == JsonValueKind.Undefined ? "{}" : t.JsonSchema.GetRawText(),
            LooksLikeWrite(t.Name, t.Description ?? "")
        )).ToList();
    }

    /// <summary>
    /// Calls one tool. The caller is responsible for checking the tool is enabled
    /// and approved — this method deliberately does not police that, so the run
    /// engine stays the single place approval is enforced.
    /// </summary>
    public async Task<string> CallToolAsync(
        McpConnection connection,
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        await using var client = await ConnectAsync(connection, ct);
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: ct);

        var text = string.Join("\n", result.Content
            .Select(c => c is ModelContextProtocol.Protocol.TextContentBlock tb ? tb.Text : null)
            .Where(s => !string.IsNullOrWhiteSpace(s)));

        if (result.IsError == true)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(text) ? "The tool reported an error." : text);

        return text;
    }

    /// <summary>Verifies a connection and refreshes its stored tool list. Never throws.</summary>
    public async Task<(bool Ok, string? Error, int ToolCount)> RefreshAsync(McpConnection connection, CancellationToken ct = default)
    {
        try
        {
            var tools = await DiscoverToolsAsync(connection, ct);

            var existing = await _db.McpConnectionTools
                .Where(t => t.ConnectionId == connection.Id)
                .ToListAsync(ct);

            // Keep the user's enable/approval choices across a refresh.
            foreach (var tool in tools)
            {
                var current = existing.FirstOrDefault(e => e.ToolName == tool.Name);
                if (current == null)
                {
                    _db.McpConnectionTools.Add(new McpConnectionTool
                    {
                        ConnectionId = connection.Id,
                        UserId = connection.UserId,
                        ToolName = tool.Name,
                        Description = tool.Description,
                        InputSchemaJson = tool.InputSchemaJson,
                        IsWrite = tool.IsWrite,
                        // Read-only tools run unattended; anything that may write asks first.
                        ApprovalMode = tool.IsWrite ? "ask" : "auto",
                        Enabled = true,
                    });
                }
                else
                {
                    current.Description = tool.Description;
                    current.InputSchemaJson = tool.InputSchemaJson;
                    current.IsWrite = tool.IsWrite;
                }
            }

            // Drop tools the server no longer exposes.
            var liveNames = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in existing.Where(e => !liveNames.Contains(e.ToolName)))
                _db.McpConnectionTools.Remove(stale);

            connection.Status = "connected";
            connection.LastError = null;
            connection.ToolCount = tools.Count;
            connection.LastCheckedAt = DateTime.UtcNow;
            connection.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return (true, null, tools.Count);
        }
        catch (Exception ex)
        {
            var message = Summarise(ex);
            connection.Status = message.Contains("auth", StringComparison.OrdinalIgnoreCase) ? "needs_auth" : "error";
            connection.LastError = message;
            connection.LastCheckedAt = DateTime.UtcNow;
            connection.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("MCP connection {Id} ({Server}) failed to refresh: {Error}", connection.Id, connection.ServerName, message);
            return (false, message, 0);
        }
    }

    private async Task<McpClient> ConnectAsync(McpConnection connection, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(connection.RemoteUrl))
            throw new InvalidOperationException("This server has no remote endpoint — it can only run as a local package.");

        var headers = new Dictionary<string, string>();
        var secret = _secrets.Unprotect(connection.SecretEncrypted);
        if (connection.AuthType == "bearer" && !string.IsNullOrWhiteSpace(secret))
            headers["Authorization"] = $"Bearer {secret}";
        else if (connection.AuthType == "header" && !string.IsNullOrWhiteSpace(secret) && !string.IsNullOrWhiteSpace(connection.HeaderName))
            headers[connection.HeaderName!] = secret!;
        else if (connection.AuthType is "bearer" or "header" && string.IsNullOrWhiteSpace(secret))
            throw new InvalidOperationException("Stored credential could not be read — re-enter it to reconnect.");

        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(connection.RemoteUrl),
            ConnectionTimeout = TimeSpan.FromSeconds(30),
        };
        if (headers.Count > 0) options.AdditionalHeaders = headers;

        var transport = new HttpClientTransport(options, _loggerFactory);
        return await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory, cancellationToken: ct);
    }

    /// Rough read/write split so read-only tools don't nag for approval.
    private static readonly string[] WriteVerbs =
    {
        "create", "update", "delete", "remove", "write", "send", "post", "put", "patch",
        "insert", "upload", "publish", "merge", "close", "assign", "invite", "pay",
        "charge", "execute", "run", "deploy", "set", "add", "move", "archive", "cancel",
    };

    private static bool LooksLikeWrite(string name, string description)
    {
        var haystack = $"{name} {description}".ToLowerInvariant();
        // Explicit read verbs win: "list_write_permissions" is a read.
        if (haystack.StartsWith("get") || haystack.StartsWith("list") || haystack.StartsWith("read")
            || haystack.StartsWith("search") || haystack.StartsWith("find") || haystack.StartsWith("fetch"))
            return false;
        return WriteVerbs.Any(haystack.Contains);
    }

    private static string Summarise(Exception ex) => ex switch
    {
        HttpRequestException http => $"Could not reach the server ({(int?)http.StatusCode ?? 0}).",
        TaskCanceledException => "The server did not respond in time.",
        UriFormatException => "That endpoint URL is not valid.",
        _ => ex.Message.Length > 200 ? ex.Message.Substring(0, 200) : ex.Message,
    };
}
