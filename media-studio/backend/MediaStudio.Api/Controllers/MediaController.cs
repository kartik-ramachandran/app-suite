using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using MediaStudio.Api.Services;
using System.Text;

namespace MediaStudio.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("media")]
public class MediaController : ControllerBase
{
    private readonly IAIService _aiService;
    private readonly ILogger<MediaController> _logger;
    private readonly IConfiguration _configuration;

    public MediaController(
        IAIService aiService,
        ILogger<MediaController> logger,
        IConfiguration configuration)
    {
        _aiService = aiService;
        _logger = logger;
        _configuration = configuration;
    }

    [HttpPost("image-prompt")]
    public async Task<IActionResult> GenerateImagePrompt([FromBody] ImagePromptRequest request)
    {
        try
        {
            var systemPrompt = GetImagePromptSystemPrompt(request.Platform);
            var userPrompt = BuildImagePromptUserMessage(request);

            // Use QuickRefineAsync with system prompt as refinement instruction
            var generatedPrompt = await _aiService.QuickRefineAsync(userPrompt, systemPrompt);

            return Ok(new { prompt = generatedPrompt });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate image prompt");
            return StatusCode(500, new { error = "Failed to generate image prompt" });
        }
    }

    [HttpPost("video-script")]
    public async Task<IActionResult> GenerateVideoScript([FromBody] VideoScriptRequest request)
    {
        try
        {
            var systemPrompt = GetVideoScriptSystemPrompt(request.VideoType);
            var userPrompt = BuildVideoScriptUserMessage(request);

            var script = await _aiService.QuickRefineAsync(userPrompt, systemPrompt);

            return Ok(new { script });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate video script");
            return StatusCode(500, new { error = "Failed to generate video script" });
        }
    }

    [HttpPost("voice-to-prompt")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(10_000_000)] // 10MB limit
    public async Task<IActionResult> VoiceToPrompt(IFormFile audio)
    {
        if (audio == null || audio.Length == 0)
        {
            return BadRequest(new { error = "No audio file provided" });
        }

        try
        {
            // Transcribe audio using OpenAI Whisper
            var transcript = await TranscribeAudio(audio);

            // Enhance transcript into a prompt
            var enhancedPrompt = await EnhanceTranscriptToPrompt(transcript);

            return Ok(new
            {
                transcript,
                enhancedPrompt
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process voice recording");
            return StatusCode(500, new { error = "Failed to process voice recording" });
        }
    }

    [HttpPost("enhance-transcript")]
    public async Task<IActionResult> EnhanceTranscript([FromBody] EnhanceTranscriptRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Transcript))
            return BadRequest(new { error = "Transcript is required" });

        try
        {
            var systemPrompt = BuildEnhanceTranscriptSystemPrompt(request.SelectedGoals);
            var userMessage = BuildEnhanceTranscriptUserMessage(request);
            var enhancedPrompt = await _aiService.QuickRefineAsync(userMessage, systemPrompt);
            return Ok(new { enhancedPrompt });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enhance transcript");
            return StatusCode(500, new { error = "Failed to enhance transcript" });
        }
    }

