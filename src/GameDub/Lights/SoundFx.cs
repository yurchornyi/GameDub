using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using GameDub.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GameDub.Lights;

public enum SoundKind { Impact, Siren, Horn, Shot }

/// <summary>
/// Listens to what Windows plays (speakers or headphones alike: the loopback of the default output) and reports
/// sound effects only: explosions, shots, crashes and breaking glass, sirens and alarms, horns. Speech and music are
/// ignored. Two models, both as ONNX from the waveform in to the scores out (src/tools/sound-models/export.py):
/// - EfficientAT mn10 (JKU Linz, MIT; AudioSet, 527 classes, mAP 0.47 against YAMNet's 0.31 at about the same cost)
///   names every sound, in two ways:
///   - Sudden sounds (shots, explosions, breaking glass) are judged one by one, at once: 60 ms after every sharp onset
///     (a jump in loudness between 10 ms blocks) the model is asked about the second that ends there. A shot needs
///     gunfire over its threshold, more than any blast or footsteps, and a loudness of at least 15 % of the loudest
///     shot lately. Within 300 ms of a shot, a sound at least 40 % as loud is the next shot of the burst and flashes
///     at once. A blast needs a blast class over its threshold and over gunfire, footsteps and doors.
///   - Lasting sounds (sirens, horns, an engine) are looked at twice a second over the last second. A siren is shown
///     only once two looks in a row hear it, and kept while it is heard within 2.5 s.
/// - CLAP (LAION, Apache-2.0) hears what AudioSet has no class for: a car crash. While an engine was heard lately, a
///   sudden sound that is neither a shot nor a blast but sounds like a hit is compared with written descriptions
///   ("a car crash", "a car door closing", "footsteps", ...) and flashes only when the crash ones win.
/// Thresholds were chosen on public sounds (AudioSet, ESC-50, SFXMint) against YAMNet's (sound-models workflow).
/// Thunder and heartbeats are not shown (16.11: in Far Cry 3 the old model heard both in music and ambience).
/// </summary>
public sealed class SoundFx : IDisposable
{
    const int Rate = 32000, Window = Rate, HopMs = 480, Block = Rate / 100;
    const int PostSamples = Rate * 60 / 1000; // a sudden sound is judged 60 ms after its onset
    const int OnsetGap = Rate * 60 / 1000;  // a fast gun fires 10-15 times a second
    const int StaleMs = 250;                // a cut the model reaches later than this is dropped: late is worse than missed
    const int CrashPostSamples = Rate * 250 / 1000; // a crash is heard on the second that ends 250 ms after its onset
    const int CrashStaleMs = 450;
    const int ClapRate = 48000, ClapSamples = ClapRate * 10;
    // Real hits only: "Slam", "Thump, thud" and "Burst, pop" also name doors, footsteps and bubbles; "Bang" too (a car
    // door in GTA 5 flashed as a blast, 16.11); "Fireworks", "Firecracker" and "Eruption" name crackles and rumbles that
    // are not there. Game blasts read as "Explosion" or "Boom".
    static readonly string[] ImpactNames = { "Explosion", "Boom", "Smash, crash", "Breaking", "Shatter" };
    // doors and knocks: when they sound stronger than a blast, it was a door
    static readonly string[] DoorNames = { "Door", "Sliding door", "Slam", "Knock", "Tap", "Thump, thud", "Bang" };
    static readonly string[] GunNames = { "Gunshot, gunfire", "Machine gun", "Fusillade", "Artillery fire", "Cap gun" };
    static readonly string[] FootNames = { "Walk, footsteps", "Run", "Shuffle" };
    // a sudden sound that may be a hit of some kind: only these are asked whether they were a crash
    static readonly string[] HintNames = { "Smash, crash", "Bang", "Thump, thud", "Slam", "Breaking", "Crushing", "Clang",
        "Whack, thwack", "Shatter", "Glass", "Crumpling, crinkling" };
    const double GunScore = 0.2, ShotLoudness = 0.15, ImpactScore = 0.35, SirenScore = 0.3, SirenKeep = 0.25, HornScore = 0.4,
        EngineScore = 0.3, HintScore = 0.05, CrashScore = 0.5;
    static readonly string[] SirenNames = { "Siren", "Civil defense siren", "Police car (siren)", "Ambulance (siren)", "Fire engine, fire truck (siren)",
        "Alarm", "Car alarm", "Smoke detector, smoke alarm", "Fire alarm" };
    static readonly string[] HornNames = { "Vehicle horn, car horn, honking", "Air horn, truck horn", "Train horn", "Foghorn" };
    // an engine running: a racing game, where the left trigger is the brake
    static readonly string[] EngineNames = { "Vehicle", "Motor vehicle (road)", "Car", "Race car, auto racing", "Truck", "Motorcycle",
        "Engine", "Light engine (high frequency)", "Medium engine (mid frequency)", "Heavy engine (low frequency)", "Idling",
        "Accelerating, revving, vroom", "Tire squeal", "Skidding" };

