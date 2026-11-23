using OpenAI.Chat;
using OpenAI.Audio;
using OpenAI.Moderations;
using StudyManager.Api.Models;
using System.Runtime.CompilerServices;

namespace StudyManager.Api.Services;

public class OpenAIService : IAIService
{
    private readonly ChatClient _chatClient;
    private readonly AudioClient _audioClient;
    private readonly ModerationClient _moderationClient;
    private readonly ILogger<OpenAIService> _logger;
    private readonly string _defaultModel;
    private readonly string _apiKey;

    // Safety guardrail appended to all system prompts
    private const string SafetyClause =
        "\n\nIMPORTANT SAFETY RULES: " +
        "You MUST refuse to generate content that is violent, sexually explicit, hateful, discriminatory, " +
        "promotes illegal activities, targets minors, encourages self-harm, or violates any ethical guidelines. " +
        "If the user's request involves any prohibited content, respond with: " +
        "\"I cannot generate this type of content. Please provide a different request that is safe and appropriate.\" " +
        "All generated prompts must be suitable for a general audience and comply with standard advertising content policies.";

    public bool IsAvailable => _chatClient != null;
    public string ProviderName => "OpenAI";
    public List<string> SupportedModels { get; }

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

    public OpenAIService(IConfiguration configuration, ILogger<OpenAIService> logger)
    {
        _logger = logger;
        var apiKey = configuration["OpenAI:ApiKey"];

        _defaultModel = configuration["OpenAI:DefaultModel"] ?? "gpt-5.4";
        SupportedModels = configuration.GetSection("OpenAI:Models").Get<List<string>>()
            ?? new List<string> { "gpt-5.4", "gpt-5", "gpt-4.1", "gpt-4.1-mini", "gpt-4o", "gpt-4o-mini" };

        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("OpenAI API key not configured. Service will be unavailable.");
            _chatClient = null!;
            _audioClient = null!;
            _moderationClient = null!;
            _apiKey = null!;
            return;
        }

