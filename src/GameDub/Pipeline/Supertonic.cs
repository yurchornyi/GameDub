using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GameDub.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GameDub.Pipeline;

/// <summary>
/// Supertonic 3 (Supertone, OpenRAIL-M): fully offline neural TTS via ONNX Runtime on the CPU.
/// Models live in models\supertonic3 (~380 MB); loaded lazily on first use and kept in memory.
/// Port of the MIT-licensed sample from github.com/supertone-inc/supertonic.
/// </summary>
public sealed class SupertonicTts : ITtsEngine, IDisposable
{
    public static string ModelDir => Path.Combine(Paths.Root, "models", "supertonic3");
    public static bool IsInstalled => File.Exists(Path.Combine(ModelDir, "onnx", "vector_estimator.onnx")) && File.Exists(Path.Combine(ModelDir, "onnx", "vocoder.onnx"));

    /// <summary>Denoising steps: 8 is the sample default; fewer is faster, slightly less clean.</summary>
    public static int Steps = 5;

    // Presets picked by ear for Ukrainian; the first of each list is the narrator.
    static readonly string[] Male = { "M1", "M2", "M3", "M5" };
    static readonly string[] Female = { "F1", "F2", "F3", "F4", "F5" };

    readonly AppSettings _s;
    readonly SemaphoreSlim _gate = new(1, 1);
    InferenceSession? _dp, _enc, _est, _voc;
    Dictionary<int, long>? _indexer;
    int _sampleRate, _baseChunk, _compress, _latentDim;
    readonly Dictionary<string, Style> _styles = new();

    sealed record Style(DenseTensor<float> Ttl, DenseTensor<float> Dp);

    public SupertonicTts(AppSettings s)
    {
        _s = s;
        if (s.SupertonicSteps is >= 2 and <= 16) Steps = s.SupertonicSteps;
    }

    static string StyleName(string gender, int variant)
    {
        if (variant < 0) return (gender == "f" ? "F" : "M") + (-variant); // raw preset for tests
        var list = gender == "f" ? Female : Male;
        return list[variant % list.Length];
    }

    public string Describe(TtsRequest r) => "supertonic:" + StyleName(r.Gender, r.Variant);

