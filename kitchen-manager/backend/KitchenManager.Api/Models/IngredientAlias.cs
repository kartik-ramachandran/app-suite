using System.ComponentModel.DataAnnotations;

namespace KitchenManager.Api.Models;

/// <summary>
/// Maps an ingredient name to its canonical form, so "scallions" and
/// "spring onions" are the same thing when matching a pantry.
///
/// Why a table and not an AI call at match time: matching runs on every page
/// load of Cook Tonight, for every recipe, forever. Resolving synonyms there
/// would mean a model call per page view for a comparison that is usually
/// trivial, with latency and non-deterministic answers on refresh.
///
/// A name's canonical form does not depend on who is asking or when, so it is
/// resolved ONCE at write time and cached here. Matching then stays free.
///
/// Rows are global, not per-user: "aubergine" is "eggplant" for everybody, and
/// sharing the table means the AI fallback fires once across all users rather
/// than once per user.
/// </summary>
public class IngredientAlias
{
    public int Id { get; set; }

    /// <summary>The name as written, lower-cased. Unique.</summary>
    [Required, MaxLength(120)]
    public string Alias { get; set; } = string.Empty;

    /// <summary>What it should be treated as.</summary>
    [Required, MaxLength(120)]
    public string Canonical { get; set; } = string.Empty;

    /// <summary>seed | ai — so a bad AI guess can be found and corrected later.</summary>
    [MaxLength(20)]
    public string Source { get; set; } = "seed";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
