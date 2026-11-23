namespace KitBuilder.Api.Models;

public class ContextDocument
{
    public string Content { get; set; } = string.Empty;
    public string? Source { get; set; }
}

public class PromptTestRequest
{
    public required string PromptContent { get; set; }
    public string? UserInput { get; set; }
    public List<ContextDocument>? ContextDocuments { get; set; }

    /// <summary>Run on a specific model instead of the provider default.</summary>
    public string? Model { get; set; }

    /// <summary>Validate the response against this JSON Schema before returning it.</summary>
    public string? OutputSchema { get; set; }
}

public class PromptTestResponse
{
    public required string Result { get; set; }
    public required string Model { get; set; }
    public int TokensUsed { get; set; }

    public decimal EstimatedCostUsd { get; set; }
    public long LatencyMs { get; set; }

    /// <summary>Set when the model that answered was not the one first asked.</summary>
    public string? FellBackFrom { get; set; }

    /// <summary>Null when no schema was in play.</summary>
    public bool? SchemaValid { get; set; }
    public List<string>? SchemaErrors { get; set; }
}

// ── Multi-model comparison ──────────────────────────────────────────────────

public class ComparePromptsRequest
{
    public required string PromptContent { get; set; }
    public string? UserInput { get; set; }
    public List<string> Models { get; set; } = new();
    public List<ContextDocument>? ContextDocuments { get; set; }

    /// <summary>Have a model grade each output 1-10. Doubles the calls, so opt-in.</summary>
    public bool ScoreOutputs { get; set; } = false;

    /// <summary>What "good" means for this comparison. Defaults to general quality.</summary>
    public string? ScoringCriteria { get; set; }

    /// <summary>Attribute the comparison to a prompt, so it shows up in that prompt's traces.</summary>
    public string? PromptId { get; set; }
    public string? PromptName { get; set; }
}

public class ModelComparisonResult
{
    public required string Model { get; set; }
    public string? Result { get; set; }
    public string? Error { get; set; }
    public int TokensUsed { get; set; }
    public long LatencyMs { get; set; }
    public decimal EstimatedCostUsd { get; set; }

    /// <summary>True when the model is not in the price table and cost is a guess.</summary>
    public bool CostIsEstimate { get; set; }

    public string? Provider { get; set; }

    /// <summary>1-10 quality score, when scoring was requested.</summary>
    public int? Score { get; set; }
}

public class ComparePromptsResponse
{
    public List<ModelComparisonResult> Results { get; set; } = new();

    /// <summary>Cheapest model that was good enough — the answer the grid exists to give.</summary>
    public string? RecommendedModel { get; set; }
    public string? RecommendationReason { get; set; }
}

// ── Model / schema / lint settings ──────────────────────────────────────────

public class UpdateModelSettingsRequest
{
    public string? PreferredModel { get; set; }
    public List<string>? FallbackModels { get; set; }
    public bool CostOptimalRouting { get; set; }
    public int RoutingMinScore { get; set; } = 8;
}

public class UpdateSchemaRequest
{
    public string? OutputSchema { get; set; }
    public bool EnforceSchema { get; set; }
}

public class ValidateSchemaRequest
{
    public string? Output { get; set; }
    public string? Schema { get; set; }
}

public class LintPromptRequest
{
    public string Content { get; set; } = string.Empty;

    /// <summary>Variable names that will be supplied at run time, to catch unbound ones.</summary>
    public List<string>? Variables { get; set; }

    /// <summary>Add an LLM review pass on top of the static checks.</summary>
    public bool UseAi { get; set; } = false;
}

public class RunPromptRequest
{
    public string? UserInput { get; set; }

    /// <summary>Force a model for this one call, ignoring the prompt's preference.</summary>
    public string? ModelOverride { get; set; }
}

// ── Ratings loop ────────────────────────────────────────────────────────────

public class SuggestImprovementRequest
{
    /// <summary>How many recent downvoted executions to learn from.</summary>
    public int SampleSize { get; set; } = 5;

    /// <summary>Extra steer for the rewrite, e.g. "keep it under 100 words".</summary>
    public string? AdditionalContext { get; set; }
}

public class ApplyImprovementRequest
{
    public required string Content { get; set; }
    public string? ChangeNote { get; set; }
}

// ── Conversation import ─────────────────────────────────────────────────────

public class ImportConversationsRequest
{
    /// <summary>The raw contents of a ChatGPT or Claude conversations.json export.</summary>
    public string RawJson { get; set; } = string.Empty;

    public int MaxCandidates { get; set; } = 50;
}

public class SaveImportedPromptsRequest
{
    public List<ImportedPromptDto>? Prompts { get; set; }
}

public class ImportedPromptDto
{
    public string? Name { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Description { get; set; }
}


