using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Models;

namespace PromptStudio.Api.Services;

/// <summary>
/// Runs a test suite and records the result.
///
/// The comparison against the previous run is the feature. A suite that passes
/// is worth almost nothing on its own; a suite that passed last night and fails
/// this morning, with no commit in between, is the only warning you get that a
/// provider changed a model underneath you.
/// </summary>
public class TestSuiteRunner
{
    private readonly PromptDbContext _db;
    private readonly ModelExecutionService _exec;
    private readonly JsonSchemaValidator _schema;
    private readonly NotificationBroker _notifications;
    private readonly IEmailService _email;
    private readonly ILogger<TestSuiteRunner> _logger;

    public TestSuiteRunner(
        PromptDbContext db,
        ModelExecutionService exec,
        JsonSchemaValidator schema,
        NotificationBroker notifications,
        IEmailService email,
        ILogger<TestSuiteRunner> logger)
    {
        _db = db;
        _exec = exec;
        _schema = schema;
        _notifications = notifications;
        _email = email;
        _logger = logger;
    }

    public async Task<TestSuiteRun> RunAsync(TestSuite suite, string trigger = "manual", CancellationToken ct = default)
    {
        var cases = await _db.TestCases
            .Where(c => c.TestSuiteId == suite.Id && c.IsActive)
            .OrderBy(c => c.SortOrder)
            .ToListAsync(ct);

        // Read the baseline before writing this run, or we'd compare against ourselves.
        var previous = await _db.TestSuiteRuns
            .Where(r => r.TestSuiteId == suite.Id && r.Status != "running")
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);

        var promptContent = suite.PromptContent;
        if (string.IsNullOrWhiteSpace(promptContent) && suite.PromptId != null)
            promptContent = (await _db.Prompts.FirstOrDefaultAsync(p => p.Id == suite.PromptId, ct))?.Content;

        var model = suite.Model
            ?? (suite.PromptId != null
                ? (await _db.Prompts.Where(p => p.Id == suite.PromptId).Select(p => p.PreferredModel).FirstOrDefaultAsync(ct))
                : null)
            ?? "gpt-4o-mini";

        var run = new TestSuiteRun
        {
            TestSuiteId = suite.Id,
            UserId = suite.UserId,
            Trigger = trigger,
            Model = model,
            Status = "running",
            TotalCases = cases.Count,
            PreviousPassRate = previous?.PassRate,
            StartedAt = DateTime.UtcNow
        };
        _db.TestSuiteRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        if (string.IsNullOrWhiteSpace(promptContent))
        {
            run.Status = "error";
            run.ErrorMessage = "The suite has no prompt content and its linked prompt is gone.";
            run.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return run;
        }

