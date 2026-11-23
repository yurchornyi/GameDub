using System;
using System.Collections.Generic;
using System.Diagnostics;
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

public enum SoundKind { Impact, Siren, Horn, Shot }

/// <summary>
/// Listens to what Windows plays (speakers or headphones alike: the loopback of the default output) and reports
/// sound effects only: explosions, shots, crashes and breaking glass, sirens and alarms, horns. Speech and music are
/// ignored. Thunder and heartbeats were dropped (16.11): in Far Cry 3 the model heard both in music and ambience,
/// scoring up to 0.9, with neither in the game. YAMNet (Google, AudioSet, 521 classes) names the sounds,
/// in two ways:
/// - Sudden sounds (shots, explosions, crashes, breaking glass) are judged one by one, at once: 60 ms
///   after every sharp onset (a jump in loudness between 10 ms blocks) the model is asked about the second that ends
///   there. A shot needs gunfire over 0.2, more than any blast or footsteps, and a loudness of at least 15 % of the
///   loudest shot lately (a third dropped most far shots, GTA 5, 16.11). Within 300 ms of a shot, a sound at least
///   40 % as loud is the next shot of the burst and flashes at once. Tuned on recordings of Call of Duty, GTA 5 and
///   Wreckfest (16.11): shots flash about 0.07 s after they are heard, each shot of a burst on its own; footsteps,
///   speech and music do not; a race flashes no shots. Tried and dropped: looks twice a second (up to a second late),
///   a cut alone in silence (the model averages over its second: "Silence"), a cut repeated over the second
///   (footsteps then read as gunfire).
/// - Lasting sounds (sirens, horns, an engine) are looked at twice a second over the last second. A siren
///   is shown once two looks within 2 s hear it at 0.3, and kept while it is heard within 2.5 s: in GTA 5 the model heard
///   police sirens on and off, and the lights flickered; two looks in a row missed a car alarm, which beeps on and off
///   (GTA Online, 18.11: «Car alarm» up to 0.62, never twice in a row). One look alone, however sure, does not start it:
///   at 0.5 single looks in the radio's music lit the police lights with nothing there (19.11, 0.55-0.77).
/// A loudness detector without the model was tried for crashes (16.11) and dropped: it flashed on starts, menus and
/// gear shifts in Wreckfest.
/// A blast is checked by a second model, EfficientAT (mn10_as, JKU Linz, AudioSet, mAP 0.47 against YAMNet's 0.31),
/// when it is present: YAMNet proposes it at 0.2 instead of 0.35, and it flashes only if EfficientAT also hears an
/// explosion or breaking (0.1) in the same second at 32 kHz. On 3,200 public sounds (AudioSet, ESC-50, SFXMint;
/// 17.11) this kept the explosions caught (75 % against 72 %) and halved the false blasts (5.2 % against 9.8 %):
/// footsteps 45 % to 0, clapping 35 % to 0, crackling fire 40 % to 5, doors and knocks 12 % to 2. EfficientAT hears only
/// 10 s clips (trained on them, short ones padded with silence): one second alone scores every class near 1.0, so
/// its second is padded with 9 s of silence. It does not replace YAMNet: alone it caught fewer shots and horns, and
/// at 10 s it costs 30 times YAMNet's time; checking blasts only, it runs a few times a minute. On the console
/// (17.11): a check ~80 ms against YAMNet's ~8 ms, ~25 MB more for the service (95 MB private), ~9 MB more for a moment.
/// A car crash has no AudioSet class, so a third model, CLAP (LAION, int8), compares the sound with descriptions ("a car
/// crash", "a car crashing into another car"... against "a car door closing", "a gear change", "tires screeching"...),
/// when it is present: while an engine was heard within 8 s, a sudden sound that is neither a shot nor a blast and
/// sounds a little like a hit (YAMNet: smash, bang, thump, slam, crushing, clang... 0.05), or jumps to 2.5 times the
/// sound before it, is heard again on the second that ends 250 ms after it (at most one a second), and pulses as an impact when the crash descriptions take 0.6 of it. On public sounds
/// (17.11) it flashed on 0.1-0.3 % of non-crashes, skidding the most; no crash recordings were found, so how many crashes
/// it catches is tried in Wreckfest. In Wreckfest (18.11) YAMNet named every crash «Vehicle» under the engine, no hit
/// reached 0.05, and CLAP never heard one: hence the jump. Every check from 0.1 up is logged with the description that
/// won, its jump and hint, for tuning.
/// Whose sound it was (the player's crash or an AI car's, a blast nearby or far off) the model cannot say; for tuning,
/// the log gives each shot, blast and crash check its loudness and side (+1 all left, -1 all right, 0 the middle,
/// from the first two channels over its first 60 ms, 250 ms for a crash).
/// Lag (owner, 18.11: «шоб не було такого відставання»): the loopback is read every 15 ms (NAudio's own read every 50 ms),
/// on a thread of the highest priority; the next shot of a burst flashes on its onset, without waiting the 60 ms for the
/// model; the model's thread is above normal, and the slow checks (EfficientAT, CLAP) run on threads of their own, so a
/// blast check never holds up a shot. Each minute the log gives the lag from a sound's start to its flash.
/// </summary>
public sealed class SoundFx : IDisposable
{
    const int Rate = 16000, Window = 15600, HopMs = 480, Block = Rate / 100;
    const int PostSamples = Rate * 60 / 1000; // a sudden sound is judged 60 ms after its onset
    const int OnsetGap = Rate * 60 / 1000;  // a fast gun fires 10-15 times a second
    const int StaleMs = 250;                // a cut the model reaches later than this is dropped: late is worse than missed
    // Real hits only: "Slam", "Thump, thud" and "Burst, pop" also name doors, footsteps and bubbles; "Bang" too (a car
    // door in GTA 5 flashed as a blast, 16.11); "Fireworks", "Firecracker" and "Eruption" name crackles and rumbles that
    // are not there (GTA 5, 16.11: "Fireworks" 0.68 on nothing). Game blasts read as "Explosion" or "Boom".
    static readonly string[] ImpactNames = { "Explosion", "Boom", "Smash, crash", "Breaking", "Shatter" };
    // doors and knocks: when they sound stronger than a blast, it was a door
    static readonly string[] DoorNames = { "Door", "Sliding door", "Slam", "Knock", "Tap", "Thump, thud", "Bang" };
    static readonly string[] GunNames = { "Gunshot, gunfire", "Machine gun", "Fusillade", "Artillery fire", "Cap gun" };
    static readonly string[] FootNames = { "Walk, footsteps", "Run", "Shuffle" };
    const double GunScore = 0.2, ShotLoudness = 0.15, ImpactScore = 0.35, SirenScore = 0.3, SirenKeep = 0.25;
    // with the blast check: YAMNet proposes at 0.2, EfficientAT confirms at 0.1 (its scores run lower)
    const double ImpactProposal = 0.2, CheckScore = 0.1;
    const int CheckRate = 32000, CheckWindow = CheckRate, CheckPadded = CheckRate * 10;
    // the crash check (CLAP): on the second that ends 250 ms after the onset, while an engine was heard within 8 s
    const int CrashPostSamples = Rate * 250 / 1000, CrashStaleMs = 450, CrashEngineMs = 8000;
    const int ClapRate = 48000, ClapSamples = ClapRate * 10;
    const double HintScore = 0.05, CrashJump = 2.5, CrashScore = 0.6, RaceCrashScore = 0.4, CrashLogScore = 0.1;
    const int CrashEveryMs = 1000;          // one CLAP check a second at most: ~0.3 s of two threads each, beside the game
    // what a crash may sound like to YAMNet: only these go on to CLAP
    static readonly string[] HintNames = { "Smash, crash", "Bang", "Thump, thud", "Slam", "Breaking", "Crushing", "Clang",
        "Whack, thwack", "Shatter", "Glass", "Crumpling, crinkling" };
    static readonly string[] SirenNames = { "Siren", "Civil defense siren", "Police car (siren)", "Ambulance (siren)", "Fire engine, fire truck (siren)",
        "Alarm", "Car alarm", "Smoke detector, smoke alarm", "Fire alarm" };
    // A helicopter's blades beat 4-5 times a second, loud and sharp: YAMNet names the beats gunfire (GTA 5, 22.11: «Helicopter»
    // the loudest sound for whole minutes, and there the right trigger is the throttle). While one is heard, a sound is a
    // shot only when it is more gunfire than aircraft, and the next shots of a burst wait for the model.
    static readonly string[] AirNames = { "Helicopter", "Aircraft", "Aircraft engine", "Propeller, airscrew" };
    const double AirScore = 0.3; const int AirKeepMs = 2000;
    // Tires sliding squeal like a siren (owner, 23.11: in a race, drifting with the gas, the police lights came on): a look
    // with more squeal than siren is no siren; in a racing game a siren needs 0.5, like the brake and the crashes kept apart
    static readonly string[] SkidNames = { "Tire squeal", "Skidding" };
    const double RaceSirenScore = 0.5;
    static readonly string[] HornNames = { "Vehicle horn, car horn, honking", "Air horn, truck horn", "Train horn", "Foghorn" };
    // an engine running: a racing game, where the left trigger is the brake
    static readonly string[] EngineNames = { "Vehicle", "Motor vehicle (road)", "Car", "Race car, auto racing", "Truck", "Motorcycle",
        "Engine", "Light engine (high frequency)", "Medium engine (mid frequency)", "Heavy engine (low frequency)", "Idling",
        "Accelerating, revving, vroom", "Tire squeal", "Skidding" };

