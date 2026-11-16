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

public enum SoundKind { Impact, Siren, Horn, Heartbeat, Thunder, Shot }

/// <summary>
/// Listens to what Windows plays (speakers or headphones alike: the loopback of the default output) and reports
/// sound effects only: explosions, shots, crashes and breaking glass, sirens and alarms, horns, a heartbeat (low health
/// in many games) and thunder. Speech and music are ignored. YAMNet (Google, AudioSet, 521 classes) names the sound
/// twice a second on one CPU thread. Gunfire is the one exception to "the model alone": while the model hears gunfire,
/// every shot's own sharp onset in the sound (10 ms blocks) flashes at once, as loud as it is, so all shots show, the
/// player's and far ones alike, not one in 0.7 s. Onsets outside gunfire flash nothing: a loudness-jump detector for
/// crashes in races (where YAMNet hears only "Vehicle") was tried and dropped on 16.11, in Wreckfest it flashed on
/// starts, menus and shifts as often as on crashes.
/// </summary>
public sealed class SoundFx : IDisposable
{
    const int Rate = 16000, Window = 15600, HopMs = 480;
    // Real hits only: "Slam", "Thump, thud" and "Burst, pop" also name doors, footsteps and bubbles
    static readonly string[] ImpactNames = { "Explosion", "Fireworks", "Firecracker", "Eruption", "Boom", "Bang", "Smash, crash",
        "Breaking", "Shatter" };
    static readonly string[] GunNames = { "Gunshot, gunfire", "Machine gun", "Fusillade", "Artillery fire", "Cap gun" };
    const double GunThreshold = 0.25;  // far shots score lower than the player's own; each still needs its own onset
    const int GunHold = 1100;          // gunfire heard in a look counts until the next look's window has passed
    static readonly string[] SirenNames = { "Siren", "Civil defense siren", "Police car (siren)", "Ambulance (siren)", "Fire engine, fire truck (siren)",
        "Alarm", "Car alarm", "Smoke detector, smoke alarm", "Fire alarm" };
    static readonly string[] HornNames = { "Vehicle horn, car horn, honking", "Air horn, truck horn", "Train horn", "Foghorn" };
    // an engine running: a racing game, where the left trigger is the brake
    static readonly string[] EngineNames = { "Vehicle", "Motor vehicle (road)", "Car", "Race car, auto racing", "Truck", "Motorcycle",
        "Engine", "Light engine (high frequency)", "Medium engine (mid frequency)", "Heavy engine (low frequency)", "Idling",
        "Accelerating, revving, vroom", "Tire squeal", "Skidding" };
    static readonly string[] HeartNames = { "Heart sounds, heartbeat", "Heart murmur" };
    // "Thunderstorm" is rain as well, heard for minutes: only the clap counts
    static readonly string[] ThunderNames = { "Thunder" };

    /// <summary>Kind and strength of a detected effect: 0 just over its threshold, 1 as sure as the model gets.</summary>
    public event Action<SoundKind, double>? Effect;

    readonly InferenceSession _model;
    readonly string _input, _output;
    readonly int[] _impact, _gun, _siren, _horn, _heart, _thunder, _engine;
    readonly float[] _ring = new float[Rate * 2];
    readonly object _gate = new();
    int _write;
    long _samples, _classified;
    WasapiLoopbackCapture? _capture;
    string _deviceId = "";
    readonly Timer _hop;
    int _busy;
    // loudness over 10 ms blocks: silence skips the model, and a shot's onset times its flash
    double _block; int _blockN;
    readonly double[] _recent = new double[5];  // the last 50 ms of block loudness, for onsets
    int _recentN;
    long _gunUntil, _onsetTick, _shotTick; double _onsetLoud, _loudestShot = 0.05;
    double _pendingShot = -1; int _shots;
    long _lastEffectTick, _lastThunderTick;
    readonly long[] _lastHeard = new long[Enum.GetValues<SoundKind>().Length];
    // what was heard, for the log: strongest effect scores and the loudest top class over a minute
    double _sumImpact, _sumGun, _sumSiren, _sumHorn, _sumHeart, _sumThunder; string _top = ""; double _topScore; int _hops; long _statTick;
    string[] _names = Array.Empty<string>();
    double _peak;

