using MeetingManager.Api.Models;
using System.Runtime.CompilerServices;

namespace MeetingManager.Api.Services;

public class AIServiceManager : IAIService
{
    private readonly List<IAIService> _providers;
    private readonly ILogger<AIServiceManager> _logger;
    private readonly IConfiguration _configuration;

    public bool IsAvailable => _providers.Any(p => p.IsAvailable);
    public string ProviderName => "AI Service Manager";
    public List<string> SupportedModels => _providers.SelectMany(p => p.SupportedModels).Distinct().ToList();

    public AIServiceManager(
        IEnumerable<IAIService> providers,
        ILogger<AIServiceManager> logger,
        IConfiguration configuration)
    {
        _providers = providers.ToList();
        _logger = logger;
        _configuration = configuration;

        // Get preferred provider from configuration
        var preferredProvider = configuration["AI:PreferredProvider"];
        // "Azure" is accepted as a short name for the AzureOpenAI provider.
        if (string.Equals(preferredProvider, "Azure", StringComparison.OrdinalIgnoreCase)) preferredProvider = "AzureOpenAI";
        
        // Order providers by priority based on preferred provider configuration
        _providers = _providers.OrderBy(p => GetProviderPriority(p, preferredProvider)).ToList();
        
        var providerList = string.Join(", ", _providers.Select(p => p.ProviderName));
        _logger.LogInformation($"AI Service Manager initialized with providers: {providerList}");
        if (!string.IsNullOrEmpty(preferredProvider))
        {
            _logger.LogInformation($"Preferred provider: {preferredProvider}");
        }
    }

    private int GetProviderPriority(IAIService provider, string? preferredProvider)
    {
        // If preferred provider is set, give it highest priority
        if (!string.IsNullOrEmpty(preferredProvider) && 
            provider.ProviderName.Equals(preferredProvider, StringComparison.OrdinalIgnoreCase))
        {
            return -1; // Highest priority
        }

        return provider.ProviderName switch
        {
            "Claude"        => 0,
            "AzureOpenAI"   => 1,
            "OpenAI"        => 2,
            "AmazonBedrock" => 3,
            "Ollama"        => 4,
            _ => 99
        };
    }

    public async IAsyncEnumerable<string> StreamExecuteAsync(
        string systemPrompt,
        List<ConversationMessage> messages,
        string? modelOverride,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Pick provider: prefer one that explicitly lists this model, else use priority order
        var provider = modelOverride != null
            ? _providers.FirstOrDefault(p => p.IsAvailable && p.SupportedModels.Contains(modelOverride))
              ?? _providers.FirstOrDefault(p => p.IsAvailable)
            : _providers.FirstOrDefault(p => p.IsAvailable);

        if (provider == null)
            throw new InvalidOperationException("No AI providers are available.");

        _logger.LogInformation("Streaming execution with {Provider} using model {Model}", provider.ProviderName, modelOverride ?? "default");

        await foreach (var token in provider.StreamExecuteAsync(systemPrompt, messages, modelOverride, ct))
            yield return token;
    }

