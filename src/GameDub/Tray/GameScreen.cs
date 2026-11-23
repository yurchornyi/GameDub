using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using GameDub.Core;

namespace GameDub.Tray;

/// <summary>
/// The screen as close to an OLED as an LCD gets, while a game is in front: AMD's Vari-Bright at its strongest
/// (the display engine dims the backlight in dark scenes and raises the pixels, so black goes deeper) and colors a fifth
/// more saturated. On the desktop the owner's own settings come back: there Vari-Bright made the brightness drift on
/// white pages and the vivid colors shouted (owner, 19.11: «на робочому столі воно погано виглядає»). On the ROG Xbox
/// Ally X (BOE 7" IPS LCD) he saw black go deeper at Vari-Bright's top level, on battery and on the charger alike.
/// Driven through AMD's ADLX (amdadlx64.dll, comes with the driver); AMD's own «Vivid Gaming» color enhancement has no
/// ADLX call, so the saturation of «Custom Color» stands in for it. The desktop values are kept in data\screen-desktop.json
/// while a game look is on, so a crash or a restart mid-game still puts them back. Without an AMD display: nothing.
/// The game look stays on while the game is the window in front, and goes 6 s after it is not (an overlay or the
/// Xbox button's guide come and go). It comes on only after the game has been in front for 15 s: while a game starts it
/// sets up its full screen, and the display is left alone then (owner, 23.11: games started in a window).
/// </summary>
sealed class GameScreen : IDisposable
{
    const double SaturationBoost = 1.2;                 // «a fifth more»: AMD's own Vivid Gaming looked right to him (19.11)
    const int AwayTicks = 2;                            // ticks of 3 s without the game in front before the desktop look
    const int SettleTicks = 5;                          // ticks of 3 s with the game in front before the game look
    static string SavedFile => Path.Combine(Paths.Data, "screen-desktop.json");

    /// <summary>One display's settings that the game look changes.</summary>
    sealed record Look(bool VariBright, int Level, int Saturation);

    readonly Adlx? _adlx;
    bool _game;
    int _away, _front;

    public GameScreen()
    {
        _adlx = Adlx.Open();
        if (_adlx == null) { Log.Write("game screen: no AMD display settings (ADLX), off"); return; }
        if (File.Exists(SavedFile)) { Log.Write("game screen: a game look was left on (a crash or a restart): desktop look back"); Restore(); }
        Log.Write("game screen: on");
    }

    /// <summary>Every 3 s from the tray: is a game the window in front.</summary>
    public void Tick(bool gameInFront)
    {
        if (_adlx == null) return;
        if (gameInFront)
        {
            _away = 0;
            if (!_game && ++_front >= SettleTicks) { _game = true; GameLook(); }
        }
        else
        {
            _front = 0;
            if (_game && ++_away >= AwayTicks) { _game = false; Restore(); }
        }
    }

    void GameLook()
    {
        try
        {
            var desktop = new Dictionary<string, Look>();
            foreach (var d in _adlx!.Displays())
                using (d)
                {
                    if (!d.VariBrightSupported) continue;      // the built-in panel only: a TV has no Vari-Bright
                    var look = new Look(d.VariBrightEnabled, d.VariBrightLevel, d.Saturation);
                    desktop[d.Key] = look;
                    d.SetVariBright(true, 4);
                    int sat = d.SaturationSupported ? d.ClampSaturation((int)Math.Round(look.Saturation * SaturationBoost)) : look.Saturation;
                    if (d.SaturationSupported) d.SetSaturation(sat);
                    Log.Write($"game screen: game look on {d.Key}: Vari-Bright {(look.VariBright ? look.Level : "off")} → 4, saturation {look.Saturation} → {sat}");
                }
            if (desktop.Count == 0) { Log.Write("game screen: no display with Vari-Bright"); return; }
            Directory.CreateDirectory(Paths.Data);
            File.WriteAllText(SavedFile, JsonSerializer.Serialize(desktop));
        }
        catch (Exception ex) { Log.Error("game screen", ex); }
    }

    void Restore()
    {
        try
        {
            if (!File.Exists(SavedFile)) return;
            var desktop = JsonSerializer.Deserialize<Dictionary<string, Look>>(File.ReadAllText(SavedFile)) ?? new();
            foreach (var d in _adlx!.Displays())
                using (d)
                    if (desktop.TryGetValue(d.Key, out var look))
                    {
                        d.SetVariBright(look.VariBright, look.Level);
                        if (d.SaturationSupported) d.SetSaturation(look.Saturation);
                        Log.Write($"game screen: desktop look back on {d.Key}: Vari-Bright {(look.VariBright ? look.Level : "off")}, saturation {look.Saturation}");
                    }
            File.Delete(SavedFile);
        }
        catch (Exception ex) { Log.Error("game screen", ex); }
    }

