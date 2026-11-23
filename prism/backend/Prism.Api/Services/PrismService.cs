using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Prism.Api.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Prism.Api.Services;

/// <summary>
/// Prism — Osmos-style autonomous data engineering over tabular files
/// (CSV / TSV / JSON / XLSX / PDF). The AI never edits cells directly: it compiles
/// the user's intent (map, clean, dedupe, validate, transform) into a small fixed
/// vocabulary of operations which this service then executes deterministically
/// over the FULL dataset. That keeps results exact and repeatable on large
/// files while only ever sending headers, profiles and a few sample rows to
/// the model. A dumped PDF is reconstructed into a table (via PdfPig, the same
/// engine the PDF Tools suite uses) so every tool works on it too.
/// </summary>
public class PrismService
{
    private const int MaxRows = 1_000_000;
    private const int MaxColumns = 256;
    private const int LlmSampleRows = 25;
    private const int LlmCellChars = 80;
    private const int PreviewRows = 50;

    private static readonly Regex EmailRegex = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private readonly IAIService _ai;
    private readonly ILogger<PrismService> _logger;

    public PrismService(IAIService ai, ILogger<PrismService> logger)
    {
        _ai = ai;
        _logger = logger;
    }

    // ── Table model ─────────────────────────────────────────────────────────

    public class PrismTable
    {
        public List<string> Headers { get; } = new();
        public List<string[]> Rows { get; } = new();
        public string SourceName { get; set; } = "data";
    }

    private class ColumnProfile
    {
        public string Name = string.Empty;
        public string Type = "text";
        public int Filled;
        public int Empty;
        public int Distinct;
        public List<string> Samples = new();
    }

    // ── Parsing ─────────────────────────────────────────────────────────────

    public async Task<PrismTable> ParseFileAsync(string path, string originalFileName)
    {
        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        PrismTable table;

        if (ext == ".xlsx")
        {
            await using var fs = File.OpenRead(path);
            table = ParseXlsx(fs);
        }
        else if (ext == ".pdf")
        {
            var bytes = await File.ReadAllBytesAsync(path);
            table = ParsePdf(bytes);
        }
        else
        {
            var text = await File.ReadAllTextAsync(path, Encoding.UTF8);
            table = ext == ".json" || LooksLikeJson(text) ? ParseJson(text) : ParseCsv(text);
        }

        table.SourceName = Path.GetFileNameWithoutExtension(originalFileName);
        if (table.Headers.Count == 0)
            throw new InvalidOperationException("No columns could be detected in this file.");
        return table;
    }

    /// <summary>
    /// Dispatch a tool by name. Runs on a background worker (not the request
    /// thread) — the input lives at <paramref name="inputPath"/> because the
    /// original IFormFile stream dies with the HTTP request.
    /// </summary>
    public async Task<PrismResult> RunToolAsync(string tool, string? inputPath, string? originalFileName, Dictionary<string, string> args)
    {
        string Arg(string key) => args.TryGetValue(key, out var v) ? v : string.Empty;

        if (tool == "extract")
        {
            var text = Arg("text");
            if (string.IsNullOrWhiteSpace(text) && inputPath != null)
            {
                text = Path.GetExtension(originalFileName ?? "").ToLowerInvariant() == ".pdf"
                    ? ExtractPdfText(await File.ReadAllBytesAsync(inputPath))
                    : await File.ReadAllTextAsync(inputPath, Encoding.UTF8);
            }
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("No text was provided to extract from.");
            return await ExtractAsync(text);
        }

        if (inputPath == null)
            throw new InvalidOperationException("No input file was provided.");
        var table = await ParseFileAsync(inputPath, originalFileName ?? "data");

        return tool switch
        {
            "profile" => await ProfileAsync(table),
            "map" => await MapAsync(table, Arg("targetSchema"), Arg("notes")),
            "clean" => await CleanAsync(table, Arg("notes")),
            "dedupe" => await DedupeAsync(table, Arg("notes")),
            "validate" => await ValidateAsync(table, Arg("rules")),
            "transform" => await TransformAsync(table, Arg("instruction")),
            "convert" => Convert(table, string.IsNullOrWhiteSpace(Arg("format")) ? "csv" : Arg("format")),
            _ => throw new InvalidOperationException($"Unknown tool '{tool}'."),
        };
    }

    // ── Job result store ─────────────────────────────────────────────────────
    // Async jobs persist their result to disk so the client can fetch the report
    // and stream the output file after the job completes (results keep 24h).
    // The output file is stored raw — never base64 inside the report JSON.

    private static readonly JsonSerializerOptions JsonWeb = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan ResultTtl = TimeSpan.FromHours(24);
    private static string ResultsRoot => Path.Combine(Path.GetTempPath(), "genprompt-prism-results");

    public static async Task SaveJobResultAsync(int jobId, PrismResult result)
    {
        var dir = Path.Combine(ResultsRoot, jobId.ToString());
        Directory.CreateDirectory(dir);
        if (result.Download is { } dl && dl.Base64.Length > 0)
        {
            await File.WriteAllBytesAsync(Path.Combine(dir, "output.bin"), System.Convert.FromBase64String(dl.Base64));
            dl.Base64 = string.Empty;   // streamed by the download endpoint instead
        }
        await File.WriteAllTextAsync(Path.Combine(dir, "report.json"), JsonSerializer.Serialize(result, JsonWeb));
    }

    public static PrismResult? LoadJobReport(int jobId)
    {
        var path = Path.Combine(ResultsRoot, jobId.ToString(), "report.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<PrismResult>(File.ReadAllText(path), JsonWeb); }
        catch { return null; }
    }

    public static string? GetJobOutputPath(int jobId)
    {
        var path = Path.Combine(ResultsRoot, jobId.ToString(), "output.bin");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Best-effort removal of job results past their TTL. Called when new work is queued.</summary>
    public static void CleanupExpiredResults()
    {
        try
        {
            if (!Directory.Exists(ResultsRoot)) return;
            var cutoff = DateTime.UtcNow - ResultTtl;
            foreach (var dir in Directory.EnumerateDirectories(ResultsRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                        Directory.Delete(dir, true);
                }
                catch { /* another worker may be touching it */ }
            }
        }
        catch { /* never let cleanup break job submission */ }
    }

    private static bool LooksLikeJson(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith('[') || t.StartsWith('{');
    }

    public static PrismTable ParseCsv(string text)
    {
        var delimiter = SniffDelimiter(text);
        var table = new PrismTable();
        var record = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var records = new List<List<string>>();

        void EndCell() { record.Add(cell.ToString()); cell.Clear(); }
        void EndRecord()
        {
            EndCell();
            if (record.Count > 1 || record[0].Trim().Length > 0) records.Add(new List<string>(record));
            record.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) inQuotes = true;
            else if (c == delimiter) EndCell();
            else if (c == '\r') { /* swallow — \n ends the record */ }
            else if (c == '\n') EndRecord();
            else cell.Append(c);

            if (records.Count > MaxRows) throw new InvalidOperationException($"File exceeds the {MaxRows:N0} row limit.");
        }
        if (cell.Length > 0 || record.Count > 0) EndRecord();

        if (records.Count == 0) return table;

        var headers = records[0].Select(h => h.Trim()).ToList();
        if (headers.Count > MaxColumns) throw new InvalidOperationException($"File exceeds the {MaxColumns} column limit.");
        table.Headers.AddRange(MakeUniqueHeaders(headers));

        foreach (var rec in records.Skip(1))
        {
            var row = new string[table.Headers.Count];
            for (var c = 0; c < row.Length; c++) row[c] = c < rec.Count ? rec[c] : string.Empty;
            table.Rows.Add(row);
        }
        return table;
    }

    private static char SniffDelimiter(string text)
    {
        var firstLine = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? string.Empty;
        var candidates = new[] { ',', ';', '\t', '|' };
        var best = ',';
        var bestCount = -1;
        foreach (var d in candidates)
        {
            var count = 0;
            var quoted = false;
            foreach (var c in firstLine)
            {
                if (c == '"') quoted = !quoted;
                else if (c == d && !quoted) count++;
            }
            if (count > bestCount) { bestCount = count; best = d; }
        }
        return best;
    }

    public static PrismTable ParseJson(string text)
    {
        var table = new PrismTable();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        // Accept a bare array of objects, or an object wrapping a single array property.
        if (root.ValueKind == JsonValueKind.Object)
        {
            var arrayProp = root.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.Array);
            if (arrayProp.Value.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("JSON must be an array of objects (or contain one).");
            root = arrayProp.Value;
        }
        if (root.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("JSON must be an array of objects.");

        var headerIndex = new Dictionary<string, int>();
        var rawRows = new List<Dictionary<int, string>>();

        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var row = new Dictionary<int, string>();
            foreach (var prop in item.EnumerateObject())
            {
                if (!headerIndex.TryGetValue(prop.Name, out var idx))
                {
                    if (headerIndex.Count >= MaxColumns) continue;
                    idx = headerIndex.Count;
                    headerIndex[prop.Name] = idx;
                    table.Headers.Add(prop.Name);
                }
                row[idx] = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                    _ => prop.Value.GetRawText(),
                };
            }
            rawRows.Add(row);
            if (rawRows.Count > MaxRows) throw new InvalidOperationException($"File exceeds the {MaxRows:N0} row limit.");
        }

        foreach (var raw in rawRows)
        {
            var row = new string[table.Headers.Count];
            for (var c = 0; c < row.Length; c++) row[c] = raw.TryGetValue(c, out var v) ? v : string.Empty;
            table.Rows.Add(row);
        }
        return table;
    }

