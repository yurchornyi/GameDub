using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameDub.Core;
using GameDub.Pipeline;

namespace GameDub;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Paths.Init();
        if (args.Contains("--tray")) return Tray.TrayApp.Run();
        if (args.Length > 0 && args[0] == "--test") return SelfTest(args.Skip(1).ToArray());

        // Only one UI at a time; a second launch just brings nothing new up.
        using var mutex = new Mutex(true, @"Local\GameDub.UI", out bool created);
        if (!created) return 0;
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
        return app.Run(new UI.MainWindow());
    }

    /// <summary>
    /// Diagnostics for development: --test scan | ocr &lt;image&gt; | region &lt;image&gt; | translate &lt;text&gt; | tts &lt;text&gt; | speaker &lt;text&gt;.
    /// Output goes to data\test-out.txt.
    /// </summary>
    static int SelfTest(string[] a)
    {
        var sb = new StringBuilder();
        if (a.FirstOrDefault() == "ui")
        {
            double w = a.Length > 3 ? double.Parse(a[2]) : 0, h = a.Length > 3 ? double.Parse(a[3]) : 0;
            try { UI.UiSelfTest.Run(a[1], w, h, sb); }
            catch (Exception ex) { sb.AppendLine("FAIL: " + ex); }
            File.WriteAllText(Path.Combine(Paths.Data, "test-out.txt"), sb.ToString(), Encoding.UTF8);
            return 0;
        }
        try { RunTest(a, sb).GetAwaiter().GetResult(); }
        catch (Exception ex) { sb.AppendLine("FAIL: " + ex); }
        File.WriteAllText(Path.Combine(Paths.Data, "test-out.txt"), sb.ToString(), Encoding.UTF8);
        return 0;
    }

    static async Task RunTest(string[] a, StringBuilder o)
    {
        var s = Store.LoadSettings();
        switch (a.FirstOrDefault())
        {
            case "keys":
                await ApiKeySelfTest.Run(sb: o);
                break;
            case "scan":
                foreach (var g in GameScanner.ScanAndMerge())
                    o.AppendLine($"{g.Id} | {g.Name} | {g.InstallDir} | cover={(g.Cover != null)}");
                break;
            case "running":
                foreach (var g in GameScanner.RunningApps()) o.AppendLine($"{g.Name} | {g.InstallDir}");
                break;
            case "ocr":
            {
                using var bmp = new Bitmap(a[1]);
                using var argb = new Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var gr = Graphics.FromImage(argb)) gr.DrawImage(bmp, 0, 0, bmp.Width, bmp.Height);
                var ocr = new OcrService();
                foreach (bool bin in new[] { false, true, true })
                {
                    ocr.Binarize = bin;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var lines = await ocr.RecognizeAsync(argb);
                    o.AppendLine($"--- binarize={bin}, {sw.ElapsedMilliseconds} ms");
                    foreach (var l in lines) o.AppendLine($"{l.Box}: {l.Text}");
                    o.AppendLine("clean: " + TextTools.Clean(lines));
                }
                break;
            }
            case "region":
            {
                using var bmp = new Bitmap(a[1]);
                var client = new Rectangle(0, 0, bmp.Width, bmp.Height);
                var area = RegionDetector.SearchArea(client);
                using var crop = bmp.Clone(area, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var det = new RegionDetector();
                var ocr = new OcrService();
                var lines = await ocr.RecognizeAsync(crop);
                det.Feed(lines, client);
                var r = det.Feed(lines, client);
                o.AppendLine(r == null ? "no region" : $"region x={r.X:F3} y={r.Y:F3} w={r.W:F3} h={r.H:F3} → {r.ToScreen(client)}");
                break;
            }
            case "translate":
            {
                var text = string.Join(" ", a.Skip(1));
                var (sp, body) = Speakers.Split(text);
                body = TextTools.FixCaps(body);
                if (sp != null) sp = TextTools.FixCaps(sp);
                o.AppendLine("en: " + body);
                var gender = sp == null ? s.NarratorGender : Speakers.GuessGender(sp);
                o.AppendLine($"speaker={sp ?? "-"} gender={gender}");
                var tr = await TranslatorFactory.Create(s, "Test").TranslateAsync(body, sp, gender, CancellationToken.None);
                o.AppendLine($"{tr.Text}  [mood: {tr.Mood ?? "-"}{(tr.Offline ? ", offline" : "")}]");
                break;
            }
            case "pipeline":
            {
                // --test pipeline <english line>: exactly what the game path does (hybrid translation + auto voice), then play
                var text = string.Join(" ", a.Skip(1));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var tr = await TranslatorFactory.Create(s, "Test").TranslateAsync(text, null, "m", CancellationToken.None);
                o.AppendLine($"translate {sw.ElapsedMilliseconds} ms ({(tr.Offline ? "OFFLINE model" : "online")}): {tr.Text}");
                sw.Restart();
                var tts = Voices.CreateEngine(s);
                var req = new TtsRequest(tr.Text, "m", 0);
                var audio = await tts.SynthesizeAsync(req, CancellationToken.None);
                o.AppendLine($"voice {sw.ElapsedMilliseconds} ms: {tts.Describe(req)}");
                File.WriteAllText(Path.Combine(Paths.Data, "test-out.txt"), o.ToString(), Encoding.UTF8);
                await AudioPlayer.PlayAsync(audio, s.Volume, CancellationToken.None);
                break;
            }
            case "offline":
            {
                // --test offline [model-dir-name|all] <lines separated by | > : compare offline models, no audio
                var rest = a.Skip(1).ToList();
                string? only = rest.Count > 0 && (rest[0] == "all" || rest[0].StartsWith("opus-")) ? rest[0] : null;
                if (only != null) rest.RemoveAt(0);
                var lines = string.Join(" ", rest).Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var dirs = new[] { "opus-mt-tc-big-en-uk", "opus-mt-en-uk" }.Where(d => only is null or "all" || d == only)
                    .Select(d => Path.Combine(Paths.Root, "models", d)).Where(Directory.Exists);
                foreach (var dir in dirs)
                {
                    var tr = new OfflineTranslator(dir);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    tr.Translate("Hello.");
                    o.AppendLine($"=== {Path.GetFileName(dir)} (load {sw.ElapsedMilliseconds} ms)");
                    foreach (var line in lines)
                    {
                        sw.Restart();
                        var uk = tr.Translate(line);
                        o.AppendLine($"{sw.ElapsedMilliseconds,5} ms  {line}\n         → {uk}");
                    }
                    tr.Dispose();
                    File.WriteAllText(Path.Combine(Paths.Data, "test-out.txt"), o.ToString(), Encoding.UTF8);
                }
                break;
            }
            case "tts":
            {
                var text = string.Join(" ", a.Skip(1));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var tts = Voices.CreateEngine(s);
                var mp3 = await tts.SynthesizeAsync(new TtsRequest(text, "m", 0), CancellationToken.None);
                o.AppendLine($"tts {tts.Describe(new TtsRequest(text, "m", 0))}: {mp3.Length} bytes in {sw.ElapsedMilliseconds} ms");
                await AudioPlayer.PlayAsync(mp3, s.Volume, CancellationToken.None);
                mp3 = await tts.SynthesizeAsync(new TtsRequest(text, "f", 2, "excited"), CancellationToken.None);
                await AudioPlayer.PlayAsync(mp3, s.Volume, CancellationToken.None);
                o.AppendLine("played");
                break;
            }
            case "variants":
            {
                // --test variants <m|f> <text>: narrator + character variants of one voice
                var tts = new EdgeTts(s);
                var text = string.Join(" ", a.Skip(2));
                for (int v = 0; v < Voices.VariantCount; v++)
                {
                    var req = new TtsRequest(text, a[1], v);
                    o.AppendLine($"variant {v}: {tts.Describe(req)}");
                    await AudioPlayer.PlayAsync(await tts.SynthesizeAsync(req, CancellationToken.None), s.Volume, CancellationToken.None);
                    await Task.Delay(700);
                }
                break;
            }
            case "supertonic":
            {
                // --test supertonic <M1|F2|all> <text>: offline voices, with timing
                // Optional "steps=N" as the 3rd argument, e.g. --test supertonic M1 steps=5 <text>
                var tts = new SupertonicTts(s);
                var rest = a.Skip(2).ToList();
                if (rest.Count > 0 && rest[0].StartsWith("steps=")) { SupertonicTts.Steps = int.Parse(rest[0][6..]); rest.RemoveAt(0); }
                var text = string.Join(" ", rest);
                var which = a[1] == "all" ? new[] { "m0", "m1", "m2", "m3", "f0", "f1", "f2", "f3", "f4" } : new[] { a[1] };
                foreach (var name in which)
                {
                    // name is gender + variant index ("m0" = narrator) or a raw preset like "M3"/"F2"
                    var req = char.IsUpper(name[0])
                        ? new TtsRequest(text, name[0] == 'F' ? "f" : "m", -int.Parse(name[1..]))
                        : new TtsRequest(text, name[0] == 'f' ? "f" : "m", int.Parse(name[1..]));
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var wav = await tts.SynthesizeAsync(req, CancellationToken.None);
                    double audioMs = (wav.Length - 44) / 2.0 / 44100 * 1000;
                    o.AppendLine($"{tts.Describe(req)}: {sw.ElapsedMilliseconds} ms synth, audio {audioMs:F0} ms (RTF {sw.ElapsedMilliseconds / audioMs:F2})");
                    File.WriteAllText(Path.Combine(Paths.Data, "test-out.txt"), o.ToString(), Encoding.UTF8);
                    await AudioPlayer.PlayAsync(wav, s.Volume, CancellationToken.None);
                    await Task.Delay(500);
                }
                break;
            }
            case "voice":
            {
                // --test voice <voiceId> <text>: try any Edge voice, e.g. multilingual ones speaking Ukrainian
                var text = string.Join(" ", a.Skip(2));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var mp3 = await new EdgeTts(s).SynthesizeAsync(text, new VoiceSpec(a[1], 0, s.RatePercent), CancellationToken.None);
                o.AppendLine($"{a[1]}: {mp3.Length} bytes in {sw.ElapsedMilliseconds} ms, format {EdgeTts.Format}, audio {Mp3Tools.DurationMs(mp3):F0} ms");
                sw.Restart();
                await AudioPlayer.PlayAsync(mp3, s.Volume, CancellationToken.None);
                o.AppendLine($"played in {sw.ElapsedMilliseconds} ms");
                break;
            }
            default:
                o.AppendLine("usage: --test scan|running|ocr <img>|region <img>|translate <text>|tts <text>");
                break;
        }
    }
}
