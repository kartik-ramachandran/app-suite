using Microsoft.EntityFrameworkCore;

namespace MeetingManager.Api.Data;

/// <summary>
/// Raw-SQL tables (no EF model) this app reads and writes, copied from the
/// parent app's ApplyNonEfTablesAsync and filtered to the tables this app's
/// code actually references. Idempotent: every statement is IF NOT EXISTS.
/// </summary>
public static class SchemaBootstrap
{
    private static readonly string[] SqliteCmds = new string[]
    {
            "CREATE TABLE IF NOT EXISTS CalendarConnections (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Provider TEXT NOT NULL, AccessToken TEXT NOT NULL DEFAULT '', RefreshToken TEXT NOT NULL DEFAULT '', TokenExpiresAt TEXT NOT NULL DEFAULT '', CalendarEmail TEXT NOT NULL DEFAULT '', DisplayName TEXT NOT NULL DEFAULT '', IsActive INTEGER NOT NULL DEFAULT 1, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_CalendarConnections_UserId ON CalendarConnections (UserId)",
            "CREATE TABLE IF NOT EXISTS CalendarEvents (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, CalendarConnectionId INTEGER NOT NULL, Provider TEXT NOT NULL, ProviderEventId TEXT NOT NULL, Title TEXT NOT NULL DEFAULT '', Description TEXT, Location TEXT, MeetingLink TEXT, StartDateTime TEXT NOT NULL, EndDateTime TEXT NOT NULL, Timezone TEXT NOT NULL DEFAULT 'UTC', OrganizerEmail TEXT, OrganizerName TEXT, BusyStatus TEXT NOT NULL DEFAULT 'busy', IsAllDay INTEGER NOT NULL DEFAULT 0, IsRecurring INTEGER NOT NULL DEFAULT 0, AttendeesJson TEXT NOT NULL DEFAULT '[]', RawPayloadJson TEXT, LastSyncedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_CalendarEvents_UserId_Start ON CalendarEvents (UserId, StartDateTime)",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_CalendarEvents_Provider_EventId ON CalendarEvents (CalendarConnectionId, ProviderEventId)",
            "CREATE TABLE IF NOT EXISTS MeetingWorkspaces (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, CalendarEventId INTEGER, ProviderEventId TEXT, EventTitle TEXT NOT NULL DEFAULT '', EventStart TEXT, EventEnd TEXT, AttendeesJson TEXT NOT NULL DEFAULT '[]', Objective TEXT, PrepPackJson TEXT, Notes TEXT, Summary TEXT, DecisionsJson TEXT NOT NULL DEFAULT '[]', RisksJson TEXT NOT NULL DEFAULT '[]', FollowUpDraft TEXT, ActionsJson TEXT NOT NULL DEFAULT '[]', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_MeetingWorkspaces_UserId ON MeetingWorkspaces (UserId)",
            "CREATE TABLE IF NOT EXISTS MeetingTasks (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, WorkspaceId INTEGER, Title TEXT NOT NULL, Description TEXT, Status TEXT NOT NULL DEFAULT 'open', Priority TEXT NOT NULL DEFAULT 'medium', DueDate TEXT, Owner TEXT, Source TEXT NOT NULL DEFAULT 'manual', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_MeetingTasks_UserId ON MeetingTasks (UserId)",
            "CREATE TABLE IF NOT EXISTS MeetingFollowUps (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, WorkspaceId INTEGER, Title TEXT NOT NULL, Details TEXT, Owner TEXT, DueDate TEXT, Status TEXT NOT NULL DEFAULT 'pending', FollowUpType TEXT NOT NULL DEFAULT 'action', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_MeetingFollowUps_UserId ON MeetingFollowUps (UserId)",
            "CREATE TABLE IF NOT EXISTS UserTravelPrefs (UserId TEXT PRIMARY KEY, DietaryPreferences TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE TABLE IF NOT EXISTS TravelGuides (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Destination TEXT NOT NULL DEFAULT '', StartDate TEXT, EndDate TEXT, GuideJson TEXT NOT NULL DEFAULT '{}', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_TravelGuides_UserId ON TravelGuides (UserId, Id)",
            "CREATE TABLE IF NOT EXISTS MeetingSessions (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, CalendarEventId INTEGER, InteractionSessionId TEXT, Title TEXT NOT NULL DEFAULT '', MeetingLink TEXT, Platform TEXT NOT NULL DEFAULT 'other', Source TEXT NOT NULL DEFAULT 'tab', ExternalId TEXT, BotToken TEXT, Status TEXT NOT NULL DEFAULT 'armed', ErrorMessage TEXT, Transcript TEXT NOT NULL DEFAULT '', ChunkCount INTEGER NOT NULL DEFAULT 0, ParticipantsJson TEXT NOT NULL DEFAULT '[]', AutoToolsJson TEXT NOT NULL DEFAULT '[]', ScheduledStart TEXT NOT NULL DEFAULT (datetime('now')), ScheduledEnd TEXT NOT NULL DEFAULT (datetime('now')), StartedAt TEXT, EndedAt TEXT, LastChunkAt TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_MeetingSessions_User_Status ON MeetingSessions (UserId, Status)",
            "CREATE TABLE IF NOT EXISTS OrganizationMembers (Id TEXT PRIMARY KEY, OrganizationId TEXT NOT NULL, UserId TEXT NOT NULL, Role TEXT NOT NULL DEFAULT 'member', Status TEXT NOT NULL DEFAULT 'active', InvitedByUserId TEXT, JoinedAt TEXT NOT NULL DEFAULT (datetime('now')), CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_OrganizationMembers_Org_User ON OrganizationMembers (OrganizationId, UserId)",
            "CREATE INDEX IF NOT EXISTS IX_OrganizationMembers_UserId ON OrganizationMembers (UserId)"
    };

