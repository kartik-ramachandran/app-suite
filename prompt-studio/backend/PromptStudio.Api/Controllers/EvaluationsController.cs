using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Models;
using PromptStudio.Api.Services;
using System.Diagnostics;
using System.Security.Claims;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace PromptStudio.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class EvaluationsController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly IAIService _aiService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<EvaluationsController> _logger;

    public EvaluationsController(
        PromptDbContext context,
        IAIService aiService,
        IConfiguration configuration,
        ILogger<EvaluationsController> logger)
    {
        _context = context;
        _aiService = aiService;
        _configuration = configuration;
        _logger = logger;
    }

    private string GetCurrentUserId() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    // POST /api/evaluations - Create and run A/B test
    [HttpPost]
    [EnableRateLimiting("ai-generation")]
    public async Task<ActionResult> CreateEvaluation([FromBody] CreateEvaluationRequest request)
    {
        var userId = GetCurrentUserId();
        var apiKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(500, new { error = "OpenAI API key not configured." });

        var eval = new PromptEvaluation
        {
            UserId = userId,
            Name = request.Name,
            PromptA = request.PromptA,
            PromptB = request.PromptB,
            TestInput = request.TestInput,
            Model = request.Model ?? "gpt-4o-mini",
            Status = EvalStatus.Running
        };
        _context.PromptEvaluations.Add(eval);
        await _context.SaveChangesAsync();

        try
        {
            var chatClient = new OpenAI.Chat.ChatClient(model: eval.Model, apiKey: apiKey);
            var userMsg = request.TestInput ?? "";

            // Run A and B in parallel
            var swA = Stopwatch.StartNew();
            var swB = Stopwatch.StartNew();

            var taskA = RunPrompt(chatClient, eval.PromptA, userMsg, swA);
            var taskB = RunPrompt(chatClient, eval.PromptB, userMsg, swB);
            await Task.WhenAll(taskA, taskB);

            eval.ResultA = taskA.Result.text;
            eval.TokensA = taskA.Result.tokens;
            eval.LatencyAMs = taskA.Result.latency;
            eval.ResultB = taskB.Result.text;
            eval.TokensB = taskB.Result.tokens;
            eval.LatencyBMs = taskB.Result.latency;
            eval.Status = EvalStatus.Completed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running evaluation {Id}", eval.Id);
            eval.Status = EvalStatus.Failed;
        }

        eval.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(ToDto(eval));
    }

    private async Task<(string text, int tokens, long latency)> RunPrompt(
        OpenAI.Chat.ChatClient client, string systemPrompt, string userInput, Stopwatch sw)
    {
        var messages = new List<OpenAI.Chat.ChatMessage>
        {
            OpenAI.Chat.ChatMessage.CreateSystemMessage(systemPrompt)
        };
        if (!string.IsNullOrWhiteSpace(userInput))
            messages.Add(OpenAI.Chat.ChatMessage.CreateUserMessage(userInput));

        var response = await client.CompleteChatAsync(messages);
        sw.Stop();
        return (
            response.Value.Content[0].Text,
            response.Value.Usage.TotalTokenCount,
            sw.ElapsedMilliseconds
        );
    }

    // GET /api/evaluations - List user's evaluations
    [HttpGet]
    public async Task<ActionResult> GetEvaluations([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var userId = GetCurrentUserId();
        var query = _context.PromptEvaluations
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt);

        var total = await query.CountAsync();
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();

        return Ok(new { evaluations = items.Select(ToDto), total, page, pageSize });
    }

    // GET /api/evaluations/{id}
    [HttpGet("{id}")]
    public async Task<ActionResult> GetEvaluation(string id)
    {
        var userId = GetCurrentUserId();
        var eval = await _context.PromptEvaluations
            .FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);

        if (eval == null) return NotFound();
        return Ok(ToDto(eval));
    }

    // POST /api/evaluations/{id}/vote - Submit winner
    [HttpPost("{id}/vote")]
    public async Task<ActionResult> Vote(string id, [FromBody] VoteRequest request)
    {
        var userId = GetCurrentUserId();
        var eval = await _context.PromptEvaluations
            .FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);

        if (eval == null) return NotFound();
        if (eval.Status != EvalStatus.Completed)
            return BadRequest(new { error = "Evaluation must be completed before voting." });

        eval.Winner = request.Winner; // "A", "B", or "Tie"
        eval.Notes = request.Notes;
        eval.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(ToDto(eval));
    }

    // DELETE /api/evaluations/{id}
    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteEvaluation(string id)
    {
        var userId = GetCurrentUserId();
        var eval = await _context.PromptEvaluations
            .FirstOrDefaultAsync(e => e.Id == id && e.UserId == userId);

        if (eval == null) return NotFound();
        _context.PromptEvaluations.Remove(eval);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private static object ToDto(PromptEvaluation e) => new
    {
        e.Id, e.Name, e.PromptA, e.PromptB, e.TestInput,
        e.ResultA, e.ResultB, e.Model,
        e.TokensA, e.TokensB, e.LatencyAMs, e.LatencyBMs,
        e.Winner, e.Notes, Status = e.Status.ToString(),
        e.CreatedAt, e.UpdatedAt
    };
}

public class CreateEvaluationRequest
{
    public string Name { get; set; } = string.Empty;
    public string PromptA { get; set; } = string.Empty;
    public string PromptB { get; set; } = string.Empty;
    public string? TestInput { get; set; }
    public string? Model { get; set; }
}

public class VoteRequest
{
    public string Winner { get; set; } = string.Empty; // "A", "B", "Tie"
    public string? Notes { get; set; }
}
