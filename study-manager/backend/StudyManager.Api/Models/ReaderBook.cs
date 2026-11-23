using System.ComponentModel.DataAnnotations;

namespace StudyManager.Api.Models;

/// <summary>
/// A book uploaded for the read-aloud reader.
///
/// Ported from the standalone Bookreader app. Two deliberate differences:
///
///  - UserId is a string, matching GenPrompt's User.Id, rather than the Guid
///    the original used. Everything else in this database keys off that, and a
///    second identity shape would need translating at every join.
///
///  - Characters and dialogue are separate tables rather than JSON columns.
///    The original stored CharactersJson/DialogueLinesJson, which meant loading
///    an entire novel's dialogue to render one page. The reader pages through
///    dialogue constantly, so it needs to be queryable.
/// </summary>
public class ReaderBook
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? Author { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Blob key for the uploaded file, e.g. "{bookId}/source.epub". Never
    /// returned to the client — it is resolved server-side on download.
    /// </summary>
    public string? StorageKey { get; set; }

    [MaxLength(50)]
    public string? Format { get; set; }

    public long? FileSize { get; set; }

    /// <summary>uploaded → parsing → analysing → ready → failed</summary>
    [MaxLength(50)]
    public string Status { get; set; } = "uploaded";

    public string? StatusMessage { get; set; }

    /// <summary>
    /// Whether the extracted text and AI script exist in blob storage.
    ///
    /// The text and script themselves are deliberately NOT columns: a novel
    /// runs to megabytes, and holding that in Postgres means paying database
    /// rates to store, back up and replicate a payload that is written once
    /// and read sequentially. They live at "{Id}/text.txt" and
    /// "{Id}/analysis.json"; this row keeps only what is worth querying.
    /// </summary>
    public bool HasAnalysis { get; set; }

    public int? WordCount { get; set; }

    // Reading progress — what makes "resume where I left off" work across devices.
    public int CurrentLineIndex { get; set; }
    public double CompletionPercentage { get; set; }
    public DateTime? LastReadAt { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }

    /// <summary>
    /// When a re-analysis was last STARTED. Used to rate-limit re-analysis:
    /// each one is ~29 AI calls, so the button needs a daily ceiling.
    /// </summary>
    public DateTime? LastReanalysedAt { get; set; }

    /// <summary>
    /// Set when the user deletes the book. The row is kept as a tombstone
    /// rather than removed, because the free allowance counts books ever
    /// uploaded, not books currently held — otherwise deleting and re-uploading
    /// would be an unlimited free tier.
    ///
    /// The blobs ARE deleted, so a tombstone costs one small row and nothing
    /// else. Cast rows go with it.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    /// <summary>Line count, denormalised so the library never loads a script to count it.</summary>
    public int LineCount { get; set; }

    /// <summary>
    /// What kind of book this is: see BookGenres. Decided during processing,
    /// and it decides everything downstream — a cookbook becomes recipes in
    /// Kitchen, an exam guide becomes study material, a novel gets a cast.
    ///
    /// This replaced a plain fiction/non-fiction flag, which could only choose
    /// whether to show a cast panel.
    /// </summary>
    [MaxLength(30)]
    public string Genre { get; set; } = BookGenres.General;

    /// <summary>How sure the classifier was, 0-1. Shown when we ask the user to confirm.</summary>
    public double GenreConfidence { get; set; }

    /// <summary>
    /// How many items were imported to this book's destination module, and
    /// when. Null ImportedAt means the offer has not been accepted yet — which
    /// is what keeps the import opt-in rather than automatic.
    /// </summary>
    public int ImportedCount { get; set; }
    public DateTime? ImportedAt { get; set; }

    /// <summary>
    /// upload | snap.
    ///
    /// "snap" is a book photographed off a shelf: catalogued, not read, so it
    /// has no file, no text and no audio. Bookshelf used to be a separate
    /// module for exactly this, which split one question - "what books do I
    /// have?" - across two places in the app.
    /// </summary>
    [MaxLength(20)]
    public string Source { get; set; } = "upload";

    /// <summary>Catalogue fields, populated for snapped books.</summary>
    [MaxLength(200)]
    public string? Subject { get; set; }

    [MaxLength(20)]
    public string? PublishedYear { get; set; }

    /// <summary>
    /// The cast stays in the database: it is small, it is queried to render
    /// the library and the cast panel, and voice assignments are edited
    /// individually — all things a blob is bad at.
    /// </summary>
    public ICollection<BookCharacter> Characters { get; set; } = new List<BookCharacter>();
}

/// <summary>
/// A character the AI found in the book, and the voice assigned to them.
/// The voice assignment is what makes the reader more than a text-to-speech
/// button: each speaker sounds like a different person.
/// </summary>
public class BookCharacter
{
    public int Id { get; set; }
    public Guid BookId { get; set; }
    public ReaderBook? Book { get; set; }

    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>male | female | unknown — drives the default voice pick.</summary>
    [MaxLength(20)]
    public string Gender { get; set; } = "unknown";

    /// <summary>JSON array of trait strings, e.g. ["gruff","loyal"].</summary>
    public string? TraitsJson { get; set; }

    /// <summary>Azure Speech voice short name, e.g. en-GB-RyanNeural.</summary>
    [MaxLength(100)]
    public string? AssignedVoiceId { get; set; }

    /// <summary>How many lines this character speaks — used to order the cast list.</summary>
    public int LineCount { get; set; }
}

/// <summary>
/// One spoken or narrated line.
///
/// A plain object, not a database entity: the script for a novel is tens of
/// thousands of these, they are only ever read in order, and they are never
/// queried individually. They are serialised into the analysis blob instead.
/// </summary>
public class BookLine
{
    public int LineIndex { get; set; }
    public string Speaker { get; set; } = "Narrator";
    public string Text { get; set; } = string.Empty;
    public bool IsNarration { get; set; }

    /// <summary>Source page, used to pair a figure caption with its picture.</summary>
    public int Page { get; set; }

    /// <summary>
    /// What this block is, as judged by the model: heading | body | caption.
    /// Apparatus (front matter, contents, running headers) is dropped during
    /// structuring and never reaches the reader.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// Blob keys for figures printed on this line's page, when the line is a
    /// figure caption. Empty for ordinary text.
    ///
    /// Held on the line rather than looked up at render time because the
    /// pairing is decided once during processing, and the reader should not
    /// have to re-derive it on every scroll.
    /// </summary>
    public List<string>? Figures { get; set; }
}

/// <summary>What gets written to and read from "{bookId}/analysis.json".</summary>
public class BookAnalysisBlob
{
    public List<BookLine> Lines { get; set; } = new();
}
