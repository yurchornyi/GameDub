using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameDub.Core;

public static class GameScanner
{
    static readonly HashSet<string> SkipSteamIds = new() { "228980", "1070560", "1391110", "1628350", "1493710" };

    /// <summary>Scans launchers and merges results, keeping selection state, ordering and manual games.</summary>
    public static List<GameEntry> ScanAndMerge()
    {
        var saved = Store.LoadGames().ToDictionary(g => g.Id);
        var found = new List<GameEntry>();
        Try("steam", () => found.AddRange(Steam()));
        Try("epic", () => found.AddRange(Epic()));
        Try("xbox", () => found.AddRange(Xbox()));

        var result = new List<GameEntry>();
        foreach (var g in found.GroupBy(g => g.Id).Select(x => x.First()))
        {
            if (saved.TryGetValue(g.Id, out var old))
            {
                g.Enabled = old.Enabled;
                g.LastEnabledUtc = old.LastEnabledUtc;
                g.SubLang = old.SubLang;
            }
            result.Add(g);
        }
        result.AddRange(saved.Values.Where(g => g.Source == "manual" && result.All(r => r.Id != g.Id)));
        Store.SaveGames(result);
        return Store.LoadGames();
    }

    static void Try(string what, Action a)
    {
        try { a(); } catch (Exception ex) { Log.Error("scan " + what, ex); }
    }

