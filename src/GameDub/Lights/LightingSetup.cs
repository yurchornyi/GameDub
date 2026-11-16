using System;
using System.Linq;
using GameDub.Core;
using Microsoft.Win32;

namespace GameDub.Lights;

/// <summary>
/// Who drives the stick lights, in Windows' own settings (Settings -> Personalization -> Dynamic Lighting), so the
/// «Підсвітка стіків» toggle hands them over by itself.
/// GameDub: Dynamic Lighting on for the device, GameDub first in background light control, brightness 100 (GameDub
/// follows the screen itself). Armoury: Dynamic Lighting off for the device. On the ROG Xbox Ally X Armoury lights the
/// sticks through the controller's own firmware mode, which Dynamic Lighting overrides: with it on, Armoury's colors
/// did not show (16.11).
/// </summary>
static class LightingSetup
{
    const string Root = @"Software\Microsoft\Lighting";
    const string FallbackFamily = "GameDub_4v5n28xgxx7zm"; // the sparse package's family name (publisher CN=GameDub)

    public static void Hand(bool toGameDub)
    {
        try
        {
            string me = Family();
            using var root = Registry.CurrentUser.OpenSubKey(Root, true);
            if (root == null) return;
            Reorder(root, me, toGameDub);
            using var devices = root.OpenSubKey("Devices", true);
            if (devices != null)
                foreach (var id in devices.GetSubKeyNames())
                {
                    using var d = devices.OpenSubKey(id, true);
                    if (d == null) continue;
                    d.SetValue("AmbientLightingEnabled", toGameDub ? 1 : 0, RegistryValueKind.DWord);
                    if (toGameDub) d.SetValue("Brightness", 100, RegistryValueKind.DWord);
                    Reorder(d, me, toGameDub);
                }
            Log.Write($"lights: handed to {(toGameDub ? "GameDub" : "Armoury")}");
        }
        catch (Exception ex) { Log.Error("lights hand over", ex); }
    }

    /// <summary>GameDub to the top of a background-control list, or to its end. Untouched when GameDub is not on it.</summary>
    static void Reorder(RegistryKey parent, string me, bool first)
    {
        using var p = parent.OpenSubKey("Providers", true);
        if (p == null) return;
        var list = p.GetValueNames().Where(n => int.TryParse(n, out _)).OrderBy(int.Parse)
            .Select(n => p.GetValue(n) as string).Where(v => !string.IsNullOrEmpty(v)).Cast<string>().ToList();
        if (!list.Contains(me, StringComparer.OrdinalIgnoreCase)) return;
        list.RemoveAll(v => v.Equals(me, StringComparison.OrdinalIgnoreCase));
        if (first) list.Insert(0, me); else list.Add(me);
        for (int i = 0; i < list.Count; i++) p.SetValue((i + 1).ToString(), list[i], RegistryValueKind.String);
    }

    static string Family()
    {
        try { return Windows.ApplicationModel.Package.Current.Id.FamilyName; }
        catch { return FallbackFamily; }
    }
}
