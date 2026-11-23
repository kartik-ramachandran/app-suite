using System.Security.Cryptography;
using System.Text;

namespace Research.Api.Middleware;

/// <summary>
/// Protects public/anonymous API endpoints from being called by unauthorized frontends.
/// Validates a backend-issued CSRF token and the request origin.
/// Authenticated (JWT) requests bypass this check since the user already proved identity.
/// </summary>
public class FrontendGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<FrontendGuardMiddleware> _logger;
    private readonly string _csrfSecret;
    private readonly HashSet<string> _allowedOrigins;
    private const int TokenMaxAgeSeconds = 24 * 60 * 60; // 24 hours

    public FrontendGuardMiddleware(
        RequestDelegate next,
        ILogger<FrontendGuardMiddleware> logger,
        IConfiguration configuration)
    {
        _next = next;
        _logger = logger;
        _csrfSecret = (configuration["Jwt:Key"] ?? "") + "-csrf";
        _allowedOrigins = configuration.GetSection("FrontendGuard:AllowedOrigins")
            .Get<string[]>()
            ?.Select(o => o.TrimEnd('/').ToLowerInvariant())
            .ToHashSet() ?? new HashSet<string>();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only guard /api/* routes
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        // Skip if the request already has a valid JWT (authenticated users)
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            await _next(context);
            return;
        }

        // Skip for /api/external/* (handled by ApiKeyAuthMiddleware)
        if (context.Request.Path.StartsWithSegments("/api/external"))
        {
            await _next(context);
            return;
        }

        // Skip for auth endpoints (login, signup, csrf-token) so users can sign in
        if (context.Request.Path.StartsWithSegments("/api/auth"))
        {
            await _next(context);
            return;
        }

        // Skip for admin endpoints (admin has its own JWT-based auth)
        if (context.Request.Path.StartsWithSegments("/api/admin"))
        {
            await _next(context);
            return;
        }

        // Skip for our own meeting bot posting a call it was sent to. It has no
        // session and no CSRF token; it authenticates with a token scoped to a
        // single meeting, checked in MeetingBotIngestController.
        if (context.Request.Path.StartsWithSegments("/api/meetings/bot"))
        {
            await _next(context);
            return;
        }

        // Skip for the public API surface a business evaluates before it has
        // any session: service health and self-serve trial keys. These are
        // meant to be called from curl, a monitoring probe, or a developer's
        // terminal — none of which can present a browser CSRF token.
        //
        // Each carries its own abuse controls (per-IP and per-email limits on
        // trial issuance; status is read-only and exposes no internals), so the
        // guard would add nothing here except a wall in front of the funnel.
        if (context.Request.Path.StartsWithSegments("/api/trial")
            || context.Request.Path.StartsWithSegments("/api/status"))
        {
            await _next(context);
            return;
        }

        // Skip for the anonymous free PDF tools. These are deliberately usable
        // without an account or a session — that is the whole point of the
        // public tool pages — so there is no CSRF token to present.
        //
        // Safe because this route has no ambient authority to abuse: it reads
        // no user data, writes nothing, and is protected by its own per-IP
        // quota, size, page and concurrency limits (AnonymousPdfPolicy) rather
        // than by session origin. CSRF protects against a third-party site
        // acting AS a logged-in user; there is no user here to act as.
        if (context.Request.Path.StartsWithSegments("/api/free-pdf"))
        {
            await _next(context);
            return;
        }

        // Skip for the EMMD public seal key — it is meant to be fetched by anyone
        // (like a JWKS endpoint) so third parties can verify a sealed document.
        if (context.Request.Path.StartsWithSegments("/api/emmd/public-key"))
        {
            await _next(context);
            return;
        }

        // Guard is disabled if no JWT key is configured (dev without setup)
        if (string.IsNullOrEmpty(_csrfSecret) || _csrfSecret == "-csrf")
        {
            await _next(context);
            return;
        }

        // Validate X-CSRF-Token header
        if (!context.Request.Headers.TryGetValue("X-CSRF-Token", out var csrfHeader)
            || !ValidateCsrfToken(csrfHeader.ToString()))
        {
            _logger.LogWarning(
                "Blocked request to {Path} - missing or invalid CSRF token from {IP}",
                context.Request.Path, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Forbidden" });
            return;
        }

        // Validate Origin or Referer header (browser requests always send one)
        var origin = context.Request.Headers.Origin.FirstOrDefault()
                  ?? context.Request.Headers.Referer.FirstOrDefault();

        if (!string.IsNullOrEmpty(origin) && _allowedOrigins.Count > 0)
        {
            // Normalize: extract origin from a full referer URL
            if (Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            {
                origin = $"{uri.Scheme}://{uri.Host}" +
                         (uri.IsDefaultPort ? "" : $":{uri.Port}");
            }

            if (!_allowedOrigins.Contains(origin.ToLowerInvariant()))
            {
                _logger.LogWarning(
                    "Blocked request to {Path} - disallowed origin {Origin} from {IP}",
                    context.Request.Path, origin, context.Connection.RemoteIpAddress);
                context.Response.StatusCode = 403;
                await context.Response.WriteAsJsonAsync(new { error = "Forbidden" });
                return;
            }
        }

        await _next(context);
    }

    private bool ValidateCsrfToken(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 2) return false;

            if (!long.TryParse(parts[0], out var timestamp)) return false;

            // Check token age
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now - timestamp > TokenMaxAgeSeconds) return false;

            // Verify HMAC signature
            var payload = parts[0];
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_csrfSecret));
            var expectedHash = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));

            return string.Equals(parts[1], expectedHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}
