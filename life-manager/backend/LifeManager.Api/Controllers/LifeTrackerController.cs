using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using DocumentFormat.OpenXml.Packaging;
using LifeManager.Api.Data;
using LifeManager.Api.Models;
using LifeManager.Api.Services;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace LifeManager.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class LifeTrackerController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly IAIService _aiService;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<LifeTrackerController> _logger;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".txt", ".md", ".csv", ".json", ".rtf", ".jpg", ".jpeg", ".png", ".webp"
    };
    private const long MaxEvidenceSize = 10 * 1024 * 1024;
    private const int MaxCloudContextChars = 80_000;
    private static readonly HttpClient CloudHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };


    public LifeTrackerController(PromptDbContext context, IAIService aiService, IWebHostEnvironment env, ILogger<LifeTrackerController> logger)
    {
        _context = context;
        _aiService = aiService;
        _env = env;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    [HttpGet]
    public async Task<ActionResult<LifeTrackerSummaryResponse>> GetSummary([FromQuery] int days = 30)
    {
        var userId = GetUserId();
        var clampedDays = Math.Clamp(days, 7, 365);
        var since = DateTime.UtcNow.Date.AddDays(-clampedDays + 1);

        var checkIns = await _context.LifeCheckIns
            .Where(x => x.UserId == userId && x.Date >= since)
            .OrderBy(x => x.Date)
            .ToListAsync();

        var goals = await _context.LifeGoals
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.Status == "active")
            .ThenByDescending(x => x.UpdatedAt)
            .Take(20)
            .ToListAsync();

        var decisions = await _context.LifeDecisions
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.Date)
            .Take(12)
            .ToListAsync();

        var relationships = await _context.LifeRelationships
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.Importance)
            .ThenBy(x => x.LastContactedAt ?? DateTime.MinValue)
            .Take(12)
            .ToListAsync();
        var evidence = await _context.LifeEvidence
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(20)
            .Select(x => new LifeEvidenceSummary(x.Id, x.FileName, x.ContentType, x.FileSize, x.EvidenceType, x.Notes, x.CreatedAt))
            .ToListAsync();
        var cloudLinks = await _context.LifeCloudLinks
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => new LifeCloudLinkSummary(
                x.Id,
                x.Provider,
                x.DisplayName,
                x.Url,
                x.Notes,
                x.SyncStatus,
                x.LastSyncedAt,
                x.ExtractedText.Length > 400 ? x.ExtractedText.Substring(0, 400) : x.ExtractedText,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync();

        // Insights are computed over the FULL set of people/decisions (not just the
        // top slices returned above) so nudges and review reminders aren't missed.
        var allRelationships = await _context.LifeRelationships
            .Where(x => x.UserId == userId)
            .ToListAsync();
        var reviewWindow = DateTime.UtcNow.Date.AddDays(7);
        var dueReviews = await _context.LifeDecisions
            .Where(x => x.UserId == userId && x.ReviewDate != null && x.ReviewDate <= reviewWindow)
            .OrderBy(x => x.ReviewDate)
            .ToListAsync();

        var insights = BuildInsights(checkIns, allRelationships, dueReviews);
        var stats = BuildStats(checkIns, goals, relationships);
        return Ok(new LifeTrackerSummaryResponse(checkIns, goals, decisions, relationships, evidence, cloudLinks, stats, insights));
    }

    [HttpPost("relationships/{id:int}/contacted")]
    public async Task<ActionResult<LifeRelationship>> MarkContacted(int id)
    {
        var userId = GetUserId();
        var relationship = await _context.LifeRelationships.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (relationship == null) return NotFound();

        relationship.LastContactedAt = DateTime.UtcNow;
        relationship.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return Ok(relationship);
    }

    [HttpPost("weekly-summary")]
    public async Task<ActionResult<LifeWeeklySummaryResponse>> WeeklySummary([FromBody] LifeWeeklySummaryRequest? request)
    {
        var userId = GetUserId();
        var days = Math.Clamp(request?.Days ?? 7, 7, 31);
        var since = DateTime.UtcNow.Date.AddDays(-days + 1);

        var checkIns = await _context.LifeCheckIns
            .Where(x => x.UserId == userId && x.Date >= since)
            .OrderBy(x => x.Date)
            .ToListAsync();

        if (checkIns.Count == 0)
            return Ok(new LifeWeeklySummaryResponse("No check-ins in this period yet — log a few days and I'll have something to reflect on.", string.Empty, 0));

        var goals = await _context.LifeGoals.Where(x => x.UserId == userId).OrderByDescending(x => x.UpdatedAt).Take(20).ToListAsync();
        var relationships = await _context.LifeRelationships.Where(x => x.UserId == userId).ToListAsync();
        var dueReviews = await _context.LifeDecisions
            .Where(x => x.UserId == userId && x.ReviewDate != null && x.ReviewDate <= DateTime.UtcNow.Date.AddDays(7))
            .ToListAsync();

        var insights = BuildInsights(checkIns, relationships, dueReviews);

        var data = new StringBuilder();
        data.AppendLine($"Period: last {days} days, {checkIns.Count} check-ins logged.");
        data.AppendLine($"Averages — mood {checkIns.Average(x => x.Mood):0.0}/10, energy {checkIns.Average(x => x.Energy):0.0}/10, focus {checkIns.Average(x => x.Focus):0.0}/10, sleep {checkIns.Average(x => x.SleepHours):0.0}h/night, exercise {checkIns.Sum(x => x.ExerciseMinutes)}m total, screen {Math.Round(checkIns.Average(x => x.ScreenMinutes) / 60.0, 1)}h/day.");
        if (insights.Trends.Count > 0)
        {
            data.AppendLine("Observed patterns:");
            foreach (var t in insights.Trends) data.AppendLine($"- {t.Text}");
        }
        data.AppendLine($"Goals — {goals.Count(g => g.Status == "active")} active, {goals.Count(g => g.Status == "done")} done.");
        if (insights.RelationshipNudges.Count > 0)
            data.AppendLine($"People going cold: {string.Join(", ", insights.RelationshipNudges.Select(n => n.Name))}.");
        if (insights.DecisionReviews.Count > 0)
            data.AppendLine($"Decisions due for review: {string.Join(", ", insights.DecisionReviews.Select(d => d.Title))}.");

        var systemPrompt = """
            You are GenPrompt's weekly reflection coach. From the user's own life-tracker data,
            write a short, warm weekly summary (about 120-180 words). Cover: how the week went,
            one or two honest patterns worth noticing, and 2-3 concrete, gentle suggestions for
            next week. Ground every claim in the data provided — do not invent numbers. Avoid
            medical/clinical advice. Use plain prose, not headers or bullet lists.
            """;

        var answer = await _aiService.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = systemPrompt,
            UserInput = data.ToString(),
        });

        return Ok(new LifeWeeklySummaryResponse(answer.Result, answer.Model, answer.TokensUsed));
    }

    [HttpPost("checkins")]
    public async Task<ActionResult<LifeCheckIn>> UpsertCheckIn([FromBody] UpsertLifeCheckInRequest request)
    {
        var userId = GetUserId();
        var date = (request.Date ?? DateTime.UtcNow).Date;

        var checkIn = await _context.LifeCheckIns.FirstOrDefaultAsync(x => x.UserId == userId && x.Date == date);
        if (checkIn == null)
        {
            checkIn = new LifeCheckIn { UserId = userId, Date = date };
            _context.LifeCheckIns.Add(checkIn);
        }

        checkIn.Mood = ClampScore(request.Mood);
        checkIn.Energy = ClampScore(request.Energy);
        checkIn.Focus = ClampScore(request.Focus);
        checkIn.SleepHours = Math.Clamp(request.SleepHours, 0, 24);
        checkIn.ExerciseMinutes = Math.Clamp(request.ExerciseMinutes, 0, 1440);
        checkIn.ScreenMinutes = Math.Clamp(request.ScreenMinutes, 0, 1440);
        checkIn.MoneySpent = Math.Max(0, request.MoneySpent);
        checkIn.TopTasks = request.TopTasks?.Trim() ?? string.Empty;
        checkIn.Gratitude = request.Gratitude?.Trim() ?? string.Empty;
        checkIn.Notes = request.Notes?.Trim() ?? string.Empty;
        checkIn.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(checkIn);
    }

    [HttpPost("goals")]
    public async Task<ActionResult<LifeGoal>> CreateGoal([FromBody] UpsertLifeGoalRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest("Title is required.");

        var goal = new LifeGoal
        {
            UserId = GetUserId(),
            Title = request.Title.Trim(),
            Area = string.IsNullOrWhiteSpace(request.Area) ? "Growth" : request.Area.Trim(),
            TargetDate = request.TargetDate?.Date,
            Progress = Math.Clamp(request.Progress, 0, 100),
            Status = NormalizeStatus(request.Status),
            Notes = request.Notes?.Trim() ?? string.Empty
        };

        _context.LifeGoals.Add(goal);
        await _context.SaveChangesAsync();
        return Ok(goal);
    }

    [HttpPut("goals/{id:int}")]
    public async Task<ActionResult<LifeGoal>> UpdateGoal(int id, [FromBody] UpsertLifeGoalRequest request)
    {
        var userId = GetUserId();
        var goal = await _context.LifeGoals.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (goal == null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest("Title is required.");

        goal.Title = request.Title.Trim();
        goal.Area = string.IsNullOrWhiteSpace(request.Area) ? goal.Area : request.Area.Trim();
        goal.TargetDate = request.TargetDate?.Date;
        goal.Progress = Math.Clamp(request.Progress, 0, 100);
        goal.Status = NormalizeStatus(request.Status);
        goal.Notes = request.Notes?.Trim() ?? string.Empty;
        goal.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(goal);
    }

    [HttpDelete("goals/{id:int}")]
    public async Task<ActionResult> DeleteGoal(int id)
    {
        var userId = GetUserId();
        var goal = await _context.LifeGoals.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (goal == null) return NotFound();
        _context.LifeGoals.Remove(goal);
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("decisions")]
    public async Task<ActionResult<LifeDecision>> CreateDecision([FromBody] CreateLifeDecisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest("Title is required.");

        var decision = new LifeDecision
        {
            UserId = GetUserId(),
            Date = request.Date ?? DateTime.UtcNow,
            Title = request.Title.Trim(),
            Reasoning = request.Reasoning?.Trim() ?? string.Empty,
            Outcome = request.Outcome?.Trim() ?? string.Empty,
            ReviewDate = request.ReviewDate?.Date
        };

        _context.LifeDecisions.Add(decision);
        await _context.SaveChangesAsync();
        return Ok(decision);
    }

    [HttpPut("decisions/{id:int}")]
    public async Task<ActionResult<LifeDecision>> UpdateDecision(int id, [FromBody] CreateLifeDecisionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title)) return BadRequest("Title is required.");

        var userId = GetUserId();
        var decision = await _context.LifeDecisions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (decision == null) return NotFound();

        decision.Date = request.Date ?? decision.Date;
        decision.Title = request.Title.Trim();
        decision.Reasoning = request.Reasoning?.Trim() ?? string.Empty;
        decision.Outcome = request.Outcome?.Trim() ?? string.Empty;
        decision.ReviewDate = request.ReviewDate?.Date;

        await _context.SaveChangesAsync();
        return Ok(decision);
    }

    [HttpDelete("decisions/{id:int}")]
    public async Task<ActionResult> DeleteDecision(int id)
    {
        var userId = GetUserId();
        var decision = await _context.LifeDecisions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (decision == null) return NotFound();
        _context.LifeDecisions.Remove(decision);
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("relationships")]
    public async Task<ActionResult<LifeRelationship>> UpsertRelationship([FromBody] UpsertLifeRelationshipRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("Name is required.");

        var userId = GetUserId();
        var name = request.Name.Trim();
        var relationship = await _context.LifeRelationships
            .FirstOrDefaultAsync(x => x.UserId == userId && x.Name.ToLower() == name.ToLower());

        if (relationship == null)
        {
            relationship = new LifeRelationship { UserId = userId, Name = name };
            _context.LifeRelationships.Add(relationship);
        }

        relationship.Circle = string.IsNullOrWhiteSpace(request.Circle) ? relationship.Circle : request.Circle.Trim();
        relationship.LastContactedAt = request.LastContactedAt;
        relationship.Importance = ClampScore(request.Importance);
        relationship.Notes = request.Notes?.Trim() ?? string.Empty;
        relationship.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(relationship);
    }

    [HttpPut("relationships/{id:int}")]
    public async Task<ActionResult<LifeRelationship>> UpdateRelationship(int id, [FromBody] UpsertLifeRelationshipRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("Name is required.");

        var userId = GetUserId();
        var relationship = await _context.LifeRelationships.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (relationship == null) return NotFound();

        relationship.Name = request.Name.Trim();
        relationship.Circle = string.IsNullOrWhiteSpace(request.Circle) ? relationship.Circle : request.Circle.Trim();
        relationship.LastContactedAt = request.LastContactedAt;
        relationship.Importance = ClampScore(request.Importance);
        relationship.Notes = request.Notes?.Trim() ?? string.Empty;
        relationship.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return Ok(relationship);
    }

    [HttpDelete("relationships/{id:int}")]
    public async Task<ActionResult> DeleteRelationship(int id)
    {
        var userId = GetUserId();
        var relationship = await _context.LifeRelationships.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (relationship == null) return NotFound();
        _context.LifeRelationships.Remove(relationship);
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("evidence")]
    [RequestSizeLimit(MaxEvidenceSize * 10)]
    public async Task<ActionResult> UploadEvidence([FromForm] List<IFormFile> files, [FromForm] string? notes)
    {
        var userId = GetUserId();
        if (files == null || files.Count == 0) return BadRequest("No files provided.");
        if (files.Count > 10) return BadRequest("Maximum 10 files per upload.");

        var uploadsPath = GetEvidencePath();
        var uploaded = new List<LifeEvidenceSummary>();

        foreach (var file in files)
        {
            if (file.Length <= 0) continue;
            if (file.Length > MaxEvidenceSize) return BadRequest($"File '{file.FileName}' exceeds 10MB.");

            var ext = Path.GetExtension(file.FileName);
            if (!AllowedExtensions.Contains(ext)) return BadRequest($"File type '{ext}' is not supported.");

            var storedName = $"{Guid.NewGuid()}{ext.ToLowerInvariant()}";
            var path = Path.Combine(uploadsPath, storedName);
            await using (var stream = new FileStream(path, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var extracted = await ExtractEvidenceTextAsync(file, path);
            var evidence = new LifeEvidence
            {
                UserId = userId,
                FileName = file.FileName,
                StoredFileName = storedName,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                FileSize = file.Length,
                EvidenceType = file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image" : "document",
                ExtractedText = extracted.Length > 80_000 ? extracted[..80_000] : extracted,
                Notes = notes?.Trim() ?? string.Empty,
            };
            _context.LifeEvidence.Add(evidence);
            await _context.SaveChangesAsync();
            uploaded.Add(new LifeEvidenceSummary(evidence.Id, evidence.FileName, evidence.ContentType, evidence.FileSize, evidence.EvidenceType, evidence.Notes, evidence.CreatedAt));
        }

        return Ok(new { files = uploaded });
    }

    [HttpGet("evidence/{id:int}")]
    public async Task<ActionResult<LifeEvidenceDetail>> GetEvidence(int id)
    {
        var userId = GetUserId();
        var evidence = await _context.LifeEvidence.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (evidence == null) return NotFound();

        return Ok(new LifeEvidenceDetail(
            evidence.Id,
            evidence.FileName,
            evidence.ContentType,
            evidence.FileSize,
            evidence.EvidenceType,
            evidence.Notes,
            evidence.ExtractedText,
            evidence.CreatedAt));
    }

    [HttpGet("evidence/{id:int}/download")]
    public async Task<IActionResult> DownloadEvidence(int id)
    {
        var userId = GetUserId();
        var evidence = await _context.LifeEvidence.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (evidence == null) return NotFound();

        var path = Path.Combine(GetEvidencePath(), evidence.StoredFileName);
        if (!System.IO.File.Exists(path)) return NotFound("Stored file was not found.");

        var bytes = await System.IO.File.ReadAllBytesAsync(path);
        return File(bytes, evidence.ContentType, evidence.FileName);
    }

    [HttpDelete("evidence/{id:int}")]
    public async Task<ActionResult> DeleteEvidence(int id)
    {
        var userId = GetUserId();
        var evidence = await _context.LifeEvidence.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (evidence == null) return NotFound();

        var path = Path.Combine(GetEvidencePath(), evidence.StoredFileName);
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        _context.LifeEvidence.Remove(evidence);
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("cloud-links")]
    public async Task<ActionResult<LifeCloudLinkSummary>> SaveCloudLink([FromBody] UpsertLifeCloudLinkRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Url)) return BadRequest("Cloud link is required.");
        if (!Uri.TryCreate(request.Url.Trim(), UriKind.Absolute, out var uri)) return BadRequest("Enter a valid cloud URL.");

        var provider = NormalizeCloudProvider(request.Provider, uri.Host);
        var userId = GetUserId();
        var link = new LifeCloudLink
        {
            UserId = userId,
            Provider = provider,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? provider + " source" : request.DisplayName.Trim(),
            Url = request.Url.Trim(),
            Notes = request.Notes?.Trim() ?? string.Empty
        };
        await SyncCloudLinkAsync(link, uri);

        _context.LifeCloudLinks.Add(link);
        await _context.SaveChangesAsync();

        return Ok(ToCloudLinkSummary(link));
    }

    [HttpPost("cloud-links/{id:int}/sync")]
    public async Task<ActionResult<LifeCloudLinkSummary>> SyncCloudLink(int id)
    {
        var userId = GetUserId();
        var link = await _context.LifeCloudLinks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (link == null) return NotFound();
        if (!Uri.TryCreate(link.Url, UriKind.Absolute, out var uri)) return BadRequest("Saved cloud URL is invalid.");

        await SyncCloudLinkAsync(link, uri);
        await _context.SaveChangesAsync();
        return Ok(ToCloudLinkSummary(link));
    }

    [HttpDelete("cloud-links/{id:int}")]
    public async Task<ActionResult> DeleteCloudLink(int id)
    {
        var userId = GetUserId();
        var link = await _context.LifeCloudLinks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (link == null) return NotFound();
        _context.LifeCloudLinks.Remove(link);
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpPost("coach")]
    public async Task<ActionResult<LifeCoachResponse>> AskCoach([FromBody] LifeCoachRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question)) return BadRequest("Question is required.");

        var userId = GetUserId();
        var since = DateTime.UtcNow.Date.AddDays(-90);
        var checkIns = await _context.LifeCheckIns.Where(x => x.UserId == userId && x.Date >= since).OrderByDescending(x => x.Date).Take(45).ToListAsync();
        var goals = await _context.LifeGoals.Where(x => x.UserId == userId).OrderByDescending(x => x.UpdatedAt).Take(20).ToListAsync();
        var decisions = await _context.LifeDecisions.Where(x => x.UserId == userId).OrderByDescending(x => x.Date).Take(20).ToListAsync();
        var relationships = await _context.LifeRelationships.Where(x => x.UserId == userId).OrderByDescending(x => x.Importance).Take(20).ToListAsync();
        var evidence = await _context.LifeEvidence.Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedAt).Take(12).ToListAsync();
        var cloudLinks = await _context.LifeCloudLinks.Where(x => x.UserId == userId).OrderByDescending(x => x.UpdatedAt).Take(20).ToListAsync();

        var context = BuildLifeContext(checkIns, goals, decisions, relationships, evidence, cloudLinks);
        var systemPrompt = """
            You are GenPrompt Life Coach, a thoughtful decision-support agent.
            Use only the user's life tracker data and uploaded life evidence as grounding.
            Help the user clarify tradeoffs, spot patterns, ask better questions, and make informed decisions.
            Do not claim certainty. Do not provide medical, legal, investment, tax, or mental-health diagnosis advice.
            For high-stakes choices, recommend consulting a qualified professional.
            Structure answers with: read on the situation, options, tradeoffs, next 1-3 actions, and questions to answer.
            Be direct, warm, and practical.
            """;

        var answer = await _aiService.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = systemPrompt,
            UserInput = $"Life tracker context:\n{context}\n\nUser question:\n{request.Question.Trim()}"
        });

        return Ok(new LifeCoachResponse(answer.Result, answer.Model, answer.TokensUsed));
    }

    private static int ClampScore(int value) => Math.Clamp(value, 1, 10);

    private static string NormalizeStatus(string? status)
    {
        var normalized = (status ?? "active").Trim().ToLowerInvariant();
        return normalized is "active" or "paused" or "done" ? normalized : "active";
    }

    private static string NormalizeCloudProvider(string? provider, string host)
    {
        var normalized = (provider ?? string.Empty).Trim();
        if (normalized.Equals("OneDrive", StringComparison.OrdinalIgnoreCase)) return "OneDrive";
        if (normalized.Equals("Google Drive", StringComparison.OrdinalIgnoreCase)) return "Google Drive";
        if (host.Contains("1drv", StringComparison.OrdinalIgnoreCase) || host.Contains("onedrive", StringComparison.OrdinalIgnoreCase) || host.Contains("sharepoint", StringComparison.OrdinalIgnoreCase)) return "OneDrive";
        return "Google Drive";
    }

    private string GetEvidencePath()
    {
        var path = Path.Combine(_env.ContentRootPath, "uploads", "life-evidence");
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task<string> ExtractEvidenceTextAsync(IFormFile file, string path)
    {
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        try
        {
            if (file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await System.IO.File.ReadAllBytesAsync(path);
                var base64 = Convert.ToBase64String(bytes);
                return await _aiService.DescribeScreenAsync(base64);
            }

            if (ext == ".pdf")
            {
                using var pdf = PdfDocument.Open(path);
                var sb = new StringBuilder();
                foreach (var page in pdf.GetPages())
                    sb.AppendLine(string.Join(" ", page.GetWords().Select(w => w.Text)));
                return sb.ToString().Trim();
            }

            if (ext == ".docx")
            {
                await using var stream = System.IO.File.OpenRead(path);
                using var doc = WordprocessingDocument.Open(stream, false);
                return doc.MainDocumentPart?.Document?.Body?.InnerText?.Trim() ?? string.Empty;
            }

            return await System.IO.File.ReadAllTextAsync(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Life evidence extraction failed for {FileName}", file.FileName);
            return $"Uploaded file: {file.FileName}. Text extraction was not available.";
        }
    }

    private async Task SyncCloudLinkAsync(LifeCloudLink link, Uri uri)
    {
        link.UpdatedAt = DateTime.UtcNow;
        link.LastSyncedAt = DateTime.UtcNow;

        try
        {
            var text = await FetchCloudContextAsync(uri);
            link.ExtractedText = text.Length > MaxCloudContextChars ? text[..MaxCloudContextChars] : text;
            link.SyncStatus = string.IsNullOrWhiteSpace(link.ExtractedText)
                ? "No readable text found. Make the source shareable or upload the file as evidence."
                : "Synced";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Life cloud source sync failed for {Url}", link.Url);
            link.ExtractedText = string.Empty;
            link.SyncStatus = "Could not read this link. Make it public/shareable or upload the file as evidence.";
        }
    }

    private static async Task<string> FetchCloudContextAsync(Uri uri)
    {
        var candidates = BuildCloudFetchCandidates(uri);
        Exception? lastError = null;

        foreach (var candidate in candidates)
        {
            try
            {
                using var response = await CloudHttp.GetAsync(candidate, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 2_000_000)
                    throw new InvalidOperationException("Cloud source is too large to sync directly.");
                var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
                await using var stream = await response.Content.ReadAsStreamAsync();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 8192, leaveOpen: false);
                var raw = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                return contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                    ? HtmlToPlainText(raw)
                    : raw.Trim();
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("No readable cloud source candidates were available.");
    }

    private static IEnumerable<Uri> BuildCloudFetchCandidates(Uri uri)
    {
        yield return uri;

        var url = uri.ToString();
        var googleDoc = Regex.Match(url, @"docs\.google\.com/document/d/([^/]+)", RegexOptions.IgnoreCase);
        if (googleDoc.Success)
            yield return new Uri($"https://docs.google.com/document/d/{googleDoc.Groups[1].Value}/export?format=txt");

        var googleSheet = Regex.Match(url, @"docs\.google\.com/spreadsheets/d/([^/]+)", RegexOptions.IgnoreCase);
        if (googleSheet.Success)
            yield return new Uri($"https://docs.google.com/spreadsheets/d/{googleSheet.Groups[1].Value}/export?format=csv");

        var googleFile = Regex.Match(url, @"drive\.google\.com/file/d/([^/]+)", RegexOptions.IgnoreCase);
        if (googleFile.Success)
            yield return new Uri($"https://drive.google.com/uc?export=download&id={googleFile.Groups[1].Value}");
    }

    private static string HtmlToPlainText(string html)
    {
        var withoutScripts = Regex.Replace(html, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var withBreaks = Regex.Replace(withoutScripts, "</(p|div|li|tr|h[1-6])>", "\n", RegexOptions.IgnoreCase);
        var noTags = Regex.Replace(withBreaks, "<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        return Regex.Replace(decoded, @"[ \t\r\f\v]+", " ").Replace("\n ", "\n").Trim();
    }

    private static LifeCloudLinkSummary ToCloudLinkSummary(LifeCloudLink link) =>
        new(
            link.Id,
            link.Provider,
            link.DisplayName,
            link.Url,
            link.Notes,
            link.SyncStatus,
            link.LastSyncedAt,
            link.ExtractedText.Length > 400 ? link.ExtractedText[..400] : link.ExtractedText,
            link.CreatedAt,
            link.UpdatedAt);

    private static string BuildLifeContext(
        List<LifeCheckIn> checkIns,
        List<LifeGoal> goals,
        List<LifeDecision> decisions,
        List<LifeRelationship> relationships,
        List<LifeEvidence> evidence,
        List<LifeCloudLink> cloudLinks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Recent check-ins:");
        foreach (var x in checkIns.Take(20))
            sb.AppendLine($"- {x.Date:yyyy-MM-dd}: mood {x.Mood}/10, energy {x.Energy}/10, focus {x.Focus}/10, sleep {x.SleepHours}h, exercise {x.ExerciseMinutes}m, screen {x.ScreenMinutes}m, tasks: {x.TopTasks}, gratitude: {x.Gratitude}, notes: {x.Notes}");

        sb.AppendLine("\nGoals:");
        foreach (var g in goals)
            sb.AppendLine($"- {g.Title} [{g.Area}] {g.Progress}% {g.Status}, target {g.TargetDate:yyyy-MM-dd}, notes: {g.Notes}");

        sb.AppendLine("\nDecisions:");
        foreach (var d in decisions)
            sb.AppendLine($"- {d.Date:yyyy-MM-dd}: {d.Title}. Reasoning: {d.Reasoning}. Outcome: {d.Outcome}. Review: {d.ReviewDate:yyyy-MM-dd}");

        sb.AppendLine("\nRelationships:");
        foreach (var r in relationships)
            sb.AppendLine($"- {r.Name} [{r.Circle}], importance {r.Importance}/10, last contact {r.LastContactedAt:yyyy-MM-dd}, notes: {r.Notes}");

        sb.AppendLine("\nUploaded life evidence:");
        foreach (var e in evidence)
        {
            var text = e.ExtractedText.Length > 4000 ? e.ExtractedText[..4000] : e.ExtractedText;
            sb.AppendLine($"- {e.FileName} ({e.EvidenceType}, {e.CreatedAt:yyyy-MM-dd}). Notes: {e.Notes}. Extracted context: {text}");
        }

        sb.AppendLine("\nSaved cloud sources:");
        foreach (var link in cloudLinks)
        {
            var text = link.ExtractedText.Length > 4000 ? link.ExtractedText[..4000] : link.ExtractedText;
            sb.AppendLine($"- {link.Provider}: {link.DisplayName}. URL: {link.Url}. Notes: {link.Notes}. Sync: {link.SyncStatus}. Saved {link.CreatedAt:yyyy-MM-dd}. Extracted context: {text}");
        }

        return sb.ToString();
    }

    private static LifeInsights BuildInsights(
        List<LifeCheckIn> checkIns,
        List<LifeRelationship> relationships,
        List<LifeDecision> dueReviews)
    {
        var trends = new List<LifeInsight>();

        if (checkIns.Count >= 5)
        {
            var drivers = new (string Name, Func<LifeCheckIn, double> Select)[]
            {
                ("sleep", x => x.SleepHours),
                ("exercise", x => x.ExerciseMinutes),
                ("screen time", x => x.ScreenMinutes),
            };
            var outcomes = new (string Name, Func<LifeCheckIn, double> Select)[]
            {
                ("mood", x => x.Mood),
                ("energy", x => x.Energy),
                ("focus", x => x.Focus),
            };

            var candidates = new List<(double Strength, LifeInsight Insight)>();
            foreach (var outcome in outcomes)
            {
                var outcomeSeries = checkIns.Select(outcome.Select).ToList();
                foreach (var driver in drivers)
                {
                    var r = Correlation(outcomeSeries, checkIns.Select(driver.Select).ToList());
                    if (double.IsNaN(r) || Math.Abs(r) < 0.35) continue;

                    var direction = r > 0 ? "higher" : "lower";
                    var text = $"More {driver.Name} tracks with {direction} {outcome.Name} (r={r:0.00}).";
                    candidates.Add((Math.Abs(r), new LifeInsight("correlation", text, Math.Round(r, 2))));
                }
            }

            if (candidates.Count > 0)
                trends.AddRange(candidates.OrderByDescending(c => c.Strength).Take(4).Select(c => c.Insight));
            else
                trends.Add(new LifeInsight("info", "No strong patterns between your habits and how you feel yet — keep logging.", null));
        }
        else
        {
            trends.Add(new LifeInsight("info", $"Log a few more daily check-ins ({checkIns.Count}/5) to unlock trend correlations.", null));
        }

        var now = DateTime.UtcNow.Date;
        var nudges = relationships
            .Select(r =>
            {
                var threshold = NudgeThresholdDays(r.Importance);
                int? daysSince = r.LastContactedAt.HasValue ? (now - r.LastContactedAt.Value.Date).Days : null;
                return (r, threshold, daysSince);
            })
            .Where(x => x.daysSince == null || x.daysSince.Value >= x.threshold)
            .OrderByDescending(x => x.daysSince ?? int.MaxValue)
            .ThenByDescending(x => x.r.Importance)
            .Take(6)
            .Select(x => new RelationshipNudge(x.r.Id, x.r.Name, x.r.Circle, x.r.Importance, x.r.LastContactedAt, x.daysSince, x.threshold))
            .ToList();

        var reviews = dueReviews
            .Where(d => d.ReviewDate.HasValue)
            .OrderBy(d => d.ReviewDate)
            .Select(d => new DecisionReview(
                d.Id,
                d.Title,
                d.ReviewDate!.Value,
                (d.ReviewDate.Value.Date - now).Days,
                !string.IsNullOrWhiteSpace(d.Outcome)))
            .ToList();

        return new LifeInsights(trends, nudges, reviews);
    }

    // Shorter cadence for more important people: VIPs get a weekly nudge, acquaintances bi-monthly.
    private static int NudgeThresholdDays(int importance) => importance switch
    {
        >= 9 => 7,
        >= 7 => 14,
        >= 4 => 30,
        _ => 60,
    };

    // Pearson correlation. Returns NaN when there isn't enough variance/data to be meaningful.
    private static double Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var n = Math.Min(a.Count, b.Count);
        if (n < 3) return double.NaN;

        double meanA = 0, meanB = 0;
        for (var i = 0; i < n; i++) { meanA += a[i]; meanB += b[i]; }
        meanA /= n; meanB /= n;

        double cov = 0, varA = 0, varB = 0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i] - meanA;
            var db = b[i] - meanB;
            cov += da * db;
            varA += da * da;
            varB += db * db;
        }

        if (varA == 0 || varB == 0) return double.NaN;
        return cov / Math.Sqrt(varA * varB);
    }

    private static LifeTrackerStats BuildStats(List<LifeCheckIn> checkIns, List<LifeGoal> goals, List<LifeRelationship> relationships)
    {
        var latest = checkIns.LastOrDefault();
        var avgMood = checkIns.Count == 0 ? 0 : Math.Round(checkIns.Average(x => x.Mood), 1);
        var avgEnergy = checkIns.Count == 0 ? 0 : Math.Round(checkIns.Average(x => x.Energy), 1);
        var avgFocus = checkIns.Count == 0 ? 0 : Math.Round(checkIns.Average(x => x.Focus), 1);
        var avgSleep = checkIns.Count == 0 ? 0 : Math.Round(checkIns.Average(x => x.SleepHours), 1);
        var exercise = checkIns.Sum(x => x.ExerciseMinutes);
        var screen = checkIns.Sum(x => x.ScreenMinutes);
        var activeGoals = goals.Count(x => x.Status == "active");
        var completedGoals = goals.Count(x => x.Status == "done");
        var staleRelationships = relationships.Count(x => !x.LastContactedAt.HasValue || x.LastContactedAt.Value < DateTime.UtcNow.AddDays(-21));
        var lifeScore = latest == null
            ? 0
            : Math.Round((latest.Mood + latest.Energy + latest.Focus + Math.Min(10, latest.SleepHours / 8 * 10)) / 4, 1);

        return new LifeTrackerStats(avgMood, avgEnergy, avgFocus, avgSleep, exercise, screen, activeGoals, completedGoals, staleRelationships, lifeScore);
    }
}

public record LifeTrackerSummaryResponse(
    List<LifeCheckIn> CheckIns,
    List<LifeGoal> Goals,
    List<LifeDecision> Decisions,
    List<LifeRelationship> Relationships,
    List<LifeEvidenceSummary> Evidence,
    List<LifeCloudLinkSummary> CloudLinks,
    LifeTrackerStats Stats,
    LifeInsights Insights);

public record LifeInsight(string Kind, string Text, double? Value);

public record RelationshipNudge(
    int Id,
    string Name,
    string Circle,
    int Importance,
    DateTime? LastContactedAt,
    int? DaysSinceContact,
    int ThresholdDays);

public record DecisionReview(
    int Id,
    string Title,
    DateTime ReviewDate,
    int DaysUntil,
    bool HasOutcome);

public record LifeInsights(
    List<LifeInsight> Trends,
    List<RelationshipNudge> RelationshipNudges,
    List<DecisionReview> DecisionReviews);

public class LifeWeeklySummaryRequest
{
    public int Days { get; set; } = 7;
}

public record LifeWeeklySummaryResponse(string Summary, string Model, int TokensUsed);

public record LifeEvidenceSummary(
    int Id,
    string FileName,
    string ContentType,
    long FileSize,
    string EvidenceType,
    string Notes,
    DateTime CreatedAt);

public record LifeEvidenceDetail(
    int Id,
    string FileName,
    string ContentType,
    long FileSize,
    string EvidenceType,
    string Notes,
    string ExtractedText,
    DateTime CreatedAt);

public record LifeCloudLinkSummary(
    int Id,
    string Provider,
    string DisplayName,
    string Url,
    string Notes,
    string SyncStatus,
    DateTime? LastSyncedAt,
    string ExtractedPreview,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record LifeTrackerStats(
    double AvgMood,
    double AvgEnergy,
    double AvgFocus,
    double AvgSleep,
    int ExerciseMinutes,
    int ScreenMinutes,
    int ActiveGoals,
    int CompletedGoals,
    int StaleRelationships,
    double LifeScore);

public class UpsertLifeCheckInRequest
{
    public DateTime? Date { get; set; }
    public int Mood { get; set; } = 5;
    public int Energy { get; set; } = 5;
    public int Focus { get; set; } = 5;
    public double SleepHours { get; set; } = 7;
    public int ExerciseMinutes { get; set; }
    public int ScreenMinutes { get; set; }
    public decimal MoneySpent { get; set; }
    public string? TopTasks { get; set; }
    public string? Gratitude { get; set; }
    public string? Notes { get; set; }
}

public class UpsertLifeGoalRequest
{
    public string Title { get; set; } = string.Empty;
    public string? Area { get; set; }
    public DateTime? TargetDate { get; set; }
    public int Progress { get; set; }
    public string? Status { get; set; }
    public string? Notes { get; set; }
}

public class CreateLifeDecisionRequest
{
    public DateTime? Date { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Reasoning { get; set; }
    public string? Outcome { get; set; }
    public DateTime? ReviewDate { get; set; }
}

public class UpsertLifeRelationshipRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Circle { get; set; }
    public DateTime? LastContactedAt { get; set; }
    public int Importance { get; set; } = 5;
    public string? Notes { get; set; }
}

public class LifeCoachRequest
{
    public string Question { get; set; } = string.Empty;
}

public record LifeCoachResponse(string Answer, string Model, int TokensUsed);

public class UpsertLifeCloudLinkRequest
{
    public string Provider { get; set; } = "Google Drive";
    public string DisplayName { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Notes { get; set; }
}
