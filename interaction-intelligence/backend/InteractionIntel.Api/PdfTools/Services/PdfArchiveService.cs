using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PdfAI.API.Services;

/// <summary>
/// Archival-compliance tools: convert to PDF/A (via Ghostscript's pdfwrite with
/// an sRGB OutputIntent), run a quick structural conformance check, and strip
/// document metadata (Info dictionary + XMP) for privacy scrubbing.
/// </summary>
public class PdfArchiveService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PdfArchiveService> _logger;

    public PdfArchiveService(IConfiguration configuration, ILogger<PdfArchiveService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public record PdfAValidationReport(
        bool ClaimsPdfA, string? Part, string? Conformance, bool HasOutputIntent,
        bool IsEncrypted, int PageCount, string PdfVersion, List<string> Warnings, string Note);

    /// <summary>Convert to PDF/A-2b (default) or PDF/A-3b using Ghostscript.</summary>
    public async Task<byte[]> ConvertToPdfAAsync(byte[] pdfBytes, string level = "2b")
    {
        var pdfaPart = level.StartsWith('3') ? "3" : "2";
        var gs = GhostscriptPath();

        var tempDir = Directory.CreateTempSubdirectory("pdfa-");
        try
        {
            var inputPath = Path.Combine(tempDir.FullName, "input.pdf");
            var outputPath = Path.Combine(tempDir.FullName, "output.pdf");
            var defPath = Path.Combine(tempDir.FullName, "PDFA_def.ps");
            await File.WriteAllBytesAsync(inputPath, pdfBytes);
            var (defContent, iccPath) = BuildPdfADef();
            await File.WriteAllTextAsync(defPath, defContent);

            // -dSAFER blocks file reads outside the invocation set, so the ICC
            // profile referenced by PDFA_def.ps must be explicitly permitted.
            var permitIcc = iccPath != null ? $"\"--permit-file-read={iccPath}\" " : "";
            var args =
                $"-dPDFA={pdfaPart} -dBATCH -dNOPAUSE -dNOOUTERSAVE -dSAFER {permitIcc}" +
                "-sColorConversionStrategy=RGB -sDEVICE=pdfwrite -dPDFACompatibilityPolicy=1 " +
                $"-sOutputFile=\"{outputPath}\" \"{defPath}\" \"{inputPath}\"";

            await RunToolAsync(gs, args);

            if (!File.Exists(outputPath))
                throw new InvalidOperationException("PDF/A conversion produced no output.");
            return await File.ReadAllBytesAsync(outputPath);
        }
        finally
        {
            try { tempDir.Delete(true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Quick structural conformance check: XMP PDF/A identification, OutputIntent,
    /// encryption. This is a fast sanity report, NOT a full veraPDF validation —
    /// the report says so explicitly.
    /// </summary>
    public PdfAValidationReport ValidatePdfA(byte[] pdfBytes)
    {
        var warnings = new List<string>();
        using var stream = new MemoryStream(pdfBytes);

        PdfDocument document;
        try
        {
            document = PdfReader.Open(stream, PdfDocumentOpenMode.ReadOnly);
        }
        catch (PdfReaderException)
        {
            return new PdfAValidationReport(false, null, null, false, true, 0, "unknown",
                new List<string> { "The document is encrypted or could not be parsed — encrypted PDFs can never be PDF/A." },
                ValidationNote);
        }

        using (document)
        {
            var catalog = document.Internals.Catalog;

            // XMP metadata: look for pdfaid:part / pdfaid:conformance.
            string? part = null, conformance = null;
            if (catalog.Elements.GetObject("/Metadata") is PdfDictionary metadata && metadata.Stream != null)
            {
                var xmp = Encoding.UTF8.GetString(metadata.Stream.UnfilteredValue);
                part = Regex.Match(xmp, @"pdfaid:part(?:>|="")\s*(\d)").Groups[1].Value is { Length: > 0 } p ? p : null;
                conformance = Regex.Match(xmp, @"pdfaid:conformance(?:>|="")\s*([ABUabu])").Groups[1].Value is { Length: > 0 } c ? c.ToUpperInvariant() : null;
            }
            else
            {
                warnings.Add("No XMP metadata stream — PDF/A requires XMP with a pdfaid identification block.");
            }

            var hasOutputIntent = catalog.Elements.ContainsKey("/OutputIntents");
            if (!hasOutputIntent)
                warnings.Add("No OutputIntent — PDF/A requires a device-independent colour profile (e.g. sRGB).");

            var claims = part != null;
            if (!claims) warnings.Add("The document does not identify itself as PDF/A in XMP.");

            return new PdfAValidationReport(
                claims, part, conformance, hasOutputIntent,
                IsEncrypted: false,
                document.PageCount,
                document.Version.ToString(),
                warnings,
                ValidationNote);
        }
    }

    private const string ValidationNote =
        "Quick structural check (identification, OutputIntent, encryption). For certified archival submission, " +
        "run the file through a full ISO 19005 validator such as veraPDF.";

    /// <summary>
    /// Strip document metadata: clears the Info dictionary and removes the XMP
    /// metadata stream. Content is untouched.
    /// </summary>
    public byte[] StripMetadata(byte[] pdfBytes)
    {
        using var stream = new MemoryStream(pdfBytes);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);

        foreach (var key in new[] { "/Title", "/Author", "/Subject", "/Keywords", "/Creator", "/Producer" })
            document.Info.Elements.Remove(key);

        document.Internals.Catalog.Elements.Remove("/Metadata");

        using var output = new MemoryStream();
        document.Save(output);
        return output.ToArray();
    }

    private string GhostscriptPath()
    {
        var configured = _configuration["Tools:Ghostscript:Path"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        if (OperatingSystem.IsWindows())
        {
            var candidates = new[] { "gswin64c", "gswin64c.exe", "gs" };
            return candidates[0];
        }
        return "gs";
    }

    /// <summary>
    /// pdfmark prologue that attaches an sRGB OutputIntent — required for PDF/A.
    /// Uses Ghostscript's bundled srgb.icc when present on disk; recent Ghostscript
    /// also resolves it from its ROM file system via the iccprofiles path.
    /// </summary>
    private static (string Content, string? IccPath) BuildPdfADef()
    {
        string? iccFound = null;
        try
        {
            // Debian's ghostscript package installs it under /usr/share/color.
            var candidates = new List<string>();
            foreach (var root in new[] { "/usr/share/color", "/usr/share/ghostscript", "/usr/local/share/ghostscript" })
            {
                if (Directory.Exists(root))
                    candidates.AddRange(Directory.GetFiles(root, "srgb.icc", SearchOption.AllDirectories));
            }
            if (candidates.Count > 0) iccFound = candidates[0].Replace("\\", "/");
        }
        catch { /* fall back to ROM-resolved name */ }

        var iccPath = iccFound ?? "srgb.icc";
        var content =
            "%!\n" +
            "% PDF/A definition: sRGB OutputIntent.\n" +
            "[/_objdef {icc_PDFA} /type /stream /OBJ pdfmark\n" +
            "[{icc_PDFA} <</N 3>> /PUT pdfmark\n" +
            $"[{{icc_PDFA}} ({iccPath}) (r) file /PUT pdfmark\n" +
            "[/_objdef {OutputIntent_PDFA} /type /dict /OBJ pdfmark\n" +
            "[{OutputIntent_PDFA} <<\n" +
            "  /Type /OutputIntent /S /GTS_PDFA1\n" +
            "  /DestOutputProfile {icc_PDFA}\n" +
            "  /OutputConditionIdentifier (sRGB)\n" +
            ">> /PUT pdfmark\n" +
            "[{Catalog} <</OutputIntents [ {OutputIntent_PDFA} ]>> /PUT pdfmark\n";
        return (content, iccFound);
    }

    private static async Task RunToolAsync(string executable, string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stderr = await process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{executable} failed with exit code {process.ExitCode}. {Truncate(stderr + stdout, 600)}");
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
