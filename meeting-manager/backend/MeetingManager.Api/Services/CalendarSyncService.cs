using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;

namespace MeetingManager.Api.Services;

public record CalendarEventDto(
    int Id,
    string Provider,
    string ProviderEventId,
    string Title,
    string? Description,
    string? Location,
    string? MeetingLink,
    string StartDateTime,
    string EndDateTime,
    string Timezone,
    string? OrganizerEmail,
    string? OrganizerName,
    string BusyStatus,
    bool IsAllDay,
    bool IsRecurring,
    List<AttendeeDto> Attendees,
    int? WorkspaceId,
    int? ReminderMinutes = null,
    bool CreatedHere = false
);

public record AttendeeDto(string Name, string Email, string ResponseStatus, bool IsOrganizer);

/// <summary>
/// Calendar aggregation + two-way sync. All raw SQL is written dialect-neutral:
/// identifiers are double-quoted (required by PostgreSQL for the PascalCase tables,
/// accepted by SQLite), values are passed as parameters (never string-concatenated),
/// and no provider-specific functions (datetime('now'), last_insert_rowid()) are used.
/// Native GenPrompt appointments are pushed to the connected Google/Outlook calendar
/// so they show up on the user's real devices — not just stored on the platform.
/// </summary>
public class CalendarSyncService
{
    private readonly PromptDbContext _db;
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<CalendarSyncService> _logger;

    // Marker stored in RawPayloadJson so we know an event originated in GenPrompt and
    // may therefore be edited/deleted on the provider (we never mutate purely-synced events).
    private const string GenPromptSource = "genprompt-premium";

    public CalendarSyncService(PromptDbContext db, IHttpClientFactory http, IConfiguration config, ILogger<CalendarSyncService> logger)
    {
        _db = db;
        _http = http;
        _config = config;
        _logger = logger;
    }

    private static string NowIso() => DateTime.UtcNow.ToString("o");

    // ── Google OAuth ──────────────────────────────────────────────────────────

    public string BuildGoogleAuthUrl(string userId, string redirectUri)
    {
        var clientId = _config["GoogleCalendar:ClientId"] ?? "";
        // calendar.events grants read + write of events (needed for two-way sync).
        var scopes = Uri.EscapeDataString(
            "https://www.googleapis.com/auth/calendar.events " +
            "https://www.googleapis.com/auth/userinfo.email " +
            "https://www.googleapis.com/auth/userinfo.profile");
        var state = Convert.ToBase64String(Encoding.UTF8.GetBytes(userId));
        return $"https://accounts.google.com/o/oauth2/v2/auth" +
               $"?client_id={clientId}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&response_type=code" +
               $"&scope={scopes}" +
               $"&access_type=offline" +
               $"&prompt=consent" +
               $"&state={state}";
    }

