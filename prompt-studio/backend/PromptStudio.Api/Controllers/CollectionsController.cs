using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Models;
using System.Security.Claims;

namespace PromptStudio.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class CollectionsController : ControllerBase
{
    private readonly PromptDbContext _context;

    public CollectionsController(PromptDbContext context) => _context = context;

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<CollectionSummaryDto>>> GetCollections()
    {
        var userId = GetUserId();
        var collections = await _context.PromptCollections
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new CollectionSummaryDto
            {
                Id = c.Id,
                Name = c.Name,
                Description = c.Description,
                Emoji = c.Emoji,
                IsPublic = c.IsPublic,
                ItemCount = c.Items.Count,
                CreatedAt = c.CreatedAt,
                UpdatedAt = c.UpdatedAt
            })
            .ToListAsync();

        return Ok(collections);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<CollectionDetailDto>> GetCollection(string id)
    {
        var userId = GetUserId();
        var collection = await _context.PromptCollections
            .Include(c => c.Items)
                .ThenInclude(i => i.Prompt)
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);

        if (collection == null) return NotFound();

        return Ok(new CollectionDetailDto
        {
            Id = collection.Id,
            Name = collection.Name,
            Description = collection.Description,
            Emoji = collection.Emoji,
            IsPublic = collection.IsPublic,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt,
            Prompts = collection.Items
                .Where(i => i.Prompt != null)
                .OrderByDescending(i => i.AddedAt)
                .Select(i => new CollectionPromptDto
                {
                    Id = i.Prompt!.Id,
                    Name = i.Prompt.Name,
                    Content = i.Prompt.Content,
                    Category = i.Prompt.Category,
                    Description = i.Prompt.Description,
                    Difficulty = i.Prompt.Difficulty,
                    AddedAt = i.AddedAt
                }).ToList()
        });
    }

    [HttpPost]
    public async Task<ActionResult<CollectionSummaryDto>> CreateCollection([FromBody] CreateCollectionRequest req)
    {
        var userId = GetUserId();
        var collection = new PromptCollection
        {
            Name = req.Name,
            Description = req.Description,
            Emoji = req.Emoji ?? "📁",
            IsPublic = req.IsPublic,
            UserId = userId
        };

        _context.PromptCollections.Add(collection);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetCollection), new { id = collection.Id }, new CollectionSummaryDto
        {
            Id = collection.Id,
            Name = collection.Name,
            Description = collection.Description,
            Emoji = collection.Emoji,
            IsPublic = collection.IsPublic,
            ItemCount = 0,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt
        });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateCollection(string id, [FromBody] CreateCollectionRequest req)
    {
        var userId = GetUserId();
        var collection = await _context.PromptCollections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (collection == null) return NotFound();

        collection.Name = req.Name;
        collection.Description = req.Description;
        collection.Emoji = req.Emoji ?? collection.Emoji;
        collection.IsPublic = req.IsPublic;
        collection.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCollection(string id)
    {
        var userId = GetUserId();
        var collection = await _context.PromptCollections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (collection == null) return NotFound();

        _context.PromptCollections.Remove(collection);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/prompts/{promptId}")]
    public async Task<IActionResult> AddPrompt(string id, string promptId)
    {
        var userId = GetUserId();
        var collection = await _context.PromptCollections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (collection == null) return NotFound();

        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == promptId && p.UserId == userId);
        if (prompt == null) return NotFound(new { error = "Prompt not found" });

        var exists = await _context.CollectionItems.AnyAsync(i => i.CollectionId == id && i.PromptId == promptId);
        if (exists) return Conflict(new { error = "Prompt already in collection" });

        _context.CollectionItems.Add(new CollectionItem { CollectionId = id, PromptId = promptId });
        collection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpDelete("{id}/prompts/{promptId}")]
    public async Task<IActionResult> RemovePrompt(string id, string promptId)
    {
        var userId = GetUserId();
        var collection = await _context.PromptCollections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (collection == null) return NotFound();

        var item = await _context.CollectionItems.FirstOrDefaultAsync(i => i.CollectionId == id && i.PromptId == promptId);
        if (item == null) return NotFound();

        _context.CollectionItems.Remove(item);
        collection.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return NoContent();
    }
}

public class CollectionSummaryDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Emoji { get; set; }
    public bool IsPublic { get; set; }
    public int ItemCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CollectionDetailDto : CollectionSummaryDto
{
    public List<CollectionPromptDto> Prompts { get; set; } = [];
}

public class CollectionPromptDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Description { get; set; }
    public string? Difficulty { get; set; }
    public DateTime AddedAt { get; set; }
}

public record CreateCollectionRequest(string Name, string? Description, string? Emoji, bool IsPublic);
