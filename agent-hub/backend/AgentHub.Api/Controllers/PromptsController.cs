using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using System.Security.Claims;
using IConfiguration = Microsoft.Extensions.Configuration.IConfiguration;

namespace AgentHub.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
[EnableRateLimiting("ai-generation")]
public class PromptsController : ControllerBase
{
    private readonly PromptDbContext _context;
    private readonly PromptFileService _fileService;
    private readonly IAIService _aiService;
    private readonly AIServiceManager _aiServiceManager;
    private readonly ILogger<PromptsController> _logger;
    private readonly IConfiguration _configuration;
    private readonly ModelExecutionService _exec;
    private readonly JsonSchemaValidator _schemaValidator;
    private readonly PromptLinter _linter;
    private readonly TeamService _teams;

    public PromptsController(
        PromptDbContext context,
        PromptFileService fileService,
        IAIService aiService,
        AIServiceManager aiServiceManager,
        ILogger<PromptsController> logger,
        IConfiguration configuration,
        ModelExecutionService exec,
        JsonSchemaValidator schemaValidator,
        PromptLinter linter,
        TeamService teams)
    {
        _context = context;
        _fileService = fileService;
        _aiService = aiService;
        _aiServiceManager = aiServiceManager;
        _logger = logger;
        _configuration = configuration;
        _exec = exec;
        _schemaValidator = schemaValidator;
        _linter = linter;
        _teams = teams;
    }

