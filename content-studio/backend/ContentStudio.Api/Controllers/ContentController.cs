using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ContentStudio.Api.Data;
using ContentStudio.Api.Models;
using ContentStudio.Api.Services;
using System.Security.Claims;
using System.Text.Json;

namespace ContentStudio.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("ai-generation")]
public class ContentController : ControllerBase
{
    private readonly ContentGenerationService _contentService;
    private readonly PromptDbContext _context;
    private readonly ILogger<ContentController> _logger;

    public ContentController(ContentGenerationService contentService, PromptDbContext context, ILogger<ContentController> logger)
    {
        _contentService = contentService;
        _context = context;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    [HttpPost("generate")]
    public async Task Generate([FromBody] ContentGenerateRequest req)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");

        if (string.IsNullOrWhiteSpace(req.Topic))
        {
            await WriteSse(new { type = "error", message = "Topic is required" });
            return;
        }

        var userId = GetUserId();
        var userSettings = await _context.UserAiSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        var apiKey = !string.IsNullOrWhiteSpace(req.UserApiKey) ? req.UserApiKey
            : !string.IsNullOrWhiteSpace(userSettings?.OpenAiKey) ? userSettings!.OpenAiKey
            : null;
        var model = req.ModelId ?? userSettings?.ModelId;

        IAIService? serviceOverride = null;
        if (!string.IsNullOrWhiteSpace(userSettings?.AzureOpenAiEndpoint) &&
            !string.IsNullOrWhiteSpace(userSettings?.AzureOpenAiKey))
        {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureOpenAI:Endpoint"] = userSettings.AzureOpenAiEndpoint,
                    ["AzureOpenAI:ApiKey"] = userSettings.AzureOpenAiKey,
                    ["AzureOpenAI:DeploymentName"] = userSettings.AzureOpenAiDeployment ?? model ?? "gpt-4o",
                }).Build();
            serviceOverride = new AzureOpenAIService(cfg, NullLogger<AzureOpenAIService>.Instance);
        }

        var fullContent = new System.Text.StringBuilder();

        try
        {
            await foreach (var update in _contentService.GenerateAsync(
                req.Topic,
                req.ContentType ?? "blog post",
                req.Tone ?? "professional",
                req.TargetLength ?? 800,
                req.IncludeWebSearch ?? true,
                apiKey, model, serviceOverride))
            {
                await WriteSse(new { type = update.Phase, content = update.Content, isFinal = update.IsFinal });

                if (update.Phase == "writing" && !update.IsFinal)
                    fullContent.Append(update.Content);
                else if (update.IsFinal)
                    fullContent.Clear().Append(update.Content);
            }

            // Save as execution
            await _context.PromptExecutions.AddAsync(new PromptExecution
            {
                UserId = userId,
                PromptName = $"Content: {req.Topic[..Math.Min(req.Topic.Length, 50)]}",
                SystemPrompt = $"Content generation: {req.ContentType}, {req.Tone}",
                Model = model ?? "gpt-4o",
                MessagesJson = JsonSerializer.Serialize(new { topic = req.Topic, contentType = req.ContentType }),
                TotalTokensUsed = 0
            });
            await _context.SaveChangesAsync();

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Content generation failed");
            await WriteSse(new { type = "error", message = "Content generation failed." });
        }
    }

    // Ready-to-post social copy for multiple platforms from a topic or source content.
    [HttpPost("social-pack")]
    public async Task<IActionResult> SocialPack([FromBody] SocialPackRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Source))
            return BadRequest(new { message = "Enter a topic or paste some content to post about." });

        var userId = GetUserId();
        var userSettings = await _context.UserAiSettings.FirstOrDefaultAsync(s => s.UserId == userId);
        var apiKey = !string.IsNullOrWhiteSpace(req.UserApiKey) ? req.UserApiKey
            : !string.IsNullOrWhiteSpace(userSettings?.OpenAiKey) ? userSettings!.OpenAiKey
            : null;
        var model = req.ModelId ?? userSettings?.ModelId;

        try
        {
            var posts = await _contentService.GenerateSocialPackAsync(
                req.Source.Trim(),
                string.IsNullOrWhiteSpace(req.Tone) ? "professional" : req.Tone!,
                req.Platforms,
                apiKey, model,
                includeWebSearch: req.IncludeWebSearch ?? true);
            if (posts.Count == 0)
                return StatusCode(500, new { message = "Could not generate posts — please try again." });
            var topic = req.Source.Trim().Split('\n')[0].Trim();
            if (topic.Length > 80) topic = topic[..80] + "…";
            return Ok(new { posts });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Social pack generation failed");
            return StatusCode(500, new { message = "Could not generate posts — please try again." });
        }
    }

    private async Task WriteSse(object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await Response.WriteAsync($"data: {json}\n\n");
        await Response.Body.FlushAsync();
    }
}

public class ContentGenerateRequest
{
    public required string Topic { get; set; }
    public string? ContentType { get; set; }
    public string? Tone { get; set; }
    public int? TargetLength { get; set; }
    public bool? IncludeWebSearch { get; set; }
    public string? UserApiKey { get; set; }
    public string? ModelId { get; set; }
}

public class SocialPackRequest
{
    public string? Source { get; set; }
    public string? Tone { get; set; }
    public List<string>? Platforms { get; set; }
    public bool? IncludeWebSearch { get; set; }
    public string? UserApiKey { get; set; }
    public string? ModelId { get; set; }
}
