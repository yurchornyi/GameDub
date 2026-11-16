using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GameDub.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GameDub.Lights;

public enum SoundKind { Impact, Siren, Horn }

/// <summary>
/// Listens to what Windows plays (speakers or headphones alike: the loopback of the default output) and reports
/// sound effects only: explosions, shots, crashes and breaking glass, sirens and alarms, horns. Speech and music
/// are ignored. Two detectors: YAMNet (Google, AudioSet, 521 classes) names the sound twice a second on one CPU
/// thread; and a loudness jump (a sudden burst 3x over the last 2 s, e.g. a crash over a racing game's engine,
/// which YAMNet only ever calls "Vehicle", 16.11 test) is an impact unless YAMNet hears speech or music right now.
/// </summary>
public sealed class SoundFx : IDisposable
{
    const int Rate = 16000, Window = 15600, HopMs = 480;
    static readonly string[] ImpactNames = { "Explosion", "Gunshot, gunfire", "Machine gun", "Fusillade", "Artillery fire", "Cap gun",
        "Fireworks", "Firecracker", "Burst, pop", "Eruption", "Boom", "Bang", "Slam", "Smash, crash", "Breaking", "Shatter", "Thump, thud" };
    static readonly string[] SirenNames = { "Siren", "Civil defense siren", "Police car (siren)", "Ambulance (siren)", "Fire engine, fire truck (siren)",
        "Alarm", "Car alarm", "Smoke detector, smoke alarm", "Fire alarm" };
    static readonly string[] HornNames = { "Vehicle horn, car horn, honking", "Air horn, truck horn", "Train horn", "Foghorn" };

    /// <summary>Kind and strength 0..1 of a detected effect.</summary>
    public event Action<SoundKind, double>? Effect;

    readonly InferenceSession _model;
    readonly string _input, _output;
    readonly int[] _impact, _siren, _horn;
    readonly float[] _ring = new float[Rate * 2];
    readonly object _gate = new();
    int _write;
    long _samples, _classified;
    WasapiLoopbackCapture? _capture;
    string _deviceId = "";
    readonly Timer _hop;
    int _busy;
    // loudness gate on 20 ms blocks
    double _block, _slow;
    int _warm; // 20 ms blocks heard since the capture started: the bed is known after 2 s
    int _blockN;
    long _lastEffectTick, _lastImpactTick, _lastSirenTick, _lastHornTick;
    double _lastImpactScore;
    const double JumpRatio = 3.5;
    double _hpBlock, _hpSlow, _maxHp; float _prev;
    long _talkTick;
    double _slowBefore;
    double _maxRatio; int _jumps2, _jumps3, _jumps4, _jumpsTalk;
    // what was heard, for the log: strongest effect scores and the loudest top class over 10 s
    double _sumImpact, _sumSiren, _sumHorn; string _top = ""; double _topScore; int _hops; long _statTick;
    string[] _names = Array.Empty<string>();
    double _peak;

    public static string ModelDir => Path.Combine(Paths.Root, "models", "yamnet");
    public static bool ModelPresent => File.Exists(Path.Combine(ModelDir, "yamnet.onnx")) && File.Exists(Path.Combine(ModelDir, "yamnet_class_map.csv"));

    public SoundFx()
    {
        var names = File.ReadAllLines(Path.Combine(ModelDir, "yamnet_class_map.csv")).Skip(1)
            .Select(l => l.Split(',', 3)).Where(p => p.Length == 3).Select(p => (i: int.Parse(p[0]), name: p[2].Trim().Trim('"'))).ToList();
        int[] Find(string[] wanted) => names.Where(n => wanted.Contains(n.name)).Select(n => n.i).ToArray();
        _impact = Find(ImpactNames); _siren = Find(SirenNames); _horn = Find(HornNames);
        _names = new string[521];
        foreach (var n in names) if (n.i >= 0 && n.i < 521) _names[n.i] = n.name;

        var opts = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        _model = new InferenceSession(Path.Combine(ModelDir, "yamnet.onnx"), opts);
        _input = _model.InputMetadata.Keys.First();
        _output = _model.OutputMetadata.First(o => o.Value.Dimensions.LastOrDefault() == 521).Key;
        Log.Write($"soundfx: model ready ({_impact.Length} impact, {_siren.Length} siren, {_horn.Length} horn classes)");
        _hop = new Timer(_ => Classify(), null, HopMs, HopMs);
    }

