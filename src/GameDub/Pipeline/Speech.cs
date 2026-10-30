using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GameDub.Core;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace GameDub.Pipeline;

/// <summary>A concrete voice: engine voice id plus pitch/rate tweaks that make variants sound distinct.</summary>
public sealed record VoiceSpec(string Voice, int PitchHz, int RatePct);

/// <summary>What to say and who says it. Each engine maps gender + variant to its own voices.</summary>
/// <remarks>Pace &gt; 1 speeds the line up (used when the queue falls behind the game).</remarks>
public sealed record TtsRequest(string Text, string Gender, int Variant, string? Mood = null, double Pace = 1.0);

/// <summary>TTS engines return MP3 or WAV bytes. New engines (ElevenLabs, ...) plug in here.</summary>
public interface ITtsEngine
{
    Task<byte[]> SynthesizeAsync(TtsRequest r, CancellationToken ct);
    string Describe(TtsRequest r);
}

public static class Voices
{
    // Pitch shifting is a post-process that makes neural voices sound robotic, so the narrator
    // gets the untouched voice and characters differ by pace with at most a 2 Hz nudge.
    static readonly (int pitch, int rate)[] Variants = { (0, 0), (0, -6), (0, 6), (-2, 2), (2, 10), (-2, -3) };
    /// <summary>Variant 0 is the narrator; characters get 1..VariantCount-1.</summary>
    public static int VariantCount => Variants.Length;

    public static VoiceSpec Edge(AppSettings s, string gender, int variant)
    {
        string voice = gender == "f" ? "uk-UA-PolinaNeural" : "uk-UA-OstapNeural";
        var v = Variants[Math.Abs(variant) % Variants.Length];
        return new VoiceSpec(voice, v.pitch, s.RatePercent + v.rate);
    }

    static readonly string[] OpenAiMale = { "cedar", "ash", "echo", "onyx", "verse", "ballad" };
    static readonly string[] OpenAiFemale = { "marin", "coral", "nova", "shimmer", "sage", "alloy" };

    public static string OpenAi(string gender, int variant)
    {
        var list = gender == "f" ? OpenAiFemale : OpenAiMale;
        return list[Math.Abs(variant) % list.Length];
    }

    public static bool UsesOpenAi(AppSettings s) => s.TtsEngine == "openai" && s.OpenAiApiKey.Length > 0;
    public static bool UsesGoogle(AppSettings s) => s.TtsEngine == "google" && s.GoogleApiKeys.Count > 0;

    // Google Chirp 3 HD voices for uk-UA.
    static readonly string[] GoogleMale = { "Charon", "Puck", "Fenrir", "Orus", "Iapetus", "Enceladus" };
    static readonly string[] GoogleFemale = { "Aoede", "Kore", "Leda", "Zephyr", "Despina", "Callirrhoe" };

    public static string Google(string gender, int variant)
    {
        var list = gender == "f" ? GoogleFemale : GoogleMale;
        return list[Math.Abs(variant) % list.Length];
    }

    public static bool UsesEleven(AppSettings s) => s.TtsEngine == "eleven" && s.ElevenApiKeys.Count > 0;

    /// <summary>
    /// "auto": best engine that has a key, each falling back to the next (and finally Edge)
    /// when it errors or runs out of quota. A specific engine still falls back to Edge.
    /// </summary>
    public static ITtsEngine CreateEngine(AppSettings s) => CreateEngine(s, Http.Client);

