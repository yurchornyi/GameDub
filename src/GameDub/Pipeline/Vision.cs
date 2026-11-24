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

    /// <summary>The largest change of one cell: a short line in a wide band changes a few cells a lot, and the average
    /// over all of them stayed under the bar (24.11: new subtitles in Dead Island 2 were not read at all).</summary>
    public static int MaxDiff(byte[]? a, byte[]? b)
    {
        if (a == null || b == null || a.Length != b.Length) return int.MaxValue;
        int m = 0;
        for (int i = 0; i < a.Length; i++) m = Math.Max(m, Math.Abs(a[i] - b[i]));
        return m;
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

    public OcrService(string subLang = "en")
    {
        // the console lists Russian as «ru», not «ru-RU»: take the installed recognizer whose tag starts with the language
        string want = subLang == "ru" ? "ru" : "en";
        var lang = OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith(want, StringComparison.OrdinalIgnoreCase))
                   ?? new Language(subLang == "ru" ? "ru-RU" : "en-US");
        // No fallback to another language: reading Cyrillic with the English engine gives garbage.
        _engine = OcrEngine.TryCreateFromLanguage(lang)
                  ?? throw new InvalidOperationException($"Windows OCR ({lang.LanguageTag}) is not available");
    }

    public static bool IsAvailable(string subLang) =>
        OcrEngine.AvailableRecognizerLanguages.Any(l => l.LanguageTag.StartsWith(subLang == "ru" ? "ru" : "en", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Subtitles are almost always light text with a dark outline or shadow. Keeping only light pixels (as dark text on
    /// white) removes busy game backgrounds before OCR. A speaker's name in orange or red is not light by its gray level
    /// and was lost (names came out as junk, «Xaæa», 23.11), so a second read also keeps bright strong colors; its name
    /// («Marcus:») goes before the first read's text when the rest of both agree. A junk-looking first read is checked
    /// against the picture as it is. Tried on the console (23.11) on outlined 24 px text over a busy background, English
    /// and Russian: reading it enlarged 2-3 times, or keeping every bright channel, misread more letters.
    /// </summary>
    public bool Binarize { get; set; } = true;

    public async Task<List<OcrLine>> RecognizeAsync(Bitmap bmp)
    {
        int scale = bmp.Height < 50 ? 2 : 1;   // only a very low strip (one small line) is enlarged
        while (scale > 1 && Math.Max(bmp.Width, bmp.Height) * scale > OcrEngine.MaxImageDimension) scale--;
        int w = bmp.Width * scale, h = bmp.Height * scale;
        byte[] bytes;
        using (var big = scale == 1 ? null : new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            if (big != null)
                using (var g = Graphics.FromImage(big))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(bmp, new Rectangle(0, 0, w, h), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
                }
            var src = big ?? bmp;
            var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                bytes = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * w * 4, w * 4);
            }
            finally { src.UnlockBits(data); }
        }
        if (!Binarize) return await Read(bytes, w, h, scale);

        var light = await Read(Mask(bytes, colors: false), w, h, scale);
        var (good, bad) = Quality(light);
        if (bad > 0)
        {
            var plain = await Read(bytes, w, h, scale);
            var (good2, bad2) = Quality(plain);
            if (good2 - 2 * bad2 > good - 2 * bad) { light = plain; (good, bad) = (good2, bad2); }
        }
        if (light.Count == 0) return light;
        // the speaker's name, when it is colored
        var colored = await Read(Mask(bytes, colors: true), w, h, scale);
        string text = string.Join(" ", light.Select(l => l.Text)), all = string.Join(" ", colored.Select(l => l.Text));
        int colon = all.IndexOf(':');
        if (colon < 2 || colon > 30 || text.Contains(':')) return light;
        string name = all[..colon].Trim(), rest = all[(colon + 1)..].Trim();
        if (!name.All(c => char.IsLetter(c) || c is ' ' or '-' or '\'' or '.') || TextTools.Similarity(rest, text) < 0.85) return light;
        var box = colored.Select(l => l.Box).Concat(light.Select(l => l.Box)).Aggregate(Rectangle.Union);
        return new List<OcrLine> { new($"{name}: {text}", box) };
    }

    /// <summary>Light pixels as black on white; with colors, bright strong colors too (an orange or red name).</summary>
    static byte[] Mask(byte[] bytes, bool colors)
    {
        var m = new byte[bytes.Length];
        for (int i = 0; i < m.Length; i += 4)
        {
            int b = bytes[i], g = bytes[i + 1], r = bytes[i + 2];
            int lum = (b * 29 + g * 150 + r * 77) >> 8;
            int max = Math.Max(b, Math.Max(g, r)), min = Math.Min(b, Math.Min(g, r));
            bool on = lum > 175 || colors && max > 230 && max - min > 150;
            m[i] = m[i + 1] = m[i + 2] = on ? (byte)0 : (byte)255;
            m[i + 3] = 255;
        }
        return m;
    }

    async Task<List<OcrLine>> Read(byte[] bytes, int w, int h, int scale)
    {
        using var sb = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
        var result = await _engine.RecognizeAsync(sb);
        var lines = new List<OcrLine>();
        foreach (var l in result.Lines)
        {
            if (l.Words.Count == 0) continue;
            double x0 = l.Words.Min(w => w.BoundingRect.X), y0 = l.Words.Min(w => w.BoundingRect.Y);
            double x1 = l.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width), y1 = l.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
            lines.Add(new OcrLine(l.Text, Rectangle.FromLTRB((int)(x0 / scale), (int)(y0 / scale), (int)Math.Ceiling(x1 / scale), (int)Math.Ceiling(y1 / scale))));
        }
        return lines;
    }

    /// <summary>Letters in words that look like words, and letters in junk (letters mixed with digits or odd signs, lone
    /// glyphs).</summary>
    public static (int good, int bad) Quality(IEnumerable<OcrLine> lines)
    {
        int good = 0, bad = 0;
        foreach (var l in lines)
            foreach (var raw in l.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var word = raw.Trim('.', ',', '!', '?', ':', ';', '"', '\'', '«', '»', '(', ')', '-', '—', '…');
                if (word.Length == 0) continue;
                int letters = word.Count(char.IsLetter);
                bool clean = word.All(c => char.IsLetter(c) || c is '\'' or '-' or '’');
                if (clean && (letters >= 2 || "IiAaяЯвВиИсСкКуУоОаА".Contains(word))) good += letters;
                else bad += Math.Max(1, word.Length);
            }
        return (good, bad);
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
            var t = FixLookalikes(l.Text.Replace('’', '\'').Replace('“', '"').Replace('”', '"').Trim());
            if (!t.Any(c => c is >= 'Ѐ' and <= 'ӿ')) t = t.Replace('|', 'I'); // a misread capital I in Latin text
            int letters = t.Count(char.IsLetter);
            if (letters < 3 || letters < t.Length * 0.5) continue;
            parts.Add(t);
        }
        var s = string.Join(" ", parts);
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return s.TrimStart('-', '–', '—', '.', ',', '\'', '"', ' ', '_', '~', '*');
    }

    static readonly System.Text.RegularExpressions.Regex NameColon =
        new(@"^([\p{L}][\p{L}'’\-]*(?:\s[\p{L}][\p{L}'’\-]*)?)\s*[:：]\s*(.*)$");
    // sound and stage notes, not speech: [стонет], (взрывы), *смеётся*, ♪ музыка ♪ (owner, 24.11: «вибухи не треба озвучувати»)
    static readonly System.Text.RegularExpressions.Regex StageNote =
        new(@"\[[^\]]*\]|\[[^\]]*$|\([^)]*\)|\([^)]*$|\*[^*]*\*|[♪♫][^♪♫]*[♪♫]?");

    /// <summary>
    /// The box's lines, top to bottom, as what each speaker says: a line that starts with a name and a colon starts a new
    /// one («джеикоб:» too, read without its capital), a line without one goes on the one before. Stage notes in square
    /// brackets («[стонет]»), round brackets, asterisks or music notes are not said; junk lines are dropped.
    /// </summary>
    public static List<string> Utterances(IEnumerable<OcrLine> lines)
    {
        var said = new List<(string? name, string text)>();
        foreach (var l in lines.OrderBy(l => l.Box.Top))
        {
            var t = FixLookalikes(l.Text.Replace('’', '\'').Replace('“', '"').Replace('”', '"').Trim());
            if (!t.Any(c => c is >= 'Ѐ' and <= 'ӿ')) t = t.Replace('|', 'I');
            int letters = t.Count(char.IsLetter);
            if (letters < 2 || letters < t.Length * 0.5) continue;
            // a label of the game's own (ВЕРНУТЬСЯ В ГЛАВНОЕ МЕНЮ): all capitals, no name, no sentence end
            bool caps = t.Count(char.IsUpper) >= letters * 0.8, ends = ".!?…".Contains(t.TrimEnd('"', '»', '\'', ' ').LastOrDefault());
            var m = NameColon.Match(t);
            if (caps && !ends && !(m.Success && m.Groups[1].Length <= 24)) continue;
            if (m.Success && m.Groups[1].Length <= 24)
            {
                var name = m.Groups[1].Value;
                said.Add((char.ToUpperInvariant(name[0]) + name[1..], m.Groups[2].Value));
            }
            else if (said.Count > 0) said[^1] = (said[^1].name, said[^1].text + " " + t);
            else said.Add((null, t));
        }
        var result = new List<string>();
        foreach (var (name, text) in said)
        {
            var body = System.Text.RegularExpressions.Regex.Replace(StageNote.Replace(text, " "), @"\s+", " ").Trim()
                .TrimStart('-', '–', '—', '.', ',', '\'', '"', ' ', '_', '~', '*');
            if (body.Count(char.IsLetter) < 2) continue;
            result.Add(name != null ? $"{name}: {body}" : body);
        }
        return result;
    }

    const string Latin = "ABCEHKMOPTXaceopxy", Cyrillic = "АВСЕНКМОРТХасеорху";

    /// <summary>The Russian engine writes Latin letters that look Cyrillic inside Cyrillic words («Bce» for «Все»), and
    /// a translator takes such a word for junk: in a word with Cyrillic letters they become Cyrillic.</summary>
    public static string FixLookalikes(string s)
    {
        if (!s.Any(c => c is >= 'Ѐ' and <= 'ӿ')) return s;
        var words = s.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (!w.Any(c => c is >= 'Ѐ' and <= 'ӿ') || !w.Any(c => Latin.Contains(c))) continue;
            if (w.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' && !Latin.Contains(c))) continue;   // a real Latin word
            var sb = new StringBuilder(w);
            for (int k = 0; k < sb.Length; k++) { int j = Latin.IndexOf(sb[k]); if (j >= 0) sb[k] = Cyrillic[j]; }
            words[i] = sb.ToString();
        }
        return string.Join(' ', words);
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
/// <summary>
/// Which lines on screen are new and settled enough to say. A subtitle box holds several lines, often from different
/// speakers («Джошуа: [стонет]» over «Джейкоб: Кто там?», Dead Island 2), and a new line joins the ones already shown:
/// each is said once, when it was read the same twice, or at once when it is a whole clean sentence (ends with . ! ? …).
/// A line said within the last 30 s is not said again.
/// </summary>
public sealed class SubtitleTracker
{
    sealed class Seen { public string Text = ""; public int Count; public long First; public bool Done; }
    List<Seen> _seen = new();
    readonly List<(string text, long at)> _said = new();

    public List<(string text, long firstSeen)> Push(IReadOnlyList<string> lines)
    {
        long now = Environment.TickCount64;
        var ready = new List<(string, long)>();
        var next = new List<Seen>();
        _said.RemoveAll(x => now - x.at > 30_000);
        foreach (var line in lines)
        {
            var s = _seen.FirstOrDefault(x => !next.Contains(x) && TextTools.Similarity(x.Text, line) >= 0.85);
            if (s == null) s = new Seen { Text = line, First = now };
            else if (line.Length > s.Text.Length) s.Text = line;
            s.Count++;
            next.Add(s);
            if (s.Done) continue;
            var (good, bad) = OcrService.Quality(new[] { new OcrLine(line, Rectangle.Empty) });
            string end = line.TrimEnd('"', '»', '\'', ' ');
            bool whole = bad == 0 && good >= 4 && end.Length > 0 && ".!?…".Contains(end[^1]);
            if (s.Count < 2 && !whole) continue;
            s.Done = true;
            if (_said.Any(x => TextTools.Similarity(x.text, s.Text) >= 0.8)) continue;
            _said.Add((s.Text, now));
            ready.Add((s.Text, s.First));
        }
        _seen = next;   // a line gone from the screen is forgotten
        return ready;
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
