using Microsoft.EntityFrameworkCore;

namespace Cosmos.Api.Data;

/// <summary>
/// Raw-SQL tables (no EF model) this app reads and writes, copied from the
/// parent app's ApplyNonEfTablesAsync and filtered to the tables this app's
/// code actually references. Idempotent: every statement is IF NOT EXISTS.
/// </summary>
public static class SchemaBootstrap
{
    private static readonly string[] SqliteCmds = new string[]
    {
            "CREATE TABLE IF NOT EXISTS UserTravelPrefs (UserId TEXT PRIMARY KEY, DietaryPreferences TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE TABLE IF NOT EXISTS TravelGuides (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Destination TEXT NOT NULL DEFAULT '', StartDate TEXT, EndDate TEXT, GuideJson TEXT NOT NULL DEFAULT '{}', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_TravelGuides_UserId ON TravelGuides (UserId, Id)",
            "CREATE TABLE IF NOT EXISTS LifeGoals (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Title TEXT NOT NULL, Area TEXT NOT NULL DEFAULT 'Growth', TargetDate TEXT, Progress INTEGER NOT NULL DEFAULT 0, Status TEXT NOT NULL DEFAULT 'active', Notes TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_LifeGoals_UserId_Status ON LifeGoals (UserId, Status)",
            "CREATE TABLE IF NOT EXISTS LifeDecisions (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Date TEXT NOT NULL, Title TEXT NOT NULL, Reasoning TEXT NOT NULL DEFAULT '', Outcome TEXT NOT NULL DEFAULT '', ReviewDate TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_LifeDecisions_UserId_Date ON LifeDecisions (UserId, Date)",
            "CREATE TABLE IF NOT EXISTS LifeEvidence (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, FileName TEXT NOT NULL, StoredFileName TEXT NOT NULL, ContentType TEXT NOT NULL, FileSize INTEGER NOT NULL DEFAULT 0, EvidenceType TEXT NOT NULL DEFAULT 'document', ExtractedText TEXT NOT NULL DEFAULT '', Notes TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_LifeEvidence_UserId_CreatedAt ON LifeEvidence (UserId, CreatedAt)",
            "CREATE TABLE IF NOT EXISTS MeetingSessions (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, CalendarEventId INTEGER, InteractionSessionId TEXT, Title TEXT NOT NULL DEFAULT '', MeetingLink TEXT, Platform TEXT NOT NULL DEFAULT 'other', Source TEXT NOT NULL DEFAULT 'tab', ExternalId TEXT, BotToken TEXT, Status TEXT NOT NULL DEFAULT 'armed', ErrorMessage TEXT, Transcript TEXT NOT NULL DEFAULT '', ChunkCount INTEGER NOT NULL DEFAULT 0, ParticipantsJson TEXT NOT NULL DEFAULT '[]', AutoToolsJson TEXT NOT NULL DEFAULT '[]', ScheduledStart TEXT NOT NULL DEFAULT (datetime('now')), ScheduledEnd TEXT NOT NULL DEFAULT (datetime('now')), StartedAt TEXT, EndedAt TEXT, LastChunkAt TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_MeetingSessions_User_Status ON MeetingSessions (UserId, Status)",
            "CREATE TABLE IF NOT EXISTS EmmdDocuments (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, OrganizationId TEXT, Title TEXT NOT NULL DEFAULT 'Untitled document', Theme TEXT NOT NULL DEFAULT 'clean', Version INTEGER NOT NULL DEFAULT 0, Content TEXT NOT NULL DEFAULT '', SealToken TEXT, SealHash TEXT, SealedBy TEXT, SealedAt TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "ALTER TABLE EmmdDocuments ADD COLUMN Version INTEGER NOT NULL DEFAULT 0",
            "CREATE INDEX IF NOT EXISTS IX_EmmdDocuments_UserId ON EmmdDocuments (UserId, UpdatedAt)",
            "CREATE INDEX IF NOT EXISTS IX_EmmdDocuments_OrgId ON EmmdDocuments (OrganizationId, UpdatedAt)"
    };

