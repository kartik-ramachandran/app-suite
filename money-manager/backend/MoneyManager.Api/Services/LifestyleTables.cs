using Microsoft.EntityFrameworkCore;
using MoneyManager.Api.Data;

namespace MoneyManager.Api.Services;

/// <summary>
/// The lifestyle snap-tools store their rows in plain tables rather than EF
/// entities, created on demand with raw SQL. They used to be created only by
/// their own controller, which meant anything else that read them — notably
/// <see cref="ReminderSweepService"/> — hit "relation does not exist" on a
/// database where nobody had opened that page yet.
///
/// The DDL lives here so the controllers and the sweep agree on the shape, and
/// so adding a column is a single edit rather than a hunt for the other copy.
/// </summary>
public static class LifestyleTables
{
    // One flag per table: a successful CREATE ... IF NOT EXISTS need not be repeated.
    private static bool _bills, _warranties, _medicines, _plants;

    private static string PrimaryKey(PromptDbContext db) =>
        (db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) ?? false)
            ? "INTEGER PRIMARY KEY AUTOINCREMENT"
            : "SERIAL PRIMARY KEY";

    public static async Task EnsureBillsAsync(PromptDbContext db, ILogger? logger = null)
    {
        if (_bills) return;
        try
        {
            var pk = PrimaryKey(db);
            await db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""Bills"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Biller"" TEXT, ""Category"" TEXT, ""Amount"" REAL, ""Currency"" TEXT,
                ""DueDate"" TEXT, ""Recurring"" TEXT, ""Notes"" TEXT, ""CreatedAt"" TEXT)");
            await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_Bills_UserId"" ON ""Bills"" (""UserId"", ""DueDate"")");
            _bills = true;
        }
        catch (Exception ex) { logger?.LogWarning(ex, "EnsureBills failed"); }
    }

    public static async Task EnsureWarrantiesAsync(PromptDbContext db, ILogger? logger = null)
    {
        if (_warranties) return;
        try
        {
            var pk = PrimaryKey(db);
            await db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""Warranties"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Product"" TEXT, ""Brand"" TEXT, ""Retailer"" TEXT,
                ""PurchaseDate"" TEXT, ""WarrantyMonths"" INTEGER, ""ExpiryDate"" TEXT, ""Notes"" TEXT, ""ImageData"" TEXT, ""CreatedAt"" TEXT)");
            await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_Warranties_UserId"" ON ""Warranties"" (""UserId"", ""Id"")");
            _warranties = true;
        }
        catch (Exception ex) { logger?.LogWarning(ex, "EnsureWarranties failed"); }
    }

    public static async Task EnsureMedicinesAsync(PromptDbContext db, ILogger? logger = null)
    {
        if (_medicines) return;
        try
        {
            var pk = PrimaryKey(db);
            await db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""Medicines"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Name"" TEXT, ""Form"" TEXT, ""Strength"" TEXT,
                ""Purpose"" TEXT, ""ExpiryDate"" TEXT, ""Notes"" TEXT, ""ImageData"" TEXT, ""CreatedAt"" TEXT)");
            await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_Medicines_UserId"" ON ""Medicines"" (""UserId"", ""Id"")");
            _medicines = true;
        }
        catch (Exception ex) { logger?.LogWarning(ex, "EnsureMedicines failed"); }
    }

    public static async Task EnsurePlantsAsync(PromptDbContext db, ILogger? logger = null)
    {
        if (_plants) return;
        try
        {
            var pk = PrimaryKey(db);
            await db.Database.ExecuteSqlRawAsync($@"CREATE TABLE IF NOT EXISTS ""Plants"" (
                ""Id"" {pk}, ""UserId"" TEXT NOT NULL, ""Name"" TEXT, ""Nickname"" TEXT, ""Species"" TEXT,
                ""Health"" TEXT, ""Diagnosis"" TEXT, ""Light"" TEXT, ""CareTips"" TEXT,
                ""WaterEveryDays"" INTEGER, ""LastWatered"" TEXT, ""ImageData"" TEXT, ""CreatedAt"" TEXT)");
            await db.Database.ExecuteSqlRawAsync(@"CREATE INDEX IF NOT EXISTS ""IX_Plants_UserId"" ON ""Plants"" (""UserId"", ""Id"")");
            _plants = true;
        }
        catch (Exception ex) { logger?.LogWarning(ex, "EnsurePlants failed"); }
    }

    /// <summary>Every table the reminder sweep reads.</summary>
    public static async Task EnsureRemindableAsync(PromptDbContext db, ILogger? logger = null)
    {
        await EnsureBillsAsync(db, logger);
        await EnsureWarrantiesAsync(db, logger);
        await EnsureMedicinesAsync(db, logger);
        await EnsurePlantsAsync(db, logger);
    }
}
