using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Prism.Api.Data;
using Prism.Api.Services;

namespace Prism.Api.Controllers;

/// <summary>
/// Webhook subscriptions: get a POST to your own endpoint whenever one of your
/// Signal or Prism jobs finishes, instead of polling the job endpoints.
/// Payloads are signed with HMAC-SHA256 (X-GenPrompt-Signature: t=...,v1=...;
/// sign "{t}.{rawBody}" with your webhook secret to verify).
/// </summary>
[ApiController]
[Route("api/webhooks")]
[Authorize]
public class WebhooksController : ControllerBase
{
    private const string DefaultModules = "prism,interaction-intelligence";

    private readonly PromptDbContext _db;
    private readonly WebhookDispatcher _dispatcher;
    private readonly ILogger<WebhooksController> _logger;

    public WebhooksController(PromptDbContext db, WebhookDispatcher dispatcher, ILogger<WebhooksController> logger)
    { _db = db; _dispatcher = dispatcher; _logger = logger; }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    private static bool _ensured;
    private async Task EnsureAsync()
    {
        if (_ensured) return;
        try
        {
            var sqlite = _db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
            var pk = sqlite ? "INTEGER PRIMARY KEY AUTOINCREMENT" : "SERIAL PRIMARY KEY";
            await _db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""WebhookSubscriptions"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Url"" TEXT NOT NULL, ""Secret"" TEXT NOT NULL,
                ""Modules"" TEXT, ""Active"" INTEGER NOT NULL DEFAULT 1, ""CreatedAt"" TEXT)");
            await _db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_WebhookSubscriptions_UserId"" ON ""WebhookSubscriptions"" (""UserId"")");
            _ensured = true;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "EnsureWebhookSubscriptions failed"); }
    }

    private sealed record WebhookRow(int Id, string Url, string Secret, string? Modules, int Active, string? CreatedAt);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        await EnsureAsync();
        var rows = await _db.Database.SqlQuery<WebhookRow>($@"
            SELECT ""Id"", ""Url"", ""Secret"", ""Modules"", ""Active"", ""CreatedAt""
            FROM ""WebhookSubscriptions"" WHERE ""UserId""={UserId} ORDER BY ""Id""").ToListAsync();
        return Ok(rows);
    }

    public class CreateWebhookRequest
    {
        public string? Url { get; set; }
        public string? Modules { get; set; }   // csv, e.g. "prism" — empty = prism + interaction-intelligence
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWebhookRequest req)
    {
        await EnsureAsync();

        if (!Uri.TryCreate(req?.Url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            return BadRequest(new { error = "Provide an absolute https:// URL (http is allowed for localhost only)." });

        var count = await _db.Database.SqlQuery<int>($@"SELECT COUNT(*) AS ""Value"" FROM ""WebhookSubscriptions"" WHERE ""UserId""={UserId}").FirstAsync();
        if (count >= 5) return BadRequest(new { error = "You can have at most 5 webhook endpoints — delete one first." });

        var modules = string.IsNullOrWhiteSpace(req!.Modules) ? DefaultModules : req.Modules.Trim();
        var secret = "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var now = DateTime.UtcNow.ToString("o");

        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""WebhookSubscriptions"" (""UserId"", ""Url"", ""Secret"", ""Modules"", ""Active"", ""CreatedAt"")
            VALUES ({UserId}, {uri.ToString()}, {secret}, {modules}, 1, {now})");

        var row = await _db.Database.SqlQuery<WebhookRow>($@"
            SELECT ""Id"", ""Url"", ""Secret"", ""Modules"", ""Active"", ""CreatedAt""
            FROM ""WebhookSubscriptions"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC").FirstAsync();
        return Ok(row);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        await EnsureAsync();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""WebhookSubscriptions"" WHERE ""Id""={id} AND ""UserId""={UserId}");
        return NoContent();
    }

    /// <summary>Send a signed test event to the endpoint so integrators can verify their handler.</summary>
    [HttpPost("{id:int}/test")]
    public async Task<IActionResult> Test(int id)
    {
        await EnsureAsync();
        var row = await _db.Database.SqlQuery<WebhookRow>($@"
            SELECT ""Id"", ""Url"", ""Secret"", ""Modules"", ""Active"", ""CreatedAt""
            FROM ""WebhookSubscriptions"" WHERE ""Id""={id} AND ""UserId""={UserId}").FirstOrDefaultAsync();
        if (row == null) return NotFound(new { error = "Webhook not found." });

        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            @event = "webhook.test",
            message = "GenPrompt webhook test — your endpoint and signature verification are working.",
            sentAt = DateTime.UtcNow,
        });
        var (ok, status) = await _dispatcher.SendAsync(
            new WebhookDispatcher.SubscriptionRow(row.Id, row.Url, row.Secret, row.Modules), "webhook.test", body);
        return Ok(new { delivered = ok, status });
    }
}
