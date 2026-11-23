using Microsoft.EntityFrameworkCore;

namespace LifeManager.Api.Data;

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
            "CREATE TABLE IF NOT EXISTS LifeCheckIns (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Date TEXT NOT NULL, Mood INTEGER NOT NULL DEFAULT 5, Energy INTEGER NOT NULL DEFAULT 5, Focus INTEGER NOT NULL DEFAULT 5, SleepHours REAL NOT NULL DEFAULT 7, ExerciseMinutes INTEGER NOT NULL DEFAULT 0, ScreenMinutes INTEGER NOT NULL DEFAULT 0, MoneySpent TEXT NOT NULL DEFAULT '0', TopTasks TEXT NOT NULL DEFAULT '', Gratitude TEXT NOT NULL DEFAULT '', Notes TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_LifeCheckIns_UserId_Date ON LifeCheckIns (UserId, Date)",
            "CREATE TABLE IF NOT EXISTS LifeGoals (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Title TEXT NOT NULL, Area TEXT NOT NULL DEFAULT 'Growth', TargetDate TEXT, Progress INTEGER NOT NULL DEFAULT 0, Status TEXT NOT NULL DEFAULT 'active', Notes TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_LifeGoals_UserId_Status ON LifeGoals (UserId, Status)",
            "CREATE TABLE IF NOT EXISTS LifeDecisions (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Date TEXT NOT NULL, Title TEXT NOT NULL, Reasoning TEXT NOT NULL DEFAULT '', Outcome TEXT NOT NULL DEFAULT '', ReviewDate TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_LifeDecisions_UserId_Date ON LifeDecisions (UserId, Date)",
            "CREATE TABLE IF NOT EXISTS LifeRelationships (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Name TEXT NOT NULL, Circle TEXT NOT NULL DEFAULT 'Friends', LastContactedAt TEXT, Importance INTEGER NOT NULL DEFAULT 5, Notes TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_LifeRelationships_UserId_Name ON LifeRelationships (UserId, Name)",
            "CREATE TABLE IF NOT EXISTS LifeEvidence (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, FileName TEXT NOT NULL, StoredFileName TEXT NOT NULL, ContentType TEXT NOT NULL, FileSize INTEGER NOT NULL DEFAULT 0, EvidenceType TEXT NOT NULL DEFAULT 'document', ExtractedText TEXT NOT NULL DEFAULT '', Notes TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_LifeEvidence_UserId_CreatedAt ON LifeEvidence (UserId, CreatedAt)",
            "CREATE TABLE IF NOT EXISTS LifeCloudLinks (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Provider TEXT NOT NULL DEFAULT 'Google Drive', DisplayName TEXT NOT NULL DEFAULT '', Url TEXT NOT NULL DEFAULT '', Notes TEXT NOT NULL DEFAULT '', ExtractedText TEXT NOT NULL DEFAULT '', SyncStatus TEXT NOT NULL DEFAULT 'pending', LastSyncedAt TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_LifeCloudLinks_UserId_Provider ON LifeCloudLinks (UserId, Provider)",
            "CREATE TABLE IF NOT EXISTS MeetingSessions (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, CalendarEventId INTEGER, InteractionSessionId TEXT, Title TEXT NOT NULL DEFAULT '', MeetingLink TEXT, Platform TEXT NOT NULL DEFAULT 'other', Source TEXT NOT NULL DEFAULT 'tab', ExternalId TEXT, BotToken TEXT, Status TEXT NOT NULL DEFAULT 'armed', ErrorMessage TEXT, Transcript TEXT NOT NULL DEFAULT '', ChunkCount INTEGER NOT NULL DEFAULT 0, ParticipantsJson TEXT NOT NULL DEFAULT '[]', AutoToolsJson TEXT NOT NULL DEFAULT '[]', ScheduledStart TEXT NOT NULL DEFAULT (datetime('now')), ScheduledEnd TEXT NOT NULL DEFAULT (datetime('now')), StartedAt TEXT, EndedAt TEXT, LastChunkAt TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_MeetingSessions_User_Status ON MeetingSessions (UserId, Status)"
    };

    private static readonly string[] PostgresCmds = new string[]
    {
            @"CREATE TABLE IF NOT EXISTS ""UserTravelPrefs"" (""UserId"" TEXT PRIMARY KEY, ""DietaryPreferences"" TEXT, ""CreatedAt"" TEXT NOT NULL DEFAULT '', ""UpdatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE TABLE IF NOT EXISTS ""TravelGuides"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Destination"" TEXT NOT NULL DEFAULT '', ""StartDate"" TEXT, ""EndDate"" TEXT, ""GuideJson"" TEXT NOT NULL DEFAULT '{}', ""CreatedAt"" TEXT NOT NULL DEFAULT '')",
            @"CREATE INDEX IF NOT EXISTS ""IX_TravelGuides_UserId"" ON ""TravelGuides"" (""UserId"", ""Id"")",
            @"CREATE TABLE IF NOT EXISTS ""LifeCheckIns"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Date"" TIMESTAMPTZ NOT NULL, ""Mood"" INTEGER NOT NULL DEFAULT 5, ""Energy"" INTEGER NOT NULL DEFAULT 5, ""Focus"" INTEGER NOT NULL DEFAULT 5, ""SleepHours"" DOUBLE PRECISION NOT NULL DEFAULT 7, ""ExerciseMinutes"" INTEGER NOT NULL DEFAULT 0, ""ScreenMinutes"" INTEGER NOT NULL DEFAULT 0, ""MoneySpent"" NUMERIC NOT NULL DEFAULT 0, ""TopTasks"" TEXT NOT NULL DEFAULT '', ""Gratitude"" TEXT NOT NULL DEFAULT '', ""Notes"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_LifeCheckIns_UserId_Date"" ON ""LifeCheckIns"" (""UserId"", ""Date"")",
            @"CREATE TABLE IF NOT EXISTS ""LifeGoals"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Title"" TEXT NOT NULL, ""Area"" TEXT NOT NULL DEFAULT 'Growth', ""TargetDate"" TIMESTAMPTZ, ""Progress"" INTEGER NOT NULL DEFAULT 0, ""Status"" TEXT NOT NULL DEFAULT 'active', ""Notes"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_LifeGoals_UserId_Status"" ON ""LifeGoals"" (""UserId"", ""Status"")",
            @"CREATE TABLE IF NOT EXISTS ""LifeDecisions"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Date"" TIMESTAMPTZ NOT NULL, ""Title"" TEXT NOT NULL, ""Reasoning"" TEXT NOT NULL DEFAULT '', ""Outcome"" TEXT NOT NULL DEFAULT '', ""ReviewDate"" TIMESTAMPTZ, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_LifeDecisions_UserId_Date"" ON ""LifeDecisions"" (""UserId"", ""Date"")",
            @"CREATE TABLE IF NOT EXISTS ""LifeRelationships"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Name"" TEXT NOT NULL, ""Circle"" TEXT NOT NULL DEFAULT 'Friends', ""LastContactedAt"" TIMESTAMPTZ, ""Importance"" INTEGER NOT NULL DEFAULT 5, ""Notes"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_LifeRelationships_UserId_Name"" ON ""LifeRelationships"" (""UserId"", ""Name"")",
            @"CREATE TABLE IF NOT EXISTS ""LifeEvidence"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""FileName"" TEXT NOT NULL, ""StoredFileName"" TEXT NOT NULL, ""ContentType"" TEXT NOT NULL, ""FileSize"" BIGINT NOT NULL DEFAULT 0, ""EvidenceType"" TEXT NOT NULL DEFAULT 'document', ""ExtractedText"" TEXT NOT NULL DEFAULT '', ""Notes"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_LifeEvidence_UserId_CreatedAt"" ON ""LifeEvidence"" (""UserId"", ""CreatedAt"")",
            @"CREATE TABLE IF NOT EXISTS ""LifeCloudLinks"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Provider"" TEXT NOT NULL DEFAULT 'Google Drive', ""DisplayName"" TEXT NOT NULL DEFAULT '', ""Url"" TEXT NOT NULL DEFAULT '', ""Notes"" TEXT NOT NULL DEFAULT '', ""ExtractedText"" TEXT NOT NULL DEFAULT '', ""SyncStatus"" TEXT NOT NULL DEFAULT 'pending', ""LastSyncedAt"" TIMESTAMPTZ, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_LifeCloudLinks_UserId_Provider"" ON ""LifeCloudLinks"" (""UserId"", ""Provider"")",
            @"CREATE TABLE IF NOT EXISTS ""MeetingSessions"" (""Id"" TEXT PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""CalendarEventId"" INTEGER, ""InteractionSessionId"" TEXT, ""Title"" TEXT NOT NULL DEFAULT '', ""MeetingLink"" TEXT, ""Platform"" TEXT NOT NULL DEFAULT 'other', ""Source"" TEXT NOT NULL DEFAULT 'tab', ""ExternalId"" TEXT, ""BotToken"" TEXT, ""Status"" TEXT NOT NULL DEFAULT 'armed', ""ErrorMessage"" TEXT, ""Transcript"" TEXT NOT NULL DEFAULT '', ""ChunkCount"" INTEGER NOT NULL DEFAULT 0, ""ParticipantsJson"" TEXT NOT NULL DEFAULT '[]', ""AutoToolsJson"" TEXT NOT NULL DEFAULT '[]', ""ScheduledStart"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""ScheduledEnd"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""StartedAt"" TIMESTAMPTZ, ""EndedAt"" TIMESTAMPTZ, ""LastChunkAt"" TIMESTAMPTZ, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"ALTER TABLE ""MeetingSessions"" ADD COLUMN IF NOT EXISTS ""ExternalId"" TEXT",
            @"ALTER TABLE ""MeetingSessions"" ADD COLUMN IF NOT EXISTS ""BotToken"" TEXT",
            @"ALTER TABLE ""MeetingSessions"" ADD COLUMN IF NOT EXISTS ""ParticipantsJson"" TEXT NOT NULL DEFAULT '[]'",
            @"CREATE INDEX IF NOT EXISTS ""IX_MeetingSessions_User_Status"" ON ""MeetingSessions"" (""UserId"", ""Status"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_MeetingSessions_External"" ON ""MeetingSessions"" (""ExternalId"")"
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
