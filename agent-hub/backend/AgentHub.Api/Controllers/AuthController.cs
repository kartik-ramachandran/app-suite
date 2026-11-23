using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AgentHub.Api.Models;
using AgentHub.Api.Services;

namespace AgentHub.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(IAuthService authService, ILogger<AuthController> logger)
    {
        _authService = authService;
        _logger = logger;
    }

    [HttpGet("csrf-token")]
    public ActionResult GetCsrfToken()
    {
        var secret = _authService.GetCsrfSecret();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = $"{timestamp}";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
        var token = $"{timestamp}.{hash}";

        return Ok(new { token });
    }

    [HttpPost("login")]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] EmailLoginRequest request)
    {
        try
        {
            var response = await _authService.EmailLoginAsync(request.Email, request.Password);
            return Ok(response);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Email login failed");
            return StatusCode(500, new { error = "Authentication failed" });
        }
    }

    [HttpPost("register")]
    public async Task<ActionResult<LoginResponse>> Register([FromBody] RegisterRequest request)
    {
        try
        {
            var response = await _authService.RegisterAsync(request.Email, request.Password, request.Name);
            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Registration failed");
            return StatusCode(500, new { error = "Registration failed" });
        }
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> GetCurrentUser()
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userIdClaim == null) return Unauthorized();

        var user = await _authService.GetUserByIdAsync(userIdClaim);
        if (user == null) return NotFound();

        return Ok(new UserDto
        {
            Id = user.Id,
            Email = user.Email,
            Name = user.Name,
            ProfilePicture = user.ProfilePicture,
            Language = user.Language
        });
    }

    // AllowAnonymous (not [Authorize]) so an already-expired token can still be
    // refreshed: the token is validated manually, ignoring lifetime, and accepted
    // only within the grace window. This keeps returning users signed in instead
    // of bouncing them to /signin the moment their token lapses.
    [AllowAnonymous]
    [HttpPost("refresh-token")]
    public async Task<ActionResult<LoginResponse>> RefreshToken()
    {
        var authHeader = Request.Headers.Authorization.FirstOrDefault();
        var token = authHeader != null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader.Substring("Bearer ".Length).Trim()
            : null;

        if (string.IsNullOrEmpty(token))
            return Unauthorized(new { message = "No session token provided." });

        if (!_authService.TryValidateForRefresh(token, out var userIdClaim, out var validationError))
            return Unauthorized(new { message = validationError });

        var user = await _authService.GetUserByIdAsync(userIdClaim);
        if (user == null) return NotFound();

        var newToken = _authService.GenerateJwtToken(user);

        return Ok(new LoginResponse
        {
            Token = newToken,
            User = new UserDto
            {
                Id = user.Id,
                Email = user.Email,
                Name = user.Name,
                ProfilePicture = user.ProfilePicture,
                Language = user.Language
            }
        });
    }
}
