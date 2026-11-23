using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Models;
using PromptStudio.Api.Services;
using System.Security.Claims;

namespace PromptStudio.Api.Controllers;

/// <summary>
/// The operations view: what ran, what it cost, what broke.
///
/// This is what turns GenPrompt from a place you write prompts into a place you
/// run them. Everything here reads the trace table that ModelExecutionService
/// writes on every call.
/// </summary>
[Authorize]
[ApiController]
[Route("api/traces")]
public class TracesController : ControllerBase
{
    private readonly PromptDbContext _context;

    public TracesController(PromptDbContext context) => _context = context;

    private string GetCurrentUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    // GET /api/traces - the live feed, filterable
    [HttpGet]
    public async Task<ActionResult> GetTraces(
        [FromQuery] string? promptId,
        [FromQuery] string? model,
        [FromQuery] string? status,
        [FromQuery] string? source,
        [FromQuery] int days = 7,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var userId = GetCurrentUserId();
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 365));

        var q = _context.PromptTraces.Where(t => t.UserId == userId && t.CreatedAt >= since);

        if (!string.IsNullOrWhiteSpace(promptId)) q = q.Where(t => t.PromptId == promptId);
        if (!string.IsNullOrWhiteSpace(model)) q = q.Where(t => t.Model == model);
        if (!string.IsNullOrWhiteSpace(status)) q = q.Where(t => t.Status == status);
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(t => t.Source == source);

        var total = await q.CountAsync();

        pageSize = Math.Clamp(pageSize, 1, 200);
        var items = await q
            .OrderByDescending(t => t.CreatedAt)
            .Skip((Math.Max(page, 1) - 1) * pageSize)
            .Take(pageSize)
            .Select(t => new
            {
                t.Id, t.PromptId, t.PromptName, t.Source, t.Model, t.Provider,
                t.Status, t.ErrorMessage, t.TotalTokens, t.CostUsd, t.LatencyMs,
                t.FellBackFrom, t.InputPreview, t.OutputPreview, t.CreatedAt
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, items });
    }

    // GET /api/traces/summary - cost, latency and errors at a glance
    [HttpGet("summary")]
    public async Task<ActionResult> GetSummary([FromQuery] int days = 30)
    {
        var userId = GetCurrentUserId();
        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 365));

        var traces = await _context.PromptTraces
            .Where(t => t.UserId == userId && t.CreatedAt >= since)
            .Select(t => new { t.PromptId, t.PromptName, t.Model, t.Provider, t.Source, t.Status, t.CostUsd, t.LatencyMs, t.TotalTokens, t.CreatedAt })
            .ToListAsync();

        if (traces.Count == 0)
            return Ok(new { totalCalls = 0, totalCostUsd = 0m, message = "No traffic in this window yet." });

        var latencies = traces.Select(t => (double)t.LatencyMs).ToList();

        var byPrompt = traces
            .Where(t => t.PromptId != null)
            .GroupBy(t => new { t.PromptId, t.PromptName })
            .Select(g => new
            {
                promptId = g.Key.PromptId,
                promptName = g.Key.PromptName,
                calls = g.Count(),
                costUsd = g.Sum(t => t.CostUsd),
                tokens = g.Sum(t => t.TotalTokens),
                errors = g.Count(t => t.Status != "ok"),
                p50LatencyMs = TestSuiteSchedulerService.Percentile(g.Select(t => (double)t.LatencyMs).ToList(), 50),
                p95LatencyMs = TestSuiteSchedulerService.Percentile(g.Select(t => (double)t.LatencyMs).ToList(), 95)
            })
            .OrderByDescending(x => x.costUsd)
            .Take(20)
            .ToList();

        var byModel = traces
            .GroupBy(t => t.Model)
            .Select(g => new
            {
                model = g.Key,
                provider = g.First().Provider,
                calls = g.Count(),
                costUsd = g.Sum(t => t.CostUsd),
                tokens = g.Sum(t => t.TotalTokens),
                errors = g.Count(t => t.Status != "ok"),
                avgLatencyMs = (long)g.Average(t => t.LatencyMs)
            })
            .OrderByDescending(x => x.costUsd)
            .ToList();

        var daily = traces
            .GroupBy(t => t.CreatedAt.Date)
            .Select(g => new
            {
                date = g.Key.ToString("yyyy-MM-dd"),
                calls = g.Count(),
                costUsd = g.Sum(t => t.CostUsd),
                errors = g.Count(t => t.Status != "ok")
            })
            .OrderBy(x => x.date)
            .ToList();

        var bySource = traces
            .GroupBy(t => t.Source)
            .Select(g => new { source = g.Key, calls = g.Count(), costUsd = g.Sum(t => t.CostUsd) })
            .OrderByDescending(x => x.calls)
            .ToList();

        return Ok(new
        {
            windowDays = days,
            totalCalls = traces.Count,
            totalCostUsd = traces.Sum(t => t.CostUsd),
            totalTokens = traces.Sum(t => t.TotalTokens),
            errorCount = traces.Count(t => t.Status != "ok"),
            errorRatePct = Math.Round(100.0 * traces.Count(t => t.Status != "ok") / traces.Count, 2),
            p50LatencyMs = TestSuiteSchedulerService.Percentile(latencies, 50),
            p95LatencyMs = TestSuiteSchedulerService.Percentile(latencies, 95),
            p99LatencyMs = TestSuiteSchedulerService.Percentile(latencies, 99),
            // Straight-line projection from the observed window. Crude, but the
            // question "what will this month cost" deserves an answer.
            projectedMonthlyCostUsd = Math.Round(traces.Sum(t => t.CostUsd) / Math.Max(1, days) * 30, 2),
            byPrompt,
            byModel,
            bySource,
            daily
        });
    }

    // ── Alert rules ─────────────────────────────────────────────────────────

    [HttpGet("alerts")]
    public async Task<ActionResult> GetAlertRules()
    {
        var userId = GetCurrentUserId();
        var rules = await _context.TraceAlertRules
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return Ok(rules.Select(r => new
        {
            r.Id, r.Name, r.Metric, r.PromptId, r.WindowHours, r.Threshold,
            r.IsActive, r.CooldownHours, r.LastFiredAt, r.LastValue, r.CreatedAt
        }));
    }

    [HttpPost("alerts")]
    public async Task<ActionResult> CreateAlertRule([FromBody] SaveAlertRuleRequest request)
    {
        var userId = GetCurrentUserId();

        var allowed = new[] { "cost", "error_rate", "latency_p95", "volume" };
        if (!allowed.Contains(request.Metric))
            return BadRequest(new { error = $"Metric must be one of: {string.Join(", ", allowed)}." });

        if (request.Threshold <= 0)
            return BadRequest(new { error = "Threshold must be greater than zero." });

        var rule = new TraceAlertRule
        {
            UserId = userId,
            Name = string.IsNullOrWhiteSpace(request.Name) ? $"{request.Metric} alert" : request.Name,
            Metric = request.Metric,
            PromptId = request.PromptId,
            WindowHours = Math.Clamp(request.WindowHours, 1, 720),
            Threshold = request.Threshold,
            CooldownHours = Math.Clamp(request.CooldownHours, 1, 168),
            IsActive = true
        };

        _context.TraceAlertRules.Add(rule);
        await _context.SaveChangesAsync();
        return Ok(new { rule.Id, rule.Name, rule.Metric, rule.Threshold });
    }

    [HttpPut("alerts/{id}")]
    public async Task<ActionResult> UpdateAlertRule(string id, [FromBody] SaveAlertRuleRequest request)
    {
        var userId = GetCurrentUserId();
        var rule = await _context.TraceAlertRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (rule == null) return NotFound();

        rule.Name = request.Name ?? rule.Name;
        rule.Metric = request.Metric ?? rule.Metric;
        rule.PromptId = request.PromptId;
        rule.WindowHours = Math.Clamp(request.WindowHours, 1, 720);
        rule.Threshold = request.Threshold > 0 ? request.Threshold : rule.Threshold;
        rule.CooldownHours = Math.Clamp(request.CooldownHours, 1, 168);
        rule.IsActive = request.IsActive;

        await _context.SaveChangesAsync();
        return Ok(new { rule.Id, rule.Name, rule.IsActive });
    }

    [HttpDelete("alerts/{id}")]
    public async Task<ActionResult> DeleteAlertRule(string id)
    {
        var userId = GetCurrentUserId();
        var rule = await _context.TraceAlertRules.FirstOrDefaultAsync(r => r.Id == id && r.UserId == userId);
        if (rule == null) return NotFound();

        _context.TraceAlertRules.Remove(rule);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}

public class SaveAlertRuleRequest
{
    public string? Name { get; set; }
    public string Metric { get; set; } = "cost";
    public string? PromptId { get; set; }
    public int WindowHours { get; set; } = 24;
    public double Threshold { get; set; }
    public int CooldownHours { get; set; } = 6;
    public bool IsActive { get; set; } = true;
}
