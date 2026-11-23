namespace AgentHub.Api.Models;

/// <summary>
/// What a model costs, per million tokens, split by direction.
///
/// This lives in one place because it is the only thing standing between a
/// comparison grid that says "gpt-4o is better" and one that says "gpt-4o is
/// better and costs 33x more". Prices drift; revisit quarterly. Anything not
/// listed falls back to <see cref="Unknown"/> and is reported as an estimate
/// rather than silently costed at zero.
/// </summary>
public record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion, string? Provider = null)
{
    /// <summary>Blended cost when we only know a total token count.</summary>
    public decimal Blended => (InputPerMillion + OutputPerMillion) / 2m;
}

public static class ModelPricing
{
    /// <summary>Used when a model is not in the table — cost is reported as 0 and flagged unknown.</summary>
    public static readonly ModelPrice Unknown = new(0m, 0m);

    private static readonly Dictionary<string, ModelPrice> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── OpenAI ──────────────────────────────────────────────────────────
        ["gpt-4o"]                  = new(2.50m, 10.00m, "OpenAI"),
        ["gpt-4o-mini"]             = new(0.15m, 0.60m, "OpenAI"),
        ["gpt-4-turbo"]             = new(10.00m, 30.00m, "OpenAI"),
        ["gpt-4"]                   = new(30.00m, 60.00m, "OpenAI"),
        ["gpt-3.5-turbo"]           = new(0.50m, 1.50m, "OpenAI"),
        ["o1"]                      = new(15.00m, 60.00m, "OpenAI"),
        ["o1-mini"]                 = new(1.10m, 4.40m, "OpenAI"),
        ["o3-mini"]                 = new(1.10m, 4.40m, "OpenAI"),
        ["gpt-4.1"]                 = new(2.00m, 8.00m, "OpenAI"),
        ["gpt-4.1-mini"]            = new(0.40m, 1.60m, "OpenAI"),
        ["gpt-4.1-nano"]            = new(0.10m, 0.40m, "OpenAI"),

        // ── Anthropic (direct + via Bedrock) ────────────────────────────────
        ["claude-opus-4"]           = new(15.00m, 75.00m, "Anthropic"),
        ["claude-sonnet-4"]         = new(3.00m, 15.00m, "Anthropic"),
        ["claude-3-7-sonnet"]       = new(3.00m, 15.00m, "Anthropic"),
        ["claude-3-5-sonnet"]       = new(3.00m, 15.00m, "Anthropic"),
        ["claude-3-5-haiku"]        = new(0.80m, 4.00m, "Anthropic"),
        ["claude-3-opus"]           = new(15.00m, 75.00m, "Anthropic"),
        ["claude-3-haiku"]          = new(0.25m, 1.25m, "Anthropic"),

        // ── Google ──────────────────────────────────────────────────────────
        ["gemini-2.0-flash"]        = new(0.10m, 0.40m, "Google"),
        ["gemini-1.5-pro"]          = new(1.25m, 5.00m, "Google"),
        ["gemini-1.5-flash"]        = new(0.075m, 0.30m, "Google"),

        // ── Amazon ──────────────────────────────────────────────────────────
        ["amazon.nova-pro-v1:0"]    = new(0.80m, 3.20m, "AmazonBedrock"),
        ["amazon.nova-lite-v1:0"]   = new(0.06m, 0.24m, "AmazonBedrock"),
        ["amazon.nova-micro-v1:0"]  = new(0.035m, 0.14m, "AmazonBedrock"),

        // ── Meta / Mistral via Bedrock ──────────────────────────────────────
        ["meta.llama3-70b-instruct-v1:0"] = new(2.65m, 3.50m, "AmazonBedrock"),
        ["mistral.mistral-large-2407-v1:0"] = new(2.00m, 6.00m, "AmazonBedrock"),
    };

    /// <summary>Local models cost nothing per token — the electricity is not our problem.</summary>
    private static bool IsLocal(string model) =>
        model.Contains("llama", StringComparison.OrdinalIgnoreCase) && !model.Contains(':') ||
        model.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase);

    public static ModelPrice? Lookup(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (Table.TryGetValue(model, out var exact)) return exact;

        // Bedrock and Azure prefix/suffix the same underlying model in a dozen
        // ways ("anthropic.claude-3-5-sonnet-20241022-v2:0"). Match on the
        // longest known key contained in the id rather than demanding an
        // exact string, so a new deployment name does not silently cost 0.
        var match = Table.Keys
            .Where(k => model.Contains(k, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();

        if (match != null) return Table[match];
        if (IsLocal(model)) return new ModelPrice(0m, 0m, "Ollama");
        return null;
    }

    /// <summary>
    /// Cost in USD. When we have the input/output split we use it; when we only
    /// have a total (most providers' usage payloads) we blend.
    /// </summary>
    public static decimal Estimate(string? model, int totalTokens, int? inputTokens = null, int? outputTokens = null)
    {
        var price = Lookup(model);
        if (price == null) return 0m;

        if (inputTokens.HasValue && outputTokens.HasValue)
            return Math.Round(
                (price.InputPerMillion * inputTokens.Value + price.OutputPerMillion * outputTokens.Value) / 1_000_000m,
                6);

        return Math.Round(price.Blended * totalTokens / 1_000_000m, 6);
    }

    public static bool IsKnown(string? model) => Lookup(model) != null;

    public static IReadOnlyDictionary<string, ModelPrice> All => Table;
}
