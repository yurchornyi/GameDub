using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using GameDub.Core;

namespace GameDub.Lights;

/// <summary>
/// Whether the game in front is a racing game, where the left trigger is the brake. An engine heard is not enough: in
/// Call of Duty a helicopter sounded like one and aiming with the left trigger lit the brake (16.11). The genre comes
/// from the game's Steam store page ("Racing", genre 9): the game's folder under steamapps\common gives its app id
/// through the library's appmanifest files, and the genres are asked once per game from store.steampowered.com (only
/// the app id is sent), then kept in data\genres.json. A game not from Steam, or not answered yet, is not a racing game.
/// </summary>
static class RacingGames
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly object Gate = new();
    static Dictionary<string, bool>? _racing;                 // app id -> racing, as in genres.json
    static readonly Dictionary<string, string?> _appOf = new(StringComparer.OrdinalIgnoreCase); // game folder -> app id
    static readonly Dictionary<string, long> _asked = new();  // app id -> when asked last (a failure waits 10 min)

    static string GenresFile => Path.Combine(Paths.Data, "genres.json");

    /// <summary>Cheap to call every second: dictionary lookups, a file read once per game folder.</summary>
    public static bool IsRacing(string? exePath)
    {
        if (exePath == null) return false;
        string? app = AppId(exePath);
        if (app == null) return false;
        lock (Gate)
        {
            _racing ??= Load();
            if (_racing.TryGetValue(app, out bool racing)) return racing;
            long now = Environment.TickCount64;
            if (_asked.TryGetValue(app, out long at) && now - at < 600_000) return false;
            _asked[app] = now;
        }
        _ = Task.Run(() => Ask(app));
        return false;
    }

    /// <summary>The Steam app id of the game an exe belongs to: ...\steamapps\common\&lt;installdir&gt;\... .</summary>
    static string? AppId(string exePath)
    {
        string full = Path.GetFullPath(exePath);
        int at = full.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        string library = full[..(at + @"\steamapps".Length)];
        string rest = full[(at + @"\steamapps\common\".Length)..];
        int slash = rest.IndexOf('\\');
        if (slash <= 0) return null;
        string folder = rest[..slash];
        string key = Path.Combine(library, folder);
        lock (Gate) if (_appOf.TryGetValue(key, out var known)) return known;
        string? found = null;
        try
        {
            foreach (var acf in Directory.EnumerateFiles(library, "appmanifest_*.acf"))
            {
                string text = File.ReadAllText(acf);
                if (string.Equals(Field(text, "installdir"), folder, StringComparison.OrdinalIgnoreCase)) { found = Field(text, "appid"); break; }
            }
        }
        catch (Exception ex) { Log.Error("racing games: manifests", ex); }
        if (string.IsNullOrEmpty(found)) found = null;
        lock (Gate) _appOf[key] = found;
        return found;
    }

    static string Field(string acf, string name)
    {
        string tag = "\"" + name + "\"";
        int i = acf.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return "";
        int a = acf.IndexOf('"', i + tag.Length);
        int b = a < 0 ? -1 : acf.IndexOf('"', a + 1);
        return a < 0 || b < 0 ? "" : acf[(a + 1)..b];
    }

    static async Task Ask(string app)
    {
        try
        {
            string json = await Http.GetStringAsync($"https://store.steampowered.com/api/appdetails?appids={app}&filters=genres");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(app, out var entry) || !entry.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return;
            bool racing = entry.TryGetProperty("data", out var data) && data.TryGetProperty("genres", out var genres) &&
                genres.EnumerateArray().Any(g => g.TryGetProperty("id", out var id) && id.ToString() == "9");
            lock (Gate)
            {
                _racing ??= Load();
                _racing[app] = racing;
                File.WriteAllText(GenresFile, JsonSerializer.Serialize(_racing));
            }
            Log.Write($"racing games: steam {app} is {(racing ? "" : "not ")}a racing game");
        }
        catch (Exception ex) { Log.Write($"racing games: steam {app} not answered ({ex.GetType().Name})"); }
    }

    static Dictionary<string, bool> Load()
    {
        try { if (File.Exists(GenresFile)) return JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(GenresFile)) ?? new(); }
        catch (Exception ex) { Log.Error("racing games: genres.json", ex); }
        return new();
    }
}
