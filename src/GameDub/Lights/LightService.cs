using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using GameDub.Core;

namespace GameDub.Lights;

/// <summary>
/// Joystick lighting, owned by the tray service. A dim idle glow in the Windows accent color, and on top of it
/// at most one event at a time, by priority: a sound effect, low battery, overheating, the charger, a game download.
/// Games listed in <see cref="AppSettings.LightsSkipGames"/>, and any game that takes the lamps itself through
/// Windows Dynamic Lighting, are left alone, unless <see cref="AppSettings.LightsOverGames"/> keeps them ours.
/// Dark screen: lights off, except a running download.
/// The console on a TV (an external display active): lights and listening off, the sticks are out of sight.
/// No rumble of its own: games rumble themselves, and a second one on top was a bother (owner, 16.11).
/// Racing: brake lights and, with Forza's telemetry, a shift light and crashes. The sticks are as bright as the screen.
/// Shots: only the player's own, told by the right trigger (<see cref="MyShot"/>); others' shots stay dark.
/// Switched off in the settings, the tray disposes this, and Windows' own lighting effect comes back.
/// </summary>
public sealed class LightService : IDisposable
{
    // Colors as the stick LEDs show them: their green is far brighter than their red, so warm colors carry little
    // green (a light orange #FFB068 showed as green on the ROG Xbox Ally X, 16.11).
    static readonly Rgb Red = Rgb.Hex(0xFF0800), Orange = Rgb.Hex(0xFF2A00), Yellow = Rgb.Hex(0xFF6A00),
        Green = Rgb.Hex(0x10FF30), Blue = Rgb.Hex(0x0020FF);

    // Every effect rises softly and fades on a curve that looks even to the eye, 30 frames a second. No fixed lengths
    // (owner, 16.11): an impact is as long and as bright as it is hard (a touch 0.4 s, a heavy crash 1.6 s); a siren,
    // a horn light while they are heard and go out right after. Each effect has its own shape, so it reads even where
    // colors look alike: one pulse (impact), a quick blink (shot), steady yellow (horn), red and blue in turn (siren),
    // slow breathing (states).
    const int FrameMs = 33, SirenPeriod = 800, ShiftPeriod = 500, ShiftGap = 180;
    const int HeardHold = 700; // a sound heard again within this keeps its light on: the model looks every 480 ms
    // a shot is the player's own when the right trigger was pulled within 0.6 s before it is shown (the game, the sound
    // and the model take 0.1-0.25 s); a burst goes on while its shots come less than 1 s apart (the model misses some)
    const int FireMs = 600, BurstGapMs = 1000;
    const double MineLoudness = 0.4;   // with the aim held, the player's shot is at least this share of the loudest lately
    // both triggers on the middle of Z: held together, or both let go. A shot at least 60 % as loud as the player's own gun
    // (learned from his sure shots, forgotten by half in a minute) is his.
    const double MyGunShare = 0.55, FreshShare = 0.3;
    static readonly Pulse ChargePulse = new(150, 600, 900), ShiftBlink = new(20, 60, 80),
        ShotPulse = new(10, 30, 150); // one shot: a quick orange blink, so a burst reads as separate shots
    /// <summary>An impact by its strength 0..1: 0.4 s and dim for a touch, 1.6 s and bright for a heavy crash.</summary>
    static Pulse ImpactPulse(double s) => new(25 + (int)(25 * s), 80 + (int)(520 * s), 260 + (int)(640 * s)); // a quick rise: a slow one read as lag

    /// <summary>Brightness 0..1 of a one-shot effect over time: smooth rise, hold, then a squared fall
    /// (LED light looks linear to the eye only on a curve; a straight fall seems to hang, then drop).</summary>
    readonly record struct Pulse(int Rise, int Hold, int Fall)
    {
        public int Total => Rise + Hold + Fall;
        public double At(long age)
        {
            if (age < 0) return 0;
            if (age < Rise) { double x = age / (double)Rise; return x * x * (3 - 2 * x); }
            if (age < Rise + Hold) return 1;
            double f = 1 - (age - Rise - Hold) / (double)Fall;
            return f <= 0 ? 0 : f * f;
        }
    }

