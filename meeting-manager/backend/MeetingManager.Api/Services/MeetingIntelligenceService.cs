using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;
using MeetingManager.Api.Models;

namespace MeetingManager.Api.Services;

/// <summary>
/// Turns a finished transcript into the things a person actually wanted from
/// the meeting: what was decided, who owes what, what changed since last time.
///
/// Runs automatically when a meeting closes. A summary you have to go and ask
/// for is a summary nobody reads, and the capture layer we already have is only
/// worth its cost if something useful comes out the other end.
/// </summary>
public class MeetingIntelligenceService
{
    private readonly PromptDbContext _db;
    private readonly AIServiceManager _ai;
    private readonly ILogger<MeetingIntelligenceService> _logger;

    public MeetingIntelligenceService(PromptDbContext db, AIServiceManager ai, ILogger<MeetingIntelligenceService> logger)
    {
        _db = db;
        _ai = ai;
        _logger = logger;
    }

    // ── Series matching ─────────────────────────────────────────────────────

    /// <summary>
    /// Strip the parts of a title that change every week so "Standup — 12 Aug"
    /// and "Standup (Aug 19)" land in the same series.
    /// </summary>
    public static string NormalizeTitle(string title)
    {
        var t = (title ?? string.Empty).ToLowerInvariant();
        t = Regex.Replace(t, @"\b\d{1,2}[/-]\d{1,2}([/-]\d{2,4})?\b", " ");
        t = Regex.Replace(t, @"\b\d{4}-\d{2}-\d{2}\b", " ");

        // A day number next to a month name goes with it. Handled before the
        // bare month rule, otherwise "12 Aug" loses "Aug" and keeps "12",
        // which would put this week's standup in its own series.
        const string months = @"(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?";
        t = Regex.Replace(t, $@"\b\d{{1,2}}(st|nd|rd|th)?\s+{months}\b", " ");
        t = Regex.Replace(t, $@"\b{months}\s*\d{{1,2}}(st|nd|rd|th)?\b", " ");
        t = Regex.Replace(t, $@"\b{months}\s*\d{{0,4}}\b", " ");

        t = Regex.Replace(t, @"\b(mon|tue|wed|thu|fri|sat|sun)[a-z]*\b", " ");
        t = Regex.Replace(t, @"\b(week|wk|w)\s*#?\d+\b", " ");
        t = Regex.Replace(t, @"#\d+", " ");
        t = Regex.Replace(t, @"\bq[1-4]\b", " ");
        t = Regex.Replace(t, @"[^\w\s]", " ");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t;
    }

    /// <summary>
    /// Place a session in its series, creating one on first sight. Returns null
    /// when the title normalises to nothing meaningful — a bad link makes the
    /// "what changed" view lie, which is worse than having no link at all.
    /// </summary>
    public async Task<MeetingSeries?> AttachToSeriesAsync(MeetingSession session, CancellationToken ct = default)
    {
        var normalized = NormalizeTitle(session.Title);
        if (normalized.Length < 3) return null;

        var series = await _db.MeetingSeries
            .FirstOrDefaultAsync(s => s.UserId == session.UserId && s.NormalizedTitle == normalized, ct);

        if (series == null)
        {
            series = new MeetingSeries
            {
                UserId = session.UserId,
                Title = session.Title,
                NormalizedTitle = normalized,
                FirstMeetingAt = session.StartedAt ?? session.ScheduledStart,
                SessionCount = 0
            };
            _db.MeetingSeries.Add(series);
        }

        series.SessionCount += 1;
        series.LastMeetingAt = session.EndedAt ?? DateTime.UtcNow;
        series.UpdatedAt = DateTime.UtcNow;

        session.MeetingSeriesId = series.Id;
        await _db.SaveChangesAsync(ct);
        return series;
    }

    // ── Outcome generation ──────────────────────────────────────────────────

