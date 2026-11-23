using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Services;
using System.Security.Claims;

namespace PromptStudio.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/assist")]
public class AssistController : ControllerBase
{
    private readonly IAIService _aiService;
    private readonly PromptDbContext _db;
    private readonly ILogger<AssistController> _logger;

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    public AssistController(IAIService aiService, PromptDbContext db, ILogger<AssistController> logger)
    {
        _aiService = aiService;
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Gen-Prompt Assist endpoint — takes a user's intent + optional screenshot,
    /// runs the full intent-driven pipeline (vision → goals → prompt), and returns
    /// an optimized prompt ready for OpenClaw to execute.
    /// </summary>
    [HttpPost("generate")]
    public async Task<ActionResult<AssistGenerateResponse>> Generate([FromBody] AssistGenerateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Intent))
            return BadRequest(new { error = "Intent is required" });

        try
        {
            // 0. Load agent definition if provided — its instructions shape everything
            string? agentInstructions = null;
            string? agentName = null;
            if (req.AgentId.HasValue)
            {
                var agent = await _db.Agents.FirstOrDefaultAsync(
                    a => a.Id == req.AgentId.Value && a.UserId == UserId);
                if (agent != null)
                {
                    agentInstructions = agent.Instructions;
                    agentName = agent.Name;
                }
            }

            // 1. Vision: understand what's on screen
            var screenContext = string.Empty;
            if (!string.IsNullOrWhiteSpace(req.Screenshot))
            {
                screenContext = await _aiService.DescribeScreenAsync(req.Screenshot);
                _logger.LogDebug("Screen described: {Chars} chars", screenContext.Length);
            }

            // 2. Build enhanced intent — layers agent context + screen context on top
            var intentParts = new List<string> { req.Intent };
            if (!string.IsNullOrWhiteSpace(agentName))
                intentParts.Add($"Agent role: {agentName}");
            if (!string.IsNullOrWhiteSpace(screenContext))
                intentParts.Add($"Screen context: {screenContext}");
            var enhancedIntent = string.Join("\n\n", intentParts);

            // 3. Generate goal suggestions — seeded by agent instructions if present
            var suggestions = await _aiService.GenerateGoalSuggestionsAsync(enhancedIntent, "intent");
            var selectedGoals = suggestions.Goals.Take(3).ToList();

            // 4. Additional context: agent instructions + screen description
            var contextParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(agentInstructions))
                contextParts.Add($"Agent instructions: {agentInstructions}");
            if (!string.IsNullOrWhiteSpace(screenContext))
                contextParts.Add($"The user is currently looking at: {screenContext}");
            var additionalContext = contextParts.Count > 0 ? string.Join("\n\n", contextParts) : null;

            // 5. Generate the optimized prompt with agent persona baked in
            var prompt = string.IsNullOrWhiteSpace(agentInstructions)
                ? await _aiService.GeneratePromptFromGoalsAsync(enhancedIntent, selectedGoals, additionalContext)
                : await _aiService.GeneratePromptWithPersonaAsync(enhancedIntent, selectedGoals, additionalContext, agentInstructions);

            return Ok(new AssistGenerateResponse
            {
                Prompt = prompt,
                ScreenContext = screenContext,
                Goals = selectedGoals,
                AgentName = agentName,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Assist generate failed for intent: {Intent}", req.Intent);
            return StatusCode(500, new { error = "Failed to generate assist prompt" });
        }
    }
}

public class AssistGenerateRequest
{
    public required string Intent { get; set; }
    public string? Screenshot { get; set; }  // base64 data URL, never persisted
    public int? AgentId { get; set; }
}

public class AssistGenerateResponse
{
    public required string Prompt { get; set; }
    public string ScreenContext { get; set; } = string.Empty;
    public List<string> Goals { get; set; } = [];
    public string? AgentName { get; set; }
}