    private string BuildEnhanceTranscriptSystemPrompt(List<string>? goals)
    {
        var sb = new StringBuilder();
        sb.AppendLine(@"You are an expert at converting voice recordings into well-structured, professional prompts.

Transform the user's speech into a clear, actionable prompt that:
- Captures the core intent and requirements
- Structures information logically
- Removes filler words and verbal tics
- Adds necessary context and clarity
- Formats it professionally

Keep the user's original meaning but make it more polished and effective.");

        if (goals?.Count > 0)
        {
            sb.AppendLine("\nAdditionally, ensure the final prompt incorporates these enhancement goals:");
            foreach (var goal in goals)
                sb.AppendLine($"- {goal}");
        }

        return sb.ToString();
    }

    private string BuildEnhanceTranscriptUserMessage(EnhanceTranscriptRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Transform this voice transcript into a well-structured prompt:\n\n{request.Transcript}");

        if (!string.IsNullOrWhiteSpace(request.AdditionalContext))
            sb.AppendLine($"\nAdditional context: {request.AdditionalContext}");

        return sb.ToString();
    }

    private string GetImagePromptSystemPrompt(string platform)
    {
        return platform.ToLower() switch
        {
            "dalle3" => @"You are an expert at creating optimized prompts for ChatGPT / OpenAI image generation (GPT Image).
Generate a clear, detailed prompt that follows OpenAI image-generation best practices:
- Be descriptive and specific
- Include style, lighting, composition, and mood
- Use natural language
- Avoid overly technical terms
- Keep it under 400 characters
- Don't use negative prompts (OpenAI image generation doesn't support them)

Return ONLY the optimized prompt, nothing else.",

            "midjourney" => @"You are an expert at creating optimized prompts for Midjourney.
Generate a structured prompt that follows Midjourney best practices:
- Start with subject description
- Add style and mood descriptors
- Include camera/composition details
- Mention lighting and atmosphere
- Add quality parameters (--ar for aspect ratio, --style, --quality)
- Use comma-separated keywords
- Be concise but descriptive

Return ONLY the optimized prompt with parameters, nothing else.",

            "stable-diffusion" => @"You are an expert at creating optimized prompts for Stable Diffusion.
Generate a detailed prompt that follows Stable Diffusion best practices:
- Use comma-separated tags and descriptors
- Include artist styles and techniques
- Add quality boosters (highly detailed, 8k, photorealistic, masterpiece)
- Mention lighting and composition
- Include negative prompt suggestions in brackets at the end
- Be specific and detailed

Return ONLY the optimized prompt, nothing else.",

            _ => "Generate an optimized image generation prompt based on the user's input."
        };
    }

    private string BuildImagePromptUserMessage(ImagePromptRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Subject: {request.Subject}");
        sb.AppendLine($"Style: {request.Style}");
        sb.AppendLine($"Aspect Ratio: {request.AspectRatio}");

        if (!string.IsNullOrWhiteSpace(request.Lighting))
            sb.AppendLine($"Lighting: {request.Lighting}");

        if (!string.IsNullOrWhiteSpace(request.CameraAngle))
            sb.AppendLine($"Camera Angle: {request.CameraAngle}");

        if (!string.IsNullOrWhiteSpace(request.Mood))
            sb.AppendLine($"Mood: {request.Mood}");

        if (!string.IsNullOrWhiteSpace(request.Details))
            sb.AppendLine($"Additional Details: {request.Details}");

        if (request.SelectedGoals?.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Style & Composition Goals (must be incorporated):");
            foreach (var goal in request.SelectedGoals)
                sb.AppendLine($"- {goal}");
        }

        if (!string.IsNullOrWhiteSpace(request.AdditionalContext))
            sb.AppendLine($"Extra Context: {request.AdditionalContext}");

        sb.AppendLine();
        sb.AppendLine($"Create an optimized {request.Platform.ToUpper()} prompt from the above details.");

        return sb.ToString();
    }

    private string GetVideoScriptSystemPrompt(string videoType)
    {
        var basePrompt = @"You are an expert video script writer. Create a professional, engaging script that:
- Has a clear structure (Hook, Introduction, Body, Conclusion, Call-to-Action)
- Uses conversational, engaging language
- Includes timing markers [XX:XX]
- Suggests visual cues in [VISUAL: description]
- Keeps voiceover natural and easy to follow
- Matches the specified tone and target audience";

        var typeSpecific = videoType.ToLower() switch
        {
            "explainer" => "\n- Focus on clarity and simplicity\n- Break down complex concepts\n- Use examples and analogies",
            "tutorial" => "\n- Provide step-by-step instructions\n- Include tips and best practices\n- Anticipate common mistakes",
            "advertisement" => "\n- Lead with benefits, not features\n- Create urgency and desire\n- Strong call-to-action",
            "documentary" => "\n- Tell a compelling story\n- Use facts and data\n- Maintain narrative flow",
            "social-media" => "\n- Hook viewers in first 3 seconds\n- Keep it short and punchy\n- Optimize for mobile viewing",
            "testimonial" => "\n- Focus on authentic storytelling\n- Show transformation/results\n- Build trust and credibility",
            _ => ""
        };

        return basePrompt + typeSpecific;
    }

