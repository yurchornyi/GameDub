using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using GameDub.Core;
using Microsoft.Win32;

namespace GameDub.Tray;

/// <summary>
/// Keeps game launchers (Epic, Ubisoft, Rockstar, EA, Battle.net, GOG, Amazon) out of the way, as on a console
/// (owner, 16.11: on the Steam Deck nothing jumped over the game):
/// - no start with Windows (Windows' own startup switch, so Settings -> Apps -> Startup can turn it back on);
/// - while a game is in front, a launcher that takes the screen from it is minimized, and the launcher's other windows
///   that pop up (news, friends, offers) are minimized or, if small, hidden;
/// - a minute after the game ends, a launcher that started for it is closed, as on the Steam Deck (see
///   <see cref="CloseLaunchers"/>): asked first, ended if it does not go.
/// Steam is left alone: it already starts in the background for its games, and its keyboard is a window of its own.
/// A game is any full-screen window without a title bar of an app that is not Windows, a launcher or a tool. A launcher the player opens
/// on purpose (through the Xbox button, the task switcher) is not pushed back: only one that takes the screen straight
/// from the game is.
/// </summary>
sealed class QuietLaunchers : IDisposable
{
    // Install folders of the launchers (with their helpers and web views), and which launcher each is. Games live
    // elsewhere, except Ubisoft's.
    static readonly (string dir, string name)[] Groups =
    {
        (@"\Epic Games\Launcher\", "Epic"), (@"\Ubisoft Game Launcher\", "Ubisoft Connect"),
        (@"\Rockstar Games\Launcher\", "Rockstar"), (@"\Rockstar Games\Social Club\", "Rockstar"),
        (@"\Electronic Arts\EA Desktop\", "EA app"), (@"\Origin\", "EA app"), (@"\Battle.net\", "Battle.net"),
        (@"\GOG Galaxy\", "GOG Galaxy"), (@"\Amazon Games\App\", "Amazon Games"),
    };
    static readonly string[] LauncherDirs = Groups.Select(g => g.dir).ToArray();
    // Not games, even full screen: the Xbox full screen home, Game Bar, Windows itself, Armoury Crate. Microsoft's games
    // (Forza) are WindowsApps\Microsoft.<number> packages or live in XboxGames, so they still count.
    static readonly string[] NotGameDirs =
    {
        @"\WindowsApps\Microsoft.GamingApp", @"\WindowsApps\Microsoft.XboxGamingOverlay", @"\WindowsApps\Microsoft.Windows",
        @"\WindowsApps\MicrosoftWindows.", @"\ASUS\", "ArmouryCrate",
    };
    static string StateFile => Path.Combine(Paths.Data, "quiet-launchers.json");

    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0;
    const int SW_HIDE = 0, SW_MINIMIZE = 6, SW_SHOWMINNOACTIVE = 7;
    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const long WS_CAPTION = 0x00C00000, WS_EX_TOOLWINDOW = 0x80;

    readonly WinEventProc _proc;   // kept alive: Windows calls it
    readonly IntPtr _fgHook, _showHook;
    readonly Dictionary<int, string?> _paths = new();
    readonly Dictionary<int, long> _written = new();
    static readonly HashSet<string> Told = new();
    IntPtr _gameWnd, _lastFg;
    int _gamePid;
    DateTime _sessionStart, _ended;
    long _pathsTick, _autostartTick;

    public QuietLaunchers()
    {
        _proc = OnEvent;
        _fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        _showHook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
        Log.Write("quiet launchers: on");
        OnForeground(GetForegroundWindow());
        Tick();
    }

    /// <summary>Every few seconds from the tray: the game's end, closing launchers after it, autostart.</summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        if (now - _pathsTick > 60_000) { _paths.Clear(); _pathsTick = now; }   // pids get reused
        if (_autostartTick == 0 || now - _autostartTick > 10 * 60_000) { _autostartTick = now; DisableAutostart(); }

        if (_gamePid != 0 && !Alive(_gamePid))
        {
            Log.Write("quiet launchers: the game ended");
            _gamePid = 0; _gameWnd = IntPtr.Zero; _ended = DateTime.UtcNow;
            _written.Clear();
            foreach (var p in LauncherProcesses()) _written[p.pid] = Written(p.pid);
        }
        if (_gamePid == 0 && _ended != default && DateTime.UtcNow - _ended > TimeSpan.FromMinutes(1))
        {
            var since = _sessionStart;
            _ended = default; _sessionStart = default;
            CloseLaunchers(since);
        }
    }

    void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            if (ev == EVENT_SYSTEM_FOREGROUND) OnForeground(hwnd);
            else if (idObject == 0 && idChild == 0) OnShow(hwnd);
        }
        catch (Exception ex) { Log.Error("quiet launchers", ex); }
    }

    void OnForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        var path = PathOf(hwnd, out int pid);
        if (IsLauncher(path))
        {
            // Straight from the game: a pop-up, not the player (who comes through the Xbox button or the task switcher).
            if (_gamePid != 0 && _lastFg == _gameWnd && IsWindow(_gameWnd))
            {
                ShowWindowAsync(hwnd, SW_MINIMIZE);
                SetForegroundWindow(_gameWnd);
                Tell(path!, "jumped over the game: minimized");
                return;   // the game stays the last one in front
            }
        }
        else if (IsGameWindow(hwnd, path))
        {
            if (pid != _gamePid)
            {
                if (_sessionStart == default) _sessionStart = DateTime.UtcNow;
                _ended = default;
                _gamePid = pid;
                Log.Write($"quiet launchers: game {Path.GetFileName(path)}");
                MinimizeLaunchers();
            }
            _gameWnd = hwnd;
        }
        _lastFg = hwnd;
    }