    public async Task<PromptTestResponse> TestPromptAsync(PromptTestRequest request)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable)
            {
                _logger.LogDebug($"{provider.ProviderName} is not available, trying next provider");
                continue;
            }

            try
            {
                _logger.LogInformation($"Testing prompt with {provider.ProviderName}");
                var result = await provider.TestPromptAsync(request);

                _logger.LogInformation($"Successfully tested prompt with {provider.ProviderName}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error with {provider.ProviderName}: {ex.Message}");
            }
        }

        throw new Exception("All AI providers are unavailable or failed. Please check your configuration and ensure at least one AI service is properly set up.");
    }

    public async Task<GoalSuggestionsResult> GenerateGoalSuggestionsAsync(string intent, string mode = "intent")
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;

            try
            {
                _logger.LogDebug($"Generating goal suggestions with {provider.ProviderName}");
                return await provider.GenerateGoalSuggestionsAsync(intent, mode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error generating goals with {provider.ProviderName}: {ex.Message}");
            }
        }

        // Fallback suggestions if all providers fail
        return new GoalSuggestionsResult(
            new List<string>
            {
                "Create clear and specific instructions",
                "Define the expected output format",
                "Provide relevant context and examples",
                "Specify any constraints or limitations",
                "Include success criteria"
            },
            "professional"
        );
    }

    public async Task<string> GeneratePromptFromGoalsAsync(string intent, List<string> selectedGoals, string? additionalContext)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;

            try
            {
                _logger.LogDebug($"Generating prompt from goals with {provider.ProviderName}");
                return await provider.GeneratePromptFromGoalsAsync(intent, selectedGoals, additionalContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error generating prompt with {provider.ProviderName}: {ex.Message}");
            }
        }

        // Fallback prompt generation
        var goalsText = string.Join(", ", selectedGoals);
        return $"Create a prompt for: {intent}\n\nGoals: {goalsText}\n\nPlease provide clear, actionable instructions that achieve these objectives.";
    }

    public async Task<List<string>> GenerateRefinementSuggestionsAsync(string currentPrompt)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;

            try
            {
                _logger.LogDebug($"Generating refinement suggestions with {provider.ProviderName}");
                return await provider.GenerateRefinementSuggestionsAsync(currentPrompt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error generating refinements with {provider.ProviderName}: {ex.Message}");
            }
        }

        // Fallback suggestions
        return new List<string>
        {
            "Add more specific examples to illustrate requirements",
            "Include clear success criteria and evaluation metrics",
            "Specify the desired output format and structure",
            "Add context about the target audience or use case",
            "Include constraints, limitations, or edge cases to consider"
        };
    }

    public async Task<string> RefinePromptAsync(string currentPrompt, List<string> selectedRefinements, string? additionalContext)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;

            try
            {
                _logger.LogDebug($"Refining prompt with {provider.ProviderName}");
                return await provider.RefinePromptAsync(currentPrompt, selectedRefinements, additionalContext);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error refining prompt with {provider.ProviderName}: {ex.Message}");
            }
        }

        // Return original prompt if all providers fail
        return currentPrompt;
    }

    // Additional methods for provider management
    // Advanced features - delegate to available providers
    public async Task<List<string>> GenerateGoalSuggestionsWithPersonaAsync(string intent, string style, string category, string? personaPrompt)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;

            try
            {
                _logger.LogDebug($"Generating goal suggestions with persona using {provider.ProviderName}");
                return await provider.GenerateGoalSuggestionsWithPersonaAsync(intent, style, category, personaPrompt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error generating goals with persona using {provider.ProviderName}: {ex.Message}");
            }
        }

        // Fallback to basic goal generation
        return (await GenerateGoalSuggestionsAsync(intent)).Goals;
    }

    public async Task<string> GeneratePromptWithPersonaAsync(string intent, List<string> selectedGoals, string? additionalContext, string? personaPrompt)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;

            try
            {
                _logger.LogDebug($"Generating prompt with persona using {provider.ProviderName}");
                return await provider.GeneratePromptWithPersonaAsync(intent, selectedGoals, additionalContext, personaPrompt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error generating prompt with persona using {provider.ProviderName}: {ex.Message}");
            }
        }

        // Fallback to basic prompt generation
        return await GeneratePromptFromGoalsAsync(intent, selectedGoals, additionalContext);
    }

    public async Task<string> DescribeScreenAsync(string base64Image)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;
            try
            {
                var result = await provider.DescribeScreenAsync(base64Image);
                if (!string.IsNullOrWhiteSpace(result)) return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DescribeScreenAsync failed on {Provider}", provider.ProviderName);
            }
        }
        return string.Empty;
    }

    public async Task<string> TranscribeAudioAsync(Stream audioStream, string fileName)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;

            try
            {
                _logger.LogDebug($"Transcribing audio with {provider.ProviderName}");
                return await provider.TranscribeAudioAsync(audioStream, fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error transcribing audio with {provider.ProviderName}: {ex.Message}");
            }
        }

        throw new Exception("Audio transcription is not available. No supported providers are configured for audio features.");
    }

    public async Task<string> RefineTranscriptionToIntentAsync(string transcription)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;

            try
            {
                _logger.LogDebug($"Refining transcription with {provider.ProviderName}");
                return await provider.RefineTranscriptionToIntentAsync(transcription);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error refining transcription with {provider.ProviderName}: {ex.Message}");
            }
        }

        throw new Exception("Transcription refinement is not available. No supported providers are configured for audio features.");
    }

    public async Task<string> QuickRefineAsync(string currentPrompt, string refinementInstruction)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;

            try
            {
                _logger.LogDebug($"Quick refining prompt with {provider.ProviderName}");
                return await provider.QuickRefineAsync(currentPrompt, refinementInstruction);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error quick refining with {provider.ProviderName}: {ex.Message}");
            }
        }

        return currentPrompt; // Return original if all providers fail
    }

    public async Task<string> GeneratePersonaAsync(string description)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;

            try
            {
                _logger.LogDebug($"Generating persona with {provider.ProviderName}");
                return await provider.GeneratePersonaAsync(description);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error generating persona with {provider.ProviderName}: {ex.Message}");
            }
        }

        // Fallback persona
        return $"AI Assistant\n\nDescription: {description}\n\nMaintain a helpful and professional demeanor in all interactions.";
    }

    public async Task<ContentModerationResult> ModerateContentAsync(string content)
    {
        foreach (var provider in _providers)
        {
            if (!provider.IsAvailable) continue;

            try
            {
                return await provider.ModerateContentAsync(content);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Moderation failed with {provider.ProviderName}");
            }
        }

        return new ContentModerationResult { IsFlagged = false, FlaggedCategories = new List<string>() };
    }

    public async Task<AgentLoopResult> RunWithToolsAsync(
        string systemPrompt,
        string userInput,
        List<AgentToolDefinition> tools,
        Func<AgentToolCall, Task<string>> toolExecutor,
        Action<AgentRunStep>? onStep = null,
        int maxIterations = 5,
        string? modelOverride = null,
        CancellationToken ct = default)
    {
        // Prefer OpenAI for native function calling; Bedrock has Anthropic tool_use as fallback
        var provider = modelOverride != null
            ? _providers.FirstOrDefault(p => p.IsAvailable && p.SupportedModels.Contains(modelOverride))
              ?? _providers.FirstOrDefault(p => p.IsAvailable)
            : _providers.FirstOrDefault(p => p.IsAvailable);

        if (provider == null)
            throw new InvalidOperationException("No AI providers are available for agent execution.");

        // Only forward the model override if the chosen provider actually supports that model ID
        var effectiveModel = modelOverride != null && provider.SupportedModels.Contains(modelOverride)
            ? modelOverride : null;

        _logger.LogInformation("Running agent with tools via {Provider} (model: {Model})", provider.ProviderName, effectiveModel ?? "default");
        return await provider.RunWithToolsAsync(systemPrompt, userInput, tools, toolExecutor, onStep, maxIterations, effectiveModel, ct);
    }

    // Additional methods for provider management
    public List<ProviderStatus> GetProviderStatuses()
    {
        return _providers.Select(p => new ProviderStatus
        {
            Name = p.ProviderName,
            IsAvailable = p.IsAvailable,
            SupportedModels = p.SupportedModels
        }).ToList();
    }

    public async Task<ProviderStatus> TestProviderAsync(string providerName)
    {
        var provider = _providers.FirstOrDefault(p => p.ProviderName == providerName);
        if (provider == null)
        {
            return new ProviderStatus
            {
                Name = providerName,
                IsAvailable = false,
                Error = "Provider not found"
            };
        }

        try
        {
            // Simple test - try to generate a basic response
            var testRequest = new PromptTestRequest
            {
                PromptContent = "Say 'Hello'",
                UserInput = ""
            };
            var testResult = await provider.TestPromptAsync(testRequest);
            return new ProviderStatus
            {
                Name = provider.ProviderName,
                IsAvailable = true,
                SupportedModels = provider.SupportedModels,
                LastTestResult = "Success"
            };
        }
        catch (Exception ex)
        {
            return new ProviderStatus
            {
                Name = provider.ProviderName,
                IsAvailable = false,
                Error = ex.Message
            };
        }
    }
}

public class ProviderStatus
{
    public string Name { get; set; } = "";
    public bool IsAvailable { get; set; }
    public List<string> SupportedModels { get; set; } = new();
    public string? Error { get; set; }
    public string? LastTestResult { get; set; }
}