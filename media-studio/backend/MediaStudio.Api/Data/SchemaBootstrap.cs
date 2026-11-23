using Microsoft.EntityFrameworkCore;

namespace MediaStudio.Api.Data;

/// <summary>
/// Raw-SQL tables (no EF model) this app reads and writes, copied from the
/// parent app's ApplyNonEfTablesAsync and filtered to the tables this app's
/// code actually references. Idempotent: every statement is IF NOT EXISTS.
/// </summary>
public static class SchemaBootstrap
{
    private static readonly string[] SqliteCmds = new string[]
    {

    };

    private static readonly string[] PostgresCmds = new string[]
    {

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
