using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using InkReel.Api.Data;
using InkReel.Api.Services;

namespace InkReel.Api.Controllers;

/// <summary>
/// Video Studio — the enhancement: turn an uploaded document (PDF/DOCX/MD/TXT,
/// optionally a page range), a saved InkReel document, or a plain prompt into a
/// narrated slide video. Document figures are pulled out and placed on the
/// slides that discuss them.
///
/// COST GUARDRAILS (enforced here, not in the UI): signed-in users only, one
/// render in flight per user, and a daily cap. Each render costs one script
/// completion plus one TTS call per scene.
/// </summary>
[Authorize]
[ApiController]
[Route("api/video")]
public class VideoController : ControllerBase
{
    private readonly VideoJobStore _store;
    private readonly VideoQueue _queue;
    private readonly DocumentExtractor _extractor;
    private readonly SpeechService _speech;
    private readonly AzureVisionService _ai;
    private readonly AppDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<VideoController> _logger;

    private const long MaxUploadBytes = 40 * 1024 * 1024;
    private const long MaxLogoBytes = 2 * 1024 * 1024;
    private const int DailyCap = 20;
    private static readonly int[] Lengths = { 30, 60, 90, 120, 180, 240 };
    private static readonly string[] NarrationStyles = { "explainer", "storyteller", "energetic", "calm" };