    /// <summary>--test game-screen: what ADLX sees, then the game look for 5 s and back.</summary>
    public static void Test(System.Text.StringBuilder o)
    {
        using var screen = new GameScreen();
        var adlx = screen._adlx;
        if (adlx == null) { o.AppendLine("no ADLX"); return; }
        o.AppendLine($"ADLX {adlx.Version}");
        foreach (var d in adlx.Displays())
            using (d)
                o.AppendLine($"{d.Key}: Vari-Bright supported {d.VariBrightSupported}, on {d.VariBrightEnabled}, level {d.VariBrightLevel}; " +
                    $"saturation supported {d.SaturationSupported}, {d.Saturation} in {d.SaturationRange}");
        for (int i = 0; i < SettleTicks; i++) screen.Tick(true);
        foreach (var d in adlx.Displays()) using (d) o.AppendLine($"game look: {d.Key}: Vari-Bright on {d.VariBrightEnabled}, level {d.VariBrightLevel}, saturation {d.Saturation}");
        System.Threading.Thread.Sleep(5000);
        screen.Tick(false); screen.Tick(false);
        foreach (var d in adlx.Displays()) using (d) o.AppendLine($"desktop look: {d.Key}: Vari-Bright on {d.VariBrightEnabled}, level {d.VariBrightLevel}, saturation {d.Saturation}");
    }

    public void Dispose()
    {
        if (_adlx == null) return;
        if (_game || File.Exists(SavedFile)) Restore();
        _adlx.Dispose();
    }
}

