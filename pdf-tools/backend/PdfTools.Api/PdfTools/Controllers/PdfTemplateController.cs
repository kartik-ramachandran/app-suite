using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PdfAI.API.Services;
using PdfTools.Api.Data;

namespace PdfAI.API.Controllers;

/// <summary>
/// Snappdf document generation: store reusable HTML templates with
/// {{variable}} placeholders and render them to PDF from JSON data — the
/// invoice/contract/certificate-at-scale workflow. Template syntax:
///   {{path.to.value}}   — inserted HTML-escaped
///   {{{path.to.value}}} — inserted raw (trusted HTML)
///   {{#each items}} … {{this}} / {{field}} / {{@index}} … {{/each}}
/// Falls under /api/pdf, so org API keys are metered exactly like conversions.
/// </summary>
[Authorize]
[ApiController]
[Route("api/pdf/templates")]
public class PdfTemplateController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly PdfProcessingService _pdfService;
    private readonly ILogger<PdfTemplateController> _logger;

    public PdfTemplateController(PromptDbContext db, PdfProcessingService pdfService, ILogger<PdfTemplateController> logger)
    {
        _db = db;
        _pdfService = pdfService;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    private const int MaxTemplateBytes = 512_000;
    private const int MaxDataBytes = 256_000;

    private static bool _ensured;
    private async Task EnsureAsync()
    {
        if (_ensured) return;
        try
        {
            var sqlite = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
            var pk = sqlite ? "INTEGER PRIMARY KEY AUTOINCREMENT" : "SERIAL PRIMARY KEY";
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""PdfTemplates"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Name"" TEXT NOT NULL,
                ""Description"" TEXT, ""Html"" TEXT NOT NULL, ""SampleDataJson"" TEXT,
                ""CreatedAt"" TEXT, ""UpdatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_PdfTemplates_UserId"" ON ""PdfTemplates"" (""UserId"", ""Id"")");
            _ensured = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EnsurePdfTemplates failed"); }
    }

    // ── CRUD ─────────────────────────────────────────────────────────────────

    private sealed record TemplateListRow(int Id, string Name, string? Description, string? UpdatedAt);
    private sealed record TemplateFullRow(int Id, string Name, string? Description, string Html, string? SampleDataJson, string? UpdatedAt);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<TemplateListRow>($@"
            SELECT ""Id"", ""Name"", ""Description"", ""UpdatedAt"" FROM ""PdfTemplates""
            WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 100").ToListAsync();
        return Ok(rows);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        await EnsureAsync();
        var row = await _db.Database.SqlQuery<TemplateFullRow>($@"
            SELECT ""Id"", ""Name"", ""Description"", ""Html"", ""SampleDataJson"", ""UpdatedAt""
            FROM ""PdfTemplates"" WHERE ""Id""={id} AND ""UserId""={UserId}").FirstOrDefaultAsync();
        if (row == null) return NotFound(new { error = "Template not found." });
        return Ok(row);
    }

    public class SaveTemplateRequest
    {
        public int? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Html { get; set; }
        public string? SampleDataJson { get; set; }
    }

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] SaveTemplateRequest req)
    {
        var html = req?.Html;
        if (string.IsNullOrWhiteSpace(html)) return BadRequest(new { error = "Template HTML is required." });
        if (Encoding.UTF8.GetByteCount(html) > MaxTemplateBytes) return BadRequest(new { error = "Template is too large (max 500 KB)." });

        var name = (req!.Name ?? "").Trim();
        if (name.Length == 0) name = "Untitled template";
        if (name.Length > 120) name = name[..120];
        var description = req.Description?.Trim();
        if (description is { Length: > 400 }) description = description[..400];

        var sample = req.SampleDataJson;
        if (!string.IsNullOrWhiteSpace(sample))
        {
            if (Encoding.UTF8.GetByteCount(sample) > MaxDataBytes) return BadRequest(new { error = "Sample data is too large." });
            try { using var _ = JsonDocument.Parse(sample); }
            catch (JsonException) { return BadRequest(new { error = "Sample data must be valid JSON." }); }
        }

        await EnsureAsync();
        var now = DateTime.UtcNow.ToString("o");

        if (req.Id is int existingId)
        {
            var updated = await _db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE ""PdfTemplates"" SET ""Name""={name}, ""Description""={description},
                    ""Html""={html}, ""SampleDataJson""={sample}, ""UpdatedAt""={now}
                WHERE ""Id""={existingId} AND ""UserId""={UserId}");
            if (updated > 0) return Ok(new { id = existingId, savedAt = now });
        }

        var idRows = await _db.Database.SqlQuery<int>($@"
            INSERT INTO ""PdfTemplates"" (""UserId"", ""Name"", ""Description"", ""Html"", ""SampleDataJson"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({UserId}, {name}, {description}, {html}, {sample}, {now}, {now})
            RETURNING ""Id"" AS ""Value""").ToListAsync();
        return Ok(new { id = idRows[0], savedAt = now });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"DELETE FROM ""PdfTemplates"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return NoContent();
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    public class RenderRequest
    {
        /// <summary>JSON object whose values replace {{placeholders}}.</summary>
        public JsonElement? Data { get; set; }
        /// <summary>Inline-render only: template HTML supplied directly.</summary>
        public string? Html { get; set; }
        public string? FileName { get; set; }
    }

    [HttpPost("{id:int}/render")]
    public async Task<IActionResult> RenderTemplate(int id, [FromBody] RenderRequest req)
    {
        await EnsureAsync();
        var row = await _db.Database.SqlQuery<TemplateFullRow>($@"
            SELECT ""Id"", ""Name"", ""Description"", ""Html"", ""SampleDataJson"", ""UpdatedAt""
            FROM ""PdfTemplates"" WHERE ""Id""={id} AND ""UserId""={UserId}").FirstOrDefaultAsync();
        if (row == null) return NotFound(new { error = "Template not found." });

        return RenderHtml(row.Html, req?.Data, req?.FileName ?? Slug(row.Name));
    }

    /// <summary>One-shot generation without storing a template (API-first callers).</summary>
    [HttpPost("/api/pdf/generate")]
    public IActionResult Generate([FromBody] RenderRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Html)) return BadRequest(new { error = "Template HTML is required." });
        if (Encoding.UTF8.GetByteCount(req.Html) > MaxTemplateBytes) return BadRequest(new { error = "Template is too large (max 500 KB)." });
        return RenderHtml(req.Html, req.Data, req.FileName ?? "document");
    }

    private IActionResult RenderHtml(string templateHtml, JsonElement? data, string fileName)
    {
        try
        {
            var html = PdfTemplateEngine.Render(templateHtml, data);
            var pdf = _pdfService.ConvertHtmlToPdf(Encoding.UTF8.GetBytes(html));
            return File(pdf, "application/pdf", $"{Slug(fileName)}.pdf");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Template render failed");
            return BadRequest(new { error = "The document could not be generated — check the template and data." });
        }
    }

    private static string Slug(string s) =>
        Regex.Replace(s.Trim(), @"[^\w\-]+", "-").Trim('-') is { Length: > 0 } slug ? slug[..Math.Min(slug.Length, 60)] : "document";
}

/// <summary>Minimal, dependency-free {{placeholder}} engine (dot paths + #each blocks).</summary>
public static class PdfTemplateEngine
{
    private static readonly Regex EachRx = new(@"\{\{#each\s+([\w.@]+)\s*\}\}(.*?)\{\{/each\}\}", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RawRx = new(@"\{\{\{\s*([\w.@]+)\s*\}\}\}", RegexOptions.Compiled);
    private static readonly Regex EscRx = new(@"\{\{\s*([\w.@]+)\s*\}\}", RegexOptions.Compiled);

    public static string Render(string template, JsonElement? data)
    {
        var root = data ?? default;
        return RenderScope(template, root, null, 0);
    }

    private static string RenderScope(string template, JsonElement root, JsonElement? item, int index)
    {
        // Loops first, innermost values second — one level of #each nesting is
        // supported by re-entering RenderScope for each item.
        var withLoops = EachRx.Replace(template, m =>
        {
            var listPath = m.Groups[1].Value;
            var block = m.Groups[2].Value;
            var list = Resolve(root, item, index, listPath);
            if (list is not { ValueKind: JsonValueKind.Array }) return "";
            var sb = new StringBuilder();
            var i = 0;
            foreach (var element in list.Value.EnumerateArray())
            {
                sb.Append(RenderScope(block, root, element, i));
                i++;
            }
            return sb.ToString();
        });

        withLoops = RawRx.Replace(withLoops, m => Stringify(Resolve(root, item, index, m.Groups[1].Value)));
        return EscRx.Replace(withLoops, m => HtmlEscape(Stringify(Resolve(root, item, index, m.Groups[1].Value))));
    }

    private static JsonElement? Resolve(JsonElement root, JsonElement? item, int index, string path)
    {
        if (path == "@index")
        {
            using var doc = JsonDocument.Parse((index + 1).ToString());
            return doc.RootElement.Clone();
        }

        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);

        // "this" (or "this.x") resolves against the current #each item only.
        if (segments.Length > 0 && segments[0] == "this")
        {
            if (item == null) return null;
            return TryWalk(item.Value, segments.Skip(1).ToArray(), out var thisValue) ? thisValue : null;
        }

        // Inside a loop, item properties shadow root properties.
        if (item is { } itemScope && TryWalk(itemScope, segments, out var itemValue)) return itemValue;
        if (root.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        return TryWalk(root, segments, out var rootValue) ? rootValue : null;
    }

    private static bool TryWalk(JsonElement element, IReadOnlyList<string> segments, out JsonElement value)
    {
        value = element;
        foreach (var segment in segments)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(segment, out var next))
            {
                value = next;
            }
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var idx) && idx >= 0 && idx < value.GetArrayLength())
            {
                value = value[idx];
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    private static string Stringify(JsonElement? value) => value?.ValueKind switch
    {
        JsonValueKind.String => value.Value.GetString() ?? "",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.Value.GetRawText(),
        JsonValueKind.Object or JsonValueKind.Array => value.Value.GetRawText(),
        _ => "",
    };

    private static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
