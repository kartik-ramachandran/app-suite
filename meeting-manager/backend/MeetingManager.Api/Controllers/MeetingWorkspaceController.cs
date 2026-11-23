using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MeetingManager.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace MeetingManager.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/meetings")]
[EnableRateLimiting("ai-generation")]
public class MeetingWorkspaceController : ControllerBase
{
    private readonly MeetingWorkspaceService _svc;
    private readonly ILogger<MeetingWorkspaceController> _logger;

    public MeetingWorkspaceController(MeetingWorkspaceService svc, ILogger<MeetingWorkspaceController> logger)
    {
        _svc = svc;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Workspace CRUD ────────────────────────────────────────────────────────

    [HttpGet("workspaces")]
    public async Task<IActionResult> ListWorkspaces()
    {
        var rows = await _svc.GetWorkspacesAsync(UserId);
        return Ok(rows.Select(MapWorkspace));
    }

    [HttpGet("workspaces/{id}")]
    public async Task<IActionResult> GetWorkspace(int id)
    {
        var row = await _svc.GetWorkspaceAsync(id, UserId);
        if (row == null) return NotFound();
        return Ok(MapWorkspace(row));
    }

    [HttpPost("workspaces")]
    public async Task<IActionResult> GetOrCreateWorkspace([FromBody] WorkspaceCreateRequest req)
    {
        var attendeesJson = JsonSerializer.Serialize(req.Attendees ?? new());
        var row = await _svc.GetOrCreateWorkspaceAsync(
            UserId, req.ProviderEventId, req.EventTitle,
            req.EventStart, req.EventEnd, attendeesJson);
        return Ok(MapWorkspace(row!));
    }

    // ── AI Actions ────────────────────────────────────────────────────────────

    [HttpPost("workspaces/{id}/generate-prep")]
    public async Task<IActionResult> GeneratePrep(int id, [FromBody] GeneratePrepRequest req)
    {
        var workspace = await _svc.GetWorkspaceAsync(id, UserId);
        if (workspace == null) return NotFound();

        try
        {
            var attendees = string.IsNullOrEmpty(workspace.AttendeesJson)
                ? new List<AttendeeDto>()
                : JsonSerializer.Deserialize<List<AttendeeDto>>(workspace.AttendeesJson) ?? new();

            var openTasks = await _svc.GetTasksAsync(UserId);
            var taskTitles = openTasks.Where(t => t.Status == "open").Select(t => t.Title).Take(10).ToList();

            var prep = await _svc.GeneratePrepPackAsync(
                workspace.EventTitle, req.Description ?? workspace.EventTitle,
                attendees, req.PreviousNotes ?? workspace.Notes, taskTitles);

            await _svc.SavePrepPackAsync(id, prep, prep.Objective);
            return Ok(new { prep });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prep pack generation failed for workspace {Id}", id);
            return StatusCode(500, new { message = "Generation failed. Please try again." });
        }
    }

    [HttpPost("workspaces/{id}/generate-follow-up")]
    public async Task<IActionResult> GenerateFollowUp(int id, [FromBody] FollowUpRequest req)
    {
        var workspace = await _svc.GetWorkspaceAsync(id, UserId);
        if (workspace == null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Notes))
            return BadRequest(new { message = "Meeting notes are required to generate follow-up" });

        try
        {
            var attendees = string.IsNullOrEmpty(workspace.AttendeesJson)
                ? new List<AttendeeDto>()
                : JsonSerializer.Deserialize<List<AttendeeDto>>(workspace.AttendeesJson) ?? new();

            var followUp = await _svc.GenerateFollowUpAsync(workspace.EventTitle, req.Notes, attendees);
            await _svc.SaveFollowUpAsync(id, followUp);
            await _svc.SaveNotesAsync(id, req.Notes);
            await _svc.SaveFollowUpsFromPackAsync(UserId, id, followUp);
            return Ok(new { followUp });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Follow-up generation failed for workspace {Id}", id);
            return StatusCode(500, new { message = "Generation failed. Please try again." });
        }
    }

