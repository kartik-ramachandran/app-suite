using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Models;

namespace PromptStudio.Api.Services;

public class ChainExecutionService
{
    private readonly PromptDbContext _context;
    private readonly IAIService _aiService;
    private readonly WebSearchService _webSearchService;
    private readonly ILogger<ChainExecutionService> _logger;
    private const int MaxNodes = 20;
    private const int NodeTimeoutMs = 30000;

    public ChainExecutionService(
        PromptDbContext context,
        IAIService aiService,
        WebSearchService webSearchService,
        ILogger<ChainExecutionService> logger)
    {
        _context = context;
        _aiService = aiService;
        _webSearchService = webSearchService;
        _logger = logger;
    }

    // Tool node types that respect permission mode
    private static readonly HashSet<string> ToolNodeTypes = new(StringComparer.OrdinalIgnoreCase)
        { "web_search", "http_request", "code", "mcp", "file_search" };

    public async Task<ChainExecutionResult> ExecuteChainAsync(Guid chainId, string initialInput, string userId, string permissionMode = "auto")
    {
        var chain = await _context.PromptChains
            .Include(c => c.Nodes).ThenInclude(n => n.Prompt)
            .Include(c => c.Edges)
            .FirstOrDefaultAsync(c => c.Id == chainId && c.UserId == userId);

        if (chain == null)
            throw new InvalidOperationException("Chain not found");

        if (chain.Nodes.Count > MaxNodes)
            throw new InvalidOperationException($"Chain exceeds maximum of {MaxNodes} nodes");

        var startTime = DateTime.UtcNow;
        var stepResults = new List<ChainStepResult>();
        var totalTokens = 0;

        // Build adjacency list from edges
        var adjacency = new Dictionary<int, List<(int targetId, string? branch)>>();
        foreach (var edge in chain.Edges)
        {
            if (!adjacency.ContainsKey(edge.SourceNodeId))
                adjacency[edge.SourceNodeId] = new List<(int, string?)>();
            adjacency[edge.SourceNodeId].Add((edge.TargetNodeId, edge.ConditionBranch));
        }

        // Find input node (starting point)
        var inputNode = chain.Nodes.FirstOrDefault(n => n.NodeType == "input");
        if (inputNode == null)
            throw new InvalidOperationException("Chain must have an input node");

        // Execute nodes using BFS traversal
        var nodeOutputs = new Dictionary<int, string>();
        var queue = new Queue<int>();
        var visited = new HashSet<int>();

        nodeOutputs[inputNode.Id] = initialInput;
        queue.Enqueue(inputNode.Id);

        stepResults.Add(new ChainStepResult
        {
            NodeId = inputNode.Id,
            NodeType = "input",
            Label = inputNode.Label ?? "Input",
            Input = initialInput,
            Output = initialInput,
            TokensUsed = 0
        });

        visited.Add(inputNode.Id);

        // Process connected nodes
        if (adjacency.ContainsKey(inputNode.Id))
        {
            foreach (var (targetId, _) in adjacency[inputNode.Id])
                queue.Enqueue(targetId);
        }

        while (queue.Count > 0)
        {
            var currentNodeId = queue.Dequeue();
            if (visited.Contains(currentNodeId)) continue;
            visited.Add(currentNodeId);

            var node = chain.Nodes.FirstOrDefault(n => n.Id == currentNodeId);
            if (node == null) continue;

            // Get input from parent nodes
            var parentEdges = chain.Edges.Where(e => e.TargetNodeId == currentNodeId);
            var nodeInput = string.Join("\n", parentEdges
                .Where(e => nodeOutputs.ContainsKey(e.SourceNodeId))
                .Select(e => nodeOutputs[e.SourceNodeId]));

            if (string.IsNullOrEmpty(nodeInput)) nodeInput = initialInput;

            var stepResult = new ChainStepResult
            {
                NodeId = node.Id,
                NodeType = node.NodeType,
                Label = node.Label ?? node.NodeType,
                Input = nodeInput
            };

            try
            {
                // Permission mode gate: bypass skips all tool nodes
                if (permissionMode == "bypass" && ToolNodeTypes.Contains(node.NodeType))
                {
                    stepResult.Output = $"[Tool node '{node.NodeType}' skipped — permission mode: bypass]";
                    nodeOutputs[node.Id] = stepResult.Output;
                    stepResults.Add(stepResult);
                    if (adjacency.ContainsKey(node.Id))
                        foreach (var (tid, _) in adjacency[node.Id]) queue.Enqueue(tid);
                    continue;
                }

                using var cts = new CancellationTokenSource(NodeTimeoutMs);

                switch (node.NodeType)
                {
                    case "prompt":
                        var promptContent = node.InlinePrompt ?? node.Prompt?.Content ?? "";
                        var promptFull = promptContent.Replace("{{input}}", nodeInput);
                        var testRequest = new PromptTestRequest { PromptContent = promptFull, UserInput = nodeInput };
                        var response = await _aiService.TestPromptAsync(testRequest);
                        stepResult.Output = response.Result;
                        stepResult.TokensUsed = response.TokensUsed;
                        totalTokens += response.TokensUsed;
                        break;

                    case "agent":
                        var agentSystem = node.SystemPrompt ?? "You are a helpful assistant.";
                        var agentPrompt = (node.InlinePrompt ?? "{{input}}").Replace("{{input}}", nodeInput);
                        var agentRequest = new PromptTestRequest
                        {
                            PromptContent = $"[System]: {agentSystem}\n\n{agentPrompt}",
                            UserInput = nodeInput
                        };
                        var agentResponse = await _aiService.TestPromptAsync(agentRequest);
                        stepResult.Output = agentResponse.Result;
                        stepResult.TokensUsed = agentResponse.TokensUsed;
                        totalTokens += agentResponse.TokensUsed;
                        break;

                    case "classify":
                        var labels = node.ClassifyLabels ?? "positive, negative, neutral";
                        var classifyRequest = new PromptTestRequest
                        {
                            PromptContent = $"Classify the following text into exactly one of these categories: {labels}. Respond with only the category name, nothing else.",
                            UserInput = nodeInput
                        };
                        var classifyResponse = await _aiService.TestPromptAsync(classifyRequest);
                        stepResult.Output = classifyResponse.Result.Trim();
                        stepResult.TokensUsed = classifyResponse.TokensUsed;
                        totalTokens += classifyResponse.TokensUsed;
                        break;

                    case "note":
                        stepResult.Output = nodeInput; // pass-through, notes are informational
                        break;

                    case "web_search":
                        var searchQuery = !string.IsNullOrWhiteSpace(node.InlinePrompt)
                            ? node.InlinePrompt.Replace("{{input}}", nodeInput)
                            : nodeInput;
                        stepResult.Output = await _webSearchService.SearchAsync(searchQuery);
                        break;

                    case "http_request":
                        stepResult.Output = await ExecuteHttpRequestAsync(node, nodeInput);
                        break;

                    case "file_search":
                        stepResult.Output = $"[File Search] Searching for: {nodeInput}\n(Connect to your document store for real results)";
                        break;

                    case "mcp":
                        stepResult.Output = await ExecuteMcpToolAsync(node, nodeInput);
                        break;

                    case "code":
                        stepResult.Output = ExecuteCodeSnippet(node.CodeSnippet, nodeInput);
                        break;

                    case "condition":
                        var conditionMet = EvaluateCondition(node.ConditionExpression, nodeInput);
                        stepResult.Output = conditionMet ? "true" : "false";
                        if (adjacency.ContainsKey(node.Id))
                        {
                            foreach (var (targetId, branch) in adjacency[node.Id])
                            {
                                if (branch == stepResult.Output || string.IsNullOrEmpty(branch))
                                {
                                    nodeOutputs[node.Id] = nodeInput;
                                    if (!visited.Contains(targetId)) queue.Enqueue(targetId);
                                }
                            }
                        }
                        break;

                    case "switch":
                        var switchBranch = EvaluateSwitch(node.SwitchExpression, nodeInput);
                        stepResult.Output = switchBranch;
                        if (adjacency.ContainsKey(node.Id))
                        {
                            foreach (var (targetId, branch) in adjacency[node.Id])
                            {
                                if (branch == switchBranch || branch == "default" || string.IsNullOrEmpty(branch))
                                {
                                    nodeOutputs[node.Id] = nodeInput;
                                    if (!visited.Contains(targetId)) queue.Enqueue(targetId);
                                }
                            }
                        }
                        break;

                    case "user_approval":
                        stepResult.Output = $"[Awaiting Approval]\n{nodeInput}";
                        break;

                    case "merge":
                        stepResult.Output = nodeInput; // inputs already joined above
                        break;

                    case "delay":
                        var delaySecs = node.DelaySeconds > 0 ? node.DelaySeconds : 1;
                        await Task.Delay(Math.Min(delaySecs, 10) * 1000, cts.Token);
                        stepResult.Output = nodeInput;
                        break;

                    case "transform":
                        stepResult.Output = ApplyTransform(node.TransformExpression, nodeInput);
                        break;

                    case "set_variable":
                        var varValue = (node.VariableValue ?? "{{input}}").Replace("{{input}}", nodeInput);
                        stepResult.Output = varValue;
                        break;

                    case "format":
                        var template = node.FormatTemplate ?? "{{input}}";
                        stepResult.Output = template.Replace("{{input}}", nodeInput);
                        break;

                    case "filter":
                        var passes = EvaluateFilter(node.FilterExpression, nodeInput);
                        stepResult.Output = passes ? nodeInput : "";
                        if (!passes) stepResult.Error = "Filtered out — condition not met";
                        break;

                    case "aggregate":
                        stepResult.Output = nodeInput; // multi-input already joined
                        break;

                    case "loop":
                        stepResult.Output = nodeInput; // loop logic handled by graph traversal
                        break;

                    case "output":
                        stepResult.Output = nodeInput;
                        break;

                    default:
                        stepResult.Output = nodeInput;
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing node {NodeId} in chain {ChainId}", node.Id, chainId);
                stepResult.Output = $"Error: {ex.Message}";
                stepResult.Error = ex.Message;
            }

            nodeOutputs[node.Id] = stepResult.Output;
            stepResults.Add(stepResult);

            // Enqueue connected nodes (skip for condition nodes, handled above)
            if (node.NodeType != "condition" && adjacency.ContainsKey(node.Id))
            {
                foreach (var (targetId, _) in adjacency[node.Id])
                {
                    if (!visited.Contains(targetId))
                        queue.Enqueue(targetId);
                }
            }
        }

        // Find output node result
        var outputNode = chain.Nodes.FirstOrDefault(n => n.NodeType == "output");
        var finalOutput = outputNode != null && nodeOutputs.ContainsKey(outputNode.Id)
            ? nodeOutputs[outputNode.Id]
            : stepResults.LastOrDefault()?.Output ?? "";

        var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

        return new ChainExecutionResult
        {
            FinalOutput = finalOutput,
            StepResults = stepResults,
            TotalTokens = totalTokens,
            ExecutionTimeMs = executionTime
        };
    }

    private bool EvaluateCondition(string? expression, string input)
    {
        if (string.IsNullOrEmpty(expression)) return true;

        var expr = expression.ToLower().Trim();

        // Simple condition evaluations
        if (expr.StartsWith("contains:"))
        {
            var searchTerm = expression.Substring(9).Trim();
            return input.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);
        }
        if (expr.StartsWith("not_contains:"))
        {
            var searchTerm = expression.Substring(13).Trim();
            return !input.Contains(searchTerm, StringComparison.OrdinalIgnoreCase);
        }
        if (expr.StartsWith("length_gt:"))
        {
            if (int.TryParse(expression.Substring(10).Trim(), out var len))
                return input.Length > len;
        }
        if (expr.StartsWith("length_lt:"))
        {
            if (int.TryParse(expression.Substring(10).Trim(), out var len))
                return input.Length < len;
        }
        if (expr == "is_empty")
            return string.IsNullOrWhiteSpace(input);
        if (expr == "is_not_empty")
            return !string.IsNullOrWhiteSpace(input);

        return true;
    }

