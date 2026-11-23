using Microsoft.EntityFrameworkCore;
using ContentStudio.Api.Data;
using ContentStudio.Api.Models;

namespace ContentStudio.Api.Services;

public class JobSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobSchedulerService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromMinutes(5);

    public JobSchedulerService(IServiceScopeFactory scopeFactory, ILogger<JobSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Job Scheduler started");
        using var timer = new PeriodicTimer(_checkInterval);

        while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct))
        {
            try { await RunDueJobsAsync(ct); }
            catch (Exception ex) { _logger.LogError(ex, "Error in job scheduler tick"); }
        }
    }

    private async Task RunDueJobsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PromptDbContext>();
        var aiService = scope.ServiceProvider.GetRequiredService<IAIService>();
        var chainService = scope.ServiceProvider.GetRequiredService<ChainExecutionService>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var agentRunner = scope.ServiceProvider.GetRequiredService<AgentRunnerService>();

        await ConsolidateMemoriesIfDueAsync(db, aiService, ct);

        var dueJobs = await db.ScheduledJobs
            .Where(j => j.IsActive && j.NextRunAt != null && j.NextRunAt <= DateTime.UtcNow)
            .ToListAsync(ct);

        foreach (var job in dueJobs)
        {
            var run = new JobRun { JobId = job.Id, UserId = job.UserId, Status = "running" };
            db.JobRuns.Add(run);
            await db.SaveChangesAsync(ct);

            try
            {
                var result = await ExecuteJobAsync(job, aiService, chainService, ct);
                run.Status = "success";
                run.ResultText = result;
                job.LastRunStatus = "success";

                if (!string.IsNullOrWhiteSpace(job.DeliverToEmail))
                {
                    await emailService.SendEmailAsync(job.DeliverToEmail,
                        $"[GenPrompt] Job result: {job.Name}",
                        $"<h2>{job.Name}</h2><p>Your scheduled job ran successfully.</p><hr/><pre>{result}</pre>");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job {JobId} failed", job.Id);
                run.Status = "failed";
                run.ErrorMessage = ex.Message;
                job.LastRunStatus = "failed";
            }

            job.LastRunAt = DateTime.UtcNow;
            job.NextRunAt = CalculateNextRun(job);
            await db.SaveChangesAsync(ct);
        }
    }

    // autoDream-style: consolidate each user's memories once per week
    private static readonly TimeSpan ConsolidationInterval = TimeSpan.FromDays(7);
    private const string ConsolidationKey = "system:last_memory_consolidation";

    private async Task ConsolidateMemoriesIfDueAsync(PromptDbContext db, IAIService aiService, CancellationToken ct)
    {
        try
        {
            var userIds = await db.UserMemories
                .Select(m => m.UserId)
                .Distinct()
                .ToListAsync(ct);

            foreach (var userId in userIds)
            {
                var marker = await db.UserMemories
                    .FirstOrDefaultAsync(m => m.UserId == userId && m.Key == ConsolidationKey, ct);

                var lastRun = marker != null && DateTime.TryParse(marker.Value, out var dt) ? dt : DateTime.MinValue;
                if (DateTime.UtcNow - lastRun < ConsolidationInterval) continue;

                var memories = await db.UserMemories
                    .Where(m => m.UserId == userId && !m.Key.StartsWith("system:"))
                    .ToListAsync(ct);

                if (memories.Count < 10) continue;

                var raw = string.Join("\n", memories.Select(m => $"- {m.Key}: {m.Value}"));
                var prompt = $"Consolidate and deduplicate the following user memories into a clean, concise set. Merge related items, remove redundancies, and keep only facts that would be useful in future conversations. Return as a JSON array of {{\"key\":\"...\",\"value\":\"...\"}} objects.\n\n{raw}";

                var response = await aiService.TestPromptAsync(new PromptTestRequest { PromptContent = prompt });
                var json = response.Result.Trim();
                var start = json.IndexOf('[');
                var end = json.LastIndexOf(']');
                if (start < 0 || end < 0) continue;

                var consolidated = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(json[start..(end + 1)]);
                if (consolidated == null || consolidated.Count == 0) continue;

                // Replace memories with consolidated set
                db.UserMemories.RemoveRange(memories);
                foreach (var item in consolidated)
                {
                    if (!item.TryGetValue("key", out var k) || !item.TryGetValue("value", out var v)) continue;
                    db.UserMemories.Add(new UserMemory { UserId = userId, Key = k, Value = v, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
                }

                // Update marker
                if (marker != null) { marker.Value = DateTime.UtcNow.ToString("O"); marker.UpdatedAt = DateTime.UtcNow; }
                else db.UserMemories.Add(new UserMemory { UserId = userId, Key = ConsolidationKey, Value = DateTime.UtcNow.ToString("O"), CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });

                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Consolidated {Count} memories for user {UserId}", memories.Count, userId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Memory consolidation failed");
        }
    }

    private async Task<string> ExecuteJobAsync(
        ScheduledJob job, IAIService aiService,
        ChainExecutionService chainService, CancellationToken ct)
    {
        if (job.AgentType == "chain" && job.ChainId.HasValue)
        {
            var result = await chainService.ExecuteChainAsync(job.ChainId.Value, job.Input, job.UserId);
            return result.FinalOutput ?? "No output";
        }

        if (job.AgentType == "social")
        {
            using var scope = _scopeFactory.CreateScope();
            var content = scope.ServiceProvider.GetRequiredService<ContentGenerationService>();
            // Input may be "platforms=linkedin,twitter|topic text"; fall back to all platforms + whole input as topic.
            string topic = job.Input;
            List<string>? platforms = null;
            var m = System.Text.RegularExpressions.Regex.Match(job.Input, @"^\s*platforms\s*=\s*([a-z, ]+)\|(.*)$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
            if (m.Success)
            {
                platforms = m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                topic = m.Groups[2].Value.Trim();
            }
            var posts = await content.GenerateSocialPackAsync(topic, "professional", platforms);
            return FormatSocialPack(posts, topic);
        }

        if (job.AgentType == "agent")
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PromptDbContext>();
            var runner = scope.ServiceProvider.GetRequiredService<AgentRunnerService>();
            var agentIdStr = job.Input.Split(':').FirstOrDefault() ?? "";
            var agentInput = job.Input.Contains(':') ? job.Input[(job.Input.IndexOf(':') + 1)..].Trim() : job.Input;
            if (int.TryParse(agentIdStr, out var agentId))
            {
                var agent = await db.Agents.FirstOrDefaultAsync(a => a.Id == agentId && a.UserId == job.UserId, ct);
                if (agent != null)
                {
                    var result = await runner.RunAsync(agent, agentInput, job.UserId, ct: ct);
                    return result.FinalOutput ?? "No output";
                }
            }
        }

        var response = await aiService.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = job.Input,
            UserInput = $"Run scheduled at {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC"
        });
        return response.Result;
    }

    private static readonly Dictionary<string, string> SocialLabels = new()
    {
        ["linkedin"] = "LinkedIn", ["twitter"] = "X (Twitter)", ["instagram"] = "Instagram",
        ["facebook"] = "Facebook", ["threads"] = "Threads", ["tiktok"] = "TikTok",
        ["newsletter"] = "Newsletter (email)",
    };

    public static string FormatSocialPack(Dictionary<string, string> posts, string topic)
    {
        if (posts.Count == 0) return "No posts could be generated for this topic.";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Your ready-to-post social pack for: {topic}");
        sb.AppendLine();
        foreach (var (key, text) in posts)
        {
            var label = SocialLabels.TryGetValue(key, out var l) ? l : key;
            sb.AppendLine($"── {label} ──");
            sb.AppendLine(text.Trim());
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    public static DateTime? CalculateNextRun(ScheduledJob job)
    {
        if (!job.IsActive) return null;
        var now = DateTime.UtcNow;

        return job.Schedule switch
        {
            "once" => null, // one-off, don't reschedule
            "daily" => now.Date.AddDays(1).AddHours(job.Hour),
            "weekly" => GetNextWeekday(now, job.DayOfWeek, job.Hour),
            "monthly" => GetNextMonthDay(now, job.DayOfMonth, job.Hour),
            _ => now.AddDays(1)
        };
    }

    private static DateTime GetNextWeekday(DateTime from, int targetDow, int hour)
    {
        var daysAhead = ((targetDow - (int)from.DayOfWeek + 7) % 7);
        if (daysAhead == 0) daysAhead = 7;
        return from.Date.AddDays(daysAhead).AddHours(hour);
    }

    private static DateTime GetNextMonthDay(DateTime from, int day, int hour)
    {
        var next = new DateTime(from.Year, from.Month, Math.Min(day, DateTime.DaysInMonth(from.Year, from.Month)), hour, 0, 0, DateTimeKind.Utc);
        if (next <= from) next = next.AddMonths(1);
        return next;
    }
}
