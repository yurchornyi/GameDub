using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameDub.Core;

namespace GameDub.Pipeline;

/// <summary>Translated line plus an optional English voice direction ("whispering, scared").</summary>
public sealed record Translation(string Text, string? Mood = null, bool Offline = false);

public interface ITranslator
{
    /// <summary>Keeps the connection to the translator open (a cheap request); nothing for an offline one.</summary>
    Task WarmAsync() => Task.CompletedTask;

    Task<Translation> TranslateAsync(string text, string? speaker, string gender, CancellationToken ct);
}

public static class Http
{
    // Connections stay open 10 min and IPv4 goes first: on the console a line's translation took 1.5-1.9 s while the
    // same request from curl took 0.2-0.4 s (24.11), the time of a new connection each line.
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
        ConnectCallback = async (ctx, ct) =>
        {
            var addrs = await System.Net.Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
            Exception? last = null;
            foreach (var a in addrs.OrderBy(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 1 : 0))
            {
                var sock = new System.Net.Sockets.Socket(a.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(3000);
                    await sock.ConnectAsync(a, ctx.DnsEndPoint.Port, cts.Token);
                    return new System.Net.Sockets.NetworkStream(sock, true);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { last = ex; sock.Dispose(); }
            }
            throw last ?? new HttpRequestException($"no address for {ctx.DnsEndPoint.Host}");
        },
    }) { Timeout = TimeSpan.FromSeconds(15) };
}

/// <summary>
/// Free translation without keys. Several public endpoints are tried in order; one that
/// fails (typically 429 rate limiting) rests for a while and the next takes over.
/// </summary>
public sealed class GoogleTranslator : ITranslator
{
    const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36";
    readonly string _lang, _from;
    readonly Dictionary<string, string> _cache = new();
    readonly (string name, Func<string, CancellationToken, Task<string>> run)[] _endpoints;
    readonly long[] _restUntil;
    string? _bingIg, _bingToken, _bingKey;
    long _bingTokenAt;

    public GoogleTranslator(string from = "en", string lang = "uk")
    {
        _from = from;
        _lang = lang;
        _endpoints = new (string, Func<string, CancellationToken, Task<string>>)[]
        {
            ("google-chrome", GoogleChrome), ("google-gtx", GoogleGtx), ("bing", Bing), ("mymemory", MyMemory),
        };
        _restUntil = new long[_endpoints.Length];
    }

    public async Task<Translation> TranslateAsync(string text, string? speaker, string gender, CancellationToken ct) =>
        new(await TranslateAsync(text, ct));

    public async Task WarmAsync()
    {
        // the endpoint that is not resting: a refused one is not kept warm (that keeps it refused)
        long now = Environment.TickCount64;
        try
        {
            if (now >= _restUntil[1]) await GoogleGtx(_from == "ru" ? "да" : "ok", CancellationToken.None);
            else if (now >= _restUntil[0]) await GoogleChrome(_from == "ru" ? "да" : "ok", CancellationToken.None);
        }
        catch { }
    }

    public async Task<string> TranslateAsync(string text, CancellationToken ct)
    {
        if (_cache.TryGetValue(text, out var hit)) return hit;
        Exception? last = null;
        long now = Environment.TickCount64;
        // both Google endpoints at once, the first answer wins: one of them took 0.9-1.2 s now and then (24.11)
        if (now >= _restUntil[0] && now >= _restUntil[1])
        {
            using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var running = new List<Task<string>> { _endpoints[0].run(text, race.Token), _endpoints[1].run(text, race.Token) };
            var which = new Dictionary<Task<string>, int> { [running[0]] = 0, [running[1]] = 1 };
            while (running.Count > 0)
            {
                var done = await Task.WhenAny(running);
                running.Remove(done);
                if (done.IsFaulted && done.Exception?.InnerException is { } err)
                {
                    // a refused endpoint rests (24.11: google-chrome answered «automated queries» to the console)
                    int k = which[done];
                    _restUntil[k] = now + (err.Message.Contains("429") ? 3 * 60_000 : 30_000);
                    Log.Write($"translate {_endpoints[k].name} failed: {err.Message[..Math.Min(120, err.Message.Length)]}");
                }
                if (done.IsCompletedSuccessfully && done.Result.Trim().Length > 0)
                {
                    race.Cancel();
                    if (_cache.Count > 500) _cache.Clear();
                    return _cache[text] = done.Result.Trim();
                }
            }
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
        }
        for (int i = 0; i < _endpoints.Length; i++)
        {
            if (now < _restUntil[i]) continue;
            try
            {
                long t0 = Environment.TickCount64;
                var res = (await _endpoints[i].run(text, ct)).Trim();
                long ms = Environment.TickCount64 - t0;
                if (ms > 800) Log.Write($"translate {_endpoints[i].name} slow: {ms} ms");
                if (res.Length == 0) throw new InvalidOperationException("empty translation");
                if (_cache.Count > 500) _cache.Clear();
                return _cache[text] = res;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                last = ex;
                bool limited = ex.Message.Contains("429");
                _restUntil[i] = now + (limited ? 3 * 60_000 : 30_000);
                Log.Write($"translate {_endpoints[i].name} failed: {ex.Message[..Math.Min(120, ex.Message.Length)]}");
            }
        }
        throw last ?? new InvalidOperationException("no translation endpoint available");
    }

