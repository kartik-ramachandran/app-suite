using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MoneyManager.Api.Data;
using MoneyManager.Api.Models;

namespace MoneyManager.Api.Services;

/// <summary>
/// Delivers job-completion webhooks so API customers don't have to poll.
/// Subscriptions live in the WebhookSubscriptions table (managed by
/// WebhooksController); when a ModuleJob for a subscribed module completes or
/// fails, the payload is POSTed to the subscriber's URL with an HMAC-SHA256
/// signature header. Delivery is fire-and-forget with one retry — a webhook
/// must never slow down or break the job itself.
/// </summary>
public class WebhookDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WebhookDispatcher> _logger;

    public WebhookDispatcher(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory, ILogger<WebhookDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public sealed record SubscriptionRow(int Id, string Url, string Secret, string? Modules);

    public void QueueJobEvent(ModuleJob job, string evt)
    {
        if (evt is not ("job.completed" or "job.failed")) return;

        // Snapshot now — the EF entity belongs to the request scope.
        var payload = new
        {
            @event = evt,
            jobId = job.Id,
            module = job.Module,
            jobType = job.JobType,
            title = job.Title,
            status = job.Status,
            result = job.ResultText,
            error = job.ErrorMessage,
            createdAt = job.CreatedAt,
            completedAt = job.CompletedAt,
            durationMs = job.DurationMs,
        };
        var userId = job.UserId;
        var module = job.Module;
        var body = JsonSerializer.Serialize(payload);

        _ = Task.Run(() => DispatchAsync(userId, module, evt, body));
    }

    private static bool _tableEnsured;

    private async Task DispatchAsync(string userId, string module, string evt, string body)
    {
        List<SubscriptionRow> subs;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<PromptDbContext>();
            if (!_tableEnsured)
            {
                // Create the table here too (not only in WebhooksController) so
                // job completions don't log query failures before anyone subscribes.
                var sqlite = db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false;
                var pk = sqlite ? "INTEGER PRIMARY KEY AUTOINCREMENT" : "SERIAL PRIMARY KEY";
                await db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""WebhookSubscriptions"" (
                    ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Url"" TEXT NOT NULL, ""Secret"" TEXT NOT NULL,
                    ""Modules"" TEXT, ""Active"" INTEGER NOT NULL DEFAULT 1, ""CreatedAt"" TEXT)");
                _tableEnsured = true;
            }
            subs = await db.Database
                .SqlQuery<SubscriptionRow>($@"SELECT ""Id"", ""Url"", ""Secret"", ""Modules"" FROM ""WebhookSubscriptions"" WHERE ""UserId""={userId} AND ""Active""=1")
                .ToListAsync();
        }
        catch
        {
            return; // table not available — nobody has subscribed
        }

        foreach (var sub in subs)
        {
            var modules = (sub.Modules ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (modules.Length > 0 && !modules.Contains(module, StringComparer.OrdinalIgnoreCase)) continue;

            await SendAsync(sub, evt, body);
        }
    }

    public async Task<(bool ok, int status)> SendAsync(SubscriptionRow sub, string evt, string body)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var client = _httpFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);

                using var req = new HttpRequestMessage(HttpMethod.Post, sub.Url);
                var t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                req.Headers.Add("X-GenPrompt-Event", evt);
                req.Headers.Add("X-GenPrompt-Signature", $"t={t},v1={Sign(sub.Secret, $"{t}.{body}")}");
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var res = await client.SendAsync(req);
                if (res.IsSuccessStatusCode) return (true, (int)res.StatusCode);

                _logger.LogWarning("Webhook {Id} → {Url} returned {Status} (attempt {Attempt})",
                    sub.Id, sub.Url, (int)res.StatusCode, attempt + 1);
                if (attempt == 1) return (false, (int)res.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Webhook {Id} → {Url} delivery failed (attempt {Attempt})", sub.Id, sub.Url, attempt + 1);
                if (attempt == 1) return (false, 0);
            }
            await Task.Delay(TimeSpan.FromSeconds(30));
        }
        return (false, 0);
    }

    private static string Sign(string secret, string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }
}
