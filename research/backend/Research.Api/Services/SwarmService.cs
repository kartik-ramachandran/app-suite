using OpenAI.Chat;
using Research.Api.Models;

namespace Research.Api.Services;

public record SwarmAgentResult(string Perspective, string Prompt, double ConfidenceScore);
public record SwarmGenerationResult(List<SwarmAgentResult> Agents, string ConsensusPrompt, double AverageConfidence);
public enum SwarmOutputMode { Prompt, Analysis }

public class SwarmService
{
    private readonly IAIService _aiService;
    private readonly WebSearchService _webSearch;
    private readonly ILogger<SwarmService> _logger;
    private readonly IConfiguration _config;

    private static readonly string[] Perspectives = new[]
    {
        "Analytical & Logical — focus on precision, structure, and measurable outcomes",
        "Creative & Innovative — prioritise originality, metaphors, and novel angles",
        "Practical & Implementation-focused — emphasise clear steps and actionable instructions",
        "User Experience-focused — optimise for clarity, readability, and tone alignment",
        "Risk & Edge-case Aware — anticipate failure modes, constraints, and guard-rails"
    };

    public SwarmService(IAIService aiService, WebSearchService webSearch, ILogger<SwarmService> logger, IConfiguration config)
    {
        _aiService = aiService;
        _webSearch = webSearch;
        _logger = logger;
        _config = config;
    }

    public async Task<SwarmGenerationResult> GenerateAsync(
        string intent,
        List<string> selectedGoals,
        string? additionalContext,
        int agentCount = 5,
        string? userApiKey = null,
        string? userModel = null,
        bool useWebSearch = true,
        IAIService? serviceOverride = null,
        List<string>? customPerspectives = null,
        string? fileContext = null,
        SwarmOutputMode outputMode = SwarmOutputMode.Prompt)
    {
        agentCount = Math.Clamp(agentCount, 2, 8);
        var perspectives = (customPerspectives != null && customPerspectives.Count > 0)
            ? customPerspectives.Take(agentCount).ToArray()
            : Perspectives.Take(agentCount).ToArray();

        // Run a single web search upfront and share the results with all agents
        string? searchContext = null;
        if (useWebSearch)
        {
            try { searchContext = await _webSearch.SearchAsync(intent, 5); }
            catch { /* non-fatal — agents proceed without search context */ }
        }

        var agentTasks = perspectives.Select(perspective =>
            GenerateFromPerspectiveAsync(intent, selectedGoals, additionalContext, perspective, userApiKey, userModel, searchContext, serviceOverride, fileContext, outputMode)
        ).ToArray();

        var results = await Task.WhenAll(agentTasks);

        var validResults = results.OfType<SwarmAgentResult>().ToList();

        if (validResults.Count == 0)
            throw new InvalidOperationException("All swarm agents failed to generate prompts.");

        var consensus = await SynthesiseConsensusAsync(intent, validResults, userApiKey, userModel, serviceOverride, outputMode);

        return new SwarmGenerationResult(
            validResults,
            consensus,
            validResults.Average(r => r.ConfidenceScore)
        );
    }

