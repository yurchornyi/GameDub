using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using GameDub.Core;

namespace GameDub.Pipeline;

/// <summary>
/// capture → OCR → dedupe → translate → TTS → playback, for one running game.
/// Runs only while the game window is in the foreground; otherwise it idles at 1 Hz.
/// </summary>
public sealed class DubPipeline : IDisposable
{
    public GameEntry Game { get; }
    public volatile bool Paused;
    public volatile bool Editing;

    readonly AppSettings _s;
    readonly GameProfile _profile;
    readonly Func<IntPtr> _window;
    readonly Action<string> _osd;
    readonly CancellationTokenSource _cts = new();
    // Items carry the tick they were queued at, so stale ones can be skipped instead of played late.
    readonly Channel<(string text, long at)> _lines = Channel.CreateBounded<(string, long)>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });
    readonly Channel<(byte[] audio, long at, string timing)> _audio = Channel.CreateBounded<(byte[], long, string)>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
    const int StaleMs = 8000;
    readonly ITranslator _translator;
    readonly ITtsEngine _tts;
    SubtitleTracker _tracker = new();
    RegionDetector _detector = new();
    CancellationTokenSource? _playCts;
    byte[]? _lastSig;
    List<string> _lastLines = new();
    long _warmTick;
    // The subtitles' language as the game shows them, not as set: the other language's recognizer reads a line that
    // looks like junk too, and when it reads it clearly better twice in a row, the game's language is switched (owner,
    // 24.11: Dead Island 2 was set to English, the game showed Russian, and «Продолжить» was said as «О нпоаой1)квіт»).
    OcrService? _other;
    string _otherLang = "";
    int _otherWins;
    volatile bool _switched;

    public DubPipeline(GameEntry game, AppSettings s, Func<IntPtr> window, Action<string> osd)
    {
        Game = game; _s = s; _window = window; _osd = osd;
        _profile = Store.LoadProfile(game.Id);
        _translator = TranslatorFactory.Create(s, game.Name, game.SubLang);
        _tts = Voices.CreateEngine(s);
    }

    public RelRect Region => _profile.Region ?? RegionDetector.Default();
    public IntPtr Window => _window();

    public void Start()
    {
        var ct = _cts.Token;
        Task.Run(() => CaptureLoop(ct));
        Task.Run(() => SpeakLoop(ct));
        Task.Run(() => PlayLoop(ct));
        _osd(_profile.Region == null ? "GameDub: шукаю субтитри…" : "GameDub: переклад увімкнено");
        Log.Write($"pipeline start: {Game.Name}, region={(_profile.Region == null ? "auto" : "saved")}");
    }

    public void SetPaused(bool paused)
    {
        Paused = paused;
        if (paused) _playCts?.Cancel();
    }

    public void SetRegion(RelRect r)
    {
        r.Clamp();
        lock (_profile) { _profile.Region = r; Store.SaveProfile(Game.Id, _profile); }
        _tracker = new SubtitleTracker();
        _lastSig = null;
    }

    public void RestartDetection()
    {
        lock (_profile) { _profile.Region = null; Store.SaveProfile(Game.Id, _profile); }
        _detector = new RegionDetector();
        _lastSig = null;
    }

    async Task CaptureLoop(CancellationToken ct)
    {
        OcrService ocr;
        try { ocr = new OcrService(Game.SubLang); }
        catch (Exception ex) { Log.Error("ocr init", ex); _osd("GameDub: OCR недоступний"); return; }
        _otherLang = Game.SubLang == "ru" ? "en" : "ru";
        try { if (OcrService.IsAvailable(_otherLang)) _other = new OcrService(_otherLang); }
        catch (Exception ex) { Log.Error("ocr init (other language)", ex); }

        while (!ct.IsCancellationRequested)
        {
            int delay;
            try { delay = await Step(ocr); }
            catch (Exception ex) { Log.Error("capture", ex); delay = 1000; }
            try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
        }
    }

    async Task<int> Step(OcrService ocr)
    {
        if (Paused || Editing || _switched) return 300;
        var hwnd = _window();
        if (hwnd == IntPtr.Zero || Native.IsIconic(hwnd) || Native.GetForegroundWindow() != hwnd) { _lastSig = null; return 1000; }
        var client = Native.GetClientScreenRect(hwnd);
        if (client.Width < 320 || client.Height < 240) return 1000;

        if (_profile.Region == null)
        {
            var area = RegionDetector.SearchArea(client);
            using var shot = ScreenCapture.Capture(area);
            // A static subtitle must still be confirmed by a second read, so keep reading while a hit is pending.
            if (!Changed(shot) && !_detector.HasHits) return 1000;
            var read = await Read(ocr, shot);
            if (read == null) return 1000;
            var found = _detector.Feed(read, client);
            if (found != null)
            {
                SetRegion(found);
                Log.Write($"region found: x={found.X:F2} y={found.Y:F2} w={found.W:F2} h={found.H:F2}");
                _osd("GameDub: область субтитрів знайдено");
            }
            return 700;
        }

        // Read a little around the saved band: a box of three lines grows above it (Dead Island 2, 24.11: the top line of
        // two sat on the band's edge). Text that touches the edge of what is read grows the band itself, and it is saved.
        var band = _profile.Region.ToScreen(client);
        var rect = Rectangle.Intersect(client, Rectangle.FromLTRB(band.Left - client.Width * 3 / 100, band.Top - band.Height,
            band.Right + client.Width * 3 / 100, band.Bottom + band.Height / 3));
        using var bmp = ScreenCapture.Capture(rect);
        if (Changed(bmp))
        {
            var read = await Read(ocr, bmp);
            if (read == null) return 1000;
            var (good, bad) = OcrService.Quality(read);
            // mostly junk (a menu in another script, a logo, the HUD): nothing to say
            _lastLines = bad > good ? new List<string>() : TextTools.Utterances(read);
            if (_lastLines.Count > 0) Grow(read, rect, client);
        }
        foreach (var (line, first) in _tracker.Push(_lastLines))
        {
            Log.Write($"{Game.SubLang.ToUpperInvariant()}: {line}");
            _lines.Writer.TryWrite((line, first));   // timed from when it came on screen
        }
        // keep the translator's connection open while the game is played: a new one cost ~1 s a line
        if (Environment.TickCount64 - _warmTick > 45_000) { _warmTick = Environment.TickCount64; _ = Task.Run(() => _translator.WarmAsync()); }
        return _lastLines.Count > 0 ? Math.Min(_s.OcrIntervalMs, 250) : _s.OcrIntervalMs;
    }

    /// <summary>Text on the edge of what was read: the band grows that way (up by half, or wider), at most to a third of
    /// the screen's height.</summary>
    void Grow(List<OcrLine> read, Rectangle rect, Rectangle client)
    {
        var r = _profile.Region!;
        bool top = read.Any(l => l.Box.Top <= 3), side = read.Any(l => l.Box.Left <= 3 || l.Box.Right >= rect.Width - 3);
        if (!(top && r.H < 0.33) && !(side && r.W < 0.98)) return;
        var g = new RelRect { X = r.X, Y = r.Y, W = r.W, H = r.H };
        if (top && r.H < 0.33) { g.Y -= r.H * 0.5; g.H *= 1.5; }
        if (side && r.W < 0.98) { g.X -= 0.03; g.W += 0.06; }
        g.Clamp();
        lock (_profile) { _profile.Region = g; Store.SaveProfile(Game.Id, _profile); }
        _lastSig = null;
        Log.Write($"region grown (text on its {(top ? "top" : "side")}): x={g.X:F2} y={g.Y:F2} w={g.W:F2} h={g.H:F2}");
    }

    /// <summary>The subtitles read; null once the other language turned out to be the game's (the pipeline restarts).</summary>
    async Task<List<OcrLine>?> Read(OcrService ocr, Bitmap bmp)
    {
        var lines = await ocr.RecognizeAsync(bmp);
        if (_other == null) return lines;
        var (good, bad) = OcrService.Quality(lines);
        if (bad == 0 || bad * 2 < good) { _otherWins = 0; return lines; }
        var alt = await _other.RecognizeAsync(bmp);
        var (good2, bad2) = OcrService.Quality(alt);
        if (good2 - 2 * bad2 <= good - 2 * bad + 3) { _otherWins = 0; return lines; }
        Log.Write($"subtitles read better as {_otherLang}: «{string.Join(" ", alt.Select(l => l.Text))}» than as {Game.SubLang}: «{string.Join(" ", lines.Select(l => l.Text))}»");
        if (++_otherWins < 2) return alt.Count > 0 ? new System.Collections.Generic.List<OcrLine>() : lines;
        _switched = true;
        try
        {
            var games = Store.LoadGames();
            var g = games.FirstOrDefault(x => x.Id == Game.Id);
            if (g != null) { g.SubLang = _otherLang; Store.SaveGames(games); }
            Log.Write($"subtitles of {Game.Name} are {_otherLang}, not {Game.SubLang}: switched");
            _osd(_otherLang == "ru" ? "GameDub: субтитри російські — перемкнув" : "GameDub: субтитри англійські — перемкнув");
        }
        catch (Exception ex) { Log.Error("switch subtitle language", ex); }
        return null;
    }

    bool Changed(Bitmap bmp)
    {
        var sig = ScreenCapture.Signature(bmp);
        bool changed = ScreenCapture.Diff(sig, _lastSig) >= 1.0;
        _lastSig = sig;
        return changed;
    }

    async Task SpeakLoop(CancellationToken ct)
    {
        await foreach (var (line, at) in _lines.Reader.ReadAllAsync(ct))
        {
            try
            {
                long waited = Environment.TickCount64 - at;
                if (waited > StaleMs) { Log.Write($"skip stale ({waited / 1000.0:F1}s): {line}"); continue; }
                var (speaker, body) = Speakers.Split(line);
                if (body.Count(char.IsLetter) < 2) continue;
                // only speech: a line with a speaker, or a sentence; a menu item, a hint or a label is not said (owner,
                // 24.11: «тільки те, що треба»; «Налаштувати», «O BOVITWB» came from the menu)
                int words = body.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w.Count(char.IsLetter) >= 2);
                string end = body.TrimEnd('"', '»', '\'', ' ');
                bool sentence = words >= 4 || words >= 2 && end.Length > 0 && ".!?…".Contains(end[^1]);
                if (speaker == null && !sentence) { Log.Write($"not speech, not said: {body}"); continue; }
                body = TextTools.FixCaps(body);
                if (speaker != null) speaker = TextTools.FixCaps(speaker);
                var (gender, variant) = VoiceFor(speaker);
                long t1 = Environment.TickCount64;
                var tr = await _translator.TranslateAsync(body, speaker, gender, ct);
                long t2 = Environment.TickCount64;
                if (tr.Text.Length == 0 || Paused) continue;
                // a translator gives back what it cannot read (junk from the screen) as it came: no Ukrainian letter, so
                // nothing is said, instead of English or junk in a Ukrainian voice (owner, 23.11)
                if (!tr.Text.Any(c => c is >= 'Ѐ' and <= 'ӿ') || TextTools.Similarity(tr.Text, body) > 0.97)
                {
                    Log.Write($"not translated, not said: {body} → {tr.Text}");
                    continue;
                }
                // Behind the game? Speak a little faster: +8% per queued item, up to +20%.
                int backlog = _lines.Reader.Count + _audio.Reader.Count + (waited > 2500 ? 1 : 0);
                double pace = 1 + Math.Min(0.20, backlog * 0.08);
                var req = new TtsRequest(tr.Text, gender, variant, tr.Mood, pace);
                Log.Write($"UK{(tr.Offline ? " (offline)" : "")} [{speaker ?? "narrator"} → {_tts.Describe(req)}{(tr.Mood != null ? ", " + tr.Mood : "")}{(pace > 1 ? $", x{pace:F2}" : "")}]: {tr.Text}");
                var mp3 = await _tts.SynthesizeAsync(req, ct);
                long t3 = Environment.TickCount64;
                _audio.Writer.TryWrite((mp3, at, $"read {(t1 - at) / 1000.0:F1} s, translated {(t2 - t1) / 1000.0:F1} s, voiced {(t3 - t2) / 1000.0:F1} s"));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Error("speak", ex); }
        }
    }

    (string gender, int variant) VoiceFor(string? speaker)
    {
        if (speaker == null) return (_s.NarratorGender, 0);
        lock (_profile)
        {
            // a misread name («Джеикоб» for «Джейкоб») is the same speaker
            var known = _profile.Speakers.Keys.FirstOrDefault(k => TextTools.Similarity(k, speaker) >= 0.75);
            if (known != null) speaker = known;
            if (!_profile.Speakers.TryGetValue(speaker, out var info))
            {
                var gender = Speakers.GuessGender(speaker);
                int same = _profile.Speakers.Values.Count(x => x.Gender == gender);
                info = new SpeakerInfo { Gender = gender, Variant = same % (Voices.VariantCount - 1) + 1 };
                _profile.Speakers[speaker] = info;
                Store.SaveProfile(Game.Id, _profile);
                Log.Write($"new speaker {speaker}: {gender}{info.Variant}");
            }
            return (info.Gender, info.Variant);
        }
    }

    async Task PlayLoop(CancellationToken ct)
    {
        await foreach (var (mp3, at, timing) in _audio.Reader.ReadAllAsync(ct))
        {
            if (Paused) continue;
            long waited = Environment.TickCount64 - at;
            if (waited > StaleMs + 4000) { Log.Write($"skip stale audio ({waited / 1000.0:F1}s)"); continue; }
            // from the line on screen to its voice: where the time went
            Log.Write($"said {waited / 1000.0:F1} s after the line came on screen ({timing})");
            _playCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try { await AudioPlayer.PlayAsync(mp3, _s.Volume, _playCts.Token); }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) break; }
            catch (Exception ex) { Log.Error("play", ex); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _playCts?.Cancel();
        Log.Write($"pipeline stop: {Game.Name}");
    }
}
