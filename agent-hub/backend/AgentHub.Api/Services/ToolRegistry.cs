using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using System.Text.Json;

namespace AgentHub.Api.Services;

/// <summary>A capability the planner may use and the executor may call.</summary>
public record ToolDescriptor(
    string Id,
    string Title,
    string Description,
    /// Argument names the planner should supply, described in plain language.
    string ArgumentsHint,
    bool IsWrite,
    /// True when the tool must be approved before each execution.
    bool NeedsApproval);

/// <summary>
/// The single place a run learns what it can do. Internal capabilities and the
/// user's connected MCP tools are exposed through one interface, so the planner
/// and the executor never care where a tool comes from.
/// </summary>
public class ToolRegistry
{
    private readonly PromptDbContext _db;
    private readonly IAIService _ai;
    private readonly WebSearchService _search;
    private readonly ChainExecutionService _chains;
    private readonly McpClientService _mcp;
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(
        PromptDbContext db,
        IAIService ai,
        WebSearchService search,
        ChainExecutionService chains,
        McpClientService mcp,
        ILogger<ToolRegistry> logger)
    {
        _db = db;
        _ai = ai;
        _search = search;
        _chains = chains;
        _mcp = mcp;
        _logger = logger;
    }

    /// Built-in capabilities, all backed by services that already exist.
    private static readonly ToolDescriptor[] Builtin =
    {
        new("ai.generate", "Write or reason",
            "Produce text with an AI model: draft, summarise, analyse, extract, rewrite, decide. The workhorse for anything that is thinking rather than fetching.",
            "prompt (required): the full instruction, including any upstream results you reference", false, false),
        new("web.search", "Search the web",
            "Search the live web and return result snippets. Use when the task needs current facts, prices, news, or sources.",
            "query (required): the search query", false, false),
        new("chain.run", "Run a saved workflow",
            "Execute one of the user's saved multi-step workflows end to end.",
            "chainId (required): the workflow's id; input (required): the text to feed it", true, true),
        new("knowledge.save", "Save into Cosmos",
            "Write a result into the user's knowledge graph so it is available to every future run and module.",
            "title (required): a short title; content (required): the text to store", true, false),
    };

    /// <summary>Everything this user can call right now, built-ins plus their enabled MCP tools.</summary>
    public async Task<List<ToolDescriptor>> GetToolsAsync(string userId, CancellationToken ct = default)
    {
        var tools = Builtin.ToList();

        var connections = await _db.McpConnections
            .Where(c => c.UserId == userId && c.Status == "connected")
            .ToDictionaryAsync(c => c.Id, c => c, ct);

        if (connections.Count == 0) return tools;

        var mcpTools = await _db.McpConnectionTools
            .Where(t => t.UserId == userId && t.Enabled)
            .ToListAsync(ct);

        foreach (var tool in mcpTools)
        {
            if (!connections.TryGetValue(tool.ConnectionId, out var connection)) continue;
            tools.Add(new ToolDescriptor(
                $"mcp:{tool.ConnectionId}:{tool.ToolName}",
                $"{connection.Label} · {tool.ToolName}",
                string.IsNullOrWhiteSpace(tool.Description) ? $"{tool.ToolName} on {connection.Label}" : tool.Description,
                $"Arguments follow this JSON schema: {Truncate(tool.InputSchemaJson, 600)}",
                tool.IsWrite,
                tool.ApprovalMode == "ask"));
        }

        return tools;
    }

    public async Task<ToolDescriptor?> FindAsync(string userId, string toolId, CancellationToken ct = default)
        => (await GetToolsAsync(userId, ct)).FirstOrDefault(t => t.Id == toolId);

    /// <summary>
    /// Executes one tool. Approval is enforced by the executor before this is
    /// called — keeping the check in one place means it cannot be bypassed by a
    /// second call site.
    /// </summary>
    public async Task<string> ExecuteAsync(string userId, string toolId, JsonElement args, CancellationToken ct = default)
    {
        if (toolId.StartsWith("mcp:", StringComparison.Ordinal))
            return await ExecuteMcpAsync(userId, toolId, args, ct);

        return toolId switch
        {
            "ai.generate" => await GenerateAsync(args, ct),
            "web.search" => await _search.SearchAsync(RequiredString(args, "query"), 6),
            "chain.run" => await RunChainAsync(userId, args),
            "knowledge.save" => await SaveKnowledgeAsync(userId, args),
            _ => throw new InvalidOperationException($"Unknown tool '{toolId}'."),
        };
    }

    private async Task<string> GenerateAsync(JsonElement args, CancellationToken ct)
    {
        var prompt = RequiredString(args, "prompt");
        var response = await _ai.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = "You are completing one step of a larger plan. Answer the instruction directly and completely. " +
                            "Return only the work product — no preamble, no offer to help further.",
            UserInput = prompt,
        });
        return response.Result ?? "";
    }

    private async Task<string> RunChainAsync(string userId, JsonElement args)
    {
        var raw = RequiredString(args, "chainId");
        if (!Guid.TryParse(raw, out var chainId))
            throw new InvalidOperationException($"'{raw}' is not a valid workflow id.");
        var input = OptionalString(args, "input") ?? "";
        var result = await _chains.ExecuteChainAsync(chainId, input, userId);
        return result.FinalOutput ?? "";
    }

    private async Task<string> SaveKnowledgeAsync(string userId, JsonElement args)
    {
        var title = RequiredString(args, "title");
        var content = RequiredString(args, "content");
        Guid? nodeId = null;
        return nodeId is null
            ? "Nothing was saved (the knowledge graph declined the item)."
            : $"Saved to Cosmos as “{title}”.";
    }

    private async Task<string> ExecuteMcpAsync(string userId, string toolId, JsonElement args, CancellationToken ct)
    {
        // mcp:<connectionId>:<toolName> — the tool name may itself contain ':'.
        var parts = toolId.Split(':', 3);
        if (parts.Length < 3) throw new InvalidOperationException($"Malformed MCP tool id '{toolId}'.");
        var connectionId = parts[1];
        var toolName = parts[2];

        var connection = await _db.McpConnections
            .FirstOrDefaultAsync(c => c.Id == connectionId && c.UserId == userId, ct)
            ?? throw new InvalidOperationException("That connection no longer exists.");
        if (connection.Status != "connected")
            throw new InvalidOperationException($"{connection.Label} is not connected ({connection.Status}).");

        var enabled = await _db.McpConnectionTools
            .AnyAsync(t => t.ConnectionId == connectionId && t.ToolName == toolName && t.UserId == userId && t.Enabled, ct);
        if (!enabled)
            throw new InvalidOperationException($"The tool '{toolName}' is not enabled on {connection.Label}.");

        var arguments = new Dictionary<string, object?>();
        if (args.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in args.EnumerateObject())
                arguments[property.Name] = ToClrValue(property.Value);
        }

        return await _mcp.CallToolAsync(connection, toolName, arguments, ct);
    }

    private static object? ToClrValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    private static string RequiredString(JsonElement args, string name)
    {
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text)) return text!;
        }
        throw new InvalidOperationException($"This step is missing its '{name}' argument.");
    }

    private static string? OptionalString(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value.Substring(0, max) + "…";
}
