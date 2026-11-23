using System.ComponentModel.DataAnnotations;

namespace PromptStudio.Api.Models;

public enum EvalStatus { Pending, Running, Completed, Failed }

public class PromptEvaluation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string PromptA { get; set; } = string.Empty;

    [Required]
    public string PromptB { get; set; } = string.Empty;

    public string? TestInput { get; set; }

    public string? ResultA { get; set; }

    public string? ResultB { get; set; }

    [MaxLength(50)]
    public string Model { get; set; } = "gpt-4o-mini";

    public int TokensA { get; set; }
    public int TokensB { get; set; }
    public long LatencyAMs { get; set; }
    public long LatencyBMs { get; set; }

    // "A", "B", "Tie", or null (not voted yet)
    [MaxLength(10)]
    public string? Winner { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public EvalStatus Status { get; set; } = EvalStatus.Pending;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public User? User { get; set; }
}