    private string ApplyTransform(string? expression, string input)
    {
        if (string.IsNullOrEmpty(expression)) return input;

        var expr = expression.ToLower().Trim();

        return expr switch
        {
            "uppercase" => input.ToUpper(),
            "lowercase" => input.ToLower(),
            "trim" => input.Trim(),
            "first_line" => input.Split('\n').FirstOrDefault() ?? input,
            "last_line" => input.Split('\n').LastOrDefault() ?? input,
            "word_count" => input.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length.ToString(),
            "char_count" => input.Length.ToString(),
            "reverse" => new string(input.Reverse().ToArray()),
            _ when expr.StartsWith("first:") => int.TryParse(expression.Substring(6).Trim(), out var n)
                ? input.Length > n ? input.Substring(0, n) : input
                : input,
            _ when expr.StartsWith("last:") => int.TryParse(expression.Substring(5).Trim(), out var n)
                ? input.Length > n ? input.Substring(input.Length - n) : input
                : input,
            _ when expr.StartsWith("replace:") => ApplyReplace(expression.Substring(8), input),
            _ => input
        };
    }

    private string ApplyReplace(string args, string input)
    {
        var parts = args.Split("->", 2);
        if (parts.Length == 2)
            return input.Replace(parts[0].Trim(), parts[1].Trim());
        return input;
    }

