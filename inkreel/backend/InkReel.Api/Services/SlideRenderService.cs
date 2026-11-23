using SkiaSharp;

namespace InkReel.Api.Services;

public record VideoStyle(string Id, string Label, string Blurb);
public record VideoPalette(string Id, string Label, string Primary, string Accent);

/// <summary>
/// Renders 1920x1080 slide PNGs with SkiaSharp — server-side, no browser.
/// Styles and palettes mirror the enhancement spec's gallery (White on Navy,
/// Hand-Drawn Education, Bauhaus Poster, …) in simplified, deterministic form:
/// each style is a layout/decoration recipe over the chosen brand palette.
/// </summary>
public class SlideRenderService
{
    public const int W = 1920;
    public const int H = 1080;

    public static readonly IReadOnlyList<VideoStyle> Styles = new List<VideoStyle>
    {
        new("white-on-navy",  "White on Navy",       "High-contrast headline slides on deep navy."),
        new("clean-light",    "Clean Light",          "White slides, crisp typography, thin rules."),
        new("hand-drawn",     "Hand-Drawn Education", "Warm paper look with marker-style accents."),
        new("bauhaus-poster", "Bauhaus Poster",       "Bold geometric shapes and poster type."),
        new("earthy-organic", "Earthy Organic",       "Soft cream background with rounded blocks."),
        new("duotone",        "Duotone Indigo",       "Two-tone panels with strong side band."),
    };

    public static readonly IReadOnlyList<VideoPalette> Palettes = new List<VideoPalette>
    {
        new("trust-blue",    "Trust Blue",    "#1d4ed8", "#38bdf8"),
        new("deep-navy",     "Deep Navy",     "#1e293b", "#60a5fa"),
        new("tech-purple",   "Tech Purple",   "#7c3aed", "#ec4899"),
        new("calm-teal",     "Calm Teal",     "#0d9488", "#f59e0b"),
        new("energy-orange", "Energy Orange", "#ea580c", "#facc15"),
        new("bold-red",      "Bold Red",      "#dc2626", "#0ea5e9"),
        new("royal-gold",    "Royal Gold",    "#a16207", "#1e293b"),
        new("ink-mono",      "Ink Mono",      "#111827", "#6b7280"),
    };

    public static (SKColor primary, SKColor accent) ResolvePalette(VideoRequest req)
    {
        if (req.PaletteId == "custom"
            && SKColor.TryParse(req.CustomPrimary ?? "", out var p)
            && SKColor.TryParse(req.CustomAccent ?? "", out var a))
            return (p, a);
        var pal = Palettes.FirstOrDefault(x => x.Id == req.PaletteId) ?? Palettes[0];
        return (SKColor.Parse(pal.Primary), SKColor.Parse(pal.Accent));
    }

