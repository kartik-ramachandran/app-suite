namespace KitBuilder.Api.Models;

public class User
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? PasswordHash { get; set; }
    public string? GoogleId { get; set; }
    public string? ProfilePicture { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastLoginAt { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsAdmin { get; set; } = false;
    public string Language { get; set; } = "en";
    public string ReferralCode { get; set; } = string.Empty;
    public string? ReferredBy { get; set; }
    public bool DigestEnabled { get; set; } = false;



    // ── Manual book-reader grant ──────────────────────────────────────────────
    //
    // Set by an admin after arranging payment out of band. Deliberately kept
    // off the organisation plan: this is one person paying for one thing, and
    // promoting them to a paid ORG would silently unlock every other paid
    // module too.

    /// <summary>
    /// When the manual grant expires. Null means no grant. An expiry rather
    /// than a flag, because a lapsed arrangement should close by itself - a
    /// boolean would leave someone on the paid tier forever once forgotten.
    /// </summary>
    public DateTime? BookGrantExpiresAt { get; set; }

    /// <summary>Books this user may upload in total while the grant is live.</summary>
    public int? BookGrantLimit { get; set; }

    /// <summary>Free-text note: who arranged it, what they paid, when.</summary>
    public string? BookGrantNote { get; set; }
    public DateTime? DigestLastSentAt { get; set; }

    // Navigation property
    public ICollection<Prompt> Prompts { get; set; } = new List<Prompt>();
}
