using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using PromptStudio.Api.Data;
using PromptStudio.Api.Models;
using PromptStudio.Api.Services;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace PromptStudio.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("ai-generation")]
public class ChainsController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly ChainExecutionService _chainService;
    private readonly IAIService _aiService;
    private readonly ILogger<ChainsController> _logger;

    public ChainsController(
        PromptDbContext context,
        ChainExecutionService chainService,
        IAIService aiService,
        ILogger<ChainsController> logger)
    {
        _context = context;
        _chainService = chainService;
        _aiService = aiService;
        _logger = logger;
    }

    // Description plus a short summary of node labels — the searchable text for a chain's graph node.
    private static string ChainCaptureContent(CreateChainRequest request)
    {
        var labels = request.Nodes == null || request.Nodes.Count == 0
            ? string.Empty
            : "Nodes: " + string.Join(", ", request.Nodes
                .Select(n => string.IsNullOrWhiteSpace(n.Label) ? n.NodeType : n.Label)
                .Take(30));
        return $"{request.Description}\n{labels}".Trim();
    }

    private string GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userIdClaim ?? string.Empty;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> GetChains()
    {
        var userId = GetCurrentUserId();
        var chains = await _context.PromptChains
            .Include(c => c.Nodes)
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Description,
                c.Category,
                c.IsPublic,
                c.UsageCount,
                c.CreatedAt,
                c.UpdatedAt,
                NodeCount = c.Nodes.Count
            })
            .ToListAsync();
        return Ok(chains);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult> GetChain(Guid id)
    {
        var userId = GetCurrentUserId();
        var chain = await _context.PromptChains
            .Include(c => c.Nodes)
            .Include(c => c.Edges)
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);

        if (chain == null) return NotFound();
        return Ok(new
        {
            chain.Id,
            chain.Name,
            chain.Description,
            chain.Category,
            chain.IsPublic,
            chain.UsageCount,
            chain.CreatedAt,
            chain.UpdatedAt,
            Nodes = chain.Nodes.Select(n => new
            {
                n.Id,
                n.NodeType,
                n.Label,
                n.PromptId,
                n.InlinePrompt,
                n.PersonaId,
                n.ConditionExpression,
                n.TransformExpression,
                n.SystemPrompt,
                n.ClassifyLabels,
                n.SwitchExpression,
                n.LoopCondition,
                n.LoopMax,
                n.HttpUrl,
                n.HttpMethod,
                n.HttpBody,
                n.CodeSnippet,
                n.VariableName,
                n.VariableValue,
                n.FormatTemplate,
                n.FilterExpression,
                n.DelaySeconds,
                n.NoteText,
                n.MergeStrategy,
                n.MergeSeparator,
                n.FileSearchQuery,
                n.FileSearchTypes,
                n.FileSearchMaxResults,
                n.McpServerUrl,
                n.McpToolName,
                n.McpArguments,
                n.PositionX,
                n.PositionY,
                n.SortOrder
            }),
            Edges = chain.Edges.Select(e => new
            {
                e.Id,
                e.SourceNodeId,
                e.TargetNodeId,
                e.Label,
                e.ConditionBranch
            })
        });
    }

    [HttpPost]
    public async Task<ActionResult> CreateChain([FromBody] CreateChainRequest request)
    {
        var userId = GetCurrentUserId();

        var chain = new PromptChain
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            Description = request.Description,
            Category = request.Category,
            UserId = userId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _context.PromptChains.Add(chain);
        await _context.SaveChangesAsync();

        // Add nodes
        if (request.Nodes != null)
        {
            var nodeIdMap = new Dictionary<int, int>(); // temp ID -> real ID
            foreach (var nodeReq in request.Nodes)
            {
                var node = new ChainNode
                {
                    ChainId = chain.Id,
                    NodeType = nodeReq.NodeType,
                    Label = nodeReq.Label,
                    PromptId = nodeReq.PromptId,
                    InlinePrompt = nodeReq.InlinePrompt,
                    PersonaId = nodeReq.PersonaId,
                    ConditionExpression = nodeReq.ConditionExpression,
                    TransformExpression = nodeReq.TransformExpression,
                    SystemPrompt = nodeReq.SystemPrompt,
                    ClassifyLabels = nodeReq.ClassifyLabels,
                    SwitchExpression = nodeReq.SwitchExpression,
                    LoopCondition = nodeReq.LoopCondition,
                    LoopMax = nodeReq.LoopMax ?? 5,
                    HttpUrl = nodeReq.HttpUrl,
                    HttpMethod = nodeReq.HttpMethod,
                    HttpBody = nodeReq.HttpBody,
                    CodeSnippet = nodeReq.CodeSnippet,
                    VariableName = nodeReq.VariableName,
                    VariableValue = nodeReq.VariableValue,
                    FormatTemplate = nodeReq.FormatTemplate,
                    FilterExpression = nodeReq.FilterExpression,
                    DelaySeconds = nodeReq.DelaySeconds ?? 1,
                    NoteText = nodeReq.NoteText,
                    MergeStrategy = nodeReq.MergeStrategy,
                    MergeSeparator = nodeReq.MergeSeparator,
                    FileSearchQuery = nodeReq.FileSearchQuery,
                    FileSearchTypes = nodeReq.FileSearchTypes,
                    FileSearchMaxResults = nodeReq.FileSearchMaxResults,
                    McpServerUrl = nodeReq.McpServerUrl,
                    McpToolName = nodeReq.McpToolName,
                    McpArguments = nodeReq.McpArguments,
                    PositionX = nodeReq.PositionX,
                    PositionY = nodeReq.PositionY,
                    SortOrder = nodeReq.SortOrder
                };
                _context.ChainNodes.Add(node);
                await _context.SaveChangesAsync();
                nodeIdMap[nodeReq.TempId] = node.Id;
            }

            // Add edges using mapped IDs
            if (request.Edges != null)
            {
                foreach (var edgeReq in request.Edges)
                {
                    if (nodeIdMap.ContainsKey(edgeReq.SourceTempId) && nodeIdMap.ContainsKey(edgeReq.TargetTempId))
                    {
                        var edge = new ChainEdge
                        {
                            ChainId = chain.Id,
                            SourceNodeId = nodeIdMap[edgeReq.SourceTempId],
                            TargetNodeId = nodeIdMap[edgeReq.TargetTempId],
                            Label = edgeReq.Label,
                            ConditionBranch = edgeReq.ConditionBranch
                        };
                        _context.ChainEdges.Add(edge);
                    }
                }
                await _context.SaveChangesAsync();
            }
        }


        return CreatedAtAction(nameof(GetChain), new { id = chain.Id }, new { chain.Id, chain.Name });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateChain(Guid id, [FromBody] CreateChainRequest request)
    {
        var userId = GetCurrentUserId();
        var chain = await _context.PromptChains
            .Include(c => c.Nodes)
            .Include(c => c.Edges)
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);

        if (chain == null) return NotFound();

        chain.Name = request.Name;
        chain.Description = request.Description;
        chain.Category = request.Category;
        chain.UpdatedAt = DateTime.UtcNow;

        // Remove old nodes and edges
        _context.ChainEdges.RemoveRange(chain.Edges);
        _context.ChainNodes.RemoveRange(chain.Nodes);
        await _context.SaveChangesAsync();

        // Re-add nodes and edges
        if (request.Nodes != null)
        {
            var nodeIdMap = new Dictionary<int, int>();
            foreach (var nodeReq in request.Nodes)
            {
                var node = new ChainNode
                {
                    ChainId = chain.Id,
                    NodeType = nodeReq.NodeType,
                    Label = nodeReq.Label,
                    PromptId = nodeReq.PromptId,
                    InlinePrompt = nodeReq.InlinePrompt,
                    PersonaId = nodeReq.PersonaId,
                    ConditionExpression = nodeReq.ConditionExpression,
                    TransformExpression = nodeReq.TransformExpression,
                    SystemPrompt = nodeReq.SystemPrompt,
                    ClassifyLabels = nodeReq.ClassifyLabels,
                    SwitchExpression = nodeReq.SwitchExpression,
                    LoopCondition = nodeReq.LoopCondition,
                    LoopMax = nodeReq.LoopMax ?? 5,
                    HttpUrl = nodeReq.HttpUrl,
                    HttpMethod = nodeReq.HttpMethod,
                    HttpBody = nodeReq.HttpBody,
                    CodeSnippet = nodeReq.CodeSnippet,
                    VariableName = nodeReq.VariableName,
                    VariableValue = nodeReq.VariableValue,
                    FormatTemplate = nodeReq.FormatTemplate,
                    FilterExpression = nodeReq.FilterExpression,
                    DelaySeconds = nodeReq.DelaySeconds ?? 1,
                    NoteText = nodeReq.NoteText,
                    MergeStrategy = nodeReq.MergeStrategy,
                    MergeSeparator = nodeReq.MergeSeparator,
                    FileSearchQuery = nodeReq.FileSearchQuery,
                    FileSearchTypes = nodeReq.FileSearchTypes,
                    FileSearchMaxResults = nodeReq.FileSearchMaxResults,
                    McpServerUrl = nodeReq.McpServerUrl,
                    McpToolName = nodeReq.McpToolName,
                    McpArguments = nodeReq.McpArguments,
                    PositionX = nodeReq.PositionX,
                    PositionY = nodeReq.PositionY,
                    SortOrder = nodeReq.SortOrder
                };
                _context.ChainNodes.Add(node);
                await _context.SaveChangesAsync();
                nodeIdMap[nodeReq.TempId] = node.Id;
            }

            if (request.Edges != null)
            {
                foreach (var edgeReq in request.Edges)
                {
                    if (nodeIdMap.ContainsKey(edgeReq.SourceTempId) && nodeIdMap.ContainsKey(edgeReq.TargetTempId))
                    {
                        var edge = new ChainEdge
                        {
                            ChainId = chain.Id,
                            SourceNodeId = nodeIdMap[edgeReq.SourceTempId],
                            TargetNodeId = nodeIdMap[edgeReq.TargetTempId],
                            Label = edgeReq.Label,
                            ConditionBranch = edgeReq.ConditionBranch
                        };
                        _context.ChainEdges.Add(edge);
                    }
                }
                await _context.SaveChangesAsync();
            }
        }


        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteChain(Guid id)
    {
        var userId = GetCurrentUserId();
        var chain = await _context.PromptChains
            .Include(c => c.Nodes)
            .Include(c => c.Edges)
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);

        if (chain == null) return NotFound();

        _context.ChainEdges.RemoveRange(chain.Edges);
        _context.ChainNodes.RemoveRange(chain.Nodes);
        _context.PromptChains.Remove(chain);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/execute")]
    public async Task<ActionResult> ExecuteChain(Guid id, [FromBody] ExecuteChainRequest request)
    {
        try
        {
            var userId = GetCurrentUserId();
            var result = await _chainService.ExecuteChainAsync(id, request.Input, userId, request.PermissionMode ?? "auto");
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing chain {ChainId}", id);
            return StatusCode(500, new { error = "Failed to execute chain", message = ex.Message });
        }
    }

    [HttpPost("{id}/share")]
    public async Task<IActionResult> ShareChain(Guid id)
    {
        var userId = GetCurrentUserId();
        var chain = await _context.PromptChains.FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId);
        if (chain == null) return NotFound();
        chain.IsPublic = true;
        chain.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        return Ok(new { message = "Chain shared", isPublic = true });
    }

    [HttpPost("ai-command")]
    public async Task<ActionResult<WorkflowAssistantResponse>> GenerateWorkflowSuggestion([FromBody] WorkflowAssistantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
            return BadRequest(new { error = "prompt_required", message = "Prompt is required." });

        if (!_aiService.IsAvailable)
            return StatusCode(503, new { error = "ai_unavailable", message = "No AI provider is configured for workflow automation." });

        try
        {
            var currentNodes = request.Nodes ?? new List<WorkflowAssistantExistingNode>();
            var currentEdges = request.Edges ?? new List<WorkflowAssistantExistingEdge>();
            var selectedNode = request.SelectedNodeTempId.HasValue
                ? currentNodes.FirstOrDefault(node => node.TempId == request.SelectedNodeTempId.Value)
                : null;

            var systemPrompt = """
You help users extend an existing workflow builder with real AI planning.

Rules:
- Never replace, remove, or rewrite existing nodes.
- Only propose NEW nodes and NEW edges that should be appended to the current workflow.
- Assume the frontend will preserve all existing nodes and edges.
- Use only allowed node types:
  input, agent, classify, note, output, prompt, web_search, http_request, file_search, code, mcp,
  condition, switch, loop, user_approval, merge, delay, transform, set_variable, format, filter, aggregate.
- Keep the plan compact and practical.
- If the request is ambiguous, still make the most reasonable append-only suggestion.
- Return JSON only, no markdown fences.

Return exactly this JSON shape:
{
  "message": "short explanation of what was added",
  "shouldConnectFromAnchor": true,
  "nodes": [
    {
      "nodeType": "agent",
      "label": "Draft reply",
      "inlinePrompt": "optional",
      "systemPrompt": "optional",
      "conditionExpression": null,
      "transformExpression": null,
      "httpUrl": null,
      "httpMethod": null,
      "httpBody": null,
      "codeSnippet": null,
      "variableName": null,
      "variableValue": null,
      "formatTemplate": null,
      "filterExpression": null,
      "delaySeconds": null,
      "noteText": null,
      "classifyLabels": null,
      "switchExpression": null,
      "loopCondition": null,
      "loopMax": null,
      "mergeStrategy": null,
      "mergeSeparator": null,
      "fileSearchQuery": null,
      "fileSearchTypes": null,
      "fileSearchMaxResults": null,
      "mcpServerUrl": null,
      "mcpToolName": null,
      "mcpArguments": null,
      "approvalMessage": null,
      "approvalTimeout": null,
      "approvalButtonLabel": null
    }
  ],
  "edges": [
    {
      "sourceIndex": 0,
      "targetIndex": 1,
      "label": null,
      "conditionBranch": null
    }
  ]
}

If only one new node is needed, return one node and an empty edges array.
""";

            var userPrompt = BuildWorkflowAssistantPrompt(request, currentNodes, currentEdges, selectedNode);
            var responseBuilder = new StringBuilder();

            await foreach (var token in _aiService.StreamExecuteAsync(
                systemPrompt,
                new List<ConversationMessage> { new("user", userPrompt) },
                null,
                HttpContext.RequestAborted))
            {
                responseBuilder.Append(token);
            }

            var json = ExtractJsonPayload(responseBuilder.ToString());
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<WorkflowAssistantResponse>(json, options);

            if (parsed == null)
            {
                return StatusCode(502, new { error = "ai_invalid_response", message = "AI returned an empty workflow suggestion." });
            }

            var sanitized = new WorkflowAssistantResponse(
                string.IsNullOrWhiteSpace(parsed.Message) ? "Added AI-generated workflow nodes." : parsed.Message,
                parsed.ShouldConnectFromAnchor,
                parsed.Nodes?.Where(node => !string.IsNullOrWhiteSpace(node.NodeType)).ToList() ?? new List<WorkflowAssistantSuggestedNode>(),
                parsed.Edges ?? new List<WorkflowAssistantSuggestedEdge>());

            return Ok(sanitized);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating AI workflow suggestion");
            return StatusCode(500, new { error = "ai_command_failed", message = ex.Message });
        }
    }

    private static string BuildWorkflowAssistantPrompt(
        WorkflowAssistantRequest request,
        List<WorkflowAssistantExistingNode> nodes,
        List<WorkflowAssistantExistingEdge> edges,
        WorkflowAssistantExistingNode? selectedNode)
    {
        var nodeSummary = nodes.Count == 0
            ? "No existing nodes."
            : string.Join("\n", nodes.Select(node =>
                $"- tempId={node.TempId}, type={node.NodeType}, label={node.Label}, x={node.PositionX}, y={node.PositionY}, prompt={node.InlinePrompt ?? ""}, condition={node.ConditionExpression ?? ""}, transform={node.TransformExpression ?? ""}, url={node.HttpUrl ?? ""}, note={node.NoteText ?? ""}"));

        var edgeSummary = edges.Count == 0
            ? "No existing edges."
            : string.Join("\n", edges.Select(edge =>
                $"- {edge.SourceTempId} -> {edge.TargetTempId}, label={edge.Label ?? ""}"));

        return $"""
User request: {request.Prompt}

Workflow name: {request.ChainName ?? "Untitled workflow"}
Workflow description: {request.ChainDescription ?? ""}

Selected node: {(selectedNode != null ? $"tempId={selectedNode.TempId}, type={selectedNode.NodeType}, label={selectedNode.Label}" : "none")}

Existing nodes:
{nodeSummary}

Existing edges:
{edgeSummary}
""";
    }

    private static string ExtractJsonPayload(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```") )
        {
            var firstBrace = trimmed.IndexOf('{');
            var lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return trimmed[firstBrace..(lastBrace + 1)];
            }
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        return trimmed;
    }

    // POST /chains/ai-connect — AI suggests edges between existing nodes
    [HttpPost("ai-connect")]
    public async Task<ActionResult> AiConnect([FromBody] AiConnectRequest request)
    {
        if (!_aiService.IsAvailable)
            return StatusCode(503, new { error = "ai_unavailable", message = "No AI provider configured." });

        var nodeSummary = request.Nodes == null || request.Nodes.Count == 0
            ? "No nodes."
            : string.Join("\n", request.Nodes.Select(n => $"- tempId={n.TempId}, type={n.NodeType}, label={n.Label}"));

        var systemPrompt = """
You are a workflow architect. Given a list of nodes, suggest a complete set of logical edges (connections) between them.
Return JSON only — no markdown fences:
{
  "message": "one-line summary of the connections",
  "edges": [
    { "sourceTempId": 1, "targetTempId": 2, "label": null, "conditionBranch": null }
  ]
}
Rules:
- Only use tempId values from the provided node list.
- Produce a DAG (no cycles). Start from "input" nodes, end at "output" nodes.
- Connect nodes in a meaningful processing order based on their types and labels.
- Replace ALL existing edges — return the full desired edge set.
- If a "condition" node is present, label its outgoing edges "true" and "false".
""";

        var userMsg = $"Workflow: {request.ChainName ?? "Untitled"}\n\nNodes:\n{nodeSummary}";

        var sb = new StringBuilder();
        await foreach (var token in _aiService.StreamExecuteAsync(systemPrompt,
            new List<ConversationMessage> { new("user", userMsg) }, null, HttpContext.RequestAborted))
            sb.Append(token);

        var json = ExtractJsonPayload(sb.ToString());
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var parsed = JsonSerializer.Deserialize<AiConnectResponse>(json, opts);
        return Ok(parsed ?? new AiConnectResponse("Could not parse AI response.", new List<AiEdgeSuggestion>()));
    }

    // POST /chains/ai-layout — AI suggests x/y positions for each node
    [HttpPost("ai-layout")]
    public async Task<ActionResult> AiLayout([FromBody] AiLayoutRequest request)
    {
        if (!_aiService.IsAvailable)
            return StatusCode(503, new { error = "ai_unavailable", message = "No AI provider configured." });

        var nodeSummary = request.Nodes == null || request.Nodes.Count == 0
            ? "No nodes."
            : string.Join("\n", request.Nodes.Select(n => $"- tempId={n.TempId}, type={n.NodeType}, label={n.Label}, x={n.PositionX}, y={n.PositionY}"));
        var edgeSummary = request.Edges == null || request.Edges.Count == 0
            ? "No edges."
            : string.Join("\n", request.Edges.Select(e => $"- {e.SourceTempId} -> {e.TargetTempId}"));

        var systemPrompt = """
You are a diagram layout engine. Given nodes and edges, return optimal x/y positions for each node.
Return JSON only — no markdown fences:
{
  "message": "one-line summary",
  "positions": [
    { "tempId": 1, "positionX": 100, "positionY": 200 }
  ]
}
Rules:
- Every tempId in the input must appear in the output.
- Left-to-right flow: input nodes near x=80, output nodes at the far right.
- Parallel branches spread on the y axis; sequential nodes advance on the x axis.
- Minimum 240px horizontal gap and 140px vertical gap between nodes.
- Keep positions non-negative; canvas origin is (0,0).
""";

        var userMsg = $"Workflow: {request.ChainName ?? "Untitled"}\n\nNodes:\n{nodeSummary}\n\nEdges:\n{edgeSummary}";

        var sb = new StringBuilder();
        await foreach (var token in _aiService.StreamExecuteAsync(systemPrompt,
            new List<ConversationMessage> { new("user", userMsg) }, null, HttpContext.RequestAborted))
            sb.Append(token);

        var json = ExtractJsonPayload(sb.ToString());
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var parsed = JsonSerializer.Deserialize<AiLayoutResponse>(json, opts);
        return Ok(parsed ?? new AiLayoutResponse("Could not parse AI response.", new List<AiPositionSuggestion>()));
    }

    [AllowAnonymous]
    [HttpGet("public")]
    public async Task<ActionResult> GetPublicChains(
        [FromQuery] string? category = null,
        [FromQuery] string? search = null)
    {
        var query = _context.PromptChains
            .Include(c => c.User)
            .Include(c => c.Nodes)
            .Where(c => c.IsPublic);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(c => c.Category != null && c.Category.ToLower() == category.ToLower());

        if (!string.IsNullOrEmpty(search))
        {
            var s = search.ToLower();
            query = query.Where(c => c.Name.ToLower().Contains(s) || (c.Description != null && c.Description.ToLower().Contains(s)));
        }

        var chains = await query.OrderByDescending(c => c.UsageCount).Take(50).Select(c => new
        {
            c.Id,
            c.Name,
            c.Description,
            c.Category,
            c.UsageCount,
            c.CreatedAt,
            NodeCount = c.Nodes.Count,
            AuthorName = c.User != null ? c.User.Name : "Anonymous"
        }).ToListAsync();

        return Ok(chains);
    }
}

// Request DTOs
public record CreateChainRequest(
    string Name,
    string? Description,
    string? Category,
    List<CreateChainNodeRequest>? Nodes,
    List<CreateChainEdgeRequest>? Edges);

public record CreateChainNodeRequest(
    int TempId,
    string NodeType,
    string? Label,
    string? PromptId,
    string? InlinePrompt,
    int? PersonaId,
    string? ConditionExpression,
    string? TransformExpression,
    string? SystemPrompt,
    string? ClassifyLabels,
    string? SwitchExpression,
    string? LoopCondition,
    int? LoopMax,
    string? HttpUrl,
    string? HttpMethod,
    string? HttpBody,
    string? CodeSnippet,
    string? VariableName,
    string? VariableValue,
    string? FormatTemplate,
    string? FilterExpression,
    int? DelaySeconds,
    string? NoteText,
    string? MergeStrategy,
    string? MergeSeparator,
    string? FileSearchQuery,
    string? FileSearchTypes,
    int? FileSearchMaxResults,
    string? McpServerUrl,
    string? McpToolName,
    string? McpArguments,
    double PositionX,
    double PositionY,
    int SortOrder);

public record CreateChainEdgeRequest(
    int SourceTempId,
    int TargetTempId,
    string? Label,
    string? ConditionBranch);

public record ExecuteChainRequest(string Input, string? PermissionMode = "auto");

public record WorkflowAssistantRequest(
    string Prompt,
    string? ChainName,
    string? ChainDescription,
    List<WorkflowAssistantExistingNode>? Nodes,
    List<WorkflowAssistantExistingEdge>? Edges,
    int? SelectedNodeTempId);

public record WorkflowAssistantExistingNode(
    int TempId,
    string NodeType,
    string Label,
    double PositionX,
    double PositionY,
    string? InlinePrompt,
    string? SystemPrompt,
    string? ConditionExpression,
    string? TransformExpression,
    string? HttpUrl,
    string? HttpMethod,
    string? NoteText,
    string? VariableName,
    string? VariableValue,
    string? ClassifyLabels);

public record WorkflowAssistantExistingEdge(int SourceTempId, int TargetTempId, string? Label);

public record WorkflowAssistantResponse(
    string Message,
    bool ShouldConnectFromAnchor,
    List<WorkflowAssistantSuggestedNode>? Nodes,
    List<WorkflowAssistantSuggestedEdge>? Edges);

public record WorkflowAssistantSuggestedNode(
    string NodeType,
    string? Label,
    string? InlinePrompt,
    string? SystemPrompt,
    string? ConditionExpression,
    string? TransformExpression,
    string? HttpUrl,
    string? HttpMethod,
    string? HttpBody,
    string? CodeSnippet,
    string? VariableName,
    string? VariableValue,
    string? FormatTemplate,
    string? FilterExpression,
    int? DelaySeconds,
    string? NoteText,
    string? ClassifyLabels,
    string? SwitchExpression,
    string? LoopCondition,
    int? LoopMax,
    string? MergeStrategy,
    string? MergeSeparator,
    string? FileSearchQuery,
    string? FileSearchTypes,
    int? FileSearchMaxResults,
    string? McpServerUrl,
    string? McpToolName,
    string? McpArguments,
    string? ApprovalMessage,
    int? ApprovalTimeout,
    string? ApprovalButtonLabel);

public record WorkflowAssistantSuggestedEdge(int SourceIndex, int TargetIndex, string? Label, string? ConditionBranch);

// ai-connect DTOs
public record AiConnectNodeRef(int TempId, string NodeType, string Label);
public record AiConnectEdgeRef(int SourceTempId, int TargetTempId);
public record AiConnectRequest(string? ChainName, List<AiConnectNodeRef>? Nodes);
public record AiEdgeSuggestion(int SourceTempId, int TargetTempId, string? Label, string? ConditionBranch);
public record AiConnectResponse(string Message, List<AiEdgeSuggestion>? Edges);

// ai-layout DTOs
public record AiLayoutNodeRef(int TempId, string NodeType, string Label, double PositionX, double PositionY);
public record AiLayoutEdgeRef(int SourceTempId, int TargetTempId);
public record AiLayoutRequest(string? ChainName, List<AiLayoutNodeRef>? Nodes, List<AiLayoutEdgeRef>? Edges);
public record AiPositionSuggestion(int TempId, double PositionX, double PositionY);
public record AiLayoutResponse(string Message, List<AiPositionSuggestion>? Positions);
