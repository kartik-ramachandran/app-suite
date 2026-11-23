using Microsoft.EntityFrameworkCore;

namespace PromptStudio.Api.Data;

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
            "CREATE INDEX IF NOT EXISTS IX_ModuleJobs_UserId_CreatedAt ON ModuleJobs (UserId, CreatedAt)"
    };

    private static readonly string[] PostgresCmds = new string[]
    {
            @"CREATE TABLE IF NOT EXISTS ""ModuleJobs"" (""Id"" SERIAL PRIMARY KEY, ""UserId"" TEXT NOT NULL, ""Module"" TEXT NOT NULL DEFAULT '', ""JobType"" TEXT NOT NULL DEFAULT '', ""Title"" TEXT NOT NULL DEFAULT '', ""Status"" TEXT NOT NULL DEFAULT 'running', ""ResultText"" TEXT, ""ErrorMessage"" TEXT, ""IsRead"" BOOLEAN NOT NULL DEFAULT FALSE, ""CreatedAt"" TIMESTAMPTZ NOT NULL DEFAULT NOW(), ""CompletedAt"" TIMESTAMPTZ, ""DurationMs"" BIGINT NOT NULL DEFAULT 0)",
            @"CREATE INDEX IF NOT EXISTS ""IX_ModuleJobs_UserId_CreatedAt"" ON ""ModuleJobs"" (""UserId"", ""CreatedAt"")"
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
