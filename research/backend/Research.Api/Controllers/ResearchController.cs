using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Research.Api.Data;
using Research.Api.Models;
using Research.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace Research.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("ai-generation")]
public class ResearchController : ControllerBase
{
    private readonly SwarmService _swarmService;
    private readonly PromptDbContext _db;
    private readonly ILogger<ResearchController> _logger;
    private readonly IAIService _aiService;

    public ResearchController(SwarmService swarmService, PromptDbContext db, ILogger<ResearchController> logger, IAIService aiService)
    {
        _swarmService = swarmService;
        _db = db;
        _logger = logger;
        _aiService = aiService;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    // POST /api/research/run — SSE streaming scenario research across time horizons
    [HttpPost("run")]
    public async Task Run([FromBody] ResearchRunRequest req, CancellationToken ct)
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

        if (string.IsNullOrWhiteSpace(req.Scenario))
        {
            await Send(new { type = "error", content = "Scenario is required" });
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

        var horizons = (req.Horizons?.Count > 0) ? req.Horizons : ["3 months", "1 year", "3 years", "5 years"];
        var agentsPerHorizon = Math.Clamp(req.AgentsPerHorizon ?? 3, 1, 6);

        await Send(new { type = "start", horizons, agentsPerHorizon });

        try
        {
            var horizonConsensuses = new List<(string Horizon, string Consensus)>();

            // Run each horizon sequentially to avoid overwhelming the API
            int horizonIndex = 0;
            foreach (var horizon in horizons)
            {
                if (ct.IsCancellationRequested) break;

                await Send(new { type = "horizon_start", horizon, index = horizonIndex });

                // Run swarm for this horizon
                var horizonIntent = $"Scenario: {req.Scenario}\n\nTime horizon: {horizon}\n\nAnalyse how this scenario will unfold over {horizon}. Provide specific predictions, probabilities where possible, key drivers, and potential inflection points.";

                var result = await _swarmService.GenerateAsync(
                    horizonIntent,
                    new List<string>(),
                    null,
                    agentsPerHorizon,
                    apiKey,
                    model,
                    useWebSearch: req.Tools != "none",
                    serviceOverride: serviceOverride,
                    customPerspectives: req.Perspectives,
                    fileContext: req.FileContext,
                    outputMode: SwarmOutputMode.Analysis);

                // Emit each agent's result
                for (int i = 0; i < result.Agents.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    var agent = result.Agents[i];
                    await Send(new
                    {
                        type = "agent",
                        horizon,
                        horizonIndex,
                        agent = $"Agent {i + 1} ({agent.Perspective[..Math.Min(agent.Perspective.Length, 40)]}…)",
                        content = agent.Prompt,
                        confidence = agent.ConfidenceScore
                    });
                }

                await Send(new
                {
                    type = "horizon_done",
                    horizon,
                    consensus = result.ConsensusPrompt,
                    averageConfidence = result.AverageConfidence
                });

                horizonConsensuses.Add((horizon, result.ConsensusPrompt));
                horizonIndex++;
            }

            // Generate final summary across all horizons
            if (horizonConsensuses.Count > 0 && !ct.IsCancellationRequested)
            {
                var horizonText = string.Join("\n\n", horizonConsensuses.Select(h => $"## {h.Horizon}\n{h.Consensus}"));
                var service = serviceOverride ?? _aiService;
                var summary = await service.TestPromptAsync(new PromptTestRequest
                {
                    PromptContent = "You synthesise multi-horizon research findings into a concise overall summary. Write 5–7 bullet points covering the key themes across all horizons, overall trajectory, critical risks or inflection points, and the most actionable conclusions. Be direct and specific. Output ONLY the bullet list, no preamble.",
                    UserInput = $"Scenario: {req.Scenario}\n\nHorizon findings:\n{horizonText}"
                });
                await Send(new { type = "final_summary", content = summary.Result });
            }

            await Send(new { type = "done" });
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Research run failed for scenario: {Scenario}", req.Scenario);
            await Send(new { type = "error", content = "Research failed. Please try again." });
        }
    }
}

public class ResearchRunRequest
{
    public required string Scenario { get; set; }
    public List<string>? Horizons { get; set; }
    public int? AgentsPerHorizon { get; set; }
    public List<string>? Perspectives { get; set; }
    public string? FileContext { get; set; }
    public string Tools { get; set; } = "auto";
    public string? UserApiKey { get; set; }
    public string? ModelId { get; set; }
}
