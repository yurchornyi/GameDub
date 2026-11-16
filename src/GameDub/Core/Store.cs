using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace GameDub.Core;

public static class Paths
{
    /// <summary>C:\GameDub (the parent of the app folder).</summary>
    public static string Root { get; private set; } = "";
    public static string Data => Path.Combine(Root, "data");
    public static string Profiles => Path.Combine(Data, "profiles");
    public static string ConfigFile => Path.Combine(Data, "config.json");
    public static string GamesFile => Path.Combine(Data, "games.json");
    public static string StatusFile => Path.Combine(Data, "status.json");
    public static string LogFile => Path.Combine(Data, "log.txt");
    public static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GameDub.exe");

    public static void Init()
    {
        Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        Directory.CreateDirectory(Profiles);
    }
}

public static class Log
{
    static readonly object Gate = new();

    public static void Write(string msg)
    {
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(Paths.LogFile);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:MM-dd HH:mm:ss.fff} {msg}{Environment.NewLine}", Encoding.UTF8);
            }
            catch { }
        }
    }

    public static void Error(string where, Exception ex) => Write($"ERR {where}: {ex.GetType().Name}: {ex.Message}");
}

public sealed class AppSettings
{
    public const int MaxApiKeys = 5;

    public string Translator { get; set; } = "google";      // google | llm
    public string LlmProvider { get; set; } = "openai";     // openai (uses OpenAiApiKey) | anthropic | custom (OpenAI-compatible LlmBaseUrl)
    public string LlmBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string LlmModel { get; set; } = "gpt-6-luna";
    public string LlmApiKey { get; set; } = "";             // key for anthropic / custom
    public string OpenAiApiKey { get; set; } = "";
    public string OpenAiTtsModel { get; set; } = "gpt-4o-mini-tts";
    public string TtsEngine { get; set; } = "edge";          // edge | eleven | google | openai
    public List<string> GoogleApiKeys { get; set; } = new();
    public List<string> ElevenApiKeys { get; set; } = new();
    // Read old configurations; omit the single-key fields after migration.
    public string? GoogleApiKey { get; set; }
    public string? ElevenApiKey { get; set; }
    public string NarratorGender { get; set; } = "m";
    public int RatePercent { get; set; } = 0;
    public int Volume { get; set; } = 100;
    public int OcrIntervalMs { get; set; } = 400;
    public int SupertonicSteps { get; set; } = 5;   // offline voice quality vs speed (2..16); 5 picked by ear
    // Single keys first: Armoury Crate maps a back button (M1/M2) to one keyboard key reliably.
    public string HotkeyPause { get; set; } = "Pause, Ctrl+Alt+P";
    public string HotkeyRegion { get; set; } = "Scroll, Ctrl+Alt+R";

    // Joystick lighting (Lights\LightService): dark, or a dim glow, plus events. Battery and heat are off unless he turns them on.
    public bool LightsOn { get; set; } = true;
    public bool LightsSound { get; set; } = true;      // flashes on sound effects only (explosions, shots, sirens, horns)
    public bool LightsDownload { get; set; } = true;   // blue while a game downloads, green when done
    public bool LightsBattery { get; set; } = true;     // red breathing under 10 %, a green flash on the charger (owner: on)
    public bool LightsHeat { get; set; } = false;      // orange breathing at 85 °C and above
    public bool LightsRumble { get; set; } = false;    // a short rumble on explosions and crashes, for games without their own
    public int LightsBase { get; set; } = 0;           // idle glow brightness, %; 0 = dark until an event (owner, 16.11)
    // Games that drive the lights themselves: GameDub stays off while one is in front (exe names without .exe).
    public List<string> LightsSkipGames { get; set; } = new() { "eurotrucks2", "amtrucks" };

    public void NormalizeApiKeys()
    {
        GoogleApiKeys = CleanApiKeys(GoogleApiKeys, GoogleApiKey);
        ElevenApiKeys = CleanApiKeys(ElevenApiKeys, ElevenApiKey);
        GoogleApiKey = ElevenApiKey = null;
    }

    static List<string> CleanApiKeys(IEnumerable<string>? keys, string? legacyKey) =>
        (keys ?? Array.Empty<string>()).Prepend(legacyKey ?? "")
            .Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim())
            .Distinct(StringComparer.Ordinal).Take(MaxApiKeys).ToList();
}

public sealed class GameEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";   // steam | epic | xbox | manual
    public string InstallDir { get; set; } = "";
    public string? Cover { get; set; }
    public bool Enabled { get; set; }
    public DateTime LastEnabledUtc { get; set; }
    public string SubLang { get; set; } = "en";   // language of the game's subtitles: en | ru
}

public sealed class RelRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    public System.Drawing.Rectangle ToScreen(System.Drawing.Rectangle client) => new(
        client.Left + (int)(X * client.Width), client.Top + (int)(Y * client.Height),
        Math.Max(8, (int)(W * client.Width)), Math.Max(8, (int)(H * client.Height)));

    public void Clamp()
    {
        W = Math.Clamp(W, 0.1, 1); H = Math.Clamp(H, 0.03, 0.6);
        X = Math.Clamp(X, 0, 1 - W); Y = Math.Clamp(Y, 0, 1 - H);
    }
}

public sealed class SpeakerInfo
{
    public string Gender { get; set; } = "m";
    public int Variant { get; set; }
}

public sealed class GameProfile
{
    public RelRect? Region { get; set; }
    public Dictionary<string, SpeakerInfo> Speakers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class Store
{
    static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Load<T>(string path) where T : new()
    {
        try { if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opts) ?? new T(); }
        catch (Exception ex) { Log.Error("load " + Path.GetFileName(path), ex); }
        return new T();
    }

    public static void Save<T>(string path, T value)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts), Encoding.UTF8);
        File.Move(tmp, path, true);
    }

    public static AppSettings LoadSettings()
    {
        var s = Load<AppSettings>(Paths.ConfigFile);
        s.NormalizeApiKeys();
        return s;
    }

    public static void SaveSettings(AppSettings s)
    {
        s.NormalizeApiKeys();
        Save(Paths.ConfigFile, s);
    }
    public static List<GameEntry> OrderGames(IEnumerable<GameEntry> games) => games
        .OrderByDescending(g => g.Enabled)
        .ThenByDescending(g => g.Enabled ? g.LastEnabledUtc : DateTime.MinValue)
        .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public static List<GameEntry> LoadGames() => OrderGames(Load<List<GameEntry>>(Paths.GamesFile));
    public static void SaveGames(List<GameEntry> g) => Save(Paths.GamesFile, OrderGames(g));

    static string ProfilePath(string gameId)
    {
        var safe = string.Concat(gameId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(Paths.Profiles, safe + ".json");
    }

    public static GameProfile LoadProfile(string gameId) => Load<GameProfile>(ProfilePath(gameId));
    public static void SaveProfile(string gameId, GameProfile p) => Save(ProfilePath(gameId), p);

    public static DateTime Stamp(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
}
