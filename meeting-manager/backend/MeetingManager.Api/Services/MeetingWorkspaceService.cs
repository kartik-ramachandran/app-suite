using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;
using MeetingManager.Api.Models;

namespace MeetingManager.Api.Services;

public record PrepPack(
    string Objective,
    string Agenda,
    List<string> TalkingPoints,
    List<string> Questions,
    List<string> Risks,
    List<string> DecisionsNeeded,
    List<string> FollowUpItems,
    string OpeningStatement,
    string ClosingStatement
);

public record FollowUpPack(
    string Summary,
    List<string> Decisions,
    List<ActionItem> Actions,
    List<string> OpenQuestions,
    List<string> Risks,
    string FollowUpEmail,
    string NextMeetingAgenda
);

public record ActionItem(string Title, string Owner, string? DueDate, string Priority);

public class MeetingWorkspaceService
{
    private readonly IAIService _ai;
    private readonly PromptDbContext _db;
    private readonly ILogger<MeetingWorkspaceService> _logger;

    public MeetingWorkspaceService(IAIService ai, PromptDbContext db, ILogger<MeetingWorkspaceService> logger)
    {
        _ai = ai;
        _db = db;
        _logger = logger;
    }

    // ── Meeting Prep ──────────────────────────────────────────────────────────

    public async Task<PrepPack> GeneratePrepPackAsync(
        string title, string? description, List<AttendeeDto> attendees,
        string? previousNotes, List<string>? openTasks)
    {
        var attendeeList = string.Join(", ", attendees.Select(a => string.IsNullOrEmpty(a.Name) ? a.Email : $"{a.Name} ({a.Email})"));
        var tasksText = openTasks?.Count > 0 ? string.Join("\n", openTasks.Select(t => $"- {t}")) : "None";
        var previousText = !string.IsNullOrWhiteSpace(previousNotes) ? previousNotes : "No previous notes available.";

        var system = @"You are a senior personal AI work agent. Your role is to prepare users for meetings with comprehensive, actionable insights.
Return a JSON object with exactly this structure (no markdown fences):
{
  ""objective"": ""string"",
  ""agenda"": ""string (numbered list as text)"",
  ""talkingPoints"": [""string""],
  ""questions"": [""string""],
  ""risks"": [""string""],
  ""decisionsNeeded"": [""string""],
  ""followUpItems"": [""string""],
  ""openingStatement"": ""string"",
  ""closingStatement"": ""string""
}";

        var user = $@"Prepare me for this meeting:

Meeting title: {title}
Description: {description ?? "No description provided."}
Attendees: {attendeeList}
Previous notes / context: {previousText}
Open tasks: {tasksText}

Generate a complete preparation pack with objective, agenda, talking points, questions to ask, risks to raise, decisions needed, follow-up items to watch for, and suggested opening/closing statements.";

        var resp = await _ai.TestPromptAsync(new PromptTestRequest { PromptContent = system, UserInput = user });
        return ParsePrepPack(resp.Result);
    }

    // ── Follow-up Generation ──────────────────────────────────────────────────

    public async Task<FollowUpPack> GenerateFollowUpAsync(string title, string roughNotes, List<AttendeeDto> attendees)
    {
        var attendeeList = string.Join(", ", attendees.Select(a => string.IsNullOrEmpty(a.Name) ? a.Email : a.Name));

        var system = @"You are a meeting follow-up assistant. Using rough meeting notes, generate a structured follow-up pack.
Return a JSON object with exactly this structure (no markdown fences):
{
  ""summary"": ""string"",
  ""decisions"": [""string""],
  ""actions"": [{ ""title"": ""string"", ""owner"": ""string"", ""dueDate"": ""string or null"", ""priority"": ""high|medium|low"" }],
  ""openQuestions"": [""string""],
  ""risks"": [""string""],
  ""followUpEmail"": ""string (full email draft)"",
  ""nextMeetingAgenda"": ""string""
}";

        var user = $@"Meeting: {title}
Attendees: {attendeeList}

Rough meeting notes:
{roughNotes}

Generate the complete follow-up pack including a professional follow-up email draft.";

        var resp = await _ai.TestPromptAsync(new PromptTestRequest { PromptContent = system, UserInput = user });
        return ParseFollowUpPack(resp.Result);
    }

    // ── Task Extraction ───────────────────────────────────────────────────────

