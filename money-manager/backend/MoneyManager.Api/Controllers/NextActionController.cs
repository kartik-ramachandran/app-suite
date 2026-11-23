using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MoneyManager.Api.Data;
using MoneyManager.Api.Services;

namespace MoneyManager.Api.Controllers;

/// <summary>
/// "What should I do about this?" for anything the scanners captured.
///
/// WHY ONE CONTROLLER INSTEAD OF SIX
/// Warranty, medicine, bill, plant, label and card each end at extraction —
/// fields read off a photo. The useful half is the action: your warranty ends
/// in three weeks, here is how to claim. Putting that in each controller would
/// scatter the safety rules across six files and guarantee they drift apart.
/// One endpoint means the disclaimer and the output screen cannot be forgotten
/// in one place while being present in another.
///
/// The item is always re-read from the database by id, never taken from the
/// request body, so a caller cannot describe an imaginary medicine and harvest
/// advice about it.
///
/// Login-only, and rate-limited: this costs an AI call per request.
/// </summary>
[ApiController]
[Route("api/next-action")]
[Authorize]
public class NextActionController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly NextActionService _next;
    private readonly ILogger<NextActionController> _logger;

    public NextActionController(PromptDbContext db, NextActionService next, ILogger<NextActionController> logger)
    {
        _db = db;
        _next = next;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    /// <summary>
    /// Suggest next steps for one scanned item.
    /// </summary>
    /// <param name="kind">warranty | medicine | bill | plant | label | receipt</param>
    /// <param name="id">Row id, always scoped to the signed-in user.</param>
    [HttpGet("{kind}/{id:int}")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> Suggest(string kind, int id)
    {
        kind = (kind ?? "").Trim().ToLowerInvariant();

        string description;
        string? context = null;
        NextActionService.Domain domain;

        switch (kind)
        {
            case "warranty":
            {
                await LifestyleTables.EnsureWarrantiesAsync(_db, _logger);
                var w = (await _db.Database.SqlQuery<WarrantyItem>($@"
                    SELECT ""Product"", ""Brand"", ""Retailer"", ""PurchaseDate"", ""ExpiryDate""
                    FROM ""Warranties"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
                if (w == null) return NotFound(new { message = "Not found." });

                domain = NextActionService.Domain.Consumer;
                description = $"{w.Product} ({w.Brand}) bought from {w.Retailer}";
                context = DaysLeft(w.ExpiryDate) is int d
                    ? $"Warranty expires {w.ExpiryDate} — {(d < 0 ? $"{-d} days ago" : $"in {d} days")}. Purchased {w.PurchaseDate}."
                    : $"Purchased {w.PurchaseDate}.";
                break;
            }

            case "medicine":
            {
                await LifestyleTables.EnsureMedicinesAsync(_db, _logger);
                var m = (await _db.Database.SqlQuery<MedicineItem>($@"
                    SELECT ""Name"", ""Form"", ""Strength"", ""Purpose"", ""ExpiryDate""
                    FROM ""Medicines"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
                if (m == null) return NotFound(new { message = "Not found." });

                domain = NextActionService.Domain.Health;
                // Purpose is deliberately included so the model can say "ask your
                // pharmacist about X" — but the guardrail forbids dosing advice.
                description = $"{m.Name} {m.Strength} ({m.Form}), kept for: {m.Purpose}";
                context = DaysLeft(m.ExpiryDate) is int d
                    ? $"Expires {m.ExpiryDate} — {(d < 0 ? $"{-d} days ago" : $"in {d} days")}."
                    : null;
                break;
            }

            case "bill":
            {
                await LifestyleTables.EnsureBillsAsync(_db, _logger);
                var b = (await _db.Database.SqlQuery<BillItem>($@"
                    SELECT ""Biller"", ""Category"", ""Amount"", ""Currency"", ""DueDate"", ""Recurring""
                    FROM ""Bills"" WHERE ""Id""={id} AND ""UserId""={UserId}").ToListAsync()).FirstOrDefault();
                if (b == null) return NotFound(new { message = "Not found." });

                domain = NextActionService.Domain.Consumer;
                description = $"{b.Biller} bill, {b.Category}, {b.Currency}{b.Amount:0.00}, {b.Recurring}";
                context = DaysLeft(b.DueDate) is int d
                    ? $"Due {b.DueDate} — {(d < 0 ? $"{-d} days overdue" : $"in {d} days")}."
                    : null;
                break;
            }

            default:
                return BadRequest(new { message = "Unknown item type." });
        }

        var suggestion = await _next.SuggestAsync(domain, description, context);

        return Ok(new
        {
            actions = suggestion.Actions,
            // Always present, appended in code — never left to the model to
            // remember. This is the difference between a request and a guarantee.
            disclaimer = suggestion.Disclaimer,
            withheld = suggestion.Withheld,
            withheldReason = suggestion.WithheldReason,
        });
    }

    /// <summary>Days until a yyyy-MM-dd date, or null when unparseable.</summary>
    private static int? DaysLeft(string? date) =>
        DateTime.TryParse(date, out var d) ? (int)(d.Date - DateTime.UtcNow.Date).TotalDays : null;

    private sealed class WarrantyItem
    {
        public string? Product { get; set; }
        public string? Brand { get; set; }
        public string? Retailer { get; set; }
        public string? PurchaseDate { get; set; }
        public string? ExpiryDate { get; set; }
    }

    private sealed class MedicineItem
    {
        public string? Name { get; set; }
        public string? Form { get; set; }
        public string? Strength { get; set; }
        public string? Purpose { get; set; }
        public string? ExpiryDate { get; set; }
    }

    private sealed class BillItem
    {
        public string? Biller { get; set; }
        public string? Category { get; set; }
        public double? Amount { get; set; }
        public string? Currency { get; set; }
        public string? DueDate { get; set; }
        public string? Recurring { get; set; }
    }
}
