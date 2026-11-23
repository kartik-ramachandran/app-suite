using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Services;

namespace AgentHub.Api.Controllers;

/// <summary>
/// In-app notifications backed by the ModuleJobs execution log. Provides a durable
/// list (for the bell dropdown / catch-up) and a live SSE stream that pushes job
/// completions as they happen.
/// </summary>
[ApiController]
[Route("api/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly NotificationBroker _broker;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(PromptDbContext db, NotificationBroker broker, ILogger<NotificationsController> logger)
    {
        _db = db;
        _broker = broker;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    /// <summary>Recent job notifications for the current user, plus the unread count.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 30)
    {
        var userId = GetUserId();
        take = Math.Clamp(take, 1, 100);

        var items = await _db.ModuleJobs
            .Where(j => j.UserId == userId)
            .OrderByDescending(j => j.Id)
            .Take(take)
            .Select(j => new
            {
                j.Id, j.Module, j.JobType, j.Title, j.Status,
                j.ErrorMessage, j.DurationMs, j.IsRead, j.CreatedAt, j.CompletedAt
            })
            .ToListAsync();

        var unread = await _db.ModuleJobs.CountAsync(j => j.UserId == userId && !j.IsRead);
        return Ok(new { items, unread });
    }

    [HttpPost("{id}/read")]
    public async Task<IActionResult> MarkRead(int id)
    {
        var userId = GetUserId();
        await _db.ModuleJobs
            .Where(j => j.Id == id && j.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.IsRead, true));
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = GetUserId();
        await _db.ModuleJobs
            .Where(j => j.UserId == userId && !j.IsRead)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.IsRead, true));
        return NoContent();
    }

    /// <summary>
    /// Server-Sent Events stream of live job notifications for the current user.
    /// EventSource cannot send an Authorization header, so the JWT is accepted via
    /// the ?access_token= query string (wired in Program.cs JwtBearer events).
    /// </summary>
    [HttpGet("stream")]
    public async Task Stream(CancellationToken ct)
    {
        var userId = GetUserId();

        Response.Headers["Content-Type"] = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["Connection"] = "keep-alive";
        Response.Headers["X-Accel-Buffering"] = "no"; // disable proxy buffering

        var (subId, reader) = _broker.Subscribe(userId);
        try
        {
            await Response.WriteAsync(": connected\n\n", ct);
            await Response.Body.FlushAsync(ct);

            while (!ct.IsCancellationRequested)
            {
                var readTask = reader.WaitToReadAsync(ct).AsTask();
                var heartbeat = Task.Delay(TimeSpan.FromSeconds(20), ct);
                var completed = await Task.WhenAny(readTask, heartbeat);

                if (completed == heartbeat)
                {
                    // Keep the connection alive through idle periods / proxies.
                    await Response.WriteAsync(": ping\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                    continue;
                }

                if (!await readTask) break; // channel completed

                while (reader.TryRead(out var message))
                {
                    await Response.WriteAsync($"data: {message}\n\n", ct);
                    await Response.Body.FlushAsync(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client disconnected — normal.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SSE stream ended for user {UserId}", userId);
        }
        finally
        {
            _broker.Unsubscribe(userId, subId);
        }
    }
}
