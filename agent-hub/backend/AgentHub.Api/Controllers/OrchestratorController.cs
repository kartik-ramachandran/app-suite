using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Services;
using System.Security.Claims;

namespace AgentHub.Api.Controllers;

/// <summary>
/// Autonomous runs: give a goal, get a plan, watch it execute, approve the steps
/// that touch the outside world. All runs are scoped to the calling user.
/// </summary>
[Authorize]
[ApiController]
[Route("api/orchestrator")]
public class OrchestratorController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly OrchestrationService _orchestration;
    private readonly ToolRegistry _tools;

    public OrchestratorController(PromptDbContext db, OrchestrationService orchestration, ToolRegistry tools)
    {
        _db = db;
        _orchestration = orchestration;
        _tools = tools;
    }

    private string UserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    public class StartRequest
    {
        public string Goal { get; set; } = string.Empty;
        public Guid? SourceNodeId { get; set; }
    }

    /// <summary>What this user's runs can currently do — built-ins plus their MCP tools.</summary>
    [HttpGet("tools")]
    public async Task<ActionResult> Tools(CancellationToken ct)
    {
        var tools = await _tools.GetToolsAsync(UserId, ct);
        return Ok(tools.Select(t => new { t.Id, t.Title, t.Description, t.IsWrite, t.NeedsApproval }));
    }

    [HttpPost("runs")]
    public async Task<ActionResult> Start([FromBody] StartRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Goal))
            return BadRequest(new { message = "Describe what you want done." });

        // The executor picks it up within a few seconds; the request returns at once
        // so a long plan never blocks the browser.
        var run = await _orchestration.CreateRunAsync(UserId, request.Goal, request.SourceNodeId, ct);
        return Ok(new { run.Id, run.Status, run.Goal });
    }

    [HttpGet("runs")]
    public async Task<ActionResult> List()
    {
        var runs = await _db.OrchestrationRuns
            .Where(r => r.UserId == UserId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(30)
            .Select(r => new
            {
                r.Id, r.Goal, r.Status, r.PlanSummary, r.StepsUsed, r.MaxSteps,
                r.CreatedAt, r.CompletedAt, r.ErrorMessage,
            })
            .ToListAsync();
        return Ok(runs);
    }

    [HttpGet("runs/{id}")]
    public async Task<ActionResult> Get(string id)
    {
        var run = await _db.OrchestrationRuns.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId);
        if (run == null) return NotFound();

        var tasks = await _db.OrchestrationTasks
            .Where(t => t.RunId == id)
            .OrderBy(t => t.OrderIndex)
            .ToListAsync();

        return Ok(new
        {
            run.Id, run.Goal, run.Status, run.PlanSummary, run.Result, run.ErrorMessage,
            run.StepsUsed, run.MaxSteps, run.CreatedAt, run.CompletedAt,
            tasks = tasks.Select(t => new
            {
                t.Id, t.Key, t.Title, t.Tool, t.Status, t.Output, t.ErrorMessage,
                t.NeedsApproval, t.ApprovedAt, t.DurationMs, t.Attempts,
                dependsOn = t.DependsOnJson,
                arguments = t.ArgumentsJson,
            }),
        });
    }

    /// <summary>Approve a parked step so the executor may run it.</summary>
    [HttpPost("tasks/{taskId}/approve")]
    public async Task<ActionResult> Approve(int taskId, CancellationToken ct)
    {
        var task = await _db.OrchestrationTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == UserId, ct);
        if (task == null) return NotFound();
        if (task.Status != "waiting_approval")
            return BadRequest(new { message = $"That step is '{task.Status}', not awaiting approval." });

        task.ApprovedBy = UserId;
        task.ApprovedAt = DateTime.UtcNow;
        task.Status = "ready";

        var run = await _db.OrchestrationRuns.FirstOrDefaultAsync(r => r.Id == task.RunId, ct);
        if (run != null) { run.Status = "running"; run.UpdatedAt = DateTime.UtcNow; }

        await _db.SaveChangesAsync(ct);
        return Ok(new { approved = true, task.Id });
    }

    /// <summary>Decline a step. Anything depending on it is skipped, the rest continues.</summary>
    [HttpPost("tasks/{taskId}/decline")]
    public async Task<ActionResult> Decline(int taskId, CancellationToken ct)
    {
        var task = await _db.OrchestrationTasks.FirstOrDefaultAsync(t => t.Id == taskId && t.UserId == UserId, ct);
        if (task == null) return NotFound();

        task.Status = "skipped";
        task.ErrorMessage = "Declined.";
        task.CompletedAt = DateTime.UtcNow;

        var run = await _db.OrchestrationRuns.FirstOrDefaultAsync(r => r.Id == task.RunId, ct);
        if (run != null) { run.Status = "running"; run.UpdatedAt = DateTime.UtcNow; }

        await _db.SaveChangesAsync(ct);
        return Ok(new { declined = true, task.Id });
    }

    [HttpPost("runs/{id}/cancel")]
    public async Task<ActionResult> Cancel(string id, CancellationToken ct)
    {
        var run = await _db.OrchestrationRuns.FirstOrDefaultAsync(r => r.Id == id && r.UserId == UserId, ct);
        if (run == null) return NotFound();
        if (run.Status is "completed" or "failed" or "cancelled")
            return BadRequest(new { message = "That run has already finished." });

        run.Status = "cancelled";
        run.CompletedAt = DateTime.UtcNow;
        run.UpdatedAt = DateTime.UtcNow;

        var pending = await _db.OrchestrationTasks
            .Where(t => t.RunId == id && (t.Status == "pending" || t.Status == "ready" || t.Status == "waiting_approval"))
            .ToListAsync(ct);
        foreach (var task in pending) { task.Status = "skipped"; task.ErrorMessage = "Run cancelled."; }

        await _db.SaveChangesAsync(ct);
        return Ok(new { cancelled = true });
    }
}
