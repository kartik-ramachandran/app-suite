using System.ComponentModel.DataAnnotations;

namespace KitchenManager.Api.Models;

/// <summary>
/// A recipe the user owns.
///
/// Kitchen has no recipe storage today: it generates suggestions from whatever
/// is in the pantry and throws them away. So importing a cookbook is not just
/// "a book feature" — it fills a real hole. Once recipes persist, "what can I
/// cook tonight?" can answer from a library the user actually chose, instead of
/// inventing something new every time.
/// </summary>
public class Recipe
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required, MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>
    /// JSON array of { name, quantity, unit }. Kept as JSON rather than a
    /// child table because ingredients are only ever read as a whole list —
    /// but the `name` values are also copied to IngredientNames below, which
    /// IS what pantry matching searches.
    /// </summary>
    public string IngredientsJson { get; set; } = "[]";

    /// <summary>
    /// Lower-cased ingredient names, newline separated.
    ///
    /// Denormalised on purpose: matching a pantry against recipes is the main
    /// query this table exists for, and doing it by parsing JSON per row means
    /// loading every recipe to answer "what can I cook?".
    /// </summary>
    public string IngredientNames { get; set; } = string.Empty;

    /// <summary>JSON array of method steps, in order.</summary>
    public string StepsJson { get; set; } = "[]";

    [MaxLength(100)]
    public string? Cuisine { get; set; }

    [MaxLength(100)]
    public string? Course { get; set; }

    public int? Servings { get; set; }
    public int? TotalMinutes { get; set; }

    /// <summary>JSON array: vegetarian, vegan, gluten-free, contains-nuts…</summary>
    public string DietaryJson { get; set; } = "[]";

    // Provenance. A recipe imported from a book should say so — both to credit
    // the source and so a user can delete everything one book brought in.
    [MaxLength(300)]
    public string? SourceBookTitle { get; set; }

    public Guid? SourceBookId { get; set; }

    /// <summary>book | manual | generated</summary>
    [MaxLength(30)]
    public string Source { get; set; } = "book";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastCookedAt { get; set; }
    public int TimesCooked { get; set; }
    public bool IsFavourite { get; set; }
}

/// <summary>One ingredient line, as extracted from a book.</summary>
public class RecipeIngredient
{
    public string Name { get; set; } = string.Empty;
    public string? Quantity { get; set; }
    public string? Unit { get; set; }
}
