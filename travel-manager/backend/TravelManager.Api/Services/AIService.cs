using TravelManager.Api.Models;

namespace TravelManager.Api.Services;

/// <summary>
/// Text-completion seam used by the copied travel/wardrobe code. In the parent
/// app this was a multi-provider manager (Azure, OpenAI, Bedrock, Ollama); the
/// standalone keeps the same interface backed by AzureVisionService's
/// Azure-first / OpenAI-fallback chat client, so every call site is unchanged.
/// </summary>
public interface IAIService
{
    bool IsAvailable { get; }
    Task<PromptTestResponse> TestPromptAsync(PromptTestRequest request);
}

public class AIService : IAIService
{
    private readonly AzureVisionService _chat;

    public AIService(AzureVisionService chat)
    {
        _chat = chat;
    }

    public bool IsAvailable => _chat.IsAvailable;

    public async Task<PromptTestResponse> TestPromptAsync(PromptTestRequest request)
    {
        var text = await _chat.CompleteTextAsync(request.PromptContent, request.UserInput, maxTokens: 3000);
        return new PromptTestResponse { Result = text };
    }
}
