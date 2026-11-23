using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace AgentHub.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("ai-generation")]
public class CoordinatorController : ControllerBase
{
    private readonly AgentRunnerService _runner;
    private readonly PromptDbContext _db;
    private readonly ILogger<CoordinatorController> _logger;

    public CoordinatorController(AgentRunnerService runner, PromptDbContext db, ILogger<CoordinatorController> logger)
    {
        _runner = runner;
        _db = db;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    // POST /api/coordinator/run — SSE streaming
    [HttpPost("run")]
    public async Task Run([FromBody] CoordinatorRunRequest req, CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        async Task Send(object data)
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(req.Question))
        {
            await Send(new { type = "error", content = "Question is required" });
            return;
        }

        var userSettings = await _db.UserAiSettings.FirstOrDefaultAsync(s => s.UserId == UserId, ct);
        var apiKey = !string.IsNullOrWhiteSpace(req.UserApiKey) ? req.UserApiKey
            : !string.IsNullOrWhiteSpace(userSettings?.OpenAiKey) ? userSettings!.OpenAiKey
            : null;
        var model = req.ModelId ?? userSettings?.ModelId;

        IAIService? serviceOverride = null;
        if (!string.IsNullOrWhiteSpace(userSettings?.AzureOpenAiEndpoint) &&
            !string.IsNullOrWhiteSpace(userSettings?.AzureOpenAiKey))
        {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureOpenAI:Endpoint"] = userSettings.AzureOpenAiEndpoint,
                    ["AzureOpenAI:ApiKey"] = userSettings.AzureOpenAiKey,
                    ["AzureOpenAI:DeploymentName"] = userSettings.AzureOpenAiDeployment ?? model ?? "gpt-4o",
                }).Build();
            serviceOverride = new AzureOpenAIService(cfg, NullLogger<AzureOpenAIService>.Instance);
        }

        if (string.IsNullOrWhiteSpace(apiKey) && !_runner.IsServiceAvailable)
        {
            await Send(new { type = "error", content = "No AI provider configured. Please add an OpenAI API key in Settings → AI Provider." });
            return;
        }

        // Build an ephemeral coordinator agent
        var enabledTools = req.PermissionMode == "none" ? "[]" : """["web_search"]""";
        var workerCount = Math.Clamp(req.WorkerCount ?? 3, 1, 10);
        var perspectivesNote = (req.Perspectives != null && req.Perspectives.Count > 0)
            ? $"\n\nApproach this from these specific perspectives: {string.Join(", ", req.Perspectives)}."
            : string.Empty;

        var agent = new Agent
        {
            UserId = UserId,
            Name = "Coordinator",
            Instructions = $"""
                You are a master research coordinator. When given a complex question:
                1. Break the question into {workerCount} focused sub-questions, one per worker
                2. Use web_search to research each sub-question thoroughly
                3. Synthesize the findings into a comprehensive, well-structured answer with citations
                4. Highlight key insights and any conflicting information found
                {perspectivesNote}
                Be thorough, cite sources where possible, and provide actionable conclusions.
                """,
            Model = model ?? "gpt-4o",
            EnabledTools = enabledTools,
            MaxIterations = workerCount * 3,
            Temperature = 0.5f
        };

        void OnStep(AgentRunStep step)
        {
            Send(new { type = step.Type, content = step.Content, toolName = step.ToolName, toolInput = step.ToolInput, toolOutput = step.ToolOutput })
                .GetAwaiter().GetResult();
        }

        try
        {
            var result = await _runner.RunAsync(agent, req.Question, UserId, apiKey, model, OnStep, ct, serviceOverride);
            await Send(new { type = "done", tokensUsed = result.TokensUsed, iterations = result.IterationsUsed });
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Coordinator run failed");
            await Send(new { type = "error", content = "Coordinator failed. Please try again." });
        }
    }
}

public class CoordinatorRunRequest
{
    public required string Question { get; set; }
    public string PermissionMode { get; set; } = "auto";
    public int? WorkerCount { get; set; }
    public List<string>? Perspectives { get; set; }
    public string? UserApiKey { get; set; }
    public string? ModelId { get; set; }
}
