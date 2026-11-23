using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;

namespace AgentHub.Api.Controllers;

[ApiController]
[Route("api/agents")]
[Authorize]
public class AgentsController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly AgentRunnerService _runner;

    public AgentsController(PromptDbContext db, AgentRunnerService runner)
    {
        _db = db;
        _runner = runner;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private static object ToAgentDto(Agent a) => new
    {
        a.Id,
        a.Name,
        a.Description,
        a.Instructions,
        a.Model,
        a.EnabledTools,
        a.ToolPermissions,
        a.InputSchemaJson,
        a.MemoryConfigJson,
        a.GuardrailsJson,
        a.OutputSchemaJson,
        a.KnowledgeSourcesJson,
        a.IntegrationActionsJson,
        a.ModelRoutingJson,
        a.BenchmarkConfigJson,
        a.GovernanceJson,
        a.SourcePromptId,
        a.SourcePersonaId,
        a.FallbackBehavior,
        a.EscalationRules,
        a.Temperature,
        a.MaxIterations,
        a.Version,
        a.IsTemplate,
        a.TemplateCategory,
        a.IsPublic,
        a.CreatedAt,
        a.UpdatedAt
    };

    private static string BuildPersonaAgentInstructions(Persona persona)
    {
        var sections = new List<string> { persona.SystemPrompt };

        if (!string.IsNullOrWhiteSpace(persona.Expertise))
            sections.Add($"Expertise: {persona.Expertise}");
        if (!string.IsNullOrWhiteSpace(persona.CommunicationStyle))
            sections.Add($"Communication style: {persona.CommunicationStyle}");
        if (!string.IsNullOrWhiteSpace(persona.PersonalityTraits))
            sections.Add($"Traits: {persona.PersonalityTraits}");
        if (!string.IsNullOrWhiteSpace(persona.Constraints))
            sections.Add($"Constraints: {persona.Constraints}");
        if (!string.IsNullOrWhiteSpace(persona.TargetAudience))
            sections.Add($"Target audience: {persona.TargetAudience}");

        return string.Join("\n\n", sections.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    // GET /api/agents
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var agents = await _db.Agents
            .Where(a => a.UserId == UserId)
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync();
        return Ok(agents.Select(ToAgentDto));
    }

    // GET /api/agents/public
    [AllowAnonymous]
    [HttpGet("public")]
    public async Task<IActionResult> ListPublic([FromQuery] string? category = null, [FromQuery] string? search = null)
    {
        var query = _db.Agents
            .Include(a => a.User)
            .Where(a => a.IsPublic || a.IsTemplate);

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(a => a.TemplateCategory == category);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.ToLower();
            query = query.Where(a =>
                a.Name.ToLower().Contains(term) ||
                a.Description.ToLower().Contains(term) ||
                (a.TemplateCategory != null && a.TemplateCategory.ToLower().Contains(term)));
        }

        var agents = await query
            .OrderByDescending(a => a.IsTemplate)
            .ThenByDescending(a => a.UpdatedAt)
            .Take(100)
            .ToListAsync();

        var result = agents.Select(a => new
        {
            a.Id,
            a.Name,
            a.Description,
            a.Model,
            a.EnabledTools,
            a.KnowledgeSourcesJson,
            a.IntegrationActionsJson,
            a.TemplateCategory,
            a.IsTemplate,
            a.Version,
            a.CreatedAt,
            authorName = a.User?.Name ?? "GenPrompt",
            authorPicture = a.User?.ProfilePicture
        });

        return Ok(result);
    }

    // GET /api/agents/tools
    [HttpGet("tools")]
    public IActionResult GetAvailableTools()
    {
        var tools = AgentRunnerService.AvailableTools.Select(kv => new
        {
            name = kv.Key,
            description = kv.Value.Description,
            parametersSchema = kv.Value.ParametersSchema
        });
        return Ok(tools);
    }

    // GET /api/agents/{id}
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.UserId == UserId);
        if (agent == null) return NotFound();
        return Ok(ToAgentDto(agent));
    }

    // GET /api/agents/{id}/runs
    [HttpGet("{id:int}/runs")]
    public async Task<IActionResult> GetRuns(int id, int page = 1, int pageSize = 20)
    {
        var runs = await _db.AgentRuns
            .Where(r => r.AgentId == id && r.UserId == UserId)
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new { r.Id, r.Input, r.FinalOutput, r.Status, r.DurationMs, r.TokensUsed, r.Iterations, r.ModelUsed, r.EstimatedCostUsd, r.SafetyFlagsJson, r.ApprovalRequired, r.BenchmarkGroupId, r.CreatedAt })
            .ToListAsync();
        return Ok(runs);
    }

    // GET /api/agents/runs/{runId}
    [HttpGet("runs/{runId:int}")]
    public async Task<IActionResult> GetRun(int runId)
    {
        var run = await _db.AgentRuns.FirstOrDefaultAsync(r => r.Id == runId && r.UserId == UserId);
        if (run == null) return NotFound();
        return Ok(run);
    }

    // POST /api/agents
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] AgentUpsertRequest req)
    {
        var agent = new Agent
        {
            UserId = UserId,
            Name = req.Name,
            Description = req.Description ?? string.Empty,
            Instructions = req.Instructions,
            Model = req.Model,
            EnabledTools = req.EnabledTools ?? "[]",
            ToolPermissions = req.ToolPermissions ?? "{}",
            InputSchemaJson = req.InputSchemaJson ?? "[]",
            MemoryConfigJson = req.MemoryConfigJson ?? "{}",
            GuardrailsJson = req.GuardrailsJson ?? "{}",
            OutputSchemaJson = req.OutputSchemaJson ?? "{}",
            KnowledgeSourcesJson = req.KnowledgeSourcesJson ?? "[]",
            IntegrationActionsJson = req.IntegrationActionsJson ?? "[]",
            ModelRoutingJson = req.ModelRoutingJson ?? "{}",
            BenchmarkConfigJson = req.BenchmarkConfigJson ?? "{}",
            GovernanceJson = req.GovernanceJson ?? "{}",
            SourcePromptId = req.SourcePromptId,
            SourcePersonaId = req.SourcePersonaId,
            FallbackBehavior = req.FallbackBehavior ?? string.Empty,
            EscalationRules = req.EscalationRules ?? string.Empty,
            Temperature = req.Temperature,
            MaxIterations = Math.Clamp(req.MaxIterations, 1, 20),
            Version = Math.Max(req.Version ?? 1, 1),
            IsTemplate = req.IsTemplate,
            TemplateCategory = req.TemplateCategory,
            IsPublic = req.IsPublic
        };
        _db.Agents.Add(agent);
        await _db.SaveChangesAsync();
        return Ok(ToAgentDto(agent));
    }

    // PUT /api/agents/{id}
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] AgentUpsertRequest req)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.UserId == UserId);
        if (agent == null) return NotFound();

        agent.Name = req.Name;
        agent.Description = req.Description ?? agent.Description;
        agent.Instructions = req.Instructions;
        agent.Model = req.Model;
        agent.EnabledTools = req.EnabledTools ?? "[]";
        agent.ToolPermissions = req.ToolPermissions ?? "{}";
        agent.InputSchemaJson = req.InputSchemaJson ?? "[]";
        agent.MemoryConfigJson = req.MemoryConfigJson ?? "{}";
        agent.GuardrailsJson = req.GuardrailsJson ?? "{}";
        agent.OutputSchemaJson = req.OutputSchemaJson ?? "{}";
        agent.KnowledgeSourcesJson = req.KnowledgeSourcesJson ?? "[]";
        agent.IntegrationActionsJson = req.IntegrationActionsJson ?? "[]";
        agent.ModelRoutingJson = req.ModelRoutingJson ?? "{}";
        agent.BenchmarkConfigJson = req.BenchmarkConfigJson ?? "{}";
        agent.GovernanceJson = req.GovernanceJson ?? "{}";
        agent.SourcePromptId = req.SourcePromptId;
        agent.SourcePersonaId = req.SourcePersonaId;
        agent.FallbackBehavior = req.FallbackBehavior ?? string.Empty;
        agent.EscalationRules = req.EscalationRules ?? string.Empty;
        agent.Temperature = req.Temperature;
        agent.MaxIterations = Math.Clamp(req.MaxIterations, 1, 20);
        agent.Version = Math.Max(req.Version ?? agent.Version, 1);
        agent.IsTemplate = req.IsTemplate;
        agent.TemplateCategory = req.TemplateCategory;
        agent.IsPublic = req.IsPublic;
        agent.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(ToAgentDto(agent));
    }

    // POST /api/agents/from-prompt/{promptId}
    [HttpPost("from-prompt/{promptId}")]
    public async Task<IActionResult> CreateFromPrompt(string promptId)
    {
        var prompt = await _db.Prompts.FirstOrDefaultAsync(p => p.Id == promptId && p.UserId == UserId);
        if (prompt == null) return NotFound();

        var agent = new Agent
        {
            UserId = UserId,
            Name = $"{prompt.Name} Agent",
            Description = prompt.Description ?? $"Agent blueprint generated from prompt '{prompt.Name}'.",
            Instructions = prompt.Content,
            Model = "gpt-4o",
            EnabledTools = "[]",
            ToolPermissions = "{}",
            SourcePromptId = prompt.Id,
            InputSchemaJson = "[{\"name\":\"input\",\"type\":\"string\",\"required\":true}]",
            OutputSchemaJson = "{\"type\":\"string\"}",
            GovernanceJson = "{\"approvalMode\":\"none\",\"blockedTopics\":[],\"citationRequired\":false}",
            IsTemplate = true,
            TemplateCategory = prompt.Category,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Agents.Add(agent);
        await _db.SaveChangesAsync();
        return Ok(ToAgentDto(agent));
    }

    // POST /api/agents/from-persona/{personaId}
    [HttpPost("from-persona/{personaId:int}")]
    public async Task<IActionResult> CreateFromPersona(int personaId)
    {
        var persona = await _db.Personas.FirstOrDefaultAsync(p =>
            p.Id == personaId &&
            (p.UserId == UserId || p.IsTemplate || p.IsPublic));
        if (persona == null) return NotFound();

        var agent = new Agent
        {
            UserId = UserId,
            Name = $"{persona.Name} Agent",
            Description = persona.Description ?? $"Runnable agent converted from persona '{persona.Name}'.",
            Instructions = BuildPersonaAgentInstructions(persona),
            Model = "gpt-4o",
            EnabledTools = "[]",
            ToolPermissions = "{}",
            SourcePersonaId = persona.Id,
            InputSchemaJson = "[{\"name\":\"task\",\"type\":\"string\",\"required\":true}]",
            OutputSchemaJson = "{\"type\":\"string\"}",
            GovernanceJson = "{\"approvalMode\":\"none\",\"blockedTopics\":[],\"citationRequired\":false}",
            FallbackBehavior = "Ask for clarification when instructions are ambiguous.",
            EscalationRules = "Escalate when the request falls outside configured expertise or requires human approval.",
            IsTemplate = true,
            TemplateCategory = persona.Category,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Agents.Add(agent);
        await _db.SaveChangesAsync();
        return Ok(ToAgentDto(agent));
    }

    // DELETE /api/agents/{id}
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.UserId == UserId);
        if (agent == null) return NotFound();
        _db.Agents.Remove(agent);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // POST /api/agents/{id}/run — synchronous run, saves transcript
    [HttpPost("{id:int}/run")]
    public async Task<IActionResult> Run(int id, [FromBody] AgentRunRequest req)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.UserId == UserId);
        if (agent == null) return NotFound();

        var agentRun = new AgentRun { AgentId = id, UserId = UserId, Input = req.Input, Status = "running" };
        _db.AgentRuns.Add(agentRun);
        await _db.SaveChangesAsync();

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _runner.RunAsync(agent, req.Input, UserId, req.UserApiKey, req.ModelOverride);
            sw.Stop();
            agentRun.FinalOutput = result.FinalOutput;
            agentRun.StepsJson = JsonSerializer.Serialize(result.Steps);
            agentRun.Status = "completed";
            agentRun.DurationMs = (int)sw.ElapsedMilliseconds;
            agentRun.TokensUsed = result.TokensUsed;
            agentRun.Iterations = result.IterationsUsed;
            agentRun.ModelUsed = req.ModelOverride ?? agent.Model;
            agentRun.ApprovalRequired = agent.GovernanceJson.Contains("approvalMode", StringComparison.OrdinalIgnoreCase);
            await _db.SaveChangesAsync();
            return Ok(new { result.FinalOutput, result.TokensUsed, result.IterationsUsed, runId = agentRun.Id, agentRun.ModelUsed, agentRun.ApprovalRequired });
        }
        catch (Exception ex)
        {
            sw.Stop();
            agentRun.Status = "failed";
            agentRun.FinalOutput = ex.Message;
            agentRun.DurationMs = (int)sw.ElapsedMilliseconds;
            await _db.SaveChangesAsync();
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // POST /api/agents/{id}/run/stream — SSE streaming, emits steps in real time
    [HttpPost("{id:int}/run/stream")]
    public async Task RunStream(int id, [FromBody] AgentRunRequest req, CancellationToken ct)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.Id == id && a.UserId == UserId, ct);
        if (agent == null) { Response.StatusCode = 404; return; }

        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var agentRun = new AgentRun { AgentId = id, UserId = UserId, Input = req.Input, Status = "running" };
        _db.AgentRuns.Add(agentRun);
        await _db.SaveChangesAsync(ct);

        async Task Send(object data)
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await Response.WriteAsync($"data: {json}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        var steps = new List<object>();
        void OnStep(AgentRunStep step)
        {
            var obj = new { type = step.Type, content = step.Content, toolName = step.ToolName, toolInput = step.ToolInput, toolOutput = step.ToolOutput };
            steps.Add(obj);
            Send(obj).GetAwaiter().GetResult();
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _runner.RunAsync(agent, req.Input, UserId, req.UserApiKey, req.ModelOverride, OnStep, ct);
            sw.Stop();
            agentRun.FinalOutput = result.FinalOutput;
            agentRun.StepsJson = JsonSerializer.Serialize(result.Steps);
            agentRun.Status = "completed";
            agentRun.DurationMs = (int)sw.ElapsedMilliseconds;
            agentRun.TokensUsed = result.TokensUsed;
            agentRun.Iterations = result.IterationsUsed;
            agentRun.ModelUsed = req.ModelOverride ?? agent.Model;
            agentRun.ApprovalRequired = agent.GovernanceJson.Contains("approvalMode", StringComparison.OrdinalIgnoreCase);
            await _db.SaveChangesAsync(CancellationToken.None);
            await Send(new { type = "done", finalOutput = result.FinalOutput, tokensUsed = result.TokensUsed, iterations = result.IterationsUsed, runId = agentRun.Id, modelUsed = agentRun.ModelUsed, approvalRequired = agentRun.ApprovalRequired });
        }
        catch (Exception ex)
        {
            sw.Stop();
            agentRun.Status = "failed";
            agentRun.FinalOutput = ex.Message;
            agentRun.DurationMs = (int)sw.ElapsedMilliseconds;
            await _db.SaveChangesAsync(CancellationToken.None);
            await Send(new { type = "error", content = ex.Message });
        }
    }
}

public record AgentUpsertRequest(
    string Name,
    string Instructions,
    string? Description = null,
    string? Model = null,
    string? EnabledTools = "[]",
    string? ToolPermissions = "{}",
    string? InputSchemaJson = "[]",
    string? MemoryConfigJson = "{}",
    string? GuardrailsJson = "{}",
    string? OutputSchemaJson = "{}",
    string? KnowledgeSourcesJson = "[]",
    string? IntegrationActionsJson = "[]",
    string? ModelRoutingJson = "{}",
    string? BenchmarkConfigJson = "{}",
    string? GovernanceJson = "{}",
    string? SourcePromptId = null,
    int? SourcePersonaId = null,
    string? FallbackBehavior = null,
    string? EscalationRules = null,
    float Temperature = 0.7f,
    int MaxIterations = 5,
    int? Version = null,
    bool IsTemplate = false,
    string? TemplateCategory = null,
    bool IsPublic = false);

public record AgentRunRequest(
    string Input,
    string? UserApiKey = null,
    string? ModelOverride = null);