    private static readonly string[] PostgresCmds = new string[]
    {
            @"CREATE TABLE IF NOT EXISTS ""UserTravelPrefs"" (""UserId"" TEXT PRIMARY KEY, ""DietaryPreferences"" TEXT, ""CreatedAt"" TEXT NOT NULL DEFAULT '', ""UpdatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE TABLE IF NOT EXISTS ""TravelGuides"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Destination"" TEXT NOT NULL DEFAULT '', ""StartDate"" TEXT, ""EndDate"" TEXT, ""GuideJson"" TEXT NOT NULL DEFAULT '{}', ""CreatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE INDEX IF NOT EXISTS ""IX_TravelGuides_UserId"" ON ""TravelGuides"" (""UserId"", ""Id"")",
            @"CREATE TABLE IF NOT EXISTS ""LifeGoals"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Title"" TEXT NOT NULL, ""Area"" TEXT NOT NULL DEFAULT 'Growth', ""TargetDate"" TIMESTAMPTZ, ""Progress"" INTEGER NOT NULL DEFAULT 0, ""Status"" TEXT NOT NULL DEFAULT 'active', ""Notes"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_LifeGoals_UserId_Status"" ON ""LifeGoals"" (""UserId"", ""Status"")",
            @"CREATE TABLE IF NOT EXISTS ""LifeDecisions"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Date"" TIMESTAMPTZ NOT NULL, ""Title"" TEXT NOT NULL, ""Reasoning"" TEXT NOT NULL DEFAULT '', ""Outcome"" TEXT NOT NULL DEFAULT '', ""ReviewDate"" TIMESTAMPTZ, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_LifeDecisions_UserId_Date"" ON ""LifeDecisions"" (""UserId"", ""Date"")",
            @"CREATE TABLE IF NOT EXISTS ""LifeEvidence"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""FileName"" TEXT NOT NULL, ""StoredFileName"" TEXT NOT NULL, ""ContentType"" TEXT NOT NULL, ""FileSize"" BIGINT NOT NULL DEFAULT 0, ""EvidenceType"" TEXT NOT NULL DEFAULT 'document', ""ExtractedText"" TEXT NOT NULL DEFAULT '', ""Notes"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_LifeEvidence_UserId_CreatedAt"" ON ""LifeEvidence"" (""UserId"", ""CreatedAt"")",
            @"CREATE TABLE IF NOT EXISTS ""MeetingSessions"" (""Id"" TEXT PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""CalendarEventId"" INTEGER, ""InteractionSessionId"" TEXT, ""Title"" TEXT NOT NULL DEFAULT '', ""MeetingLink"" TEXT, ""Platform"" TEXT NOT NULL DEFAULT 'other', ""Source"" TEXT NOT NULL DEFAULT 'tab', ""ExternalId"" TEXT, ""BotToken"" TEXT, ""Status"" TEXT NOT NULL DEFAULT 'armed', ""ErrorMessage"" TEXT, ""Transcript"" TEXT NOT NULL DEFAULT '', ""ChunkCount"" INTEGER NOT NULL DEFAULT 0, ""ParticipantsJson"" TEXT NOT NULL DEFAULT '[]', ""AutoToolsJson"" TEXT NOT NULL DEFAULT '[]', ""ScheduledStart"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""ScheduledEnd"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""StartedAt"" TIMESTAMPTZ, ""EndedAt"" TIMESTAMPTZ, ""LastChunkAt"" TIMESTAMPTZ, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"ALTER TABLE ""MeetingSessions"" ADD COLUMN IF NOT EXISTS ""ExternalId"" TEXT",
            @"ALTER TABLE ""MeetingSessions"" ADD COLUMN IF NOT EXISTS ""BotToken"" TEXT",
            @"ALTER TABLE ""MeetingSessions"" ADD COLUMN IF NOT EXISTS ""ParticipantsJson"" TEXT NOT NULL DEFAULT '[]'",
            @"CREATE INDEX IF NOT EXISTS ""IX_MeetingSessions_User_Status"" ON ""MeetingSessions"" (""UserId"", ""Status"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_MeetingSessions_External"" ON ""MeetingSessions"" (""ExternalId"")",
            @"CREATE TABLE IF NOT EXISTS ""EmmdDocuments"" (""Id"" TEXT PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""OrganizationId"" TEXT, ""Title"" TEXT NOT NULL DEFAULT 'Untitled document', ""Theme"" TEXT NOT NULL DEFAULT 'clean', ""Version"" INTEGER NOT NULL DEFAULT 0, ""Content"" TEXT NOT NULL DEFAULT '', ""SealToken"" TEXT, ""SealHash"" TEXT, ""SealedBy"" TEXT, ""SealedAt"" TIMESTAMPTZ, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"ALTER TABLE ""EmmdDocuments"" ADD COLUMN IF NOT EXISTS ""Version"" INTEGER NOT NULL DEFAULT 0",
            @"CREATE INDEX IF NOT EXISTS ""IX_EmmdDocuments_UserId"" ON ""EmmdDocuments"" (""UserId"", ""UpdatedAt"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_EmmdDocuments_OrgId"" ON ""EmmdDocuments"" (""OrganizationId"", ""UpdatedAt"")"
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
