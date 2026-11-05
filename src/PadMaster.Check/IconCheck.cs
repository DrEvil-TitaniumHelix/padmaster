using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

static class IconCheck
{
    public static void Draw(string path)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(35, 36, 41)), null, new Rect(0, 0, 128, 128));
            var body = Geometry.Parse("M 36,38 C 25,38 22,47 18,62 L 12,87 C 9,102 22,107 32,96 L 46,81 L 82,81 L 96,96 C 106,107 119,102 116,87 L 110,62 C 106,47 103,38 92,38 Z");
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(225, 228, 235)), null, body);
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(35, 36, 41)), null, new Rect(29, 51, 10, 30), 2, 2);
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(35, 36, 41)), null, new Rect(19, 61, 30, 10), 2, 2);
            var button = new SolidColorBrush(Color.FromRgb(82, 150, 224));
            foreach (var point in new[] { new Point(92, 53), new Point(103, 64), new Point(81, 64), new Point(92, 75) }) drawing.DrawEllipse(button, null, point, 5, 5);
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(113, 119, 135)), null, new Rect(55, 59, 18, 5), 2, 2);
        }
        var bitmap = new RenderTargetBitmap(128, 128, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream); Console.WriteLine("OK  icon: 128x128 code-drawn gamepad on #232429");
    }
}
