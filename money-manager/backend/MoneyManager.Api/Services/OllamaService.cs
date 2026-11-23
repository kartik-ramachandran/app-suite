using MoneyManager.Api.Models;
using System.Text.Json;
using System.Net.Http.Json;

namespace MoneyManager.Api.Services;

public class OllamaService : IAIService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaService> _logger;
    private readonly string _baseUrl;
    private readonly string _defaultModel;
    private List<string>? _availableModels;

    public bool IsAvailable => CheckAvailabilityAsync().GetAwaiter().GetResult();
    public string ProviderName => "Ollama";
    public List<string> SupportedModels => GetAvailableModelsAsync().GetAwaiter().GetResult();

    private const string SafetyClause =
        "\n\nIMPORTANT: Do NOT generate content that is violent, sexually explicit, hateful, " +
        "discriminatory, promotes illegal activities, or encourages self-harm. " +
        "Refuse any such request and ask for a safe alternative.";

    private static readonly Dictionary<string, string> StyleDescriptions = new()
    {
        { "professional", "Use a formal, business-appropriate tone. Be clear, concise, and authoritative. Avoid casual language and maintain a polished, corporate communication style." },
        { "creative", "Use imaginative, expressive language. Be playful with words, use metaphors and vivid descriptions. Encourage creative thinking and unique perspectives." },
        { "technical", "Use precise, detailed technical language. Include specific terminology, structured explanations, and focus on accuracy and completeness. Be methodical and thorough." },
        { "conversational", "Use a friendly, natural dialogue style. Be warm and approachable, like talking to a helpful friend. Use casual but respectful language." },
        { "persuasive", "Use compelling, action-oriented language. Focus on benefits, create urgency, and motivate action. Be confident and use strong calls-to-action." }
    };

    private static readonly Dictionary<string, string[]> CategorySuggestions = new()
    {
        { "marketing", new[] { "Create compelling headlines", "Write persuasive calls-to-action", "Generate engaging social media content", "Craft email subject lines with high open rates", "Develop brand voice consistency" } },
        { "support", new[] { "Handle customer complaints with empathy", "Provide accurate troubleshooting steps", "Escalate complex issues appropriately", "Maintain professional tone under pressure", "Follow up on unresolved issues" } },
        { "code", new[] { "Write clean, maintainable code", "Follow best practices and conventions", "Include helpful comments", "Handle edge cases gracefully", "Optimize for performance" } },
        { "writing", new[] { "Maintain consistent tone", "Use clear and concise language", "Structure content logically", "Engage the reader", "Include relevant examples" } },
        { "sales", new[] { "Identify customer pain points", "Present solutions effectively", "Handle objections professionally", "Create urgency", "Build rapport and trust" } }
    };

    public OllamaService(IConfiguration configuration, ILogger<OllamaService> logger)
    {
        _baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _defaultModel = configuration["Ollama:DefaultModel"] ?? "llama2";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_baseUrl),
            Timeout = TimeSpan.FromSeconds(configuration.GetValue<int>("Ollama:TimeoutSeconds", 120))
        };
        _logger = logger;
    }

    private async Task<bool> CheckAvailabilityAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<string>> GetAvailableModelsAsync()
    {
        if (_availableModels != null)
            return _availableModels;

        try
        {
            var response = await _httpClient.GetAsync("/api/tags");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<OllamaTagsResponse>();
                _availableModels = result?.Models?.Select(m => m.Name).ToList() ?? new List<string>();
                return _availableModels;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch Ollama models");
        }

        // Fallback to common models if we can't fetch
        _availableModels = new List<string> { "llama2", "mistral", "codellama", "llama2:13b", "mistral:7b" };
        return _availableModels;
    }

    public async Task<PromptTestResponse> TestPromptAsync(PromptTestRequest request)
    {
        try
        {
            var model = _defaultModel;
            var fullPrompt = $"{request.PromptContent}\n\nUser Input: {request.UserInput}\n\nResponse:";

            var ollamaRequest = new OllamaGenerateRequest
            {
                Model = model,
                Prompt = fullPrompt,
                Stream = false
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", ollamaRequest);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
            if (result == null)
                throw new Exception("Invalid response from Ollama");

            return new PromptTestResponse
            {
                Result = result.Response?.Trim() ?? "",
                Model = model,
                TokensUsed = (int)(result.EvalCount ?? 0)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing prompt with Ollama");
            throw;
        }
    }

    public async Task<GoalSuggestionsResult> GenerateGoalSuggestionsAsync(string intent, string mode = "intent")
    {
        var fallbackGoals = CategorySuggestions.ContainsKey(intent.ToLower())
            ? CategorySuggestions[intent.ToLower()].ToList()
            : new List<string> { "Create clear instructions", "Define expected output format", "Add context information" };

        var prompt = $"Generate 5 specific, actionable goals for creating an AI prompt with the following intent: {intent}\n\nRespond with a JSON array of strings, each representing a goal.{SafetyClause}";

        try
        {
            var request = new OllamaGenerateRequest
            {
                Model = _defaultModel,
                Prompt = prompt,
                Stream = false
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
            if (result?.Response == null)
                return new GoalSuggestionsResult(fallbackGoals, "professional");

            try
            {
                var suggestions = JsonSerializer.Deserialize<List<string>>(result.Response) ?? fallbackGoals;
                return new GoalSuggestionsResult(suggestions, "professional");
            }
            catch
            {
                var lines = result.Response.Split('\n')
                    .Where(line => !string.IsNullOrWhiteSpace(line) && line.Length > 10)
                    .Take(5)
                    .ToList();
                return new GoalSuggestionsResult(lines, "professional");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating goal suggestions with Ollama");
            return new GoalSuggestionsResult(fallbackGoals, "professional");
        }
    }

    public async Task<string> GeneratePromptFromGoalsAsync(string intent, List<string> selectedGoals, string? additionalContext)
    {
        var goalsText = string.Join(", ", selectedGoals);
        var contextText = string.IsNullOrEmpty(additionalContext) ? "" : $"\n\nAdditional Context: {additionalContext}";

        var prompt = $"Create a detailed AI prompt for the following intent: {intent}\n\nGoals to achieve: {goalsText}{contextText}\n\nGenerate a comprehensive prompt that incorporates these goals and provides clear instructions for an AI assistant.{SafetyClause}";

        try
        {
            var request = new OllamaGenerateRequest
            {
                Model = _defaultModel,
                Prompt = prompt,
                Stream = false
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
            return result?.Response?.Trim() ?? "Error generating prompt. Please try again.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating prompt from goals with Ollama");
            return $"Create a prompt for: {intent}\n\nGoals: {goalsText}\n\n{GetStyleInstruction("professional")}";
        }
    }

    public async Task<List<string>> GenerateRefinementSuggestionsAsync(string currentPrompt)
    {
        var prompt = $"Analyze this AI prompt and suggest 5 specific improvements:\n\n\"{currentPrompt}\"\n\nRespond with a JSON array of strings, each representing a specific refinement suggestion.{SafetyClause}";

        try
        {
            var request = new OllamaGenerateRequest
            {
                Model = _defaultModel,
                Prompt = prompt,
                Stream = false
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
            if (result?.Response == null)
                return new List<string> {
                    "Add more specific examples",
                    "Include clear success criteria",
                    "Specify the desired output format",
                    "Add context about the target audience",
                    "Include constraints or limitations"
                };

            try
            {
                var suggestions = JsonSerializer.Deserialize<List<string>>(result.Response);
                return suggestions ?? new List<string> {
                    "Add more specific examples",
                    "Include clear success criteria",
                    "Specify the desired output format",
                    "Add context about the target audience",
                    "Include constraints or limitations"
                };
            }
            catch
            {
                return result.Response.Split('\n')
                    .Where(line => !string.IsNullOrWhiteSpace(line) && line.Length > 10)
                    .Take(5)
                    .ToList();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating refinement suggestions with Ollama");
            return new List<string> {
                "Add more specific examples",
                "Include clear success criteria",
                "Specify the desired output format",
                "Add context about the target audience",
                "Include constraints or limitations"
            };
        }
    }

    public async Task<string> RefinePromptAsync(string currentPrompt, List<string> selectedRefinements, string? additionalContext)
    {
        var refinementsText = string.Join(", ", selectedRefinements);
        var contextText = string.IsNullOrEmpty(additionalContext) ? "" : $"\n\nAdditional Context: {additionalContext}";

        var prompt = $"Refine this AI prompt by incorporating the following improvements:\n\nOriginal Prompt:\n\"{currentPrompt}\"\n\nImprovements to apply: {refinementsText}{contextText}\n\nGenerate the improved version of the prompt.{SafetyClause}";

        try
        {
            var request = new OllamaGenerateRequest
            {
                Model = _defaultModel,
                Prompt = prompt,
                Stream = false
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
            return result?.Response?.Trim() ?? currentPrompt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refining prompt with Ollama");
            return currentPrompt; // Return original if refinement fails
        }
    }

    // Advanced features - basic implementations with fallbacks
    public async Task<List<string>> GenerateGoalSuggestionsWithPersonaAsync(string intent, string style, string category, string? personaPrompt)
    {
        // For Ollama, we'll combine persona context with basic goal generation
        var personaContext = string.IsNullOrEmpty(personaPrompt) ? "" : $"Considering this persona: {personaPrompt}\n\n";
        var enhancedIntent = $"{personaContext}Generate goals for: {intent} (Style: {style}, Category: {category})";

        return (await GenerateGoalSuggestionsAsync(enhancedIntent, "goal")).Goals;
    }

    public async Task<string> GeneratePromptWithPersonaAsync(string intent, List<string> selectedGoals, string? additionalContext, string? personaPrompt)
    {
        var personaContext = string.IsNullOrEmpty(personaPrompt) ? "" : $"\n\nPersona Context: {personaPrompt}";
        var enhancedContext = (additionalContext ?? "") + personaContext;

        return await GeneratePromptFromGoalsAsync(intent, selectedGoals, enhancedContext);
    }

    public Task<string> DescribeScreenAsync(string base64Image)
        => Task.FromResult(string.Empty);

    public async Task<string> TranscribeAudioAsync(Stream audioStream, string fileName)
    {
        // Ollama doesn't have built-in audio transcription
        // This would require additional setup with Whisper or similar
        throw new NotSupportedException("Audio transcription is not supported by Ollama. Please use OpenAI for audio features.");
    }

    public async Task<string> RefineTranscriptionToIntentAsync(string transcription)
    {
        // Related to audio transcription
        throw new NotSupportedException("Audio transcription refinement is not supported by Ollama. Please use OpenAI for audio features.");
    }

    public async Task<string> QuickRefineAsync(string currentPrompt, string refinementInstruction)
    {
        var prompt = $"Refine this prompt using the following instruction: {refinementInstruction}\n\nOriginal Prompt:\n\"{currentPrompt}\"\n\nProvide the refined version:{SafetyClause}";

        try
        {
            var request = new OllamaGenerateRequest
            {
                Model = _defaultModel,
                Prompt = prompt,
                Stream = false
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
            return result?.Response?.Trim() ?? currentPrompt;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error with quick refine using Ollama");
            return currentPrompt;
        }
    }

    public async Task<string> GeneratePersonaAsync(string description)
    {
        var prompt = $"Create a detailed AI persona based on this description: {description}\n\nGenerate a comprehensive persona that an AI assistant can use to maintain consistent behavior and personality.{SafetyClause}";

        try
        {
            var request = new OllamaGenerateRequest
            {
                Model = _defaultModel,
                Prompt = prompt,
                Stream = false
            };

            var response = await _httpClient.PostAsJsonAsync("/api/generate", request);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<OllamaGenerateResponse>();
            return result?.Response?.Trim() ?? "Error generating persona. Please try again.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating persona with Ollama");
            return $"Professional AI Assistant\n\nDescription: {description}\n\nMaintain a helpful, knowledgeable, and professional tone in all interactions.";
        }
    }

    public Task<ContentModerationResult> ModerateContentAsync(string content)
    {
        return Task.FromResult(new ContentModerationResult { IsFlagged = false, FlaggedCategories = new List<string>() });
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
        // Ollama does not support native function calling — fall back to plain completion
        var result = await TestPromptAsync(new MoneyManager.Api.Models.PromptTestRequest
        {
            PromptContent = systemPrompt,
            UserInput = userInput
        });
        var step = new AgentRunStep("output", result.Result);
        onStep?.Invoke(step);
        return new AgentLoopResult(result.Result, [step], result.TokensUsed, 1);
    }

    public async IAsyncEnumerable<string> StreamExecuteAsync(
        string systemPrompt,
        List<ConversationMessage> messages,
        string? modelOverride,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var lastUser = messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var result = await TestPromptAsync(new MoneyManager.Api.Models.PromptTestRequest { PromptContent = systemPrompt, UserInput = lastUser });
        yield return result.Result;
    }

    private string GetStyleInstruction(string? style)
    {
        if (string.IsNullOrEmpty(style) || !StyleDescriptions.ContainsKey(style.ToLower()))
        {
            return StyleDescriptions["professional"];
        }
        return StyleDescriptions[style.ToLower()];
    }
}

// Ollama API Models
public class OllamaGenerateRequest
{
    public string Model { get; set; } = "";
    public string Prompt { get; set; } = "";
    public bool Stream { get; set; }
}

public class OllamaGenerateResponse
{
    public string Response { get; set; } = "";
    public bool Done { get; set; }
    public string Model { get; set; } = "";
    public long? EvalCount { get; set; }
    public long? EvalDuration { get; set; }
}

public class OllamaTagsResponse
{
    public List<OllamaModel> Models { get; set; } = new();
}

public class OllamaModel
{
    public string Name { get; set; } = "";
    public string Size { get; set; } = "";
    public string Digest { get; set; } = "";
    public DateTime ModifiedAt { get; set; }
}