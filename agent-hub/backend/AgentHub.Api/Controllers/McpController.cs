using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using System.Security.Claims;

namespace AgentHub.Api.Controllers;

/// <summary>
/// MCP connections: browse the synced catalogue of public MCP servers, connect
/// to one, and choose which of its tools are enabled.
///
/// Everything here is scoped to the calling user. Stored credentials are never
/// returned — not even masked — only whether one is set.
/// </summary>
[Authorize]
[ApiController]
[Route("api/mcp")]
public class McpController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly McpClientService _client;
    private readonly McpRegistryService _registry;
    private readonly SecretProtector _secrets;

    public McpController(PromptDbContext db, McpClientService client, McpRegistryService registry, SecretProtector secrets)
    {
        _db = db;
        _client = client;
        _registry = registry;
        _secrets = secrets;
    }

    private string UserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    // ── Catalogue ────────────────────────────────────────────────────────────

    /// <summary>Browse/search the catalogue. Connectable (remote) servers first.</summary>
    [HttpGet("catalog")]
    public async Task<ActionResult> Catalog(
        [FromQuery] string? q,
        [FromQuery] string? category,
        [FromQuery] bool connectableOnly = true,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 60)
    {
        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);

        var query = _db.McpCatalogServers.AsNoTracking().AsQueryable();
        if (connectableOnly) query = query.Where(s => s.IsConnectable);
        if (!string.IsNullOrWhiteSpace(category) && category != "All") query = query.Where(s => s.Category == category);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLower();
            query = query.Where(s =>
                s.Title.ToLower().Contains(term) ||
                s.Name.ToLower().Contains(term) ||
                s.Description.ToLower().Contains(term));
        }

        var total = await query.CountAsync();
        var servers = await query
            .OrderByDescending(s => s.IsConnectable)
            .ThenBy(s => s.Title)
            .Skip(skip).Take(take)
            .Select(s => new
            {
                s.Name, s.Title, s.Description, s.Category, s.Publisher,
                s.Transport, s.IsConnectable, s.WebsiteUrl, s.Version,
            })
            .ToListAsync();

        // Mark what the user already has, so the picker can show "Connected".
        var connected = await _db.McpConnections
            .Where(c => c.UserId == UserId)
            .Select(c => c.ServerName)
            .ToListAsync();

        var categories = await _db.McpCatalogServers.AsNoTracking()
            .Where(s => !connectableOnly || s.IsConnectable)
            .GroupBy(s => s.Category)
            .Select(g => new { category = g.Key, count = g.Count() })
            .OrderByDescending(x => x.count)
            .ToListAsync();

        return Ok(new { total, skip, take, servers, categories, connected });
    }

    /// <summary>Admin-triggered catalogue refresh (it also runs daily on its own).</summary>
    [HttpPost("catalog/sync")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> SyncCatalog(CancellationToken ct)
    {
        var result = await _registry.SyncAsync(ct);
        return Ok(result);
    }

    // ── Connections ──────────────────────────────────────────────────────────

    public class CreateConnectionRequest
    {
        public string ServerName { get; set; } = string.Empty;
        /// Optional override; defaults to the catalogue entry's endpoint.
        public string? RemoteUrl { get; set; }
        public string? Label { get; set; }
        /// none | bearer | header
        public string AuthType { get; set; } = "none";
        public string? Secret { get; set; }
        public string? HeaderName { get; set; }
    }

    [HttpGet("connections")]
    public async Task<ActionResult> Connections()
    {
        var connections = await _db.McpConnections
            .Where(c => c.UserId == UserId)
            .OrderBy(c => c.Label)
            .ToListAsync();

        var ids = connections.Select(c => c.Id).ToList();
        var tools = await _db.McpConnectionTools
            .Where(t => ids.Contains(t.ConnectionId))
            .ToListAsync();

        return Ok(connections.Select(c => new
        {
            c.Id, c.ServerName, c.Label, c.RemoteUrl, c.AuthType, c.Status,
            c.LastError, c.ToolCount, c.LastCheckedAt, c.CreatedAt,
            hasSecret = !string.IsNullOrEmpty(c.SecretEncrypted),
            tools = tools.Where(t => t.ConnectionId == c.Id)
                .OrderBy(t => t.ToolName)
                .Select(t => new { t.Id, t.ToolName, t.Description, t.Enabled, t.ApprovalMode, t.IsWrite }),
        }));
    }

    [HttpPost("connections")]
    public async Task<ActionResult> Create([FromBody] CreateConnectionRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ServerName))
            return BadRequest(new { message = "Pick a server to connect." });

        var catalogEntry = await _db.McpCatalogServers.FirstOrDefaultAsync(s => s.Name == request.ServerName, ct);
        var remoteUrl = !string.IsNullOrWhiteSpace(request.RemoteUrl) ? request.RemoteUrl!.Trim() : catalogEntry?.RemoteUrl;

        if (string.IsNullOrWhiteSpace(remoteUrl))
            return BadRequest(new { message = "That server has no remote endpoint — it can only run as a local package, which isn't supported yet." });
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps)
            return BadRequest(new { message = "The endpoint must be an https:// URL." });

        if (await _db.McpConnections.AnyAsync(c => c.UserId == UserId && c.ServerName == request.ServerName, ct))
            return Conflict(new { message = "You're already connected to this server." });

        var connection = new McpConnection
        {
            UserId = UserId,
            ServerName = request.ServerName,
            Label = string.IsNullOrWhiteSpace(request.Label) ? (catalogEntry?.Title ?? request.ServerName) : request.Label!.Trim(),
            RemoteUrl = remoteUrl!,
            AuthType = request.AuthType is "bearer" or "header" ? request.AuthType : "none",
            HeaderName = request.HeaderName,
            SecretEncrypted = string.IsNullOrWhiteSpace(request.Secret) ? null : _secrets.Protect(request.Secret!),
        };

        _db.McpConnections.Add(connection);
        await _db.SaveChangesAsync(ct);

        // Verify immediately: a connection that can't list tools is not a connection.
        var (ok, error, toolCount) = await _client.RefreshAsync(connection, ct);
        return Ok(new
        {
            connection.Id, connection.ServerName, connection.Label, connection.Status,
            toolCount, ok, error,
        });
    }

    [HttpPost("connections/{id}/refresh")]
    public async Task<ActionResult> Refresh(string id, CancellationToken ct)
    {
        var connection = await _db.McpConnections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == UserId, ct);
        if (connection == null) return NotFound();
        var (ok, error, toolCount) = await _client.RefreshAsync(connection, ct);
        return Ok(new { ok, error, toolCount, connection.Status });
    }

    public class UpdateConnectionRequest
    {
        public string? Label { get; set; }
        public string? Secret { get; set; }
        public bool? Disabled { get; set; }
    }

    [HttpPut("connections/{id}")]
    public async Task<ActionResult> Update(string id, [FromBody] UpdateConnectionRequest request, CancellationToken ct)
    {
        var connection = await _db.McpConnections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == UserId, ct);
        if (connection == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(request.Label)) connection.Label = request.Label!.Trim();
        if (!string.IsNullOrWhiteSpace(request.Secret)) connection.SecretEncrypted = _secrets.Protect(request.Secret!);
        if (request.Disabled.HasValue)
            connection.Status = request.Disabled.Value ? "disabled" : "connected";
        connection.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new { connection.Id, connection.Label, connection.Status });
    }

    [HttpDelete("connections/{id}")]
    public async Task<ActionResult> Delete(string id, CancellationToken ct)
    {
        var connection = await _db.McpConnections.FirstOrDefaultAsync(c => c.Id == id && c.UserId == UserId, ct);
        if (connection == null) return NotFound();

        var tools = await _db.McpConnectionTools.Where(t => t.ConnectionId == id).ToListAsync(ct);
        _db.McpConnectionTools.RemoveRange(tools);
        _db.McpConnections.Remove(connection);
        await _db.SaveChangesAsync(ct);
        return Ok(new { deleted = true });
    }

    // ── Tools ────────────────────────────────────────────────────────────────

    public class UpdateToolRequest
    {
        public bool? Enabled { get; set; }
        /// auto | ask
        public string? ApprovalMode { get; set; }
    }

    [HttpPut("tools/{id}")]
    public async Task<ActionResult> UpdateTool(int id, [FromBody] UpdateToolRequest request, CancellationToken ct)
    {
        var tool = await _db.McpConnectionTools.FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId, ct);
        if (tool == null) return NotFound();

        if (request.Enabled.HasValue) tool.Enabled = request.Enabled.Value;
        if (request.ApprovalMode is "auto" or "ask") tool.ApprovalMode = request.ApprovalMode;
        await _db.SaveChangesAsync(ct);
        return Ok(new { tool.Id, tool.Enabled, tool.ApprovalMode });
    }

    /// <summary>Every enabled tool across a user's connections — the tool set an agent run can draw on.</summary>
    [HttpGet("tools")]
    public async Task<ActionResult> Tools()
    {
        var connections = await _db.McpConnections
            .Where(c => c.UserId == UserId && c.Status == "connected")
            .ToDictionaryAsync(c => c.Id, c => c);

        var tools = await _db.McpConnectionTools
            .Where(t => t.UserId == UserId && t.Enabled)
            .ToListAsync();

        return Ok(tools
            .Where(t => connections.ContainsKey(t.ConnectionId))
            .Select(t => new
            {
                t.Id,
                t.ToolName,
                t.Description,
                t.ApprovalMode,
                t.IsWrite,
                connectionId = t.ConnectionId,
                server = connections[t.ConnectionId].Label,
                inputSchema = t.InputSchemaJson,
            }));
    }
}
