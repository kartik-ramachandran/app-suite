using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Prism.Api.Data;
using Prism.Api.Models;
using Prism.Api.Services;

namespace Prism.Api.Controllers;

/// <summary>
/// Prism API — Osmos-style autonomous data engineering. Upload a messy
/// tabular file (CSV / TSV / JSON / XLSX) and let AI agents profile it, map it
/// onto a target schema, clean it, dedupe it, validate it against natural-
/// language rules, transform it, convert it, or extract tables from raw text.
///
/// Every tool runs ASYNCHRONOUSLY: the endpoint saves the upload, logs a
/// ModuleJob, queues the work on a background worker and returns 202 with the
/// jobId. Track it via GET job/{id} (or the notifications SSE stream), then
/// fetch the report and stream the output file when it completes. Results are
/// kept for 24 hours.
///
/// Metered as a sellable API by PdfApiKeyMiddleware; gated for self-hosted
/// installs by OnPremLicenceMiddleware.
/// </summary>
[ApiController]
[Route("api/prism")]
[Authorize]
[RequestSizeLimit(209_715_200)]                                  // 200 MB
[RequestFormLimits(MultipartBodyLengthLimit = 209_715_200)]
public class PrismController : ControllerBase
{
    private const string ModuleName = "prism";

    private readonly JobLogService _jobLog;
    private readonly PromptDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PrismController> _logger;

    public PrismController(
        JobLogService jobLog,
        PromptDbContext db,
        IServiceScopeFactory scopeFactory,
        ILogger<PrismController> logger)
    {
        _jobLog = jobLog;
        _db = db;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    private static bool NoFile(IFormFile? file) => file is null || file.Length == 0;

    // ── Queueing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Persist the upload (the IFormFile stream dies with this request), log a
    /// ModuleJob, kick off the work in the background, and return 202 + jobId.
    /// </summary>
    private async Task<IActionResult> QueueTool(string tool, string title, IFormFile? file, Dictionary<string, string> args)
    {
        PrismService.CleanupExpiredResults();

        DirectoryInfo? inputDir = null;
        string? inputPath = null;
        var originalName = file?.FileName;

        if (!NoFile(file))
        {
            inputDir = Directory.CreateTempSubdirectory("prism-in-");
            inputPath = Path.Combine(inputDir.FullName, "input" + Path.GetExtension(file!.FileName));
            await using var fs = System.IO.File.Create(inputPath);
            await file.CopyToAsync(fs);
        }

        var job = await _jobLog.StartAsync(GetUserId(), ModuleName, tool, title);

        // Fire-and-forget on a fresh DI scope — the request's scoped services are
        // disposed as soon as we return 202.
        _ = Task.Run(() => ExecuteJobAsync(job.Id, tool, inputPath, inputDir, originalName, args));

        return Accepted(new { jobId = job.Id, status = "running" });
    }

    private async Task ExecuteJobAsync(int jobId, string tool, string? inputPath, DirectoryInfo? inputDir, string? originalName, Dictionary<string, string> args)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PromptDbContext>();
            var jobLog = scope.ServiceProvider.GetRequiredService<JobLogService>();
            var service = scope.ServiceProvider.GetRequiredService<PrismService>();

            var job = await db.ModuleJobs.FindAsync(jobId);
            if (job == null) return;

            try
            {
                var result = await service.RunToolAsync(tool, inputPath, originalName, args);
                await PrismService.SaveJobResultAsync(jobId, result);
                await jobLog.CompleteAsync(job, result.Headline);

                // Weave the run's summary (headline/metrics/findings — never the dataset)
                // into the knowledge cosmos. Resolved from THIS background scope: the
                // controller's request-scoped services are long gone by now.
                var weaver = scope.ServiceProvider.GetRequiredService<KnowledgeWeaverService>();
                await weaver.CaptureAsync(job.UserId, job.Title, PrismSummaryText(result), "prism", "fetched", $"prism-job:{jobId}");
            }
            catch (InvalidOperationException ex)
            {
                await jobLog.FailAsync(job, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Prism job {JobId} ({Tool}) failed", jobId, tool);
                await jobLog.FailAsync(job, "Something went wrong processing this file. Make sure it is a valid CSV, TSV, JSON, or XLSX file.");
            }
        }
        catch (Exception ex)
        {
            // Scope/DB failure — nothing left to report to but the log.
            _logger.LogError(ex, "Prism job {JobId} could not record its outcome", jobId);
        }
        finally
        {
            try { inputDir?.Delete(true); } catch { /* best effort */ }
        }
    }

