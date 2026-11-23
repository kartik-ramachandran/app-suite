using PromptStudio.Api.Models;

namespace PromptStudio.Api.Services;

public record ConversationMessage(string Role, string Content);
public record GoalSuggestionsResult(List<string> Goals, string SuggestedStyle);

// ── Agent / Tool-calling types ──────────────────────────────────────────────

public record AgentToolDefinition(
    string Name,
    string Description,
    string ParametersSchema   // JSON Schema: {"type":"object","properties":{...},"required":[...]}
);

public record AgentToolCall(string CallId, string ToolName, string ArgumentsJson);

public record AgentRunStep(
    string Type,              // "thinking" | "tool_call" | "tool_result" | "output"
    string Content,
    string? ToolName = null,
    string? ToolInput = null,
    string? ToolOutput = null
);

public record AgentLoopResult(
    string FinalOutput,
    List<AgentRunStep> Steps,
    int TokensUsed,
    int IterationsUsed
);

public interface IAIService
{
    Task<PromptTestResponse> TestPromptAsync(PromptTestRequest request);
    IAsyncEnumerable<string> StreamExecuteAsync(string systemPrompt, List<ConversationMessage> messages, string? modelOverride, CancellationToken ct = default);
    Task<GoalSuggestionsResult> GenerateGoalSuggestionsAsync(string intent, string mode = "intent");
    Task<string> GeneratePromptFromGoalsAsync(string intent, List<string> selectedGoals, string? additionalContext);
    Task<List<string>> GenerateRefinementSuggestionsAsync(string currentPrompt);
    Task<string> RefinePromptAsync(string currentPrompt, List<string> selectedRefinements, string? additionalContext);
    bool IsAvailable { get; }
    string ProviderName { get; }
    List<string> SupportedModels { get; }
    // Advanced features (may not be available in all providers)
    Task<List<string>> GenerateGoalSuggestionsWithPersonaAsync(string intent, string style, string category, string? personaPrompt);
    Task<string> GeneratePromptWithPersonaAsync(string intent, List<string> selectedGoals, string? additionalContext, string? personaPrompt);
    Task<string> TranscribeAudioAsync(Stream audioStream, string fileName);
    Task<string> RefineTranscriptionToIntentAsync(string transcription);
    Task<string> QuickRefineAsync(string currentPrompt, string refinementInstruction);
    Task<string> GeneratePersonaAsync(string description);
    Task<ContentModerationResult> ModerateContentAsync(string content);

    // Native tool / function calling — used by the agent runner
    // Vision: describe what is on screen to extract context for prompt generation
    Task<string> DescribeScreenAsync(string base64Image);

    Task<AgentLoopResult> RunWithToolsAsync(
        string systemPrompt,
        string userInput,
        List<AgentToolDefinition> tools,
        Func<AgentToolCall, Task<string>> toolExecutor,
        Action<AgentRunStep>? onStep = null,
        int maxIterations = 5,
        string? modelOverride = null,
        CancellationToken ct = default);
}