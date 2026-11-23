using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;
using WardrobeManager.Api.Data;
using WardrobeManager.Api.Middleware;
using WardrobeManager.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// SQLite for development, PostgreSQL for production — same switch as the parent app.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var usePostgreSql = builder.Configuration.GetValue<bool>("UsePostgreSQL");

if (usePostgreSql && !string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
    Console.WriteLine("📊 Using PostgreSQL database");
}
else
{
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseSqlite(connectionString ?? "Data Source=wardrobe.db"));
    Console.WriteLine("📊 Using SQLite database");
}

builder.Services.AddHttpClient();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<AzureVisionService>();
builder.Services.AddScoped<IAIService, AIService>();

// JWT key: production refuses to start without one (a baked-in fallback would let
// anyone mint tokens); development generates a random per-run key.
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (builder.Environment.IsProduction())
    {
        throw new InvalidOperationException(
            "Jwt:Key is not configured. Set the Jwt__Key environment variable to a random secret "
            + "of at least 32 characters. See appsettings.example.json.");
    }

    jwtKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
    builder.Configuration["Jwt:Key"] = jwtKey;
    Console.WriteLine("⚠️  Jwt:Key not set - generated a random development key. "
                    + "Tokens will not survive a restart. Set Jwt__Key to fix.");
}
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "WardrobeManager";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "WardrobeManager";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

// Rate limiting: the copied controllers tag their AI endpoints with
// [EnableRateLimiting("ai-generation")], so that policy must exist here.
builder.Services.AddRateLimiter(options =>
{
    options.AddTokenBucketLimiter("ai-generation", opt =>
    {
        opt.TokenLimit = 1000;
        opt.TokensPerPeriod = 1000;
        opt.ReplenishmentPeriod = TimeSpan.FromHours(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        opt.QueueLimit = 10;
    });

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "rate_limit_exceeded",
            message = "Too many requests. Please try again later.",
        }, cancellationToken: cancellationToken);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var userId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        var partitionKey = userId ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous";

        return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = 200,
            TokensPerPeriod = 200,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 5
        });
    });
});

var allowedOrigins = builder.Configuration.GetSection("FrontendGuard:AllowedOrigins")
    .Get<string[]>() ?? new[] { "http://localhost:3000", "http://localhost:5274" };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy =>
        {
            policy.SetIsOriginAllowed(origin => allowedOrigins.Contains(origin))
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        });
});

var app = builder.Build();

// Local mode (no sign-in) is loopback-only; this guard enforces it. No-op when off.
app.UseLocalModeGuard();

// Schema: Users comes from the EF model via EnsureCreated (no migrations in this
// project — the parent's migration chain never replayed from empty either). The
// Wardrobe tables are raw SQL with IF NOT EXISTS that self-ensure inside the
// controller on first request, same as the parent, so a wiped database heals itself.
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await context.Database.EnsureCreatedAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    // Serve the built frontend from wwwroot in production.
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseCors("AllowReactApp");

app.UseAuthentication();
app.UseAuthorization();

app.UseRateLimiter();
app.UseMiddleware<FrontendGuardMiddleware>();
app.MapControllers();

// Fallback for client-side routing in production.
if (!app.Environment.IsDevelopment())
{
    app.MapFallbackToFile("index.html");
}

app.Run();