    private string EvaluateSwitch(string? expression, string input)
    {
        if (string.IsNullOrEmpty(expression)) return "default";
        foreach (var line in expression.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("//") || string.IsNullOrEmpty(trimmed)) continue;
            var colonIdx = trimmed.IndexOf(':');
            if (colonIdx < 0) continue;
            var caseLabel = trimmed.Substring(0, colonIdx).Trim();
            var caseExpr = trimmed.Substring(colonIdx + 1).Trim();
            if (EvaluateCondition(caseExpr, input)) return caseLabel;
        }
        return "default";
    }

    private bool EvaluateFilter(string? expression, string input)
    {
        if (string.IsNullOrEmpty(expression)) return true;
        var expr = expression.ToLower().Trim();
        if (expr.StartsWith("min_length:") && int.TryParse(expression.Substring(11).Trim(), out var minLen))
            return input.Length >= minLen;
        if (expr.StartsWith("max_length:") && int.TryParse(expression.Substring(11).Trim(), out var maxLen))
            return input.Length <= maxLen;
        return EvaluateCondition(expression, input);
    }

    private async Task<string> ExecuteHttpRequestAsync(ChainNode node, string nodeInput)
    {
        var url = node.HttpUrl;
        if (string.IsNullOrEmpty(url)) return "[HTTP Request] No URL configured";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var method = (node.HttpMethod ?? "GET").ToUpper();
            var body = node.HttpBody?.Replace("{{input}}", nodeInput);
            HttpResponseMessage result;
            if (method == "GET")
            {
                result = await client.GetAsync(url);
            }
            else
            {
                var content = new StringContent(body ?? "", Encoding.UTF8, "application/json");
                result = method switch
                {
                    "POST" => await client.PostAsync(url, content),
                    "PUT" => await client.PutAsync(url, content),
                    "DELETE" => await client.DeleteAsync(url),
                    _ => await client.PostAsync(url, content)
                };
            }
            var responseText = await result.Content.ReadAsStringAsync();
            return responseText.Length > 2000 ? responseText.Substring(0, 2000) + "..." : responseText;
        }
        catch (Exception ex)
        {
            return $"[HTTP Error] {ex.Message}";
        }
    }

    private async Task<string> ExecuteMcpToolAsync(ChainNode node, string nodeInput)
    {
        if (string.IsNullOrEmpty(node.McpServerUrl))
            return "[MCP] No server URL configured. Double-click the node to set one.";
        if (string.IsNullOrEmpty(node.McpToolName))
            return "[MCP] No tool name configured. Double-click the node to set one.";

        try
        {
            // Build the tool arguments JSON.
            // If McpArguments is supplied, replace {{input}} placeholders; otherwise default to { "input": nodeInput }
            JsonNode argsNode;
            if (!string.IsNullOrWhiteSpace(node.McpArguments))
            {
                var filled = node.McpArguments.Replace("{{input}}", nodeInput);
                argsNode = JsonNode.Parse(filled) ?? new JsonObject { ["input"] = nodeInput };
            }
            else
            {
                argsNode = new JsonObject { ["input"] = nodeInput };
            }

            // Build JSON-RPC 2.0 tools/call request (MCP HTTP transport)
            var rpcRequest = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = node.McpToolName,
                    ["arguments"] = argsNode
                }
            };

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Add("Accept", "application/json, text/event-stream");

            var content = new StringContent(rpcRequest.ToJsonString(), Encoding.UTF8, "application/json");
            var response = await client.PostAsync(node.McpServerUrl, content);
            var rawBody = await response.Content.ReadAsStringAsync();

            // Handle SSE response format (data: {...}\n\n)
            var jsonBody = rawBody.TrimStart();
            if (jsonBody.StartsWith("data:"))
            {
                var lines = jsonBody.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                jsonBody = string.Empty;
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("data:"))
                    {
                        jsonBody = trimmed.Substring(5).Trim();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(jsonBody))
                return $"[MCP] Empty response from server (HTTP {(int)response.StatusCode})";

            // Parse JSON-RPC response
            var rpcResponse = JsonNode.Parse(jsonBody);

            // Check for JSON-RPC error
            var rpcError = rpcResponse?["error"];
            if (rpcError != null)
            {
                var errMsg = rpcError["message"]?.GetValue<string>() ?? rpcError.ToJsonString();
                return $"[MCP Error] {errMsg}";
            }

            // Extract result content array (MCP tools/call standard format)
            var result = rpcResponse?["result"];
            if (result != null)
            {
                var contentArr = result["content"]?.AsArray();
                if (contentArr != null && contentArr.Count > 0)
                {
                    var texts = contentArr
                        .Where(c => c?["type"]?.GetValue<string>() == "text")
                        .Select(c => c?["text"]?.GetValue<string>() ?? "")
                        .Where(t => !string.IsNullOrEmpty(t));
                    var joined = string.Join("\n", texts);
                    if (!string.IsNullOrEmpty(joined)) return joined;
                }
                // Fallback: return raw result JSON
                return result.ToJsonString();
            }

            return jsonBody.Length > 2000 ? jsonBody.Substring(0, 2000) + "…" : jsonBody;
        }
        catch (Exception ex)
        {
            return $"[MCP Error] {ex.Message}";
        }
    }

    private string ExecuteCodeSnippet(string? code, string input)
    {
        if (string.IsNullOrEmpty(code)) return input;
        // Safe built-in transforms only — no eval for security
        var lower = code.ToLower().Trim();
        if (lower.Contains("touppercase()") || lower.Contains(".touppercase")) return input.ToUpper();
        if (lower.Contains("tolowercase()") || lower.Contains(".tolowercase")) return input.ToLower();
        if (lower.Contains(".trim()")) return input.Trim();
        if (lower.Contains(".length")) return input.Length.ToString();
        if (lower.Contains("split") && lower.Contains("length")) return input.Split(' ').Length.ToString();
        return $"[Code] Input processed: {input.Substring(0, Math.Min(200, input.Length))}";
    }
}

// Result types
public class ChainExecutionResult
{
    public string FinalOutput { get; set; } = string.Empty;
    public List<ChainStepResult> StepResults { get; set; } = new();
    public int TotalTokens { get; set; }
    public double ExecutionTimeMs { get; set; }
}

public class ChainStepResult
{
    public int NodeId { get; set; }
    public string NodeType { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Input { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public int TokensUsed { get; set; }
    public string? Error { get; set; }
}
