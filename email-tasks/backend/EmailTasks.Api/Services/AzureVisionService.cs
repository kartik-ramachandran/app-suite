using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace EmailTasks.Api.Services;

/// <summary>
/// Shared AI helper for the platform's vision + text features (Wardrobe, Kitchen, etc.).
/// Runs on Claude or Azure OpenAI (or the OpenAI API) as chosen by AiChatClients /
/// AI:PreferredProvider. Images are analyzed in-memory and never persisted here.
/// </summary>
public class AzureVisionService
{
    private readonly string? _azureEndpoint;
    private readonly string? _azureKey;
    private readonly string _azureDeployment;
    private readonly string? _openAiKey;
    private readonly ILogger<AzureVisionService> _logger;
    private readonly IConfiguration _configuration;

    public AzureVisionService(IConfiguration configuration, ILogger<AzureVisionService> logger)
    {
        _azureEndpoint = configuration["AzureOpenAI:Endpoint"];
        _azureKey = configuration["AzureOpenAI:ApiKey"];
        _azureDeployment = configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4o";
        _openAiKey = configuration["OpenAI:ApiKey"];
        _logger = logger;
        _configuration = configuration;
    }

    private bool AzureConfigured => !string.IsNullOrWhiteSpace(_azureEndpoint) && !string.IsNullOrWhiteSpace(_azureKey);

    public bool IsAvailable => AiChatClients.IsConfigured(_configuration);

    /// <summary>Claude, Azure OpenAI or OpenAI — whichever AiChatClients selects.</summary>
    private ChatClient CreateClient() => AiChatClients.Create(_configuration, _azureDeployment, openAiModel: "gpt-4o-mini");

    /// <summary>Run a vision prompt over a single image (data-URI or raw base64). Returns the model's text.</summary>
    public async Task<string> AnalyzeAsync(string base64Image, string systemPrompt, string userPrompt, int maxTokens = 600)
    {
        if (!IsAvailable) throw new InvalidOperationException("Vision analysis is not configured.");

        var comma = base64Image.IndexOf(',');
        var mime = "image/jpeg";
        if (base64Image.StartsWith("data:") && comma > 5)
        {
            var header = base64Image[5..comma];
            var semi = header.IndexOf(';');
            if (semi > 0) mime = header[..semi];
        }
        var data = comma >= 0 ? base64Image[(comma + 1)..] : base64Image;
        var bytes = Convert.FromBase64String(data);

        var client = CreateClient();
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(new[]
            {
                ChatMessageContentPart.CreateTextPart(userPrompt),
                ChatMessageContentPart.CreateImagePart(new BinaryData(bytes), mime)
            })
        };

        var result = await client.CompleteChatAsync(messages, AzureChatOptions.For((AiChatClients.Active(_configuration) == "Azure" ? _azureDeployment : null), maxOutputTokens: maxTokens));
        return result.Value.Content[0].Text ?? string.Empty;
    }

    /// <summary>Text-only completion on the same (Azure-first) client.</summary>
    public async Task<string> CompleteTextAsync(string systemPrompt, string userPrompt, int maxTokens = 1600)
    {
        if (!IsAvailable) throw new InvalidOperationException("AI completion is not configured.");
        var client = CreateClient();
        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };
        var result = await client.CompleteChatAsync(messages, AzureChatOptions.For((AiChatClients.Active(_configuration) == "Azure" ? _azureDeployment : null), maxOutputTokens: maxTokens));
        return result.Value.Content[0].Text ?? string.Empty;
    }
}