    public async Task<List<ActionItem>> ExtractTasksAsync(string notes)
    {
        var system = @"You are a task extraction assistant. Extract all action items and tasks from meeting notes.
Return a JSON array (no markdown fences):
[{ ""title"": ""string"", ""owner"": ""string"", ""dueDate"": ""string or null"", ""priority"": ""high|medium|low"" }]";

        var resp = await _ai.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = system,
            UserInput = $"Extract all tasks from these meeting notes:\n\n{notes}"
        });

        return ParseActionItems(resp.Result);
    }

    // ── Workspace CRUD ────────────────────────────────────────────────────────

    public async Task<WorkspaceRow?> GetOrCreateWorkspaceAsync(string userId, string? providerEventId, string eventTitle, string? startDt, string? endDt, string attsJson)
    {
        var pid = providerEventId ?? "";
        if (!string.IsNullOrEmpty(pid))
        {
            var rows = await _db.Database.SqlQuery<WorkspaceRow>(
                $@"SELECT ""Id"", ""UserId"", ""ProviderEventId"", ""EventTitle"", ""EventStart"", ""EventEnd"", ""AttendeesJson"", ""Objective"", ""PrepPackJson"", ""Notes"", ""Summary"", ""DecisionsJson"", ""RisksJson"", ""ActionsJson"", ""FollowUpDraft"", ""CreatedAt"", ""UpdatedAt""
                   FROM ""MeetingWorkspaces"" WHERE ""UserId""={userId} AND ""ProviderEventId""={pid}").ToListAsync();
            var existing = rows.FirstOrDefault();
            if (existing != null) return existing;
        }

        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""MeetingWorkspaces"" (""UserId"", ""ProviderEventId"", ""EventTitle"", ""EventStart"", ""EventEnd"", ""AttendeesJson"", ""CreatedAt"", ""UpdatedAt"")
            VALUES ({userId}, {pid}, {eventTitle}, {startDt ?? ""}, {endDt ?? ""}, {attsJson}, {now}, {now})");

        var newRows = await _db.Database.SqlQuery<WorkspaceRow>(
            $@"SELECT ""Id"", ""UserId"", ""ProviderEventId"", ""EventTitle"", ""EventStart"", ""EventEnd"", ""AttendeesJson"", ""Objective"", ""PrepPackJson"", ""Notes"", ""Summary"", ""DecisionsJson"", ""RisksJson"", ""ActionsJson"", ""FollowUpDraft"", ""CreatedAt"", ""UpdatedAt""
               FROM ""MeetingWorkspaces"" WHERE ""UserId""={userId} AND ""ProviderEventId""={pid} ORDER BY ""Id"" DESC LIMIT 1").ToListAsync();
        return newRows.FirstOrDefault();
    }

    public async Task<WorkspaceRow?> GetWorkspaceAsync(int id, string userId)
        => (await _db.Database.SqlQuery<WorkspaceRow>($@"SELECT ""Id"", ""UserId"", ""ProviderEventId"", ""EventTitle"", ""EventStart"", ""EventEnd"", ""AttendeesJson"", ""Objective"", ""PrepPackJson"", ""Notes"", ""Summary"", ""DecisionsJson"", ""RisksJson"", ""ActionsJson"", ""FollowUpDraft"", ""CreatedAt"", ""UpdatedAt"" FROM ""MeetingWorkspaces"" WHERE ""Id""={id} AND ""UserId""={userId}").ToListAsync()).FirstOrDefault();

    public async Task<List<WorkspaceRow>> GetWorkspacesAsync(string userId)
        => await _db.Database.SqlQuery<WorkspaceRow>($@"SELECT ""Id"", ""UserId"", ""ProviderEventId"", ""EventTitle"", ""EventStart"", ""EventEnd"", ""AttendeesJson"", ""Objective"", ""PrepPackJson"", ""Notes"", ""Summary"", ""DecisionsJson"", ""RisksJson"", ""ActionsJson"", ""FollowUpDraft"", ""CreatedAt"", ""UpdatedAt"" FROM ""MeetingWorkspaces"" WHERE ""UserId""={userId} ORDER BY ""EventStart"" DESC LIMIT 100").ToListAsync();

    public async Task SavePrepPackAsync(int workspaceId, PrepPack pack, string objective)
    {
        var packJson = JsonSerializer.Serialize(pack);
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""MeetingWorkspaces"" SET ""PrepPackJson""={packJson}, ""Objective""={objective}, ""UpdatedAt""={now} WHERE ""Id""={workspaceId}");
    }

    public async Task SaveFollowUpAsync(int workspaceId, FollowUpPack pack)
    {
        var decisionsJson = JsonSerializer.Serialize(pack.Decisions);
        var risksJson = JsonSerializer.Serialize(pack.Risks);
        var actionsJson = JsonSerializer.Serialize(pack.Actions);
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""MeetingWorkspaces"" SET
                ""Summary""={pack.Summary}, ""FollowUpDraft""={pack.FollowUpEmail},
                ""DecisionsJson""={decisionsJson}, ""RisksJson""={risksJson}, ""ActionsJson""={actionsJson},
                ""UpdatedAt""={now}
            WHERE ""Id""={workspaceId}");
    }

    public async Task SaveNotesAsync(int workspaceId, string notes)
    {
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""MeetingWorkspaces"" SET ""Notes""={notes}, ""UpdatedAt""={now} WHERE ""Id""={workspaceId}");
    }

    // ── Tasks & Follow-ups CRUD ───────────────────────────────────────────────

    public async Task SaveTasksAsync(string userId, int workspaceId, List<ActionItem> tasks)
    {
        foreach (var t in tasks)
        {
            var now = DateTime.UtcNow.ToString("o");
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""MeetingTasks"" (""UserId"", ""WorkspaceId"", ""Title"", ""Owner"", ""DueDate"", ""Status"", ""Priority"", ""Source"", ""CreatedAt"", ""UpdatedAt"")
                VALUES ({userId}, {workspaceId}, {t.Title}, {t.Owner ?? ""}, {t.DueDate ?? ""}, {"open"}, {t.Priority}, {"meeting"}, {now}, {now})");
        }
    }

    public async Task<List<TaskRow>> GetTasksAsync(string userId, int? workspaceId = null)
    {
        if (workspaceId.HasValue)
            return await _db.Database.SqlQuery<TaskRow>(
                $@"SELECT ""Id"", ""UserId"", ""WorkspaceId"", ""Title"", ""Description"", ""Status"", ""Priority"", ""DueDate"", ""Owner"", ""Source"", ""CreatedAt"" FROM ""MeetingTasks"" WHERE ""UserId""={userId} AND ""WorkspaceId""={workspaceId.Value} ORDER BY ""CreatedAt"" DESC").ToListAsync();
        return await _db.Database.SqlQuery<TaskRow>(
            $@"SELECT ""Id"", ""UserId"", ""WorkspaceId"", ""Title"", ""Description"", ""Status"", ""Priority"", ""DueDate"", ""Owner"", ""Source"", ""CreatedAt"" FROM ""MeetingTasks"" WHERE ""UserId""={userId} ORDER BY ""CreatedAt"" DESC").ToListAsync();
    }

    public async Task UpdateTaskStatusAsync(int taskId, string userId, string status)
    {
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""MeetingTasks"" SET ""Status""={status}, ""UpdatedAt""={now} WHERE ""Id""={taskId} AND ""UserId""={userId}");
    }

    public async Task<List<FollowUpRow>> GetFollowUpsAsync(string userId, string? status = null)
    {
        if (status != null)
            return await _db.Database.SqlQuery<FollowUpRow>(
                $@"SELECT ""Id"", ""UserId"", ""WorkspaceId"", ""Title"", ""Details"", ""Owner"", ""DueDate"", ""Status"", ""FollowUpType"", ""CreatedAt"" FROM ""MeetingFollowUps"" WHERE ""UserId""={userId} AND ""Status""={status} ORDER BY ""CreatedAt"" DESC").ToListAsync();
        return await _db.Database.SqlQuery<FollowUpRow>(
            $@"SELECT ""Id"", ""UserId"", ""WorkspaceId"", ""Title"", ""Details"", ""Owner"", ""DueDate"", ""Status"", ""FollowUpType"", ""CreatedAt"" FROM ""MeetingFollowUps"" WHERE ""UserId""={userId} ORDER BY ""CreatedAt"" DESC").ToListAsync();
    }

    public async Task SaveFollowUpsFromPackAsync(string userId, int workspaceId, FollowUpPack pack)
    {
        foreach (var action in pack.Actions)
        {
            var now = DateTime.UtcNow.ToString("o");
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""MeetingFollowUps"" (""UserId"", ""WorkspaceId"", ""Title"", ""Owner"", ""DueDate"", ""Status"", ""FollowUpType"", ""CreatedAt"", ""UpdatedAt"")
                VALUES ({userId}, {workspaceId}, {action.Title}, {action.Owner ?? ""}, {action.DueDate ?? ""}, {"pending"}, {"action"}, {now}, {now})");
        }
        foreach (var q in pack.OpenQuestions)
        {
            var now = DateTime.UtcNow.ToString("o");
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO ""MeetingFollowUps"" (""UserId"", ""WorkspaceId"", ""Title"", ""Status"", ""FollowUpType"", ""CreatedAt"", ""UpdatedAt"")
                VALUES ({userId}, {workspaceId}, {q}, {"pending"}, {"question"}, {now}, {now})");
        }
    }

    public async Task UpdateFollowUpStatusAsync(int followUpId, string userId, string status)
    {
        var now = DateTime.UtcNow.ToString("o");
        await _db.Database.ExecuteSqlInterpolatedAsync($@"UPDATE ""MeetingFollowUps"" SET ""Status""={status}, ""UpdatedAt""={now} WHERE ""Id""={followUpId} AND ""UserId""={userId}");
    }

    // ── Parsers ───────────────────────────────────────────────────────────────

    private PrepPack ParsePrepPack(string raw)
    {
        try
        {
            var json = raw.Trim();
            if (json.StartsWith("```")) json = string.Join("\n", json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new PrepPack(
                r.TryGetProperty("objective", out var obj) ? obj.GetString() ?? "" : "",
                r.TryGetProperty("agenda", out var ag) ? ag.GetString() ?? "" : "",
                GetStringList(r, "talkingPoints"),
                GetStringList(r, "questions"),
                GetStringList(r, "risks"),
                GetStringList(r, "decisionsNeeded"),
                GetStringList(r, "followUpItems"),
                r.TryGetProperty("openingStatement", out var op) ? op.GetString() ?? "" : "",
                r.TryGetProperty("closingStatement", out var cl) ? cl.GetString() ?? "" : ""
            );
        }
        catch { return new PrepPack("", raw, new(), new(), new(), new(), new(), "", ""); }
    }

    private FollowUpPack ParseFollowUpPack(string raw)
    {
        try
        {
            var json = raw.Trim();
            if (json.StartsWith("```")) json = string.Join("\n", json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new FollowUpPack(
                r.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "",
                GetStringList(r, "decisions"),
                ParseActionItems(r.TryGetProperty("actions", out var acts) ? acts.GetRawText() : "[]"),
                GetStringList(r, "openQuestions"),
                GetStringList(r, "risks"),
                r.TryGetProperty("followUpEmail", out var em) ? em.GetString() ?? "" : "",
                r.TryGetProperty("nextMeetingAgenda", out var nma) ? nma.GetString() ?? "" : ""
            );
        }
        catch { return new FollowUpPack(raw, new(), new(), new(), new(), "", ""); }
    }

    private List<ActionItem> ParseActionItems(string raw)
    {
        try
        {
            var json = raw.Trim();
            if (json.StartsWith("```")) json = string.Join("\n", json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                      : doc.RootElement.TryGetProperty("actions", out var a) ? a : default;
            if (arr.ValueKind != JsonValueKind.Array) return new();
            return arr.EnumerateArray().Select(a => new ActionItem(
                a.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                a.TryGetProperty("owner", out var o) ? o.GetString() ?? "" : "",
                a.TryGetProperty("dueDate", out var d) ? d.GetString() : null,
                a.TryGetProperty("priority", out var p) ? p.GetString() ?? "medium" : "medium"
            )).ToList();
        }
        catch { return new(); }
    }

    private static List<string> GetStringList(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array) return new();
        return el.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
    }
}

// DB row types
public class WorkspaceRow
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string? ProviderEventId { get; set; }
    public string EventTitle { get; set; } = "";
    public string? EventStart { get; set; }
    public string? EventEnd { get; set; }
    public string AttendeesJson { get; set; } = "[]";
    public string? Objective { get; set; }
    public string? PrepPackJson { get; set; }
    public string? Notes { get; set; }
    public string? Summary { get; set; }
    public string DecisionsJson { get; set; } = "[]";
    public string RisksJson { get; set; } = "[]";
    public string ActionsJson { get; set; } = "[]";
    public string? FollowUpDraft { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

public class TaskRow
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int? WorkspaceId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string Status { get; set; } = "open";
    public string Priority { get; set; } = "medium";
    public string? DueDate { get; set; }
    public string? Owner { get; set; }
    public string Source { get; set; } = "manual";
    public string CreatedAt { get; set; } = "";
}

public class FollowUpRow
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public int? WorkspaceId { get; set; }
    public string Title { get; set; } = "";
    public string? Details { get; set; }
    public string? Owner { get; set; }
    public string? DueDate { get; set; }
    public string Status { get; set; } = "pending";
    public string FollowUpType { get; set; } = "action";
    public string CreatedAt { get; set; } = "";
}
