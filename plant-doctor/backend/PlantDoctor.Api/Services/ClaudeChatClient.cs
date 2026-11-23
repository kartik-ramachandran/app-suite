using Microsoft.Extensions.Configuration;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenAI.Chat;

namespace PlantDoctor.Api.Services;

/// <summary>
/// Claude (Anthropic Messages API) behind the OpenAI SDK's <see cref="ChatClient"/>
/// shape, so every existing <c>CompleteChatAsync</c> / <c>CompleteChatStreamingAsync</c>
/// call site in this app runs on Claude without being rewritten.
///
/// Translates: system / user / assistant / tool messages, text + image + PDF parts,
/// function tools and tool choice, max tokens, JSON response format, stop reasons,
/// token usage, and server-sent-event streaming.
///
/// Configuration (appsettings.json or environment variables):
///   Anthropic:ApiKey     (env Anthropic__ApiKey)  — required
///   Anthropic:Model      default claude-sonnet-5-5
///   Anthropic:MaxTokens  default 8192, used when a call sets no limit
///   Anthropic:BaseUrl    default https://api.anthropic.com
/// </summary>
public sealed class ClaudeChatClient : ChatClient
{
    public const string DefaultModel = "claude-sonnet-5-5";
    private const string ApiVersion = "2023-06-01";

    // One shared HttpClient for the process: sockets are pooled, not leaked per call.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly string _apiKey;
    private readonly string _model;
    private readonly int _defaultMaxTokens;
    private readonly Uri _messagesUri;

    /// <param name="requestedModel">
    /// The model the call site asked for. Call sites copied from the OpenAI era pass
    /// names like "gpt-4o"; anything that isn't a Claude model id uses Anthropic:Model.
    /// </param>
    public ClaudeChatClient(IConfiguration configuration, string? requestedModel = null)
    {
        _apiKey = configuration["Anthropic:ApiKey"] ?? "";
        var configured = configuration["Anthropic:Model"];
        _model = requestedModel != null && requestedModel.StartsWith("claude", StringComparison.OrdinalIgnoreCase)
            ? requestedModel
            : string.IsNullOrWhiteSpace(configured) ? DefaultModel : configured;
        _defaultMaxTokens = int.TryParse(configuration["Anthropic:MaxTokens"], out var mt) && mt > 0 ? mt : 8192;
        var baseUrl = configuration["Anthropic:BaseUrl"];
        _messagesUri = new Uri((string.IsNullOrWhiteSpace(baseUrl) ? "https://api.anthropic.com" : baseUrl.TrimEnd('/')) + "/v1/messages");
    }

    public static bool IsConfigured(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["Anthropic:ApiKey"]);

    public string ModelId => _model;

    // ── Non-streaming ───────────────────────────────────────────────────────

    public override Task<ClientResult<ChatCompletion>> CompleteChatAsync(params ChatMessage[] messages)
        => CompleteChatAsync(messages, null, default);

    public override async Task<ClientResult<ChatCompletion>> CompleteChatAsync(
        IEnumerable<ChatMessage> messages, ChatCompletionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var body = BuildRequest(messages, options, stream: false);
        using var req = NewRequest(body);
        using var resp = await Http.SendAsync(req, cancellationToken);
        var text = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new ClaudeApiException((int)resp.StatusCode, text);

        var json = JsonNode.Parse(text)!;
        var completion = ToCompletion(json, RequestedJson(options));
        return ClientResult.FromValue(completion, new StaticResponse(200, text));
    }

    public override ClientResult<ChatCompletion> CompleteChat(
        IEnumerable<ChatMessage> messages, ChatCompletionOptions? options = null, CancellationToken cancellationToken = default)
        => CompleteChatAsync(messages, options, cancellationToken).GetAwaiter().GetResult();

    public override ClientResult<ChatCompletion> CompleteChat(params ChatMessage[] messages)
        => CompleteChat(messages, null, default);

    // ── Streaming ───────────────────────────────────────────────────────────

    public override AsyncCollectionResult<StreamingChatCompletionUpdate> CompleteChatStreamingAsync(params ChatMessage[] messages)
        => CompleteChatStreamingAsync(messages, null, default);

    public override AsyncCollectionResult<StreamingChatCompletionUpdate> CompleteChatStreamingAsync(
        IEnumerable<ChatMessage> messages, ChatCompletionOptions? options = null, CancellationToken cancellationToken = default)
        => new StreamResult(this, BuildRequest(messages, options, stream: true), cancellationToken);

