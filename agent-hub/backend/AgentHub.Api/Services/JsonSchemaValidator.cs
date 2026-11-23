using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentHub.Api.Services;

public record SchemaValidationResult(bool IsValid, List<string> Errors)
{
    public static SchemaValidationResult Ok() => new(true, []);
    public static SchemaValidationResult Fail(params string[] errors) => new(false, [.. errors]);
}

/// <summary>
/// Validates model output against a JSON Schema.
///
/// Implements the subset of Draft-07 that prompt authors actually write —
/// types, required, enum, nested objects and arrays, numeric and string bounds.
/// A dedicated schema library would cover more, but this avoids a dependency
/// for a feature whose whole job is "did the model return the shape I asked
/// for", and an unsupported keyword is ignored rather than failing the payload.
/// </summary>
public class JsonSchemaValidator
{
    /// <summary>Pull a JSON object or array out of a response that may be wrapped in prose or fences.</summary>
    public static string? ExtractJson(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim();

        // ```json … ``` is the single most common wrapper.
        var fence = Regex.Match(text, @"```(?:json)?\s*(.+?)\s*```", RegexOptions.Singleline);
        if (fence.Success) text = fence.Groups[1].Value.Trim();

        if (LooksLikeJson(text)) return text;

        // Fall back to the outermost braces/brackets in the response.
        foreach (var (open, close) in new[] { ('{', '}'), ('[', ']') })
        {
            var start = text.IndexOf(open);
            var end = text.LastIndexOf(close);
            if (start >= 0 && end > start)
            {
                var candidate = text[start..(end + 1)];
                if (LooksLikeJson(candidate)) return candidate;
            }
        }

        return null;
    }

    private static bool LooksLikeJson(string s)
    {
        try { using var _ = JsonDocument.Parse(s); return true; }
        catch { return false; }
    }

    public SchemaValidationResult Validate(string? output, string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson)) return SchemaValidationResult.Ok();

        var json = ExtractJson(output);
        if (json == null)
            return SchemaValidationResult.Fail("Response is not valid JSON.");

        JsonDocument schemaDoc;
        try { schemaDoc = JsonDocument.Parse(schemaJson); }
        catch (Exception ex) { return SchemaValidationResult.Fail($"Schema itself is invalid JSON: {ex.Message}"); }

        using (schemaDoc)
        using (var dataDoc = JsonDocument.Parse(json))
        {
            var errors = new List<string>();
            ValidateNode(dataDoc.RootElement, schemaDoc.RootElement, "$", errors);
            return errors.Count == 0 ? SchemaValidationResult.Ok() : new SchemaValidationResult(false, errors);
        }
    }

    private void ValidateNode(JsonElement data, JsonElement schema, string path, List<string> errors)
    {
        if (schema.ValueKind != JsonValueKind.Object) return;

        if (schema.TryGetProperty("type", out var typeEl))
        {
            var expected = typeEl.ValueKind == JsonValueKind.Array
                ? typeEl.EnumerateArray().Select(t => t.GetString()).ToList()
                : [typeEl.GetString()];

            if (!expected.Any(t => MatchesType(data, t)))
                errors.Add($"{path}: expected {string.Join(" or ", expected)}, got {data.ValueKind.ToString().ToLowerInvariant()}");
        }

        if (schema.TryGetProperty("enum", out var enumEl) && enumEl.ValueKind == JsonValueKind.Array)
        {
            var raw = data.ToString();
            if (!enumEl.EnumerateArray().Any(v => v.ToString() == raw))
                errors.Add($"{path}: value '{Trim(raw)}' is not one of the allowed values");
        }

        if (data.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in req.EnumerateArray())
                {
                    var name = r.GetString();
                    if (name != null && !data.TryGetProperty(name, out _))
                        errors.Add($"{path}: missing required property '{name}'");
                }
            }

            if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in props.EnumerateObject())
                {
                    if (data.TryGetProperty(prop.Name, out var child))
                        ValidateNode(child, prop.Value, $"{path}.{prop.Name}", errors);
                }
            }
        }

        if (data.ValueKind == JsonValueKind.Array)
        {
            if (schema.TryGetProperty("items", out var items))
            {
                var i = 0;
                foreach (var el in data.EnumerateArray())
                    ValidateNode(el, items, $"{path}[{i++}]", errors);
            }

            if (schema.TryGetProperty("minItems", out var minI) && minI.TryGetInt32(out var min) &&
                data.GetArrayLength() < min)
                errors.Add($"{path}: expected at least {min} items, got {data.GetArrayLength()}");

            if (schema.TryGetProperty("maxItems", out var maxI) && maxI.TryGetInt32(out var max) &&
                data.GetArrayLength() > max)
                errors.Add($"{path}: expected at most {max} items, got {data.GetArrayLength()}");
        }

        if (data.ValueKind == JsonValueKind.String)
        {
            var s = data.GetString() ?? "";
            if (schema.TryGetProperty("minLength", out var mn) && mn.TryGetInt32(out var minL) && s.Length < minL)
                errors.Add($"{path}: string shorter than minLength {minL}");
            if (schema.TryGetProperty("maxLength", out var mx) && mx.TryGetInt32(out var maxL) && s.Length > maxL)
                errors.Add($"{path}: string longer than maxLength {maxL}");
            if (schema.TryGetProperty("pattern", out var pat) && pat.GetString() is { } pattern)
            {
                try
                {
                    if (!Regex.IsMatch(s, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
                        errors.Add($"{path}: does not match pattern {pattern}");
                }
                catch (RegexMatchTimeoutException)
                {
                    // A pathological pattern in a user's schema must not hang
                    // the request; treat it as unenforceable and move on.
                }
                catch (ArgumentException)
                {
                    errors.Add($"{path}: schema pattern is not a valid regex");
                }
            }
        }

        if (data.ValueKind == JsonValueKind.Number)
        {
            var v = data.GetDouble();
            if (schema.TryGetProperty("minimum", out var mnN) && mnN.TryGetDouble(out var minN) && v < minN)
                errors.Add($"{path}: {v} is below minimum {minN}");
            if (schema.TryGetProperty("maximum", out var mxN) && mxN.TryGetDouble(out var maxN) && v > maxN)
                errors.Add($"{path}: {v} is above maximum {maxN}");
        }
    }

    private static bool MatchesType(JsonElement e, string? type) => type switch
    {
        "object"  => e.ValueKind == JsonValueKind.Object,
        "array"   => e.ValueKind == JsonValueKind.Array,
        "string"  => e.ValueKind == JsonValueKind.String,
        "number"  => e.ValueKind == JsonValueKind.Number,
        // JSON has one number type; "integer" is a constraint on top of it.
        "integer" => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out _),
        "boolean" => e.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null"    => e.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string Trim(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
