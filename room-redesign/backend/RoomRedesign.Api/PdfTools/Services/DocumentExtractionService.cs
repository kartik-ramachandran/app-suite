using System.Text;
using UglyToad.PdfPig;

namespace PdfAI.API.Services;

public class DocumentExtractionService
{
    public string ExtractText(byte[] pdfBytes, int maxCharacters = 12000)
    {
        var builder = new StringBuilder();

        using var document = PdfDocument.Open(pdfBytes);
        foreach (var page in document.GetPages())
        {
            builder.AppendLine($"Page {page.Number}");
            builder.AppendLine(page.Text);
            builder.AppendLine();

            if (builder.Length >= maxCharacters)
            {
                break;
            }
        }

        return builder.Length > maxCharacters
            ? builder.ToString(0, maxCharacters)
            : builder.ToString();
    }

    public int GetPageCount(byte[] pdfBytes)
    {
        using var document = PdfDocument.Open(pdfBytes);
        return document.NumberOfPages;
    }
}
