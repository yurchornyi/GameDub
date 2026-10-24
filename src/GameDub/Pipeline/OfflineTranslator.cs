using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GameDub.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace GameDub.Pipeline;

/// <summary>
/// English → Ukrainian on the device with a Helsinki-NLP Marian model (CC-BY 4.0) in ONNX, int8:
/// models\opus-mt-tc-big-en-uk (tc-big en→zle, ~350 MB, forced ">>ukr<<") is preferred,
/// models\opus-mt-en-uk (~105 MB) is the small fallback. SentencePiece-unigram tokenization
/// and greedy decoding are implemented here; no Python, no extra packages.
/// </summary>
public sealed class OfflineTranslator : ITranslator, IDisposable
{
    static readonly string[] Candidates = { "opus-mt-tc-big-en-uk", "opus-mt-en-uk" };

    public static string? FindModelDir() => Candidates.Select(c => Path.Combine(Paths.Root, "models", c)).FirstOrDefault(d =>
        Directory.Exists(d)
        && Directory.GetFiles(d, "encoder_model*.onnx").Length > 0
        && Directory.GetFiles(d, "decoder_model*.onnx").Length > 0
        && File.Exists(Path.Combine(d, "config.json"))
        && (File.Exists(Path.Combine(d, "tokenizer.json")) || File.Exists(Path.Combine(d, "source.spm"))));

    public static bool IsInstalled => FindModelDir() != null;

    static OfflineTranslator? _shared;
    public static OfflineTranslator Shared => _shared ??= new OfflineTranslator(FindModelDir()!);

    readonly string _dir;
    long _eos = 0, _pad = 61586, _langToken = -1; // Marian: decoder_start_token_id == pad_token_id
    readonly SemaphoreSlim _gate = new(1, 1);

    public OfflineTranslator(string dir) => _dir = dir;
    public string ModelName => Path.GetFileName(_dir);
    readonly Dictionary<string, string> _cache = new();
    InferenceSession? _enc, _dec;
    Dictionary<string, (int id, float score)> _pieces = new();
    string[] _idToPiece = Array.Empty<string>();
    int _maxPieceLen, _unkId = 1;
    float _unkScore;

    public async Task<Translation> TranslateAsync(string text, string? speaker, string gender, CancellationToken ct)
    {
        if (_cache.TryGetValue(text, out var hit)) return new Translation(hit, Offline: true);
        await _gate.WaitAsync(ct);
        try
        {
            var res = await Task.Run(() => Translate(text), ct);
            if (_cache.Count > 500) _cache.Clear();
            _cache[text] = res;
            return new Translation(res, Offline: true);
        }
        finally { _gate.Release(); }
    }

    public string Translate(string text)
    {
        EnsureLoaded();
        // One sentence at a time keeps Marian accurate and the decoder sequences short.
        var parts = System.Text.RegularExpressions.Regex.Split(text.Trim(), @"(?<=[.!?…])\s+").Where(p => p.Length > 0);
        return string.Join(" ", parts.Select(TranslateSentence)).Trim();
    }

