using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookReader.Api.Models;

namespace BookReader.Api.Services;

/// <summary>
/// Turns a book's raw text into a cast of characters and an ordered script.
///
/// Ported from the standalone Bookreader app, with one substantive fix: the
/// original analysed only the first 8,000 characters of a book
/// (`text[..Math.Min(text.Length, 8000)]`), which on a novel is roughly chapter
/// one — every character introduced later was invisible and their lines fell
/// to the narrator. This version samples across the whole book to build the
/// cast, then attributes dialogue chunk by chunk.
///
/// Cost is the reason the original cut the corner, so the work here is bounded:
/// a fixed number of cast samples and a cap on dialogue chunks, both tunable.
/// Anything past the cap still reads — it just reads as narration.
/// </summary>
public class BookAnalysisService
{
    private readonly IAIService _ai;
    private readonly ILogger<BookAnalysisService> _logger;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Characters per chunk sent to the model. Comfortably inside context limits.</summary>
    private const int ChunkChars = 12000;

    /// <summary>Evenly-spaced samples used to discover the cast.</summary>
    private const int CastSamples = 5;

    /// <summary>Upper bound on dialogue-attribution calls per book.</summary>
    private const int MaxDialogueChunks = 12;

    /// <summary>Text per structuring call. Smaller than the cast chunk so the
    /// model can return every block's cleaned text without truncation.</summary>
    private const int StructureBatchChars = 24000;

    /// <summary>Caps the cost of structuring a very long book.</summary>
    private const int MaxStructureBatches = 40;

    /// <summary>Parallel structuring calls. Enough to keep a long book quick
    /// without opening a connection per batch.</summary>
    private const int StructureConcurrency = 6;