    static HttpRequestMessage Get(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);
        return req;
    }

    static async Task<string> Send(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await Http.Client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode}");
        return body;
    }

    // The endpoint Chrome's built-in translator uses; far less rate-limited than gtx.
    async Task<string> GoogleChrome(string text, CancellationToken ct)
    {
        var body = await Send(Get($"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl={_from}&tl={_lang}&q={Uri.EscapeDataString(text)}"), ct);
        using var doc = JsonDocument.Parse(body);
        var first = doc.RootElement[0];
        return (first.ValueKind == JsonValueKind.String ? first.GetString() : first[0].GetString()) ?? "";
    }

    async Task<string> GoogleGtx(string text, CancellationToken ct)
    {
        var body = await Send(Get($"https://translate.googleapis.com/translate_a/single?client=gtx&sl={_from}&tl={_lang}&dt=t&q={Uri.EscapeDataString(text)}"), ct);
        using var doc = JsonDocument.Parse(body);
        var sb = new StringBuilder();
        foreach (var seg in doc.RootElement[0].EnumerateArray())
            if (seg[0].ValueKind == JsonValueKind.String) sb.Append(seg[0].GetString());
        return sb.ToString();
    }

    async Task<string> Bing(string text, CancellationToken ct)
    {
        if (_bingToken == null || Environment.TickCount64 - _bingTokenAt > 20 * 60_000)
        {
            var page = await Send(Get("https://www.bing.com/translator"), ct);
            var m = System.Text.RegularExpressions.Regex.Match(page, "params_AbusePreventionHelper\\s*=\\s*\\[(\\d+),\"([^\"]+)\",(\\d+)\\]");
            var ig = System.Text.RegularExpressions.Regex.Match(page, "IG:\"([A-Z0-9]+)\"");
            if (!m.Success || !ig.Success) throw new InvalidOperationException("bing token not found");
            _bingKey = m.Groups[1].Value; _bingToken = m.Groups[2].Value; _bingIg = ig.Groups[1].Value;
            _bingTokenAt = Environment.TickCount64;
        }
        var req = new HttpRequestMessage(HttpMethod.Post, $"https://www.bing.com/ttranslatev3?isVertical=1&IG={_bingIg}&IID=translator.5028");
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["fromLang"] = _from, ["to"] = _lang, ["text"] = text, ["token"] = _bingToken!, ["key"] = _bingKey!,
        });
        string body;
        try { body = await Send(req, ct); }
        catch { _bingToken = null; throw; }
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) { _bingToken = null; throw new InvalidOperationException("bing: unexpected response"); }
        return doc.RootElement[0].GetProperty("translations")[0].GetProperty("text").GetString() ?? "";
    }

    async Task<string> MyMemory(string text, CancellationToken ct)
    {
        var body = await Send(Get($"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(text)}&langpair={_from}|{_lang}"), ct);
        using var doc = JsonDocument.Parse(body);
        var res = doc.RootElement.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
        if (res.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("mymemory quota (429)");
        return res;
    }
}

/// <summary>LLM translation with game context. Falls back to Google on any error.</summary>
public sealed class LlmTranslator : ITranslator
{
    readonly AppSettings _s;
    readonly string _game, _sourceName;
    readonly GoogleTranslator _fallback;
    readonly Queue<string> _history = new();

    public LlmTranslator(AppSettings s, string game, string from = "en")
    {
        _s = s; _game = game;
        _sourceName = from == "ru" ? "Russian" : "English";
        _fallback = new GoogleTranslator(from);
    }

    public async Task<Translation> TranslateAsync(string text, string? speaker, string gender, CancellationToken ct)
    {
        try
        {
            var system = $"You translate {_sourceName} subtitles of the video game \"{_game}\" into Ukrainian for voice-over. " +
                         "Use natural, spoken, emotionally matching language that fits the game's setting. " +
                         "Keep character and place names consistent (transliterate them). " +
                         "Use the speaker's grammatical gender for first-person verb and adjective forms.\n" +
                         "Answer in exactly two lines:\n<translation>\nMOOD: <2-6 English words describing how the line should be voiced, e.g. 'angry, shouting' or 'calm, tired'>";
            var context = _history.Count > 0 ? "Previous lines for context:\n" + string.Join("\n", _history) + "\n\n" : "";
            var who = speaker == null ? $"Narrator / protagonist ({(gender == "f" ? "female" : "male")})" : $"{speaker} ({(gender == "f" ? "female" : "male")})";
            var user = $"{context}Speaker: {who}\nTranslate this line:\n{text}";
            var res = _s.LlmProvider == "anthropic" ? await Anthropic(system, user, ct) : await OpenAi(system, user, ct);
            _history.Enqueue((speaker != null ? speaker + ": " : "") + text);
            while (_history.Count > 6) _history.Dequeue();
            return Parse(res);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Log.Error("llm", ex);
            return new Translation(await _fallback.TranslateAsync(text, ct));
        }
    }