    // The lamps. After the console slept, Windows may keep the old link «taken» for good while it hands the lamps to a new
    // one (20.11: from 00:09 to 19:20 nothing lit, «lights: taken by another app», until the tray was restarted; a new link
    // was «ours» at once). So lamps taken for 30 s with the screen on are opened again, at most once a minute.
    volatile StickLights _lights = new();
    long _takenSince, _reopenTick, _reopenLogTick;
    readonly System.Threading.Timer _slow;
    // the frames: a thread of their own above normal, woken at once by a new effect (a timer of the thread pool waited up to
    // a frame, longer while a game kept the processor busy)
    readonly Thread _frame;
    readonly AutoResetEvent _kick = new(false);
    // A hang anywhere here ends in a restart of the tray (see Watch): 22.11 after the console slept a frame never ended,
    // and the slow check, once a second on the thread pool, piled up 4,700 waiting threads within a day.
    long _frameDone = Environment.TickCount64, _slowStart, _watchLast = Environment.TickCount64, _watchQuietUntil;
    int _slowBusy;
    volatile string _slowStage = "";
    readonly Thread _watch;
    volatile bool _closing;
    readonly DisplayWatcher _display;
    AppSettings _s = new();
    SoundFx? _sound;
    Telemetry? _telemetry;
    readonly object _gate = new();
    readonly PadInput _pad = new(); // the brake trigger and presses, read in the background (XInput reads zeros there)
    long _padSeen;                 // the pad's last activity as last looked at
    int _brakeHeldFrames, _brakeLitFrames; long _brakeStatTick; // for the log: the brake held and lit over a minute
    double _screen = 1; long _screenTick; // the screen's brightness, 0.1..1: the sticks follow it

    // one-shot effect
    long _fxStart; int _fxMs; SoundKind? _fx; double _fxStrength;
    long _shotStart; double _shotStrength; // the last shot: its own short pulse, over an impact's longer one
    long _mineTick, _darkLogTick; int _shotsMine, _shotsDark; // the player's last own shot; shots over a minute, for the log
    long _sureTick;                                           // his last shot told by the trigger or his gun's loudness
    double _myGun; long _myGunTick;                           // the loudness of the player's own gun, from his sure shots
    // a siren or a horn heard in the game: lit from its start until its until, moved on while it is heard
    long _sirenStart, _sirenUntil, _hornStart, _hornUntil; double _hornStrength;
    // states
    volatile bool _downloading, _downloaded, _batteryLow, _hot, _skip, _charging, _docked, _racingGame;
    int _dockLogged = -1;
    bool _skipShown;
    long _chargeFlashStart = -1;
    Rgb _accent = Rgb.Hex(0x0078D4);
    // download detection, like the console's own keep-awake: > 500 KB/s for 20 s
    long _rxPrev = -1; int _rxHits; long _busyTick, _launcherTick, _launcherWritten = -1;
    long _lastInputTick;
    long _heatTick;
    string _demo = ""; long _demoUntil;

    public LightService()
    {
        _display = new DisplayWatcher();
        _accent = ReadAccent();
        _frame = new Thread(() =>
        {
            _kick.WaitOne(500);
            while (!_closing) { Frame(); Interlocked.Exchange(ref _frameDone, Environment.TickCount64); _kick.WaitOne(FrameMs); }
        }) { IsBackground = true, Name = "lights frame", Priority = ThreadPriority.AboveNormal };
        _frame.Start();
        _watch = new Thread(() => { while (!_closing) { Thread.Sleep(5000); Watch(); } }) { IsBackground = true, Name = "lights watchdog" };
        _watch.Start();
        _slow = new System.Threading.Timer(_ => Slow(), null, 1000, 1000);
        Log.Write("lights: service started");
    }

    public string Describe() => _lights.Describe() + (_sound != null ? "; sound effects on" : SoundFx.ModelPresent ? "" : "; no yamnet model");

    public void Apply(AppSettings s)
    {
        lock (_gate)
        {
            _s = s;
            bool wantSound = s.LightsOn && s.LightsSound && SoundFx.ModelPresent;
            if (wantSound && _sound == null)
            {
                try { _sound = new SoundFx(); _sound.Effect += OnSound; _sound.FollowDefaultDevice(); }
                catch (Exception ex) { Log.Error("soundfx start", ex); _sound = null; }
            }
            else if (!wantSound && _sound != null) { _sound.Dispose(); _sound = null; }

            bool wantRace = s.LightsOn && s.LightsRacing;
            if (wantRace && _telemetry == null)
            {
                try { _telemetry = new Telemetry(); _telemetry.Crash += OnCrash; }
                catch (Exception ex) { Log.Error("telemetry start", ex); _telemetry = null; }
            }
            else if (!wantRace && _telemetry != null) { _telemetry.Dispose(); _telemetry = null; }
        }
    }

    /// <summary>A crash in Forza's telemetry: the impact light.</summary>
    void OnCrash(double strength)
    {
        if (_docked) return;
        lock (_gate) { _fx = SoundKind.Impact; _fxStrength = strength; _fxStart = Environment.TickCount64; _fxMs = ImpactPulse(strength).Total; }
        _kick.Set();
    }

