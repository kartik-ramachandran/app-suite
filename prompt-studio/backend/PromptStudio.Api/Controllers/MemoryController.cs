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
[Route("api/[controller]")]
public class MemoryController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly IAIService _aiService;
    private readonly ILogger<MemoryController> _logger;

    public MemoryController(PromptDbContext context, IAIService aiService, ILogger<MemoryController> logger)
    {
        _context = context;
        _aiService = aiService;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    [HttpGet]
    public async Task<ActionResult> GetMemories()
    {
        var userId = GetUserId();
        var memories = await _context.UserMemories
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Key)
            .Select(m => new { m.Id, m.Key, m.Value, m.UpdatedAt })
            .ToListAsync();
        return Ok(memories);
    }

    [HttpPost]
    public async Task<ActionResult> SetMemory([FromBody] SetMemoryRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Key)) return BadRequest("Key is required");
        if (req.Value == null) return BadRequest("Value is required");

        var userId = GetUserId();
        var count = await _context.UserMemories.CountAsync(m => m.UserId == userId);
        var existing = await _context.UserMemories.FirstOrDefaultAsync(m => m.UserId == userId && m.Key == req.Key);

        if (existing != null)
        {
            existing.Value = req.Value;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            if (count >= 500)
                return BadRequest("Memory limit reached (500 entries).");
            _context.UserMemories.Add(new UserMemory
            {
                UserId = userId,
                Key = req.Key,
                Value = req.Value
            });
        }

        await _context.SaveChangesAsync();
        return Ok(new { key = req.Key, value = req.Value });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteMemory(int id)
    {
        var userId = GetUserId();
        var memory = await _context.UserMemories.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId);
        if (memory == null) return NotFound();
        _context.UserMemories.Remove(memory);
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpDelete]
    public async Task<ActionResult> ClearAllMemories()
    {
        var userId = GetUserId();
        var memories = await _context.UserMemories.Where(m => m.UserId == userId).ToListAsync();
        _context.UserMemories.RemoveRange(memories);
        await _context.SaveChangesAsync();
        return Ok(new { deleted = memories.Count });
    }

    // POST /api/memory/consolidate — extract memories from recent executions
    [HttpPost("consolidate")]
    public async Task<ActionResult> Consolidate()
    {
        var userId = GetUserId();

        var recentExecutions = await _context.PromptExecutions
            .Where(e => e.UserId == userId)
            .OrderByDescending(e => e.CreatedAt)
            .Take(20)
            .Select(e => new { e.PromptName, e.SystemPrompt })
            .ToListAsync();

        if (!recentExecutions.Any())
            return Ok(new { message = "No executions to consolidate.", saved = 0 });

        var summary = string.Join("\n", recentExecutions.Select(e => $"Prompt: {e.PromptName}"));

        var systemPrompt =
            "You are a memory extraction agent. Analyse the user's recent AI prompt activity and extract " +
            "durable facts about their preferences, projects, and goals. " +
            "Output a JSON array of objects: [{\"key\": \"dot.path.key\", \"value\": \"short fact\"}]. " +
            "Max 10 items. Keys use dot notation like user.preference.tone, user.project.name. " +
            "Output ONLY valid JSON.";

        try
        {
            var response = await _aiService.TestPromptAsync(new PromptTestRequest
            {
                PromptContent = systemPrompt,
                UserInput = $"Recent prompt activity:\n{summary}"
            });

            var json = response.Result.Trim();
            // Strip markdown fences if present
            if (json.StartsWith("```")) json = System.Text.RegularExpressions.Regex.Replace(json, @"```\w*\n?", "").Trim();

            var items = System.Text.Json.JsonSerializer.Deserialize<List<MemoryItem>>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (items == null) return Ok(new { message = "No memories extracted.", saved = 0 });

            var saved = 0;
            foreach (var item in items.Where(i => !string.IsNullOrWhiteSpace(i.Key) && !string.IsNullOrWhiteSpace(i.Value)))
            {
                var existing = await _context.UserMemories.FirstOrDefaultAsync(m => m.UserId == userId && m.Key == item.Key);
                if (existing != null) { existing.Value = item.Value!; existing.UpdatedAt = DateTime.UtcNow; }
                else _context.UserMemories.Add(new UserMemory { UserId = userId, Key = item.Key!, Value = item.Value! });
                saved++;
            }
            await _context.SaveChangesAsync();
            return Ok(new { message = $"Extracted and saved {saved} memories.", saved });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Memory consolidation failed");
            return StatusCode(500, new { message = "Consolidation failed." });
        }
    }

    private record MemoryItem(string? Key, string? Value);
}

public class SetMemoryRequest
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