    public VideoController(VideoJobStore store, VideoQueue queue, DocumentExtractor extractor, SpeechService speech,
        AzureVisionService ai, AppDbContext db, IConfiguration cfg, IWebHostEnvironment env, ILogger<VideoController> logger)
    {
        _store = store; _queue = queue; _extractor = extractor; _speech = speech;
        _ai = ai; _db = db; _cfg = cfg; _env = env; _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Options for the settings panel ───────────────────────────────────────

    [HttpGet("options")]
    public IActionResult Options() => Ok(new
    {
        styles = SlideRenderService.Styles.Select(s => new { s.Id, s.Label, s.Blurb }),
        palettes = SlideRenderService.Palettes.Select(p => new { p.Id, p.Label, p.Primary, p.Accent }),
        voices = SpeechService.Voices.Select(v => new { v.Id, v.Label, v.Vibe, v.Gender }),
        lengths = Lengths,
        narrationStyles = NarrationStyles,
        narrationAvailable = _speech.IsAvailable,
        aiAvailable = _ai.IsAvailable,
        dailyCap = DailyCap,
    });

    // ── Inspect an upload (page count + figures) before committing ───────────

    [HttpPost("inspect")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Inspect([FromForm] IFormFile? file)
    {
        if (file == null || file.Length == 0) return BadRequest(new { error = "Choose a document first." });
        if (file.Length > MaxUploadBytes) return BadRequest(new { error = "That document is larger than 40 MB." });

        try
        {
            var bytes = await ReadAsync(file);
            var doc = _extractor.Extract(bytes, file.FileName, null, null);
            var words = doc.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            return Ok(new
            {
                fileName = file.FileName,
                pageCount = doc.PageCount,
                imageCount = doc.Images.Count,
                wordCount = words,
                preview = doc.Text.Length > 600 ? doc.Text[..600] + "…" : doc.Text,
            });
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Document inspect failed for {File}", file.FileName);
            return BadRequest(new { error = "That document could not be read — is it password-protected or damaged?" });
        }
    }

    // ── Create a video job ───────────────────────────────────────────────────

    /// <summary>
    /// Multipart form. Source is exactly one of: file (+fromPage/toPage),
    /// docId (a saved InkReel document), or prompt. Settings travel as form
    /// fields so the logo can ride in the same request.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("ai-generation")]
    [RequestSizeLimit(MaxUploadBytes + MaxLogoBytes + 1024 * 1024)]
    public async Task<IActionResult> Create(
        [FromForm] IFormFile? file, [FromForm] IFormFile? logo,
        [FromForm] int? fromPage, [FromForm] int? toPage,
        [FromForm] int? docId, [FromForm] string? prompt,
        [FromForm] string? title, [FromForm] int? lengthSeconds,
        [FromForm] string? styleId, [FromForm] string? paletteId,
        [FromForm] string? customPrimary, [FromForm] string? customAccent,
        [FromForm] string? voiceId, [FromForm] string? narrationStyle, [FromForm] string? audience)
    {
        if (!_ai.IsAvailable) return StatusCode(503, new { error = "AI is not configured on this server." });

        // Guardrails first — before any expensive work.
        if (await _store.CountActiveAsync(UserId) > 0)
            return Conflict(new { error = "You already have a video rendering — wait for it to finish." });
        if (await _store.CountRecentAsync(UserId) >= DailyCap)
            return StatusCode(429, new { error = $"Daily limit of {DailyCap} videos reached — try again tomorrow." });

        var sources = (file != null ? 1 : 0) + (docId != null ? 1 : 0) + (!string.IsNullOrWhiteSpace(prompt) ? 1 : 0);
        if (sources != 1) return BadRequest(new { error = "Provide exactly one source: a document, a saved InkReel doc, or a prompt." });

        var req = new VideoRequest
        {
            TargetSeconds = Lengths.Contains(lengthSeconds ?? 0) ? lengthSeconds!.Value : 120,
            StyleId = SlideRenderService.Styles.Any(s => s.Id == styleId) ? styleId! : SlideRenderService.Styles[0].Id,
            PaletteId = paletteId == "custom" || SlideRenderService.Palettes.Any(p => p.Id == paletteId) ? paletteId! : SlideRenderService.Palettes[0].Id,
            CustomPrimary = customPrimary,
            CustomAccent = customAccent,
            VoiceId = SpeechService.Voices.Any(v => v.Id == voiceId) ? voiceId! : SpeechService.Voices[0].Id,
            NarrationStyle = NarrationStyles.Contains(narrationStyle ?? "") ? narrationStyle! : "explainer",
            Audience = Clip(audience, 120) ?? "General audience",
        };

        // ── Resolve the source ──
        string derivedTitle;
        try
        {
            if (file != null)
            {
                if (file.Length > MaxUploadBytes) return BadRequest(new { error = "That document is larger than 40 MB." });
                var doc = _extractor.Extract(await ReadAsync(file), file.FileName, fromPage, toPage);
                req.SourceText = doc.Text;
                req.Images = doc.Images;
                derivedTitle = Path.GetFileNameWithoutExtension(file.FileName);
            }
            else if (docId != null)
            {
                var (docTitle, text) = await LoadInkDocAsync(docId.Value);
                if (text == null) return NotFound(new { error = "That saved document was not found." });
                req.SourceText = text;
                derivedTitle = docTitle ?? "My document";
            }
            else
            {
                req.SourceText = Clip(prompt, 8000)!;
                derivedTitle = Clip(prompt, 60)!;
            }
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Video source extraction failed");
            return BadRequest(new { error = "That document could not be read — is it password-protected or damaged?" });
        }

        if (req.SourceText.Trim().Length < 40)
            return BadRequest(new { error = "There isn't enough text to build a video from — pick more pages or add detail." });

        req.Title = Clip(title, 120) ?? derivedTitle;

        if (logo is { Length: > 0 })
        {
            if (logo.Length > MaxLogoBytes) return BadRequest(new { error = "The logo must be 2 MB or smaller." });
            var ext = Path.GetExtension(logo.FileName).ToLowerInvariant();
            if (ext is not (".png" or ".webp" or ".jpg" or ".jpeg"))
                return BadRequest(new { error = "Logo must be PNG, WebP, or JPEG." });
            req.Logo = await ReadAsync(logo);
        }

        var jobId = Guid.NewGuid().ToString("N");
        await _store.CreateAsync(jobId, UserId, req.Title);
        await _queue.EnqueueAsync(new VideoWorkItem(jobId, UserId, req));

        return Ok(new { id = jobId, status = "queued", narrated = _speech.IsAvailable });
    }

    // ── Status / list / download ─────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> List() => Ok(await _store.ListAsync(UserId));

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        if (!IsJobId(id)) return NotFound();
        var job = await _store.GetAsync(id, UserId);
        return job == null ? NotFound(new { error = "Video not found." }) : Ok(job);
    }

    [HttpGet("{id}/script")]
    public async Task<IActionResult> Script(string id)
    {
        if (!IsJobId(id)) return NotFound();
        var json = await _store.GetScriptAsync(id, UserId);
        if (string.IsNullOrEmpty(json)) return NotFound(new { error = "No script yet." });
        return Content(json, "application/json");
    }

    [HttpGet("{id}/file")]
    public async Task<IActionResult> File(string id)
    {
        if (!IsJobId(id)) return NotFound();
        var job = await _store.GetAsync(id, UserId);
        if (job == null || job.Status != "done") return NotFound(new { error = "The video is not ready." });

        var path = Path.Combine(VideoRenderWorker.OutputRoot(_cfg, _env), id, "video.mp4");
        if (!System.IO.File.Exists(path)) return NotFound(new { error = "The video file is missing on the server." });

        var safeName = new string(job.Title.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_').ToArray()).Trim();
        return PhysicalFile(path, "video/mp4", $"{(safeName.Length > 0 ? safeName : "video")}.mp4", enableRangeProcessing: true);
    }

    [HttpGet("{id}/thumb")]
    public async Task<IActionResult> Thumb(string id)
    {
        if (!IsJobId(id)) return NotFound();
        if (await _store.GetAsync(id, UserId) == null) return NotFound();
        var path = Path.Combine(VideoRenderWorker.OutputRoot(_cfg, _env), id, "title.png");
        return System.IO.File.Exists(path) ? PhysicalFile(path, "image/png") : NotFound();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        if (!IsJobId(id)) return NotFound();
        var job = await _store.GetAsync(id, UserId);
        if (job == null) return NotFound();
        if (job.Status is not ("done" or "failed"))
            return Conflict(new { error = "Wait for the video to finish before deleting it." });

        await _store.DeleteAsync(id, UserId);
        var dir = Path.Combine(VideoRenderWorker.OutputRoot(_cfg, _env), id);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Could not delete video folder {Dir}", dir); }
        return Ok(new { message = "Deleted." });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Job ids are server-minted 32-char hex — anything else never touches the filesystem.</summary>
    private static bool IsJobId(string id) => id.Length == 32 && id.All(Uri.IsHexDigit);

    private static async Task<byte[]> ReadAsync(IFormFile f)
    {
        using var ms = new MemoryStream();
        await f.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static string? Clip(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>Flatten a saved InkReel document's blocks back into narratable text.</summary>
    private async Task<(string? title, string? text)> LoadInkDocAsync(int id)
    {
        var rows = await _db.Database.SqlQuery<string?>($@"
            SELECT ""ContentJson"" AS ""Value"" FROM ""InklingDocs"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync();
        var json = rows.FirstOrDefault();
        if (string.IsNullOrEmpty(json)) return (null, null);
        var titles = await _db.Database.SqlQuery<string?>($@"
            SELECT ""Title"" AS ""Value"" FROM ""InklingDocs"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync();

        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            foreach (var b in blocks.EnumerateArray())
                if (b.ValueKind == JsonValueKind.String) sb.AppendLine(b.GetString()).AppendLine();
        return (titles.FirstOrDefault(), sb.ToString());
    }
}
