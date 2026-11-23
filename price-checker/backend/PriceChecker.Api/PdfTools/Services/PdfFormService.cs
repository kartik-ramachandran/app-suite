using PdfSharp.Pdf;
using PdfSharp.Pdf.AcroForms;
using PdfSharp.Pdf.IO;

namespace PdfAI.API.Services;

/// <summary>
/// AcroForm support for the Snappdf API: inspect a PDF's form fields and fill
/// them from a flat name→value map. Flattening is handled by the caller via a
/// rasterize round-trip (PdfSharp 6.1 has no appearance-stream flatten).
/// </summary>
public class PdfFormService
{
    private readonly ILogger<PdfFormService> _logger;

    public PdfFormService(ILogger<PdfFormService> logger) => _logger = logger;

    public record FormFieldInfo(string Name, string Type, string? Value, IReadOnlyList<string> Options, bool ReadOnly);

    /// <summary>Enumerate every fillable field with its type, current value and options.</summary>
    public List<FormFieldInfo> ListFields(byte[] pdfBytes)
    {
        using var stream = new MemoryStream(pdfBytes);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.ReadOnly);
        var result = new List<FormFieldInfo>();
        // PdfSharp 6.1's AcroForm getter THROWS on documents without a form.
        if (!document.Internals.Catalog.Elements.ContainsKey("/AcroForm")) return result;
        var form = document.AcroForm;
        if (form == null) return result;