    void OnSound(SoundKind kind, double strength, double loud = 0)
    {
        if (_docked) return;                  // on a TV: the sticks are out of sight and the pad in hand is another one
        AppSettings s; lock (_gate) s = _s;
        long now = Environment.TickCount64;
        if (!s.LightsSound) return;
        // With the game's own data coming in (Forza), only it shows crashes: a crash sound may be an AI car's
        if (kind == SoundKind.Impact && _telemetry?.Live == true) return;
        lock (_gate)
        {
            switch (kind)
            {
                case SoundKind.Shot:
                    if (!MyShot(now, strength, loud)) return;
                    _shotStart = now; _shotStrength = strength;
                    break;
                case SoundKind.Siren:
                    // police lights while the siren wails, out right after it
                    if (now >= _sirenUntil) _sirenStart = now;
                    _sirenUntil = now + Math.Max(HeardHold, SirenPeriod);
                    break;
                case SoundKind.Horn:
                    // yellow while the horn sounds: a beep is short, a long honk stays on, as bright as it is loud
                    if (now >= _hornUntil) _hornStart = now;
                    _hornUntil = now + HeardHold; _hornStrength = strength;
                    break;
            }
            if (kind != SoundKind.Impact) { _kick.Set(); return; }
            _fx = kind; _fxStrength = strength; _fxStart = now;
            _fxMs = ImpactPulse(strength).Total;
        }
        _kick.Set();
    }

    /// <summary>
    /// A shot is shown only when it is the player's own: the right trigger, fire in shooters, was pulled within 0.6 s
    /// (others' shots, NPCs' and enemies', stay dark). The Xbox-style pad shows both triggers on one axis, so fire with
    /// the aim held (the left trigger) is only a fall of that axis, like letting the aim go: a shot right after a fall
    /// counts, and while the axis stays below the aim alone, the next shots count as long as they come less than 1 s apart
    /// (an automatic burst with both held). No pad to read (another kind of pad, or none has spoken yet): every shot, as
    /// before. In a car the right trigger is the gas (owner, 18.11: the police's shots lit while he drove): so a shot
    /// counts at once only within 0.6 s of pressing the right trigger down (fire); later in a long hold it must be as loud
    /// as his gun, like with the aim.
    /// Letting the aim go looks like that fall too, and the police shots right after it flashed (GTA Online, 18.11: «коли я
    /// постріляв, тоді блимає»), so with the aim in play a shot counts only when it is as loud as the player's own gun:
    /// at least 60 % of it (learned from his sure shots; the police's were up to half of it). A burst no longer carries
    /// itself on: the police's fire kept the lights going after he stopped («я постріляв і перестав, воно далі показувало»,
    /// 18.11). Before his gun is known: 40 % of the loudest shot lately, and a burst under 1 s apart. Under _gate.
    /// </summary>
    const double GunForgotten = 0.01;

