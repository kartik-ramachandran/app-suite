using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Models;
using PromptStudio.Api.Services;
using System.Security.Claims;

namespace PromptStudio.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/test-suites")]
public class TestSuitesController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly TestSuiteRunner _runner;
    private readonly ILogger<TestSuitesController> _logger;

    public TestSuitesController(PromptDbContext context, TestSuiteRunner runner, ILogger<TestSuitesController> logger)
    {
        _context = context;
        _runner = runner;
        _logger = logger;
    }

    private string GetCurrentUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    [HttpGet]
    public async Task<ActionResult> GetSuites()
    {
        var userId = GetCurrentUserId();

        var suites = await _context.TestSuites
            .Where(s => s.UserId == userId && s.IsActive)
            .OrderByDescending(s => s.UpdatedAt)
            .Select(s => new
            {
                s.Id,
                s.Name,
                s.Description,
                s.PromptId,
                s.Model,
                s.ScheduleEnabled,
                s.ScheduleCadence,
                s.ScheduleHour,
                s.NextRunAt,
                s.LastRunAt,
                s.LastPassRate,
                s.AlertOnRegression,
                s.CreatedAt,
                caseCount = _context.TestCases.Count(c => c.TestSuiteId == s.Id && c.IsActive)
            })
            .ToListAsync();

        return Ok(suites);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult> GetSuite(string id)
    {
        var userId = GetCurrentUserId();
        var suite = await _context.TestSuites.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (suite == null) return NotFound();

        var cases = await _context.TestCases
            .Where(c => c.TestSuiteId == id)
            .OrderBy(c => c.SortOrder)
            .ToListAsync();

        var runs = await _context.TestSuiteRuns
            .Where(r => r.TestSuiteId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(20)
            .Select(r => new
            {
                r.Id, r.Trigger, r.Status, r.Model, r.TotalCases, r.PassedCases, r.FailedCases,
                r.PassRate, r.PreviousPassRate, r.IsRegression, r.TotalCostUsd, r.TotalLatencyMs,
                r.StartedAt, r.CompletedAt
            })
            .ToListAsync();

        return Ok(new
        {
            suite.Id, suite.Name, suite.Description, suite.PromptId, suite.PromptContent, suite.Model,
            suite.ScheduleEnabled, suite.ScheduleCadence, suite.ScheduleHour, suite.NextRunAt,
            suite.LastRunAt, suite.LastPassRate, suite.AlertOnRegression, suite.CreatedAt,
            cases = cases.Select(Shape),
            runs
        });
    }

    [HttpPost]
    public async Task<ActionResult> CreateSuite([FromBody] SaveTestSuiteRequest request)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "The suite needs a name." });

        if (string.IsNullOrWhiteSpace(request.PromptId) && string.IsNullOrWhiteSpace(request.PromptContent))
            return BadRequest(new { error = "Link a prompt or provide prompt content to test." });

        if (!string.IsNullOrWhiteSpace(request.PromptId))
        {
            var owns = await _context.Prompts.AnyAsync(p => p.Id == request.PromptId && p.UserId == userId);
            if (!owns) return BadRequest(new { error = "That prompt does not exist." });
        }

        var suite = new TestSuite
        {
            UserId = userId,
            Name = request.Name,
            Description = request.Description,
            PromptId = request.PromptId,
            PromptContent = request.PromptContent,
            Model = request.Model,
            ScheduleEnabled = request.ScheduleEnabled,
            ScheduleCadence = request.ScheduleCadence ?? "daily",
            ScheduleHour = Math.Clamp(request.ScheduleHour, 0, 23),
            AlertOnRegression = request.AlertOnRegression
        };
        suite.NextRunAt = suite.ScheduleEnabled ? TestSuiteRunner.NextRunFor(suite) : null;

        _context.TestSuites.Add(suite);

        if (request.Cases != null)
        {
            var i = 0;
            foreach (var c in request.Cases)
                _context.TestCases.Add(BuildCase(suite.Id, c, i++));
        }

        await _context.SaveChangesAsync();
        return Ok(new { suite.Id, suite.Name, suite.NextRunAt });
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> UpdateSuite(string id, [FromBody] SaveTestSuiteRequest request)
    {
        var userId = GetCurrentUserId();
        var suite = await _context.TestSuites.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (suite == null) return NotFound();

        suite.Name = request.Name ?? suite.Name;
        suite.Description = request.Description;
        suite.PromptId = request.PromptId;
        suite.PromptContent = request.PromptContent;
        suite.Model = request.Model;
        suite.ScheduleEnabled = request.ScheduleEnabled;
        suite.ScheduleCadence = request.ScheduleCadence ?? suite.ScheduleCadence;
        suite.ScheduleHour = Math.Clamp(request.ScheduleHour, 0, 23);
        suite.AlertOnRegression = request.AlertOnRegression;
        suite.NextRunAt = suite.ScheduleEnabled ? TestSuiteRunner.NextRunFor(suite) : null;
        suite.UpdatedAt = DateTime.UtcNow;

        // Cases are replaced wholesale when supplied — simpler than reconciling,
        // and the run history keeps the record of what previous cases were.
        if (request.Cases != null)
        {
            var existing = await _context.TestCases.Where(c => c.TestSuiteId == id).ToListAsync();
            _context.TestCases.RemoveRange(existing);

            var i = 0;
            foreach (var c in request.Cases)
                _context.TestCases.Add(BuildCase(suite.Id, c, i++));
        }

        await _context.SaveChangesAsync();
        return Ok(new { suite.Id, suite.Name, suite.NextRunAt });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteSuite(string id)
    {
        var userId = GetCurrentUserId();
        var suite = await _context.TestSuites.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (suite == null) return NotFound();

        _context.TestSuites.Remove(suite);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // POST /api/test-suites/{id}/run
    //
    // Also the CI entry point: ApiKeyAuthMiddleware already authenticates a
    // non-browser caller, so a GitHub Action can hit this with a key header.
    [HttpPost("{id}/run")]
    public async Task<ActionResult> RunSuite(string id, [FromQuery] string trigger = "manual")
    {
        var userId = GetCurrentUserId();
        var suite = await _context.TestSuites.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId);
        if (suite == null) return NotFound();

        try
        {
            var run = await _runner.RunAsync(suite, trigger == "ci" ? "ci" : "manual");

            var results = await _context.TestCaseResults
                .Where(r => r.TestSuiteRunId == run.Id)
                .ToListAsync();

            return Ok(new
            {
                runId = run.Id,
                run.Status,
                run.Model,
                run.TotalCases,
                run.PassedCases,
                run.FailedCases,
                run.PassRate,
                run.PreviousPassRate,
                run.IsRegression,
                run.TotalTokens,
                run.TotalCostUsd,
                run.TotalLatencyMs,
                run.ErrorMessage,
                results = results.Select(r => new
                {
                    r.CaseName,
                    r.Passed,
                    failureReasons = Safe(r.FailureReasonsJson),
                    r.Output,
                    r.Score,
                    r.TokensUsed,
                    r.LatencyMs,
                    r.CostUsd
                })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test suite {Id} failed to run", id);
            return StatusCode(500, new { error = "The suite could not be run.", message = ex.Message });
        }
    }

    [HttpGet("{id}/runs")]
    public async Task<ActionResult> GetRuns(string id, [FromQuery] int limit = 50)
    {
        var userId = GetCurrentUserId();
        var owns = await _context.TestSuites.AnyAsync(s => s.Id == id && s.UserId == userId);
        if (!owns) return NotFound();

        var runs = await _context.TestSuiteRuns
            .Where(r => r.TestSuiteId == id)
            .OrderByDescending(r => r.StartedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync();

        return Ok(runs.Select(r => new
        {
            r.Id, r.Trigger, r.Status, r.Model, r.TotalCases, r.PassedCases, r.FailedCases,
            r.PassRate, r.PreviousPassRate, r.IsRegression, r.TotalTokens, r.TotalCostUsd,
            r.TotalLatencyMs, r.ErrorMessage, r.StartedAt, r.CompletedAt
        }));
    }

    [HttpGet("runs/{runId}")]
    public async Task<ActionResult> GetRun(string runId)
    {
        var userId = GetCurrentUserId();
        var run = await _context.TestSuiteRuns.FirstOrDefaultAsync(r => r.Id == runId && r.UserId == userId);
        if (run == null) return NotFound();

        var results = await _context.TestCaseResults
            .Where(r => r.TestSuiteRunId == runId)
            .ToListAsync();

        return Ok(new
        {
            run.Id, run.TestSuiteId, run.Trigger, run.Status, run.Model,
            run.TotalCases, run.PassedCases, run.FailedCases, run.PassRate,
            run.PreviousPassRate, run.IsRegression, run.TotalTokens, run.TotalCostUsd,
            run.TotalLatencyMs, run.StartedAt, run.CompletedAt,
            results = results.Select(r => new
            {
                r.CaseName, r.Passed, failureReasons = Safe(r.FailureReasonsJson),
                r.Output, r.Score, r.TokensUsed, r.LatencyMs, r.CostUsd
            })
        });
    }

    private static TestCase BuildCase(string suiteId, TestCaseDto dto, int order) => new()
    {
        TestSuiteId = suiteId,
        Name = dto.Name ?? $"Case {order + 1}",
        Input = dto.Input ?? string.Empty,
        ExpectedContainsJson = JsonSerializer.Serialize(dto.ExpectedContains ?? []),
        ExpectedNotContainsJson = JsonSerializer.Serialize(dto.ExpectedNotContains ?? []),
        ExpectedRegex = dto.ExpectedRegex,
        MinScore = dto.MinScore,
        GradingCriteria = dto.GradingCriteria,
        JsonSchema = dto.JsonSchema,
        MaxLatencyMs = dto.MaxLatencyMs,
        SortOrder = order
    };

    private static object Shape(TestCase c) => new
    {
        c.Id,
        c.Name,
        c.Input,
        expectedContains = Safe(c.ExpectedContainsJson),
        expectedNotContains = Safe(c.ExpectedNotContainsJson),
        c.ExpectedRegex,
        c.MinScore,
        c.GradingCriteria,
        c.JsonSchema,
        c.MaxLatencyMs,
        c.SortOrder
    };

    private static List<string> Safe(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }
}

public class SaveTestSuiteRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? PromptId { get; set; }
    public string? PromptContent { get; set; }
    public string? Model { get; set; }
    public bool ScheduleEnabled { get; set; }
    public string? ScheduleCadence { get; set; }
    public int ScheduleHour { get; set; } = 3;
    public bool AlertOnRegression { get; set; } = true;
    public List<TestCaseDto>? Cases { get; set; }
}

public class TestCaseDto
{
    public string? Name { get; set; }
    public string? Input { get; set; }
    public List<string>? ExpectedContains { get; set; }
    public List<string>? ExpectedNotContains { get; set; }
    public string? ExpectedRegex { get; set; }
    public int? MinScore { get; set; }
    public string? GradingCriteria { get; set; }
    public string? JsonSchema { get; set; }
    public int MaxLatencyMs { get; set; }
}
