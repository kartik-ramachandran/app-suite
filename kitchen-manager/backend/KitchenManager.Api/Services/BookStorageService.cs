using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace KitchenManager.Api.Services;

/// <summary>
/// Where book files and their AI analysis live.
///
/// Two reasons this is not the database:
///
///  1. Cost. A novel's extracted text and script run to megabytes. Postgres
///     charges for that storage at database rates, backs it up on every
///     snapshot, and drags it through every replication — to hold a blob that
///     is written once and read sequentially. Blob storage is roughly two
///     orders of magnitude cheaper for the same bytes.
///
///  2. Durability. The previous version wrote uploads to local disk under
///     ContentRootPath. On Azure Container Apps that filesystem is ephemeral:
///     every deploy, scale event, or restart wipes it. Books would have
///     silently disappeared from users' libraries in production while working
///     perfectly on a developer machine.
///
/// The database keeps only what it is good at: rows to list, search and
/// order — title, status, progress, the cast — plus pointers to the blobs.
///
/// With no connection string configured this falls back to local disk so a
/// developer machine needs no cloud account. The fallback logs a warning,
/// because silently using ephemeral storage in production is the exact bug
/// this class exists to fix.
/// </summary>
public class BookStorageService
{
    private readonly BlobContainerClient? _container;
    private readonly string _localRoot;
    private readonly bool _misconfigured;
    private readonly ILogger<BookStorageService> _logger;

    public BookStorageService(IConfiguration cfg, IWebHostEnvironment env, ILogger<BookStorageService> logger)
    {
        _logger = logger;
        _localRoot = Path.Combine(env.ContentRootPath, "uploads", "books");

        var connectionString = cfg["BookStorage:ConnectionString"];
        var containerName = cfg["BookStorage:Container"] ?? "books";

        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                _container = new BlobContainerClient(connectionString, containerName);
                // Private by default: a book someone uploaded is theirs, and a
                // guessable public URL would hand it to anyone.
                _container.CreateIfNotExists(PublicAccessType.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Blob storage is configured but unreachable; falling back to local disk");
                _container = null;
            }
        }
        else
        {
            // In production, local disk means data loss: the container
            // filesystem is wiped on every deploy, restart and scale event, so
            // books would disappear from users' libraries with no error raised
            // anywhere.
            //
            // This does NOT throw at startup — that would take the whole app
            // down over one module. Instead the book reader refuses uploads
            // (see IsAvailable) so the failure is visible and contained, and
            // the other 30-odd modules keep working.
            _misconfigured = env.IsProduction();

            if (_misconfigured)
            {
                _logger.LogError(
                    "BookStorage:ConnectionString is not configured. The book reader is DISABLED in this " +
                    "environment because the container filesystem is ephemeral and uploaded books would be " +
                    "lost on the next deploy. Set BookStorage:ConnectionString to an Azure Blob connection string.");
            }
            else
            {
                _logger.LogWarning(
                    "BookStorage:ConnectionString is not set — book files will be written to local disk. " +
                    "Fine for development; on a container host this storage is ephemeral.");
            }
        }
    }

    public bool UsingBlobStorage => _container is not null;

    /// <summary>
    /// Whether the book reader can safely accept uploads. False only when
    /// production has no blob storage configured — in that state accepting a
    /// book would be promising to keep something we are about to lose.
    /// </summary>
    public bool IsAvailable => !_misconfigured;

    public string UnavailableReason =>
        "Book storage isn't configured on this server yet. Uploads are turned off so nothing is lost.";

    // ── Write ─────────────────────────────────────────────────────────────────

    public async Task<string> SaveAsync(Guid bookId, string extension, Stream content, CancellationToken ct = default)
    {
        var key = $"{bookId}/source{extension}";

        if (_container is not null)
        {
            await _container.GetBlobClient(key).UploadAsync(content, overwrite: true, ct);
            return key;
        }

        var path = Path.Combine(_localRoot, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
        return key;
    }

    /// <summary>
    /// Stores the extracted text and the AI-generated script.
    ///
    /// This is the payload that would otherwise sit in two large Postgres text
    /// columns being backed up nightly forever. The reader fetches it once per
    /// session and pages through it client-side.
    /// </summary>
    public Task SaveAnalysisAsync(Guid bookId, string json, CancellationToken ct = default) =>
        WriteTextAsync($"{bookId}/analysis.json", json, ct);

    public Task SaveTextAsync(Guid bookId, string text, CancellationToken ct = default) =>
        WriteTextAsync($"{bookId}/text.txt", text, ct);

    private async Task WriteTextAsync(string key, string content, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);

        if (_container is not null)
        {
            using var ms = new MemoryStream(bytes);
            await _container.GetBlobClient(key).UploadAsync(ms, overwrite: true, ct);
            return;
        }

        var path = Path.Combine(_localRoot, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
    }

    // ── Read ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores one figure lifted out of a PDF and returns its key.
    ///
    /// Figures live beside the book's text and analysis rather than in the
    /// database: they are binary, they are never queried, and a technical
    /// book can carry a couple of hundred of them.
    /// </summary>
    public async Task<string> SaveFigureAsync(
        Guid bookId, int page, int index, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        var ext = contentType == "image/jpeg" ? "jpg" : "png";
        var key = $"{bookId}/figures/p{page}-{index}.{ext}";

        if (_container is not null)
        {
            var blob = _container.GetBlobClient(key);
            using var ms = new MemoryStream(bytes);
            await blob.UploadAsync(ms, new BlobHttpHeaders { ContentType = contentType }, cancellationToken: ct);
        }
        else
        {
            var path = Path.Combine(_localRoot, key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, ct);
        }

        return key;
    }

    public async Task<Stream?> OpenAsync(string key, CancellationToken ct = default)
    {
        if (_container is not null)
        {
            var blob = _container.GetBlobClient(key);
            if (!await blob.ExistsAsync(ct)) return null;
            return (await blob.DownloadStreamingAsync(cancellationToken: ct)).Value.Content;
        }

        var path = Path.Combine(_localRoot, key.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    public async Task<string?> ReadTextAsync(string key, CancellationToken ct = default)
    {
        await using var stream = await OpenAsync(key, ct);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }

    public Task<string?> ReadAnalysisAsync(Guid bookId, CancellationToken ct = default) =>
        ReadTextAsync($"{bookId}/analysis.json", ct);

    // ── Delete ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes every blob for a book. Deleting the row alone would leave the
    /// source file and analysis paid for forever with nothing pointing at them.
    /// </summary>
    public async Task DeleteBookAsync(Guid bookId, CancellationToken ct = default)
    {
        if (_container is not null)
        {
            await foreach (var blob in _container.GetBlobsAsync(prefix: $"{bookId}/", cancellationToken: ct))
            {
                try { await _container.DeleteBlobIfExistsAsync(blob.Name, cancellationToken: ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not delete blob {Name}", blob.Name); }
            }
            return;
        }

        var dir = Path.Combine(_localRoot, bookId.ToString());
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not delete local files for book {BookId}", bookId); }
        }
    }
}
