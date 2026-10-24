using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace GameDub.Pipeline;

public static class ScreenCapture
{
    public static Bitmap Capture(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>
    /// Cheap signature of bright (text-like) pixels on a coarse grid. Moving game backgrounds
    /// change it much less than the raw image, so OCR runs only when the text likely changed.
    /// </summary>
    public static byte[] Signature(Bitmap bmp, int cols = 64, int rows = 12)
    {
        var sig = new byte[cols * rows];
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[data.Stride];
            int cw = Math.Max(1, bmp.Width / cols), ch = Math.Max(1, bmp.Height / rows);
            for (int y = 0; y < bmp.Height; y += 2)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                int cy = Math.Min(rows - 1, y / ch);
                for (int x = 0; x < bmp.Width; x += 2)
                {
                    int i = x * 4;
                    int max = Math.Max(row[i], Math.Max(row[i + 1], row[i + 2]));
                    if (max > 200)
                    {
                        int c = cy * cols + Math.Min(cols - 1, x / cw);
                        if (sig[c] < 255) sig[c]++;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return sig;
    }

    public static double Diff(byte[]? a, byte[]? b)
    {
        if (a == null || b == null || a.Length != b.Length) return double.MaxValue;
        long sum = 0;
        for (int i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }
}

public sealed record OcrLine(string Text, Rectangle Box);

public sealed class OcrService
{
    readonly OcrEngine _engine;

    public OcrService()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Language("en-US"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages()
                  ?? throw new InvalidOperationException("Windows OCR (en-US) is not available");
    }

    /// <summary>
    /// Subtitles are almost always light text with a dark outline or shadow. Keeping only
    /// light pixels (as dark text on white) removes busy game backgrounds before OCR.
    /// </summary>
    public bool Binarize { get; set; } = true;

    public async Task<List<OcrLine>> RecognizeAsync(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] bytes;
        try
        {
            bytes = new byte[bmp.Width * bmp.Height * 4];
            for (int y = 0; y < bmp.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * bmp.Width * 4, bmp.Width * 4);
        }
        finally { bmp.UnlockBits(data); }

        if (Binarize)
        {
            for (int i = 0; i < bytes.Length; i += 4)
            {
                int lum = (bytes[i] * 29 + bytes[i + 1] * 150 + bytes[i + 2] * 77) >> 8;
                byte v = lum > 175 ? (byte)0 : (byte)255;
                bytes[i] = bytes[i + 1] = bytes[i + 2] = v;
                bytes[i + 3] = 255;
            }
        }

        using var sb = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, bmp.Width, bmp.Height, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(sb);
        var lines = new List<OcrLine>();
        foreach (var l in result.Lines)
        {
            if (l.Words.Count == 0) continue;
            double x0 = l.Words.Min(w => w.BoundingRect.X), y0 = l.Words.Min(w => w.BoundingRect.Y);
            double x1 = l.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width), y1 = l.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            lines.Add(new OcrLine(l.Text, Rectangle.FromLTRB((int)x0, (int)y0, (int)x1, (int)y1)));
        }
        return lines;
    }
}

public static class TextTools
{
    /// <summary>Joins OCR lines and drops obvious garbage (HUD numbers, single glyphs).</summary>
    public static string Clean(IEnumerable<OcrLine> lines)
    {
        var parts = new List<string>();
        foreach (var l in lines)
        {
            var t = l.Text.Replace('|', 'I').Replace('’', '\'').Replace('“', '"').Replace('”', '"').Trim();
            int letters = t.Count(char.IsLetter);
            if (letters < 3 || letters < t.Length * 0.5) continue;
            parts.Add(t);
        }
        var s = string.Join(" ", parts);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s.TrimStart('-', '–', '—', '.', ',', '\'', '"', ' ', '_', '~', '*');
    }

    /// <summary>
    /// "WE HAVE TO GO. NOW!" → "We have to go. Now!" — all-caps subtitles translate and
    /// speak badly. Mixed-case text is returned unchanged.
    /// </summary>
    public static string FixCaps(string s)
    {
        int letters = s.Count(char.IsLetter), upper = s.Count(char.IsUpper);
        if (letters < 4 || upper < letters * 0.8) return s;
        var sb = new StringBuilder(s.ToLowerInvariant());
        bool start = true;
        for (int i = 0; i < sb.Length; i++)
        {
            char c = sb[i];
            if (char.IsLetter(c))
            {
                if (start) sb[i] = char.ToUpperInvariant(c);
                start = false;
            }
            else if (c is '.' or '!' or '?' or ':') start = true;
        }
        var res = sb.ToString();
        return System.Text.RegularExpressions.Regex.Replace(res, @"\bi\b", "I"); // English "I"
    }

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>1.0 = identical, based on Levenshtein distance of normalized text.</summary>
    public static double Similarity(string a, string b)
    {
        a = Normalize(a); b = Normalize(b);
        if (a.Length == 0 && b.Length == 0) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Length > 400) a = a[..400];
        if (b.Length > 400) b = b[..400];
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }
}

/// <summary>
/// Emits a subtitle once it has been read identically twice in a row (filters typewriter
/// animations and OCR flicker) and was not spoken already.
/// </summary>
public sealed class SubtitleTracker
{
    string _candidate = "";
    int _seen;
    string _spoken = "";
    long _lastTextAt;

