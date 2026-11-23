using Microsoft.Extensions.Logging.Abstractions;
using KitBuilder.Api.Models;
using System.Text.Json;

namespace KitBuilder.Api.Services;

public record CompanyRole(string Title, string Description, string Scope, string? SystemPrompt = null);
public record CompanyTask(string RoleTitle, string Task, string Rationale);
public record AgentOutput(string RoleTitle, string Output);
public record AgentRunResult(string RoleTitle, string Output);

public class CompanyService
{
    private readonly IAIService _aiService;
    private readonly ILogger<CompanyService> _logger;
    private readonly IConfiguration _config;

    private static readonly JsonSerializerOptions _jsonOpts = new() { PropertyNameCaseInsensitive = true };

    public CompanyService(IAIService aiService, ILogger<CompanyService> logger, IConfiguration config)
    {
        _aiService = aiService;
        _logger = logger;
        _config = config;
    }

    public async Task<List<CompanyTask>> DecomposeAsync(
        string goal,
        List<CompanyRole> roles,
        string? userApiKey = null,
        string? userModel = null,
        IAIService? serviceOverride = null)
    {
        var service = GetService(userApiKey, userModel, serviceOverride);

        var rolesText = string.Join("\n", roles.Select(r =>
            $"- {r.Title}: {r.Description} (Scope: {r.Scope})"));

        var systemPrompt =
            "You are a strategic org chart coordinator. Given a company goal and team roles, " +
            "break the goal into one specific, actionable task per role. " +
            "Return ONLY valid JSON in exactly this format with no markdown fences, no extra text:\n" +
            "{\"tasks\":[{\"roleTitle\":\"CEO\",\"task\":\"specific task\",\"rationale\":\"why this role\"}]}";

        var userMsg = $"Goal: {goal}\n\nTeam roles:\n{rolesText}";

        var response = await service.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = systemPrompt,
            UserInput = userMsg
        });

        var raw = response.Result.Trim();
        // Strip markdown code fences if the model adds them
        if (raw.StartsWith("```"))
        {
            var lines = raw.Split('\n');
            raw = string.Join("\n", lines.Skip(1).TakeWhile(l => !l.StartsWith("```")));
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<DecomposeResponse>(raw, _jsonOpts);
            return parsed?.Tasks ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse decompose JSON: {Raw}", raw);
            // Fallback: generate a generic task per role
            return roles.Select(r => new CompanyTask(r.Title, $"Contribute your {r.Title} expertise toward: {goal}", "Fallback task")).ToList();
        }
    }

    public async Task<AgentRunResult> RunAgentAsync(
        string goal,
        CompanyRole role,
        string task,
        string? skillContext = null,
        string? userApiKey = null,
        string? userModel = null,
        IAIService? serviceOverride = null)
    {
        var service = GetService(userApiKey, userModel, serviceOverride);

        // Use the persona/agent's actual system prompt if provided; fall back to generic role prompt
        var systemPrompt = !string.IsNullOrWhiteSpace(role.SystemPrompt)
            ? role.SystemPrompt +
              $"\n\nIn this company run your scope is: {role.Scope}. " +
              "Analyse the goal from your perspective and produce a detailed, actionable output using clear markdown headings."
            : $"You are {role.Title}. {role.Description}. Your scope: {role.Scope}.\n\n" +
              "Analyse the company goal from your role's perspective and produce a detailed, actionable output. " +
              "Be specific, practical, and thorough. Use markdown with clear section headings.";

        var skillText = !string.IsNullOrWhiteSpace(skillContext)
            ? $"\n\nAdditional skill context to apply:\n{skillContext}"
            : "";

        var userMsg = $"Company goal: {goal}\n\nYour specific task: {task}{skillText}";

        var response = await service.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = systemPrompt,
            UserInput = userMsg
        });

        return new AgentRunResult(role.Title, response.Result.Trim());
    }

    public async Task<string> SynthesizeAsync(
        string goal,
        List<AgentOutput> outputs,
        string? userApiKey = null,
        string? userModel = null,
        IAIService? serviceOverride = null)
    {
        var service = GetService(userApiKey, userModel, serviceOverride);

        var outputsText = string.Join("\n\n", outputs.Select(o =>
            $"## {o.RoleTitle}\n{o.Output}"));

        var systemPrompt =
            "You are a strategic synthesizer combining a team's individual outputs into one cohesive plan. " +
            "Produce an executive strategy document with: an Executive Summary, Key Actions by Department, " +
            "Timeline (short/mid/long term), Dependencies & Risks, and Success Metrics. " +
            "Use clear markdown headings. Be concise but comprehensive.";

        var userMsg = $"Company goal: {goal}\n\nTeam outputs:\n{outputsText}";

        var response = await service.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = systemPrompt,
            UserInput = userMsg
        });

        return response.Result.Trim();
    }

    private IAIService GetService(string? userApiKey, string? userModel, IAIService? serviceOverride)
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
            return new OpenAIService(cfg, NullLogger<OpenAIService>.Instance);
        }
        return _aiService;
    }
}

internal class DecomposeResponse
{
    public List<CompanyTask> Tasks { get; set; } = [];
}
