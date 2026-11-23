using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using AgentHub.Api.Data;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public class AgentRunnerService
{
    private readonly IAIService _aiService;
    private readonly WebSearchService _webSearchService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AgentRunnerService> _logger;
    private readonly PromptDbContext _db;
    private readonly ChainExecutionService _chainService;
    private readonly IEmailService _emailService;

    public static readonly Dictionary<string, AgentToolDefinition> AvailableTools = new()
    {
        ["web_search"] = new AgentToolDefinition(
            "web_search",
            "Search the web for current information on any topic. Use this when you need up-to-date facts.",
            """{"type":"object","properties":{"query":{"type":"string","description":"The search query to look up"}},"required":["query"]}"""
        ),
        ["http_request"] = new AgentToolDefinition(
            "http_request",
            "Make an HTTP GET or POST request to a URL and return the response body.",
            """{"type":"object","properties":{"url":{"type":"string","description":"The URL to request"},"method":{"type":"string","enum":["GET","POST"],"description":"HTTP method, default GET"},"body":{"type":"string","description":"JSON body for POST requests"}},"required":["url"]}"""
        ),
        ["run_prompt"] = new AgentToolDefinition(
            "run_prompt",
            "Run a saved prompt by its ID with an input message and return the AI response.",
            """{"type":"object","properties":{"promptId":{"type":"string","description":"The saved prompt ID (UUID)"},"input":{"type":"string","description":"User message to send alongside the prompt"}},"required":["promptId","input"]}"""
        ),
        ["run_chain"] = new AgentToolDefinition(
            "run_chain",
            "Execute a saved workflow chain with an input and return the final output.",
            """{"type":"object","properties":{"chainId":{"type":"string","description":"The workflow chain ID (GUID)"},"input":{"type":"string","description":"Input text for the chain"}},"required":["chainId","input"]}"""
        ),
        ["run_agent"] = new AgentToolDefinition(
            "run_agent",
            "Delegate a subtask to another installed agent by its ID and return its output.",
            """{"type":"object","properties":{"agentId":{"type":"integer","description":"ID of the agent to run"},"input":{"type":"string","description":"Task input for the sub-agent"}},"required":["agentId","input"]}"""
        ),
        ["send_email"] = new AgentToolDefinition(
            "send_email",
            "Send an email to a recipient with subject and body.",
            """{"type":"object","properties":{"to":{"type":"string","description":"Recipient email address"},"subject":{"type":"string","description":"Email subject line"},"body":{"type":"string","description":"Email body (plain text or HTML)"}},"required":["to","subject","body"]}"""
        ),
        ["memory_read"] = new AgentToolDefinition(
            "memory_read",
            "Read stored memory values for the current user. Optionally filter by key prefix.",
            """{"type":"object","properties":{"key":{"type":"string","description":"Optional key or prefix to filter (leave empty for all)"}}}"""
        ),
        ["memory_write"] = new AgentToolDefinition(
            "memory_write",
            "Store or update a persistent memory key-value pair for the user.",
            """{"type":"object","properties":{"key":{"type":"string","description":"Memory key"},"value":{"type":"string","description":"Value to store"}},"required":["key","value"]}"""
        ),
        ["knowledge_lookup"] = new AgentToolDefinition(
            "knowledge_lookup",
            "Search configured knowledge sources and attached context for relevant information.",
            """{"type":"object","properties":{"query":{"type":"string","description":"What to look up in configured knowledge sources"}},"required":["query"]}"""
        ),
        ["microsoft_teams_message"] = new AgentToolDefinition(
            "microsoft_teams_message",
            "Prepare or simulate a Microsoft Teams message action using configured integration actions.",
            """{"type":"object","properties":{"channel":{"type":"string"},"message":{"type":"string"}},"required":["channel","message"]}"""
        ),
        ["microsoft_outlook_draft"] = new AgentToolDefinition(
            "microsoft_outlook_draft",
            "Prepare an Outlook draft email action with recipient, subject, and body.",
            """{"type":"object","properties":{"to":{"type":"string"},"subject":{"type":"string"},"body":{"type":"string"}},"required":["to","subject","body"]}"""
        ),
        ["microsoft_excel_write"] = new AgentToolDefinition(
            "microsoft_excel_write",
            "Prepare an Excel table update action using configured workbook metadata.",
            """{"type":"object","properties":{"workbook":{"type":"string"},"table":{"type":"string"},"rowJson":{"type":"string"}},"required":["workbook","table","rowJson"]}"""
        ),
        ["microsoft_sharepoint_search"] = new AgentToolDefinition(
            "microsoft_sharepoint_search",
            "Search configured SharePoint knowledge sources for matching content.",
            """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}"""
        ),
        ["power_automate_flow"] = new AgentToolDefinition(
            "power_automate_flow",
            "Prepare a Power Automate flow trigger using configured flow metadata and payload.",
            """{"type":"object","properties":{"flowName":{"type":"string"},"payloadJson":{"type":"string"}},"required":["flowName","payloadJson"]}"""
        ),
    };

    private static readonly Regex EmailRegex = new(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new(@"\+?\d[\d\s\-()]{7,}\d", RegexOptions.Compiled);

    public AgentRunnerService(
        IAIService aiService,
        WebSearchService webSearchService,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<AgentRunnerService> logger,
        PromptDbContext db,
        ChainExecutionService chainService,
        IEmailService emailService)
    {
        _aiService = aiService;
        _webSearchService = webSearchService;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
        _db = db;
        _chainService = chainService;
        _emailService = emailService;
    }

    public bool IsServiceAvailable => _aiService.IsAvailable;

    public Task<AgentLoopResult> RunAsync(
        Agent agent,
        string input,
        string userId = "",
        string? userApiKey = null,
        string? userModel = null,
        Action<AgentRunStep>? onStep = null,
        CancellationToken ct = default,
        IAIService? serviceOverride = null)
    {
        var guardrails = ParseObject(agent.GuardrailsJson);
        var governance = ParseObject(agent.GovernanceJson);

        var blockedTopics = ReadStringArray(guardrails, "blockedTopics");
        if (blockedTopics.Any(topic => input.Contains(topic, StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult(new AgentLoopResult(
                $"Request blocked by agent guardrails. Matched blocked topic: {blockedTopics.First(topic => input.Contains(topic, StringComparison.OrdinalIgnoreCase))}",
                new List<AgentRunStep> { new("error", "Blocked by configured topic guardrails.") },
                0,
                0));
        }

        var piiPolicy = ReadString(governance, "piiPolicy") ?? ReadString(guardrails, "piiPolicy");
        if (string.Equals(piiPolicy, "block", StringComparison.OrdinalIgnoreCase) && ContainsSensitiveData(input))
        {
            return Task.FromResult(new AgentLoopResult(
                "Request blocked because PII detection is enabled for this agent.",
                new List<AgentRunStep> { new("error", "Input appears to contain sensitive data and the current policy is block.") },
                0,
                0));
        }

        var enabledTools = JsonSerializer.Deserialize<List<string>>(
            string.IsNullOrWhiteSpace(agent.EnabledTools) ? "[]" : agent.EnabledTools) ?? [];

        var toolPermissions = JsonSerializer.Deserialize<Dictionary<string, string>>(
            string.IsNullOrWhiteSpace(agent.ToolPermissions) ? "{}" : agent.ToolPermissions) ?? new();

        var toolDefs = enabledTools
            .Select(name => AvailableTools.TryGetValue(name, out var def) ? def : null)
            .OfType<AgentToolDefinition>()
            .ToList();

        var modelOverride = userModel ?? agent.Model;

        IAIService service = serviceOverride ?? _aiService;
        if (serviceOverride == null && !string.IsNullOrWhiteSpace(userApiKey))
        {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenAI:ApiKey"] = userApiKey,
                    ["OpenAI:DefaultModel"] = modelOverride ?? "gpt-4o"
                }).Build();
            service = new OpenAIService(cfg, NullLogger<OpenAIService>.Instance);
        }

        var routedModels = ReadStringArray(ParseObject(agent.ModelRoutingJson), "models");
        var routingMode = ReadString(ParseObject(agent.ModelRoutingJson), "mode");
        var benchmarkEnabled = string.Equals(routingMode, "benchmark", StringComparison.OrdinalIgnoreCase) || ReadBool(ParseObject(agent.BenchmarkConfigJson), "enabled");

        if (benchmarkEnabled && routedModels.Count > 1)
        {
            return RunBenchmarkedAsync(agent, input, userId, toolPermissions, toolDefs, routedModels, userApiKey, onStep, ct, serviceOverride);
        }

        return ExecuteSingleModelAsync(service, agent, input, userId, toolPermissions, toolDefs, modelOverride, onStep, ct);
    }

    private async Task<AgentLoopResult> RunBenchmarkedAsync(
        Agent agent,
        string input,
        string userId,
        Dictionary<string, string> toolPermissions,
        List<AgentToolDefinition> toolDefs,
        List<string> routedModels,
        string? userApiKey,
        Action<AgentRunStep>? onStep,
        CancellationToken ct,
        IAIService? serviceOverride)
    {
        var steps = new List<AgentRunStep>();
        var outputs = new List<string>();
        var totalTokens = 0;
        var maxIterations = 0;

        foreach (var model in routedModels.Distinct())
        {
            var service = serviceOverride ?? _aiService;
            if (serviceOverride == null && !string.IsNullOrWhiteSpace(userApiKey))
            {
                var cfg = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["OpenAI:ApiKey"] = userApiKey,
                        ["OpenAI:DefaultModel"] = model
                    }).Build();
                service = new OpenAIService(cfg, NullLogger<OpenAIService>.Instance);
            }

            onStep?.Invoke(new AgentRunStep("thinking", $"Benchmarking model {model}"));
            var result = await ExecuteSingleModelAsync(service, agent, input, userId, toolPermissions, toolDefs, model, null, ct);
            outputs.Add($"## {model}\n{result.FinalOutput}");
            steps.AddRange(result.Steps);
            totalTokens += result.TokensUsed;
            maxIterations = Math.Max(maxIterations, result.IterationsUsed);
        }

        return new AgentLoopResult(string.Join("\n\n", outputs), steps, totalTokens, maxIterations);
    }

    private async Task<AgentLoopResult> ExecuteSingleModelAsync(
        IAIService service,
        Agent agent,
        string input,
        string userId,
        Dictionary<string, string> toolPermissions,
        List<AgentToolDefinition> toolDefs,
        string? modelOverride,
        Action<AgentRunStep>? onStep,
        CancellationToken ct)
    {
        var result = await service.RunWithToolsAsync(
            agent.Instructions,
            input,
            toolDefs,
            call => ExecuteToolAsync(agent, call, userId, toolPermissions, ct),
            onStep,
            agent.MaxIterations,
            modelOverride,
            ct);

        var governance = ParseObject(agent.GovernanceJson);
        if (ReadBool(governance, "citationRequired") &&
            !result.FinalOutput.Contains("source", StringComparison.OrdinalIgnoreCase) &&
            !result.FinalOutput.Contains("citation", StringComparison.OrdinalIgnoreCase))
        {
            return result with { FinalOutput = result.FinalOutput + "\n\nSources: Required by policy. Add citations before external use." };
        }

        return result;
    }

    private async Task<string> ExecuteToolAsync(
        Agent agent,
        AgentToolCall call,
        string userId,
        Dictionary<string, string> toolPermissions,
        CancellationToken ct)
    {
        var permission = toolPermissions.TryGetValue(call.ToolName, out var p) ? p : "always_allow";
        if (permission == "deny")
            return $"[blocked] Tool '{call.ToolName}' is denied by agent settings.";

        var governance = ParseObject(agent.GovernanceJson);
        var approvalMode = ReadString(governance, "approvalMode");
        if (string.Equals(approvalMode, "manual", StringComparison.OrdinalIgnoreCase) && IsExternalActionTool(call.ToolName))
            return $"[approval-required] Tool '{call.ToolName}' requires human approval before execution.";

        try
        {
            return await DispatchToolAsync(agent, call, userId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool {Tool} failed", call.ToolName);
            return $"Tool error: {ex.Message}";
        }
    }

    private async Task<string> DispatchToolAsync(Agent agent, AgentToolCall call, string userId, CancellationToken ct)
    {
        switch (call.ToolName)
        {
            case "web_search":
            {
                var args = JsonDocument.Parse(call.ArgumentsJson).RootElement;
                var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? call.ArgumentsJson : call.ArgumentsJson;
                return await _webSearchService.SearchAsync(query);
            }

            case "http_request":
            {
                var args = JsonDocument.Parse(call.ArgumentsJson).RootElement;
                var url    = args.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var method = args.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
                var body   = args.TryGetProperty("body", out var b) ? b.GetString() : null;

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(15);

                HttpResponseMessage resp;
                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && body != null)
                    resp = await client.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct);
                else
                    resp = await client.GetAsync(url, ct);

                var content = await resp.Content.ReadAsStringAsync(ct);
                return content[..Math.Min(3000, content.Length)];
            }

            case "run_prompt":
            {
                var args = JsonDocument.Parse(call.ArgumentsJson).RootElement;
                var promptId = args.TryGetProperty("promptId", out var pid) ? pid.GetString() ?? "" : "";
                var input    = args.TryGetProperty("input", out var inp) ? inp.GetString() ?? "" : "";

                var prompt = await _db.Prompts.FindAsync(new object[] { promptId }, ct);
                if (prompt == null) return $"Prompt '{promptId}' not found.";

                var result = await _aiService.TestPromptAsync(new PromptTestRequest
                {
                    PromptContent = prompt.Content,
                    UserInput = input
                });
                return result.Result;
            }

            case "run_chain":
            {
                var args = JsonDocument.Parse(call.ArgumentsJson).RootElement;
                var chainIdValue = args.TryGetProperty("chainId", out var cid) ? cid.GetString() : null;
                var input   = args.TryGetProperty("input", out var inp) ? inp.GetString() ?? "" : "";

                if (!Guid.TryParse(chainIdValue, out var chainId)) return "Invalid chainId.";
                var chainResult = await _chainService.ExecuteChainAsync(chainId, input, userId);
                return chainResult.FinalOutput ?? "Chain produced no output.";
            }

            case "run_agent":
            {
                var args = JsonDocument.Parse(call.ArgumentsJson).RootElement;
                var agentId = args.TryGetProperty("agentId", out var aid) ? aid.GetInt32() : 0;
                var input   = args.TryGetProperty("input", out var inp) ? inp.GetString() ?? "" : "";

                var subAgent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == agentId && a.UserId == userId, ct);
                if (subAgent == null) return "Agent not found or access denied.";

                var subResult = await RunAsync(subAgent, input, userId, ct: ct);
                return subResult.FinalOutput ?? "Sub-agent produced no output.";
            }

            case "send_email":
            {
                var args    = JsonDocument.Parse(call.ArgumentsJson).RootElement;
                var to      = args.TryGetProperty("to", out var t) ? t.GetString() ?? "" : "";
                var subject = args.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "";
                var body    = args.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(to)) return "No recipient email provided.";
                await _emailService.SendEmailAsync(to, subject, $"<pre style='font-family:sans-serif'>{body}</pre>");
                return $"Email sent to {to}.";
            }

            case "memory_read":
            {
                var args = JsonDocument.Parse(call.ArgumentsJson).RootElement;
                var key  = args.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";

                var memories = string.IsNullOrWhiteSpace(key)
                    ? await _db.UserMemories.Where(m => m.UserId == userId).ToListAsync(ct)
                    : await _db.UserMemories.Where(m => m.UserId == userId && m.Key.StartsWith(key)).ToListAsync(ct);

                if (!memories.Any()) return "No memories found.";
                return string.Join("\n", memories.Select(m => $"{m.Key}: {m.Value}"));
            }

            case "memory_write":
            {
                var args  = JsonDocument.Parse(call.ArgumentsJson).RootElement;
                var key   = args.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                var value = args.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(key)) return "Key is required.";

                var existing = await _db.UserMemories
                    .FirstOrDefaultAsync(m => m.UserId == userId && m.Key == key, ct);

                if (existing != null)
                {
                    existing.Value = value;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    _db.UserMemories.Add(new UserMemory
                    {
                        UserId = userId, Key = key, Value = value,
                        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                    });
                }
                await _db.SaveChangesAsync(ct);
                return $"Memory '{key}' saved.";
            }

            case "knowledge_lookup":
            case "microsoft_sharepoint_search":
            {
                var args = JsonDocument.Parse(call.ArgumentsJson).RootElement;
                var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? string.Empty : string.Empty;
                var sources = JsonSerializer.Deserialize<List<KnowledgeSourceDefinition>>(string.IsNullOrWhiteSpace(agent.KnowledgeSourcesJson) ? "[]" : agent.KnowledgeSourcesJson) ?? [];
                var matches = sources
                    .Where(s =>
                        (!string.IsNullOrWhiteSpace(s.Name) && s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(s.Content) && s.Content.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                        (!string.IsNullOrWhiteSpace(s.Url) && s.Url.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .Take(5)
                    .Select(s => $"[{s.Type}] {s.Name}: {(!string.IsNullOrWhiteSpace(s.Content) ? s.Content : s.Url)}")
                    .ToList();

                if (!matches.Any()) return "No matching knowledge source entries found.";
                return string.Join("\n\n", matches);
            }

            case "microsoft_teams_message":
            case "microsoft_outlook_draft":
            case "microsoft_excel_write":
            case "power_automate_flow":
            {
                var actions = JsonSerializer.Deserialize<List<IntegrationActionDefinition>>(string.IsNullOrWhiteSpace(agent.IntegrationActionsJson) ? "[]" : agent.IntegrationActionsJson) ?? [];
                var payload = JsonDocument.Parse(call.ArgumentsJson).RootElement.ToString();
                var matching = actions.FirstOrDefault(a => string.Equals(a.ToolName, call.ToolName, StringComparison.OrdinalIgnoreCase));
                var label = matching?.DisplayName ?? call.ToolName;
                return $"Prepared integration action '{label}' with payload: {payload}";
            }

            default:
                return $"Unknown tool: {call.ToolName}";
        }
    }

    private static bool ContainsSensitiveData(string input) =>
        EmailRegex.IsMatch(input) || PhoneRegex.IsMatch(input);

    private static bool IsExternalActionTool(string toolName) => toolName is
        "send_email" or
        "microsoft_teams_message" or
        "microsoft_outlook_draft" or
        "microsoft_excel_write" or
        "power_automate_flow";

    private static JsonElement? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return null; }
    }

    private static List<string> ReadStringArray(JsonElement? root, string property)
    {
        if (root == null || !root.Value.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? string.Empty)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
    }

    private static string? ReadString(JsonElement? root, string property)
    {
        if (root == null || !root.Value.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static bool ReadBool(JsonElement? root, string property)
    {
        if (root == null || !root.Value.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
            return false;
        return value.GetBoolean();
    }
}

public sealed class KnowledgeSourceDefinition
{
    public string Type { get; set; } = "text";
    public string Name { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string? Content { get; set; }
}

public sealed class IntegrationActionDefinition
{
    public string ToolName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Target { get; set; }
    public string? MetadataJson { get; set; }
}
