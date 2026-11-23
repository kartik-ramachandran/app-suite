using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;
using Whiteboard.Api.Data;
using Whiteboard.Api.Middleware;
using Whiteboard.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// A failing background service must not take the whole API down.
builder.Services.Configure<HostOptions>(o =>
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Uploads (photos, documents) up to 100 MB — same limit as the parent app.
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 104_857_600);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 104_857_600;
    o.ValueLengthLimit = 104_857_600;
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMemoryCache();

// SQLite for development, PostgreSQL for production — same switch as the parent app.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var usePostgreSql = builder.Configuration.GetValue<bool>("UsePostgreSQL");
if (usePostgreSql && !string.IsNullOrEmpty(connectionString))
{
    builder.Services.AddDbContext<PromptDbContext>(options => options.UseNpgsql(connectionString));
    Console.WriteLine("📊 Using PostgreSQL database");
}
else
{
    builder.Services.AddDbContext<PromptDbContext>(options =>
        options.UseSqlite(connectionString ?? "Data Source=whiteboard.db"));
    Console.WriteLine("📊 Using SQLite database");
}

builder.Services.AddHttpClient();
builder.Services.AddScoped<IAuthService, AuthService>();

// ── Services this app's code depends on (copied registrations from the parent) ──
builder.Services.AddScoped<AzureVisionService>();

// JWT key: production refuses to start without one (a baked-in fallback would let
// anyone mint tokens); development generates a random per-run key.
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (builder.Environment.IsProduction())
        throw new InvalidOperationException(
            "Jwt:Key is not configured. Set the Jwt__Key environment variable to a random secret "
            + "of at least 32 characters. See appsettings.example.json.");

    jwtKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
    builder.Configuration["Jwt:Key"] = jwtKey;
    Console.WriteLine("⚠️  Jwt:Key not set - generated a random development key. "
                    + "Tokens will not survive a restart. Set Jwt__Key to fix.");
}
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "Whiteboard";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "Whiteboard";

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
        // Browser EventSource (SSE) can't send an Authorization header.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"].ToString();
                if (!string.IsNullOrEmpty(accessToken) && ctx.HttpContext.Request.Path.StartsWithSegments("/api/notifications/stream"))
                    ctx.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Rate limiting: the same three named policies the parent's controllers use.
builder.Services.AddRateLimiter(options =>
{
    options.AddTokenBucketLimiter("media", opt =>
    {
        opt.TokenLimit = 100; opt.TokensPerPeriod = 100; opt.ReplenishmentPeriod = TimeSpan.FromHours(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst; opt.QueueLimit = 2;
    });
    options.AddTokenBucketLimiter("ai-generation", opt =>
    {
        opt.TokenLimit = 1000; opt.TokensPerPeriod = 1000; opt.ReplenishmentPeriod = TimeSpan.FromHours(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst; opt.QueueLimit = 10;
    });
    options.AddFixedWindowLimiter("general", opt =>
    {
        opt.PermitLimit = 100; opt.Window = TimeSpan.FromMinutes(1);
        opt.QueueProcessingOrder = QueueProcessingOrder.OldestFirst; opt.QueueLimit = 10;
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
            TokenLimit = 200, TokensPerPeriod = 200, ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 5
        });
    });
});

var allowedOrigins = builder.Configuration.GetSection("FrontendGuard:AllowedOrigins")
    .Get<string[]>() ?? new[] { "http://localhost:3000", "http://localhost:5286" };
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp", policy =>
        policy.SetIsOriginAllowed(origin => allowedOrigins.Contains(origin))
              .AllowAnyHeader().AllowAnyMethod().AllowCredentials());
});

var app = builder.Build();

// Local mode (no sign-in) is loopback-only; this guard enforces it. No-op when off.
app.UseLocalModeGuard();

// Schema: EF entities via EnsureCreated (fresh database, no migrations), then the
// raw-SQL tables this app references (none).
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<PromptDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SchemaBootstrap");
    await context.Database.EnsureCreatedAsync();
    await SchemaBootstrap.ApplyAsync(context, logger);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.UseCors("AllowReactApp");
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseMiddleware<FrontendGuardMiddleware>();
app.MapControllers();

if (!app.Environment.IsDevelopment())
    app.MapFallbackToFile("index.html");

app.Run();
