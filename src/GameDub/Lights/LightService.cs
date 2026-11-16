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
/// at most one event at a time, by priority: a sound effect flash, low battery, overheating, the charger, a game download.
/// Games listed in <see cref="AppSettings.LightsSkipGames"/>, and any game that takes the lamps itself through
/// Windows Dynamic Lighting, are left alone. Dark screen: lights off, except a running download.
/// Switched off in the settings, the tray disposes this, and Windows' own lighting effect comes back.
/// </summary>
public sealed class LightService : IDisposable
{
    // Colors as the stick LEDs show them: their green is far brighter than their red, so warm colors carry little
    // green (a light orange #FFB068 showed as green on the ROG Xbox Ally X, 16.11).
    static readonly Rgb Red = Rgb.Hex(0xFF0800), Orange = Rgb.Hex(0xFF2A00), Yellow = Rgb.Hex(0xFF6A00),
        Green = Rgb.Hex(0x10FF30), Blue = Rgb.Hex(0x0020FF);

    // Every effect rises softly, holds, then fades on a curve that looks even to the eye, 30 frames a second.
    const int FrameMs = 33, SirenMs = 2400, SirenPeriod = 1200;
    static readonly Pulse ImpactPulse = new(120, 600, 900), HornPulse = new(100, 350, 700), ChargePulse = new(150, 600, 900),
        SirenEnvelope = new(150, SirenMs - 550, 400);

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

    readonly StickLights _lights = new();
    readonly System.Threading.Timer _frame, _slow;
    readonly DisplayWatcher _display;
    AppSettings _s = new();
    SoundFx? _sound;
    readonly object _gate = new();

    // one-shot effect
    long _fxStart; int _fxMs; SoundKind? _fx; double _fxStrength;
    // states
    volatile bool _downloading, _downloaded, _batteryLow, _hot, _skip, _charging;
    bool _skipShown;
    long _chargeFlashStart = -1;
    Rgb _accent = Rgb.Hex(0x0078D4);
    // download detection, like the console's own keep-awake: > 500 KB/s for 20 s
    long _rxPrev = -1; int _rxHits; long _busyTick;
    long _lastInputTick; uint _lastPadPacket;
    long _heatTick;
    string _demo = ""; long _demoUntil;
    Rgb _logged = new(-1, -1, -1); long _loggedAt;
    static string Hex8(Rgb c) => $"#{(int)(Math.Clamp(c.R, 0, 1) * 255):X2}{(int)(Math.Clamp(c.G, 0, 1) * 255):X2}{(int)(Math.Clamp(c.B, 0, 1) * 255):X2}";

    public LightService()
    {
        _display = new DisplayWatcher();
        _accent = ReadAccent();
        _frame = new System.Threading.Timer(_ => Frame(), null, 500, FrameMs);
        _slow = new System.Threading.Timer(_ => Slow(), null, 1000, 1000);
        Log.Write("lights: service started");
    }

    public string Describe() => _lights.Describe() + (_sound != null ? "; sound effects on" : SoundFx.ModelPresent ? "" : "; no yamnet model");

    public void Apply(AppSettings s)
    {
        lock (_gate)
        {
            _s = s;
            bool wantSound = s.LightsOn && (s.LightsSound || s.LightsRumble) && SoundFx.ModelPresent;
            if (wantSound && _sound == null)
            {
                try { _sound = new SoundFx(); _sound.Effect += OnSound; _sound.FollowDefaultDevice(); }
                catch (Exception ex) { Log.Error("soundfx start", ex); _sound = null; }
            }
            else if (!wantSound && _sound != null) { _sound.Dispose(); _sound = null; }
        }
    }

    void OnSound(SoundKind kind, double strength)
    {
        AppSettings s; lock (_gate) s = _s;
        if (s.LightsRumble && !_skip) Rumble.Pulse(kind, strength);
        if (!s.LightsSound) return;
        lock (_gate)
        {
            _fx = kind; _fxStrength = strength; _fxStart = Environment.TickCount64;
            _fxMs = kind switch { SoundKind.Impact => ImpactPulse.Total, SoundKind.Siren => SirenMs, _ => HornPulse.Total };
        }
    }

