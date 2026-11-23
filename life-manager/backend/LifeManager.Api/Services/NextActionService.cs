using System.Text.Json;

namespace LifeManager.Api.Services;

/// <summary>
/// Turns a scanned thing into what the person can actually DO next, with a
/// disclaimer that is enforced in code rather than requested in a prompt.
///
/// WHY THIS EXISTS
/// The scanner modules (warranty, medicine, bill, plant, label, card) all end
/// at extraction: here are the fields we read off your photo. That is a filing
/// cabinet, not help. The useful half is "your warranty ends in 3 weeks — here
/// is how to claim before it does".
///
/// WHY THE DISCLAIMER IS NOT IN THE PROMPT
/// MedicineController already asked the model to "End with: 'This is general
/// information, not medical advice…'". A prompt is a request, not a guarantee:
/// the model can drop it, reword it into meaninglessness, or omit it exactly
/// when the answer is most alarming. Here the disclaimer is appended by this
/// service after generation, so it is present whatever the model returns — and
/// for the health domains the output is also screened and can be discarded
/// entirely.
///
/// DOMAINS ARE NOT EQUAL
/// A warranty suggestion that is wrong wastes an afternoon. A medicine
/// suggestion that is wrong can hurt someone. Domains carrying physical risk
/// (medicine, food labels, plants that may be eaten or touched) run through
/// <see cref="LifeSafetyService.IsUnsafeOutput"/> and are dropped on any hit —
/// failing closed, the same posture as the LifeTracker guardrails.
/// </summary>
public class NextActionService
{
    // The scanner modules all reach the model through AzureVisionService, so
    // this uses the same path rather than introducing a second AI client with
    // its own configuration and failure modes.
    private readonly AzureVisionService _ai;
    private readonly ILogger<NextActionService> _logger;

