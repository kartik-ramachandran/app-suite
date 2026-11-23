namespace Research.Api.Models;

public class Agent
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public string? Model { get; set; }
    public string EnabledTools { get; set; } = "[]";         // JSON array: ["web_search","http_request"]
    public string ToolPermissions { get; set; } = "{}";      // JSON: {"web_search":"always_allow","http_request":"deny"}
    public string InputSchemaJson { get; set; } = "[]";
    public string MemoryConfigJson { get; set; } = "{}";
    public string GuardrailsJson { get; set; } = "{}";
    public string OutputSchemaJson { get; set; } = "{}";
    public string KnowledgeSourcesJson { get; set; } = "[]";
    public string IntegrationActionsJson { get; set; } = "[]";
    public string ModelRoutingJson { get; set; } = "{}";
    public string BenchmarkConfigJson { get; set; } = "{}";
    public string GovernanceJson { get; set; } = "{}";
    public string? SourcePromptId { get; set; }
    public int? SourcePersonaId { get; set; }
    public string FallbackBehavior { get; set; } = string.Empty;
    public string EscalationRules { get; set; } = string.Empty;
    public float Temperature { get; set; } = 0.7f;
    public int MaxIterations { get; set; } = 5;
    public int Version { get; set; } = 1;
    public bool IsTemplate { get; set; }
    public string? TemplateCategory { get; set; }
    public bool IsPublic { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public User? User { get; set; }
}