    internal static ITtsEngine CreateEngine(AppSettings s, HttpClient client)
    {
        s.NormalizeApiKeys();
        ITtsEngine chain = new EdgeTts(s);
        if (s.TtsEngine == "supertonic" && SupertonicTts.IsInstalled) return new FallbackTts(Supertonic(s), chain);
        if (s.TtsEngine == "auto")
        {
            // Offline Supertonic beats Edge (no network, no quota) but sits below the cloud HD voices.
            if (SupertonicTts.IsInstalled) chain = new FallbackTts(Supertonic(s), chain);
            if (s.OpenAiApiKey.Length > 0) chain = new FallbackTts(new OpenAiTts(s), chain);
            chain = WithKeys(s.GoogleApiKeys, key => new GoogleTts(s, key, client), chain);
            chain = WithKeys(s.ElevenApiKeys, key => new ElevenTts(s, key, client), chain);
            return chain;
        }
        return UsesEleven(s) ? WithKeys(s.ElevenApiKeys, key => new ElevenTts(s, key, client), chain)
            : UsesGoogle(s) ? WithKeys(s.GoogleApiKeys, key => new GoogleTts(s, key, client), chain)
            : UsesOpenAi(s) ? new FallbackTts(new OpenAiTts(s), chain)
            : chain;
    }

    // One shared instance: the models take ~400 MB of RAM and seconds to load.
    static SupertonicTts? _supertonic;
    static SupertonicTts Supertonic(AppSettings s) => _supertonic ??= new SupertonicTts(s);

    internal static ITtsEngine WithKeys(IEnumerable<string> keys, Func<string, ITtsEngine> create, ITtsEngine fallback)
    {
        // Keep a separate cooldown per key; try them in the order shown in settings.
        foreach (var key in keys.Reverse()) fallback = new FallbackTts(create(key), fallback);
        return fallback;
    }
}

/// <summary>ElevenLabs (free tier ≈ 10k credits/month). Flash v2.5 is fast and costs half a credit per character.</summary>
public sealed class ElevenTts : ITtsEngine
{
    readonly AppSettings _s;
    readonly string _apiKey;
    readonly HttpClient _client;
    // Premade voices that exist on every account; refreshed from /v1/voices on first use.
    string[] _male = { "JBFqnCBsd6RMkjVDRZzb", "TX3LPaxmHKxFdv7VOQHJ", "nPczCjzI2devNBz1zQrb", "onwK4e9ZLuTAKqWW03F9", "IKne3meq5aSn9XLyUdCD", "N2lVS1w4EtoT3dr4eOWO" };
    string[] _female = { "EXAVITQu4vr4xnSDxMaL", "FGY2WhTYpPnrIDTdsKH5", "Xb7hH8MSUJpSbSDYk0k2", "XrExE9yKIg1WjnnlVkGX", "cgSgspJ2msm6clMCkdW9", "pFZP5JQG7iQjIQuC4Bku" };
    readonly Dictionary<string, string> Names = new();
    bool _voicesLoaded;

    public ElevenTts(AppSettings s, string apiKey, HttpClient? client = null)
    {
        _s = s; _apiKey = apiKey; _client = client ?? Http.Client;
    }

    string Pick(string gender, int variant)
    {
        var list = gender == "f" ? _female : _male;
        return list[Math.Abs(variant) % list.Length];
    }

    public string Describe(TtsRequest r)
    {
        var id = Pick(r.Gender, r.Variant);
        return "eleven:" + (Names.TryGetValue(id, out var n) ? n : id[..6]);
    }

    async Task LoadVoices(CancellationToken ct)
    {
        _voicesLoaded = true;
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://api.elevenlabs.io/v1/voices");
            req.Headers.Add("xi-api-key", _apiKey);
            using var resp = await _client.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var male = new List<string>(); var female = new List<string>();
            foreach (var v in doc.RootElement.GetProperty("voices").EnumerateArray())
            {
                if (!v.TryGetProperty("category", out var cat) || cat.GetString() != "premade") continue;
                var id = v.GetProperty("voice_id").GetString()!;
                Names[id] = v.GetProperty("name").GetString() ?? id;
                var gender = v.TryGetProperty("labels", out var l) && l.TryGetProperty("gender", out var g) ? g.GetString() : null;
                if (gender == "male") male.Add(id); else if (gender == "female") female.Add(id);
            }
            if (male.Count >= 2) _male = male.Take(6).ToArray();
            if (female.Count >= 2) _female = female.Take(6).ToArray();
            Log.Write($"eleven voices: {_male.Length} male, {_female.Length} female");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Error("eleven voices", ex); }
    }

    public async Task<byte[]> SynthesizeAsync(TtsRequest r, CancellationToken ct)
    {
        if (!_voicesLoaded) await LoadVoices(ct);
        double speed = Math.Clamp((1 + _s.RatePercent / 100.0) * r.Pace, 0.7, 1.2);
        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{Pick(r.Gender, r.Variant)}?output_format=mp3_44100_64");
        req.Headers.Add("xi-api-key", _apiKey);
        req.Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new
        {
            text = r.Text,
            model_id = "eleven_flash_v2_5",
            language_code = "uk",
            voice_settings = new { stability = 0.45, similarity_boost = 0.8, style = 0.3, use_speaker_boost = true, speed = Math.Round(speed, 2) },
        }), Encoding.UTF8, "application/json");
        using var resp = await _client.SendAsync(req, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"eleven tts {(int)resp.StatusCode}: {Encoding.UTF8.GetString(bytes, 0, Math.Min(300, bytes.Length))}", null, resp.StatusCode);
        return Mp3Tools.PadWithSilence(bytes, 120, 300);
    }
}

