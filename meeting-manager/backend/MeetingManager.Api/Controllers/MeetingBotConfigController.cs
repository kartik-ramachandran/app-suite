using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;
using MeetingManager.Api.Models;
using MeetingManager.Api.Services.MeetingCapture;

namespace MeetingManager.Api.Controllers;

/// <summary>
/// Where a person or a team says how the notetaker should behave.
///
/// Three scopes, resolved most-specific-first at meeting time: your own
/// settings, your team's, then the site default. Anyone can manage their own.
/// Only an organisation admin or owner can manage their team's, and only a site
/// admin the site default -- a member being able to switch on automatic
/// recording for everyone they work with would not be theirs to decide.
/// </summary>
[ApiController]
[Route("api/meeting-bot-configs")]
[Authorize]
public class MeetingBotConfigController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly ILogger<MeetingBotConfigController> _logger;

    public MeetingBotConfigController(
        PromptDbContext db,
        ILogger<MeetingBotConfigController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    private bool IsSiteAdmin => User.IsInRole("Admin") || User.HasClaim("role", "admin");

    /// <summary>The organisations this user may configure on behalf of.</summary>
    private async Task<List<string>> AdminOfOrganizations(CancellationToken ct = default)
    {
        try
        {
            return await _db.OrganizationMembers
                .Where(m => m.UserId == UserId
                    && m.Status == "active"
                    && (m.Role == OrgRoles.Owner || m.Role == OrgRoles.Admin))
                .Select(m => m.OrganizationId)
                .ToListAsync(ct);
        }
        catch { return []; }
    }

    /// <summary>Everything this user is allowed to see, without any keys in it.</summary>
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var orgIds = await AdminOfOrganizations();

        List<MeetingBotConfig> configs;
        try
        {
            configs = await _db.MeetingBotConfigs
                .Where(c =>
                    (c.Scope == MeetingBotScopes.User && c.OwnerId == UserId) ||
                    (c.Scope == MeetingBotScopes.Organization && c.OwnerId != null && orgIds.Contains(c.OwnerId)) ||
                    c.Scope == MeetingBotScopes.Platform)
                .OrderBy(c => c.Scope)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Meeting bot configs unavailable");
            return Ok(Array.Empty<object>());
        }

        return Ok(configs.Select(c => Shape(c, canEdit: CanEdit(c, orgIds))));
    }

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] SaveRequest request)
    {
        if (request is null) return BadRequest(new { error = "Nothing to save." });
        if (!MeetingBotScopes.IsValid(request.Scope)) return BadRequest(new { error = "Unknown scope." });


        var orgIds = await AdminOfOrganizations();
        var scope = request.Scope!.ToLowerInvariant();

        // Who the config belongs to is decided here, never taken from the client.
        string? ownerId = scope switch
        {
            MeetingBotScopes.User => UserId,
            MeetingBotScopes.Organization => request.OwnerId,
            _ => null,
        };

        if (scope == MeetingBotScopes.Organization)
        {
            if (string.IsNullOrWhiteSpace(ownerId) || !orgIds.Contains(ownerId))
                return Forbid();
        }
        else if (scope == MeetingBotScopes.Platform && !IsSiteAdmin)
        {
            return Forbid();
        }

        var platform = string.IsNullOrWhiteSpace(request.MeetingPlatform) ? "*" : request.MeetingPlatform!.ToLowerInvariant();

        var existing = await _db.MeetingBotConfigs.FirstOrDefaultAsync(c =>
            c.Scope == scope && c.OwnerId == ownerId && c.MeetingPlatform == platform);

        var config = existing ?? new MeetingBotConfig
        {
            Scope = scope,
            OwnerId = ownerId,
            MeetingPlatform = platform,
            CreatedBy = UserId,
        };

        config.BotName = string.IsNullOrWhiteSpace(request.BotName) ? "GenPrompt Notetaker" : request.BotName!.Trim();
        config.AutoRecord = request.AutoRecord ?? false;
        config.MaxMinutes = Math.Clamp(request.MaxMinutes ?? 240, 5, 720);
        config.Enabled = request.Enabled ?? true;
        if (request.Tools is { Length: > 0 })
            config.AutoToolsJson = System.Text.Json.JsonSerializer.Serialize(request.Tools);
        config.UpdatedAt = DateTime.UtcNow;

        if (existing is null) _db.Add(config);
        await _db.SaveChangesAsync();

        return Ok(Shape(config, canEdit: true));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var config = await _db.MeetingBotConfigs.FirstOrDefaultAsync(c => c.Id == id);
        if (config is null) return NotFound();

        var orgIds = await AdminOfOrganizations();
        if (!CanEdit(config, orgIds)) return Forbid();

        _db.Remove(config);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private bool CanEdit(MeetingBotConfig c, List<string> adminOrgIds) => c.Scope switch
    {
        MeetingBotScopes.User => c.OwnerId == UserId,
        MeetingBotScopes.Organization => c.OwnerId is not null && adminOrgIds.Contains(c.OwnerId),
        MeetingBotScopes.Platform => IsSiteAdmin,
        _ => false,
    };

    private static object Shape(MeetingBotConfig c, bool canEdit) => new
    {
        id = c.Id,
        scope = c.Scope,
        ownerId = c.OwnerId,
        meetingPlatform = c.MeetingPlatform,
        botName = c.BotName,
        autoRecord = c.AutoRecord,
        maxMinutes = c.MaxMinutes,
        tools = SafeTools(c.AutoToolsJson),
        enabled = c.Enabled,
        canEdit,
        updatedAt = c.UpdatedAt,
    };

    private static string[] SafeTools(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch { return []; }
    }

    public class SaveRequest
    {
        public string? Scope { get; set; }
        public string? OwnerId { get; set; }
        public string? MeetingPlatform { get; set; }
        public string? BotName { get; set; }
        public bool? AutoRecord { get; set; }
        public int? MaxMinutes { get; set; }
        public string[]? Tools { get; set; }
        public bool? Enabled { get; set; }
    }
}
