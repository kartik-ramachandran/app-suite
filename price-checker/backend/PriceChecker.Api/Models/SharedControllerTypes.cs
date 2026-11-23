using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PriceChecker.Api.Data;
using PriceChecker.Api.Services;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace PriceChecker.Api.Controllers;

// Lifted from parent Controllers/MedicineController.cs
public class ImageOnlyRequest { public string? ImageData { get; set; } }