    /// <summary>Every second: things that change slowly.</summary>
    void Slow()
    {
        try
        {
            AppSettings s; lock (_gate) s = _s;
            long now = Environment.TickCount64;
            _sound?.FollowDefaultDevice();
            if (Input.Touched(ref _lastInputTick, ref _lastPadPacket)) _downloaded = false; // XInput is slow for empty slots: once a second
            if (now % 60000 < 1000) _accent = ReadAccent();
            _skip = s.LightsSkipGames.Count > 0 && ForegroundExe() is string exe && s.LightsSkipGames.Any(g => exe.Equals(g, StringComparison.OrdinalIgnoreCase));

            // download: received bytes over all live adapters
            long rx = 0;
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    try { rx += n.GetIPStatistics().BytesReceived; } catch { }
            if (_rxPrev >= 0) { _rxHits = rx - _rxPrev > 500 * 1024 ? _rxHits + 1 : 0; if (_rxHits >= 20) _busyTick = now; }
            _rxPrev = rx;
            bool was = _downloading;
            _downloading = s.LightsDownload && _busyTick > 0 && now - _busyTick < 120_000;
            if (was && !_downloading && s.LightsDownload) _downloaded = true;  // green until he touches the console
            if (!s.LightsDownload) _downloaded = false;

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
    /// (impact, siren, horn, battery, charge, download, downloaded, heat). The file is deleted when read.
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
                case "siren": OnSound(SoundKind.Siren, 1); break;
                case "horn": OnSound(SoundKind.Horn, 1); break;
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
                if (!_skipShown) { _lights.Show(_accent.Scale(s.LightsBase / 100.0), _accent.Scale(s.LightsBase / 100.0)); _skipShown = true; }
                return;
            }
            _skipShown = false;
            if (!_lights.Available) return;           // Windows gave the lamps to the foreground app

            var (l, r) = Render(s, now);
            if (l != _logged && now - _loggedAt > 500)
            {
                _logged = l; _loggedAt = now;
                Log.Write($"lights: show {Hex8(l)} / {Hex8(r)} (dark={_display.Off}, downloading={_downloading}, done={_downloaded}, low={_batteryLow})");
            }
            _lights.Show(l, r);
        }
        catch (Exception ex) { Log.Error("lights frame", ex); }
    }

    (Rgb l, Rgb r) Render(AppSettings s, long now)
    {
        double t = now / 1000.0;
        Rgb idle = _accent.Scale(s.LightsBase / 100.0);
        bool dark = _display.Off;

        // 1. sound effect: over everything
        SoundKind? fx; long fxStart; int fxMs; double strength;
        lock (_gate) { fx = _fx; fxStart = _fxStart; fxMs = _fxMs; strength = _fxStrength; }
        if (fx != null && !dark && now - fxStart < fxMs)
        {
            long age = now - fxStart; double k = 0.5 + 0.5 * strength;
            switch (fx)
            {
                case SoundKind.Impact:
                    var c = Rgb.Mix(idle, Orange.Scale(k), ImpactPulse.At(age));
                    return (c, c);
                case SoundKind.Siren:
                    // police lights: red on the left and blue on the right swell and ebb in turn
                    double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * age / SirenPeriod), env = k * SirenEnvelope.At(age);
                    return (Rgb.Mix(idle, Red, env * w * w), Rgb.Mix(idle, Blue, env * (1 - w) * (1 - w)));
                default:
                    var h = Rgb.Mix(idle, Yellow.Scale(k), HornPulse.At(age));
                    return (h, h);
            }
        }
        // 2. battery under 10 % and overheating: slow breathing for 3 s of every 20
        bool cycle = (now / 1000) % 20 < 3 || now < _demoUntil; // a demo breathes all the time
        if (_batteryLow && cycle) { var c = Red.Scale(Breath(t, 3) * 0.8); return (c, c); }
        if (_hot && cycle) { var c = Orange.Scale(Breath(t, 3) * 0.7); return (c, c); }
        // 3. charger plugged in: one green flash
        if (_chargeFlashStart > 0 && now - _chargeFlashStart < ChargePulse.Total) { var c = Rgb.Mix(idle, Green, ChargePulse.At(now - _chargeFlashStart)); return (c, c); }
        // 4. downloads: blue breathing while it runs, green when it is done (until he touches the console)
        if (_downloading) { var c = Blue.Scale(0.1 + 0.3 * Breath(t, 4)); return (c, c); }
        if (_downloaded) { var c = Green.Scale(0.35); return (c, c); }
        // 5. idle glow; nothing on a dark screen
        return dark ? (Rgb.Off, Rgb.Off) : (idle, idle);
    }

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

    static string? ForegroundExe()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(h, out uint pid);
        var path = Native.GetProcessPath((int)pid);
        return path == null ? null : Path.GetFileNameWithoutExtension(path);
    }

    public void Dispose()
    {
        _frame.Dispose(); _slow.Dispose();
        _sound?.Dispose();
        _display.Dispose();
        _lights.Dispose();
    }
}

