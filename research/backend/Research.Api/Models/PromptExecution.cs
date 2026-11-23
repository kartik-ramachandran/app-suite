using System.ComponentModel.DataAnnotations;

namespace Research.Api.Models;

public class PromptExecution
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string? PromptId { get; set; }

    [Required]
    public string UserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string PromptName { get; set; } = string.Empty;

    [Required]
    public string SystemPrompt { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Model { get; set; } = "gpt-4o-mini";

    [Required]
    public string MessagesJson { get; set; } = "[]";

    public int TotalTokensUsed { get; set; }

    // Thumbs up/down rating: 1 = up, -1 = down, 0 = unrated
    public int Rating { get; set; } = 0;

    [MaxLength(1000)]
    public string? RatingNote { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Prompt? Prompt { get; set; }
    public User? User { get; set; }
}
