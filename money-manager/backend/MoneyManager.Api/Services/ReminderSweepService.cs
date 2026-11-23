using Microsoft.EntityFrameworkCore;
using MoneyManager.Api.Data;

namespace MoneyManager.Api.Services;

/// <summary>
/// Unified reminders: periodically sweeps the lifestyle modules that hold a
/// date the user cares about — bill due dates, warranty and medicine expiries,
/// plant watering schedules — and surfaces anything due soon as a notification
/// through the existing ModuleJobs bell / SSE pipeline. Each occurrence is
/// deduplicated via a stable JobType key, so an item is announced once per
/// due date, not once per sweep.
/// </summary>
public class ReminderSweepService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReminderSweepService> _logger;

    public const string ModuleName = "reminders";

    public ReminderSweepService(IServiceScopeFactory scopeFactory, ILogger<ReminderSweepService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the app (and lazy table creation) a moment on cold start.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Reminder sweep failed");
            }

            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PromptDbContext>();
        var jobLog = scope.ServiceProvider.GetRequiredService<JobLogService>();

        // These tables are created on demand by their controllers, so on a
        // database where nobody has opened the page yet they simply do not
        // exist. Create them up front: the reads below then succeed (returning
        // nothing) instead of throwing a logged exception every sweep.
        await LifestyleTables.EnsureRemindableAsync(db, _logger);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var due = new List<ReminderItem>();
        due.AddRange(await LoadBillsAsync(db, today));
        due.AddRange(await LoadWarrantiesAsync(db, today));
        due.AddRange(await LoadMedicinesAsync(db, today));
        due.AddRange(await LoadPlantsAsync(db, today));
        if (due.Count == 0) return;

        // One notification per occurrence: skip keys we've already announced.
        var keys = due.Select(d => d.Key).ToList();
        var existing = await db.ModuleJobs
            .Where(j => j.Module == ModuleName && keys.Contains(j.JobType))
            .Select(j => j.JobType)
            .ToListAsync(ct);
        var seen = existing.ToHashSet();

        foreach (var item in due.Where(d => !seen.Contains(d.Key)))
        {
            var job = await jobLog.StartAsync(item.UserId, ModuleName, item.Key, item.Title);
            await jobLog.CompleteAsync(job, item.Detail);
        }

        var created = due.Count(d => !seen.Contains(d.Key));
        if (created > 0) _logger.LogInformation("Reminder sweep created {Count} notifications", created);
    }

    // ── Sources ──────────────────────────────────────────────────────────────
    // These tables hold raw rows rather than EF entities; LifestyleTables owns
    // their DDL and callers create them before reading. The try/catch in each
    // source stays as a backstop, and all date filtering happens in memory to
    // stay SQLite/Postgres agnostic.

    public sealed record ReminderItem(string UserId, string Kind, string Key, string Title, string Detail, DateOnly Date);

    private sealed record BillRow(int Id, string UserId, string? Biller, string? DueDate);
    private sealed record WarrantyRow(int Id, string UserId, string? Product, string? ExpiryDate);
    private sealed record MedicineRow(int Id, string UserId, string? Name, string? ExpiryDate);
    private sealed record PlantRow(int Id, string UserId, string? Name, string? Nickname, int? WaterEveryDays, string? LastWatered);

    internal static async Task<List<ReminderItem>> LoadBillsAsync(PromptDbContext db, DateOnly today, string? userId = null)
    {
        var items = new List<ReminderItem>();
        try
        {
            var rows = await db.Database
                .SqlQuery<BillRow>($@"SELECT ""Id"", ""UserId"", ""Biller"", ""DueDate"" FROM ""Bills"" WHERE ""DueDate"" IS NOT NULL")
                .ToListAsync();
            foreach (var r in rows.Where(r => userId == null || r.UserId == userId))
            {
                if (!DateOnly.TryParse(r.DueDate, out var d)) continue;
                var days = d.DayNumber - today.DayNumber;
                if (days is < -1 or > 3) continue;   // announce from 3 days out until 1 day overdue
                var name = string.IsNullOrWhiteSpace(r.Biller) ? "A bill" : r.Biller;
                items.Add(new ReminderItem(r.UserId, "bill", $"bill:{r.Id}:{r.DueDate}",
                    days < 0 ? $"Bill overdue: {name}" : days == 0 ? $"Bill due today: {name}" : $"Bill due {Pretty(d)}: {name}",
                    "From your Bill & Subscription Tracker.", d));
            }
        }
        catch { /* table not created yet */ }
        return items;
    }

    internal static async Task<List<ReminderItem>> LoadWarrantiesAsync(PromptDbContext db, DateOnly today, string? userId = null)
    {
        var items = new List<ReminderItem>();
        try
        {
            var rows = await db.Database
                .SqlQuery<WarrantyRow>($@"SELECT ""Id"", ""UserId"", ""Product"", ""ExpiryDate"" FROM ""Warranties"" WHERE ""ExpiryDate"" IS NOT NULL")
                .ToListAsync();
            foreach (var r in rows.Where(r => userId == null || r.UserId == userId))
            {
                if (!DateOnly.TryParse(r.ExpiryDate, out var d)) continue;
                var days = d.DayNumber - today.DayNumber;
                if (days is < 0 or > 14) continue;
                var name = string.IsNullOrWhiteSpace(r.Product) ? "A product" : r.Product;
                items.Add(new ReminderItem(r.UserId, "warranty", $"warranty:{r.Id}:{r.ExpiryDate}",
                    days == 0 ? $"Warranty expires today: {name}" : $"Warranty expires {Pretty(d)}: {name}",
                    "From your Warranty Vault — claim anything outstanding before it lapses.", d));
            }
        }
        catch { /* table not created yet */ }
        return items;
    }

    internal static async Task<List<ReminderItem>> LoadMedicinesAsync(PromptDbContext db, DateOnly today, string? userId = null)
    {
        var items = new List<ReminderItem>();
        try
        {
            var rows = await db.Database
                .SqlQuery<MedicineRow>($@"SELECT ""Id"", ""UserId"", ""Name"", ""ExpiryDate"" FROM ""Medicines"" WHERE ""ExpiryDate"" IS NOT NULL")
                .ToListAsync();
            foreach (var r in rows.Where(r => userId == null || r.UserId == userId))
            {
                if (!DateOnly.TryParse(r.ExpiryDate, out var d)) continue;
                var days = d.DayNumber - today.DayNumber;
                if (days is < 0 or > 14) continue;
                var name = string.IsNullOrWhiteSpace(r.Name) ? "A medicine" : r.Name;
                items.Add(new ReminderItem(r.UserId, "medicine", $"medicine:{r.Id}:{r.ExpiryDate}",
                    days == 0 ? $"Medicine expires today: {name}" : $"Medicine expires {Pretty(d)}: {name}",
                    "From your Medicine Cabinet — replace it and dispose of the old one safely.", d));
            }
        }
        catch { /* table not created yet */ }
        return items;
    }

    internal static async Task<List<ReminderItem>> LoadPlantsAsync(PromptDbContext db, DateOnly today, string? userId = null)
    {
        var items = new List<ReminderItem>();
        try
        {
            var rows = await db.Database
                .SqlQuery<PlantRow>($@"SELECT ""Id"", ""UserId"", ""Name"", ""Nickname"", ""WaterEveryDays"", ""LastWatered"" FROM ""Plants"" WHERE ""WaterEveryDays"" IS NOT NULL AND ""LastWatered"" IS NOT NULL")
                .ToListAsync();
            foreach (var r in rows.Where(r => userId == null || r.UserId == userId))
            {
                if (r.WaterEveryDays is not > 0 || !DateOnly.TryParse(r.LastWatered, out var last)) continue;
                var dueDate = last.AddDays(r.WaterEveryDays.Value);
                if (dueDate > today) continue;      // not due yet; overdue keeps reminding per-cycle key
                var name = !string.IsNullOrWhiteSpace(r.Nickname) ? r.Nickname
                    : !string.IsNullOrWhiteSpace(r.Name) ? r.Name : "Your plant";
                items.Add(new ReminderItem(r.UserId, "plant", $"plant:{r.Id}:{dueDate:yyyy-MM-dd}",
                    dueDate == today ? $"Water {name} today" : $"{name} needs watering (due {Pretty(dueDate)})",
                    "From Plant Doctor — mark it watered once you've done it.", dueDate));
            }
        }
        catch { /* table not created yet */ }
        return items;
    }

    private static string Pretty(DateOnly d) => d.ToString("ddd d MMM");
}