    long _engineTick;
    /// <summary>An engine was heard within the last 3 s: a racing game is on.</summary>
    public bool EngineHeard => EngineHeardWithin(3000);
    public bool EngineHeardWithin(int ms) => Environment.TickCount64 - Interlocked.Read(ref _engineTick) < ms;

    /// <summary>No listening while nobody sees the lights (screen off, the console on a TV): no work for the model.</summary>
    public volatile bool Paused;

    public static string ModelDir => Path.Combine(Paths.Root, "models", "yamnet");
    public static bool ModelPresent => File.Exists(Path.Combine(ModelDir, "yamnet.onnx")) && File.Exists(Path.Combine(ModelDir, "yamnet_class_map.csv"));

    public SoundFx()
    {
        var names = File.ReadAllLines(Path.Combine(ModelDir, "yamnet_class_map.csv")).Skip(1)
            .Select(l => l.Split(',', 3)).Where(p => p.Length == 3).Select(p => (i: int.Parse(p[0]), name: p[2].Trim().Trim('"'))).ToList();
        int[] Find(string[] wanted) => names.Where(n => wanted.Contains(n.name)).Select(n => n.i).ToArray();
        _impact = Find(ImpactNames); _gun = Find(GunNames); _siren = Find(SirenNames); _horn = Find(HornNames); _heart = Find(HeartNames); _thunder = Find(ThunderNames); _engine = Find(EngineNames);
        _names = new string[521];
        foreach (var n in names) if (n.i >= 0 && n.i < 521) _names[n.i] = n.name;

        var opts = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        _model = new InferenceSession(Path.Combine(ModelDir, "yamnet.onnx"), opts);
        _input = _model.InputMetadata.Keys.First();
        _output = _model.OutputMetadata.First(o => o.Value.Dimensions.LastOrDefault() == 521).Key;
        Log.Write($"soundfx: model ready ({_impact.Length} impact, {_gun.Length} gunfire, {_siren.Length} siren, {_horn.Length} horn, {_heart.Length} heart, {_thunder.Length} thunder classes)");
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
        lock (_gate) _peak = 0;
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
        double shot = Interlocked.Exchange(ref _pendingShot, -1);
        if (shot >= 0) Shoot(shot);
    }

    /// <summary>Every 10 ms block: the loudness for the silence gate, and a shot's onset, a jump to 1.8 times the last 50 ms.</summary>
    void Gate(float v)
    {
        _block += v * v;
        if (++_blockN < Rate / 100) return;
        double rms = Math.Sqrt(_block / _blockN);
        _block = 0; _blockN = 0;
        _peak = Math.Max(_peak * 0.95, rms);
        double before = 0; for (int i = 0; i < _recent.Length; i++) before += _recent[i];
        before /= _recent.Length;
        _recent[_recentN++ % _recent.Length] = rms;
        long now = Environment.TickCount64;
        if (rms < 0.01 || rms < 1.8 * before || now - _onsetTick < 60) return;  // 60 ms: a fast gun fires 10-15 a second
        _onsetTick = now; _onsetLoud = rms;
        if (now < _gunUntil) _pendingShot = Loudness(rms);   // gunfire going on: this shot flashes now
    }

    /// <summary>A shot's strength 0..1 against the loudest shot lately: the player's own about 1, far ones less.</summary>
    double Loudness(double rms)
    {
        _loudestShot = Math.Max(rms, _loudestShot);
        return Math.Clamp(rms / _loudestShot, 0, 1);
    }

    void Shoot(double strength)
    {
        _shotTick = Environment.TickCount64; _shots++;
        try { Effect?.Invoke(SoundKind.Shot, strength); } catch (Exception ex) { Log.Error("soundfx effect", ex); }
    }

