using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MoneyManager.Api.Data;
using MoneyManager.Api.Services;

namespace MoneyManager.Api.Controllers;

/// <summary>
/// One view of everything with a date attached to it.
///
/// WHY THIS EXISTS
/// Bills, warranties and medicines are three separate screens holding the same
/// shape of fact: a thing, a date, and a consequence for missing it. A warranty
/// expiring in three weeks is exactly as actionable as a subscription renewing
/// in three weeks, but they lived in different modules and nothing ever showed
/// them together — so the app could know your extended warranty ran out on
/// Friday and still never mention it.
///
/// This is a join, not a new feature: same database, same user, no new storage.
/// It is what turns a set of tools into something with memory.
///
/// Login-only by design. Every row is personal data.
/// </summary>
[ApiController]
[Route("api/upcoming")]
[Authorize]
public class UpcomingController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly ILogger<UpcomingController> _logger;

    public UpcomingController(PromptDbContext db, ILogger<UpcomingController> logger)
    {
        _db = db;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>A dated obligation from any module, normalised to one shape.</summary>
    public sealed record UpcomingItem(
        string Source,      // "bill" | "warranty" | "medicine"
        int Id,
        string Title,
        string? Detail,
        string Date,        // yyyy-MM-dd
        int DaysAway,       // negative = already passed
        string Urgency,     // "overdue" | "soon" | "later"
        string Route);      // where to act on it

    /// <summary>
    /// Everything dated, nearest first.
    /// </summary>
    /// <param name="days">
    /// Look-ahead window. Default 60: far enough that a warranty is still
    /// actionable (you can still claim), short enough to stay a list rather
    /// than an archive.
    /// </param>
    /// <param name="includePast">
    /// Include items already overdue. On by default — a bill you missed
    /// yesterday is the single most useful thing this endpoint can tell you.
    /// </param>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int days = 60, [FromQuery] bool includePast = true)
    {
        // Clamp rather than reject: a silly value should narrow the view, not
        // fail the request.
        days = Math.Clamp(days, 1, 365);

        await LifestyleTables.EnsureRemindableAsync(_db, _logger);

        var today = DateTime.UtcNow.Date;
        var horizon = today.AddDays(days);
        var items = new List<UpcomingItem>();

        // These tables are created on demand by LifestyleTables and queried with
        // raw SQL elsewhere in the codebase, so the same approach is used here
        // rather than introducing EF entities that would need a migration.
        try
        {
            var bills = await _db.Database.SqlQuery<BillDue>($@"
                SELECT ""Id"", ""Biller"", ""Category"", ""Amount"", ""Currency"", ""DueDate"", ""Recurring""
                FROM ""Bills""
                WHERE ""UserId""={UserId} AND ""DueDate"" IS NOT NULL AND ""DueDate"" <> ''
            ").ToListAsync();

            foreach (var b in bills)
            {
                if (!DateTime.TryParse(b.DueDate, out var due)) continue;
                var away = (due.Date - today).Days;
                if (away > days) continue;
                if (away < 0 && !includePast) continue;

                var amount = b.Amount is > 0 ? $"{b.Currency}{b.Amount:0.00}" : null;
                var detail = string.Join(" · ", new[] { amount, b.Recurring, b.Category }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));

                items.Add(new UpcomingItem("bill", b.Id, b.Biller ?? "Bill",
                    string.IsNullOrWhiteSpace(detail) ? null : detail,
                    due.ToString("yyyy-MM-dd"), away, Urgency(away), "/bills"));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Upcoming: bills unavailable"); }

        try
        {
            var warranties = await _db.Database.SqlQuery<WarrantyDue>($@"
                SELECT ""Id"", ""Product"", ""Brand"", ""Retailer"", ""ExpiryDate""
                FROM ""Warranties""
                WHERE ""UserId""={UserId} AND ""ExpiryDate"" IS NOT NULL AND ""ExpiryDate"" <> ''
            ").ToListAsync();

            foreach (var w in warranties)
            {
                if (!DateTime.TryParse(w.ExpiryDate, out var exp)) continue;
                var away = (exp.Date - today).Days;
                if (away > days) continue;
                if (away < 0 && !includePast) continue;

                var detail = string.Join(" · ", new[] { w.Brand, w.Retailer }
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Append("warranty ends"));

                items.Add(new UpcomingItem("warranty", w.Id, w.Product ?? "Warranty",
                    detail, exp.ToString("yyyy-MM-dd"), away, Urgency(away), "/warranty"));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Upcoming: warranties unavailable"); }

        try
        {
            var meds = await _db.Database.SqlQuery<MedicineDue>($@"
                SELECT ""Id"", ""Name"", ""Strength"", ""Purpose"", ""ExpiryDate""
                FROM ""Medicines""
                WHERE ""UserId""={UserId} AND ""ExpiryDate"" IS NOT NULL AND ""ExpiryDate"" <> ''
            ").ToListAsync();

            foreach (var m in meds)
            {
                if (!DateTime.TryParse(m.ExpiryDate, out var exp)) continue;
                var away = (exp.Date - today).Days;
                if (away > days) continue;
                if (away < 0 && !includePast) continue;

                var detail = string.Join(" · ", new[] { m.Strength, m.Purpose }
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Append("expires"));

                items.Add(new UpcomingItem("medicine", m.Id, m.Name ?? "Medicine",
                    detail, exp.ToString("yyyy-MM-dd"), away, Urgency(away), "/medicine"));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Upcoming: medicines unavailable"); }

        // Overdue first, then soonest. Sorting purely by date would bury a
        // missed bill under things that have not happened yet.
        var ordered = items.OrderBy(i => i.DaysAway).ToList();

        return Ok(new
        {
            items = ordered,
            counts = new
            {
                total = ordered.Count,
                overdue = ordered.Count(i => i.Urgency == "overdue"),
                soon = ordered.Count(i => i.Urgency == "soon"),
                bills = ordered.Count(i => i.Source == "bill"),
                warranties = ordered.Count(i => i.Source == "warranty"),
                medicines = ordered.Count(i => i.Source == "medicine"),
            },
            windowDays = days,
        });
    }

    /// <summary>
    /// Seven days is the line between "act now" and "note it". Anything already
    /// past is its own category because the required action is different:
    /// chase, not plan.
    /// </summary>
    private static string Urgency(int daysAway) =>
        daysAway < 0 ? "overdue" : daysAway <= 7 ? "soon" : "later";

    // Raw-SQL projections. Kept private and minimal: only the columns actually
    // rendered, so a schema addition elsewhere cannot break this query.
    private sealed class BillDue
    {
        public int Id { get; set; }
        public string? Biller { get; set; }
        public string? Category { get; set; }
        public double? Amount { get; set; }
        public string? Currency { get; set; }
        public string? DueDate { get; set; }
        public string? Recurring { get; set; }
    }

    private sealed class WarrantyDue
    {
        public int Id { get; set; }
        public string? Product { get; set; }
        public string? Brand { get; set; }
        public string? Retailer { get; set; }
        public string? ExpiryDate { get; set; }
    }

    private sealed class MedicineDue
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Strength { get; set; }
        public string? Purpose { get; set; }
        public string? ExpiryDate { get; set; }
    }
}