    private static readonly string[] PostgresCmds = new string[]
    {
            @"CREATE TABLE IF NOT EXISTS ""CalendarConnections"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Provider"" TEXT NOT NULL, ""AccessToken"" TEXT NOT NULL DEFAULT '', ""RefreshToken"" TEXT NOT NULL DEFAULT '', ""TokenExpiresAt"" TEXT NOT NULL DEFAULT '', ""CalendarEmail"" TEXT NOT NULL DEFAULT '', ""DisplayName"" TEXT NOT NULL DEFAULT '', ""IsActive"" INTEGER NOT NULL DEFAULT 1, ""CreatedAt"" TEXT NOT NULL DEFAULT '', ""UpdatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE INDEX IF NOT EXISTS ""IX_CalendarConnections_UserId"" ON ""CalendarConnections"" (""UserId"")",
            @"CREATE TABLE IF NOT EXISTS ""CalendarEvents"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""CalendarConnectionId"" INTEGER NOT NULL, ""Provider"" TEXT NOT NULL, ""ProviderEventId"" TEXT NOT NULL, ""Title"" TEXT NOT NULL DEFAULT '', ""Description"" TEXT, ""Location"" TEXT, ""MeetingLink"" TEXT, ""StartDateTime"" TEXT NOT NULL, ""EndDateTime"" TEXT NOT NULL, ""Timezone"" TEXT NOT NULL DEFAULT 'UTC', ""OrganizerEmail"" TEXT, ""OrganizerName"" TEXT, ""BusyStatus"" TEXT NOT NULL DEFAULT 'busy', ""IsAllDay"" INTEGER NOT NULL DEFAULT 0, ""IsRecurring"" INTEGER NOT NULL DEFAULT 0, ""AttendeesJson"" TEXT NOT NULL DEFAULT '[]', ""RawPayloadJson"" TEXT, ""LastSyncedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE INDEX IF NOT EXISTS ""IX_CalendarEvents_UserId_Start"" ON ""CalendarEvents"" (""UserId"", ""StartDateTime"")",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CalendarEvents_Provider_EventId"" ON ""CalendarEvents"" (""CalendarConnectionId"", ""ProviderEventId"")",
            @"CREATE TABLE IF NOT EXISTS ""MeetingWorkspaces"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""CalendarEventId"" INTEGER, ""ProviderEventId"" TEXT, ""EventTitle"" TEXT NOT NULL DEFAULT '', ""EventStart"" TEXT, ""EventEnd"" TEXT, ""AttendeesJson"" TEXT NOT NULL DEFAULT '[]', ""Objective"" TEXT, ""PrepPackJson"" TEXT, ""Notes"" TEXT, ""Summary"" TEXT, ""DecisionsJson"" TEXT NOT NULL DEFAULT '[]', ""RisksJson"" TEXT NOT NULL DEFAULT '[]', ""FollowUpDraft"" TEXT, ""ActionsJson"" TEXT NOT NULL DEFAULT '[]', ""CreatedAt"" TEXT NOT NULL DEFAULT '', ""UpdatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE INDEX IF NOT EXISTS ""IX_MeetingWorkspaces_UserId"" ON ""MeetingWorkspaces"" (""UserId"")",
            @"CREATE TABLE IF NOT EXISTS ""MeetingTasks"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""WorkspaceId"" INTEGER, ""Title"" TEXT NOT NULL, ""Description"" TEXT, ""Status"" TEXT NOT NULL DEFAULT 'open', ""Priority"" TEXT NOT NULL DEFAULT 'medium', ""DueDate"" TEXT, ""Owner"" TEXT, ""Source"" TEXT NOT NULL DEFAULT 'manual', ""CreatedAt"" TEXT NOT NULL DEFAULT '', ""UpdatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE INDEX IF NOT EXISTS ""IX_MeetingTasks_UserId"" ON ""MeetingTasks"" (""UserId"")",
            @"CREATE TABLE IF NOT EXISTS ""MeetingFollowUps"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""WorkspaceId"" INTEGER, ""Title"" TEXT NOT NULL, ""Details"" TEXT, ""Owner"" TEXT, ""DueDate"" TEXT, ""Status"" TEXT NOT NULL DEFAULT 'pending', ""FollowUpType"" TEXT NOT NULL DEFAULT 'action', ""CreatedAt"" TEXT NOT NULL DEFAULT '', ""UpdatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE INDEX IF NOT EXISTS ""IX_MeetingFollowUps_UserId"" ON ""MeetingFollowUps"" (""UserId"")",
            @"CREATE TABLE IF NOT EXISTS ""UserTravelPrefs"" (""UserId"" TEXT PRIMARY KEY, ""DietaryPreferences"" TEXT, ""CreatedAt"" TEXT NOT NULL DEFAULT '', ""UpdatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE TABLE IF NOT EXISTS ""TravelGuides"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Destination"" TEXT NOT NULL DEFAULT '', ""StartDate"" TEXT, ""EndDate"" TEXT, ""GuideJson"" TEXT NOT NULL DEFAULT '{}', ""CreatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE INDEX IF NOT EXISTS ""IX_TravelGuides_UserId"" ON ""TravelGuides"" (""UserId"", ""Id"")",
            @"CREATE TABLE IF NOT EXISTS ""MeetingSessions"" (""Id"" TEXT PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""CalendarEventId"" INTEGER, ""InteractionSessionId"" TEXT, ""Title"" TEXT NOT NULL DEFAULT '', ""MeetingLink"" TEXT, ""Platform"" TEXT NOT NULL DEFAULT 'other', ""Source"" TEXT NOT NULL DEFAULT 'tab', ""ExternalId"" TEXT, ""BotToken"" TEXT, ""Status"" TEXT NOT NULL DEFAULT 'armed', ""ErrorMessage"" TEXT, ""Transcript"" TEXT NOT NULL DEFAULT '', ""ChunkCount"" INTEGER NOT NULL DEFAULT 0, ""ParticipantsJson"" TEXT NOT NULL DEFAULT '[]', ""AutoToolsJson"" TEXT NOT NULL DEFAULT '[]', ""ScheduledStart"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""ScheduledEnd"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""StartedAt"" TIMESTAMPTZ, ""EndedAt"" TIMESTAMPTZ, ""LastChunkAt"" TIMESTAMPTZ, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"ALTER TABLE ""MeetingSessions"" ADD COLUMN IF NOT EXISTS ""ExternalId"" TEXT",
            @"ALTER TABLE ""MeetingSessions"" ADD COLUMN IF NOT EXISTS ""BotToken"" TEXT",
            @"ALTER TABLE ""MeetingSessions"" ADD COLUMN IF NOT EXISTS ""ParticipantsJson"" TEXT NOT NULL DEFAULT '[]'",
            @"CREATE INDEX IF NOT EXISTS ""IX_MeetingSessions_User_Status"" ON ""MeetingSessions"" (""UserId"", ""Status"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_MeetingSessions_External"" ON ""MeetingSessions"" (""ExternalId"")",
            @"CREATE TABLE IF NOT EXISTS ""MeetingBotConfigs"" (""Id"" TEXT PRIMARY KEY, ""Scope"" TEXT NOT NULL DEFAULT 'user', ""OwnerId"" TEXT, ""MeetingPlatform"" TEXT NOT NULL DEFAULT '*', ""BotName"" TEXT NOT NULL DEFAULT 'GenPrompt Notetaker', ""AutoRecord"" BOOLEAN NOT NULL DEFAULT FALSE, ""AutoToolsJson"" TEXT NOT NULL DEFAULT '[""summaries""]', ""MaxMinutes"" INTEGER NOT NULL DEFAULT 240, ""Enabled"" BOOLEAN NOT NULL DEFAULT TRUE, ""CreatedBy"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_MeetingBotConfigs_Scope_Owner"" ON ""MeetingBotConfigs"" (""Scope"", ""OwnerId"")",
            @"CREATE TABLE IF NOT EXISTS ""OrganizationMembers"" (""Id"" TEXT PRIMARY KEY, ""OrganizationId"" TEXT NOT NULL, ""UserId"" TEXT NOT NULL, ""Role"" TEXT NOT NULL DEFAULT 'member', ""Status"" TEXT NOT NULL DEFAULT 'active', ""InvitedByUserId"" TEXT, ""JoinedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_OrganizationMembers_Org_User"" ON ""OrganizationMembers"" (""OrganizationId"", ""UserId"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_OrganizationMembers_UserId"" ON ""OrganizationMembers"" (""UserId"")"
    };

    public static async Task ApplyAsync(PromptDbContext context, ILogger logger)
    {
        var isSqlite = context.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? true;
        foreach (var cmd in isSqlite ? SqliteCmds : PostgresCmds)
        {
            try { await context.Database.ExecuteSqlRawAsync(cmd); }
            catch (Exception ex) { logger.LogWarning(ex, "Schema bootstrap statement failed: {Cmd}", cmd.Length > 80 ? cmd[..80] : cmd); }
        }
    }
}
