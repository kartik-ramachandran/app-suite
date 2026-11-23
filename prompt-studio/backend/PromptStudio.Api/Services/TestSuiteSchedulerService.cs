using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Models;

namespace PromptStudio.Api.Services;

/// <summary>
/// Runs due test suites and evaluates trace alert rules.
///
/// This is the half of the feature people pay for. A suite you have to remember
/// to run tells you a prompt broke when you next think to check; a suite that
/// runs at 3am tells you the morning it happens, which is usually before a
/// customer does.
/// </summary>
public class TestSuiteSchedulerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<TestSuiteSchedulerService> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(10);

    public TestSuiteSchedulerService(IServiceScopeFactory scopes, ILogger<TestSuiteSchedulerService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let the app finish starting before touching the database.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(_interval);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunDueSuitesAsync(ct);
                await EvaluateAlertRulesAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // One bad tick must not kill the loop for the process lifetime.
                _logger.LogError(ex, "Test suite scheduler tick failed");
            }

            try { if (!await timer.WaitForNextTickAsync(ct)) break; }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunDueSuitesAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PromptDbContext>();
        var runner = scope.ServiceProvider.GetRequiredService<TestSuiteRunner>();

        var now = DateTime.UtcNow;
        var due = await db.TestSuites
            .Where(s => s.IsActive && s.ScheduleEnabled && s.NextRunAt != null && s.NextRunAt <= now)
            .Take(20)
            .ToListAsync(ct);

        foreach (var suite in due)
        {
            try
            {
                _logger.LogInformation("Running scheduled test suite {Suite}", suite.Id);
                await runner.RunAsync(suite, "scheduled", ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled suite {Suite} failed", suite.Id);
                // Push the next attempt out so a permanently broken suite does
                // not re-run every tick forever.
                suite.NextRunAt = TestSuiteRunner.NextRunFor(suite);
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task EvaluateAlertRulesAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PromptDbContext>();
        var broker = scope.ServiceProvider.GetRequiredService<NotificationBroker>();

        var now = DateTime.UtcNow;
        var rules = await db.TraceAlertRules.Where(r => r.IsActive).ToListAsync(ct);

        foreach (var rule in rules)
        {
            if (rule.LastFiredAt.HasValue && rule.LastFiredAt.Value.AddHours(rule.CooldownHours) > now)
                continue;

            var since = now.AddHours(-Math.Max(1, rule.WindowHours));

            var q = db.PromptTraces.Where(t => t.UserId == rule.UserId && t.CreatedAt >= since);
            if (rule.PromptId != null) q = q.Where(t => t.PromptId == rule.PromptId);

            var traces = await q
                .Select(t => new { t.CostUsd, t.Status, t.LatencyMs })
                .ToListAsync(ct);

            if (traces.Count == 0) continue;

            double value = rule.Metric switch
            {
                "cost" => (double)traces.Sum(t => t.CostUsd),
                "error_rate" => 100.0 * traces.Count(t => t.Status != "ok") / traces.Count,
                "latency_p95" => Percentile(traces.Select(t => (double)t.LatencyMs).ToList(), 95),
                "volume" => traces.Count,
                _ => 0
            };

            rule.LastValue = value;

            if (value > rule.Threshold)
            {
                rule.LastFiredAt = now;
                var unit = rule.Metric switch
                {
                    "cost" => "USD",
                    "error_rate" => "%",
                    "latency_p95" => "ms",
                    _ => ""
                };

                try
                {
                    broker.Publish(rule.UserId, JsonSerializer.Serialize(new
                    {
                        type = "trace_alert",
                        ruleId = rule.Id,
                        ruleName = rule.Name,
                        metric = rule.Metric,
                        value = Math.Round(value, 4),
                        threshold = rule.Threshold,
                        windowHours = rule.WindowHours,
                        message = $"{rule.Name}: {rule.Metric} is {Math.Round(value, 2)}{unit} over the last {rule.WindowHours}h (threshold {rule.Threshold}{unit})."
                    }));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to publish trace alert for rule {Rule}", rule.Id);
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Nearest-rank percentile. Exact on small samples, which is what we have.</summary>
    public static double Percentile(List<double> values, int p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        var rank = (int)Math.Ceiling(p / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }
}