    public async Task<int> HandleGoogleCallbackAsync(string code, string userId, string redirectUri)
    {
        var clientId = _config["GoogleCalendar:ClientId"] ?? throw new InvalidOperationException("Google Calendar ClientId not configured");
        var clientSecret = _config["GoogleCalendar:ClientSecret"] ?? throw new InvalidOperationException("Google Calendar ClientSecret not configured");

        using var client = _http.CreateClient();
        var tokenResp = await client.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
            }));

        var tokenJson = await tokenResp.Content.ReadAsStringAsync();
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!;
        var refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString()! : "";
        var expiresIn = tokenDoc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        var expiresAt = DateTime.UtcNow.AddSeconds(expiresIn).ToString("o");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var profileResp = await client.GetAsync("https://www.googleapis.com/oauth2/v2/userinfo");
        var profileJson = await profileResp.Content.ReadAsStringAsync();
        using var profileDoc = JsonDocument.Parse(profileJson);
        var email = profileDoc.RootElement.TryGetProperty("email", out var em) ? em.GetString()! : "";
        var name = profileDoc.RootElement.TryGetProperty("name", out var nm) ? nm.GetString()! : email;

        var connectionId = await UpsertConnectionAsync(userId, "google", accessToken, refreshToken, expiresAt, email, name);
        await SyncGoogleEventsAsync(connectionId, userId, accessToken);
        return connectionId;
    }

    // ── Microsoft OAuth ───────────────────────────────────────────────────────

    public string BuildMicrosoftAuthUrl(string userId, string redirectUri)
    {
        var clientId = _config["MicrosoftGraph:ClientId"] ?? "";
        var tenantId = _config["MicrosoftGraph:TenantId"] ?? "common";
        var scopes = Uri.EscapeDataString("Calendars.ReadWrite User.Read offline_access");
        var state = Convert.ToBase64String(Encoding.UTF8.GetBytes(userId));
        return $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/authorize" +
               $"?client_id={clientId}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&response_type=code" +
               $"&scope={scopes}" +
               $"&state={state}";
    }

    public async Task<int> HandleMicrosoftCallbackAsync(string code, string userId, string redirectUri)
    {
        var clientId = _config["MicrosoftGraph:ClientId"] ?? throw new InvalidOperationException("Microsoft Graph ClientId not configured");
        var clientSecret = _config["MicrosoftGraph:ClientSecret"] ?? throw new InvalidOperationException("Microsoft Graph ClientSecret not configured");
        var tenantId = _config["MicrosoftGraph:TenantId"] ?? "common";

        using var client = _http.CreateClient();
        var tokenResp = await client.PostAsync($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
            }));

        var tokenJson = await tokenResp.Content.ReadAsStringAsync();
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        var accessToken = tokenDoc.RootElement.GetProperty("access_token").GetString()!;
        var refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString()! : "";
        var expiresIn = tokenDoc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        var expiresAt = DateTime.UtcNow.AddSeconds(expiresIn).ToString("o");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var meResp = await client.GetAsync("https://graph.microsoft.com/v1.0/me");
        var meJson = await meResp.Content.ReadAsStringAsync();
        using var meDoc = JsonDocument.Parse(meJson);
        var email = meDoc.RootElement.TryGetProperty("mail", out var em) ? em.GetString()!
                  : meDoc.RootElement.TryGetProperty("userPrincipalName", out var upn) ? upn.GetString()! : "";
        var name = meDoc.RootElement.TryGetProperty("displayName", out var dn) ? dn.GetString()! : email;

        var connectionId = await UpsertConnectionAsync(userId, "microsoft", accessToken, refreshToken, expiresAt, email, name);
        await SyncMicrosoftEventsAsync(connectionId, userId, accessToken);
        return connectionId;
    }

    // ── Connection upsert (shared, dialect-neutral) ───────────────────────────

    private async Task<int> UpsertConnectionAsync(string userId, string provider, string accessToken, string refreshToken, string expiresAt, string email, string name)
    {
        var now = NowIso();
        var existing = await _db.Database.SqlQuery<int>(
            $@"SELECT ""Id"" AS ""Value"" FROM ""CalendarConnections"" WHERE ""UserId""={userId} AND ""Provider""={provider} AND ""CalendarEmail""={email}"
        ).ToListAsync();

        if (existing.Count > 0)
        {
            var id = existing[0];
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $@"UPDATE ""CalendarConnections"" SET ""AccessToken""={accessToken}, ""RefreshToken""={refreshToken}, ""TokenExpiresAt""={expiresAt}, ""IsActive""={1}, ""UpdatedAt""={now} WHERE ""Id""={id}");
            return id;
        }

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"INSERT INTO ""CalendarConnections"" (""UserId"", ""Provider"", ""AccessToken"", ""RefreshToken"", ""TokenExpiresAt"", ""CalendarEmail"", ""DisplayName"", ""IsActive"", ""CreatedAt"", ""UpdatedAt"")
               VALUES ({userId}, {provider}, {accessToken}, {refreshToken}, {expiresAt}, {email}, {name}, {1}, {now}, {now})");

        // Read the id back by its natural key (existing rows are updated above, so this
        // is the row we just inserted). Avoids provider-specific last-insert-id calls.
        var inserted = await _db.Database.SqlQuery<int>(
            $@"SELECT ""Id"" AS ""Value"" FROM ""CalendarConnections"" WHERE ""UserId""={userId} AND ""Provider""={provider} AND ""CalendarEmail""={email} ORDER BY ""Id"" DESC").ToListAsync();
        return inserted[0];
    }

    // ── Event Sync: Google ────────────────────────────────────────────────────

    public async Task SyncGoogleEventsAsync(int connectionId, string userId, string? accessToken = null)
    {
        if (accessToken == null)
        {
            var row = await GetConnectionAsync(connectionId);
            if (row == null) return;
            accessToken = await EnsureValidGoogleTokenAsync(row);
            if (string.IsNullOrEmpty(accessToken)) return;
        }

        using var client = _http.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var from = DateTime.UtcNow.AddDays(-7).ToString("o");
        var to = DateTime.UtcNow.AddDays(90).ToString("o");
        var url = $"https://www.googleapis.com/calendar/v3/calendars/primary/events?timeMin={Uri.EscapeDataString(from)}&timeMax={Uri.EscapeDataString(to)}&singleEvents=true&orderBy=startTime&maxResults=250";

        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items)) return;

        foreach (var item in items.EnumerateArray())
        {
            try { await UpsertGoogleEventAsync(connectionId, userId, item); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to upsert Google event"); }
        }
    }

    private async Task UpsertGoogleEventAsync(int connectionId, string userId, JsonElement item)
    {
        var eventId = item.GetProperty("id").GetString()!;
        var title = item.TryGetProperty("summary", out var s) ? s.GetString() ?? "(No title)" : "(No title)";
        var desc = item.TryGetProperty("description", out var d) ? d.GetString() : null;
        var location = item.TryGetProperty("location", out var loc) ? loc.GetString() : null;
        var meetingLink = ExtractMeetingLink(item);

        string startDt, endDt, tz = "UTC";
        bool isAllDay = false;
        if (item.TryGetProperty("start", out var start))
        {
            if (start.TryGetProperty("dateTime", out var sdt)) { startDt = sdt.GetString()!; tz = start.TryGetProperty("timeZone", out var stz) ? stz.GetString() ?? "UTC" : "UTC"; }
            else { startDt = start.GetProperty("date").GetString()! + "T00:00:00Z"; isAllDay = true; }
        }
        else return;

        if (item.TryGetProperty("end", out var end))
        {
            if (end.TryGetProperty("dateTime", out var edt)) endDt = edt.GetString()!;
            else endDt = end.GetProperty("date").GetString()! + "T23:59:59Z";
        }
        else return;

        var organizer = item.TryGetProperty("organizer", out var org) ? org.TryGetProperty("email", out var oe) ? oe.GetString() : null : null;
        var organizerName = item.TryGetProperty("organizer", out var org2) ? org2.TryGetProperty("displayName", out var dn) ? dn.GetString() : null : null;
        var busyStatus = item.TryGetProperty("transparency", out var tr) ? (tr.GetString() == "transparent" ? "free" : "busy") : "busy";
        var isRecurring = item.TryGetProperty("recurringEventId", out _);
        var status = item.TryGetProperty("status", out var st) ? st.GetString() : "confirmed";
        if (status == "cancelled") return;

        var attendees = new List<AttendeeDto>();
        if (item.TryGetProperty("attendees", out var atts))
        {
            foreach (var att in atts.EnumerateArray())
            {
                attendees.Add(new AttendeeDto(
                    att.TryGetProperty("displayName", out var an) ? an.GetString() ?? "" : "",
                    att.TryGetProperty("email", out var ae) ? ae.GetString() ?? "" : "",
                    att.TryGetProperty("responseStatus", out var rs) ? rs.GetString() ?? "needsAction" : "needsAction",
                    att.TryGetProperty("organizer", out var ao) && ao.GetBoolean()
                ));
            }
        }

        await UpsertSyncedEventAsync(userId, connectionId, "google", eventId, title, desc, location, meetingLink,
            startDt, endDt, tz, organizer, organizerName, busyStatus, isAllDay, isRecurring, attendees);
    }

    // ── Event Sync: Microsoft ─────────────────────────────────────────────────

    public async Task SyncMicrosoftEventsAsync(int connectionId, string userId, string? accessToken = null)
    {
        if (accessToken == null)
        {
            var row = await GetConnectionAsync(connectionId);
            if (row == null) return;
            accessToken = await EnsureValidMicrosoftTokenAsync(row);
            if (string.IsNullOrEmpty(accessToken)) return;
        }

        using var client = _http.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var from = DateTime.UtcNow.AddDays(-7).ToString("o");
        var to = DateTime.UtcNow.AddDays(90).ToString("o");
        var url = $"https://graph.microsoft.com/v1.0/me/calendarview?startDateTime={Uri.EscapeDataString(from)}&endDateTime={Uri.EscapeDataString(to)}&$top=250&$select=id,subject,body,location,start,end,organizer,attendees,isAllDay,recurrence,onlineMeeting,showAs";

        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return;

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var items)) return;

        foreach (var item in items.EnumerateArray())
        {
            try { await UpsertMicrosoftEventAsync(connectionId, userId, item); }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to upsert Microsoft event"); }
        }
    }

    private async Task UpsertMicrosoftEventAsync(int connectionId, string userId, JsonElement item)
    {
        var eventId = item.GetProperty("id").GetString()!;
        var title = item.TryGetProperty("subject", out var s) ? s.GetString() ?? "(No title)" : "(No title)";
        var desc = item.TryGetProperty("body", out var b) && b.TryGetProperty("content", out var bc) ? StripHtml(bc.GetString() ?? "") : null;
        var location = item.TryGetProperty("location", out var loc) && loc.TryGetProperty("displayName", out var ldn) ? ldn.GetString() : null;

        var startDt = item.TryGetProperty("start", out var start) && start.TryGetProperty("dateTime", out var sdt) ? sdt.GetString()! : "";
        var endDt = item.TryGetProperty("end", out var end) && end.TryGetProperty("dateTime", out var edt) ? edt.GetString()! : "";
        var tz = item.TryGetProperty("start", out var start2) && start2.TryGetProperty("timeZone", out var stz) ? stz.GetString() ?? "UTC" : "UTC";
        if (string.IsNullOrEmpty(startDt) || string.IsNullOrEmpty(endDt)) return;

        var isAllDay = item.TryGetProperty("isAllDay", out var iad) && iad.GetBoolean();
        var isRecurring = item.TryGetProperty("recurrence", out var rec) && rec.ValueKind != JsonValueKind.Null;
        var busyStatus = item.TryGetProperty("showAs", out var sa) ? (sa.GetString() == "free" ? "free" : "busy") : "busy";

        var organizer = item.TryGetProperty("organizer", out var org) && org.TryGetProperty("emailAddress", out var oa) && oa.TryGetProperty("address", out var oea) ? oea.GetString() : null;
        var organizerName = item.TryGetProperty("organizer", out var org2) && org2.TryGetProperty("emailAddress", out var oa2) && oa2.TryGetProperty("name", out var on2) ? on2.GetString() : null;

        var meetingLink = item.TryGetProperty("onlineMeeting", out var om) && om.ValueKind == JsonValueKind.Object && om.TryGetProperty("joinUrl", out var ju) ? ju.GetString() : null;

        var attendees = new List<AttendeeDto>();
        if (item.TryGetProperty("attendees", out var atts))
        {
            foreach (var att in atts.EnumerateArray())
            {
                var ea = att.TryGetProperty("emailAddress", out var atEa) ? atEa : default;
                attendees.Add(new AttendeeDto(
                    ea.ValueKind == JsonValueKind.Object && ea.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "",
                    ea.ValueKind == JsonValueKind.Object && ea.TryGetProperty("address", out var ae) ? ae.GetString() ?? "" : "",
                    att.TryGetProperty("status", out var rs) && rs.TryGetProperty("response", out var r) ? r.GetString() ?? "none" : "none",
                    false
                ));
            }
        }

        await UpsertSyncedEventAsync(userId, connectionId, "microsoft", eventId, title, desc, location, meetingLink,
            startDt, endDt, tz, organizer, organizerName, busyStatus, isAllDay, isRecurring, attendees);
    }

    // Shared parameterized upsert for pulled provider events.
    private async Task UpsertSyncedEventAsync(
        string userId, int connectionId, string provider, string eventId,
        string title, string? desc, string? location, string? meetingLink,
        string startDt, string endDt, string tz, string? organizer, string? organizerName,
        string busyStatus, bool isAllDay, bool isRecurring, List<AttendeeDto> attendees)
    {
        var attsJson = JsonSerializer.Serialize(attendees);
        var now = NowIso();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""CalendarEvents"" (""UserId"", ""CalendarConnectionId"", ""Provider"", ""ProviderEventId"", ""Title"", ""Description"", ""Location"", ""MeetingLink"", ""StartDateTime"", ""EndDateTime"", ""Timezone"", ""OrganizerEmail"", ""OrganizerName"", ""BusyStatus"", ""IsAllDay"", ""IsRecurring"", ""AttendeesJson"", ""LastSyncedAt"")
            VALUES ({userId}, {connectionId}, {provider}, {eventId}, {title}, {desc}, {location}, {meetingLink}, {startDt}, {endDt}, {tz}, {organizer}, {organizerName}, {busyStatus}, {(isAllDay ? 1 : 0)}, {(isRecurring ? 1 : 0)}, {attsJson}, {now})
            ON CONFLICT (""CalendarConnectionId"", ""ProviderEventId"") DO UPDATE SET
                ""Title""=excluded.""Title"", ""Description""=excluded.""Description"", ""Location""=excluded.""Location"", ""MeetingLink""=excluded.""MeetingLink"",
                ""StartDateTime""=excluded.""StartDateTime"", ""EndDateTime""=excluded.""EndDateTime"", ""Timezone""=excluded.""Timezone"",
                ""OrganizerEmail""=excluded.""OrganizerEmail"", ""OrganizerName""=excluded.""OrganizerName"", ""BusyStatus""=excluded.""BusyStatus"",
                ""IsAllDay""=excluded.""IsAllDay"", ""IsRecurring""=excluded.""IsRecurring"", ""AttendeesJson""=excluded.""AttendeesJson"", ""LastSyncedAt""=excluded.""LastSyncedAt""");
    }

    // ── Token Refresh ─────────────────────────────────────────────────────────

    private async Task<string?> EnsureValidGoogleTokenAsync(ConnectionWithToken conn)
    {
        if (DateTime.TryParse(conn.TokenExpiresAt, out DateTime expires) && expires > DateTime.UtcNow.AddMinutes(5))
            return conn.AccessToken;

        var clientId = _config["GoogleCalendar:ClientId"] ?? "";
        var clientSecret = _config["GoogleCalendar:ClientSecret"] ?? "";
        if (string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(conn.RefreshToken)) return conn.AccessToken;

        using var client = _http.CreateClient();
        var resp = await client.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["refresh_token"] = conn.RefreshToken,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "refresh_token",
            }));

        if (!resp.IsSuccessStatusCode) return conn.AccessToken;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var newToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        var newExpiry = DateTime.UtcNow.AddSeconds(expiresIn).ToString("o");
        var now = NowIso();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""CalendarConnections"" SET ""AccessToken""={newToken}, ""TokenExpiresAt""={newExpiry}, ""UpdatedAt""={now} WHERE ""Id""={conn.Id}");
        return newToken;
    }

    private async Task<string?> EnsureValidMicrosoftTokenAsync(ConnectionWithToken conn)
    {
        if (DateTime.TryParse(conn.TokenExpiresAt, out DateTime expires) && expires > DateTime.UtcNow.AddMinutes(5))
            return conn.AccessToken;

        var clientId = _config["MicrosoftGraph:ClientId"] ?? "";
        var clientSecret = _config["MicrosoftGraph:ClientSecret"] ?? "";
        var tenantId = _config["MicrosoftGraph:TenantId"] ?? "common";
        if (string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(conn.RefreshToken)) return conn.AccessToken;

        using var client = _http.CreateClient();
        var resp = await client.PostAsync($"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["refresh_token"] = conn.RefreshToken,
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["grant_type"] = "refresh_token",
                ["scope"] = "Calendars.ReadWrite User.Read offline_access",
            }));

        if (!resp.IsSuccessStatusCode) return conn.AccessToken;
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var newToken = doc.RootElement.GetProperty("access_token").GetString()!;
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 3600;
        var newExpiry = DateTime.UtcNow.AddSeconds(expiresIn).ToString("o");
        var now = NowIso();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""CalendarConnections"" SET ""AccessToken""={newToken}, ""TokenExpiresAt""={newExpiry}, ""UpdatedAt""={now} WHERE ""Id""={conn.Id}");
        return newToken;
    }

    // ── Query Events ──────────────────────────────────────────────────────────

    public async Task<List<CalendarEventDto>> GetEventsAsync(string userId, DateTime from, DateTime to)
    {
        var fromStr = from.ToString("o");
        var toStr = to.ToString("o");
        var rows = await _db.Database.SqlQuery<CalendarEventRow>($@"
            SELECT e.""Id"", e.""CalendarConnectionId"", e.""Provider"", e.""ProviderEventId"", e.""Title"", e.""Description"", e.""Location"", e.""MeetingLink"",
                   e.""StartDateTime"", e.""EndDateTime"", e.""Timezone"", e.""OrganizerEmail"", e.""OrganizerName"",
                   e.""BusyStatus"", e.""IsAllDay"", e.""IsRecurring"", e.""AttendeesJson"", e.""RawPayloadJson"",
                   w.""Id"" as ""WorkspaceId""
            FROM ""CalendarEvents"" e
            LEFT JOIN ""MeetingWorkspaces"" w ON w.""ProviderEventId"" = e.""ProviderEventId"" AND w.""UserId"" = e.""UserId""
            WHERE e.""UserId"" = {userId} AND e.""StartDateTime"" >= {fromStr} AND e.""StartDateTime"" <= {toStr}
            ORDER BY e.""StartDateTime""").ToListAsync();

        return rows.Select(MapRow).ToList();
    }

    // ── Native events (created in GenPrompt, pushed to the connected provider) ──

    public async Task<CalendarEventDto> CreateNativeEventAsync(string userId, NativeCalendarEventInput input, int? targetConnectionId = null)
    {
        // Push to a connected Google/Outlook calendar when a target is chosen and reachable.
        if (targetConnectionId is int cid)
        {
            var conn = await GetOwnedConnectionAsync(cid, userId);
            if (conn != null)
            {
                var token = conn.Provider == "google"
                    ? await EnsureValidGoogleTokenAsync(conn)
                    : await EnsureValidMicrosoftTokenAsync(conn);

                if (!string.IsNullOrEmpty(token))
                {
                    var providerEventId = conn.Provider == "google"
                        ? await GoogleWriteEventAsync(token, HttpMethod.Post, null, input)
                        : await MicrosoftWriteEventAsync(token, HttpMethod.Post, null, input);

                    if (!string.IsNullOrEmpty(providerEventId))
                    {
                        await InsertLocalEventAsync(userId, cid, conn.Provider, providerEventId, input);
                        var created = await GetEventByProviderIdAsync(userId, cid, providerEventId);
                        if (created != null) return created;
                    }
                    else
                    {
                        throw new InvalidOperationException($"Could not create the appointment on {conn.Provider}. Please reconnect the calendar and try again.");
                    }
                }
            }
        }

        // Fallback: local-only GenPrompt appointment (no calendar connected / no target).
        var localProviderEventId = $"genprompt-{Guid.NewGuid():N}";
        await InsertLocalEventAsync(userId, 0, "genprompt", localProviderEventId, input);
        return (await GetEventByProviderIdAsync(userId, 0, localProviderEventId))!;
    }

    private async Task InsertLocalEventAsync(string userId, int connectionId, string provider, string providerEventId, NativeCalendarEventInput input)
    {
        var rawPayload = JsonSerializer.Serialize(new { source = GenPromptSource, reminderMinutes = input.ReminderMinutes });
        var attendeesJson = "[]";
        var now = NowIso();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO ""CalendarEvents"" (""UserId"", ""CalendarConnectionId"", ""Provider"", ""ProviderEventId"", ""Title"", ""Description"", ""Location"", ""MeetingLink"", ""StartDateTime"", ""EndDateTime"", ""Timezone"", ""OrganizerEmail"", ""OrganizerName"", ""BusyStatus"", ""IsAllDay"", ""IsRecurring"", ""AttendeesJson"", ""RawPayloadJson"", ""LastSyncedAt"")
            VALUES ({userId}, {connectionId}, {provider}, {providerEventId}, {input.Title}, {input.Description}, {input.Location}, {input.MeetingLink}, {input.StartDateTime.ToString("o")}, {input.EndDateTime.ToString("o")}, {input.Timezone}, {null}, {"GenPrompt Calendar"}, {"busy"}, {(input.IsAllDay ? 1 : 0)}, {0}, {attendeesJson}, {rawPayload}, {now})
            ON CONFLICT (""CalendarConnectionId"", ""ProviderEventId"") DO UPDATE SET
                ""Title""=excluded.""Title"", ""Description""=excluded.""Description"", ""Location""=excluded.""Location"", ""MeetingLink""=excluded.""MeetingLink"",
                ""StartDateTime""=excluded.""StartDateTime"", ""EndDateTime""=excluded.""EndDateTime"", ""Timezone""=excluded.""Timezone"",
                ""IsAllDay""=excluded.""IsAllDay"", ""RawPayloadJson""=excluded.""RawPayloadJson"", ""LastSyncedAt""=excluded.""LastSyncedAt""");
    }

    public async Task<CalendarEventDto?> UpdateNativeEventAsync(string userId, int id, NativeCalendarEventInput input)
    {
        var row = await GetEventRowByIdAsync(userId, id);
        if (row == null || !IsGenPromptOwned(row)) return null;

        // Mirror the edit to the provider when the event lives on Google/Outlook.
        if (row.Provider is "google" or "microsoft" && row.CalendarConnectionId > 0)
        {
            var conn = await GetOwnedConnectionAsync(row.CalendarConnectionId, userId);
            if (conn != null)
            {
                var token = conn.Provider == "google"
                    ? await EnsureValidGoogleTokenAsync(conn)
                    : await EnsureValidMicrosoftTokenAsync(conn);
                if (!string.IsNullOrEmpty(token))
                {
                    if (conn.Provider == "google")
                        await GoogleWriteEventAsync(token, HttpMethod.Patch, row.ProviderEventId, input);
                    else
                        await MicrosoftWriteEventAsync(token, HttpMethod.Patch, row.ProviderEventId, input);
                }
            }
        }

        var rawPayload = JsonSerializer.Serialize(new { source = GenPromptSource, reminderMinutes = input.ReminderMinutes });
        var now = NowIso();
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE ""CalendarEvents""
            SET ""Title""={input.Title}, ""Description""={input.Description}, ""Location""={input.Location}, ""MeetingLink""={input.MeetingLink},
                ""StartDateTime""={input.StartDateTime.ToString("o")}, ""EndDateTime""={input.EndDateTime.ToString("o")}, ""Timezone""={input.Timezone},
                ""IsAllDay""={(input.IsAllDay ? 1 : 0)}, ""RawPayloadJson""={rawPayload}, ""LastSyncedAt""={now}
            WHERE ""Id""={id} AND ""UserId""={userId}");

        return await GetEventByRowIdAsync(userId, id);
    }

    public async Task<bool> DeleteNativeEventAsync(string userId, int id)
    {
        var row = await GetEventRowByIdAsync(userId, id);
        if (row == null || !IsGenPromptOwned(row)) return false;

        if (row.Provider is "google" or "microsoft" && row.CalendarConnectionId > 0)
        {
            var conn = await GetOwnedConnectionAsync(row.CalendarConnectionId, userId);
            if (conn != null)
            {
                var token = conn.Provider == "google"
                    ? await EnsureValidGoogleTokenAsync(conn)
                    : await EnsureValidMicrosoftTokenAsync(conn);
                if (!string.IsNullOrEmpty(token))
                {
                    if (conn.Provider == "google")
                        await GoogleDeleteEventAsync(token, row.ProviderEventId);
                    else
                        await MicrosoftDeleteEventAsync(token, row.ProviderEventId);
                }
            }
        }

        var changed = await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"DELETE FROM ""CalendarEvents"" WHERE ""Id""={id} AND ""UserId""={userId}");
        return changed > 0;
    }

    // ── Provider write-back (Google) ──────────────────────────────────────────

    private async Task<string?> GoogleWriteEventAsync(string accessToken, HttpMethod method, string? providerEventId, NativeCalendarEventInput input)
    {
        using var client = _http.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var descriptionParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(input.Description)) descriptionParts.Add(input.Description!);
        if (!string.IsNullOrWhiteSpace(input.MeetingLink)) descriptionParts.Add($"Join: {input.MeetingLink}");

        var body = new Dictionary<string, object?>
        {
            ["summary"] = input.Title,
            ["description"] = descriptionParts.Count > 0 ? string.Join("\n\n", descriptionParts) : null,
            ["location"] = input.Location,
        };
        if (input.IsAllDay)
        {
            body["start"] = new { date = input.StartDateTime.ToString("yyyy-MM-dd") };
            body["end"] = new { date = input.EndDateTime.ToString("yyyy-MM-dd") };
        }
        else
        {
            body["start"] = new { dateTime = input.StartDateTime.ToString("o"), timeZone = input.Timezone };
            body["end"] = new { dateTime = input.EndDateTime.ToString("o"), timeZone = input.Timezone };
        }
        if (input.ReminderMinutes is int rm)
            body["reminders"] = new { useDefault = false, overrides = new[] { new { method = "popup", minutes = rm } } };

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var url = method == HttpMethod.Post
            ? "https://www.googleapis.com/calendar/v3/calendars/primary/events"
            : $"https://www.googleapis.com/calendar/v3/calendars/primary/events/{Uri.EscapeDataString(providerEventId ?? "")}";

        var resp = await client.SendAsync(new HttpRequestMessage(method, url) { Content = content });
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Google event {method} failed: {code} {body}", method, resp.StatusCode, await resp.Content.ReadAsStringAsync());
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("id", out var idp) ? idp.GetString() : providerEventId;
    }

    private async Task GoogleDeleteEventAsync(string accessToken, string providerEventId)
    {
        using var client = _http.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await client.DeleteAsync($"https://www.googleapis.com/calendar/v3/calendars/primary/events/{Uri.EscapeDataString(providerEventId)}");
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("Google event delete failed: {code}", resp.StatusCode);
    }

    // ── Provider write-back (Microsoft Graph) ─────────────────────────────────

    private async Task<string?> MicrosoftWriteEventAsync(string accessToken, HttpMethod method, string? providerEventId, NativeCalendarEventInput input)
    {
        using var client = _http.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var descriptionParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(input.Description)) descriptionParts.Add(input.Description!);
        if (!string.IsNullOrWhiteSpace(input.MeetingLink)) descriptionParts.Add($"Join: {input.MeetingLink}");

        var body = new Dictionary<string, object?>
        {
            ["subject"] = input.Title,
            ["body"] = new { contentType = "text", content = descriptionParts.Count > 0 ? string.Join("\n\n", descriptionParts) : "" },
            ["location"] = new { displayName = input.Location ?? "" },
            ["isAllDay"] = input.IsAllDay,
            ["start"] = new { dateTime = input.StartDateTime.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = input.Timezone },
            ["end"] = new { dateTime = input.EndDateTime.ToString("yyyy-MM-ddTHH:mm:ss"), timeZone = input.Timezone },
        };
        if (input.ReminderMinutes is int rm)
        {
            body["isReminderOn"] = true;
            body["reminderMinutesBeforeStart"] = rm;
        }

        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var url = method == HttpMethod.Post
            ? "https://graph.microsoft.com/v1.0/me/events"
            : $"https://graph.microsoft.com/v1.0/me/events/{Uri.EscapeDataString(providerEventId ?? "")}";

        var resp = await client.SendAsync(new HttpRequestMessage(method, url) { Content = content });
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Microsoft event {method} failed: {code} {body}", method, resp.StatusCode, await resp.Content.ReadAsStringAsync());
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("id", out var idp) ? idp.GetString() : providerEventId;
    }

    private async Task MicrosoftDeleteEventAsync(string accessToken, string providerEventId)
    {
        using var client = _http.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await client.DeleteAsync($"https://graph.microsoft.com/v1.0/me/events/{Uri.EscapeDataString(providerEventId)}");
        if (!resp.IsSuccessStatusCode)
            _logger.LogWarning("Microsoft event delete failed: {code}", resp.StatusCode);
    }

    // ── Connections ───────────────────────────────────────────────────────────

    public async Task<List<ConnectionRow>> GetConnectionsAsync(string userId)
        => await _db.Database.SqlQuery<ConnectionRow>(
            $@"SELECT ""Id"", ""Provider"", ""CalendarEmail"", ""DisplayName"", ""IsActive"", ""CreatedAt"" FROM ""CalendarConnections"" WHERE ""UserId""={userId} AND ""IsActive""={1}"
        ).ToListAsync();

    private async Task<ConnectionWithToken?> GetConnectionAsync(int id)
        => (await _db.Database.SqlQuery<ConnectionWithToken>(
            $@"SELECT ""Id"", ""Provider"", ""AccessToken"", ""RefreshToken"", ""TokenExpiresAt"" FROM ""CalendarConnections"" WHERE ""Id""={id} AND ""IsActive""={1}"
        ).ToListAsync()).FirstOrDefault();

    private async Task<ConnectionWithToken?> GetOwnedConnectionAsync(int id, string userId)
        => (await _db.Database.SqlQuery<ConnectionWithToken>(
            $@"SELECT ""Id"", ""Provider"", ""AccessToken"", ""RefreshToken"", ""TokenExpiresAt"" FROM ""CalendarConnections"" WHERE ""Id""={id} AND ""UserId""={userId} AND ""IsActive""={1}"
        ).ToListAsync()).FirstOrDefault();

    public async Task SyncAllForUserAsync(string userId)
    {
        var connections = await _db.Database.SqlQuery<ConnectionWithToken>(
            $@"SELECT ""Id"", ""Provider"", ""AccessToken"", ""RefreshToken"", ""TokenExpiresAt"" FROM ""CalendarConnections"" WHERE ""UserId""={userId} AND ""IsActive""={1}"
        ).ToListAsync();

        foreach (var conn in connections)
        {
            if (conn.Provider == "google")
            {
                var token = await EnsureValidGoogleTokenAsync(conn);
                if (!string.IsNullOrEmpty(token)) await SyncGoogleEventsAsync(conn.Id, userId, token);
            }
            else if (conn.Provider == "microsoft")
            {
                var token = await EnsureValidMicrosoftTokenAsync(conn);
                if (!string.IsNullOrEmpty(token)) await SyncMicrosoftEventsAsync(conn.Id, userId, token);
            }
        }
    }

    public async Task DisconnectAsync(int connectionId, string userId)
    {
        var now = NowIso();
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $@"UPDATE ""CalendarConnections"" SET ""IsActive""={0}, ""UpdatedAt""={now} WHERE ""Id""={connectionId} AND ""UserId""={userId}");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsGenPromptOwned(CalendarEventRow row)
    {
        if (row.Provider == "genprompt") return true;
        if (string.IsNullOrWhiteSpace(row.RawPayloadJson)) return false;
        try
        {
            using var doc = JsonDocument.Parse(row.RawPayloadJson);
            return doc.RootElement.TryGetProperty("source", out var src) && src.GetString() == GenPromptSource;
        }
        catch { return false; }
    }

    private static string? ExtractMeetingLink(JsonElement item)
    {
        if (item.TryGetProperty("hangoutLink", out var hl)) return hl.GetString();
        if (item.TryGetProperty("conferenceData", out var cd) && cd.TryGetProperty("entryPoints", out var eps))
            foreach (var ep in eps.EnumerateArray())
                if (ep.TryGetProperty("entryPointType", out var ept) && ept.GetString() == "video" && ep.TryGetProperty("uri", out var uri))
                    return uri.GetString();
        return null;
    }

    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return html;
        return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]*>", " ")
                      .Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                      .Trim();
    }

    private async Task<CalendarEventRow?> GetEventRowByIdAsync(string userId, int id)
        => (await _db.Database.SqlQuery<CalendarEventRow>($@"
            SELECT ""Id"", ""CalendarConnectionId"", ""Provider"", ""ProviderEventId"", ""Title"", ""Description"", ""Location"", ""MeetingLink"", ""StartDateTime"", ""EndDateTime"",
                   ""Timezone"", ""OrganizerEmail"", ""OrganizerName"", ""BusyStatus"", ""IsAllDay"", ""IsRecurring"", ""AttendeesJson"", ""RawPayloadJson"",
                   CAST(NULL AS INTEGER) as ""WorkspaceId""
            FROM ""CalendarEvents"" WHERE ""UserId""={userId} AND ""Id""={id} LIMIT 1").ToListAsync()).FirstOrDefault();

    private async Task<CalendarEventDto?> GetEventByRowIdAsync(string userId, int id)
    {
        var row = await GetEventRowByIdAsync(userId, id);
        return row == null ? null : MapRow(row);
    }

    private async Task<CalendarEventDto?> GetEventByProviderIdAsync(string userId, int connectionId, string providerEventId)
    {
        var row = (await _db.Database.SqlQuery<CalendarEventRow>($@"
            SELECT ""Id"", ""CalendarConnectionId"", ""Provider"", ""ProviderEventId"", ""Title"", ""Description"", ""Location"", ""MeetingLink"", ""StartDateTime"", ""EndDateTime"",
                   ""Timezone"", ""OrganizerEmail"", ""OrganizerName"", ""BusyStatus"", ""IsAllDay"", ""IsRecurring"", ""AttendeesJson"", ""RawPayloadJson"",
                   CAST(NULL AS INTEGER) as ""WorkspaceId""
            FROM ""CalendarEvents"" WHERE ""UserId""={userId} AND ""CalendarConnectionId""={connectionId} AND ""ProviderEventId""={providerEventId} LIMIT 1").ToListAsync()).FirstOrDefault();
        return row == null ? null : MapRow(row);
    }

    private static CalendarEventDto MapRow(CalendarEventRow r) => new(
        r.Id, r.Provider, r.ProviderEventId, r.Title, r.Description, r.Location, r.MeetingLink,
        r.StartDateTime, r.EndDateTime, r.Timezone, r.OrganizerEmail, r.OrganizerName, r.BusyStatus,
        r.IsAllDay == 1, r.IsRecurring == 1,
        string.IsNullOrEmpty(r.AttendeesJson) ? new() : JsonSerializer.Deserialize<List<AttendeeDto>>(r.AttendeesJson) ?? new(),
        r.WorkspaceId,
        ExtractReminderMinutes(r.RawPayloadJson),
        IsGenPromptOwned(r)
    );

    private static int? ExtractReminderMinutes(string? rawPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(rawPayloadJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(rawPayloadJson);
            return doc.RootElement.TryGetProperty("reminderMinutes", out var minutes) && minutes.ValueKind == JsonValueKind.Number
                ? minutes.GetInt32()
                : null;
        }
        catch { return null; }
    }
}

public record NativeCalendarEventInput(
    string Title,
    string? Description,
    string? Location,
    string? MeetingLink,
    DateTime StartDateTime,
    DateTime EndDateTime,
    string Timezone,
    bool IsAllDay,
    int? ReminderMinutes
);

// Raw query result types
public class CalendarEventRow
{
    public int Id { get; set; }
    public int CalendarConnectionId { get; set; }
    public string Provider { get; set; } = "";
    public string ProviderEventId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Location { get; set; }
    public string? MeetingLink { get; set; }
    public string StartDateTime { get; set; } = "";
    public string EndDateTime { get; set; } = "";
    public string Timezone { get; set; } = "UTC";
    public string? OrganizerEmail { get; set; }
    public string? OrganizerName { get; set; }
    public string BusyStatus { get; set; } = "busy";
    public int IsAllDay { get; set; }
    public int IsRecurring { get; set; }
    public string AttendeesJson { get; set; } = "[]";
    public string? RawPayloadJson { get; set; }
    public int? WorkspaceId { get; set; }
}

public class ConnectionRow
{
    public int Id { get; set; }
    public string Provider { get; set; } = "";
    public string CalendarEmail { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int IsActive { get; set; }
    public string CreatedAt { get; set; } = "";
}

public class ConnectionWithToken
{
    public int Id { get; set; }
    public string Provider { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public string TokenExpiresAt { get; set; } = "";
}