    static Translation Parse(string res)
    {
        string? mood = null;
        var lines = res.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        int m = lines.FindIndex(l => l.StartsWith("MOOD:", StringComparison.OrdinalIgnoreCase));
        if (m >= 0)
        {
            mood = lines[m][5..].Trim();
            lines.RemoveAt(m);
        }
        return new Translation(string.Join(" ", lines).Trim().Trim('"', '«', '»'), mood);
    }

    async Task<string> Anthropic(string system, string user, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", _s.LlmApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = Json(new
        {
            model = _s.LlmModel,
            max_tokens = 400,
            system,
            messages = new[] { new { role = "user", content = user } },
        });
        using var resp = await Http.Client.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        using var doc = JsonDocument.Parse(body);
        return string.Concat(doc.RootElement.GetProperty("content").EnumerateArray()
            .Where(b => b.GetProperty("type").GetString() == "text").Select(b => b.GetProperty("text").GetString()));
    }

    async Task<string> OpenAi(string system, string user, CancellationToken ct)
    {
        bool official = _s.LlmProvider == "openai";
        var url = (official ? "https://api.openai.com/v1" : _s.LlmBaseUrl.TrimEnd('/')) + "/chat/completions";
        var messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } };
        while (true)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("Authorization", "Bearer " + (official ? _s.OpenAiApiKey : _s.LlmApiKey));
            // Official OpenAI models are reasoning models: skip reasoning for speed (if the model allows it).
            req.Content = official
                ? (_noReasoningParam ? Json(new { model = _s.LlmModel, messages }) : Json(new { model = _s.LlmModel, messages, reasoning_effort = "none" }))
                : Json(new { model = _s.LlmModel, temperature = 0.3, messages });
            using var resp = await Http.Client.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (official && !_noReasoningParam && (int)resp.StatusCode == 400 && body.Contains("reasoning"))
            {
                _noReasoningParam = true;
                continue;
            }
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode}: {body[..Math.Min(200, body.Length)]}");
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        }
    }

    bool _noReasoningParam;

    static StringContent Json(object o) => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");
}

public static class TranslatorFactory
{
    public static bool HasLlmKey(AppSettings s) => (s.LlmProvider == "openai" ? s.OpenAiApiKey : s.LlmApiKey).Length > 0;

    public static ITranslator Create(AppSettings s, string game, string from = "en")
    {
        ITranslator online = s.Translator == "llm" && HasLlmKey(s) ? new LlmTranslator(s, game, from) : new GoogleTranslator(from);
        return OfflineTranslator.IsInstalled(from) ? new HybridTranslator(online, OfflineTranslator.For(from)) : online;
    }
}

/// <summary>
/// Online translation while the network works; the offline model when it doesn't.
/// After an online failure it stays offline for a minute instead of waiting on timeouts per line.
/// </summary>
public sealed class HybridTranslator : ITranslator
{
    readonly ITranslator _online, _offline;
    long _offlineUntil;

    public HybridTranslator(ITranslator online, ITranslator offline) { _online = online; _offline = offline; }

    bool _offlineWarm;

    /// <summary>The online connection kept open; the offline model loaded once (~1.2 s), so it is ready to stand in.</summary>
    public async Task WarmAsync()
    {
        await _online.WarmAsync();
        if (_offlineWarm) return;
        _offlineWarm = true;
        try { await _offline.TranslateAsync("да", null, "m", CancellationToken.None); } catch { }
    }

    public async Task<Translation> TranslateAsync(string text, string? speaker, string gender, CancellationToken ct)
    {
        if (Environment.TickCount64 >= _offlineUntil && System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            try
            {
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quick.CancelAfter(TimeSpan.FromSeconds(4)); // a slow line is worse than an offline one
                var online = _online.TranslateAsync(text, speaker, gender, quick.Token);
                // online late (over 1.2 s): the offline model starts too, and the first one back is said
                if (await Task.WhenAny(online, Task.Delay(1200, ct)) == online) return await online;
                var offline = _offline.TranslateAsync(text, speaker, gender, ct);
                if (await Task.WhenAny(online, offline) == online && online.IsCompletedSuccessfully) return online.Result;
                Log.Write("online translation late: the offline one said");
                return await offline;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Write($"online translation unavailable ({ex.GetType().Name}), offline for 60 s");
                _offlineUntil = Environment.TickCount64 + 60_000;
            }
        }
        return await _offline.TranslateAsync(text, speaker, gender, ct);
    }
}
