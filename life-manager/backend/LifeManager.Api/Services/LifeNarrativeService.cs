using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LifeManager.Api.Data;
using LifeManager.Api.Models;

namespace LifeManager.Api.Services;

/// <summary>
/// Turns spoken life journal entries into structure, and structure into
/// foresight.
///
/// Three jobs, in order of how the user experiences them:
///   1. Listen  — a rambling transcript becomes a check-in, goals, decisions
///                and relationships the user can accept or discard.
///   2. Project — the accumulated record becomes a spread of plausible futures.
///   3. Guide   — those futures become a small number of next moves.
///
/// Everything is grounded in the user's own recorded data. Where the data is
/// thin the output says so rather than inventing a life story, because a life
/// coach that confidently hallucinates your circumstances is worse than none.
/// </summary>
public class LifeNarrativeService
{
    private readonly PromptDbContext _db;
    private readonly AIServiceManager _ai;
    private readonly LifeSafetyService _safety;
    private readonly ILogger<LifeNarrativeService> _logger;

    public LifeNarrativeService(PromptDbContext db, AIServiceManager ai, LifeSafetyService safety, ILogger<LifeNarrativeService> logger)
    {
        _db = db;
        _ai = ai;
        _safety = safety;
        _logger = logger;
    }

    // ── 1. Listening ────────────────────────────────────────────────────────

    public record ExtractedCheckIn(int? Mood, int? Energy, int? Focus, double? SleepHours,
        int? ExerciseMinutes, string? TopTasks, string? Gratitude, string? Notes);

    public record ExtractedGoal(string Title, string? Area, string? Notes, int? Progress);
    public record ExtractedDecision(string Title, string? Reasoning, string? Outcome);
    public record ExtractedRelationship(string Name, string? Circle, string? Notes);

    public record VoiceExtraction(
        string Headline,
        string Narrative,
        string? Sentiment,
        ExtractedCheckIn? CheckIn,
        List<ExtractedGoal> Goals,
        List<ExtractedDecision> Decisions,
        List<ExtractedRelationship> Relationships,
        List<string> Themes,
        List<string> OpenLoops);

    /// <summary>
    /// Pull structure out of a spoken entry.
    ///
    /// The prompt is written to under-extract rather than over-extract: a
    /// mis-parsed "goal" the user never set is noise they have to clean up, and
    /// too much of that and they stop trusting the feature entirely.
    /// </summary>
    public async Task<VoiceExtraction> ExtractAsync(string transcript, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            throw new InvalidOperationException("There was nothing to transcribe in that recording.");

        var system = """
            You extract structure from a spoken life-journal entry. The speaker is
            talking casually about their day or their situation.

            Return ONLY a JSON object with this shape:
            {
              "headline": "<=90 chars, plain summary of what this entry is about",
              "narrative": "2-4 sentences retelling it in second person ('You had...')",
              "sentiment": "one or two words in the speaker's own register",
              "checkIn": { "mood": 1-10|null, "energy": 1-10|null, "focus": 1-10|null,
                           "sleepHours": number|null, "exerciseMinutes": number|null,
                           "topTasks": "string|null", "gratitude": "string|null",
                           "notes": "string|null" },
              "goals": [ { "title": "...", "area": "Health|Career|Money|Relationships|Growth|Home",
                           "notes": "...", "progress": 0-100|null } ],
              "decisions": [ { "title": "...", "reasoning": "...", "outcome": "..." } ],
              "relationships": [ { "name": "...", "circle": "Family|Friends|Work|Partner",
                                   "notes": "..." } ],
              "themes": ["recurring subject in this entry"],
              "openLoops": ["something unresolved the speaker raised"]
            }

            Rules that matter:
            - Only include a field if the speaker actually indicated it. Use null.
            - Numeric ratings: infer ONLY from clear signals ("exhausted" -> low
              energy). If they did not convey it, use null. Never default to 5.
            - A "goal" is something they said they want to do or achieve, not a
              passing wish. A "decision" is a choice made or being weighed.
            - Only list a person if the speaker named them.
            - Empty arrays are correct and expected. Do not pad.
            - Never invent detail that is not in the transcript.
            """;

        var raw = await CompleteAsync(system, transcript, ct);
        var json = JsonSchemaValidator.ExtractJson(raw)
            ?? throw new InvalidOperationException("Could not read the structure back from that entry.");

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        return new VoiceExtraction(
            Headline: Clip(Str(r, "headline") ?? "Voice entry", 300),
            Narrative: Str(r, "narrative") ?? "",
            Sentiment: Clip(Str(r, "sentiment"), 60),
            CheckIn: ReadCheckIn(r),
            Goals: Arr(r, "goals").Select(g => new ExtractedGoal(
                    Str(g, "title") ?? "", Str(g, "area"), Str(g, "notes"), Int(g, "progress")))
                .Where(g => !string.IsNullOrWhiteSpace(g.Title)).ToList(),
            Decisions: Arr(r, "decisions").Select(d => new ExtractedDecision(
                    Str(d, "title") ?? "", Str(d, "reasoning"), Str(d, "outcome")))
                .Where(d => !string.IsNullOrWhiteSpace(d.Title)).ToList(),
            Relationships: Arr(r, "relationships").Select(p => new ExtractedRelationship(
                    Str(p, "name") ?? "", Str(p, "circle"), Str(p, "notes")))
                .Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList(),
            Themes: StrList(r, "themes"),
            OpenLoops: StrList(r, "openLoops"));
    }

