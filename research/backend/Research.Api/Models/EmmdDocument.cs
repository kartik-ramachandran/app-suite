namespace Research.Api.Models;

// ── EMMD limits / themes ────────────────────────────────────────────────────
public static class EmmdLimits
{
    // A 2 MB text document is already enormous; this bounds DB growth and the
    // cost of hashing on verify without getting in the way of real documents
    // (including modest embedded data: images).
    public const int MaxContentBytes = 2 * 1024 * 1024;
    public const int MaxTitleLength = 200;

    // Themes must be one of the renderer's known ids; anything else falls back to
    // "clean" so a bad value can never end up in a CSS class or export.
    public static readonly string[] Themes =
        { "clean", "proposal", "invoice", "letter", "contract", "technical", "dark" };

    public static string NormalizeTheme(string? theme) =>
        theme != null && Themes.Contains(theme, StringComparer.OrdinalIgnoreCase)
            ? theme.ToLowerInvariant()
            : "clean";
}

// ── Enhanced MMD (EMMD) documents ───────────────────────────────────────────
// EMMD is plain Markdown plus optional, comment-based enhancement layers
// (YAML frontmatter, a scoped <!--emmd:style--> block, and a <!--emmd:seal-->
// integrity comment). A .emmd file therefore still opens as ordinary Markdown
// in any reader, but our renderer applies themes/HTML and can verify the seal.
//
// Documents are owned by a user and, when the author belongs to a business,
// also attributed to their Organization so team members share a workspace.
// The seal is a platform-held ECDSA signature over a hash of the canonical
// content — tamper-evidence ("sealed by GenPrompt, content unmodified"), NOT a
// legally-binding electronic signature.

public class EmmdDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = string.Empty;
    public string? OrganizationId { get; set; }

    public string Title { get; set; } = "Untitled document";
    public string Theme { get; set; } = "clean";

    /// <summary>Bumped on every mutation. Powers optimistic-concurrency checks so
    /// two people editing the same team document don't silently clobber each other.</summary>
    public int Version { get; set; }

    /// <summary>
    /// The full .emmd source WITHOUT the seal comment (frontmatter + optional
    /// style block + Markdown body). The seal is stored in the columns below and
    /// re-embedded only when a sealed file is exported.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    // ── Seal (tamper-evidence) ───────────────────────────────────────────────
    /// <summary>GPSEAL1.&lt;payload&gt;.&lt;signature&gt; token, or null when unsealed.</summary>
    public string? SealToken { get; set; }
    /// <summary>SHA-256 (hex) of the canonical content that was sealed.</summary>
    public string? SealHash { get; set; }
    /// <summary>Human label for who sealed it (e.g. "GenPrompt Platform").</summary>
    public string? SealedBy { get; set; }
    public DateTime? SealedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// ── Request DTOs ────────────────────────────────────────────────────────────

public class SaveEmmdDocumentRequest
{
    public string? Title { get; set; }
    public string? Theme { get; set; }
    public string Content { get; set; } = string.Empty;
    /// <summary>On update: the version the client last saw. If it no longer
    /// matches, the server rejects with 409 instead of overwriting newer work.</summary>
    public int? ExpectedVersion { get; set; }
}

public class VerifyEmmdRequest
{
    /// <summary>The document content with the seal comment removed.</summary>
    public string Content { get; set; } = string.Empty;
    /// <summary>The seal token extracted from the &lt;!--emmd:seal--&gt; comment.</summary>
    public string SealToken { get; set; } = string.Empty;
}

// ── Response DTOs ───────────────────────────────────────────────────────────

public class EmmdSealStatusDto
{
    public bool Sealed { get; set; }
    public bool Valid { get; set; }
    public bool Tampered { get; set; }
    public string? SealedBy { get; set; }
    public DateTime? SealedAt { get; set; }
    public string? Reason { get; set; }
}

public class EmmdDocumentSummaryDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Theme { get; set; } = "clean";
    public bool Sealed { get; set; }
    public bool Mine { get; set; }
    public string? OrganizationId { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class EmmdDocumentDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Theme { get; set; } = "clean";
    public string Content { get; set; } = string.Empty;
    public bool Mine { get; set; }
    public bool CanEdit { get; set; }
    public string? OrganizationId { get; set; }
    public int Version { get; set; }
    public string? SealToken { get; set; }
    public EmmdSealStatusDto Seal { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
