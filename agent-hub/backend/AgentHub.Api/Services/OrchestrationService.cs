using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentHub.Api.Services;

/// <summary>
/// Turns a goal into a task graph and persists it. Planning is deliberately a
/// separate step from execution: the plan is stored before anything runs, so a
/// crash between planning and execution costs nothing, and the user can see
/// what is about to happen.
/// </summary>
public class OrchestrationService
{
    private readonly PromptDbContext _db;
    private readonly IAIService _ai;
    private readonly ToolRegistry _tools;
    private readonly ILogger<OrchestrationService> _logger;

    public OrchestrationService(PromptDbContext db, IAIService ai, ToolRegistry tools, ILogger<OrchestrationService> logger)
    {
        _db = db;
        _ai = ai;
        _tools = tools;
        _logger = logger;
    }

    public async Task<OrchestrationRun> CreateRunAsync(string userId, string goal, Guid? sourceNodeId, CancellationToken ct = default)
    {
        var run = new OrchestrationRun
        {
            UserId = userId,
            Goal = goal.Trim(),
            Status = "planning",
            SourceNodeId = sourceNodeId,
        };
        _db.OrchestrationRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Plans the run. On failure the run is marked failed with a readable reason.</summary>
    public async Task PlanAsync(OrchestrationRun run, CancellationToken ct = default)
    {
        try
        {
            var tools = await _tools.GetToolsAsync(run.UserId, ct);
            var catalogue = string.Join("\n", tools.Select(t =>
                $"- {t.Id} — {t.Description} Arguments: {t.ArgumentsHint}"));

            var response = await _ai.TestPromptAsync(new PromptTestRequest
            {
                PromptContent =
                    "You plan work for an autonomous agent. Break the user's goal into the SMALLEST set of concrete steps that fully achieves it.\n\n" +
                    "Available tools:\n" + catalogue + "\n\n" +
                    "Rules:\n" +
                    "- Use ONLY tool ids from the list above, exactly as written.\n" +
                    "- Every step needs a unique snake_case key.\n" +
                    "- dependsOn lists the keys whose output this step needs. Steps with no dependencies run in parallel, so do not chain steps that are genuinely independent.\n" +
                    "- To use an earlier step's output inside an argument, write {{that_step_key}}. It is replaced with that step's actual output before the step runs.\n" +
                    "- The FINAL step must assemble the finished deliverable for the user, normally with ai.generate.\n" +
                    "- Prefer 3-8 steps. Never exceed 20.\n\n" +
                    "Respond with ONLY minified JSON, no code fences:\n" +
                    "{\"summary\":\"<one sentence describing the approach>\",\"tasks\":[{\"key\":\"<snake_case>\",\"title\":\"<short label>\",\"tool\":\"<tool id>\",\"arguments\":{...},\"dependsOn\":[\"<key>\"]}]}",
                UserInput = $"Goal: {run.Goal}",
            });

            var raw = (response.Result ?? "").Trim();
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) throw new InvalidOperationException("The planner did not return a plan.");

            using var doc = JsonDocument.Parse(raw.Substring(start, end - start + 1));
            var root = doc.RootElement;

            run.PlanSummary = root.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "" : "";

            if (!root.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array || tasks.GetArrayLength() == 0)
                throw new InvalidOperationException("The planner returned no steps.");

            var known = (await _tools.GetToolsAsync(run.UserId, ct)).ToDictionary(t => t.Id, t => t);
            var order = 0;
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var task in tasks.EnumerateArray())
            {
                var key = task.TryGetProperty("key", out var k) ? k.GetString() : null;
                var toolId = task.TryGetProperty("tool", out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(toolId)) continue;

                // A hallucinated tool id must not become a task that fails at run time.
                if (!known.TryGetValue(toolId!, out var descriptor))
                {
                    _logger.LogInformation("Planner proposed unknown tool {Tool}; dropping step {Key}.", toolId, key);
                    continue;
                }
                if (!keys.Add(key!)) continue;

                _db.OrchestrationTasks.Add(new OrchestrationTask
                {
                    RunId = run.Id,
                    UserId = run.UserId,
                    Key = key!,
                    Title = task.TryGetProperty("title", out var title) ? title.GetString() ?? key! : key!,
                    Tool = toolId!,
                    ArgumentsJson = task.TryGetProperty("arguments", out var args) ? args.GetRawText() : "{}",
                    DependsOnJson = task.TryGetProperty("dependsOn", out var deps) && deps.ValueKind == JsonValueKind.Array
                        ? deps.GetRawText() : "[]",
                    NeedsApproval = descriptor.NeedsApproval,
                    OrderIndex = order++,
                });
            }

            if (order == 0) throw new InvalidOperationException("None of the planned steps used an available tool.");

            run.Status = "running";
            run.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("Run {RunId} planned with {Count} steps.", run.Id, order);
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.ErrorMessage = ex.Message.Length > 300 ? ex.Message.Substring(0, 300) : ex.Message;
            run.UpdatedAt = DateTime.UtcNow;
            run.CompletedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogWarning(ex, "Planning failed for run {RunId}.", run.Id);
        }
    }

    /// <summary>Substitutes {{task_key}} with that task's output.</summary>
    public static string Interpolate(string arguments, IReadOnlyDictionary<string, string> outputs)
        => Regex.Replace(arguments, @"\{\{\s*([a-zA-Z0-9_]+)\s*\}\}", match =>
        {
            if (!outputs.TryGetValue(match.Groups[1].Value, out var value)) return match.Value;
            // The arguments blob is JSON, so the substituted text must be escaped
            // or a quote in an upstream result corrupts the whole document.
            var encoded = JsonEncodedText.Encode(value).ToString();
            return encoded;
        });
}
