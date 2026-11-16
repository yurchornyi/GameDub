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
/// in many games) and thunder. Speech and music are ignored. YAMNet (Google, AudioSet, 521 classes) names the sounds,
/// in two ways:
/// - Sudden sounds (shots, explosions, crashes, breaking glass, a thunder clap) are judged one by one, at once. Every
///   sharp onset (a jump in loudness between 10 ms blocks) is cut out, 40 ms before to 100 ms after, and the model is
///   asked twice: about the cut repeated over its whole one-second window (what is this one sound? the model averages
///   over its window, so a cut alone in silence read as "Silence", 16.11), and about the last second as it sounded
///   (is there gunfire or a blast around at all?). Both must agree. So a shot flashes about 0.1 s after it is heard,
///   each shot of a burst on its own, as bright as it is loud; and a footstep, a reload or a menu click names itself as
///   what it is and flashes nothing (16.11, Call of Duty: looks twice a second were up to a second late, and onsets
///   taken while gunfire went on flashed on footsteps).
/// - Lasting sounds (sirens, horns, a heartbeat, an engine) are looked at twice a second over the last second.
/// A loudness detector without the model was tried for crashes (16.11) and dropped: it flashed on starts, menus and
/// gear shifts in Wreckfest.
/// </summary>
public sealed class SoundFx : IDisposable
{
    const int Rate = 16000, Window = 15600, HopMs = 480, Block = Rate / 100;
    const int PreSamples = Rate * 40 / 1000, PostSamples = Rate * 100 / 1000; // the cut around an onset
    const int OnsetGap = Rate * 60 / 1000;  // a fast gun fires 10-15 times a second
    const int StaleMs = 250;                // a cut the model reaches later than this is dropped: late is worse than missed
    // Real hits only: "Slam", "Thump, thud" and "Burst, pop" also name doors, footsteps and bubbles
    static readonly string[] ImpactNames = { "Explosion", "Fireworks", "Firecracker", "Eruption", "Boom", "Bang", "Smash, crash",
        "Breaking", "Shatter" };
    static readonly string[] GunNames = { "Gunshot, gunfire", "Machine gun", "Fusillade", "Artillery fire", "Cap gun" };
    static readonly string[] FootNames = { "Walk, footsteps", "Run", "Shuffle" };
    const double GunScore = 0.3, ImpactScore = 0.35, ThunderScore = 0.35, AroundScore = 0.2;
    const int Fade = Rate * 5 / 1000;       // each repeat of a cut fades in and out, or the seams click like shots
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
    readonly int[] _impact, _gun, _foot, _siren, _horn, _heart, _thunder, _engine;
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
    readonly double[] _recent = new double[5];  // the last 50 ms of block loudness, for onsets
    int _recentN;
    long _lastOnset = long.MinValue / 2;
    readonly List<(long at, double rms)> _onsets = new();      // onsets waiting for their 100 ms after
    // cuts waiting for the model, on a thread of their own
    readonly Queue<(float[] clip, float[] around, double rms, long tick)> _cuts = new();
    readonly SemaphoreSlim _cutReady = new(0);
    readonly Thread _judge;
    volatile bool _stop;
    readonly object _shotGate = new();
    long _gunTick; double _gunRms, _loudestShot = 0.05;          // the last shot named, and the loudest shot lately
    // sudden sounds over a minute, for the log
    int _cutsMade, _judged, _late, _fast, _shots, _impacts; double _clipGun, _clipImpact, _judgeMs;
    readonly Dictionary<string, int> _clipTops = new();
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
        _impact = Find(ImpactNames); _gun = Find(GunNames); _foot = Find(FootNames); _siren = Find(SirenNames); _horn = Find(HornNames); _heart = Find(HeartNames); _thunder = Find(ThunderNames); _engine = Find(EngineNames);
        _names = new string[521];
        foreach (var n in names) if (n.i >= 0 && n.i < 521) _names[n.i] = n.name;

