// The same IAIService implementation as AzureOpenAIService.cs, running on a local Ollama model (no API key).
using OpenAI;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Chat;
using BookReader.Api.Models;
using System.Runtime.CompilerServices;

namespace BookReader.Api.Services;

public class OllamaService : IAIService
{
    private readonly OpenAIClient? _client;
    private readonly string _deploymentName;
    private readonly ILogger<OllamaService> _logger;

    public bool IsAvailable => _client != null;
    public string ProviderName => "Ollama";
    public List<string> SupportedModels => new() { _deploymentName };

    public OllamaService(IConfiguration configuration, ILogger<OllamaService> logger)
    {
        _logger = logger;
        _deploymentName = AiChatClients.OllamaModel(configuration);

        if (!AiChatClients.IsOllamaEnabled(configuration))
        {
            _client = null;
            _logger.LogInformation("Ollama not enabled — set Ollama:Enabled=true to use a local model.");
            return;
        }

        _client = AiChatClients.OllamaClient(configuration);
        _logger.LogInformation("Ollama initialised with local model: {Model}", _deploymentName);
    }

    private ChatClient Chat(string? deploymentOverride = null)
        => _client!.GetChatClient(_deploymentName);

    // ── Core: chat completion ────────────────────────────────────────────────

    public async Task<PromptTestResponse> TestPromptAsync(PromptTestRequest request)
    {
        if (!IsAvailable) throw new InvalidOperationException("Ollama is not enabled.");

        var msgs = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(request.PromptContent))
            msgs.Add(new SystemChatMessage(request.PromptContent));
        msgs.Add(new UserChatMessage(request.UserInput ?? string.Empty));

