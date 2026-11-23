namespace BookReader.Api.Models;

/// <summary>
/// What kind of book this is, and therefore which module should own it.
///
/// This replaces a plain fiction/non-fiction flag. The flag only decided
/// whether to show a cast panel; a genre decides what the book BECOMES —
/// a cookbook is a recipe library, an exam guide is a study deck, a novel is a
/// voiced audiobook. The parse is the same either way and is already paid for;
/// only the extraction schema and the destination change.
///
/// Adding a genre is a table entry plus an extractor, not a new module.
/// </summary>
public static class BookGenres
{
    public const string Cookbook = "cookbook";
    public const string ExamGuide = "exam-guide";
    public const string Fiction = "fiction";
    public const string Travel = "travel";
    public const string Health = "health";
    public const string Manual = "manual";
    public const string General = "general";

    public record Genre(
        string Id,
        string Label,
        string Description,
        string Module,
        string ModuleLabel,
        string Route,
        string Icon,
        string ExtractLabel);

    /// <summary>
    /// Every genre the router knows. `Module` is where its content goes;
    /// General has none, which is how "just read it" stays the safe default.
    /// </summary>
    public static readonly Genre[] All =
    {
        new(Cookbook, "Cookbook",
            "Recipes with ingredients and method",
            // Recipes has its own page: Kitchen is the pantry, Recipes is what
            // you can make from it, and an import should land where the user
            // will actually see the result.
            "recipes", "Recipes", "/recipes", "bi-egg-fried", "recipes"),

        new(ExamGuide, "Study or exam guide",
            "Course material, certification prep, textbook",
            "study", "Study Studio", "/study", "bi-mortarboard", "study topics"),

        new(Travel, "Travel guide",
            "Places, itineraries, things to do",
            "travel", "Travel", "/travel", "bi-airplane", "places"),

        new(Health, "Health or medical",
            "Conditions, treatments, wellbeing",
            // Routed to Medicine, but everything extracted is screened by
            // LifeSafetyService before it reaches the user. A book stating a
            // drug interaction is not a licence for us to repeat it as advice.
            "medicine", "Medicine Cabinet", "/medicine", "bi-capsule", "health notes"),

        new(Manual, "Technical manual",
            "Procedures, commands, reference material",
            "skills", "Skills", "/skills", "bi-tools", "procedures"),

        new(Fiction, "Novel or story",
            "Characters and dialogue",
            "book-reader", "Book Reader", "/book-reader", "bi-book", "characters"),

        new(General, "General reading",
            "Read aloud, nothing extracted",
            "", "", "", "bi-book", ""),
    };

    public static Genre Find(string? id) =>
        All.FirstOrDefault(g => g.Id == id) ?? All.First(g => g.Id == General);

    /// <summary>Genres whose content can be imported somewhere.</summary>
    public static bool HasDestination(string? id) => !string.IsNullOrEmpty(Find(id).Module);
}
