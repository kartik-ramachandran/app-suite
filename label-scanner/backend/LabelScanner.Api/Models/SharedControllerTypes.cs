using LabelScanner.Api.Data;
using LabelScanner.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace LabelScanner.Api.Controllers;

// Lifted from parent Controllers/MedicineController.cs
public class ImageOnlyRequest { public string? ImageData { get; set; } }