    /// <summary>Kind and strength of a detected effect: 0 just over its threshold, 1 as sure as the model gets.</summary>
    public event Action<SoundKind, double>? Effect;

    readonly InferenceSession _model;
    readonly InferenceSession? _clap;
    readonly int[] _impact, _gun, _foot, _door, _siren, _horn, _engine, _hint, _crashIdx = Array.Empty<int>();
    readonly string[] _prompts = Array.Empty<string>();
    readonly int _classes;
    readonly float[] _ring = new float[Rate * 2];
    readonly object _gate = new();
    int _write;
    long _samples, _classified;
    WasapiLoopbackCapture? _capture;
    string _deviceId = "";
    readonly Timer _hop;
    int _busy;
    // loudness over 10 ms blocks: silence skips the model, and an onset starts a sudden sound's cut
    double _block; int _blockN;
    readonly double[] _recent = new double[3];  // the last 30 ms of block loudness, for onsets
    int _recentN;
    long _lastOnset = long.MinValue / 2;
    readonly List<(long at, double rms)> _onsets = new();      // onsets waiting for their 60 ms after
    readonly List<long> _crashWait = new();                    // onsets waiting for their 250 ms after, for CLAP
    // cuts waiting for the models, each on a thread of its own
    readonly Queue<(float[] around, double rms, long at, long tick)> _cuts = new();
    readonly SemaphoreSlim _cutReady = new(0);
    readonly Thread _judge;
    readonly Queue<(float[] cut, long tick)> _crashCuts = new();
    readonly SemaphoreSlim _crashReady = new(0);
    readonly Thread? _crashJudge;
    volatile bool _stop;
    readonly object _shotGate = new(), _fireGate = new();
    long _gunTick; double _gunRms, _loudestShot = 0.05;          // the last shot named, and the loudest shot lately
    // sudden sounds over a minute, for the log
    int _cutsMade, _judged, _late, _fast, _shots, _impacts; double _clipGun, _clipImpact, _judgeMs;
    int _crashChecks, _crashLate, _crashes; double _crashMax, _crashMs;
    readonly Dictionary<string, int> _clipTops = new();
    long _lastEffectTick, _sirenOnUntil, _shotLogTick; int _shotsSinceLog; bool _sirenLookPrev;
    readonly long[] _lastHeard = new long[Enum.GetValues<SoundKind>().Length];
    // what was heard, for the log: strongest effect scores and the loudest top class over a minute
    double _sumImpact, _sumGun, _sumSiren, _sumHorn; string _top = ""; double _topScore; int _hops; long _statTick;
    readonly string[] _names;
    double _peak;

    long _engineTick;
    /// <summary>An engine was heard within the last 3 s: a racing game is on.</summary>
    public bool EngineHeard => EngineHeardWithin(3000);
    public bool EngineHeardWithin(int ms) => Environment.TickCount64 - Interlocked.Read(ref _engineTick) < ms;

    /// <summary>No listening while nobody sees the lights (screen off, the console on a TV): no work for the model.</summary>
    public volatile bool Paused;