/// <summary>
/// The few ADLX calls GameScreen needs, through its C interface: vtables of function pointers (SDK 1.5 headers,
/// GPUOpen-LibrariesAndSDKs/ADLX). Initialized with the driver's own ADLX version, so any newer driver works.
/// </summary>
sealed class Adlx : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll")] static extern bool FreeLibrary(IntPtr module);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int QueryFullVersionFn(out ulong version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitializeFn(ulong version, out IntPtr system);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TerminateFn();
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetFn(IntPtr self, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetForFn(IntPtr self, IntPtr display, out IntPtr result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint SizeFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AtFn(IntPtr self, uint index, out IntPtr item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetBoolFn(IntPtr self, [MarshalAs(UnmanagedType.U1)] out bool value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetBoolFn(IntPtr self, [MarshalAs(UnmanagedType.U1)] bool value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CallFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetIntFn(IntPtr self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetIntFn(IntPtr self, int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetRangeFn(IntPtr self, out IntRange range);
    [StructLayout(LayoutKind.Sequential)] public struct IntRange { public int Min, Max, Step; public override string ToString() => $"{Min}..{Max} step {Step}"; }

    // vtable slots (SDK 1.5, C interface)
    const int SystemGetDisplaysServices = 3;
    const int ServicesGetDisplays = 4, ServicesGetCustomColor = 16, ServicesGetVariBright = 19;
    const int ListSize = 3, ListAtDisplay = 11;
    const int Release = 1;
    const int VbIsSupported = 3, VbIsEnabled = 4, VbSetEnabled = 5, VbIsCurrentFirst = 6, VbSetFirst = 11;   // 5 levels each
    const int CcIsSaturationSupported = 7, CcGetSaturationRange = 8, CcGetSaturation = 9, CcSetSaturation = 10;

    readonly IntPtr _module, _system, _services;
    public string Version { get; }

    Adlx(IntPtr module, IntPtr system, IntPtr services, string version) { _module = module; _system = system; _services = services; Version = version; }

    public static Adlx? Open()
    {
        IntPtr module = LoadLibrary("amdadlx64.dll");
        if (module == IntPtr.Zero) return null;
        try
        {
            IntPtr q = GetProcAddress(module, "ADLXQueryFullVersion"), init = GetProcAddress(module, "ADLXInitialize");
            if (q == IntPtr.Zero || init == IntPtr.Zero) { FreeLibrary(module); return null; }
            if (Marshal.GetDelegateForFunctionPointer<QueryFullVersionFn>(q)(out ulong version) != 0) { FreeLibrary(module); return null; }
            int r = Marshal.GetDelegateForFunctionPointer<InitializeFn>(init)(version, out IntPtr system);   // 1, 2: already on
            if (r is < 0 or > 2 || system == IntPtr.Zero) { Log.Write($"adlx: initialize failed ({r})"); FreeLibrary(module); return null; }
            if (Fn<GetFn>(system, SystemGetDisplaysServices)(system, out IntPtr services) != 0 || services == IntPtr.Zero)
            {
                Terminate(module); FreeLibrary(module); return null;
            }
            string v = $"{version >> 48}.{(version >> 32) & 0xFFFF}.{(version >> 16) & 0xFFFF}.{version & 0xFFFF}";
            return new Adlx(module, system, services, v);
        }
        catch (Exception ex) { Log.Error("adlx", ex); FreeLibrary(module); return null; }
    }

    static T Fn<T>(IntPtr obj, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size));

    static void Free(IntPtr obj) { if (obj != IntPtr.Zero) Fn<ReleaseFn>(obj, Release)(obj); }

    static void Terminate(IntPtr module)
    {
        IntPtr t = GetProcAddress(module, "ADLXTerminate");
        if (t != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<TerminateFn>(t)();
    }

    /// <summary>The displays now (each to be disposed): a docked console has its TV too.</summary>
    public List<Display> Displays()
    {
        var list = new List<Display>();
        if (Fn<GetFn>(_services, ServicesGetDisplays)(_services, out IntPtr displays) != 0 || displays == IntPtr.Zero) return list;
        try
        {
            uint n = Fn<SizeFn>(displays, ListSize)(displays);
            for (uint i = 0; i < n; i++)
                if (Fn<AtFn>(displays, ListAtDisplay)(displays, i, out IntPtr d) == 0 && d != IntPtr.Zero)
                    list.Add(new Display(_services, d, i));
        }
        finally { Free(displays); }
        return list;
    }

    public void Dispose()
    {
        Free(_services);
        Terminate(_module);
        FreeLibrary(_module);
    }

    /// <summary>One display's Vari-Bright and custom color.</summary>
    public sealed class Display : IDisposable
    {
        readonly IntPtr _display, _vb, _cc;
        public string Key { get; }

        internal Display(IntPtr services, IntPtr display, uint index)
        {
            _display = display;
            Key = "display " + index;
            if (Fn<GetForFn>(services, ServicesGetVariBright)(services, display, out IntPtr vb) == 0) _vb = vb;
            if (Fn<GetForFn>(services, ServicesGetCustomColor)(services, display, out IntPtr cc) == 0) _cc = cc;
        }

        bool Bool(IntPtr obj, int slot) => obj != IntPtr.Zero && Fn<GetBoolFn>(obj, slot)(obj, out bool v) == 0 && v;

        public bool VariBrightSupported => Bool(_vb, VbIsSupported);
        public bool VariBrightEnabled => Bool(_vb, VbIsEnabled);

        /// <summary>0 maximize brightness, 1 optimize brightness, 2 balanced, 3 optimize battery, 4 maximize battery.</summary>
        public int VariBrightLevel
        {
            get { for (int i = 0; i < 5; i++) if (Bool(_vb, VbIsCurrentFirst + i)) return i; return -1; }
        }

        public void SetVariBright(bool on, int level)
        {
            if (_vb == IntPtr.Zero) return;
            Fn<SetBoolFn>(_vb, VbSetEnabled)(_vb, on);
            if (on && level is >= 0 and <= 4) Fn<CallFn>(_vb, VbSetFirst + level)(_vb);
        }

        public bool SaturationSupported => Bool(_cc, CcIsSaturationSupported);
        public int Saturation => _cc != IntPtr.Zero && Fn<GetIntFn>(_cc, CcGetSaturation)(_cc, out int v) == 0 ? v : 0;
        public IntRange SaturationRange => _cc != IntPtr.Zero && Fn<GetRangeFn>(_cc, CcGetSaturationRange)(_cc, out var r) == 0 ? r : default;
        public int ClampSaturation(int v) { var r = SaturationRange; return r.Max > r.Min ? Math.Clamp(v, r.Min, r.Max) : v; }
        public void SetSaturation(int v) { if (_cc != IntPtr.Zero) Fn<SetIntFn>(_cc, CcSetSaturation)(_cc, v); }

        public void Dispose() { Free(_vb); Free(_cc); Free(_display); }
    }
}
