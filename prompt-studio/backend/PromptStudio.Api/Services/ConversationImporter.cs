using System.Text.Json;
using System.Text.RegularExpressions;

namespace PromptStudio.Api.Services;

public class ConversationImportCandidate
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;

    /// <summary>system-prompt | custom-instruction | repeated-opener | long-instruction</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>How many separate conversations this text appeared in. Repetition is the strongest signal.</summary>
    public int OccurrenceCount { get; set; } = 1;

    public string? ConversationTitle { get; set; }
    public DateTime? CreatedAt { get; set; }
    public string? SuggestedCategory { get; set; }
}

/// <summary>
/// Mines reusable prompts out of a ChatGPT or Claude data export.
///
/// The insight is that people have already written their best prompts — they
/// are just buried in two years of conversation history. What makes something
/// worth extracting is repetition (the same opener typed into fifteen chats) or
/// explicit configuration (a system prompt, a custom instruction), not merely
/// being long.
///
/// Both vendors change their export shape periodically, so parsing is
/// deliberately forgiving: unknown keys are ignored and a node that does not
/// look like a message is skipped rather than throwing.
/// </summary>
public static class ConversationImporter
{
    public static string DetectFormat(string rawJson)
    {
        var head = rawJson.Length > 4000 ? rawJson[..4000] : rawJson;
        if (head.Contains("\"mapping\"") && head.Contains("\"author\"")) return "chatgpt";
        if (head.Contains("\"chat_messages\"")) return "claude";
        if (head.Contains("\"conversations\"")) return "generic";
        return "unknown";
    }

    public static List<ConversationImportCandidate> Parse(string rawJson, int maxCandidates = 50)
    {
        maxCandidates = Math.Clamp(maxCandidates, 1, 200);

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        var conversations = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray().ToList()
            : root.TryGetProperty("conversations", out var c) && c.ValueKind == JsonValueKind.Array
                ? c.EnumerateArray().ToList()
                : [root];

        var systemPrompts = new List<ConversationImportCandidate>();
        // Keyed on normalised text so the same opener across many chats collapses
        // into one candidate with a count.
        var openers = new Dictionary<string, ConversationImportCandidate>();

        foreach (var conv in conversations)
        {
            var title = Str(conv, "title") ?? Str(conv, "name");
            var created = Epoch(conv, "create_time") ?? Date(conv, "created_at");

            foreach (var (role, text) in Messages(conv))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                var trimmed = text.Trim();

                if (role == "system")
                {
                    // A system prompt is already a prompt — take it as-is.
                    if (trimmed.Length >= 40 && !IsBoilerplate(trimmed))
                        systemPrompts.Add(new ConversationImportCandidate
                        {
                            Title = title ?? Summarize(trimmed),
                            Content = trimmed,
                            Source = "system-prompt",
                            ConversationTitle = title,
                            CreatedAt = created,
                            SuggestedCategory = Categorize(trimmed)
                        });
                    continue;
                }

                if (role != "user") continue;
                if (!LooksLikeInstruction(trimmed)) continue;

                var key = Normalize(trimmed);
                if (openers.TryGetValue(key, out var existing))
                {
                    existing.OccurrenceCount++;
                }
                else
                {
                    openers[key] = new ConversationImportCandidate
                    {
                        Title = Summarize(trimmed),
                        Content = trimmed,
                        Source = trimmed.Length > 400 ? "long-instruction" : "repeated-opener",
                        ConversationTitle = title,
                        CreatedAt = created,
                        SuggestedCategory = Categorize(trimmed)
                    };
                }
            }
        }