    // Flatten a run's report into plain summary text (headline, metrics, section
    // findings) — deliberately excludes the preview rows and output file.
    private static string PrismSummaryText(PrismResult result)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(result.Headline)) sb.AppendLine(result.Headline);
        foreach (var m in result.Metrics) sb.AppendLine($"{m.Label}: {m.Value}");
        foreach (var s in result.Sections)
        {
            if (!string.IsNullOrWhiteSpace(s.Heading)) sb.AppendLine(s.Heading);
            foreach (var item in s.Items) sb.AppendLine($"- {item}");
        }
        return sb.ToString().Trim();
    }

    // ── Tool endpoints (all return 202 + jobId) ──────────────────────────────

    /// <summary>Profile a dataset: types, completeness, quality issues, recommendations.</summary>
    [HttpPost("profile")]
    public Task<IActionResult> Profile(IFormFile? file)
    {
        if (NoFile(file)) return Task.FromResult<IActionResult>(BadRequest(new { error = "Upload a data file (field 'file')." }));
        return QueueTool("profile", $"Profile: {file!.FileName}", file, new Dictionary<string, string>());
    }

    /// <summary>Map a messy file onto a target schema (AutoMap).</summary>
    [HttpPost("map")]
    public Task<IActionResult> Map(IFormFile? file, [FromForm] string? targetSchema, [FromForm] string? notes)
    {
        if (NoFile(file)) return Task.FromResult<IActionResult>(BadRequest(new { error = "Upload a data file (field 'file')." }));
        if (string.IsNullOrWhiteSpace(targetSchema))
            return Task.FromResult<IActionResult>(BadRequest(new { error = "Provide the target schema (field 'targetSchema') — one column per line." }));
        return QueueTool("map", $"Schema map: {file!.FileName}", file,
            new Dictionary<string, string> { ["targetSchema"] = targetSchema, ["notes"] = notes ?? string.Empty });
    }

    /// <summary>Auto-detect and fix formatting inconsistencies (AutoClean).</summary>
    [HttpPost("clean")]
    public Task<IActionResult> Clean(IFormFile? file, [FromForm] string? notes)
    {
        if (NoFile(file)) return Task.FromResult<IActionResult>(BadRequest(new { error = "Upload a data file (field 'file')." }));
        return QueueTool("clean", $"Clean: {file!.FileName}", file,
            new Dictionary<string, string> { ["notes"] = notes ?? string.Empty });
    }

    /// <summary>Detect and remove duplicate records using an AI-chosen identity key.</summary>
    [HttpPost("dedupe")]
    public Task<IActionResult> Dedupe(IFormFile? file, [FromForm] string? notes)
    {
        if (NoFile(file)) return Task.FromResult<IActionResult>(BadRequest(new { error = "Upload a data file (field 'file')." }));
        return QueueTool("dedupe", $"Dedupe: {file!.FileName}", file,
            new Dictionary<string, string> { ["notes"] = notes ?? string.Empty });
    }

    /// <summary>Validate every row against natural-language rules (or AI-proposed checks).</summary>
    [HttpPost("validate")]
    public Task<IActionResult> Validate(IFormFile? file, [FromForm] string? rules)
    {
        if (NoFile(file)) return Task.FromResult<IActionResult>(BadRequest(new { error = "Upload a data file (field 'file')." }));
        return QueueTool("validate", $"Validate: {file!.FileName}", file,
            new Dictionary<string, string> { ["rules"] = rules ?? string.Empty });
    }

    /// <summary>Transform the data with a natural-language instruction.</summary>
    [HttpPost("transform")]
    public Task<IActionResult> Transform(IFormFile? file, [FromForm] string? instruction)
    {
        if (NoFile(file)) return Task.FromResult<IActionResult>(BadRequest(new { error = "Upload a data file (field 'file')." }));
        if (string.IsNullOrWhiteSpace(instruction))
            return Task.FromResult<IActionResult>(BadRequest(new { error = "Describe the transformation (field 'instruction')." }));
        return QueueTool("transform", $"Transform: {file!.FileName}", file,
            new Dictionary<string, string> { ["instruction"] = instruction });
    }

    /// <summary>Convert between CSV, JSON, and XLSX. Deterministic — no AI involved.</summary>
    [HttpPost("convert")]
    public Task<IActionResult> Convert(IFormFile? file, [FromForm] string? format)
    {
        if (NoFile(file)) return Task.FromResult<IActionResult>(BadRequest(new { error = "Upload a data file (field 'file')." }));
        var target = string.IsNullOrWhiteSpace(format) ? "csv" : format.Trim().ToLowerInvariant();
        if (target is not ("csv" or "json" or "xlsx"))
            return Task.FromResult<IActionResult>(BadRequest(new { error = "Format must be csv, json, or xlsx." }));
        return QueueTool("convert", $"Convert: {file!.FileName} → {target.ToUpperInvariant()}", file,
            new Dictionary<string, string> { ["format"] = target });
    }

    /// <summary>Extract a structured table from unstructured text (paste or .txt upload).</summary>
    [HttpPost("extract")]
    public Task<IActionResult> Extract(IFormFile? file, [FromForm] string? text)
    {
        if (string.IsNullOrWhiteSpace(text) && NoFile(file))
            return Task.FromResult<IActionResult>(BadRequest(new { error = "Paste some text (field 'text') or upload a text file (field 'file')." }));
        var label = !NoFile(file) ? file!.FileName : $"{text!.Length:N0} characters of text";
        return QueueTool("extract", $"Extract table: {label}", file,
            new Dictionary<string, string> { ["text"] = text ?? string.Empty });
    }

    // ── Job tracking & results ───────────────────────────────────────────────

    /// <summary>Status of a job; includes the full report once completed.</summary>
    [HttpGet("job/{id:int}")]
    public async Task<IActionResult> GetJob(int id)
    {
        var job = await _db.ModuleJobs
            .FirstOrDefaultAsync(j => j.Id == id && j.UserId == GetUserId() && j.Module == ModuleName);
        if (job == null) return NotFound(new { error = "Job not found." });

        var report = job.Status == "completed" ? PrismService.LoadJobReport(id) : null;
        return Ok(new
        {
            jobId = job.Id,
            tool = job.JobType,
            title = job.Title,
            status = job.Status,
            error = job.ErrorMessage,
            createdAt = job.CreatedAt,
            completedAt = job.CompletedAt,
            durationMs = job.DurationMs,
            report,
            resultExpired = job.Status == "completed" && report == null,
            hasDownload = report?.Download != null && PrismService.GetJobOutputPath(id) != null,
        });
    }

    /// <summary>Stream a completed job's output file.</summary>
    [HttpGet("job/{id:int}/download")]
    public async Task<IActionResult> DownloadJobOutput(int id)
    {
        var owns = await _db.ModuleJobs
            .AnyAsync(j => j.Id == id && j.UserId == GetUserId() && j.Module == ModuleName);
        if (!owns) return NotFound(new { error = "Job not found." });

        var report = PrismService.LoadJobReport(id);
        var path = PrismService.GetJobOutputPath(id);
        if (report?.Download == null || path == null)
            return NotFound(new { error = "The output file for this job is not available (results are kept for 24 hours)." });

        return PhysicalFile(path, report.Download.ContentType, report.Download.FileName);
    }

    /// <summary>The user's recent Prism jobs (for the page's job panel).</summary>
    [HttpGet("jobs")]
    public async Task<IActionResult> ListJobs([FromQuery] int take = 30)
    {
        take = Math.Clamp(take, 1, 100);
        var userId = GetUserId();
        var rows = await _db.ModuleJobs
            .Where(j => j.UserId == userId && j.Module == ModuleName)
            .OrderByDescending(j => j.CreatedAt)
            .Take(take)
            .Select(j => new { j.Id, j.JobType, j.Title, j.Status, j.ErrorMessage, j.CreatedAt, j.CompletedAt, j.DurationMs })
            .ToListAsync();

        var items = rows.Select(j => new
        {
            jobId = j.Id,
            tool = j.JobType,
            title = j.Title,
            status = j.Status,
            error = j.ErrorMessage,
            createdAt = j.CreatedAt,
            completedAt = j.CompletedAt,
            durationMs = j.DurationMs,
            hasResult = j.Status == "completed" && PrismService.LoadJobReport(j.Id) != null,
        });
        return Ok(new { items });
    }
}
