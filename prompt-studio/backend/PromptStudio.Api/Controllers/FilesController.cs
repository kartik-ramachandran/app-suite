using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Models;
using System.Security.Claims;
using System.Text;
using UglyToad.PdfPig;

namespace PromptStudio.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("general")]
public class FilesController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly ILogger<FilesController> _logger;
    private readonly IWebHostEnvironment _env;

    private static readonly string[] AllowedExtensions = { ".pdf", ".doc", ".docx", ".txt", ".md", ".csv", ".json", ".xml", ".xlsx", ".xls", ".pptx", ".ppt", ".rtf" };
    private const long MaxFileSize = 10 * 1024 * 1024; // 10MB

    public FilesController(PromptDbContext context, ILogger<FilesController> logger, IWebHostEnvironment env)
    {
        _context = context;
        _logger = logger;
        _env = env;
    }

    private string GetCurrentUserId()
    {
        return User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
    }

    private string GetUploadsPath()
    {
        var path = Path.Combine(_env.ContentRootPath, "uploads");
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Upload files for a persona, prompt, or Cosmos knowledge node.</summary>
    [HttpPost("upload")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult> Upload([FromForm] List<IFormFile> files, [FromForm] string entityType, [FromForm] string entityId)
    {
        var userId = GetCurrentUserId();
        if (string.IsNullOrEmpty(entityType) || string.IsNullOrEmpty(entityId))
            return BadRequest(new { error = "entityType and entityId are required" });

        // "vault" is the user's own saved-files area (e.g. "keep this file"
        // after a free PDF conversion). It has no owning entity to validate —
        // the user IS the scope — so it needs no existence check below.
        if (entityType != "persona" && entityType != "prompt" && entityType != "knowledge" && entityType != "vault")
            return BadRequest(new { error = "entityType must be 'persona', 'prompt', 'knowledge', or 'vault'" });

        if (files == null || files.Count == 0)
            return BadRequest(new { error = "No files provided" });

        // A Cosmos attachment must point at a node owned by the current user.
        if (entityType == "knowledge")
        {
            if (!Guid.TryParse(entityId, out var nodeId) ||
                !await _context.KnowledgeNodes.AnyAsync(n => n.Id == nodeId && n.UserId == userId))
                return NotFound(new { error = "Cosmos note not found" });
        }

        // Limit to 5 files per entity
        var existingCount = await _context.FileAttachments.CountAsync(f => f.EntityType == entityType && f.EntityId == entityId && f.UserId == userId);
        if (existingCount + files.Count > 10)
            return BadRequest(new { error = "Maximum 10 files per item" });

        var uploaded = new List<object>();
        var uploadsPath = GetUploadsPath();

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            // Persona/prompt context files retain their extraction-oriented
            // allowlist. Cosmos can safely store any type because uploads live
            // outside wwwroot and are only returned as downloads.
            if (entityType != "knowledge" && !AllowedExtensions.Contains(ext))
                return BadRequest(new { error = $"File type '{ext}' not allowed. Allowed: {string.Join(", ", AllowedExtensions)}" });

            if (file.Length > MaxFileSize)
                return BadRequest(new { error = $"File '{file.FileName}' exceeds 10MB limit" });

            // Do not let a hostile or unusually long extension influence the
            // storage path. The original display name is preserved in the DB.
            var safeExt = ext.Length is > 1 and <= 20 &&
                          ext.Skip(1).All(char.IsLetterOrDigit)
                ? ext
                : ".bin";
            var storedName = $"{Guid.NewGuid()}{safeExt}";
            var filePath = Path.Combine(uploadsPath, storedName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var attachment = new FileAttachment
            {
                FileName = file.FileName,
                StoredFileName = storedName,
                ContentType = string.IsNullOrWhiteSpace(file.ContentType)
                    ? "application/octet-stream"
                    : file.ContentType,
                FileSize = file.Length,
                Source = "upload",
                EntityType = entityType,
                EntityId = entityId,
                UserId = userId,
                CreatedAt = DateTime.UtcNow
            };

            _context.FileAttachments.Add(attachment);
            await _context.SaveChangesAsync();

            uploaded.Add(new
            {
                attachment.Id,
                attachment.FileName,
                attachment.ContentType,
                attachment.FileSize,
                attachment.Source,
                attachment.CreatedAt
            });
        }

        return Ok(new { files = uploaded });
    }

    /// <summary>List files for an entity</summary>
    [HttpGet]
    public async Task<ActionResult> GetFiles([FromQuery] string entityType, [FromQuery] string entityId)
    {
        var userId = GetCurrentUserId();
        var files = await _context.FileAttachments
            .Where(f => f.EntityType == entityType && f.EntityId == entityId && f.UserId == userId)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new
            {
                f.Id,
                f.FileName,
                f.ContentType,
                f.FileSize,
                f.Source,
                f.GoogleDriveFileId,
                f.CreatedAt
            })
            .ToListAsync();

        return Ok(files);
    }

    /// <summary>Download a file</summary>
    [HttpGet("{id}/download")]
    public async Task<ActionResult> DownloadFile(int id)
    {
        var userId = GetCurrentUserId();
        var file = await _context.FileAttachments.FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId);
        if (file == null) return NotFound();

        var filePath = Path.Combine(GetUploadsPath(), file.StoredFileName);
        if (!System.IO.File.Exists(filePath))
            return NotFound(new { error = "File not found on disk" });

        var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        return File(stream, file.ContentType, file.FileName);
    }

    /// <summary>Delete a file</summary>
    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteFile(int id)
    {
        var userId = GetCurrentUserId();
        var file = await _context.FileAttachments.FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId);
        if (file == null) return NotFound();

        // Delete from disk
        var filePath = Path.Combine(GetUploadsPath(), file.StoredFileName);
        if (System.IO.File.Exists(filePath))
            System.IO.File.Delete(filePath);

        _context.FileAttachments.Remove(file);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Extract plain text from an uploaded file for use as AI agent context</summary>
    [HttpPost("extract-text")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult> ExtractText(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No file provided" });

        if (file.Length > MaxFileSize)
            return BadRequest(new { error = "File exceeds 10MB limit" });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        try
        {
            string text;
            if (ext == ".pdf")
            {
                using var stream = file.OpenReadStream();
                using var pdf = PdfDocument.Open(stream);
                var sb = new StringBuilder();
                foreach (var page in pdf.GetPages())
                    sb.AppendLine(string.Join(" ", page.GetWords().Select(w => w.Text)));
                text = sb.ToString();
            }
            else if (ext == ".docx")
            {
                using var stream = file.OpenReadStream();
                using var doc = WordprocessingDocument.Open(stream, false);
                text = doc.MainDocumentPart?.Document?.Body?.InnerText ?? string.Empty;
            }
            else
            {
                using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                text = await reader.ReadToEndAsync();
            }

            return Ok(new { text = text.Trim(), fileName = file.FileName });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Text extraction failed for {FileName}", file.FileName);
            return StatusCode(500, new { error = $"Failed to extract text from {file.FileName}" });
        }
    }

    /// <summary>Import a file from Google Drive</summary>
    [HttpPost("import-google-drive")]
    public async Task<ActionResult> ImportFromGoogleDrive([FromBody] GoogleDriveImportRequest request)
    {
        var userId = GetCurrentUserId();

        if (string.IsNullOrEmpty(request.EntityType) || string.IsNullOrEmpty(request.EntityId))
            return BadRequest(new { error = "entityType and entityId are required" });

        // Check limit
        var existingCount = await _context.FileAttachments.CountAsync(f => f.EntityType == request.EntityType && f.EntityId == request.EntityId && f.UserId == userId);
        if (existingCount + request.Files.Count > 10)
            return BadRequest(new { error = "Maximum 10 files per item" });

        var uploaded = new List<object>();

        foreach (var driveFile in request.Files)
        {
            // Download from Google Drive using the access token
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.AccessToken);

            var downloadUrl = $"https://www.googleapis.com/drive/v3/files/{driveFile.Id}?alt=media";
            var response = await httpClient.GetAsync(downloadUrl);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to download Google Drive file {FileId}: {Status}", driveFile.Id, response.StatusCode);
                continue;
            }

            var ext = Path.GetExtension(driveFile.Name).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = ".bin";

            var storedName = $"{Guid.NewGuid()}{ext}";
            var filePath = Path.Combine(GetUploadsPath(), storedName);

            using (var fileStream = new FileStream(filePath, FileMode.Create))
            {
                await response.Content.CopyToAsync(fileStream);
            }

            var fileInfo = new FileInfo(filePath);

            var attachment = new FileAttachment
            {
                FileName = driveFile.Name,
                StoredFileName = storedName,
                ContentType = driveFile.MimeType ?? "application/octet-stream",
                FileSize = fileInfo.Length,
                Source = "google-drive",
                GoogleDriveFileId = driveFile.Id,
                EntityType = request.EntityType,
                EntityId = request.EntityId,
                UserId = userId,
                CreatedAt = DateTime.UtcNow
            };

            _context.FileAttachments.Add(attachment);
            await _context.SaveChangesAsync();

            uploaded.Add(new
            {
                attachment.Id,
                attachment.FileName,
                attachment.ContentType,
                attachment.FileSize,
                attachment.Source,
                attachment.GoogleDriveFileId,
                attachment.CreatedAt
            });
        }

        return Ok(new { files = uploaded });
    }
}

public class GoogleDriveImportRequest
{
    public string AccessToken { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public List<GoogleDriveFileInfo> Files { get; set; } = new();
}

public class GoogleDriveFileInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? MimeType { get; set; }
}