/// <summary>Google Cloud Text-to-Speech, Chirp 3 HD voices (free tier: 1M characters/month).</summary>
public sealed class GoogleTts : ITtsEngine
{
    readonly AppSettings _s;
    readonly string _apiKey;
    readonly HttpClient _client;

    public GoogleTts(AppSettings s, string apiKey, HttpClient? client = null)
    {
        _s = s; _apiKey = apiKey; _client = client ?? Http.Client;
    }

    static string VoiceName(TtsRequest r) => $"uk-UA-Chirp3-HD-{Voices.Google(r.Gender, r.Variant)}";

    public string Describe(TtsRequest r) => "google:" + Voices.Google(r.Gender, r.Variant);

    public async Task<byte[]> SynthesizeAsync(TtsRequest r, CancellationToken ct)
    {
        // Characters differ by pace a little; the narrator (variant 0) speaks at the configured rate.
        double[] paceByVariant = { 0, -0.06, 0.06, 0.03, 0.10, -0.03 };
        double rate = Math.Clamp((1 + _s.RatePercent / 100.0 + paceByVariant[Math.Abs(r.Variant) % paceByVariant.Length]) * r.Pace, 0.5, 1.8);
        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "https://texttospeech.googleapis.com/v1/text:synthesize");
        req.Headers.Add("x-goog-api-key", _apiKey);
        req.Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new
        {
            input = new { text = r.Text },
            voice = new { languageCode = "uk-UA", name = VoiceName(r) },
            audioConfig = new { audioEncoding = "MP3", speakingRate = Math.Round(rate, 2) },
        }), Encoding.UTF8, "application/json");
        using var resp = await _client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"google tts {(int)resp.StatusCode}: {body[..Math.Min(300, body.Length)]}", null, resp.StatusCode);
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var mp3 = Convert.FromBase64String(doc.RootElement.GetProperty("audioContent").GetString() ?? "");
        return Mp3Tools.PadWithSilence(mp3, 120, 300);
    }
}

/// <summary>OpenAI speech API: expressive voices, mood via "instructions".</summary>
public sealed class OpenAiTts : ITtsEngine
{
    readonly AppSettings _s;

    public OpenAiTts(AppSettings s) => _s = s;

    public string Describe(TtsRequest r) => "openai:" + Voices.OpenAi(r.Gender, r.Variant);

