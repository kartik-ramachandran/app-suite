using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using EmmdStudio.Api.Data;
using EmmdStudio.Api.Models;
using EmmdStudio.Api.Services;
using System.Security.Claims;

namespace EmmdStudio.Api.Controllers;

/// <summary>
/// Enhanced MMD (EMMD) documents: create, edit, seal and verify. Available to
/// every signed-in user; when the author belongs to a business, documents are
/// attributed to their Organization so teammates share a workspace.
/// </summary>
[Authorize]
[ApiController]
[Route("api/[controller]")]
public class EmmdController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly OrganizationService _orgs;
    private readonly EmmdSealService _seal;

    public EmmdController(PromptDbContext context, OrganizationService orgs, EmmdSealService seal)
    {
        _context = context;
        _orgs = orgs;
        _seal = seal;
    }

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    // ── List ─────────────────────────────────────────────────────────────────
    [HttpGet("documents")]
    public async Task<ActionResult<IEnumerable<EmmdDocumentSummaryDto>>> List()
    {
        var userId = GetUserId();
        var membership = await _orgs.GetActiveMembershipAsync(userId);
        var orgId = membership?.OrganizationId;

        var docs = await _context.EmmdDocuments
            .Where(d => d.UserId == userId || (orgId != null && d.OrganizationId == orgId))
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync();

        return Ok(docs.Select(d => new EmmdDocumentSummaryDto
        {
            Id = d.Id,
            Title = d.Title,
            Theme = d.Theme,
            Sealed = !string.IsNullOrEmpty(d.SealToken),
            Mine = d.UserId == userId,
            OrganizationId = d.OrganizationId,
            Version = d.Version,
            CreatedAt = d.CreatedAt,
            UpdatedAt = d.UpdatedAt,
        }));
    }

    // ── Get one ──────────────────────────────────────────────────────────────
    [HttpGet("documents/{id}")]
    public async Task<ActionResult<EmmdDocumentDto>> Get(string id)
    {
        var userId = GetUserId();
        var membership = await _orgs.GetActiveMembershipAsync(userId);
        var doc = await _context.EmmdDocuments.FirstOrDefaultAsync(d => d.Id == id);
        if (doc == null || !CanView(doc, userId, membership)) return NotFound();

        return Ok(ToDto(doc, userId, membership));
    }

    // ── Create ───────────────────────────────────────────────────────────────
    [HttpPost("documents")]
    [RequestSizeLimit(EmmdLimits.MaxContentBytes + 64 * 1024)]
    public async Task<ActionResult<EmmdDocumentDto>> Create([FromBody] SaveEmmdDocumentRequest req)
    {
        var sizeError = ContentSizeError(req.Content);
        if (sizeError != null) return BadRequest(new { message = sizeError });

        var userId = GetUserId();
        var membership = await _orgs.GetActiveMembershipAsync(userId);

        var doc = new EmmdDocument
        {
            UserId = userId,
            OrganizationId = membership?.OrganizationId,
            Title = Clean(req.Title, "Untitled document", EmmdLimits.MaxTitleLength),
            Theme = EmmdLimits.NormalizeTheme(req.Theme),
            Content = req.Content ?? string.Empty,
            Version = 1,
        };
        _context.EmmdDocuments.Add(doc);
        await _context.SaveChangesAsync();


        return Ok(ToDto(doc, userId, membership));
    }

    // ── Update (editing invalidates any existing seal) ───────────────────────
    [HttpPut("documents/{id}")]
    [RequestSizeLimit(EmmdLimits.MaxContentBytes + 64 * 1024)]
    public async Task<ActionResult<EmmdDocumentDto>> Update(string id, [FromBody] SaveEmmdDocumentRequest req)
    {
        var sizeError = ContentSizeError(req.Content);
        if (sizeError != null) return BadRequest(new { message = sizeError });

        var userId = GetUserId();
        var membership = await _orgs.GetActiveMembershipAsync(userId);
        var doc = await _context.EmmdDocuments.FirstOrDefaultAsync(d => d.Id == id);
        if (doc == null || !CanView(doc, userId, membership)) return NotFound();
        if (!CanEdit(doc, userId, membership)) return Forbid();

        // Optimistic concurrency: refuse to overwrite work the client hasn't seen.
        if (req.ExpectedVersion is { } expected && expected != doc.Version)
            return Conflict(new { message = "This document was changed elsewhere. Reload to get the latest version before saving.", currentVersion = doc.Version });

        doc.Title = Clean(req.Title, doc.Title, EmmdLimits.MaxTitleLength);
        doc.Theme = EmmdLimits.NormalizeTheme(req.Theme);

        var newContent = req.Content ?? string.Empty;
        // A change to the sealed bytes breaks the seal — clear it so the document
        // never claims to be sealed while its content has moved on.
        if (!string.IsNullOrEmpty(doc.SealToken) &&
            EmmdSealService.HashHex(newContent) != doc.SealHash)
        {
            doc.SealToken = null;
            doc.SealHash = null;
            doc.SealedBy = null;
            doc.SealedAt = null;
        }
        doc.Content = newContent;
        doc.Version++;
        doc.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();


        return Ok(ToDto(doc, userId, membership));
    }

    // ── Delete ───────────────────────────────────────────────────────────────
    [HttpDelete("documents/{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var userId = GetUserId();
        var membership = await _orgs.GetActiveMembershipAsync(userId);
        var doc = await _context.EmmdDocuments.FirstOrDefaultAsync(d => d.Id == id);
        if (doc == null || !CanView(doc, userId, membership)) return NotFound();
        if (!CanEdit(doc, userId, membership)) return Forbid();

        _context.EmmdDocuments.Remove(doc);
        await _context.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    // ── Seal (platform-held key) ─────────────────────────────────────────────
    [HttpPost("documents/{id}/seal")]
    public async Task<ActionResult<EmmdDocumentDto>> SealDocument(string id)
    {
        var userId = GetUserId();
        var membership = await _orgs.GetActiveMembershipAsync(userId);
        var doc = await _context.EmmdDocuments.FirstOrDefaultAsync(d => d.Id == id);
        if (doc == null || !CanView(doc, userId, membership)) return NotFound();
        if (!CanEdit(doc, userId, membership)) return Forbid();

        var (token, hash, sealedBy, sealedAt) = _seal.Seal(doc.Id, doc.Content);
        doc.SealToken = token;
        doc.SealHash = hash;
        doc.SealedBy = sealedBy;
        doc.SealedAt = sealedAt;
        doc.Version++;
        doc.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(ToDto(doc, userId, membership));
    }

    // ── Remove a seal (so the document can be edited again cleanly) ──────────
    [HttpPost("documents/{id}/unseal")]
    public async Task<ActionResult<EmmdDocumentDto>> Unseal(string id)
    {
        var userId = GetUserId();
        var membership = await _orgs.GetActiveMembershipAsync(userId);
        var doc = await _context.EmmdDocuments.FirstOrDefaultAsync(d => d.Id == id);
        if (doc == null || !CanView(doc, userId, membership)) return NotFound();
        if (!CanEdit(doc, userId, membership)) return Forbid();

        doc.SealToken = null;
        doc.SealHash = null;
        doc.SealedBy = null;
        doc.SealedAt = null;
        doc.Version++;
        doc.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(ToDto(doc, userId, membership));
    }

    // ── Verify arbitrary content (e.g. a pasted / uploaded .emmd file) ───────
    [HttpPost("verify")]
    [RequestSizeLimit(EmmdLimits.MaxContentBytes + 64 * 1024)]
    public ActionResult<EmmdSealStatusDto> VerifyContent([FromBody] VerifyEmmdRequest req)
    {
        var sizeError = ContentSizeError(req.Content);
        if (sizeError != null) return BadRequest(new { message = sizeError });
        return Ok(_seal.Verify(req.Content, req.SealToken));
    }

    // ── Public verification key (so anyone can check a seal offline) ─────────
    [AllowAnonymous]
    [HttpGet("public-key")]
    public IActionResult PublicKey()
    {
        return Ok(new
        {
            alg = EmmdSealService.Algorithm,
            tokenPrefix = EmmdSealService.TokenPrefix,
            publicKeyPem = _seal.PublicKeyPem,
            note = "ECDSA P-256 + SHA-256. Verify GPSEAL1 tokens against this key. " +
                   "A valid seal proves the document was sealed by GenPrompt and is unmodified; " +
                   "it is tamper-evidence, not a legally-binding electronic signature.",
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private static bool CanView(EmmdDocument d, string userId, OrganizationMember? m) =>
        d.UserId == userId || (m != null && d.OrganizationId != null && d.OrganizationId == m.OrganizationId);

    private static bool CanEdit(EmmdDocument d, string userId, OrganizationMember? m) =>
        d.UserId == userId || (m != null && d.OrganizationId != null && d.OrganizationId == m.OrganizationId && OrgRoles.IsManager(m.Role));

    private EmmdDocumentDto ToDto(EmmdDocument d, string userId, OrganizationMember? m) => new()
    {
        Id = d.Id,
        Title = d.Title,
        Theme = d.Theme,
        Content = d.Content,
        Mine = d.UserId == userId,
        CanEdit = CanEdit(d, userId, m),
        OrganizationId = d.OrganizationId,
        Version = d.Version,
        SealToken = d.SealToken,
        Seal = _seal.Verify(d.Content, d.SealToken),
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
    };

    private static string Clean(string? value, string fallback, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var v = value.Trim();
        return v.Length > max ? v.Substring(0, max) : v;
    }

    /// <summary>Null when the content is within limits, else a user-facing message.</summary>
    private static string? ContentSizeError(string? content)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(content ?? string.Empty);
        return bytes > EmmdLimits.MaxContentBytes
            ? $"Document is too large ({bytes / 1024} KB). The limit is {EmmdLimits.MaxContentBytes / 1024} KB."
            : null;
    }
}
