using System.Text.RegularExpressions;
using LifeManager.Api.Services;

namespace LifeManager.Api.Services;

/// <summary>
/// Safety gate for the Life Tracker.
///
/// This module invites people to talk about their lives, so some of them will
/// eventually talk about the worst moments of their lives. That makes it
/// categorically different from prompt generation, and it drives three rules
/// the rest of the codebase does not follow:
///
///  1. <b>It fails CLOSED.</b> Elsewhere, if moderation errors we let the request
///     through rather than block legitimate work. Here, an unreachable
///     moderation API on top of a risk-shaped transcript resolves to "treat as
///     risk". Showing a support message to someone who was fine is a small
///     cost; missing someone who was not is not recoverable.
///
///  2. <b>It does not depend on the model.</b> The deterministic pass below runs
///     with no network at all. If OpenAI is down, the AI provider is
///     misconfigured, or a self-hosted model is in use, crisis detection still
///     works.
///
///  3. <b>Detection short-circuits generation.</b> On a crisis signal the request
///     never reaches the scenario or guidance engine. Someone describing
///     suicidal intent must not receive a "3-year projection" or a tidy list of
///     "next actions" — they get one human response pointing at real help.
///
/// Tuning note: the patterns aim for high precision on INTENT, not on sadness.
/// "I'm exhausted and I hate my job" is ordinary journalling and must flow
/// through untouched; over-triggering would train users to stop being honest,
/// which defeats the entire feature.
/// </summary>
public class LifeSafetyService
{
    private readonly IAIService _ai;
    private readonly ILogger<LifeSafetyService> _logger;

