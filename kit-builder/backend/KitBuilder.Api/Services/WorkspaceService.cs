using Microsoft.Extensions.Logging.Abstractions;
using KitBuilder.Api.Models;
using System.Text.Json;

namespace KitBuilder.Api.Services;

public record WorkspacePersona(string Name, string Description, string SystemPrompt, string Expertise, string Tone, string Category);
public record WorkspaceAgent(string Name, string Description, string Instructions, List<string> EnabledTools);
public record WorkspaceSkill(string Name, string Description, string Instruction, string Category, List<string> Tags);
public record WorkspaceKit(List<WorkspacePersona> Personas, List<WorkspaceAgent> Agents, List<WorkspaceSkill> Skills);

public class WorkspaceService
{
    private readonly IAIService _aiService;
    private readonly ILogger<WorkspaceService> _logger;

    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    public WorkspaceService(IAIService aiService, ILogger<WorkspaceService> logger)
    {
        _aiService = aiService;
        _logger = logger;
    }

    public async Task<WorkspaceKit> GenerateAsync(
        string description,
        string? userApiKey = null,
        string? userModel = null,
        IAIService? serviceOverride = null)
    {
        var service = GetService(userApiKey, userModel, serviceOverride);

        var systemPrompt = """
            You are an AI workspace architect. Given a user's use case or goal, design the ideal team kit of AI personas, agents, and skills.

            Rules:
            - Personas: 2–5 distinct AI roles with real personalities and expertise. Write full, production-ready systemPrompts.
            - Agents: 1–3 autonomous task-runners with detailed instructions. Only suggest enabledTools from: ["web_search", "http_request", "code_interpreter"].
            - Skills: 3–6 reusable prompt instructions (not roles, but techniques/capabilities). Write the actual instruction text, not a description of it.

            Return ONLY valid JSON with no markdown fences, no extra text:
            {
              "personas": [{"name":"...","description":"...","systemPrompt":"...","expertise":"...","tone":"...","category":"..."}],
              "agents": [{"name":"...","description":"...","instructions":"...","enabledTools":["web_search"]}],
              "skills": [{"name":"...","description":"...","instruction":"...","category":"...","tags":["..."]}]
            }

            Skill categories must be one of: marketing, engineering, support, legal, finance, hr, product, sales, data, education.
            Persona categories must be one of: Development, Writing, Marketing, Support, Education, Business, Creative, Data, Design, Sales, Legal, Health, Finance, Other.
            """;

        var response = await service.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = systemPrompt,
            UserInput = $"Use case: {description}"
        });

        var raw = response.Result.Trim();
        if (raw.StartsWith("```"))
            raw = string.Join("\n", raw.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));

        try
        {
            var parsed = JsonSerializer.Deserialize<WorkspaceKitRaw>(raw, _json)
                ?? throw new InvalidOperationException("Null result");

            return new WorkspaceKit(
                parsed.Personas?.Select(p => new WorkspacePersona(p.Name, p.Description, p.SystemPrompt, p.Expertise, p.Tone, p.Category)).ToList() ?? [],
                parsed.Agents?.Select(a => new WorkspaceAgent(a.Name, a.Description, a.Instructions, a.EnabledTools ?? [])).ToList() ?? [],
                parsed.Skills?.Select(s => new WorkspaceSkill(s.Name, s.Description, s.Instruction, s.Category, s.Tags ?? [])).ToList() ?? []
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse workspace kit JSON: {Raw}", raw[..Math.Min(raw.Length, 200)]);
            throw new InvalidOperationException("AI returned an unexpected format. Please try again.");
        }
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

// Raw deserialization models (property names match JSON)
internal class WorkspaceKitRaw
{
    public List<PersonaRaw>? Personas { get; set; }
    public List<AgentRaw>? Agents { get; set; }
    public List<SkillRaw>? Skills { get; set; }
}
internal class PersonaRaw { public string Name { get; set; } = ""; public string Description { get; set; } = ""; public string SystemPrompt { get; set; } = ""; public string Expertise { get; set; } = ""; public string Tone { get; set; } = ""; public string Category { get; set; } = ""; }
internal class AgentRaw { public string Name { get; set; } = ""; public string Description { get; set; } = ""; public string Instructions { get; set; } = ""; public List<string>? EnabledTools { get; set; } }
internal class SkillRaw { public string Name { get; set; } = ""; public string Description { get; set; } = ""; public string Instruction { get; set; } = ""; public string Category { get; set; } = ""; public List<string>? Tags { get; set; } }
