using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Models;
using PromptStudio.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace PromptStudio.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class ExecutionsController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly IAIService _aiService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ExecutionsController> _logger;
    private readonly ILogger<OpenAIService> _logger2;

    public ExecutionsController(
        PromptDbContext context,
        IAIService aiService,
        IConfiguration configuration,
        ILogger<ExecutionsController> logger,
        ILogger<OpenAIService> logger2)
    {
        _context = context;
        _aiService = aiService;
        _configuration = configuration;
        _logger = logger;
        _logger2 = logger2;
    }

    private string GetCurrentUserId()
    {
        return User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
    }

    // POST /api/executions/stream - SSE streaming endpoint
    [HttpPost("stream")]
    [EnableRateLimiting("ai-generation")]
    public async Task Stream([FromBody] StreamExecutionRequest request)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");

        try
        {
            // Content moderation on the latest user message
            var lastUserMessage = request.Messages?.LastOrDefault(m => m.Role == "user")?.Content;
            if (!string.IsNullOrWhiteSpace(lastUserMessage))
            {
                var moderation = await _aiService.ModerateContentAsync(lastUserMessage);
                if (moderation.IsFlagged)
                {
                    await WriteSseEvent(new { type = "error", message = "Your message contains content that violates our usage policy. Please try a different message." });
                    return;
                }
            }

            var messages = (request.Messages ?? [])
                .Select(m => new ConversationMessage(m.Role, m.Content))
                .ToList();

            // Inject language instruction into system prompt if non-English
            var systemPrompt = request.SystemPrompt ?? "";
            if (!string.IsNullOrWhiteSpace(request.Language) && request.Language != "en")
            {
                var langNames = new Dictionary<string, string>
                {
                    ["es"] = "Spanish", ["fr"] = "French", ["de"] = "German",
                    ["pt-BR"] = "Brazilian Portuguese", ["ar"] = "Arabic",
                    ["hi"] = "Hindi", ["ja"] = "Japanese"
                };
                if (langNames.TryGetValue(request.Language, out var langName))
                    systemPrompt = $"IMPORTANT: Always respond in {langName}.\n\n{systemPrompt}";
            }

            var aiService = _aiService;

            // If user supplied their own OpenAI key, use a temporary OpenAI service
            if (!string.IsNullOrWhiteSpace(request.UserApiKey))
            {
                var tempConfig = new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenAI:ApiKey"] = request.UserApiKey })
                    .Build();
                aiService = new OpenAIService(tempConfig, _logger2);
            }

            int tokensUsed = 0;
            await foreach (var token in aiService.StreamExecuteAsync(systemPrompt, messages, request.Model, HttpContext.RequestAborted))
            {
                await WriteSseEvent(new { type = "token", content = token });
                tokensUsed++;
            }

            await WriteSseEvent(new { type = "done", tokensUsed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during prompt execution streaming");
            var errorMessage = ex.Message.Contains("API key")
                ? "Invalid API key. Please check your API key in Settings."
                : "An error occurred while generating the response. Please try again.";
            await WriteSseEvent(new { type = "error", message = errorMessage });
        }
    }

    private async Task WriteSseEvent(object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await Response.WriteAsync($"data: {json}\n\n");
        await Response.Body.FlushAsync();
    }

    // POST /api/executions - Save execution
    [HttpPost]
    public async Task<ActionResult<PromptExecution>> SaveExecution([FromBody] SaveExecutionRequest request)
    {
        var userId = GetCurrentUserId();

        PromptExecution? execution;

        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            // Update existing execution
            execution = await _context.PromptExecutions
                .FirstOrDefaultAsync(e => e.Id == request.Id && e.UserId == userId);

            if (execution == null)
                return NotFound();

            execution.MessagesJson = request.MessagesJson;
            execution.TotalTokensUsed = request.TotalTokensUsed;
            execution.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            // Create new execution
            execution = new PromptExecution
            {
                PromptId = request.PromptId,
                UserId = userId,
                PromptName = request.PromptName,
                SystemPrompt = request.SystemPrompt,
                Model = request.Model ?? "gpt-4o-mini",
                MessagesJson = request.MessagesJson,
                TotalTokensUsed = request.TotalTokensUsed
            };
            _context.PromptExecutions.Add(execution);
        }

        await _context.SaveChangesAsync();
        return Ok(new
        {
            execution.Id,
            execution.PromptId,
            execution.PromptName,
            execution.SystemPrompt,
            execution.Model,
            execution.MessagesJson,
            execution.TotalTokensUsed,
            execution.CreatedAt,
            execution.UpdatedAt
        });
    }

    // GET /api/executions - List user's executions
    [HttpGet]
    public async Task<ActionResult> GetExecutions(
        [FromQuery] string? promptId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var userId = GetCurrentUserId();
        var query = _context.PromptExecutions
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt);

        if (!string.IsNullOrWhiteSpace(promptId))
        {
            query = (IOrderedQueryable<PromptExecution>)query.Where(e => e.PromptId == promptId);
        }

        var total = await query.CountAsync();
        var rawExecutions = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new
            {
                e.Id,
                e.PromptId,
                e.PromptName,
                e.Model,
                e.TotalTokensUsed,
                e.CreatedAt,
                e.UpdatedAt,
                e.MessagesJson
            })
            .ToListAsync();

        var executions = rawExecutions.Select(e => new
        {
            e.Id,
            e.PromptId,
            e.PromptName,
            e.Model,
            e.TotalTokensUsed,
            e.CreatedAt,
            e.UpdatedAt,
            MessageCount = e.MessagesJson.Length > 2 ? e.MessagesJson.Split("\"role\"").Length - 1 : 0
        });

        return Ok(new { executions, total, page, pageSize });
    }

    // GET /api/executions/{id} - Get single execution
    [HttpGet("{id}")]
    public async Task<ActionResult<PromptExecution>> GetExecution(string id)
    {
        var userId = GetCurrentUserId();
        var execution = await _context.PromptExecutions
            .FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);

        if (execution == null)
            return NotFound();

        return Ok(new
        {
            execution.Id,
            execution.PromptId,
            execution.PromptName,
            execution.SystemPrompt,
            execution.Model,
            execution.MessagesJson,
            execution.TotalTokensUsed,
            execution.CreatedAt,
            execution.UpdatedAt
        });
    }

    // DELETE /api/executions/{id} - Delete execution
    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteExecution(string id)
    {
        var userId = GetCurrentUserId();
        var execution = await _context.PromptExecutions
            .FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);

        if (execution == null)
            return NotFound();

        _context.PromptExecutions.Remove(execution);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // POST /api/executions/{id}/rate - Thumbs up/down
    [HttpPost("{id}/rate")]
    public async Task<ActionResult> RateExecution(string id, [FromBody] RateExecutionRequest request)
    {
        var userId = GetCurrentUserId();
        var execution = await _context.PromptExecutions
            .FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);

        if (execution == null)
            return NotFound();

        execution.Rating = request.Rating; // 1 = up, -1 = down, 0 = unrate
        execution.RatingNote = request.Note;
        execution.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new { execution.Id, execution.Rating, execution.RatingNote });
    }

    // GET /api/executions/insights
    [HttpGet("insights")]
    public async Task<ActionResult> GetInsights()
    {
        var userId = GetCurrentUserId();
        var executions = await _context.PromptExecutions
            .Where(e => e.UserId == userId)
            .Select(e => new { e.PromptId, e.PromptName, e.Model, e.Rating, e.TotalTokensUsed, e.CreatedAt })
            .ToListAsync();

        var thumbsUp = executions.Count(e => e.Rating == 1);
        var thumbsDown = executions.Count(e => e.Rating == -1);

        var topPrompts = executions
            .Where(e => e.Rating == 1 && e.PromptId != null)
            .GroupBy(e => new { e.PromptId, e.PromptName })
            .Select(g => new { g.Key.PromptName, PositiveRatings = g.Count() })
            .OrderByDescending(x => x.PositiveRatings).Take(5).ToList();

        var worstPrompts = executions
            .Where(e => e.Rating == -1 && e.PromptId != null)
            .GroupBy(e => new { e.PromptId, e.PromptName })
            .Select(g => new { g.Key.PromptName, NegativeRatings = g.Count() })
            .OrderByDescending(x => x.NegativeRatings).Take(5).ToList();

        var byModel = executions
            .GroupBy(e => e.Model)
            .Select(g => new { Model = g.Key, Count = g.Count(), AvgRating = Math.Round(g.Average(e => (double)e.Rating), 2) })
            .OrderByDescending(x => x.Count).ToList();

        var since = DateTime.UtcNow.AddDays(-30);
        var dailyUsage = executions
            .Where(e => e.CreatedAt >= since)
            .GroupBy(e => e.CreatedAt.Date)
            .Select(g => new { Date = g.Key.ToString("yyyy-MM-dd"), Count = g.Count() })
            .OrderBy(x => x.Date).ToList();

        return Ok(new
        {
            total = executions.Count,
            thumbsUp,
            thumbsDown,
            totalTokens = executions.Sum(e => e.TotalTokensUsed),
            topPrompts,
            worstPrompts,
            byModel,
            dailyUsage
        });
    }

    // GET /api/executions/suggestions
    [HttpGet("suggestions")]
    public async Task<ActionResult> GetSuggestions()
    {
        var userId = GetCurrentUserId();
        var now = DateTime.UtcNow;
        var sevenDaysAgo = now.AddDays(-7);
        var thirtyDaysAgo = now.AddDays(-30);

        var suggestions = new List<object>();

        // --- 1. Frequent: prompts run 5+ times in last 7 days ---
        var recentExecs = await _context.PromptExecutions
            .Where(e => e.UserId == userId && e.PromptId != null && e.CreatedAt >= sevenDaysAgo)
            .Select(e => new { e.PromptId, e.PromptName })
            .ToListAsync();

        var frequent = recentExecs
            .GroupBy(e => new { e.PromptId, e.PromptName })
            .Select(g => new { g.Key.PromptId, g.Key.PromptName, Count = g.Count() })
            .Where(x => x.Count >= 5)
            .OrderByDescending(x => x.Count)
            .FirstOrDefault();

        if (frequent != null)
            suggestions.Add(new {
                type = "frequent",
                title = "Trending prompt",
                message = $"You've run \"{frequent.PromptName}\" {frequent.Count} times this week.",
                actionType = "pin",
                promptId = frequent.PromptId,
                promptName = frequent.PromptName,
                count = frequent.Count
            });

        // --- 2. Unorganized: prompts with no category that have run executions ---
        var executedPromptIds = await _context.PromptExecutions
            .Where(e => e.UserId == userId && e.PromptId != null)
            .Select(e => e.PromptId!)
            .Distinct()
            .ToListAsync();

        var unorganizedCount = await _context.Prompts
            .Where(p => p.UserId == userId
                && (p.Category == null || p.Category == "")
                && executedPromptIds.Contains(p.Id))
            .CountAsync();

        if (unorganizedCount >= 3)
            suggestions.Add(new {
                type = "unorganized",
                title = "Organize your prompts",
                message = $"{unorganizedCount} prompts have no folder. Group them for easier access.",
                actionType = "organize",
                promptId = (string?)null,
                promptName = (string?)null,
                count = unorganizedCount
            });

        // --- 3. Stale: prompts not run in 30+ days (but were run before) ---
        var recentlyUsedIds = await _context.PromptExecutions
            .Where(e => e.UserId == userId && e.PromptId != null && e.CreatedAt >= thirtyDaysAgo)
            .Select(e => e.PromptId!)
            .Distinct()
            .ToListAsync();

        var staleCount = await _context.Prompts
            .Where(p => p.UserId == userId
                && executedPromptIds.Contains(p.Id)
                && !recentlyUsedIds.Contains(p.Id))
            .CountAsync();

        if (staleCount >= 3)
            suggestions.Add(new {
                type = "stale",
                title = "Clean up stale prompts",
                message = $"{staleCount} prompts haven't been used in 30+ days. Archive to declutter.",
                actionType = "archive",
                promptId = (string?)null,
                promptName = (string?)null,
                count = staleCount
            });

        return Ok(suggestions);
    }
}

// Request DTOs
public class StreamExecutionRequest
{
    public string? SystemPrompt { get; set; }
    public List<ChatMessageDto>? Messages { get; set; }
    public string? Model { get; set; }
    public string? UserApiKey { get; set; }
    public string? Language { get; set; }
}

public class ChatMessageDto
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}

public class SaveExecutionRequest
{
    public string? Id { get; set; }
    public string? PromptId { get; set; }
    public string PromptName { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string MessagesJson { get; set; } = "[]";
    public int TotalTokensUsed { get; set; }
}

public class RateExecutionRequest
{
    public int Rating { get; set; } // 1, -1, or 0
    public string? Note { get; set; }
}