    static IEnumerable<GameEntry> Steam()
    {
        var steam = (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)?.Replace('/', '\\');
        if (steam == null || !Directory.Exists(steam)) yield break;

        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
                libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));

        foreach (var lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                string id = Field(text, "appid"), name = Field(text, "name"), dir = Field(text, "installdir");
                if (id == "" || SkipSteamIds.Contains(id) || name.Contains("Redistributable") || name.StartsWith("Proton")) continue;
                var full = Path.Combine(apps, "common", dir);
                if (!Directory.Exists(full)) continue;
                yield return new GameEntry { Id = "steam:" + id, Name = name, Source = "steam", InstallDir = full, Cover = SteamCover(steam, id) };
            }
        }
    }

    static string Field(string acf, string key)
    {
        var m = Regex.Match(acf, "\"" + key + "\"\\s+\"(.*?)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    static string? SteamCover(string steam, string id)
    {
        var cache = Path.Combine(steam, "appcache", "librarycache");
        foreach (var p in new[] { Path.Combine(cache, $"{id}_library_600x900.jpg"), Path.Combine(cache, id, "library_600x900.jpg") })
            if (File.Exists(p)) return p;
        var dir = Path.Combine(cache, id);
        if (Directory.Exists(dir))
        {
            // Newer Steam keeps covers in hashed subfolders; the 600x900 portrait is "library_capsule".
            foreach (var pattern in new[] { "library_600x900*.jpg", "library_capsule*.jpg", "library_header*.jpg", "header*.jpg" })
            {
                var hit = Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
        }
        var header = Path.Combine(cache, $"{id}_header.jpg");
        return File.Exists(header) ? header : null;
    }

    static IEnumerable<GameEntry> Epic()
    {
        var dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (!Directory.Exists(dir)) yield break;
        foreach (var f in Directory.EnumerateFiles(dir, "*.item"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(f));
            var r = doc.RootElement;
            string name = r.TryGetProperty("DisplayName", out var n) ? n.GetString() ?? "" : "";
            string loc = r.TryGetProperty("InstallLocation", out var l) ? l.GetString() ?? "" : "";
            string app = r.TryGetProperty("AppName", out var a) ? a.GetString() ?? name : name;
            if (name != "" && Directory.Exists(loc))
                yield return new GameEntry { Id = "epic:" + app, Name = name, Source = "epic", InstallDir = loc };
        }
    }

    static IEnumerable<GameEntry> Xbox()
    {
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;
            foreach (var d in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(d);
                if (name.Equals("GameSave", StringComparison.OrdinalIgnoreCase)) continue;
                var content = Path.Combine(d, "Content");
                yield return new GameEntry { Id = "xbox:" + name, Name = name, Source = "xbox", InstallDir = Directory.Exists(content) ? content : d };
            }
        }
    }

    /// <summary>Windowed apps currently running, as candidates for manual adding.</summary>
    public static List<GameEntry> RunningApps()
    {
        var skip = new[] { @"C:\Windows\", @"C:\Program Files\WindowsApps\Microsoft.", Path.GetDirectoryName(Paths.Exe) + "\\" };
        // Launchers, browsers and tools that are never games.
        var skipExe = new[]
        {
            "claude", "armourycrate", "steam", "steamwebhelper", "epicgameslauncher", "eadesktop", "ubisoftconnect", "upc",
            "galaxyclient", "battle.net", "xboxpcapp", "gamebar", "discord", "chrome", "msedge", "firefox", "opera", "brave",
            "explorer", "code", "windowsterminal", "notepad", "telegram", "spotify", "obs64", "powershell", "cmd",
        };
        var list = new List<GameEntry>();
        foreach (var (h, pid) in Native.TopLevelWindows())
        {
            var title = Native.GetTitle(h);
            if (title.Length == 0) continue;
            var r = Native.GetClientScreenRect(h);
            if (r.Width < 400 || r.Height < 300) continue;
            var path = Native.GetProcessPath(pid);
            if (path == null || skip.Any(s => path.StartsWith(s, StringComparison.OrdinalIgnoreCase))) continue;
            var exe = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
            if (skipExe.Any(s => exe.StartsWith(s))) continue;
            var dir = Path.GetDirectoryName(path)!;
            if (list.Any(x => x.InstallDir.Equals(dir, StringComparison.OrdinalIgnoreCase))) continue;
            list.Add(new GameEntry
            {
                Id = "manual:" + dir.ToLowerInvariant(),
                Name = title.Length > 40 ? title[..40] : title,
                Source = "manual",
                InstallDir = dir,
                Enabled = true,
            });
        }
        return list;
    }
}

/// <summary>Finds running processes that belong to enabled games (by install folder).</summary>
public sealed class ProcessMatcher
{
    readonly Dictionary<int, string?> _paths = new();

    public (GameEntry game, HashSet<int> pids)? FindRunning(IReadOnlyList<GameEntry> enabled)
    {
        var alive = new HashSet<int>();
        (GameEntry, HashSet<int>)? hit = null;
        foreach (var p in Process.GetProcesses())
        {
            int pid = p.Id;
            p.Dispose();
            if (pid <= 4) continue;
            alive.Add(pid);
            if (!_paths.TryGetValue(pid, out var path)) _paths[pid] = path = Native.GetProcessPath(pid);
            if (path == null) continue;
            foreach (var g in enabled)
            {
                var dir = g.InstallDir.TrimEnd('\\') + "\\";
                if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                hit ??= (g, new HashSet<int>());
                if (hit.Value.Item1 == g) hit.Value.Item2.Add(pid);
            }
        }
        foreach (var dead in _paths.Keys.Where(k => !alive.Contains(k)).ToList()) _paths.Remove(dead);
        return hit;
    }

    /// <summary>The largest visible top-level window of the given processes.</summary>
    public static IntPtr MainWindow(HashSet<int> pids)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;
        foreach (var (h, pid) in Native.TopLevelWindows())
        {
            if (!pids.Contains(pid)) continue;
            var r = Native.GetClientScreenRect(h);
            long area = (long)r.Width * r.Height;
            if (area > bestArea) { best = h; bestArea = area; }
        }
        return bestArea >= 320 * 240 ? best : IntPtr.Zero;
    }
}

/// <summary>
/// Start with Windows, as a Task Scheduler task at this user's sign-in. Not the Run key: the Xbox full screen
/// experience on the console starts no Run-key apps (no desktop shell), so after a restart the tray was missing and
/// Armoury kept the stick lights (16.11). The task runs in both the full screen experience and the desktop.
/// Older installs had the Run key: <see cref="Migrate"/> moves them. If the task cannot be made, the Run key stays,
/// with Windows' own startup switch (Settings -> Apps -> Startup) turned on.
/// </summary>
public static class Autostart
{
    const string TaskName = "GameDub";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    static bool? _task; // cached: asking Task Scheduler starts a process

    public static bool IsEnabled() => HasTask() || (HasRunKey() && !OffInWindows());

    /// <summary>The Run entry is there, but Windows' startup switch for it is off.</summary>
    public static bool OffInWindows()
    {
        using var k = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        return k?.GetValue("GameDub") is byte[] b && b.Length > 0 && (b[0] & 1) == 1;
    }

    public static void Set(bool on)
    {
        if (!on) { Schtasks($"/delete /tn {TaskName} /f"); _task = false; RemoveRunKey(); return; }
        if (MakeTask()) { RemoveRunKey(); return; }
        // no task: the Run key, switched on in Windows too
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) k.SetValue("GameDub", $"\"{Paths.Exe}\" --tray");
        if (OffInWindows())
        {
            using var a = Registry.CurrentUser.CreateSubKey(ApprovedKey);
            a.SetValue("GameDub", new byte[12] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }
    }

    /// <summary>At tray start: an old Run-key autostart becomes the sign-in task.</summary>
    public static void Migrate()
    {
        if (!HasRunKey() || HasTask()) return;
        if (MakeTask()) { RemoveRunKey(); Log.Write("autostart: moved from the Run key to a sign-in task"); }
        else if (OffInWindows()) Log.Write("autostart is switched off in Windows (Settings -> Apps -> Startup): the tray will not start after a restart");
    }

    static bool HasRunKey()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("GameDub") is string;
    }

    static void RemoveRunKey()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        k?.DeleteValue("GameDub", false);
    }

    static bool HasTask() => _task ??= Schtasks($"/query /tn {TaskName}") == 0;

    static bool MakeTask()
    {
        string user = System.Security.SecurityElement.Escape($@"{Environment.UserDomainName}\{Environment.UserName}");
        string exe = System.Security.SecurityElement.Escape(Paths.Exe);
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>GameDub tray service: dubbing and the joystick lights</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--tray</Arguments></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), "gamedub-task.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            bool ok = Schtasks($"/create /tn {TaskName} /xml \"{file}\" /f") == 0;
            _task = ok;
            if (!ok) Log.Write("autostart: could not make the sign-in task, using the Run key");
            return ok;
        }
        finally { try { File.Delete(file); } catch { } }
    }

    static int Schtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
            return p.WaitForExit(15_000) ? p.ExitCode : -1;
        }
        catch { return -1; }
    }
}