        var opts = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        _model = new InferenceSession(Path.Combine(ModelDir, "yamnet.onnx"), opts);
        _input = _model.InputMetadata.Keys.First();
        _output = _model.OutputMetadata.First(o => o.Value.Dimensions.LastOrDefault() == 521).Key;
        Log.Write($"soundfx: model ready ({_impact.Length} impact, {_gun.Length} gunfire, {_siren.Length} siren, {_horn.Length} horn, {_heart.Length} heart, {_thunder.Length} thunder classes)");
        _hop = new Timer(_ => Classify(), null, HopMs, HopMs);
        _judge = new Thread(JudgeLoop) { IsBackground = true, Name = "soundfx judge" };
        _judge.Start();
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

    // A recording of what is heard (16 kHz mono float), for the test to replay: on while data\lights-record.txt exists,
    // at most five minutes. Only for tuning on the console; nothing is recorded otherwise.
    FileStream? _rec;
    long _recSamples;
    static string RecordSwitch => Path.Combine(Paths.Data, "lights-record.txt");
    public static string RecordFile => Path.Combine(Paths.Data, "sound-rec.raw");

    void Record(List<float> mono)
    {
        if (_rec == null || _recSamples > Rate * 300L) return;
        var bytes = new byte[mono.Count * 4];
        for (int i = 0; i < mono.Count; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), mono[i]);
        _rec.Write(bytes);
        _recSamples += mono.Count;
    }

    void RecordSwitchCheck()
    {
        bool want = File.Exists(RecordSwitch);
        lock (_gate)
        {
            if (want && _rec == null)
            {
                _rec = new FileStream(RecordFile, FileMode.Create, FileAccess.Write, FileShare.Read); _recSamples = 0;
                Log.Write("soundfx: recording to " + RecordFile);
            }
            else if (!want && _rec != null)
            {
                Log.Write($"soundfx: recorded {_recSamples / (double)Rate:F0} s");
                _rec.Dispose(); _rec = null;
            }
        }
    }

    /// <summary>The test: plays a recording through the same path in real time (so every timing rule holds).</summary>
    public void Replay(string rawPath)
    {
        var data = File.ReadAllBytes(rawPath);
        int total = data.Length / 4, chunk = Rate / 100;
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        for (int at = 0; at < total; at += chunk)
        {
            var mono = new List<float>(chunk);
            for (int i = at; i < Math.Min(total, at + chunk); i++) mono.Add(BitConverter.ToSingle(data, i * 4));
            Push(mono);
            int due = (int)((at + chunk) * 1000L / Rate - t0.ElapsedMilliseconds);
            if (due > 0) Thread.Sleep(due);
        }
        Thread.Sleep(600);
    }

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

    /// <summary>16 kHz mono samples, from the loopback or a recording replayed by the test.</summary>
    void Push(List<float> mono)
    {
        List<(float[] clip, float[] around, double rms)>? ready = null;
        lock (_gate)
        {
            foreach (float v in mono)
            {
                _ring[_write] = v;
                _write = (_write + 1) % _ring.Length;
                _samples++;
                Gate(v);
            }
            Record(mono);
            // onsets whose 100 ms after have come: cut them out for the model
            while (_onsets.Count > 0 && _samples - _onsets[0].at >= PostSamples)
            {
                var (at, rms) = _onsets[0];
                _onsets.RemoveAt(0);
                long from = at - PreSamples;
                if (Paused || _samples - from > _ring.Length) continue;
                var clip = new float[PreSamples + PostSamples];
                int start = (int)(((_write - (_samples - from)) % _ring.Length + _ring.Length) % _ring.Length);
                for (int k = 0; k < clip.Length; k++) clip[k] = _ring[(start + k) % _ring.Length];
                var around = new float[Window];                       // the last second, as it sounded
                int aStart = (_write - Window + _ring.Length) % _ring.Length;
                for (int k = 0; k < Window; k++) around[k] = _ring[(aStart + k) % _ring.Length];
                (ready ??= new()).Add((clip, around, rms));
            }
        }
        if (ready != null) foreach (var (clip, around, rms) in ready) Hand(clip, around, rms);
    }

    /// <summary>Every 10 ms block: the loudness for the silence gate, and an onset, a jump to 1.8 times the last 50 ms.</summary>
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
        if (rms < 0.01 || rms < 1.8 * before || _samples - _lastOnset < OnsetGap) return;
        _lastOnset = _samples;
        if (_onsets.Count < 8) _onsets.Add((_samples - Block, rms));   // from the start of the block that jumped
    }

    /// <summary>A cut for the model. While the model is behind in fast fire, a sound as loud as the shot it named just
    /// before is the next shot, so a burst keeps flashing shot by shot.</summary>
    void Hand(float[] clip, float[] around, double rms)
    {
        long now = Environment.TickCount64;
        bool fast = false;
        lock (_cuts)
        {
            _cutsMade++;
            lock (_shotGate) fast = _cuts.Count >= 2 && now - _gunTick < 250 && rms >= 0.5 * _gunRms;
            if (!fast)
            {
                if (_cuts.Count >= 4) { _cuts.Dequeue(); _late++; }
                _cuts.Enqueue((clip, around, rms, now));
            }
        }
        if (fast) { _fast++; Shoot(rms, now); }
        else _cutReady.Release();
    }

    void JudgeLoop()
    {
        var wave = new float[Window];
        while (!_stop)
        {
            _cutReady.Wait(1000);
            (float[] clip, float[] around, double rms, long tick) cut;
            lock (_cuts)
            {
                if (_cuts.Count == 0) continue;
                cut = _cuts.Dequeue();
            }
            if (_stop) return;
            if (Environment.TickCount64 - cut.tick > StaleMs) { _late++; continue; }
            try { Judge(cut.clip, cut.around, cut.rms, wave); }
            catch (Exception ex) { Log.Error("soundfx judge", ex); }
        }
    }

    /// <summary>
    /// One sudden sound: what is it, and is there gunfire or a blast around? A shot blinks, an explosion or a crash
    /// pulses, thunder strikes; a footstep, a click or music does nothing.
    /// </summary>
    void Judge(float[] clip, float[] around, double rms, float[] wave)
    {
        // the cut repeated over the window, each repeat faded in and out
        for (int i = 0; i < Window; i++)
        {
            int k = i % clip.Length;
            double edge = Math.Min(1, Math.Min(k, clip.Length - 1 - k) / (double)Fade);
            wave[i] = (float)(clip[k] * edge);
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var one = Scores(wave);
        var all = Scores(around);
        double gun = one.Max(_gun), impact = one.Max(_impact), foot = one.Max(_foot), thunder = one.Max(_thunder);
        double gunAround = all.Max(_gun), impactAround = all.Max(_impact), thunderAround = all.Max(_thunder);
        int top = one.Top();
        long now = Environment.TickCount64;
        bool shot = gun >= GunScore && gun >= impact && gun > foot && gunAround >= AroundScore;
        bool blast = !shot && impact >= ImpactScore && impact > foot && Math.Max(impactAround, gunAround) >= AroundScore;
        bool clap = !shot && !blast && thunder >= ThunderScore && thunderAround >= AroundScore;
        lock (_clipTops)
        {
            _judged++; _judgeMs += System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            _clipGun = Math.Max(_clipGun, gun); _clipImpact = Math.Max(_clipImpact, impact);
            string name = _names[top] ?? top.ToString();
            _clipTops[name] = _clipTops.TryGetValue(name, out int n) ? n + 1 : 1;
        }
        if (Lab) Log.Write($"soundfx lab: rms {rms:F3} one «{_names[top]}» gun {gun:F2} impact {impact:F2} foot {foot:F2} | around gun {gunAround:F2} impact {impactAround:F2}" +
            (shot ? " -> SHOT" : blast ? " -> BLAST" : clap ? " -> THUNDER" : ""));
        if (shot) Shoot(rms, now);
        else if (blast) { _impacts++; Fire(SoundKind.Impact, impact, ImpactScore, now); }
        else if (clap && now - _lastThunderTick > 4000) { _lastThunderTick = now; Fire(SoundKind.Thunder, thunder, ThunderScore, now); }
    }

    /// <summary>A judging session's log of every cut, while data\lights-lab.txt exists (for tuning on the console).</summary>
    static bool Lab => File.Exists(Path.Combine(Paths.Data, "lights-lab.txt"));

    /// <summary>The model's scores over one window, each class at its strongest frame.</summary>
    sealed class Heard
    {
        readonly double[] _v = new double[521];
        public Heard(Tensor<float> scores)
        {
            int frames = scores.Dimensions[0];
            for (int i = 0; i < 521; i++) { double m = 0; for (int f = 0; f < frames; f++) m = Math.Max(m, scores[f, i]); _v[i] = m; }
        }
        public double Max(int[] idx) { double m = 0; foreach (var i in idx) m = Math.Max(m, _v[i]); return m; }
        public int Top() { int t = 0; for (int i = 1; i < 521; i++) if (_v[i] > _v[t]) t = i; return t; }
    }

    Heard Scores(float[] wave)
    {
        using var res = _model.Run(new[] { NamedOnnxValue.CreateFromTensor(_input, new DenseTensor<float>(wave, new[] { Window })) }, new[] { _output });
        return new Heard(res.First().AsTensor<float>());
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
        try { RecordSwitchCheck(); } catch (Exception ex) { Log.Error("soundfx record", ex); }
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
            // Sudden sounds are judged one by one as they come (Judge); a look would show them up to a second late.
            // a siren, a horn and a heartbeat fire every look while they last: their lights follow the sound
            if (siren > 0.4) Fire(SoundKind.Siren, siren, 0.4, now);
            if (horn > 0.4) Fire(SoundKind.Horn, horn, 0.4, now);
            if (heart > 0.3) Fire(SoundKind.Heartbeat, heart, 0.3, now);
            // thunder rolls on after its clap: a look still catches one the clap's cut missed
            if (thunder > ThunderScore && now - _lastThunderTick > 4000) { _lastThunderTick = now; Fire(SoundKind.Thunder, thunder, ThunderScore, now); }
        }
        catch (Exception ex) { Log.Error("soundfx classify", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    void Stats(Tensor<float> scores, int frames, double impact, double gun, double siren, double horn, double heart, double thunder, long now)
    {
        _hops++;
        lock (_shotGate) _loudestShot = Math.Max(0.05, _loudestShot * 0.97);  // the reference for shot strength forgets slowly (about 30 s)
        _sumImpact = Math.Max(_sumImpact, impact); _sumGun = Math.Max(_sumGun, gun); _sumSiren = Math.Max(_sumSiren, siren); _sumHorn = Math.Max(_sumHorn, horn);
        _sumHeart = Math.Max(_sumHeart, heart); _sumThunder = Math.Max(_sumThunder, thunder);
        for (int i = 0; i < 521; i++)
        {
            double v = 0; for (int f = 0; f < frames; f++) v = Math.Max(v, scores[f, i]);
            if (v > _topScore) { _topScore = v; _top = _names[i] ?? i.ToString(); }
        }
        if (now - _statTick < 60_000) return; // a summary a minute, for tuning from the log
        Log.Write($"soundfx: {_hops} looks, max impact {_sumImpact:F2} gunfire {_sumGun:F2} siren {_sumSiren:F2} horn {_sumHorn:F2} heart {_sumHeart:F2} thunder {_sumThunder:F2}, loudest «{_top}» {_topScore:F2}");
        lock (_clipTops)
        {
            if (_cutsMade > 0)
                Log.Write($"soundfx: {_cutsMade} sudden sounds, {_judged} judged ({(_judged > 0 ? _judgeMs / _judged : 0):F1} ms each), {_late} late, {_fast} fast-fire: " +
                    $"{_shots} shots, {_impacts} blasts; max gunfire {_clipGun:F2} impact {_clipImpact:F2}; mostly " +
                    string.Join(", ", _clipTops.OrderByDescending(t => t.Value).Take(4).Select(t => $"«{t.Key}» {t.Value}")));
            _cutsMade = _judged = _late = _fast = _shots = _impacts = 0; _clipGun = _clipImpact = _judgeMs = 0; _clipTops.Clear();
        }
        _statTick = now; _hops = 0; _sumImpact = _sumGun = _sumSiren = _sumHorn = _sumHeart = _sumThunder = _topScore = 0; _top = "";
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
        _stop = true;
        _cutReady.Release();
        _judge.Join(2000);
        lock (_gate) { _rec?.Dispose(); _rec = null; }
        _model.Dispose();
    }
}
