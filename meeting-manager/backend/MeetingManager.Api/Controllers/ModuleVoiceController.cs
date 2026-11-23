using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MeetingManager.Api.Data;
using MeetingManager.Api.Services;

namespace MeetingManager.Api.Controllers;

/// <summary>
/// Talk to your wardrobe, your kitchen, or your travel plans.
///
/// WHY THIS EXISTS
/// These three modules already hold rich personal data — every garment you own,
/// what is in your pantry, where you are going — but the only way to use it was
/// to click through forms. "What can I wear to a wedding in October?" is a
/// question you ask out loud, and the answer needs the wardrobe you actually
/// have, not a generic style article.
///
/// ONE CONTROLLER, THREE MODULES
/// The shape is identical in each case: transcribe speech, load what we know
/// about this user in this domain, answer using only that. Splitting it three
/// ways would triple the places the allergy rule has to be remembered.
///
/// SAFETY
/// Kitchen is a food domain: the user's stored allergies are injected into
/// every prompt as a hard constraint, and the reply is screened with the same
/// deterministic filter used by the LifeTracker guardrails. It fails closed —
/// a withheld answer beats a dangerous one. Wardrobe and travel carry no
/// physical risk and are not screened, but still carry a disclaimer because
/// the model is reasoning about photos and free text it may have misread.
/// </summary>
[ApiController]
[Route("api/module-voice")]
[Authorize]
public class ModuleVoiceController : ControllerBase
{
    private readonly PromptDbContext _db;
    private readonly AIServiceManager _ai;
    private readonly AzureVisionService _text;
    private readonly ILogger<ModuleVoiceController> _logger;

    /// <summary>~20 minutes of speech. Matches the LifeVoice limit.</summary>
    private const long MaxAudioBytes = 25 * 1024 * 1024;

    private static readonly HashSet<string> AllowedAudio = new(StringComparer.OrdinalIgnoreCase)
    { ".webm", ".mp3", ".mp4", ".m4a", ".wav", ".ogg", ".oga", ".flac" };

    private static readonly HashSet<string> Modules = new(StringComparer.OrdinalIgnoreCase)
    { "wardrobe", "kitchen", "travel", "bookshelf", "plant", "warranty", "medicine", "study",
      "bills", "expenses", "cosmos", "meetings" };

    public ModuleVoiceController(
        PromptDbContext db,
        AIServiceManager ai,
        AzureVisionService text,
        ILogger<ModuleVoiceController> logger)
    {
        _db = db;
        _ai = ai;
        _text = text;
        _logger = logger;
    }

    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    public sealed class TextAsk
    {
        public string Question { get; set; } = "";
    }

    /// <summary>
    /// Speak a question. Transcribes, then answers from your own data.
    /// </summary>
    [HttpPost("{module}")]
    [EnableRateLimiting("ai-generation")]
    [RequestSizeLimit(MaxAudioBytes)]
    public async Task<IActionResult> Ask(string module, IFormFile audio, CancellationToken ct = default)
    {
        if (!Modules.Contains(module))
            return BadRequest(new { message = "Unknown module." });

        if (audio is null || audio.Length == 0)
            return BadRequest(new { message = "No audio was received." });

        if (audio.Length > MaxAudioBytes)
            return BadRequest(new { message = "That recording is too long." });

        var ext = Path.GetExtension(audio.FileName);
        if (!string.IsNullOrEmpty(ext) && !AllowedAudio.Contains(ext))
            return BadRequest(new { message = $"Unsupported audio format '{ext}'." });

        string question;
        try
        {
            using var stream = audio.OpenReadStream();
            // Whisper infers the codec from the extension, so supply a sane one
            // when MediaRecorder omits the filename.
            var name = string.IsNullOrWhiteSpace(audio.FileName) ? "ask.webm" : audio.FileName;
            question = await _ai.TranscribeAudioAsync(stream, name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Module voice transcription failed for {Module}", module);
            return StatusCode(502, new { message = "Could not transcribe that recording." });
        }

        if (string.IsNullOrWhiteSpace(question))
            return Ok(new { question = "", answer = "", message = "We couldn't hear anything in that recording." });

        return await Answer(module, question.Trim(), ct);
    }

    /// <summary>
    /// The same conversation, typed. Voice is the headline, but a text box is
    /// how people use this on a train — and it costs no transcription call.
    /// </summary>
    [HttpPost("{module}/text")]
    [EnableRateLimiting("ai-generation")]
    public async Task<IActionResult> AskText(string module, [FromBody] TextAsk req, CancellationToken ct = default)
    {
        if (!Modules.Contains(module))
            return BadRequest(new { message = "Unknown module." });

        var q = (req?.Question ?? "").Trim();
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { message = "Ask a question." });

        // Bound the input: this becomes part of a prompt we pay for.
        if (q.Length > 1000) q = q[..1000];

        return await Answer(module, q, ct);
    }