    void Classify()
    {
        if (Paused || Interlocked.Exchange(ref _busy, 1) == 1) return;
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
            double impact = Max(_impact), gun = Max(_gun), siren = Max(_siren), horn = Max(_horn), heart = Max(_heart), thunder = Max(_thunder);
            long now = Environment.TickCount64;
            if (Max(_engine) > 0.3) Interlocked.Exchange(ref _engineTick, now);
            Stats(scores, frames, impact, gun, siren, horn, heart, thunder, now);
            bool gunfire = gun > GunThreshold;
            if (gunfire)
            {
                double late = -1;
                lock (_gate)
                {
                    // the shot that the model has just recognized came before this look: flash it late if it has not flashed
                    if (now >= _gunUntil && now - _onsetTick < 1000 && _shotTick < _onsetTick) late = Loudness(_onsetLoud);
                    _gunUntil = now + GunHold;
                }
                if (late >= 0) Shoot(late);
            }
            // a shot also scores "Bang": amid gunfire only a real blast (a grenade) is an impact, or the long pulses
            // would hide the shots' own blinks
            double need = gunfire ? 0.6 : 0.35;
            if (impact > need) Fire(SoundKind.Impact, impact, need, now);
            // a siren, a horn and a heartbeat fire every look while they last: their lights follow the sound
            if (siren > 0.4) Fire(SoundKind.Siren, siren, 0.4, now);
            if (horn > 0.4) Fire(SoundKind.Horn, horn, 0.4, now);
            if (heart > 0.3) Fire(SoundKind.Heartbeat, heart, 0.3, now);
            if (thunder > 0.35 && now - _lastThunderTick > 4000) { _lastThunderTick = now; Fire(SoundKind.Thunder, thunder, 0.35, now); }
        }
        catch (Exception ex) { Log.Error("soundfx classify", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    void Stats(Tensor<float> scores, int frames, double impact, double gun, double siren, double horn, double heart, double thunder, long now)
    {
        _hops++;
        _loudestShot = Math.Max(0.05, _loudestShot * 0.97);  // the reference for shot strength forgets slowly (about 30 s)
        _sumImpact = Math.Max(_sumImpact, impact); _sumGun = Math.Max(_sumGun, gun); _sumSiren = Math.Max(_sumSiren, siren); _sumHorn = Math.Max(_sumHorn, horn);
        _sumHeart = Math.Max(_sumHeart, heart); _sumThunder = Math.Max(_sumThunder, thunder);
        for (int i = 0; i < 521; i++)
        {
            double v = 0; for (int f = 0; f < frames; f++) v = Math.Max(v, scores[f, i]);
            if (v > _topScore) { _topScore = v; _top = _names[i] ?? i.ToString(); }
        }
        if (now - _statTick < 60_000) return; // a summary a minute, for tuning from the log
        Log.Write($"soundfx: {_hops} looks, max impact {_sumImpact:F2} gunfire {_sumGun:F2} ({_shots} shots) siren {_sumSiren:F2} horn {_sumHorn:F2} heart {_sumHeart:F2} thunder {_sumThunder:F2}, loudest «{_top}» {_topScore:F2}");
        _statTick = now; _hops = 0; _shots = 0; _sumImpact = _sumGun = _sumSiren = _sumHorn = _sumHeart = _sumThunder = _topScore = 0; _top = "";
    }

    /// <summary>The strength is how far the score is over its threshold, 0..1: a faint sound gives a short dim light,
    /// a loud one a long bright one. The log keeps the model's own score, for tuning.</summary>
    void Fire(SoundKind kind, double score, double threshold, long now)
    {
        if (kind == SoundKind.Impact)
        {
            if (now - _lastEffectTick < 700) return; // one flash per burst of explosions, not a strobe
            _lastEffectTick = now;
        }
        // a siren, a horn or a heartbeat comes every look while it lasts: log only its start
        bool ongoing = kind is SoundKind.Heartbeat or SoundKind.Siren or SoundKind.Horn;
        ref long last = ref _lastHeard[(int)kind];
        if (!ongoing || now - last > 2000) Log.Write($"soundfx: {kind} {score:F2}");
        last = now;
        double strength = Math.Clamp((score - threshold) / (1 - threshold), 0, 1);
        try { Effect?.Invoke(kind, strength); } catch (Exception ex) { Log.Error("soundfx effect", ex); }
    }

    public void Dispose()
    {
        _hop.Dispose();
        StopCapture();
        _model.Dispose();
    }
}