    private static ExtractedCheckIn? ReadCheckIn(JsonElement root)
    {
        if (!root.TryGetProperty("checkIn", out var c) || c.ValueKind != JsonValueKind.Object) return null;

        var checkIn = new ExtractedCheckIn(
            Clamp(Int(c, "mood"), 1, 10),
            Clamp(Int(c, "energy"), 1, 10),
            Clamp(Int(c, "focus"), 1, 10),
            Dbl(c, "sleepHours"),
            Int(c, "exerciseMinutes"),
            Str(c, "topTasks"), Str(c, "gratitude"), Str(c, "notes"));

        // An all-null check-in is not a check-in; don't offer the user an empty row.
        var hasAnything = checkIn.Mood.HasValue || checkIn.Energy.HasValue || checkIn.Focus.HasValue
            || checkIn.SleepHours.HasValue || checkIn.ExerciseMinutes.HasValue
            || !string.IsNullOrWhiteSpace(checkIn.TopTasks)
            || !string.IsNullOrWhiteSpace(checkIn.Gratitude)
            || !string.IsNullOrWhiteSpace(checkIn.Notes);

        return hasAnything ? checkIn : null;
    }

    // ── 2. Projection ───────────────────────────────────────────────────────

    /// <summary>
    /// Raised when the safety gate stops generation. Carries the message the
    /// user should see, so callers never have to compose crisis wording.
    /// </summary>
    public class LifeSafetyStopException : Exception
    {
        public string Category { get; }
        public LifeSafetyStopException(string message, string category) : base(message) => Category = category;
    }

