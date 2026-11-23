using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MeetingManager.Api.Services.MeetingCapture;

/// <summary>
/// Starts one meeting-bot container for one meeting.
///
/// Two ways to run it, chosen by what is configured rather than by a flag:
///
///   Azure Container Apps job -- production. The job is created once with the
///     bot image; each meeting starts an execution with its own environment.
///     Set MeetingBot:JobResourceId.
///
///   Docker -- a machine with a Docker daemon. Set MeetingBot:UseDocker=true.
///
/// Neither is a fallback for the other: running the bot needs somewhere to run
/// it, and pretending otherwise is how you end up with a feature that looks
/// enabled and silently records nothing.
/// </summary>
public class NativeBotLauncher
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<NativeBotLauncher> _logger;

    public NativeBotLauncher(IConfiguration config, IHttpClientFactory http, ILogger<NativeBotLauncher> logger)
    {
        _config = config;
        _http = http;
        _logger = logger;
    }

    private string? JobResourceId => _config["MeetingBot:JobResourceId"];
    private bool UseDocker => _config.GetValue("MeetingBot:UseDocker", false);
    /// <summary>
    /// Which bot build to start, as a fully qualified image with a timestamped
    /// tag -- set by deploy-meetingbot.sh. There is deliberately no default: a
    /// :latest fallback would hide which bot joined a given meeting and leave
    /// nothing to roll back to, and this is a recording, so the build that made
    /// it matters.
    /// </summary>
    private string? Image => _config["MeetingBot:Image"];
    private string? PublicBaseUrl => _config["MeetingBot:PublicBaseUrl"] ?? _config["App:PublicBaseUrl"];

    public bool CanLaunch => (!string.IsNullOrWhiteSpace(JobResourceId) || UseDocker)
        && !string.IsNullOrWhiteSpace(PublicBaseUrl)
        && !string.IsNullOrWhiteSpace(Image);

    public string? WhyNot
    {
        get
        {
            if (string.IsNullOrWhiteSpace(PublicBaseUrl))
                return "The bot needs to know this site's public address. Set MeetingBot:PublicBaseUrl.";
            if (string.IsNullOrWhiteSpace(JobResourceId) && !UseDocker)
                return "There is nowhere to run the bot. Set MeetingBot:JobResourceId for Azure, or MeetingBot:UseDocker for a Docker host.";
            if (string.IsNullOrWhiteSpace(Image))
                return "No bot build is configured. Run deploy-meetingbot.sh, which sets MeetingBot:Image to the tag it pushed.";
            return null;
        }
    }

    /// <param name="botName">What attendees see in the participant list.</param>
    /// <param name="maxMinutes">Leave the call after this long, whatever the calendar said.</param>
    public async Task<(bool Started, string? Message, string? Handle)> LaunchAsync(
        string meetingId,
        string meetingUrl,
        string botToken,
        string botName,
        int maxMinutes,
        CancellationToken ct = default)
    {
        if (!CanLaunch) return (false, WhyNot, null);

        var env = new Dictionary<string, string>
        {
            ["MEETING_ID"] = meetingId,
            ["MEETING_URL"] = meetingUrl,
            ["API_BASE"] = PublicBaseUrl!.TrimEnd('/'),
            ["BOT_TOKEN"] = botToken,
            ["BOT_NAME"] = botName,
            ["MAX_MINUTES"] = maxMinutes.ToString(),
        };

        return UseDocker
            ? LaunchDocker(meetingId, env)
            : await LaunchContainerAppJobAsync(meetingId, env, ct);
    }

    // ── Azure Container Apps job ─────────────────────────────────────────────

    private async Task<(bool, string?, string?)> LaunchContainerAppJobAsync(
        string meetingId, Dictionary<string, string> env, CancellationToken ct)
    {
        var token = await ManagedIdentityTokenAsync(ct);
        if (token is null)
            return (false, "Could not get a managed identity token to start the bot job.", null);

        var payload = new
        {
            template = new
            {
                containers = new[]
                {
                    new
                    {
                        name = "meetingbot",
                        image = Image!,
                        env = env.Select(kv => new { name = kv.Key, value = kv.Value }).ToArray(),
                    },
                },
            },
        };

        var url = $"https://management.azure.com{JobResourceId}/start?api-version=2024-03-01";

        try
        {
            using var client = _http.CreateClient("meeting-bot-launcher");
            client.Timeout = TimeSpan.FromSeconds(40);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Bot job refused {Status}: {Body}", (int)response.StatusCode, Trim(body));
                return (false, $"The bot job could not be started ({(int)response.StatusCode}).", null);
            }

            return (true, null, ReadExecutionName(body) ?? meetingId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not start the bot job for {Meeting}", meetingId);
            return (false, "Could not reach Azure to start the bot.", null);
        }
    }

    /// <summary>The container app's own identity, from the platform's token endpoint.</summary>
    private async Task<string?> ManagedIdentityTokenAsync(CancellationToken ct)
    {
        var endpoint = Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT");
        var header = Environment.GetEnvironmentVariable("IDENTITY_HEADER");
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(header)) return null;

        try
        {
            using var client = _http.CreateClient("meeting-bot-launcher");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{endpoint}?resource=https%3A%2F%2Fmanagement.azure.com%2F&api-version=2019-08-01");
            request.Headers.Add("X-IDENTITY-HEADER", header);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Managed identity token request failed");
            return null;
        }
    }

    private static string? ReadExecutionName(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("name", out var n)) return n.GetString();
        }
        catch { /* not JSON we recognise */ }
        return null;
    }

    // ── Docker ───────────────────────────────────────────────────────────────

    private (bool, string?, string?) LaunchDocker(string meetingId, Dictionary<string, string> env)
    {
        var name = $"meetingbot-{meetingId[..Math.Min(8, meetingId.Length)]}";
        var args = new List<string> { "run", "--rm", "-d", "--name", name };
        foreach (var kv in env) { args.Add("-e"); args.Add($"{kv.Key}={kv.Value}"); }
        args.Add(Image!);

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) startInfo.ArgumentList.Add(a);

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null) return (false, "Could not start Docker.", null);

            process.WaitForExit(30_000);
            if (process.ExitCode != 0)
            {
                var error = process.StandardError.ReadToEnd();
                _logger.LogError("docker run failed: {Error}", Trim(error));
                return (false, "The bot container could not be started.", null);
            }

            return (true, null, name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not run the bot container for {Meeting}", meetingId);
            return (false, "Could not run the bot container.", null);
        }
    }

    private static string Trim(string s) => s.Length > 400 ? s[..400] : s;
}