    private async Task<IActionResult> Answer(string module, string question, CancellationToken ct)
    {
        if (!_text.IsAvailable)
            return StatusCode(503, new { message = "The assistant is not available right now." });

        var (context, systemPrompt, isFood, disclaimer) = module.ToLowerInvariant() switch
        {
            "wardrobe" => (await WardrobeContext(), WardrobeSystem, false,
                "Suggestions based on the photos and details you saved — check the actual garment before you rely on it."),
            "kitchen" => (await KitchenContext(), KitchenSystem, true,
                "General cooking suggestions, not dietary or medical advice. Always check packaging for allergens yourself."),

            "bookshelf" => (await BookshelfContext(), BookshelfSystem, false,
                "Recommendations based on the books you've saved — availability and editions vary."),

            // Plants may be eaten by a child or a pet, or handled bare-handed.
            // Screened for the same reason food is.
            "plant" => (await PlantContext(), PlantSystem, true,
                "Plant care guidance from photos and notes, not an expert identification. Never eat a plant or apply it to skin based on this — assume unknown plants are harmful to children and pets."),

            "warranty" => (await WarrantyContext(), WarrantySystem, false,
                "Based on what we read from your receipts — check the actual warranty terms and dates before relying on this."),

            // Medicine is the highest-risk domain in the app.
            "medicine" => (await MedicineContext(), MedicineSystem, true,
                "General information only — not medical advice. Never start, stop, or change a medicine based on this. Check with a pharmacist or doctor."),

            "study" => (await StudyContext(), StudySystem, false,
                "Study help based on the material you've saved — always check against your source material."),

            "bills" => (await BillsContext(), BillsSystem, false,
                "Based on the bills you've saved — check the actual statement before paying or cancelling anything."),

            "expenses" => (await ExpensesContext(), ExpensesSystem, false,
                "Totals from receipts we read automatically — verify against your bank before relying on the numbers."),

            "cosmos" => (await CosmosContext(), CosmosSystem, false,
                "Answers drawn from notes you saved — check the original source before acting on anything important."),

            "meetings" => (await MeetingsContext(), MeetingsSystem, false,
                "Based on recorded meetings — transcripts mishear names and numbers, so confirm anything important."),

            _ => (await TravelContext(), TravelSystem, false,
                "Travel details change constantly — confirm opening hours, prices, visas and entry rules with official sources."),
        };

        string answer;
        try
        {
            answer = await _text.CompleteTextAsync(
                systemPrompt,
                $"WHAT WE KNOW ABOUT THIS USER:\n{context}\n\nTHEIR QUESTION: {question}",
                maxTokens: 700);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Module voice answer failed for {Module}", module);
            return StatusCode(502, new { message = "Could not answer that right now." });
        }

        // Food answers are screened before they are shown. Fails closed: a
        // withheld answer is recoverable, a harmful one is not.
        if (isFood && LifeSafetyService.IsUnsafeOutput(answer))
        {
            _logger.LogWarning("Module voice answer withheld for kitchen: failed safety screen");
            return Ok(new
            {
                question,
                answer = "",
                withheld = true,
                disclaimer,
                message = "We held that answer back. Please check with a professional if this is about allergies or health.",
            });
        }

        return Ok(new { question, answer, withheld = false, disclaimer });
    }

