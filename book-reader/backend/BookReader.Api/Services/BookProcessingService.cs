using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using BookReader.Api.Data;
using BookReader.Api.Models;

namespace BookReader.Api.Services;

/// <summary>
/// Runs book parsing and AI analysis away from the request.
///
/// A novel takes a dozen-plus model calls to cast and script, which is minutes,
/// not milliseconds. Doing that inside the upload request would hold a
/// connection open long past any sensible proxy timeout and lose the work if
/// the user closed the tab. The upload returns immediately with status
/// "uploaded"; the client polls until "ready".
///
/// The queue is in-memory and deliberately unbounded in duration but bounded in
/// concurrency: one book at a time, so a burst of uploads cannot fan out into
/// dozens of simultaneous AI calls and an unbounded bill.
/// </summary>
public class BookProcessingQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ValueTask EnqueueAsync(Guid bookId) => _channel.Writer.WriteAsync(bookId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

public class BookProcessingService : BackgroundService
{
    private readonly BookProcessingQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<BookProcessingService> _logger;

    public BookProcessingService(
        BookProcessingQueue queue,
        IServiceScopeFactory scopes,
        ILogger<BookProcessingService> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var bookId in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(bookId, stoppingToken);
            }
            catch (Exception ex)
            {
                // One bad book must never take down the worker, or every later
                // upload silently stops being processed.
                _logger.LogError(ex, "Book {BookId} failed to process", bookId);

                // The parser raises InvalidOperationException for the cases it can
                // actually explain — a DRM-protected Kindle file, an unsupported
                // compression scheme, images with no AI configured. Those messages
                // are written for the reader and tell them what to do next, so they
                // are shown as-is. Anything else is a genuine surprise and gets the
                // generic line rather than leaking an exception at someone.
                await MarkFailedAsync(bookId, ex is InvalidOperationException
                    ? ex.Message
                    : "Something went wrong while preparing this book.");
            }
        }
    }

    /// <summary>
    /// A figure caption: "FIGURE 1-47 ...", "Figure 3.2 ...", "Fig. 5 ...".
    /// Anchored to the start because a mid-sentence reference ("see Figure
    /// 1-48") is a cross-reference, not the figure itself.
    /// </summary>
    private static readonly Regex FigureCaption = new(
        // Must match BookParserService.CaptionStart: a caption carries a title,
        // which is what separates it from a wrapped cross-reference.
        @"^\s*(FIGURE|Figure|FIG\.|Fig\.|TABLE|Table)\s*\d+([.\-]\d+)?\s+\p{L}[\p{L}\s]{3,}",
        RegexOptions.Compiled);

    private async Task ProcessAsync(Guid bookId, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PromptDbContext>();
        var parser = scope.ServiceProvider.GetRequiredService<BookParserService>();
        var analysis = scope.ServiceProvider.GetRequiredService<BookAnalysisService>();
        var extraction = scope.ServiceProvider.GetRequiredService<BookExtractionService>();
        var storage = scope.ServiceProvider.GetRequiredService<BookStorageService>();
        var cache = scope.ServiceProvider.GetRequiredService<IMemoryCache>();

        var book = await db.ReaderBooks.FirstOrDefaultAsync(b => b.Id == bookId, ct);
        if (book is null || string.IsNullOrEmpty(book.StorageKey)) return;

        // ── Parse ────────────────────────────────────────────────────────────
        book.Status = "parsing";
        book.StatusMessage = "Reading the file…";
        await db.SaveChangesAsync(ct);

        string text;
        var figuresByPage = new Dictionary<int, List<string>>();

        await using (var stream = await storage.OpenAsync(book.StorageKey, ct))
        {
            if (stream is null)
            {
                await MarkFailedAsync(bookId, "The uploaded file is no longer available.");
                return;
            }
            text = await parser.ExtractTextAsync(stream, book.StorageKey, ct);

            // Diagrams are most of the meaning in a technical book, and text
            // extraction drops all of them: the reader was showing
            // "FIGURE 1-47 ..." as a bare caption with nothing beside it.
            if (book.StorageKey.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && stream.CanSeek)
            {
                stream.Position = 0;
                foreach (var img in parser.ExtractPdfImages(stream))
                {
                    var key = await storage.SaveFigureAsync(
                        bookId, img.PageNumber, img.Index, img.Bytes, img.ContentType, ct);

                    if (!figuresByPage.TryGetValue(img.PageNumber, out var list))
                        figuresByPage[img.PageNumber] = list = new List<string>();
                    list.Add(key);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            await MarkFailedAsync(bookId,
                "No text could be read from this file. Scanned or image-only books need OCR first.");
            return;
        }

        // No pre-scrubbing here. Front matter, contents pages, running
        // headers and DRM watermarks used to be stripped with keyword lists
        // and regexes, which never held: "from the library of" is a
        // watermark in one book and a sentence in another, and every new
        // book brought a new exception. The structuring pass in
        // BookAnalysisService asks the model what each block IS and drops
        // the apparatus, so the raw text goes through untouched.

        book.WordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        // The text itself goes to blob storage, not a database column.
        await storage.SaveTextAsync(bookId, text, ct);

        // ── Classify ─────────────────────────────────────────────────────────
        // One cheap call that decides everything downstream: whether to cast
        // characters, and which module (if any) this book's content belongs to.
        book.Status = "analysing";
        book.StatusMessage = "Working out what kind of book this is…";
        await db.SaveChangesAsync(ct);

        var (genre, confidence) = await extraction.ClassifyAsync(text, ct);
        book.Genre = genre;
        book.GenreConfidence = confidence;

        // ── Analyse ──────────────────────────────────────────────────────────
        book.StatusMessage = genre == BookGenres.Fiction
            ? "Finding characters and voices…"
            : "Preparing it for reading…";
        await db.SaveChangesAsync(ct);

        var result = await analysis.AnalyseAsync(bookId, text, genre, ct);

        // Pair each figure caption with the pictures printed on its page.
        //
        // Matching on the caption line is deliberate: a page can hold body
        // text and a diagram, and hanging the image off every line of that
        // page would repeat it. "FIGURE 1-47 ..." is where a reader expects
        // to see it, so that is the only line that gets it.
        if (figuresByPage.Count > 0)
        {
            var used = new HashSet<string>();
            foreach (var line in result.Lines)
            {
                if (line.Page == 0) continue;
                // The model tagged this during structuring; no pattern needed.
                if (!string.Equals(line.Kind, "caption", StringComparison.OrdinalIgnoreCase)) continue;
                if (!figuresByPage.TryGetValue(line.Page, out var keys)) continue;

                // A page with several figures gives each caption the next
                // unused one, in order, rather than all of them to the first.
                var next = keys.FirstOrDefault(k => !used.Contains(k));
                if (next is null) continue;

                used.Add(next);
                line.Figures = new List<string> { next };
            }

            _logger.LogInformation(
                "Attached {Attached} of {Total} figures to captions",
                used.Count, figuresByPage.Sum(p => p.Value.Count));
        }


        // The script — tens of thousands of lines for a novel — is written as
        // one blob. Overwriting it means reprocessing can never double a book.
        await storage.SaveAnalysisAsync(
            bookId,
            JsonSerializer.Serialize(new BookAnalysisBlob { Lines = result.Lines }),
            ct);

        // Only the cast goes to the database: small, queried, and edited.
        db.BookCharacters.RemoveRange(db.BookCharacters.Where(c => c.BookId == bookId));
        await db.SaveChangesAsync(ct);
        db.BookCharacters.AddRange(result.Characters);

        // Drop the cached script: it holds the PREVIOUS structuring, and a
        // reader that keeps serving it makes a finished re-analysis look
        // like it never ran.
        cache.Remove($"book-script:{bookId}");

        book.HasAnalysis = true;
        book.LineCount = result.Lines.Count;
        book.Status = "ready";
        book.StatusMessage = null;
        book.ProcessedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Book {BookId} ready: {Chars} characters, {Lines} lines",
            bookId, result.Characters.Count, result.Lines.Count);
    }

    private async Task MarkFailedAsync(Guid bookId, string message)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PromptDbContext>();
            var book = await db.ReaderBooks.FirstOrDefaultAsync(b => b.Id == bookId);
            if (book is null) return;
            book.Status = "failed";
            book.StatusMessage = message;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record failure for book {BookId}", bookId);
        }
    }
}