        // Custom instructions live outside the conversation list in a ChatGPT
        // export. Guarded on ValueKind because a real export's root is an
        // ARRAY of conversations, and TryGetProperty throws on an array rather
        // than returning false.
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "custom_instructions", "user_profile", "about_user_message", "about_model_message" })
            {
                if (root.TryGetProperty(name, out var ci))
                {
                    var text = ci.ValueKind == JsonValueKind.String ? ci.GetString() : ci.ToString();
                    if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length >= 40)
                        systemPrompts.Add(new ConversationImportCandidate
                        {
                            Title = "Custom instructions",
                            Content = text.Trim(),
                            Source = "custom-instruction",
                            SuggestedCategory = "General"
                        });
                }
            }
        }

        // System prompts and custom instructions first — they were explicitly
        // written as reusable configuration. Then openers by how often they
        // recur, because that is the honest measure of "I use this a lot".
        return systemPrompts
            .Concat(openers.Values
                .Where(o => o.OccurrenceCount >= 2 || o.Content.Length >= 400)
                .OrderByDescending(o => o.OccurrenceCount)
                .ThenByDescending(o => o.Content.Length))
            .Take(maxCandidates)
            .ToList();
    }

    /// <summary>Yield (role, text) for every message, across both export shapes.</summary>
    private static IEnumerable<(string Role, string Text)> Messages(JsonElement conv)
    {
        // ChatGPT: a "mapping" object of nodes, each with a message.
        if (conv.TryGetProperty("mapping", out var mapping) && mapping.ValueKind == JsonValueKind.Object)
        {
            foreach (var node in mapping.EnumerateObject())
            {
                if (!node.Value.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
                    continue;

                var role = msg.TryGetProperty("author", out var a) ? Str(a, "role") : null;
                if (role == null) continue;

                var text = ContentText(msg);
                if (text != null) yield return (role, text);
            }
            yield break;
        }

        // Claude: a flat "chat_messages" array.
        if (conv.TryGetProperty("chat_messages", out var cm) && cm.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in cm.EnumerateArray())
            {
                var sender = Str(msg, "sender") ?? Str(msg, "role");
                if (sender == null) continue;

                var role = sender == "human" ? "user" : sender;
                var text = Str(msg, "text") ?? ContentText(msg);
                if (text != null) yield return (role, text);
            }
            yield break;
        }

        // Generic: a "messages" array of {role, content}.
        if (conv.TryGetProperty("messages", out var ms) && ms.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in ms.EnumerateArray())
            {
                var role = Str(msg, "role");
                if (role == null) continue;
                var text = Str(msg, "content") ?? ContentText(msg);
                if (text != null) yield return (role, text);
            }
        }
    }

    /// <summary>Content is a string in some exports and a parts array in others.</summary>
    private static string? ContentText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content)) return null;

        if (content.ValueKind == JsonValueKind.String) return content.GetString();

        if (content.ValueKind == JsonValueKind.Object &&
            content.TryGetProperty("parts", out var parts) &&
            parts.ValueKind == JsonValueKind.Array)
        {
            var joined = string.Join("\n", parts.EnumerateArray()
                .Where(p => p.ValueKind == JsonValueKind.String)
                .Select(p => p.GetString()));
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            var joined = string.Join("\n", content.EnumerateArray()
                .Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : Str(p, "text"))
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }

        return null;
    }

    /// <summary>
    /// Does this read like a reusable instruction rather than a one-off question?
    /// Wrong answers here are cheap — the user picks from the candidates — so
    /// this errs toward recall.
    /// </summary>
    private static bool LooksLikeInstruction(string text)
    {
        if (text.Length < 60 || text.Length > 8000) return false;

        // Pasted content rather than an instruction.
        if (text.StartsWith("http://") || text.StartsWith("https://")) return false;
        if (text.Count(c => c == '\n') > 60) return false;

        var lower = text.ToLowerInvariant();

        var instructional =
            Regex.IsMatch(lower, @"\b(you are|act as|your role is|behave as|pretend to be)\b") ||
            Regex.IsMatch(lower, @"\b(always|never|must|should|ensure that|make sure)\b.{0,60}\b(respond|reply|answer|output|format|write)\b") ||
            Regex.IsMatch(lower, @"\b(respond|reply|answer|output|format|return)\b.{0,40}\b(as|in|with|using)\b.{0,30}\b(json|markdown|bullet|list|table|format)\b") ||
            Regex.IsMatch(lower, @"\b(step by step|follow these|your task is|the goal is|instructions:|rules:|constraints:)\b");

        // A question with no instructional framing is a one-off.
        var isBareQuestion = text.TrimEnd().EndsWith('?') && !instructional;

        return instructional && !isBareQuestion;
    }

    /// <summary>Vendor preamble that is not the user's prompt.</summary>
    private static bool IsBoilerplate(string text)
    {
        var lower = text.ToLowerInvariant();
        return lower.Contains("you are chatgpt, a large language model")
            || lower.Contains("knowledge cutoff")
            || lower.StartsWith("the assistant is claude, created by anthropic");
    }

    private static string Categorize(string text)
    {
        var lower = text.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"\b(code|function|bug|refactor|api|typescript|python|sql)\b")) return "Engineering";
        if (Regex.IsMatch(lower, @"\b(blog|article|copy|headline|newsletter|seo|social post)\b")) return "Writing";
        if (Regex.IsMatch(lower, @"\b(email|reply|customer|support|ticket|apolog)\b")) return "Support";
        if (Regex.IsMatch(lower, @"\b(analy[sz]e|data|report|metric|dashboard|forecast)\b")) return "Analysis";
        if (Regex.IsMatch(lower, @"\b(market|campaign|ad copy|brand|persona|audience)\b")) return "Marketing";
        if (Regex.IsMatch(lower, @"\b(summar|meeting|notes|transcript|action item)\b")) return "Summarization";
        return "Imported";
    }

    /// <summary>First sentence or so, as a title.</summary>
    private static string Summarize(string text)
    {
        var firstLine = text.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? text;
        var sentenceEnd = firstLine.IndexOfAny(['.', '!', '?']);
        var candidate = sentenceEnd > 20 ? firstLine[..sentenceEnd] : firstLine;
        candidate = candidate.Trim();
        return candidate.Length <= 80 ? candidate : candidate[..80].TrimEnd() + "…";
    }

    private static string Normalize(string s) =>
        Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string? Str(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static DateTime? Epoch(JsonElement el, string name) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds((long)v.GetDouble()).UtcDateTime : null;

    private static DateTime? Date(JsonElement el, string name)
    {
        var s = Str(el, name);
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
    }
}