        var resp = await Chat().CompleteChatAsync(msgs);
        return new PromptTestResponse
        {
            Result = resp.Value.Content[0].Text,
            Model = _deploymentName,
            TokensUsed = resp.Value.Usage.TotalTokenCount
        };
    }

    public async IAsyncEnumerable<string> StreamExecuteAsync(
        string systemPrompt,
        List<ConversationMessage> messages,
        string? modelOverride,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("Ollama is not enabled.");

        var chatMessages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            chatMessages.Add(new SystemChatMessage(systemPrompt));
        foreach (var m in messages)
            chatMessages.Add(m.Role == "user"
                ? (ChatMessage)new UserChatMessage(m.Content)
                : new AssistantChatMessage(m.Content));

        await foreach (var update in Chat(modelOverride).CompleteChatStreamingAsync(chatMessages, cancellationToken: ct))
            foreach (var part in update.ContentUpdate)
                yield return part.Text;
    }

    // ── Agent tool-calling loop ──────────────────────────────────────────────

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
        if (!IsAvailable) throw new InvalidOperationException("Ollama is not enabled.");

        var chatClient = Chat(modelOverride);
        var options = AzureChatOptions.For(modelOverride ?? _deploymentName, temperature: 0.7f);

        foreach (var t in tools)
            options.Tools.Add(ChatTool.CreateFunctionTool(
                functionName: t.Name,
                functionDescription: t.Description,
                functionParameters: BinaryData.FromString(t.ParametersSchema)));

        if (tools.Count > 0)
            options.ToolChoice = ChatToolChoice.CreateAutoChoice();

        var chatMessages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            chatMessages.Add(new SystemChatMessage(systemPrompt));
        chatMessages.Add(new UserChatMessage(userInput));

        var steps = new List<AgentRunStep>();
        var totalTokens = 0;

        for (var iteration = 1; iteration <= maxIterations; iteration++)
        {
            var response = await chatClient.CompleteChatAsync(chatMessages, options, ct);
            totalTokens += response.Value.Usage.TotalTokenCount;

            if (response.Value.FinishReason == ChatFinishReason.ToolCalls)
            {
                chatMessages.Add(new AssistantChatMessage(response.Value));

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

                    chatMessages.Add(new ToolChatMessage(toolCall.Id, toolOutput));
                }
                continue;
            }

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

    // ── Prompt engineering helpers ──────────────────────────────────────────

    public async Task<GoalSuggestionsResult> GenerateGoalSuggestionsAsync(string intent, string mode = "intent")
    {
        var isIntentMode = mode == "intent";
        var systemText = isIntentMode
            ? "You are an expert AI assistant helping users clarify what they want an AI prompt to do. " +
              "Based on the user's description, generate 20 clear intent statements — each describing a specific PURPOSE or USE CASE for the prompt. " +
              "Each intent should start with an action verb and describe what the AI should do. " +
              "Also suggest the best prompt style.\n\nReturn ONLY a JSON object:\n- \"goals\": array of 20 intent strings\n- \"suggestedStyle\": one of: professional, creative, technical, conversational, persuasive\n\nNo markdown, no explanations, just the raw JSON object."
            : "You are an expert AI assistant helping users define what they want to achieve. " +
              "Based on the user's intent, generate 20 clear, specific goal statements AND suggest the best prompt style.\n\nReturn ONLY a JSON object:\n- \"goals\": array of 20 goal strings\n- \"suggestedStyle\": one of: professional, creative, technical, conversational, persuasive\n\nNo markdown, no explanations, just the raw JSON object.";

        var msgs = new List<ChatMessage> { new SystemChatMessage(systemText), new UserChatMessage(intent) };
        var resp = await Chat().CompleteChatAsync(msgs);
        var raw = resp.Value.Content[0].Text;
        var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(ExtractJson(raw));
        var goals = System.Text.Json.JsonSerializer.Deserialize<List<string>>(parsed.GetProperty("goals").GetRawText()) ?? [];
        var style = parsed.TryGetProperty("suggestedStyle", out var se) ? se.GetString() ?? "professional" : "professional";
        return new GoalSuggestionsResult(goals, style);
    }

    public async Task<string> GeneratePromptFromGoalsAsync(string intent, List<string> selectedGoals, string? additionalContext)
    {
        var goalsText = string.Join("\n", selectedGoals.Select((g, i) => $"{i + 1}. {g}"));
        var ctx = string.IsNullOrEmpty(additionalContext) ? "" : $"\n\nAdditional Context: {additionalContext}";
        var msgs = new List<ChatMessage>
        {
            new SystemChatMessage("You are an expert prompt engineer. Create a single, comprehensive, production-ready prompt that accomplishes all the selected objectives. Return ONLY the prompt text itself, no markdown, no explanations."),
            new UserChatMessage($"Intent: {intent}\n\nGoals:\n{goalsText}{ctx}\n\nGenerate a detailed prompt addressing the intent and all objectives.")
        };
        var resp = await Chat().CompleteChatAsync(msgs);
        return resp.Value.Content[0].Text;
    }

    public async Task<List<string>> GenerateRefinementSuggestionsAsync(string currentPrompt)
    {
        var msgs = new List<ChatMessage>
        {
            new SystemChatMessage("You are an expert prompt engineer. Generate 20 specific refinement suggestions for the given prompt. Return ONLY a JSON array of 20 strings, no markdown, no explanations."),
            new UserChatMessage($"Current Prompt:\n{currentPrompt}\n\nGenerate 20 refinement suggestions.")
        };
        var resp = await Chat().CompleteChatAsync(msgs);
        return System.Text.Json.JsonSerializer.Deserialize<List<string>>(ExtractJson(resp.Value.Content[0].Text)) ?? [];
    }

    public async Task<string> RefinePromptAsync(string currentPrompt, List<string> selectedRefinements, string? additionalContext)
    {
        var refinementsText = string.Join("\n", selectedRefinements.Select((r, i) => $"{i + 1}. {r}"));
        var ctx = string.IsNullOrEmpty(additionalContext) ? "" : $"\n\nAdditional Instructions: {additionalContext}";
        var msgs = new List<ChatMessage>
        {
            new SystemChatMessage("You are an expert prompt engineer. Apply the selected refinements to improve the prompt. Return ONLY the refined prompt text, no markdown, no explanations."),
            new UserChatMessage($"Current Prompt:\n{currentPrompt}\n\nRefinements:\n{refinementsText}{ctx}")
        };
        var resp = await Chat().CompleteChatAsync(msgs);
        return resp.Value.Content[0].Text;
    }

    public async Task<List<string>> GenerateGoalSuggestionsWithPersonaAsync(string intent, string style, string category, string? personaPrompt)
    {
        var personaCtx = string.IsNullOrEmpty(personaPrompt) ? "" : $"\n\nPersona: {personaPrompt}";
        var msgs = new List<ChatMessage>
        {
            new SystemChatMessage("You are an expert AI assistant. Generate 20 specific goal suggestions for the given intent and persona. Return ONLY a JSON array of 20 strings, no markdown."),
            new UserChatMessage($"Intent: {intent}\nStyle: {style}\nCategory: {category}{personaCtx}")
        };
        var resp = await Chat().CompleteChatAsync(msgs);
        return System.Text.Json.JsonSerializer.Deserialize<List<string>>(ExtractJson(resp.Value.Content[0].Text)) ?? [];
    }

    public async Task<string> GeneratePromptWithPersonaAsync(string intent, List<string> selectedGoals, string? additionalContext, string? personaPrompt)
    {
        var goalsText = string.Join("\n", selectedGoals.Select((g, i) => $"{i + 1}. {g}"));
        var ctx = string.IsNullOrEmpty(additionalContext) ? "" : $"\n\nAdditional Context: {additionalContext}";
        var personaCtx = string.IsNullOrEmpty(personaPrompt) ? "" : $"\n\nPersona: {personaPrompt}";
        var msgs = new List<ChatMessage>
        {
            new SystemChatMessage("You are an expert prompt engineer. Create a production-ready prompt for the intent and goals, taking the persona into account. Return ONLY the prompt text."),
            new UserChatMessage($"Intent: {intent}\n\nGoals:\n{goalsText}{ctx}{personaCtx}")
        };
        var resp = await Chat().CompleteChatAsync(msgs);
        return resp.Value.Content[0].Text;
    }

    public async Task<string> QuickRefineAsync(string currentPrompt, string refinementInstruction)
    {
        var msgs = new List<ChatMessage>
        {
            new SystemChatMessage("You are an expert prompt engineer. Apply the refinement instruction to improve the prompt. Return ONLY the refined prompt text."),
            new UserChatMessage($"Prompt:\n{currentPrompt}\n\nRefinement: {refinementInstruction}")
        };
        var resp = await Chat().CompleteChatAsync(msgs);
        return resp.Value.Content[0].Text;
    }

    public async Task<string> GeneratePersonaAsync(string description)
    {
        var msgs = new List<ChatMessage>
        {
            new SystemChatMessage("You are an expert at creating AI personas. Generate a detailed system prompt for the described persona. Return ONLY the system prompt text."),
            new UserChatMessage(description)
        };
        var resp = await Chat().CompleteChatAsync(msgs);
        return resp.Value.Content[0].Text;
    }

    public Task<string> DescribeScreenAsync(string base64Image)
        => Task.FromResult(string.Empty);

    public Task<string> TranscribeAudioAsync(Stream audioStream, string fileName)
        => throw new NotSupportedException("Audio transcription is not supported via Ollama in this implementation.");

    public Task<string> RefineTranscriptionToIntentAsync(string transcription)
        => Task.FromResult(transcription);

    public Task<ContentModerationResult> ModerateContentAsync(string content)
        => Task.FromResult(new ContentModerationResult { IsFlagged = false });

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
