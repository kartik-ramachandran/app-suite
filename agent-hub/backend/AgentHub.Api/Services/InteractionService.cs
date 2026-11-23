using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// Interaction Intelligence engine. Industry-agnostic: turns any recording
/// (call, consult, meeting, interview …) into a transcript, then runs a
/// tool-specific LLM analysis.
///
/// Transcription uses Azure AI Speech (Fast Transcription REST API) — NOT
/// OpenAI Whisper. Large/video files are audio-extracted and compressed with
/// ffmpeg first (mono 16 kHz mp3) so the upload to Azure stays small.
/// Analysis runs through IAIService, which is configured Azure-OpenAI-first.
/// </summary>
public class InteractionService
{
    private readonly IAIService _aiService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<InteractionService> _logger;

    public InteractionService(
        IAIService aiService,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<InteractionService> logger)
    {
        _aiService = aiService;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Transcribe arbitrary audio/video at <paramref name="inputPath"/> via Azure
    /// AI Speech. Extracts compressed mono audio with ffmpeg first.
    /// </summary>
    public async Task<string> TranscribeAsync(string inputPath)
    {
        var key = _configuration["AzureSpeech:SubscriptionKey"];
        var region = _configuration["AzureSpeech:Region"] ?? "eastus";
        var language = _configuration["AzureSpeech:SttLanguage"] ?? "en-US";

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "Transcription isn't available right now — you can paste or upload an existing transcript instead.");

        var ffmpeg = _configuration["Tools:Ffmpeg:Path"] ?? "ffmpeg";
        var workDir = Directory.CreateTempSubdirectory("interaction-stt-");
        var audioPath = Path.Combine(workDir.FullName, "audio.mp3");

        try
        {
            // Extract audio (-vn), mono (-ac 1), 16 kHz (-ar 16000), low bitrate (-b:a 32k).
            // A 1-hour recording compresses to ~14 MB regardless of the source container.
            await RunProcessAsync(ffmpeg, $"-i \"{inputPath}\" -vn -ac 1 -ar 16000 -b:a 32k \"{audioPath}\"");

            if (!File.Exists(audioPath) || new FileInfo(audioPath).Length == 0)
                throw new InvalidOperationException("No audio could be extracted from this file.");

            return await AzureFastTranscribeAsync(audioPath, key, region, language);
        }
        finally
        {
            try { workDir.Delete(true); } catch { /* best effort */ }
        }
    }

    // Azure AI Speech "Fast transcription" — one synchronous POST returns the
    // combined transcript. Handles long recordings (up to the service limits)
    // without us managing batch jobs or blob storage.
    private async Task<string> AzureFastTranscribeAsync(string audioPath, string key, string region, string language)
    {
        var endpoint = $"https://{region}.api.cognitive.microsoft.com/speechtotext/transcriptions:transcribe?api-version=2024-11-15";

        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        using var form = new MultipartFormDataContent();

        var audioBytes = await File.ReadAllBytesAsync(audioPath);
        var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
        form.Add(audioContent, "audio", "audio.mp3");

        // Enable speaker diarization so the transcript is attributed per speaker.
        // Diarization runs on mono audio (which our ffmpeg step produces).
        var definition = JsonSerializer.Serialize(new
        {
            locales = new[] { language },
            profanityFilterMode = "Masked",
            diarization = new { maxSpeakers = 8, enabled = true }
        });
        form.Add(new StringContent(definition, Encoding.UTF8, "application/json"), "definition");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        request.Headers.Add("Ocp-Apim-Subscription-Key", key);

        using var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Azure Speech transcription failed ({Status}): {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException(
                $"Azure Speech transcription failed ({(int)response.StatusCode}). Check the AzureSpeech key/region.");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Preferred: speaker-attributed transcript from diarized phrases.
        var diarized = BuildDiarizedTranscript(root);
        if (!string.IsNullOrWhiteSpace(diarized)) return diarized;

        // Fallback: combined transcript (no diarization available).
        var sb = new StringBuilder();
        if (root.TryGetProperty("combinedPhrases", out var combined) && combined.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in combined.EnumerateArray())
                if (c.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString()).Append(' ');
        }

        return sb.ToString().Trim();
    }

