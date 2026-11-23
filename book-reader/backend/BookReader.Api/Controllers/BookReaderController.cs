using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using BookReader.Api.Data;
using BookReader.Api.Models;
using BookReader.Api.Services;

namespace BookReader.Api.Controllers;

/// <summary>
/// Upload a book, have AI cast its characters, then listen to it with a
/// different voice per speaker.
///
/// Ported from the standalone Bookreader app. Auth, payments and file storage
/// come from GenPrompt rather than being carried over — the original shipped
/// its own of each, and running two of any of them in one app is how things rot.
/// </summary>
[ApiController]
[Route("api/book-reader")]
[Authorize]
public class BookReaderController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly BookProcessingQueue _queue;
    private readonly BookSpeechService _speech;
    private readonly BookStorageService _storage;
    private readonly AzureVisionService _vision;
    private readonly IMemoryCache _cache;
    private readonly OrganizationService _orgs;
    private readonly ILogger<BookReaderController> _logger;

    /// <summary>
    /// Books a free account may upload, ever. Three is enough to become a
    /// habit — one book is a demo, three is a library someone returns to.
    ///
    /// This counts uploads over the account's lifetime, including deleted
    /// books. Counting only what the user currently holds would make the
    /// allowance unlimited: delete, upload, repeat.
    /// </summary>
    private const int FreeBookLimit = 3;

    /// <summary>Books a manually granted user may upload.</summary>
    private const int GrantedBookLimit = 50;

    /// <summary>
    /// Re-analyses allowed per user per day.
    ///
    /// Re-analysing is free - charging someone to fix our own bad extraction
    /// would be wrong - but free has to mean "does not use up a book", not
    /// "unlimited". A re-analysis is ~29 AI calls, so an unbounded button is an
    /// unbounded bill.
    /// </summary>
    private const int FreeReanalysesPerDay = 5;

    /// <summary>The same allowance for a granted user.</summary>
    private const int GrantedReanalysesPerDay = 25;

    private const long MaxUploadBytes = 100 * 1024 * 1024;

    public BookReaderController(
        PromptDbContext db,
        BookProcessingQueue queue,
        BookSpeechService speech,
        BookStorageService storage,
        AzureVisionService vision,
        IMemoryCache cache,
        OrganizationService orgs,
        ILogger<BookReaderController> logger)
    {
        _db = db;
        _queue = queue;
        _speech = speech;
        _storage = storage;
        _vision = vision;
        _cache = cache;
        _orgs = orgs;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    // ── Library ───────────────────────────────────────────────────────────────

    [HttpGet("books")]
    public async Task<IActionResult> List()
    {
        var books = await _db.ReaderBooks
            .Where(b => b.UserId == UserId && b.DeletedAt == null)
            .OrderByDescending(b => b.UploadedAt)
            .Select(b => new
            {
                b.Id,
                b.Title,
                b.Author,
                b.Format,
                b.Status,
                b.StatusMessage,
                b.WordCount,
                b.Genre,
                // The card needs these to know a snapped book has nothing to play.
                b.Source,
                b.Subject,
                b.PublishedYear,
                b.ImportedCount,
                b.ImportedAt,
                b.CurrentLineIndex,
                b.CompletionPercentage,
                b.LastReadAt,
                b.UploadedAt,
                CharacterCount = b.Characters.Count,
                b.LineCount,
            })
            .ToListAsync();

        // Name what the import extracted, per genre — "study topics" for an
        // exam guide, "recipes" for a cookbook. Resolved here because the
        // genre table lives server-side; the card used to hardcode "recipes"
        // and told the owner of an Azure exam guide it had imported recipes.
        var shaped = books.Select(b => new
        {
            b.Id, b.Title, b.Author, b.Format, b.Status, b.StatusMessage, b.WordCount,
            b.Genre, b.Source, b.Subject, b.PublishedYear, b.ImportedCount, b.ImportedAt,
            b.CurrentLineIndex, b.CompletionPercentage, b.LastReadAt, b.UploadedAt,
            b.CharacterCount, b.LineCount,
            ImportedLabel = string.IsNullOrEmpty(BookGenres.Find(b.Genre).ExtractLabel)
                ? "items" : BookGenres.Find(b.Genre).ExtractLabel,
        }).ToList();

        // Lifetime count, so the UI can show "2 of 3 used" honestly rather
        // than implying a deleted book gave a slot back.
        var everUploaded = await _db.ReaderBooks.CountAsync(b => b.UserId == UserId);
        var statusAllowance = await GetAllowanceAsync();

        return Ok(new
        {
            books = shaped,
            freeLimit = statusAllowance.Books,
            booksUsed = everUploaded,
            booksRemaining = statusAllowance.Unlimited
                ? (int?)null
                : Math.Max(0, statusAllowance.Books - everUploaded),
            tier = statusAllowance.Tier,
            reanalysesPerDay = statusAllowance.Unlimited ? (int?)null : statusAllowance.Reanalyses,
            reanalysesUsedToday = await ReanalysesTodayAsync(),
            unlimited = statusAllowance.Unlimited,
            // The client needs to know whether to offer neural voices or fall
            // back to the browser's own speech synthesis.
            neuralVoices = _speech.IsAvailable,
            uploadsEnabled = _storage.IsAvailable,
            uploadsDisabledReason = _storage.IsAvailable ? null : _storage.UnavailableReason,
        });
    }

    // ── Upload ────────────────────────────────────────────────────────────────

    [HttpPost("books")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload(IFormFile? file, [FromForm] string? title, [FromForm] string? author)
    {
        // Never accept a book we know will be lost: see BookStorageService.
        if (!_storage.IsAvailable)
            return StatusCode(503, new { error = _storage.UnavailableReason });

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Choose a book file to upload." });

        if (file.Length > MaxUploadBytes)
            return BadRequest(new { error = "That file is larger than 100 MB." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!BookParserService.SupportedExtensions.Contains(ext))
            return BadRequest(new
            {
                error = $"{ext} files aren't supported yet.",
                supported = BookParserService.SupportedExtensions,
            });

        // The limit, checked before any work is done. Deleted books still
        // count: the allowance is N books ever, not N at a time.
        var allowance = await GetAllowanceAsync();
        var everUploaded = await _db.ReaderBooks.CountAsync(b => b.UserId == UserId);

        if (!allowance.Unlimited && everUploaded >= allowance.Books)
        {
            return StatusCode(402, new
            {
                error = $"You've used all {allowance.Books} of your books.",
                detail = allowance.Tier == "granted"
                    ? "Deleting a book frees up storage, but not another upload. Contact us if you need a larger library."
                    : "Deleting a book frees up storage, but not another free upload. Contact us to arrange more.",
                freeLimit = allowance.Books,
                booksUsed = everUploaded,
                tier = allowance.Tier,
                contactUrl = "/contact",
            });
        }

        var bookId = Guid.NewGuid();

        // Straight to blob storage. The original filename is untrusted and is
        // kept only as the display title; the blob key is derived from the id.
        string storageKey;
        await using (var stream = file.OpenReadStream())
            storageKey = await _storage.SaveAsync(bookId, ext, stream);

        var book = new ReaderBook
        {
            Id = bookId,
            Title = string.IsNullOrWhiteSpace(title)
                ? Path.GetFileNameWithoutExtension(file.FileName)
                : title.Trim(),
            Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
            UserId = UserId,
            StorageKey = storageKey,
            Format = ext.TrimStart('.'),
            FileSize = file.Length,
            Status = "uploaded",
            StatusMessage = "Queued…",
        };

        _db.ReaderBooks.Add(book);
        await _db.SaveChangesAsync();

        await _queue.EnqueueAsync(book.Id);

        return Ok(new { book.Id, book.Title, book.Status });
    }

    // ── Status (polled while processing) ──────────────────────────────────────

    [HttpGet("books/{id:guid}/status")]
    public async Task<IActionResult> Status(Guid id)
    {
        var book = await _db.ReaderBooks
            .Where(b => b.Id == id && b.UserId == UserId && b.DeletedAt == null)
            .Select(b => new { b.Id, b.Status, b.StatusMessage, CharacterCount = b.Characters.Count, b.LineCount })
            .FirstOrDefaultAsync();

        return book is null ? NotFound(new { error = "Book not found." }) : Ok(book);
    }

    // ── Reader ────────────────────────────────────────────────────────────────

    [HttpGet("books/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromQuery] int skip = 0, [FromQuery] int take = 200)
    {
        var book = await _db.ReaderBooks
            .Include(b => b.Characters)
            .FirstOrDefaultAsync(b => b.Id == id && b.UserId == UserId && b.DeletedAt == null);

        if (book is null) return NotFound(new { error = "Book not found." });

        // The script lives in blob storage; page it here so opening a novel
        // does not ship tens of thousands of lines to the browser at once.
        take = Math.Clamp(take, 1, 500);
        var script = await LoadScriptAsync(id);
        var lines = script.Skip(Math.Max(0, skip)).Take(take)
            .Select(l => new
            {
                l.LineIndex, l.Speaker, l.Text, l.IsNarration, l.Page, l.Kind,
                // Served through our own endpoint rather than a blob URL, so
                // ownership is checked on every image request.
                figures = l.Figures == null || l.Figures.Count == 0
                    ? null
                    : l.Figures.Select(k => $"/api/book-reader/books/{id}/figure?key={Uri.EscapeDataString(k)}").ToList(),
            });

        return Ok(new
        {
            book.Id,
            book.Title,
            book.Author,
            book.Status,
            book.StatusMessage,
            book.CurrentLineIndex,
            book.CompletionPercentage,
            book.WordCount,
            book.Genre,
            book.GenreConfidence,
            book.ImportedCount,
            book.ImportedAt,
            // What this book could become, and where. Null when the genre has
            // no destination — that is how "just read it" stays the default.
            destination = Destination(book),
            totalLines = book.LineCount,
            neuralVoices = _speech.IsAvailable,
            characters = book.Characters
                .OrderByDescending(c => c.LineCount)
                .Select(c => new
                {
                    c.Id,
                    c.Name,
                    c.Description,
                    c.Gender,
                    c.AssignedVoiceId,
                    c.LineCount,
                    Traits = ParseTraits(c.TraitsJson),
                }),
            lines,
        });
    }

    // ── Progress ──────────────────────────────────────────────────────────────

    [HttpPut("books/{id:guid}/progress")]
    public async Task<IActionResult> UpdateProgress(Guid id, [FromBody] ProgressRequest request)
    {
        var book = await _db.ReaderBooks.FirstOrDefaultAsync(b => b.Id == id && b.UserId == UserId && b.DeletedAt == null);
        if (book is null) return NotFound(new { error = "Book not found." });

        var total = book.LineCount;

        book.CurrentLineIndex = Math.Max(0, request.LineIndex);
        book.CompletionPercentage = total > 0
            ? Math.Round(Math.Min(100, (double)book.CurrentLineIndex / total * 100), 1)
            : 0;
        book.LastReadAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { book.CurrentLineIndex, book.CompletionPercentage });
    }

    // ── Voices ────────────────────────────────────────────────────────────────

    [HttpGet("voices")]
    public IActionResult Voices() => Ok(new
    {
        available = _speech.IsAvailable,
        voices = BookVoices.All().Select(v => new { id = v.Id, label = v.Label }),
    });

    /// <summary>Recast a character. Immediate — the next line they speak uses it.</summary>
    [HttpPut("characters/{characterId:int}/voice")]
    public async Task<IActionResult> SetVoice(int characterId, [FromBody] VoiceRequest request)
    {
        var character = await _db.BookCharacters
            .Include(c => c.Book)
            .FirstOrDefaultAsync(c => c.Id == characterId && c.Book!.UserId == UserId && c.Book.DeletedAt == null);

        if (character is null) return NotFound(new { error = "Character not found." });

        // Only voices we actually offer: an arbitrary string here would reach
        // the speech API and fail per line, which looks like a broken reader.
        var valid = BookVoices.All().Any(v => v.Id == request.VoiceId);
        if (!valid) return BadRequest(new { error = "That voice isn't available." });

        character.AssignedVoiceId = request.VoiceId;
        await _db.SaveChangesAsync();
        return Ok(new { character.Id, character.AssignedVoiceId });
    }

    // ── Speech ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders one line as mp3 in its speaker's voice. Returns 204 when neural
    /// speech is unavailable, which tells the client to use the browser's own
    /// synthesiser rather than showing an error.
    /// </summary>
    /// <param name="v">
    /// The voice the client believes is assigned. Not used to choose the voice —
    /// the database is the authority — but it makes the URL change when the
    /// voice changes, which is what stops a 24-hour cached mp3 being replayed
    /// after the user picks a different voice.
    /// </param>
    [HttpGet("books/{id:guid}/lines/{lineIndex:int}/audio")]
    public async Task<IActionResult> LineAudio(Guid id, int lineIndex, [FromQuery] string? v, CancellationToken ct)
    {
        var owns = await _db.ReaderBooks.AnyAsync(b => b.Id == id && b.UserId == UserId && b.DeletedAt == null, ct);
        if (!owns) return NotFound(new { error = "Book not found." });

        var script = await LoadScriptAsync(id, ct);
        var line = script.FirstOrDefault(l => l.LineIndex == lineIndex);
        if (line is null) return NotFound(new { error = "Line not found." });

        if (!_speech.IsAvailable) return NoContent();

        // The speaker's assigned voice — including narration: the Narrator is
        // a real character row now, so a non-fiction book's one voice can be
        // changed like any other. The static default is only the last resort.
        var voice = (await _db.BookCharacters
                .Where(c => c.BookId == id && c.Name == line.Speaker)
                .Select(c => c.AssignedVoiceId)
                .FirstOrDefaultAsync(ct))
            ?? BookVoices.Narrator;

        var audio = await _speech.SynthesiseAsync(line.Text, voice, ct);
        if (audio is null) return NoContent();

        // Cached hard: the same line in the same voice never changes, and
        // re-synthesising on every replay is a bill for nothing. Safe because
        // the voice is part of the request URL, so a voice change is a
        // different cache entry rather than a stale hit.
        Response.Headers.CacheControl = "private, max-age=86400";
        Response.Headers.Vary = "Authorization";
        return File(audio, "audio/mpeg");
    }

    /// <summary>
    /// Runs the analysis again on a book that is already uploaded.
    ///
    /// Needed because analysis improves over time — the book-type classifier
    /// landed after the first books were processed, so an Azure manual sitting
    /// in someone's library still claims to have a cast of eleven.
    ///
    /// Deliberately does NOT count against the free allowance: the user is not
    /// adding a book, and charging them to fix our mistake would be wrong. The
    /// source file is already in storage, so nothing is re-uploaded.
    /// </summary>
    /// <summary>
    /// How long a book may sit in "parsing"/"analysing" before a retry is
    /// allowed. Longer than the slowest real run, short enough that a user
    /// who hit a crash is not locked out for the rest of the day.
    /// </summary>
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(15);

    [HttpPost("books/{id:guid}/reanalyse")]
    public async Task<IActionResult> Reanalyse(Guid id)
    {
        var book = await _db.ReaderBooks
            .FirstOrDefaultAsync(b => b.Id == id && b.UserId == UserId && b.DeletedAt == null);

        if (book is null) return NotFound(new { error = "Book not found." });
        if (string.IsNullOrEmpty(book.StorageKey))
            return BadRequest(new { error = "The original file is no longer available for this book." });

        // Guard against queueing the same book repeatedly while it works -
        // but only while it is GENUINELY working.
        //
        // If the server restarts or processing throws, the row is left saying
        // "analysing" forever and this guard then refuses every retry: the
        // book is permanently stuck and re-analyse, the one button that could
        // fix it, is the thing that will not run. A run that has not touched
        // the row for a while is dead, not busy.
        var stale = book.ProcessedAt is null
            ? DateTime.UtcNow - book.UploadedAt > StuckAfter
            : DateTime.UtcNow - book.ProcessedAt > StuckAfter;

        if (book.Status is "parsing" or "analysing" && !stale)
            return Conflict(new { error = "This book is already being processed." });

        if (stale && book.Status is "parsing" or "analysing")
        {
            _logger.LogWarning(
                "Book {BookId} was stuck in {Status}; treating it as failed and re-queueing",
                id, book.Status);
        }

        // Re-analysing does not use up a book - fixing our own bad extraction
        // should not cost the user - but it is ~29 AI calls, so it needs a
        // daily ceiling or the button is an open tap on the AI bill.
        var allowance = await GetAllowanceAsync();
        if (!allowance.Unlimited)
        {
            var usedToday = await ReanalysesTodayAsync();
            if (usedToday >= allowance.Reanalyses)
            {
                return StatusCode(429, new
                {
                    error = $"You've re-analysed {usedToday} books today.",
                    detail = "Re-analysis is free but limited per day. Please try again tomorrow.",
                    limit = allowance.Reanalyses,
                    usedToday,
                });
            }
        }

        book.LastReanalysedAt = DateTime.UtcNow;

        _cache.Remove(ScriptKey(id));

        book.Status = "uploaded";
        book.StatusMessage = "Queued for a fresh read…";
        await _db.SaveChangesAsync();

        await _queue.EnqueueAsync(id);
        return Ok(new { book.Id, book.Status });
    }

    /// <summary>
    /// Serves one figure from a book.
    ///
    /// Goes through the API rather than handing out a blob URL because the key
    /// is guessable from a book id, and a book is private to the person who
    /// uploaded it.
    /// </summary>
    [HttpGet("books/{id:guid}/figure")]
    public async Task<IActionResult> Figure(Guid id, [FromQuery] string key, CancellationToken ct)
    {
        var owns = await _db.ReaderBooks
            .AnyAsync(b => b.Id == id && b.UserId == UserId && b.DeletedAt == null, ct);
        if (!owns) return NotFound();

        // The key comes from the client, so it must be confined to this book's
        // own folder - otherwise it is a path to any file in the container.
        if (string.IsNullOrWhiteSpace(key) ||
            !key.StartsWith($"{id}/figures/", StringComparison.Ordinal) ||
            key.Contains("..", StringComparison.Ordinal))
        {
            return BadRequest(new { error = "Bad figure reference." });
        }

        var stream = await _storage.OpenAsync(key, ct);
        if (stream is null) return NotFound();

        var contentType = key.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            ? "image/jpeg"
            : "image/png";

        // Figures never change once extracted, so let the browser keep them.
        Response.Headers.CacheControl = "private, max-age=86400";
        return File(stream, contentType);
    }

    // ── Snap a shelf ──────────────────────────────────────────────────────────

    /// <summary>Roughly 4 MB of base64, matching the other photo endpoints.</summary>
    private const int MaxImageChars = 5_600_000;

    /// <summary>
    /// Photographs a shelf and catalogues the books on it.
    ///
    /// This was the whole of the old Bookshelf module. It lives here because
    /// "what books do I have?" is one question: some you uploaded and can
    /// listen to, some you own on paper and can only catalogue. Two modules
    /// meant two answers and two places to look.
    /// </summary>
    [HttpPost("snap")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Snap([FromBody] SnapRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ImageData))
            return BadRequest(new { error = "Add a photo of your bookshelf." });

        if (req.ImageData.Length > MaxImageChars)
            return BadRequest(new { error = "That photo is too large - try a smaller one." });

        if (!_vision.IsAvailable)
            return StatusCode(503, new { error = "Reading photos is not available on this server yet." });

        string raw;
        try
        {
            raw = await _vision.AnalyzeAsync(req.ImageData,
                "You identify books from a photo of a shelf, a stack, or a single cover.\n" +
                "Return ONLY JSON (no markdown fences):\n" +
                "{\"books\":[{\"title\":\"...\",\"author\":\"...\",\"subject\":\"...\",\"year\":\"...\"}]}\n" +
                "Read the spines. Give the title as printed. Leave a field out if you cannot read it - " +
                "never guess an author. If no books are visible, return {\"books\":[]}.",
                "List the books you can see.", 1500);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read a bookshelf photo");
            return Ok(new { added = 0, message = "Could not read that photo. A straight-on shot of the spines works best." });
        }

        List<(string Title, string? Author, string? Subject, string? Year)> found = new();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("books", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in arr.EnumerateArray())
                {
                    var title = Str(b, "title");
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    found.Add((title.Trim(), Str(b, "author"), Str(b, "subject"), Str(b, "year")));
                }
            }
        }
        catch (JsonException)
        {
            return Ok(new { added = 0, message = "Could not read that photo clearly." });
        }

        if (found.Count == 0)
            return Ok(new { added = 0, message = "No books could be read from that photo." });

        // Skip titles already on the shelf, so photographing the same shelf
        // twice does not double it.
        var existing = (await _db.ReaderBooks
                .Where(b => b.UserId == UserId && b.DeletedAt == null)
                .Select(b => b.Title)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = new List<ReaderBook>();
        foreach (var (title, author, subject, year) in found)
        {
            if (existing.Contains(title)) continue;
            added.Add(new ReaderBook
            {
                UserId = UserId,
                Title = title,
                Author = author,
                Subject = subject,
                PublishedYear = year,
                Source = "snap",
                // Catalogued, not readable: there is no file behind a photo.
                Status = "catalogued",
                Genre = BookGenres.General,
            });
        }

        _db.ReaderBooks.AddRange(added);
        await _db.SaveChangesAsync(ct);

        return Ok(new
        {
            added = added.Count,
            skipped = found.Count - added.Count,
            titles = added.Take(10).Select(b => b.Title),
        });
    }

    /// <summary>
    /// Recommends from everything the user owns - uploaded and photographed.
    ///
    /// Reading across both is the point of the merge: the old Bookshelf could
    /// only see snapped books, so it ignored every book the user had actually
    /// uploaded and read.
    /// </summary>
    [HttpPost("recommend")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Recommend(CancellationToken ct)
    {
        var owned = await _db.ReaderBooks
            .Where(b => b.UserId == UserId && b.DeletedAt == null)
            .OrderByDescending(b => b.UploadedAt)
            .Select(b => new { b.Title, b.Author, b.Genre, b.Source })
            .Take(120)
            .ToListAsync(ct);

        if (owned.Count < 2)
            return Ok(new { advice = "", message = "Add a couple more books and I can suggest what to read next." });

        var list = string.Join("\n", owned.Select(b =>
            $"- {b.Title}{(string.IsNullOrWhiteSpace(b.Author) ? "" : " by " + b.Author)}"));

        try
        {
            var advice = await _vision.CompleteTextAsync(
                "You suggest what someone should read next, based on the books they own.\n" +
                "Give 4 or 5 suggestions as short lines: title, author, and one sentence on why it " +
                "follows from their shelf. Do not recommend a book already on the list. " +
                "No preamble, no numbering.",
                $"Their books:\n{list}", 700);

            return Ok(new { advice, basedOn = owned.Count });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not generate reading recommendations");
            return Ok(new { advice = "", message = "Could not think of suggestions just now." });
        }
    }

    public class SnapRequest
    {
        public string? ImageData { get; set; }
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ── Delete ────────────────────────────────────────────────────────────────

    [HttpDelete("books/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var book = await _db.ReaderBooks.FirstOrDefaultAsync(b => b.Id == id && b.UserId == UserId && b.DeletedAt == null);
        if (book is null) return NotFound(new { error = "Book not found." });

        // The blobs go — they are the expensive part and the user asked for
        // them gone. The row stays as a tombstone so the free allowance still
        // remembers this upload; see FreeBookLimit.
        await _storage.DeleteBookAsync(id);
        _cache.Remove(ScriptKey(id));

        _db.BookCharacters.RemoveRange(_db.BookCharacters.Where(c => c.BookId == id));

        book.DeletedAt = DateTime.UtcNow;
        book.StorageKey = null;
        book.HasAnalysis = false;

        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Whether this user may exceed the free limit.
    ///
    /// Uses the plan resolution the rest of the app already uses, so the book
    /// reader can never disagree with the other modules about who is paid.
    /// </summary>
    /// <summary>
    /// What this user may do, from the strongest entitlement that applies.
    ///
    /// A paid ORGANISATION is unlimited (it already pays for every module). A
    /// manual grant is a per-person arrangement with a fixed size and an expiry
    /// date, so a lapsed one closes by itself. Everyone else gets the free tier.
    /// </summary>
    private async Task<(int Books, int Reanalyses, bool Unlimited, string Tier)> GetAllowanceAsync()
    {
        if (await IsPaidAsync())
            return (int.MaxValue, int.MaxValue, true, "paid");

        var grant = await _db.Users
            .Where(u => u.Id == UserId)
            .Select(u => new { u.BookGrantExpiresAt, u.BookGrantLimit })
            .FirstOrDefaultAsync();

        if (grant?.BookGrantExpiresAt is { } expires && expires > DateTime.UtcNow)
            return (grant.BookGrantLimit ?? GrantedBookLimit, GrantedReanalysesPerDay, false, "granted");

        return (FreeBookLimit, FreeReanalysesPerDay, false, "free");
    }

    /// <summary>
    /// Re-analyses this user has started since midnight UTC, counted from the
    /// books themselves so no extra table is needed.
    /// </summary>
    private async Task<int> ReanalysesTodayAsync()
    {
        var since = DateTime.UtcNow.Date;
        return await _db.ReaderBooks
            .CountAsync(b => b.UserId == UserId && b.LastReanalysedAt >= since);
    }

    private async Task<bool> IsPaidAsync() => !await _orgs.IsFreeTierAsync(UserId);

    /// <summary>
    /// Reads a book's script out of blob storage.
    ///
    /// Cached in memory for a few minutes because playback asks for one line at
    /// a time: without this, listening to a chapter would mean a blob download
    /// per sentence. Keyed by book id, so a reprocessed book picks up its new
    /// script once the entry expires.
    /// </summary>
    private async Task<List<BookLine>> LoadScriptAsync(Guid bookId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue<List<BookLine>>(ScriptKey(bookId), out var cached) && cached is not null)
            return cached;

        var json = await _storage.ReadAnalysisAsync(bookId, ct);
        if (string.IsNullOrWhiteSpace(json)) return new();

        List<BookLine> lines;
        try
        {
            lines = JsonSerializer.Deserialize<BookAnalysisBlob>(json)?.Lines ?? new();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Analysis blob for book {BookId} is not readable", bookId);
            return new();
        }

        _cache.Set(ScriptKey(bookId), lines, TimeSpan.FromMinutes(10));
        return lines;
    }

    private static string ScriptKey(Guid bookId) => $"book-script:{bookId}";

    /// <summary>
    /// The offer shown after a book is ready: "this looks like a cookbook —
    /// add its recipes to your Kitchen?". Null when there is nothing to offer,
    /// so the UI has one thing to check.
    /// </summary>
    private static object? Destination(ReaderBook book)
    {
        if (!BookGenres.HasDestination(book.Genre)) return null;

        var genre = BookGenres.Find(book.Genre);
        return new
        {
            genre = genre.Id,
            label = genre.Label,
            module = genre.Module,
            moduleLabel = genre.ModuleLabel,
            route = genre.Route,
            icon = genre.Icon,
            extractLabel = genre.ExtractLabel,
            // Cookbooks import into Recipes and study guides into Study Studio;
            // each genre lists its own endpoint so the UI never guesses. The
            // rest are declared so the UI can say what a book IS without
            // pretending an import exists that does not.
            importable = ImportEndpoint(genre.Id) != null,
            importEndpoint = ImportEndpoint(genre.Id),
            imported = book.ImportedAt is not null,
        };
    }

    /// <summary>Where an import for this genre actually lands, or null when none exists yet.</summary>
    private static string? ImportEndpoint(string genreId) => genreId switch
    {
        BookGenres.Cookbook => "/recipes/import-from-book",
        BookGenres.ExamGuide => "/study/import-from-book",
        _ => null,
    };

    private static List<string> ParseTraits(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public record ProgressRequest(int LineIndex);
    public record VoiceRequest(string VoiceId);
}