    /// <summary>Render one scene slide to PNG bytes.</summary>
    public byte[] RenderScene(VideoRequest req, VideoScene scene, int index, int total)
    {
        var (primary, accent) = ResolvePalette(req);
        var dark = req.StyleId is "white-on-navy" or "duotone";
        var bg = req.StyleId switch
        {
            "white-on-navy" => Darken(primary, 0.72f),
            "duotone" => Darken(primary, 0.80f),
            "hand-drawn" => SKColor.Parse("#fdfaf3"),
            "earthy-organic" => SKColor.Parse("#faf6ef"),
            "bauhaus-poster" => SKColor.Parse("#f5f1e8"),
            _ => SKColors.White,
        };
        var ink = dark ? SKColors.White : SKColor.Parse("#0f172a");
        var sub = dark ? SKColor.Parse("#cbd5e1") : SKColor.Parse("#475569");

        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        canvas.Clear(bg);

        DrawStyleDecorations(canvas, req.StyleId, primary, accent, dark);

        var hasImage = scene.ImageIndex >= 0 && scene.ImageIndex < req.Images.Count;
        var textRight = hasImage ? W / 2 - 40 : W - 160;

        // Title
        using var titleFont = new SKFont(Typeface(req.StyleId, bold: true), scene.Bullets.Count == 0 && !hasImage ? 110 : 84);
        using var titlePaint = new SKPaint { Color = ink, IsAntialias = true };
        var titleLines = Wrap(scene.Title, titleFont, textRight - 160);
        float y = scene.Bullets.Count == 0 && !hasImage ? H / 2f - (titleLines.Count - 1) * 65 : 260;
        foreach (var line in titleLines)
        {
            canvas.DrawText(line, 160, y, SKTextAlign.Left, titleFont, titlePaint);
            y += titleFont.Size * 1.18f;
        }

        // Accent underline
        using var rule = new SKPaint { Color = accent, IsAntialias = true };
        canvas.DrawRoundRect(160, y - 44, 170, 12, 6, 6, rule);
        y += 60;

        // Bullets
        using var bulletFont = new SKFont(Typeface(req.StyleId, bold: false), 52);
        using var bulletPaint = new SKPaint { Color = sub, IsAntialias = true };
        using var dot = new SKPaint { Color = accent, IsAntialias = true };
        foreach (var b in scene.Bullets)
        {
            var lines = Wrap(b, bulletFont, textRight - 240);
            canvas.DrawCircle(185, y - 16, 10, dot);
            foreach (var line in lines)
            {
                canvas.DrawText(line, 225, y, SKTextAlign.Left, bulletFont, bulletPaint);
                y += bulletFont.Size * 1.35f;
            }
            y += 18;
        }

        // Document image panel (right half) — the "videos from the images in
        // the document" half of the enhancement.
        if (hasImage)
        {
            using var img = SKImage.FromEncodedData(req.Images[scene.ImageIndex]);
            if (img != null)
            {
                var panel = new SKRect(W / 2 + 20, 200, W - 120, H - 200);
                var scale = Math.Min(panel.Width / img.Width, panel.Height / img.Height);
                var dw = img.Width * scale;
                var dh = img.Height * scale;
                var dest = new SKRect(
                    panel.MidX - dw / 2, panel.MidY - dh / 2,
                    panel.MidX + dw / 2, panel.MidY + dh / 2);

                using var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(dark ? (byte)90 : (byte)40), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18) };
                canvas.DrawRoundRect(dest.Left + 10, dest.Top + 14, dest.Width, dest.Height, 18, 18, shadow);
                using var card = new SKPaint { Color = SKColors.White, IsAntialias = true };
                var cardRect = SKRect.Inflate(dest, 16, 16);
                canvas.DrawRoundRect(cardRect, 18, 18, card);
                canvas.Save();
                canvas.ClipRoundRect(new SKRoundRect(dest, 10), antialias: true);
                canvas.DrawImage(img, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                canvas.Restore();
                using var border = new SKPaint { Color = accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 };
                canvas.DrawRoundRect(cardRect, 18, 18, border);
            }
        }

        // Logo top-left (spec: "top-left corner of every slide")
        if (req.Logo is { Length: > 0 })
        {
            using var logo = SKImage.FromEncodedData(req.Logo);
            if (logo != null)
            {
                const float logoH = 84;
                var lw = logo.Width * (logoH / logo.Height);
                canvas.DrawImage(logo, new SKRect(70, 56, 70 + lw, 56 + logoH),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            }
        }

        // Progress footer
        using var footFont = new SKFont(Typeface(req.StyleId, bold: false), 34);
        using var footPaint = new SKPaint { Color = sub.WithAlpha(190), IsAntialias = true };
        canvas.DrawText($"{index + 1} / {total}", W - 200, H - 70, SKTextAlign.Left, footFont, footPaint);
        using var bar = new SKPaint { Color = accent.WithAlpha(dark ? (byte)230 : (byte)255), IsAntialias = true };
        canvas.DrawRect(0, H - 14, W * ((index + 1) / (float)total), 14, bar);

        using var snap = surface.Snapshot();
        using var data = snap.Encode(SKEncodedImageFormat.Png, 92);
        return data.ToArray();
    }

    /// <summary>Title card shown first (video title, style-dressed).</summary>
    public byte[] RenderTitleCard(VideoRequest req)
    {
        var scene = new VideoScene { Title = req.Title, Bullets = new(), Narration = "", ImageIndex = -1 };
        return RenderScene(req, scene, 0, 1);
    }