    // Builds a "Speaker N: …" transcript from the diarized phrases. Consecutive
    // phrases from the same speaker are merged into one turn. Phrases without a
    // speaker id (e.g. single-speaker audio) default to Speaker 1.
    private static string BuildDiarizedTranscript(JsonElement root)
    {
        if (!root.TryGetProperty("phrases", out var phrases) || phrases.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var turns = new List<(long Offset, int Speaker, string Text)>();
        foreach (var p in phrases.EnumerateArray())
        {
            var text = p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text)) continue;

            var offset = p.TryGetProperty("offsetMilliseconds", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt64() : 0;
            var speaker = p.TryGetProperty("speaker", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
            turns.Add((offset, speaker, text.Trim()));
        }

        if (turns.Count == 0) return string.Empty;

        turns.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var sb = new StringBuilder();
        var current = int.MinValue;
        foreach (var turn in turns)
        {
            var speaker = turn.Speaker > 0 ? turn.Speaker : 1;
            if (speaker != current)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("Speaker ").Append(speaker).Append(": ");
                current = speaker;
            }
            else
            {
                sb.Append(' ');
            }
            sb.Append(turn.Text);
        }

        return sb.ToString().Trim();
    }

    // ── Transcript ingestion (no speech-to-text) ────────────────────────────
    // Accepts an ALREADY-WRITTEN transcript from a pasted string or an uploaded
    // text/caption/PDF/Word file, so a meeting that already produced a transcript
    // (Teams, Zoom, Otter, a court reporter, …) can be summarised without paying
    // for transcription. Teams/Zoom export .vtt/.docx/.txt — all handled here.

    private static readonly string[] TranscriptExtensions =
        { ".txt", ".md", ".vtt", ".srt", ".pdf", ".docx" };

    public static bool IsSupportedTranscriptFile(string fileName) =>
        TranscriptExtensions.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Extract a clean, readable transcript from an uploaded file.</summary>
    public async Task<string> ExtractTranscriptFromFileAsync(string path, string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var transcript = ext switch
        {
            ".vtt" or ".srt" => CleanCaptions(await File.ReadAllTextAsync(path)),
            ".pdf" => ExtractPdfText(await File.ReadAllBytesAsync(path)),
            ".docx" => ExtractDocxText(await File.ReadAllBytesAsync(path)),
            ".doc" => throw new InvalidOperationException(
                "Legacy .doc files aren't supported — save it as .docx, .txt, or PDF and upload again."),
            _ => NormalizeWhitespace(await File.ReadAllTextAsync(path)),
        };

        if (string.IsNullOrWhiteSpace(transcript))
            throw new InvalidOperationException("No readable transcript text was found in that file.");
        return transcript;
    }

    /// <summary>Clean a pasted transcript (also strips caption timestamps if pasted).</summary>
    public static string CleanPastedTranscript(string raw) =>
        raw.Contains("-->") ? CleanCaptions(raw) : NormalizeWhitespace(raw);

