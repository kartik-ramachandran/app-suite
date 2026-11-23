using System.Text.Json;
using ContentStudio.Api.Models;

namespace ContentStudio.Api.Services;

/// <summary>
/// Service to manage prompt files - writes prompts to disk for efficient AI calls
/// </summary>
public class PromptFileService
{
    private readonly string _promptsDirectory;
    private readonly ILogger<PromptFileService> _logger;

    public PromptFileService(IConfiguration configuration, ILogger<PromptFileService> logger)
    {
        _promptsDirectory = configuration["PromptsDirectory"] ?? Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "prompts");
        _logger = logger;
        
        // Ensure prompts directory exists
        Directory.CreateDirectory(_promptsDirectory);
    }

    /// <summary>
    /// Saves a prompt to a file for caching
    /// </summary>
    public async Task SavePromptToFileAsync(Prompt prompt)
    {
        try
        {
            var fileName = GetPromptFileName(prompt.Id, prompt.Name);
            var filePath = Path.Combine(_promptsDirectory, fileName);

            var promptData = new
            {
                prompt.Id,
                prompt.Name,
                prompt.Content,
                prompt.Category,
                prompt.Description,
                prompt.UpdatedAt
            };

            var json = JsonSerializer.Serialize(promptData, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(filePath, json);
            
            _logger.LogInformation("Saved prompt {PromptId} to file {FileName}", prompt.Id, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving prompt {PromptId} to file", prompt.Id);
            throw;
        }
    }

    /// <summary>
    /// Loads a prompt from file
    /// </summary>
    public async Task<string?> LoadPromptContentFromFileAsync(string promptId, string promptName)
    {
        try
        {
            var fileName = GetPromptFileName(promptId, promptName);
            var filePath = Path.Combine(_promptsDirectory, fileName);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Prompt file {FileName} not found", fileName);
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var promptData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            
            return promptData?["Content"].GetString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading prompt {PromptId} from file", promptId);
            return null;
        }
    }

    /// <summary>
    /// Deletes a prompt file
    /// </summary>
    public void DeletePromptFile(string promptId, string promptName)
    {
        try
        {
            var fileName = GetPromptFileName(promptId, promptName);
            var filePath = Path.Combine(_promptsDirectory, fileName);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogInformation("Deleted prompt file {FileName}", fileName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting prompt file for {PromptId}", promptId);
        }
    }

    /// <summary>
    /// Loads all prompts from files on app startup
    /// </summary>
    public async Task<Dictionary<string, string>> LoadAllPromptsFromFilesAsync()
    {
        var prompts = new Dictionary<string, string>();

        try
        {
            var files = Directory.GetFiles(_promptsDirectory, "prompt_*.json");
            
            foreach (var filePath in files)
            {
                var json = await File.ReadAllTextAsync(filePath);
                var promptData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
                
                if (promptData != null && promptData.ContainsKey("Id") && promptData.ContainsKey("Content"))
                {
                    var id = promptData["Id"].GetString();
                    var content = promptData["Content"].GetString();
                    if (content != null && id != null)
                    {
                        prompts[id] = content;
                    }
                }
            }

            _logger.LogInformation("Loaded {Count} prompts from files", prompts.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading prompts from files");
        }

        return prompts;
    }

    private static string GetPromptFileName(string id, string name)
    {
        // Create a safe filename from the prompt name
        var safeName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        return $"prompt_{id}_{safeName}.json";
    }
}
