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
/// are ignored. YAMNet (Google, AudioSet, 521 classes) names the sound twice a second on one CPU thread; a cheap
/// loudness gate makes an impact flash at once when the last named sound already was an effect.
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
    double _block, _slow = 0.01;
    int _blockN;
    long _lastEffectTick, _lastImpactTick, _lastSirenTick, _lastHornTick;
    double _lastImpactScore;
    double _peak;

    public static string ModelDir => Path.Combine(Paths.Root, "models", "yamnet");
    public static bool ModelPresent => File.Exists(Path.Combine(ModelDir, "yamnet.onnx")) && File.Exists(Path.Combine(ModelDir, "yamnet_class_map.csv"));

    public SoundFx()
    {
        var names = File.ReadAllLines(Path.Combine(ModelDir, "yamnet_class_map.csv")).Skip(1)
            .Select(l => l.Split(',', 3)).Where(p => p.Length == 3).Select(p => (i: int.Parse(p[0]), name: p[2].Trim().Trim('"'))).ToList();
        int[] Find(string[] wanted) => names.Where(n => wanted.Contains(n.name)).Select(n => n.i).ToArray();
        _impact = Find(ImpactNames); _siren = Find(SirenNames); _horn = Find(HornNames);

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
        if (++_blockN < Rate / 50) return;
        double rms = Math.Sqrt(_block / _blockN);
        _block = 0; _blockN = 0;
        _peak = Math.Max(_peak * 0.9, rms);
        bool onset = rms > 0.03 && rms > _slow * 4;
        _slow = _slow * 0.99 + rms * 0.01; // ~2 s memory
        if (!onset) return;
        long now = Environment.TickCount64;
        // The last named sound was an impact a moment ago: this burst is one too, flash without waiting for the model.
        if (now - _lastImpactTick < 1500 && _lastImpactScore > 0.2) Fire(SoundKind.Impact, Math.Min(1, rms / 0.25), now);
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
            if (impact > 0.2) { _lastImpactTick = now; _lastImpactScore = impact; }
            if (impact > 0.35) Fire(SoundKind.Impact, Math.Min(1, impact), now);
            if (siren > 0.4 && now - _lastSirenTick > 3000) { _lastSirenTick = now; Fire(SoundKind.Siren, siren, now); }
            if (horn > 0.4 && now - _lastHornTick > 1000) { _lastHornTick = now; Fire(SoundKind.Horn, horn, now); }
        }
        catch (Exception ex) { Log.Error("soundfx classify", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    void Fire(SoundKind kind, double strength, long now)
    {
        if (kind == SoundKind.Impact)
        {
            if (now - _lastEffectTick < 700) return; // one flash per burst of gunfire, not a strobe
            _lastEffectTick = now;
        }
        try { Effect?.Invoke(kind, strength); } catch (Exception ex) { Log.Error("soundfx effect", ex); }
    }

    public void Dispose()
    {
        _hop.Dispose();
        StopCapture();
        _model.Dispose();
    }
}