    // WebVTT / SRT → readable transcript. Drops the WEBVTT header, cue numbers and
    // timestamp lines; keeps speaker attribution (Teams uses <v Name>…</v>, Zoom
    // uses "Name: …") and merges consecutive lines from the same speaker.
    private static string CleanCaptions(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        string? lastSpeaker = null;

        foreach (var lineRaw in lines)
        {
            var line = lineRaw.Trim();
            if (line.Length == 0) continue;
            if (line.Equals("WEBVTT", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Kind:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith("Language:", StringComparison.OrdinalIgnoreCase)) continue;
            if (line.Contains("-->")) continue;                          // timestamp cue
            if (Regex.IsMatch(line, @"^\d+$")) continue;                 // sequence number

            // Speaker from a <v Name> voice tag (Teams).
            string? speaker = null;
            var vMatch = Regex.Match(line, @"<v\s+([^>]+)>", RegexOptions.IgnoreCase);
            if (vMatch.Success) speaker = vMatch.Groups[1].Value.Trim();

            var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(line, @"<[^>]+>", " ")).Trim();
            text = Regex.Replace(text, @"\s{2,}", " ");
            if (text.Length == 0) continue;

            // "Name: text" style (Zoom, and many exporters).
            if (speaker == null)
            {
                var m = Regex.Match(text, @"^([\p{L}][\p{L}\p{N}\.\-'’ ]{0,58}?):\s+(.+)$");
                if (m.Success) { speaker = m.Groups[1].Value.Trim(); text = m.Groups[2].Value.Trim(); }
            }

            if (speaker != null && !string.Equals(speaker, lastSpeaker, StringComparison.OrdinalIgnoreCase))
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(speaker).Append(": ").Append(text);
                lastSpeaker = speaker;
            }
            else
            {
                sb.Append(sb.Length > 0 ? " " : string.Empty).Append(text);
            }
        }
        return sb.ToString().Trim();
    }

    private static string ExtractDocxText(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var para in body.Descendants<Paragraph>())
        {
            var text = string.Concat(para.Descendants<Text>().Select(t => t.Text)).Trim();
            if (text.Length > 0) sb.AppendLine(text);
        }
        return NormalizeWhitespace(sb.ToString());
    }

    private static string ExtractPdfText(byte[] bytes)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(bytes);
        foreach (var page in doc.GetPages())
            sb.AppendLine(page.Text);
        return NormalizeWhitespace(sb.ToString());
    }

    private static string NormalizeWhitespace(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Regex.Replace(s, @"[ \t]+", " ");
        s = Regex.Replace(s, @" *\n *", "\n");    // trim spaces hugging line breaks
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>
    /// Run a tool-specific analysis over a transcript. Returns a generic,
    /// render-able structure so one frontend renderer can present all tools.
    /// </summary>
    public async Task<InteractionAnalysis> AnalyseAsync(string transcript, string tool, string? intent = null)
    {
        // Use TestPromptAsync (system prompt + user content), NOT QuickRefineAsync —
        // the latter frames input as "a prompt to refine" which both fights the
        // instruction and yields shallow output. This routes Azure-OpenAI-first.
        var response = await _aiService.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = BuildInstruction(tool, intent),
            UserInput = transcript
        });
        return ParseAnalysis(response.Result);
    }

    /// <summary>
    /// Suggest goals the user might want from this recording — grounded in the
    /// transcript when one is available, otherwise broadly useful. Pass already-shown
    /// goals in <paramref name="existing"/> to get a fresh batch (the "show more" path).
    /// </summary>
    public async Task<List<string>> SuggestGoalsAsync(string? transcript, string? seed, IReadOnlyCollection<string>? existing, int count = 20)
    {
        var instruction = BuildGoalSuggestionInstruction(existing, count);

        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(seed))
            sb.Append("USER CONTEXT (what they care about): ").Append(seed.Trim()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(transcript))
        {
            var t = transcript.Length > 12000 ? transcript.Substring(0, 12000) : transcript;
            sb.Append("TRANSCRIPT (may be truncated) — tailor the goals to what it actually contains:\n").Append(t);
        }
        else
        {
            sb.Append("No transcript is available yet — propose broadly useful goals for analysing this kind of recording.");
        }

        var response = await _aiService.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = instruction,
            UserInput = sb.ToString()
        });
        return ParseGoals(response.Result, existing, count);
    }

    private static string BuildGoalSuggestionInstruction(IReadOnlyCollection<string>? existing, int count)
    {
        var avoid = existing is { Count: > 0 }
            ? "\n\nThe user has ALREADY seen the goals below — do NOT repeat or closely paraphrase any of them; propose entirely new, different ones:\n- "
              + string.Join("\n- ", existing.Take(80))
            : string.Empty;
        return
            "You help a user decide what to get out of analysing a recorded interaction (support call, meeting, consult, sales " +
            "call, interview — ANY industry). " +
            $"Propose EXACTLY {count} specific, DISTINCT goals or questions the user might want this analysis to answer or achieve. " +
            "Ground them in the transcript when one is provided; otherwise propose broadly useful goals for this kind of recording. " +
            "Each goal is ONE short, concrete sentence — an imperative or question the analysis could serve, e.g. " +
            "'Find out why the customer is unhappy and whether they'll churn', 'Check the advisor gave the required disclosures', " +
            "'List the action items and who owns each', 'Assess the agent's tone and empathy', " +
            "'Identify the main objection and how it was handled'. " +
            "Keep each under 120 characters. Make them genuinely different from one another — span outcomes, risks, compliance, " +
            "coaching, sentiment, facts and next steps." + avoid + "\n\n" +
            "Return ONLY minified JSON: {\"goals\": [string, ...]}. No commentary, no markdown fences.";
    }

    private static List<string> ParseGoals(string raw, IReadOnlyCollection<string>? existing, int count)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (existing != null)
            foreach (var e in existing) seen.Add(NormalizeGoal(e));

        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(ExtractJson(raw));
            if (doc.RootElement.TryGetProperty("goals", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in arr.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.String) continue;
                    var s = CleanGoal(g.GetString());
                    if (string.IsNullOrEmpty(s) || !seen.Add(NormalizeGoal(s))) continue;
                    result.Add(s);
                    if (result.Count >= count) break;
                }
            }
        }
        catch (JsonException) { /* return whatever parsed */ }
        return result;
    }

    private static string CleanGoal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var v = Regex.Replace(s.Trim(), @"^\s*(\d+[\.\)]|[-*•])\s*", "").Trim();  // strip leading numbering/bullets
        return v.Length > 160 ? v.Substring(0, 160) : v;
    }

    private static string NormalizeGoal(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    public static bool IsKnownTool(string tool) => ToolFocus.ContainsKey(tool);

    // What each tool should focus on. The output schema is shared; only the lens differs.
    // Each focus is written to demand depth — specific, evidence-backed, comprehensive.
    private static readonly Dictionary<string, string> ToolFocus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["summaries"] =
            "TASK: Produce a thorough, decision-grade summary of the interaction — the kind a leader could read instead of " +
            "listening to the whole recording. " +
            "headline = a single sharp executive takeaway (the one thing a busy leader must know). " +
            "metrics = [{label:\"Sentiment\", value:...}, {label:\"Escalation risk\", value:...}, {label:\"Topics\", value: count}, {label:\"Decisions\", value: count}]. " +
            "sections = [" +
            "{heading:\"Overview\", items: 4-7 information-dense sentences covering the purpose, context/background, the headline outcome and how it concluded}, " +
            "{heading:\"Participants & roles\", items: one bullet per speaker — who they appear to be (role) and the stance/position they took}, " +
            "{heading:\"Discussion by topic\", items: one detailed bullet PER topic discussed — the topic, the specifics (numbers/systems/names), each party's position, and any trade-offs}, " +
            "{heading:\"Decisions made\", items: each decision reached and the rationale behind it}, " +
            "{heading:\"Key points\", items: 6-12 specific bullets capturing the most important facts, including disagreements}, " +
            "{heading:\"Risks & open questions\", items: unresolved items, risks, dependencies and assumptions}, " +
            "{heading:\"Action items\", items: EVERY commitment or follow-up, each 'Owner — concrete action — why/context — due date or dependency if mentioned'}, " +
            "{heading:\"Notable quotes\", items: 2-4 impactful verbatim quotes, each prefixed with the speaker}" +
            "]. pairs = [].",
        ["wrap-up"] =
            "TASK: Do the COMPLETE after-interaction wrap-up work a diligent professional would do, ready to action immediately. " +
            "headline = the single most important next step and by when. " +
            "metrics = [{label:\"Follow-up needed\", value: Yes/No}, {label:\"Priority\", value: Low/Medium/High}, {label:\"Owner\", value:...}, {label:\"Due\", value:...}]. " +
            "sections = [" +
            "{heading:\"Next steps\", items: detailed, sequenced steps — each with the owner, the concrete action, and timing}, " +
            "{heading:\"Record / CRM notes\", items: structured notes ready to paste into a CRM — status, key facts, commitments made, risks, sentiment}, " +
            "{heading:\"Systems & fields to update\", items: what data should be updated where (records, statuses, fields) as a result of this interaction}, " +
            "{heading:\"Internal escalations & handoffs\", items: who needs to be looped in or what needs handing off, and why}, " +
            "{heading:\"Tags\", items: 4-8 categorisation tags}" +
            "]. " +
            "pairs = [" +
            "{label:\"Draft follow-up message\", value: a complete, ready-to-send, professionally written external message reflecting what was agreed}, " +
            "{label:\"Internal handoff note\", value: a concise internal note summarising what the next person needs to know}].",
        ["quality"] =
            "TASK: Act as a senior QA reviewer and produce a DETAILED QA SCORECARD. Score the interaction rigorously and " +
            "ground every score in evidence (quote or cite the moment). If multiple speakers, evaluate the agent/host/professional. " +
            "headline = overall verdict with the headline score. " +
            "metrics = [{label:\"Overall\", value:\"N/100\"}, and a separate metric for 4-5 individual criteria scored out of 10]. " +
            "sections = [" +
            "{heading:\"Scorecard\", items: EXACTLY one bullet per criterion — 'Criterion — N/10 — justification citing a real moment'. " +
            "Score ALL of: Opening & rapport; Needs identification/discovery; Accuracy of information; Empathy & tone; Ownership & proactivity; " +
            "Resolution/outcome; Compliance & process; Closing & next steps}, " +
            "{heading:\"Strengths\", items: 3-5 specific things done well, each quoting/citing the moment}, " +
            "{heading:\"Areas to improve\", items: 3-5 specific gaps — what happened and what should have happened instead}, " +
            "{heading:\"Customer / outcome impact\", items: how the handling affected the outcome and the other party's experience}, " +
            "{heading:\"Compliance & process adherence\", items: where required process/scripts/disclosures were or weren't followed}" +
            "]. pairs = [].",
        ["compliance"] =
            "TASK: Act as a compliance & risk reviewer and produce a DETAILED compliance assessment (industry-neutral: data " +
            "handling/PII, required disclosures & consent, identity verification, promises/guarantees, prohibited or misleading " +
            "statements, regulatory obligations). Cite evidence for every point. " +
            "headline = overall compliance verdict and why. " +
            "metrics = [{label:\"Status\", value: Pass/Review/Fail}, {label:\"Risk level\", value: Low/Medium/High}, {label:\"Issues\", value: count}, {label:\"Critical\", value: count}]. " +
            "sections = [" +
            "{heading:\"Issues found\", items: each as 'Issue — severity (Critical/High/Medium/Low) — evidence/quote — recommended remediation'}, " +
            "{heading:\"Disclosures & obligations met\", items: required disclosures/obligations that WERE correctly handled, with the moment}, " +
            "{heading:\"Disclosures & obligations missed\", items: obligations missed or only partially met, and the consequence}, " +
            "{heading:\"Data handling & privacy\", items: how personal/sensitive data and consent were handled}, " +
            "{heading:\"Regulatory considerations\", items: relevant regulatory/policy angles to be aware of for this interaction type}, " +
            "{heading:\"Prioritised remediations\", items: concrete fixes ordered by urgency, with suggested owner}" +
            "]. pairs = [].",
        ["coaching"] =
            "TASK: Produce a FULL, DETAILED COACHING SCORECARD for the person being coached. If there are multiple speakers, " +
            "coach the agent/host/professional (the one serving or leading), not the customer/client. Ground EVERY point in the " +
            "transcript: quote or closely paraphrase the actual moment, and reference the speaker label and rough position " +
            "(e.g. 'early in the call', 'when discussing billing') where you can. Do not be generic. " +
            "headline = the single highest-impact coaching focus. " +
            "metrics = [{label:\"Overall\", value:\"N/5\"}, {label:\"Skill level\", value: Developing/Proficient/Strong}, " +
            "{label:\"Top strength\", value: short phrase}, {label:\"Biggest gap\", value: short phrase}]. " +
            "sections = [" +
            "{heading:\"Competency scorecard\", items: EXACTLY one bullet per competency, each formatted " +
            "'Competency — N/5 — concise justification citing a real moment'. Score ALL of these competencies: " +
            "Active listening; Empathy & rapport; Communication & clarity; Questioning & discovery; Knowledge & accuracy; " +
            "Problem solving & resolution; Compliance & process; Closing & next steps}, " +
            "{heading:\"Strengths to reinforce\", items: 3-5 specific strengths, each citing the moment it happened and why it worked}, " +
            "{heading:\"Priority development areas\", items: 3-5 specific gaps, each as 'What happened — why it matters (impact) — what to do instead'}, " +
            "{heading:\"Practice drills\", items: 3-5 concrete exercises, scripts or techniques the person can rehearse to close the gaps}, " +
            "{heading:\"Development plan (next 30 days)\", items: 3-4 sequenced focus areas, each with a measurable goal}" +
            "]. " +
            "pairs = 4-8 entries, each {label: a weak or ineffective phrase the speaker ACTUALLY said (quote it), " +
            "value: a stronger, recommended rephrasing}.",
        ["sentiment"] =
            "TASK: Perform a DETAILED sentiment, emotion and escalation-risk analysis, per speaker and over time. " +
            "headline = the overall emotional arc of the interaction (how it started, shifted and ended). " +
            "metrics = [{label:\"Overall sentiment\", value:...}, {label:\"Customer/caller\", value:...}, {label:\"Agent/host\", value:...}, " +
            "{label:\"Escalation risk\", value: Low/Medium/High}, {label:\"Trend\", value: Improving/Stable/Declining}]. " +
            "sections = [" +
            "{heading:\"Sentiment by speaker\", items: one bullet per speaker — their overall emotional state and tone, with evidence}, " +
            "{heading:\"Emotional timeline\", items: chronological notable moments — what was said and how sentiment shifted, with speaker labels}, " +
            "{heading:\"Escalation triggers\", items: specific phrases/events that raised tension or risk, and why}, " +
            "{heading:\"De-escalation opportunities\", items: what worked to calm things, and what could have calmed the situation sooner}, " +
            "{heading:\"Sentiment drivers & root causes\", items: the underlying reasons behind the emotions observed}, " +
            "{heading:\"Risk indicators & watch-outs\", items: signals that this could escalate or churn, to flag for follow-up}" +
            "]. pairs = [].",
        ["knowledge"] =
            "TASK: Act as a thorough knowledge assistant. Identify EVERY question or unresolved issue raised and supply " +
            "accurate, detailed, usable answers, plus the knowledge the agent was missing. " +
            "headline = the core information need of the interaction. " +
            "metrics = [{label:\"Questions raised\", value: count}, {label:\"Answered live\", value: count}, {label:\"Knowledge gaps\", value: count}]. " +
            "sections = [" +
            "{heading:\"Questions & issues raised\", items: each question/issue, who asked it, and whether it was resolved during the interaction}, " +
            "{heading:\"Knowledge gaps identified\", items: points where the agent lacked information, was unsure, or gave an incomplete/incorrect answer}, " +
            "{heading:\"Recommended resources & topics\", items: specific knowledge-base topics, docs, policies or SMEs the agent should reference}, " +
            "{heading:\"Follow-up information needed\", items: what still needs to be found out or confirmed after the interaction}" +
            "]. " +
            "pairs = one entry PER question/issue {label: the question asked, value: a detailed, accurate, ready-to-use suggested answer}.",
        ["audit"] =
            "TASK: Produce a formal, DETAILED assurance / audit report suitable for a compliance or quality record. " +
            "headline = the overall assurance statement. " +
            "metrics = [{label:\"Risk rating\", value: Low/Medium/High}, {label:\"Quality\", value: score or grade}, " +
            "{label:\"Compliance\", value: Pass/Review/Fail}, {label:\"Assurance\", value: a one-word confidence level}]. " +
            "sections = [" +
            "{heading:\"Scope & context\", items: what this interaction was, who was involved, and what was assessed}, " +
            "{heading:\"Checks performed\", items: each control checked as 'Check — Pass/Fail — evidence/quote'. Cover at least " +
            "identity/authority, disclosures & consent, accuracy, process adherence, commitments, and data handling}, " +
            "{heading:\"Findings\", items: detailed findings, each with severity and business/customer impact}, " +
            "{heading:\"Root causes\", items: the underlying causes behind the findings}, " +
            "{heading:\"Recommendations\", items: prioritised, concrete remediation actions, each with a suggested owner and timeframe}, " +
            "{heading:\"Assurance statement\", items: 2-3 sentences giving the overall assurance conclusion and residual risk}" +
            "]. pairs = [].",
    };

    private static string BuildInstruction(string tool, string? intent = null)
    {
        var focus = ToolFocus.TryGetValue(tool, out var f) ? f : ToolFocus["summaries"];
        return
            "You are a senior interaction analyst producing a thorough, decision-grade report from the transcript of a " +
            "recorded interaction. It may be from ANY industry (healthcare, finance, support, sales, legal, etc.) — adapt " +
            "your terminology to what the transcript reveals. Read the ENTIRE transcript carefully before answering.\n\n" +
            focus +
            GoalClause(intent) + "\n\n" +
            "DEPTH & QUALITY REQUIREMENTS (critical):\n" +
            "- Be specific and concrete: name the people, roles, systems, products, numbers, dates, amounts and decisions " +
            "actually mentioned. Quote or paraphrase real moments from the transcript as evidence.\n" +
            "- Write full, information-dense sentences — never terse fragments or generic filler.\n" +
            "- Be comprehensive: cover every substantive topic, including disagreements, risks and trade-offs. Favour more " +
            "detail over less; aim for the higher end of any suggested item counts.\n" +
            "- NEVER invent facts that are not supported by the transcript. If something is unclear or absent, say so plainly.\n\n" +
            "OUTPUT FORMAT: Return ONLY a minified JSON object with EXACTLY these keys: " +
            "{\"headline\": string, " +
            "\"metrics\": [{\"label\": string, \"value\": string, \"tone\": one of \"good\"|\"warn\"|\"bad\"|\"neutral\"}], " +
            "\"sections\": [{\"heading\": string, \"items\": string[]}], " +
            "\"pairs\": [{\"label\": string, \"value\": string}]}. " +
            "Use [] for any part that does not apply. No commentary, no markdown fences.";
    }

    // When the user states what they want from this recording, make it the priority
    // lens for every tool — without dropping the tool's full structured output.
    private static string GoalClause(string? intent)
    {
        var goal = intent?.Trim();
        if (string.IsNullOrEmpty(goal)) return string.Empty;
        // Cap length so a runaway paste can't blow up the prompt.
        if (goal.Length > 600) goal = goal.Substring(0, 600);
        return
            "\n\nUSER'S GOAL FOR THIS ANALYSIS (treat as the priority lens): the person requesting this analysis said they " +
            $"want:\n\"{goal}\"\n" +
            "Lead with and emphasise whatever most directly serves this goal, make sure it is answered thoroughly and " +
            "explicitly, and surface everything in the transcript relevant to it. If the transcript does not address the " +
            "goal, say so plainly rather than inventing an answer. Still return the COMPLETE structured output described above.";
    }

    private static readonly JsonDocumentOptions LenientJson = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static InteractionAnalysis ParseAnalysis(string raw)
    {
        var json = ExtractJson(raw);
        var sanitized = SanitizeJson(json);
        foreach (var candidate in new[] { json, RepairJson(json), sanitized, RepairJson(sanitized) })
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate, LenientJson);
                return ReadAnalysis(doc.RootElement);
            }
            catch (JsonException) { /* try the repaired candidate */ }
        }

        // Unparseable even after repair — present as readable text, never as a JSON blob.
        var fallback = new InteractionAnalysis { Headline = "Analysis" };
        var lines = raw.Trim()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (lines.Count > 0)
            fallback.Sections.Add(new Section { Heading = "Result", Items = lines });
        return fallback;
    }

    private static InteractionAnalysis ReadAnalysis(JsonElement root)
    {
        var result = new InteractionAnalysis
        {
            Headline = GetString(root, "headline")
        };

        if (root.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in metrics.EnumerateArray())
            {
                var label = GetString(m, "label");
                var value = GetString(m, "value");
                if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(value)) continue;
                result.Metrics.Add(new Metric { Label = label, Value = value, Tone = GetString(m, "tone", "neutral") });
            }
        }

        if (root.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sections.EnumerateArray())
                ReadSections(s, result.Sections);
        }

        if (root.TryGetProperty("pairs", out var pairs) && pairs.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in pairs.EnumerateArray())
            {
                var label = GetString(p, "label");
                var value = GetString(p, "value");
                if (string.IsNullOrWhiteSpace(label) && string.IsNullOrWhiteSpace(value)) continue;
                result.Pairs.Add(new Pair { Label = label, Value = value });
            }
        }

        return result;
    }

    // The model occasionally flattens several sections into ONE object with repeated
    // "heading"/"items" keys. EnumerateObject yields duplicates in order, so start a
    // new section every time "heading" reappears.
    private static void ReadSections(JsonElement el, List<Section> target)
    {
        if (el.ValueKind != JsonValueKind.Object) return;
        var heading = string.Empty;
        var items = new List<string>();
        var open = false;

        void Flush()
        {
            if (string.IsNullOrWhiteSpace(heading) && items.Count == 0) return;
            target.Add(new Section { Heading = heading, Items = items });
            items = new List<string>();
        }

        foreach (var prop in el.EnumerateObject())
        {
            if (prop.NameEquals("heading"))
            {
                if (open) Flush();
                heading = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? string.Empty : string.Empty;
                open = true;
            }
            else if (prop.NameEquals("items") && prop.Value.ValueKind == JsonValueKind.Array)
            {
                open = true;
                foreach (var item in prop.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) continue;
                    var v = item.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) items.Add(v);
                }
            }
        }
        Flush();
    }

    // Models occasionally emit typographic quotes or raw control characters inside
    // strings — both rejected by strict JSON parsers. Normalize them away.
    private static string SanitizeJson(string json)
    {
        var sb = new System.Text.StringBuilder(json.Length);
        foreach (var ch in json)
        {
            if (ch == '“' || ch == '”') sb.Append('"');
            else if (ch == '‘' || ch == '’') sb.Append('\'');
            else if (ch < ' ') sb.Append(' ');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    // Best-effort repair for truncated model output: close an unterminated string,
    // then close any unclosed arrays/objects (trailing commas are handled by the
    // lenient parse options).
    private static string RepairJson(string json)
    {
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;
        foreach (var ch in json)
        {
            if (escaped) { escaped = false; continue; }
            if (ch == '\\') { escaped = inString; continue; }
            if (ch == '"') { inString = !inString; continue; }
            if (inString) continue;
            switch (ch)
            {
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}':
                case ']':
                    if (stack.Count > 0 && stack.Peek() == ch) stack.Pop();
                    break;
            }
        }
        var sb = new System.Text.StringBuilder(json);
        if (inString) sb.Append('"');
        while (stack.Count > 0) sb.Append(stack.Pop());
        return sb.ToString();
    }

    private static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw.Substring(start, end - start + 1) : raw;
    }

    private static string GetString(JsonElement el, string name, string fallback = "") =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    private static List<string> GetStringArray(JsonElement el, string name)
    {
        var list = new List<string>();
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var v = item.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) list.Add(v);
                }
            }
        }
        return list;
    }

    private async Task RunProcessAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Audio processing tool '{fileName}' is not available. Install ffmpeg or set Tools:Ffmpeg:Path.", ex);
        }

        using (process)
        {
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                _logger.LogWarning("ffmpeg failed ({Code}): {Error}", process.ExitCode, error);
                throw new InvalidOperationException("Could not process this recording. It may be corrupt or in an unsupported format.");
            }
        }
    }
}

public class InteractionAnalysis
{
    public string Headline { get; set; } = string.Empty;
    public List<Metric> Metrics { get; set; } = new();
    public List<Section> Sections { get; set; } = new();
    public List<Pair> Pairs { get; set; } = new();
}

public class Metric
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Tone { get; set; } = "neutral";
}

public class Section
{
    public string Heading { get; set; } = string.Empty;
    public List<string> Items { get; set; } = new();
}

public class Pair
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