    private async Task<SwarmAgentResult?> GenerateFromPerspectiveAsync(
        string intent, List<string> goals, string? context,
        string perspective, string? userApiKey, string? userModel, string? searchContext = null, IAIService? serviceOverride = null, string? fileContext = null,
        SwarmOutputMode outputMode = SwarmOutputMode.Prompt)
    {
        try
        {
            var service = GetService(userApiKey, userModel, serviceOverride);
            var goalsText = goals.Any() ? $"\nSelected goals:\n{string.Join("\n", goals.Select(g => $"- {g}"))}" : "";
            var ctxText = !string.IsNullOrWhiteSpace(context) ? $"\nAdditional context: {context}" : "";
            var searchText = !string.IsNullOrWhiteSpace(searchContext) ? $"\n\nWeb search results for context:\n{searchContext}" : "";
            var fileText = !string.IsNullOrWhiteSpace(fileContext) ? $"\n\nUploaded documents for analysis:\n{fileContext}" : "";

            var systemPrompt = outputMode == SwarmOutputMode.Analysis
                ? $"You are a scenario research analyst. Your perspective: {perspective}. " +
                  "Analyze the user's scenario and time horizon. Return concise analytical findings, not an AI prompt and not instructions for another model. " +
                  "Focus on likely trajectory, key drivers, risks, assumptions, inflection points, and concrete implications. " +
                  (string.IsNullOrWhiteSpace(fileContext) ? "" : "Use relevant details from the uploaded documents. ") +
                  "Write 2-4 short paragraphs or bullets. Output ONLY the analysis. " +
                  "After the analysis, on a new line write: CONFIDENCE: <score 0.0-1.0>"
                : $"You are a prompt engineering expert. Your perspective: {perspective}. " +
                  "Generate a single, production-ready AI prompt that fulfils the user's intent. " +
                  (string.IsNullOrWhiteSpace(fileContext) ? "" : "Incorporate insights and specific details from the uploaded documents into the prompt. ") +
                  "Output ONLY the prompt text — no preamble, no labels, no explanations. " +
                  "After the prompt, on a new line write: CONFIDENCE: <score 0.0-1.0>";

            var userMsg = $"Intent: {intent}{goalsText}{ctxText}{searchText}{fileText}";

            var response = await service.TestPromptAsync(new PromptTestRequest
            {
                PromptContent = systemPrompt,
                UserInput = userMsg
            });

            var text = response.Result.Trim();
            var confidenceLine = text.Split('\n').LastOrDefault(l => l.StartsWith("CONFIDENCE:"));
            double confidence = 0.75;
            if (confidenceLine != null)
            {
                if (double.TryParse(confidenceLine.Replace("CONFIDENCE:", "").Trim(), out var c))
                    confidence = Math.Clamp(c, 0, 1);
                text = text[..text.LastIndexOf("CONFIDENCE:")].Trim();
            }

            return new SwarmAgentResult(perspective.Split('—')[0].Trim(), text, confidence);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Swarm agent failed for perspective: {Perspective}", perspective);
            return null;
        }
    }

    private async Task<string> SynthesiseConsensusAsync(
        string intent, List<SwarmAgentResult> agents,
        string? userApiKey, string? userModel, IAIService? serviceOverride = null,
        SwarmOutputMode outputMode = SwarmOutputMode.Prompt)
    {
        var service = GetService(userApiKey, userModel, serviceOverride);

        var agentSummaries = string.Join("\n\n", agents.Select((a, i) =>
            $"Agent {i + 1} [{a.Perspective}] (confidence {a.ConfidenceScore:F2}):\n{a.Prompt}"));

        var systemPrompt = outputMode == SwarmOutputMode.Analysis
            ? "You are a senior research analyst synthesising multiple scenario analyses. " +
              "Produce a concise executive horizon summary for the user, not an AI prompt and not instructions for another model. " +
              "Synthesize the strongest shared signals and disagreements into 4-6 short bullet points. " +
              "Include likely trajectory, confidence, key drivers, risks, and the most important inflection points. " +
              "Output ONLY the bullet list."
            : "You are a senior prompt engineer synthesising multiple AI-generated prompts into one superior version. " +
              "Take the strongest elements from each agent's output and produce a single, cohesive, production-ready prompt. " +
              "Output ONLY the final synthesised prompt — no preamble, labels, or commentary.";

        var userMsg = $"Original intent: {intent}\n\nAgent outputs:\n{agentSummaries}";

        var response = await service.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = systemPrompt,
            UserInput = userMsg
        });

        return response.Result.Trim();
    }

    private IAIService GetService(string? userApiKey, string? userModel, IAIService? serviceOverride = null)
    {
        if (serviceOverride != null) return serviceOverride;
        if (!string.IsNullOrWhiteSpace(userApiKey))
        {
            var cfg = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenAI:ApiKey"] = userApiKey,
                    ["OpenAI:DefaultModel"] = userModel ?? "gpt-4o"
                }).Build();
            return new OpenAIService(cfg, Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenAIService>.Instance);
        }
        return _aiService;
    }
}