    bool MyShot(long now, double strength, double loud)
    {
        var fire = _pad.RightTrigger(now, FireMs);
        bool strong = strength >= MineLoudness;
        double myGun = _myGun * Math.Pow(0.5, (now - _myGunTick) / 60_000.0);
        // After a few minutes without shooting the halving left his gun at 0.001: a bar of nothing, so others' shots and
        // echoes lit after his own (GTA, 23.11: «блимає з запізненням, коли я вже давно постріляв»), and the 2x cap kept it
        // from being learned again. Under 0.01 it is forgotten: the next sure shot sets it anew.
        if (myGun < GunForgotten) myGun = 0;
        // with both triggers held together Z stays in the middle (GTA Online, 18.11: most of the player's long bursts were
        // dark this way, «Held», as loud as the loudest): his gun's own loudness tells
        bool asMyGun = myGun > 0 && loud >= MyGunShare * myGun;
        bool fresh = now - _pad.PressTick < FireMs;      // the right trigger pressed down just now: fire, not a held gas
        bool mine = fire switch
        {
            PadInput.Fire.Unknown => true,
            // a quiet shot right after his own (echoes in the city, a far shot) blinked on after he stopped (GTA, 19.11:
            // «як я постріляв, воно потім ще доганяло і блимало», 0.011 against his gun's 0.064): once his gun is known, a shot
            // just after a press needs a third of its loudness, later in a hold more than half
            PadInput.Fire.Pulled => myGun > 0 ? loud >= (fresh ? FreshShare : MyGunShare) * myGun : fresh || strong,
            PadInput.Fire.Fell => myGun > 0 ? asMyGun : strong,
            // before his gun is known (the start of a game): the loudest shot lately, or a burst going on
            // the loudest shot lately counts too while he is surely shooting (within 2 s of a sure shot): in Call of Duty his own
            // gun ran from 0.08 to 0.54 (20.11), and the louder ones raised the bar over the rest
            PadInput.Fire.Held => myGun > 0 ? asMyGun || (strength >= 0.9 && now - _sureTick < 2000) : strength >= 0.8 || (strong && now - _mineTick < BurstGapMs),
            _ => false,
        };
        string how = $"trigger {fire}, Z {_pad.Z:F2}, pressed {(now - _pad.PressTick) / 1000.0:F1} s ago, strength {strength:F2}, loud {loud:F3}, own gun {myGun:F3}";
        if (mine)
        {
            // an average of his sure shots: the loudest one alone (a near blast read as a shot) set the bar too high (18.11)
            // his sure shots: right after a press (not a long hold, which may be the gas) or a fall
            // learned only from the loudest shots lately: a quiet far shot after a slip of the aim set it at 0.028 (19.11)
            // one shot moves it at most to twice the average: a single 0.54 lifted it from 0.19 to 0.42 (CoD, 20.11)
            if ((fire == PadInput.Fire.Pulled && fresh || fire == PadInput.Fire.Fell) && strength >= 0.7)
            {
                _myGun = myGun > 0 ? myGun + 0.2 * (Math.Min(loud, 2 * myGun) - myGun) : loud; _myGunTick = now;
            }
            else if (myGun > 0 && loud >= MyGunShare * myGun) _myGunTick = now; // still his gun: keep it in mind
            if (fire == PadInput.Fire.Pulled && fresh || fire == PadInput.Fire.Fell || asMyGun) _sureTick = now;
            if (now - _mineTick > 2000) Log.Write($"lights: the player's shot ({how})");
            _mineTick = now; _shotsMine++; return true;
        }
        _shotsDark++;
        if (now - _darkLogTick > 2000) { _darkLogTick = now; Log.Write($"lights: a shot left dark, not the player's ({how})"); }
        return false;
    }

    /// <summary>Lamps taken for 30 s while the screen is on: a fresh link to them (see _lights). A game that drives the lamps
    /// itself keeps them anyway; then this only tries again once a minute.</summary>
    void ReopenTakenLamps(long now)
    {
        if (_display.Off || _docked || _lights.Available || _lights.Count == 0) { _takenSince = 0; return; }
        if (_takenSince == 0) { _takenSince = now; return; }
        if (now - _takenSince < 30_000 || now - _reopenTick < 60_000) return;
        _reopenTick = now;
        var old = _lights;
        _lights = new StickLights();
        old.Dispose();
        if (now - _reopenLogTick > 10 * 60_000) { _reopenLogTick = now; Log.Write("lights: the lamps were taken for 30 s: opened again"); }
    }

    /// <summary>
    /// Every 5 s on its own thread: a frame that has not ended for 20 s, or a slow check that runs for a minute, hangs for
    /// good (a call to the lamps, the sound device or WMI that never comes back): the tray starts again. Time asleep does
    /// not count: after a gap in its own ticks the watch starts over.
    /// </summary>
    void Watch()
    {
        long now = Environment.TickCount64;
        long gap = now - _watchLast; _watchLast = now;
        if (gap > 15_000)
        {
            Interlocked.Exchange(ref _frameDone, now);
            Interlocked.Exchange(ref _slowStart, now);
            return;
        }
        if (now < _watchQuietUntil || _closing) return;
        long frame = now - Interlocked.Read(ref _frameDone);
        long slow = Volatile.Read(ref _slowBusy) == 1 ? now - Interlocked.Read(ref _slowStart) : 0;
        string? stuck = frame > 20_000 ? $"lights: a frame stuck for {frame / 1000} s{(_lights.Busy ? " in a call to the lamps" : "")}"
            : slow > 60_000 ? $"lights: the slow check stuck for {slow / 1000} s at «{_slowStage}»" : null;
        if (stuck == null) return;
        _watchQuietUntil = now + 10 * 60_000;
        Tray.TrayApp.Restart(stuck);
    }

    /// <summary>Every second: things that change slowly. One at a time: a call that hangs must not pile up threads.</summary>
    void Slow()
    {
        if (Interlocked.Exchange(ref _slowBusy, 1) == 1) return;
        Interlocked.Exchange(ref _slowStart, Environment.TickCount64);
        try { SlowWork(); }
        finally { Volatile.Write(ref _slowBusy, 0); }
    }