/// <summary>
/// A short rumble on the built-in gamepad for a sound effect: heavy and low for an impact, light for a horn,
/// nothing for a siren. Any app may set XInput vibration; it stops by itself after the pulse.
/// </summary>
static class Rumble
{
    static int _gen;

    public static void Pulse(SoundKind kind, double strength)
    {
        if (kind == SoundKind.Siren) return;
        int pad = -1;
        for (int i = 0; i < 4 && pad < 0; i++) if (Native.XInputGetState(i, out _) == 0) pad = i;
        if (pad < 0) return;
        double k = 0.4 + 0.6 * Math.Clamp(strength, 0, 1);
        var v = kind == SoundKind.Impact
            ? new Native.XINPUT_VIBRATION { wLeftMotorSpeed = (ushort)(50000 * k), wRightMotorSpeed = (ushort)(20000 * k) }
            : new Native.XINPUT_VIBRATION { wLeftMotorSpeed = 0, wRightMotorSpeed = (ushort)(18000 * k) };
        int ms = kind == SoundKind.Impact ? 180 : 120;
        int gen = Interlocked.Increment(ref _gen);
        Native.XInputSetState(pad, ref v);
        _ = System.Threading.Tasks.Task.Delay(ms).ContinueWith(_ =>
        {
            if (gen != _gen) return; // a newer pulse owns the motors
            var off = new Native.XINPUT_VIBRATION();
            Native.XInputSetState(pad, ref off);
        });
    }
}

/// <summary>Any keyboard, mouse or touch input (GetLastInputInfo) or gamepad activity (XInput packet number).</summary>
static class Input
{
    [StructLayout(LayoutKind.Sequential)] struct LastInput { public uint Size; public uint Time; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LastInput li);

    public static bool Touched(ref long lastInput, ref uint lastPad)
    {
        bool touched = false;
        var li = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        if (GetLastInputInfo(ref li)) { if (lastInput != 0 && li.Time != lastInput) touched = true; lastInput = li.Time; }
        // A real press, not the packet counter: the sticks' noise bumps that all the time.
        for (int i = 0; i < 4; i++)
        {
            if (Native.XInputGetState(i, out var st) != 0) continue;
            var g = st.Gamepad;
            if (g.wButtons != 0 || g.bLeftTrigger > 40 || g.bRightTrigger > 40 ||
                Math.Abs((int)g.sThumbLX) > 9000 || Math.Abs((int)g.sThumbLY) > 9000 || Math.Abs((int)g.sThumbRX) > 9000 || Math.Abs((int)g.sThumbRY) > 9000)
                touched = true;
        }
        return touched;
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