    /// <summary>
    /// Build (or rebuild) the outcome for a meeting. Idempotent — re-running
    /// replaces the previous outcome rather than accumulating duplicates.
    /// </summary>
    public async Task<MeetingOutcome> GenerateOutcomeAsync(MeetingSession session, CancellationToken ct = default)
    {
        var outcome = await _db.MeetingOutcomes.FirstOrDefaultAsync(o => o.MeetingSessionId == session.Id, ct)
            ?? new MeetingOutcome { MeetingSessionId = session.Id, UserId = session.UserId };

        if (outcome.Id is { Length: > 0 } && _db.Entry(outcome).State == EntityState.Detached)
            _db.MeetingOutcomes.Add(outcome);
        else if (_db.Entry(outcome).State == EntityState.Detached)
            _db.MeetingOutcomes.Add(outcome);

        if (string.IsNullOrWhiteSpace(session.Transcript))
        {
            outcome.Status = "failed";
            outcome.ErrorMessage = "No transcript was captured.";
            outcome.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return outcome;
        }

        try
        {
            var extracted = await ExtractAsync(session.Transcript, ct);

            outcome.Summary = extracted.Summary;
            outcome.DecisionsJson = JsonSerializer.Serialize(extracted.Decisions);
            outcome.ActionItemsJson = JsonSerializer.Serialize(extracted.ActionItems);
            outcome.OpenQuestionsJson = JsonSerializer.Serialize(extracted.OpenQuestions);
            outcome.SpeakerStatsJson = JsonSerializer.Serialize(ComputeSpeakerStats(session.Transcript));

            // Persist action items as rows so they can be queried and carried.
            await SyncActionItemsAsync(session, extracted.ActionItems, ct);

            if (session.MeetingSeriesId != null)
                outcome.ChangesSinceLast = await BuildSeriesDeltaAsync(session, extracted, ct);

            var followUp = await DraftFollowUpAsync(session, extracted, ct);
            outcome.FollowUpSubject = followUp.Subject;
            outcome.FollowUpBody = followUp.Body;

            outcome.Status = "ready";
            outcome.ErrorMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build outcome for meeting {Id}", session.Id);
            outcome.Status = "failed";
            outcome.ErrorMessage = ex.Message;
        }

        outcome.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return outcome;
    }

    public record ActionItemDto(string Text, string? Owner, string? DueDate);
    public record DecisionDto(string Text, string? Context);
    public record ExtractedMeeting(
        string Summary,
        List<DecisionDto> Decisions,
        List<ActionItemDto> ActionItems,
        List<string> OpenQuestions);