    /// <summary>
    /// Generate a spread of plausible futures from the user's recorded life.
    ///
    /// Always returns a mix of kinds — the path they are on, branches they could
    /// take, and risks worth naming — because showing only good outcomes is
    /// flattery and showing only bad ones is anxiety. Neither is guidance.
    /// </summary>
    public async Task<List<LifeScenario>> ProjectScenariosAsync(
        string userId, string horizon = "1-year", string? focus = null, string? countryCode = null, CancellationToken ct = default)
    {
        var context = await BuildContextAsync(userId, ct);
        if (context.EntryCount < 3)
            throw new InvalidOperationException(
                "There isn't enough history yet to project anything honest. Add a few voice entries or check-ins first.");

        // Screen the material we are about to reason over. Projecting a
        // "3-year outlook" for someone describing suicidal intent would be an
        // appalling thing for this product to do.
        var verdict = await _safety.ScreenAsync(context.Text + "\n" + focus, countryCode, ct);
        if (verdict.BlocksGeneration)
            throw new LifeSafetyStopException(verdict.Message!, verdict.Category ?? "crisis");

        var system = $$"""
            You are a life-scenario analyst. From a person's own tracked data,
            describe several distinct ways the next {{horizon}} could plausibly unfold.

            Return ONLY a JSON array of 4-6 objects:
            [ {
                "title": "<=70 chars, concrete and specific",
                "kind": "current-path|opportunity|risk|wildcard",
                "summary": "3-5 sentences in second person, describing how it plays out",
                "likelihood": 0-100,
                "drivers": ["evidence FROM THEIR DATA pointing this way"],
                "assumptions": ["what would have to hold true"],
                "actions": ["a concrete move that makes this more or less likely"],
                "signals": ["an early sign this is becoming real"],
                "areas": ["Health|Career|Money|Relationships|Growth|Home"]
            } ]

            Requirements:
            - Include exactly one "current-path": where today's observed habits
              lead if nothing changes. Be honest, not harsh.
            - Include at least one "opportunity" and at least one "risk".
            - Every driver must cite something real from their data. If the data
              is thin on a point, say so in the assumption instead of inventing.
            - Likelihood is a rough read on THEIR observed behaviour, not a
              population statistic. Keep the set varied; do not put everything at 50.
            - Speak plainly. No therapy-speak, no horoscope hedging.
            - No medical, legal, financial or psychiatric diagnosis. For
              high-stakes matters, name that a professional is the right call.
            """ + LifeSafetyService.SafetyClause;

        var ask = new StringBuilder();
        ask.AppendLine(context.Text);
        if (!string.IsNullOrWhiteSpace(focus))
            ask.AppendLine($"\nThe person especially wants to think about: {focus}");

        var raw = await CompleteAsync(system, ask.ToString(), ct);
        var json = JsonSchemaValidator.ExtractJson(raw)
            ?? throw new InvalidOperationException("Could not read the scenarios back.");

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Scenario generation returned an unexpected shape.");

        var batch = Guid.NewGuid().ToString();
        var scenarios = doc.RootElement.EnumerateArray().Take(6).Select(s => new LifeScenario
        {
            UserId = userId,
            BatchId = batch,
            Title = Clip(Str(s, "title") ?? "Possible path", 200)!,
            Kind = NormalizeKind(Str(s, "kind")),
            Horizon = horizon,
            Summary = Str(s, "summary") ?? "",
            Likelihood = Clamp(Int(s, "likelihood"), 0, 100) ?? 50,
            DriversJson = JsonSerializer.Serialize(StrList(s, "drivers")),
            AssumptionsJson = JsonSerializer.Serialize(StrList(s, "assumptions")),
            ActionsJson = JsonSerializer.Serialize(StrList(s, "actions")),
            SignalsJson = JsonSerializer.Serialize(StrList(s, "signals")),
            AreasJson = JsonSerializer.Serialize(StrList(s, "areas")),
        }).Where(s => !string.IsNullOrWhiteSpace(s.Summary)).ToList();

        // Last line of defence: the prompt forbids unsafe advice, but a prompt is
        // a request, not a guarantee. Drop anything that slipped through.
        var unsafeCount = scenarios.RemoveAll(sc =>
            LifeSafetyService.IsUnsafeOutput(sc.Summary) ||
            LifeSafetyService.IsUnsafeOutput(sc.ActionsJson));
        if (unsafeCount > 0)
            _logger.LogWarning("Discarded {Count} scenario(s) containing unsafe advice", unsafeCount);

        if (scenarios.Count == 0)
            throw new InvalidOperationException("No usable scenarios came back. Try again.");

        _db.LifeScenarios.AddRange(scenarios);
        await _db.SaveChangesAsync(ct);
        return scenarios;
    }