    /// <summary>A launcher window shown while the game is in front, without taking the screen: put away.</summary>
    void OnShow(IntPtr hwnd)
    {
        if (_gamePid == 0 || GetForegroundWindow() != _gameWnd) return;
        if (GetAncestor(hwnd, 2) != hwnd || !IsWindowVisible(hwnd)) return;   // top-level only
        var path = PathOf(hwnd, out _);
        if (!IsLauncher(path) || !GetWindowRect(hwnd, out var r)) return;
        long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
        if (area <= 0) return;
        bool popup = (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0
            || (GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CAPTION) != WS_CAPTION;
        ShowWindowAsync(hwnd, popup ? SW_HIDE : SW_SHOWMINNOACTIVE);
        Tell(path!, popup ? "pop-up during the game: hidden" : "window during the game: minimized");
    }

    /// <summary>The game is in front: launcher windows go down, so none comes up over it later.</summary>
    void MinimizeLaunchers()
    {
        var pids = LauncherProcesses().Select(p => p.pid).ToHashSet();
        foreach (var (h, pid) in Native.TopLevelWindows())
            if (pids.Contains(pid) && !Native.IsIconic(h) && GetWindowLongPtr(h, GWL_STYLE).ToInt64() is var s && (s & WS_CAPTION) == WS_CAPTION)
                ShowWindowAsync(h, SW_SHOWMINNOACTIVE);
    }

    /// <summary>
    /// After the game, as on the Steam Deck: there a launcher lives in the game's Proton prefix and ends with the game,
    /// and the sign-ins were kept (owner, 16.11). A launcher that started for this game (within ten minutes before it:
    /// Rockstar signs in and patches first), is not in front and is not downloading is first asked to close as at
    /// Windows shutdown (Restart Manager). 16.11 Ubisoft Connect and the Rockstar launcher refused that (code 351), so
    /// whatever of it still runs 15 s later is ended. Only this user's processes: the launchers' services stay.
    /// </summary>
    void CloseLaunchers(DateTime sinceUtc)
    {
        int session = Process.GetCurrentProcess().SessionId;
        var fg = PidOf(GetForegroundWindow());
        var withWindows = new HashSet<int>();
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint p); withWindows.Add((int)p); return true; }, IntPtr.Zero);
        var groups = new Dictionary<string, List<(int pid, DateTime start)>>();
        foreach (var (pid, path) in LauncherProcesses())
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.SessionId != session) continue;
                var name = GroupOf(path)!;
                if (!groups.TryGetValue(name, out var list)) groups[name] = list = new();
                list.Add((pid, p.StartTime.ToUniversalTime()));
            }
            catch { }   // gone, or not ours to ask
        }
        var close = new List<(string name, List<(int pid, DateTime start)> procs)>();
        foreach (var (name, procs) in groups)
        {
            if (procs.Any(p => p.pid == fg)) { Tell(name, "in front: left"); continue; }
            var windowed = procs.Where(p => withWindows.Contains(p.pid)).ToList();
            var first = (windowed.Count > 0 ? windowed : procs).Min(p => p.start);
            if (first < sinceUtc - TimeSpan.FromMinutes(10)) { Tell(name, "opened before the game: left"); continue; }
            long wrote = procs.Sum(p => _written.TryGetValue(p.pid, out long w0) ? Math.Max(0, Written(p.pid) - w0) : 0);
            if (wrote > 5 * 1024 * 1024) { Tell(name, "downloading: left"); continue; }
            close.Add((name, procs));
        }
        if (close.Count == 0) return;
        Task.Run(() =>
        {
            try
            {
                var ask = close.SelectMany(c => c.procs).Where(p => withWindows.Contains(p.pid)).ToArray();
                int err = ask.Length > 0 ? Shutdown(ask) : 0;
                System.Threading.Thread.Sleep(15_000);
                foreach (var (name, procs) in close)
                {
                    var alive = procs.Where(p => Alive(p.pid)).ToList();
                    if (alive.Count == 0) { Log.Write($"quiet launchers: {name} closed after the game"); continue; }
                    int ended = 0;
                    foreach (var (pid, start) in alive)
                        try
                        {
                            using var pr = Process.GetProcessById(pid);
                            if (pr.StartTime.ToUniversalTime() != start) continue;   // the pid went to another process
                            pr.Kill();
                            ended++;
                        }
                        catch { }
                    Log.Write($"quiet launchers: {name} did not close by itself (code {err}): ended {ended} of {alive.Count} processes");
                }
            }
            catch (Exception ex) { Log.Error("quiet launchers close", ex); }
        });
    }

    // ---- autostart ----

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunKey32 = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    /// <summary>Launcher entries in Run: switched off in Windows (the startup switch), remembered to switch back on.</summary>
    static void DisableAutostart()
    {
        var done = Store.Load<List<string>>(StateFile);
        int before = done.Count;
        foreach (var (hive, run, approved) in RunPlaces())
        {
            try
            {
                using var k = hive.OpenSubKey(run);
                if (k == null) continue;
                foreach (var name in k.GetValueNames())
                {
                    if (k.GetValue(name) is not string cmd || !IsLauncher(cmd)) continue;
                    string id = $"{(hive == Registry.LocalMachine ? "HKLM" : "HKCU")}|{approved}|{name}";
                    using var a = hive.OpenSubKey(ApprovedRoot + approved, false);
                    if (a?.GetValue(name) is byte[] b && b.Length > 0 && (b[0] & 1) == 1) continue;   // already off
                    try
                    {
                        using var w = hive.CreateSubKey(ApprovedRoot + approved);
                        var v = new byte[12]; v[0] = 3;
                        BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(v, 4);
                        w.SetValue(name, v, RegistryValueKind.Binary);
                        if (!done.Contains(id)) done.Add(id);
                        Log.Write($"quiet launchers: no start with Windows for {name}");
                    }
                    catch (UnauthorizedAccessException) { Tell(name, "starts with Windows for all users; switch it off in Settings -> Apps -> Startup"); }
                }
            }
            catch (Exception ex) { Log.Error("quiet launchers autostart", ex); }
        }
        if (done.Count != before) Store.Save(StateFile, done);
    }

    /// <summary>The switch turned off: the launchers GameDub took out of startup go back.</summary>
    public static void RestoreAutostart()
    {
        if (!File.Exists(StateFile)) return;
        var done = Store.Load<List<string>>(StateFile);
        foreach (var id in done)
        {
            var parts = id.Split('|', 3);
            if (parts.Length != 3) continue;
            var hive = parts[0] == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            try
            {
                using var w = hive.OpenSubKey(ApprovedRoot + parts[1], true);
                if (w?.GetValue(parts[2]) is not byte[] b || b.Length == 0 || b[0] != 3) continue;   // switched by hand since
                w.SetValue(parts[2], new byte[12] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
                Log.Write($"quiet launchers: {parts[2]} starts with Windows again");
            }
            catch (Exception ex) { Log.Error("quiet launchers restore", ex); }
        }
        try { File.Delete(StateFile); } catch { }
    }

    static IEnumerable<(RegistryKey hive, string run, string approved)> RunPlaces()
    {
        yield return (Registry.CurrentUser, RunKey, "Run");
        yield return (Registry.LocalMachine, RunKey, "Run");
        yield return (Registry.LocalMachine, RunKey32, "Run32");
    }

    // ---- helpers ----

    static bool IsLauncher(string? path)
    {
        if (path == null) return false;
        if (path.Contains(@"\Ubisoft Game Launcher\games\", StringComparison.OrdinalIgnoreCase)) return false;
        return LauncherDirs.Any(d => path.Contains(d, StringComparison.OrdinalIgnoreCase));
    }

    static string? GroupOf(string? path) =>
        IsLauncher(path) ? Groups.First(g => path!.Contains(g.dir, StringComparison.OrdinalIgnoreCase)).name : null;

    /// <summary>A game is the window in front now (the same test as for the launchers; for the game screen).</summary>
    public static bool GameInFront()
    {
        var hwnd = GetForegroundWindow();
        int pid = PidOf(hwnd);
        return pid != 0 && IsGameWindow(hwnd, Native.GetProcessPath(pid));
    }

    static bool IsGameWindow(IntPtr hwnd, string? path)
    {
        if (path == null || IsLauncher(path)) return false;
        // A window with a title bar is not a game, even maximized over the whole screen (Bitwarden, 16.11).
        if ((GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CAPTION) == WS_CAPTION) return false;
        if (path.StartsWith(Path.GetDirectoryName(Paths.Exe) + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + "\\", StringComparison.OrdinalIgnoreCase)) return false;
        if (NotGameDirs.Any(d => path.Contains(d, StringComparison.OrdinalIgnoreCase))) return false;
        var exe = Path.GetFileNameWithoutExtension(path);
        if (exe.Equals("steam", StringComparison.OrdinalIgnoreCase) || exe.Equals("steamwebhelper", StringComparison.OrdinalIgnoreCase)) return false;
        // Full screen: the window covers its whole monitor (exclusive or borderless).
        if (!GetWindowRect(hwnd, out var r)) return false;
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return false;
        var m = mi.rcMonitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    string? PathOf(IntPtr hwnd, out int pid)
    {
        pid = PidOf(hwnd);
        if (pid == 0) return null;
        if (!_paths.TryGetValue(pid, out var path)) _paths[pid] = path = Native.GetProcessPath(pid);
        return path;
    }

    static int PidOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    static List<(int pid, string path)> LauncherProcesses()
    {
        var list = new List<(int, string)>();
        foreach (var p in Process.GetProcesses())
            using (p)
            {
                if (p.Id <= 4) continue;
                var path = Native.GetProcessPath(p.Id);
                if (IsLauncher(path)) list.Add((p.Id, path!));
            }
        return list;
    }

    static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    static long Written(int pid)
    {
        IntPtr h = Native.OpenProcess(0x1000, false, (uint)pid);
        if (h == IntPtr.Zero) return 0;
        try { return GetProcessIoCounters(h, out var c) ? (long)c.WriteBytes : 0; }
        finally { Native.CloseHandle(h); }
    }

    /// <summary>Once per launcher and action, so the log does not fill up.</summary>
    static void Tell(string path, string what)
    {
        string line = $"{Path.GetFileName(path)}: {what}";
        lock (Told) if (Told.Add(line)) Log.Write("quiet launchers: " + line);
    }

    /// <summary>Restart Manager, without the force flag: the apps get WM_QUERYENDSESSION / WM_ENDSESSION and close themselves.</summary>
    static int Shutdown((int pid, DateTime startUtc)[] procs)
    {
        var key = new StringBuilder(33);
        int err = RmStartSession(out uint session, 0, key);
        if (err != 0) return err;
        try
        {
            var apps = procs.Select(p =>
            {
                long ft = p.startUtc.ToFileTimeUtc();
                return new RM_UNIQUE_PROCESS { dwProcessId = p.pid, ProcessStartTime = new FILETIME { Low = (uint)ft, High = (uint)(ft >> 32) } };
            }).ToArray();
            err = RmRegisterResources(session, 0, null, (uint)apps.Length, apps, 0, null);
            if (err != 0) return err;
            uint count = 0;
            RmGetList(session, out _, ref count, IntPtr.Zero, out _);   // asked once before a shutdown, as documented
            return RmShutdown(session, 0, IntPtr.Zero);
        }
        finally { RmEndSession(session); }
    }

    public void Dispose()
    {
        if (_fgHook != IntPtr.Zero) UnhookWinEvent(_fgHook);
        if (_showHook != IntPtr.Zero) UnhookWinEvent(_showHook);
        Log.Write("quiet launchers: off");
    }

    // ---- native ----

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] struct FILETIME { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] struct RM_UNIQUE_PROCESS { public int dwProcessId; public FILETIME ProcessStartTime; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO mi);
    [DllImport("kernel32.dll")] static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint session, uint nFiles, string[]? files, uint nApps, RM_UNIQUE_PROCESS[] apps, uint nServices, string[]? services);
    [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint session, out uint needed, ref uint count, IntPtr info, out uint reasons);
    [DllImport("rstrtmgr.dll")] static extern int RmShutdown(uint session, uint flags, IntPtr callback);
}