        var results = new List<TestCaseResult>();
        foreach (var c in cases)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await RunCaseAsync(run.Id, c, promptContent!, model, ct));
        }

        run.PassedCases = results.Count(r => r.Passed);
        run.FailedCases = results.Count - run.PassedCases;
        run.PassRate = results.Count == 0 ? 100 : Math.Round(100.0 * run.PassedCases / results.Count, 2);
        run.TotalTokens = results.Sum(r => r.TokensUsed);
        run.TotalCostUsd = results.Sum(r => r.CostUsd);
        run.TotalLatencyMs = results.Sum(r => r.LatencyMs);
        run.Status = run.FailedCases == 0 ? "passed" : "failed";
        run.CompletedAt = DateTime.UtcNow;

        // A regression is a drop against the previous run, not merely a failure.
        // A suite that has always failed one case is not news every night.
        run.IsRegression = previous != null && run.PassRate < previous.PassRate;

        _db.TestCaseResults.AddRange(results);

        suite.LastRunAt = DateTime.UtcNow;
        suite.LastPassRate = run.PassRate;
        suite.NextRunAt = suite.ScheduleEnabled ? NextRunFor(suite) : null;
        suite.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        if (run.IsRegression && suite.AlertOnRegression)
            await AlertRegressionAsync(suite, run, results, ct);

        return run;
    }

    private async Task<TestCaseResult> RunCaseAsync(
        string runId, TestCase c, string promptContent, string model, CancellationToken ct)
    {
        var outcome = await _exec.RunAsync(promptContent, c.Input, model, null, ct);

        var result = new TestCaseResult
        {
            TestSuiteRunId = runId,
            TestCaseId = c.Id,
            CaseName = string.IsNullOrWhiteSpace(c.Name) ? Truncate(c.Input, 60) : c.Name,
            Output = outcome.Output,
            TokensUsed = outcome.TokensUsed,
            LatencyMs = outcome.LatencyMs,
            CostUsd = outcome.CostUsd
        };

        var failures = new List<string>();

        if (outcome.Error != null)
        {
            failures.Add($"Execution failed: {outcome.Error}");
            result.Passed = false;
            result.FailureReasonsJson = JsonSerializer.Serialize(failures);
            return result;
        }

        var output = outcome.Output ?? string.Empty;

        foreach (var needle in Deserialize(c.ExpectedContainsJson))
        {
            if (!output.Contains(needle, StringComparison.OrdinalIgnoreCase))
                failures.Add($"Expected to contain \"{Truncate(needle, 60)}\"");
        }

        foreach (var needle in Deserialize(c.ExpectedNotContainsJson))
        {
            if (output.Contains(needle, StringComparison.OrdinalIgnoreCase))
                failures.Add($"Expected NOT to contain \"{Truncate(needle, 60)}\"");
        }

        if (!string.IsNullOrWhiteSpace(c.ExpectedRegex))
        {
            try
            {
                if (!Regex.IsMatch(output, c.ExpectedRegex, RegexOptions.None, TimeSpan.FromSeconds(2)))
                    failures.Add($"Did not match pattern /{c.ExpectedRegex}/");
            }
            catch (ArgumentException) { failures.Add($"Invalid regex in test case: /{c.ExpectedRegex}/"); }
            catch (RegexMatchTimeoutException) { failures.Add("Regex evaluation timed out"); }
        }

        if (!string.IsNullOrWhiteSpace(c.JsonSchema))
        {
            var v = _schema.Validate(output, c.JsonSchema);
            if (!v.IsValid) failures.AddRange(v.Errors.Select(e => $"Schema: {e}"));
        }

        if (c.MaxLatencyMs > 0 && outcome.LatencyMs > c.MaxLatencyMs)
            failures.Add($"Took {outcome.LatencyMs}ms, limit is {c.MaxLatencyMs}ms");

        if (c.MinScore.HasValue)
        {
            var criteria = string.IsNullOrWhiteSpace(c.GradingCriteria)
                ? "The response should be accurate, relevant, coherent and directly address the input."
                : c.GradingCriteria!;

            var score = await _exec.ScoreOutputAsync(output, criteria, ct);
            result.Score = score;

            // A grader that failed to answer must not fail the case — that would
            // report our own outage as the user's regression.
            if (score.HasValue && score.Value < c.MinScore.Value)
                failures.Add($"Scored {score}/10, minimum is {c.MinScore}");
            else if (!score.HasValue)
                _logger.LogWarning("Grader returned no score for case {Case}", c.Id);
        }

        result.Passed = failures.Count == 0;
        result.FailureReasonsJson = JsonSerializer.Serialize(failures);
        return result;
    }

    private async Task AlertRegressionAsync(
        TestSuite suite, TestSuiteRun run, List<TestCaseResult> results, CancellationToken ct)
    {
        var failed = results.Where(r => !r.Passed).Take(5).ToList();
        var summary = $"\"{suite.Name}\" dropped from {run.PreviousPassRate:0.#}% to {run.PassRate:0.#}% on {run.Model}.";

        try
        {
            _notifications.Publish(suite.UserId, JsonSerializer.Serialize(new
            {
                type = "test_regression",
                suiteId = suite.Id,
                suiteName = suite.Name,
                runId = run.Id,
                passRate = run.PassRate,
                previousPassRate = run.PreviousPassRate,
                model = run.Model,
                message = summary
            }));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Regression notification failed"); }

        try
        {
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == suite.UserId, ct);
            if (user?.Email is { Length: > 0 } email)
            {
                var lines = failed.Select(f =>
                {
                    var reasons = string.Join("; ", Deserialize(f.FailureReasonsJson));
                    return $"<li><strong>{System.Net.WebUtility.HtmlEncode(f.CaseName)}</strong>: {System.Net.WebUtility.HtmlEncode(reasons)}</li>";
                });

                var body = $@"
<p>{System.Net.WebUtility.HtmlEncode(summary)}</p>
<p>{run.FailedCases} of {run.TotalCases} cases failed.</p>
<ul>{string.Join("", lines)}</ul>
<p>Nothing on your side needs to have changed for this to happen — model providers
update models in place. Worth checking whether the prompt still holds.</p>";

                await _email.SendEmailAsync(email, $"Prompt regression: {suite.Name}", body);
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Regression email failed"); }
    }

    /// <summary>Next scheduled run, at the configured hour UTC.</summary>
    public static DateTime NextRunFor(TestSuite suite)
    {
        var now = DateTime.UtcNow;
        var next = new DateTime(now.Year, now.Month, now.Day, Math.Clamp(suite.ScheduleHour, 0, 23), 0, 0, DateTimeKind.Utc);

        if (suite.ScheduleCadence.Equals("weekly", StringComparison.OrdinalIgnoreCase))
        {
            next = next.AddDays(7);
        }
        else
        {
            if (next <= now) next = next.AddDays(1);
        }

        return next;
    }

    private static List<string> Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");
}