    void SlowWork()
    {
        try
        {
            AppSettings s; lock (_gate) s = _s;
            long now = Environment.TickCount64;
            _slowStage = "sound device";
            _sound?.FollowDefaultDevice();
            _slowStage = "displays";
            _docked = Dock.OnTv();
            _slowStage = "lamps";
            ReopenTakenLamps(now);
            _slowStage = "pad and front window";
            if ((_docked ? 1 : 0) != _dockLogged) { _dockLogged = _docked ? 1 : 0; Log.Write(_docked ? "lights: console on a TV, lights off" : "lights: handheld"); }
            if (_sound is { } snd) snd.Paused = _docked || _display.Off;
            long padAt = _pad.ActiveTick; bool padTouched = padAt != _padSeen; _padSeen = padAt;
            if (Input.Touched(ref _lastInputTick) || padTouched) _downloaded = false;
            if (now % 60000 < 1000) _accent = ReadAccent();
            // the brake: only in a racing game (its Steam genre), and the pad is read only while a race may be on (an engine
            // heard lately, no telemetry) or a press is awaited
            string? front = ForegroundPath();
            _racingGame = s.LightsRacing && RacingGames.IsRacing(front);
            if (_sound is { } fx) fx.Racing = _racingGame;   // in a race every sudden sound goes to the crash check
            bool race = _racingGame && _telemetry?.Live != true && _sound?.EngineHeardWithin(30_000) == true;
            bool shots = _sound != null && s.LightsSound && !_display.Off;   // the right trigger tells the player's own shots
            _pad.Listen(!_docked && (race || _downloaded || shots));
            if (now - _brakeStatTick > 60_000)
            {
                if (_brakeHeldFrames > 0) Log.Write($"race: a minute, brake held {_brakeHeldFrames * FrameMs / 1000.0:F1} s, lit {_brakeLitFrames * FrameMs / 1000.0:F1} s");
                lock (_gate)
                {
                    if (_shotsMine + _shotsDark > 0) Log.Write($"lights: a minute, {_shotsMine} shots shown as the player's, {_shotsDark} others' left dark");
                    _shotsMine = _shotsDark = 0;
                }
                _brakeStatTick = now; _brakeHeldFrames = _brakeLitFrames = 0;
            }
            _slowStage = "brightness";
            if (now - _screenTick > 3000)
            {
                // 0 % is the console's own download dimming (start.ps1): keep the last real brightness then
                _screenTick = now;
                if (Backlight.Percent() is int b && b > 0) _screen = Math.Clamp(b / 100.0, 0.1, 1);
            }
            _skip = !s.LightsOverGames && s.LightsSkipGames.Count > 0 && front != null &&
                s.LightsSkipGames.Any(g => Path.GetFileNameWithoutExtension(front).Equals(g, StringComparison.OrdinalIgnoreCase));

            // download: received bytes over all live adapters
            _slowStage = "network";
            long rx = 0;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    try { rx += n.GetIPStatistics().BytesReceived; } catch { }
            bool busy = _rxPrev >= 0 && rx - _rxPrev > 500 * 1024;
            _rxPrev = rx;
            // A game, not a video or a stream: a game launcher writes to disk too (in bursts, so within the last 10 s).
            // Looked at only while the network is busy: the process list costs a little.
            if (busy)
            {
                long written = Launchers.Written();
                if (_launcherWritten >= 0 && written - _launcherWritten > 200 * 1024) _launcherTick = now;
                _launcherWritten = written;
            }
            else _launcherWritten = -1;
            bool game = _launcherTick > 0 && now - _launcherTick < 10_000;
            _rxHits = busy && game ? _rxHits + 1 : 0;
            if (_rxHits >= 20) _busyTick = now;
            bool was = _downloading;
            _downloading = s.LightsDownload && _busyTick > 0 && now - _busyTick < 120_000;
            if (was && !_downloading && s.LightsDownload) _downloaded = true;  // green until he touches the console
            if (!s.LightsDownload) _downloaded = false;

            _slowStage = "battery and heat";
            var p = SystemInformation.PowerStatus;
            bool charging = p.PowerLineStatus == PowerLineStatus.Online;
            if (charging && !_charging && s.LightsBattery) _chargeFlashStart = now;
            _charging = charging;
            _batteryLow = s.LightsBattery && !charging && p.BatteryLifePercent is > 0 and < 0.1f;

            if (s.LightsHeat && now - _heatTick > 10_000) { _heatTick = now; _hot = Thermal.MaxCelsius() >= 85; }
            if (!s.LightsHeat) _hot = false;
            Demo(now);
        }
        catch (Exception ex) { Log.Error("lights slow", ex); }
    }

