using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using System.Security.Claims;

namespace AgentHub.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class JobsController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly IAIService _aiService;
    private readonly ChainExecutionService _chainService;
    private readonly IEmailService _emailService;
    private readonly ContentGenerationService _contentService;
    private readonly ILogger<JobsController> _logger;

    public JobsController(
        PromptDbContext context, IAIService aiService,
        ChainExecutionService chainService, IEmailService emailService,
        ContentGenerationService contentService,
        ILogger<JobsController> logger)
    {
        _context = context;
        _aiService = aiService;
        _chainService = chainService;
        _emailService = emailService;
        _contentService = contentService;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    [HttpGet]
    public async Task<ActionResult> GetJobs()
    {
        var userId = GetUserId();
        var jobs = await _context.ScheduledJobs
            .Where(j => j.UserId == userId)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new {
                j.Id, j.Name, j.AgentType, j.ChainId, j.Schedule, j.Hour,
                j.DayOfWeek, j.DayOfMonth, j.NextRunAt, j.LastRunAt,
                j.LastRunStatus, j.DeliverToEmail, j.IsActive, j.CreatedAt, j.Input
            })
            .ToListAsync();
        return Ok(jobs);
    }

    [HttpGet("{id}/runs")]
    public async Task<ActionResult> GetRuns(int id)
    {
        var userId = GetUserId();
        var job = await _context.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId);
        if (job == null) return NotFound();

        var runs = await _context.JobRuns
            .Where(r => r.JobId == id)
            .OrderByDescending(r => r.CreatedAt)
            .Take(20)
            .Select(r => new { r.Id, r.Status, r.ResultText, r.ErrorMessage, r.CreatedAt })
            .ToListAsync();
        return Ok(runs);
    }

    [HttpPost]
    public async Task<ActionResult> CreateJob([FromBody] CreateJobRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required");

        var userId = GetUserId();
        var job = new ScheduledJob
        {
            UserId = userId,
            Name = req.Name,
            AgentType = req.AgentType ?? "prompt",
            ChainId = req.ChainId,
            Input = req.Input ?? string.Empty,
            Schedule = req.Schedule ?? "daily",
            Hour = req.Hour ?? 9,
            DayOfWeek = req.DayOfWeek ?? 1,
            DayOfMonth = req.DayOfMonth ?? 1,
            OneOffAt = req.OneOffAt,
            DeliverToEmail = req.DeliverToEmail,
            IsActive = true
        };
        job.NextRunAt = JobSchedulerService.CalculateNextRun(job);

        _context.ScheduledJobs.Add(job);
        await _context.SaveChangesAsync();
        return Ok(new { job.Id, job.Name, job.NextRunAt });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> UpdateJob(int id, [FromBody] CreateJobRequest req)
    {
        var userId = GetUserId();
        var job = await _context.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId);
        if (job == null) return NotFound();

        job.Name = req.Name ?? job.Name;
        job.AgentType = req.AgentType ?? job.AgentType;
        job.ChainId = req.ChainId ?? job.ChainId;
        job.Input = req.Input ?? job.Input;
        job.Schedule = req.Schedule ?? job.Schedule;
        job.Hour = req.Hour ?? job.Hour;
        job.DayOfWeek = req.DayOfWeek ?? job.DayOfWeek;
        job.DayOfMonth = req.DayOfMonth ?? job.DayOfMonth;
        job.DeliverToEmail = req.DeliverToEmail ?? job.DeliverToEmail;
        job.IsActive = req.IsActive ?? job.IsActive;
        job.NextRunAt = JobSchedulerService.CalculateNextRun(job);

        await _context.SaveChangesAsync();
        return Ok(new { job.Id, job.Name, job.NextRunAt });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteJob(int id)
    {
        var userId = GetUserId();
        var job = await _context.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId);
        if (job == null) return NotFound();
        _context.ScheduledJobs.Remove(job);
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("{id}/trigger")]
    public async Task<ActionResult> TriggerNow(int id)
    {
        var userId = GetUserId();
        var job = await _context.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == id && j.UserId == userId);
        if (job == null) return NotFound();

        var run = new JobRun { JobId = job.Id, UserId = userId, Status = "running" };
        _context.JobRuns.Add(run);
        await _context.SaveChangesAsync();

        try
        {
            string result;
            if (job.AgentType == "chain" && job.ChainId.HasValue)
            {
                var chainResult = await _chainService.ExecuteChainAsync(job.ChainId.Value, job.Input, userId);
                result = chainResult.FinalOutput ?? "No output";
            }
            else if (job.AgentType == "social")
            {
                string topic = job.Input;
                List<string>? platforms = null;
                var m = System.Text.RegularExpressions.Regex.Match(job.Input, @"^\s*platforms\s*=\s*([a-z, ]+)\|(.*)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                if (m.Success)
                {
                    platforms = m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    topic = m.Groups[2].Value.Trim();
                }
                var posts = await _contentService.GenerateSocialPackAsync(topic, "professional", platforms);
                result = JobSchedulerService.FormatSocialPack(posts, topic);
            }
            else
            {
                var response = await _aiService.TestPromptAsync(new PromptTestRequest
                {
                    PromptContent = job.Input,
                    UserInput = $"Manual trigger at {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"
                });
                result = response.Result;
            }

            run.Status = "success";
            run.ResultText = result;
            job.LastRunAt = DateTime.UtcNow;
            job.LastRunStatus = "success";
            await _context.SaveChangesAsync();

            if (!string.IsNullOrWhiteSpace(job.DeliverToEmail))
            {
                await _emailService.SendEmailAsync(job.DeliverToEmail,
                    $"[GenPrompt] Job result: {job.Name}",
                    $"<h2>{job.Name}</h2><pre>{result}</pre>");
            }

            return Ok(new { status = "success", result });
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.ErrorMessage = ex.Message;
            job.LastRunStatus = "failed";
            await _context.SaveChangesAsync();
            return StatusCode(500, new { status = "failed", message = ex.Message });
        }
    }
}

public class CreateJobRequest
{
    public string? Name { get; set; }
    public string? AgentType { get; set; }
    public Guid? ChainId { get; set; }
    public string? Input { get; set; }
    public string? Schedule { get; set; }
    public int? Hour { get; set; }
    public int? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public DateTime? OneOffAt { get; set; }
    public string? DeliverToEmail { get; set; }
    public bool? IsActive { get; set; }
}