    /// <summary>Kind and strength of a detected effect (0 just over its threshold, 1 as sure as the model gets), and for a
    /// shot its loudness (the onset's RMS; 0 for the rest).</summary>
    public event Action<SoundKind, double, double>? Effect;

    readonly InferenceSession _model;
    readonly string _input, _output;
    readonly int[] _impact, _gun, _foot, _door, _siren, _horn, _engine, _hint, _air, _skid;
    long _airTick = long.MinValue / 2;
    readonly float[] _ring = new float[Rate * 2];
    readonly object _gate = new();
    int _write;
    long _samples, _classified;
    WasapiCapture? _capture;
    string _deviceId = "";
    readonly Timer _hop;
    int _busy;
    // loudness over 10 ms blocks: silence skips the model, and an onset starts a sudden sound's cut
    double _block, _blockL, _blockR; int _blockN;
    readonly (double l, double r)[] _sides = new (double, double)[64];   // left and right energy of the last 64 blocks
    long _blockNo;
    readonly double[] _recent = new double[3];  // the last 30 ms of block loudness, for onsets
    int _recentN;
    long _lastOnset = long.MinValue / 2;
    readonly List<(long at, double rms, long block, double jump)> _onsets = new();   // onsets waiting for their 60 ms after
    // cuts waiting for the model, on a thread of their own
    readonly record struct Cut(float[] Around, float[]? Around32, double Rms, double Side, long At, long Block, double Jump, long Tick);
    readonly Queue<Cut> _cuts = new();
    readonly SemaphoreSlim _cutReady = new(0);
    readonly Thread _judge;
    readonly Worker? _checker, _crasher;                         // the blast check and the crash check, each on its own thread
    readonly List<(double rms, long at)> _fastShots = new();     // next shots of a burst, flashed as soon as they start
    readonly object _fireGate = new();
    double _lagShot, _lagShotMax, _lagHit, _lagHitMax; int _lagShots, _lagHits;   // a sound's start to its flash, over a minute
    [ThreadStatic] static bool _raised;
    volatile bool _stop;
    readonly object _shotGate = new();
    long _gunTick; double _gunRms, _loudestShot = 0.05;          // the last shot named, and the loudest shot lately
    // sudden sounds over a minute, for the log
    int _cutsMade, _judged, _late, _fast, _shots, _impacts; double _clipGun, _clipImpact, _judgeMs;
    readonly Dictionary<string, int> _clipTops = new();
    long _lastEffectTick, _sirenOnUntil, _shotLogTick; int _shotsSinceLog; long _sirenHitTick, _sirenLogTick;
    readonly long[] _lastHeard = new long[Enum.GetValues<SoundKind>().Length];
    // what was heard, for the log: strongest effect scores and the loudest top class over a minute
    double _sumImpact, _sumGun, _sumSiren, _sumHorn; string _top = ""; double _topScore; int _hops; long _statTick;
    string[] _names = Array.Empty<string>();
    double _peak;

