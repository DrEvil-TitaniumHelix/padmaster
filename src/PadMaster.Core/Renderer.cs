using System.Text.Json.Nodes;
using SkiaSharp;

namespace PadMaster;

public static class Renderer
{
    const int BoxW = 180, LineH = 22, Pad = 8, BoxH = 2 * LineH + 2 * Pad;
    public const int BandH = 120, FootH = 26;
    public static readonly SKColor Accent = new(255, 0, 120), Band = new(34, 36, 42);
    public sealed record Header(string Title, string Platform, string State, bool Back, string? Logo, string Source, string Emulator)
    {
        public string Line2 => Platform + " · " + State + (Back ? " · back view" : "");
        public string Text => string.Join("|", Title, Platform, Source, Emulator, Logo);
    }
    public sealed record Label(string Key, float X, float Y, bool Right, SKRect Box, IReadOnlyList<string> Lines);
    public sealed record Layout(int Width, int Height, float OffsetX, float OffsetY, IReadOnlyList<Label> Labels);

    static JsonObject Load(string dir) => JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "anchors.json")))!.AsObject();
    static IEnumerable<KeyValuePair<string, JsonNode?>> Anchors(JsonObject doc, string view) => doc["anchors"]!.AsObject().Where(a => a.Value!["view"]!.GetValue<string>() == view);
    static float F(JsonNode? n) => n!.GetValue<float>();

    public static IReadOnlyList<string> Keys(string controllerDir, string view) => Anchors(Load(controllerDir), view).Select(a => a.Key).ToList();

    static SKFont Font(float size = 17, bool bold = true) => new(SKTypeface.FromFamilyName("Segoe UI", bold ? SKFontStyle.Bold : SKFontStyle.Normal), size);
    static SKFont Fit(string s, float size, float width) { var f = Font(size); while (f.Size > 18 && f.MeasureText(s) > width) f.Size -= 2; return f; }
    static Layout Measure(JsonObject doc, Dictionary<string, string> actions, string view, SKBitmap photo, SKFont font, Header? header)
    {
        // autoLabels (AI-built pads): the AI finds buttons well and places text badly, so boxes go in two columns beside the photo, by button height.
        var auto = doc["autoLabels"]?.GetValue<bool>() == true;
        var labels = Anchors(doc, view).Where(a => actions.ContainsKey(a.Key)).Select(a =>
        {
            var anchor = a.Value!; var right = auto ? F(anchor["x"]) < photo.Width / 2f : anchor["label"]!["align"]!.GetValue<string>() == "right";
            var x = auto ? (right ? -12 - BoxW : photo.Width + 12) : F(anchor["label"]!["x"]) - (right ? BoxW : 0); var y = auto ? F(anchor["y"]) : F(anchor["label"]!["y"]);
            var lines = Wrap(actions[a.Key], font, BoxW - 2 * Pad); var height = Math.Max(BoxH, lines.Count * LineH + 2 * Pad);
            return new Label(a.Key, F(anchor["x"]), F(anchor["y"]), right, new SKRect(x, y - height / 2f, x + BoxW, y + height / 2f), lines);
        }).ToList();
        foreach (var column in labels.GroupBy(a => (a.Right, a.Box.Left)).Select(g => g.OrderBy(a => a.Box.Top).ToArray()))
            for (var i = 1; i < column.Length; i++)
            {
                var gap = column[i - 1].Box.Bottom + 4 - column[i].Box.Top; if (gap <= 0) continue;
                var box = column[i].Box; labels[labels.IndexOf(column[i])] = column[i] = column[i] with { Box = new SKRect(box.Left, box.Top + gap, box.Right, box.Bottom + gap) };
            }
        var left = Math.Min(0, labels.Count == 0 ? 0 : labels.Min(a => a.Box.Left - 1)); var right = Math.Max(photo.Width, labels.Count == 0 ? 0 : labels.Max(a => a.Box.Right + 1));
        var top = Math.Min(0, labels.Count == 0 ? 0 : labels.Min(a => a.Box.Top - 1)); var bottom = Math.Max(photo.Height, labels.Count == 0 ? 0 : labels.Max(a => a.Box.Bottom + 1));
        var band = header == null ? 0 : BandH; top -= band;
        return new((int)Math.Ceiling(right - left), (int)Math.Ceiling(bottom - top) + (header == null ? 0 : FootH), -left, -top,
            labels.Select(a => a with { X = a.X - left, Y = a.Y - top, Box = new SKRect(a.Box.Left - left, a.Box.Top - top, a.Box.Right - left, a.Box.Bottom - top) }).ToArray());
    }
    public static Layout Measure(string controllerDir, Dictionary<string, string> actions, string view, Header? header = null)
    {
        var doc = Load(controllerDir);
        using var photo = SKBitmap.Decode(Path.Combine(controllerDir, doc["photos"]![view]!.GetValue<string>())) ?? throw new FileNotFoundException(view);
        using var font = Font(); return Measure(doc, actions, view, photo, font, header);
    }
    public static byte[] Render(string controllerDir, Dictionary<string, string> actions, string view, Header? header = null)
    {
        var doc = Load(controllerDir);
        using var photo = SKBitmap.Decode(Path.Combine(controllerDir, doc["photos"]![view]!.GetValue<string>())) ?? throw new FileNotFoundException(view);
        using var font = Font(); var layout = Measure(doc, actions, view, photo, font, header);
        using var surface = SKSurface.Create(new SKImageInfo(layout.Width, layout.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        c.Clear(SKColors.White);
        c.DrawBitmap(photo, layout.OffsetX, layout.OffsetY);
        using var stroke = new SKPaint { Color = new SKColor(40, 40, 40), StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var halo = new SKPaint { Color = SKColors.White, StrokeWidth = 6, IsAntialias = true, Style = SKPaintStyle.Stroke };   // the line stays visible on a dark photo
        using var fill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill };
        using var ring = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var dot = new SKPaint { Color = Accent, IsAntialias = false, Style = SKPaintStyle.Fill };
        using var text = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        if (header != null)
        {
            using var bandFill = new SKPaint { Color = Band, Style = SKPaintStyle.Fill }; c.DrawRect(0, 0, layout.Width, BandH, bandFill);
            using var white = new SKPaint { Color = SKColors.White, IsAntialias = true }; using var grey = new SKPaint { Color = new SKColor(200, 200, 205), IsAntialias = true }; using var dim = new SKPaint { Color = new SKColor(90, 90, 95), IsAntialias = true };
            var textRight = layout.Width - 16f;
            if (header.Logo != null && SKBitmap.Decode(header.Logo) is { } logo) using (logo)
            {
                var h = BandH - 16f; var w = Math.Min(logo.Width * h / logo.Height, layout.Width * .4f); h = w * logo.Height / logo.Width;
                var dest = new SKRect(textRight - w, (BandH - h) / 2, textRight, (BandH + h) / 2); c.DrawBitmap(logo, dest, new SKSamplingOptions(SKCubicResampler.Mitchell), text); textRight = dest.Left - 24;
            }
            using var title = Fit(header.Title, 36, textRight - 16); c.DrawText(header.Title, 16, 54, SKTextAlign.Left, title, white);
            using var sub = Fit(header.Line2, 20, textRight - 16); c.DrawText(header.Line2, 16, 90, SKTextAlign.Left, sub, grey);
            using var small = Font(13, false); var footer = string.Join("  ·  ", new[] { "Source: " + header.Source, doc["displayName"]?.GetValue<string>(), header.Emulator }.Where(s => !string.IsNullOrEmpty(s)));
            c.DrawText(footer, 16, layout.Height - 9, SKTextAlign.Left, small, dim);
        }
        foreach (var a in layout.Labels)
        {
            var rect = a.Box; var lines = a.Lines;
            c.DrawLine(a.X, a.Y, a.Right ? rect.Right : rect.Left, rect.MidY, halo);
            c.DrawLine(a.X, a.Y, a.Right ? rect.Right : rect.Left, rect.MidY, stroke);
            c.DrawRoundRect(rect, 6, 6, fill);
            c.DrawRoundRect(rect, 6, 6, stroke);
            var top = rect.Top + (rect.Height - lines.Count * LineH) / 2f;
            for (var i = 0; i < lines.Count; i++) c.DrawText(lines[i], rect.Left + Pad, top + LineH * (i + 1) - 6, SKTextAlign.Left, font, text);
        }
        foreach (var a in layout.Labels) { c.DrawCircle(a.X, a.Y, 11, ring); c.DrawCircle(a.X, a.Y, 8, dot); }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    static List<string> Wrap(string s, SKFont font, float width)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length == 0 || font.MeasureText(candidate) <= width) current = candidate;
            else { lines.Add(current); current = word; }
            while (font.MeasureText(current) > width && current.Length > 1)
            {
                var length = current.Length - 1; while (length > 1 && font.MeasureText(current[..length]) > width) length--;
                lines.Add(current[..length]); current = current[length..];
            }
        }
        lines.Add(current);
        return lines;
    }
}
