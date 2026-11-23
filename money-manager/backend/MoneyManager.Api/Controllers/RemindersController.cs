using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MoneyManager.Api.Data;
using MoneyManager.Api.Services;

namespace MoneyManager.Api.Controllers;

/// <summary>
/// Read-side of the unified reminders engine: everything with an upcoming date
/// across the lifestyle modules (bills, warranties, medicines, plant watering),
/// aggregated for the current user. The push side (bell notifications) is
/// handled by <see cref="ReminderSweepService"/>.
/// </summary>
[ApiController]
[Route("api/reminders")]
[Authorize]
public class RemindersController : ControllerBase
{
    private readonly PromptDbContext _db;

    public RemindersController(PromptDbContext db) => _db = db;

    private string UserId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;

    /// <summary>Upcoming (and just-overdue) dated items across all lifestyle modules.</summary>
    [HttpGet("upcoming")]
    public async Task<IActionResult> Upcoming()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var userId = UserId;

        var items = new List<ReminderSweepService.ReminderItem>();
        await LifestyleTables.EnsureRemindableAsync(_db);

        items.AddRange(await ReminderSweepService.LoadBillsAsync(_db, today, userId));
        items.AddRange(await ReminderSweepService.LoadWarrantiesAsync(_db, today, userId));
        items.AddRange(await ReminderSweepService.LoadMedicinesAsync(_db, today, userId));
        items.AddRange(await ReminderSweepService.LoadPlantsAsync(_db, today, userId));

        var result = items
            .OrderBy(i => i.Date)
            .Select(i => new
            {
                kind = i.Kind,
                title = i.Title,
                detail = i.Detail,
                date = i.Date.ToString("yyyy-MM-dd"),
                daysLeft = i.Date.DayNumber - today.DayNumber,
            })
            .ToList();

        return Ok(new { items = result });
    }
}
