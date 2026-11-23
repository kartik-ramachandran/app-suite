using Microsoft.EntityFrameworkCore;

namespace BookReader.Api.Data;

/// <summary>
/// Raw-SQL tables (no EF model) this app reads and writes, copied from the
/// parent app's ApplyNonEfTablesAsync and filtered to the tables this app's
/// code actually references. Idempotent: every statement is IF NOT EXISTS.
/// </summary>
public static class SchemaBootstrap
{
    private static readonly string[] SqliteCmds = new string[]
    {
            "CREATE TABLE IF NOT EXISTS ModuleJobs (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, Module TEXT NOT NULL DEFAULT '', JobType TEXT NOT NULL DEFAULT '', Title TEXT NOT NULL DEFAULT '', Status TEXT NOT NULL DEFAULT 'running', ResultText TEXT, ErrorMessage TEXT, IsRead INTEGER NOT NULL DEFAULT 0, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), CompletedAt TEXT, DurationMs INTEGER NOT NULL DEFAULT 0)",
            "CREATE INDEX IF NOT EXISTS IX_ModuleJobs_UserId_CreatedAt ON ModuleJobs (UserId, CreatedAt)",
            "CREATE TABLE IF NOT EXISTS Organizations (Id TEXT PRIMARY KEY, Name TEXT NOT NULL DEFAULT '', Domain TEXT, Industry TEXT, Size TEXT, Website TEXT, Plan TEXT NOT NULL DEFAULT 'free', MaxMembers INTEGER NOT NULL DEFAULT 5, CreatedByUserId TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_Organizations_Domain ON Organizations (Domain)",
            "CREATE TABLE IF NOT EXISTS OrganizationMembers (Id TEXT PRIMARY KEY, OrganizationId TEXT NOT NULL, UserId TEXT NOT NULL, Role TEXT NOT NULL DEFAULT 'member', Status TEXT NOT NULL DEFAULT 'active', InvitedByUserId TEXT, JoinedAt TEXT NOT NULL DEFAULT (datetime('now')), CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_OrganizationMembers_Org_User ON OrganizationMembers (OrganizationId, UserId)",
            "CREATE INDEX IF NOT EXISTS IX_OrganizationMembers_UserId ON OrganizationMembers (UserId)",
            "CREATE TABLE IF NOT EXISTS AppUsages (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, OrganizationId TEXT, Module TEXT NOT NULL DEFAULT '', Endpoint TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_AppUsages_User_CreatedAt ON AppUsages (UserId, CreatedAt)",
            "CREATE INDEX IF NOT EXISTS IX_AppUsages_Org_CreatedAt ON AppUsages (OrganizationId, CreatedAt)"
    };

    private static readonly string[] PostgresCmds = new string[]
    {
            @"CREATE TABLE IF NOT EXISTS ""ModuleJobs"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Module"" TEXT NOT NULL DEFAULT '', ""JobType"" TEXT NOT NULL DEFAULT '', ""Title"" TEXT NOT NULL DEFAULT '', ""Status"" TEXT NOT NULL DEFAULT 'running', ""ResultText"" TEXT, ""ErrorMessage"" TEXT, ""IsRead"" BOOLEAN NOT NULL DEFAULT FALSE, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""CompletedAt"" TIMESTAMPTZ, ""DurationMs"" BIGINT NOT NULL DEFAULT 0)",
            @"CREATE INDEX IF NOT EXISTS ""IX_ModuleJobs_UserId_CreatedAt"" ON ""ModuleJobs"" (""UserId"", ""CreatedAt"")",
            @"CREATE TABLE IF NOT EXISTS ""Organizations"" (""Id"" TEXT PRIMARY KEY, ""Name"" TEXT NOT NULL DEFAULT '', ""Domain"" TEXT, ""Industry"" TEXT, ""Size"" TEXT, ""Website"" TEXT, ""Plan"" TEXT NOT NULL DEFAULT 'free', ""MaxMembers"" INTEGER NOT NULL DEFAULT 5, ""CreatedByUserId"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_Organizations_Domain"" ON ""Organizations"" (""Domain"")",
            @"CREATE TABLE IF NOT EXISTS ""OrganizationMembers"" (""Id"" TEXT PRIMARY KEY, ""OrganizationId"" TEXT NOT NULL, ""UserId"" TEXT NOT NULL, ""Role"" TEXT NOT NULL DEFAULT 'member', ""Status"" TEXT NOT NULL DEFAULT 'active', ""InvitedByUserId"" TEXT, ""JoinedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_OrganizationMembers_Org_User"" ON ""OrganizationMembers"" (""OrganizationId"", ""UserId"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_OrganizationMembers_UserId"" ON ""OrganizationMembers"" (""UserId"")",
            @"CREATE TABLE IF NOT EXISTS ""AppUsages"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""OrganizationId"" TEXT, ""Module"" TEXT NOT NULL DEFAULT '', ""Endpoint"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_AppUsages_User_CreatedAt"" ON ""AppUsages"" (""UserId"", ""CreatedAt"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_AppUsages_Org_CreatedAt"" ON ""AppUsages"" (""OrganizationId"", ""CreatedAt"")"
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
