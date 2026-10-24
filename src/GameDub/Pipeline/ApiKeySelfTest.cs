using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameDub.Core;

namespace GameDub.Pipeline;

/// <summary>Offline regression checks: --test keys. Uses dummy credentials and an in-memory HTTP handler.</summary>
internal static class ApiKeySelfTest
{
    static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }

    public static async Task Run(StringBuilder sb)
    {
        var migrated = JsonSerializer.Deserialize<AppSettings>("""
            {"GoogleApiKey":" legacy-google ","ElevenApiKey":"legacy-eleven"}
            """)!;
        migrated.NormalizeApiKeys();
        Check(migrated.GoogleApiKeys.SequenceEqual(new[] { "legacy-google" })
            && migrated.ElevenApiKeys.SequenceEqual(new[] { "legacy-eleven" }), "Single-key migration lost a credential");
        migrated.NormalizeApiKeys();
        Check(migrated.GoogleApiKey == null && migrated.ElevenApiKey == null
            && migrated.GoogleApiKeys.Count == 1 && migrated.ElevenApiKeys.Count == 1, "Migration is not idempotent");
        sb.AppendLine("PASS: legacy keys migrate without duplication");

        var settings = new AppSettings
        {
            GoogleApiKeys = new() { " one ", "", "one", null!, "two", "three", "four", "five", "six" },
            ElevenApiKeys = new() { "one", "two", "three", "four", "five", "six" },
        };
        settings.NormalizeApiKeys();
        var expected = new[] { "one", "two", "three", "four", "five" };
        Check(settings.GoogleApiKeys.SequenceEqual(expected) && settings.ElevenApiKeys.SequenceEqual(expected),
            "Key lists must trim, deduplicate, ignore blanks and stop at five");
        sb.AppendLine("PASS: both providers enforce five unique non-empty keys");

        var path = Path.Combine(Path.GetTempPath(), $"GameDub-keys-{Guid.NewGuid():N}.json");
        try
        {
            Store.Save(path, migrated);
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            Check(!doc.RootElement.TryGetProperty("GoogleApiKey", out _) && !doc.RootElement.TryGetProperty("ElevenApiKey", out _),
                "Legacy credentials should not be persisted twice");
            var loaded = Store.Load<AppSettings>(path);
            loaded.NormalizeApiKeys();
            Check(loaded.GoogleApiKeys.SequenceEqual(migrated.GoogleApiKeys)
                && loaded.ElevenApiKeys.SequenceEqual(migrated.ElevenApiKeys), "Key lists failed to round-trip");
            loaded.GoogleApiKeys.Clear(); loaded.ElevenApiKeys.Clear();
            Store.Save(path, loaded);
            loaded = Store.Load<AppSettings>(path); loaded.NormalizeApiKeys();
            Check(loaded.GoogleApiKeys.Count == 0 && loaded.ElevenApiKeys.Count == 0, "Deleted legacy keys reappeared");
            sb.AppendLine("PASS: saving, reloading and deleting migrated keys");
        }
        finally { File.Delete(path); }

        foreach (bool eleven in new[] { false, true })
        {
            string provider = eleven ? "eleven" : "google";
            var attempts = new List<string>();
            using var client = new HttpClient(new FakeHandler(req =>
            {
                string key = req.Headers.GetValues(eleven ? "xi-api-key" : "x-goog-api-key").Single();
                if (req.Method == HttpMethod.Get)
                    return Reply(HttpStatusCode.OK, JsonSerializer.Serialize(new { voices = new[]
                    {
                        new { voice_id = key + "-male-1", name = "Male 1", category = "premade", labels = new { gender = "male" } },
                        new { voice_id = key + "-male-2", name = "Male 2", category = "premade", labels = new { gender = "male" } },
                    } }));
                attempts.Add(key);
                if (key == "bad") return Reply(HttpStatusCode.Unauthorized, "invalid credential");
                if (key.StartsWith("spent", StringComparison.Ordinal)) return Reply(HttpStatusCode.TooManyRequests, "quota exceeded");
                if (eleven)
                {
                    Check(req.RequestUri!.AbsolutePath.Contains(key + "-male-1", StringComparison.Ordinal), "Voices leaked between accounts");
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
                }
                return Reply(HttpStatusCode.OK, "{\"audioContent\":\"AQID\"}");
            }));
            var s = new AppSettings { TtsEngine = provider };
            if (eleven) s.ElevenApiKeys = new() { "bad", "spent", "good" };
            else s.GoogleApiKeys = new() { "bad", "spent", "good" };
            var engine = Voices.CreateEngine(s, client);
            var request = new TtsRequest("Test", "m", 0);
            Check((await engine.SynthesizeAsync(request, CancellationToken.None)).SequenceEqual(new byte[] { 1, 2, 3 }), "Audio lost on failover");
            Check(attempts.SequenceEqual(new[] { "bad", "spent", "good" }), provider + " did not try keys in order");
            attempts.Clear();
            await engine.SynthesizeAsync(request, CancellationToken.None);
            Check(attempts.SequenceEqual(new[] { "good" }), provider + " retried a resting key");
            sb.AppendLine($"PASS: {provider} tries the next key and respects per-key cooldowns");

            attempts.Clear();
            var five = new List<string> { "spent1", "spent2", "spent3", "spent4", "good" };
            if (eleven) s.ElevenApiKeys = five; else s.GoogleApiKeys = five;
            await Voices.CreateEngine(s, client).SynthesizeAsync(request, CancellationToken.None);
            Check(attempts.SequenceEqual(five), provider + " did not reach the fifth key");
            sb.AppendLine($"PASS: {provider} can use the fifth key");

            attempts.Clear();
            var fallback = new StubTts();
            engine = Voices.WithKeys(new[] { "bad", "spent" }, key => eleven
                ? new ElevenTts(s, key, client) : new GoogleTts(s, key, client), fallback);
            await engine.SynthesizeAsync(request, CancellationToken.None);
            Check(fallback.Calls == 1 && attempts.SequenceEqual(new[] { "bad", "spent" }), provider + " skipped remaining keys or fallback");
            attempts.Clear();
            await engine.SynthesizeAsync(request, CancellationToken.None);
            Check(fallback.Calls == 2 && attempts.Count == 0, provider + " did not rest unavailable keys");
            sb.AppendLine($"PASS: {provider} falls back after all its keys fail");
        }

        var autoAttempts = new List<string>();
        using (var client = new HttpClient(new FakeHandler(req =>
        {
            bool eleven = req.RequestUri!.Host == "api.elevenlabs.io";
            string key = req.Headers.GetValues(eleven ? "xi-api-key" : "x-goog-api-key").Single();
            if (req.Method == HttpMethod.Get) return Reply(HttpStatusCode.OK, "{\"voices\":[]}");
            autoAttempts.Add(key);
            return eleven ? Reply(HttpStatusCode.TooManyRequests, "quota") : Reply(HttpStatusCode.OK, "{\"audioContent\":\"AQID\"}");
        })))
        {
            var auto = new AppSettings { TtsEngine = "auto", ElevenApiKeys = new() { "eleven1", "eleven2" }, GoogleApiKeys = new() { "google1" } };
            await Voices.CreateEngine(auto, client).SynthesizeAsync(new TtsRequest("Test", "m", 0), CancellationToken.None);
            Check(autoAttempts.SequenceEqual(new[] { "eleven1", "eleven2", "google1" }), "Auto mode changed providers before trying all keys");
        }
        sb.AppendLine("PASS: auto mode exhausts provider keys before changing provider");

        using (var cts = new CancellationTokenSource())
        {
            var fallback = new StubTts();
            var engine = new FallbackTts(new StubTts(cts), fallback);
            try
            {
                await engine.SynthesizeAsync(new TtsRequest("Test", "m", 0), cts.Token);
                throw new InvalidOperationException("Cancellation was swallowed");
            }
            catch (OperationCanceledException) { Check(fallback.Calls == 0, "Cancellation invoked the next key"); }
        }
        sb.AppendLine("PASS: cancellation stops key fallback");
        sb.AppendLine("ALL KEY TESTS PASSED (offline; no real credentials used)");
    }

    static HttpResponseMessage Reply(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(send(request));
        }
    }

    sealed class StubTts(CancellationTokenSource? cancel = null) : ITtsEngine
    {
        public int Calls;
        public string Describe(TtsRequest r) => "stub";
        public Task<byte[]> SynthesizeAsync(TtsRequest r, CancellationToken ct)
        {
            Calls++;
            if (cancel != null) { cancel.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(new byte[] { 9 });
        }
    }
}
