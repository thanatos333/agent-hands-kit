using System.Drawing;
using System.Drawing.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace DesktopAgent;

/// <summary>Screen text search via the built-in Windows OCR engine
/// (Windows.Media.Ocr). Needs the OCR language pack for the requested
/// language; without it TryCreateFromLanguage returns null and we say so.</summary>
internal static class OcrOps
{
    public sealed record TextMatch(int X, int Y, int W, int H, string Line);

    private static string StripSpaces(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    public static TextMatch? FindText(Bitmap bmp, string query, string? lang)
    {
        // Bitmap -> SoftwareBitmap (PNG round-trip through a WinRT stream).
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        var ras = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(ras))
        {
            writer.WriteBytes(ms.ToArray());
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }
        ras.Seek(0);
        var decoder = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
        var soft = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();

        OcrEngine? engine = null;
        if (!string.IsNullOrWhiteSpace(lang))
            engine = OcrEngine.TryCreateFromLanguage(new Language(lang));
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null)
            throw new InvalidOperationException(
                $"no OCR engine available{(lang != null ? $" for language '{lang}'" : "")}; install the Windows OCR language pack for that language");

        var result = engine.RecognizeAsync(soft).AsTask().GetAwaiter().GetResult();
        string needle = StripSpaces(query);
        foreach (var line in result.Lines)
        {
            string hay = StripSpaces(line.Text);
            if (hay.Length == 0 || !hay.Contains(needle, StringComparison.OrdinalIgnoreCase)) continue;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = 0, y1 = 0;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                x0 = Math.Min(x0, r.X); y0 = Math.Min(y0, r.Y);
                x1 = Math.Max(x1, r.X + r.Width); y1 = Math.Max(y1, r.Y + r.Height);
            }
            if (x1 <= x0) continue;
            return new TextMatch((int)x0, (int)y0, (int)(x1 - x0), (int)(y1 - y0), line.Text);
        }
        return null;
    }
}
