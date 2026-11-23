using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MoneyManager.Api.Data;
using MoneyManager.Api.Services;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace MoneyManager.Api.Controllers;

// Lifted from parent Controllers/MedicineController.cs
public class ImageOnlyRequest { public string? ImageData { get; set; } }