    // ── System prompts ───────────────────────────────────────────────────────
    // Each is explicit that it may only use what the user actually owns. A
    // generic style or recipe answer is worthless — the whole value here is
    // that the assistant knows your wardrobe and your pantry.

    private const string WardrobeSystem =
        @"You are the user's personal stylist and you can see their actual wardrobe.
Answer using ONLY garments in the list provided. Name specific items exactly as they appear.
If they genuinely have nothing suitable, say so plainly and name the ONE item that would
fix it — do not invent items they do not own. Be warm, brief, and concrete.
Never comment on their body, weight, or appearance.";

    private const string KitchenSystem =
        @"You are the user's cook and you can see their actual pantry.
Prefer recipes from what they already have and say clearly what is missing.
ALLERGIES ARE ABSOLUTE: never suggest a dish containing a listed allergen, and never suggest
'just leave it out' for an allergen that is structural to the dish. Respect their diet goal
and spice level. Never give medical, weight-loss, or nutritional-therapy advice.
Be brief and practical — real steps, not an essay.";

    private const string TravelSystem =
        @"You are the user's travel planner and you can see their saved trips and preferences.
Answer for the destinations and dates provided. Be specific and practical.
Say plainly when something depends on the season, or on visa and entry rules you cannot verify.
Never state visa requirements, vaccination rules, or safety advisories as fact — always say
these must be confirmed with the official source. Be brief.";

    private const string BookshelfSystem =
        @"You are the user's well-read friend and you can see the books they own.
Answer using their actual shelf: what to read next, what pairs with what, what they seem to
be avoiding. When recommending something they do NOT own, say so explicitly and keep it to
one or two suggestions. Never invent books or attribute a book to the wrong author.
Be warm and brief.";

    private const string PlantSystem =
        @"You are the user's gardener and you can see the plants they keep.
Answer about THEIR plants using the species, light, health and watering notes provided.
NEVER suggest eating, brewing, juicing, applying to skin, or any medicinal or herbal use of
any plant. NEVER tell them a plant is safe for children or pets — say it must be checked
against a reliable toxicity source. Stick to watering, light, soil, pests, and placement.
Be brief and practical.";

    private const string WarrantySystem =
        @"You are the user's household administrator and you can see their warranties.
Answer about what is still covered, what is about to expire, and the practical steps to
claim — keep the receipt, contact the retailer, check the serial number.
Never state a legal right or statutory guarantee as fact: consumer law varies by country,
so say it depends on where they bought it and point them to the retailer's terms.
Be brief and concrete.";

    private const string MedicineSystem =
        @"You can see what is in the user's medicine cabinet. You are NOT a clinician.
NEVER suggest a dose, a schedule, starting or stopping a medicine, or combining medicines.
NEVER interpret symptoms, diagnose, or estimate whether something is serious.
NEVER say a medicine is safe, or safe in pregnancy, or safe with alcohol.
You MAY answer only about: what is expired or expiring, safe storage, safe disposal,
duplicate ingredients they should ASK A PHARMACIST about, and what to take to an appointment.
For anything else, say plainly that it needs a pharmacist or doctor. Be brief.";

    private const string StudySystem =
        @"You are the user's study partner and you can see the material they have saved.
Answer from their own notes and documents: explain a concept, quiz them, summarise, or
suggest what to review next. When something is not in their material, say so rather than
filling the gap from memory — they are revising from these notes and a confident wrong
answer costs them marks. Be brief and clear.";

    private const string BillsSystem =
        @"You are the user's household administrator and you can see their bills.
Answer about what is due, what is overdue, what recurs, and where the money goes.
You may point out duplicate or overlapping subscriptions and unusually large jumps.
Never give financial, tax, investment, or debt advice, and never tell them to stop paying
something — say it is worth reviewing and let them decide. Be brief and concrete.";

    private const string ExpensesSystem =
        @"You are the user's bookkeeper and you can see the receipts they scanned.
Answer about totals, categories, merchants, and trends across the receipts provided.
Numbers were read from photos and may be wrong — say so when a total looks odd rather
than presenting it as fact. Never give tax, accounting, or financial advice; say it needs
an accountant. Be brief and specific.";

