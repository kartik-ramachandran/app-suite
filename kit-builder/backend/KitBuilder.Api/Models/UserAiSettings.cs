namespace KitBuilder.Api.Models;

public class UserAiSettings
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Provider { get; set; } = "platform"; // platform | openai | azureopenai | bedrock
    public string? ModelId { get; set; }
    public string? OpenAiKey { get; set; }
    public string? AzureOpenAiEndpoint { get; set; }
    public string? AzureOpenAiKey { get; set; }
    public string? AzureOpenAiDeployment { get; set; }
    public string? BedrockAccessKey { get; set; }
    public string? BedrockSecretKey { get; set; }
    public string? BedrockRegion { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public User? User { get; set; }
}