    private async Task<ExtractedMeeting> ExtractAsync(string transcript, CancellationToken ct)
    {
        var system =
            "You extract structured outcomes from meeting transcripts. Return ONLY a JSON object:\n" +
            "{\"summary\":\"<= 200 words, what happened and why it matters\"," +
            "\"decisions\":[{\"text\":\"what was decided\",\"context\":\"why\"}]," +
            "\"actionItems\":[{\"text\":\"what must be done\",\"owner\":\"who or null\",\"dueDate\":\"YYYY-MM-DD or null\"}]," +
            "\"openQuestions\":[\"raised but not resolved\"]}\n" +
            "Only include items genuinely present. Empty arrays are correct when nothing qualifies. " +
            "Never invent an owner — use null when it was not said.";

        // Long meetings blow the context window; the head and tail carry the
        // agenda and the commitments, which is what we are extracting.
        var text = Clamp(transcript, 48_000);

        var sb = new System.Text.StringBuilder();
        await foreach (var t in _ai.StreamExecuteAsync(system, [new ConversationMessage("user", text)], null, ct))
            sb.Append(t);

        var json = JsonSchemaValidator.ExtractJson(sb.ToString())
            ?? throw new InvalidOperationException("The model did not return usable JSON for this meeting.");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return new ExtractedMeeting(
            Summary: Str(root, "summary") ?? "",
            Decisions: Arr(root, "decisions")
                .Select(e => new DecisionDto(Str(e, "text") ?? "", Str(e, "context")))
                .Where(d => !string.IsNullOrWhiteSpace(d.Text)).ToList(),
            ActionItems: Arr(root, "actionItems")
                .Select(e => new ActionItemDto(Str(e, "text") ?? "", Str(e, "owner"), Str(e, "dueDate")))
                .Where(a => !string.IsNullOrWhiteSpace(a.Text)).ToList(),
            OpenQuestions: Arr(root, "openQuestions")
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : Str(e, "text"))
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList());
    }

    // ── Action items ────────────────────────────────────────────────────────

    private async Task SyncActionItemsAsync(MeetingSession session, List<ActionItemDto> items, CancellationToken ct)
    {
        var existing = await _db.MeetingActionItems
            .Where(a => a.MeetingSessionId == session.Id)
            .ToListAsync(ct);
        _db.MeetingActionItems.RemoveRange(existing);

        // Anything still open from the previous session in this series gets
        // carried, with a counter. The counter is the number that starts
        // arguments in the next standup, which is the point.
        var carried = new List<MeetingActionItem>();
        if (session.MeetingSeriesId != null)
        {
            var previousSessionId = await _db.MeetingSessions
                .Where(s => s.MeetingSeriesId == session.MeetingSeriesId && s.Id != session.Id)
                .OrderByDescending(s => s.EndedAt ?? s.ScheduledStart)
                .Select(s => s.Id)
                .FirstOrDefaultAsync(ct);

            if (previousSessionId != null)
            {
                var stillOpen = await _db.MeetingActionItems
                    .Where(a => a.MeetingSessionId == previousSessionId && a.Status == "open")
                    .ToListAsync(ct);

                foreach (var prev in stillOpen)
                {
                    // Only carry what nobody restated — a repeat would double up.
                    if (items.Any(i => Similar(i.Text, prev.Text))) continue;

                    carried.Add(new MeetingActionItem
                    {
                        MeetingSessionId = session.Id,
                        MeetingSeriesId = session.MeetingSeriesId,
                        UserId = session.UserId,
                        Text = prev.Text,
                        Owner = prev.Owner,
                        DueDate = prev.DueDate,
                        Status = "open",
                        CarriedFromSessionId = prev.CarriedFromSessionId ?? previousSessionId,
                        CarryOverCount = prev.CarryOverCount + 1
                    });
                }
            }
        }

        var fresh = items.Select(i => new MeetingActionItem
        {
            MeetingSessionId = session.Id,
            MeetingSeriesId = session.MeetingSeriesId,
            UserId = session.UserId,
            Text = Clamp(i.Text, 1000),
            Owner = i.Owner is { Length: > 0 } ? Clamp(i.Owner, 200) : null,
            DueDate = ParseDate(i.DueDate),
            Status = "open"
        });

        _db.MeetingActionItems.AddRange(fresh);
        _db.MeetingActionItems.AddRange(carried);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Loose match so a restated commitment isn't tracked twice.</summary>
    private static bool Similar(string a, string b)
    {
        var wa = Tokens(a);
        var wb = Tokens(b);
        if (wa.Count == 0 || wb.Count == 0) return false;
        var overlap = wa.Intersect(wb).Count();
        return overlap >= Math.Min(wa.Count, wb.Count) * 0.6;
    }

    private static HashSet<string> Tokens(string s) =>
        Regex.Split(s.ToLowerInvariant(), @"\W+")
             .Where(w => w.Length > 3)
             .ToHashSet();

    // ── Series delta ────────────────────────────────────────────────────────

    private async Task<string?> BuildSeriesDeltaAsync(MeetingSession session, ExtractedMeeting current, CancellationToken ct)
    {
        var previousId = await _db.MeetingSessions
            .Where(s => s.MeetingSeriesId == session.MeetingSeriesId && s.Id != session.Id)
            .OrderByDescending(s => s.EndedAt ?? s.ScheduledStart)
            .Select(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (previousId == null) return null;

        var previous = await _db.MeetingOutcomes.FirstOrDefaultAsync(o => o.MeetingSessionId == previousId, ct);
        if (previous?.Summary == null) return null;

        var previousItems = await _db.MeetingActionItems
            .Where(a => a.MeetingSessionId == previousId)
            .ToListAsync(ct);

        var closed = previousItems.Where(p => p.Status == "done").ToList();
        var stillOpen = previousItems.Where(p => p.Status == "open" && !current.ActionItems.Any(c => Similar(c.Text, p.Text))).ToList();

        var parts = new List<string>();
        if (closed.Count > 0) parts.Add($"Closed since last time: {string.Join("; ", closed.Take(5).Select(c => c.Text))}");
        if (stillOpen.Count > 0) parts.Add($"Still open: {string.Join("; ", stillOpen.Take(5).Select(c => c.Text))}");

        try
        {
            var system =
                "Compare two consecutive meetings in the same recurring series. In at most 120 words, state what " +
                "moved, what stalled, and anything newly raised. Be concrete and unsentimental. Plain prose, no headings.";

            var user = $"PREVIOUS MEETING:\n{Clamp(previous.Summary, 4000)}\n\nTHIS MEETING:\n{Clamp(current.Summary, 4000)}\n\n" +
                       (parts.Count > 0 ? "ACTION ITEM STATUS:\n" + string.Join("\n", parts) : "");

            var sb = new System.Text.StringBuilder();
            await foreach (var t in _ai.StreamExecuteAsync(system, [new ConversationMessage("user", user)], null, ct))
                sb.Append(t);

            var result = sb.ToString().Trim();
            return result.Length > 0 ? result : string.Join("\n", parts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Series delta generation failed; falling back to the mechanical diff");
            return parts.Count > 0 ? string.Join("\n", parts) : null;
        }
    }

    // ── Follow-up email ─────────────────────────────────────────────────────

    public record FollowUpDraft(string Subject, string Body);

    public async Task<FollowUpDraft> DraftFollowUpAsync(MeetingSession session, ExtractedMeeting m, CancellationToken ct)
    {
        var subject = $"Notes & actions — {session.Title}";

        try
        {
            var system =
                "Write a short follow-up email after a meeting. Lead with one sentence of context, then " +
                "'Decisions' and 'Action items' as short bullet lists, with owners where known. " +
                "No greeting block, no sign-off, no invented commitments. Plain text with '- ' bullets.";

            var user = new
            {
                title = session.Title,
                summary = m.Summary,
                decisions = m.Decisions.Select(d => d.Text),
                actionItems = m.ActionItems.Select(a => new { a.Text, a.Owner, a.DueDate })
            };

            var sb = new System.Text.StringBuilder();
            await foreach (var t in _ai.StreamExecuteAsync(system,
                [new ConversationMessage("user", JsonSerializer.Serialize(user))], null, ct))
                sb.Append(t);

            var body = sb.ToString().Trim();
            if (body.Length > 0) return new FollowUpDraft(subject, body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Follow-up draft generation failed; using the mechanical fallback");
        }

        // Deterministic fallback so the Follow-Up Hub always has something.
        var lines = new List<string> { m.Summary, "" };
        if (m.Decisions.Count > 0)
        {
            lines.Add("Decisions");
            lines.AddRange(m.Decisions.Select(d => $"- {d.Text}"));
            lines.Add("");
        }
        if (m.ActionItems.Count > 0)
        {
            lines.Add("Action items");
            lines.AddRange(m.ActionItems.Select(a =>
                $"- {a.Text}" + (a.Owner is { Length: > 0 } ? $" ({a.Owner})" : "") + (a.DueDate is { Length: > 0 } ? $" — due {a.DueDate}" : "")));
        }
        return new FollowUpDraft(subject, string.Join("\n", lines).Trim());
    }

    // ── Speaker analytics ───────────────────────────────────────────────────

    public record SpeakerStat(string Name, int WordCount, double SharePct, int Commitments);

    /// <summary>
    /// Talk-time split from a diarised transcript ("Name: text" per line).
    /// Word count stands in for talk time — we do not get per-speaker durations
    /// from the capture layer, and words spoken tracks it closely enough to show
    /// who dominated the room.
    /// </summary>
    public static List<SpeakerStat> ComputeSpeakerStats(string transcript)
    {
        var perSpeaker = new Dictionary<string, (int Words, int Commitments)>(StringComparer.OrdinalIgnoreCase);
        var speakerLine = new Regex(@"^\s*(?:\[[^\]]*\]\s*)?([A-Z][\w' .-]{1,40}?)\s*:\s*(.+)$");
        var commitment = new Regex(@"\b(i'?ll|i will|i can|let me|i'?m going to|i'll take|on me|i'?ve got)\b", RegexOptions.IgnoreCase);

        foreach (var raw in (transcript ?? "").Split('\n'))
        {
            var m = speakerLine.Match(raw);
            if (!m.Success) continue;

            var name = m.Groups[1].Value.Trim();
            var text = m.Groups[2].Value;
            var words = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Length;
            var commits = commitment.IsMatch(text) ? 1 : 0;

            perSpeaker.TryGetValue(name, out var cur);
            perSpeaker[name] = (cur.Words + words, cur.Commitments + commits);
        }

        var total = perSpeaker.Values.Sum(v => v.Words);
        if (total == 0) return [];

        return perSpeaker
            .Select(kv => new SpeakerStat(kv.Key, kv.Value.Words, Math.Round(100.0 * kv.Value.Words / total, 1), kv.Value.Commitments))
            .OrderByDescending(s => s.WordCount)
            .ToList();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static IEnumerable<JsonElement> Arr(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : [];

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// <summary>Keep the head and the tail — the agenda and the commitments.</summary>
    private static string Clamp(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max) return s;
        var half = max / 2;
        return s[..half] + "\n\n…[transcript truncated]…\n\n" + s[^half..];
    }
}