    /// <summary>
    /// Show one effect on request, for the owner to see them one by one: write its name to data\lights-demo.txt
    /// (impact, bump: a light one, siren, horn, brake, shift, battery, charge, download, downloaded, heat). The file is deleted when read.
    /// </summary>
    void Demo(long now)
    {
        string file = Path.Combine(Paths.Data, "lights-demo.txt");
        if (File.Exists(file))
        {
            string name = "";
            try { name = File.ReadAllText(file).Trim().ToLowerInvariant(); File.Delete(file); } catch { }
            Log.Write("lights: demo " + name);
            _demo = name; _demoUntil = now + 8000;
            switch (name)
            {
                case "impact": OnSound(SoundKind.Impact, 1); break;
                case "bump": OnSound(SoundKind.Impact, 0); break;
                case "siren": lock (_gate) { _sirenStart = now; _sirenUntil = now + 3 * SirenPeriod; } break;
                case "horn": lock (_gate) { _hornStart = now; _hornUntil = now + 1000; _hornStrength = 1; } break;
                case "charge": _chargeFlashStart = now; break;
                case "downloaded": _downloaded = true; break;
            }
        }
        if (now >= _demoUntil) return;
        if (_demo == "battery") _batteryLow = true;
        if (_demo == "heat") _hot = true;
        if (_demo == "download") _downloading = true;
    }

    /// <summary>30 times a second: the color of each stick. Only changes reach the device.</summary>
    void Frame()
    {
        try
        {
            AppSettings s; lock (_gate) s = _s;
            long now = Environment.TickCount64;
            if (_skip)                                // a game with its own lighting: leave the idle glow, then hands off
            {
                if (!_skipShown) { var g = _accent.Scale(s.LightsBase / 100.0 * _screen); _lights.Show(g, g); _skipShown = true; }
                return;
            }
            _skipShown = false;
            if (!_lights.Available) return;           // Windows gave the lamps to the foreground app

            var (l, r) = Render(s, now);
            _lights.Show(l.Scale(_screen), r.Scale(_screen));
        }
        catch (Exception ex) { Log.Error("lights frame", ex); }
    }

