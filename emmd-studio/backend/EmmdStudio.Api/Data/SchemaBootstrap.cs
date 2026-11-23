using Microsoft.EntityFrameworkCore;

namespace EmmdStudio.Api.Data;

/// <summary>
/// Raw-SQL tables (no EF model) this app reads and writes, copied from the
/// parent app's ApplyNonEfTablesAsync and filtered to the tables this app's
/// code actually references. Idempotent: every statement is IF NOT EXISTS.
/// </summary>
public static class SchemaBootstrap
{
    private static readonly string[] SqliteCmds = new string[]
    {
            "CREATE TABLE IF NOT EXISTS Organizations (Id TEXT PRIMARY KEY, Name TEXT NOT NULL DEFAULT '', Domain TEXT, Industry TEXT, Size TEXT, Website TEXT, Plan TEXT NOT NULL DEFAULT 'free', MaxMembers INTEGER NOT NULL DEFAULT 5, CreatedByUserId TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_Organizations_Domain ON Organizations (Domain)",
            "CREATE TABLE IF NOT EXISTS OrganizationMembers (Id TEXT PRIMARY KEY, OrganizationId TEXT NOT NULL, UserId TEXT NOT NULL, Role TEXT NOT NULL DEFAULT 'member', Status TEXT NOT NULL DEFAULT 'active', InvitedByUserId TEXT, JoinedAt TEXT NOT NULL DEFAULT (datetime('now')), CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE UNIQUE INDEX IF NOT EXISTS IX_OrganizationMembers_Org_User ON OrganizationMembers (OrganizationId, UserId)",
            "CREATE INDEX IF NOT EXISTS IX_OrganizationMembers_UserId ON OrganizationMembers (UserId)",
            "CREATE TABLE IF NOT EXISTS AppUsages (Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, OrganizationId TEXT, Module TEXT NOT NULL DEFAULT '', Endpoint TEXT NOT NULL DEFAULT '', CreatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "CREATE INDEX IF NOT EXISTS IX_AppUsages_User_CreatedAt ON AppUsages (UserId, CreatedAt)",
            "CREATE INDEX IF NOT EXISTS IX_AppUsages_Org_CreatedAt ON AppUsages (OrganizationId, CreatedAt)",
            "CREATE TABLE IF NOT EXISTS EmmdDocuments (Id TEXT PRIMARY KEY, UserId TEXT NOT NULL, OrganizationId TEXT, Title TEXT NOT NULL DEFAULT 'Untitled document', Theme TEXT NOT NULL DEFAULT 'clean', Version INTEGER NOT NULL DEFAULT 0, Content TEXT NOT NULL DEFAULT '', SealToken TEXT, SealHash TEXT, SealedBy TEXT, SealedAt TEXT, CreatedAt TEXT NOT NULL DEFAULT (datetime('now')), UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')))",
            "ALTER TABLE EmmdDocuments ADD COLUMN Version INTEGER NOT NULL DEFAULT 0",
            "CREATE INDEX IF NOT EXISTS IX_EmmdDocuments_UserId ON EmmdDocuments (UserId, UpdatedAt)",
            "CREATE INDEX IF NOT EXISTS IX_EmmdDocuments_OrgId ON EmmdDocuments (OrganizationId, UpdatedAt)"
    };

    private static readonly string[] PostgresCmds = new string[]
    {
            @"CREATE TABLE IF NOT EXISTS ""Organizations"" (""Id"" TEXT PRIMARY KEY, ""Name"" TEXT NOT NULL DEFAULT '', ""Domain"" TEXT, ""Industry"" TEXT, ""Size"" TEXT, ""Website"" TEXT, ""Plan"" TEXT NOT NULL DEFAULT 'free', ""MaxMembers"" INTEGER NOT NULL DEFAULT 5, ""CreatedByUserId"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_Organizations_Domain"" ON ""Organizations"" (""Domain"")",
            @"CREATE TABLE IF NOT EXISTS ""OrganizationMembers"" (""Id"" TEXT PRIMARY KEY, ""OrganizationId"" TEXT NOT NULL, ""UserId"" TEXT NOT NULL, ""Role"" TEXT NOT NULL DEFAULT 'member', ""Status"" TEXT NOT NULL DEFAULT 'active', ""InvitedByUserId"" TEXT, ""JoinedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""UpdatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE UNIQUE INDEX IF NOT EXISTS ""IX_OrganizationMembers_Org_User"" ON ""OrganizationMembers"" (""OrganizationId"", ""UserId"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_OrganizationMembers_UserId"" ON ""OrganizationMembers"" (""UserId"")",
            @"CREATE TABLE IF NOT EXISTS ""AppUsages"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""OrganizationId"" TEXT, ""Module"" TEXT NOT NULL DEFAULT '', ""Endpoint"" TEXT NOT NULL DEFAULT '', ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW())",
            @"CREATE INDEX IF NOT EXISTS ""IX_AppUsages_User_CreatedAt"" ON ""AppUsages"" (""UserId"", ""CreatedAt"")",
            @"CREATE INDEX IF NOT EXISTS ""IX_AppUsages_Org_CreatedAt"" ON ""AppUsages"" (""OrganizationId"", ""CreatedAt"")",
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