    long _engineTick;
    /// <summary>An engine was heard within the last 3 s: a racing game is on.</summary>
    public bool EngineHeard => EngineHeardWithin(3000);
    public bool EngineHeardWithin(int ms) => Environment.TickCount64 - Interlocked.Read(ref _engineTick) < ms;

    /// <summary>No listening while nobody sees the lights (screen off, the console on a TV): no work for the model.</summary>
    public volatile bool Paused;

    /// <summary>A racing game is in front (its Steam genre): there every sudden sound goes to CLAP, and a crash lights
    /// at 0.4. Wreckfest, 18.11: under its engine YAMNet heard no hit at all (hint 0.01) and the largest jump of a minute of
    /// crashes was 2.5; with every sound sent, crashes scored 0.50-0.69 («a car hitting a wall») and the rest (music,
    /// rain, revving) up to 0.34.</summary>
    public volatile bool Racing;

    public static string ModelDir => Path.Combine(Paths.Root, "models", "yamnet");
    public static bool ModelPresent => File.Exists(Path.Combine(ModelDir, "yamnet.onnx")) && File.Exists(Path.Combine(ModelDir, "yamnet_class_map.csv"));
    public static string CheckDir => Path.Combine(Paths.Root, "models", "efficientat");

    // the blast check (EfficientAT), when its model is there: its own 32 kHz copy of the sound
    readonly InferenceSession? _check;
    readonly int[] _checkImpact = Array.Empty<int>();
    readonly float[] _ring32 = new float[CheckRate * 2];
    readonly float[] _padded = new float[CheckPadded];   // the judge thread's only: a second, then silence
    int _write32;
    int _vetoed; double _checkMs; int _checks;
    public static string ClapDir => Path.Combine(Paths.Root, "models", "clap");

    // the crash check (CLAP), when its model is there
    readonly InferenceSession? _clap;
    readonly int[] _crashIdx = Array.Empty<int>();
    readonly string[] _prompts = Array.Empty<string>();
    readonly bool _hear32;                                       // a 32 kHz copy for EfficientAT or CLAP
    readonly List<(long at, long block, double rms, double jump, double hint)> _crashWait = new();   // onsets waiting for their 250 ms after
    long _crashCandTick; int _byHint, _byJump; double _raceJump, _raceHint;   // race hits over a minute, for the log
    int _crashChecks, _crashes; double _crashMs, _crashMax;

