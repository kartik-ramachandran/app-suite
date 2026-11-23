using System.Text.Json;
using BookReader.Api.Data;
using BookReader.Api.Models;

namespace BookReader.Api.Services;

/// <summary>
/// Reusable job-execution logger any module can use. Records start, then
/// completion or failure of a unit of work to the ModuleJobs table, and pushes a
/// live notification to the user via <see cref="NotificationBroker"/> when the
/// job finishes.
///
/// Usage:
///   var job = await jobLog.StartAsync(userId, "interaction-intelligence", "transcribe", "Transcribe: meeting.mp4");
///   try   { ...work...; await jobLog.CompleteAsync(job, "Transcribed 1,240 words"); }
///   catch (Exception ex) { await jobLog.FailAsync(job, ex.Message); throw; }
/// </summary>
public class JobLogService
{
    private readonly PromptDbContext _db;
    private readonly NotificationBroker _broker;
    private readonly WebhookDispatcher _webhooks;
    private readonly ILogger<JobLogService> _logger;

    public JobLogService(PromptDbContext db, NotificationBroker broker, WebhookDispatcher webhooks, ILogger<JobLogService> logger)
    {
        _db = db;
        _broker = broker;
        _webhooks = webhooks;
        _logger = logger;
    }

    public async Task<ModuleJob> StartAsync(string userId, string module, string jobType, string title)
    {
        var job = new ModuleJob
        {
            UserId = userId,
            Module = module,
            JobType = jobType,
            Title = title,
            Status = "running",
            CreatedAt = DateTime.UtcNow
        };
        _db.ModuleJobs.Add(job);
        await _db.SaveChangesAsync();
        Publish(job, "job.started");
        return job;
    }

    public async Task CompleteAsync(ModuleJob job, string? resultText = null)
    {
        job.Status = "completed";
        job.ResultText = Truncate(resultText, 2000);
        Finish(job);
        await _db.SaveChangesAsync();
        Publish(job, "job.completed");
        _webhooks.QueueJobEvent(job, "job.completed");
    }

    public async Task FailAsync(ModuleJob job, string error)
    {
        job.Status = "failed";
        job.ErrorMessage = Truncate(error, 1000);
        Finish(job);
        await _db.SaveChangesAsync();
        Publish(job, "job.failed");
        _webhooks.QueueJobEvent(job, "job.failed");
    }

    private static void Finish(ModuleJob job)
    {
        job.CompletedAt = DateTime.UtcNow;
        job.DurationMs = (long)(job.CompletedAt.Value - job.CreatedAt).TotalMilliseconds;
    }

    private void Publish(ModuleJob job, string evt)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                @event = evt,
                id = job.Id,
                module = job.Module,
                jobType = job.JobType,
                title = job.Title,
                status = job.Status,
                error = job.ErrorMessage,
                durationMs = job.DurationMs,
                createdAt = job.CreatedAt,
                completedAt = job.CompletedAt
            });
            _broker.Publish(job.UserId, payload);
        }
        catch (Exception ex)
        {
            // Never let a notification failure break the actual work.
            _logger.LogWarning(ex, "Failed to publish job notification for job {JobId}", job.Id);
        }
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s.Substring(0, max) + "…");
}
