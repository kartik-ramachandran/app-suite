namespace PdfAI.API.Models;

/// <summary>
/// Describes the page numbers, header and footer to stamp onto a finished PDF.
/// The same options object is used both by the standalone overlay tool and (later)
/// as an optional post-conversion pass on the convert-to-PDF endpoints.
///
/// Text fields support tokens that are resolved per page:
///   {page}  {pages}  {date}  {time}  {filename}  {title}
/// </summary>
public class PdfOverlayOptions
{
    public PageNumberOptions? PageNumbers { get; set; }
    public HeaderFooterOptions? Header { get; set; }
    public HeaderFooterOptions? Footer { get; set; }

    /// <summary>Distance (in points) of the header/footer band from the page edge. 1 inch = 72 pt.</summary>
    public double MarginPoints { get; set; } = 36;

    /// <summary>Optional document title used to resolve the {title} token. Falls back to the file name.</summary>
    public string? Title { get; set; }
}

public class PageNumberOptions
{
    public bool Enabled { get; set; }

    /// <summary>Template using {page}/{pages} tokens, e.g. "Page {page} of {pages}".</summary>
    public string Format { get; set; } = "{page} / {pages}";

    /// <summary>One of: top-left, top-center, top-right, bottom-left, bottom-center, bottom-right.</summary>
    public string Position { get; set; } = "bottom-center";

    /// <summary>The number printed on the first counted page (lets you start at 0, 5, etc.).</summary>
    public int StartAt { get; set; } = 1;

    /// <summary>When true the first page is left blank (e.g. a cover page) but counting still advances.</summary>
    public bool SkipFirstPage { get; set; }

    public string FontFamily { get; set; } = "Arial";
    public double FontSize { get; set; } = 10;
    public bool Bold { get; set; }
    public string Color { get; set; } = "#333333";
}

public class HeaderFooterOptions
{
    public bool Enabled { get; set; }

    public OverlaySegment? Left { get; set; }
    public OverlaySegment? Center { get; set; }
    public OverlaySegment? Right { get; set; }

    /// <summary>When true the header/footer is omitted from the first page.</summary>
    public bool SkipFirstPage { get; set; }

    public string FontFamily { get; set; } = "Arial";
    public double FontSize { get; set; } = 10;
    public bool Bold { get; set; }
    public string Color { get; set; } = "#333333";
}

/// <summary>
/// One slot of a header/footer (left, center or right). A slot may carry text, an
/// uploaded logo image, or both (image is drawn first, text follows beside it).
/// </summary>
public class OverlaySegment
{
    /// <summary>Static or token text. Resolved per page.</summary>
    public string? Text { get; set; }

    /// <summary>Zero-based index into the uploaded images list, or null for no image.</summary>
    public int? ImageIndex { get; set; }

    /// <summary>Maximum drawn height of the image in points; width scales to preserve aspect ratio.</summary>
    public double ImageMaxHeight { get; set; } = 28;
}