    public async Task<byte[]> SynthesizeAsync(TtsRequest r, CancellationToken ct)
    {
        const string lang = "Ukrainian";
        int ratePct = _s.RatePercent + (int)Math.Round((r.Pace - 1) * 100);
        var pace = ratePct >= 30 ? "fast" : ratePct >= 10 ? "slightly faster than normal" : ratePct < 0 ? "slow" : "normal";
        var instructions = $"Speak native, natural {lang} like a professional video game voice actor. Pace: {pace}." +
                           (string.IsNullOrWhiteSpace(r.Mood) ? "" : $" Emotion and delivery: {r.Mood}.");
        using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
        req.Headers.Add("Authorization", "Bearer " + _s.OpenAiApiKey);
        req.Content = new System.Net.Http.StringContent(System.Text.Json.JsonSerializer.Serialize(new
        {
            model = _s.OpenAiTtsModel,
            voice = Voices.OpenAi(r.Gender, r.Variant),
            input = r.Text,
            instructions,
            response_format = "wav",
        }), Encoding.UTF8, "application/json");
        using var resp = await Http.Client.SendAsync(req, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new System.Net.Http.HttpRequestException($"openai tts {(int)resp.StatusCode}: {Encoding.UTF8.GetString(bytes, 0, Math.Min(300, bytes.Length))}");
        return bytes;
    }
}

/// <summary>Uses the primary engine; on errors falls back, and rests the primary for a while after repeated failures.</summary>
public sealed class FallbackTts : ITtsEngine
{
    readonly ITtsEngine _primary, _fallback;
    int _failures;
    long _retryAt;

    public FallbackTts(ITtsEngine primary, ITtsEngine fallback) { _primary = primary; _fallback = fallback; }

    bool PrimaryResting => Environment.TickCount64 < _retryAt;

    public string Describe(TtsRequest r) => PrimaryResting ? _fallback.Describe(r) : _primary.Describe(r);

    public async Task<byte[]> SynthesizeAsync(TtsRequest r, CancellationToken ct)
    {
        // Cloud engines are pointless without a network adapter; offline ones (Supertonic) always run.
        bool skipCloud = _primary is not SupertonicTts && !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();
        if (!PrimaryResting && !skipCloud)
        {
            try
            {
                var res = await _primary.SynthesizeAsync(r, ct);
                _failures = 0;
                return res;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Error("tts primary", ex);
                bool quota = ex.Message.Contains("quota", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("credits", StringComparison.OrdinalIgnoreCase)
                    || ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests };
                bool invalidKey = ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
                // No connection at all (offline, DNS, refused, timeout): don't retry per line, wait a minute.
                bool network = ex is HttpRequestException { StatusCode: null } || ex is TaskCanceledException || ex is System.Net.WebSockets.WebSocketException
                               || ex.InnerException is System.Net.Sockets.SocketException;
                if (quota) { _retryAt = Environment.TickCount64 + 60 * 60_000; _failures = 0; }
                else if (network) { _retryAt = Environment.TickCount64 + 60_000; _failures = 0; }
                else if (invalidKey) { _retryAt = Environment.TickCount64 + 5 * 60_000; _failures = 0; }
                else if (++_failures >= 3) { _retryAt = Environment.TickCount64 + 5 * 60_000; _failures = 0; }
            }
        }
        return await _fallback.SynthesizeAsync(r, ct);
    }
}

/// <summary>Microsoft Edge "Read aloud" neural voices over WebSocket (free, no key).</summary>
public sealed class EdgeTts : ITtsEngine
{
    const string ChromiumFull = "143.0.3650.75";
    static readonly string Muid = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    static long _skewSeconds;
    readonly AppSettings _s;

    public EdgeTts(AppSettings s) => _s = s;

    public string Describe(TtsRequest r)
    {
        var v = Voices.Edge(_s, r.Gender, r.Variant);
        return $"{v.Voice} {v.PitchHz}Hz";
    }

    public Task<byte[]> SynthesizeAsync(TtsRequest r, CancellationToken ct)
    {
        var v = Voices.Edge(_s, r.Gender, r.Variant);
        return SynthesizeAsync(r.Text, v with { RatePct = v.RatePct + (int)Math.Round((r.Pace - 1) * 100) }, ct);
    }

    /// <summary>Output format; the 96 kbps one sounds cleaner if the service accepts it.</summary>
    public static string Format = "audio-24khz-96kbitrate-mono-mp3";
    static bool _formatVerified;

