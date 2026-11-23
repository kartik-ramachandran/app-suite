using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;
using MeetingManager.Api.Models;

namespace MeetingManager.Api.Services;

public enum SlotDecision { Available, TentativelyAvailable, NotRecommended, Unavailable }

public record SlotSuggestion(
    string Start,
    string End,
    SlotDecision Decision,
    int Score,
    string Reason
);

public record AvailabilityResult(
    string RequestedStart,
    string RequestedEnd,
    SlotDecision Decision,
    int Score,
    string Reason,
    List<SlotSuggestion> Alternatives,
    string? SuggestedReply
);

public class AvailabilityService
{
    private readonly PromptDbContext _db;
    private readonly IAIService _ai;

    public AvailabilityService(PromptDbContext db, IAIService ai)
    {
        _db = db;
        _ai = ai;
    }

    // ── Check a specific slot ──────────────────────────────────────────────────

    public async Task<AvailabilityResult> CheckSlotAsync(
        string userId, DateTime start, DateTime end, string timezone,
        string? meetingType, bool requiresPrep, int bufferMinutes, int workStart = 9, int workEnd = 18)
    {
        var events = await GetBusyBlocksAsync(userId, start.AddHours(-4), end.AddHours(4));
        var (score, reason) = ScoreSlot(start, end, events, requiresPrep, bufferMinutes, workStart, workEnd);
        var decision = ScoreToDecision(score);

        List<SlotSuggestion> alternatives = new();
        if (decision != SlotDecision.Available)
            alternatives = await FindBestSlotsAsync(userId, start.Date, start.AddDays(5), (int)(end - start).TotalMinutes, timezone, requiresPrep, bufferMinutes, workStart, workEnd, 3);

        var suggestedReply = await GenerateSuggestedReplyAsync(decision, start, end, timezone, alternatives, meetingType);

        return new AvailabilityResult(
            start.ToString("o"), end.ToString("o"),
            decision, score, reason, alternatives, suggestedReply
        );
    }

    // ── Suggest best slots ────────────────────────────────────────────────────

    public async Task<List<SlotSuggestion>> FindBestSlotsAsync(
        string userId, DateTime fromDate, DateTime toDate,
        int durationMinutes, string timezone, bool requiresPrep, int bufferMinutes,
        int workStart, int workEnd, int maxResults = 5)
    {
        var events = await GetBusyBlocksAsync(userId, fromDate, toDate.AddDays(1));
        var candidates = new List<SlotSuggestion>();

        for (var day = fromDate.Date; day <= toDate.Date; day = day.AddDays(1))
        {
            if (day.DayOfWeek == DayOfWeek.Saturday || day.DayOfWeek == DayOfWeek.Sunday) continue;

            for (int hour = workStart; hour <= workEnd - (durationMinutes / 60); hour++)
            {
                for (int minute = 0; minute < 60; minute += 30)
                {
                    var slotStart = day.AddHours(hour).AddMinutes(minute);
                    var slotEnd = slotStart.AddMinutes(durationMinutes);
                    if (slotEnd.Hour > workEnd || slotEnd > day.AddHours(workEnd)) continue;

                    var (score, reason) = ScoreSlot(slotStart, slotEnd, events, requiresPrep, bufferMinutes, workStart, workEnd);
                    if (score > 40)
                    {
                        candidates.Add(new SlotSuggestion(
                            slotStart.ToString("o"), slotEnd.ToString("o"),
                            ScoreToDecision(score), score, reason
                        ));
                    }
                }
            }
        }

        return candidates.OrderByDescending(c => c.Score).Take(maxResults).ToList();
    }

    // ── Slot scoring algorithm (Section 24 of spec) ───────────────────────────