    (Rgb l, Rgb r) Render(AppSettings s, long now)
    {
        double t = now / 1000.0;
        Rgb idle = _accent.Scale(s.LightsBase / 100.0);
        bool dark = _display.Off;
        if (_docked) return (Rgb.Off, Rgb.Off);

        // 1. sound effect: over everything
        SoundKind? fx; long fxStart; int fxMs; double strength;
        lock (_gate) { fx = _fx; fxStart = _fxStart; fxMs = _fxMs; strength = _fxStrength; }
        long sAge; double sk; lock (_gate) { sAge = now - _shotStart; sk = _shotStrength; }
        double shot = !dark && sAge < ShotPulse.Total ? Felt(sk, 0.15) * ShotPulse.At(sAge) : 0;
        bool fxOn = fx != null && !dark && now - fxStart < fxMs;
        if (fxOn || shot > 0)
        {
            // an impact's pulse and a shot's blink, both orange: the brighter one shows
            double hit = fxOn ? Felt(strength, 0.25) * ImpactPulse(strength).At(now - fxStart) : 0;
            var c = Rgb.Mix(idle, Orange, Math.Max(hit, shot));
            return (c, c);
        }
        // 2. a siren or an alarm in the game: police lights, red on the left and blue on the right swell in turn
        long ss, su; lock (_gate) { ss = _sirenStart; su = _sirenUntil; }
        if (!dark && now < su)
        {
            long a = now - ss;
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * a / SirenPeriod), env = Math.Clamp(Math.Min(a / 150.0, (su - now) / 400.0), 0, 1);
            return (Rgb.Mix(idle, Red, env * w * w), Rgb.Mix(idle, Blue, env * (1 - w) * (1 - w)));
        }
        // 2a. a horn: steady yellow while it sounds, soft at both ends
        long bs, bu; double bk; lock (_gate) { bs = _hornStart; bu = _hornUntil; bk = _hornStrength; }
        if (!dark && now < bu)
        {
            double env = Math.Clamp(Math.Min((now - bs) / 60.0, (bu - now) / 250.0), 0, 1);
            var h = Rgb.Mix(idle, Yellow.Scale(0.5 + 0.5 * bk), env * env);
            return (h, h);
        }
        // 2b. racing: brake lights, and Forza's shift light
        if (!dark && s.LightsRacing && RaceLight(now) is Rgb race) return (race, race);
        // 3. battery under 10 % and overheating: slow breathing for 3 s of every 20
        bool cycle = (now / 1000) % 20 < 3 || now < _demoUntil; // a demo breathes all the time
        if (_batteryLow && cycle) { var c = Red.Scale(Breath(t, 3) * 0.8); return (c, c); }
        if (_hot && cycle) { var c = Orange.Scale(Breath(t, 3) * 0.7); return (c, c); }
        // 4. charger plugged in: one green flash
        if (_chargeFlashStart > 0 && now - _chargeFlashStart < ChargePulse.Total) { var c = Rgb.Mix(idle, Green, ChargePulse.At(now - _chargeFlashStart)); return (c, c); }
        // 5. downloads: blue breathing while it runs, green when it is done (until he touches the console)
        if (_downloading) { var c = Blue.Scale(0.1 + 0.3 * Breath(t, 4)); return (c, c); }
        if (_downloaded) { var c = Green.Scale(0.35); return (c, c); }
        // 6. idle glow; nothing on a dark screen
        return dark ? (Rgb.Off, Rgb.Off) : (idle, idle);
    }

    /// <summary>
    /// Racing lights. With Forza's telemetry coming in, only it counts (the trigger is not looked at, so nothing lights
    /// twice): "shift up" near the rev limit with the throttle down, blue double blinks; else the brake pedal. Without
    /// telemetry: the left trigger (read in the background through Raw Input, <see cref="PadInput"/>), but only in a
    /// racing game (<see cref="RacingGames"/>, its Steam genre) while an engine is heard, so aiming in a shooter never
    /// lights, even with a helicopter overhead.
    /// Red as hard as the brake is pressed, on a curve the eye reads as even: a light touch glows faintly, a full press
    /// is full red (a straight 0.3..1 looked the same at any pressure on the sticks, 16.11).
    /// </summary>
    Rgb? RaceLight(long now)
    {
        double brake = 0; bool shift = false;
        if (now < _demoUntil && _demo is ("brake" or "shift"))
        {
            shift = _demo == "shift";
            brake = 0.5 - 0.5 * Math.Cos(2 * Math.PI * now / 2000.0);
        }
        else if (_telemetry is { Live: true } tm) { brake = tm.Brake; shift = tm.Rev >= 0.93 && tm.Throttle > 0.5; }
        else
        {
            double held = _pad.LeftTrigger;
            if (held > 0.08) _brakeHeldFrames++;
            if (_racingGame && _sound?.EngineHeardWithin(8000) == true) brake = held; // the music may drown the engine for a few seconds
            if (brake > 0.08) _brakeLitFrames++;
        }
        if (shift)
        {
            long p = now % ShiftPeriod;
            return Blue.Scale(Math.Max(ShiftBlink.At(p), ShiftBlink.At(p - ShiftGap)));
        }
        return brake > 0.08 ? Red.Scale(Felt(brake, 0.08)) : null;
    }

    /// <summary>
    /// LED output for a share 0..1 that the eye should see as that share: the eye reads light roughly as its square root,
    /// so the output is the square, over a floor that still shows. Screen brightness and Armoury's LED level scale it after.
    /// </summary>
    static double Felt(double share, double floor) { double x = Math.Clamp(share, 0, 1); return floor + (1 - floor) * x * x; }

    /// <summary>Slow breathing 0..1, squared so it swells and ebbs evenly to the eye.</summary>
    static double Breath(double t, double period) { double b = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t / period); return b * b; }

    static Rgb ReadAccent()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            return new Rgb(c.R / 255.0, c.G / 255.0, c.B / 255.0);
        }
        catch { return Rgb.Hex(0x0078D4); }
    }

    static string? ForegroundPath()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(h, out uint pid);
        return Native.GetProcessPath((int)pid);
    }

    public void Dispose()
    {
        _closing = true; _kick.Set(); _frame.Join(1000);
        _slow.Dispose();
        _sound?.Dispose();
        _telemetry?.Dispose();
        _pad.Dispose();
        _display.Dispose();
        _lights.Dispose();
    }
}

/// <summary>Any keyboard, mouse or touch input (GetLastInputInfo). The gamepad is <see cref="PadInput"/>'s.</summary>
static class Input
{
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size; public uint Time; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput li);

    public static bool Touched(ref long lastInput)
    {
        var li = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (!GetLastInputInfo(ref li)) return false;
        bool touched = lastInput != 0 && li.Time != lastInput;
        lastInput = li.Time;
        return touched;
    }
}