    public string? Push(string text)
    {
        long now = Environment.TickCount64;
        if (text.Length == 0)
        {
            _candidate = ""; _seen = 0;
            if (now - _lastTextAt > 8000) _spoken = ""; // allow an identical line again after a long gap
            return null;
        }
        _lastTextAt = now;
        if (TextTools.Similarity(text, _candidate) >= 0.85) _seen++;
        else { _candidate = text; _seen = 1; }

        if (_seen != 2 || TextTools.Similarity(_candidate, _spoken) >= 0.8) return null;
        _spoken = _candidate;
        return _candidate;
    }
}

/// <summary>Finds the subtitle band: centered multi-word text in the lower part of the screen.</summary>
public sealed class RegionDetector
{
    const double SearchTop = 0.55;
    readonly List<Rectangle> _hits = new();

    public bool HasHits => _hits.Count > 0;

    public static Rectangle SearchArea(Rectangle client) =>
        new(client.Left, client.Top + (int)(client.Height * SearchTop), client.Width, (int)(client.Height * (1 - SearchTop)));

    /// <summary>Feeds OCR lines found in <see cref="SearchArea"/>; returns a region once confident.</summary>
    public Core.RelRect? Feed(List<OcrLine> lines, Rectangle client)
    {
        int areaTop = (int)(client.Height * SearchTop);
        var good = lines.Where(l =>
        {
            int words = l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            double cx = (l.Box.Left + l.Box.Right) / 2.0;
            return words >= 3 && Math.Abs(cx - client.Width / 2.0) < client.Width * 0.18
                   && l.Text.Count(char.IsLetter) >= l.Text.Length * 0.6;
        }).ToList();
        if (good.Count == 0) return null;

        var box = good.Select(l => l.Box).Aggregate(Rectangle.Union);
        box.Offset(0, areaTop);
        _hits.Add(box);
        if (_hits.Count < 2) return null;

        var u = _hits.Aggregate(Rectangle.Union);
        int lineH = Math.Max(20, good.Max(l => l.Box.Height));
        double top = Math.Max(0, u.Top - lineH * 1.6), bottom = Math.Min(client.Height, u.Bottom + lineH * 0.8);
        // Later lines are often wider than the first ones seen, so keep the band wide.
        double halfW = Math.Max(u.Width / 2.0 + lineH * 2, client.Width * 0.42);
        var r = new Core.RelRect
        {
            X = (client.Width / 2.0 - halfW) / client.Width,
            W = halfW * 2 / client.Width,
            Y = top / client.Height,
            H = (bottom - top) / client.Height,
        };
        r.Clamp();
        return r;
    }

    public static Core.RelRect Default() => new() { X = 0.15, Y = 0.76, W = 0.7, H = 0.2 };
}
