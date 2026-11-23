using Azure.AI.OpenAI.Chat;
using OpenAI.Chat;

namespace ContentStudio.Api.Services;

/// <summary>
/// Builds ChatCompletionOptions that work on both classic Azure OpenAI
/// deployments (gpt-4x) and new-generation ones (gpt-5x, o-series). The new
/// models reject the legacy max_tokens property (they require
/// max_completion_tokens) and only support the default temperature, so the
/// options must be shaped per deployment. Use this everywhere an Azure-bound
/// ChatCompletionOptions is constructed.
/// </summary>
public static class AzureChatOptions
{
    public static bool IsNewGeneration(string? deployment) =>
        !string.IsNullOrEmpty(deployment) &&
        (deployment.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase)
         || deployment.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
         || deployment.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
         || deployment.StartsWith("o4", StringComparison.OrdinalIgnoreCase));

    public static ChatCompletionOptions For(string? deployment, int? maxOutputTokens = null, float? temperature = null)
    {
        var options = new ChatCompletionOptions();

        if (maxOutputTokens is int max)
        {
            options.MaxOutputTokenCount = max;
            if (IsNewGeneration(deployment))
            {
                // Azure SDK serializes MaxOutputTokenCount as legacy max_tokens unless
                // this opt-in flips it to max_completion_tokens (required by gpt-5x/o*).
#pragma warning disable AOAI001
                options.SetNewMaxCompletionTokensPropertyEnabled(true);
#pragma warning restore AOAI001
            }
        }

        // New-generation models only accept the default temperature.
        if (temperature is float t && !IsNewGeneration(deployment))
            options.Temperature = t;

        return options;
    }
}