    private const string CosmosSystem =
        @"You are the user's second brain and you can see the notes they have saved.
Answer from their own notes: find what they wrote, connect related pieces, remind them what
they concluded. When something is not in their notes, say so plainly rather than answering
from general knowledge — the whole value here is that this is THEIR material.
Quote or cite the note title you are drawing from. Be brief.";

    private const string MeetingsSystem =
        @"You can see the user's recorded meetings and their transcripts.
Answer about what was discussed, decided, and committed to, and by whom.
Transcripts mishear names, numbers and technical terms — when a detail matters, say it
should be confirmed rather than stating it flatly. Never invent an attendee, a decision,
or an action item that is not in the transcript. Be brief and attribute clearly.";

    // ── Context loaders ──────────────────────────────────────────────────────
    // Deliberately compact. These become prompt tokens on every question, so
    // each loader sends the smallest set of facts that still makes the answer
    // personal.

    private async Task<string> WardrobeContext()
    {
        var sb = new StringBuilder();

        // The Wardrobe module creates its own tables on first use. If the user
        // has never opened it the tables are absent, which is not an error —
        // it is simply an empty wardrobe.
        List<WardrobeRow> items;
        try
        {
            items = await _db.Database.SqlQuery<WardrobeRow>($@"
            SELECT ""Name"", ""Category"", ""Colors"", ""Style"", ""Seasons"", ""Formality"", ""Condition""
            FROM ""WardrobeItems""
            WHERE ""UserId""={UserId} AND (""Status"" IS NULL OR ""Status"" <> 'discarded')
                ORDER BY ""Category"" LIMIT 150").ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Wardrobe table not present yet");
            return "Their wardrobe is empty — they have not added any garments yet.";
        }

        if (items.Count == 0) return "Their wardrobe is empty — they have not added any garments yet.";

        sb.AppendLine($"They own {items.Count} garments:");
        foreach (var g in items)
        {
            var bits = new[] { g.Colors, g.Style, g.Formality, g.Seasons }
                .Where(x => !string.IsNullOrWhiteSpace(x));
            sb.AppendLine($"- {g.Name} ({g.Category}) — {string.Join(", ", bits)}");
        }

        try
        {
            var profile = (await _db.Database.SqlQuery<string?>($@"
                SELECT ""BodyAdvice"" FROM ""WardrobeProfiles"" WHERE ""UserId""={UserId}")
                .ToListAsync()).FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(profile))
                sb.AppendLine($"\nStyle notes they saved: {Truncate(profile, 500)}");
        }
        catch (Exception ex) { _logger.LogInformation(ex, "Wardrobe profile unavailable"); }

        return sb.ToString();
    }