    public static string ModelDir => Path.Combine(Paths.Root, "models", "sound");
    static string ModelFile => Path.Combine(ModelDir, "efficientat.onnx");
    static string NamesFile => Path.Combine(ModelDir, "efficientat_class_map.csv");
    static string ClapFile => Path.Combine(ModelDir, "clap.onnx");
    static string PromptsFile => Path.Combine(ModelDir, "clap_prompts.csv");
    public static bool ModelPresent => File.Exists(ModelFile) && File.Exists(NamesFile);
    static bool ClapPresent => File.Exists(ClapFile) && File.Exists(PromptsFile);

    public SoundFx()
    {
        // index,mid,display_name; a name may hold commas inside its quotes
        var names = File.ReadAllLines(NamesFile).Skip(1)
            .Select(l => l.Split(',', 3)).Where(p => p.Length == 3).Select(p => (i: int.Parse(p[0]), name: p[2].Trim().Trim('"'))).ToList();
        _classes = names.Max(n => n.i) + 1;
        int[] Find(string[] wanted) => names.Where(n => wanted.Contains(n.name)).Select(n => n.i).ToArray();
        _impact = Find(ImpactNames); _gun = Find(GunNames); _foot = Find(FootNames); _door = Find(DoorNames); _siren = Find(SirenNames);
        _horn = Find(HornNames); _engine = Find(EngineNames); _hint = Find(HintNames);
        _names = new string[_classes];
        foreach (var n in names) _names[n.i] = n.name;

        var opts = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        _model = new InferenceSession(ModelFile, opts);
        long t0 = Stopwatch.GetTimestamp();
        Scores(new float[Window]);
        double modelMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        string clapNote = "no crash model";
        if (ClapPresent)
        {
            try
            {
                // index,kind,prompt: "crash" prompts are the car crash, the rest what else a hit may be
                var prompts = File.ReadAllLines(PromptsFile).Skip(1).Select(l => l.Split(',', 3)).Where(p => p.Length == 3).ToList();
                _prompts = prompts.Select(p => p[2].Trim().Trim('"')).ToArray();
                _crashIdx = prompts.Where(p => p[1] == "crash").Select(p => int.Parse(p[0])).ToArray();
                // two threads: a crash is checked rarely, and the light should not wait; no memory arena: the memory a
                // check works in (~50-100 MB for its 10 s) goes back at once instead of being kept for the next one
                _clap = new InferenceSession(ClapFile, new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, EnableCpuMemArena = false });
                t0 = Stopwatch.GetTimestamp();
                Crash(new float[Window]);
                clapNote = $"crash model {Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms";
            }
            catch (Exception ex) { Log.Error("soundfx crash model", ex); _clap?.Dispose(); _clap = null; }
        }
        Log.Write($"soundfx: models ready, sounds {modelMs:F0} ms, {clapNote} ({_impact.Length} impact, {_gun.Length} gunfire, {_siren.Length} siren, {_horn.Length} horn classes)");
        _hop = new Timer(_ => Classify(), null, HopMs, HopMs);
        _judge = new Thread(JudgeLoop) { IsBackground = true, Name = "soundfx judge" };
        _judge.Start();
        if (_clap != null)
        {
            _crashJudge = new Thread(CrashLoop) { IsBackground = true, Name = "soundfx crash" };
            _crashJudge.Start();
        }
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

    /// <summary>For a test: plays a WAV file into the effects in real time, as if Windows played it, without a speaker.</summary>
    public void PlayFile(string path)
    {
        using var file = new WaveFileReader(path);
        var r = file.ToSampleProvider();
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(r.WaveFormat.SampleRate, r.WaveFormat.Channels);
        int chunk = r.WaveFormat.SampleRate / 100 * r.WaveFormat.Channels;   // 10 ms
        var buf = new float[chunk];
        var bytes = new byte[chunk * 4];
        var clock = Stopwatch.StartNew();
        long fed = 0;
        int n;
        while ((n = r.Read(buf, 0, chunk)) > 0)
        {
            Buffer.BlockCopy(buf, 0, bytes, 0, n * 4);
            Feed(bytes, n * 4, fmt);
            fed += n / r.WaveFormat.Channels;
            long ahead = fed * 1000 / r.WaveFormat.SampleRate - clock.ElapsedMilliseconds;
            if (ahead > 0) Thread.Sleep((int)ahead);
        }
    }