    public async Task<byte[]> SynthesizeAsync(TtsRequest r, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                EnsureLoaded();
                float speed = (float)Math.Clamp(1.05 * (1 + _s.RatePercent / 100.0) * r.Pace, 0.6, 1.6);
                var style = LoadStyle(StyleName(r.Gender, r.Variant));
                const string lang = "uk";
                var pcm = new List<float>();
                foreach (var chunk in Chunk(r.Text, 300))
                {
                    ct.ThrowIfCancellationRequested();
                    if (pcm.Count > 0) pcm.AddRange(new float[(int)(0.3 * _sampleRate)]);
                    pcm.AddRange(Infer(chunk, lang, style, speed));
                }
                return ToWav(pcm, _sampleRate, leadMs: 120, trailMs: 300);
            }, ct);
        }
        finally { _gate.Release(); }
    }

    void EnsureLoaded()
    {
        if (_est != null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var onnx = Path.Combine(ModelDir, "onnx");
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(onnx, "tts.json"))))
        {
            _sampleRate = doc.RootElement.GetProperty("ae").GetProperty("sample_rate").GetInt32();
            _baseChunk = doc.RootElement.GetProperty("ae").GetProperty("base_chunk_size").GetInt32();
            _compress = doc.RootElement.GetProperty("ttl").GetProperty("chunk_compress_factor").GetInt32();
            _latentDim = doc.RootElement.GetProperty("ttl").GetProperty("latent_dim").GetInt32();
        }
        var idx = JsonSerializer.Deserialize<long[]>(File.ReadAllText(Path.Combine(onnx, "unicode_indexer.json")))!;
        _indexer = new Dictionary<int, long>(idx.Length);
        for (int i = 0; i < idx.Length; i++) _indexer[i] = idx[i];

        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        opts.IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount - 2); // leave headroom for the game
        _dp = new InferenceSession(Path.Combine(onnx, "duration_predictor.onnx"), opts);
        _enc = new InferenceSession(Path.Combine(onnx, "text_encoder.onnx"), opts);
        _est = new InferenceSession(Path.Combine(onnx, "vector_estimator.onnx"), opts);
        _voc = new InferenceSession(Path.Combine(onnx, "vocoder.onnx"), opts);
        Log.Write($"supertonic loaded in {sw.ElapsedMilliseconds} ms");
    }

    Style LoadStyle(string name)
    {
        if (_styles.TryGetValue(name, out var s)) return s;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ModelDir, "voice_styles", name + ".json")));
        DenseTensor<float> Read(string key)
        {
            var el = doc.RootElement.GetProperty(key);
            var dims = el.GetProperty("dims").EnumerateArray().Select(d => d.GetInt32()).ToArray();
            var data = el.GetProperty("data").EnumerateArray().SelectMany(a => a.EnumerateArray()).SelectMany(b => b.EnumerateArray()).Select(v => v.GetSingle()).ToArray();
            return new DenseTensor<float>(data, dims);
        }
        return _styles[name] = new Style(Read("style_ttl"), Read("style_dp"));
    }

    // ---- text ----

    static readonly Regex Spaces = new(@"\s+");

    string Preprocess(string text, string lang)
    {
        text = text.Normalize(NormalizationForm.FormKD);
        text = text.Replace('–', '-').Replace('‑', '-').Replace('—', '-').Replace('_', ' ')
            .Replace('“', '"').Replace('”', '"').Replace('‘', '\'').Replace('’', '\'').Replace('´', '\'').Replace('`', '\'')
            .Replace('[', ' ').Replace(']', ' ').Replace('|', ' ').Replace('/', ' ').Replace('#', ' ');
        text = Regex.Replace(text, @"[♥☆♡©\\]", "");
        text = new string(text.Where(c => c < 0x1F000 && !(c >= 0x2600 && c <= 0x27BF)).ToArray()); // emoji / dingbats
        text = Regex.Replace(text, @" ([,.!?;:'])", "$1");
        text = Spaces.Replace(text, " ").Trim();
        if (text.Length == 0) text = ".";
        if (!Regex.IsMatch(text, @"[.!?;:,'""…)»]$")) text += ".";
        return $"<{lang}>{text}</{lang}>";
    }

    static IEnumerable<string> Chunk(string text, int maxLen)
    {
        var sentences = Regex.Split(text.Trim(), @"(?<=[.!?…])\s+");
        var cur = "";
        foreach (var s in sentences)
        {
            if (s.Length == 0) continue;
            if (cur.Length + s.Length + 1 <= maxLen) cur = cur.Length == 0 ? s : cur + " " + s;
            else { if (cur.Length > 0) yield return cur; cur = s; }
        }
        if (cur.Length > 0) yield return cur;
    }

    // ---- inference ----

    float[] Infer(string text, string lang, Style style, float speed)
    {
        var processed = Preprocess(text, lang);
        int n = processed.Length;
        var ids = new DenseTensor<long>(new[] { 1, n });
        for (int i = 0; i < n; i++) ids[0, i] = _indexer!.TryGetValue(processed[i], out var v) ? v : 0;
        var textMask = new DenseTensor<float>(new[] { 1, 1, n });
        for (int i = 0; i < n; i++) textMask[0, 0, i] = 1f;

        using var dpOut = _dp!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("text_ids", ids), NamedOnnxValue.CreateFromTensor("style_dp", style.Dp), NamedOnnxValue.CreateFromTensor("text_mask", textMask),
        });
        float duration = dpOut.First(o => o.Name == "duration").AsTensor<float>()[0] / speed;

        using var encOut = _enc!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("text_ids", ids), NamedOnnxValue.CreateFromTensor("style_ttl", style.Ttl), NamedOnnxValue.CreateFromTensor("text_mask", textMask),
        });
        var textEmb = encOut.First(o => o.Name == "text_emb").AsTensor<float>().ToDenseTensor();

        long wavLen = (long)(duration * _sampleRate);
        int chunkSize = _baseChunk * _compress;
        int latentLen = (int)((wavLen + chunkSize - 1) / chunkSize);
        int latentDim = _latentDim * _compress;
        var rnd = new Random();
        var xt = new DenseTensor<float>(new[] { 1, latentDim, latentLen });
        for (int d = 0; d < latentDim; d++)
            for (int t = 0; t < latentLen; t++)
            {
                double u1 = 1.0 - rnd.NextDouble(), u2 = 1.0 - rnd.NextDouble();
                xt[0, d, t] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
            }
        var latentMask = new DenseTensor<float>(new[] { 1, 1, latentLen });
        for (int t = 0; t < latentLen; t++) latentMask[0, 0, t] = 1f;

        var total = new DenseTensor<float>(new[] { (float)Steps }, new[] { 1 });
        for (int step = 0; step < Steps; step++)
        {
            var cur = new DenseTensor<float>(new[] { (float)step }, new[] { 1 });
            using var estOut = _est!.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("noisy_latent", xt), NamedOnnxValue.CreateFromTensor("text_emb", textEmb),
                NamedOnnxValue.CreateFromTensor("style_ttl", style.Ttl), NamedOnnxValue.CreateFromTensor("text_mask", textMask),
                NamedOnnxValue.CreateFromTensor("latent_mask", latentMask), NamedOnnxValue.CreateFromTensor("total_step", total),
                NamedOnnxValue.CreateFromTensor("current_step", cur),
            });
            xt = estOut.First(o => o.Name == "denoised_latent").AsTensor<float>().ToDenseTensor();
        }

        using var vocOut = _voc!.Run(new[] { NamedOnnxValue.CreateFromTensor("latent", xt) });
        var wav = vocOut.First(o => o.Name == "wav_tts").AsTensor<float>().ToArray();
        int keep = (int)Math.Min(wav.Length, wavLen);
        return wav.Take(keep).ToArray();
    }

    static byte[] ToWav(List<float> pcm, int rate, int leadMs, int trailMs)
    {
        int lead = rate * leadMs / 1000, trail = rate * trailMs / 1000;
        int samples = lead + pcm.Count + trail;
        var ms = new MemoryStream(44 + samples * 2);
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + samples * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(samples * 2);
        for (int i = 0; i < lead; i++) w.Write((short)0);
        foreach (var s in pcm) w.Write((short)(Math.Clamp(s, -1f, 1f) * 32767));
        for (int i = 0; i < trail; i++) w.Write((short)0);
        return ms.ToArray();
    }

    public void Dispose()
    {
        _dp?.Dispose(); _enc?.Dispose(); _est?.Dispose(); _voc?.Dispose();
    }
}