    private static void DrawStyleDecorations(SKCanvas canvas, string styleId, SKColor primary, SKColor accent, bool dark)
    {
        switch (styleId)
        {
            case "bauhaus-poster":
            {
                using var c1 = new SKPaint { Color = accent.WithAlpha(60), IsAntialias = true };
                using var c2 = new SKPaint { Color = primary.WithAlpha(50), IsAntialias = true };
                canvas.DrawCircle(W - 240, 220, 190, c1);
                canvas.DrawRect(W - 460, H - 420, 300, 300, c2);
                using var tri = new SKPaint { Color = accent.WithAlpha(85), IsAntialias = true };
                using var path = new SKPath();
                path.MoveTo(120, H - 110); path.LineTo(320, H - 110); path.LineTo(220, H - 300); path.Close();
                canvas.DrawPath(path, tri);
                break;
            }
            case "duotone":
            {
                using var band = new SKPaint { Color = accent.WithAlpha(40), IsAntialias = true };
                canvas.DrawRect(0, 0, 90, H, band);
                using var edge = new SKPaint { Color = accent, IsAntialias = true };
                canvas.DrawRect(90, 0, 10, H, edge);
                break;
            }
            case "hand-drawn":
            {
                // Paper rule lines, slightly irregular.
                using var line = new SKPaint { Color = SKColor.Parse("#e8e0cf"), IsAntialias = true, StrokeWidth = 2 };
                for (var ly = 180; ly < H - 100; ly += 64)
                    canvas.DrawLine(120, ly + (ly % 128 == 0 ? 2 : 0), W - 120, ly, line);
                using var margin = new SKPaint { Color = SKColor.Parse("#f3b6b6"), IsAntialias = true, StrokeWidth = 3 };
                canvas.DrawLine(150, 120, 150, H - 90, margin);
                break;
            }
            case "earthy-organic":
            {
                using var blob = new SKPaint { Color = accent.WithAlpha(34), IsAntialias = true };
                canvas.DrawOval(new SKRect(W - 600, -180, W + 200, 400), blob);
                using var blob2 = new SKPaint { Color = primary.WithAlpha(26), IsAntialias = true };
                canvas.DrawOval(new SKRect(-250, H - 380, 420, H + 240), blob2);
                break;
            }
            case "clean-light":
            {
                using var top = new SKPaint { Color = primary, IsAntialias = true };
                canvas.DrawRect(0, 0, W, 16, top);
                break;
            }
            case "white-on-navy":
            {
                using var glow = new SKPaint { Color = accent.WithAlpha(26), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 120) };
                canvas.DrawCircle(W - 260, 180, 280, glow);
                break;
            }
        }
    }

    private static SKTypeface Typeface(string styleId, bool bold)
    {
        // Hand-drawn style prefers a casual face when the host has one; every
        // path falls back to the default sans so rendering never fails.
        if (styleId == "hand-drawn")
        {
            var t = SKTypeface.FromFamilyName("Segoe Print",
                bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
            if (t != null && !string.Equals(t.FamilyName, SKTypeface.Default.FamilyName, StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return SKTypeface.FromFamilyName("Segoe UI",
                   bold ? SKFontStyleWeight.ExtraBold : SKFontStyleWeight.Normal,
                   SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
               ?? SKTypeface.Default;
    }

    private static List<string> Wrap(string text, SKFont font, float maxWidth)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var current = "";
        foreach (var w in words)
        {
            var probe = current.Length == 0 ? w : current + " " + w;
            if (font.MeasureText(probe) > maxWidth && current.Length > 0)
            {
                lines.Add(current);
                current = w;
            }
            else current = probe;
        }
        if (current.Length > 0) lines.Add(current);
        return lines.Count == 0 ? new List<string> { text } : lines;
    }

    private static SKColor Darken(SKColor c, float amount) =>
        new((byte)(c.Red * (1 - amount)), (byte)(c.Green * (1 - amount)), (byte)(c.Blue * (1 - amount)));
}
