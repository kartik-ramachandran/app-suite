namespace ContentStudio.Api.Models;

public class ChainNode
{
    public int Id { get; set; }
    public Guid ChainId { get; set; }
    public PromptChain? Chain { get; set; }
    public required string NodeType { get; set; }
    public string? Label { get; set; }
    public string? PromptId { get; set; }
    public Prompt? Prompt { get; set; }
    public string? InlinePrompt { get; set; }
    public int? PersonaId { get; set; }
    public string? ConditionExpression { get; set; }
    public string? TransformExpression { get; set; }
    // Agent
    public string? SystemPrompt { get; set; }
    // Classify
    public string? ClassifyLabels { get; set; }
    // Switch
    public string? SwitchExpression { get; set; }
    // Loop
    public string? LoopCondition { get; set; }
    public int LoopMax { get; set; } = 5;
    // HTTP Request
    public string? HttpUrl { get; set; }
    public string? HttpMethod { get; set; }
    public string? HttpBody { get; set; }
    // Code
    public string? CodeSnippet { get; set; }
    // Set Variable
    public string? VariableName { get; set; }
    public string? VariableValue { get; set; }
    // Format
    public string? FormatTemplate { get; set; }
    // Filter
    public string? FilterExpression { get; set; }
    // Delay
    public int DelaySeconds { get; set; } = 1;
    // Note
    public string? NoteText { get; set; }
    // Merge
    public string? MergeStrategy { get; set; }
    public string? MergeSeparator { get; set; }
    // File Search
    public string? FileSearchQuery { get; set; }
    public string? FileSearchTypes { get; set; }
    public int? FileSearchMaxResults { get; set; }
    // MCP
    public string? McpServerUrl { get; set; }
    public string? McpToolName { get; set; }
    public string? McpArguments { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public int SortOrder { get; set; }
}