    private (int Score, string Reason) ScoreSlot(
        DateTime start, DateTime end, List<BusyBlock> events,
        bool requiresPrep, int bufferMinutes, int workStart, int workEnd)
    {
        int score = 100;
        var reasons = new List<string>();

        // Hard conflict
        var conflicts = events.Where(e => e.BusyStatus == "busy" && e.Start < end && e.End > start).ToList();
        if (conflicts.Any())
        {
            score -= 100;
            reasons.Add($"Hard conflict with: {conflicts.First().Title}");
            return (Math.Max(0, score), string.Join("; ", reasons));
        }

        // Back-to-back before
        var endsJustBefore = events.Where(e => e.BusyStatus == "busy" && e.End > start.AddMinutes(-bufferMinutes) && e.End <= start).ToList();
        if (endsJustBefore.Any()) { score -= 20; reasons.Add($"Back-to-back with {endsJustBefore.First().Title} ending just before"); }

        // Back-to-back after
        var startsJustAfter = events.Where(e => e.BusyStatus == "busy" && e.Start >= end && e.Start < end.AddMinutes(bufferMinutes)).ToList();
        if (startsJustAfter.Any()) { score -= 20; reasons.Add($"Back-to-back with {startsJustAfter.First().Title} starting just after"); }

        // No prep time (if required)
        if (requiresPrep)
        {
            var priorEvent = events.Where(e => e.BusyStatus == "busy" && e.End <= start).OrderByDescending(e => e.End).FirstOrDefault();
            if (priorEvent != null && (start - priorEvent.End).TotalMinutes < 15) { score -= 15; reasons.Add("No preparation time before meeting"); }
        }

        // During lunch (12-1 PM)
        if (start.Hour >= 12 && start.Hour < 13) { score -= 10; reasons.Add("Overlaps with typical lunch hour"); }

        // Outside preferred working hours
        if (start.Hour < workStart || end.Hour > workEnd) { score -= 30; reasons.Add("Outside working hours"); }

        // Heavy meeting day (3+ meetings that day)
        var dayEvents = events.Where(e => e.Start.Date == start.Date && e.BusyStatus == "busy").ToList();
        if (dayEvents.Count >= 3) { score -= 15; reasons.Add($"Heavy meeting day ({dayEvents.Count} meetings already)"); }

        // Good buffer both sides
        var hasGoodBufferBefore = !events.Any(e => e.BusyStatus == "busy" && e.End > start.AddMinutes(-30) && e.End <= start);
        var hasGoodBufferAfter = !events.Any(e => e.BusyStatus == "busy" && e.Start >= end && e.Start < end.AddMinutes(30));
        if (hasGoodBufferBefore && hasGoodBufferAfter) { score += 15; reasons.Add("Good buffer on both sides"); }

        // Morning focus slot (9-11 AM)
        if (start.Hour >= 9 && start.Hour < 11) { score += 10; reasons.Add("Morning focus slot"); }

        score = Math.Clamp(score, 0, 110);

        var reason = reasons.Count > 0 ? string.Join(". ", reasons) + "." : "Clean slot with no conflicts.";
        return (score, reason);
    }

    private static SlotDecision ScoreToDecision(int score) => score switch
    {
        >= 80 => SlotDecision.Available,
        >= 60 => SlotDecision.TentativelyAvailable,
        >= 40 => SlotDecision.NotRecommended,
        _ => SlotDecision.Unavailable
    };

    // ── AI reply generation ───────────────────────────────────────────────────

    private async Task<string?> GenerateSuggestedReplyAsync(
        SlotDecision decision, DateTime start, DateTime end, string timezone,
        List<SlotSuggestion> alternatives, string? meetingType)
    {
        try
        {
            var altText = alternatives.Count > 0
                ? string.Join("\n", alternatives.Take(3).Select((a, i) =>
                    $"{i + 1}. {DateTime.Parse(a.Start):ddd d MMM, h:mm tt} — {a.Reason}"))
                : "No alternatives found.";

            var system = "You are a professional calendar assistant. Write a concise, friendly availability reply.";
            var user = $@"The user was asked about scheduling at {start:ddd d MMM, h:mm tt} {timezone}.

Decision: {decision}
Reason: The slot analysis revealed issues.

Better alternatives:
{altText}

Write a professional, friendly reply (2-3 sentences) suggesting the best alternative time for a {meetingType ?? "meeting"}. Be conversational, not robotic.";

            var resp = await _ai.TestPromptAsync(new PromptTestRequest { PromptContent = system, UserInput = user });
            return resp.Result.Trim();
        }
        catch { return null; }
    }

    // ── Data helpers ──────────────────────────────────────────────────────────

    private async Task<List<BusyBlock>> GetBusyBlocksAsync(string userId, DateTime from, DateTime to)
    {
        var fromStr = from.ToString("o");
        var toStr = to.ToString("o");
        var rows = await _db.Database.SqlQuery<BusyBlockRow>($@"
            SELECT ""Title"", ""StartDateTime"", ""EndDateTime"", ""BusyStatus""
            FROM ""CalendarEvents""
            WHERE ""UserId""={userId} AND ""StartDateTime"" >= {fromStr} AND ""StartDateTime"" <= {toStr}
            ORDER BY ""StartDateTime""").ToListAsync();

        return rows.Select(r => new BusyBlock(
            r.Title,
            DateTime.TryParse(r.StartDateTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var s) ? s : DateTime.MinValue,
            DateTime.TryParse(r.EndDateTime, null, System.Globalization.DateTimeStyles.RoundtripKind, out var e) ? e : DateTime.MaxValue,
            r.BusyStatus
        )).Where(b => b.Start != DateTime.MinValue).ToList();
    }
}

public record BusyBlock(string Title, DateTime Start, DateTime End, string BusyStatus);

public class BusyBlockRow
{
    public string Title { get; set; } = "";
    public string StartDateTime { get; set; } = "";
    public string EndDateTime { get; set; } = "";
    public string BusyStatus { get; set; } = "busy";
}