/// <summary>
/// Bytes written to disk so far by the game launchers that are running (Steam, Epic, EA, Ubisoft, Battle.net, GOG,
/// Xbox). A download writes what it receives; a video or a stream in a browser does not. A process Windows does not
/// let a normal user read (a system service) counts as nothing.
/// </summary>
static class Launchers
{
    static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase) { "steam", "EpicGamesLauncher", "EADesktop",
        "EABackgroundService", "upc", "Agent", "Battle.net", "GalaxyClient", "GalaxyClientService", "gamingservices", "XboxPcApp" };
    [StructLayout(LayoutKind.Sequential)]
    struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    const uint QueryLimited = 0x1000;

    public static long Written()
    {
        long sum = 0;
        foreach (var p in Process.GetProcesses())   // one snapshot of the process list
            using (p)
            {
                if (!Names.Contains(p.ProcessName)) continue;
                IntPtr h = Native.OpenProcess(QueryLimited, false, (uint)p.Id);
                if (h == IntPtr.Zero) continue;
                try { if (GetProcessIoCounters(h, out var c)) sum += (long)c.WriteBytes; }
                finally { Native.CloseHandle(h); }
            }
        return sum;
    }
}

/// <summary>The built-in screen's brightness, 0..100 (WMI), or null when Windows does not tell (no built-in panel).</summary>
static class Backlight
{
    public static int? Percent()
    {
        try
        {
            using var q = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (System.Management.ManagementObject m in q.Get()) using (m) return Convert.ToInt32(m["CurrentBrightness"]);
        }
        catch { }
        return null;
    }
}

/// <summary>
/// The console on a TV or a monitor: an external display is active, alone or next to the built-in panel. Then the
/// sticks are out of sight and the game is played with another pad. Asks Windows for the active display paths.
/// </summary>
static class Dock
{
    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] byte[] pathInfo, ref uint modes, [Out] byte[] modeInfo, IntPtr topology);
    const uint OnlyActivePaths = 2;
    // DISPLAYCONFIG_PATH_INFO is 72 bytes, DISPLAYCONFIG_MODE_INFO 64; the target's outputTechnology sits at byte 36
    const int PathSize = 72, ModeSize = 64, TechOffset = 36;
    // a built-in panel: INTERNAL, DISPLAYPORT_EMBEDDED, UDI_EMBEDDED
    const uint Internal = 0x80000000, DisplayPortEmbedded = 11, UdiEmbedded = 13;

    public static bool OnTv()
    {
        try
        {
            if (GetDisplayConfigBufferSizes(OnlyActivePaths, out uint np, out uint nm) != 0 || np == 0) return false;
            var paths = new byte[np * PathSize]; var modes = new byte[nm * ModeSize];
            if (QueryDisplayConfig(OnlyActivePaths, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
            for (int i = 0; i < np; i++)
            {
                uint tech = BitConverter.ToUInt32(paths, i * PathSize + TechOffset);
                if (tech != Internal && tech != DisplayPortEmbedded && tech != UdiEmbedded) return true;
            }
            return false;
        }
        catch { return false; }
    }
}

/// <summary>Highest thermal zone temperature that a normal user may read (performance counters via WMI).</summary>
static class Thermal
{
    public static double MaxCelsius()
    {
        try
        {
            using var q = new System.Management.ManagementObjectSearcher(@"root\CIMV2", "SELECT Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation");
            double max = 0;
            foreach (var o in q.Get()) max = Math.Max(max, Convert.ToDouble(o["Temperature"]) - 273.15);
            return max;
        }
        catch { return 0; }
    }
}

/// <summary>Screen on or off (GUID_CONSOLE_DISPLAY_STATE), from a hidden window on the tray's UI thread.</summary>
sealed class DisplayWatcher : NativeWindow, IDisposable
{
    static Guid ConsoleDisplayState = new("6fe69556-704a-47a0-8f24-c28d936fda47");
    [DllImport("user32.dll")] static extern IntPtr RegisterPowerSettingNotification(IntPtr h, ref Guid g, int flags);
    [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] struct PowerSetting { public Guid Guid; public int Length; public int Data; }

    readonly IntPtr _reg;
    public volatile bool Off;

    public DisplayWatcher()
    {
        CreateHandle(new CreateParams());
        _reg = RegisterPowerSettingNotification(Handle, ref ConsoleDisplayState, 0);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_POWERBROADCAST = 0x218, PBT_POWERSETTINGCHANGE = 0x8013;
        if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            var ps = Marshal.PtrToStructure<PowerSetting>(m.LParam);
            if (ps.Guid == ConsoleDisplayState) Off = ps.Data == 0;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_reg != IntPtr.Zero) UnregisterPowerSettingNotification(_reg);
        DestroyHandle();
    }
}