        _apiKey = apiKey;
        _chatClient = new ChatClient(model: _defaultModel, apiKey: apiKey);
        _audioClient = new AudioClient(model: "whisper-1", apiKey: apiKey);
        _moderationClient = new ModerationClient(model: "omni-moderation-latest", apiKey: apiKey);
    }

    public async IAsyncEnumerable<string> StreamExecuteAsync(
        string systemPrompt,
        List<ConversationMessage> messages,
        string? modelOverride,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("OpenAI service is not available");

        var model = modelOverride ?? _defaultModel;
        var client = new ChatClient(model: model, apiKey: _apiKey!);

        var chatMessages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            chatMessages.Add(new SystemChatMessage(systemPrompt));

        foreach (var msg in messages)
        {
            if (msg.Role == "user") chatMessages.Add(new UserChatMessage(msg.Content));
            else if (msg.Role == "assistant") chatMessages.Add(new AssistantChatMessage(msg.Content));
        }

        var options = new ChatCompletionOptions { MaxOutputTokenCount = 4096, Temperature = 0.7f };

        await foreach (var update in client.CompleteChatStreamingAsync(chatMessages, options).WithCancellation(ct))
        {
            foreach (var part in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(part.Text))
                    yield return part.Text;
            }
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

    private string[] GetCategorySuggestions(string? category)
    {
        if (string.IsNullOrEmpty(category))
        {
            return Array.Empty<string>();
        }

        var normalizedCategory = category.ToLower().Trim();

        // Check for exact match
        if (CategorySuggestions.ContainsKey(normalizedCategory))
        {
            return CategorySuggestions[normalizedCategory];
        }

        // Check for partial matches
        foreach (var (key, suggestions) in CategorySuggestions)
        {
            if (normalizedCategory.Contains(key) || key.Contains(normalizedCategory))
            {
                return suggestions;
            }
        }

        return Array.Empty<string>();
    }

    public async Task<string> DescribeScreenAsync(string base64Image)
    {
        if (!IsAvailable) return string.Empty;
        try
        {
            // Strip data URI prefix if present
            var comma = base64Image.IndexOf(',');
            var imageData = comma >= 0 ? base64Image[(comma + 1)..] : base64Image;
            var bytes = Convert.FromBase64String(imageData);

            var visionClient = new ChatClient(model: "gpt-4o", apiKey: _apiKey);
            var imagePart = ChatMessageContentPart.CreateImagePart(new BinaryData(bytes), "image/png");
            var textPart = ChatMessageContentPart.CreateTextPart(
                "Describe what is visible on this screen in 2-3 concise sentences. Focus on: the active application, the content being worked on, and any relevant text or context visible. Be specific and factual.");

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage("You are a screen context extractor. Describe what is on screen to help generate a relevant AI prompt."),
                new UserChatMessage(new[] { textPart, imagePart })
            };

            var result = await visionClient.CompleteChatAsync(messages, new ChatCompletionOptions { MaxOutputTokenCount = 256 });
            return result.Value.Content[0].Text ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Vision screen description failed");
            return string.Empty;
        }
    }

    public async Task<PromptTestResponse> TestPromptAsync(PromptTestRequest request)
    {
        try
        {
            var messages = new List<ChatMessage> { new SystemChatMessage(request.PromptContent) };

            // Inject RAG context documents before the user message
            if (request.ContextDocuments?.Count > 0)
            {
                var contextText = "--- Retrieved Context ---\n" +
                    string.Join("\n\n", request.ContextDocuments.Select((doc, i) =>
                        $"[{(string.IsNullOrWhiteSpace(doc.Source) ? $"Document {i + 1}" : doc.Source)}]:\n{doc.Content}"));
                messages.Add(new UserChatMessage(contextText));
                messages.Add(new AssistantChatMessage("Understood. I have reviewed the provided context and will use it to inform my response."));
            }

            if (!string.IsNullOrEmpty(request.UserInput))
                messages.Add(new UserChatMessage(request.UserInput));

            var response = await _chatClient.CompleteChatAsync(messages);

            return new PromptTestResponse
            {
                Result = response.Value.Content[0].Text,
                Model = _defaultModel,
                TokensUsed = response.Value.Usage.TotalTokenCount
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing prompt with OpenAI");
            throw;
        }
    }

    public async Task<GoalSuggestionsResult> GenerateGoalSuggestionsAsync(string intent, string mode = "intent")
    {
        try
        {
            var isIntentMode = mode == "intent";
            var systemPromptText = isIntentMode
                ? "You are an expert AI assistant helping users clarify what they want an AI prompt to do. " +
                  "Based on the user's description, generate 20 clear intent statements — each describing a specific PURPOSE or USE CASE for the prompt. " +
                  "Each intent should start with an action verb and describe what the AI should do (e.g. 'Help users navigate product returns step by step'). " +
                  "Also suggest the best prompt style.\n\n" +
                  "Return ONLY a JSON object with two fields:\n" +
                  "- \"goals\": array of 20 intent strings\n" +
                  "- \"suggestedStyle\": one of exactly: professional, creative, technical, conversational, persuasive\n\n" +
                  "Choose suggestedStyle based on the intent: 'technical' for dev/engineering, 'creative' for writing/marketing/design, " +
                  "'conversational' for chatbots/support, 'persuasive' for sales/pitches, 'professional' for business/formal tasks.\n\n" +
                  "No markdown, no explanations, just the raw JSON object." + SafetyClause
                : "You are an expert AI assistant helping users define what they want to achieve. " +
                  "Based on the user's intent, generate 20 clear, specific goal statements AND suggest the best prompt style. " +
                  "Each goal should be a complete sentence describing a specific objective that relates to their intent. " +
                  "\n\nReturn ONLY a JSON object with two fields:\n" +
                  "- \"goals\": array of 20 goal strings\n" +
                  "- \"suggestedStyle\": one of exactly: professional, creative, technical, conversational, persuasive\n\n" +
                  "Choose suggestedStyle based on the intent: 'technical' for dev/engineering, 'creative' for writing/marketing/design, " +
                  "'conversational' for chatbots/support, 'persuasive' for sales/pitches, 'professional' for business/formal tasks.\n\n" +
                  "No markdown, no explanations, just the raw JSON object." + SafetyClause;

            var systemMessage = new SystemChatMessage(systemPromptText);

            var userMessage = new UserChatMessage(
                isIntentMode
                    ? $"User's Description: {intent}\n\nGenerate 20 intent statements describing what the AI prompt should DO, and suggest the best style.\nExamples:\n- Guide users through troubleshooting steps interactively\n- Summarise long documents into key bullet points\n- Draft professional emails based on brief notes\n\nMake each intent specific and action-oriented."
                    : $"User's Intent: {intent}\n\nGenerate 20 goal/objective suggestions and suggest the best style.\nExamples of good goals:\n- Handle customer complaints with empathy and professionalism\n- Provide accurate product information and specifications\n- Escalate complex issues to human agents when needed\n\nMake each goal specific, actionable, and relevant to the user's intent."
            );

            var messages = new List<ChatMessage> { systemMessage, userMessage };
            var response = await _chatClient.CompleteChatAsync(messages);
            var content = response.Value.Content[0].Text;

            var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(ExtractJson(content));
            var goals = System.Text.Json.JsonSerializer.Deserialize<List<string>>(parsed.GetProperty("goals").GetRawText()) ?? [];
            var suggestedStyle = parsed.TryGetProperty("suggestedStyle", out var styleEl)
                ? styleEl.GetString() ?? "professional"
                : "professional";

            return new GoalSuggestionsResult(goals, suggestedStyle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating goal suggestions");
            throw;
        }
    }

    public async Task<string> GeneratePromptFromGoalsAsync(string intent, List<string> selectedGoals, string? additionalContext)
    {
        try
        {
            var systemMessage = new SystemChatMessage(
                "You are an expert prompt engineer. Based on the user's intent and selected goals, create a single, comprehensive, " +
                "production-ready prompt that accomplishes all the selected objectives. " +
                "The prompt should be detailed, well-structured, and ready to use. " +
                "\n\nReturn ONLY the prompt text itself, no markdown, no explanations, no extra formatting." +
                SafetyClause
            );

            var goalsText = string.Join("\n", selectedGoals.Select((g, i) => $"{i + 1}. {g}"));
            var contextPart = !string.IsNullOrEmpty(additionalContext)
                ? $"\n\nAdditional Context/Refinement: {additionalContext}"
                : "";

            var userMessage = new UserChatMessage(
                $"User's Intent: {intent}\n\n" +
                $"The prompt must accomplish these selected goals:\n{goalsText}{contextPart}\n\n" +
                $"Generate a detailed prompt that addresses the intent and all selected objectives."
            );

            var messages = new List<ChatMessage> { systemMessage, userMessage };
            var response = await _chatClient.CompleteChatAsync(messages);

            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating prompt from goals");
            throw;
        }
    }

    public async Task<List<string>> GenerateRefinementSuggestionsAsync(string currentPrompt)
    {
        try
        {
            var systemMessage = new SystemChatMessage(
                "You are an expert prompt engineer. Based on the user's existing prompt, generate 20 clear, specific refinement suggestions. " +
                "Each refinement should be a complete sentence describing a specific improvement, modification, or addition. " +
                "Think about: tone adjustments, formatting improvements, constraint additions, capability enhancements, edge case handling, etc. " +
                "Return ONLY a JSON array of 20 strings, nothing else. No markdown, no explanations." +
                SafetyClause
            );

            var userMessage = new UserChatMessage(
                $"Current Prompt:\n{currentPrompt}\n\n" +
                $"Generate 20 different refinement suggestions to improve this prompt.\n" +
                $"Examples of good refinements:\n" +
                $"- Add more specific formatting instructions for the output\n" +
                $"- Include edge case handling for ambiguous inputs\n" +
                $"- Adjust tone to be more professional/casual\n" +
                $"- Add constraints for response length\n" +
                $"- Include examples of desired output format\n\n" +
                $"Make each refinement specific, actionable, and focused on a single improvement."
            );

            var messages = new List<ChatMessage> { systemMessage, userMessage };
            var response = await _chatClient.CompleteChatAsync(messages);
            var content = response.Value.Content[0].Text;

            var refinements = System.Text.Json.JsonSerializer.Deserialize<List<string>>(content)
                ?? new List<string>();

            return refinements;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating refinement suggestions");
            throw;
        }
    }

    public async Task<string> RefinePromptAsync(string currentPrompt, List<string> selectedRefinements, string? additionalContext)
    {
        try
        {
            var systemMessage = new SystemChatMessage(
                "You are an expert prompt engineer. Based on the current prompt and selected refinements, create an improved version. " +
                "Apply all the selected refinements while maintaining the core intent and functionality. " +
                "The refined prompt should be detailed, well-structured, and production-ready. " +
                "\n\nReturn ONLY the refined prompt text itself, no markdown, no explanations, no extra formatting." +
                SafetyClause
            );

            var refinementsText = string.Join("\n", selectedRefinements.Select((r, i) => $"{i + 1}. {r}"));
            var contextPart = !string.IsNullOrEmpty(additionalContext)
                ? $"\n\nAdditional Refinement Instructions: {additionalContext}"
                : "";

            var userMessage = new UserChatMessage(
                $"Current Prompt:\n{currentPrompt}\n\n" +
                $"Apply these refinements:\n{refinementsText}{contextPart}\n\n" +
                $"Generate the refined prompt that incorporates all selected improvements."
            );

            var messages = new List<ChatMessage> { systemMessage, userMessage };
            var response = await _chatClient.CompleteChatAsync(messages);

            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refining prompt");
            throw;
        }
    }

    public async Task<string> GeneratePromptWithPersonaAsync(string intent, List<string> selectedGoals, string? additionalContext, string? personaPrompt)
    {
        try
        {
            var personaContext = string.IsNullOrEmpty(personaPrompt) ? "" : $"\n\nPersona Context: {personaPrompt}";
            var systemMessage = new SystemChatMessage(
                "You are an expert prompt engineer. Based on the user's intent and selected goals, create a single, comprehensive, " +
                "production-ready prompt that accomplishes all the selected objectives. " +
                "The prompt should be detailed, well-structured, and ready to use. " +
                "Consider the persona context when crafting the prompt to ensure it aligns with the desired AI behavior." +
                "\n\nReturn ONLY the prompt text itself, no markdown, no explanations, no extra formatting." +
                SafetyClause
            );

            var goalsText = string.Join("\n", selectedGoals.Select((g, i) => $"{i + 1}. {g}"));
            var contextPart = !string.IsNullOrEmpty(additionalContext)
                ? $"\n\nAdditional Context/Refinement: {additionalContext}"
                : "";

            var userMessage = new UserChatMessage(
                $"User's Intent: {intent}\n\n" +
                $"The prompt must accomplish these selected goals:\n{goalsText}{contextPart}{personaContext}\n\n" +
                $"Generate a detailed prompt that addresses the intent and all selected objectives, taking the persona into account."
            );

            var messages = new List<ChatMessage> { systemMessage, userMessage };
            var response = await _chatClient.CompleteChatAsync(messages);

            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating prompt with persona");
            throw;
        }
    }

    public async Task<string> QuickRefineAsync(string currentPrompt, string refinementInstruction)
    {
        try
        {
            var systemMessage = new SystemChatMessage(
                "You are an expert prompt engineer. Apply the specified refinement to improve the given prompt. " +
                "Maintain the core intent and functionality while making the requested improvement. " +
                "The refined prompt should be well-structured and production-ready. " +
                "\n\nReturn ONLY the refined prompt text itself, no markdown, no explanations, no extra formatting." +
                SafetyClause
            );

            var userMessage = new UserChatMessage(
                $"Current Prompt:\n{currentPrompt}\n\n" +
                $"Refinement Instructions: {refinementInstruction}\n\n" +
                $"Apply this refinement to the prompt while maintaining its core purpose."
            );

            var messages = new List<ChatMessage> { systemMessage, userMessage };
            var response = await _chatClient.CompleteChatAsync(messages);

            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying quick refinement");
            throw;
        }
    }

    // ==================== PERSONA GENERATION ====================

    public async Task<string> GeneratePersonaAsync(string description)
    {
        try
        {
            var systemMessage = new SystemChatMessage(
                "You are an expert at creating AI personas. Given a description, generate a complete AI persona with all fields. " +
                "Return ONLY valid JSON with these exact fields: name, systemPrompt, description, tone, expertise, constraints, icon (single emoji), category. " +
                "The systemPrompt should be a comprehensive 'Act as...' instruction that defines the persona's behavior, knowledge, and communication style. " +
                "No markdown, no explanations, just the JSON object." +
                SafetyClause
            );

            var userMessage = new UserChatMessage(
                $"Create a detailed AI persona based on this description: {description}\n\n" +
                "Generate a JSON object with: name (creative name), systemPrompt (detailed 'Act as...' instruction), " +
                "description (1-2 sentence summary), tone (e.g. formal/witty/empathetic), " +
                "expertise (comma-separated skills), constraints (what the persona should NOT do), " +
                "icon (single relevant emoji), category (e.g. Development, Writing, Marketing, Support, Education)"
            );

            var messages = new List<ChatMessage> { systemMessage, userMessage };
            var response = await _chatClient.CompleteChatAsync(messages);
            var content = response.Value.Content[0].Text;

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating persona");
            throw;
        }
    }

    // ==================== VOICE TRANSCRIPTION ====================

    public async Task<string> TranscribeAudioAsync(Stream audioStream, string fileName)
    {
        try
        {
            var result = await _audioClient.TranscribeAudioAsync(audioStream, fileName);
            return result.Value.Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transcribing audio");
            throw;
        }
    }

    public async Task<string> RefineTranscriptionToIntentAsync(string rawTranscription)
    {
        try
        {
            var systemMessage = new SystemChatMessage(
                "You are a helpful assistant that converts spoken language into a clean, structured prompt intent. " +
                "Remove filler words (um, uh, like, you know), fix grammar, and structure the request clearly. " +
                "Return ONLY the cleaned intent text, nothing else."
            );

            var userMessage = new UserChatMessage(
                $"Clean up this spoken intent into a clear prompt description:\n\n{rawTranscription}"
            );

            var messages = new List<ChatMessage> { systemMessage, userMessage };
            var response = await _chatClient.CompleteChatAsync(messages);
            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refining transcription");
            throw;
        }
    }

    // ==================== CONTENT MODERATION ====================

    public async Task<ContentModerationResult> ModerateContentAsync(string content)
    {
        try
        {
            var result = await _moderationClient.ClassifyTextAsync(content);
            var modResult = result.Value;

            var flaggedCategories = new List<string>();
            if (modResult.Hate.Flagged) flaggedCategories.Add("Hate");
            if (modResult.HateThreatening.Flagged) flaggedCategories.Add("HateThreatening");
            if (modResult.Harassment.Flagged) flaggedCategories.Add("Harassment");
            if (modResult.HarassmentThreatening.Flagged) flaggedCategories.Add("HarassmentThreatening");
            if (modResult.SelfHarm.Flagged) flaggedCategories.Add("SelfHarm");
            if (modResult.SelfHarmIntent.Flagged) flaggedCategories.Add("SelfHarmIntent");
            if (modResult.SelfHarmInstructions.Flagged) flaggedCategories.Add("SelfHarmInstructions");
            if (modResult.Sexual.Flagged) flaggedCategories.Add("Sexual");
            if (modResult.SexualMinors.Flagged) flaggedCategories.Add("SexualMinors");
            if (modResult.Violence.Flagged) flaggedCategories.Add("Violence");
            if (modResult.ViolenceGraphic.Flagged) flaggedCategories.Add("ViolenceGraphic");

            return new ContentModerationResult
            {
                IsFlagged = modResult.Flagged,
                FlaggedCategories = flaggedCategories
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Content moderation check failed, allowing request to proceed");
            // If moderation API fails, allow the request (fail-open to avoid blocking legitimate usage)
            return new ContentModerationResult { IsFlagged = false, FlaggedCategories = new List<string>() };
        }
    }

    // ==================== PERSONA-AWARE GENERATION ====================

    public async Task<List<string>> GenerateGoalSuggestionsWithPersonaAsync(string intent, string style, string category, string? personaPrompt)
    {
        try
        {
            var systemMessage = new SystemChatMessage(
                $"You are acting with this persona: {personaPrompt}\n\n" +
                "Based on the user's intent, generate 20 clear, specific goal statements. " +
                "\n\nReturn ONLY a JSON array of 20 strings, nothing else." +
                SafetyClause
            );

            var userMessage = new UserChatMessage(
                $"User's Intent: {intent}\n\nGenerate 20 different goal/objective suggestions."
            );

            var messages = new List<ChatMessage> { systemMessage, userMessage };
            var response = await _chatClient.CompleteChatAsync(messages);
            var content = response.Value.Content[0].Text;

            return System.Text.Json.JsonSerializer.Deserialize<List<string>>(content) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating persona-aware goal suggestions");
            throw;
        }
    }

    public async Task<string> GeneratePromptWithPersonaAsync(string intent, List<string> selectedGoals, string? additionalContext, string personaSystemPrompt, string? style = null)
    {
        try
        {
            var styleInstruction = GetStyleInstruction(style);

            var systemMessage = new SystemChatMessage(
                $"You are acting with this persona: {personaSystemPrompt}\n\n" +
                "Create a comprehensive, production-ready prompt that accomplishes all the selected objectives. " +
                $"\n\nIMPORTANT - Write the prompt in this style: {styleInstruction}" +
                "\n\nReturn ONLY the prompt text itself, no markdown, no explanations." +
                SafetyClause
            );

            var goalsText = string.Join("\n", selectedGoals.Select((g, i) => $"{i + 1}. {g}"));
            var contextPart = !string.IsNullOrEmpty(additionalContext) ? $"\n\nAdditional Context: {additionalContext}" : "";

            var userMessage = new UserChatMessage(
                $"User's Intent: {intent}\n\nSelected goals:\n{goalsText}{contextPart}"
            );

            var messages = new List<ChatMessage> { systemMessage, userMessage };
            var response = await _chatClient.CompleteChatAsync(messages);
            return response.Value.Content[0].Text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating persona-aware prompt");
            throw;
        }
    }

    // ── Native function / tool calling ───────────────────────────────────────

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
            throw new InvalidOperationException("OpenAI service is not available");

        var model = modelOverride ?? _defaultModel;
        var client = new ChatClient(model: model, apiKey: _apiKey);

        var options = new ChatCompletionOptions { Temperature = 0.7f };
        foreach (var t in tools)
        {
            options.Tools.Add(ChatTool.CreateFunctionTool(
                functionName: t.Name,
                functionDescription: t.Description,
                functionParameters: BinaryData.FromString(t.ParametersSchema)));
        }
        if (tools.Count > 0)
            options.ToolChoice = ChatToolChoice.CreateAutoChoice();

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new SystemChatMessage(systemPrompt));
        messages.Add(new UserChatMessage(userInput));

        var steps = new List<AgentRunStep>();
        var totalTokens = 0;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var response = await client.CompleteChatAsync(messages, options, ct);
            totalTokens += response.Value.Usage.TotalTokenCount;

            if (response.Value.FinishReason == ChatFinishReason.ToolCalls)
            {
                // Record assistant message with its tool calls in conversation history
                messages.Add(new AssistantChatMessage(response.Value));

                foreach (var toolCall in response.Value.ToolCalls)
                {
                    var argsJson = toolCall.FunctionArguments.ToString();
                    var call = new AgentToolCall(toolCall.Id, toolCall.FunctionName, argsJson);

                    var callStep = new AgentRunStep("tool_call", argsJson, ToolName: toolCall.FunctionName, ToolInput: argsJson);
                    steps.Add(callStep);
                    onStep?.Invoke(callStep);

                    string toolOutput;
                    try { toolOutput = await toolExecutor(call); }
                    catch (Exception ex) { toolOutput = $"Tool error: {ex.Message}"; }

                    var resultStep = new AgentRunStep("tool_result", toolOutput, ToolName: toolCall.FunctionName, ToolOutput: toolOutput);
                    steps.Add(resultStep);
                    onStep?.Invoke(resultStep);

                    messages.Add(new ToolChatMessage(toolCall.Id, toolOutput));
                }
                continue;
            }

            // FinishReason.Stop or anything else — treat as final answer
            var output = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : string.Empty;
            var outStep = new AgentRunStep("output", output);
            steps.Add(outStep);
            onStep?.Invoke(outStep);
            return new AgentLoopResult(output, steps, totalTokens, iteration);
        }

        var maxStep = new AgentRunStep("output", "Maximum iterations reached.");
        steps.Add(maxStep);
        onStep?.Invoke(maxStep);
        return new AgentLoopResult(maxStep.Content, steps, totalTokens, maxIterations);
    }

    private static string ExtractJson(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```")) text = text[..^3].TrimEnd();
        }
        return text.Trim();
    }
}

public class ContentModerationResult
{
    public bool IsFlagged { get; set; }
    public List<string> FlaggedCategories { get; set; } = new();
}

// DTO for persona generation result
public class GeneratedPersona
{
    public string Name { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Tone { get; set; } = string.Empty;
    public string Expertise { get; set; } = string.Empty;
    public string Constraints { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}