    private string GetCurrentUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userIdClaim ?? string.Empty;
    }

    private async Task CreatePromptVersionAsync(Prompt prompt, string userId, string status, string? changeNote)
    {
        var version = new PromptVersion
        {
            PromptId = prompt.Id,
            UserId = userId,
            VersionNumber = prompt.CurrentVersion,
            Name = prompt.Name,
            Content = prompt.Content,
            Category = prompt.Category,
            Description = prompt.Description,
            Status = status,
            ChangeNote = changeNote,
            CreatedAt = DateTime.UtcNow
        };

        _context.PromptVersions.Add(version);
        await _context.SaveChangesAsync();
    }

    private async Task LogPromptAuditAsync(string promptId, string userId, string eventType, object details)
    {
        _context.PromptAuditEvents.Add(new PromptAuditEvent
        {
            PromptId = promptId,
            UserId = userId,
            EventType = eventType,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(details),
            CreatedAt = DateTime.UtcNow
        });
        await _context.SaveChangesAsync();
    }

    [HttpGet("models")]
    [AllowAnonymous]
    public IActionResult GetAvailableModels()
    {
        var models = _aiServiceManager.GetProviderStatuses()
            .Where(p => p.IsAvailable)
            .SelectMany(p => p.SupportedModels.Select(m => new { provider = p.Name, model = m }))
            .ToList();
        return Ok(models);
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Prompt>>> GetPrompts()
    {
        var userId = GetCurrentUserId();
        return await _context.Prompts.Where(p => p.IsActive && p.UserId == userId).ToListAsync();
    }

    [HttpGet("deleted")]
    public async Task<ActionResult<IEnumerable<Prompt>>> GetDeletedPrompts()
    {
        var userId = GetCurrentUserId();
        return await _context.Prompts.Where(p => !p.IsActive && p.UserId == userId).ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Prompt>> GetPrompt(string id)
    {
        var userId = GetCurrentUserId();
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

        if (prompt == null)
        {
            return NotFound();
        }

        return prompt;
    }

    [HttpGet("categories")]
    public async Task<ActionResult<IEnumerable<string>>> GetCategories()
    {
        var userId = GetCurrentUserId();
        var categories = await _context.Prompts
            .Where(p => p.IsActive && p.UserId == userId && !string.IsNullOrEmpty(p.Category))
            .Select(p => p.Category!)
            .Distinct()
            .ToListAsync();

        return categories;
    }

    [HttpPost]
    public async Task<ActionResult<Prompt>> CreatePrompt(Prompt prompt)
    {
        var userId = GetCurrentUserId();
        prompt.UserId = userId;
        prompt.CreatedAt = DateTime.UtcNow;
        prompt.UpdatedAt = DateTime.UtcNow;
        prompt.CurrentVersion = Math.Max(prompt.CurrentVersion, 1);
        prompt.ApprovalStatus = prompt.RequiresApproval ? "pending" : (string.IsNullOrWhiteSpace(prompt.ApprovalStatus) ? "draft" : prompt.ApprovalStatus);

        _context.Prompts.Add(prompt);
        await _context.SaveChangesAsync();
        await CreatePromptVersionAsync(prompt, userId, prompt.ApprovalStatus, "Initial version");
        await LogPromptAuditAsync(prompt.Id, userId, "created", new { prompt.Name, prompt.Category, prompt.RequiresApproval, prompt.IsPublic });

        // Save to file for caching
        await _fileService.SavePromptToFileAsync(prompt);


        return CreatedAtAction(nameof(GetPrompt), new { id = prompt.Id }, prompt);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdatePrompt(string id, Prompt prompt)
    {
        if (id != prompt.Id)
        {
            return BadRequest();
        }

        var existingPrompt = await _context.Prompts.FindAsync(id);
        if (existingPrompt == null)
        {
            return NotFound();
        }

        // Ownership check. Without this any authenticated user could edit any
        // prompt by guessing an id; team members reach team prompts through
        // their role instead.
        var userId = GetCurrentUserId();
        if (existingPrompt.UserId != userId)
        {
            if (existingPrompt.TeamId == null) return Forbid();

            var role = await _teams.RoleOfAsync(existingPrompt.TeamId, userId);
            if (!TeamRoles.CanEdit(role)) return Forbid();
        }

        // An editor on an approval-gated team proposes rather than commits.
        if (await _teams.RequiresApprovalAsync(existingPrompt, userId))
        {
            var pending = new PromptApproval
            {
                PromptId = existingPrompt.Id,
                TeamId = existingPrompt.TeamId!,
                RequestedByUserId = userId,
                BaseContent = existingPrompt.Content,
                BaseVersion = existingPrompt.CurrentVersion,
                ProposedName = prompt.Name,
                ProposedContent = prompt.Content,
                ProposedCategory = prompt.Category,
                ProposedDescription = prompt.Description,
                ChangeNote = "Submitted from the prompt editor"
            };
            _context.PromptApprovals.Add(pending);
            await _context.SaveChangesAsync();

            await LogPromptAuditAsync(existingPrompt.Id, userId, "approval-requested", new { pending.Id });

            return Accepted(new
            {
                status = "pending_approval",
                approvalId = pending.Id,
                message = "Your change was submitted for approval."
            });
        }

        existingPrompt.Name = prompt.Name;
        existingPrompt.Content = prompt.Content;
        existingPrompt.Category = prompt.Category;
        existingPrompt.Description = prompt.Description;
        existingPrompt.IsActive = prompt.IsActive;
        existingPrompt.IsTemplate = prompt.IsTemplate;
        existingPrompt.IsPublic = prompt.IsPublic;
        existingPrompt.Difficulty = prompt.Difficulty;
        existingPrompt.RequiresApproval = prompt.RequiresApproval;
        existingPrompt.ApprovalStatus = prompt.ApprovalStatus;
        existingPrompt.ApprovedAt = prompt.ApprovedAt;
        existingPrompt.CurrentVersion += 1;
        existingPrompt.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _context.SaveChangesAsync();
            await CreatePromptVersionAsync(existingPrompt, existingPrompt.UserId, existingPrompt.ApprovalStatus, "Prompt updated");
            await LogPromptAuditAsync(existingPrompt.Id, existingPrompt.UserId, "updated", new { existingPrompt.Name, existingPrompt.CurrentVersion, existingPrompt.IsPublic, existingPrompt.RequiresApproval });
            
            // Update the cached file
            await _fileService.SavePromptToFileAsync(existingPrompt);

        }
        catch (DbUpdateConcurrencyException)
        {
            if (!PromptExists(id))
            {
                return NotFound();
            }
            throw;
        }

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePrompt(string id)
    {
        var prompt = await _context.Prompts.FindAsync(id);
        if (prompt == null)
        {
            return NotFound();
        }

        _context.Prompts.Remove(prompt);
        await _context.SaveChangesAsync();
        await LogPromptAuditAsync(prompt.Id, prompt.UserId, "deleted", new { prompt.Name });

        // Delete the cached file
        _fileService.DeletePromptFile(id, prompt.Name);

        return NoContent();
    }

    private async Task<ActionResult?> CheckContentModeration(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var moderation = await _aiService.ModerateContentAsync(text);
            if (moderation.IsFlagged)
            {
                _logger.LogWarning("Content moderation flagged input: {Categories}", string.Join(", ", moderation.FlaggedCategories));
                return BadRequest(new { error = "content_violation", message = "Sorry, we can't help with that. Your request contains content that violates our usage policy. Please try a different request." });
            }
        }
        return null;
    }

    [HttpPost("test")]
    public async Task<ActionResult<PromptTestResponse>> TestPrompt([FromBody] PromptTestRequest request)
    {
        try
        {
            var blocked = await CheckContentModeration(request.PromptContent, request.UserInput);
            if (blocked != null) return blocked;

            // No model asked for and no schema to honour: keep the original
            // path, which lets the provider chain pick and costs nothing extra.
            if (string.IsNullOrWhiteSpace(request.Model) && string.IsNullOrWhiteSpace(request.OutputSchema))
            {
                var plain = await _aiService.TestPromptAsync(request);
                return Ok(plain);
            }

            var chain = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.Model)) chain.Add(request.Model!);

            var outcome = await _exec.RunWithFallbackAsync(
                request.PromptContent, request.UserInput, chain, request.ContextDocuments);

            if (outcome.Error != null)
                return StatusCode(502, new { error = "The model could not be reached.", message = outcome.Error });

            var response = new PromptTestResponse
            {
                Result = outcome.Output ?? string.Empty,
                Model = outcome.Model,
                TokensUsed = outcome.TokensUsed,
                EstimatedCostUsd = outcome.CostUsd,
                LatencyMs = outcome.LatencyMs,
                FellBackFrom = outcome.FellBackFrom
            };

            if (!string.IsNullOrWhiteSpace(request.OutputSchema))
            {
                var validation = _schemaValidator.Validate(outcome.Output, request.OutputSchema);
                response.SchemaValid = validation.IsValid;
                response.SchemaErrors = validation.Errors;
            }

            await _exec.RecordTraceAsync(GetCurrentUserId(), null, "Prompt test", "ui", outcome, request.UserInput);

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing prompt");
            return StatusCode(500, new { error = "Failed to test prompt", message = ex.Message });
        }
    }

    // POST /api/prompts/{id}/run - execute a saved prompt honouring its own settings
    //
    // This is the endpoint that makes PreferredModel, fallback chains and
    // EnforceSchema mean something. /test is a scratchpad; this is the prompt
    // running the way its author configured it.
    [HttpPost("{id}/run")]
    public async Task<ActionResult> RunPrompt(string id, [FromBody] RunPromptRequest request)
    {
        var userId = GetCurrentUserId();
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (prompt == null) return NotFound();

        var blocked = await CheckContentModeration(request.UserInput);
        if (blocked != null) return blocked;

        var chain = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.ModelOverride)) chain.Add(request.ModelOverride!);
        else if (!string.IsNullOrWhiteSpace(prompt.PreferredModel)) chain.Add(prompt.PreferredModel!);
        chain.AddRange(SafeList(prompt.FallbackModelsJson).Where(m => !chain.Contains(m)));

        var outcome = await _exec.RunWithFallbackAsync(prompt.Content, request.UserInput, chain);

        await _exec.RecordTraceAsync(userId, prompt.Id, prompt.Name, "ui", outcome, request.UserInput);

        if (outcome.Error != null)
            return StatusCode(502, new { error = "Every model in the chain failed.", message = outcome.Error });

        SchemaValidationResult? validation = null;
        if (!string.IsNullOrWhiteSpace(prompt.OutputSchema))
        {
            validation = _schemaValidator.Validate(outcome.Output, prompt.OutputSchema);

            // EnforceSchema means the caller would rather have an error than a
            // response in the wrong shape — usually because something
            // downstream is about to parse it.
            if (prompt.EnforceSchema && !validation.IsValid)
                return UnprocessableEntity(new
                {
                    error = "The response did not match the required output schema.",
                    schemaErrors = validation.Errors,
                    rawOutput = outcome.Output
                });
        }

        return Ok(new
        {
            result = outcome.Output,
            model = outcome.Model,
            provider = outcome.Provider,
            tokensUsed = outcome.TokensUsed,
            estimatedCostUsd = outcome.CostUsd,
            latencyMs = outcome.LatencyMs,
            fellBackFrom = outcome.FellBackFrom,
            schemaValid = validation?.IsValid,
            schemaErrors = validation?.Errors
        });
    }


    // POST /api/prompts/compare - Multi-model comparison
    //
    // Routes every model through AIServiceManager rather than a raw OpenAI
    // client, so Bedrock, Azure and Ollama models are comparable against OpenAI
    // ones instead of erroring out. Prices come from the shared table so the
    // grid and the cost dashboard can never disagree.
    [HttpPost("compare")]
    public async Task<ActionResult<ComparePromptsResponse>> ComparePrompts([FromBody] ComparePromptsRequest request)
    {
        try
        {
            var blocked = await CheckContentModeration(request.PromptContent, request.UserInput);
            if (blocked != null) return blocked;

            if (request.Models == null || request.Models.Count == 0)
                return BadRequest(new { error = "At least one model must be selected." });

            if (request.Models.Count > 6)
                return BadRequest(new { error = "Compare at most 6 models at a time." });

            var userId = GetCurrentUserId();

            var tasks = request.Models.Distinct().Select(model =>
                _exec.RunAsync(request.PromptContent, request.UserInput, model, request.ContextDocuments));

            var outcomes = await Task.WhenAll(tasks);

            // Grading is opt-in: it doubles the calls, and a raw side-by-side is
            // often all the user wants.
            var scores = new Dictionary<string, int?>();
            if (request.ScoreOutputs)
            {
                var criteria = string.IsNullOrWhiteSpace(request.ScoringCriteria)
                    ? "Accuracy, relevance to the input, coherence, and adherence to any instructions in the system prompt."
                    : request.ScoringCriteria!;

                foreach (var o in outcomes.Where(o => o.Error == null && !string.IsNullOrWhiteSpace(o.Output)))
                    scores[o.Model] = await _exec.ScoreOutputAsync(o.Output!, criteria);
            }

            foreach (var o in outcomes)
            {
                await _exec.RecordTraceAsync(
                    userId, request.PromptId, request.PromptName ?? "Model comparison",
                    "compare", o, request.UserInput);
            }

            var results = outcomes.Select(o => new ModelComparisonResult
            {
                Model = o.Model,
                Result = o.Output,
                Error = o.Error,
                TokensUsed = o.TokensUsed,
                LatencyMs = o.LatencyMs,
                EstimatedCostUsd = o.CostUsd,
                CostIsEstimate = !o.CostIsKnown,
                Provider = o.Provider,
                Score = scores.GetValueOrDefault(o.Model)
            }).ToList();

            var response = new ComparePromptsResponse { Results = results };

            // The recommendation is the whole point of the grid: cheapest thing
            // that was actually good enough, not simply the cheapest.
            var eligible = results.Where(r => r.Error == null).ToList();
            if (eligible.Count > 0)
            {
                var scored = eligible.Where(r => r.Score.HasValue).ToList();
                if (scored.Count > 0)
                {
                    var best = scored.Max(r => r.Score!.Value);
                    var threshold = Math.Max(1, best - 1);
                    response.RecommendedModel = scored
                        .Where(r => r.Score >= threshold)
                        .OrderBy(r => r.EstimatedCostUsd)
                        .ThenBy(r => r.LatencyMs)
                        .First().Model;
                    response.RecommendationReason =
                        $"Cheapest model scoring within 1 point of the best ({best}/10).";
                }
                else
                {
                    response.RecommendedModel = eligible.OrderBy(r => r.EstimatedCostUsd).ThenBy(r => r.LatencyMs).First().Model;
                    response.RecommendationReason = "Cheapest model that answered. Enable scoring to weigh quality too.";
                }
            }

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error comparing prompts");
            return StatusCode(500, new { error = "Failed to compare prompts", message = ex.Message });
        }
    }

    // GET /api/prompts/models/pricing - the price table behind every cost figure
    [HttpGet("models/pricing")]
    [AllowAnonymous]
    public ActionResult GetModelPricing()
    {
        var rows = ModelPricing.All.Select(kv => new
        {
            model = kv.Key,
            provider = kv.Value.Provider,
            inputPerMillion = kv.Value.InputPerMillion,
            outputPerMillion = kv.Value.OutputPerMillion
        }).OrderBy(r => r.provider).ThenBy(r => r.inputPerMillion);

        return Ok(new { models = rows });
    }

    // PUT /api/prompts/{id}/model-settings - persist the winner of a comparison
    [HttpPut("{id}/model-settings")]
    public async Task<ActionResult> UpdateModelSettings(string id, [FromBody] UpdateModelSettingsRequest request)
    {
        var userId = GetCurrentUserId();
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (prompt == null) return NotFound();

        prompt.PreferredModel = request.PreferredModel;
        prompt.CostOptimalRouting = request.CostOptimalRouting;
        prompt.RoutingMinScore = Math.Clamp(request.RoutingMinScore, 1, 10);

        if (request.FallbackModels != null)
            prompt.FallbackModelsJson = System.Text.Json.JsonSerializer.Serialize(request.FallbackModels);

        prompt.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await LogPromptAuditAsync(prompt.Id, userId, "model-settings-changed",
            new { prompt.PreferredModel, prompt.CostOptimalRouting, prompt.RoutingMinScore });

        return Ok(new
        {
            prompt.Id,
            prompt.PreferredModel,
            prompt.CostOptimalRouting,
            prompt.RoutingMinScore,
            fallbackModels = SafeList(prompt.FallbackModelsJson)
        });
    }

    // PUT /api/prompts/{id}/schema - structured output contract
    [HttpPut("{id}/schema")]
    public async Task<ActionResult> UpdateSchema(string id, [FromBody] UpdateSchemaRequest request)
    {
        var userId = GetCurrentUserId();
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (prompt == null) return NotFound();

        // Reject a schema that is not itself valid JSON now, rather than
        // silently failing every execution later.
        if (!string.IsNullOrWhiteSpace(request.OutputSchema))
        {
            try { using var _ = System.Text.Json.JsonDocument.Parse(request.OutputSchema); }
            catch (Exception ex) { return BadRequest(new { error = "The schema is not valid JSON.", message = ex.Message }); }
        }

        prompt.OutputSchema = string.IsNullOrWhiteSpace(request.OutputSchema) ? null : request.OutputSchema;
        prompt.EnforceSchema = request.EnforceSchema && prompt.OutputSchema != null;
        prompt.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return Ok(new { prompt.Id, prompt.OutputSchema, prompt.EnforceSchema });
    }

    // POST /api/prompts/validate-schema - check output against a schema without saving
    [HttpPost("validate-schema")]
    public ActionResult ValidateSchema([FromBody] ValidateSchemaRequest request)
    {
        var result = _schemaValidator.Validate(request.Output, request.Schema);
        return Ok(new
        {
            isValid = result.IsValid,
            errors = result.Errors,
            extractedJson = JsonSchemaValidator.ExtractJson(request.Output)
        });
    }

    // POST /api/prompts/lint - static analysis before you spend a token running it
    [HttpPost("lint")]
    public async Task<ActionResult> LintPrompt([FromBody] LintPromptRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "Nothing to lint." });

        var report = request.UseAi
            ? await _linter.LintWithAiAsync(request.Content, request.Variables)
            : _linter.Lint(request.Content, request.Variables);

        return Ok(new
        {
            score = report.Score,
            hasErrors = report.HasErrors,
            findings = report.Findings.Select(f => new
            {
                severity = f.Severity,
                rule = f.Rule,
                message = f.Message,
                suggestion = f.Suggestion
            })
        });
    }

    // GET /api/prompts/{id}/versions/{a}/diff/{b} - what actually changed
    [HttpGet("{id}/versions/{versionA:int}/diff/{versionB:int}")]
    public async Task<ActionResult> DiffVersions(string id, int versionA, int versionB)
    {
        var userId = GetCurrentUserId();

        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (prompt == null) return NotFound();

        var versions = await _context.PromptVersions
            .Where(v => v.PromptId == id && (v.VersionNumber == versionA || v.VersionNumber == versionB))
            .ToListAsync();

        var a = versions.FirstOrDefault(v => v.VersionNumber == versionA);
        var b = versions.FirstOrDefault(v => v.VersionNumber == versionB);

        // The newest version often has no snapshot row yet — it is the live
        // prompt. Fall back to it rather than reporting the diff as impossible.
        var aContent = a?.Content ?? (versionA == prompt.CurrentVersion ? prompt.Content : null);
        var bContent = b?.Content ?? (versionB == prompt.CurrentVersion ? prompt.Content : null);

        if (aContent == null || bContent == null)
            return NotFound(new { error = "One or both versions could not be found." });

        return Ok(new
        {
            promptId = id,
            versionA = new { version = versionA, name = a?.Name ?? prompt.Name, content = aContent, createdAt = a?.CreatedAt },
            versionB = new { version = versionB, name = b?.Name ?? prompt.Name, content = bContent, createdAt = b?.CreatedAt },
            diff = TextDiff.Compute(aContent, bContent),
            stats = TextDiff.Stats(aContent, bContent)
        });
    }

    private static List<string> SafeList(string json)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    // ── Ratings loop ────────────────────────────────────────────────────────

    // GET /api/prompts/{id}/insights - what the thumbs actually say about THIS prompt
    [HttpGet("{id}/insights")]
    public async Task<ActionResult> GetPromptInsights(string id, [FromQuery] int days = 30)
    {
        var userId = GetCurrentUserId();
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (prompt == null) return NotFound();

        var since = DateTime.UtcNow.AddDays(-Math.Clamp(days, 1, 365));

        var executions = await _context.PromptExecutions
            .Where(e => e.PromptId == id && e.UserId == userId && e.CreatedAt >= since)
            .Select(e => new { e.Id, e.Model, e.Rating, e.RatingNote, e.TotalTokensUsed, e.CreatedAt })
            .ToListAsync();

        var rated = executions.Where(e => e.Rating != 0).ToList();
        var up = rated.Count(e => e.Rating == 1);
        var down = rated.Count(e => e.Rating == -1);

        // Which model gets the complaints. This is the number that tells you
        // whether the prompt is wrong or the model is.
        var byModel = executions
            .GroupBy(e => e.Model)
            .Select(g => new
            {
                model = g.Key,
                runs = g.Count(),
                thumbsUp = g.Count(e => e.Rating == 1),
                thumbsDown = g.Count(e => e.Rating == -1),
                satisfactionPct = g.Count(e => e.Rating != 0) == 0
                    ? (double?)null
                    : Math.Round(100.0 * g.Count(e => e.Rating == 1) / g.Count(e => e.Rating != 0), 1)
            })
            .OrderByDescending(x => x.runs)
            .ToList();

        var trend = executions
            .GroupBy(e => e.CreatedAt.Date)
            .Select(g => new
            {
                date = g.Key.ToString("yyyy-MM-dd"),
                runs = g.Count(),
                thumbsUp = g.Count(e => e.Rating == 1),
                thumbsDown = g.Count(e => e.Rating == -1)
            })
            .OrderBy(x => x.date)
            .ToList();

        var complaints = rated
            .Where(e => e.Rating == -1 && !string.IsNullOrWhiteSpace(e.RatingNote))
            .OrderByDescending(e => e.CreatedAt)
            .Take(10)
            .Select(e => new { e.Id, note = e.RatingNote, model = e.Model, at = e.CreatedAt })
            .ToList();

        // Cost side, from the trace table rather than executions.
        var traces = await _context.PromptTraces
            .Where(t => t.PromptId == id && t.UserId == userId && t.CreatedAt >= since)
            .Select(t => new { t.CostUsd, t.LatencyMs, t.Status })
            .ToListAsync();

        return Ok(new
        {
            promptId = id,
            promptName = prompt.Name,
            windowDays = days,
            totalRuns = executions.Count,
            ratedRuns = rated.Count,
            thumbsUp = up,
            thumbsDown = down,
            satisfactionPct = rated.Count == 0 ? (double?)null : Math.Round(100.0 * up / rated.Count, 1),
            totalTokens = executions.Sum(e => e.TotalTokensUsed),
            totalCostUsd = traces.Sum(t => t.CostUsd),
            avgLatencyMs = traces.Count == 0 ? 0 : (long)traces.Average(t => t.LatencyMs),
            errorCount = traces.Count(t => t.Status != "ok"),
            byModel,
            trend,
            complaints,
            // Enough signal to be worth acting on, and at least one thing to learn from.
            improvementAvailable = down >= 2
        });
    }

    // POST /api/prompts/{id}/suggest-improvement
    //
    // Feeds the actual downvoted outputs and their notes back into the refiner.
    // The rating data has been collected since day one and never read; this is
    // the endpoint that closes that loop.
    [HttpPost("{id}/suggest-improvement")]
    public async Task<ActionResult> SuggestImprovement(string id, [FromBody] SuggestImprovementRequest? request)
    {
        var userId = GetCurrentUserId();
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (prompt == null) return NotFound();

        var sampleSize = Math.Clamp(request?.SampleSize ?? 5, 1, 20);

        var badRuns = await _context.PromptExecutions
            .Where(e => e.PromptId == id && e.UserId == userId && e.Rating == -1)
            .OrderByDescending(e => e.CreatedAt)
            .Take(sampleSize)
            .Select(e => new { e.MessagesJson, e.RatingNote, e.Model })
            .ToListAsync();

        if (badRuns.Count == 0)
            return BadRequest(new { error = "No downvoted runs yet, so there is nothing concrete to learn from." });

        // Build the evidence: what the user asked, what came back, why they
        // disliked it. Truncated hard — we want the failure pattern, not a
        // replay of every conversation.
        var evidence = new List<string>();
        foreach (var run in badRuns)
        {
            var userMsg = "";
            var assistantMsg = "";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(run.MessagesJson);
                foreach (var m in doc.RootElement.EnumerateArray())
                {
                    var role = m.TryGetProperty("role", out var r) ? r.GetString() : null;
                    var content = m.TryGetProperty("content", out var c) ? c.GetString() : null;
                    if (role == "user" && string.IsNullOrEmpty(userMsg)) userMsg = content ?? "";
                    if (role == "assistant") assistantMsg = content ?? "";
                }
            }
            catch { /* a malformed row must not sink the whole suggestion */ }

            evidence.Add(
                $"INPUT: {Clip(userMsg, 400)}\n" +
                $"OUTPUT: {Clip(assistantMsg, 600)}\n" +
                $"WHY IT WAS REJECTED: {(string.IsNullOrWhiteSpace(run.RatingNote) ? "(thumbs down, no note)" : run.RatingNote)}");
        }

        var instructions =
            "The following runs of this prompt were rejected by the user. Identify the pattern behind the " +
            "rejections and rewrite the system prompt so those failures stop happening. Preserve the original " +
            "intent and any working constraints. Change only what the evidence justifies.\n\n" +
            string.Join("\n\n---\n\n", evidence) +
            (string.IsNullOrWhiteSpace(request?.AdditionalContext) ? "" : $"\n\nALSO: {request!.AdditionalContext}");

        try
        {
            var improved = await _aiService.QuickRefineAsync(prompt.Content, instructions);

            if (string.IsNullOrWhiteSpace(improved) || improved.Trim() == prompt.Content.Trim())
                return Ok(new
                {
                    changed = false,
                    message = "The refiner did not find a change worth making from these examples.",
                    current = prompt.Content
                });

            var lint = _linter.Lint(improved);

            return Ok(new
            {
                changed = true,
                basedOnRuns = badRuns.Count,
                current = prompt.Content,
                suggested = improved,
                diff = TextDiff.Compute(prompt.Content, improved),
                stats = TextDiff.Stats(prompt.Content, improved),
                lintScore = lint.Score,
                lintFindings = lint.Findings.Select(f => new { f.Severity, f.Rule, f.Message, f.Suggestion })
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Improvement suggestion failed for prompt {Id}", id);
            return StatusCode(502, new { error = "Could not generate a suggestion.", message = ex.Message });
        }
    }

    // POST /api/prompts/{id}/apply-improvement - accept the rewrite as a new version
    [HttpPost("{id}/apply-improvement")]
    public async Task<ActionResult> ApplyImprovement(string id, [FromBody] ApplyImprovementRequest request)
    {
        var userId = GetCurrentUserId();
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (prompt == null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(new { error = "The new content is empty." });

        // Snapshot the outgoing version first, so the improvement is reversible.
        await CreatePromptVersionAsync(prompt, userId, prompt.ApprovalStatus, "Snapshot before auto-improvement");

        prompt.Content = request.Content;
        prompt.CurrentVersion += 1;
        prompt.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        await CreatePromptVersionAsync(prompt, userId, prompt.ApprovalStatus,
            request.ChangeNote ?? "Auto-improved from downvoted runs");
        await LogPromptAuditAsync(prompt.Id, userId, "auto-improved", new { prompt.CurrentVersion });
        await _fileService.SavePromptToFileAsync(prompt);

        return Ok(new { prompt.Id, prompt.Content, prompt.CurrentVersion });
    }

    private static string Clip(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");

    // ── Conversation history import ─────────────────────────────────────────

    // POST /api/prompts/import-conversations
    //
    // Takes a ChatGPT or Claude data export and mines the system prompts and
    // repeated instructions out of it. The acquisition funnel: someone arrives
    // with two years of prompting already done and leaves with a library.
    [HttpPost("import-conversations")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult> ImportConversations([FromBody] ImportConversationsRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RawJson))
            return BadRequest(new { error = "Nothing to import." });

        List<ConversationImportCandidate> candidates;
        try
        {
            candidates = ConversationImporter.Parse(request.RawJson, request.MaxCandidates);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Conversation import parse failed");
            return BadRequest(new
            {
                error = "That file could not be read as a ChatGPT or Claude export.",
                message = ex.Message
            });
        }

        if (candidates.Count == 0)
            return Ok(new
            {
                format = ConversationImporter.DetectFormat(request.RawJson),
                candidates = Array.Empty<object>(),
                message = "The file parsed, but no reusable prompts stood out in it."
            });

        return Ok(new
        {
            format = ConversationImporter.DetectFormat(request.RawJson),
            found = candidates.Count,
            candidates = candidates.Select(c => new
            {
                c.Title,
                c.Content,
                c.Source,
                c.OccurrenceCount,
                c.ConversationTitle,
                c.CreatedAt,
                suggestedCategory = c.SuggestedCategory
            })
        });
    }

    // POST /api/prompts/import-conversations/save - keep the ones they picked
    [HttpPost("import-conversations/save")]
    public async Task<ActionResult> SaveImportedPrompts([FromBody] SaveImportedPromptsRequest request)
    {
        var userId = GetCurrentUserId();

        if (request.Prompts == null || request.Prompts.Count == 0)
            return BadRequest(new { error = "Nothing selected to save." });

        if (request.Prompts.Count > 100)
            return BadRequest(new { error = "Import at most 100 prompts at a time." });

        // Don't create a second copy of something already in the library.
        var existingContent = await _context.Prompts
            .Where(p => p.UserId == userId && p.IsActive)
            .Select(p => p.Content)
            .ToListAsync();
        var existingSet = existingContent.Select(Normalize).ToHashSet();

        var created = new List<Prompt>();
        var skipped = 0;

        foreach (var item in request.Prompts)
        {
            if (string.IsNullOrWhiteSpace(item.Content)) { skipped++; continue; }
            if (!existingSet.Add(Normalize(item.Content))) { skipped++; continue; }

            var prompt = new Prompt
            {
                Name = string.IsNullOrWhiteSpace(item.Name) ? "Imported prompt" : Clip(item.Name, 200),
                Content = item.Content,
                Category = item.Category ?? "Imported",
                Description = item.Description,
                UserId = userId,
                IsActive = true
            };
            _context.Prompts.Add(prompt);
            created.Add(prompt);
        }

        await _context.SaveChangesAsync();

        foreach (var p in created)
        {
            try { await _fileService.SavePromptToFileAsync(p); }
            catch (Exception ex) { _logger.LogWarning(ex, "Cache write failed for imported prompt {Id}", p.Id); }
        }

        return Ok(new
        {
            imported = created.Count,
            skipped,
            prompts = created.Select(p => new { p.Id, p.Name, p.Category })
        });
    }

    /// <summary>Whitespace- and case-insensitive key for duplicate detection.</summary>
    private static string Normalize(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");




    [HttpPost("generate-suggestions")]
    [AllowAnonymous]
    public async Task<ActionResult<GoalSuggestionsResult>> GenerateSuggestions([FromBody] GenerateSuggestionsRequest request)
    {
        try
        {
            var blocked = await CheckContentModeration(request.Intent);
            if (blocked != null) return blocked;

            if (request.PersonaId.HasValue)
            {
                var persona = await _context.Personas.FindAsync(request.PersonaId.Value);
                if (persona != null)
                {
                    var personaGoals = await _aiService.GenerateGoalSuggestionsWithPersonaAsync(
                        request.Intent, request.Style ?? "", request.Category ?? "", persona.SystemPrompt);
                    return Ok(new GoalSuggestionsResult(personaGoals, request.Style ?? "professional"));
                }
            }

            if (!string.IsNullOrWhiteSpace(request.PersonaInstruction))
            {
                var personaGoals = await _aiService.GenerateGoalSuggestionsWithPersonaAsync(
                    request.Intent, request.Style ?? "", request.Category ?? "", request.PersonaInstruction);
                return Ok(new GoalSuggestionsResult(personaGoals, request.Style ?? "professional"));
            }

            var result = await _aiService.GenerateGoalSuggestionsAsync(request.Intent, request.Mode ?? "intent");
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating goal suggestions");
            return StatusCode(500, new { error = "Failed to generate suggestions", message = ex.Message });
        }
    }

    [HttpPost("suggest-personas")]
    [AllowAnonymous]
    public async Task<ActionResult<PersonaSuggestionResult>> SuggestPersonas([FromBody] PersonaSuggestionRequest request)
    {
        try
        {
            var blocked = await CheckContentModeration(request.Intent);
            if (blocked != null) return blocked;

            var systemPrompt =
                "You are a prompt engineering strategist. Given a user's intent, suggest 4 to 6 useful AI working personas the user should choose from before goals are generated.\n" +
                "The personas must be specific to the intent, not generic. For example, customer support intents should include support-focused roles; coding intents should include engineering roles; legal, finance, education, sales, research, and operations intents should get domain-appropriate roles.\n" +
                "Return ONLY a JSON array. Each object must have exactly these fields:\n" +
                "[{\"id\":\"kebab-case-id\",\"title\":\"Role title\",\"category\":\"Short category\",\"description\":\"One sentence describing when to pick this persona\",\"systemPrompt\":\"A concise system prompt for this persona\"}]\n" +
                "Do not include markdown or commentary.";

            var userInput =
                $"Intent: {request.Intent}\n" +
                $"Style: {request.Style ?? "not specified"}\n" +
                $"Category: {request.Category ?? "not specified"}";

            var response = await _aiService.TestPromptAsync(new PromptTestRequest
            {
                PromptContent = systemPrompt,
                UserInput = userInput
            });

            var json = StripJsonFence(response.Result);
            var personas = System.Text.Json.JsonSerializer.Deserialize<List<PromptPersonaSuggestion>>(
                json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            ) ?? [];

            var cleaned = personas
                .Where(p => !string.IsNullOrWhiteSpace(p.Title) && !string.IsNullOrWhiteSpace(p.SystemPrompt))
                .Take(6)
                .Select((p, index) => p with
                {
                    Id = string.IsNullOrWhiteSpace(p.Id)
                        ? Slugify(p.Title)
                        : Slugify(p.Id),
                    Category = string.IsNullOrWhiteSpace(p.Category) ? "Custom" : p.Category.Trim(),
                    Description = string.IsNullOrWhiteSpace(p.Description) ? "Use this lens for the selected intent." : p.Description.Trim(),
                    SystemPrompt = p.SystemPrompt.Trim()
                })
                .ToList();

            if (cleaned.Count == 0)
                return Ok(new PersonaSuggestionResult([]));

            return Ok(new PersonaSuggestionResult(cleaned));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error suggesting personas");
            return StatusCode(500, new { error = "Failed to suggest personas", message = ex.Message });
        }
    }

    [HttpPost("generate-prompt")]
    [AllowAnonymous]
    public async Task<ActionResult<string>> GeneratePrompt([FromBody] GeneratePromptRequest request)
    {
        try
        {
            var blocked = await CheckContentModeration(request.Intent, request.AdditionalContext);
            if (blocked != null) return blocked;

            // Append language instruction to intent if non-English
            var langNames = new Dictionary<string, string>
            {
                ["es"] = "Spanish", ["fr"] = "French", ["de"] = "German",
                ["pt-BR"] = "Brazilian Portuguese", ["ar"] = "Arabic",
                ["hi"] = "Hindi", ["ja"] = "Japanese"
            };
            var intent = request.Intent;
            if (!string.IsNullOrWhiteSpace(request.Language) && request.Language != "en"
                && langNames.TryGetValue(request.Language, out var langName))
            {
                intent += $"\n\n[IMPORTANT: Generate the prompt content in {langName}]";
            }

            if (request.PersonaId.HasValue)
            {
                var persona = await _context.Personas.FindAsync(request.PersonaId.Value);
                if (persona != null)
                {
                    var prompt = await _aiService.GeneratePromptWithPersonaAsync(
                        intent, request.SelectedGoals, request.AdditionalContext,
                        persona.SystemPrompt);
                    return Ok(new { prompt });
                }
            }

            if (!string.IsNullOrWhiteSpace(request.PersonaInstruction))
            {
                var prompt = await _aiService.GeneratePromptWithPersonaAsync(
                    intent, request.SelectedGoals, request.AdditionalContext,
                    request.PersonaInstruction);
                return Ok(new { prompt });
            }

            var result = await _aiService.GeneratePromptFromGoalsAsync(
                intent, request.SelectedGoals, request.AdditionalContext);
            return Ok(new { prompt = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating prompt");
            return StatusCode(500, new { error = "Failed to generate prompt", message = ex.Message });
        }
    }

    // ==================== VOICE-TO-TEXT ====================

    [HttpPost("voice-to-text")]
    [AllowAnonymous]
    public async Task<ActionResult> VoiceToText(IFormFile audioFile)
    {
        try
        {
            if (audioFile == null || audioFile.Length == 0)
                return BadRequest(new { error = "No audio file provided" });

            using var stream = audioFile.OpenReadStream();
            var transcription = await _aiService.TranscribeAudioAsync(stream, audioFile.FileName);
            var refinedIntent = await _aiService.RefineTranscriptionToIntentAsync(transcription);

            return Ok(new { text = transcription, refinedIntent });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transcribing audio");
            return StatusCode(500, new { error = "Failed to transcribe audio", message = ex.Message });
        }
    }

    [HttpPost("generate-refinements")]
    [AllowAnonymous]
    public async Task<ActionResult<List<string>>> GenerateRefinements([FromBody] GenerateRefinementsRequest request)
    {
        try
        {
            var blocked = await CheckContentModeration(request.CurrentPrompt);
            if (blocked != null) return blocked;

            var refinements = await _aiService.GenerateRefinementSuggestionsAsync(request.CurrentPrompt);
            return Ok(refinements);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating refinement suggestions");
            return StatusCode(500, new { error = "Failed to generate refinements", message = ex.Message });
        }
    }

    [HttpPost("refine-prompt")]
    [AllowAnonymous]
    public async Task<ActionResult<string>> RefinePrompt([FromBody] RefinePromptRequest request)
    {
        try
        {
            var blocked = await CheckContentModeration(request.CurrentPrompt, request.AdditionalContext);
            if (blocked != null) return blocked;

            if (request.PersonaId.HasValue)
            {
                var persona = await _context.Personas.FindAsync(request.PersonaId.Value);
                if (persona != null)
                {
                    // For refinement with persona, we could enhance the OpenAI service method
                    // For now, use the standard refinement
                    var refinedPrompt = await _aiService.RefinePromptAsync(
                        request.CurrentPrompt,
                        request.SelectedRefinements,
                        request.AdditionalContext);
                    return Ok(new { prompt = refinedPrompt });
                }
            }

            var result = await _aiService.RefinePromptAsync(
                string.IsNullOrWhiteSpace(request.PersonaInstruction)
                    ? request.CurrentPrompt
                    : $"Use this persona while refining:\n{request.PersonaInstruction}\n\nCurrent prompt:\n{request.CurrentPrompt}",
                request.SelectedRefinements,
                request.AdditionalContext);
            return Ok(new { prompt = result });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refining prompt");
            return StatusCode(500, new { error = "Failed to refine prompt", message = ex.Message });
        }
    }

    [HttpPost("quick-refine")]
    [AllowAnonymous]
    public async Task<ActionResult<string>> QuickRefine([FromBody] QuickRefineRequest request)
    {
        try
        {
            var blocked = await CheckContentModeration(request.CurrentPrompt);
            if (blocked != null) return blocked;

            var refinedPrompt = await _aiService.QuickRefineAsync(
                request.CurrentPrompt,
                $"{request.RefinementType}: {request.Instruction}");
            return Ok(new { prompt = refinedPrompt });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying quick refinement");
            return StatusCode(500, new { error = "Failed to apply quick refinement", message = ex.Message });
        }
    }

    // ==================== IMPORT FROM CONVERSATION ====================

    [HttpPost("import-from-conversation")]
    [AllowAnonymous]
    public async Task<IActionResult> ImportFromConversation([FromBody] ImportFromConversationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConversationText))
            return BadRequest(new { error = "Conversation text is required" });

        try
        {
            var truncated = request.ConversationText[..Math.Min(4000, request.ConversationText.Length)];
            var systemPrompt =
                "You are a prompt engineering expert. A user has pasted the following conversation with an AI assistant.\n" +
                "Extract or reconstruct the best reusable prompt from that conversation.\n" +
                "Return ONLY a JSON object with these exact fields (no markdown, no extra text):\n" +
                "{\n" +
                "  \"name\": \"<short descriptive title, max 60 chars>\",\n" +
                "  \"prompt\": \"<cleaned reusable prompt text — the user's intent, not the AI response>\",\n" +
                "  \"category\": \"<one of: Writing, Coding, Marketing, Research, Analysis, Education, Creative, Business, Other>\",\n" +
                "  \"difficulty\": \"<one of: beginner, intermediate, advanced>\",\n" +
                "  \"description\": \"<one sentence explaining what this prompt is for>\"\n" +
                "}\n\n" +
                "Conversation:\n" + truncated;

            var response = await _aiService.TestPromptAsync(new PromptTestRequest { PromptContent = systemPrompt });
            var rawJson = response.Result;

            // Strip markdown fences if present
            var json = rawJson.Trim();
            if (json.StartsWith("```")) json = string.Join('\n', json.Split('\n').Skip(1).SkipLast(1));

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            return Ok(new
            {
                name = root.GetProperty("name").GetString() ?? "Imported Prompt",
                prompt = root.GetProperty("prompt").GetString() ?? "",
                category = root.TryGetProperty("category", out var cat) ? cat.GetString() : "Other",
                difficulty = root.TryGetProperty("difficulty", out var diff) ? diff.GetString() : "beginner",
                description = root.TryGetProperty("description", out var desc) ? desc.GetString() : ""
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing from conversation");
            return StatusCode(500, new { error = "Failed to extract prompt", message = ex.Message });
        }
    }

    // ==================== PUBLIC LIBRARY ENDPOINTS ====================

    [AllowAnonymous]
    [HttpGet("public")]
    public async Task<ActionResult<IEnumerable<PublicPromptDto>>> GetPublicPrompts(
        [FromQuery] string? category = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = "recent",
        [FromQuery] string? difficulty = null)
    {
        // All public prompts come from DB — templates are seeded in on startup with stable IDs
        var query = _context.Prompts
            .Include(p => p.User)
            .Where(p => p.IsPublic && p.IsActive);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(p => p.Category != null && p.Category.ToLower() == category.ToLower());

        if (!string.IsNullOrEmpty(search))
        {
            var s = search.ToLower();
            query = query.Where(p =>
                p.Name.ToLower().Contains(s) ||
                p.Content.ToLower().Contains(s) ||
                (p.Description != null && p.Description.ToLower().Contains(s)));
        }

        if (!string.IsNullOrEmpty(difficulty))
            query = query.Where(p => p.Difficulty != null && p.Difficulty.ToLower() == difficulty.ToLower());

        var ordered = sortBy?.ToLower() switch
        {
            "popular" => query.OrderByDescending(p => p.UsageCount),
            "likes"   => query.OrderByDescending(p => p.LikeCount),
            _         => query.OrderByDescending(p => p.CreatedAt),
        };

        var prompts = await ordered.Take(50).ToListAsync();

        var result = prompts.Select(p => new PublicPromptDto
        {
            Id = p.Id,
            Name = p.Name,
            Content = p.Content,
            Category = p.Category,
            Description = p.Description,
            IsTemplate = p.IsTemplate,
            UsageCount = p.UsageCount,
            LikeCount = p.LikeCount,
            CreatedAt = p.CreatedAt,
            Difficulty = p.Difficulty,
            AuthorName = p.User?.Name ?? "GenPrompt",
            AuthorPicture = p.User?.ProfilePicture
        });

        return Ok(result);
    }

    [AllowAnonymous]
    [HttpGet("public/categories")]
    public async Task<ActionResult<IEnumerable<string>>> GetPublicCategories()
    {
        var categories = await _context.Prompts
            .Where(p => p.IsPublic && p.IsActive && !string.IsNullOrEmpty(p.Category))
            .Select(p => p.Category!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();

        return Ok(categories);
    }

    [AllowAnonymous]
    [HttpGet("public/{id}")]
    public async Task<ActionResult<PublicPromptDto>> GetPublicPrompt(string id)
    {
        var prompt = await _context.Prompts
            .Include(p => p.User)
            .FirstOrDefaultAsync(p => p.Id == id && p.IsPublic && p.IsActive);

        if (prompt == null)
        {
            return NotFound();
        }

        return Ok(new PublicPromptDto
        {
            Id = prompt.Id,
            Name = prompt.Name,
            Content = prompt.Content,
            Category = prompt.Category,
            Description = prompt.Description,
            IsTemplate = prompt.IsTemplate,
            UsageCount = prompt.UsageCount,
            LikeCount = prompt.LikeCount,
            CreatedAt = prompt.CreatedAt,
            Difficulty = prompt.Difficulty,
            AuthorName = prompt.User?.Name ?? "Anonymous",
            AuthorPicture = prompt.User?.ProfilePicture
        });
    }

    [HttpPost("public/{id}/use")]
    public async Task<IActionResult> UsePublicPrompt(string id)
    {
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.IsPublic && p.IsActive);
        if (prompt == null)
        {
            return NotFound();
        }

        prompt.UsageCount++;
        await _context.SaveChangesAsync();

        return Ok(new { usageCount = prompt.UsageCount });
    }

    [AllowAnonymous]
    [HttpPost("public/{id}/like")]
    public async Task<IActionResult> LikePublicPrompt(string id)
    {
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.IsPublic && p.IsActive);
        if (prompt == null)
        {
            return NotFound();
        }

        prompt.LikeCount++;
        await _context.SaveChangesAsync();

        return Ok(new { likeCount = prompt.LikeCount });
    }

    [HttpPost("{id}/share")]
    public async Task<IActionResult> SharePrompt(string id)
    {
        var userId = GetCurrentUserId();
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

        if (prompt == null)
        {
            return NotFound();
        }

        prompt.IsPublic = true;
        if (prompt.RequiresApproval && prompt.ApprovalStatus == "draft")
            prompt.ApprovalStatus = "pending";
        prompt.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await LogPromptAuditAsync(prompt.Id, userId, "shared", new { prompt.Name, prompt.ApprovalStatus });

        return Ok(new { message = "Prompt shared to public library", isPublic = true });
    }

    [HttpPost("{id}/unshare")]
    public async Task<IActionResult> UnsharePrompt(string id)
    {
        var userId = GetCurrentUserId();
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);

        if (prompt == null)
        {
            return NotFound();
        }

        prompt.IsPublic = false;
        prompt.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await LogPromptAuditAsync(prompt.Id, userId, "unshared", new { prompt.Name });

        return Ok(new { message = "Prompt removed from public library", isPublic = false });
    }

    [HttpGet("{id}/versions")]
    public async Task<IActionResult> GetVersions(string id)
    {
        var userId = GetCurrentUserId();
        var versions = await _context.PromptVersions
            .Where(v => v.PromptId == id && v.UserId == userId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync();

        return Ok(versions);
    }

    [HttpGet("{id}/audit")]
    public async Task<IActionResult> GetAudit(string id)
    {
        var userId = GetCurrentUserId();
        var audit = await _context.PromptAuditEvents
            .Where(v => v.PromptId == id && v.UserId == userId)
            .OrderByDescending(v => v.CreatedAt)
            .ToListAsync();

        return Ok(audit);
    }

    [HttpPost("{id}/request-approval")]
    public async Task<IActionResult> RequestApproval(string id)
    {
        var userId = GetCurrentUserId();
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (prompt == null) return NotFound();

        prompt.RequiresApproval = true;
        prompt.ApprovalStatus = "pending";
        prompt.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await LogPromptAuditAsync(prompt.Id, userId, "approval_requested", new { prompt.Name });
        return Ok(new { prompt.Id, prompt.ApprovalStatus, prompt.RequiresApproval });
    }

    [HttpPost("{id}/approve")]
    public async Task<IActionResult> Approve(string id)
    {
        var userId = GetCurrentUserId();
        var prompt = await _context.Prompts.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId);
        if (prompt == null) return NotFound();

        prompt.ApprovalStatus = "approved";
        prompt.RequiresApproval = true;
        prompt.ApprovedAt = DateTime.UtcNow;
        prompt.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();
        await LogPromptAuditAsync(prompt.Id, userId, "approved", new { prompt.Name, prompt.ApprovedAt });
        return Ok(new { prompt.Id, prompt.ApprovalStatus, prompt.ApprovedAt });
    }

    [HttpGet("analytics/top-performers")]
    public async Task<IActionResult> GetTopPerformers()
    {
        var userId = GetCurrentUserId();
        var top = await _context.Prompts
            .Where(p => p.UserId == userId && p.IsActive)
            .OrderByDescending(p => p.UsageCount + p.LikeCount)
            .Take(10)
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.Category,
                p.UsageCount,
                p.LikeCount,
                p.CurrentVersion,
                p.ApprovalStatus,
                score = p.UsageCount + p.LikeCount
            })
            .ToListAsync();

        return Ok(top);
    }

    // ==================== BULK OPERATIONS ====================

    [HttpPost("bulk/delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkOperationRequest request)
    {
        var userId = GetCurrentUserId();
        var prompts = await _context.Prompts
            .Where(p => request.Ids.Contains(p.Id) && p.UserId == userId)
            .ToListAsync();

        foreach (var prompt in prompts)
        {
            prompt.IsActive = false;
            prompt.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        return Ok(new { message = $"{prompts.Count} prompts moved to recycler" });
    }

    [HttpPost("bulk/share")]
    public async Task<IActionResult> BulkShare([FromBody] BulkOperationRequest request)
    {
        var userId = GetCurrentUserId();
        var prompts = await _context.Prompts
            .Where(p => request.Ids.Contains(p.Id) && p.UserId == userId)
            .ToListAsync();

        foreach (var prompt in prompts)
        {
            prompt.IsPublic = true;
            prompt.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        return Ok(new { message = $"{prompts.Count} prompts shared to public library" });
    }

    [HttpPost("bulk/unshare")]
    public async Task<IActionResult> BulkUnshare([FromBody] BulkOperationRequest request)
    {
        var userId = GetCurrentUserId();
        var prompts = await _context.Prompts
            .Where(p => request.Ids.Contains(p.Id) && p.UserId == userId)
            .ToListAsync();

        foreach (var prompt in prompts)
        {
            prompt.IsPublic = false;
            prompt.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();

        return Ok(new { message = $"{prompts.Count} prompts removed from public library" });
    }

    private bool PromptExists(string id)
    {
        return _context.Prompts.Any(e => e.Id == id);
    }

    private static string StripJsonFence(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0) text = text[(firstNewline + 1)..];
            if (text.EndsWith("```")) text = text[..^3].TrimEnd();
        }
        return text.Trim();
    }

    private static string Slugify(string value)
    {
        var chars = value
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
            .ToArray();
        var slug = string.Join("-", new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? "persona" : slug;
    }
}

// DTOs for public library
public class PublicPromptDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Description { get; set; }
    public bool IsTemplate { get; set; }
    public int UsageCount { get; set; }
    public int LikeCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Difficulty { get; set; }
    public string AuthorName { get; set; } = string.Empty;
    public string? AuthorPicture { get; set; }
}

public record BulkOperationRequest(List<string> Ids);

public record PersonaSuggestionRequest(string Intent, string? Style = null, string? Category = null);
public record PromptPersonaSuggestion(string Id, string Title, string Category, string Description, string SystemPrompt);
public record PersonaSuggestionResult(List<PromptPersonaSuggestion> Personas);
public record GenerateSuggestionsRequest(string Intent, string? Style = null, string? Category = null, int? PersonaId = null, string? Mode = null, string? PersonaInstruction = null);
public record GeneratePromptRequest(string Intent, List<string> SelectedGoals, string? AdditionalContext, string? Style = null, int? PersonaId = null, string? Language = null, string? PersonaInstruction = null);
public record GenerateRefinementsRequest(string CurrentPrompt);
public record RefinePromptRequest(string CurrentPrompt, List<string> SelectedRefinements, string? AdditionalContext, string? Style = null, int? PersonaId = null, string? PersonaInstruction = null);
public record QuickRefineRequest(string CurrentPrompt, string RefinementType, string Instruction, string? Style = null);
public record ImportFromConversationRequest(string ConversationText);
