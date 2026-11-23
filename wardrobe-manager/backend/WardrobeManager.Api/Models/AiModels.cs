namespace WardrobeManager.Api.Models;

/// <summary>
/// Same request/response shape as the parent app's prompt-test pipeline, so the
/// copied services (TravelIntelligenceService, WardrobeController) compile
/// unchanged. Here it is backed by a single Azure-first chat completion.
/// </summary>
public class PromptTestRequest
{
    public string PromptContent { get; set; } = string.Empty;
    public string UserInput { get; set; } = string.Empty;
}

public class PromptTestResponse
{
    public string Result { get; set; } = string.Empty;
}