    public static PrismTable ParseXlsx(Stream stream)
    {
        var table = new PrismTable();
        using var doc = SpreadsheetDocument.Open(stream, false);
        var wbPart = doc.WorkbookPart ?? throw new InvalidOperationException("Invalid XLSX file.");
        var sheet = wbPart.Workbook.Sheets?.Elements<Sheet>().FirstOrDefault()
            ?? throw new InvalidOperationException("The workbook contains no sheets.");
        var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!.Value!);
        var shared = wbPart.SharedStringTablePart?.SharedStringTable;

        var rows = wsPart.Worksheet.Descendants<Row>().ToList();
        if (rows.Count == 0) return table;

        string CellText(Cell cell)
        {
            var raw = cell.CellValue?.Text ?? cell.InnerText ?? string.Empty;
            if (cell.DataType?.Value == CellValues.SharedString && shared != null && int.TryParse(raw, out var idx))
                return shared.ElementAtOrDefault(idx)?.InnerText ?? string.Empty;
            if (cell.DataType?.Value == CellValues.InlineString) return cell.InnerText;
            return raw;
        }

        List<KeyValuePair<int, string>> ReadRow(Row row)
        {
            // Cells may lack a CellReference (some generators omit it) — fall back
            // to sequential position after the last known column.
            var cells = new List<KeyValuePair<int, string>>();
            var next = 0;
            foreach (var c in row.Elements<Cell>())
            {
                var idx = ColumnIndex(c.CellReference?.Value);
                if (idx < 0) idx = next;
                next = idx + 1;
                if (idx < MaxColumns) cells.Add(new KeyValuePair<int, string>(idx, CellText(c)));
            }
            return cells;
        }

        var headerCells = ReadRow(rows[0]);
        var width = headerCells.Count == 0 ? 0 : headerCells.Max(kv => kv.Key) + 1;
        var headers = new string[width];
        foreach (var kv in headerCells) headers[kv.Key] = kv.Value.Trim();
        table.Headers.AddRange(MakeUniqueHeaders(headers.ToList()));

