using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KitBuilder.Api.Data;
using KitBuilder.Api.Models;
using System.Security.Claims;

namespace KitBuilder.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SkillsController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly ILogger<SkillsController> _logger;

    public SkillsController(PromptDbContext context, ILogger<SkillsController> logger)
    {
        _context = context;
        _logger = logger;
    }

    private string GetCurrentUserId() =>
        User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    // ── DTOs ─────────────────────────────────────────────────────────────────

    public record PublicSkillDto(
        string Id,
        string Name,
        string Instruction,
        string? Category,
        string? Description,
        string[] Tags,
        int UsageCount,
        int LikeCount,
        DateTime CreatedAt,
        string AuthorName,
        string? AuthorPicture,
        bool IsOwner
    );

    public record CreateSkillRequest(
        string Name,
        string Instruction,
        string? Category,
        string? Description,
        string[]? Tags
    );

    // ── Public endpoints ──────────────────────────────────────────────────────

    [AllowAnonymous]
    [HttpGet("public")]
    public async Task<ActionResult<IEnumerable<PublicSkillDto>>> GetPublicSkills(
        [FromQuery] string? category = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = "recent")
    {
        var currentUserId = User.Identity?.IsAuthenticated == true ? GetCurrentUserId() : null;

        var query = _context.Skills
            .Include(s => s.User)
            .Where(s => s.IsPublic && s.IsActive)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(s => s.Category != null && s.Category.ToLower() == category.ToLower());

        if (!string.IsNullOrWhiteSpace(search))
        {
            var q = search.ToLower();
            query = query.Where(s =>
                s.Name.ToLower().Contains(q) ||
                (s.Description != null && s.Description.ToLower().Contains(q)) ||
                (s.Tags != null && s.Tags.ToLower().Contains(q)));
        }

        var skills = sortBy?.ToLower() switch
        {
            "popular" => await query.OrderByDescending(s => s.UsageCount).Take(100).ToListAsync(),
            "likes"   => await query.OrderByDescending(s => s.LikeCount).Take(100).ToListAsync(),
            _         => await query.OrderByDescending(s => s.CreatedAt).Take(100).ToListAsync()
        };

        var dtos = skills.Select(s => new PublicSkillDto(
            s.Id, s.Name, s.Instruction, s.Category, s.Description,
            s.Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            s.UsageCount, s.LikeCount, s.CreatedAt,
            s.User?.Name ?? "Community",
            s.User?.ProfilePicture,
            s.UserId == currentUserId
        ));

        return Ok(dtos);
    }

    [AllowAnonymous]
    [HttpPost("public/{id}/use")]
    public async Task<IActionResult> TrackUse(string id)
    {
        var skill = await _context.Skills.FirstOrDefaultAsync(s => s.Id == id && s.IsPublic && s.IsActive);
        if (skill is null) return NotFound();
        skill.UsageCount++;
        await _context.SaveChangesAsync();
        return Ok();
    }

    [Authorize]
    [HttpPost("public/{id}/like")]
    public async Task<IActionResult> Like(string id)
    {
        var skill = await _context.Skills.FirstOrDefaultAsync(s => s.Id == id && s.IsPublic && s.IsActive);
        if (skill is null) return NotFound();
        skill.LikeCount++;
        await _context.SaveChangesAsync();
        return Ok(new { likeCount = skill.LikeCount });
    }

    // ── Authenticated endpoints ───────────────────────────────────────────────

    [Authorize]
    [HttpGet]
    public async Task<ActionResult<IEnumerable<PublicSkillDto>>> GetMySkills()
    {
        var userId = GetCurrentUserId();
        var skills = await _context.Skills
            .Include(s => s.User)
            .Where(s => s.UserId == userId && s.IsActive)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        var dtos = skills.Select(s => new PublicSkillDto(
            s.Id, s.Name, s.Instruction, s.Category, s.Description,
            s.Tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            s.UsageCount, s.LikeCount, s.CreatedAt,
            s.User?.Name ?? "Me",
            s.User?.ProfilePicture,
            true
        ));

        return Ok(dtos);
    }

    [Authorize]
    [HttpPost]
    public async Task<ActionResult<PublicSkillDto>> CreateSkill([FromBody] CreateSkillRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Instruction))
            return BadRequest("Name and Instruction are required.");

        var userId = GetCurrentUserId();
        var user = await _context.Users.FindAsync(userId);
        if (user is null) return Unauthorized();

        var skill = new Skill
        {
            Name = req.Name.Trim(),
            Instruction = req.Instruction.Trim(),
            Category = req.Category?.Trim(),
            Description = req.Description?.Trim(),
            Tags = req.Tags is { Length: > 0 } ? string.Join(", ", req.Tags) : null,
            IsPublic = true,
            UserId = userId
        };

        _context.Skills.Add(skill);
        await _context.SaveChangesAsync();

        var dto = new PublicSkillDto(
            skill.Id, skill.Name, skill.Instruction, skill.Category, skill.Description,
            req.Tags ?? [],
            0, 0, skill.CreatedAt, user.Name, user.ProfilePicture, true
        );

        return CreatedAtAction(nameof(GetMySkills), dto);
    }

    [Authorize]
    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteSkill(string id)
    {
        var userId = GetCurrentUserId();
        var skill = await _context.Skills.FirstOrDefaultAsync(s => s.Id == id && s.UserId == userId && s.IsActive);
        if (skill is null) return NotFound();

        skill.IsActive = false;
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
