using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using System.Security.Claims;

namespace AgentHub.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("ai-generation")]
public class PersonasController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly IAIService _aiService;
    private readonly ILogger<PersonasController> _logger;

    public PersonasController(
        PromptDbContext context,
        IAIService aiService,
        ILogger<PersonasController> logger)
    {
        _context = context;
        _aiService = aiService;
        _logger = logger;
    }

    private string GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userIdClaim ?? string.Empty;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Persona>>> GetPersonas()
    {
        var userId = GetCurrentUserId();
        return await _context.Personas.Where(p => p.UserId == userId).OrderByDescending(p => p.UpdatedAt).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Persona>> GetPersona(int id)
    {
        var userId = GetCurrentUserId();
        var persona = await _context.Personas.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (persona == null) return NotFound();
        return persona;
    }

    [HttpPost]
    public async Task<ActionResult<Persona>> CreatePersona(Persona persona)
    {
        var userId = GetCurrentUserId();
        persona.UserId = userId;
        persona.CreatedAt = DateTime.UtcNow;
        persona.UpdatedAt = DateTime.UtcNow;
        _context.Personas.Add(persona);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetPersona), new { id = persona.Id }, persona);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdatePersona(int id, Persona persona)
    {
        if (id != persona.Id) return BadRequest();
        var userId = GetCurrentUserId();
        var existing = await _context.Personas.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (existing == null) return NotFound();

        existing.Name = persona.Name;
        existing.SystemPrompt = persona.SystemPrompt;
        existing.Description = persona.Description;
        existing.Tone = persona.Tone;
        existing.Expertise = persona.Expertise;
        existing.Constraints = persona.Constraints;
        existing.Icon = persona.Icon;
        existing.Category = persona.Category;
        existing.IsPublic = persona.IsPublic;
        
        // Enhanced fields
        existing.ExampleResponses = persona.ExampleResponses;
        existing.PersonalityTraits = persona.PersonalityTraits;
        existing.CommunicationStyle = persona.CommunicationStyle;
        existing.IndustryFocus = persona.IndustryFocus;
        existing.TargetAudience = persona.TargetAudience;
        
        existing.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePersona(int id)
    {
        var userId = GetCurrentUserId();
        var persona = await _context.Personas.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (persona == null) return NotFound();
        _context.Personas.Remove(persona);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/share")]
    public async Task<IActionResult> SharePersona(int id)
    {
        var userId = GetCurrentUserId();
        var persona = await _context.Personas.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (persona == null) return NotFound();
        persona.IsPublic = true;
        persona.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return Ok(new { message = "Persona shared to public library", isPublic = true });
    }

    [HttpPost("{id}/unshare")]
    public async Task<IActionResult> UnsharePersona(int id)
    {
        var userId = GetCurrentUserId();
        var persona = await _context.Personas.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (persona == null) return NotFound();
        persona.IsPublic = false;
        persona.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return Ok(new { message = "Persona removed from public library", isPublic = false });
    }

    // Public library endpoints
    [AllowAnonymous]
    [HttpGet("public")]
    public async Task<ActionResult<IEnumerable<PublicPersonaDto>>> GetPublicPersonas(
        [FromQuery] string? category = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = "recent")
    {
        var query = _context.Personas
            .Include(p => p.User)
            .Where(p => p.IsPublic);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(p => p.Category != null && p.Category.ToLower() == category.ToLower());

        if (!string.IsNullOrEmpty(search))
        {
            var searchLower = search.ToLower();
            query = query.Where(p =>
                p.Name.ToLower().Contains(searchLower) ||
                (p.Description != null && p.Description.ToLower().Contains(searchLower)) ||
                (p.Expertise != null && p.Expertise.ToLower().Contains(searchLower)));
        }

        query = sortBy?.ToLower() switch
        {
            "popular" => query.OrderByDescending(p => p.UsageCount),
            "likes" => query.OrderByDescending(p => p.LikeCount),
            _ => query.OrderByDescending(p => p.CreatedAt)
        };

        var personas = await query.Take(50).ToListAsync();

        var publicPersonas = personas.Select(p => new PublicPersonaDto
        {
            Id = p.Id,
            Name = p.Name,
            SystemPrompt = p.SystemPrompt,
            Description = p.Description,
            Tone = p.Tone,
            Expertise = p.Expertise,
            Constraints = p.Constraints,
            Icon = p.Icon,
            Category = p.Category,
            UsageCount = p.UsageCount,
            LikeCount = p.LikeCount,
            CreatedAt = p.CreatedAt,
            AuthorName = p.User?.Name ?? "Anonymous",
            AuthorPicture = p.User?.ProfilePicture
        }).ToList();

        return Ok(publicPersonas);
    }

    [AllowAnonymous]
    [HttpGet("public/categories")]
    public async Task<ActionResult<IEnumerable<string>>> GetPublicPersonaCategories()
    {
        var categories = await _context.Personas
            .Where(p => p.IsPublic && !string.IsNullOrEmpty(p.Category))
            .Select(p => p.Category!)
            .Distinct()
            .ToListAsync();
        return Ok(categories);
    }

    [HttpPost("public/{id}/use")]
    public async Task<IActionResult> UsePublicPersona(int id)
    {
        var persona = await _context.Personas.FirstOrDefaultAsync(p => p.Id == id && p.IsPublic);
        if (persona == null) return NotFound();
        persona.UsageCount++;
        await _context.SaveChangesAsync();
        return Ok(new { usageCount = persona.UsageCount });
    }

    [HttpPost("public/{id}/like")]
    public async Task<IActionResult> LikePublicPersona(int id)
    {
        var persona = await _context.Personas.FirstOrDefaultAsync(p => p.Id == id && p.IsPublic);
        if (persona == null) return NotFound();
        persona.LikeCount++;
        await _context.SaveChangesAsync();
        return Ok(new { likeCount = persona.LikeCount });
    }

    // AI-powered persona generation
    [HttpPost("generate")]
    public async Task<ActionResult> GeneratePersona([FromBody] GeneratePersonaRequest request)
    {
        try
        {
            var moderation = await _aiService.ModerateContentAsync(request.Description);
            if (moderation.IsFlagged)
            {
                return BadRequest(new { error = "content_violation", message = "Sorry, we can't help with that. Your request contains content that violates our usage policy." });
            }

            var result = await _aiService.GeneratePersonaAsync(request.Description);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating persona");
            return StatusCode(500, new { error = "Failed to generate persona", message = ex.Message });
        }
    }

    // Persona Templates (Pre-built personas)
    [AllowAnonymous]
    [HttpGet("templates")]
    public async Task<ActionResult<IEnumerable<Persona>>> GetPersonaTemplates([FromQuery] string? type = null)
    {
        var query = _context.Personas.Where(p => p.IsTemplate);
        
        if (!string.IsNullOrEmpty(type))
            query = query.Where(p => p.TemplateType == type);
            
        var templates = await query.OrderBy(p => p.Name).ToListAsync();
        return Ok(templates);
    }

    [HttpPost("templates/{id}/clone")]
    public async Task<ActionResult<Persona>> CloneTemplate(int id)
    {
        var template = await _context.Personas.FirstOrDefaultAsync(p => p.Id == id && p.IsTemplate);
        if (template == null) return NotFound();

        var userId = GetCurrentUserId();
        var cloned = new Persona
        {
            Name = template.Name + " (Copy)",
            SystemPrompt = template.SystemPrompt,
            Description = template.Description,
            Tone = template.Tone,
            Expertise = template.Expertise,
            Constraints = template.Constraints,
            Icon = template.Icon,
            Category = template.Category,
            ExampleResponses = template.ExampleResponses,
            PersonalityTraits = template.PersonalityTraits,
            CommunicationStyle = template.CommunicationStyle,
            IndustryFocus = template.IndustryFocus,
            TargetAudience = template.TargetAudience,
            ParentPersonaId = template.Id,
            Version = 1,
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.Personas.Add(cloned);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetPersona), new { id = cloned.Id }, cloned);
    }

    // Persona Analytics
    [HttpGet("analytics")]
    public async Task<ActionResult> GetPersonaAnalytics()
    {
        var userId = GetCurrentUserId();
        var personas = await _context.Personas.Where(p => p.UserId == userId).ToListAsync();
        
        var analytics = new
        {
            totalPersonas = personas.Count,
            publicPersonas = personas.Count(p => p.IsPublic),
            totalUsage = personas.Sum(p => p.UsageCount),
            totalLikes = personas.Sum(p => p.LikeCount),
            mostUsed = personas.OrderByDescending(p => p.UsageCount).Take(5).Select(p => new { p.Id, p.Name, p.Icon, p.UsageCount }),
            recentlyUpdated = personas.OrderByDescending(p => p.UpdatedAt).Take(5).Select(p => new { p.Id, p.Name, p.Icon, p.UpdatedAt }),
            byCategory = personas.Where(p => !string.IsNullOrEmpty(p.Category))
                .GroupBy(p => p.Category)
                .Select(g => new { category = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
        };
        
        return Ok(analytics);
    }

    [HttpPost("{id}/increment-usage")]
    public async Task<IActionResult> IncrementUsage(int id)
    {
        var userId = GetCurrentUserId();
        var persona = await _context.Personas.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (persona == null) return NotFound();
        
        persona.UsageCount++;
        await _context.SaveChangesAsync();
        return Ok(new { usageCount = persona.UsageCount });
    }
}

// DTOs
public class PublicPersonaDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Tone { get; set; }
    public string? Expertise { get; set; }
    public string? Constraints { get; set; }
    public string? Icon { get; set; }
    public string? Category { get; set; }
    public int UsageCount { get; set; }
    public int LikeCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string? AuthorPicture { get; set; }
}

public record GeneratePersonaRequest(string Description);
