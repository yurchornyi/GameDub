using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using GameDub.Core;
using GameDub.Pipeline;

namespace GameDub.Tray;

/// <summary>Background service: tray icon, process watching, hotkeys, OSD. Owns at most one pipeline.</summary>
public sealed class TrayApp : ApplicationContext
{
    public const string MutexName = @"Local\GameDub.Tray";
    const int HotkeyPause = 1, HotkeyRegion = 2;

    static Mutex? _mutex;

    readonly NotifyIcon _icon;
    readonly HotkeyWindow _hotkeys;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    readonly Osd _osd = new();
    readonly ProcessMatcher _matcher = new();
    readonly ToolStripMenuItem _pauseItem;
    AppSettings _s = new();
    List<GameEntry> _enabled = new();
    DateTime _cfgStamp, _gamesStamp;
    DubPipeline? _pipe;
    HashSet<int> _pids = new();
    volatile IntPtr _hwnd;
    RegionEditor? _editor;
    Lights.LightService? _lights;
    QuietLaunchers? _quiet;
    string _lastStatus = "";

    public static int Run()
    {
        _mutex = new Mutex(true, MutexName, out bool created);
        if (!created) return 0;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.Run(new TrayApp());
        return 0;
    }

    public static bool IsRunning()
    {
        if (!Mutex.TryOpenExisting(MutexName, out var m)) return false;
        m.Dispose();
        return true;
    }

    TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Відкрити GameDub", null, (_, _) => Process.Start(new ProcessStartInfo(Paths.Exe) { UseShellExecute = true }));
        _pauseItem = new ToolStripMenuItem("Пауза перекладу", null, (_, _) => TogglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add("Перевибрати область", null, (_, _) => EditRegion());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Вийти зі служби", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = Icon.ExtractAssociatedIcon(Paths.Exe) ?? SystemIcons.Application,
            Text = "GameDub — очікує гру",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => Process.Start(new ProcessStartInfo(Paths.Exe) { UseShellExecute = true });

        _hotkeys = new HotkeyWindow(id =>
        {
            Log.Write($"hotkey {(id == HotkeyPause ? "pause" : "region")}");
            if (id == HotkeyPause) TogglePause();
            else if (id == HotkeyRegion) EditRegion();
        });
        Reload(force: true);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
        Log.Write("tray started");
        System.Threading.Tasks.Task.Run(Autostart.Migrate); // an old Run-key autostart: the full screen experience skips it
    }

    void Reload(bool force = false)
    {
        var cs = Store.Stamp(Paths.ConfigFile);
        var gs = Store.Stamp(Paths.GamesFile);
        if (!force && cs == _cfgStamp && gs == _gamesStamp) return;
        bool settingsChanged = cs != _cfgStamp;
        _cfgStamp = cs; _gamesStamp = gs;
        _s = Store.LoadSettings();
        _enabled = Store.LoadGames().Where(g => g.Enabled && g.InstallDir.Length > 3).ToList();
        _hotkeys.Register(HotkeyPause, _s.HotkeyPause);
        _hotkeys.Register(HotkeyRegion, _s.HotkeyRegion);
        ApplyLights();
        ApplyQuiet();
        if (settingsChanged && _pipe != null) StopPipe(); // restarted with new settings on this tick
    }

    void Tick()
    {
        try
        {
            Reload();
            _quiet?.Tick();
            var hit = _enabled.Count == 0 ? null : _matcher.FindRunning(_enabled);
            if (hit == null)
            {
                if (_pipe != null) StopPipe();
            }
            else
            {
                _pids = hit.Value.pids;
                _hwnd = ProcessMatcher.MainWindow(_pids);
                if (_pipe == null || _pipe.Game.Id != hit.Value.game.Id || _pipe.Game.SubLang != hit.Value.game.SubLang)
                {
                    StopPipe();
                    _pipe = new DubPipeline(hit.Value.game, _s, () => _hwnd, ShowOsd);
                    _pipe.Start();
                }
            }
            UpdateStatus();
        }
        catch (Exception ex) { Log.Error("tick", ex); }
    }

    bool? _lightsWere; // LightsOn as last applied: Windows' side is handed over when it changes, and once at start when on