    public NextActionService(AzureVisionService ai, ILogger<NextActionService> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    /// <summary>What kind of thing we are advising about, and how risky it is.</summary>
    public enum Domain
    {
        /// <summary>Warranties, receipts, bills — money and paperwork. Low risk.</summary>
        Consumer,
        /// <summary>Medicines and supplements. Physical risk. Screened, fails closed.</summary>
        Health,
        /// <summary>Food and cosmetic labels. Allergy and ingestion risk. Screened.</summary>
        Food,
        /// <summary>Plants. May be touched, eaten by children or pets. Screened.</summary>
        Plant,
        /// <summary>Contacts and documents. Low risk.</summary>
        Admin,
    }

    /// <summary>A single thing the person can do, with the reason it matters.</summary>
    public sealed record NextAction(string Action, string Why, string Urgency);

    public sealed record Suggestion(
        IReadOnlyList<NextAction> Actions,
        string Disclaimer,
        bool Withheld,
        string? WithheldReason = null);

    /// <summary>
    /// Enforced disclaimers. Appended by this service, never left to the model.
    /// Wording is domain-specific because a generic "this may be inaccurate"
    /// teaches people to ignore all of them.
    /// </summary>
    private static string DisclaimerFor(Domain d) => d switch
    {
        Domain.Health =>
            "General information only — not medical advice. Never start, stop, or change a medicine based on this. " +
            "Check with a pharmacist or doctor, and call your local emergency number if someone is unwell.",
        Domain.Food =>
            "Read the physical label before relying on this. Photos miss things, formulations change, and this is " +
            "not allergy or dietary advice. If you have a severe allergy, treat the packaging as the only source.",
        Domain.Plant =>
            "A best guess from a photo, not an expert identification. Never eat a plant, or use one on skin, based " +
            "on this. Assume unknown plants are harmful to children and pets until confirmed.",
        Domain.Consumer =>
            "Suggestions based on what we read from your photo — check the actual terms, dates, and amounts before acting.",
        Domain.Admin =>
            "Based on what we read from your document — confirm details before sending anything.",
        _ => "Check the original document before acting on this.",
    };

    /// <summary>
    /// Ask for 2–4 concrete next steps for a scanned item.
    /// </summary>
    /// <param name="domain">Decides the disclaimer and whether output is screened.</param>
    /// <param name="itemDescription">What was scanned, already extracted — never the raw image.</param>
    /// <param name="context">Optional extra facts (dates, amounts) that make advice specific.</param>
    public async Task<Suggestion> SuggestAsync(Domain domain, string itemDescription, string? context = null)
    {
        var disclaimer = DisclaimerFor(domain);

        if (!_ai.IsAvailable || string.IsNullOrWhiteSpace(itemDescription))
            return new Suggestion(Array.Empty<NextAction>(), disclaimer, true, "unavailable");

        var guardrail = domain switch
        {
            Domain.Health =>
                "NEVER suggest a dose, a schedule, starting or stopping a medicine, or combining medicines. " +
                "NEVER suggest anything that replaces seeing a doctor or pharmacist. Suggest only administrative, " +
                "storage, and disposal actions, and when to ask a professional.",
            Domain.Food =>
                "NEVER give allergy clearance, medical, or dietary-restriction advice. Suggest only practical " +
                "storage, checking, and shopping actions.",
            Domain.Plant =>
                "NEVER suggest eating, brewing, applying to skin, or any medicinal use. Suggest only care, " +
                "placement, and safety actions.",
            _ =>
                "Suggest only practical administrative actions the person can take themselves.",
        };

        var prompt =
            $@"You suggest what a person should DO next about something they scanned. Today is {DateTime.UtcNow:yyyy-MM-dd}.

ITEM: {itemDescription}
{(string.IsNullOrWhiteSpace(context) ? "" : $"CONTEXT: {context}")}

{guardrail}

Return ONLY a JSON array of 2-4 objects, no prose, no code fences:
[{{""action"":""a short imperative, max 12 words"",""why"":""one plain sentence"",""urgency"":""now|soon|whenever""}}]

Each action must be something the person can do themselves this week. No filler
like ""stay informed"" or ""consider your options"". If there is genuinely nothing
useful to do, return [].";

        string raw;
        try
        {
            raw = await _ai.CompleteTextAsync(
                "You suggest concrete next actions. You return only JSON.",
                prompt,
                maxTokens: 400);
        }
        catch (Exception ex)
        {
            // Fail quiet, not loud: a missing suggestion panel is fine, a broken
            // scanner is not.
            _logger.LogWarning(ex, "Next-action generation failed for {Domain}", domain);
            return new Suggestion(Array.Empty<NextAction>(), disclaimer, true, "unavailable");
        }

        // OUTPUT SCREENING. For the domains that carry physical risk, the model's
        // answer is checked against the same deterministic filter the LifeTracker
        // guardrails use, and the whole set is discarded on any hit. Dropping a
        // useful suggestion is an acceptable price for never emitting a harmful
        // one; this fails closed by design.
        if (domain is Domain.Health or Domain.Food or Domain.Plant && LifeSafetyService.IsUnsafeOutput(raw))
        {
            _logger.LogWarning("Next-action output withheld for {Domain}: failed safety screen", domain);
            return new Suggestion(Array.Empty<NextAction>(), disclaimer, true, "screened");
        }

        var actions = Parse(raw);
        return new Suggestion(actions, disclaimer, actions.Count == 0, actions.Count == 0 ? "none" : null);
    }

    /// <summary>
    /// Parse the model's JSON defensively. Anything malformed yields no
    /// suggestions rather than an exception — a scanner must not fail because
    /// an optional panel could not be built.
    /// </summary>
    private List<NextAction> Parse(string raw)
    {
        var result = new List<NextAction>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var t = raw.Trim();
        if (t.StartsWith("```"))
            t = string.Join("\n", t.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));

        var start = t.IndexOf('[');
        var end = t.LastIndexOf(']');
        if (start < 0 || end <= start) return result;

        try
        {
            using var doc = JsonDocument.Parse(t[start..(end + 1)]);
            foreach (var el in doc.RootElement.EnumerateArray().Take(4))
            {
                var action = Str(el, "action");
                if (string.IsNullOrWhiteSpace(action)) continue;

                var urgency = Str(el, "urgency").ToLowerInvariant();
                if (urgency is not ("now" or "soon" or "whenever")) urgency = "soon";

                result.Add(new NextAction(
                    Trim(action, 120),
                    Trim(Str(el, "why"), 200),
                    urgency));
            }
        }
        catch (JsonException ex)
        {
            _logger.LogInformation(ex, "Next-action JSON unparseable; returning none");
        }

        return result;

        static string Str(JsonElement e, string p) =>
            e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        static string Trim(string s, int max) =>
            s.Length <= max ? s : s[..max];
    }
}
