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
public class SwarmController : ControllerBase
{
    private readonly SwarmService _swarmService;
    private readonly PromptDbContext _context;
    private readonly ILogger<SwarmController> _logger;

    public SwarmController(SwarmService swarmService, PromptDbContext context, ILogger<SwarmController> logger)
    {
        _swarmService = swarmService;
        _context = context;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    [HttpPost("generate")]
    public async Task<ActionResult> Generate([FromBody] SwarmGenerateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Intent))
            return BadRequest("Intent is required");

        // Resolve BYOK
        var userId = GetUserId();
        var userSettings = await _context.UserAiSettings.FirstOrDefaultAsync(s => s.UserId == userId);
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

        try
        {
            var result = await _swarmService.GenerateAsync(
                req.Intent,
                req.SelectedGoals ?? new List<string>(),
                req.AdditionalContext,
                req.AgentCount ?? 5,
                apiKey,
                model,
                req.UseWebSearch,
                serviceOverride,
                req.Perspectives,
                req.FileContext);

            // Save as execution record
            await _context.PromptExecutions.AddAsync(new PromptExecution
            {
                UserId = userId,
                PromptName = $"Swarm: {req.Intent[..Math.Min(req.Intent.Length, 50)]}",
                SystemPrompt = "Swarm generation",
                Model = model ?? "gpt-4o",
                MessagesJson = JsonSerializer.Serialize(new { intent = req.Intent, goals = req.SelectedGoals }),
                TotalTokensUsed = 0
            });
            await _context.SaveChangesAsync();

            return Ok(new
            {
                agents = result.Agents.Select(a => new
                {
                    a.Perspective,
                    a.Prompt,
                    a.ConfidenceScore
                }),
                consensusPrompt = result.ConsensusPrompt,
                averageConfidence = result.AverageConfidence
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Swarm generation failed");
            return StatusCode(500, new { message = "Swarm generation failed. Please try again." });
        }
    }
}

public class SwarmGenerateRequest
{
    public required string Intent { get; set; }
    public List<string>? SelectedGoals { get; set; }
    public List<string>? Perspectives { get; set; }
    public string? AdditionalContext { get; set; }
    public string? FileContext { get; set; }
    public int? AgentCount { get; set; }
    public string? UserApiKey { get; set; }
    public string? ModelId { get; set; }
    public bool UseWebSearch { get; set; } = true;
}
