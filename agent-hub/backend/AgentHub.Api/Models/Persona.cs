namespace AgentHub.Api.Models;

public class Persona
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string SystemPrompt { get; set; }
    public string? Description { get; set; }
    public string? Tone { get; set; }
    public string? Expertise { get; set; }
    public string? Constraints { get; set; }
    public string? Icon { get; set; }
    public string? Category { get; set; }
    
    // Enhanced Character Building (Option 2)
    public string? ExampleResponses { get; set; } // JSON array of example responses
    public string? PersonalityTraits { get; set; } // Comma-separated: humorous, patient, direct
    public string? CommunicationStyle { get; set; } // bullet-points, paragraphs, code-first, conversational
    public string? IndustryFocus { get; set; } // Legal, Finance, Healthcare, Tech, etc.
    public string? TargetAudience { get; set; } // Developers, Business Users, Support Teams
    
    // Analytics & Templates (Option 3)
    public bool IsTemplate { get; set; } = false; // Pre-built template personas
    public string? TemplateType { get; set; } // developer-bot, support-agent, creative-writer
    public int Version { get; set; } = 1; // Persona versioning
    public int? ParentPersonaId { get; set; } // For versioning/forking
    
    // Existing fields
    public bool IsPublic { get; set; } = false;
    public int UsageCount { get; set; } = 0;
    public int LikeCount { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public string UserId { get; set; } = string.Empty;
    public User? User { get; set; }
}
