using System;
using System.Collections.Generic;
using System.Linq;
using GameDub.Core;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using WinColor = Windows.UI.Color;

namespace GameDub.Lights;

/// <summary>
/// The joystick rings, as Windows Dynamic Lighting LampArrays. Lamps are split into a left and a right half
/// by their position, so each stick can show its own color. When Windows gives the lamps to another app
/// (a game with its own lighting), <see cref="LampArray.IsAvailable"/> turns false and nothing is sent.
/// </summary>
public sealed class StickLights : IDisposable
{
    sealed class Device
    {
        public required LampArray Lamps;
        public required int[] Left, Right;
        public (WinColor l, WinColor r)? Shown;
        public long ShownAt;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Device> _devices = new();
    readonly DeviceWatcher _watcher;

    public StickLights()
    {
        _watcher = DeviceInformation.CreateWatcher(LampArray.GetDeviceSelector());
        _watcher.Added += async (_, info) =>
        {
            try
            {
                var lamps = await LampArray.FromIdAsync(info.Id);
                if (lamps == null) return;
                var (left, right) = Split(lamps);
                lamps.AvailabilityChanged += (s, _) =>
                {
                    lock (_gate) if (_devices.TryGetValue(info.Id, out var d)) d.Shown = null;
                    Log.Write($"lights: {(s.IsAvailable ? "ours" : "taken by another app")}");
                };
                lock (_gate) _devices[info.Id] = new Device { Lamps = lamps, Left = left, Right = right };
                Log.Write($"lights: {info.Name}, {lamps.LampCount} lamps ({left.Length} left, {right.Length} right), available={lamps.IsAvailable}");
            }
            catch (Exception ex) { Log.Error("lights add", ex); }
        };
        _watcher.Removed += (_, upd) => { lock (_gate) _devices.Remove(upd.Id); };
        _watcher.Start();
    }

    public int Count { get { lock (_gate) return _devices.Count; } }

    Device[] Snapshot() { lock (_gate) return _devices.Values.ToArray(); }

    /// <summary>True when at least one device takes our colors right now. Asked outside the lock: a call to a device may
    /// hang after the console slept, and must not hold up the rest.</summary>
    public bool Available => Snapshot().Any(d => d.Lamps.IsAvailable);

    /// <summary>A color is being sent right now (for the watchdog's log).</summary>
    public bool Busy => _busy;
    volatile bool _busy;

    public string Describe()
    {
        var list = Snapshot();
        return list.Length == 0 ? "no LampArray devices" :
            string.Join("; ", list.Select(d => $"{d.Lamps.LampCount} lamps, kind={d.Lamps.LampArrayKind}, available={d.Lamps.IsAvailable}, enabled={d.Lamps.IsEnabled}"));
    }

    /// <summary>Shows one color per stick. Sends only what changed; skips devices another app holds.</summary>
    public void Show(Rgb left, Rgb right)
    {
        var l = left.ToWin(); var r = right.ToWin();
        _busy = true;
        try
        {
            foreach (var d in Snapshot())
            {
                try
                {
                    if (!d.Lamps.IsAvailable) { d.Shown = null; continue; }
                    // Unchanged colors go again every second: Armoury Crate writes its own color straight to the
                    // device, past Dynamic Lighting, and would otherwise stay (16.11 test).
                    long now = Environment.TickCount64;
                    if (d.Shown is { } s && s.l.Equals(l) && s.r.Equals(r) && now - d.ShownAt < 1000) continue;
                    if (l.Equals(r) || d.Left.Length == 0 || d.Right.Length == 0) d.Lamps.SetColor(l);
                    else
                    {
                        d.Lamps.SetColorsForIndices(Enumerable.Repeat(l, d.Left.Length).ToArray(), d.Left);
                        d.Lamps.SetColorsForIndices(Enumerable.Repeat(r, d.Right.Length).ToArray(), d.Right);
                    }
                    d.Shown = (l, r); d.ShownAt = now;
                }
                catch (Exception ex) { Log.Error("lights show", ex); d.Shown = null; }
            }
        }
        finally { _busy = false; }
    }

    /// <summary>Lamps left of the device's middle belong to the left stick, the rest to the right one.</summary>
    static (int[] left, int[] right) Split(LampArray lamps)
    {
        var xs = Enumerable.Range(0, lamps.LampCount).Select(i => (i, x: lamps.GetLampInfo(i).Position.X)).ToList();
        if (xs.Count < 2 || xs.Max(p => p.x) - xs.Min(p => p.x) < 1e-6) return (xs.Select(p => p.i).ToArray(), Array.Empty<int>());
        double mid = (xs.Max(p => p.x) + xs.Min(p => p.x)) / 2;
        return (xs.Where(p => p.x < mid).Select(p => p.i).ToArray(), xs.Where(p => p.x >= mid).Select(p => p.i).ToArray());
    }

    public void Dispose()
    {
        try { _watcher.Stop(); } catch { }
        Device[] old;
        lock (_gate) { old = _devices.Values.ToArray(); _devices.Clear(); }
        // the old links closed, so Windows does not keep one of them as ours (off this thread: a close may hang too)
        System.Threading.Tasks.Task.Run(() => { foreach (var d in old) try { (d.Lamps as IDisposable)?.Dispose(); } catch { } });
    }
}

/// <summary>A color with 0..1 channels, so effects can blend and scale it.</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public static readonly Rgb Off = new(0, 0, 0);
    public static Rgb Hex(uint rgb) => new(((rgb >> 16) & 255) / 255.0, ((rgb >> 8) & 255) / 255.0, (rgb & 255) / 255.0);
    public Rgb Scale(double k) => new(R * k, G * k, B * k);
    public static Rgb Mix(Rgb a, Rgb b, double t) { t = Math.Clamp(t, 0, 1); return new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t); }
    static byte B8(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    public WinColor ToWin() => WinColor.FromArgb(255, B8(R), B8(G), B8(B));
}