    private async Task<string> KitchenContext()
    {
        var sb = new StringBuilder();

        // Kitchen creates its tables on first use; absent tables mean the user
        // has never opened the module, which is an empty kitchen, not an error.
        try
        {

            var prefs = (await _db.Database.SqlQuery<KitchenPrefRow>($@"
                SELECT ""Cuisines"", ""SpiceLevel"", ""DietGoal"", ""Allergies""
                FROM ""KitchenPrefs"" WHERE ""UserId""={UserId}").ToListAsync()).FirstOrDefault();

            // Allergies go FIRST and are repeated as a hard rule. Buried in a list
            // of preferences they read as one more nice-to-have.
            if (!string.IsNullOrWhiteSpace(prefs?.Allergies))
                sb.AppendLine($"ALLERGIES — MUST NEVER BE SUGGESTED: {prefs!.Allergies}");
            else
                sb.AppendLine("No allergies recorded (do not assume none — say so if it matters).");

            if (prefs != null)
            {
                if (!string.IsNullOrWhiteSpace(prefs.DietGoal)) sb.AppendLine($"Diet goal: {prefs.DietGoal}");
                if (!string.IsNullOrWhiteSpace(prefs.SpiceLevel)) sb.AppendLine($"Spice tolerance: {prefs.SpiceLevel}");
                if (!string.IsNullOrWhiteSpace(prefs.Cuisines)) sb.AppendLine($"Cuisines they like: {prefs.Cuisines}");
            }

            var pantry = await _db.Database.SqlQuery<PantryRow>($@"
                SELECT ""Name"", ""Category"" FROM ""PantryItems""
                WHERE ""UserId""={UserId} ORDER BY ""Category"" LIMIT 200").ToListAsync();

            sb.AppendLine(pantry.Count == 0
                ? "\nTheir pantry is empty."
                : $"\nIn their pantry ({pantry.Count} items): " +
                  string.Join(", ", pantry.Select(p => p.Name)));

            var shopping = await _db.Database.SqlQuery<string?>($@"
                SELECT ""Name"" FROM ""ShoppingList""
                WHERE ""UserId""={UserId} AND ""Checked""=0 LIMIT 50").ToListAsync();

            if (shopping.Count > 0)
                sb.AppendLine($"Already on their shopping list: {string.Join(", ", shopping)}");
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "Kitchen tables not present yet");
            if (sb.Length == 0) return "Their kitchen is empty — no pantry items or preferences saved yet.";
        }

        return sb.ToString();
    }

    private async Task<string> TravelContext()
    {
        var sb = new StringBuilder();

        // UserTravelPrefs is a raw table, not an EF entity, and holds a single
        // free-text dietary field — which matters for a travel assistant
        // recommending restaurants.
        try
        {
            var diet = (await _db.Database.SqlQuery<string?>($@"
                SELECT ""DietaryPreferences"" FROM ""UserTravelPrefs"" WHERE ""UserId""={UserId}")
                .ToListAsync()).FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(diet))
                sb.AppendLine($"Dietary preferences when travelling: {Truncate(diet, 400)}");
        }
        catch (Exception ex) { _logger.LogInformation(ex, "Travel prefs unavailable"); }

        try
        {
            var guides = await _db.Database.SqlQuery<GuideRow>($@"
                SELECT ""Destination"", ""StartDate"", ""EndDate""
                FROM ""TravelGuides"" WHERE ""UserId""={UserId}
                ORDER BY ""Id"" DESC LIMIT 10").ToListAsync();

            if (guides.Count > 0)
            {
                sb.AppendLine("\nTrips they have planned:");
                foreach (var g in guides)
                    sb.AppendLine($"- {g.Destination} ({g.StartDate} to {g.EndDate})");
            }
        }
        catch (Exception ex)
        {
            // No guides table yet just means no trips planned.
            _logger.LogInformation(ex, "Travel guides unavailable for context");
        }

        return sb.Length == 0
            ? "They have not saved any trips or travel preferences yet."
            : sb.ToString();
    }

    /// <summary>
    /// Small helper: run a context query, and treat a missing table as "this
    /// module has never been opened" rather than an error. Every loader below
    /// needs the same guard, and repeating try/catch eight times invites one of
    /// them to be forgotten.
    /// </summary>
    private async Task<List<T>> SafeQuery<T>(FormattableString sql, string label)
    {
        try
        {
            return await _db.Database.SqlQuery<T>(sql).ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "{Label} unavailable for voice context", label);
            return new List<T>();
        }
    }

    private async Task<string> BookshelfContext()
    {
        // Reads the merged library, not the retired "Books" table: since
        // Bookshelf was folded into the reader, snapped and uploaded books
        // both live in ReaderBooks and the voice answer should see all of it.
        var books = await SafeQuery<BookRow>($@"
            SELECT ""Title"", ""Author"", ""Subject"" AS ""Genre"", ""PublishedYear"" AS ""Year""
            FROM ""ReaderBooks"" WHERE ""UserId""={UserId} AND ""DeletedAt"" IS NULL
            ORDER BY ""Subject"" LIMIT 200", "ReaderBooks");

        if (books.Count == 0) return "Their shelf is empty — they have not added any books yet.";

        var sb = new StringBuilder($"They own {books.Count} books:\n");
        foreach (var b in books)
            sb.AppendLine($"- {b.Title} by {b.Author}{(string.IsNullOrWhiteSpace(b.Genre) ? "" : $" ({b.Genre})")}{(b.Year > 0 ? $", {b.Year}" : "")}");
        return sb.ToString();
    }

    private async Task<string> PlantContext()
    {
        await LifestyleTables.EnsurePlantsAsync(_db, _logger);

        var plants = await SafeQuery<PlantRow>($@"
            SELECT ""Name"", ""Nickname"", ""Species"", ""Health"", ""Diagnosis"", ""Light"", ""WaterEveryDays"", ""LastWatered""
            FROM ""Plants"" WHERE ""UserId""={UserId} LIMIT 100", "Plants");

        if (plants.Count == 0) return "They have not added any plants yet.";

        var today = DateTime.UtcNow.Date;
        var sb = new StringBuilder($"They keep {plants.Count} plants. Today is {today:yyyy-MM-dd}.\n");
        foreach (var p in plants)
        {
            var name = string.IsNullOrWhiteSpace(p.Nickname) ? p.Name : $"{p.Nickname} ({p.Name})";
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(p.Species)) bits.Add(p.Species!);
            if (!string.IsNullOrWhiteSpace(p.Health)) bits.Add($"health: {p.Health}");
            if (!string.IsNullOrWhiteSpace(p.Light)) bits.Add($"light: {p.Light}");

            // Watering is the question people actually ask, so compute days
            // since last watered rather than making the model parse dates.
            if (DateTime.TryParse(p.LastWatered, out var lw))
            {
                var since = (today - lw.Date).Days;
                bits.Add(p.WaterEveryDays > 0
                    ? $"watered {since}d ago, every {p.WaterEveryDays}d"
                    : $"watered {since}d ago");
            }
            if (!string.IsNullOrWhiteSpace(p.Diagnosis)) bits.Add($"noted: {Truncate(p.Diagnosis!, 120)}");
            sb.AppendLine($"- {name} — {string.Join(", ", bits)}");
        }
        return sb.ToString();
    }

    private async Task<string> WarrantyContext()
    {
        await LifestyleTables.EnsureWarrantiesAsync(_db, _logger);

        var items = await SafeQuery<WarrantyCtxRow>($@"
            SELECT ""Product"", ""Brand"", ""Retailer"", ""PurchaseDate"", ""ExpiryDate""
            FROM ""Warranties"" WHERE ""UserId""={UserId} LIMIT 150", "Warranties");

        if (items.Count == 0) return "They have not saved any warranties yet.";

        var today = DateTime.UtcNow.Date;
        var sb = new StringBuilder($"They track {items.Count} warranties. Today is {today:yyyy-MM-dd}.\n");
        foreach (var w in items)
        {
            var status = DateTime.TryParse(w.ExpiryDate, out var e)
                ? ((e.Date - today).Days is var d && d < 0 ? $"EXPIRED {-d} days ago" : $"expires in {d} days ({w.ExpiryDate})")
                : "expiry unknown";
            sb.AppendLine($"- {w.Product} ({w.Brand}) from {w.Retailer}, bought {w.PurchaseDate} — {status}");
        }
        return sb.ToString();
    }

    private async Task<string> MedicineContext()
    {
        await LifestyleTables.EnsureMedicinesAsync(_db, _logger);

        var meds = await SafeQuery<MedicineCtxRow>($@"
            SELECT ""Name"", ""Form"", ""Strength"", ""Purpose"", ""ExpiryDate""
            FROM ""Medicines"" WHERE ""UserId""={UserId} LIMIT 150", "Medicines");

        if (meds.Count == 0) return "Their medicine cabinet is empty.";

        var today = DateTime.UtcNow.Date;
        var sb = new StringBuilder($"Their medicine cabinet holds {meds.Count} items. Today is {today:yyyy-MM-dd}.\n");
        foreach (var m in meds)
        {
            var status = DateTime.TryParse(m.ExpiryDate, out var e)
                ? ((e.Date - today).Days is var d && d < 0 ? $"EXPIRED {-d} days ago" : $"expires in {d} days")
                : "expiry unknown";
            sb.AppendLine($"- {m.Name} {m.Strength} ({m.Form}), kept for {m.Purpose} — {status}");
        }
        return sb.ToString();
    }

    private async Task<string> StudyContext()
    {
        var docs = await SafeQuery<StudyRow>($@"
            SELECT ""Topic"", ""Title"", ""CreatedAt""
            FROM ""StudyDocs"" WHERE ""UserId""={UserId} ORDER BY ""Id"" DESC LIMIT 60", "StudyDocs");

        if (docs.Count == 0) return "They have not saved any study material yet.";

        var sb = new StringBuilder($"They have {docs.Count} saved study sets:\n");
        foreach (var d in docs)
            sb.AppendLine($"- {d.Title}{(string.IsNullOrWhiteSpace(d.Topic) ? "" : $" — topic: {d.Topic}")}");
        return sb.ToString();
    }

    private async Task<string> BillsContext()
    {
        await LifestyleTables.EnsureBillsAsync(_db, _logger);

        var bills = await SafeQuery<BillCtxRow>($@"
            SELECT ""Biller"", ""Category"", ""Amount"", ""Currency"", ""DueDate"", ""Recurring""
            FROM ""Bills"" WHERE ""UserId""={UserId} LIMIT 200", "Bills");

        if (bills.Count == 0) return "They have not saved any bills yet.";

        var today = DateTime.UtcNow.Date;
        var sb = new StringBuilder($"They track {bills.Count} bills. Today is {today:yyyy-MM-dd}.\n");

        // A recurring monthly total is the single most useful derived number
        // here, and the model should not be left to add it up itself.
        var monthly = bills
            .Where(b => (b.Recurring ?? "").Contains("month", StringComparison.OrdinalIgnoreCase))
            .Sum(b => b.Amount ?? 0);
        if (monthly > 0) sb.AppendLine($"Recurring monthly total: about {monthly:0.00}");

        foreach (var b in bills)
        {
            var due = DateTime.TryParse(b.DueDate, out var d)
                ? ((d.Date - today).Days is var n && n < 0 ? $"OVERDUE {-n}d" : $"due in {n}d")
                : "no due date";
            sb.AppendLine($"- {b.Biller} ({b.Category}) {b.Currency}{b.Amount:0.00} {b.Recurring} — {due}");
        }
        return sb.ToString();
    }

    private async Task<string> ExpensesContext()
    {
        // Receipts are created by DocAgent; the table may not exist yet.
        var receipts = await SafeQuery<ReceiptCtxRow>($@"
            SELECT ""Merchant"", ""PurchaseDate"", ""Total"", ""Currency"", ""Category""
            FROM ""Receipts"" WHERE ""UserId""={UserId}
            ORDER BY ""PurchaseDate"" DESC LIMIT 150", "Receipts");

        if (receipts.Count == 0) return "They have not scanned any receipts yet.";

        var sb = new StringBuilder($"They have {receipts.Count} scanned receipts.\n");

        var byCategory = receipts
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Category) ? "other" : r.Category!)
            .Select(g => new { Cat = g.Key, Total = g.Sum(x => x.Total ?? 0), Count = g.Count() })
            .OrderByDescending(x => x.Total);

        sb.AppendLine("Spend by category:");
        foreach (var c in byCategory)
            sb.AppendLine($"- {c.Cat}: {c.Total:0.00} across {c.Count}");

        sb.AppendLine("\nMost recent:");
        foreach (var r in receipts.Take(25))
            sb.AppendLine($"- {r.PurchaseDate} {r.Merchant} {r.Currency}{r.Total:0.00} ({r.Category})");

        return sb.ToString();
    }

    private async Task<string> CosmosContext()
    {
        // KnowledgeNodes is a real EF entity, so this one can use LINQ.
        var notes = await _db.KnowledgeNodes.AsNoTracking()
            .Where(n => n.UserId == UserId)
            .OrderByDescending(n => n.UpdatedAt)
            .Take(60)
            .Select(n => new { n.Title, n.Kind, n.SourceModule, n.Content })
            .ToListAsync();

        if (notes.Count == 0) return "They have not saved any notes yet.";

        var sb = new StringBuilder($"They have {notes.Count} saved notes:\n");
        foreach (var n in notes)
        {
            // A snippet, not the whole note: 60 full notes would blow the
            // context window and cost a fortune per question.
            var snippet = string.IsNullOrWhiteSpace(n.Content)
                ? ""
                : " — " + Truncate(n.Content.Replace("\n", " "), 180);
            sb.AppendLine($"- [{n.Kind}] {n.Title}{(string.IsNullOrWhiteSpace(n.SourceModule) ? "" : $" (from {n.SourceModule})")}{snippet}");
        }
        return sb.ToString();
    }

    private async Task<string> MeetingsContext()
    {
        var meetings = await _db.MeetingSessions.AsNoTracking()
            .Where(m => m.UserId == UserId && m.Transcript != null && m.Transcript != "")
            .OrderByDescending(m => m.StartedAt)
            .Take(10)
            .Select(m => new { m.Title, m.Platform, m.StartedAt, m.Transcript })
            .ToListAsync();

        if (meetings.Count == 0) return "They have no recorded meetings with transcripts yet.";

        var sb = new StringBuilder($"Their {meetings.Count} most recent recorded meetings:\n");
        foreach (var m in meetings)
        {
            sb.AppendLine($"\n## {m.Title} ({m.Platform}, {m.StartedAt:yyyy-MM-dd})");
            // Transcripts run to tens of thousands of characters. Cap hard, and
            // take the opening where the agenda and attendees usually are.
            sb.AppendLine(Truncate(m.Transcript ?? "", 2500));
        }
        return sb.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private sealed class WardrobeRow
    {
        public string? Name { get; set; }
        public string? Category { get; set; }
        public string? Colors { get; set; }
        public string? Style { get; set; }
        public string? Seasons { get; set; }
        public string? Formality { get; set; }
        public string? Condition { get; set; }
    }

    private sealed class KitchenPrefRow
    {
        public string? Cuisines { get; set; }
        public string? SpiceLevel { get; set; }
        public string? DietGoal { get; set; }
        public string? Allergies { get; set; }
    }

    private sealed class PantryRow
    {
        public string? Name { get; set; }
        public string? Category { get; set; }
    }

    private sealed class BookRow
    {
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? Genre { get; set; }
        public int Year { get; set; }
    }

    private sealed class PlantRow
    {
        public string? Name { get; set; }
        public string? Nickname { get; set; }
        public string? Species { get; set; }
        public string? Health { get; set; }
        public string? Diagnosis { get; set; }
        public string? Light { get; set; }
        public int WaterEveryDays { get; set; }
        public string? LastWatered { get; set; }
    }

    private sealed class WarrantyCtxRow
    {
        public string? Product { get; set; }
        public string? Brand { get; set; }
        public string? Retailer { get; set; }
        public string? PurchaseDate { get; set; }
        public string? ExpiryDate { get; set; }
    }

    private sealed class MedicineCtxRow
    {
        public string? Name { get; set; }
        public string? Form { get; set; }
        public string? Strength { get; set; }
        public string? Purpose { get; set; }
        public string? ExpiryDate { get; set; }
    }

    private sealed class StudyRow
    {
        public string? Topic { get; set; }
        public string? Title { get; set; }
        public string? CreatedAt { get; set; }
    }

    private sealed class BillCtxRow
    {
        public string? Biller { get; set; }
        public string? Category { get; set; }
        public double? Amount { get; set; }
        public string? Currency { get; set; }
        public string? DueDate { get; set; }
        public string? Recurring { get; set; }
    }

    private sealed class ReceiptCtxRow
    {
        public string? Merchant { get; set; }
        public string? PurchaseDate { get; set; }
        public double? Total { get; set; }
        public string? Currency { get; set; }
        public string? Category { get; set; }
    }

    private sealed class GuideRow
    {
        public string? Destination { get; set; }
        public string? StartDate { get; set; }
        public string? EndDate { get; set; }
    }
}