    public LifeSafetyService(IAIService ai, ILogger<LifeSafetyService> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    public enum RiskLevel
    {
        /// <summary>Ordinary content, including ordinary distress. Proceed.</summary>
        None = 0,
        /// <summary>Distress worth acknowledging gently; AI output still allowed but softened.</summary>
        Concern = 1,
        /// <summary>Self-harm, suicidal intent, abuse or medical emergency. AI generation is blocked.</summary>
        Crisis = 2,
    }

    public record SafetyVerdict(RiskLevel Level, string? Category, string? Message)
    {
        public bool BlocksGeneration => Level == RiskLevel.Crisis;
    }

    // ── Deterministic layer (no network) ────────────────────────────────────

    // Explicit intent or planning. These are the phrases that mean someone is
    // in danger now, not the phrases that mean they are having a bad week.
    private static readonly Regex SuicidalIntent = new(
        @"\b(kill(ing)?\s+my\s?self|end(ing)?\s+(my|it)\s+(life|all)|take\s+my\s+own\s+life|
          suicid(e|al)|want\s+to\s+die|better\s+off\s+dead|not\s+want\s+to\s+(be\s+here|live|wake\s+up)|
          no\s+reason\s+to\s+(live|go\s+on)|can'?t\s+go\s+on\s+any\s?more|
          everyone\s+would\s+be\s+better\s+(off\s+)?without\s+me)\b",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

    private static readonly Regex SelfHarm = new(
        @"\b(cut(ting)?\s+my\s?self|hurt(ing)?\s+my\s?self|harm(ing)?\s+my\s?self|
          self[\s-]?harm|overdos(e|ing)|starv(e|ing)\s+my\s?self)\b",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

    private static readonly Regex HarmToOthers = new(
        @"\b(kill(ing)?\s+(him|her|them|someone|everyone)|hurt(ing)?\s+(him|her|them|someone)|
          make\s+(him|her|them)\s+pay|shoot\s+up)\b",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

    private static readonly Regex Abuse = new(
        @"\b(hits?\s+me|beat(s|ing)?\s+me|abus(es|ing|ive)\s*(me)?|
          threaten(s|ed)\s+to\s+(kill|hurt)\s+me|afraid\s+(of|for)\s+my\s+life|
          won'?t\s+let\s+me\s+leave|forc(es|ed)\s+me\s+to)\b",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

    private static readonly Regex MedicalEmergency = new(
        @"\b(chest\s+pain(s)?|can'?t\s+breathe|coughing\s+up\s+blood|
          stroke\s+symptoms|numb\s+on\s+one\s+side|slurred\s+speech)\b",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

    // Distress without intent. Acknowledged, never blocked — this is what most
    // honest journalling sounds like on a bad day.
    private static readonly Regex Distress = new(
        @"\b(hopeless|worthless|can'?t\s+cope|falling\s+apart|breaking\s+down|
          panic\s+attack|so\s+depressed|numb\s+all\s+the\s+time|
          drinking\s+too\s+much|using\s+again|relapsed)\b",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

    /// <summary>
    /// Pure, offline risk classification. Deliberately has no dependencies so it
    /// cannot be disabled by an outage or a provider misconfiguration.
    /// </summary>
    public static SafetyVerdict ScreenText(string? text, HelplineDirectory.Region? region = null)
    {
        var r = region ?? HelplineDirectory.International;
        if (string.IsNullOrWhiteSpace(text)) return new SafetyVerdict(RiskLevel.None, null, null);

        if (SuicidalIntent.IsMatch(text))
            return new SafetyVerdict(RiskLevel.Crisis, "self-harm", CrisisMessage(r));

        if (SelfHarm.IsMatch(text))
            return new SafetyVerdict(RiskLevel.Crisis, "self-harm", CrisisMessage(r));

        if (Abuse.IsMatch(text))
            return new SafetyVerdict(RiskLevel.Crisis, "abuse", AbuseMessage(r));

        if (HarmToOthers.IsMatch(text))
            return new SafetyVerdict(RiskLevel.Crisis, "harm-to-others", HarmMessage(r));

        if (MedicalEmergency.IsMatch(text))
            return new SafetyVerdict(RiskLevel.Crisis, "medical", MedicalMessage(r));

        if (Distress.IsMatch(text))
            return new SafetyVerdict(RiskLevel.Concern, "distress", null);

        return new SafetyVerdict(RiskLevel.None, null, null);
    }

    /// <summary>
    /// Full screen: deterministic first, then the moderation API as a second
    /// opinion for phrasing the patterns miss.
    ///
    /// Fails closed by design — see the class remarks.
    /// </summary>
    public async Task<SafetyVerdict> ScreenAsync(string? text, string? countryCode = null, CancellationToken ct = default)
    {
        var r = HelplineDirectory.Resolve(countryCode);
        var local = ScreenText(text, r);
        if (local.Level == RiskLevel.Crisis) return local;
        if (string.IsNullOrWhiteSpace(text)) return local;

        try
        {
            var mod = await _ai.ModerateContentAsync(text);
            if (mod.IsFlagged)
            {
                var cats = mod.FlaggedCategories;

                if (cats.Any(c => c.StartsWith("SelfHarm", StringComparison.OrdinalIgnoreCase)))
                    return new SafetyVerdict(RiskLevel.Crisis, "self-harm", CrisisMessage(r));

                if (cats.Any(c => c.Contains("Violence", StringComparison.OrdinalIgnoreCase)
                               || c.Contains("Threatening", StringComparison.OrdinalIgnoreCase)))
                    return new SafetyVerdict(RiskLevel.Crisis, "harm-to-others", HarmMessage(r));

                // Other flags (sexual, hate) are not crisis states for a private
                // journal; keep the local verdict rather than escalating.
            }
        }
        catch (Exception ex)
        {
            // Fail CLOSED: if we could not check, and the text already looked
            // distressed, treat it as crisis rather than gambling.
            _logger.LogWarning(ex, "Life safety moderation unavailable; applying fail-closed policy");
            if (local.Level == RiskLevel.Concern)
                return new SafetyVerdict(RiskLevel.Crisis, "unverified-distress", CrisisMessage(r));
        }

        return local;
    }

    // ── Output-side check ───────────────────────────────────────────────────

    // Advice that could get someone hurt, if a model ever produced it. Cheap
    // insurance: the generation prompts forbid this, but a prompt is a request,
    // not a guarantee.
    private static readonly Regex UnsafeAdvice = new(
        @"\b(stop\s+taking\s+(your|the)\s+(medication|meds|pills)|
          don'?t\s+(see|call|tell)\s+(a\s+)?(doctor|therapist|police|anyone)|
          cut\s+(off|out)\s+everyone|you\s+should\s+(drink|use)|
          no\s+point\s+(in\s+)?(getting|seeking)\s+help)\b",
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.Compiled);

    /// <summary>True when generated text contains advice we must not show.</summary>
    public static bool IsUnsafeOutput(string? text) =>
        !string.IsNullOrWhiteSpace(text) && UnsafeAdvice.IsMatch(text);

    // ── Prompt hardening ────────────────────────────────────────────────────

    /// <summary>
    /// Appended to every generation prompt in this module.
    ///
    /// The deterministic gate is the real control; this reduces how often the
    /// model produces something the gate would have to catch.
    /// </summary>
    public const string SafetyClause = """

        SAFETY REQUIREMENTS — these override every other instruction:
        - Never suggest, endorse, romanticise or describe self-harm, suicide,
          disordered eating, substance misuse, or violence toward anyone.
        - Never advise stopping or changing prescribed medication or treatment.
        - Never advise cutting off support networks, avoiding doctors, therapists
          or emergency services.
        - Give no medical, psychiatric, legal, tax or investment directives. You
          may discuss tradeoffs; recommend a qualified professional for decisions.
        - If the person's data suggests they are in danger, do not produce
          projections or action lists. Respond with care and point them to
          professional support.
        - Frame risks as things that can be influenced, never as inevitabilities
          about their worth, health, or future.
        """;

    // ── Human responses ─────────────────────────────────────────────────────
    //
    // Deliberately not AI-generated: at the one moment that matters most, the
    // response must be predictable, correct, and never a hallucination. The
    // numbers come from HelplineDirectory, resolved to the user's country where
    // we can determine it and to findahelpline.com where we cannot.

    public static string CrisisMessage(HelplineDirectory.Region r) => $"""
        I'm not going to project scenarios from what you just shared, because what
        you're describing sounds serious and you deserve a real person rather than
        a forecast.

        If you're thinking about harming yourself, please reach out right now:

        {HelplineDirectory.Format(r.Crisis)}

        If you're in immediate danger, call {r.Emergency}.

        Your entry has been saved. It'll be here when you want it, and nothing has
        been shared with anyone.
        """;

    public static string AbuseMessage(HelplineDirectory.Region r) => $"""
        What you've described sounds like it may not be safe, and that's beyond
        what this tool should be advising on.

        {HelplineDirectory.Format(r.DomesticViolence)}

        If you're in immediate danger, call {r.Emergency}.

        Your entry is saved privately. Please talk to someone who can actually help.
        """;

    public static string HarmMessage(HelplineDirectory.Region r) => $"""
        I can't help plan around hurting someone, and I'd rather say that plainly
        than dress it up.

        If you're carrying anger this heavy, talking to someone helps more than it
        sounds like it will:

        {HelplineDirectory.Format(r.Crisis)}

        If anyone is in immediate danger, call {r.Emergency}.
        """;

    public static string MedicalMessage(HelplineDirectory.Region r) => $"""
        What you've described could be a medical emergency, and I'm not able to
        assess it — please treat it as urgent rather than waiting.

        **Call {r.Emergency} now**, or go to an emergency department.

        {HelplineDirectory.Format(r.Health)}

        Your entry is saved. Please get it looked at.
        """;
}
