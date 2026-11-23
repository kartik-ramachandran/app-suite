using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InteractionIntel.Api.Data;
using InteractionIntel.Api.Models;
using InteractionIntel.Api.Services;

namespace InteractionIntel.Api.Controllers;

/// <summary>
/// Interaction Intelligence API — turns any recorded interaction (support call,
/// patient consult, advisory meeting, sales call, interview …) into a transcript
/// plus structured insight across 8 tools. Industry-agnostic by design.
/// Metered as a sellable API by PdfApiKeyMiddleware; gated for self-hosted
/// installs by OnPremLicenceMiddleware.
/// </summary>
[ApiController]
[Route("api/interaction")]
[Authorize]
public class InteractionController : ControllerBase
{
    private const string ModuleName = "interaction-intelligence";

    // camelCase to match the HTTP API responses the frontend already consumes.
    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);

    private readonly InteractionService _service;
    private readonly JobLogService _jobLog;
    private readonly PromptDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<InteractionController> _logger;

    public InteractionController(
        InteractionService service,
        JobLogService jobLog,
        PromptDbContext db,
        IHttpClientFactory httpClientFactory,
        ILogger<InteractionController> logger)
    {
        _service = service;
        _jobLog = jobLog;
        _db = db;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    /// <summary>
    /// Transcribe a recording. Accepts EITHER an uploaded audio/video file
    /// (multipart field "file") OR a direct link (field "url"). Handles large
    /// and video files via server-side audio extraction + chunking.
    /// </summary>
    [HttpPost("transcribe")]
    [RequestSizeLimit(1_073_741_824)]            // 1 GB
    [RequestFormLimits(MultipartBodyLengthLimit = 1_073_741_824)]
    public async Task<IActionResult> Transcribe(IFormFile? file = null, [FromForm] string? url = null, [FromForm] string? intent = null)
    {
        // Validate input before logging a job so empty requests don't create noise.
        var hasFile = file is not null && file.Length > 0;
        if (!hasFile && string.IsNullOrWhiteSpace(url))
            return BadRequest(new { error = "Upload a recording (field 'file') or share a link (field 'url')." });

        var label = hasFile ? file!.FileName : url!;
        var job = await _jobLog.StartAsync(GetUserId(), ModuleName, "transcribe", $"Transcribe: {label}");

        var tempDir = Directory.CreateTempSubdirectory("interaction-in-");
        var inputPath = Path.Combine(tempDir.FullName, "input");

        try
        {
            if (hasFile)
            {
                await using var fs = System.IO.File.Create(inputPath);
                await file!.CopyToAsync(fs);
            }
            else
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    await _jobLog.FailAsync(job, "Invalid link.");
                    return BadRequest(new { error = "Provide a valid http(s) link to an audio or video file." });
                }

                var http = _httpClientFactory.CreateClient();
                http.Timeout = TimeSpan.FromMinutes(10);
                await using var remote = await http.GetStreamAsync(uri);
                await using var fs = System.IO.File.Create(inputPath);
                await remote.CopyToAsync(fs);
            }

            var transcript = await _service.TranscribeAsync(inputPath);
            if (string.IsNullOrWhiteSpace(transcript))
            {
                await _jobLog.FailAsync(job, "No speech could be transcribed from this recording.");
                return UnprocessableEntity(new { error = "No speech could be transcribed from this recording." });
            }

            await _jobLog.CompleteAsync(job, $"Transcript ready — {transcript.Length:N0} characters");

            // Persist as a session so the page can be restored later.
            var session = new InteractionSession
            {
                UserId = GetUserId(),
                SourceLabel = label,
                Transcript = transcript,
                AnalysesJson = "{}",
                Intent = CleanIntent(intent),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.InteractionSessions.Add(session);
            await _db.SaveChangesAsync();

            return Ok(new { transcript, sessionId = session.Id });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to fetch recording from URL");
            await _jobLog.FailAsync(job, "Could not download the recording from that link.");
            return BadRequest(new { error = "Could not download the recording from that link. Make sure it points directly to an audio/video file and is publicly accessible." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transcribing interaction");
            await _jobLog.FailAsync(job, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        finally
        {
            try { tempDir.Delete(true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Ingest an ALREADY-WRITTEN transcript — pasted text, or an uploaded
    /// .txt/.md/.vtt/.srt/.pdf/.docx file (Teams/Zoom exports included). Skips
    /// speech-to-text entirely and returns the same shape as /transcribe so the
    /// analysis tools work identically.
    /// </summary>
    [HttpPost("transcript")]
    [RequestSizeLimit(52_428_800)]                               // 50 MB
    [RequestFormLimits(MultipartBodyLengthLimit = 52_428_800)]
    public async Task<IActionResult> Transcript(IFormFile? file = null, [FromForm] string? text = null, [FromForm] string? intent = null)
    {
        var hasFile = file is not null && file.Length > 0;
        var hasText = !string.IsNullOrWhiteSpace(text);
        if (!hasFile && !hasText)
            return BadRequest(new { error = "Paste a transcript (field 'text') or upload a transcript file (field 'file')." });

        if (hasFile && !InteractionService.IsSupportedTranscriptFile(file!.FileName))
            return BadRequest(new { error = "Unsupported file. Upload a .txt, .md, .vtt, .srt, .pdf, or .docx transcript (or paste the text)." });

        var label = hasFile ? file!.FileName : "Pasted transcript";
        var job = await _jobLog.StartAsync(GetUserId(), ModuleName, "transcript", $"Transcript: {label}");

        DirectoryInfo? tempDir = null;
        try
        {
            string transcript;
            if (hasFile)
            {
                tempDir = Directory.CreateTempSubdirectory("interaction-txt-");
                var inputPath = Path.Combine(tempDir.FullName, "input" + Path.GetExtension(file!.FileName));
                await using (var fs = System.IO.File.Create(inputPath))
                    await file.CopyToAsync(fs);
                transcript = await _service.ExtractTranscriptFromFileAsync(inputPath, file.FileName);
            }
            else
            {
                transcript = InteractionService.CleanPastedTranscript(text!);
            }

            if (string.IsNullOrWhiteSpace(transcript))
            {
                await _jobLog.FailAsync(job, "No transcript text could be read.");
                return UnprocessableEntity(new { error = "No transcript text could be read from that input." });
            }

            await _jobLog.CompleteAsync(job, $"Transcript ready — {transcript.Length:N0} characters");

            var session = new InteractionSession
            {
                UserId = GetUserId(),
                SourceLabel = label,
                Transcript = transcript,
                AnalysesJson = "{}",
                Intent = CleanIntent(intent),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.InteractionSessions.Add(session);
            await _db.SaveChangesAsync();

            return Ok(new { transcript, sessionId = session.Id });
        }
        catch (InvalidOperationException ex)
        {
            await _jobLog.FailAsync(job, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error ingesting transcript");
            await _jobLog.FailAsync(job, ex.Message);
            return BadRequest(new { error = "Couldn't read a transcript from that input. Make sure it's a valid text, caption, PDF, or Word file." });
        }
        finally
        {
            try { tempDir?.Delete(true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Run one of the Interaction Intelligence tools over a transcript.
    /// </summary>
    [HttpPost("analyse")]
    public async Task<IActionResult> Analyse([FromBody] AnalyseRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request?.Transcript))
                return BadRequest(new { error = "A transcript is required." });

            var tool = string.IsNullOrWhiteSpace(request.Tool) ? "summaries" : request.Tool.Trim();
            if (!InteractionService.IsKnownTool(tool))
                return BadRequest(new { error = $"Unknown tool '{tool}'." });

            var intent = CleanIntent(request.Intent);
            var job = await _jobLog.StartAsync(GetUserId(), ModuleName, $"analyse:{tool}", $"{ToolLabel(tool)} analysis");
            try
            {
                var analysis = await _service.AnalyseAsync(request.Transcript, tool, intent);
                await _jobLog.CompleteAsync(job, analysis.Headline);
                await SaveAnalysisToSessionAsync(request.SessionId, tool, analysis, intent);
                return Ok(analysis);
            }
            catch (Exception ex)
            {
                await _jobLog.FailAsync(job, ex.Message);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analysing interaction");
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Suggest goals the user might want from this recording. Powers the "choose
    /// your goals" modal — returns ~20 options, grounded in the transcript when one
    /// is supplied. Pass already-shown goals in <c>existing</c> for the next batch.
    /// </summary>
    [HttpPost("suggest-goals")]
    public async Task<IActionResult> SuggestGoals([FromBody] SuggestGoalsRequest request)
    {
        try
        {
            var goals = await _service.SuggestGoalsAsync(request?.Transcript, request?.Seed, request?.Existing, 20);
            return Ok(new { goals });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error suggesting goals");
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Restore the user's most recent Interaction Intelligence session.</summary>
    [HttpGet("session")]
    public async Task<IActionResult> GetSession()
    {
        var userId = GetUserId();
        var s = await _db.InteractionSessions
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync();

        if (s == null) return Ok(new { session = (object?)null });
        return Ok(new { session = ToSessionDto(s) });
    }

    /// <summary>Load a specific session by id (used by the History panel).</summary>
    [HttpGet("session/{id}")]
    public async Task<IActionResult> GetSessionById(string id)
    {
        var userId = GetUserId();
        var s = await _db.InteractionSessions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (s == null) return NotFound(new { error = "Session not found." });
        return Ok(new { session = ToSessionDto(s) });
    }

    /// <summary>Delete a session.</summary>
    [HttpDelete("session/{id}")]
    public async Task<IActionResult> DeleteSession(string id)
    {
        var userId = GetUserId();
        await _db.InteractionSessions
            .Where(x => x.Id == id && x.UserId == userId)
            .ExecuteDeleteAsync();
        return NoContent();
    }

    /// <summary>Lightweight list of the user's sessions for the History panel.</summary>
    [HttpGet("sessions")]
    public async Task<IActionResult> ListSessions([FromQuery] int take = 50)
    {
        var userId = GetUserId();
        take = Math.Clamp(take, 1, 200);
        var rows = await _db.InteractionSessions
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(take)
            .Select(x => new { x.Id, x.SourceLabel, x.UpdatedAt, x.AnalysesJson })
            .ToListAsync();

        var items = rows.Select(r => new
        {
            sessionId = r.Id,
            sourceLabel = r.SourceLabel,
            updatedAt = r.UpdatedAt,
            tools = ToolsOf(r.AnalysesJson)
        });
        return Ok(new { items });
    }

    private static object ToSessionDto(InteractionSession s) => new
    {
        sessionId = s.Id,
        sourceLabel = s.SourceLabel,
        transcript = s.Transcript,
        analysesJson = s.AnalysesJson,
        intent = s.Intent,
        updatedAt = s.UpdatedAt
    };

    private static string[] ToolsOf(string analysesJson)
    {
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(analysesJson);
            return map?.Keys.ToArray() ?? Array.Empty<string>();
        }
        catch { return Array.Empty<string>(); }
    }

    // Stores a tool's analysis into the session's AnalysesJson map so it can be restored.
    private async Task SaveAnalysisToSessionAsync(string? sessionId, string tool, InteractionAnalysis analysis, string? intent = null)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        var session = await _db.InteractionSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == GetUserId());
        if (session == null) return;

        Dictionary<string, JsonElement> map;
        try { map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(session.AnalysesJson) ?? new(); }
        catch { map = new(); }

        map[tool] = JsonSerializer.SerializeToElement(analysis, JsonWeb);
        session.AnalysesJson = JsonSerializer.Serialize(map);
        // Keep the session's stored goal in step with what was just used.
        if (intent != null) session.Intent = intent;
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // Flatten an analysis into plain summary text for the knowledge graph (no raw transcript).
    private static string AnalysisSummaryText(InteractionAnalysis analysis)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(analysis.Headline)) sb.AppendLine(analysis.Headline);
        foreach (var m in analysis.Metrics) sb.AppendLine($"{m.Label}: {m.Value}");
        foreach (var s in analysis.Sections)
        {
            if (!string.IsNullOrWhiteSpace(s.Heading)) sb.AppendLine(s.Heading);
            foreach (var item in s.Items) sb.AppendLine($"- {item}");
        }
        foreach (var p in analysis.Pairs) sb.AppendLine($"{p.Label}: {p.Value}");
        return sb.ToString().Trim();
    }

    private static string ToolLabel(string tool) => tool switch
    {
        "summaries" => "Conversation summary",
        "wrap-up" => "Wrap-up",
        "quality" => "Quality scoring",
        "compliance" => "Compliance check",
        "coaching" => "Coaching",
        "sentiment" => "Sentiment & escalation",
        "knowledge" => "Knowledge assist",
        "audit" => "Audit & assurance",
        _ => tool
    };

    public class AnalyseRequest
    {
        public string Transcript { get; set; } = string.Empty;
        public string Tool { get; set; } = "summaries";
        public string? SessionId { get; set; }
        /// <summary>Optional: what the user wants out of this recording — used to tailor every tool.</summary>
        public string? Intent { get; set; }
    }

    public class SuggestGoalsRequest
    {
        public string? Transcript { get; set; }
        public string? Seed { get; set; }
        public List<string>? Existing { get; set; }
    }

    // Trim and cap the user's goal so it stays a lens, not an essay (or an abuse vector).
    private static string? CleanIntent(string? intent)
    {
        var v = intent?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        return v.Length > 600 ? v.Substring(0, 600) : v;
    }
}