    public SoundFx()
    {
        var names = File.ReadAllLines(Path.Combine(ModelDir, "yamnet_class_map.csv")).Skip(1)
            .Select(l => l.Split(',', 3)).Where(p => p.Length == 3).Select(p => (i: int.Parse(p[0]), name: p[2].Trim().Trim('"'))).ToList();
        int[] Find(string[] wanted) => names.Where(n => wanted.Contains(n.name)).Select(n => n.i).ToArray();
        _impact = Find(ImpactNames); _gun = Find(GunNames); _foot = Find(FootNames); _door = Find(DoorNames); _siren = Find(SirenNames); _horn = Find(HornNames); _engine = Find(EngineNames);
        _hint = Find(HintNames); _air = Find(AirNames); _skid = Find(SkidNames);
        _names = new string[521];
        foreach (var n in names) if (n.i >= 0 && n.i < 521) _names[n.i] = n.name;

        var opts = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
        _model = new InferenceSession(Path.Combine(ModelDir, "yamnet.onnx"), opts);
        _input = _model.InputMetadata.Keys.First();
        _output = _model.OutputMetadata.First(o => o.Value.Dimensions.LastOrDefault() == 521).Key;
        Log.Write($"soundfx: model ready ({_impact.Length} impact, {_gun.Length} gunfire, {_siren.Length} siren, {_horn.Length} horn classes)");
        string checkModel = Path.Combine(CheckDir, "efficientat.onnx"), checkMap = Path.Combine(CheckDir, "efficientat_class_map.csv");
        if (File.Exists(checkModel) && File.Exists(checkMap))
        {
            try
            {
                var checkNames = File.ReadAllLines(checkMap).Skip(1).Select(l => l.Split(',', 3)).Where(p => p.Length == 3)
                    .Select(p => (i: int.Parse(p[0]), name: p[2].Trim().Trim('"'))).ToList();
                _checkImpact = checkNames.Where(n => ImpactNames.Contains(n.name)).Select(n => n.i).ToArray();
                // no memory arena: a check runs a few times a minute, and its ~10 s of work memory goes back at once
                _check = new InferenceSession(checkModel, new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, EnableCpuMemArena = false });
                long t0 = Stopwatch.GetTimestamp();
                Check(new float[CheckWindow]);
                Log.Write($"soundfx: blast check ready (EfficientAT, {_checkImpact.Length} impact classes, " +
                    $"{Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms a check)");
            }
            catch (Exception ex) { Log.Error("soundfx blast check", ex); _check?.Dispose(); _check = null; }
        }
        string clapModel = Path.Combine(ClapDir, "clap.onnx"), clapPrompts = Path.Combine(ClapDir, "clap_prompts.csv");
        if (File.Exists(clapModel) && File.Exists(clapPrompts))
        {
            try
            {
                var prompts = File.ReadAllLines(clapPrompts).Skip(1).Select(l => l.Split(',', 3)).Where(p => p.Length == 3).ToList();
                _prompts = prompts.Select(p => p[2].Trim().Trim('"')).ToArray();
                _crashIdx = prompts.Where(p => p[1] == "crash").Select(p => int.Parse(p[0])).ToArray();
                // two threads: the light waits for it; no memory arena: its ~10 s of work memory goes back after each check
                _clap = new InferenceSession(clapModel, new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, EnableCpuMemArena = false });
                long t0 = Stopwatch.GetTimestamp();
                Crash(new float[CheckWindow]);
                Log.Write($"soundfx: crash check ready (CLAP, {_crashIdx.Length} of {_prompts.Length} descriptions, " +
                    $"{Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F0} ms a check)");
            }
            catch (Exception ex) { Log.Error("soundfx crash check", ex); _clap?.Dispose(); _clap = null; }
        }
        _hear32 = _check != null || _clap != null;
        _hop = new Timer(_ => Classify(), null, HopMs, HopMs);
        // the model's ~10 ms a sound before the game's own work: a flash is late by what it waits
        _judge = new Thread(JudgeLoop) { IsBackground = true, Name = "soundfx judge", Priority = ThreadPriority.AboveNormal };
        _judge.Start();
        if (_check != null) _checker = new Worker("soundfx blast check", StaleMs);
        if (_clap != null) _crasher = new Worker("soundfx crash", CrashStaleMs);
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
            var cap = new Loopback(dev);
            cap.DataAvailable += (_, e) => Feed(e.Buffer, e.BytesRecorded, cap.WaveFormat);
            cap.RecordingStopped += (_, e) => { if (e.Exception != null) { Log.Error("soundfx capture", e.Exception); _deviceId = ""; } };
            cap.StartRecording();
            _capture = cap;
            Log.Write($"soundfx: listening to {dev.FriendlyName} ({cap.WaveFormat.SampleRate} Hz, {cap.WaveFormat.Channels} ch, read every {Loopback.BufferMs / 2} ms)");
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

    double _phase, _phase32; // resampler positions in input samples

    void Feed(byte[] buf, int bytes, WaveFormat fmt)
    {
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat && fmt.Encoding != WaveFormatEncoding.Extensible) return;
        if (fmt.BitsPerSample != 32) return;
        if (!_raised) { _raised = true; try { Thread.CurrentThread.Priority = ThreadPriority.Highest; } catch { } } // a few ms of work every 15 ms
        int ch = fmt.Channels, frames = bytes / 4 / ch;
        var lr = new List<(float l, float r)>(frames);
        var mono = Mono(buf, frames, ch, fmt.SampleRate, Rate, ref _phase, lr);
        var mono32 = _hear32 ? Mono(buf, frames, ch, fmt.SampleRate, CheckRate, ref _phase32) : null;
        Push(mono, mono32, lr);
    }

    /// <summary>Mono at the given rate, a box filter over one output period against aliasing; with lr, also the first
    /// two channels (left and right) the same way.</summary>
    static List<float> Mono(byte[] buf, int frames, int ch, int srcRate, int rate, ref double phase, List<(float l, float r)>? lr = null)
    {
        double step = srcRate / (double)rate;
        int avg = Math.Max(1, (int)Math.Round(step)), right = ch >= 2 ? 1 : 0;
        var mono = new List<float>(frames / Math.Max(1, (int)step) + 2);
        for (; phase < frames; phase += step)
        {
            int start = (int)phase;
            double sum = 0, l = 0, r = 0; int n = 0;
            for (int k = start; k < start + avg && k < frames; k++, n++)
            {
                for (int c = 0; c < ch; c++) sum += BitConverter.ToSingle(buf, (k * ch + c) * 4);
                if (lr != null) { l += BitConverter.ToSingle(buf, k * ch * 4); r += BitConverter.ToSingle(buf, (k * ch + right) * 4); }
            }
            mono.Add(n == 0 ? 0 : (float)(sum / (n * ch)));
            lr?.Add(n == 0 ? (0, 0) : ((float)(l / n), (float)(r / n)));
        }
        phase -= frames;
        return mono;
    }

    /// <summary>Plays a sound file into the effects in real time, as if Windows played it (--test soundfx-files).</summary>
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

    /// <summary>16 kHz mono samples from the loopback with their left and right, and the same sound at 32 kHz for the checks.</summary>
    void Push(List<float> mono, List<float>? mono32, List<(float l, float r)> lr)
    {
        List<(float[] around, float[]? around32, double rms, double side, long at, long block, double jump)>? ready = null;
        List<(float[] cut, double rms, double side, long at, double jump, double hint)>? crashCuts = null;
        List<(double rms, long at)>? fast = null;
        lock (_gate)
        {
            if (mono32 != null)
                foreach (float v in mono32) { _ring32[_write32] = v; _write32 = (_write32 + 1) % _ring32.Length; }
            for (int i = 0; i < mono.Count; i++)
            {
                float v = mono[i];
                _ring[_write] = v;
                _write = (_write + 1) % _ring.Length;
                _samples++;
                var (l, r) = i < lr.Count ? lr[i] : (v, v);
                Gate(v, l, r);
            }
            // onsets whose 100 ms after have come: cut them out for the model
            while (_onsets.Count > 0 && _samples - _onsets[0].at >= PostSamples)
            {
                var (at, rms, block, jump) = _onsets[0];
                _onsets.RemoveAt(0);
                if (Paused) continue;
                var around = new float[Window];                       // the second that ends 60 ms after the onset
                int aStart = (_write - Window + _ring.Length) % _ring.Length;
                for (int k = 0; k < Window; k++) around[k] = _ring[(aStart + k) % _ring.Length];
                float[]? around32 = mono32 != null ? Cut32() : null;   // the same second at 32 kHz, for the checks
                (ready ??= new()).Add((around, around32, rms, Side(block), at, block, jump));
            }
            // possible crashes whose 250 ms after have come: cut them out for CLAP
            while (_crashWait.Count > 0 && _samples - _crashWait[0].at >= CrashPostSamples)
            {
                var (at, block, rms, jump, hint) = _crashWait[0];
                _crashWait.RemoveAt(0);
                if (Paused || mono32 == null || _samples - at > CrashPostSamples + Rate / 10) continue; // came in late: skip
                (crashCuts ??= new()).Add((Cut32(), rms, Side(block), at, jump, hint));
            }
            if (_fastShots.Count > 0) { fast = new(_fastShots); _fastShots.Clear(); }
        }
        if (fast != null) foreach (var (rms, at) in fast) { _fast++; Shoot(rms, Environment.TickCount64, at, next: true); }
        if (ready != null) foreach (var (around, around32, rms, side, at, block, jump) in ready) Hand(around, around32, rms, side, at, block, jump);
        if (crashCuts != null && _crasher != null)
            foreach (var (cut, rms, side, at, jump, hint) in crashCuts) _crasher.Add(() => CrashCheck(cut, rms, side, at, jump, hint));
    }

    /// <summary>The last second at 32 kHz (under _gate).</summary>
    float[] Cut32()
    {
        var cut = new float[CheckWindow];
        int start = (_write32 - CheckWindow + _ring32.Length) % _ring32.Length;
        for (int k = 0; k < CheckWindow; k++) cut[k] = _ring32[(start + k) % _ring32.Length];
        return cut;
    }

    /// <summary>Every 10 ms block: the loudness for the silence gate, and an onset, a jump to 1.6 times the last 30 ms.</summary>
    void Gate(float v, float l, float r)
    {
        _block += v * v; _blockL += l * l; _blockR += r * r;
        if (++_blockN < Block) return;
        double rms = Math.Sqrt(_block / _blockN);
        _sides[_blockNo++ % _sides.Length] = (_blockL, _blockR);
        _block = _blockL = _blockR = 0; _blockN = 0;
        _peak = Math.Max(_peak * 0.95, rms);
        double before = 0; for (int i = 0; i < _recent.Length; i++) before += _recent[i];
        before /= _recent.Length;
        _recent[_recentN++ % _recent.Length] = rms;
        // 1.6 times the last 30 ms: within a burst the sound does not fall far between shots (1.8 over 50 ms missed
        // most shots of a burst in GTA 5, 16.11)
        if (rms < 0.01 || rms < 1.6 * before || _samples - _lastOnset < OnsetGap) return;
        _lastOnset = _samples;
        // within 300 ms of a shot, a sound at least 40 % as loud is the next shot of the burst: it flashes now, without the model
        long tick = Environment.TickCount64;
        bool next; lock (_shotGate) next = !Paused && tick - _gunTick < 300 && rms >= 0.4 * _gunRms && tick - Interlocked.Read(ref _airTick) > AirKeepMs;
        if (next) { if (_fastShots.Count < 8) _fastShots.Add((rms, _samples - Block)); return; }
        if (_onsets.Count < 8) _onsets.Add((_samples - Block, rms, _blockNo - 1, rms / Math.Max(before, 1e-4)));   // from the start of the block that jumped
    }

    /// <summary>Where a sound came from, from its first block on (under _gate): +1 all left, -1 all right, 0 the middle.</summary>
    double Side(long fromBlock)
    {
        double l = 0, r = 0;
        for (long b = Math.Max(fromBlock, _blockNo - _sides.Length); b < _blockNo; b++) { var e = _sides[b % _sides.Length]; l += e.l; r += e.r; }
        return l + r > 1e-9 ? (l - r) / (l + r) : 0;
    }

    /// <summary>A cut for the model. (The next shots of a burst never get here: they flash on their onset, see Gate.)</summary>
    void Hand(float[] around, float[]? around32, double rms, double side, long at, long block, double jump)
    {
        long now = Environment.TickCount64;
        lock (_cuts)
        {
            _cutsMade++;
            if (_cuts.Count >= 4) { _cuts.Dequeue(); _late++; }
            _cuts.Enqueue(new Cut(around, around32, rms, side, at, block, jump, now));
        }
        _cutReady.Release();
    }

    void JudgeLoop()
    {
        while (!_stop)
        {
            _cutReady.Wait(1000);
            Cut cut;
            lock (_cuts)
            {
                if (_cuts.Count == 0) continue;
                cut = _cuts.Dequeue();
            }
            if (_stop) return;
            if (Environment.TickCount64 - cut.Tick > StaleMs) { _late++; continue; }
            try { Judge(cut); }
            catch (Exception ex) { Log.Error("soundfx judge", ex); }
        }
    }

    /// <summary>
    /// One sudden sound, judged on the second that ends 60 ms after it began: a shot blinks, a blast or a crash pulses,
    /// a footstep, a click, speech or music does nothing.
    /// </summary>
    void Judge(Cut cut)
    {
        var (around, around32, rms) = (cut.Around, cut.Around32, cut.Rms);
        string heardAs = $", loud {rms:F2}, side {cut.Side:+0.00;-0.00;0.00}";
        long t0 = Stopwatch.GetTimestamp();
        var h = Scores(around);
        double gun = h.Max(_gun), impact = h.Max(_impact), foot = h.Max(_foot), door = h.Max(_door), air = h.Max(_air);
        int top = h.Top();
        long now = Environment.TickCount64;
        if (air >= AirScore) Interlocked.Exchange(ref _airTick, now);
        double loudest; lock (_shotGate) loudest = _loudestShot;
        bool shot = gun >= GunScore && gun >= impact && gun > foot && rms >= ShotLoudness * loudest
            && (gun > air || now - Interlocked.Read(ref _airTick) > AirKeepMs && air < AirScore);
        bool checking = _check != null && around32 != null;
        double impactScore = checking ? ImpactProposal : ImpactScore;
        bool blast = !shot && impact >= impactScore && impact > gun && impact > foot && impact > door;
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
            if (now - _shotLogTick > 2000) { Log.Write($"soundfx: Shot («{_names[top]}», {_shotsSinceLog} more before{heardAs})"); _shotLogTick = now; _shotsSinceLog = 0; }
            else _shotsSinceLog++;
            Shoot(rms, now, cut.At);
            return;
        }
        // a hit in a race may be a crash: CLAP hears it too (a blast the check turns down may still be one). Under a loud
        // engine YAMNet may name a crash «Vehicle» only, so a sharp jump in loudness counts as well.
        if ((!blast || checking) && _clap != null && EngineHeardWithin(CrashEngineMs))
        {
            double hint = h.Max(_hint);
            bool racing = Racing, byHint = hint >= HintScore, byJump = racing || cut.Jump >= CrashJump;
            lock (_gate)
            {
                _raceJump = Math.Max(_raceJump, cut.Jump); _raceHint = Math.Max(_raceHint, hint);
                // in a race the crash thread's own queue keeps the newest two; elsewhere one a second at most
                if ((byHint || byJump) && (racing || now - _crashCandTick >= CrashEveryMs) && _crashWait.Count < 4)
                {
                    _crashCandTick = now;
                    if (byHint) _byHint++; else _byJump++;
                    _crashWait.Add((cut.At, cut.Block, rms, cut.Jump, hint));
                }
            }
        }
        if (!blast) return;
        if (!checking) { lock (_clipTops) _impacts++; Fire(SoundKind.Impact, impact, impactScore, now, _names[top], heardAs, cut.At); return; }
        string topName = _names[top]; long at = cut.At; var second = around32!;
        _checker!.Add(() =>
        {
            long c0 = Stopwatch.GetTimestamp();
            double check = Check(second);
            bool real = check >= CheckScore;
            lock (_clipTops)
            {
                _checks++; _checkMs += Stopwatch.GetElapsedTime(c0).TotalMilliseconds;
                if (real) _impacts++; else _vetoed++;
            }
            if (real) Fire(SoundKind.Impact, impact, impactScore, Environment.TickCount64, topName, $", check {check:F2}" + heardAs, at);
        });
    }

    /// <summary>CLAP on the second that ends 250 ms after a hit in a race (the crash thread).</summary>
    void CrashCheck(float[] cut, double rms, double side, long at, double jump, double hint)
    {
        long t0 = Stopwatch.GetTimestamp();
        var (crash, top) = Crash(cut);
        double score = Racing ? RaceCrashScore : CrashScore;
        lock (_clipTops)
        {
            _crashChecks++; _crashMs += Stopwatch.GetElapsedTime(t0).TotalMilliseconds; _crashMax = Math.Max(_crashMax, crash);
            if (crash >= score) _crashes++;
        }
        string heardAs = $", loud {rms:F2}, side {side:+0.00;-0.00;0.00}, jump {jump:F1}, hint {hint:F2}";
        if (crash >= score) Fire(SoundKind.Impact, crash, score, Environment.TickCount64, $"crash, {_prompts[top]}", heardAs, at);
        else if (crash >= CrashLogScore) Log.Write($"soundfx: crash check {crash:F2} («{_prompts[top]}»), no flash{heardAs}");
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
        for (int r = n; r < ClapSamples; r += n) Array.Copy(wave, 0, wave, r, Math.Min(n, ClapSamples - r));
        using var res = _clap!.Run(new[] { NamedOnnxValue.CreateFromTensor("wave", new DenseTensor<float>(wave, new[] { 1, ClapSamples })) }, new[] { "probs" });
        var p = res.First().AsTensor<float>();
        double crash = 0;
        foreach (int i in _crashIdx) crash += p[0, i];
        int top = 0;
        for (int i = 1; i < _prompts.Length; i++) if (p[0, i] > p[0, top]) top = i;
        return (crash, top);
    }

    /// <summary>EfficientAT's strongest explosion or breaking class in one second at 32 kHz, padded with 9 s of
    /// silence: it hears only 10 s clips.</summary>
    double Check(float[] second)
    {
        Array.Copy(second, _padded, CheckWindow);
        using var res = _check!.Run(new[] { NamedOnnxValue.CreateFromTensor("wave", new DenseTensor<float>(_padded, new[] { 1, CheckPadded })) });
        var v = res.First().AsTensor<float>();
        double m = 0;
        foreach (var i in _checkImpact) m = Math.Max(m, v[0, i]);
        return m;
    }

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
    void Shoot(double rms, long now, long at, bool next = false)
    {
        double strength;
        lock (_shotGate)
        {
            _loudestShot = Math.Max(rms, _loudestShot);
            strength = Math.Clamp(rms / _loudestShot, 0, 1);
            // a next shot keeps the burst's loudness: else 40 % of 40 % of... let echoes and others' fire carry a burst on
            _gunTick = now; _gunRms = next ? Math.Max(_gunRms, rms) : rms; _shots++;
        }
        Lag(at, shot: true);
        try { Effect?.Invoke(SoundKind.Shot, strength, rms); } catch (Exception ex) { Log.Error("soundfx effect", ex); }
    }

    /// <summary>How long after its start (a sample of the loopback) a sound flashed, for the minute's log. Windows' own
    /// output buffer and the light's frame come on top.</summary>
    void Lag(long at, bool shot)
    {
        if (at < 0) return;
        double ms = (Interlocked.Read(ref _samples) - at) * 1000.0 / Rate;
        lock (_clipTops)
            if (shot) { _lagShot += ms; _lagShots++; _lagShotMax = Math.Max(_lagShotMax, ms); }
            else { _lagHit += ms; _lagHits++; _lagHitMax = Math.Max(_lagHitMax, ms); }
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
            double impact = Max(_impact), gun = Max(_gun), siren = Max(_siren), horn = Max(_horn), skid = Max(_skid);
            long now = Environment.TickCount64;
            if (Max(_engine) > 0.3) Interlocked.Exchange(ref _engineTick, now);
            if (Max(_air) >= AirScore) Interlocked.Exchange(ref _airTick, now);
            double sirenStart = Racing ? RaceSirenScore : SirenScore;
            if (siren >= sirenStart && skid >= siren)
            {
                if (now - _sirenLogTick > 5000) { Log.Write($"soundfx: siren {siren:F2} under tires squealing {skid:F2}: no siren"); _sirenLogTick = now; }
                siren = 0;
            }
            Stats(scores, frames, impact, gun, siren, horn, now);
            // Sudden sounds are judged one by one as they come (Judge); a look would show them up to a second late.
            // a siren and a horn fire every look while they last: their lights follow the sound
            // a siren: two looks within 1.5 s to start (an alarm beeps on and off), then on while heard within 2.5 s, so it
            // neither flickers nor starts on a blip
            if (siren >= sirenStart)
            {
                if (now - _sirenHitTick <= 2000) _sirenOnUntil = now + 2500;
                else if (now - _sirenLogTick > 5000)
                {
                    int best = _siren[0]; double bestScore = 0;
                    for (int f = 0; f < frames; f++) foreach (var i in _siren) if (scores[f, i] > bestScore) { bestScore = scores[f, i]; best = i; }
                    Log.Write($"soundfx: siren heard once, {siren:F2} («{_names[best]}»), waiting for a second look");
                    _sirenLogTick = now;
                }
                _sirenHitTick = now;
            }
            if (siren >= SirenKeep && now < _sirenOnUntil) _sirenOnUntil = now + 2500;
            if (now < _sirenOnUntil) Fire(SoundKind.Siren, Math.Max(siren, SirenScore), SirenScore, now);
            if (horn > 0.4) Fire(SoundKind.Horn, horn, 0.4, now);
        }
        catch (Exception ex) { Log.Error("soundfx classify", ex); }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    void Stats(Tensor<float> scores, int frames, double impact, double gun, double siren, double horn, long now)
    {
        _hops++;
        lock (_shotGate) _loudestShot = Math.Max(0.05, _loudestShot * 0.9);  // the reference for shot strength forgets in about 5 s
        _sumImpact = Math.Max(_sumImpact, impact); _sumGun = Math.Max(_sumGun, gun); _sumSiren = Math.Max(_sumSiren, siren); _sumHorn = Math.Max(_sumHorn, horn);
        for (int i = 0; i < 521; i++)
        {
            double v = 0; for (int f = 0; f < frames; f++) v = Math.Max(v, scores[f, i]);
            if (v > _topScore) { _topScore = v; _top = _names[i] ?? i.ToString(); }
        }
        if (now - _statTick < 60_000) return; // a summary a minute, for tuning from the log
        Log.Write($"soundfx: {_hops} looks, max impact {_sumImpact:F2} gunfire {_sumGun:F2} siren {_sumSiren:F2} horn {_sumHorn:F2}, loudest «{_top}» {_topScore:F2}");
        lock (_clipTops)
        {
            if (_cutsMade > 0)
                Log.Write($"soundfx: {_cutsMade} sudden sounds, {_judged} judged ({(_judged > 0 ? _judgeMs / _judged : 0):F1} ms each), {_late} late, {_fast} fast-fire: " +
                    $"{_shots} shots, {_impacts} blasts" + (_checks > 0 ? $" ({_checks} checked, {_checkMs / _checks:F0} ms each, {_vetoed} not blasts)" : "") +
                    $"; max gunfire {_clipGun:F2} impact {_clipImpact:F2}; mostly " +
                    string.Join(", ", _clipTops.OrderByDescending(t => t.Value).Take(4).Select(t => $"«{t.Key}» {t.Value}")));
            int crashLate = _crasher?.TakeLate() ?? 0, checkLate = _checker?.TakeLate() ?? 0;
            if (_crashChecks > 0 || crashLate > 0)
                Log.Write($"soundfx: {_crashChecks} crash checks ({(_crashChecks > 0 ? _crashMs / _crashChecks : 0):F0} ms each), {crashLate} late, " +
                    $"{_crashes} crashes; max crash {_crashMax:F2}");
            if (_lagShots + _lagHits > 0 || checkLate > 0)
                Log.Write($"soundfx: lag from a sound's start to its flash: shots {(_lagShots > 0 ? _lagShot / _lagShots : 0):F0} ms " +
                    $"(max {_lagShotMax:F0}), blasts and crashes {(_lagHits > 0 ? _lagHit / _lagHits : 0):F0} ms (max {_lagHitMax:F0}); {checkLate} blast checks late");
            _lagShot = _lagShotMax = _lagHit = _lagHitMax = 0; _lagShots = _lagHits = 0;
            _cutsMade = _judged = _late = _fast = _shots = _impacts = _checks = _vetoed = 0; _clipGun = _clipImpact = _judgeMs = _checkMs = 0; _clipTops.Clear();
            _crashChecks = _crashes = 0; _crashMs = _crashMax = 0;
        }
        lock (_gate)
        {
            if (_byHint + _byJump > 0 || _raceJump > 0)
                Log.Write($"soundfx: race hits sent to CLAP: {_byHint} by a hint, {_byJump} by a jump; max jump {_raceJump:F1}, max hint {_raceHint:F2}");
            _byHint = _byJump = 0; _raceJump = _raceHint = 0;
        }
        _statTick = now; _hops = 0; _sumImpact = _sumGun = _sumSiren = _sumHorn = _topScore = 0; _top = "";
    }

    /// <summary>The strength is how far the score is over its threshold, 0..1: a faint sound gives a short dim light,
    /// a loud one a long bright one. The log keeps the model's own score, for tuning.</summary>
    void Fire(SoundKind kind, double score, double threshold, long now, string? heard = null, string? note = null, long at = -1)
    {
        lock (_fireGate)    // the judge, the blast check and the crash check may fire at once
        {
            if (kind == SoundKind.Impact)
            {
                if (now - _lastEffectTick < 700) return; // one flash per burst of explosions, not a strobe
                _lastEffectTick = now;
            }
            // a siren or a horn comes every look while it lasts: log only its start
            bool ongoing = kind is SoundKind.Siren or SoundKind.Horn;
            ref long last = ref _lastHeard[(int)kind];
            if (!ongoing || now - last > 2000) Log.Write($"soundfx: {kind} {score:F2}{(heard != null ? $" («{heard}»)" : "")}{note}");
            last = now;
        }
        if (kind == SoundKind.Impact) Lag(at, shot: false);
        double strength = Math.Clamp((score - threshold) / (1 - threshold), 0, 1);
        try { Effect?.Invoke(kind, strength, 0); } catch (Exception ex) { Log.Error("soundfx effect", ex); }
    }

    public void Dispose()
    {
        _hop.Dispose();
        StopCapture();
        _stop = true;
        _cutReady.Release();
        _judge.Join(2000);
        _checker?.Dispose();
        _crasher?.Dispose();
        _model.Dispose();
        _check?.Dispose();
        _clap?.Dispose();
    }

    /// <summary>The loopback of an output, read every 15 ms: NAudio's WasapiLoopbackCapture keeps a 100 ms buffer and
    /// reads it every 50 ms, so a sound reached the model up to 50 ms late.</summary>
    sealed class Loopback : WasapiCapture
    {
        public const int BufferMs = 30;
        public Loopback(MMDevice device) : base(device, false, BufferMs) { }
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() => AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
    }

    /// <summary>A slow check on a thread of its own, so the judge never waits for it: at most two wait, the older gives
    /// way, and one that waited too long is dropped (a late flash is worse than none).</summary>
    sealed class Worker : IDisposable
    {
        readonly Queue<(Action run, long tick)> _jobs = new();
        readonly SemaphoreSlim _ready = new(0);
        readonly Thread _thread;
        readonly int _staleMs;
        readonly string _name;
        volatile bool _stop;
        int _late;

        public Worker(string name, int staleMs)
        {
            _name = name; _staleMs = staleMs;
            _thread = new Thread(Loop) { IsBackground = true, Name = name };
            _thread.Start();
        }

        public void Add(Action run)
        {
            lock (_jobs)
            {
                if (_jobs.Count >= 2) { _jobs.Dequeue(); _late++; }
                _jobs.Enqueue((run, Environment.TickCount64));
            }
            _ready.Release();
        }

        public int TakeLate() { lock (_jobs) { int n = _late; _late = 0; return n; } }

        void Loop()
        {
            while (!_stop)
            {
                _ready.Wait(1000);
                (Action run, long tick) job;
                lock (_jobs)
                {
                    if (_jobs.Count == 0) continue;
                    job = _jobs.Dequeue();
                    if (Environment.TickCount64 - job.tick > _staleMs) { _late++; continue; }
                }
                if (_stop) return;
                try { job.run(); }
                catch (Exception ex) { Log.Error(_name, ex); }
            }
        }

        public void Dispose() { _stop = true; _ready.Release(); _thread.Join(2000); }
    }
}
