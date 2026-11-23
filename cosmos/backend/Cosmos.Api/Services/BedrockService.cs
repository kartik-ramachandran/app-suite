using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using Cosmos.Api.Models;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Cosmos.Api.Services;

public class BedrockService : IAIService
{
    private readonly AmazonBedrockRuntimeClient _bedrockClient;
    private readonly ILogger<BedrockService> _logger;
    private readonly string _defaultModel;
    private readonly string _codeGenerationModel;
    private readonly string _chatModel;
    private readonly int _maxTokens;
    private readonly double _temperature;

    private const string SafetyClause =
        "\n\nIMPORTANT SAFETY RULES: " +
        "You MUST refuse to generate content that is violent, sexually explicit, hateful, discriminatory, " +
        "promotes illegal activities, targets minors, encourages self-harm, or violates any ethical guidelines. " +
        "If the user's request involves any prohibited content, respond with: " +
        "\"I cannot generate this type of content. Please provide a different request that is safe and appropriate.\" " +
        "All generated prompts must be suitable for a general audience and comply with standard advertising content policies.";

    private readonly List<string> _supportedModels;

    public bool IsAvailable => _bedrockClient != null;
    public string ProviderName => "AmazonBedrock";
    public List<string> SupportedModels => _supportedModels;

    private static readonly Dictionary<string, string> StyleDescriptions = new()
    {
        { "professional", "Use a formal, business-appropriate tone. Be clear, concise, and authoritative. Avoid casual language and maintain a polished, corporate communication style." },
        { "creative", "Use imaginative, expressive language. Be playful with words, use metaphors and vivid descriptions. Encourage creative thinking and unique perspectives." },
        { "technical", "Use precise, detailed technical language. Include specific terminology, structured explanations, and focus on accuracy and completeness. Be methodical and thorough." },
        { "conversational", "Use a friendly, natural dialogue style. Be warm and approachable, like talking to a helpful friend. Use casual but respectful language." },
        { "persuasive", "Use compelling, action-oriented language. Focus on benefits, create urgency, and motivate action. Be confident and use strong calls-to-action." }
    };

    public BedrockService(IConfiguration configuration, ILogger<BedrockService> logger)
    {
        _logger = logger;
        
        var accessKey = configuration["AWS:AccessKeyId"];
        var secretKey = configuration["AWS:SecretAccessKey"];
        var region = configuration["AWS:Region"] ?? "ap-southeast-2";

        // Initialize configuration values first
        _defaultModel = configuration["AWS:Bedrock:DefaultModel"] ?? "apac.anthropic.claude-3-5-sonnet-20240620-v1:0";
        _codeGenerationModel = configuration["AWS:Bedrock:CodeGenerationModel"] ?? _defaultModel;
        _chatModel = configuration["AWS:Bedrock:ChatModel"] ?? _defaultModel;
        _maxTokens = configuration.GetValue<int>("AWS:Bedrock:MaxTokens", 8192);
        _temperature = configuration.GetValue<double>("AWS:Bedrock:Temperature", 0.7);
        _supportedModels = configuration.GetSection("AWS:Bedrock:Models").Get<List<string>>()
            ?? new List<string> { _defaultModel };

        if (string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
        {
            _logger.LogWarning("AWS Bedrock credentials not configured. Service will be unavailable.");
            _bedrockClient = null!;
            _supportedModels = new List<string>();
            return;
        }

        var credentials = new BasicAWSCredentials(accessKey, secretKey);
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        _bedrockClient = new AmazonBedrockRuntimeClient(credentials, regionEndpoint);

        _logger.LogInformation($"AWS Bedrock service initialized with model: {_defaultModel}");
    }

    private async Task<string> InvokeClaudeAsync(string systemPrompt, string userPrompt, string? modelOverride = null)
    {
        var model = modelOverride ?? _defaultModel;
        
        var requestBody = new
        {
            anthropic_version = "bedrock-2023-05-31",
            max_tokens = _maxTokens,
            temperature = _temperature,
            system = systemPrompt,
            messages = new[]
            {
                new { role = "user", content = userPrompt }
            }
        };

        var jsonRequest = JsonSerializer.Serialize(requestBody);
        
        var request = new InvokeModelRequest
        {
            ModelId = model,
            Body = new MemoryStream(Encoding.UTF8.GetBytes(jsonRequest)),
            ContentType = "application/json",
            Accept = "application/json"
        };

        try
        {
            var response = await _bedrockClient.InvokeModelAsync(request);
            
            using var reader = new StreamReader(response.Body);
            var responseBody = await reader.ReadToEndAsync();
            
            var jsonResponse = JsonDocument.Parse(responseBody);
            var content = jsonResponse.RootElement
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString();

            return content ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error invoking Bedrock model {model}");
            throw;
        }
    }

    public async IAsyncEnumerable<string> StreamExecuteAsync(
        string systemPrompt,
        List<ConversationMessage> messages,
        string? modelOverride,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var model = modelOverride ?? _defaultModel;

        var bedrockMessages = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray();
        var requestBody = JsonSerializer.Serialize(new
        {
            anthropic_version = "bedrock-2023-05-31",
            max_tokens = _maxTokens,
            temperature = _temperature,
            system = systemPrompt,
            messages = bedrockMessages
        });

        var request = new InvokeModelWithResponseStreamRequest
        {
            ModelId = model,
            Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody)),
            ContentType = "application/json",
            Accept = "application/json"
        };

