using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;
using MeetingManager.Api.Models;

namespace MeetingManager.Api.Services;

public record DailyBriefing(
    string Summary,
    List<string> MeetingsNeedingPrep,
    List<string> PendingFollowUps,
    string? BestFocusBlock,
    List<string> Risks,
    List<string> SuggestedPriorities
);

public class AvatarAgentService
{
    private readonly IAIService _ai;
    private readonly PromptDbContext _db;

    public AvatarAgentService(IAIService ai, PromptDbContext db)
    {
        _ai = ai;
        _db = db;
    }

    public async Task<DailyBriefing> GetDailyBriefingAsync(string userId, string timezone)
    {
        var now = DateTime.UtcNow;
        var todayStart = now.Date.ToString("o");
        var todayEnd = now.Date.AddDays(1).ToString("o");

        var todayEvents = await _db.Database.SqlQuery<BusyBlockRow>($@"
            SELECT ""Title"", ""StartDateTime"", ""EndDateTime"", ""BusyStatus""
            FROM ""CalendarEvents""
            WHERE ""UserId""={userId} AND ""StartDateTime"" >= {todayStart} AND ""StartDateTime"" < {todayEnd}
            ORDER BY ""StartDateTime""").ToListAsync();

        var pendingFollowUps = await _db.Database.SqlQuery<FollowUpRow>($@"
            SELECT ""Id"", ""UserId"", ""WorkspaceId"", ""Title"", ""Details"", ""Owner"", ""DueDate"", ""Status"", ""FollowUpType"", ""CreatedAt""
            FROM ""MeetingFollowUps"" WHERE ""UserId""={userId} AND ""Status""={"pending"} ORDER BY ""CreatedAt"" DESC LIMIT 10").ToListAsync();

        var openTasks = await _db.Database.SqlQuery<TaskRow>($@"
            SELECT ""Id"", ""UserId"", ""WorkspaceId"", ""Title"", ""Description"", ""Status"", ""Priority"", ""DueDate"", ""Owner"", ""Source"", ""CreatedAt""
            FROM ""MeetingTasks"" WHERE ""UserId""={userId} AND ""Status""={"open"} ORDER BY ""CreatedAt"" DESC LIMIT 10").ToListAsync();

        var meetingList = string.Join("\n", todayEvents.Select(e => $"- {DateTime.Parse(e.StartDateTime):h:mm tt}: {e.Title}"));
        var followUpList = string.Join("\n", pendingFollowUps.Take(5).Select(f => $"- {f.Title}"));
        var taskList = string.Join("\n", openTasks.Take(5).Select(t => $"- {t.Title}"));

        var system = @"You are a personal AI work agent delivering a daily briefing. Be concise, practical, and warm.
Return JSON (no markdown fences):
{
  ""summary"": ""string"",
  ""meetingsNeedingPrep"": [""string""],
  ""pendingFollowUps"": [""string""],
  ""bestFocusBlock"": ""string or null"",
  ""risks"": [""string""],
  ""suggestedPriorities"": [""string""]
}";

        var user = $@"Today's date: {now:dddd, d MMMM yyyy}
Timezone: {timezone}

Today's meetings:
{(string.IsNullOrEmpty(meetingList) ? "No meetings today." : meetingList)}

Pending follow-ups:
{(string.IsNullOrEmpty(followUpList) ? "None." : followUpList)}

Open tasks:
{(string.IsNullOrEmpty(taskList) ? "None." : taskList)}

Generate a daily briefing with: day summary, meetings needing preparation, pending follow-ups, best focus block recommendation, risks or concerns, and suggested priorities.";

        try
        {
            var resp = await _ai.TestPromptAsync(new PromptTestRequest { PromptContent = system, UserInput = user });
            return ParseBriefing(resp.Result);
        }
        catch
        {
            return new DailyBriefing(
                $"You have {todayEvents.Count} meeting{(todayEvents.Count != 1 ? "s" : "")} today and {pendingFollowUps.Count} pending follow-up{(pendingFollowUps.Count != 1 ? "s" : "")}.",
                new(), pendingFollowUps.Take(3).Select(f => f.Title).ToList(), null, new(), new()
            );
        }
    }

    public async Task<string> AskAsync(string userId, string question, string? conversationHistory = null)
    {
        var now = DateTime.UtcNow;
        var todayStart = now.Date.ToString("o");
        var todayEnd = now.Date.AddDays(7).ToString("o");

        var events = await _db.Database.SqlQuery<BusyBlockRow>($@"
            SELECT ""Title"", ""StartDateTime"", ""EndDateTime"", ""BusyStatus""
            FROM ""CalendarEvents""
            WHERE ""UserId""={userId} AND ""StartDateTime"" >= {todayStart} AND ""StartDateTime"" < {todayEnd}
            ORDER BY ""StartDateTime"" LIMIT 20").ToListAsync();

        var followUps = await _db.Database.SqlQuery<FollowUpRow>($@"
            SELECT ""Id"", ""UserId"", ""WorkspaceId"", ""Title"", ""Details"", ""Owner"", ""DueDate"", ""Status"", ""FollowUpType"", ""CreatedAt""
            FROM ""MeetingFollowUps"" WHERE ""UserId""={userId} AND ""Status""={"pending"} ORDER BY ""CreatedAt"" DESC LIMIT 5").ToListAsync();

        var calContext = string.Join("\n", events.Select(e => $"- {(DateTime.TryParse(e.StartDateTime, out var dt) ? dt.ToString("ddd d MMM h:mm tt") : e.StartDateTime)}: {e.Title}"));
        var fuContext = string.Join("\n", followUps.Select(f => $"- {f.Title}"));

        var system = @"You are a personal AI work agent — a smart, concise executive assistant. You have access to the user's calendar and follow-ups.
Answer the user's question directly. Keep responses conversational and brief (2-4 sentences).
For scheduling questions, check the calendar carefully and give specific slot recommendations.";

        var history = !string.IsNullOrWhiteSpace(conversationHistory) ? $"\nConversation so far:\n{conversationHistory}\n" : "";
        var user = $@"Today: {now:ddd d MMM yyyy, h:mm tt}

Calendar (next 7 days):
{(string.IsNullOrEmpty(calContext) ? "No events." : calContext)}

Pending follow-ups:
{(string.IsNullOrEmpty(fuContext) ? "None." : fuContext)}
{history}
User asks: {question}";

        var resp = await _ai.TestPromptAsync(new PromptTestRequest { PromptContent = system, UserInput = user });
        return resp.Result.Trim();
    }

    private static DailyBriefing ParseBriefing(string raw)
    {
        try
        {
            var json = raw.Trim();
            if (json.StartsWith("```")) json = string.Join("\n", json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new DailyBriefing(
                r.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                GetList(r, "meetingsNeedingPrep"),
                GetList(r, "pendingFollowUps"),
                r.TryGetProperty("bestFocusBlock", out var bf) ? bf.GetString() : null,
                GetList(r, "risks"),
                GetList(r, "suggestedPriorities")
            );
        }
        catch { return new DailyBriefing(raw, new(), new(), null, new(), new()); }
    }

    private static List<string> GetList(System.Text.Json.JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var el) || el.ValueKind != System.Text.Json.JsonValueKind.Array) return new();
        return el.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
    }
}