        foreach (var (name, field) in EnumerateFields(form.Fields, prefix: null))
        {
            result.Add(new FormFieldInfo(
                name,
                FieldType(field),
                CurrentValue(field),
                FieldOptions(field),
                field.ReadOnly));
        }
        return result;
    }

    /// <summary>
    /// Fill fields by (qualified) name. Unknown names are reported back rather
    /// than failing the batch — callers fill from JSON and want per-field outcomes.
    /// </summary>
    public (byte[] Pdf, List<string> Filled, List<string> Skipped) FillForm(
        byte[] pdfBytes, IReadOnlyDictionary<string, string> values)
    {
        using var stream = new MemoryStream(pdfBytes);
        using var document = PdfReader.Open(stream, PdfDocumentOpenMode.Modify);
        if (!document.Internals.Catalog.Elements.ContainsKey("/AcroForm"))
            throw new InvalidOperationException("This PDF has no fillable form fields (AcroForm).");
        var form = document.AcroForm
            ?? throw new InvalidOperationException("This PDF has no fillable form fields (AcroForm).");

        // Viewers regenerate widget appearances from /V when this flag is set —
        // without it, values filled here can look blank in some readers.
        if (form.Elements.ContainsKey("/NeedAppearances"))
            form.Elements.SetBoolean("/NeedAppearances", true);
        else
            form.Elements.Add("/NeedAppearances", new PdfBoolean(true));

        var fields = EnumerateFields(form.Fields, prefix: null).ToList();
        var filled = new List<string>();
        var skipped = new List<string>();

        foreach (var (name, raw) in values)
        {
            var match = fields.FirstOrDefault(f => f.Name == name).Field
                     ?? fields.FirstOrDefault(f =>
                            f.Field.Name == name ||
                            f.Name.EndsWith("." + name, StringComparison.Ordinal)).Field;
            if (match == null) { skipped.Add(name); continue; }

            try
            {
                if (SetFieldValue(match, raw ?? "")) filled.Add(name);
                else skipped.Add(name);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not fill field {Field}", name);
                skipped.Add(name);
            }
        }

        using var output = new MemoryStream();
        document.Save(output);
        return (output.ToArray(), filled, skipped);
    }

    private bool SetFieldValue(PdfAcroField field, string raw)
    {
        switch (field)
        {
            case PdfTextField text:
                text.Text = raw;
                return true;

            case PdfCheckBoxField check:
                check.Checked = IsTruthy(raw);
                return true;

            case PdfRadioButtonField radio:
            {
                var options = FieldOptions(radio);
                var idx = options.FindIndex(o => string.Equals(o, raw, StringComparison.OrdinalIgnoreCase));
                if (idx < 0 && int.TryParse(raw, out var parsed) && parsed >= 0 && parsed < Math.Max(options.Count, 1)) idx = parsed;
                if (idx < 0) return false;
                radio.SelectedIndex = idx;
                return true;
            }

            case PdfComboBoxField combo:
                return SetChoiceValue(combo, i => combo.SelectedIndex = i, raw);

            case PdfListBoxField list:
                return SetChoiceValue(list, i => list.SelectedIndex = i, raw);

            default:
                return false; // push buttons, signatures — not fillable here
        }
    }

    private bool SetChoiceValue(PdfChoiceField choice, Action<int> setIndex, string raw)
    {
        var options = FieldOptions(choice);
        var idx = options.FindIndex(o => string.Equals(o, raw, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 && int.TryParse(raw, out var parsed) && parsed >= 0 && parsed < options.Count) idx = parsed;
        if (idx >= 0)
        {
            setIndex(idx);
            return true;
        }
        // Combo boxes may allow free text; store it as the value directly.
        choice.Value = new PdfString(raw);
        return true;
    }

    private static bool IsTruthy(string raw) =>
        raw.Trim().ToLowerInvariant() is "true" or "yes" or "on" or "1" or "checked" or "x";

    private static string FieldType(PdfAcroField field) => field switch
    {
        PdfTextField => "text",
        PdfCheckBoxField => "checkbox",
        PdfRadioButtonField => "radio",
        PdfComboBoxField => "combobox",
        PdfListBoxField => "listbox",
        PdfSignatureField => "signature",
        PdfPushButtonField => "button",
        _ => "unknown",
    };

    private static string? CurrentValue(PdfAcroField field) => field switch
    {
        PdfTextField text => text.Text,
        PdfCheckBoxField check => check.Checked ? "true" : "false",
        _ => field.Value?.ToString()?.Trim('(', ')', '/'),
    };

    /// <summary>
    /// Options for choice fields come from the /Opt array (strings or
    /// [export, display] pairs — we return the export value). Radio groups get
    /// theirs from the kids' appearance-state names (minus "Off").
    /// </summary>
    private List<string> FieldOptions(PdfAcroField field)
    {
        var options = new List<string>();
        try
        {
            if (field is PdfRadioButtonField)
            {
                foreach (var name in field.GetAppearanceNames())
                {
                    var clean = name.TrimStart('/');
                    if (!string.IsNullOrEmpty(clean) && !clean.Equals("Off", StringComparison.OrdinalIgnoreCase) && !options.Contains(clean))
                        options.Add(clean);
                }
                return options;
            }

            if (field.Elements["/Opt"] is PdfArray opt)
            {
                foreach (var item in opt.Elements)
                {
                    switch (item)
                    {
                        case PdfString s:
                            options.Add(s.Value);
                            break;
                        case PdfArray pair when pair.Elements.Count > 0 && pair.Elements[0] is PdfString exportValue:
                            options.Add(exportValue.Value);
                            break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read options for field {Field}", field.Name);
        }
        return options;
    }

    /// <summary>Depth-first walk of the field tree, yielding qualified names for terminal fields.</summary>
    private static IEnumerable<(string Name, PdfAcroField Field)> EnumerateFields(
        PdfAcroField.PdfAcroFieldCollection fields, string? prefix)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            PdfAcroField? field;
            try { field = fields[i]; }
            catch { continue; }
            if (field == null) continue;

            var name = string.IsNullOrEmpty(prefix) ? field.Name : $"{prefix}.{field.Name}";

            // Radio groups have kids (one per button) but are filled as one field.
            if (field.HasKids && field is not PdfRadioButtonField && field.Fields.Count > 0)
            {
                foreach (var child in EnumerateFields(field.Fields, name)) yield return child;
            }
            else
            {
                yield return (name, field);
            }
        }
    }
}