    void EnsureLoaded()
    {
        if (_dec != null) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "config.json"))))
        {
            _eos = cfg.RootElement.GetProperty("eos_token_id").GetInt64();
            _pad = cfg.RootElement.GetProperty("decoder_start_token_id").GetInt64();
        }
        float minScore = 0;
        if (File.Exists(Path.Combine(_dir, "tokenizer.json")))
        {
            // HF tokenizers format: vocab index == model id
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "tokenizer.json")));
            var list = new List<string>();
            foreach (var entry in doc.RootElement.GetProperty("model").GetProperty("vocab").EnumerateArray())
            {
                var piece = entry[0].GetString() ?? "";
                float score = entry[1].GetSingle();
                int id = list.Count;
                list.Add(piece);
                if (piece is "</s>" or "<pad>") continue;
                if (piece == "<unk>") { _unkId = id; continue; }
                _pieces.TryAdd(piece, (id, score));
                minScore = Math.Min(minScore, score);
            }
            _idToPiece = list.ToArray();
        }
        else
        {
            // Marian format: pieces + scores from source.spm, ids from vocab.json
            var vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(_dir, "vocab.json")))!;
            _idToPiece = new string[vocab.Values.Max() + 1];
            foreach (var (piece, id) in vocab) _idToPiece[id] = piece;
            if (vocab.TryGetValue("<unk>", out var unk)) _unkId = unk;
            if (vocab.TryGetValue(">>ukr<<", out var ukr)) _langToken = ukr;
            foreach (var (piece, score) in ReadSpm(Path.Combine(_dir, "source.spm")))
            {
                if (piece.StartsWith('<') && piece.EndsWith('>')) continue;
                if (!vocab.TryGetValue(piece, out var id)) continue;
                _pieces.TryAdd(piece, (id, score));
                minScore = Math.Min(minScore, score);
            }
        }
        _maxPieceLen = _pieces.Keys.Max(p => p.Length);
        _unkScore = minScore - 10f;
        // Pieces that only Russian uses: never let the decoder pick them for Ukrainian output.
        _banned = new HashSet<int>(Enumerable.Range(0, _idToPiece.Length).Where(i => _idToPiece[i] is { } p && p.IndexOfAny(RussianOnly) >= 0)) { (int)_pad };

        var opts = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, IntraOpNumThreads = Math.Max(2, Environment.ProcessorCount / 2) };
        _enc = new InferenceSession(Directory.GetFiles(_dir, "encoder_model*.onnx").OrderBy(f => f.Length).First(), opts);
        _dec = new InferenceSession(Directory.GetFiles(_dir, "decoder_model*.onnx").Where(f => !f.Contains("merged") && !f.Contains("with_past")).OrderBy(f => f.Length).First(), opts);
        Log.Write($"offline translator {ModelName} loaded in {sw.ElapsedMilliseconds} ms ({_pieces.Count} pieces, lang token {_langToken}, {_banned.Count} banned)");
    }

    static readonly char[] RussianOnly = { 'ы', 'Ы', 'э', 'Э', 'ъ', 'Ъ', 'ё', 'Ё' };
    HashSet<int> _banned = new();

    /// <summary>Minimal protobuf reader for SentencePiece ModelProto: repeated pieces {1: piece, 2: score}.</summary>
    static IEnumerable<(string piece, float score)> ReadSpm(string path)
    {
        var b = File.ReadAllBytes(path);
        int pos = 0;
        ulong Varint(ref int p) { ulong v = 0; int shift = 0; while (true) { byte x = b[p++]; v |= (ulong)(x & 0x7F) << shift; if ((x & 0x80) == 0) return v; shift += 7; } }
        var result = new List<(string, float)>();
        while (pos < b.Length)
        {
            ulong tag = Varint(ref pos);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            if (wire == 2)
            {
                int len = (int)Varint(ref pos), end = pos + len;
                if (field == 1)
                {
                    string piece = ""; float score = 0; int p = pos;
                    while (p < end)
                    {
                        ulong t = Varint(ref p);
                        int f = (int)(t >> 3), w = (int)(t & 7);
                        if (w == 2) { int l = (int)Varint(ref p); if (f == 1) piece = Encoding.UTF8.GetString(b, p, l); p += l; }
                        else if (w == 5) { if (f == 2) score = BitConverter.ToSingle(b, p); p += 4; }
                        else if (w == 0) Varint(ref p);
                        else if (w == 1) p += 8;
                    }
                    result.Add((piece, score));
                }
                pos = end;
            }
            else if (wire == 0) Varint(ref pos);
            else if (wire == 5) pos += 4;
            else if (wire == 1) pos += 8;
            else break;
        }
        return result;
    }

    // ---- SentencePiece unigram (Viterbi) ----

    List<long> Encode(string text)
    {
        var ids = new List<long>();
        if (_langToken >= 0) ids.Add(_langToken); // multi-target model: ask for Ukrainian explicitly
        foreach (var word in text.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = "▁" + word;
            int n = s.Length;
            var best = new float[n + 1];
            var back = new int[n + 1];
            var backId = new int[n + 1];
            for (int i = 1; i <= n; i++) best[i] = float.NegativeInfinity;
            for (int end = 1; end <= n; end++)
            {
                for (int len = 1; len <= Math.Min(_maxPieceLen, end); len++)
                {
                    int start = end - len;
                    if (float.IsNegativeInfinity(best[start])) continue;
                    if (_pieces.TryGetValue(s.Substring(start, len), out var p) && best[start] + p.score > best[end])
                    {
                        best[end] = best[start] + p.score; back[end] = start; backId[end] = p.id;
                    }
                }
                if (float.IsNegativeInfinity(best[end]) && !float.IsNegativeInfinity(best[end - 1]))
                {
                    best[end] = best[end - 1] + _unkScore; back[end] = end - 1; backId[end] = _unkId; // unknown character
                }
            }
            var wordIds = new List<long>();
            for (int pos = n; pos > 0; pos = back[pos]) wordIds.Add(backId[pos]);
            wordIds.Reverse();
            ids.AddRange(wordIds);
        }
        ids.Add(_eos);
        return ids;
    }

    string Decode(IEnumerable<long> ids)
    {
        var sb = new StringBuilder();
        foreach (var id in ids)
            if (id != _eos && id != _pad && id != _unkId && id < _idToPiece.Length) sb.Append(_idToPiece[id]);
        return sb.ToString().Replace('▁', ' ').Trim();
    }

    // ---- Marian inference, greedy ----

    string TranslateSentence(string sentence)
    {
        var src = Encode(sentence);
        int n = src.Count;
        var inputIds = new DenseTensor<long>(src.ToArray(), new[] { 1, n });
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), new[] { 1, n });
        using var encOut = _enc!.Run(new[] { NamedOnnxValue.CreateFromTensor("input_ids", inputIds), NamedOnnxValue.CreateFromTensor("attention_mask", mask) });
        var hidden = encOut.First().AsTensor<float>().ToDenseTensor();

        var output = new List<long> { _pad };
        int maxLen = Math.Min(256, 2 * n + 16);
        while (output.Count < maxLen)
        {
            var decIds = new DenseTensor<long>(output.ToArray(), new[] { 1, output.Count });
            using var decOut = _dec!.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("input_ids", decIds),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
            });
            var logits = decOut.First().AsTensor<float>();
            int t = output.Count - 1, vocab = logits.Dimensions[2];
            long bestId = _eos; float bestVal = float.NegativeInfinity;
            for (int v = 0; v < vocab; v++)
            {
                if (_banned.Contains(v)) continue; // pad + Russian-only pieces
                float val = logits[0, t, v];
                if (val > bestVal) { bestVal = val; bestId = v; }
            }
            if (bestId == _eos) break;
            output.Add(bestId);
            // Guard against degenerate loops ("так так так так…").
            if (output.Count > 6 && output.Skip(output.Count - 4).Distinct().Count() == 1) break;
        }
        return Decode(output.Skip(1));
    }

    public void Dispose() { _enc?.Dispose(); _dec?.Dispose(); }
}