    public BookAnalysisService(IAIService ai, ILogger<BookAnalysisService> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    public record AnalysisResult(List<BookCharacter> Characters, List<BookLine> Lines);

    /// <param name="genre">
    /// Decided upstream by BookExtractionService. Only fiction gets a cast:
    /// asking a model to find characters in a cookbook or a technical manual
    /// invents "speakers" that make the reader look broken, and costs a dozen
    /// calls to produce.
    /// </param>
    public async Task<AnalysisResult> AnalyseAsync(Guid bookId, string text, string genre, CancellationToken ct = default)
    {
        if (genre != BookGenres.Fiction)
        {
            _logger.LogInformation("Book {BookId} is {Genre}; narrating without a cast", bookId, genre);
            var narrated = await StructureAsync(text, ct);

            // One real character row: the narrator. Non-fiction used to get an
            // empty cast, which left the panel with nothing to show and no way
            // to change the reading voice — the voice picker hangs off a
            // character row. One row, honestly named, fixes both.
            var narrator = new BookCharacter
            {
                BookId = bookId,
                Name = "Narrator",
                Description = "Reads the whole book in one clear voice.",
                Gender = "unknown",
                AssignedVoiceId = BookVoices.Narrator,
                LineCount = narrated.Count,
            };
            return new AnalysisResult(new List<BookCharacter> { narrator }, narrated);
        }

        var characters = await ExtractCastAsync(text, ct);
        var lines = await ExtractScriptAsync(text, characters, ct);

        // Every book has a narrator, including fiction: narration is most of
        // a novel's text, and without a row for it there is no way to see who
        // is reading or to change that voice.
        if (!characters.Any(c => string.Equals(c.Name, "Narrator", StringComparison.OrdinalIgnoreCase)))
        {
            characters.Insert(0, new BookCharacter
            {
                Name = "Narrator",
                Description = "Reads the story itself - everything outside dialogue.",
                Gender = "neutral",
                TraitsJson = "[\"steady\",\"clear\"]",
                LineCount = lines.Count(l => l.IsNarration),
            });
        }

        AssignVoices(characters);

        // Cast ordering is by how much each character actually speaks, so the
        // UI shows the leads first rather than whoever the model listed first.
        foreach (var c in characters)
            c.LineCount = lines.Count(l => string.Equals(l.Speaker, c.Name, StringComparison.OrdinalIgnoreCase));

        foreach (var c in characters) c.BookId = bookId;

        return new AnalysisResult(
            characters.OrderByDescending(c => c.LineCount).ToList(),
            lines);
    }

    // ── Cast ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Turns raw extracted text into the blocks a reader should actually show,
    /// by asking the model what each one IS.
    ///
    /// This replaces pattern-matching for front matter, contents pages, running
    /// headers and DRM watermarks. Those rules never held: "from the library of"
    /// is a watermark in one book and a real sentence in another, a contents
    /// page is only recognisable by what it MEANS, and every new book brought a
    /// new exception. The model reads the page the way a person does and says
    /// what it is looking at.
    ///
    /// Each block comes back tagged, so the reader can drop the apparatus and
    /// render headings as headings instead of running everything together as
    /// one grey wall of prose.
    /// </summary>
    private async Task<List<BookLine>> StructureAsync(string text, CancellationToken ct)
    {
        var paged = BookParserService.SplitIntoLinesWithPages(text);
        var output = new List<BookLine>();
        var index = 0;

        // Batched: one call per chunk rather than per block. A 400-page manual
        // has thousands of blocks and a call each would be unaffordable.
        var batches = BatchByChars(paged, StructureBatchChars).Take(MaxStructureBatches).ToList();

        // Classification ONLY. An earlier version asked the model to return each
        // block's cleaned text as well, which made output tokens roughly equal
        // input tokens - about 45,000 tokens spent restating text already in
        // hand, and five minutes of wall clock on a 400-page book. A one-letter
        // verdict per block is ~40x smaller and the judgement is identical.
        var system =
            "You are preparing a scanned book for a reading app. You will get numbered text blocks " +
            "from consecutive pages. For EACH block reply with ONE letter for what it is:\n" +
            "  h = heading (chapter or section title)\n" +
            "  b = body (ordinary readable prose the reader wants)\n" +
            "  c = caption (a figure or table caption)\n" +
            "  t = contents entry (title with a page number, or dot leaders)\n" +
            "  f = front or back matter - anything that is not the book's actual content: " +
            "copyright, ISBN, credits, editor lists, publisher address, dedication, " +
            "acknowledgments, about the author, about this book, foreword, preface, " +
            "introduction to the book itself, errata, how to use this book, " +
            "index, glossary, colophon\n" +
            "  x = furniture (running header/footer, a bare page number, print code, " +
            "library or DRM watermark, 'this page intentionally left blank')\n" +
            "The reader wants the BOOK, so a chapter opening is the first thing they should see. " +
            "Prose that is about the book or its production - who helped write it, who the author " +
            "is, how the exam is structured, how to use the book - is front matter (f), even when " +
            "it reads like ordinary paragraphs. Prose that teaches the subject is body (b).\n" +
            "Set \"d\":true when a block needs repair: words hyphen-split across a line " +
            "break, or a page number, running header, print code or library/DRM watermark " +
            "sitting INSIDE the sentence rather than on its own line.";

        // Batches are independent, so they run concurrently. Serially this was
        // 30 round trips one after another - five minutes of a user watching
        // "Preparing it for reading...". Bounded so a long book cannot open
        // 100 simultaneous connections to the AI provider.
        var gate = new SemaphoreSlim(StructureConcurrency);
        var results = new string?[batches.Count];

        await Task.WhenAll(batches.Select(async (batch, bi) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                results[bi] = await ClassifyBatchAsync(batch, system, ct);
            }
            finally
            {
                gate.Release();
            }
        }));

        var applied = 0;
        for (var bi = 0; bi < batches.Count; bi++)
        {
            var batch = batches[bi];
            var raw = results[bi];

            if (raw is not null && TryApplyStructure(raw, batch, output, ref index))
            {
                applied++;
                continue;
            }

            // Falling back silently is how a book ends up half-structured with
            // no explanation: log which batch failed and what came back.
            _logger.LogWarning(
                "Structuring batch {Batch}/{Total} fell back to raw text. Response: {Raw}",
                bi + 1, batches.Count,
                raw is null ? "(call failed)" : (raw.Length > 300 ? raw[..300] : raw));

            AppendRaw(output, batch, ref index);
        }

