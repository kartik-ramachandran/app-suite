using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MeetingManager.Api.Models;
using MeetingManager.Api.Services;

namespace MeetingManager.Api.Controllers;

/// <summary>
/// Azure Talking Avatar controller — ports the AvatarCallView.vue pattern to .NET.
///
/// Flow:
///   1. GET /api/avatar/token         → issues short-lived Azure Speech token + session
///   2. GET /api/avatar/ice-token     → relays Azure TURN/ICE credentials
///   3. POST /api/avatar/llm          → sends user text to AI, returns agent reply
///   4. DELETE /api/avatar/session/{id} → cleans up session
/// </summary>
[ApiController]
[Route("api/avatar")]
[Authorize]
public class AvatarController : ControllerBase
{
    private static readonly ConcurrentDictionary<string, AvatarSession> _sessions = new();

    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _http;
    private readonly IAIService _ai;
    private readonly AvatarAgentService _agentService;
    private readonly ILogger<AvatarController> _logger;

    // Supported Azure Talking Avatar characters (East US region — lisa only for live real-time)
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AvatarCatalog =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["lisa"] = new(StringComparer.OrdinalIgnoreCase)
                { "casual-sitting", "graceful-sitting", "graceful-standing", "technical-sitting", "technical-standing" },
            ["anna"] = new(StringComparer.OrdinalIgnoreCase) { "casual-sitting" },
        };

    public AvatarController(IConfiguration config, IHttpClientFactory http, IAIService ai, AvatarAgentService agentService, ILogger<AvatarController> logger)
    {
        _config = config;
        _http = http;
        _ai = ai;
        _agentService = agentService;
        _logger = logger;
    }

    private string UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "";

    /// <summary>Issues a short-lived Azure Speech token (10 min) and creates a session.</summary>
    [HttpGet("token")]
    public async Task<IActionResult> GetToken([FromQuery] string? avatarCharacter, [FromQuery] string? avatarStyle, CancellationToken ct)
    {
        var key = _config["AzureSpeech:SubscriptionKey"] ?? "";
        var region = _config["AzureSpeech:Region"] ?? "eastus";

        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { message = "Avatar sessions aren't available right now — please try again later." });

        var character = (!string.IsNullOrWhiteSpace(avatarCharacter) ? avatarCharacter : _config["AzureSpeech:AvatarCharacter"]) ?? "lisa";
        var style = (!string.IsNullOrWhiteSpace(avatarStyle) ? avatarStyle : _config["AzureSpeech:AvatarStyle"]) ?? "casual-sitting";

        if (!AvatarCatalog.TryGetValue(character, out var styles) || !styles.Contains(style))
            return BadRequest(new { message = $"Invalid avatar character/style: {character}/{style}" });

        var tokenEndpoint = $"https://{region}.api.cognitive.microsoft.com/sts/v1.0/issueToken";
        using var client = _http.CreateClient();
        client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", key);

        var resp = await client.PostAsync(tokenEndpoint, new StringContent(string.Empty), ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("[Avatar] Token issue failed: {Status} {Error}", resp.StatusCode, err);
            return StatusCode(502, new { message = "Failed to get Azure Speech token. Check your subscription key and region." });
        }

        var token = await resp.Content.ReadAsStringAsync(ct);
        var sessionId = Guid.NewGuid().ToString("N");
        PruneSessions();
        _sessions[sessionId] = new AvatarSession { UserId = UserId, CreatedAt = DateTime.UtcNow };

        return Ok(new
        {
            sessionId,
            token,
            region,
            avatarCharacter = character,
            avatarStyle = style,
            ttsVoice = _config["AzureSpeech:TtsVoice"] ?? "en-US-JennyNeural",
            sttLanguage = _config["AzureSpeech:SttLanguage"] ?? "en-US",
        });
    }

    /// <summary>Returns Azure Talking Avatar TURN/relay ICE credentials.</summary>
    [HttpGet("ice-token")]
    public async Task<IActionResult> GetIceToken(CancellationToken ct)
    {
        var key = _config["AzureSpeech:SubscriptionKey"] ?? "";
        var region = _config["AzureSpeech:Region"] ?? "eastus";

        if (string.IsNullOrWhiteSpace(key))
            return BadRequest(new { message = "Azure Speech is not configured." });

        var endpoint = $"https://{region}.tts.speech.microsoft.com/cognitiveservices/avatar/relay/token/v1";
        using var client = _http.CreateClient();
        client.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", key);

        var resp = await client.GetAsync(endpoint, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("[Avatar/ICE] {Status} {Error}", resp.StatusCode, err);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return StatusCode(403, new { message = "Talking Avatar requires S0 (Standard) tier Azure Speech resource." });
            return StatusCode((int)resp.StatusCode, new { message = $"Failed to get ICE token: {resp.StatusCode}" });
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        return Content(json, "application/json");
    }

    /// <summary>Sends user text to AI and returns the agent reply to be spoken by the avatar.</summary>
    [HttpPost("llm")]
    public async Task<IActionResult> Llm([FromBody] AvatarLlmRequest req, CancellationToken ct)
    {
        if (!_sessions.TryGetValue(req.SessionId, out var session) || session.UserId != UserId)
            return BadRequest(new { message = "Invalid or expired session" });

        if (string.IsNullOrWhiteSpace(req.UserText))
            return Ok(new { reply = "" });

        session.AddUserMessage(req.UserText);

        try
        {
            string reply;

            // If this is a calendar agent mode, use AvatarAgentService with full context
            if (req.Mode == "calendar")
            {
                var history = string.Join("\n", session.History.TakeLast(6).Select(m => $"{m.Role}: {m.Content}"));
                reply = await _agentService.AskAsync(UserId, req.UserText, history);
            }
            else
            {
                var systemPrompt = req.SystemPrompt ?? "You are a helpful, friendly personal AI work assistant. Keep replies concise and conversational — no markdown, no bullet points. Speak naturally as if talking to the user.";
                var messages = session.History.TakeLast(10).Select(m => new ConversationMessage(m.Role, m.Content)).ToList();
                var response = await _ai.TestPromptAsync(new PromptTestRequest
                {
                    PromptContent = systemPrompt,
                    UserInput = req.UserText,
                });
                reply = response.Result;
            }

            // Strip markdown/emojis for clean speech
            reply = System.Text.RegularExpressions.Regex.Replace(reply, @"\*\*|__|~~|\*|`|#{1,6}\s", "").Trim();
            reply = System.Text.RegularExpressions.Regex.Replace(reply, @"\p{Cs}|\p{So}", "").Trim();

            session.AddAssistantMessage(reply);
            return Ok(new { reply });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Avatar/LLM] failed");
            return Ok(new { reply = "I'm sorry, I had trouble processing that. Could you try again?" });
        }
    }

    /// <summary>Gets a daily briefing text for the avatar to speak.</summary>
    [HttpGet("daily-briefing")]
    public async Task<IActionResult> GetDailyBriefing([FromQuery] string timezone = "UTC")
    {
        var briefing = await _agentService.GetDailyBriefingAsync(UserId, timezone);
        var spoken = BuildSpokenBriefing(briefing);
        return Ok(new { briefing, spoken });
    }

    [HttpDelete("session/{sessionId}")]
    public IActionResult EndSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session) && session.UserId == UserId)
            _sessions.TryRemove(sessionId, out _);
        return Ok();
    }

    private static string BuildSpokenBriefing(DailyBriefing b)
    {
        var parts = new List<string>();
        parts.Add(b.Summary);
        if (b.MeetingsNeedingPrep.Count > 0)
            parts.Add($"Heads up — {b.MeetingsNeedingPrep.Count} meeting{(b.MeetingsNeedingPrep.Count != 1 ? "s" : "")} need{(b.MeetingsNeedingPrep.Count == 1 ? "s" : "")} preparation: {string.Join(", ", b.MeetingsNeedingPrep.Take(2))}.");
        if (b.PendingFollowUps.Count > 0)
            parts.Add($"You have {b.PendingFollowUps.Count} pending follow-up{(b.PendingFollowUps.Count != 1 ? "s" : "")}.");
        if (!string.IsNullOrEmpty(b.BestFocusBlock))
            parts.Add($"Your best focus block today is {b.BestFocusBlock}. I recommend protecting it.");
        if (b.Risks.Count > 0)
            parts.Add($"One thing to watch: {b.Risks.First()}");
        return string.Join(" ", parts);
    }

    private static void PruneSessions()
    {
        var cutoff = DateTime.UtcNow.AddHours(-2);
        foreach (var kv in _sessions)
            if (kv.Value.CreatedAt < cutoff)
                _sessions.TryRemove(kv.Key, out _);
    }
}

public class AvatarSession
{
    public string UserId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public List<AvatarMessage> History { get; } = new();
    public void AddUserMessage(string text) => History.Add(new("user", text));
    public void AddAssistantMessage(string text) => History.Add(new("assistant", text));
}

public record AvatarMessage(string Role, string Content);

public class AvatarLlmRequest
{
    public required string SessionId { get; set; }
    public required string UserText { get; set; }
    public string? SystemPrompt { get; set; }
    public string? Mode { get; set; }
}
