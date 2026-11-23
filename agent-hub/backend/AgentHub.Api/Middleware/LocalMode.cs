using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using AgentHub.Api.Services;

namespace AgentHub.Api.Middleware;

/// <summary>
/// Local mode: no sign-in, for running this app on your own machine with your
/// own AI keys. One owner account is created on first use and the app opens
/// straight into it.
///
/// Turn on with LocalMode:Enabled = true (env LocalMode__Enabled=true).
///
/// The guardrail is enforced here in code, not left to configuration:
///   • The app refuses to START if local mode is on and it is listening on
///     anything other than a loopback address (localhost / 127.0.0.1 / ::1).
///   • Every request from a non-loopback client is refused (403), so a proxy,
///     tunnel or port-forward still can't expose a no-sign-in app.
/// With local mode off, nothing here does anything and sign-in works as normal.
/// </summary>
public static class LocalMode
{
    public const string OwnerEmail = "owner@local.invalid";

    public static bool IsEnabled(IConfiguration c) => c.GetValue<bool>("LocalMode:Enabled");

    /// <summary>Registers the loopback guard. Call straight after builder.Build(), before anything else.</summary>
    public static void UseLocalModeGuard(this WebApplication app)
    {
        if (!IsEnabled(app.Configuration)) return;

        // Refuse to start on a public address.
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses ?? Array.Empty<string>();
            var exposed = addresses.Where(a => !IsLoopbackAddress(a)).ToList();
            if (exposed.Count == 0)
            {
                app.Logger.LogWarning("LOCAL MODE: no sign-in, owner account only, loopback only ({Addresses}).",
                    string.Join(", ", addresses));
                return;
            }
            app.Logger.LogCritical(
                "LOCAL MODE refused: the app is listening on {Addresses}. Local mode removes sign-in, so it only "
                + "runs on localhost. Listen on http://localhost:<port>, or turn LocalMode:Enabled off.",
                string.Join(", ", exposed));
            Environment.ExitCode = 1;
            app.Lifetime.StopApplication();
        });

        // Refuse any request that did not come from this machine.
        app.Use(async (ctx, next) =>
        {
            var remote = ctx.Connection.RemoteIpAddress;
            if (remote == null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsJsonAsync(new { error = "This app is in local mode and only answers on this machine." });
                return;
            }
            await next();
        });
    }

    internal static bool IsLoopbackAddress(string address)
    {
        // Kestrel reports e.g. http://localhost:5320, http://127.0.0.1:5320, http://[::]:5320, http://+:80
        if (!Uri.TryCreate(address.Replace("://+", "://wildcard").Replace("://*", "://wildcard"), UriKind.Absolute, out var uri))
            return false;
        var host = uri.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }
}

[ApiController]
[Route("api/auth")]
public class LocalSessionController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly IAuthService _auth;
    private readonly PromptDbContext _db;
    private readonly ILogger<LocalSessionController> _logger;

    public LocalSessionController(IConfiguration configuration, IAuthService auth, PromptDbContext db, ILogger<LocalSessionController> logger)
    {
        _configuration = configuration;
        _auth = auth;
        _db = db;
        _logger = logger;
    }

    /// <summary>Lets the frontend decide whether to show the sign-in page at all.</summary>
    [AllowAnonymous]
    [HttpGet("mode")]
    public IActionResult Mode() => Ok(new { localMode = LocalMode.IsEnabled(_configuration) });

    /// <summary>
    /// Local mode only: returns a session for the single owner account, creating
    /// it on first use. The owner has no password, so it can never be signed in to
    /// through the normal login, even if local mode is later turned off. Requests
    /// only reach here from loopback (see LocalMode.UseLocalModeGuard).
    /// </summary>
    [AllowAnonymous]
    [HttpPost("local-session")]
    public async Task<IActionResult> LocalSession()
    {
        if (!LocalMode.IsEnabled(_configuration)) return NotFound();

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == LocalMode.OwnerEmail);
        if (user == null)
        {
            var name = (_configuration["LocalMode:OwnerName"] ?? "").Trim();
            user = new User
            {
                Id = Guid.NewGuid().ToString(),
                Email = LocalMode.OwnerEmail,
                Name = name.Length is > 0 and <= 100 ? name : "Owner",
                PasswordHash = null,
                CreatedAt = DateTime.UtcNow,
                LastLoginAt = DateTime.UtcNow,
                IsActive = true,
                ReferralCode = "LOCAL-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
            };
            _db.Users.Add(user);
            _logger.LogInformation("Local mode: created the owner account.");
        }
        else
        {
            user.LastLoginAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        return Ok(new LoginResponse
        {
            Token = _auth.GenerateJwtToken(user),
            User = new UserDto { Id = user.Id, Email = user.Email, Name = user.Name },
        });
    }
}