        _logger.LogInformation(
            "Structured {Applied}/{Total} batches into {Lines} lines", applied, batches.Count, output.Count);

        output = TidyHeadings(output);

        // Anything past the batch cap is long-tail appendix material; keep it
        // readable rather than dropping it.
        foreach (var b in paged.Skip(batches.Sum(x => x.Count)))
        {
            output.Add(new BookLine
            {
                LineIndex = index++,
                Speaker = "Narrator",
                Text = b.Text,
                IsNarration = true,
                Page = b.Page,
            });
        }

        return output;
    }

    /// <summary>
    /// Repairs two things structuring cannot fix on its own.
    ///
    /// SPLIT HEADINGS: print layout breaks a chapter title across lines, so
    /// "CHAPTER 1" / "Manage Azure identities and" / "governance" arrive as
    /// three separate heading blocks. They are one title and are joined.
    ///
    /// ORPHANED HEADINGS: a heading whose content was dropped as apparatus -
    /// "Contents at a glance" with its entries removed - is left as a label
    /// pointing at nothing, so it goes too.
    ///
    /// Done after classification rather than in the prompt: the model sees one
    /// batch at a time and judges each block on its own, which is the right
    /// question to ask it. Whether two adjacent blocks are one heading is a
    /// question about the sequence, and the sequence only exists here.
    /// </summary>
    private static List<BookLine> TidyHeadings(List<BookLine> lines)
    {
        // ── Join headings split across print lines ───────────────────────────
        var joined = new List<BookLine>(lines.Count);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (!IsHeading(line))
            {
                joined.Add(line);
                continue;
            }

            var text = line.Text.Trim();

            // Absorb following heading blocks on the same page that continue
            // this title. A title that already ends in sentence punctuation is
            // complete, and a long one is a real heading rather than a fragment.
            while (i + 1 < lines.Count
                   && IsHeading(lines[i + 1])
                   && lines[i + 1].Page == line.Page
                   && text.Length < 70
                   && !EndsSentence(text))
            {
                text = text + " " + lines[i + 1].Text.Trim();
                i++;
            }

            line.Text = text;
            joined.Add(line);
        }

        // ── Drop headings with nothing under them ────────────────────────────
        var kept = new List<BookLine>(joined.Count);

        for (var i = 0; i < joined.Count; i++)
        {
            if (IsHeading(joined[i]))
            {
                // Look ahead for content this heading introduces. Another
                // heading straight after means its own body was removed.
                var hasContent = i + 1 < joined.Count && !IsHeading(joined[i + 1]);
                if (!hasContent) continue;
            }

            kept.Add(joined[i]);
        }

        // Line indexes must stay contiguous: playback, progress and the reader's
        // jump-to-line all address a book by position.
        for (var i = 0; i < kept.Count; i++) kept[i].LineIndex = i;

        return kept;
    }

    private static bool IsHeading(BookLine l) =>
        string.Equals(l.Kind, "heading", StringComparison.OrdinalIgnoreCase);

    private static bool EndsSentence(string text) =>
        text.Length > 0 && ".?!:".Contains(text[^1]);

    /// <summary>One classification round trip. Null when the call failed.</summary>
    private async Task<string?> ClassifyBatchAsync(
        List<BookParserService.PagedLine> batch, string system, CancellationToken ct)
    {
        {
            var numbered = string.Join("\n", batch.Select((b, i) => $"[{i}] {b.Text}"));

            var prompt =
                numbered + "\n\n" +
                "Return ONLY JSON, one entry per block, same order, same count:\n" +
                "{\"b\":[{\"i\":0,\"k\":\"b\"},{\"i\":1,\"k\":\"x\"},{\"i\":2,\"k\":\"b\",\"d\":true}]}";

            try
            {
                var res = await _ai.TestPromptAsync(new PromptTestRequest
                {
                    PromptContent = system,
                    UserInput = prompt,
                });
                return res.Result;
            }
            catch (Exception ex)
            {
                // A failed batch must not lose its pages; the caller keeps them.
                _logger.LogWarning(ex, "Structuring batch failed; keeping its text as-is");
                return null;
            }
        }
    }

    /// <summary>
    /// Applies one structuring response, dropping the blocks the model
    /// identified as apparatus rather than content.
    /// </summary>
    private static bool TryApplyStructure(
        string raw, List<BookParserService.PagedLine> batch, List<BookLine> output, ref int index)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // "b" is the compact shape; "blocks" is accepted so a model that
            // ignores the abbreviation still parses.
            if (!root.TryGetProperty("b", out var arr) &&
                !root.TryGetProperty("blocks", out arr)) return false;
            if (arr.ValueKind != JsonValueKind.Array) return false;

            var any = false;
            foreach (var el in arr.EnumerateArray())
            {
                if (!el.TryGetProperty("i", out var iEl) || !iEl.TryGetInt32(out var i)) continue;
                if (i < 0 || i >= batch.Count) continue;

                var code = el.TryGetProperty("k", out var k) ? (k.GetString() ?? "b")
                         : el.TryGetProperty("kind", out var k2) ? (k2.GetString() ?? "b")
                         : "b";

                any = true;

                // Apparatus is neither shown nor read aloud: the copyright page,
                // the contents, the watermark printed on every page.
                var kind = KindFromCode(code);
                if (kind is null) continue;

                // The text is what we extracted. Only blocks the model flagged
                // as damaged ("d") are repaired, and that repair is local -
                // no round trip, because asking for every block back is what
                // made this slow in the first place.
                // Furniture removal runs unconditionally: whether a DRM serial
                // is present is a fact, not a judgement, and relying on the
                // model to spot it in every block missed 37 of 224.
                var text = StripFusedFurniture(batch[i].Text);

                // Hyphen repair stays behind the model's flag - joining
                // "well- known" wrongly is worse than leaving it.
                if (el.TryGetProperty("d", out var dmg) && dmg.ValueKind == JsonValueKind.True)
                    text = RepairText(text);

                output.Add(new BookLine
                {
                    LineIndex = index++,
                    Speaker = "Narrator",
                    Text = text.Trim(),
                    IsNarration = true,
                    Page = batch[i].Page,
                    Kind = kind,
                });
            }

            return any;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Maps the model's one-letter verdict to a display kind, or null when the
    /// block is apparatus and should not reach the reader.
    /// </summary>
    private static string? KindFromCode(string code) => code.Trim().ToLowerInvariant() switch
    {
        "h" or "heading" => "heading",
        "c" or "caption" => "caption",
        "b" or "body" => "body",
        _ => null,     // t, f, x - contents, front matter, furniture
    };

    /// <summary>
    /// Repairs a block the model flagged as damaged: rejoins words the print
    /// layout hyphen-split across a line break and collapses spacing.
    ///
    /// Done locally rather than by asking the model to echo the text back. The
    /// model is used for the judgement it is uniquely good at - "is this prose
    /// or a watermark?" - not for work a string operation does instantly.
    /// </summary>
    private static string RepairText(string text)
    {
        // "cur- rently" -> "currently".
        //
        // A print line-break splits one word, so the two halves join. But a
        // genuine compound wrapped at the hyphen ("well- known") must keep
        // its hyphen - the earlier rule turned it into "wellknown". The
        // difference is that a compound's first half is itself a word, so a
        // short list of the prefixes that actually occur is enough.
        text = HyphenBreak.Replace(text, m =>
        {
            var left = m.Groups[1].Value;
            var right = m.Groups[2].Value;
            return CompoundPrefixes.Contains(left)
                ? $"{left}-{right}"
                : left + right;
        });

        return StripFusedFurniture(text);
    }

    /// <summary>
    /// Removes page furniture printed INSIDE a text column, which lands
    /// mid-sentence when the page is flattened.
    ///
    /// The block really is body text; it just has a watermark wedged into it,
    /// sometimes inside a word ("com" + serial + "pliance"). Left in, it is
    /// displayed and read aloud.
    /// </summary>
    private static string StripFusedFurniture(string text)
    {
        // Each removal leaves a marker so the next step can tell "a word was
        // cut in half here" from "the serial stood on its own". Collapsing
        // spaces blindly instead would turn "a great way to build" into
        // "agreatwaytobuild".
        foreach (var pattern in FusedFurniture)
            text = pattern.Replace(text, FurnitureMark);

        // A serial printed mid-word: "com<serial> pliance" -> "compliance".
        text = Regex.Replace(text, $@"(\p{{Ll}}){FurnitureMark}\s?(\p{{Ll}})", "$1$2");

        // Anything left stood alone, so it becomes a space.
        text = text.Replace(FurnitureMark, " ");

        return Regex.Replace(text, @"\s{2,}", " ").Trim();
    }

    /// <summary>
    /// Private-use character marking where furniture was removed. Not a
    /// character any book contains, so it cannot collide with real text.
    /// </summary>
    private const string FurnitureMark = "\uE000";

    private static readonly Regex HyphenBreak =
        new(@"\b(\p{Ll}+)-\s+(\p{Ll}+)", RegexOptions.Compiled);

    /// <summary>
    /// First halves of real hyphenated compounds. When one of these is
    /// followed by a line break the hyphen belongs to the word and stays.
    /// </summary>
    private static readonly HashSet<string> CompoundPrefixes = new(StringComparer.Ordinal)
    {
        "well", "self", "non", "pre", "post", "co", "re", "multi", "cross",
        "half", "full", "high", "low", "long", "short", "real", "time",
        "open", "close", "end", "user", "in", "on", "off", "sub", "inter",
        "intra", "anti", "auto", "built", "read", "write", "role", "point",
    };

    /// <summary>
    /// Page furniture that appears mid-sentence rather than on its own line.
    ///
    /// Deliberately narrow: each pattern matches a machine-generated string
    /// no author would write - a DRM serial, a typesetter's file stamp, a
    /// library watermark. A looser rule would eat real prose.
    /// </summary>
    private static readonly Regex[] FusedFurniture =
    {
        // DRM serial: "ptg38424893". No word boundaries - the page layout
        // drops it inside words too ("comptg38424893 pliance").
        new(@"ptg\d{6,}", RegexOptions.Compiled),
        // Typesetter stamp: "9780136805380_print.indb 41 30/06/21 4:38 PM"
        new(@"\S*\.ind[bd]\b(\s+\d+)?(\s+\d{1,2}/\d{1,2}/\d{2,4})?(\s+\d{1,2}:\d{2}\s*(AM|PM))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
        // Per-copy watermark: "From the Library of <name>"
        new(@"From the Library of(\s+\p{Lu}\p{L}*){1,4}", RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };

    private static void AppendRaw(
        List<BookLine> output, List<BookParserService.PagedLine> batch, ref int index)
    {
        foreach (var b in batch)
        {
            output.Add(new BookLine
            {
                LineIndex = index++,
                Speaker = "Narrator",
                Text = b.Text,
                IsNarration = true,
                Page = b.Page,
            });
        }
    }

    private static IEnumerable<List<BookParserService.PagedLine>> BatchByChars(
        List<BookParserService.PagedLine> lines, int budget)
    {
        var batch = new List<BookParserService.PagedLine>();
        var used = 0;

        foreach (var l in lines)
        {
            if (used + l.Text.Length > budget && batch.Count > 0)
            {
                yield return batch;
                batch = new List<BookParserService.PagedLine>();
                used = 0;
            }
            batch.Add(l);
            used += l.Text.Length;
        }

        if (batch.Count > 0) yield return batch;
    }

    private async Task<List<BookCharacter>> ExtractCastAsync(string text, CancellationToken ct)
    {
        var samples = SampleAcross(text, CastSamples, ChunkChars);
        var byName = new Dictionary<string, BookCharacter>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in samples)
        {
            var prompt = """
                Extract every named character from this passage.

                Return ONLY JSON:
                {"characters":[{"name":"...","description":"...","gender":"male|female|unknown","traits":["..."]}]}

                Rules:
                - Real characters only. Not places, objects, titles, or chapter headings.
                - Use the name the narration uses most often for that person.
                - If the passage names nobody, return {"characters":[]}.
                """;

            var raw = await AskAsync(prompt, sample, ct);
            foreach (var parsed in ParseCharacters(raw))
            {
                if (string.IsNullOrWhiteSpace(parsed.Name)) continue;
                // Later samples fill in detail the first mention lacked, but
                // never overwrite a description we already have.
                if (byName.TryGetValue(parsed.Name, out var existing))
                {
                    if (string.IsNullOrWhiteSpace(existing.Description)) existing.Description = parsed.Description;
                    if (existing.Gender == "unknown") existing.Gender = parsed.Gender;
                }
                else
                {
                    byName[parsed.Name] = parsed;
                }
            }
        }

        _logger.LogInformation("Book cast: {Count} characters from {Samples} samples", byName.Count, samples.Count);
        return byName.Values.ToList();
    }

    private List<BookCharacter> ParseCharacters(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var el = doc.RootElement.TryGetProperty("characters", out var arr) ? arr : doc.RootElement;
            if (el.ValueKind != JsonValueKind.Array) return new();

            var result = new List<BookCharacter>();
            foreach (var item in el.EnumerateArray())
            {
                var name = Str(item, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                var traits = item.TryGetProperty("traits", out var t) && t.ValueKind == JsonValueKind.Array
                    ? t.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).ToList()
                    : new List<string?>();

                result.Add(new BookCharacter
                {
                    Name = name.Trim(),
                    Description = Str(item, "description"),
                    Gender = (Str(item, "gender") ?? "unknown").ToLowerInvariant() switch
                    {
                        "male" or "m" => "male",
                        "female" or "f" => "female",
                        _ => "unknown",
                    },
                    TraitsJson = traits.Count > 0 ? JsonSerializer.Serialize(traits) : null,
                });
            }
            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Character JSON did not parse; skipping this sample");
            return new();
        }
    }

    // ── Script ────────────────────────────────────────────────────────────────

    private async Task<List<BookLine>> ExtractScriptAsync(
        string text, List<BookCharacter> cast, CancellationToken ct)
    {
        var chunks = Chunk(text, ChunkChars).Take(MaxDialogueChunks).ToList();
        var names = cast.Count > 0 ? string.Join(", ", cast.Select(c => c.Name)) : "Narrator";
        var lines = new List<BookLine>();
        var index = 0;

        foreach (var chunk in chunks)
        {
            var prompt = $$"""
                Split this passage into an ordered script for an audiobook.

                Speakers available: {{names}}, Narrator

                Return ONLY JSON:
                {"lines":[{"speaker":"...","text":"...","isNarration":true}]}

                Rules:
                - Cover the passage in order. Do not skip or summarise text.
                - Spoken dialogue goes to the character speaking it, isNarration false.
                - Everything else — description, action, thoughts — goes to Narrator, isNarration true.
                - Strip quote marks from spoken text; keep the words themselves exact.
                - Use only the speaker names listed above.
                """;

            var raw = await AskAsync(prompt, chunk, ct);
            var parsed = ParseLines(raw);

            // A chunk the model could not attribute still has to be readable,
            // so it falls back to plain narration rather than vanishing.
            if (parsed.Count == 0)
            {
                foreach (var line in BookParserService.SplitIntoLines(chunk))
                    parsed.Add(new BookLine { Speaker = "Narrator", Text = line, IsNarration = true });
            }

            foreach (var line in parsed)
            {
                line.LineIndex = index++;
                lines.Add(line);
            }
        }

        // Any tail beyond the analysis cap is still delivered, as narration.
        var analysed = Math.Min(chunks.Count * ChunkChars, text.Length);
        if (analysed < text.Length)
        {
            _logger.LogInformation("Book exceeds the analysis cap; remaining {Chars} chars read as narration",
                text.Length - analysed);
            foreach (var line in BookParserService.SplitIntoLines(text[analysed..]))
                lines.Add(new BookLine { LineIndex = index++, Speaker = "Narrator", Text = line, IsNarration = true });
        }

        return lines;
    }

    private List<BookLine> ParseLines(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var el = doc.RootElement.TryGetProperty("lines", out var arr) ? arr : doc.RootElement;
            if (el.ValueKind != JsonValueKind.Array) return new();

            var result = new List<BookLine>();
            foreach (var item in el.EnumerateArray())
            {
                var text = Str(item, "text");
                if (string.IsNullOrWhiteSpace(text)) continue;

                var speaker = Str(item, "speaker") ?? "Narrator";
                var isNarration = item.TryGetProperty("isNarration", out var n)
                    ? n.ValueKind == JsonValueKind.True
                    : speaker.Equals("Narrator", StringComparison.OrdinalIgnoreCase);

                result.Add(new BookLine
                {
                    Speaker = speaker.Trim(),
                    Text = text.Trim(),
                    IsNarration = isNarration,
                });
            }
            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Dialogue JSON did not parse; chunk will read as narration");
            return new();
        }
    }

    // ── Voices ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gives every character a distinct voice. Distinctness matters more than
    /// perfect casting: two characters sharing a voice in the same scene is
    /// confusing in a way a slightly-wrong accent is not.
    /// </summary>
    private static void AssignVoices(List<BookCharacter> characters)
    {
        var male = 0;
        var female = 0;
        var neutral = 0;

        foreach (var c in characters)
        {
            c.AssignedVoiceId = c.Gender switch
            {
                "male" => BookVoices.Male[male++ % BookVoices.Male.Length],
                "female" => BookVoices.Female[female++ % BookVoices.Female.Length],
                _ => BookVoices.Neutral[neutral++ % BookVoices.Neutral.Length],
            };
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string> AskAsync(string instruction, string passage, CancellationToken ct)
    {
        var response = await _ai.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = instruction,
            UserInput = passage,
        });
        return response.Result ?? "{}";
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    /// <summary>
    /// Evenly-spaced windows across the whole text. This is the fix for the
    /// original's opening-chapter-only blind spot.
    /// </summary>
    private static List<string> SampleAcross(string text, int count, int size)
    {
        if (text.Length <= size) return new List<string> { text };

        var samples = new List<string>();
        var stride = Math.Max(1, (text.Length - size) / Math.Max(1, count - 1));

        for (var i = 0; i < count; i++)
        {
            var start = Math.Min(i * stride, Math.Max(0, text.Length - size));
            samples.Add(text.Substring(start, Math.Min(size, text.Length - start)));
        }
        return samples;
    }
}

