using Microsoft.EntityFrameworkCore;

namespace AgentHub.Api.Data;

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
            "CREATE TABLE IF NOT EXISTS UserTravelPrefs (UserId TEXT PRIMARY KEY, DietaryPreferences TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE TABLE IF NOT EXISTS TravelGuides (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Destination TEXT NOT NULL DEFAULT '', StartDate TEXT, EndDate TEXT, GuideJson TEXT NOT NULL DEFAULT '{}', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_TravelGuides_UserId ON TravelGuides (UserId, Id)",
            "ALTER TABLE AgentChatSessions ADD COLUMN BotType TEXT",
            "CREATE TABLE IF NOT EXISTS ModuleJobs (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Module TEXT NOT NULL DEFAULT '', JobType TEXT NOT NULL DEFAULT '', Title TEXT NOT NULL DEFAULT '', Status TEXT NOT NULL DEFAULT 'running', ResultText TEXT, ErrorMessage TEXT, IsRead INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), CompletedAt TEXT, DurationMs INTEGER NOT NULL DEFAULT 0)",
            "CREATE INDEX IF NOT EXISTS IX_ModuleJobs_UserId_CreatedAt ON ModuleJobs (UserId, CreatedAt)",
            "CREATE TABLE IF NOT EXISTS InteractionSessions (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, SourceLabel TEXT NOT NULL DEFAULT '', Transcript TEXT NOT NULL DEFAULT '', AnalysesJson TEXT NOT NULL DEFAULT '', Intent TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "ALTER TABLE InteractionSessions ADD COLUMN Intent TEXT",
            "CREATE INDEX IF NOT EXISTS IX_InteractionSessions_UserId ON InteractionSessions (UserId, Id)",
            "CREATE TABLE IF NOT EXISTS McpCatalogServers (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL, Title TEXT NOT NULL DEFAULT '', Description TEXT NOT NULL DEFAULT '', Version TEXT NOT NULL DEFAULT '', Category TEXT NOT NULL DEFAULT 'Other', Transport TEXT NOT NULL DEFAULT 'remote', RemoteUrl TEXT NOT NULL DEFAULT '', Publisher TEXT NOT NULL DEFAULT '', WebsiteUrl TEXT, IsConnectable INTEGER NOT NULL DEFAULT 1, RegistryUpdatedAt TEXT NOT NULL DEFAULT (datetime('now')), SyncedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_McpCatalogServers_Name ON McpCatalogServers (Name)",
            "CREATE INDEX IF NOT EXISTS IX_McpCatalogServers_Category ON McpCatalogServers (Category, IsConnectable)",
            "CREATE TABLE IF NOT EXISTS McpConnections (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, OrganizationId TEXT, ServerName TEXT NOT NULL DEFAULT '', Label TEXT NOT NULL DEFAULT '', RemoteUrl TEXT NOT NULL DEFAULT '', AuthType TEXT NOT NULL DEFAULT 'none', SecretEncrypted TEXT, HeaderName TEXT, Status TEXT NOT NULL DEFAULT 'connected', LastError TEXT, LastCheckedAt TEXT, ToolCount INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_McpConnections_UserId ON McpConnections (UserId)",
            "CREATE TABLE IF NOT EXISTS McpConnectionTools (Id INTEGER PRIMARY KEY AUTOINCREMENT, ConnectionId TEXT NOT NULL, UserId TEXT NOT NULL, ToolName TEXT NOT NULL DEFAULT '', Description TEXT NOT NULL DEFAULT '', InputSchemaJson TEXT NOT NULL DEFAULT '{}', Enabled INTEGER NOT NULL DEFAULT 1, ApprovalMode TEXT NOT NULL DEFAULT 'ask', IsWrite INTEGER NOT NULL DEFAULT 1, DiscoveredAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_McpConnectionTools_Connection ON McpConnectionTools (ConnectionId)",
            "CREATE INDEX IF NOT EXISTS IX_McpConnectionTools_User ON McpConnectionTools (UserId, Enabled)",
            "CREATE TABLE IF NOT EXISTS OrchestrationRuns (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, OrganizationId TEXT, Goal TEXT NOT NULL DEFAULT '', Status TEXT NOT NULL DEFAULT 'planning', PlanSummary TEXT NOT NULL DEFAULT '', Result TEXT, ErrorMessage TEXT, MaxSteps INTEGER NOT NULL DEFAULT 40, StepsUsed INTEGER NOT NULL DEFAULT 0, SourceNodeId TEXT, ResultNodeId TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')), CompletedAt TEXT)",
            "CREATE INDEX IF NOT EXISTS IX_OrchestrationRuns_User ON OrchestrationRuns (UserId, CreatedAt)",
            "CREATE INDEX IF NOT EXISTS IX_OrchestrationRuns_Status ON OrchestrationRuns (Status, UpdatedAt)",
            "CREATE TABLE IF NOT EXISTS OrchestrationTasks (Id INTEGER PRIMARY KEY AUTOINCREMENT, RunId TEXT NOT NULL, UserId TEXT NOT NULL, \"Key\" TEXT NOT NULL DEFAULT '', Title TEXT NOT NULL DEFAULT '', Tool TEXT NOT NULL DEFAULT '', ArgumentsJson TEXT NOT NULL DEFAULT '{}', DependsOnJson TEXT NOT NULL DEFAULT '[]', Status TEXT NOT NULL DEFAULT 'pending', Output TEXT, ErrorMessage TEXT, Attempts INTEGER NOT NULL DEFAULT 0, NeedsApproval INTEGER NOT NULL DEFAULT 0, ApprovedBy TEXT, ApprovedAt TEXT, OrderIndex INTEGER NOT NULL DEFAULT 0, StartedAt TEXT, CompletedAt TEXT, DurationMs INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_OrchestrationTasks_Run ON OrchestrationTasks (RunId, OrderIndex)"
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
            @"CREATE TABLE IF NOT EXISTS ""UserTravelPrefs"" (""UserId"" TEXT PRIMARY KEY, ""DietaryPreferences"" TEXT, ""CreatedAt"" TEXT NOT NULL DEFAULT '', ""UpdatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE TABLE IF NOT EXISTS ""TravelGuides"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Destination"" TEXT NOT NULL DEFAULT '', ""StartDate"" TEXT, ""EndDate"" TEXT, ""GuideJson"" TEXT NOT NULL DEFAULT '{}', ""CreatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE INDEX IF NOT EXISTS ""IX_TravelGuides_UserId"" ON ""TravelGuides"" (""UserId"", ""Id"")",
            @"ALTER TABLE ""AgentChatSessions"" ADD COLUMN IF NOT EXISTS ""BotType"" TEXT",
            @"CREATE TABLE IF NOT EXISTS ""ModuleJobs"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Module"" TEXT NOT NULL DEFAULT '', ""JobType"" TEXT NOT NULL DEFAULT '', ""Title"" TEXT NOT NULL DEFAULT '', ""Status"" TEXT NOT NULL DEFAULT 'running', ""ResultText"" TEXT, ""ErrorMessage"" TEXT, ""IsRead"" BOOLEAN NOT NULL DEFAULT FALSE, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""CompletedAt"" TIMESTAMPTZ, ""DurationMs"" BIGINT NOT NULL DEFAULT 0)",
            @"CREATE INDEX IF NOT EXISTS ""IX_ModuleJobs_UserId_CreatedAt"" ON ""ModuleJobs"" (""UserId"", ""CreatedAt"")",
            @"CREATE TABLE IF NOT EXISTS ""InteractionSessions"" (""Id"" TEXT PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""SourceLabel"" TEXT NOT NULL DEFAULT '', ""Transcript"" TEXT NOT NULL DEFAULT '', ""AnalysesJson"" TEXT NOT NULL DEFAULT '', ""Intent"" TEXT, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"ALTER TABLE ""InteractionSessions"" ADD COLUMN IF NOT EXISTS ""Intent"" TEXT",
            @"CREATE INDEX IF NOT EXISTS ""IX_InteractionSessions_UserId"" ON ""InteractionSessions"" (""UserId"", ""Id"")",
            @"CREATE TABLE IF NOT EXISTS ""McpCatalogServers"" (""Id"" SERIAL PRIMARY KEY, ""Name"" TEXT NOT NULL, ""Title"" TEXT NOT NULL DEFAULT '', ""Description"" TEXT NOT NULL DEFAULT '', ""Version"" TEXT NOT NULL DEFAULT '', ""Category"" TEXT NOT NULL DEFAULT 'Other', ""Transport"" TEXT NOT NULL DEFAULT 'remote', ""RemoteUrl"" TEXT NOT NULL DEFAULT '', ""Publisher"" TEXT NOT NULL DEFAULT '', ""WebsiteUrl"" TEXT, ""IsConnectable"" BOOLEAN NOT NULL DEFAULT TRUE, ""RegistryUpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""SyncedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_McpCatalogServers_Name"" ON ""McpCatalogServers"" (""Name"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_McpCatalogServers_Category"" ON ""McpCatalogServers"" (""Category"", ""IsConnectable"")",
            @"CREATE TABLE IF NOT EXISTS ""McpConnections"" (""Id"" TEXT PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""OrganizationId"" TEXT, ""ServerName"" TEXT NOT NULL DEFAULT '', ""Label"" TEXT NOT NULL DEFAULT '', ""RemoteUrl"" TEXT NOT NULL DEFAULT '', ""AuthType"" TEXT NOT NULL DEFAULT 'none', ""SecretEncrypted"" TEXT, ""HeaderName"" TEXT, ""Status"" TEXT NOT NULL DEFAULT 'connected', ""LastError"" TEXT, ""LastCheckedAt"" TIMESTAMPTZ, ""ToolCount"" INTEGER NOT NULL DEFAULT 0, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_McpConnections_UserId"" ON ""McpConnections"" (""UserId"")",
            @"CREATE TABLE IF NOT EXISTS ""McpConnectionTools"" (""Id"" SERIAL PRIMARY KEY, ""ConnectionId"" TEXT NOT NULL, ""UserId"" TEXT NOT NULL, ""ToolName"" TEXT NOT NULL DEFAULT '', ""Description"" TEXT NOT NULL DEFAULT '', ""InputSchemaJson"" TEXT NOT NULL DEFAULT '{}', ""Enabled"" BOOLEAN NOT NULL DEFAULT TRUE, ""ApprovalMode"" TEXT NOT NULL DEFAULT 'ask', ""IsWrite"" BOOLEAN NOT NULL DEFAULT TRUE, ""DiscoveredAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_McpConnectionTools_Connection"" ON ""McpConnectionTools"" (""ConnectionId"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_McpConnectionTools_User"" ON ""McpConnectionTools"" (""UserId"", ""Enabled"")",
            @"CREATE TABLE IF NOT EXISTS ""OrchestrationRuns"" (""Id"" TEXT PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""OrganizationId"" TEXT, ""Goal"" TEXT NOT NULL DEFAULT '', ""Status"" TEXT NOT NULL DEFAULT 'planning', ""PlanSummary"" TEXT NOT NULL DEFAULT '', ""Result"" TEXT, ""ErrorMessage"" TEXT, ""MaxSteps"" INTEGER NOT NULL DEFAULT 40, ""StepsUsed"" INTEGER NOT NULL DEFAULT 0, ""SourceNodeId"" UUID, ""ResultNodeId"" UUID, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""CompletedAt"" TIMESTAMPTZ)",
            @"CREATE INDEX IF NOT EXISTS ""IX_OrchestrationRuns_User"" ON ""OrchestrationRuns"" (""UserId"", ""CreatedAt"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_OrchestrationRuns_Status"" ON ""OrchestrationRuns"" (""Status"", ""UpdatedAt"")",
            @"CREATE TABLE IF NOT EXISTS ""OrchestrationTasks"" (""Id"" SERIAL PRIMARY KEY, ""RunId"" TEXT NOT NULL, ""UserId"" TEXT NOT NULL, ""Key"" TEXT NOT NULL DEFAULT '', ""Title"" TEXT NOT NULL DEFAULT '', ""Tool"" TEXT NOT NULL DEFAULT '', ""ArgumentsJson"" TEXT NOT NULL DEFAULT '{}', ""DependsOnJson"" TEXT NOT NULL DEFAULT '[]', ""Status"" TEXT NOT NULL DEFAULT 'pending', ""Output"" TEXT, ""ErrorMessage"" TEXT, ""Attempts"" INTEGER NOT NULL DEFAULT 0, ""NeedsApproval"" BOOLEAN NOT NULL DEFAULT FALSE, ""ApprovedBy"" TEXT, ""ApprovedAt"" TIMESTAMPTZ, ""OrderIndex"" INTEGER NOT NULL DEFAULT 0, ""StartedAt"" TIMESTAMPTZ, ""CompletedAt"" TIMESTAMPTZ, ""DurationMs"" INTEGER NOT NULL DEFAULT 0, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_OrchestrationTasks_Run"" ON ""OrchestrationTasks"" (""RunId"", ""OrderIndex"")"
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
