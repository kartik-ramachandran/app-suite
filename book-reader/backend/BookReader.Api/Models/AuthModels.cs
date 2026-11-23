namespace BookReader.Api.Models;

public class GoogleLoginRequest
{
    public string Credential { get; set; } = string.Empty;
}

public class LoginResponse
{
    public string Token { get; set; } = string.Empty;
    public UserDto User { get; set; } = new UserDto();
}

public class UserDto
{
    public string Id { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ProfilePicture { get; set; }
    public string Language { get; set; } = "en";
}

public class UpdateLanguageRequest
{
    public string Language { get; set; } = "en";
}
