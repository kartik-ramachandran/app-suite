using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using System.Text.Json;

namespace AgentHub.Api.Controllers;

// Agent-to-Agent (A2A) Protocol support — exposes GenPrompt as an agent card
[ApiController]
public class A2AController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly IAIService _aiService;
    private readonly ChainExecutionService _chainService;
    private readonly IConfiguration _config;

    private readonly AgentRunnerService _agentRunner;

    public A2AController(PromptDbContext context, IAIService aiService, ChainExecutionService chainService, IConfiguration config, AgentRunnerService agentRunner)
    {
        _context = context;
        _aiService = aiService;
        _chainService = chainService;
        _config = config;
        _agentRunner = agentRunner;
    }

    // GET /.well-known/agent.json — Agent card for A2A discovery
    [HttpGet("/.well-known/agent.json")]
    public ActionResult GetAgentCard()
    {
        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        return Ok(new
        {
            name = "GenPrompt",
            description = "AI prompt generation, management, and workflow execution platform.",
            url = baseUrl,
            version = "1.0",
            capabilities = new { streaming = false, pushNotifications = false },
            defaultInputModes = new[] { "text" },
            defaultOutputModes = new[] { "text" },
            skills = new[]
            {
                new { id = "generate-prompt", name = "Generate Prompt", description = "Generate a production-ready AI prompt from a natural language intent.", tags = new[] { "prompt", "ai", "generation" } },
                new { id = "test-prompt", name = "Test Prompt", description = "Run a prompt through AI and return the result.", tags = new[] { "prompt", "execution" } },
                new { id = "execute-chain", name = "Execute Chain", description = "Execute a saved workflow chain with the provided input.", tags = new[] { "chain", "workflow", "automation" } },
                new { id = "swarm-generate", name = "Swarm Generate", description = "Generate a prompt using multi-agent consensus synthesis.", tags = new[] { "swarm", "consensus", "prompt" } },
                new { id = "run-agent", name = "Run Agent", description = "Run a specific installed agent by its ID with an input.", tags = new[] { "agent", "automation", "tool" } }
            },
            authentication = new
            {
                schemes = new[] { "ApiKey" },
                credentials = new { apiKeyHeader = "X-GenPrompt-Header" }
            }
        });
    }

    // POST /api/a2a/tasks — A2A task submission (requires API key auth)
    [HttpPost("/api/a2a/tasks")]
    public async Task<ActionResult> SendTask([FromBody] A2ATaskRequest req)
    {
        // API key auth via header
        var apiKeyHeader = Request.Headers["X-GenPrompt-Header"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(apiKeyHeader))
            return Unauthorized(new { error = "API key required in X-GenPrompt-Header" });

        // Find matching API key
        var keys = await _context.ApiKeys.Where(k => k.IsActive).ToListAsync();
        var matchedKey = keys.FirstOrDefault(k => BCrypt.Net.BCrypt.Verify(apiKeyHeader, k.Key));
        if (matchedKey == null) return Unauthorized(new { error = "Invalid API key" });

        var userId = matchedKey.UserId;
        var input = req.Message?.Parts?.FirstOrDefault()?.Text ?? string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return BadRequest(new { error = "No input text provided" });

        // Update last used
        matchedKey.LastUsedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        try
        {
            string result;
            var skillId = req.SkillId ?? "generate-prompt";

            switch (skillId)
            {
                case "generate-prompt":
                    result = await _aiService.GeneratePromptFromGoalsAsync(input, new List<string>(), null);
                    break;

                case "test-prompt":
                    var testResp = await _aiService.TestPromptAsync(new PromptTestRequest { PromptContent = input });
                    result = testResp.Result;
                    break;

                case "execute-chain":
                    if (!Guid.TryParse(req.Parameters?.GetValueOrDefault("chainId"), out var chainId))
                        return BadRequest(new { error = "chainId parameter required for execute-chain skill" });
                    var chainResult = await _chainService.ExecuteChainAsync(chainId, input, userId);
                    result = chainResult.FinalOutput ?? "No output";
                    break;

                case "run-agent":
                    if (!int.TryParse(req.Parameters?.GetValueOrDefault("agentId"), out var agentId))
                        return BadRequest(new { error = "agentId parameter required for run-agent skill" });
                    var agent = await _context.Set<AgentHub.Api.Models.Agent>().FirstOrDefaultAsync(a => a.Id == agentId && a.UserId == userId);
                    if (agent == null) return NotFound(new { error = "Agent not found" });
                    var agentResult = await _agentRunner.RunAsync(agent, input);
                    result = agentResult.FinalOutput ?? "No output";
                    break;

                default:
                    result = await _aiService.GeneratePromptFromGoalsAsync(input, new List<string>(), null);
                    break;
            }

            return Ok(new
            {
                id = Guid.NewGuid().ToString(),
                status = new { state = "completed" },
                artifacts = new[]
                {
                    new { parts = new[] { new { type = "text", text = result } } }
                }
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

public class A2ATaskRequest
{
    public string? SkillId { get; set; }
    public A2AMessage? Message { get; set; }
    public Dictionary<string, string>? Parameters { get; set; }
}

public class A2AMessage
{
    public List<A2APart>? Parts { get; set; }
}

public class A2APart
{
    public string? Text { get; set; }
}
