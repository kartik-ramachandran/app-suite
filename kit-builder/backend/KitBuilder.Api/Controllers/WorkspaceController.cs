using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using KitBuilder.Api.Data;
using KitBuilder.Api.Services;
using System.Security.Claims;

namespace KitBuilder.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("ai-generation")]
public class WorkspaceController : ControllerBase
{
    private readonly WorkspaceService _workspaceService;
    private readonly PromptDbContext _db;
    private readonly ILogger<WorkspaceController> _logger;

    public WorkspaceController(WorkspaceService workspaceService, PromptDbContext db, ILogger<WorkspaceController> logger)
    {
        _workspaceService = workspaceService;
        _db = db;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    [HttpPost("generate")]
    public async Task<ActionResult> Generate([FromBody] WorkspaceGenerateRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Description))
            return BadRequest(new { message = "Description is required" });

        var settings = await _db.UserAiSettings.FirstOrDefaultAsync(s => s.UserId == UserId);

        var apiKey = !string.IsNullOrWhiteSpace(req.UserApiKey) ? req.UserApiKey
            : !string.IsNullOrWhiteSpace(settings?.OpenAiKey) ? settings!.OpenAiKey
            : null;
        var model = req.ModelId ?? settings?.ModelId;

        IAIService? serviceOverride = null;
        if (!string.IsNullOrWhiteSpace(settings?.AzureOpenAiEndpoint) &&
            !string.IsNullOrWhiteSpace(settings?.AzureOpenAiKey))
        {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureOpenAI:Endpoint"] = settings.AzureOpenAiEndpoint,
                    ["AzureOpenAI:ApiKey"] = settings.AzureOpenAiKey,
                    ["AzureOpenAI:DeploymentName"] = settings.AzureOpenAiDeployment ?? model ?? "gpt-4o",
                }).Build();
            serviceOverride = new AzureOpenAIService(cfg, NullLogger<AzureOpenAIService>.Instance);
        }

        try
        {
            var kit = await _workspaceService.GenerateAsync(req.Description, apiKey, model, serviceOverride);
            return Ok(new
            {
                personas = kit.Personas.Select(p => new { p.Name, p.Description, p.SystemPrompt, p.Expertise, p.Tone, p.Category }),
                agents = kit.Agents.Select(a => new { a.Name, a.Description, a.Instructions, a.EnabledTools }),
                skills = kit.Skills.Select(s => new { s.Name, s.Description, s.Instruction, s.Category, s.Tags }),
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workspace generation failed");
            return StatusCode(500, new { message = "Generation failed. Please try again." });
        }
    }
}

public class WorkspaceGenerateRequest
{
    public required string Description { get; set; }
    public string? UserApiKey { get; set; }
    public string? ModelId { get; set; }
}