    [HttpPost("workspaces/{id}/extract-tasks")]
    public async Task<IActionResult> ExtractTasks(int id, [FromBody] ExtractTasksRequest req)
    {
        var workspace = await _svc.GetWorkspaceAsync(id, UserId);
        if (workspace == null) return NotFound();
        if (string.IsNullOrWhiteSpace(req.Notes))
            return BadRequest(new { message = "Notes are required" });

        try
        {
            var tasks = await _svc.ExtractTasksAsync(req.Notes);
            await _svc.SaveTasksAsync(UserId, id, tasks);
            return Ok(new { tasks });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task extraction failed");
            return StatusCode(500, new { message = "Extraction failed. Please try again." });
        }
    }

    [HttpPatch("workspaces/{id}/notes")]
    public async Task<IActionResult> SaveNotes(int id, [FromBody] NotesRequest req)
    {
        var workspace = await _svc.GetWorkspaceAsync(id, UserId);
        if (workspace == null) return NotFound();
        await _svc.SaveNotesAsync(id, req.Notes ?? "");
        return Ok(new { message = "Saved" });
    }

    // ── Tasks ─────────────────────────────────────────────────────────────────

    [HttpGet("tasks")]
    public async Task<IActionResult> GetTasks([FromQuery] int? workspaceId)
    {
        var tasks = await _svc.GetTasksAsync(UserId, workspaceId);
        return Ok(tasks);
    }

    [HttpPatch("tasks/{taskId}/status")]
    public async Task<IActionResult> UpdateTaskStatus(int taskId, [FromBody] StatusRequest req)
    {
        await _svc.UpdateTaskStatusAsync(taskId, UserId, req.Status);
        return Ok(new { message = "Updated" });
    }

    // ── Follow-ups ────────────────────────────────────────────────────────────

    [HttpGet("follow-ups")]
    public async Task<IActionResult> GetFollowUps([FromQuery] string? status)
    {
        var followUps = await _svc.GetFollowUpsAsync(UserId, status);
        return Ok(followUps);
    }

    [HttpPatch("follow-ups/{followUpId}/status")]
    public async Task<IActionResult> UpdateFollowUpStatus(int followUpId, [FromBody] StatusRequest req)
    {
        await _svc.UpdateFollowUpStatusAsync(followUpId, UserId, req.Status);
        return Ok(new { message = "Updated" });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static object MapWorkspace(WorkspaceRow r) => new
    {
        r.Id, r.ProviderEventId, r.EventTitle, r.EventStart, r.EventEnd,
        r.Objective, r.Notes, r.Summary, r.FollowUpDraft, r.CreatedAt, r.UpdatedAt,
        attendees = string.IsNullOrEmpty(r.AttendeesJson) ? new() : TryDeserialize(r.AttendeesJson),
        prepPack = string.IsNullOrEmpty(r.PrepPackJson) ? null : TryDeserializeObj(r.PrepPackJson),
        decisions = string.IsNullOrEmpty(r.DecisionsJson) ? new() : TryDeserialize(r.DecisionsJson),
        risks = string.IsNullOrEmpty(r.RisksJson) ? new() : TryDeserialize(r.RisksJson),
        actions = string.IsNullOrEmpty(r.ActionsJson) ? new() : TryDeserialize(r.ActionsJson),
    };

    private static List<object> TryDeserialize(string json)
    {
        try { return JsonSerializer.Deserialize<List<object>>(json) ?? new(); } catch { return new(); }
    }

    private static object? TryDeserializeObj(string json)
    {
        try { return JsonSerializer.Deserialize<object>(json); } catch { return null; }
    }
}

public class WorkspaceCreateRequest
{
    public string? ProviderEventId { get; set; }
    public required string EventTitle { get; set; }
    public string? EventStart { get; set; }
    public string? EventEnd { get; set; }
    public List<AttendeeDto>? Attendees { get; set; }
}

public class GeneratePrepRequest
{
    public string? Description { get; set; }
    public string? PreviousNotes { get; set; }
}

public class FollowUpRequest
{
    public required string Notes { get; set; }
}

public class ExtractTasksRequest
{
    public required string Notes { get; set; }
}

public class NotesRequest
{
    public string? Notes { get; set; }
}

public class StatusRequest
{
    public required string Status { get; set; }
}
