using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LifeManager.Api.Data;
using LifeManager.Api.Models;
using LifeManager.Api.Services;
using System.Security.Claims;

namespace LifeManager.Api.Controllers;

/// <summary>
/// The spoken half of the Life Tracker.
///
/// Separate from <see cref="LifeTrackerController"/> — which is already large
/// and owns the manual CRUD — because this is a distinct pipeline: audio in,
/// structure proposed, user accepts, futures projected.
/// </summary>
[Authorize]
[ApiController]
[Route("api/lifetracker/voice")]
public class LifeVoiceController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly IAIService _aiService;
    private readonly LifeNarrativeService _narrative;
    private readonly LifeSafetyService _safety;
    private readonly ILogger<LifeVoiceController> _logger;

    /// <summary>Whisper's own ceiling is 25 MB; refuse earlier with a clear message.</summary>
    private const long MaxAudioBytes = 25 * 1024 * 1024;

    private static readonly HashSet<string> AllowedAudio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".webm", ".mp3", ".mp4", ".m4a", ".wav", ".ogg", ".oga", ".flac", ".mpeg", ".mpga",
    };

    public LifeVoiceController(
        PromptDbContext context,
        IAIService aiService,
        LifeNarrativeService narrative,
        LifeSafetyService safety,
        ILogger<LifeVoiceController> logger)
    {
        _context = context;
        _aiService = aiService;
        _narrative = narrative;
        _safety = safety;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    /// <summary>
    /// Which country's crisis helplines to show. Resolved per-request from edge
    /// geo headers, falling back to Accept-Language and then to international.
    /// </summary>
    private string? GetCountry() => HelplineDirectory.DetectCountry(HttpContext);

    // ── Recording ───────────────────────────────────────────────────────────

    /// <summary>
    /// POST /api/lifetracker/voice — speak, and get structure back.
    ///
    /// Transcribes, extracts, and stores as a *proposal*. Nothing is written to
    /// the user's real tracker until they call /apply.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("ai-generation")]
    [RequestSizeLimit(MaxAudioBytes)]
    public async Task<ActionResult> RecordEntry(
        IFormFile audio,
        [FromForm] int durationSeconds = 0,
        [FromForm] string? entryDate = null,
        CancellationToken ct = default)
    {
        if (audio is null || audio.Length == 0)
            return BadRequest(new { error = "No audio was received." });

        if (audio.Length > MaxAudioBytes)
            return BadRequest(new { error = "That recording is too long. Keep entries under about 20 minutes." });

        var ext = Path.GetExtension(audio.FileName);
        if (!string.IsNullOrEmpty(ext) && !AllowedAudio.Contains(ext))
            return BadRequest(new { error = $"Unsupported audio format '{ext}'." });

        var userId = GetUserId();

        string transcript;
        try
        {
            using var stream = audio.OpenReadStream();
            // Whisper picks the codec off the extension, so give it a sane one
            // when the browser's MediaRecorder omits a filename.
            var fileName = string.IsNullOrWhiteSpace(audio.FileName) ? "entry.webm" : audio.FileName;
            transcript = await _aiService.TranscribeAudioAsync(stream, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice entry transcription failed");
            return StatusCode(502, new { error = "Could not transcribe that recording.", message = ex.Message });
        }

        if (string.IsNullOrWhiteSpace(transcript))
            return BadRequest(new { error = "That recording came back empty. Try speaking a little longer." });

        var entry = new LifeVoiceEntry
        {
            UserId = userId,
            Transcript = transcript,
            DurationSeconds = Math.Clamp(durationSeconds, 0, 60 * 60),
            EntryDate = DateTime.TryParse(entryDate, out var d) ? d.Date : DateTime.UtcNow.Date,
            Status = "pending",
        };
        _context.LifeVoiceEntries.Add(entry);
        await _context.SaveChangesAsync(ct);

        // Extraction can fail without costing the user their words — the
        // transcript is already saved and /reprocess can try again.
        try
        {
            var x = await _narrative.ExtractAsync(transcript, ct);
            entry.Headline = x.Headline;
            entry.Narrative = x.Narrative;
            entry.Sentiment = x.Sentiment;
            entry.ExtractionJson = JsonSerializer.Serialize(x);
            entry.Status = "ready";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice extraction failed for entry {Id}", entry.Id);
            entry.Status = "failed";
            entry.ErrorMessage = ex.Message;
            entry.Headline = Clip(transcript, 300);
        }

        entry.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        // Screen what they just told us. The entry is saved either way — their
        // words are theirs — but if this sounds like crisis, the response leads
        // with real help rather than a tidy list of extracted "goals".
        var verdict = await _safety.ScreenAsync(transcript, GetCountry(), ct);
        if (verdict.BlocksGeneration)
            return Ok(new { entry = Shape(entry), safetyStop = true, category = verdict.Category, message = verdict.Message });

        return Ok(Shape(entry));
    }

    /// <summary>Re-run extraction on a stored transcript (after a failure, or a model change).</summary>
    [HttpPost("{id:int}/reprocess")]
    [EnableRateLimiting("ai-generation")]
    public async Task<ActionResult> Reprocess(int id, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var entry = await _context.LifeVoiceEntries.FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId, ct);
        if (entry is null) return NotFound();

        try
        {
            var x = await _narrative.ExtractAsync(entry.Transcript, ct);
            entry.Headline = x.Headline;
            entry.Narrative = x.Narrative;
            entry.Sentiment = x.Sentiment;
            entry.ExtractionJson = JsonSerializer.Serialize(x);
            entry.Status = "ready";
            entry.ErrorMessage = null;
        }
        catch (Exception ex)
        {
            entry.Status = "failed";
            entry.ErrorMessage = ex.Message;
        }

        entry.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        return Ok(Shape(entry));
    }

    /// <summary>A typed entry, for when speaking aloud isn't an option.</summary>
    [HttpPost("text")]
    [EnableRateLimiting("ai-generation")]
    public async Task<ActionResult> RecordText([FromBody] TextEntryRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { error = "Nothing to record." });

        var entry = new LifeVoiceEntry
        {
            UserId = GetUserId(),
            Transcript = request.Text.Trim(),
            EntryDate = DateTime.TryParse(request.EntryDate, out var d) ? d.Date : DateTime.UtcNow.Date,
            Status = "pending",
        };
        _context.LifeVoiceEntries.Add(entry);
        await _context.SaveChangesAsync(ct);

        try
        {
            var x = await _narrative.ExtractAsync(entry.Transcript, ct);
            entry.Headline = x.Headline;
            entry.Narrative = x.Narrative;
            entry.Sentiment = x.Sentiment;
            entry.ExtractionJson = JsonSerializer.Serialize(x);
            entry.Status = "ready";
        }
        catch (Exception ex)
        {
            entry.Status = "failed";
            entry.ErrorMessage = ex.Message;
            entry.Headline = Clip(entry.Transcript, 300);
        }

        entry.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        var textVerdict = await _safety.ScreenAsync(entry.Transcript, GetCountry(), ct);
        if (textVerdict.BlocksGeneration)
            return Ok(new { entry = Shape(entry), safetyStop = true, category = textVerdict.Category, message = textVerdict.Message });

        return Ok(Shape(entry));
    }

    // ── Reading back ────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<ActionResult> List([FromQuery] int take = 30, [FromQuery] int skip = 0)
    {
        var userId = GetUserId();
        var q = _context.LifeVoiceEntries.Where(e => e.UserId == userId);

        var total = await q.CountAsync();
        var items = await q
            .OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 100))
            .ToListAsync();

        return Ok(new
        {
            total,
            totalMinutes = await q.SumAsync(e => e.DurationSeconds) / 60,
            items = items.Select(Shape),
        });
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult> Get(int id)
    {
        var userId = GetUserId();
        var entry = await _context.LifeVoiceEntries.FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);
        if (entry is null) return NotFound();

        return Ok(new
        {
            entry.Id,
            entry.EntryDate,
            entry.Headline,
            entry.Narrative,
            entry.Sentiment,
            entry.Transcript,          // full text only on the detail view
            entry.DurationSeconds,
            entry.Status,
            entry.ErrorMessage,
            entry.Applied,
            entry.AppliedAt,
            entry.CreatedAt,
            extraction = Parse(entry.ExtractionJson),
        });
    }

    [HttpDelete("{id:int}")]
    public async Task<ActionResult> Delete(int id, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var entry = await _context.LifeVoiceEntries.FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId, ct);
        if (entry is null) return NotFound();

        _context.LifeVoiceEntries.Remove(entry);
        await _context.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Accepting into the tracker ──────────────────────────────────────────

    /// <summary>
    /// POST /api/lifetracker/voice/{id}/apply — commit the parts the user chose.
    ///
    /// The client sends back exactly which items to keep, so a wrong guess by
    /// the model is discarded rather than corrected after the fact. This is the
    /// line between "assistant" and "something that edits your journal without
    /// asking".
    /// </summary>
    [HttpPost("{id:int}/apply")]
    public async Task<ActionResult> Apply(int id, [FromBody] ApplyVoiceEntryRequest? request, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var entry = await _context.LifeVoiceEntries.FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId, ct);
        if (entry is null) return NotFound();
        if (entry.Status != "ready") return BadRequest(new { error = "This entry has not been processed yet." });
        if (entry.Applied) return BadRequest(new { error = "This entry has already been added to your tracker." });

        LifeNarrativeService.VoiceExtraction? x;
        try { x = JsonSerializer.Deserialize<LifeNarrativeService.VoiceExtraction>(entry.ExtractionJson); }
        catch { return BadRequest(new { error = "This entry's extracted data could not be read." }); }
        if (x is null) return BadRequest(new { error = "Nothing to apply." });

        var created = new List<string>();

        // Check-in — merged into the day's existing row rather than duplicating
        // it, so speaking twice in one day doesn't create two conflicting days.
        if ((request?.ApplyCheckIn ?? true) && x.CheckIn is { } c)
        {
            var checkIn = await _context.LifeCheckIns
                .FirstOrDefaultAsync(k => k.UserId == userId && k.Date == entry.EntryDate, ct);

            var isNew = checkIn is null;
            checkIn ??= new LifeCheckIn { UserId = userId, Date = entry.EntryDate };

            if (c.Mood.HasValue) checkIn.Mood = c.Mood.Value;
            if (c.Energy.HasValue) checkIn.Energy = c.Energy.Value;
            if (c.Focus.HasValue) checkIn.Focus = c.Focus.Value;
            if (c.SleepHours.HasValue) checkIn.SleepHours = c.SleepHours.Value;
            if (c.ExerciseMinutes.HasValue) checkIn.ExerciseMinutes = c.ExerciseMinutes.Value;
            if (!string.IsNullOrWhiteSpace(c.TopTasks)) checkIn.TopTasks = c.TopTasks!;
            if (!string.IsNullOrWhiteSpace(c.Gratitude)) checkIn.Gratitude = c.Gratitude!;

            var note = string.IsNullOrWhiteSpace(c.Notes) ? entry.Headline : c.Notes!;
            checkIn.Notes = string.IsNullOrWhiteSpace(checkIn.Notes) ? note : $"{checkIn.Notes}\n{note}";
            checkIn.UpdatedAt = DateTime.UtcNow;

            if (isNew) _context.LifeCheckIns.Add(checkIn);
            await _context.SaveChangesAsync(ct);

            entry.CreatedCheckInId = checkIn.Id;
            created.Add(isNew ? "check-in" : "check-in (updated)");
        }

        var wantGoals = request?.GoalIndexes;
        for (var i = 0; i < x.Goals.Count; i++)
        {
            if (wantGoals is not null && !wantGoals.Contains(i)) continue;
            var g = x.Goals[i];

            // Don't create a second copy of a goal they already track.
            var existing = await _context.LifeGoals
                .FirstOrDefaultAsync(e2 => e2.UserId == userId && e2.Title == g.Title, ct);
            if (existing is not null)
            {
                if (g.Progress.HasValue) existing.Progress = Math.Clamp(g.Progress.Value, 0, 100);
                if (!string.IsNullOrWhiteSpace(g.Notes)) existing.Notes = g.Notes!;
                existing.UpdatedAt = DateTime.UtcNow;
                created.Add($"goal updated: {g.Title}");
                continue;
            }

            _context.LifeGoals.Add(new LifeGoal
            {
                UserId = userId,
                Title = g.Title,
                Area = string.IsNullOrWhiteSpace(g.Area) ? "Growth" : g.Area!,
                Notes = g.Notes ?? string.Empty,
                Progress = Math.Clamp(g.Progress ?? 0, 0, 100),
            });
            created.Add($"goal: {g.Title}");
        }

        var wantDecisions = request?.DecisionIndexes;
        for (var i = 0; i < x.Decisions.Count; i++)
        {
            if (wantDecisions is not null && !wantDecisions.Contains(i)) continue;
            var d = x.Decisions[i];
            _context.LifeDecisions.Add(new LifeDecision
            {
                UserId = userId,
                Date = entry.EntryDate,
                Title = d.Title,
                Reasoning = d.Reasoning ?? string.Empty,
                Outcome = d.Outcome ?? string.Empty,
            });
            created.Add($"decision: {d.Title}");
        }

        var wantPeople = request?.RelationshipIndexes;
        for (var i = 0; i < x.Relationships.Count; i++)
        {
            if (wantPeople is not null && !wantPeople.Contains(i)) continue;
            var p = x.Relationships[i];

            var existing = await _context.LifeRelationships
                .FirstOrDefaultAsync(e2 => e2.UserId == userId && e2.Name == p.Name, ct);
            if (existing is not null)
            {
                // Mentioning someone is contact, and that is the whole point of
                // the "you haven't spoken to X in 3 months" nudge.
                existing.LastContactedAt = entry.EntryDate;
                if (!string.IsNullOrWhiteSpace(p.Notes)) existing.Notes = p.Notes!;
                existing.UpdatedAt = DateTime.UtcNow;
                created.Add($"contact logged: {p.Name}");
                continue;
            }

            _context.LifeRelationships.Add(new LifeRelationship
            {
                UserId = userId,
                Name = p.Name,
                Circle = string.IsNullOrWhiteSpace(p.Circle) ? "Friends" : p.Circle!,
                Notes = p.Notes ?? string.Empty,
                LastContactedAt = entry.EntryDate,
            });
            created.Add($"person: {p.Name}");
        }

        entry.Applied = true;
        entry.AppliedAt = DateTime.UtcNow;
        entry.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return Ok(new { entry.Id, entry.Applied, created });
    }

    // ── Futures ─────────────────────────────────────────────────────────────

    /// <summary>POST /api/lifetracker/voice/scenarios — project plausible futures.</summary>
    [HttpPost("/api/lifetracker/scenarios")]
    [EnableRateLimiting("ai-generation")]
    public async Task<ActionResult> GenerateScenarios([FromBody] ScenarioRequest? request, CancellationToken ct = default)
    {
        var horizon = (request?.Horizon ?? "1-year").ToLowerInvariant();
        if (horizon is not ("3-months" or "1-year" or "3-years"))
            return BadRequest(new { error = "Horizon must be 3-months, 1-year or 3-years." });

        try
        {
            var scenarios = await _narrative.ProjectScenariosAsync(GetUserId(), horizon, request?.Focus, GetCountry(), ct);
            return Ok(new
            {
                batchId = scenarios[0].BatchId,
                horizon,
                scenarios = scenarios.Select(ShapeScenario),
            });
        }
        catch (LifeNarrativeService.LifeSafetyStopException ex)
        {
            // 200, not an error status: this IS the response, and the client
            // must render it as care rather than as a failed request.
            return Ok(new { safetyStop = true, category = ex.Category, message = ex.Message, scenarios = Array.Empty<object>() });
        }
        catch (InvalidOperationException ex)
        {
            // Thin data is a user-fixable state, not a server fault.
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scenario generation failed");
            return StatusCode(502, new { error = "Could not generate scenarios right now.", message = ex.Message });
        }
    }

    [HttpGet("/api/lifetracker/scenarios")]
    public async Task<ActionResult> ListScenarios([FromQuery] string? batchId, [FromQuery] bool pinnedOnly = false)
    {
        var userId = GetUserId();
        var q = _context.LifeScenarios.Where(s => s.UserId == userId);

        if (!string.IsNullOrWhiteSpace(batchId)) q = q.Where(s => s.BatchId == batchId);
        else if (pinnedOnly) q = q.Where(s => s.IsPinned);

        var items = await q.OrderByDescending(s => s.CreatedAt).Take(60).ToListAsync();

        // Newest batch only, unless they asked for something specific — a wall
        // of every projection ever made is noise, not insight.
        if (string.IsNullOrWhiteSpace(batchId) && !pinnedOnly && items.Count > 0)
        {
            var newest = items[0].BatchId;
            items = items.Where(s => s.BatchId == newest).ToList();
        }

        return Ok(new { scenarios = items.Select(ShapeScenario) });
    }

    [HttpPost("/api/lifetracker/scenarios/{id:int}/pin")]
    public async Task<ActionResult> PinScenario(int id, [FromBody] PinRequest? request, CancellationToken ct = default)
    {
        var userId = GetUserId();
        var s = await _context.LifeScenarios.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (s is null) return NotFound();

        s.IsPinned = request?.Pinned ?? !s.IsPinned;
        await _context.SaveChangesAsync(ct);
        return Ok(new { s.Id, s.IsPinned });
    }

    /// <summary>POST /api/lifetracker/guidance — "what should I do next?"</summary>
    [HttpPost("/api/lifetracker/guidance")]
    [EnableRateLimiting("ai-generation")]
    public async Task<ActionResult> Guidance([FromBody] GuidanceRequest? request, CancellationToken ct = default)
    {
        try
        {
            var g = await _narrative.GuideAsync(GetUserId(), request?.Question, GetCountry(), ct);
            return Ok(new
            {
                safetyStop = false,
                read = g.Read,
                nextActions = g.NextActions,
                questions = g.Questions,
                patterns = g.Patterns,
                caution = g.Caution,
            });
        }
        catch (LifeNarrativeService.LifeSafetyStopException ex)
        {
            return Ok(new
            {
                safetyStop = true,
                category = ex.Category,
                read = ex.Message,
                nextActions = Array.Empty<string>(),
                questions = Array.Empty<string>(),
                patterns = Array.Empty<string>(),
                caution = (string?)null,
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Guidance generation failed");
            return StatusCode(502, new { error = "Could not generate guidance right now.", message = ex.Message });
        }
    }

    // ── Shaping ─────────────────────────────────────────────────────────────

    private static object Shape(LifeVoiceEntry e) => new
    {
        e.Id,
        e.EntryDate,
        e.Headline,
        e.Narrative,
        e.Sentiment,
        e.DurationSeconds,
        e.Status,
        e.ErrorMessage,
        e.Applied,
        e.AppliedAt,
        e.CreatedAt,
        extraction = Parse(e.ExtractionJson),
    };

    private static object ShapeScenario(LifeScenario s) => new
    {
        s.Id,
        s.BatchId,
        s.Title,
        s.Kind,
        s.Horizon,
        s.Summary,
        s.Likelihood,
        s.IsPinned,
        s.CreatedAt,
        drivers = ParseList(s.DriversJson),
        assumptions = ParseList(s.AssumptionsJson),
        actions = ParseList(s.ActionsJson),
        signals = ParseList(s.SignalsJson),
        areas = ParseList(s.AreasJson),
    };

    private static object? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(json); }
        catch { return null; }
    }

    private static List<string> ParseList(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static string Clip(string s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max].TrimEnd() + "…");
}

// ── Requests ────────────────────────────────────────────────────────────────

public class TextEntryRequest
{
    public string Text { get; set; } = string.Empty;
    public string? EntryDate { get; set; }
}

/// <summary>
/// Which extracted items to commit. Null list = accept all of that type;
/// an empty list = accept none.
/// </summary>
public class ApplyVoiceEntryRequest
{
    public bool? ApplyCheckIn { get; set; }
    public List<int>? GoalIndexes { get; set; }
    public List<int>? DecisionIndexes { get; set; }
    public List<int>? RelationshipIndexes { get; set; }
}

public class ScenarioRequest
{
    /// <summary>3-months | 1-year | 3-years</summary>
    public string? Horizon { get; set; }

    /// <summary>Optional steer, e.g. "should I change jobs".</summary>
    public string? Focus { get; set; }
}

public class PinRequest
{
    public bool? Pinned { get; set; }
}

public class GuidanceRequest
{
    public string? Question { get; set; }
}