    private async IAsyncEnumerable<StreamingChatCompletionUpdate> StreamAsync(
        JsonObject body, [EnumeratorCancellation] CancellationToken ct)
    {
        using var req = NewRequest(body);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
            throw new ClaudeApiException((int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));

        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string id = "", model = _model;
        int inputTokens = 0, outputTokens = 0;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0) continue;
            var ev = JsonNode.Parse(payload);
            var type = ev?["type"]?.GetValue<string>();
            if (type == "message_start")
            {
                id = ev!["message"]?["id"]?.GetValue<string>() ?? "";
                model = ev["message"]?["model"]?.GetValue<string>() ?? model;
                inputTokens = ev["message"]?["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
            }
            else if (type == "content_block_delta")
            {
                var delta = ev!["delta"];
                if (delta?["type"]?.GetValue<string>() != "text_delta") continue;
                var piece = delta["text"]?.GetValue<string>() ?? "";
                if (piece.Length > 0)
                    yield return OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                        completionId: id, contentUpdate: new ChatMessageContent(piece),
                        role: ChatMessageRole.Assistant, createdAt: DateTimeOffset.UtcNow, model: model);
            }
            else if (type == "message_delta")
            {
                outputTokens = ev!["usage"]?["output_tokens"]?.GetValue<int>() ?? outputTokens;
                var finish = MapStop(ev["delta"]?["stop_reason"]?.GetValue<string>());
                yield return OpenAIChatModelFactory.StreamingChatCompletionUpdate(
                    completionId: id, contentUpdate: new ChatMessageContent(), finishReason: finish,
                    createdAt: DateTimeOffset.UtcNow, model: model,
                    usage: OpenAIChatModelFactory.ChatTokenUsage(outputTokens, inputTokens, inputTokens + outputTokens, null));
            }
            else if (type == "error")
            {
                throw new ClaudeApiException(500, payload);
            }
        }
    }

    // ── Request building ────────────────────────────────────────────────────

    private HttpRequestMessage NewRequest(JsonObject body)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("Claude is not configured. Set Anthropic:ApiKey (env Anthropic__ApiKey).");
        var req = new HttpRequestMessage(HttpMethod.Post, _messagesUri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", ApiVersion);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return req;
    }

    private JsonObject BuildRequest(IEnumerable<ChatMessage> messages, ChatCompletionOptions? options, bool stream)
    {
        var system = new StringBuilder();
        var turns = new JsonArray();

        void Add(string role, JsonArray blocks)
        {
            if (blocks.Count == 0) return;
            // Claude requires alternating roles; consecutive same-role turns are merged.
            if (turns.Count > 0 && turns[^1]!["role"]!.GetValue<string>() == role)
            {
                var prev = turns[^1]!["content"]!.AsArray();
                foreach (var b in blocks.ToList()) { blocks.Remove(b); prev.Add(b); }
            }
            else turns.Add(new JsonObject { ["role"] = role, ["content"] = blocks });
        }

        foreach (var m in messages)
        {
            switch (m)
            {
                case SystemChatMessage s:
                    foreach (var p in s.Content) if (!string.IsNullOrEmpty(p.Text)) system.AppendLine(p.Text);
                    break;
                case UserChatMessage u:
                    Add("user", ContentBlocks(u.Content));
                    break;
                case AssistantChatMessage a:
                    var blocks = ContentBlocks(a.Content);
                    foreach (var call in a.ToolCalls)
                        blocks.Add(new JsonObject
                        {
                            ["type"] = "tool_use",
                            ["id"] = call.Id,
                            ["name"] = call.FunctionName,
                            ["input"] = ParseObject(call.FunctionArguments?.ToString()),
                        });
                    Add("assistant", blocks);
                    break;
                case ToolChatMessage t:
                    var resultText = string.Concat(t.Content.Select(p => p.Text ?? ""));
                    Add("user", new JsonArray(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = t.ToolCallId,
                        ["content"] = string.IsNullOrEmpty(resultText) ? "(empty)" : resultText,
                    }));
                    break;
                default:
                    var fallback = string.Concat(m.Content.Select(p => p.Text ?? ""));
                    if (fallback.Length > 0) Add("user", new JsonArray(TextBlock(fallback)));
                    break;
            }
        }

        // The conversation must open with a user turn.
        if (turns.Count == 0 || turns[0]!["role"]!.GetValue<string>() != "user")
            turns.Insert(0, new JsonObject { ["role"] = "user", ["content"] = new JsonArray(TextBlock("Begin.")) });

        var json = RequestedJson(options);
        if (json != null)
            system.AppendLine(json.Length > 0
                ? $"Respond with ONLY a JSON value matching this JSON Schema, with no prose and no code fences:\n{json}"
                : "Respond with ONLY a valid JSON value, with no prose and no code fences.");

        var body = new JsonObject
        {
            ["model"] = _model,
            ["max_tokens"] = options?.MaxOutputTokenCount is int max && max > 0 ? max : _defaultMaxTokens,
            ["messages"] = turns,
        };
        if (system.Length > 0) body["system"] = system.ToString().Trim();
        if (stream) body["stream"] = true;
        if (options?.StopSequences.Count > 0) body["stop_sequences"] = new JsonArray(options.StopSequences.Select(s => (JsonNode)s).ToArray());

        if (options?.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(options.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.FunctionName,
                ["description"] = t.FunctionDescription ?? "",
                ["input_schema"] = ParseObject(t.FunctionParameters?.ToString(), schema: true),
            }).ToArray());
            var choice = options.ToolChoice == null ? null : ModelReaderWriter.Write(options.ToolChoice).ToString();
            body["tool_choice"] = choice switch
            {
                null => new JsonObject { ["type"] = "auto" },
                var c when c.Contains("\"required\"") => new JsonObject { ["type"] = "any" },
                var c when c.Contains("\"none\"") => new JsonObject { ["type"] = "none" },
                var c when c.Contains("\"function\"") => new JsonObject
                {
                    ["type"] = "tool",
                    ["name"] = JsonNode.Parse(c)?["function"]?["name"]?.GetValue<string>() ?? "",
                },
                _ => new JsonObject { ["type"] = "auto" },
            };
        }
        return body;
    }

    /// <summary>Null = plain text requested; "" = any JSON; otherwise the JSON Schema text.</summary>
    private static string? RequestedJson(ChatCompletionOptions? options)
    {
        if (options?.ResponseFormat == null) return null;
        var fmt = JsonNode.Parse(ModelReaderWriter.Write(options.ResponseFormat).ToString());
        return fmt?["type"]?.GetValue<string>() switch
        {
            "json_object" => "",
            "json_schema" => fmt["json_schema"]?["schema"]?.ToJsonString() ?? "",
            _ => null,
        };
    }

    private static JsonArray ContentBlocks(ChatMessageContent content)
    {
        var blocks = new JsonArray();
        foreach (var part in content)
        {
            switch (part.Kind)
            {
                case ChatMessageContentPartKind.Text when !string.IsNullOrEmpty(part.Text):
                    blocks.Add(TextBlock(part.Text));
                    break;
                case ChatMessageContentPartKind.Image:
                    if (part.ImageBytes != null)
                        blocks.Add(Base64Block("image", part.ImageBytesMediaType ?? "image/jpeg", part.ImageBytes.ToArray()));
                    else if (part.ImageUri != null)
                        blocks.Add(UriImageBlock(part.ImageUri));
                    break;
                case ChatMessageContentPartKind.File when part.FileBytes != null:
                    var media = part.FileBytesMediaType ?? "application/pdf";
                    blocks.Add(media == "application/pdf"
                        ? Base64Block("document", media, part.FileBytes.ToArray())
                        : TextBlock(Encoding.UTF8.GetString(part.FileBytes.ToArray())));
                    break;
            }
        }
        return blocks;
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject Base64Block(string type, string mediaType, byte[] data) => new()
    {
        ["type"] = type,
        ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = Convert.ToBase64String(data) },
    };

    private static JsonObject UriImageBlock(Uri uri)
    {
        var s = uri.OriginalString;
        if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = s.IndexOf(',');
            var header = s[5..comma];                    // e.g. image/png;base64
            var media = header.Split(';')[0];
            return new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = string.IsNullOrEmpty(media) ? "image/jpeg" : media,
                    ["data"] = s[(comma + 1)..],
                },
            };
        }
        return new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "url", ["url"] = s } };
    }

    private static JsonObject ParseObject(string? json, bool schema = false)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(json) && JsonNode.Parse(json) is JsonObject o) return o;
        }
        catch (JsonException) { }
        return schema ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } : new JsonObject();
    }

    // ── Response mapping ────────────────────────────────────────────────────

    private ChatCompletion ToCompletion(JsonNode json, string? jsonRequested)
    {
        var text = new StringBuilder();
        var calls = new List<ChatToolCall>();
        foreach (var block in json["content"]?.AsArray() ?? new JsonArray())
        {
            switch (block?["type"]?.GetValue<string>())
            {
                case "text":
                    text.Append(block["text"]?.GetValue<string>());
                    break;
                case "tool_use":
                    calls.Add(ChatToolCall.CreateFunctionToolCall(
                        block["id"]!.GetValue<string>(),
                        block["name"]!.GetValue<string>(),
                        BinaryData.FromString(block["input"]?.ToJsonString() ?? "{}")));
                    break;
                // thinking / redacted_thinking blocks carry no answer text.
            }
        }

        var output = text.ToString();
        if (jsonRequested != null) output = StripFences(output);

        var stop = json["stop_reason"]?.GetValue<string>();
        var inTok = json["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
        var outTok = json["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;

        return OpenAIChatModelFactory.ChatCompletion(
            id: json["id"]?.GetValue<string>(),
            finishReason: MapStop(stop) ?? ChatFinishReason.Stop,
            // Call sites read Content[0].Text, so a text part is always present.
            content: output.Length > 0 || calls.Count == 0 ? new ChatMessageContent(output) : new ChatMessageContent(),
            refusal: stop == "refusal" ? (output.Length > 0 ? output : "The model declined this request.") : null,
            toolCalls: calls,
            role: ChatMessageRole.Assistant,
            createdAt: DateTimeOffset.UtcNow,
            model: json["model"]?.GetValue<string>() ?? _model,
            usage: OpenAIChatModelFactory.ChatTokenUsage(outTok, inTok, inTok + outTok, null));
    }

    private static ChatFinishReason? MapStop(string? stop) => stop switch
    {
        null => null,
        "tool_use" => ChatFinishReason.ToolCalls,
        "max_tokens" or "model_context_window_exceeded" => ChatFinishReason.Length,
        "refusal" => ChatFinishReason.ContentFilter,
        _ => ChatFinishReason.Stop,
    };

    private static string StripFences(string s)
    {
        var t = s.Trim();
        if (!t.StartsWith("```")) return t;
        var nl = t.IndexOf('\n');
        t = nl >= 0 ? t[(nl + 1)..] : t[3..];
        if (t.EndsWith("```")) t = t[..^3];
        return t.Trim();
    }

    // ── SDK plumbing ────────────────────────────────────────────────────────

    private sealed class StreamResult : AsyncCollectionResult<StreamingChatCompletionUpdate>
    {
        private readonly ClaudeChatClient _client;
        private readonly JsonObject _body;
        private readonly CancellationToken _ct;

        public StreamResult(ClaudeChatClient client, JsonObject body, CancellationToken ct)
        {
            _client = client; _body = body; _ct = ct;
        }

        public override async IAsyncEnumerable<ClientResult> GetRawPagesAsync()
        {
            await Task.CompletedTask;
            yield return ClientResult.FromResponse(new StaticResponse(200, ""));
        }

        public override ContinuationToken? GetContinuationToken(ClientResult page) => null;

        protected override IAsyncEnumerable<StreamingChatCompletionUpdate> GetValuesFromPageAsync(ClientResult page)
            => _client.StreamAsync(_body, _ct);
    }

    private sealed class StaticResponse : PipelineResponse
    {
        private readonly BinaryData _content;
        private Stream? _stream;

        public StaticResponse(int status, string content)
        {
            Status = status;
            _content = BinaryData.FromString(content);
        }

        public override int Status { get; }
        public override string ReasonPhrase => Status == 200 ? "OK" : "Error";
        protected override PipelineResponseHeaders HeadersCore { get; } = new NoHeaders();
        public override Stream? ContentStream { get => _stream ??= _content.ToStream(); set => _stream = value; }
        public override BinaryData Content => _content;
        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => _content;
        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) => new(_content);
        public override void Dispose() => _stream?.Dispose();
    }

    private sealed class NoHeaders : PipelineResponseHeaders
    {
        public override bool TryGetValue(string name, out string? value) { value = null; return false; }
        public override bool TryGetValues(string name, out IEnumerable<string>? values) { values = null; return false; }
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
            => Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();
    }
}

/// <summary>Drop-in for the old <c>AzureOpenAIClient(...).GetChatClient(model)</c> pattern.</summary>
public sealed class ClaudeClient
{
    private readonly IConfiguration _configuration;
    public ClaudeClient(IConfiguration configuration) => _configuration = configuration;
    public ChatClient GetChatClient(string? model = null) => new ClaudeChatClient(_configuration, model);
}

public sealed class ClaudeApiException : Exception
{
    public int StatusCode { get; }
    public ClaudeApiException(int statusCode, string body)
        : base($"Claude API error {statusCode}: {(body.Length > 600 ? body[..600] : body)}") => StatusCode = statusCode;
}
