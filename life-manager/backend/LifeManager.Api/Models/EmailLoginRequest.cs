namespace LifeManager.Api.Models;

public class EmailLoginRequest
{
    public required string Email { get; set; }
    public required string Password { get; set; }
}

public class RegisterRequest
{
    public required string Email { get; set; }
    public required string Password { get; set; }
    public required string Name { get; set; }
    public string? ReferralCode { get; set; }
}