    public async Task<byte[]> SynthesizeAsync(string text, VoiceSpec voice, CancellationToken ct)
    {
        text = Polish(text);
        if (!_formatVerified)
        {
            // Fall back to the always-supported 48 kbps format once if the better one is refused.
            try { var a = await SynthesizeRaw(text, voice, ct); _formatVerified = true; return a; }
            catch (Exception ex) when (!ct.IsCancellationRequested && Format != "audio-24khz-48kbitrate-mono-mp3")
            {
                Log.Write($"edge-tts: {Format} refused ({ex.Message}), using 48 kbps");
                Format = "audio-24khz-48kbitrate-mono-mp3";
                _formatVerified = true;
            }
        }
        return await SynthesizeRaw(text, voice, ct);
    }

    /// <summary>Neural voices phrase by punctuation: give every line a proper ending and real pauses.</summary>
    static string Polish(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return text;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+[-–—]\s+", ", "); // mid-sentence dashes → short pause
        text = text.Replace("...", "…");
        if (!".!?…»\"'".Contains(text[^1])) text += ".";
        return text;
    }

    async Task<byte[]> SynthesizeRaw(string text, VoiceSpec voice, CancellationToken ct)
    {
        // The service sometimes drops connections; a 403 usually means our clock is off.
        for (int attempt = 1; ; attempt++)
        {
            try { return await Once(text, voice, ct); }
            catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested && ex is WebSocketException or System.IO.IOException)
            {
                Log.Error($"edge-tts (retry {attempt})", ex);
                if (ex.Message.Contains("403")) await SyncClock(ct);
            }
        }
    }

    static async Task SyncClock(CancellationToken ct)
    {
        try
        {
            using var resp = await Http.Client.SendAsync(new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, "https://speech.platform.bing.com"), ct);
            if (resp.Headers.Date is { } d) _skewSeconds = (long)(d - DateTimeOffset.UtcNow).TotalSeconds;
        }
        catch { }
    }

    static async Task<byte[]> Once(string text, VoiceSpec voice, CancellationToken ct)
    {
        string major = ChromiumFull.Split('.')[0];
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Origin", "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold");
        ws.Options.SetRequestHeader("User-Agent", $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{major}.0.0.0 Safari/537.36 Edg/{major}.0.0.0");
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cookie", $"muid={Muid};");
        var url = $"wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1?TrustedClientToken={EdgeDrm.TrustedClientToken}" +
                  $"&Sec-MS-GEC={EdgeDrm.Generate(_skewSeconds)}&Sec-MS-GEC-Version=2-{ChromiumFull}&ConnectionId={Guid.NewGuid():N}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var tk = timeout.Token;
        await ws.ConnectAsync(new Uri(url), tk);

        string ts = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", CultureInfo.InvariantCulture);
        await SendText(ws, $"X-Timestamp:{ts}\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n" +
                           "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"" + Format + "\"}}}}\r\n", tk);

        string pitch = (voice.PitchHz >= 0 ? "+" : "") + voice.PitchHz + "Hz";
        string rate = (voice.RatePct >= 0 ? "+" : "") + voice.RatePct + "%";
        string ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>" +
                      $"<voice name='{voice.Voice}'><prosody pitch='{pitch}' rate='{rate}' volume='+0%'>{SecurityElement.Escape(text)}</prosody></voice></speak>";
        await SendText(ws, $"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{ts}Z\r\nPath:ssml\r\n\r\n{ssml}", tk);

        var audio = new MemoryStream();
        var buf = new byte[16384];
        var msg = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, tk);
            if (r.MessageType == WebSocketMessageType.Close) break;
            msg.Write(buf, 0, r.Count);
            if (!r.EndOfMessage) continue;
            var data = msg.ToArray();
            msg.SetLength(0);
            if (r.MessageType == WebSocketMessageType.Text)
            {
                if (Encoding.UTF8.GetString(data).Contains("Path:turn.end")) break;
            }
            else if (data.Length >= 2)
            {
                int hl = (data[0] << 8) | data[1];
                if (2 + hl <= data.Length && Encoding.ASCII.GetString(data, 2, hl).Contains("Path:audio"))
                    audio.Write(data, 2 + hl, data.Length - 2 - hl);
            }
        }
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        if (audio.Length == 0) throw new InvalidOperationException("edge-tts returned no audio");
        return Mp3Tools.PadWithSilence(audio.ToArray(), 160, 400);
    }

    static Task SendText(ClientWebSocket ws, string s, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, ct);
}