    /// <summary>Opens the loopback of the current default output; call again from time to time to follow device changes.</summary>
    public void FollowDefaultDevice()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return;
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (dev.ID == _deviceId && _capture != null) return;
            StopCapture();
            _deviceId = dev.ID;
            var cap = new WasapiLoopbackCapture(dev);
            cap.DataAvailable += (_, e) => Feed(e.Buffer, e.BytesRecorded, cap.WaveFormat);
            cap.RecordingStopped += (_, e) => { if (e.Exception != null) { Log.Error("soundfx capture", e.Exception); _deviceId = ""; } };
            cap.StartRecording();
            _capture = cap;
            Log.Write($"soundfx: listening to {dev.FriendlyName} ({cap.WaveFormat.SampleRate} Hz, {cap.WaveFormat.Channels} ch)");
        }
        catch (Exception ex) { Log.Error("soundfx device", ex); _deviceId = ""; }
    }

    void StopCapture()
    {
        var c = _capture; _capture = null;
        lock (_gate) { _warm = 0; _slow = 0; _hpSlow = 0; }
        if (c == null) return;
        try { c.StopRecording(); } catch { }
        c.Dispose();
    }

    double _phase; // resampler position in input samples

    void Feed(byte[] buf, int bytes, WaveFormat fmt)
    {
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat && fmt.Encoding != WaveFormatEncoding.Extensible) return;
        if (fmt.BitsPerSample != 32) return;
        int ch = fmt.Channels, frames = bytes / 4 / ch;
        double step = fmt.SampleRate / (double)Rate;
        int avg = Math.Max(1, (int)Math.Round(step)); // box filter over one output period against aliasing
        lock (_gate)
        {
            for (; _phase < frames; _phase += step)
            {
                int start = (int)_phase;
                double sum = 0; int n = 0;
                for (int k = start; k < start + avg && k < frames; k++, n++)
                    for (int c = 0; c < ch; c++) sum += BitConverter.ToSingle(buf, (k * ch + c) * 4);
                float v = n == 0 ? 0 : (float)(sum / (n * ch));
                _ring[_write] = v;
                _write = (_write + 1) % _ring.Length;
                _samples++;
                Gate(v);
            }
            _phase -= frames;
        }
    }

    void Gate(float v)
    {
        _block += v * v;
        float d = v - _prev; _prev = v; // first difference: the top of the spectrum (grinding, metal, glass)
        _hpBlock += d * d;
        if (++_blockN < Rate / 50) return;
        double rms = Math.Sqrt(_block / _blockN), hp = Math.Sqrt(_hpBlock / _blockN);
        _block = 0; _hpBlock = 0; _blockN = 0;
        double hpRatio = hp / Math.Max(_hpSlow, 1e-5);
        _hpSlow = _hpSlow * 0.99 + hp * 0.01;
        _peak = Math.Max(_peak * 0.9, rms);
        double ratio = rms / Math.Max(_slow, 1e-4);
        bool warm = ++_warm > 100;
        _slow = _slow * 0.99 + rms * 0.01; // ~2 s memory
        if (rms > 0.02)
        {
            _maxRatio = Math.Max(_maxRatio, ratio);
            _maxHp = Math.Max(_maxHp, hpRatio);
            if (ratio >= 2) _jumps2++;
            if (ratio >= 3) _jumps3++;
            if (ratio >= 4) _jumps4++;
        }
        // A sound out of silence (a game starting, a menu click after a pause) is no effect: the bed must be playing.
        // A crash over an engine: louder, and much brighter (a rev is louder but not brighter). Or simply a big jump.
        bool crash = ratio >= 2.2 && hpRatio >= 2.5;
        if (!warm || rms < 0.04 || !(crash || ratio >= JumpRatio) || _slowBefore < 0.01) { _slowBefore = _slow; return; }
        _slowBefore = _slow;
        long now = Environment.TickCount64;
        if (now - _talkTick < 1500) { _jumpsTalk++; return; } // speech or music: not an effect
        Fire(SoundKind.Impact, Math.Min(1, Math.Max(ratio - 2, hpRatio - 2.5) / 3 + 0.4), now);
    }

    void Classify()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var wave = new float[Window];
            lock (_gate)
            {
                // Silence: no work at all. The loopback sends nothing while nothing plays, so also skip a stale buffer.
                if (_samples < Window || _peak < 0.01 || _samples - _classified < Rate / 4) return;
                _classified = _samples;
                int start = (_write - Window + _ring.Length) % _ring.Length;
                for (int i = 0; i < Window; i++) wave[i] = _ring[(start + i) % _ring.Length];
            }
            using var res = _model.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, new DenseTensor<float>(wave, new[] { Window })) }, new[] { _output });
            var scores = res.First().AsTensor<float>();
            int frames = scores.Dimensions[0];
            double Max(int[] idx) { double m = 0; for (int f = 0; f < frames; f++) foreach (var i in idx) m = Math.Max(m, scores[f, i]); return m; }
            double impact = Max(_impact), siren = Max(_siren), horn = Max(_horn);
            long now = Environment.TickCount64;
            Stats(scores, frames, impact, siren, horn, now);
            if (impact > 0.2) { _lastImpactTick = now; _lastImpactScore = impact; }
            if (impact > 0.35) Fire(SoundKind.Impact, Math.Min(1, impact), now);
            if (siren > 0.4 && now - _lastSirenTick > 3000) { _lastSirenTick = now; Fire(SoundKind.Siren, siren, now); }
            if (horn > 0.4 && now - _lastHornTick > 1000) { _lastHornTick = now; Fire(SoundKind.Horn, horn, now); }
        }
        catch (Exception ex) { Log.Error("soundfx classify", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    void Stats(Tensor<float> scores, int frames, double impact, double siren, double horn, long now)
    {
        _hops++;
        _sumImpact = Math.Max(_sumImpact, impact); _sumSiren = Math.Max(_sumSiren, siren); _sumHorn = Math.Max(_sumHorn, horn);
        string hopTop = ""; double hopScore = 0;
        for (int i = 0; i < 521; i++)
        {
            double v = 0; for (int f = 0; f < frames; f++) v = Math.Max(v, scores[f, i]);
            if (v > _topScore) { _topScore = v; _top = _names[i] ?? i.ToString(); }
            if (v > hopScore) { hopScore = v; hopTop = _names[i] ?? ""; }
        }
        if (hopScore > 0.3 && IsTalk(hopTop)) _talkTick = now;
        if (now - _statTick < 10_000) return;
        Log.Write($"soundfx: {_hops} looks, max impact {_sumImpact:F2} siren {_sumSiren:F2} horn {_sumHorn:F2}, loudest «{_top}» {_topScore:F2}; " +
                  $"jumps x2 {_jumps2} x3 {_jumps3} x4 {_jumps4} (in talk {_jumpsTalk}), max x{_maxRatio:F1}, bright x{_maxHp:F1}");
        _statTick = now; _hops = 0; _sumImpact = _sumSiren = _sumHorn = _topScore = 0; _top = "";
        _maxRatio = 0; _maxHp = 0; _jumps2 = _jumps3 = _jumps4 = _jumpsTalk = 0;
    }

    static bool IsTalk(string name)
    {
        foreach (var w in new[] { "Speech", "speaking", "Conversation", "Narration", "Music", "Singing", "song", "Choir", "Rapping" })
            if (name.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    void Fire(SoundKind kind, double strength, long now)
    {
        if (kind == SoundKind.Impact)
        {
            if (now - _lastEffectTick < 700) return; // one flash per burst of gunfire, not a strobe
            _lastEffectTick = now;
        }
        Log.Write($"soundfx: {kind} {strength:F2}");
        try { Effect?.Invoke(kind, strength); } catch (Exception ex) { Log.Error("soundfx effect", ex); }
    }

    public void Dispose()
    {
        _hop.Dispose();
        StopCapture();
        _model.Dispose();
    }
}
