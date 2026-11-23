using System.Diagnostics;
using System.Text.Json;
using AgentHub.Api.Data;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>What one model returned, with the numbers that let you compare it to another.</summary>
public record ModelRunOutcome(
    string Model,
    string? Output,
    string? Error,
    int TokensUsed,
    long LatencyMs,
    decimal CostUsd,
    bool CostIsKnown,
    string? Provider,
    string? FellBackFrom = null);

/// <summary>
/// Runs a prompt against specific models, prices the result, and records a trace.
///
/// Everything that executes a prompt outside the interactive UI goes through
/// here, so that "what did this cost" and "how slow was it" have exactly one
/// answer rather than one per call site. The comparison grid, the test-suite
/// runner, the fallback chain and the external API are all the same three
/// operations: pick a model, run it, write down what happened.
/// </summary>
public class ModelExecutionService
{
    private readonly AIServiceManager _ai;
    private readonly PromptDbContext _db;
    private readonly ILogger<ModelExecutionService> _logger;

    public ModelExecutionService(AIServiceManager ai, PromptDbContext db, ILogger<ModelExecutionService> logger)
    {
        _ai = ai;
        _db = db;
        _logger = logger;
    }

    /// <summary>Prepend retrieved context to the user's message, the same way the test endpoint does.</summary>
    public static string BuildUserMessage(string? userInput, List<ContextDocument>? contextDocs)
    {
        if (contextDocs is not { Count: > 0 }) return userInput ?? string.Empty;

        var prefix = "--- Retrieved Context ---\n" +
            string.Join("\n\n", contextDocs.Select((d, i) =>
                $"[{(string.IsNullOrWhiteSpace(d.Source) ? $"Document {i + 1}" : d.Source)}]:\n{d.Content}")) +
            "\n\n---\n\n";

        return prefix + (userInput ?? string.Empty);
    }

    /// <summary>
    /// Run one model. Routes through <see cref="AIServiceManager"/> rather than
    /// a provider SDK directly, so Ollama and Bedrock models are comparable
    /// against OpenAI ones instead of erroring out.
    /// </summary>
    public async Task<ModelRunOutcome> RunAsync(
        string promptContent,
        string? userInput,
        string model,
        List<ContextDocument>? contextDocs = null,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var messages = new List<ConversationMessage>();
            var userMessage = BuildUserMessage(userInput, contextDocs);
            if (!string.IsNullOrWhiteSpace(userMessage))
                messages.Add(new ConversationMessage("user", userMessage));

            // StreamExecuteAsync is the only path that honours a per-call model
            // override across every provider, so we drain the stream rather than
            // add a second execution path that behaves subtly differently.
            var sb = new System.Text.StringBuilder();
            await foreach (var token in _ai.StreamExecuteAsync(promptContent, messages, model, ct))
                sb.Append(token);

            sw.Stop();
            var output = sb.ToString();

            // No usage numbers come back from the streaming API, so estimate.
            // Flagged via CostIsKnown so the UI can mark it approximate rather
            // than presenting a guess as a measurement.
            var tokens = EstimateTokens(promptContent) + EstimateTokens(userMessage) + EstimateTokens(output);
            var price = ModelPricing.Lookup(model);

            return new ModelRunOutcome(
                Model: model,
                Output: output,
                Error: null,
                TokensUsed: tokens,
                LatencyMs: sw.ElapsedMilliseconds,
                CostUsd: ModelPricing.Estimate(model, tokens),
                CostIsKnown: price != null,
                Provider: price?.Provider);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Model {Model} failed", model);
            return new ModelRunOutcome(model, null, ex.Message, 0, sw.ElapsedMilliseconds, 0m, false, null);
        }
    }

    /// <summary>
    /// Try each model in order until one answers.
    ///
    /// The point is resilience, not cost: a provider outage should degrade the
    /// answer, not remove it. The trace records which model actually served the
    /// request and what it fell back from, because a silent fallback that
    /// nobody can see later is worse than an error.
    /// </summary>
    public async Task<ModelRunOutcome> RunWithFallbackAsync(
        string promptContent,
        string? userInput,
        IReadOnlyList<string> modelChain,
        List<ContextDocument>? contextDocs = null,
        CancellationToken ct = default)
    {
        if (modelChain.Count == 0)
            return await RunAsync(promptContent, userInput, string.Empty, contextDocs, ct);

        ModelRunOutcome? last = null;
        for (var i = 0; i < modelChain.Count; i++)
        {
            var outcome = await RunAsync(promptContent, userInput, modelChain[i], contextDocs, ct);
            if (outcome.Error == null)
            {
                return i == 0
                    ? outcome
                    : outcome with { FellBackFrom = modelChain[0] };
            }

            last = outcome;
            _logger.LogWarning("Model {Model} failed, falling back to next in chain", modelChain[i]);
        }

        return last!;
    }

    /// <summary>
    /// Write down what a call cost and how long it took.
    ///
    /// Never throws: a trace that fails to save must not take the user's actual
    /// request with it. Observability is not worth an outage.
    /// </summary>
    public async Task RecordTraceAsync(
        string userId,
        string? promptId,
        string promptName,
        string source,
        ModelRunOutcome outcome,
        string? input = null,
        string? organizationId = null,
        int? apiKeyId = null,
        CancellationToken ct = default)
    {
        try
        {
            _db.PromptTraces.Add(new PromptTrace
            {
                UserId = userId,
                OrganizationId = organizationId,
                PromptId = promptId,
                PromptName = Truncate(promptName, 200),
                Source = source,
                ApiKeyId = apiKeyId,
                Model = Truncate(outcome.Model, 100),
                Provider = outcome.Provider,
                Status = outcome.Error == null ? "ok" : "error",
                ErrorMessage = outcome.Error,
                TotalTokens = outcome.TokensUsed,
                CostUsd = outcome.CostUsd,
                LatencyMs = outcome.LatencyMs,
                FellBackFrom = outcome.FellBackFrom,
                InputPreview = Truncate(input, 2000),
                OutputPreview = Truncate(outcome.Output, 2000),
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record trace for prompt {PromptId}", promptId);
        }
    }

    /// <summary>
    /// Ask a model to grade two outputs, or one against criteria. Returns 1-10,
    /// or null when the grader itself failed — which must not be read as a zero.
    /// </summary>
    public async Task<int?> ScoreOutputAsync(string output, string criteria, CancellationToken ct = default)
    {
        try
        {
            var system = "You are a strict evaluator. Score the candidate text from 1 to 10 against the criteria. " +
                         "Reply with ONLY a JSON object: {\"score\": <int 1-10>, \"reason\": \"<one sentence>\"}.";
            var user = $"CRITERIA:\n{criteria}\n\nCANDIDATE:\n{output}";

            var sb = new System.Text.StringBuilder();
            await foreach (var t in _ai.StreamExecuteAsync(system, [new ConversationMessage("user", user)], null, ct))
                sb.Append(t);

            var raw = sb.ToString();
            var start = raw.IndexOf('{');
            var end = raw.LastIndexOf('}');
            if (start < 0 || end <= start) return null;

            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            if (doc.RootElement.TryGetProperty("score", out var s) && s.TryGetInt32(out var score))
                return Math.Clamp(score, 1, 10);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scoring failed");
            return null;
        }
    }

    /// <summary>
    /// Rough token count. Deliberately crude — 4 chars per token is close
    /// enough to rank models by cost, and the alternative is shipping a
    /// tokenizer per provider for a number shown to two decimal places.
    /// </summary>
    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