public static class Mp3Tools
{
    static readonly int[] BitratesV2L3 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
    static readonly int[] BitratesV1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
    static readonly int[] RatesV1 = { 44100, 48000, 32000 }, RatesV2 = { 22050, 24000, 16000 }, RatesV25 = { 11025, 12000, 8000 };

    /// <summary>Parses the first frame header: (offset, frameBytes, frameMs), or null.</summary>
    public static (int offset, int size, double ms)? FirstFrame(byte[] mp3)
    {
        for (int i = 0; i + 4 <= mp3.Length; i++)
        {
            if (mp3[i] != 0xFF || (mp3[i + 1] & 0xE0) != 0xE0) continue;
            int version = (mp3[i + 1] >> 3) & 3, layer = (mp3[i + 1] >> 1) & 3;
            int bi = mp3[i + 2] >> 4, si = (mp3[i + 2] >> 2) & 3, pad = (mp3[i + 2] >> 1) & 1;
            if (version == 1 || layer != 1 || bi == 0 || bi == 15 || si == 3) continue;
            bool v1 = version == 3;
            int bitrate = (v1 ? BitratesV1L3 : BitratesV2L3)[bi] * 1000;
            int rate = (v1 ? RatesV1 : version == 2 ? RatesV2 : RatesV25)[si];
            int samples = v1 ? 1152 : 576;
            int size = samples / 8 * bitrate / rate + pad;
            return (i, size, 1000.0 * samples / rate);
        }
        return null;
    }

    public static double DurationMs(byte[] mp3)
    {
        var f = FirstFrame(mp3);
        return f == null ? 0 : (mp3.Length - f.Value.offset) / (double)f.Value.size * f.Value.ms;
    }

    /// <summary>
    /// Adds silent frames (same header, zeroed payload) before and after the audio so decoder
    /// priming and early "media ended" can only eat silence, never the first or last word.
    /// </summary>
    public static byte[] PadWithSilence(byte[] mp3, int leadMs, int trailMs)
    {
        var f = FirstFrame(mp3);
        if (f == null) return mp3;
        var (off, size, ms) = f.Value;
        var silent = new byte[size];
        Array.Copy(mp3, off, silent, 0, 4);
        silent[2] &= 0xFD; // clear padding bit so the frame length matches
        int lead = (int)Math.Ceiling(leadMs / ms), trail = (int)Math.Ceiling(trailMs / ms);
        var res = new byte[size * (lead + trail) + mp3.Length];
        int p = 0;
        for (int i = 0; i < lead; i++) { silent.CopyTo(res, p); p += size; }
        mp3.CopyTo(res, p); p += mp3.Length;
        for (int i = 0; i < trail; i++) { silent.CopyTo(res, p); p += size; }
        return res;
    }
}

/// <summary>Plays MP3 through the default audio output via Windows Media Foundation.</summary>
public static class AudioPlayer
{
    public static async Task PlayAsync(byte[] mp3, int volume, CancellationToken ct)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(mp3.AsBuffer());
        stream.Seek(0);
        using var player = new MediaPlayer { AutoPlay = false, Volume = Math.Clamp(volume, 0, 100) / 100.0 };
        player.CommandManager.IsEnabled = false; // keep it out of the Windows media overlay
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.MediaEnded += (_, _) => done.TrySetResult(true);
        player.MediaFailed += (_, e) => done.TrySetException(new InvalidOperationException(e.ErrorMessage));
        bool wav = mp3.Length > 4 && mp3[0] == 'R' && mp3[1] == 'I' && mp3[2] == 'F' && mp3[3] == 'F';
        player.Source = MediaSource.CreateFromStream(stream, wav ? "audio/wav" : "audio/mpeg");
        player.Play();
        using (ct.Register(() => { try { player.Pause(); } catch { } done.TrySetCanceled(); }))
            await done.Task;
    }
}

