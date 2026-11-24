using System;
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
    readonly Channel<(byte[] audio, long at)> _audio = Channel.CreateBounded<(byte[], long)>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.DropOldest });
    const int StaleMs = 8000;
    readonly ITranslator _translator;
    readonly ITtsEngine _tts;
    SubtitleTracker _tracker = new();
    RegionDetector _detector = new();
    CancellationTokenSource? _playCts;
    byte[]? _lastSig;
    string _lastText = "";
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

        var rect = _profile.Region.ToScreen(client);
        using var bmp = ScreenCapture.Capture(rect);
        if (Changed(bmp))
        {
            var read = await Read(ocr, bmp);
            if (read == null) return 1000;
            var (good, bad) = OcrService.Quality(read);
            // mostly junk (a menu in another script, a logo, the HUD): nothing to say
            _lastText = bad > good ? "" : TextTools.Clean(read);
        }
        var line = _tracker.Push(_lastText);
        if (line != null)
        {
            Log.Write($"{Game.SubLang.ToUpperInvariant()}: {line}");
            _lines.Writer.TryWrite((line, Environment.TickCount64));
        }
        return _lastText.Length > 0 ? _s.OcrIntervalMs : _s.OcrIntervalMs + 200;
    }

    /// <summary>The subtitles read; null once the other language turned out to be the game's (the pipeline restarts).</summary>
    async Task<System.Collections.Generic.List<OcrLine>?> Read(OcrService ocr, Bitmap bmp)
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
                body = TextTools.FixCaps(body);
                if (speaker != null) speaker = TextTools.FixCaps(speaker);
                var (gender, variant) = VoiceFor(speaker);
                var tr = await _translator.TranslateAsync(body, speaker, gender, ct);
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
                _audio.Writer.TryWrite((await _tts.SynthesizeAsync(req, ct), at));
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
        await foreach (var (mp3, at) in _audio.Reader.ReadAllAsync(ct))
        {
            if (Paused) continue;
            long waited = Environment.TickCount64 - at;
            if (waited > StaleMs + 4000) { Log.Write($"skip stale audio ({waited / 1000.0:F1}s)"); continue; }
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