/// <summary>
/// Azure Speech neural voices used for casting. Kept as one list so the reader
/// and the analysis service can never disagree about what a voice id means.
/// </summary>
public static class BookVoices
{
    public static readonly string[] Male =
    {
        "en-GB-RyanNeural", "en-US-GuyNeural", "en-AU-WilliamNeural",
        "en-US-DavisNeural", "en-IE-ConnorNeural", "en-US-TonyNeural",
    };

    public static readonly string[] Female =
    {
        "en-GB-SoniaNeural", "en-US-JennyNeural", "en-AU-NatashaNeural",
        "en-US-AriaNeural", "en-IE-EmilyNeural", "en-US-JaneNeural",
    };

    public static readonly string[] Neutral =
    {
        "en-GB-LibbyNeural", "en-US-EricNeural", "en-GB-ThomasNeural", "en-US-NancyNeural",
    };

    /// <summary>The default narrator — warm, unobtrusive, reads well at length.</summary>
    public const string Narrator = "en-GB-RyanNeural";

    public static IEnumerable<(string Id, string Label)> All()
    {
        foreach (var v in Male) yield return (v, Pretty(v) + " (male)");
        foreach (var v in Female) yield return (v, Pretty(v) + " (female)");
        foreach (var v in Neutral) yield return (v, Pretty(v));
    }

    private static string Pretty(string voiceId)
    {
        // en-GB-RyanNeural → "Ryan · en-GB"
        var parts = voiceId.Split('-');
        if (parts.Length < 3) return voiceId;
        var name = parts[^1].Replace("Neural", string.Empty);
        return $"{name} · {parts[0]}-{parts[1]}";
    }
}
