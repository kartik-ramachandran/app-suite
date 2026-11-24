using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using LifeManager.Api.Services;
using System.Security.Claims;
using System.Text;

namespace LifeManager.Api.Controllers;

/// <summary>
/// The journal's voice agent: a short spoken back-and-forth instead of a monologue.
///
/// The browser does the listening and speaking (Web Speech API), so no audio is
/// uploaded and no transcription key is needed — only a chat model, which can be
/// a local Ollama model. Each turn the agent asks one warm follow-up question,
/// grounded in what the user has recorded before. When the user is done, the
/// conversation is filed through the normal journal pipeline
/// (POST /api/lifetracker/voice/text), so review-before-save and the safety
/// screen behave exactly as they do for a recording.
/// </summary>
[Authorize]
[ApiController]
[Route("api/lifetracker/voice-agent")]
public class LifeVoiceAgentController : ControllerBase
{
    private const int MaxTurns = 24;
    private const int MaxTurnChars = 4000;

    private readonly IAIService _ai;
    private readonly LifeNarrativeService _narrative;
    private readonly LifeSafetyService _safety;
    private readonly ILogger<LifeVoiceAgentController> _logger;

    public LifeVoiceAgentController(IAIService ai, LifeNarrativeService narrative, LifeSafetyService safety, ILogger<LifeVoiceAgentController> logger)
    {
        _ai = ai;
        _narrative = narrative;
        _safety = safety;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    public sealed record AgentTurn(string Role, string Text);
    public sealed record AgentRequest(List<AgentTurn>? Turns, string? Mood);

    /// <summary>The agent's next line. Called with an empty conversation to open.</summary>
    [HttpPost("reply")]
    [EnableRateLimiting("ai-generation")]
    public async Task<ActionResult> Reply([FromBody] AgentRequest req, CancellationToken ct = default)
    {
        if (!_ai.IsAvailable)
            return StatusCode(503, new { error = "No AI provider is set up. Add a key, or turn on Ollama for a free local model." });

        var turns = (req.Turns ?? new())
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .TakeLast(MaxTurns)
            .Select(t => new AgentTurn(t.Role == "agent" ? "agent" : "user", t.Text.Length > MaxTurnChars ? t.Text[..MaxTurnChars] : t.Text.Trim()))
            .ToList();

        // Safety first, on everything the user has said. A crisis gets real help,
        // read aloud, not another journalling prompt.
        var said = string.Join("\n", turns.Where(t => t.Role == "user").Select(t => t.Text));
        if (said.Length > 0)
        {
            var verdict = await _safety.ScreenAsync(said, HelplineDirectory.DetectCountry(HttpContext), ct);
            if (verdict.BlocksGeneration)
                return Ok(new { reply = verdict.Message, safetyStop = true, category = verdict.Category, done = true });
        }

        var life = await _narrative.BuildContextAsync(GetUserId(), ct);
        var userTurns = turns.Count(t => t.Role == "user");

        var system = new StringBuilder();
        system.AppendLine("""
            You are a warm, perceptive journalling companion having a short SPOKEN conversation.
            Your words are read aloud, so:
            - Reply in 1–3 short sentences. No lists, no markdown, no emojis, no headings.
            - Ask exactly ONE open question per turn. Never two.
            - Reflect back something specific they just said before asking, so they feel heard.
            - Gently cover, over the conversation: how they feel (mood, energy, sleep), what happened today,
              any decision they're weighing, people on their mind, and what they want next.
              Don't interrogate — follow what matters to them.
            - Do not diagnose, give medical advice, or lecture. You are not a therapist.
            - If they say they're done, or after about 6 of their answers, close warmly in one or two sentences,
              say you'll turn this into today's entry, and end your reply with the exact token [[DONE]].
            """);
        system.AppendLine(LifeSafetyService.SafetyClause);
        if (!string.IsNullOrWhiteSpace(req.Mood)) system.AppendLine($"They opened by choosing the mood: {req.Mood}.");
        system.AppendLine($"This is their answer number {userTurns + 1} coming up.");
        system.AppendLine();
        system.AppendLine("What you already know from their journal (use it lightly — mention one thread at most, never recite it):");
        system.AppendLine(life.Text.Length > 6000 ? life.Text[..6000] : life.Text);

        var history = turns.Select(t => new ConversationMessage(t.Role == "agent" ? "assistant" : "user", t.Text)).ToList();
        if (history.Count == 0)
            history.Add(new ConversationMessage("user", "(The user has just started a voice journal session. Greet them briefly and ask your first question.)"));

        string reply;
        try
        {
            var sb = new StringBuilder();
            await foreach (var token in _ai.StreamExecuteAsync(system.ToString(), history, null, ct))
                sb.Append(token);
            reply = sb.ToString().Trim();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Voice agent reply failed");
            return StatusCode(502, new { error = "The AI didn't answer. Check the provider (or that Ollama is running) and try again." });
        }

        var done = reply.Contains("[[DONE]]", StringComparison.Ordinal);
        reply = reply.Replace("[[DONE]]", "").Trim();
        if (string.IsNullOrWhiteSpace(reply)) reply = done ? "Thank you for sharing all that. I'll turn it into today's entry." : "Tell me a little more about that?";

        // Small local models sometimes ignore "no markdown"; strip it so the voice doesn't read symbols.
        reply = System.Text.RegularExpressions.Regex.Replace(reply, @"[*_#`>]+", "");

        // Hard stop so a runaway session can't go on forever.
        if (userTurns >= MaxTurns / 2) done = true;

        return Ok(new { reply, done });
    }
}