    double _phase; // resampler position in input samples

    void Feed(byte[] buf, int bytes, WaveFormat fmt)
    {
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat && fmt.Encoding != WaveFormatEncoding.Extensible) return;
        if (fmt.BitsPerSample != 32) return;
        int ch = fmt.Channels, frames = bytes / 4 / ch;
        double step = fmt.SampleRate / (double)Rate;
        int avg = Math.Max(1, (int)Math.Round(step)); // box filter over one output period against aliasing
        var mono = new List<float>(frames / Math.Max(1, (int)step) + 2);
        for (; _phase < frames; _phase += step)
        {
            int start = (int)_phase;
            double sum = 0; int n = 0;
            for (int k = start; k < start + avg && k < frames; k++, n++)
                for (int c = 0; c < ch; c++) sum += BitConverter.ToSingle(buf, (k * ch + c) * 4);
            mono.Add(n == 0 ? 0 : (float)(sum / (n * ch)));
        }
        _phase -= frames;
        Push(mono);
    }

    /// <summary>32 kHz mono samples from the loopback.</summary>
    void Push(List<float> mono)
    {
        List<(float[] around, double rms, long at)>? ready = null;
        List<float[]>? crashCuts = null;
        lock (_gate)
        {
            foreach (float v in mono)
            {
                _ring[_write] = v;
                _write = (_write + 1) % _ring.Length;
                _samples++;
                Gate(v);
            }
            // onsets whose 60 ms after have come: cut them out for the model
            while (_onsets.Count > 0 && _samples - _onsets[0].at >= PostSamples)
            {
                var (at, rms) = _onsets[0];
                _onsets.RemoveAt(0);
                if (Paused) continue;
                (ready ??= new()).Add((Cut(), rms, at));                // the second that ends 60 ms after the onset
            }
            // possible crashes whose 250 ms after have come: cut them out for CLAP
            while (_crashWait.Count > 0 && _samples - _crashWait[0] >= CrashPostSamples)
            {
                long at = _crashWait[0];
                _crashWait.RemoveAt(0);
                if (Paused || _samples - at > CrashPostSamples + Rate / 10) continue; // the audio came in late: skip
                (crashCuts ??= new()).Add(Cut());
            }
        }
        if (ready != null) foreach (var (around, rms, at) in ready) Hand(around, rms, at);
        if (crashCuts != null)
        {
            long now = Environment.TickCount64;
            lock (_crashCuts)
                foreach (var c in crashCuts)
                {
                    if (_crashCuts.Count >= 2) { _crashCuts.Dequeue(); _crashLate++; }
                    _crashCuts.Enqueue((c, now));
                    _crashReady.Release();
                }
        }
    }

    /// <summary>The last second from the ring (under _gate).</summary>
    float[] Cut()
    {
        var c = new float[Window];
        int start = (_write - Window + _ring.Length) % _ring.Length;
        for (int k = 0; k < Window; k++) c[k] = _ring[(start + k) % _ring.Length];
        return c;
    }

    /// <summary>Every 10 ms block: the loudness for the silence gate, and an onset, a jump to 1.6 times the last 30 ms.</summary>
    void Gate(float v)
    {
        _block += v * v;
        if (++_blockN < Block) return;
        double rms = Math.Sqrt(_block / _blockN);
        _block = 0; _blockN = 0;
        _peak = Math.Max(_peak * 0.95, rms);
        double before = 0; for (int i = 0; i < _recent.Length; i++) before += _recent[i];
        before /= _recent.Length;
        _recent[_recentN++ % _recent.Length] = rms;
        // 1.6 times the last 30 ms: within a burst the sound does not fall far between shots (1.8 over 50 ms missed
        // most shots of a burst in GTA 5, 16.11)
        if (rms < 0.01 || rms < 1.6 * before || _samples - _lastOnset < OnsetGap) return;
        _lastOnset = _samples;
        if (_onsets.Count < 8) _onsets.Add((_samples - Block, rms));   // from the start of the block that jumped
    }

    /// <summary>A cut for the model. Within 300 ms of a shot, a sound at least 40 % as loud as it is the next shot of the
    /// burst and flashes at once, without waiting for the model, so a burst flashes shot by shot.</summary>
    void Hand(float[] around, double rms, long at)
    {
        long now = Environment.TickCount64;
        bool fast = false;
        lock (_cuts)
        {
            _cutsMade++;
            lock (_shotGate) fast = now - _gunTick < 300 && rms >= 0.4 * _gunRms;
            if (!fast)
            {
                if (_cuts.Count >= 4) { _cuts.Dequeue(); _late++; }
                _cuts.Enqueue((around, rms, at, now));
            }
        }
        if (fast) { _fast++; Shoot(rms, now); }
        else _cutReady.Release();
    }

    void JudgeLoop()
    {
        while (!_stop)
        {
            _cutReady.Wait(1000);
            (float[] around, double rms, long at, long tick) cut;
            lock (_cuts)
            {
                if (_cuts.Count == 0) continue;
                cut = _cuts.Dequeue();
            }
            if (_stop) return;
            if (Environment.TickCount64 - cut.tick > StaleMs) { _late++; continue; }
            try { Judge(cut.around, cut.rms, cut.at); }
            catch (Exception ex) { Log.Error("soundfx judge", ex); }
        }
    }

    /// <summary>
    /// One sudden sound, judged on the second that ends 60 ms after it began: a shot blinks, a blast pulses, a hit while
    /// an engine runs goes on to CLAP, a footstep, a click, speech or music does nothing.
    /// </summary>
    void Judge(float[] around, double rms, long at)
    {
        long t0 = Stopwatch.GetTimestamp();
        var h = Scores(around);
        double gun = h.Max(_gun), impact = h.Max(_impact), foot = h.Max(_foot), door = h.Max(_door);
        int top = h.Top();
        long now = Environment.TickCount64;
        double loudest; lock (_shotGate) loudest = _loudestShot;
        bool shot = gun >= GunScore && gun >= impact && gun > foot && rms >= ShotLoudness * loudest;
        bool blast = !shot && impact >= ImpactScore && impact > gun && impact > foot && impact > door;
        lock (_clipTops)
        {
            _judged++; _judgeMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            _clipGun = Math.Max(_clipGun, gun); _clipImpact = Math.Max(_clipImpact, impact);
            string name = _names[top] ?? top.ToString();
            _clipTops[name] = _clipTops.TryGetValue(name, out int n) ? n + 1 : 1;
        }
        if (shot)
        {
            // a burst is many shots: the log names the first of each and how many followed
            if (now - _shotLogTick > 2000) { Log.Write($"soundfx: Shot («{_names[top]}», {_shotsSinceLog} more before)"); _shotLogTick = now; _shotsSinceLog = 0; }
            else _shotsSinceLog++;
            Shoot(rms, now);
        }
        else if (blast) { _impacts++; Fire(SoundKind.Impact, impact, ImpactScore, now, _names[top]); }
        else if (_clap != null && EngineHeardWithin(8000) && h.Max(_hint) >= HintScore)
            lock (_gate) if (_crashWait.Count < 4) _crashWait.Add(at);
    }

    /// <summary>The model's scores over one window, each class at its strongest frame.</summary>
    sealed class Heard
    {
        readonly double[] _v;
        public Heard(Tensor<float> scores, int classes)
        {
            _v = new double[classes];
            int frames = scores.Dimensions[0];
            for (int i = 0; i < classes; i++) { double m = 0; for (int f = 0; f < frames; f++) m = Math.Max(m, scores[f, i]); _v[i] = m; }
        }
        public double Max(int[] idx) { double m = 0; foreach (var i in idx) m = Math.Max(m, _v[i]); return m; }
        public int Top() { int t = 0; for (int i = 1; i < _v.Length; i++) if (_v[i] > _v[t]) t = i; return t; }
    }

    Heard Scores(float[] wave)
    {
        using var res = _model.Run(new[] { NamedOnnxValue.CreateFromTensor("wave", new DenseTensor<float>(wave, new[] { 1, wave.Length })) }, new[] { "scores" });
        return new Heard(res.First().AsTensor<float>(), _classes);
    }

    void CrashLoop()
    {
        while (!_stop)
        {
            _crashReady.Wait(1000);
            (float[] cut, long tick) c;
            lock (_crashCuts)
            {
                if (_crashCuts.Count == 0) continue;
                c = _crashCuts.Dequeue();
            }
            if (_stop) return;
            if (Environment.TickCount64 - c.tick > CrashStaleMs) { _crashLate++; continue; }
            try
            {
                long t0 = Stopwatch.GetTimestamp();
                var (crash, top) = Crash(c.cut);
                lock (_clipTops) { _crashChecks++; _crashMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds; _crashMax = Math.Max(_crashMax, crash); if (crash >= CrashScore) _crashes++; }
                if (crash >= CrashScore) { Fire(SoundKind.Impact, crash, CrashScore, Environment.TickCount64, $"crash, {_prompts[top]}"); }
            }
            catch (Exception ex) { Log.Error("soundfx crash", ex); }
        }
    }

    /// <summary>CLAP over one second at 32 kHz: made 48 kHz (linear) and repeated to its 10 s, as its processor does
    /// with a short sound. The share of the crash descriptions, and the description that won.</summary>
    (double crash, int top) Crash(float[] cut32)
    {
        int n = cut32.Length * 3 / 2;
        var wave = new float[ClapSamples];
        for (int i = 0; i < n; i++)
        {
            double pos = i * 2.0 / 3;
            int j = (int)pos;
            double f = pos - j;
            wave[i] = j + 1 < cut32.Length ? (float)(cut32[j] * (1 - f) + cut32[j + 1] * f) : cut32[^1];
        }
        int reps = ClapSamples / n;
        for (int r = 1; r < reps; r++) Array.Copy(wave, 0, wave, r * n, n);
        using var res = _clap!.Run(new[] { NamedOnnxValue.CreateFromTensor("wave", new DenseTensor<float>(wave, new[] { 1, ClapSamples })) }, new[] { "probs" });
        var p = res.First().AsTensor<float>();
        double crash = 0;
        foreach (int i in _crashIdx) crash += p[0, i];
        int top = 0;
        for (int i = 1; i < _prompts.Length; i++) if (p[0, i] > p[0, top]) top = i;
        return (crash, top);
    }

    /// <summary>A shot's flash, as bright as the shot is loud against the loudest shot lately: the player's own about 1.</summary>
    void Shoot(double rms, long now)
    {
        double strength;
        lock (_shotGate)
        {
            _loudestShot = Math.Max(rms, _loudestShot);
            strength = Math.Clamp(rms / _loudestShot, 0, 1);
            _gunTick = now; _gunRms = rms; _shots++;
        }
        try { Effect?.Invoke(SoundKind.Shot, strength); } catch (Exception ex) { Log.Error("soundfx effect", ex); }
    }

    void Classify()
    {
        if (Paused || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            float[] wave;
            lock (_gate)
            {
                // Silence: no work at all. The loopback sends nothing while nothing plays, so also skip a stale buffer.
                if (_samples < Window || _peak < 0.01 || _samples - _classified < Rate / 4) return;
                _classified = _samples;
                wave = Cut();
            }
            var h = Scores(wave);
            double impact = h.Max(_impact), gun = h.Max(_gun), siren = h.Max(_siren), horn = h.Max(_horn);
            long now = Environment.TickCount64;
            if (h.Max(_engine) > EngineScore) Interlocked.Exchange(ref _engineTick, now);
            Stats(h, impact, gun, siren, horn, now);
            // Sudden sounds are judged one by one as they come (Judge); a look would show them up to a second late.
            // a siren and a horn fire every look while they last: their lights follow the sound
            // a siren: two looks in a row to start, then on while heard within 2.5 s, so it neither flickers nor starts on a blip
            if (siren >= SirenScore && _sirenLookPrev) _sirenOnUntil = now + 2500;
            _sirenLookPrev = siren >= SirenScore;
            if (siren >= SirenKeep && now < _sirenOnUntil) _sirenOnUntil = now + 2500;
            if (now < _sirenOnUntil) Fire(SoundKind.Siren, Math.Max(siren, SirenScore), SirenScore, now);
            if (horn > HornScore) Fire(SoundKind.Horn, horn, HornScore, now);
        }
        catch (Exception ex) { Log.Error("soundfx classify", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    void Stats(Heard h, double impact, double gun, double siren, double horn, long now)
    {
        _hops++;
        lock (_shotGate) _loudestShot = Math.Max(0.05, _loudestShot * 0.9);  // the reference for shot strength forgets in about 5 s
        _sumImpact = Math.Max(_sumImpact, impact); _sumGun = Math.Max(_sumGun, gun); _sumSiren = Math.Max(_sumSiren, siren); _sumHorn = Math.Max(_sumHorn, horn);
        int top = h.Top();
        double topScore = h.Max(new[] { top });
        if (topScore > _topScore) { _topScore = topScore; _top = _names[top] ?? top.ToString(); }
        if (now - _statTick < 60_000) return; // a summary a minute, for tuning from the log
        Log.Write($"soundfx: {_hops} looks, max impact {_sumImpact:F2} gunfire {_sumGun:F2} siren {_sumSiren:F2} horn {_sumHorn:F2}, loudest «{_top}» {_topScore:F2}");
        lock (_clipTops)
        {
            if (_cutsMade > 0)
                Log.Write($"soundfx: {_cutsMade} sudden sounds, {_judged} judged ({(_judged > 0 ? _judgeMs / _judged : 0):F1} ms each), {_late} late, {_fast} fast-fire: " +
                    $"{_shots} shots, {_impacts} blasts; max gunfire {_clipGun:F2} impact {_clipImpact:F2}; mostly " +
                    string.Join(", ", _clipTops.OrderByDescending(t => t.Value).Take(4).Select(t => $"«{t.Key}» {t.Value}")));
            if (_crashChecks + _crashLate > 0)
                Log.Write($"soundfx: {_crashChecks} crash checks ({(_crashChecks > 0 ? _crashMs / _crashChecks : 0):F0} ms each), {_crashLate} late, {_crashes} crashes; max crash {_crashMax:F2}");
            _cutsMade = _judged = _late = _fast = _shots = _impacts = 0; _clipGun = _clipImpact = _judgeMs = 0; _clipTops.Clear();
            _crashChecks = _crashLate = _crashes = 0; _crashMax = _crashMs = 0;
        }
        _statTick = now; _hops = 0; _sumImpact = _sumGun = _sumSiren = _sumHorn = _topScore = 0; _top = "";
    }

    /// <summary>The strength is how far the score is over its threshold, 0..1: a faint sound gives a short dim light,
    /// a loud one a long bright one. The log keeps the model's own score, for tuning.</summary>
    void Fire(SoundKind kind, double score, double threshold, long now, string? heard = null)
    {
        lock (_fireGate)
        {
            if (kind == SoundKind.Impact)
            {
                if (now - _lastEffectTick < 700) return; // one flash per burst of explosions, not a strobe
                _lastEffectTick = now;
            }
            // a siren or a horn comes every look while it lasts: log only its start
            bool ongoing = kind is SoundKind.Siren or SoundKind.Horn;
            ref long last = ref _lastHeard[(int)kind];
            if (!ongoing || now - last > 2000) Log.Write($"soundfx: {kind} {score:F2}{(heard != null ? $" («{heard}»)" : "")}");
            last = now;
        }
        double strength = Math.Clamp((score - threshold) / (1 - threshold), 0, 1);
        try { Effect?.Invoke(kind, strength); } catch (Exception ex) { Log.Error("soundfx effect", ex); }
    }

    public void Dispose()
    {
        _hop.Dispose();
        StopCapture();
        _stop = true;
        _cutReady.Release();
        _crashReady.Release();
        _judge.Join(2000);
        _crashJudge?.Join(2000);
        _model.Dispose();
        _clap?.Dispose();
    }
}