public static class Speakers
{
    static readonly Regex Colon = new(@"^\s*(\p{Lu}[\w'.\-]*(?:\s+\p{Lu}[\w'.\-]*){0,2})\s*[:：]\s*(.{2,})$", RegexOptions.Singleline);
    static readonly Regex Bracket = new(@"^\s*\[([^\]]{1,30})\]\s*(.{2,})$", RegexOptions.Singleline);
    static readonly HashSet<string> NotSpeakers = new(StringComparer.OrdinalIgnoreCase)
        { "Note", "Tip", "Hint", "Objective", "Warning", "Mission", "Quest", "Info", "Press", "Hold", "New", "Reward", "Location", "Chapter",
          "Совет", "Подсказка", "Цель", "Внимание", "Задание", "Миссия", "Квест", "Нажмите", "Удерживайте", "Новое", "Награда", "Локация", "Глава", "Примечание" };

    /// <summary>Splits "John: Hello" into ("John", "Hello"); speaker is null for narrator lines.</summary>
    public static (string? speaker, string line) Split(string text)
    {
        var m = Colon.Match(text);
        if (!m.Success) m = Bracket.Match(text);
        if (!m.Success) return (null, text);
        var name = m.Groups[1].Value.Trim();
        if (NotSpeakers.Contains(name.Split(' ')[0])) return (null, text);
        return (name, m.Groups[2].Value.Trim());
    }

    static readonly HashSet<string> FemaleNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "alice","amy","anna","anne","ashley","barbara","bella","beth","betty","carol","catherine","cassie","chloe","claire","clara",
        "dana","diana","ellie","elena","eliza","elizabeth","ella","emily","emma","eva","eve","faith","grace","hannah","helen","holly",
        "isabel","jane","jenny","jess","jessica","jill","joy","judy","julia","julie","kate","katie","kim","laura","lily","linda","lisa",
        "liz","lucy","maria","marie","mary","mia","mila","nancy","natalie","nina","olivia","rachel","rose","ruby","sarah","sara","sophie",
        "sophia","susan","tess","tina","valerie","vera","zoe","ada","aloy","lara","jill","claire","ada","ciri","yennefer","triss","tifa",
        "aerith","yuna","samus","zelda","citra","amita","faith","mother","mom","mum","sister","queen","lady","princess","girl","woman",
        "wife","grandma","aunt","madam","miss","mrs","ms","nun","witch","daughter","duchess","empress","goddess","priestess",
    };

    static readonly HashSet<string> FemaleWordsRu = new(StringComparer.OrdinalIgnoreCase)
        { "мать", "мама", "сестра", "королева", "принцесса", "девушка", "женщина", "жена", "бабушка", "тётя", "тетя", "госпожа", "дочь", "ведьма", "монахиня", "любовь" };
    static readonly HashSet<string> MaleWordsRu = new(StringComparer.OrdinalIgnoreCase)
        { "никита", "илья", "лука", "фома", "кузьма", "миша", "саша", "паша", "дима", "вова", "коля", "ваня", "петя", "вася", "гриша", "лёша", "леша", "сережа", "серёжа", "женя", "дядя", "папа", "глава", "старшина", "судья", "слуга", "мужчина" };

    public static string GuessGender(string name)
    {
        var words = name.ToLowerInvariant().Split(new[] { ' ', '.', '-' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var w in words)
        {
            if (FemaleNames.Contains(w) || FemaleWordsRu.Contains(w)) return "f";
            if (MaleWordsRu.Contains(w)) return "m";
        }
        var first = name.Split(' ')[0].ToLowerInvariant();
        if (first.Any(c => c is >= 'Ѐ' and <= 'ӿ')) return first.Length > 2 && first[^1] is 'а' or 'я' ? "f" : "m";
        string[] femaleEndings = { "a", "ie", "ette", "elle", "ine", "lyn", "issa", "ah" };
        string[] maleExceptions = { "joshua", "luca", "nikita", "sasha", "ezra", "elijah", "noah", "jonah", "isaiah", "micah", "dakota", "andrea" };
        if (first.Length > 2 && femaleEndings.Any(first.EndsWith) && !maleExceptions.Contains(first)) return "f";
        return "m";
    }
}