    private string BuildVideoScriptUserMessage(VideoScriptRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Video Topic: {request.Topic}");
        sb.AppendLine($"Video Type: {request.VideoType}");
        sb.AppendLine($"Target Duration: {request.Duration} seconds");
        sb.AppendLine($"Tone: {request.Tone}");

        if (!string.IsNullOrWhiteSpace(request.TargetAudience))
            sb.AppendLine($"Target Audience: {request.TargetAudience}");

        if (!string.IsNullOrWhiteSpace(request.KeyPoints))
        {
            sb.AppendLine("\nKey Points to Cover:");
            sb.AppendLine(request.KeyPoints);
        }

        if (request.SelectedGoals?.Count > 0)
        {
            sb.AppendLine("\nScript Goals (must be reflected in the script):");
            foreach (var goal in request.SelectedGoals)
                sb.AppendLine($"- {goal}");
        }

        if (!string.IsNullOrWhiteSpace(request.AdditionalContext))
            sb.AppendLine($"\nExtra Context: {request.AdditionalContext}");

        sb.AppendLine("\nCreate a complete video script based on the above requirements.");

        return sb.ToString();
    }

    private async Task<string> TranscribeAudio(IFormFile audio)
    {
        var openAIKey = _configuration["OpenAI:ApiKey"];
        if (string.IsNullOrEmpty(openAIKey))
        {
            throw new InvalidOperationException("OpenAI API key not configured");
        }

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {openAIKey}");

        using var content = new MultipartFormDataContent();
        using var audioStream = audio.OpenReadStream();
        using var streamContent = new StreamContent(audioStream);
        
        content.Add(streamContent, "file", audio.FileName);
        content.Add(new StringContent("whisper-1"), "model");

        var response = await httpClient.PostAsync("https://api.openai.com/v1/audio/transcriptions", content);
        
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            _logger.LogError($"Whisper API error: {error}");
            throw new Exception("Failed to transcribe audio");
        }

        var result = await response.Content.ReadAsStringAsync();
        var json = System.Text.Json.JsonDocument.Parse(result);
        return json.RootElement.GetProperty("text").GetString() ?? "";
    }

    private async Task<string> EnhanceTranscriptToPrompt(string transcript)
    {
        var systemPrompt = @"You are an expert at converting voice recordings into well-structured, professional prompts.

Transform the user's speech into a clear, actionable prompt that:
- Captures the core intent and requirements
- Structures information logically
- Removes filler words and verbal tics
- Adds necessary context and clarity
- Formats it professionally

Keep the user's original meaning but make it more polished and effective.";

        var userMessage = $"Transform this voice transcript into a well-structured prompt:\n\n{transcript}";

        return await _aiService.QuickRefineAsync(userMessage, systemPrompt);
    }
}

public class ImagePromptRequest
{
    public string Platform { get; set; } = "dalle3";
    public string Subject { get; set; } = "";
    public string Style { get; set; } = "photorealistic";
    public string AspectRatio { get; set; } = "1:1";
    public string Lighting { get; set; } = "";
    public string CameraAngle { get; set; } = "";
    public string Mood { get; set; } = "";
    public string Details { get; set; } = "";
    public List<string>? SelectedGoals { get; set; }
    public string? AdditionalContext { get; set; }
}

public class VideoScriptRequest
{
    public string VideoType { get; set; } = "explainer";
    public string Topic { get; set; } = "";
    public string Duration { get; set; } = "60";
    public string TargetAudience { get; set; } = "";
    public string Tone { get; set; } = "professional";
    public string KeyPoints { get; set; } = "";
    public List<string>? SelectedGoals { get; set; }
    public string? AdditionalContext { get; set; }
}

public class EnhanceTranscriptRequest
{
    public string Transcript { get; set; } = "";
    public List<string>? SelectedGoals { get; set; }
    public string? AdditionalContext { get; set; }
}
