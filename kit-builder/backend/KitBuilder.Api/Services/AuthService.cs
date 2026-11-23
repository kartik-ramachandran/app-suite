using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using KitBuilder.Api.Data;
using KitBuilder.Api.Models;

namespace KitBuilder.Api.Services;

public interface IAuthService
{
    Task<LoginResponse> EmailLoginAsync(string email, string password);
    Task<LoginResponse> RegisterAsync(string email, string password, string name);
    Task<User?> GetUserByIdAsync(string userId);
    string GenerateJwtToken(User user);
    bool TryValidateForRefresh(string token, out string userId, out string error);
    string GetCsrfSecret();
}

/// <summary>
/// Email/password auth + JWT issuance. Carried over from the parent app with the
/// same validation rules (the `required` keyword on DTOs guarantees nothing at
/// runtime, so every field is checked explicitly here) and the same
/// expired-token refresh grace window. OAuth, referrals, and password-reset
/// email flows were deliberately left behind.
/// </summary>
public class AuthService : IAuthService
{
    private readonly PromptDbContext _context;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthService> _logger;

    public AuthService(PromptDbContext context, IConfiguration configuration, ILogger<AuthService> logger)
    {
        _context = context;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<LoginResponse> EmailLoginAsync(string email, string password)
    {
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Email == email);

        if (user == null || user.PasswordHash == null)
            throw new UnauthorizedAccessException("Invalid email or password. Please check your credentials and try again.");

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            throw new UnauthorizedAccessException("Invalid email or password. Please check your credentials and try again.");

        user.LastLoginAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return new LoginResponse { Token = GenerateJwtToken(user), User = ToDto(user) };
    }

    public async Task<LoginResponse> RegisterAsync(string email, string password, string name)
    {
        // `required` on the DTO is a compile-time guarantee for C# callers only —
        // it does nothing to a JSON body, so validate everything explicitly.
        email = (email ?? "").Trim();
        name = (name ?? "").Trim();

        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("Email is required.");

        try
        {
            var parsed = new System.Net.Mail.MailAddress(email);
            if (parsed.Address != email) throw new FormatException();
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("That email address doesn't look valid.");
        }

        if (email.Length > 254)
            throw new InvalidOperationException("That email address is too long.");

        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Name is required.");

        if (name.Length > 100)
            throw new InvalidOperationException("Name must be 100 characters or fewer.");

        if (string.IsNullOrEmpty(password) || password.Length < 8)
            throw new InvalidOperationException("Password must be at least 8 characters.");

        if (password.Length > 200)
            throw new InvalidOperationException("Password must be 200 characters or fewer.");

        var normalised = email.ToLowerInvariant();
        var existingUser = await _context.Users.FirstOrDefaultAsync(u => u.Email.ToLower() == normalised);
        if (existingUser != null)
            throw new InvalidOperationException("User with this email already exists");

        var user = new User
        {
            Id = Guid.NewGuid().ToString(),
            Email = email,
            Name = name,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            CreatedAt = DateTime.UtcNow,
            LastLoginAt = DateTime.UtcNow,
            IsActive = true,
        };

        _context.Users.Add(user);
        await _context.SaveChangesAsync();
        _logger.LogInformation("New user registered: {Email}", email);

        return new LoginResponse { Token = GenerateJwtToken(user), User = ToDto(user) };
    }

    public async Task<User?> GetUserByIdAsync(string userId)
        => await _context.Users.FindAsync(userId);

    public string GenerateJwtToken(User user)
    {
        var jwtKey = _configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT key not configured");
        var jwtIssuer = _configuration["Jwt:Issuer"] ?? "KitBuilder";
        var jwtAudience = _configuration["Jwt:Audience"] ?? "KitBuilder";

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Name, user.Name),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var expiryDays = _configuration.GetValue<int?>("Jwt:ExpiryDays") ?? 30;
        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddDays(expiryDays),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Validate a token for the purpose of REFRESHING it. Accepts an
    /// already-expired token — as long as signature, issuer, and audience are
    /// valid and it expired no longer ago than the grace window
    /// (Jwt:RefreshGraceDays, default 14) — so a returning user is silently
    /// re-authenticated instead of bounced to the sign-in page.
    /// </summary>
    public bool TryValidateForRefresh(string token, out string userId, out string error)
    {
        userId = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "No session token provided.";
            return false;
        }

        var jwtKey = _configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT key not configured");
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidIssuer = _configuration["Jwt:Issuer"] ?? "KitBuilder",
            ValidAudience = _configuration["Jwt:Audience"] ?? "KitBuilder",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateLifetime = false,   // the whole point: allow an expired token
        };

        ClaimsPrincipal principal;
        SecurityToken validated;
        try
        {
            principal = new JwtSecurityTokenHandler().ValidateToken(token, validationParameters, out validated);
        }
        catch
        {
            error = "Invalid session token.";
            return false;
        }

        if (validated is not JwtSecurityToken jwt)
        {
            error = "Malformed session token.";
            return false;
        }

        var graceDays = _configuration.GetValue<int?>("Jwt:RefreshGraceDays") ?? 14;
        if (jwt.ValidTo < DateTime.UtcNow.AddDays(-graceDays))
        {
            error = "Session expired. Please sign in again.";
            return false;
        }

        userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                 ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                 ?? string.Empty;
        if (string.IsNullOrEmpty(userId))
        {
            error = "Session token is missing its user id.";
            return false;
        }

        return true;
    }

    public string GetCsrfSecret()
        => _configuration["Jwt:Key"] + "-csrf";

    private static UserDto ToDto(User user) => new()
    {
        Id = user.Id,
        Email = user.Email,
        Name = user.Name,
        ProfilePicture = user.ProfilePicture,
        Language = user.Language
    };
}