    private static string NormalizeKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "current-path" or "current" or "status-quo" => "current-path",
        "risk" or "warning" => "risk",
        "wildcard" or "wild-card" => "wildcard",
        _ => "opportunity",
    };

    // ── 3. Guidance ─────────────────────────────────────────────────────────

    public record GuidanceResult(
        string Read, List<string> NextActions, List<string> Questions,
        List<string> Patterns, string? Caution);

    /// <summary>
    /// "What should I do next?" — answered from the record, not from platitudes.
    /// </summary>
    public async Task<GuidanceResult> GuideAsync(string userId, string? question = null, string? countryCode = null, CancellationToken ct = default)
    {
        var context = await BuildContextAsync(userId, ct);

        // Same gate as projection. "What should I do next?" from someone in
        // crisis needs a hotline, not an action list.
        var verdict = await _safety.ScreenAsync(context.Text + "\n" + question, countryCode, ct);
        if (verdict.BlocksGeneration)
            throw new LifeSafetyStopException(verdict.Message!, verdict.Category ?? "crisis");

        var system = """
            You are a grounded life guide. Using only the person's tracked data,
            tell them where they actually stand and what to do next.

            Return ONLY JSON:
            {
              "read": "3-5 sentences on where they genuinely are right now",
              "nextActions": ["a specific, doable move — not 'exercise more'"],
              "questions": ["a question only they can answer, that would unlock the next step"],
              "patterns": ["something recurring in their data they may not have noticed"],
              "caution": "one honest risk worth naming, or null"
            }

            Requirements:
            - Ground every observation in their data. Reference specifics.
            - 2-4 next actions. Each should be doable this week.
            - Patterns must be things visible across MULTIPLE entries, not one bad day.
            - If the data is too thin to say something useful, say that plainly
              in "read" rather than padding with generic advice.
            - No diagnosis. For medical, legal or financial stakes, point them to
              a professional in "caution".
            """ + LifeSafetyService.SafetyClause;

        var ask = context.Text + (string.IsNullOrWhiteSpace(question)
            ? "\n\nThey have not asked anything specific — tell them what stands out."
            : $"\n\nTheir question: {question}");

        var raw = await CompleteAsync(system, ask, ct);
        var json = JsonSchemaValidator.ExtractJson(raw)
            ?? throw new InvalidOperationException("Could not read the guidance back.");

        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;

        // Filter individual suggestions rather than discarding the whole
        // response — one bad line should not cost the user their guidance.
        var actions = StrList(r, "nextActions").Where(a => !LifeSafetyService.IsUnsafeOutput(a)).ToList();

        return new GuidanceResult(
            Read: Str(r, "read") ?? "",
            NextActions: actions,
            Questions: StrList(r, "questions"),
            Patterns: StrList(r, "patterns"),
            Caution: Str(r, "caution"));
    }

    // ── Shared context ──────────────────────────────────────────────────────

    public record LifeContext(string Text, int EntryCount);

    /// <summary>
    /// Everything the model is allowed to reason about, and nothing else.
    /// Voice entries lead because they carry the texture the numbers miss.
    /// </summary>
    public async Task<LifeContext> BuildContextAsync(string userId, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.Date.AddDays(-180);

        var voice = await _db.LifeVoiceEntries
            .Where(v => v.UserId == userId && v.Status == "ready")
            .OrderByDescending(v => v.EntryDate)
            .Take(30).ToListAsync(ct);

        var checkIns = await _db.LifeCheckIns
            .Where(c => c.UserId == userId && c.Date >= since)
            .OrderByDescending(c => c.Date).Take(60).ToListAsync(ct);

        var goals = await _db.LifeGoals.Where(g => g.UserId == userId)
            .OrderByDescending(g => g.UpdatedAt).Take(25).ToListAsync(ct);

        var decisions = await _db.LifeDecisions.Where(d => d.UserId == userId)
            .OrderByDescending(d => d.Date).Take(25).ToListAsync(ct);

        var relationships = await _db.LifeRelationships.Where(r => r.UserId == userId)
            .OrderByDescending(r => r.Importance).Take(25).ToListAsync(ct);

        var sb = new StringBuilder();

        if (voice.Count > 0)
        {
            sb.AppendLine("## In their own words (most recent first)");
            foreach (var v in voice.Take(12))
                sb.AppendLine($"- {v.EntryDate:yyyy-MM-dd} [{v.Sentiment ?? "—"}] {v.Headline}: {Clip(v.Narrative, 600)}");
            sb.AppendLine();
        }

        if (checkIns.Count > 0)
        {
            sb.AppendLine("## Daily check-ins");
            foreach (var c in checkIns.Take(30))
                sb.AppendLine($"- {c.Date:yyyy-MM-dd}: mood {c.Mood}/10, energy {c.Energy}/10, focus {c.Focus}/10, " +
                              $"sleep {c.SleepHours}h, exercise {c.ExerciseMinutes}m. {Clip(c.Notes, 160)}");

            // Trends the model would otherwise have to compute in prose, badly.
            var recent = checkIns.Take(14).ToList();
            var older = checkIns.Skip(14).Take(14).ToList();
            if (recent.Count >= 3 && older.Count >= 3)
            {
                sb.AppendLine($"\nTrend (last {recent.Count} vs previous {older.Count}): " +
                    $"mood {Avg(recent, x => x.Mood):0.0} vs {Avg(older, x => x.Mood):0.0}, " +
                    $"energy {Avg(recent, x => x.Energy):0.0} vs {Avg(older, x => x.Energy):0.0}, " +
                    $"sleep {Avg(recent, x => x.SleepHours):0.0}h vs {Avg(older, x => x.SleepHours):0.0}h, " +
                    $"exercise {Avg(recent, x => x.ExerciseMinutes):0}m vs {Avg(older, x => x.ExerciseMinutes):0}m");
            }
            sb.AppendLine();
        }

        if (goals.Count > 0)
        {
            sb.AppendLine("## Goals");
            foreach (var g in goals)
                sb.AppendLine($"- [{g.Area}] {g.Title} — {g.Progress}%, {g.Status}" +
                              (g.TargetDate.HasValue ? $", target {g.TargetDate:yyyy-MM-dd}" : "") +
                              (string.IsNullOrWhiteSpace(g.Notes) ? "" : $". {Clip(g.Notes, 200)}"));
            sb.AppendLine();
        }

        if (decisions.Count > 0)
        {
            sb.AppendLine("## Decisions");
            foreach (var d in decisions)
                sb.AppendLine($"- {d.Date:yyyy-MM-dd} {d.Title}. Why: {Clip(d.Reasoning, 200)}. " +
                              $"Outcome: {(string.IsNullOrWhiteSpace(d.Outcome) ? "not yet known" : Clip(d.Outcome, 200))}");
            sb.AppendLine();
        }

        if (relationships.Count > 0)
        {
            sb.AppendLine("## People");
            foreach (var r in relationships)
            {
                var stale = r.LastContactedAt.HasValue
                    ? $"last contact {(DateTime.UtcNow - r.LastContactedAt.Value).Days}d ago"
                    : "no contact logged";
                sb.AppendLine($"- {r.Name} [{r.Circle}] importance {r.Importance}/10, {stale}");
            }
        }

        if (sb.Length == 0) sb.AppendLine("(No life data recorded yet.)");

        return new LifeContext(sb.ToString(), voice.Count + checkIns.Count);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var sb = new StringBuilder();
        await foreach (var token in _ai.StreamExecuteAsync(system, [new ConversationMessage("user", user)], null, ct))
            sb.Append(token);

        var text = sb.ToString();
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("The AI service did not return a response.");
        return text;
    }

    private static double Avg<T>(List<T> items, Func<T, double> pick) => items.Count == 0 ? 0 : items.Average(pick);

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int? Int(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            ? (int)Math.Round(d) : null;

    private static double? Dbl(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static IEnumerable<JsonElement> Arr(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : [];

    private static List<string> StrList(JsonElement el, string name) =>
        Arr(el, name)
            .Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : Str(x, "text"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Take(8).ToList();

    private static int? Clamp(int? value, int min, int max) =>
        value.HasValue ? Math.Clamp(value.Value, min, max) : null;

    private static string? Clip(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max].TrimEnd() + "…");
}
