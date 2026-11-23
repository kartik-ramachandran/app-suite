using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using KitBuilder.Api.Data;
using KitBuilder.Api.Models;
using KitBuilder.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace KitBuilder.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("ai-generation")]
public class CompanyController : ControllerBase
{
    private readonly CompanyService _companyService;
    private readonly PromptDbContext _db;
    private readonly ILogger<CompanyController> _logger;

    public CompanyController(CompanyService companyService, PromptDbContext db, ILogger<CompanyController> logger)
    {
        _companyService = companyService;
        _db = db;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private (string? apiKey, string? model, IAIService? serviceOverride) ResolveAI(string? reqApiKey, string? reqModel, UserAiSettings? settings)
    {
        var apiKey = !string.IsNullOrWhiteSpace(reqApiKey) ? reqApiKey
            : !string.IsNullOrWhiteSpace(settings?.OpenAiKey) ? settings!.OpenAiKey
            : null;
        var model = reqModel ?? settings?.ModelId;

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

        return (apiKey, model, serviceOverride);
    }

    // POST /api/company/decompose — break goal into per-role tasks
    [HttpPost("decompose")]
    public async Task<ActionResult> Decompose([FromBody] CompanyDecomposeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Goal))
            return BadRequest(new { message = "Goal is required" });
        if (req.Roles == null || req.Roles.Count == 0)
            return BadRequest(new { message = "At least one role is required" });

        var settings = await _db.UserAiSettings.FirstOrDefaultAsync(s => s.UserId == UserId);
        var (apiKey, model, serviceOverride) = ResolveAI(req.UserApiKey, req.ModelId, settings);

        try
        {
            var tasks = await _companyService.DecomposeAsync(
                req.Goal,
                req.Roles.Select(r => new CompanyRole(r.Title, r.Description, r.Scope)).ToList(),
                apiKey, model, serviceOverride);

            return Ok(new { tasks });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Decompose failed for goal: {Goal}", req.Goal);
            return StatusCode(500, new { message = "Failed to decompose goal. Please try again." });
        }
    }

    // POST /api/company/execute — SSE: run each agent and stream results
    [HttpPost("execute")]
    public async Task Execute([FromBody] CompanyExecuteRequest req, CancellationToken ct)
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

        if (string.IsNullOrWhiteSpace(req.Goal) || req.Roles == null || req.Tasks == null)
        {
            await Send(new { type = "error", message = "Invalid request" });
            return;
        }

        var settings = await _db.UserAiSettings.FirstOrDefaultAsync(s => s.UserId == UserId, ct);
        var (apiKey, model, serviceOverride) = ResolveAI(req.UserApiKey, req.ModelId, settings);

        var roleMap = req.Roles.ToDictionary(r => r.Title, r => r);

        try
        {
            foreach (var task in req.Tasks)
            {
                if (ct.IsCancellationRequested) break;
                if (!roleMap.TryGetValue(task.RoleTitle, out var roleDto)) continue;

                var role = new CompanyRole(roleDto.Title, roleDto.Description, roleDto.Scope, roleDto.SystemPrompt);

                await Send(new { type = "agent_start", roleTitle = task.RoleTitle });

                try
                {
                    var result = await _companyService.RunAgentAsync(req.Goal, role, task.Task, req.SkillContext, apiKey, model, serviceOverride);
                    await Send(new { type = "agent_done", roleTitle = task.RoleTitle, output = result.Output });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Agent failed for role {Role}", task.RoleTitle);
                    await Send(new { type = "agent_error", roleTitle = task.RoleTitle, message = "Agent failed" });
                }
            }

            await Send(new { type = "done" });
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Execute failed");
            await Send(new { type = "error", message = "Execution failed. Please try again." });
        }
    }

    // POST /api/company/synthesize — combine all agent outputs into final strategy
    [HttpPost("synthesize")]
    public async Task<ActionResult> Synthesize([FromBody] CompanySynthesizeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Goal) || req.Outputs == null || req.Outputs.Count == 0)
            return BadRequest(new { message = "Goal and outputs are required" });

        var settings = await _db.UserAiSettings.FirstOrDefaultAsync(s => s.UserId == UserId);
        var (apiKey, model, serviceOverride) = ResolveAI(req.UserApiKey, req.ModelId, settings);

        try
        {
            var synthesis = await _companyService.SynthesizeAsync(
                req.Goal,
                req.Outputs.Select(o => new AgentOutput(o.RoleTitle, o.Output)).ToList(),
                apiKey, model, serviceOverride);

            return Ok(new { synthesis });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Synthesize failed");
            return StatusCode(500, new { message = "Synthesis failed. Please try again." });
        }
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public class RoleDto
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Scope { get; set; } = "";
    public string? SystemPrompt { get; set; }
}

public class TaskDto
{
    public string RoleTitle { get; set; } = "";
    public string Task { get; set; } = "";
    public string Rationale { get; set; } = "";
}

public class OutputDto
{
    public string RoleTitle { get; set; } = "";
    public string Output { get; set; } = "";
}

public class CompanyDecomposeRequest
{
    public required string Goal { get; set; }
    public List<RoleDto> Roles { get; set; } = [];
    public string? UserApiKey { get; set; }
    public string? ModelId { get; set; }
}

public class CompanyExecuteRequest
{
    public required string Goal { get; set; }
    public List<RoleDto> Roles { get; set; } = [];
    public List<TaskDto> Tasks { get; set; } = [];
    public string? SkillContext { get; set; }
    public string? UserApiKey { get; set; }
    public string? ModelId { get; set; }
}

public class CompanySynthesizeRequest
{
    public required string Goal { get; set; }
    public List<OutputDto> Outputs { get; set; } = [];
    public string? UserApiKey { get; set; }
    public string? ModelId { get; set; }
}