        foreach (var r in rows.Skip(1))
        {
            var cells = ReadRow(r);
            var row = new string[table.Headers.Count];
            Array.Fill(row, string.Empty);
            foreach (var kv in cells)
                if (kv.Key < row.Length) row[kv.Key] = kv.Value;
            if (row.All(v => v.Length == 0)) continue;
            table.Rows.Add(row);
            if (table.Rows.Count > MaxRows) throw new InvalidOperationException($"File exceeds the {MaxRows:N0} row limit.");
        }
        return table;
    }

    private static int ColumnIndex(string? cellReference)
    {
        if (string.IsNullOrEmpty(cellReference)) return -1;
        var index = 0;
        foreach (var c in cellReference)
        {
            if (c >= 'A' && c <= 'Z') index = index * 26 + (c - 'A' + 1);
            else if (c >= 'a' && c <= 'z') index = index * 26 + (c - 'a' + 1);
            else break;
        }
        return index - 1;
    }

    private static List<string> MakeUniqueHeaders(List<string> headers)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            var name = string.IsNullOrWhiteSpace(headers[i]) ? $"Column {i + 1}" : headers[i];
            var candidate = name;
            var n = 2;
            while (!seen.Add(candidate)) candidate = $"{name} ({n++})";
            result.Add(candidate);
        }
        return result;
    }

    // ── PDF table reconstruction ─────────────────────────────────────────────
    // A dumped PDF has no cell structure — only positioned glyphs. We reuse the
    // same PdfPig engine the PDF Tools suite is built on to read each word's
    // bounding box, group words into visual rows by their vertical position, and
    // recover columns by clustering word left-edges across the whole document.
    // The first visual row becomes the header. Works on real (text) PDFs — a
    // scanned/image PDF has no words and yields a clear error.

    public static PrismTable ParsePdf(byte[] pdfBytes)
    {
        var table = new PrismTable();

        // Each entry is one visual row: its words already ordered left-to-right.
        var visualRows = new List<List<Word>>();
        double widthSum = 0;
        long charSum = 0;

        using (var doc = PdfDocument.Open(pdfBytes))
        {
            foreach (var page in doc.GetPages())
            {
                var words = page.GetWords()
                    .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                    .ToList();
                if (words.Count == 0) continue;

                foreach (var w in words)
                {
                    widthSum += Math.Abs(w.BoundingBox.Width);
                    charSum += w.Text.Length;
                }

                // Row grouping: walk words top-to-bottom, start a new row when the
                // vertical centre jumps by more than ~60% of the median glyph height.
                var medianHeight = Median(words.Select(w => Math.Abs(w.BoundingBox.Height)));
                var rowTol = Math.Max(1.0, medianHeight * 0.6);

                var sorted = words
                    .OrderByDescending(w => (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2.0)
                    .ToList();

                var current = new List<Word>();
                var currentY = double.NaN;
                foreach (var w in sorted)
                {
                    var y = (w.BoundingBox.Top + w.BoundingBox.Bottom) / 2.0;
                    if (current.Count == 0 || Math.Abs(y - currentY) <= rowTol)
                    {
                        current.Add(w);
                        currentY = double.IsNaN(currentY) ? y : (currentY * (current.Count - 1) + y) / current.Count;
                    }
                    else
                    {
                        visualRows.Add(current.OrderBy(x => x.BoundingBox.Left).ToList());
                        current = new List<Word> { w };
                        currentY = y;
                    }
                }
                if (current.Count > 0)
                    visualRows.Add(current.OrderBy(x => x.BoundingBox.Left).ToList());

                if (visualRows.Count > MaxRows)
                    throw new InvalidOperationException($"PDF exceeds the {MaxRows:N0} row limit.");
            }
        }

        if (visualRows.Count == 0)
            throw new InvalidOperationException(
                "No selectable text was found in this PDF — it looks like a scanned document. " +
                "Use a PDF that contains real text (or OCR it first).");

        var avgChar = charSum > 0 ? widthSum / charSum : 4.0;

        // Stage 1 — segment each visual row into cells. A normal single space
        // between words stays in one cell; a wider whitespace gap (column padding)
        // starts a new cell. Measuring the ACTUAL gap (next.Left − prev.Right),
        // not word left-edges, keeps multi-word cells like "New Zealand" together.
        var cellGapTol = Math.Max(2.0, avgChar * 1.6);
        var rowCells = new List<List<(double Left, string Text)>>();
        foreach (var row in visualRows)
        {
            var cells = new List<(double Left, string Text)>();
            var cell = new StringBuilder();
            double cellLeft = 0, prevRight = double.NaN;
            foreach (var w in row)
            {
                if (cell.Length == 0)
                {
                    cellLeft = w.BoundingBox.Left;
                    cell.Append(w.Text);
                }
                else if (w.BoundingBox.Left - prevRight > cellGapTol)
                {
                    cells.Add((cellLeft, cell.ToString()));
                    cell.Clear();
                    cellLeft = w.BoundingBox.Left;
                    cell.Append(w.Text);
                }
                else
                {
                    cell.Append(' ').Append(w.Text);
                }
                prevRight = w.BoundingBox.Right;
            }
            if (cell.Length > 0) cells.Add((cellLeft, cell.ToString()));
            if (cells.Count > 0) rowCells.Add(cells);
        }

        // Stage 2 — the cell-left positions now cluster tightly at each real
        // column start; align them into a shared set of columns.
        var colTol = Math.Max(2.0, avgChar * 2.5);
        var columnStarts = ClusterColumnStarts(rowCells.SelectMany(cs => cs.Select(c => c.Left)).ToList(), colTol);
        if (columnStarts.Count == 0) columnStarts.Add(0);
        if (columnStarts.Count > MaxColumns) columnStarts = columnStarts.Take(MaxColumns).ToList();

        // Stage 3 — place each cell in its column.
        var snap = avgChar;
        var grid = new List<string[]>();
        foreach (var cells in rowCells)
        {
            var arr = new string[columnStarts.Count];
            for (var i = 0; i < arr.Length; i++) arr[i] = string.Empty;
            foreach (var (left, text) in cells)
            {
                var col = ColumnForLeft(left, columnStarts, snap);
                arr[col] = arr[col].Length == 0 ? text : arr[col] + " " + text;
            }
            if (arr.Any(c => c.Length > 0)) grid.Add(arr);
        }

        if (grid.Count == 0)
            throw new InvalidOperationException("The PDF text could not be arranged into a table.");

        // Drop trailing columns that are empty in every row.
        var lastUsed = 0;
        foreach (var row in grid)
            for (var c = row.Length - 1; c >= 0; c--)
                if (row[c].Length > 0) { lastUsed = Math.Max(lastUsed, c); break; }
        var width = lastUsed + 1;

        var header = grid[0];
        table.Headers.AddRange(MakeUniqueHeaders(
            Enumerable.Range(0, width).Select(c => c < header.Length ? header[c].Trim() : string.Empty).ToList()));

        foreach (var row in grid.Skip(1))
        {
            var outRow = new string[width];
            for (var c = 0; c < width; c++) outRow[c] = c < row.Length ? row[c] : string.Empty;
            table.Rows.Add(outRow);
        }
        return table;
    }

    /// <summary>Plain-text extraction from a PDF (for the Text→Table tool).</summary>
    public static string ExtractPdfText(byte[] pdfBytes)
    {
        var sb = new StringBuilder();
        using var doc = PdfDocument.Open(pdfBytes);
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(page.Text);
            if (sb.Length > 200_000) break;
        }
        return sb.ToString();
    }

    private static double Median(IEnumerable<double> values)
    {
        var arr = values.Where(v => v > 0).OrderBy(v => v).ToArray();
        if (arr.Length == 0) return 1.0;
        var mid = arr.Length / 2;
        return arr.Length % 2 == 1 ? arr[mid] : (arr[mid - 1] + arr[mid]) / 2.0;
    }

    private static List<double> ClusterColumnStarts(List<double> lefts, double tol)
    {
        var starts = new List<double>();
        if (lefts.Count == 0) return starts;
        var sorted = lefts.OrderBy(x => x).ToList();

        var clusterMin = sorted[0];
        var prev = sorted[0];
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] - prev > tol)
            {
                starts.Add(clusterMin);
                clusterMin = sorted[i];
            }
            prev = sorted[i];
        }
        starts.Add(clusterMin);
        return starts;
    }

    private static int ColumnForLeft(double left, List<double> columnStarts, double snap)
    {
        var idx = 0;
        for (var i = 0; i < columnStarts.Count; i++)
        {
            if (left >= columnStarts[i] - snap) idx = i;
            else break;
        }
        return idx;
    }

    // ── Writers ─────────────────────────────────────────────────────────────

    public static byte[] WriteCsv(PrismTable table)
    {
        var sb = new StringBuilder();
        static string Quote(string v) =>
            v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 || v != v.Trim()
                ? "\"" + v.Replace("\"", "\"\"") + "\""
                : v;
        sb.AppendLine(string.Join(',', table.Headers.Select(Quote)));
        foreach (var row in table.Rows)
            sb.AppendLine(string.Join(',', row.Select(Quote)));
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    public static byte[] WriteJson(PrismTable table)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartArray();
            foreach (var row in table.Rows)
            {
                writer.WriteStartObject();
                for (var c = 0; c < table.Headers.Count; c++)
                    writer.WriteString(table.Headers[c], c < row.Length ? row[c] : string.Empty);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        return ms.ToArray();
    }

    public static byte[] WriteXlsx(PrismTable table)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();
            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            wsPart.Worksheet = new Worksheet(sheetData);
            var sheets = wbPart.Workbook.AppendChild(new Sheets());
            sheets.AppendChild(new Sheet { Id = wbPart.GetIdOfPart(wsPart), SheetId = 1, Name = "Data" });

            static string ColLetters(int index)
            {
                var s = string.Empty;
                var i = index + 1;
                while (i > 0)
                {
                    var r = (i - 1) % 26;
                    s = (char)('A' + r) + s;
                    i = (i - 1) / 26;
                }
                return s;
            }

            static Cell MakeCell(string value, int col, uint rowNum)
            {
                var clean = new string(value.Where(ch => ch >= ' ' || ch == '\t' || ch == '\n').ToArray());
                var cell = new Cell { CellReference = ColLetters(col) + rowNum, DataType = CellValues.InlineString };
                cell.AppendChild(new InlineString(new Text(clean) { Space = SpaceProcessingModeValues.Preserve }));
                return cell;
            }

            var headerRow = new Row { RowIndex = 1 };
            for (var c = 0; c < table.Headers.Count; c++) headerRow.AppendChild(MakeCell(table.Headers[c], c, 1));
            sheetData.AppendChild(headerRow);

            for (var i = 0; i < table.Rows.Count; i++)
            {
                var rowNum = (uint)(i + 2);
                var r = new Row { RowIndex = rowNum };
                for (var c = 0; c < table.Headers.Count; c++)
                    r.AppendChild(MakeCell(c < table.Rows[i].Length ? table.Rows[i][c] : string.Empty, c, rowNum));
                sheetData.AppendChild(r);
            }
            wbPart.Workbook.Save();
        }
        return ms.ToArray();
    }

    // ── Profiling ───────────────────────────────────────────────────────────

    private static List<ColumnProfile> ProfileColumns(PrismTable table)
    {
        var profiles = new List<ColumnProfile>();
        for (var c = 0; c < table.Headers.Count; c++)
        {
            var p = new ColumnProfile { Name = table.Headers[c] };
            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int numbers = 0, dates = 0, bools = 0, emails = 0, checkedVals = 0;

            foreach (var row in table.Rows)
            {
                var v = c < row.Length ? row[c] : string.Empty;
                if (string.IsNullOrWhiteSpace(v)) { p.Empty++; continue; }
                p.Filled++;
                if (distinct.Count <= 10_000) distinct.Add(v);
                if (p.Samples.Count < 5 && !p.Samples.Contains(v)) p.Samples.Add(Truncate(v, 60));

                if (checkedVals < 1000)
                {
                    checkedVals++;
                    if (IsNumber(v)) numbers++;
                    else if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) dates++;
                    else if (v is "true" or "false" or "True" or "False" or "TRUE" or "FALSE" or "yes" or "no" or "Yes" or "No") bools++;
                    else if (SafeIsMatch(EmailRegex, v)) emails++;
                }
            }

            p.Distinct = distinct.Count;
            if (checkedVals > 0)
            {
                var threshold = (int)Math.Ceiling(checkedVals * 0.9);
                p.Type = numbers >= threshold ? "number"
                    : dates >= threshold ? "date"
                    : bools >= threshold ? "boolean"
                    : emails >= threshold ? "email"
                    : "text";
            }
            profiles.Add(p);
        }
        return profiles;
    }

    private static bool IsNumber(string v)
    {
        var cleaned = CleanNumberString(v);
        return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
    }

    private static string CleanNumberString(string v) =>
        v.Trim().TrimStart('$', '€', '£', '₹', '¥').Replace(",", "").Replace(" ", "").Replace("%", "");

    private static bool SafeIsMatch(Regex regex, string value)
    {
        try { return regex.IsMatch(value); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static string Truncate(string v, int max) => v.Length <= max ? v : v[..max] + "…";

    // ── LLM plumbing ────────────────────────────────────────────────────────

    private string DescribeForLlm(PrismTable table, List<ColumnProfile> profiles)
    {
        var sb = new StringBuilder();
        sb.Append("FILE: ").Append(table.SourceName)
          .Append(" — ").Append(table.Rows.Count).Append(" rows × ").Append(table.Headers.Count).AppendLine(" columns");
        sb.AppendLine("COLUMNS:");
        foreach (var p in profiles.Take(80))
        {
            sb.Append("- \"").Append(p.Name).Append("\" | type=").Append(p.Type)
              .Append(" | filled=").Append(p.Filled).Append('/').Append(p.Filled + p.Empty)
              .Append(" | distinct=").Append(p.Distinct)
              .Append(" | examples: ").AppendLine(string.Join(" ; ", p.Samples.Select(s => "\"" + s + "\"")));
        }
        sb.AppendLine();
        sb.AppendLine($"SAMPLE ROWS (first {Math.Min(LlmSampleRows, table.Rows.Count)}, tab-separated, cells truncated):");
        sb.AppendLine(string.Join('\t', table.Headers.Select(h => Truncate(h, LlmCellChars))));
        foreach (var row in table.Rows.Take(LlmSampleRows))
            sb.AppendLine(string.Join('\t', row.Select(v => Truncate(v.Replace('\t', ' ').Replace('\n', ' '), LlmCellChars))));
        return sb.ToString();
    }

    private async Task<JsonDocument> CompilePlanAsync(string systemPrompt, string payload)
    {
        var response = await _ai.TestPromptAsync(new PromptTestRequest
        {
            PromptContent = systemPrompt,
            UserInput = payload
        });
        var json = ExtractJson(response.Result);
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Prism: model returned unparseable plan: {Raw}", Truncate(response.Result, 500));
            throw new InvalidOperationException("The AI could not produce a valid plan for this data. Please try again or rephrase your instructions.");
        }
    }

    private static string ExtractJson(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```"))
        {
            var firstNewline = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                text = text[(firstNewline + 1)..lastFence].Trim();
        }
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start >= 0 && end > start) return text[start..(end + 1)];
        return text;
    }

    private static string GetString(JsonElement el, string prop, string fallback = "")
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static List<string> GetStringList(JsonElement el, string prop)
    {
        var list = new List<string>();
        if (el.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    list.Add(item.GetString()!);
        return list;
    }

    private static Dictionary<string, string> GetValueMap(JsonElement el, string prop)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (el.TryGetProperty(prop, out var obj) && obj.ValueKind == JsonValueKind.Object)
            foreach (var p in obj.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String)
                    map[p.Name] = p.Value.GetString() ?? string.Empty;
        return map;
    }

    // ── Cell operation engine (the fixed vocabulary the AI compiles into) ───

    private static readonly string[] KnownOps =
    {
        "trim", "collapse_spaces", "uppercase", "lowercase", "titlecase",
        "date_iso", "number_clean", "phone_digits", "digits_only", "letters_only",
    };

    private const string OpsVocabulary =
        "Allowed ops (apply in order): \"trim\" (strip surrounding whitespace), \"collapse_spaces\" (multiple spaces to one), " +
        "\"uppercase\", \"lowercase\", \"titlecase\", \"date_iso\" (parse any date and rewrite as YYYY-MM-DD), " +
        "\"number_clean\" (strip currency symbols/thousand separators, keep a plain decimal number), " +
        "\"phone_digits\" (keep only digits and a leading +), \"digits_only\", \"letters_only\", " +
        "\"fill_empty=<value>\" (replace empty cells with <value>), \"prefix=<value>\", \"suffix=<value>\". " +
        "Additionally a \"valueMap\" object may map exact raw values to replacements (e.g. {\"M\":\"Male\"}). Do not invent other ops.";

    private static string ApplyOps(string value, List<string> ops, Dictionary<string, string>? valueMap)
    {
        var v = value;
        if (valueMap != null && v.Length > 0 && valueMap.TryGetValue(v.Trim(), out var mapped))
            v = mapped;

        foreach (var op in ops)
        {
            var eq = op.IndexOf('=');
            var name = (eq >= 0 ? op[..eq] : op).Trim().ToLowerInvariant();
            var arg = eq >= 0 ? op[(eq + 1)..] : string.Empty;

            v = name switch
            {
                "trim" => v.Trim(),
                "collapse_spaces" => Regex.Replace(v, @"\s{2,}", " "),
                "uppercase" => v.ToUpperInvariant(),
                "lowercase" => v.ToLowerInvariant(),
                "titlecase" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(v.ToLowerInvariant()),
                "date_iso" => ToIsoDate(v),
                "number_clean" => ToCleanNumber(v),
                "phone_digits" => ToPhone(v),
                "digits_only" => new string(v.Where(char.IsDigit).ToArray()),
                "letters_only" => new string(v.Where(char.IsLetter).ToArray()),
                "fill_empty" => string.IsNullOrWhiteSpace(v) ? arg : v,
                "prefix" => v.Length > 0 ? arg + v : v,
                "suffix" => v.Length > 0 ? v + arg : v,
                _ => v,
            };
        }
        return v;
    }

    private static string ToIsoDate(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return v;
        var formats = new[]
        {
            "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "d/M/yyyy", "M/d/yyyy",
            "dd-MM-yyyy", "MM-dd-yyyy", "dd.MM.yyyy", "yyyyMMdd",
            "d MMM yyyy", "d MMMM yyyy", "MMM d, yyyy", "MMMM d, yyyy",
        };
        if (DateTime.TryParseExact(v.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return exact.ToString("yyyy-MM-dd");
        if (DateTime.TryParse(v.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed.TimeOfDay == TimeSpan.Zero ? parsed.ToString("yyyy-MM-dd") : parsed.ToString("yyyy-MM-dd HH:mm:ss");
        return v;
    }

    private static string ToCleanNumber(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return v;
        var cleaned = CleanNumberString(v);
        return decimal.TryParse(cleaned, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d)
            ? d.ToString(CultureInfo.InvariantCulture)
            : v;
    }

    private static string ToPhone(string v)
    {
        var digits = new string(v.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return v.Trim();
        return v.TrimStart().StartsWith('+') ? "+" + digits : digits;
    }

    // ── Shared result helpers ───────────────────────────────────────────────

    private static PrismPreview BuildPreview(PrismTable table, int rows = PreviewRows) => new()
    {
        Headers = table.Headers.ToList(),
        Rows = table.Rows.Take(rows).Select(r => r.Select(v => Truncate(v, 200)).ToList()).ToList(),
        TotalRows = table.Rows.Count,
    };

    private static PrismDownload MakeDownload(PrismTable table, string suffix, string format = "csv")
    {
        var (bytes, contentType, ext) = format switch
        {
            "json" => (WriteJson(table), "application/json", "json"),
            "xlsx" => (WriteXlsx(table), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
            _ => (WriteCsv(table), "text/csv", "csv"),
        };
        return new PrismDownload
        {
            FileName = $"{table.SourceName}-{suffix}.{ext}",
            ContentType = contentType,
            Base64 = System.Convert.ToBase64String(bytes),
        };
    }

    private static int FindColumn(PrismTable table, string name)
        => table.Headers.FindIndex(h => string.Equals(h, name, StringComparison.OrdinalIgnoreCase));

    // ── Tool 1: Profile ─────────────────────────────────────────────────────

    public async Task<PrismResult> ProfileAsync(PrismTable table)
    {
        var profiles = ProfileColumns(table);
        var totalCells = table.Rows.Count * Math.Max(1, table.Headers.Count);
        var emptyCells = profiles.Sum(p => p.Empty);
        var completeness = totalCells == 0 ? 100 : (int)Math.Round(100.0 * (totalCells - emptyCells) / totalCells);

        var result = new PrismResult
        {
            Headline = $"Profiled {table.Rows.Count:N0} rows × {table.Headers.Count} columns — {completeness}% of cells filled.",
            Preview = BuildPreview(table, 8),
        };
        result.Metrics.Add(new PrismMetric { Label = "Rows", Value = table.Rows.Count.ToString("N0") });
        result.Metrics.Add(new PrismMetric { Label = "Columns", Value = table.Headers.Count.ToString() });
        result.Metrics.Add(new PrismMetric
        {
            Label = "Completeness",
            Value = completeness + "%",
            Tone = completeness >= 95 ? "good" : completeness >= 80 ? "warn" : "bad",
        });

        var overview = new PrismSection { Heading = "Column overview" };
        foreach (var p in profiles)
        {
            var pct = p.Filled + p.Empty == 0 ? 0 : (int)Math.Round(100.0 * p.Filled / (p.Filled + p.Empty));
            overview.Items.Add($"{p.Name} — {p.Type}, {pct}% filled, {p.Distinct:N0} distinct. Examples: {string.Join(", ", p.Samples)}");
        }
        result.Sections.Add(overview);

        // AI narrative on top of the deterministic stats — best effort.
        try
        {
            var system =
                "You are a senior data engineer reviewing the profile of a tabular dataset. Assess data quality and readiness, " +
                "grounded ONLY in the profile and sample rows provided — never invent values. " +
                "Return ONLY minified JSON: {\"headline\": string (one-sentence quality verdict), " +
                "\"metrics\": [{\"label\":\"Quality score\",\"value\":\"N/100\",\"tone\":\"good\"|\"warn\"|\"bad\"}], " +
                "\"sections\": [" +
                "{\"heading\":\"Data quality issues\",\"items\": [specific issues you can see — inconsistent formats, mixed types, gaps, suspicious values — each naming the column and an example]}, " +
                "{\"heading\":\"Recommended fixes\",\"items\": [concrete, ordered cleanup recommendations]}, " +
                "{\"heading\":\"What this data appears to be\",\"items\": [1-3 sentences inferring the domain and grain of the dataset]}" +
                "]}. No commentary, no markdown fences.";
            using var plan = await CompilePlanAsync(system, DescribeForLlm(table, profiles));
            var root = plan.RootElement;

            var aiHeadline = GetString(root, "headline");
            if (!string.IsNullOrWhiteSpace(aiHeadline)) result.Headline = aiHeadline;

            if (root.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Array)
                foreach (var m in metrics.EnumerateArray())
                    result.Metrics.Add(new PrismMetric
                    {
                        Label = GetString(m, "label"),
                        Value = GetString(m, "value"),
                        Tone = GetString(m, "tone", "neutral"),
                    });

            if (root.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
                foreach (var s in sections.EnumerateArray())
                {
                    var section = new PrismSection { Heading = GetString(s, "heading") };
                    section.Items.AddRange(GetStringList(s, "items"));
                    if (section.Items.Count > 0) result.Sections.Add(section);
                }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prism profile: AI narrative failed; returning deterministic profile only");
        }

        return result;
    }

    // ── Tool 2: AutoMap to target schema ────────────────────────────────────

    public async Task<PrismResult> MapAsync(PrismTable table, string targetSchema, string? notes)
    {
        var profiles = ProfileColumns(table);
        var system =
            "You are an autonomous data-engineering agent. Map the SOURCE columns of a dataset onto the user's TARGET schema. " +
            "Choose the best source column for each target column (match on meaning, not just name). " + OpsVocabulary + " " +
            "Return ONLY minified JSON: {\"mappings\": [{\"target\": string (target column name, keep the user's order), " +
            "\"source\": string|null (exact source column name, null if nothing fits), " +
            "\"ops\": [string], \"valueMap\": {}, \"confidence\": \"high\"|\"medium\"|\"low\", " +
            "\"note\": string (one short line explaining the choice)}], " +
            "\"unmapped\": [source columns that were not used]}. No commentary, no markdown fences.";

        var payload = DescribeForLlm(table, profiles) +
            "\n\nTARGET SCHEMA (one column per line, may include descriptions):\n" + targetSchema +
            (string.IsNullOrWhiteSpace(notes) ? "" : "\n\nADDITIONAL INSTRUCTIONS:\n" + notes);

        using var plan = await CompilePlanAsync(system, payload);
        var root = plan.RootElement;
        if (!root.TryGetProperty("mappings", out var mappings) || mappings.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The AI did not return a usable mapping plan.");

        var output = new PrismTable { SourceName = table.SourceName };
        var planSection = new PrismSection { Heading = "Mapping plan" };
        var columnSources = new List<(int SourceIndex, List<string> Ops, Dictionary<string, string> ValueMap)>();
        int mapped = 0, low = 0;

        foreach (var m in mappings.EnumerateArray())
        {
            var target = GetString(m, "target");
            if (string.IsNullOrWhiteSpace(target)) continue;
            var source = GetString(m, "source");
            var ops = GetStringList(m, "ops");
            var valueMap = GetValueMap(m, "valueMap");
            var confidence = GetString(m, "confidence", "medium");
            var note = GetString(m, "note");

            var idx = string.IsNullOrWhiteSpace(source) ? -1 : FindColumn(table, source);
            output.Headers.Add(target);
            columnSources.Add((idx, ops, valueMap));

            if (idx >= 0)
            {
                mapped++;
                if (confidence == "low") low++;
                var opsNote = ops.Count > 0 ? $" [{string.Join(", ", ops)}]" : "";
                planSection.Items.Add($"{target} ← {table.Headers[idx]} ({confidence}){opsNote}{(string.IsNullOrWhiteSpace(note) ? "" : " — " + note)}");
            }
            else
            {
                planSection.Items.Add($"{target} ← (no source found — left empty){(string.IsNullOrWhiteSpace(note) ? "" : " — " + note)}");
            }
        }

        if (output.Headers.Count == 0)
            throw new InvalidOperationException("No target columns could be parsed from the schema.");

        foreach (var row in table.Rows)
        {
            var outRow = new string[output.Headers.Count];
            for (var c = 0; c < columnSources.Count; c++)
            {
                var (srcIdx, ops, valueMap) = columnSources[c];
                var raw = srcIdx >= 0 && srcIdx < row.Length ? row[srcIdx] : string.Empty;
                outRow[c] = ApplyOps(raw, ops, valueMap);
            }
            output.Rows.Add(outRow);
        }

        var result = new PrismResult
        {
            Headline = $"Mapped {mapped} of {output.Headers.Count} target columns from \"{table.SourceName}\" — {table.Rows.Count:N0} rows converted.",
            Preview = BuildPreview(output),
            Download = MakeDownload(output, "mapped"),
        };
        result.Metrics.Add(new PrismMetric { Label = "Target columns", Value = output.Headers.Count.ToString() });
        result.Metrics.Add(new PrismMetric { Label = "Mapped", Value = mapped.ToString(), Tone = mapped == output.Headers.Count ? "good" : "warn" });
        if (output.Headers.Count - mapped > 0)
            result.Metrics.Add(new PrismMetric { Label = "Unmapped targets", Value = (output.Headers.Count - mapped).ToString(), Tone = "bad" });
        if (low > 0)
            result.Metrics.Add(new PrismMetric { Label = "Low confidence", Value = low.ToString(), Tone = "warn" });
        result.Sections.Add(planSection);

        var unused = GetStringList(root, "unmapped");
        if (unused.Count > 0)
            result.Sections.Add(new PrismSection { Heading = "Source columns not used", Items = unused });

        return result;
    }

    // ── Tool 3: AutoClean ───────────────────────────────────────────────────

    public async Task<PrismResult> CleanAsync(PrismTable table, string? notes)
    {
        var profiles = ProfileColumns(table);
        var system =
            "You are an autonomous data-cleaning agent. Inspect the dataset profile and sample rows, find formatting problems " +
            "(inconsistent casing, stray whitespace, mixed date formats, currency symbols in numbers, inconsistent category labels, " +
            "messy phone numbers) and produce a cleaning plan. Only clean what is actually inconsistent — do not churn columns that are already clean. " +
            OpsVocabulary + " " +
            "Return ONLY minified JSON: {\"columns\": [{\"column\": string (exact source column name), \"ops\": [string], " +
            "\"valueMap\": {}, \"reason\": string (one short line)}], \"summary\": string (one sentence describing the overall cleanup)}. " +
            "No commentary, no markdown fences.";

        var payload = DescribeForLlm(table, profiles) +
            (string.IsNullOrWhiteSpace(notes) ? "" : "\n\nADDITIONAL INSTRUCTIONS:\n" + notes);

        using var plan = await CompilePlanAsync(system, payload);
        var root = plan.RootElement;
        if (!root.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The AI did not return a usable cleaning plan.");

        var output = new PrismTable { SourceName = table.SourceName };
        output.Headers.AddRange(table.Headers);
        foreach (var row in table.Rows) output.Rows.Add((string[])row.Clone());

        var applied = new PrismSection { Heading = "Cleaning applied" };
        var totalChanged = 0;

        foreach (var colPlan in columns.EnumerateArray())
        {
            var name = GetString(colPlan, "column");
            var idx = FindColumn(output, name);
            if (idx < 0) continue;
            var ops = GetStringList(colPlan, "ops");
            var valueMap = GetValueMap(colPlan, "valueMap");
            if (ops.Count == 0 && valueMap.Count == 0) continue;

            var changed = 0;
            foreach (var row in output.Rows)
            {
                var before = row[idx];
                var after = ApplyOps(before, ops, valueMap);
                if (!string.Equals(before, after, StringComparison.Ordinal)) { row[idx] = after; changed++; }
            }
            totalChanged += changed;

            var reason = GetString(colPlan, "reason");
            var opsLabel = ops.Count > 0 ? string.Join(", ", ops) : "value mapping";
            applied.Items.Add($"{output.Headers[idx]} — {opsLabel} — {changed:N0} cells changed{(string.IsNullOrWhiteSpace(reason) ? "" : " — " + reason)}");
        }

        var summary = GetString(root, "summary");
        var result = new PrismResult
        {
            Headline = string.IsNullOrWhiteSpace(summary)
                ? $"Cleaned {totalChanged:N0} cells across {applied.Items.Count} columns."
                : summary,
            Preview = BuildPreview(output),
            Download = MakeDownload(output, "cleaned"),
        };
        result.Metrics.Add(new PrismMetric { Label = "Rows", Value = output.Rows.Count.ToString("N0") });
        result.Metrics.Add(new PrismMetric { Label = "Columns cleaned", Value = applied.Items.Count.ToString(), Tone = applied.Items.Count > 0 ? "good" : "neutral" });
        result.Metrics.Add(new PrismMetric { Label = "Cells changed", Value = totalChanged.ToString("N0"), Tone = totalChanged > 0 ? "good" : "neutral" });
        if (applied.Items.Count == 0)
            applied.Items.Add("No inconsistencies worth fixing were found — the data already looks clean.");
        result.Sections.Add(applied);
        return result;
    }

    // ── Tool 4: Dedupe ──────────────────────────────────────────────────────

    public async Task<PrismResult> DedupeAsync(PrismTable table, string? notes)
    {
        var profiles = ProfileColumns(table);
        var system =
            "You are an autonomous data-deduplication agent. From the dataset profile and sample rows, choose the column(s) that " +
            "identify a real-world entity (e.g. email, id, or name+company) so duplicate records can be detected, and how to " +
            "normalize the key before comparing. " +
            "Return ONLY minified JSON: {\"keyColumns\": [exact source column names, 1-3 of them], " +
            "\"normalize\": [any of \"trim\",\"lowercase\",\"collapse_spaces\",\"digits_only\",\"phone_digits\",\"number_clean\",\"date_iso\"], " +
            "\"reason\": string (one short line explaining the choice)}. No commentary, no markdown fences.";

        var payload = DescribeForLlm(table, profiles) +
            (string.IsNullOrWhiteSpace(notes) ? "" : "\n\nADDITIONAL INSTRUCTIONS:\n" + notes);

        using var plan = await CompilePlanAsync(system, payload);
        var root = plan.RootElement;
        var keyColumns = GetStringList(root, "keyColumns");
        var normalize = GetStringList(root, "normalize");
        var reason = GetString(root, "reason");

        var keyIndexes = keyColumns.Select(k => FindColumn(table, k)).Where(i => i >= 0).ToList();
        if (keyIndexes.Count == 0)
            throw new InvalidOperationException("The AI could not identify key columns for deduplication. Try adding an instruction like 'dedupe by email'.");

        var output = new PrismTable { SourceName = table.SourceName };
        output.Headers.AddRange(table.Headers);

        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var groupSizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in table.Rows)
        {
            var parts = keyIndexes.Select(i => ApplyOps(i < row.Length ? row[i] : string.Empty, normalize, null));
            var key = string.Join('\u0001', parts);

            if (key.Replace("\u0001", "").Length == 0)
            {
                output.Rows.Add(row);           // rows with an entirely empty key are never merged
                continue;
            }
            if (seen.TryAdd(key, output.Rows.Count))
                output.Rows.Add(row);
            else
                groupSizes[key] = groupSizes.TryGetValue(key, out var n) ? n + 1 : 2;
        }

        var removed = table.Rows.Count - output.Rows.Count;
        var result = new PrismResult
        {
            Headline = removed > 0
                ? $"Removed {removed:N0} duplicate rows ({groupSizes.Count:N0} duplicate groups) using key: {string.Join(" + ", keyIndexes.Select(i => table.Headers[i]))}."
                : $"No duplicates found using key: {string.Join(" + ", keyIndexes.Select(i => table.Headers[i]))}.",
            Preview = BuildPreview(output),
            Download = MakeDownload(output, "deduped"),
        };
        result.Metrics.Add(new PrismMetric { Label = "Rows before", Value = table.Rows.Count.ToString("N0") });
        result.Metrics.Add(new PrismMetric { Label = "Rows after", Value = output.Rows.Count.ToString("N0") });
        result.Metrics.Add(new PrismMetric { Label = "Removed", Value = removed.ToString("N0"), Tone = removed > 0 ? "good" : "neutral" });

        var detail = new PrismSection { Heading = "How duplicates were detected" };
        detail.Items.Add($"Identity key: {string.Join(" + ", keyIndexes.Select(i => table.Headers[i]))}");
        if (normalize.Count > 0) detail.Items.Add($"Key normalization before comparing: {string.Join(", ", normalize)}");
        if (!string.IsNullOrWhiteSpace(reason)) detail.Items.Add($"Why this key: {reason}");
        detail.Items.Add("The first occurrence of each duplicate group is kept; rows with an empty key are never merged.");
        result.Sections.Add(detail);

        if (groupSizes.Count > 0)
        {
            var top = new PrismSection { Heading = "Largest duplicate groups" };
            foreach (var kv in groupSizes.OrderByDescending(g => g.Value).Take(10))
                top.Items.Add($"\"{Truncate(kv.Key.Replace('\u0001', '/'), 100)}\" — {kv.Value} occurrences");
            result.Sections.Add(top);
        }
        return result;
    }

    // ── Tool 5: Validate ────────────────────────────────────────────────────

    private class CompiledCheck
    {
        public string Rule = string.Empty;
        public string Column = string.Empty;
        public string Type = "required";
        public string? Pattern;
        public List<string> Values = new();
        public decimal? Min;
        public decimal? Max;
        public string Severity = "error";
    }

    public async Task<PrismResult> ValidateAsync(PrismTable table, string? rules)
    {
        var profiles = ProfileColumns(table);
        var auto = string.IsNullOrWhiteSpace(rules);
        var system =
            "You are an autonomous data-validation agent. Compile validation rules into structured checks that will be executed " +
            "in code over every row. " +
            (auto
                ? "The user gave no rules, so propose sensible checks from the column profiles (required key fields, email/date/number formats, obvious categorical domains). Be conservative — only checks the data is clearly meant to satisfy. "
                : "Compile EVERY rule the user wrote. If a rule names a column loosely, use the closest exact column name. ") +
            "Check types: \"required\" (no empty cells), \"unique\", \"email\", \"url\", \"date\" (parseable date), \"integer\", " +
            "\"number\" (numeric, optional min/max), \"length\" (string length, min/max), " +
            "\"allowed\" (value must be in \"values\"), \"regex\" (must match \"pattern\", .NET syntax). " +
            "Return ONLY minified JSON: {\"checks\": [{\"rule\": string (readable restatement), \"column\": string (exact name), " +
            "\"type\": string, \"pattern\": string|null, \"values\": [string], \"min\": number|null, \"max\": number|null, " +
            "\"severity\": \"error\"|\"warning\"}]}. No commentary, no markdown fences.";

        var payload = DescribeForLlm(table, profiles) +
            (auto ? "" : "\n\nRULES TO ENFORCE:\n" + rules);

        using var plan = await CompilePlanAsync(system, payload);
        var root = plan.RootElement;
        var checks = new List<CompiledCheck>();
        if (root.TryGetProperty("checks", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in arr.EnumerateArray())
            {
                var check = new CompiledCheck
                {
                    Rule = GetString(c, "rule"),
                    Column = GetString(c, "column"),
                    Type = GetString(c, "type", "required").ToLowerInvariant(),
                    Pattern = c.TryGetProperty("pattern", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                    Values = GetStringList(c, "values"),
                    Severity = GetString(c, "severity", "error"),
                };
                if (c.TryGetProperty("min", out var min) && min.ValueKind == JsonValueKind.Number) check.Min = min.GetDecimal();
                if (c.TryGetProperty("max", out var max) && max.ValueKind == JsonValueKind.Number) check.Max = max.GetDecimal();
                if (!string.IsNullOrWhiteSpace(check.Column)) checks.Add(check);
            }
        }
        if (checks.Count == 0)
            throw new InvalidOperationException("No checks could be compiled from these rules.");

        // Execute checks deterministically over every row.
        var violations = new PrismTable { SourceName = table.SourceName };
        violations.Headers.AddRange(new[] { "Row", "Column", "Value", "Rule", "Severity" });
        var perCheck = new List<(CompiledCheck Check, int Count, bool Skipped)>();
        const int maxViolations = 10_000;

        foreach (var check in checks)
        {
            var idx = FindColumn(table, check.Column);
            if (idx < 0) { perCheck.Add((check, 0, true)); continue; }

            Regex? regex = null;
            if (check.Type == "regex" && !string.IsNullOrEmpty(check.Pattern))
            {
                try { regex = new Regex(check.Pattern, RegexOptions.None, TimeSpan.FromMilliseconds(200)); }
                catch (ArgumentException) { perCheck.Add((check, 0, true)); continue; }
            }

            var allowedSet = new HashSet<string>(check.Values.Select(v => v.Trim()), StringComparer.OrdinalIgnoreCase);
            var seenValues = check.Type == "unique" ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : null;
            if (seenValues != null)
                for (var r = 0; r < table.Rows.Count; r++)
                {
                    var v = idx < table.Rows[r].Length ? table.Rows[r][idx].Trim() : string.Empty;
                    if (v.Length > 0) seenValues[v] = seenValues.TryGetValue(v, out var n) ? n + 1 : 1;
                }

            var count = 0;
            for (var r = 0; r < table.Rows.Count; r++)
            {
                var value = idx < table.Rows[r].Length ? table.Rows[r][idx] : string.Empty;
                var trimmed = value.Trim();
                var empty = trimmed.Length == 0;

                var failed = check.Type switch
                {
                    "required" => empty,
                    "unique" => !empty && seenValues![trimmed] > 1,
                    "email" => !empty && !SafeIsMatch(EmailRegex, trimmed),
                    "url" => !empty && !(Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)),
                    "date" => !empty && ToIsoDate(trimmed) == trimmed && !DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                    "integer" => !empty && !long.TryParse(CleanNumberString(trimmed), out _),
                    "number" => !empty && (!decimal.TryParse(CleanNumberString(trimmed), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var num)
                                           || (check.Min.HasValue && num < check.Min) || (check.Max.HasValue && num > check.Max)),
                    "length" => !empty && ((check.Min.HasValue && trimmed.Length < check.Min) || (check.Max.HasValue && trimmed.Length > check.Max)),
                    "allowed" => !empty && allowedSet.Count > 0 && !allowedSet.Contains(trimmed),
                    "regex" => !empty && regex != null && !SafeIsMatch(regex, trimmed),
                    _ => false,
                };

                if (failed)
                {
                    count++;
                    if (violations.Rows.Count < maxViolations)
                        violations.Rows.Add(new[] { (r + 2).ToString(), check.Column, Truncate(value, 120), check.Rule, check.Severity });
                }
            }
            perCheck.Add((check, count, false));
        }

        var totalViolations = perCheck.Sum(p => p.Count);
        var failedChecks = perCheck.Count(p => p.Count > 0);
        var result = new PrismResult
        {
            Headline = totalViolations == 0
                ? $"All {perCheck.Count(p => !p.Skipped)} checks passed across {table.Rows.Count:N0} rows."
                : $"{totalViolations:N0} violations found across {failedChecks} of {perCheck.Count(p => !p.Skipped)} checks ({table.Rows.Count:N0} rows checked).",
            Preview = violations.Rows.Count > 0 ? BuildPreview(violations, 100) : null,
            Download = violations.Rows.Count > 0 ? MakeDownload(violations, "violations") : null,
        };
        result.Metrics.Add(new PrismMetric { Label = "Rows checked", Value = table.Rows.Count.ToString("N0") });
        result.Metrics.Add(new PrismMetric { Label = "Checks", Value = perCheck.Count(p => !p.Skipped).ToString() });
        result.Metrics.Add(new PrismMetric { Label = "Violations", Value = totalViolations.ToString("N0"), Tone = totalViolations == 0 ? "good" : "bad" });

        var breakdown = new PrismSection { Heading = "Check results" };
        foreach (var (check, count, skipped) in perCheck)
            breakdown.Items.Add(skipped
                ? $"SKIPPED — {check.Rule} (column \"{check.Column}\" not found or pattern invalid)"
                : $"{(count == 0 ? "PASS" : count.ToString("N0") + " violations")} — {check.Rule} [{check.Column}, {check.Type}]");
        result.Sections.Add(breakdown);

        if (violations.Rows.Count >= maxViolations)
            result.Sections.Add(new PrismSection
            {
                Heading = "Note",
                Items = { $"Violation detail capped at {maxViolations:N0} rows; the summary counts above cover the full file." }
            });
        return result;
    }

    // ── Tool 6: Transform (natural language) ────────────────────────────────

    public async Task<PrismResult> TransformAsync(PrismTable table, string instruction)
    {
        var profiles = ProfileColumns(table);
        var system =
            "You are an autonomous data-transformation agent. Compile the user's instruction into an ordered list of table " +
            "operations that will be executed in code. " + OpsVocabulary + " " +
            "Step shapes: " +
            "{\"op\":\"rename\",\"column\":s,\"to\":s} | " +
            "{\"op\":\"drop\",\"columns\":[s]} | " +
            "{\"op\":\"keep\",\"columns\":[s]} | " +
            "{\"op\":\"filter\",\"column\":s,\"comparator\":\"equals\"|\"not_equals\"|\"contains\"|\"not_contains\"|\"starts_with\"|\"ends_with\"|\"gt\"|\"gte\"|\"lt\"|\"lte\"|\"empty\"|\"not_empty\"|\"regex\",\"value\":s} (keeps matching rows) | " +
            "{\"op\":\"cell\",\"column\":s,\"ops\":[cell ops],\"valueMap\":{}} | " +
            "{\"op\":\"derive\",\"column\":s (new name),\"from\":[source columns],\"separator\":s} (concatenate) | " +
            "{\"op\":\"split\",\"column\":s,\"separator\":s,\"index\":n (0-based),\"to\":s (new column)} | " +
            "{\"op\":\"sort\",\"column\":s,\"direction\":\"asc\"|\"desc\",\"numeric\":bool} | " +
            "{\"op\":\"distinct\"} | {\"op\":\"limit\",\"count\":n}. " +
            "Column names must exactly match the source columns. " +
            "Return ONLY minified JSON: {\"steps\": [...], \"summary\": string (one sentence)}. No commentary, no markdown fences.";

        var payload = DescribeForLlm(table, profiles) + "\n\nINSTRUCTION:\n" + instruction;

        using var plan = await CompilePlanAsync(system, payload);
        var root = plan.RootElement;
        if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("The AI did not return a usable transformation plan.");

        var output = new PrismTable { SourceName = table.SourceName };
        output.Headers.AddRange(table.Headers);
        foreach (var row in table.Rows) output.Rows.Add((string[])row.Clone());

        var log = new PrismSection { Heading = "Steps applied" };
        var rowsBefore = output.Rows.Count;

        foreach (var step in steps.EnumerateArray())
        {
            var op = GetString(step, "op").ToLowerInvariant();
            switch (op)
            {
                case "rename":
                {
                    var idx = FindColumn(output, GetString(step, "column"));
                    var to = GetString(step, "to");
                    if (idx >= 0 && !string.IsNullOrWhiteSpace(to))
                    {
                        log.Items.Add($"Renamed \"{output.Headers[idx]}\" to \"{to}\"");
                        output.Headers[idx] = to;
                    }
                    break;
                }
                case "drop":
                {
                    var toDrop = GetStringList(step, "columns").Select(c => FindColumn(output, c)).Where(i => i >= 0).OrderByDescending(i => i).ToList();
                    foreach (var idx in toDrop)
                    {
                        log.Items.Add($"Dropped column \"{output.Headers[idx]}\"");
                        output.Headers.RemoveAt(idx);
                        for (var r = 0; r < output.Rows.Count; r++)
                        {
                            var list = output.Rows[r].ToList();
                            if (idx < list.Count) list.RemoveAt(idx);
                            output.Rows[r] = list.ToArray();
                        }
                    }
                    break;
                }
                case "keep":
                {
                    var keepIdx = GetStringList(step, "columns").Select(c => FindColumn(output, c)).Where(i => i >= 0).ToList();
                    if (keepIdx.Count == 0) break;
                    log.Items.Add($"Kept only: {string.Join(", ", keepIdx.Select(i => output.Headers[i]))}");
                    var newHeaders = keepIdx.Select(i => output.Headers[i]).ToList();
                    for (var r = 0; r < output.Rows.Count; r++)
                        output.Rows[r] = keepIdx.Select(i => i < output.Rows[r].Length ? output.Rows[r][i] : string.Empty).ToArray();
                    output.Headers.Clear();
                    output.Headers.AddRange(newHeaders);
                    break;
                }
                case "filter":
                {
                    var idx = FindColumn(output, GetString(step, "column"));
                    if (idx < 0) break;
                    var comparator = GetString(step, "comparator", "equals").ToLowerInvariant();
                    var target = GetString(step, "value");
                    var before = output.Rows.Count;

                    Regex? filterRegex = null;
                    if (comparator == "regex")
                    {
                        try { filterRegex = new Regex(target, RegexOptions.None, TimeSpan.FromMilliseconds(200)); }
                        catch (ArgumentException) { break; }
                    }
                    decimal.TryParse(CleanNumberString(target), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var targetNum);

                    bool Keep(string[] row)
                    {
                        var v = idx < row.Length ? row[idx] : string.Empty;
                        var t = v.Trim();
                        var hasNum = decimal.TryParse(CleanNumberString(t), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var num);
                        return comparator switch
                        {
                            "equals" => string.Equals(t, target.Trim(), StringComparison.OrdinalIgnoreCase),
                            "not_equals" => !string.Equals(t, target.Trim(), StringComparison.OrdinalIgnoreCase),
                            "contains" => t.Contains(target, StringComparison.OrdinalIgnoreCase),
                            "not_contains" => !t.Contains(target, StringComparison.OrdinalIgnoreCase),
                            "starts_with" => t.StartsWith(target, StringComparison.OrdinalIgnoreCase),
                            "ends_with" => t.EndsWith(target, StringComparison.OrdinalIgnoreCase),
                            "gt" => hasNum && num > targetNum,
                            "gte" => hasNum && num >= targetNum,
                            "lt" => hasNum && num < targetNum,
                            "lte" => hasNum && num <= targetNum,
                            "empty" => t.Length == 0,
                            "not_empty" => t.Length > 0,
                            "regex" => filterRegex != null && SafeIsMatch(filterRegex, t),
                            _ => true,
                        };
                    }

                    output.Rows.RemoveAll(r => !Keep(r));
                    log.Items.Add($"Filtered \"{output.Headers[idx]}\" {comparator} \"{target}\" — {before:N0} → {output.Rows.Count:N0} rows");
                    break;
                }
                case "cell":
                {
                    var idx = FindColumn(output, GetString(step, "column"));
                    if (idx < 0) break;
                    var ops = GetStringList(step, "ops");
                    var valueMap = GetValueMap(step, "valueMap");
                    var changed = 0;
                    foreach (var row in output.Rows)
                    {
                        var after = ApplyOps(row[idx], ops, valueMap);
                        if (!string.Equals(row[idx], after, StringComparison.Ordinal)) { row[idx] = after; changed++; }
                    }
                    log.Items.Add($"Applied {string.Join(", ", ops.DefaultIfEmpty("value mapping"))} to \"{output.Headers[idx]}\" — {changed:N0} cells changed");
                    break;
                }
                case "derive":
                {
                    var name = GetString(step, "column");
                    var from = GetStringList(step, "from").Select(c => FindColumn(output, c)).Where(i => i >= 0).ToList();
                    if (string.IsNullOrWhiteSpace(name) || from.Count == 0) break;
                    var separator = GetString(step, "separator", " ");
                    output.Headers.Add(name);
                    for (var r = 0; r < output.Rows.Count; r++)
                    {
                        var parts = from.Select(i => i < output.Rows[r].Length ? output.Rows[r][i] : string.Empty).Where(v => v.Length > 0);
                        output.Rows[r] = output.Rows[r].Append(string.Join(separator, parts)).ToArray();
                    }
                    log.Items.Add($"Derived \"{name}\" from {string.Join(" + ", from.Select(i => output.Headers[i]))}");
                    break;
                }
                case "split":
                {
                    var idx = FindColumn(output, GetString(step, "column"));
                    var to = GetString(step, "to");
                    if (idx < 0 || string.IsNullOrWhiteSpace(to)) break;
                    var separator = GetString(step, "separator", " ");
                    var index = step.TryGetProperty("index", out var iEl) && iEl.ValueKind == JsonValueKind.Number ? iEl.GetInt32() : 0;
                    output.Headers.Add(to);
                    for (var r = 0; r < output.Rows.Count; r++)
                    {
                        var v = idx < output.Rows[r].Length ? output.Rows[r][idx] : string.Empty;
                        var parts = v.Split(separator, StringSplitOptions.None);
                        var picked = index >= 0 && index < parts.Length ? parts[index].Trim() : string.Empty;
                        output.Rows[r] = output.Rows[r].Append(picked).ToArray();
                    }
                    log.Items.Add($"Split \"{output.Headers[idx]}\" on \"{separator}\" → \"{to}\" (part {index})");
                    break;
                }
                case "sort":
                {
                    var idx = FindColumn(output, GetString(step, "column"));
                    if (idx < 0) break;
                    var desc = GetString(step, "direction", "asc") == "desc";
                    var numeric = step.TryGetProperty("numeric", out var nEl) && nEl.ValueKind == JsonValueKind.True;

                    Comparison<string[]> cmp = (a, b) =>
                    {
                        var va = idx < a.Length ? a[idx] : string.Empty;
                        var vb = idx < b.Length ? b[idx] : string.Empty;
                        int c;
                        if (numeric)
                        {
                            var ha = decimal.TryParse(CleanNumberString(va), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var na);
                            var hb = decimal.TryParse(CleanNumberString(vb), NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var nb);
                            c = ha && hb ? na.CompareTo(nb) : ha ? -1 : hb ? 1 : string.Compare(va, vb, StringComparison.OrdinalIgnoreCase);
                        }
                        else c = string.Compare(va, vb, StringComparison.OrdinalIgnoreCase);
                        return desc ? -c : c;
                    };
                    output.Rows.Sort(cmp);
                    log.Items.Add($"Sorted by \"{output.Headers[idx]}\" {(desc ? "descending" : "ascending")}{(numeric ? " (numeric)" : "")}");
                    break;
                }
                case "distinct":
                {
                    var before = output.Rows.Count;
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    output.Rows.RemoveAll(r => !seen.Add(string.Join('\u0001', r)));
                    log.Items.Add($"Removed exact duplicate rows — {before:N0} → {output.Rows.Count:N0}");
                    break;
                }
                case "limit":
                {
                    var count = step.TryGetProperty("count", out var cEl) && cEl.ValueKind == JsonValueKind.Number ? cEl.GetInt32() : 0;
                    if (count > 0 && output.Rows.Count > count)
                    {
                        output.Rows.RemoveRange(count, output.Rows.Count - count);
                        log.Items.Add($"Limited to first {count:N0} rows");
                    }
                    break;
                }
            }
        }

        if (log.Items.Count == 0)
            throw new InvalidOperationException("The instruction did not compile to any applicable steps. Try naming the columns you want changed.");

        var summary = GetString(root, "summary");
        var result = new PrismResult
        {
            Headline = string.IsNullOrWhiteSpace(summary)
                ? $"Applied {log.Items.Count} transformation steps — {rowsBefore:N0} → {output.Rows.Count:N0} rows."
                : summary,
            Preview = BuildPreview(output),
            Download = MakeDownload(output, "transformed"),
        };
        result.Metrics.Add(new PrismMetric { Label = "Steps", Value = log.Items.Count.ToString() });
        result.Metrics.Add(new PrismMetric { Label = "Rows before", Value = rowsBefore.ToString("N0") });
        result.Metrics.Add(new PrismMetric { Label = "Rows after", Value = output.Rows.Count.ToString("N0") });
        result.Metrics.Add(new PrismMetric { Label = "Columns", Value = output.Headers.Count.ToString() });
        result.Sections.Add(log);
        return result;
    }

    // ── Tool 7: Convert (deterministic, no LLM) ─────────────────────────────

    public PrismResult Convert(PrismTable table, string format)
    {
        format = format.ToLowerInvariant();
        if (format is not ("csv" or "json" or "xlsx"))
            throw new InvalidOperationException("Format must be csv, json, or xlsx.");

        var result = new PrismResult
        {
            Headline = $"Converted \"{table.SourceName}\" to {format.ToUpperInvariant()} — {table.Rows.Count:N0} rows × {table.Headers.Count} columns.",
            Preview = BuildPreview(table, 8),
            Download = MakeDownload(table, "converted", format),
        };
        result.Metrics.Add(new PrismMetric { Label = "Rows", Value = table.Rows.Count.ToString("N0") });
        result.Metrics.Add(new PrismMetric { Label = "Columns", Value = table.Headers.Count.ToString() });
        result.Metrics.Add(new PrismMetric { Label = "Output", Value = format.ToUpperInvariant(), Tone = "good" });
        return result;
    }

    // ── Tool 8: Extract table from unstructured text ────────────────────────

    public async Task<PrismResult> ExtractAsync(string text)
    {
        const int maxChars = 24_000;
        if (text.Length > maxChars) text = text[..maxChars];

        var system =
            "You are a data-extraction agent. The user gives you unstructured or semi-structured text (an email, a report, " +
            "meeting notes, a pasted table, a log). Extract the structured tabular data it contains. Choose clear, " +
            "snake_case column headers. Every row must have the same number of cells as there are headers. Use \"\" for unknown " +
            "cells. NEVER invent data that is not in the text. " +
            "Return ONLY minified JSON: {\"headers\": [string], \"rows\": [[string]], \"note\": string (one line on what was extracted)}. " +
            "No commentary, no markdown fences.";

        using var plan = await CompilePlanAsync(system, text);
        var root = plan.RootElement;
        var headers = GetStringList(root, "headers");
        if (headers.Count == 0)
            throw new InvalidOperationException("No table could be extracted from this text.");

        var output = new PrismTable { SourceName = "extracted" };
        output.Headers.AddRange(MakeUniqueHeaders(headers));

        if (root.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rows.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Array) continue;
                var cells = r.EnumerateArray()
                    .Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : c.ToString())
                    .ToList();
                var row = new string[output.Headers.Count];
                for (var c = 0; c < row.Length; c++) row[c] = c < cells.Count ? cells[c] : string.Empty;
                output.Rows.Add(row);
                if (output.Rows.Count >= 2000) break;
            }
        }

        var note = GetString(root, "note");
        var result = new PrismResult
        {
            Headline = string.IsNullOrWhiteSpace(note)
                ? $"Extracted a {output.Rows.Count:N0}-row × {output.Headers.Count}-column table."
                : note,
            Preview = BuildPreview(output),
            Download = MakeDownload(output, "table"),
        };
        result.Metrics.Add(new PrismMetric { Label = "Rows", Value = output.Rows.Count.ToString("N0"), Tone = output.Rows.Count > 0 ? "good" : "warn" });
        result.Metrics.Add(new PrismMetric { Label = "Columns", Value = output.Headers.Count.ToString() });
        return result;
    }
}
