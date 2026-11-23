using Azure;
using Azure.AI.OpenAI;
using DocumentFormat.OpenXml.Packaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenAI.Chat;
using AgentHub.Api.Data;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using UglyToad.PdfPig;

namespace AgentHub.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/agentchat")]
public class AgentChatController : ControllerBase
{
    private const string HiddenContextStart = "\n\n<!-- agent-chat-context:start -->\n";
    private const string HiddenContextEnd = "\n<!-- agent-chat-context:end -->";

    private readonly PromptDbContext _db;
    private readonly IConfiguration _cfg;
    private readonly ILogger<AgentChatController> _logger;
    private readonly IHttpClientFactory _http;
    private readonly WebSearchService _search;
    private readonly FlightPriceService _flights;

    private static readonly Dictionary<string, string> BotSystemPrompts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["finance-lens"] = """
            You are Finance Lens — a financial document analyst.
            You read balance sheets, P&L statements, cash flow statements, and investment portfolios.
            Extract KPIs, flag anomalies, compare periods, and present findings in structured tables with clear labels.
            Use markdown headers and tables for all output. Be concise and precise.
            GUARDRAIL: Present all findings as informational data only. Never give investment, financial, or tax advice.
            If asked for advice, respond: "I only extract and analyse document data. Please consult a qualified professional."
            """,
        ["money-doc"] = """
            You are MoneyDoc AI — a smart document processor for bank statements, receipts, and invoices.
            Extract amounts, merchants, dates, and spending categories. Summarise totals by category in clean tables.
            When analysing documents: list all transactions found, group by category, show totals, and flag unusual items.
            GUARDRAIL: Never give financial advice, investment recommendations, or personalised budgeting prescriptions.
            If asked for advice, respond: "I only extract and display document data. For financial advice, consult a qualified professional."
            """,
        ["statement-iq"] = """
            You are StatementIQ — a bank and credit card statement intelligence specialist.
            Identify all transactions, spending patterns, recurring charges, subscription traps, and anomalies.
            Present data as: transaction list → category summary → recurring items → notable observations.
            Use tables and bullet points throughout. Highlight anything unusual or potentially unwanted.
            GUARDRAIL: Present as data intelligence only. No financial advice or personalised recommendations.
            """,
        ["receipt-iq"] = """
            You are ReceiptIQ — a receipt and invoice extraction specialist.
            Pull every line item, subtotal, tax amount, grand total, vendor name, date, and payment method from the document.
            Format output as: Vendor details → Line items table → Tax breakdown → Totals → Any action items (due dates, etc.).
            Be precise with numbers — double check arithmetic where possible.
            GUARDRAIL: Extraction and summary only. No financial advice.
            """,
        ["loan-iq"] = """
            You are LoanIQ — a loan and repayment analysis specialist.
            When given loan details (balance, rate, term, minimum payment), calculate:
            - Full repayment schedule summary
            - Total interest paid
            - Snowball vs avalanche comparison if multiple debts
            - Impact of extra monthly payments
            - Break-even on overpayment scenarios
            Present all calculations as educational scenarios in clear tables. Show your workings.
            GUARDRAIL: All outputs are educational calculations, not financial advice. Rates/timelines are illustrative.
            """,
        ["tax-prep"] = """
            You are TaxPrep AI — a tax document organiser.
            Read tax returns, payslips, P60s, 1099s, contribution statements, and similar documents.
            Extract: income sources, deductible categories, PAYE/withholding amounts, super/pension contributions, and key figures.
            Present as a structured summary with clear category breakdown and flagged items needing attention.
            GUARDRAIL: Document extraction only — never give tax advice, filing recommendations, or claim strategies.
            If asked for tax advice, respond: "I only extract and organise document data. Please consult a qualified tax professional."
            """,
        ["cashflow-copilot"] = """
            You are Cashflow Copilot — a monthly finance summary specialist.
            Summarise income vs expenses, calculate net cashflow, savings rate, and present 50/30/20 breakdowns.
            When given transactions or statements: total income → total expenses by category → net cashflow → savings rate → 50/30/20 framework comparison.
            Present as clean dashboard-style output with tables and percentages.
            GUARDRAIL: Educational summaries only. No advice on what the user should spend, save, or invest.
            """,
        ["calendar-bot"] = """
            You are Calendar Bot — a scheduling and daily planning assistant.
            Help users: plan meetings and events, set daily priorities, draft agenda items, suggest time blocks, manage conflicts,
            create meeting descriptions, and think through scheduling decisions.
            When calendar context is provided, read the user's upcoming meeting times, titles, attendees, locations, and notes.
            Suggest concrete meeting prep: agenda, likely questions, materials to review, decisions needed, follow-ups, and focus blocks.
            If no calendar context is available and the user asks about real meetings, ask them to connect Google Calendar or Outlook and sync events.
            Be practical and specific. Always offer concrete suggestions with times/formats when possible.
            Format outputs as: structured plans, time-blocked schedules, or agenda drafts — whatever is most useful.
            """,
        ["email-reply"] = """
            You are Email Reply Bot — a professional email reply writer.
            When given an email thread or context, draft a polished, context-appropriate reply.
            Ask for tone preference if not clear: formal, friendly, assertive, apologetic, or firm.
            Output: the reply text only (ready to copy/send), preceded by a one-line note on tone and key choices made.
            Keep replies concise. No unnecessary filler. Match the register of the original email.
            """,
        ["resume-bot"] = """
            You are Resume Bot — a resume and cover letter specialist.
            Rewrite bullet points to be impact-driven (action verb + metric + outcome format).
            Tailor CVs to specific job descriptions when provided — highlight matching skills, rephrase to match keywords.
            Write compelling cover letters: hook → why this role → why you → call to action.
            Ensure output is ATS-friendly. Flag any gaps, weak bullets, or missing impact metrics.
            """,
        ["interview-coach"] = """
            You are Interview Coach — a role-specific interview preparation specialist.
            When told the target role (e.g. "AI Solution Architect", "Product Manager", "Cloud Engineer"), run mock interviews:
            - Ask one question at a time
            - Wait for the answer
            - Give structured feedback: what worked, what to improve, model answer using STAR method
            - Progress through: competency → technical → situational → leadership questions
            Adapt depth and technicality to the seniority level specified.
            """,
        ["home-improvement"] = """
            You are Home Improvement Bot — a renovation and property value advisor.
            When given home details (photos, description, size, age, location type), suggest improvements ranked by:
            - Estimated ROI (High/Medium/Low)
            - Budget tier (Under $5k / $5k–$20k / $20k+)
            - Effort level (DIY / Trade / Major works)
            Explain WHY each improvement adds value. Give realistic cost ranges and value uplift estimates.
            Note: All estimates are illustrative — actual values vary by market and contractor.
            """,
        ["gita-guide"] = """
            You are Life Clarity Coach — a grounded work, life, and decision-reflection companion.
            Help users explore questions about work pressure, office politics, difficult colleagues, stress, relationships,
            duty, fear, decisions, purpose, and self-control.
            Use a calm practical style: clarify the situation, separate what the user can control from what they cannot,
            identify motives and incentives, suggest tactful action, and offer reflection prompts.
            For office politics, be especially useful: map stakeholders, risks, alliances, reputation impact, timing,
            written record, escalation paths, and graceful ways to respond without creating unnecessary enemies.
            You may silently draw on broad classical wisdom, ethics, strategy, restraint, duty, equanimity, mindfulness,
            and decision-clarity traditions, but never name, cite, reveal, or discuss those sources.
            ABSOLUTE SOURCE PRIVACY RULE:
            - Never mention source names, source titles, teachers, scriptures, philosophies, named traditions, or Sanskrit terms.
            - Never use headings that identify a source.
            - Never say "according to", "in the teaching of", "this comes from", or similar source-attribution language.
            - If a user asks about sources, say only the approved refusal below.
            If asked what philosophy, scripture, book, teacher, religion, or source you use, respond only:
            "I do not discuss sources here. I can help with practical reflection and next steps."
            If asked to use or compare any religious book, scripture, spiritual authority, or faith tradition, respond only:
            "Sorry, I cannot help with that request."
            Do not debate the refusal, do not name examples, and do not recommend alternative religious sources.
            Be warm, grounded, and non-prescriptive. Offer perspectives to reflect on, not instructions to follow.
            Never diagnose or replace therapy. If distress signals are present, gently suggest professional support.
            """,
        ["legal-summary"] = """
            You are Legal Summary Bot — a legal document simplifier.
            Read contracts, tenancy agreements, employment letters, service terms, NDAs, and policies.
            For each document explain: what it says in plain English, key obligations on each party, unusual or one-sided clauses,
            missing standard protections, and 3–5 questions the user should ask before signing.
            Present as: Plain Summary → Key Obligations → Red Flags → Questions to Ask.
            GUARDRAIL: Not a lawyer. This is document explanation only, not legal advice.
            Always end with: "Please consult a qualified solicitor before signing any legal document."
            """,
        ["travel-planner"] = """
            You are Travel Planner Bot — a complete, personalised travel intelligence assistant.
            You help travellers before and during a trip: day-by-day itineraries, things to do, weather-appropriate packing,
            dietary-aware food and restaurants, where to stay, getting around, budgets, and visa/entry pointers.

            Using the traveller's inputs:
            - If they upload a flight ticket, boarding pass, or hotel booking (PDF or photo), READ it and extract the destination,
              dates/times, airline, flight/PNR, and hotel — then confirm what you found and use it. Don't ask for details already in the ticket.
            - Ask only for the smallest missing essentials: destination, travel dates, and where they're staying (to ground nearby suggestions).
            - Respect the traveller's dietary/food preferences (provided in context) in EVERY food recommendation.
            - When LIVE WEB SEARCH RESULTS are provided in context, use them for current places, opening hours, prices, and events,
              and prefer them over memory for anything time-sensitive; mention when you used them.
            - If a FLIGHT FARE INSIGHT is provided (or the traveller asks about flight prices or the best time to fly a route),
              use it to recommend the cheapest months to travel and to book early; if none is available, say so and point them to the "Best time to fly" tool on the Travel page.
            - Give weather-appropriate packing advice; if you have no forecast, state typical seasonal conditions and say to verify closer to travel.

            Format a full trip plan as: Trip summary (destination, dates, who) → Day-by-day schedule → Where to stay → Food to try
            (dietary-aware) → Getting around → What to pack → Budget snapshot → Local tips & safety.
            Be specific with real place/neighbourhood/dish names, meal timing, and practical logistics.

            ADDING PLANS TO THE CALENDAR:
            When the traveller CONFIRMS concrete plans (a flight, hotel check-in/out, a booked activity, or an agreed itinerary),
            output — in addition to your normal reply — a fenced code block tagged `calendar` containing a JSON array of the events,
            so the app can offer to add them to the user's calendar (which then syncs to their Google/Outlook). Use this exact shape and
            only include events the user has actually confirmed:
            ```calendar
            [
              { "title": "Flight NZ123 AKL→CHC", "startDateTime": "2026-07-06T23:40:00", "endDateTime": "2026-07-07T01:00:00", "location": "Auckland Airport (AKL)", "description": "Jetstar 231, PNR QQB2KT" }
            ]
            ```
            Rules for the calendar block: use the traveller's local date-times in ISO 8601 (no timezone offset — the app applies their timezone);
            one object per event; keep titles short; omit the block entirely if nothing is confirmed yet. Never invent times you weren't given.

            DISCLAIMER (include a short version at the end of any full guide): This is AI-generated guidance for inspiration only —
            verify visas/entry rules, opening hours, prices, bookings, and safety/health advice with official sources; weather is an estimate.
            """,
    };

    private const string BotIntentOperatingPrompt = """
        You are still part of GenPrompt, whose core product philosophy is prompting by intent.
        For every specialised bot conversation, do not behave like a generic chatbot. Convert the user's message into an intent-led task.

        Intent-first workflow:
        1. Infer the user's real goal, audience, context, constraints, and desired output.
        2. If required inputs are missing, ask only the smallest number of clarifying questions needed to proceed.
        3. If enough context exists, produce a useful structured output immediately.
        4. Make the output reusable where natural: include templates, checklists, tables, prompts, agendas, scripts, or next-step frameworks.
        5. Prefer clear sections, labels, assumptions, and editable language over loose advice.
        6. When files or calendar data are provided, ground the answer in that context and say what data was used.

        User experience:
        - Help the user feel that they gave an intent, and GenPrompt turned it into an actionable artefact.
        - Keep responses practical, concise, and copy-ready.
        - Do not mention this internal workflow unless the user asks how the bot works.
        """;

    private const string SystemPrompt = """
        You are the GenPrompt AI Assistant — an expert in prompt engineering, AI workflows, and the GenPrompt platform.

        You help users with:
        - Crafting, refining, and improving AI prompts for ChatGPT, Claude, Gemini, Perplexity, and other LLMs
        - Designing multi-step AI workflows, agent pipelines, and prompt chains
        - Analysing documents (uploaded files or cloud links) and suggesting relevant prompts or workflows
        - Understanding AI concepts: chain-of-thought, few-shot prompting, personas, temperature, context windows
        - Getting the most from GenPrompt features: playground, skills, personas, collections, agents, swarm, research
        - General productivity with AI tools

        Context priority (highest to lowest):
        1. Attached file content or cloud document (if provided)
        2. User's saved prompts (if provided in context)
        3. GenPrompt platform knowledge
        4. General AI/LLM expertise

        Response style:
        - Use markdown: headers (##), bullet lists, numbered lists, **bold**, `code`, tables
        - Be concise but complete. Prefer structured output over prose walls.
        - Cite the source if using uploaded document content: [Source: attached file]
        - Do not make up facts about the user's data — only reference what was provided

        CRITICAL — PROMPT OUTPUT RULES:
        - When asked to create, write, or generate a prompt: output the FULL prompt text directly in a markdown code block right here in this chat message. That is your entire response.
        - NEVER say "draft created", "prompt saved", "Prompt ID:", "Module:", or "next step: open the module". You are a chat assistant — you cannot save anything.
        - NEVER simulate system actions or pretend to interact with any software. Everything you produce must appear inline in this conversation, immediately, in full.
        - The user can copy the prompt from the code block. Your only job is to write it here, completely, right now.
        - Correct behaviour: user says "create a prompt for an interview bot" → you write the complete prompt in a code block. Nothing else.

        Platform facts:
        - GenPrompt is at this app
        - Free playground: /playground | Prompt library: /library | Skills: /skills
        - Chrome extension and VS Code extension available
        - MCP server available for Claude Desktop, Cursor, Windsurf

        ── GUARDRAILS (non-negotiable) ──────────────────────────────────────────────

        CONTENT RESTRICTIONS — refuse and explain briefly if a request involves:
        - Sexual, explicit, adult, or pornographic content of any kind
        - Violence, self-harm, or content that could endanger someone
        - Hate speech, discrimination, or harassment targeting any group
        - Illegal activities, fraud, or deceptive practices
        - Impersonation of real individuals or organisations
        If any restriction is triggered, respond with: "I can't help with that. Please ask me something related to prompt engineering, AI workflows, or document analysis."

        FINANCIAL DOCUMENT GUARDRAIL — when analysing receipts, invoices, bank statements, or any financial document:
        - Extract, organise, and display the data found in the document
        - Summarise what is factually present: amounts, dates, merchants, categories, totals
        - You MAY perform non-advisory calculations from user-provided data, including:
          - spending pattern summaries
          - debt payoff timelines
          - snowball vs avalanche comparisons
          - extra-payment what-if scenarios and estimated interest differences when rates/balances are supplied
          - budgeting framework breakdowns such as 50/30/20 as informational comparisons
        - Present calculations as educational scenarios, not recommendations
        - Summarise general strategies neutrally without prescribing one
        - Do NOT provide financial advice, investment recommendations, tax advice, or personalised budgeting prescriptions
        - Do NOT tell the user what they should spend, save, invest, claim, or choose
        - If asked for financial advice or recommendations, respond only with: "I only extract and display document data. For financial advice, please consult a qualified professional."
        - This is a hard limit — it applies even if the user explicitly asks, reframes the question, or pushes back
        """;

    public AgentChatController(
        PromptDbContext db,
        IConfiguration cfg,
        ILogger<AgentChatController> logger,
        IHttpClientFactory http,
        WebSearchService search,
        FlightPriceService flights)
    {
        _db   = db;
        _cfg  = cfg;
        _logger = logger;
        _http = http;
        _search = search;
        _flights = flights;
    }

    // ── Sessions ──────────────────────────────────────────────────────────────

    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions([FromQuery] string? botType = null)
    {
        var userId = GetUserId();
        var query = _db.AgentChatSessions.Where(s => s.UserId == userId);
        if (!string.IsNullOrWhiteSpace(botType))
            // Include this bot's chats plus legacy untagged sessions (created before
            // sessions were tagged by bot) so older conversations aren't lost.
            query = query.Where(s => s.BotType == botType || s.BotType == null);

        var sessions = await query
            .OrderByDescending(s => s.UpdatedAt)
            .Take(100)
            .Select(s => new
            {
                s.SessionId, s.Title, s.BotType, s.UpdatedAt,
                messageCount = _db.AgentChatMessages.Count(m => m.SessionId == s.SessionId)
            })
            .ToListAsync();
        return Ok(sessions);
    }

    [HttpGet("sessions/{sessionId}/messages")]
    public async Task<IActionResult> GetMessages(string sessionId)
    {
        var userId = GetUserId();
        var session = await _db.AgentChatSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);
        if (session == null) return NotFound();

        var rawMessages = await _db.AgentChatMessages
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Id)
            .Select(m => new { m.Id, m.Role, m.Content, m.CreatedAt })
            .ToListAsync();

        var messages = rawMessages
            .Select(m => new { m.Id, m.Role, Content = StripHiddenContext(m.Content), m.CreatedAt })
            .ToList();

        return Ok(new { session = new { session.SessionId, session.Title }, messages });
    }

    [HttpDelete("sessions/{sessionId}")]
    public async Task<IActionResult> DeleteSession(string sessionId)
    {
        var userId = GetUserId();
        var session = await _db.AgentChatSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);
        if (session == null) return NotFound();
        _db.AgentChatSessions.Remove(session);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPatch("sessions/{sessionId}/rename")]
    public async Task<IActionResult> RenameSession(string sessionId, [FromBody] RenameRequest req)
    {
        var userId = GetUserId();
        var session = await _db.AgentChatSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId && s.UserId == userId);
        if (session == null) return NotFound();
        session.Title = req.Title.Trim()[..Math.Min(req.Title.Trim().Length, 80)];
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { session.SessionId, session.Title });
    }

    // ── Send message (SSE streaming) ──────────────────────────────────────────

    [HttpPost("send")]
    [RequestSizeLimit(104_857_600)]
    public async Task Send(
        [FromForm] string? sessionId,
        [FromForm] string message,
        [FromForm] string? cloudUrl,
        [FromForm] string? botType,
        [FromForm] string? interactionSessionId,
        IFormFileCollection? files,
        CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var userId = GetUserId();

        // Generate or reuse session
        var sid = string.IsNullOrWhiteSpace(sessionId)
            ? Guid.NewGuid().ToString("N")[..16]
            : sessionId.Trim();

        Response.Headers["X-Session-Id"] = sid;

        await using var writer = new StreamWriter(Response.Body, Encoding.UTF8, leaveOpen: true);

        async Task Send(string ev, object data)
        {
            await writer.WriteAsync($"event: {ev}\ndata: {JsonSerializer.Serialize(data)}\n\n");
            await writer.FlushAsync(ct);
        }

        try
        {
            // ── Build file/cloud context ──────────────────────────────────────
            var fileContextParts = new List<string>();
            var fileLabelParts   = new List<string>();
            var imageParts       = new List<(byte[] bytes, string mime)>();

            if (files != null && files.Count > 0)
            {
                foreach (var file in files)
                {
                    if (file == null || file.Length == 0) continue;
                    await Send("progress", new { text = $"Reading {file.FileName}…" });
                    using var ms = new MemoryStream();
                    await file.CopyToAsync(ms, ct);
                    var bytes = ms.ToArray();
                    var mime  = file.ContentType?.ToLowerInvariant() ?? string.Empty;

                    if (mime.StartsWith("image/"))
                    {
                        imageParts.Add((bytes, mime));
                        fileLabelParts.Add($"Image: {file.FileName}");
                    }
                    else if (mime == "application/pdf")
                    {
                        var (ctx, imgs) = ExtractPdf(bytes);
                        fileContextParts.Add(ctx);
                        imageParts.AddRange(imgs);
                        fileLabelParts.Add($"PDF: {file.FileName}");
                    }
                    else if (mime == "text/plain")
                    {
                        fileContextParts.Add(Encoding.UTF8.GetString(bytes));
                        fileLabelParts.Add($"Text: {file.FileName}");
                    }
                    else if (mime.Contains("wordprocessingml") || mime == "application/msword")
                    {
                        var (ctx, imgs) = ExtractDocx(bytes);
                        fileContextParts.Add(ctx);
                        imageParts.AddRange(imgs);
                        fileLabelParts.Add($"Document: {file.FileName}");
                    }
                }
            }

            var fileContext = string.Join("\n\n---\n\n", fileContextParts);
            var fileLabel   = string.Join(", ", fileLabelParts);

            if (string.IsNullOrWhiteSpace(fileLabel) && !string.IsNullOrWhiteSpace(cloudUrl))
            {
                await Send("progress", new { text = "Fetching cloud document…" });
                (fileContext, fileLabel) = await FetchCloudUrl(cloudUrl, ct);
            }

            var hasAttachment = !string.IsNullOrWhiteSpace(fileLabel);
            var userInstruction = string.IsNullOrWhiteSpace(message)
                ? "Please analyse this attachment and summarise what is present."
                : message.Trim();

            // ── Load history ─────────────────────────────────────────────────
            var history = await _db.AgentChatMessages
                .Where(m => m.SessionId == sid)
                .OrderBy(m => m.Id)
                .Select(m => new { m.Role, m.Content })
                .ToListAsync(ct);

            // ── Build user saved prompts context (top 10) ─────────────────────
            var savedPrompts = (await _db.Prompts
                .Where(p => p.UserId == userId)
                .OrderByDescending(p => p.CreatedAt)
                .Take(10)
                .Select(p => new { p.Name, p.Content })
                .ToListAsync(ct))
                .Select(p => $"- {p.Name}: {p.Content[..Math.Min(p.Content.Length, 120)]}")
                .ToList();

            var promptsCtx = savedPrompts.Count > 0
                ? "USER'S RECENT SAVED PROMPTS:\n" + string.Join("\n", savedPrompts)
                : string.Empty;

            // ── Assemble chat messages ────────────────────────────────────────
            string? botPrompt = null;
            var isKnownBot = !string.IsNullOrWhiteSpace(botType) && BotSystemPrompts.TryGetValue(botType, out botPrompt);
            var activePrompt = isKnownBot
                ? botPrompt ?? SystemPrompt
                : SystemPrompt;
            var isLifeClarityBot = string.Equals(botType, "gita-guide", StringComparison.OrdinalIgnoreCase);
            var chatMessages = new List<ChatMessage> { new SystemChatMessage(activePrompt) };

            if (isKnownBot)
                chatMessages.Add(new SystemChatMessage(BotIntentOperatingPrompt));

            if (isLifeClarityBot)
            {
                chatMessages.Add(new SystemChatMessage("""
                    Source privacy is mandatory. Your final answer must not include the names of any source texts, teachers,
                    religions, scriptures, named philosophies, Sanskrit terms, or source-attribution language. Give only practical,
                    modern reflection and next steps. If the user asks for sources or asks you to use any religious text, use the
                    exact refusal specified in your primary instructions.
                    """));
            }

            if (!string.IsNullOrWhiteSpace(promptsCtx))
                chatMessages.Add(new SystemChatMessage(promptsCtx));

            if (string.Equals(botType, "calendar-bot", StringComparison.OrdinalIgnoreCase))
            {
                var calendarCtx = await BuildCalendarBotContextAsync(userId, ct);
                if (!string.IsNullOrWhiteSpace(calendarCtx))
                    chatMessages.Add(new SystemChatMessage(calendarCtx));
            }

            if (string.Equals(botType, "travel-planner", StringComparison.OrdinalIgnoreCase))
            {
                await Send("progress", new { text = "Checking dietary preferences and searching the web…" });
                var travelCtx = await BuildTravelBotContextAsync(userId, userInstruction, ct);
                if (!string.IsNullOrWhiteSpace(travelCtx))
                    chatMessages.Add(new SystemChatMessage(travelCtx));
            }

            // Attached Interaction Intelligence session (transcript + analyses) as context.
            if (!string.IsNullOrWhiteSpace(interactionSessionId))
            {
                var interactionCtx = await BuildInteractionContextAsync(userId, interactionSessionId, ct);
                if (!string.IsNullOrWhiteSpace(interactionCtx))
                    chatMessages.Add(new SystemChatMessage(interactionCtx));
            }

            // Inject last 20 history turns
            foreach (var h in history.TakeLast(20))
            {
                if (h.Role == "user")      chatMessages.Add(new UserChatMessage(h.Content));
                else if (h.Role == "assistant")
                {
                    var assistantHistory = isLifeClarityBot
                        ? SanitizeLifeClarityReply(h.Content)
                        : h.Content;
                    chatMessages.Add(new AssistantChatMessage(assistantHistory));
                }
            }

            // Build current user message content
            var userText = hasAttachment
                ? string.IsNullOrWhiteSpace(fileContext)
                    ? $"[Source: {fileLabel}]\n\nThe user uploaded this attachment. No text was extracted directly, so inspect any provided image/document content and answer the user's request.\n\n---\n\n{userInstruction}"
                    : $"[Source: {fileLabel}]\n\n{fileContext[..Math.Min(fileContext.Length, 24_000)]}\n\n---\n\n{userInstruction}"
                : userInstruction;

            if (imageParts.Count > 0)
            {
                var parts = new List<ChatMessageContentPart>
                {
                    ChatMessageContentPart.CreateTextPart(userText)
                };
                foreach (var (imgBytes, imgMime) in imageParts)
                    parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imgBytes), imgMime));
                chatMessages.Add(new UserChatMessage(parts));
            }
            else
            {
                chatMessages.Add(new UserChatMessage(userText));
            }

            // ── Ensure session exists ────────────────────────────────────────
            if (!await _db.AgentChatSessions.AnyAsync(s => s.SessionId == sid, ct))
            {
                var title = string.IsNullOrWhiteSpace(message) && hasAttachment
                    ? fileLabel
                    : message.Trim();
                title = title.Length <= 72 ? title : title[..72] + "…";
                _db.AgentChatSessions.Add(new AgentChatSession
                {
                    SessionId = sid, Title = title, UserId = userId,
                    BotType = string.IsNullOrWhiteSpace(botType) ? null : botType.Trim(),
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(ct);
            }

            // Persist user message
            _db.AgentChatMessages.Add(new AgentChatMessage
            {
                SessionId = sid, Role = "user",
                Content = BuildStoredUserMessage(message, userInstruction, fileLabel, fileContext, imageParts.Count),
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync(ct);

            var moduleCommandReply = await TryHandleModuleCommand(message, userId, ct);
            if (!string.IsNullOrWhiteSpace(moduleCommandReply))
            {
                await Send("chunk", new { text = moduleCommandReply });
                await writer.WriteAsync("event: done\ndata: {}\n\n");
                await writer.FlushAsync(ct);

                _db.AgentChatMessages.Add(new AgentChatMessage
                {
                    SessionId = sid,
                    Role = "assistant",
                    Content = moduleCommandReply,
                    CreatedAt = DateTime.UtcNow
                });
                var moduleSess = await _db.AgentChatSessions.FindAsync(new object[] { sid }, ct);
                if (moduleSess != null) moduleSess.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                return;
            }

            // ── Stream from the configured AI provider ──────────────────────────────────────────
            var endpoint   = _cfg["AzureOpenAI:Endpoint"];
            var apiKey     = _cfg["AzureOpenAI:ApiKey"];
            var deployment = _cfg["AzureOpenAI:DeploymentName"] ?? "gpt-4o";

            if (!AiChatClients.IsConfigured(_cfg))
            {
                await Send("error", new { text = "No AI provider is configured on this server." });
                return;
            }

            var client = AiChatClients.Create(_cfg, deployment);

            var reply = new StringBuilder();
            if (hasAttachment)
            {
                var extractionNote = string.IsNullOrWhiteSpace(fileContext)
                    ? "I could not extract plain text directly, so I will inspect the attached document content where available."
                    : $"I extracted {fileContext.Length:N0} characters of document text for analysis.";
                var ack = $"I can see the upload: **{fileLabel}**. {extractionNote}\n\n";
                reply.Append(ack);
                await Send("chunk", new { text = ack });
            }

            await foreach (var update in client.CompleteChatStreamingAsync(chatMessages,
                AzureChatOptions.For(deployment, maxOutputTokens: 4096), ct))
            {
                foreach (var part in update.ContentUpdate)
                {
                    if (string.IsNullOrEmpty(part.Text)) continue;
                    reply.Append(part.Text);
                    if (!isLifeClarityBot)
                        await Send("chunk", new { text = part.Text });
                }
            }

            // Auto-save any generated prompt to the Prompts module
            var replyText = isLifeClarityBot
                ? SanitizeLifeClarityReply(reply.ToString())
                : reply.ToString();
            if (isLifeClarityBot)
                await Send("chunk", new { text = replyText });
            var savedPromptId = await TrySaveGeneratedPromptAsync(replyText, message, userId, ct);

            await writer.WriteAsync(savedPromptId != null
                ? $"event: saved\ndata: {{\"promptId\":\"{savedPromptId}\"}}\n\n"
                : "event: done\ndata: {}\n\n");
            await writer.FlushAsync(ct);
            _db.AgentChatMessages.Add(new AgentChatMessage
            {
                SessionId = sid, Role = "assistant",
                Content = replyText, CreatedAt = DateTime.UtcNow
            });
            var sess = await _db.AgentChatSessions.FindAsync(new object[] { sid }, ct);
            if (sess != null) sess.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AgentChat send failed");
            try { await Send("error", new { text = "Something went wrong. Please try again." }); } catch { }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string GetUserId() =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
        ?? User.FindFirst("sub")?.Value
        ?? "anonymous";

    private static string BuildStoredUserMessage(
        string message,
        string userInstruction,
        string fileLabel,
        string fileContext,
        int imageCount)
    {
        if (string.IsNullOrWhiteSpace(fileLabel)) return message;

        var visible = $"{userInstruction}\n\n[Attachment: {fileLabel}]";
        var hidden = new StringBuilder()
            .AppendLine($"Attachment source: {fileLabel}")
            .AppendLine($"Original user instruction: {userInstruction}");

        if (!string.IsNullOrWhiteSpace(fileContext))
        {
            hidden.AppendLine()
                  .AppendLine("Extracted attachment text for follow-up context:")
                  .AppendLine(fileContext[..Math.Min(fileContext.Length, 24_000)]);
        }
        else if (imageCount > 0)
        {
            hidden.AppendLine()
                  .AppendLine($"The attachment included {imageCount} image/document visual part(s). No plain text was extracted, so rely on the previous assistant analysis for follow-up questions.");
        }

        return $"{visible}{HiddenContextStart}{hidden}{HiddenContextEnd}";
    }

    private static string StripHiddenContext(string content)
    {
        var start = content.IndexOf(HiddenContextStart, StringComparison.Ordinal);
        if (start < 0) return content;

        var end = content.IndexOf(HiddenContextEnd, start, StringComparison.Ordinal);
        if (end < 0) return content[..start].TrimEnd();

        return (content[..start] + content[(end + HiddenContextEnd.Length)..]).TrimEnd();
    }

    private static string SanitizeLifeClarityReply(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;

        var text = content;
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?im)^\s*(#{1,6}\s*)?(bhagavad\s+gita|gita|krishna|chanakya(?:\s+niti)?)\s*:?\s*$",
            "### Perspective");
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?i)\b(bhagavad\s+gita|gita|krishna|chanakya(?:\s+niti)?)\b",
            "practical reflection");
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?i)\b(dharma|nishkama\s+karma)\b",
            "steady responsibility");
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?i)\b(according to|in the teachings? of|as taught by|comes from|draws from)\b",
            "A practical way to frame this is");
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?im)^\s*(source|sources|scripture|religious text)\s*:.*$",
            string.Empty);

        return text.Trim();
    }

    private async Task<string?> TrySaveGeneratedPromptAsync(string reply, string userMessage, string userId, CancellationToken ct)
    {
        // Extract the first fenced code block from the reply
        var match = System.Text.RegularExpressions.Regex.Match(
            reply, @"```(?:\w*\n)?([\s\S]+?)```", System.Text.RegularExpressions.RegexOptions.Multiline);
        if (!match.Success) return null;

        var promptContent = match.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(promptContent) || promptContent.Length < 20) return null;

        // Derive a title from the user's request (first 60 chars)
        var title = userMessage.Trim();
        if (title.Length > 60) title = title[..60] + "…";

        var prompt = new Prompt
        {
            Id          = Guid.NewGuid().ToString(),
            Name        = title,
            Content     = promptContent,
            Description = $"Generated by Agent Chat",
            Category    = "AI Generated",
            UserId      = userId,
            ApprovalStatus = "draft",
            CreatedAt   = DateTime.UtcNow,
            UpdatedAt   = DateTime.UtcNow,
        };

        _db.Prompts.Add(prompt);
        await _db.SaveChangesAsync(ct);
        return prompt.Id;
    }

    private static (string text, List<(byte[], string)> images) ExtractPdf(byte[] bytes)
    {
        var sb     = new StringBuilder();
        var images = new List<(byte[], string)>();
        try
        {
            using var doc = PdfDocument.Open(bytes);
            foreach (var page in doc.GetPages())
            {
                var text = page.Text?.Trim() ?? string.Empty;
                if (text.Length >= 20) sb.AppendLine(text);
                else
                {
                    foreach (var img in page.GetImages())
                        if (img.TryGetPng(out var png) && png?.Length > 0)
                            images.Add((png, "image/png"));
                }
            }
        }
        catch { /* unreadable PDF */ }
        return (sb.ToString(), images);
    }

    private static (string text, List<(byte[], string)> images) ExtractDocx(byte[] bytes)
    {
        var images = new List<(byte[], string)>();
        try
        {
            using var ms  = new MemoryStream(bytes);
            using var doc = WordprocessingDocument.Open(ms, false);
            var text = doc.MainDocumentPart?.Document?.Body?.InnerText?.Trim() ?? string.Empty;
            foreach (var ip in doc.MainDocumentPart?.ImageParts ?? Enumerable.Empty<ImagePart>())
            {
                try
                {
                    using var imgMs = new MemoryStream();
                    ip.GetStream().CopyTo(imgMs);
                    var imgBytes = imgMs.ToArray();
                    if (imgBytes.Length > 0 && (ip.ContentType?.StartsWith("image/") ?? false))
                        images.Add((imgBytes, ip.ContentType!));
                }
                catch { }
            }
            return (text, images);
        }
        catch { return (string.Empty, images); }
    }

    private async Task<(string text, string label)> FetchCloudUrl(string url, CancellationToken ct)
    {
        try
        {
            // Normalise Google Drive share links → direct download
            var fetchUrl = url;
            var gdMatch  = System.Text.RegularExpressions.Regex.Match(url, @"drive\.google\.com/file/d/([^/]+)");
            if (gdMatch.Success)
                fetchUrl = $"https://drive.google.com/uc?export=download&id={gdMatch.Groups[1].Value}";

            // OneDrive: try to resolve the embed/download URL
            var oneDriveMatch = System.Text.RegularExpressions.Regex.Match(url, @"1drv\.ms|onedrive\.live\.com");
            // (OneDrive share links require resolving - leave as-is and fetch, server will redirect)

            var httpClient = _http.CreateClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 GenPrompt-DocAgent/1.0");
            httpClient.Timeout = TimeSpan.FromSeconds(30);

            using var resp = await httpClient.GetAsync(fetchUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var contentType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "text/plain";
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            var docBytes = ms.ToArray();

            string text;
            if (contentType == "application/pdf")
                (text, _) = ExtractPdf(docBytes);
            else if (contentType.Contains("wordprocessingml"))
                (text, _) = ExtractDocx(docBytes);
            else
                text = Encoding.UTF8.GetString(docBytes);

            var label = gdMatch.Success ? "Google Drive document"
                      : oneDriveMatch.Success ? "OneDrive document"
                      : $"Cloud document ({new Uri(url).Host})";

            return (text[..Math.Min(text.Length, 24_000)], label);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch cloud URL {Url}", url);
            return (string.Empty, string.Empty);
        }
    }

    private async Task<string?> TryHandleModuleCommand(string message, string userId, CancellationToken ct)
    {
        var text = message.Trim();
        var lower = text.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text) || !HasIntentWord(lower)) return null;

        // Messages built by the Agent Chat module forms carry a fixed prefix.
        // Route those by the module the user explicitly selected, before any
        // keyword guessing — "Create a prompt for notetaker ai agent" must
        // create a prompt, not an agent.
        if (lower.StartsWith("create a prompt for"))
            return await CreatePromptFromChat(text, userId, "Prompt", false, ct);
        if (lower.StartsWith("generate a media prompt for"))
            return await CreatePromptFromChat(text, userId, "Media Studio", false, ct);
        if (lower.StartsWith("create an agent for"))
            return await CreateAgentFromChat(text, userId, ct);
        if (lower.StartsWith("draft a persona for"))
            return await CreatePersonaFromChat(text, userId, ct);
        if (lower.StartsWith("create marketplace prompt for"))
            return await CreatePromptFromChat(text, userId, "Marketplace", true, ct);
        if (lower.StartsWith("schedule a job to"))
            return await CreateJobFromChat(text, userId, ct);
        if (lower.StartsWith("generate research brief about"))
            return await CreatePromptFromChat(text, userId, "Research", false, ct);

        if (HasAny(lower, "media", "image", "video", "voice", "dall-e", "midjourney", "stable diffusion", "script"))
            return await CreatePromptFromChat(text, userId, "Media Studio", false, ct);

        // "prompt" outranks "agent": an unprefixed message mentioning both
        // (e.g. "create a prompt for a notetaker agent") is a prompt request.
        if (HasAny(lower, "prompt", "prompts", "prompt builder", "template", "system prompt", "instruction", "variables"))
            return await CreatePromptFromChat(text, userId, "Prompt", false, ct);

        if (HasAny(lower, "agent", "agents", "autonomous", "assistant agent") &&
            !HasAny(lower, "agent chat", "chat agent"))
            return await CreateAgentFromChat(text, userId, ct);

        if (HasAny(lower, "persona", "personas", "personality", "tone", "character", "voice"))
            return await CreatePersonaFromChat(text, userId, ct);

        if (HasAny(lower, "marketplace", "public prompt", "publish", "listing", "community prompt"))
            return await CreatePromptFromChat(text, userId, "Marketplace", true, ct);

        if (HasAny(lower, "job", "jobs", "schedule", "scheduled job", "automation"))
            return await CreateJobFromChat(text, userId, ct);

        if (HasAny(lower, "research", "brief", "scenario", "analysis", "forecast", "trend", "investigate"))
            return await CreatePromptFromChat(text, userId, "Research", false, ct);

        if (HasAny(lower, "calendar", "meeting", "agenda", "time block", "plan my day", "schedule"))
            return BuildBotLink("📅 Calendar Bot", "/bot-chat/calendar-bot", "Plan meetings, daily schedules, and time-blocked work sessions.");

        if (HasAny(lower, "bank statement", "receipt", "invoice", "cashflow", "loan", "balance sheet", "payslip", "tax return"))
            return BuildBotLink("💳 Finance Bots", "/bot-agents", "Analyse bank statements, receipts, invoices, loans, and tax documents with specialised finance bots.");

        if (HasAny(lower, "interview", "resume", "cv", "cover letter", "email reply", "job application"))
            return BuildBotLink("🎯 Career Bots", "/bot-agents", "Interview coaching, resume rewriting, and professional email replies.");

        if (HasAny(lower, "travel", "itinerary", "trip", "home improvement", "renovation", "work stress", "office politics", "decision clarity", "tenancy", "contract summary"))
            return BuildBotLink("✈️ Lifestyle Bots", "/bot-agents", "Travel planning, home improvement, legal summaries, and life clarity.");

        return null;
    }

    private async Task<string> CreateAgentFromChat(string text, string userId, CancellationToken ct)
    {
        var name = MakeTitle(text, "Agent");
        var useCase = SummarizeUseCase(text);
        var instructions = $"""
            You are {name}.

            Goal:
            {text}

            What you are good for:
            - Understand incoming requests and classify intent, urgency, required context, and next action.
            - Ask concise clarifying questions when the user's goal or required inputs are incomplete.
            - Produce structured, actionable outputs that can be reviewed, handed off, or run again.
            - Keep decisions explainable by showing key assumptions, confidence, and recommended follow-up.

            Operating instructions:
            - Start by restating the task in one sentence.
            - Identify missing inputs before taking action.
            - Use available tools only when they directly support the user's goal.
            - Return results in a predictable structure with summary, details, risks, and next action.
            - Escalate when approval is required, the task is outside scope, or the request touches sensitive data.
            """.Trim();
        var agent = new Agent
        {
            UserId = userId,
            Name = name,
            Description = $"{useCase} Created from Agent Chat: {text[..Math.Min(text.Length, 220)]}",
            Instructions = instructions,
            Model = "gpt-4o",
            EnabledTools = "[]",
            ToolPermissions = "{}",
            InputSchemaJson = "[{\"name\":\"input\",\"type\":\"string\",\"required\":true}]",
            MemoryConfigJson = "{\"mode\":\"session\",\"writeBack\":true}",
            GuardrailsJson = "{\"blockedTopics\":[],\"piiPolicy\":\"warn\"}",
            OutputSchemaJson = "{\"type\":\"string\"}",
            KnowledgeSourcesJson = "[]",
            IntegrationActionsJson = "[]",
            ModelRoutingJson = "{\"mode\":\"single\",\"models\":[\"gpt-4o\"]}",
            BenchmarkConfigJson = "{\"enabled\":false}",
            GovernanceJson = "{\"approvalMode\":\"none\",\"citationRequired\":false}",
            FallbackBehavior = "Ask a clarifying question when the request is ambiguous.",
            EscalationRules = "Escalate when approval is required or the request falls outside configured guardrails.",
            Temperature = 0.7f,
            MaxIterations = 5,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Agents.Add(agent);
        await _db.SaveChangesAsync(ct);
        return $"""
            ## Agent created

            Created **{agent.Name}** as a starter agent blueprint.

            - Module: [Agents](/agents)
            - Good for: {useCase}

            **Blueprint instructions**

            ```text
            {agent.Instructions}
            ```

            **Next step:** open Agents to add tools, permissions, knowledge sources, and approval rules.
            """;
    }

    private async Task<string> CreateJobFromChat(string text, string userId, CancellationToken ct)
    {
        var job = new ScheduledJob
        {
            UserId = userId,
            Name = MakeTitle(text, "Scheduled Job"),
            AgentType = "prompt",
            Input = text,
            Schedule = HasAny(text.ToLowerInvariant(), "weekly") ? "weekly" : HasAny(text.ToLowerInvariant(), "monthly") ? "monthly" : "daily",
            Hour = 9,
            DayOfWeek = 1,
            DayOfMonth = 1,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        job.NextRunAt = JobSchedulerService.CalculateNextRun(job);
        _db.ScheduledJobs.Add(job);
        await _db.SaveChangesAsync(ct);
        return $"""
            ## Job created

            Created **{job.Name}** as a `{job.Schedule}` scheduled job.

            - Module: [Jobs](/jobs)
            - Good for: running repeatable AI work without manually starting it each time
            - Schedule: `{job.Schedule}` at `{job.Hour:00}:00`
            - Next run: `{job.NextRunAt?.ToString("u") ?? "not scheduled"}`

            **Job input**

            ```text
            {job.Input}
            ```

            **Next step:** open Jobs to adjust cadence, delivery, source prompt or chain, and run history.
            """;
    }

    private async Task<string> CreatePersonaFromChat(string text, string userId, CancellationToken ct)
    {
        var persona = new Persona
        {
            UserId = userId,
            Name = MakeTitle(text, "Persona"),
            Description = $"Created from Agent Chat: {text[..Math.Min(text.Length, 220)]}",
            SystemPrompt = $"You are a persona created for this goal:\n\n{text}\n\nMaintain a consistent tone, explain assumptions, and provide useful structured answers.",
            Tone = "Helpful, concise, professional",
            Expertise = "AI productivity and prompt workflows",
            Category = "Agent Chat",
            Icon = "bi-person-badge",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Personas.Add(persona);
        await _db.SaveChangesAsync(ct);
        return $"""
            ## Persona created

            Created **{persona.Name}** as a reusable persona.

            - Module: [Personas](/personas)
            - Good for: giving prompts a consistent role, tone, expertise, and response style
            - Tone: {persona.Tone}
            - Expertise: {persona.Expertise}

            **System prompt**

            ```text
            {persona.SystemPrompt}
            ```

            **Next step:** open Personas to refine constraints, traits, examples, target audience, and public visibility.
            """;
    }

    private async Task<string> CreatePromptFromChat(string text, string userId, string category, bool isPublic, CancellationToken ct)
    {
        var prompt = new Prompt
        {
            UserId = userId,
            Name = MakeTitle(text, category),
            Description = $"Created from Agent Chat for {category}.",
            Category = category,
            Content = BuildModulePrompt(category, text),
            IsTemplate = true,
            IsPublic = isPublic,
            ApprovalStatus = isPublic ? "pending" : "draft",
            RequiresApproval = isPublic,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Prompts.Add(prompt);
        await _db.SaveChangesAsync(ct);

        var moduleLink = category == "Marketplace" ? "/marketplace" : category == "Media Studio" ? $"/media-studio?load={prompt.Id}&category={Uri.EscapeDataString(category)}" : category == "Research" ? "/research" : "/prompts";
        return $"""
            ## {category} draft created

            Created **{prompt.Name}** as a {(isPublic ? "public marketplace prompt pending approval" : "draft prompt")}.

            - Module: [{category}]({moduleLink})
            - Good for: {DescribePromptCategory(category, isPublic)}

            **Draft content**

            ```text
            {prompt.Content}
            ```

            **Next step:** open the module to refine variables, test the output, and publish or run when ready.
            """;
    }

    private static string BuildModulePrompt(string category, string text) => category switch
    {
        "Media Studio" => $"Create a polished media prompt from this request:\n\n{text}\n\nInclude subject, style, mood, composition, constraints, and platform-specific notes.",
        "Research" => $"Research scenario:\n\n{text}\n\nAnalyse across short, medium, and long-term horizons. Include drivers, risks, assumptions, and recommended follow-up questions.",
        _ => $"Prompt template request:\n\n{text}\n\nReturn a reusable, high-quality prompt with variables, constraints, and expected output format."
    };

    private static string DescribePromptCategory(string category, bool isPublic) => category switch
    {
        "Media Studio" => "turning a creative request into a reusable image, video, voice, or script prompt with style and production constraints",
        "Research" => "turning a topic into a structured research scenario with horizons, assumptions, risks, and follow-up questions",
        "Marketplace" when isPublic => "preparing a reusable community prompt listing that can be reviewed before publication",
        "Prompt" => "capturing a reusable prompt template that can be edited, tested, and reused from the prompt library",
        _ => "capturing reusable AI instructions that can be edited, tested, and reused"
    };

    private static string BuildBotLink(string title, string path, string description) =>
        $"""
        ## {title}

        {description}

        **Open:** [{path}]({path})

        Or tell me more about what you need, and I can help directly here in this chat.
        """;

    // Module-form messages append "Persona lens: …" and "Focus on these goals: …"
    // sections; those belong in the saved content, never in titles or summaries.
    private static string StripFormAppendices(string text)
    {
        foreach (var marker in new[] { "\n\nPersona lens:", "\n\nFocus on these goals:" })
        {
            var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) text = text[..idx];
        }
        return text.Trim();
    }

    private static string SummarizeUseCase(string text)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(StripFormAppendices(text), @"\s+", " ").Trim();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"^(show|select|choose|pick|list|configure|run|schedule|generate|create|draft|prepare|produce|make|need|want)\s+(an?|the)?\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
            return "handling a focused AI workflow with structured decisions, clear outputs, and safe handoff";

        return cleaned.Length <= 140
            ? cleaned
            : cleaned[..140] + "...";
    }

    // Builds chat context from an attached Interaction Intelligence session:
    // its transcript plus any per-tool analyses the user already ran.
    private async Task<string?> BuildInteractionContextAsync(string userId, string sessionId, CancellationToken ct)
    {
        try
        {
            var s = await _db.InteractionSessions
                .FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, ct);
            if (s == null) return null;

            var sb = new StringBuilder();
            sb.AppendLine("ATTACHED INTERACTION RECORDING. Use this transcript and its analyses to answer the user's questions. Cite speakers and quote moments where useful.");
            sb.AppendLine($"Recording: {s.SourceLabel}");
            sb.AppendLine();
            sb.AppendLine("TRANSCRIPT:");
            var transcript = s.Transcript ?? string.Empty;
            sb.AppendLine(transcript.Length > 24000 ? transcript[..24000] + "…[truncated]" : transcript);

            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(s.AnalysesJson ?? "{}");
                if (map is { Count: > 0 })
                {
                    sb.AppendLine();
                    sb.AppendLine("EXISTING ANALYSES (JSON, per tool):");
                    foreach (var kv in map)
                    {
                        sb.AppendLine($"## {kv.Key}");
                        sb.AppendLine(kv.Value.GetRawText());
                    }
                }
            }
            catch { /* analyses optional */ }

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    // Context for the Travel Planner bot: saved dietary preferences + live web search
    // results so place/food/price suggestions are current and personalised.
    private async Task<string> BuildTravelBotContextAsync(string userId, string userMessage, CancellationToken ct)
    {
        var sb = new StringBuilder();

        try
        {
            var prefs = (await _db.Database.SqlQuery<string?>(
                $@"SELECT ""DietaryPreferences"" AS ""Value"" FROM ""UserTravelPrefs"" WHERE ""UserId""={userId}").ToListAsync(ct)).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(prefs))
                sb.AppendLine($"TRAVELLER'S SAVED DIETARY / FOOD PREFERENCES: {prefs}. Respect these in every food and restaurant suggestion.");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Travel prefs load failed"); }

        try
        {
            if (!string.IsNullOrWhiteSpace(userMessage))
            {
                // Keep the user's own words as the query so specific intents survive
                // (e.g. "vegan Korean food in Lisbon" must not become a generic sightseeing search).
                // Only pad with travel terms when the message looks like a bare destination.
                var lower = userMessage.ToLowerInvariant();
                var foodIntent = System.Text.RegularExpressions.Regex.IsMatch(lower,
                    @"\b(food|foods|eat|eating|restaurant|restaurants|cuisine|dish|dishes|vegan|vegetarian|halal|kosher|gluten|street food|cafe|cafes|coffee|breakfast|brunch|lunch|dinner|dessert|bakery|bar|bars|michelin)\b");
                var placesIntent = System.Text.RegularExpressions.Regex.IsMatch(lower,
                    @"\b(visit|see|do|attraction|attractions|place|places|museum|museums|sight|sights|itinerary|tour|tours|things to do|day trip|hike|beach|nightlife|shopping|market|markets)\b");

                var query = foodIntent
                    ? $"{userMessage} best places to eat"
                    : placesIntent
                        ? userMessage
                        : $"{userMessage} travel things to do";

                var results = await _search.SearchAsync(query, 6);
                if (!string.IsNullOrWhiteSpace(results))
                {
                    sb.AppendLine();
                    sb.AppendLine($"LIVE WEB SEARCH RESULTS for \"{query}\" (use for current places, opening hours, prices, and events; prefer these over memory for anything time-sensitive; answer the user's SPECIFIC ask — if they asked for a particular cuisine or type of place, only recommend that):");
                    sb.AppendLine(results);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Travel web search failed"); }

        // If they're asking about fares/best time to fly a route, add cached fare data.
        try
        {
            if (_flights.IsConfigured && !string.IsNullOrWhiteSpace(userMessage) &&
                System.Text.RegularExpressions.Regex.IsMatch(userMessage, @"\b(flight|fly|flying|fare|fares|airfare|ticket|cheapest|best time)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                var m = System.Text.RegularExpressions.Regex.Match(userMessage,
                    @"([A-Za-z][A-Za-z .'-]{1,28}?)\s+to\s+([A-Za-z][A-Za-z .'-]{1,28}?)(?:[.?!,;]|\s+in\b|\s+on\b|\s+for\b|\s+next\b|\s+this\b|$)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var insight = await _flights.GetInsightAsync(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim(), null);
                    if (insight.Available && insight.Cheapest != null)
                    {
                        sb.AppendLine();
                        sb.AppendLine($"FLIGHT FARE INSIGHT (Travelpayouts cached fares) {insight.OriginCode}→{insight.DestinationCode}: cheapest around {insight.Cheapest.MonthLabel} (~{insight.Currency} {insight.Cheapest.Price}); priciest {insight.Priciest?.MonthLabel} (~{insight.Currency} {insight.Priciest?.Price}). {insight.Suggestion} Use this to recommend the best time to travel and to book.");
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Travel fare lookup failed"); }

        return sb.ToString().Trim();
    }

    private async Task<string> BuildCalendarBotContextAsync(string userId, CancellationToken ct)
    {
        try
        {
            var from = DateTime.UtcNow.AddHours(-2).ToString("o");
            // Identifiers must be quoted: on Postgres the table is created as
            // "CalendarEvents" (Program.cs), so an unquoted reference folds to
            // lowercase and the query fails. SqlQuery (not SqlQueryRaw) turns the
            // interpolated values into real parameters — see AvailabilityService.
            var rows = await _db.Database.SqlQuery<CalendarContextRow>($@"
                SELECT ""Title"", ""Description"", ""Location"", ""MeetingLink"", ""StartDateTime"", ""EndDateTime"", ""OrganizerEmail"", ""OrganizerName"", ""AttendeesJson""
                FROM ""CalendarEvents""
                WHERE ""UserId"" = {userId} AND ""StartDateTime"" >= {from}
                ORDER BY ""StartDateTime""
                LIMIT 12").ToListAsync(ct);

            if (rows.Count == 0)
                return "CALENDAR CONTEXT: No upcoming synced calendar events were found. If the user asks about meetings or prep, ask them to connect Google Calendar or Outlook and sync their calendar.";

            var sb = new StringBuilder("CALENDAR CONTEXT: Upcoming synced meetings/events for the user. Use this to answer scheduling and meeting-prep questions. Do not invent events.\n");
            foreach (var e in rows)
            {
                sb.AppendLine($"- {e.StartDateTime} to {e.EndDateTime}: {e.Title}");
                if (!string.IsNullOrWhiteSpace(e.OrganizerName) || !string.IsNullOrWhiteSpace(e.OrganizerEmail))
                    sb.AppendLine($"  Organizer: {e.OrganizerName} {e.OrganizerEmail}".Trim());
                if (!string.IsNullOrWhiteSpace(e.Location))
                    sb.AppendLine($"  Location: {e.Location}");
                if (!string.IsNullOrWhiteSpace(e.MeetingLink))
                    sb.AppendLine($"  Meeting link: {e.MeetingLink}");
                if (!string.IsNullOrWhiteSpace(e.Description))
                    sb.AppendLine($"  Notes: {e.Description[..Math.Min(e.Description.Length, 400)]}");
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load calendar context for Agent Chat");
            return "CALENDAR CONTEXT: Calendar events could not be loaded. Ask the user to connect or sync their calendar if meeting prep requires live calendar data.";
        }
    }

    private sealed class CalendarContextRow
    {
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Location { get; set; }
        public string? MeetingLink { get; set; }
        public string StartDateTime { get; set; } = string.Empty;
        public string EndDateTime { get; set; } = string.Empty;
        public string? OrganizerEmail { get; set; }
        public string? OrganizerName { get; set; }
        public string? AttendeesJson { get; set; }
    }

    private static bool HasIntentWord(string lower) =>
        HasAny(lower, "show", "select", "choose", "pick", "list", "configure", "run", "schedule", "generate", "create", "draft", "prepare", "produce", "make", "need", "want");

    private static bool HasAny(string lower, params string[] terms) =>
        terms.Any(term => lower.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string MakeTitle(string text, string fallback)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(StripFormAppendices(text), @"\s+", " ").Trim();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"^(show|select|choose|pick|list|configure|run|schedule|generate|create|draft|prepare|produce|make|need|want)\s+(an?|the)?\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"^(media prompt|marketplace prompt|research brief|prompt|agent|persona|job)s?\s+(for|about|to)\s+(an?|the)?\s*", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = fallback;
        return cleaned.Length <= 72 ? cleaned : cleaned[..72] + "…";
    }
}

public record RenameRequest(string Title);