        var response = await _bedrockClient.InvokeModelWithResponseStreamAsync(request, ct);

        foreach (var evt in response.Body)
        {
            if (evt is not PayloadPart payload) continue;

            var json = Encoding.UTF8.GetString(payload.Bytes.ToArray());
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl)) continue;
            if (typeEl.GetString() != "content_block_delta") continue;
            if (!root.TryGetProperty("delta", out var delta)) continue;
            if (!delta.TryGetProperty("text", out var textEl)) continue;

            var chunk = textEl.GetString();
            if (!string.IsNullOrEmpty(chunk))
                yield return chunk;
        }
    }

    public async Task<PromptTestResponse> TestPromptAsync(PromptTestRequest request)
    {
        try
        {
            var systemPrompt = request.PromptContent;
            var userPrompt = request.UserInput ?? "Hello";

            // Prepend RAG context documents to user prompt
            if (request.ContextDocuments?.Count > 0)
            {
                var contextText = "--- Retrieved Context ---\n" +
                    string.Join("\n\n", request.ContextDocuments.Select((doc, i) =>
                        $"[{(string.IsNullOrWhiteSpace(doc.Source) ? $"Document {i + 1}" : doc.Source)}]:\n{doc.Content}"));
                userPrompt = contextText + "\n\n---\n\n" + userPrompt;
            }

            var result = await InvokeClaudeAsync(systemPrompt, userPrompt);

            return new PromptTestResponse
            {
                Result = result,
                Model = _defaultModel,
                TokensUsed = 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing prompt with Bedrock");
            throw;
        }
    }

    public async Task<GoalSuggestionsResult> GenerateGoalSuggestionsAsync(string intent, string mode = "intent")
    {
        try
        {
            var isIntentMode = mode == "intent";

            var systemPrompt = isIntentMode
                ? "You are an expert AI assistant helping users clarify what they want an AI prompt to do. " +
                  "Based on the user's description, generate 20 clear intent statements — each describing a specific PURPOSE or USE CASE for the prompt. " +
                  "Each intent should start with an action verb and describe what the AI should do (e.g. 'Help users draft...', 'Explain how to...', 'Analyse and summarise...'). " +
                  "\n\nReturn ONLY a JSON object with two fields:\n" +
                  "- \"goals\": array of 20 intent strings\n" +
                  "- \"suggestedStyle\": one of exactly: professional, creative, technical, conversational, persuasive\n\n" +
                  "Choose suggestedStyle based on the intent: 'technical' for dev/engineering, 'creative' for writing/marketing/design, " +
                  "'conversational' for chatbots/support, 'persuasive' for sales/pitches, 'professional' for business/formal tasks.\n\n" +
                  "No markdown, no explanations, just the raw JSON object." +
                  SafetyClause
                : "You are an expert AI assistant helping users define what they want to achieve. " +
                  "Based on the user's description, generate 20 clear, specific GOAL statements — each describing a measurable outcome or objective the prompt should accomplish. " +
                  "Each goal should describe a desired result or capability (e.g. 'Increase response accuracy by...', 'Ensure the output always includes...', 'Reduce the time needed to...'). " +
                  "\n\nReturn ONLY a JSON object with two fields:\n" +
                  "- \"goals\": array of 20 goal strings\n" +
                  "- \"suggestedStyle\": one of exactly: professional, creative, technical, conversational, persuasive\n\n" +
                  "Choose suggestedStyle based on the intent: 'technical' for dev/engineering, 'creative' for writing/marketing/design, " +
                  "'conversational' for chatbots/support, 'persuasive' for sales/pitches, 'professional' for business/formal tasks.\n\n" +
                  "No markdown, no explanations, just the raw JSON object." +
                  SafetyClause;

            var userPrompt = $"User's description: {intent}\n\n" +
                (isIntentMode
                    ? "Generate 20 intent statements (what should this AI prompt DO?) and suggest the best style."
                    : "Generate 20 goal statements (what outcomes should this prompt ACHIEVE?) and suggest the best style.");

            var result = await InvokeClaudeAsync(systemPrompt, userPrompt);
            var json = ExtractJson(result);
            var parsed = JsonSerializer.Deserialize<JsonElement>(json);
            var goals = parsed.GetProperty("goals").Deserialize<List<string>>() ?? new List<string>();
            var suggestedStyle = parsed.TryGetProperty("suggestedStyle", out var styleEl)
                ? styleEl.GetString() ?? "professional"
                : "professional";

            return new GoalSuggestionsResult(goals, suggestedStyle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating goal suggestions with Bedrock");
            throw;
        }
    }

    public async Task<string> GeneratePromptFromGoalsAsync(string intent, List<string> selectedGoals, string? additionalContext)
    {
        try
        {
            var systemPrompt = "You are an expert prompt engineer. Based on the user's intent and selected goals, create a single, comprehensive, " +
                "production-ready prompt that accomplishes all the selected objectives. " +
                "The prompt should be detailed, well-structured, and ready to use. " +
                "\n\nReturn ONLY the prompt text itself, no markdown, no explanations, no extra formatting." +
                SafetyClause;

            var goalsText = string.Join("\n", selectedGoals.Select((g, i) => $"{i + 1}. {g}"));
            var contextPart = !string.IsNullOrEmpty(additionalContext)
                ? $"\n\nAdditional Context/Refinement: {additionalContext}"
                : "";

            var userPrompt = $"User's Intent: {intent}\n\n" +
                $"The prompt must accomplish these selected goals:\n{goalsText}{contextPart}\n\n" +
                $"Generate a detailed prompt that addresses the intent and all selected objectives.";

            return await InvokeClaudeAsync(systemPrompt, userPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating prompt from goals with Bedrock");
            throw;
        }
    }

    public async Task<List<string>> GenerateRefinementSuggestionsAsync(string currentPrompt)
    {
        try
        {
            var systemPrompt = "You are an expert prompt engineer. Based on the user's existing prompt, generate 20 clear, specific refinement suggestions. " +
                "Each refinement should be a complete sentence describing a specific improvement, modification, or addition. " +
                "Think about: tone adjustments, formatting improvements, constraint additions, capability enhancements, edge case handling, etc. " +
                "Return ONLY a JSON array of 20 strings, nothing else. No markdown, no explanations." +
                SafetyClause;

            var userPrompt = $"Current Prompt:\n{currentPrompt}\n\n" +
                $"Generate 20 different refinement suggestions to improve this prompt.\n" +
                $"Examples of good refinements:\n" +
                $"- Add more specific formatting instructions for the output\n" +
                $"- Include edge case handling for ambiguous inputs\n" +
                $"- Adjust tone to be more professional/casual\n" +
                $"- Add constraints for response length\n" +
                $"- Include examples of desired output format\n\n" +
                $"Make each refinement specific, actionable, and focused on a single improvement.";

            var result = await InvokeClaudeAsync(systemPrompt, userPrompt);
            var refinements = JsonSerializer.Deserialize<List<string>>(result) ?? new List<string>();

            return refinements;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating refinement suggestions with Bedrock");
            throw;
        }
    }

    public async Task<string> RefinePromptAsync(string currentPrompt, List<string> selectedRefinements, string? additionalContext)
    {
        try
        {
            var systemPrompt = "You are an expert prompt engineer. Based on the current prompt and selected refinements, create an improved version. " +
                "Apply all the selected refinements while maintaining the core intent and functionality. " +
                "The refined prompt should be detailed, well-structured, and production-ready. " +
                "\n\nReturn ONLY the refined prompt text itself, no markdown, no explanations, no extra formatting." +
                SafetyClause;

            var refinementsText = string.Join("\n", selectedRefinements.Select((r, i) => $"{i + 1}. {r}"));
            var contextPart = !string.IsNullOrEmpty(additionalContext)
                ? $"\n\nAdditional Refinement Instructions: {additionalContext}"
                : "";

            var userPrompt = $"Current Prompt:\n{currentPrompt}\n\n" +
                $"Apply these refinements:\n{refinementsText}{contextPart}\n\n" +
                $"Generate the refined prompt that incorporates all selected improvements.";

            return await InvokeClaudeAsync(systemPrompt, userPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refining prompt with Bedrock");
            throw;
        }
    }

    private string GetStyleInstruction(string? style)
    {
        if (string.IsNullOrEmpty(style) || !StyleDescriptions.ContainsKey(style.ToLower()))
        {
            return StyleDescriptions["professional"];
        }
        return StyleDescriptions[style.ToLower()];
    }

    public async Task<List<string>> GenerateGoalSuggestionsWithPersonaAsync(string intent, string style, string category, string? personaPrompt)
    {
        try
        {
            var systemPrompt = $"You are acting with this persona: {personaPrompt}\n\n" +
                "Based on the user's intent, generate 20 clear, specific goal statements. " +
                "\n\nReturn ONLY a JSON array of 20 strings, nothing else." +
                SafetyClause;

            var userPrompt = $"User's Intent: {intent}\n\nGenerate 20 different goal/objective suggestions.";

            var result = await InvokeClaudeAsync(systemPrompt, userPrompt);
            return JsonSerializer.Deserialize<List<string>>(result) ?? new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating persona-aware goal suggestions with Bedrock");
            throw;
        }
    }

    public async Task<string> GeneratePromptWithPersonaAsync(string intent, List<string> selectedGoals, string? additionalContext, string? personaPrompt)
    {
        try
        {
            var personaContext = string.IsNullOrEmpty(personaPrompt) ? "" : $"\n\nPersona Context: {personaPrompt}";
            var systemPrompt = "You are an expert prompt engineer. Based on the user's intent and selected goals, create a single, comprehensive, " +
                "production-ready prompt that accomplishes all the selected objectives. " +
                "The prompt should be detailed, well-structured, and ready to use. " +
                "Consider the persona context when crafting the prompt to ensure it aligns with the desired AI behavior." +
                "\n\nReturn ONLY the prompt text itself, no markdown, no explanations, no extra formatting." +
                SafetyClause;

            var goalsText = string.Join("\n", selectedGoals.Select((g, i) => $"{i + 1}. {g}"));
            var contextPart = !string.IsNullOrEmpty(additionalContext)
                ? $"\n\nAdditional Context/Refinement: {additionalContext}"
                : "";

            var userPrompt = $"User's Intent: {intent}\n\n" +
                $"The prompt must accomplish these selected goals:\n{goalsText}{contextPart}{personaContext}\n\n" +
                $"Generate a detailed prompt that addresses the intent and all selected objectives, taking the persona into account.";

            return await InvokeClaudeAsync(systemPrompt, userPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating prompt with persona with Bedrock");
            throw;
        }
    }

    public async Task<string> QuickRefineAsync(string currentPrompt, string refinementInstruction)
    {
        try
        {
            var systemPrompt = "You are an expert prompt engineer. Apply the specified refinement to improve the given prompt. " +
                "Maintain the core intent and functionality while making the requested improvement. " +
                "The refined prompt should be well-structured and production-ready. " +
                "\n\nReturn ONLY the refined prompt text itself, no markdown, no explanations, no extra formatting." +
                SafetyClause;

            var userPrompt = $"Current Prompt:\n{currentPrompt}\n\n" +
                $"Refinement Instructions: {refinementInstruction}\n\n" +
                $"Apply this refinement to the prompt while maintaining its core purpose.";

            return await InvokeClaudeAsync(systemPrompt, userPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying quick refinement with Bedrock");
            throw;
        }
    }

    public async Task<string> GeneratePersonaAsync(string description)
    {
        try
        {
            var systemPrompt = "You are an expert at creating AI personas. Given a description, generate a complete AI persona with all fields. " +
                "Return ONLY valid JSON with these exact fields: name, systemPrompt, description, tone, expertise, constraints, icon (single emoji), category. " +
                "The systemPrompt should be a comprehensive 'Act as...' instruction that defines the persona's behavior, knowledge, and communication style. " +
                "No markdown, no explanations, just the JSON object." +
                SafetyClause;

            var userPrompt = $"Create a detailed AI persona based on this description: {description}\n\n" +
                "Generate a JSON object with: name (creative name), systemPrompt (detailed 'Act as...' instruction), " +
                "description (1-2 sentence summary), tone (e.g. formal/witty/empathetic), " +
                "expertise (comma-separated skills), constraints (what the persona should NOT do), " +
                "icon (single relevant emoji), category (e.g. Development, Writing, Marketing, Support, Education)";

            return await InvokeClaudeAsync(systemPrompt, userPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating persona with Bedrock");
            throw;
        }
    }

    public Task<string> DescribeScreenAsync(string base64Image)
        => Task.FromResult(string.Empty);

    // Audio transcription - not supported by Bedrock, fallback to OpenAI or return error
    public Task<string> TranscribeAudioAsync(Stream audioStream, string fileName)
    {
        _logger.LogWarning("Audio transcription is not supported by AWS Bedrock. Please use OpenAI or another provider.");
        throw new NotSupportedException("Audio transcription is not supported by AWS Bedrock.");
    }

    public async Task<string> RefineTranscriptionToIntentAsync(string rawTranscription)
    {
        try
        {
            var systemPrompt = "You are a helpful assistant that converts spoken language into a clean, structured prompt intent. " +
                "Remove filler words (um, uh, like, you know), fix grammar, and structure the request clearly. " +
                "Return ONLY the cleaned intent text, nothing else.";

            var userPrompt = $"Clean up this spoken intent into a clear prompt description:\n\n{rawTranscription}";

            return await InvokeClaudeAsync(systemPrompt, userPrompt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refining transcription with Bedrock");
            throw;
        }
    }

    // Content moderation - not directly supported by Bedrock, return safe result
    public Task<ContentModerationResult> ModerateContentAsync(string content)
    {
        _logger.LogWarning("Content moderation is not natively supported by AWS Bedrock. Returning safe result.");
        // Return safe by default - consider implementing custom moderation logic or using separate service
        return Task.FromResult(new ContentModerationResult
        {
            IsFlagged = false,
            FlaggedCategories = new List<string>()
        });
    }

    // ── Native tool calling via Anthropic Messages API (Bedrock InvokeModel) ──

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
        if (!IsAvailable)
            throw new InvalidOperationException("Bedrock service is not available");

        var model = modelOverride ?? _defaultModel;

        // Build Anthropic-format tool definitions
        var toolDefs = tools.Select(t =>
        {
            var schema = JsonDocument.Parse(t.ParametersSchema).RootElement;
            return new { name = t.Name, description = t.Description, input_schema = schema };
        }).ToArray();

        // Conversation history as raw JSON objects (Anthropic Messages format)
        var messageHistory = new List<object>
        {
            new { role = "user", content = userInput }
        };

        var steps = new List<AgentRunStep>();
        var totalTokens = 0;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var requestBody = JsonSerializer.Serialize(new
            {
                anthropic_version = "bedrock-2023-05-31",
                max_tokens = _maxTokens,
                temperature = _temperature,
                system = systemPrompt,
                tools = toolDefs,
                tool_choice = tools.Count > 0 ? new { type = "auto" } : (object?)null,
                messages = messageHistory
            });

            var request = new InvokeModelRequest
            {
                ModelId = model,
                Body = new MemoryStream(Encoding.UTF8.GetBytes(requestBody)),
                ContentType = "application/json",
                Accept = "application/json"
            };

            var response = await _bedrockClient.InvokeModelAsync(request, ct);
            using var reader = new StreamReader(response.Body);
            var responseJson = await reader.ReadToEndAsync();
            var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            var stopReason = root.TryGetProperty("stop_reason", out var sr) ? sr.GetString() : "end_turn";
            var contentBlocks = root.GetProperty("content").EnumerateArray().ToList();

            // Usage tokens
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var inp)) totalTokens += inp.GetInt32();
                if (usage.TryGetProperty("output_tokens", out var out_)) totalTokens += out_.GetInt32();
            }

            if (stopReason == "tool_use")
            {
                // Add assistant message with the full content (text + tool_use blocks)
                messageHistory.Add(new { role = "assistant", content = contentBlocks.Select(b => b.Clone()).ToArray() });

                var toolResults = new List<object>();
                foreach (var block in contentBlocks)
                {
                    if (!block.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "tool_use") continue;

                    var toolId   = block.GetProperty("id").GetString()!;
                    var toolName = block.GetProperty("name").GetString()!;
                    var toolInput = block.TryGetProperty("input", out var inp2) ? inp2.ToString() : "{}";

                    var callStep = new AgentRunStep("tool_call", toolInput, ToolName: toolName, ToolInput: toolInput);
                    steps.Add(callStep);
                    onStep?.Invoke(callStep);

                    string toolOutput;
                    try { toolOutput = await toolExecutor(new AgentToolCall(toolId, toolName, toolInput)); }
                    catch (Exception ex) { toolOutput = $"Tool error: {ex.Message}"; }

                    var resultStep = new AgentRunStep("tool_result", toolOutput, ToolName: toolName, ToolOutput: toolOutput);
                    steps.Add(resultStep);
                    onStep?.Invoke(resultStep);

                    toolResults.Add(new { type = "tool_result", tool_use_id = toolId, content = toolOutput });
                }

                messageHistory.Add(new { role = "user", content = toolResults.ToArray() });
                continue;
            }

            // end_turn or other — final answer
            var textBlock = contentBlocks.FirstOrDefault(b =>
                b.TryGetProperty("type", out var t) && t.GetString() == "text");
            var finalText = textBlock.ValueKind == JsonValueKind.Undefined ? string.Empty
                : textBlock.GetProperty("text").GetString() ?? string.Empty;

            var outStep = new AgentRunStep("output", finalText);
            steps.Add(outStep);
            onStep?.Invoke(outStep);
            return new AgentLoopResult(finalText, steps, totalTokens, iteration);
        }

        var maxStep = new AgentRunStep("output", "Maximum iterations reached.");
        steps.Add(maxStep);
        onStep?.Invoke(maxStep);
        return new AgentLoopResult(maxStep.Content, steps, totalTokens, maxIterations);
    }

    /// <summary>
    /// Strips markdown code fences (```json ... ``` or ``` ... ```) that Claude
    /// sometimes adds despite being told not to, so JSON parsing doesn't fail.
    /// </summary>
    private static string ExtractJson(string raw)
    {
        var text = raw.Trim();
        // Remove ```json ... ``` or ``` ... ```
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```")) text = text[..^3].TrimEnd();
        }
        return text.Trim();
    }
}
