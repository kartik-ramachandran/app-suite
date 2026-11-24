using System.ClientModel;
using Microsoft.Extensions.Configuration;
using Azure;
using Azure.AI.OpenAI;
using OpenAI;
using OpenAI.Chat;

namespace KitchenManager.Api.Services;

/// <summary>
/// The one place this app decides which AI provider answers a chat call.
///
/// Providers (each enabled by filling in its settings):
///   Claude  — Anthropic:ApiKey                          (Anthropic:Model, default claude-sonnet-5-5)
///   Azure   — AzureOpenAI:Endpoint + AzureOpenAI:ApiKey  (AzureOpenAI:DeploymentName)
///   OpenAI  — OpenAI:ApiKey                             (OpenAI:DefaultModel, default gpt-4o)
///   Ollama  — Ollama:Enabled = true                     (Ollama:BaseUrl, default http://localhost:11434;
///                                                        Ollama:Model, default llama3.2)
///             A model running on this computer: no API key, no cost per call.
///
/// Choice: AI:PreferredProvider ("Claude", "Azure", "OpenAI" or "Ollama") when that
/// provider is configured; otherwise the first configured of Claude, Azure, OpenAI, Ollama.
/// </summary>
public static class AiChatClients
{
    public static readonly string[] All = { "Claude", "Azure", "OpenAI", "Ollama" };

    public const string DefaultOllamaUrl = "http://localhost:11434";
    public const string DefaultOllamaModel = "llama3.2";

    public static bool IsProviderConfigured(IConfiguration c, string provider) => provider switch
    {
        "Claude" => !string.IsNullOrWhiteSpace(c["Anthropic:ApiKey"]),
        "Azure" => !string.IsNullOrWhiteSpace(c["AzureOpenAI:Endpoint"]) && !string.IsNullOrWhiteSpace(c["AzureOpenAI:ApiKey"]),
        "OpenAI" => !string.IsNullOrWhiteSpace(c["OpenAI:ApiKey"]),
        "Ollama" => IsOllamaEnabled(c),
        _ => false,
    };

    /// <summary>Ollama is opt-in: nothing is sent to a local model unless Ollama:Enabled is true.</summary>
    public static bool IsOllamaEnabled(IConfiguration c) =>
        bool.TryParse(c["Ollama:Enabled"], out var on) && on;

    public static string OllamaModel(IConfiguration c) =>
        string.IsNullOrWhiteSpace(c["Ollama:Model"]) ? DefaultOllamaModel : c["Ollama:Model"]!.Trim();

    /// <summary>
    /// Ollama speaks the OpenAI chat API at {BaseUrl}/v1, so the same OpenAI client
    /// works against it. Ollama ignores the API key; the SDK just needs one set.
    /// </summary>
    public static OpenAIClient OllamaClient(IConfiguration c)
    {
        var baseUrl = (string.IsNullOrWhiteSpace(c["Ollama:BaseUrl"]) ? DefaultOllamaUrl : c["Ollama:BaseUrl"]!.Trim()).TrimEnd('/');
        if (!baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) baseUrl += "/v1";
        return new OpenAIClient(new ApiKeyCredential("ollama"), new OpenAIClientOptions
        {
            Endpoint = new Uri(baseUrl),
            // Local models on ordinary hardware can take minutes on a long answer.
            NetworkTimeout = TimeSpan.FromSeconds(int.TryParse(c["Ollama:TimeoutSeconds"], out var s) && s > 0 ? s : 600),
        });
    }

    /// <summary>Configured providers, preferred first.</summary>
    public static List<string> Order(IConfiguration c)
    {
        var configured = All.Where(p => IsProviderConfigured(c, p)).ToList();
        var preferred = All.FirstOrDefault(p => string.Equals(p, c["AI:PreferredProvider"]?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (preferred != null && configured.Remove(preferred)) configured.Insert(0, preferred);
        return configured;
    }

    public static bool IsConfigured(IConfiguration c) => Order(c).Count > 0;

    /// <summary>The provider that will answer, or null when none is configured.</summary>
    public static string? Active(IConfiguration c) => Order(c).FirstOrDefault();

    /// <param name="model">
    /// The model/deployment the call site asked for. Azure treats it as a deployment
    /// name; Claude uses it only if it is a Claude model id, otherwise Anthropic:Model.
    /// Ollama always uses Ollama:Model — cloud model names mean nothing to it.
    /// </param>
    /// <param name="openAiModel">OpenAI model to use when OpenAI:DefaultModel is not set.</param>
    public static ChatClient Create(IConfiguration c, string? model = null, string? openAiModel = null)
    {
        switch (Active(c))
        {
            case "Claude":
                return new ClaudeChatClient(c, model);
            case "Azure":
                var deployment = !string.IsNullOrWhiteSpace(model) && !model.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
                    ? model
                    : c["AzureOpenAI:DeploymentName"] ?? "gpt-4o";
                return new AzureOpenAIClient(new Uri(c["AzureOpenAI:Endpoint"]!), new AzureKeyCredential(c["AzureOpenAI:ApiKey"]!))
                    .GetChatClient(deployment);
            case "OpenAI":
                // An explicit OpenAI model id from the call site wins; Claude ids and Azure
                // deployment names don't mean anything to OpenAI, so they fall back.
                var openAi = !string.IsNullOrWhiteSpace(model) && (model.StartsWith("gpt", StringComparison.OrdinalIgnoreCase)
                        || model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
                        || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase))
                    ? model
                    : c["OpenAI:DefaultModel"] ?? openAiModel ?? "gpt-4o";
                return new ChatClient(model: openAi, apiKey: c["OpenAI:ApiKey"]!);
            case "Ollama":
                return OllamaClient(c).GetChatClient(OllamaModel(c));
            default:
                throw new InvalidOperationException(
                    "No AI provider is configured. Set Anthropic:ApiKey (Claude), AzureOpenAI:Endpoint + AzureOpenAI:ApiKey (Azure), OpenAI:ApiKey (OpenAI), or Ollama:Enabled=true (a local model).");
        }
    }
}