    void ApplyLights()
    {
        try
        {
            bool handOver = _lightsWere != _s.LightsOn && (_s.LightsOn || _lightsWere == true);
            if (_s.LightsOn)
            {
                if (handOver) Lights.LightingSetup.Hand(true);   // before the lamps are opened
                Lights.LightingSetup.GamesTakeOver(!_s.LightsOverGames);
                _lights ??= new Lights.LightService(); _lights.Apply(_s);
            }
            else
            {
                if (_lights != null) { _lights.Dispose(); _lights = null; }
                if (handOver) Lights.LightingSetup.Hand(false);  // Armoury's own colors back
                if (_lightsWere != null) Lights.LightingSetup.GamesTakeOver(true); // Windows' default back
            }
            _lightsWere = _s.LightsOn;
        }
        catch (Exception ex) { Log.Error("lights", ex); }
    }

    void ApplyQuiet()
    {
        try
        {
            if (_s.QuietLaunchers) _quiet ??= new QuietLaunchers();
            else
            {
                _quiet?.Dispose(); _quiet = null;
                QuietLaunchers.RestoreAutostart();   // nothing to do unless GameDub took some out of startup
            }
        }
        catch (Exception ex) { Log.Error("quiet launchers", ex); }
    }

    void StopPipe()
    {
        _editor?.Close();
        _pipe?.Dispose();
        _pipe = null;
        _hwnd = IntPtr.Zero;
    }

    void TogglePause()
    {
        if (_pipe == null) { ShowOsd("GameDub: гра з перекладом не запущена"); return; }
        _pipe.SetPaused(!_pipe.Paused);
        ShowOsd(_pipe.Paused ? "GameDub: пауза" : "GameDub: переклад увімкнено");
        UpdateStatus();
    }

    void EditRegion()
    {
        if (_editor != null) { _editor.Save(); return; }
        if (_pipe == null || _pipe.Window == IntPtr.Zero) { ShowOsd("GameDub: гра з перекладом не запущена"); return; }
        _editor = new RegionEditor(_pipe, ShowOsd);
        _editor.FormClosed += (_, _) => _editor = null;
        _editor.Show();
    }

    void ShowOsd(string text)
    {
        if (_osd.InvokeRequired) _osd.BeginInvoke(() => _osd.Flash(text, _hwnd));
        else _osd.Flash(text, _hwnd);
    }

    void UpdateStatus()
    {
        string state = _pipe == null ? "idle" : _pipe.Paused ? "paused" : "active";
        string status = $"{{\"state\":\"{state}\",\"game\":{System.Text.Json.JsonSerializer.Serialize(_pipe?.Game.Name ?? "")}}}";
        _pauseItem.Checked = _pipe?.Paused == true;
        if (status == _lastStatus) return;
        _lastStatus = status;
        _icon.Text = _pipe == null ? "GameDub — очікує гру" : $"GameDub — {(_pipe.Paused ? "пауза" : "перекладає")}: {Trim(_pipe.Game.Name, 30)}";
        try { System.IO.File.WriteAllText(Paths.StatusFile, status); } catch { }
    }

    static string Trim(string s, int n) => s.Length > n ? s[..n] : s;

    protected override void ExitThreadCore()
    {
        StopPipe();
        _lights?.Dispose();
        _quiet?.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _hotkeys.DestroyHandle();
        try { System.IO.File.WriteAllText(Paths.StatusFile, "{\"state\":\"off\",\"game\":\"\"}"); } catch { }
        Log.Write("tray stopped");
        base.ExitThreadCore();
    }
}

/// <summary>Hidden window receiving WM_HOTKEY.</summary>
sealed class HotkeyWindow : NativeWindow
{
    const int WM_HOTKEY = 0x0312;
    readonly Action<int> _onHotkey;

    public HotkeyWindow(Action<int> onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams());
    }

    const int Slots = 8;

    /// <summary>Registers one or more comma-separated combos ("Ctrl+Alt+P, Pause") under one action id.</summary>
    public void Register(int id, string combos)
    {
        for (int i = 0; i < Slots; i++) Native.UnregisterHotKey(Handle, id * Slots + i);
        var list = combos.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = 0; i < list.Length && i < Slots; i++)
        {
            if (!TryParse(list[i], out uint mods, out uint vk)) { Log.Write($"bad hotkey '{list[i]}'"); continue; }
            if (!Native.RegisterHotKey(Handle, id * Slots + i, mods | 0x4000, vk)) Log.Write($"hotkey '{list[i]}' is taken by another app");
        }
    }

    static bool TryParse(string combo, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        foreach (var part in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= 2; break;
                case "alt": mods |= 1; break;
                case "shift": mods |= 4; break;
                case "win": mods |= 8; break;
                default:
                    if (!Enum.TryParse<Keys>(part, true, out var k)) return false;
                    vk = (uint)k;
                    break;
            }
        }
        return vk != 0;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY) _onHotkey((int)m.WParam / Slots);
        base.WndProc(ref m);
    }
}
