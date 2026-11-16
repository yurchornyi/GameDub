#!/usr/bin/env python3
"""
Exports the sound models of the stick lighting to ONNX, each one from the waveform in to the scores out, so the app
needs no audio code of its own beyond resampling, and checks every export against the original PyTorch model.

- EfficientAT (Schmid et al., JKU Linz, MIT; AudioSet, 527 classes): mono 32 kHz waveform [1, n] -> scores [1, 527].
  Names every sound: shots, blasts, sirens, horns, engines, doors, footsteps.
- CLAP (LAION, Apache-2.0): mono 48 kHz waveform, 10 s [1, 480000] -> [1, prompts] probabilities over the prompts in
  PROMPTS, plus the audio embedding. Hears what AudioSet has no class for: a car crash.

Run by .github/workflows/sound-models.yml on a GitHub-hosted runner. Output: one folder with the .onnx files,
efficientat_class_map.csv, clap_prompts.csv and export.json (the checks and the speed of each model).
"""
import argparse
import csv
import json
import os
import shutil
import subprocess
import sys
import time

import numpy as np
import onnxruntime as ort
import torch
import torch.nn as nn
import torch.nn.functional as F

EAT_REPO = "https://github.com/fschmid56/EfficientAT"
EAT_COMMIT = "a425fdce92572e602a1d5634799bd9f1f2efa806"
# dymn10_as was tried too: its dynamic pooling does not export to ONNX at any length (16.11.2026)
EAT_MODELS = ["mn10_as"]
CLAP_MODELS = {"clap-tiny": "laion/clap-htsat-unfused", "clap-large": "laion/larger_clap_general"}

# What a sudden sound in a game could be. "crash" prompts are the car crash; the rest are what else it may be,
# so a crash wins only when it sounds more like a crash than like any of them.
PROMPTS = [
    ("crash", "a car crash"),
    ("crash", "a car crashing into another car"),
    ("crash", "a car hitting a wall with crunching metal"),
    ("crash", "a vehicle collision"),
    ("other", "a car door closing"),
    ("other", "a door slamming"),
    ("other", "a knock on a door"),
    ("other", "footsteps"),
    ("other", "a gunshot"),
    ("other", "an explosion"),
    ("other", "glass breaking"),
    ("other", "a car engine revving"),
    ("other", "an engine idling"),
    ("other", "a car driving past"),
    ("other", "a car changing gear"),
    ("other", "tires screeching"),
    ("other", "a car horn"),
    ("other", "a siren"),
    ("other", "a punch"),
    ("other", "a heavy object falling on the floor"),
    ("other", "a metal clang"),
    ("other", "a click"),
    ("other", "a person speaking"),
    ("other", "music"),
    ("other", "a crowd"),
    ("other", "wind"),
    ("other", "rain"),
]


def bench(path, feed, threads):
    """Median milliseconds of one run on this CPU, with the given number of threads."""
    so = ort.SessionOptions()
    so.intra_op_num_threads = threads
    so.inter_op_num_threads = 1
    sess = ort.InferenceSession(path, so, providers=["CPUExecutionProvider"])
    sess.run(None, feed)
    times = []
    for _ in range(10):
        t = time.perf_counter()
        sess.run(None, feed)
        times.append((time.perf_counter() - t) * 1000)
    return round(float(np.median(times)), 1)


def real_clip(rate):
    """A few seconds that sound like something: noise bursts over a hum, as a check signal besides pure noise."""
    rng = np.random.default_rng(1)
    t = np.arange(rate * 3) / rate
    x = 0.05 * np.sin(2 * np.pi * 110 * t) + 0.01 * rng.standard_normal(t.size)
    for s in (0.5, 1.3, 2.2):
        i = int(s * rate)
        n = int(0.15 * rate)
        x[i:i + n] += 0.5 * rng.standard_normal(n) * np.exp(-np.arange(n) / (0.03 * rate))
    return x.astype(np.float32)


DOG_WAV = "https://raw.githubusercontent.com/karolpiczak/ESC-50/master/audio/1-100032-A-0.wav"
DOG = {"Dog", "Bark", "Bow-wow", "Yip", "Growling", "Howl", "Whimper (dog)", "Animal", "Domestic animals, pets"}


def dog_barking(rate):
    """A dog barking (ESC-50, K. Piczak, CC BY-NC 3.0; downloaded, never stored): a real sound the model must name.
    Matching PyTorch on noise is not enough: both can be wrong the same way."""
    import io
    import requests
    import soundfile as sf
    import torchaudio.functional as AF
    x, sr = sf.read(io.BytesIO(requests.get(DOG_WAV, timeout=60).content), dtype="float32", always_2d=True)
    return AF.resample(torch.from_numpy(x.mean(1)), sr, rate).numpy().astype(np.float32)


def export_eat(name, out, src, report):
    src = os.path.abspath(src)
    cwd = os.getcwd()
    sys.path.insert(0, src)
    os.chdir(src)  # helpers.utils reads metadata/ from the working folder
    try:
        from models.mn.model import get_model as get_mn
        from models.preprocess import AugmentMelSTFT
        from helpers.utils import NAME_TO_WIDTH
        net = get_mn(width_mult=NAME_TO_WIDTH(name), pretrained_name=name)
    finally:
        os.chdir(cwd)
    net.eval()
    mel = AugmentMelSTFT(n_mels=128, sr=32000, win_length=800, hopsize=320).eval()

    class Wave(nn.Module):
        def __init__(self):
            super().__init__()
            self.mel, self.net = mel, net

        def forward(self, wave):
            logits, _ = self.net(self.mel(wave).unsqueeze(1))
            return torch.sigmoid(logits)

    model = Wave().eval()
    path = os.path.join(out, f"efficientat-{name}.onnx")
    with torch.no_grad():
        torch.onnx.export(model, (torch.randn(1, 32000) * 0.1,), path, input_names=["wave"], output_names=["scores"],
                          dynamic_axes={"wave": {1: "samples"}}, opset_version=17, do_constant_folding=True)
    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    worst = 0.0
    tests = [torch.randn(1, n) * 0.1 for n in (16000, 32000, 48000)]
    tests.append(torch.from_numpy(real_clip(32000))[None])
    for x in tests:
        with torch.no_grad():
            ref = model(x).numpy()
        got = sess.run(None, {"wave": x.numpy()})[0]
        worst = max(worst, float(np.abs(ref - got).max()))
    labels = [r[2] for r in csv.reader(open(os.path.join(src, "metadata", "class_labels_indices.csv")))][1:]
    top = lambda p: ", ".join(f"{labels[i]} {p[i]:.2f}" for i in np.argsort(-p)[:5])
    dog = dog_barking(32000)
    with torch.no_grad():
        spec = mel(torch.from_numpy(dog)[None])
        p_torch = model(torch.from_numpy(dog)[None]).numpy()[0]
        p_own, _ = net(spec.unsqueeze(0))  # EfficientAT's own inference.py, step by step
        p_own = torch.sigmoid(p_own.float()).squeeze().numpy()
    p_onnx = sess.run(None, {"wave": dog[None]})[0][0]
    print(f"{name}: a dog barking, mel {float(spec.min()):.2f}..{float(spec.max()):.2f} mean {float(spec.mean()):.2f}")
    print(f"{name}:   EfficientAT's own way: {top(p_own)}")
    print(f"{name}:   pytorch wrapper: {top(p_torch)}")
    print(f"{name}:   onnx: {top(p_onnx)}; mean {float(p_onnx.mean()):.3f}")
    heard = labels[int(np.argmax(p_onnx))] in DOG
    ok = worst < 1e-3 and heard
    print(f"{name}: onnx vs pytorch, worst difference {worst:.2e}; hears the dog: {heard}; {'OK' if ok else 'FAIL'}")
    report[f"efficientat-{name}"] = {
        "file": os.path.basename(path), "mb": round(os.path.getsize(path) / 2**20, 1), "worst_diff": worst, "ok": ok,
        "dog": top(p_onnx),
        "ms_1s_1thread": bench(path, {"wave": np.zeros((1, 32000), np.float32) + 0.01}, 1),
        "ms_1s_2threads": bench(path, {"wave": np.zeros((1, 32000), np.float32) + 0.01}, 2),
    }
    if not ok:
        os.remove(path)
        raise RuntimeError(f"{name}: the export does not match the model")


def export_clap(key, repo, out, report):
    from transformers import ClapModel, ClapProcessor
    from onnxruntime.quantization import QuantType, quantize_dynamic

    clap = ClapModel.from_pretrained(repo).eval()
    proc = ClapProcessor.from_pretrained(repo)
    fe = proc.feature_extractor
    texts = [p for _, p in PROMPTS]
    tok = proc.tokenizer(texts, padding=True, return_tensors="pt")
    with torch.no_grad():
        text = F.normalize(clap.get_text_features(**tok), dim=-1)
    scale = float(clap.logit_scale_a.exp())

    class Wave(nn.Module):
        """ClapFeatureExtractor (log-mel in dB, slaney mel 50..14000 Hz, 64 bands, hop 480) and the audio tower."""

        def __init__(self):
            super().__init__()
            self.clap = clap
            self.register_buffer("window", torch.hann_window(1024, periodic=True))
            self.register_buffer("melT", torch.tensor(np.asarray(fe.mel_filters_slaney).T, dtype=torch.float32))
            self.register_buffer("text", text)

        def forward(self, wave):
            spec = torch.stft(wave, 1024, hop_length=480, win_length=1024, window=self.window, center=True,
                              pad_mode="reflect", return_complex=False)
            power = spec.pow(2).sum(-1)
            mel = torch.matmul(self.melT, power).clamp(min=1e-10)
            feats = (10.0 * torch.log10(mel)).transpose(1, 2).unsqueeze(1)
            emb = F.normalize(self.clap.get_audio_features(input_features=feats), dim=-1)
            return (scale * emb @ self.text.T).softmax(-1), emb

    model = Wave().eval()
    path = os.path.join(out, f"{key}.onnx")
    x0 = torch.randn(1, 480000) * 0.1
    with torch.no_grad():
        torch.onnx.export(model, (x0,), path, input_names=["wave"], output_names=["probs", "embedding"],
                          opset_version=17, do_constant_folding=True)
    qpath = os.path.join(out, f"{key}.int8.onnx")
    # matrix products only: ONNX Runtime has no int8 convolution on the CPU (ConvInteger)
    quantize_dynamic(path, qpath, weight_type=QuantType.QInt8, op_types_to_quantize=["MatMul", "Gemm"])

    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    qsess = ort.InferenceSession(qpath, providers=["CPUExecutionProvider"])
    clip = np.tile(real_clip(48000)[:48000], 10)
    tests = [x0.numpy(), (np.random.default_rng(2).standard_normal((1, 480000)) * 0.02).astype(np.float32), clip[None]]
    worst_cos, worst_q = 1.0, 1.0
    for x in tests:
        # the reference: the processor and the model exactly as Hugging Face runs them
        feats = fe(x[0], sampling_rate=48000, return_tensors="pt")
        with torch.no_grad():
            ref = F.normalize(clap.get_audio_features(input_features=feats["input_features"]), dim=-1).numpy()[0]
        got = sess.run(None, {"wave": x})[1][0]
        q = qsess.run(None, {"wave": x})[1][0]
        worst_cos = min(worst_cos, float(ref @ got))
        worst_q = min(worst_q, float(got @ q))
    ok = worst_cos > 0.999
    print(f"{key}: onnx vs hugging face, worst cosine {worst_cos:.5f} {'OK' if ok else 'FAIL'}; int8 vs float {worst_q:.4f}")
    feed = {"wave": clip[None]}
    report[key] = {
        "repo": repo, "file": os.path.basename(path), "int8_file": os.path.basename(qpath),
        "mb": round(os.path.getsize(path) / 2**20, 1), "int8_mb": round(os.path.getsize(qpath) / 2**20, 1),
        "worst_cos": worst_cos, "int8_worst_cos": worst_q, "ok": ok, "logit_scale": scale,
        "ms_1thread": bench(path, feed, 1), "ms_2threads": bench(path, feed, 2),
        "int8_ms_1thread": bench(qpath, feed, 1), "int8_ms_2threads": bench(qpath, feed, 2),
    }
    if not ok:
        os.remove(path)
        os.remove(qpath)
        raise RuntimeError(f"{key}: the export does not match the model")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--work", default="work/src")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    src = os.path.join(a.work, "EfficientAT")
    if not os.path.isdir(src):
        subprocess.run(["git", "clone", "-q", EAT_REPO, src], check=True)
        subprocess.run(["git", "-C", src, "checkout", "-q", EAT_COMMIT], check=True)
    shutil.copy(os.path.join(src, "metadata", "class_labels_indices.csv"), os.path.join(a.out, "efficientat_class_map.csv"))
    with open(os.path.join(a.out, "clap_prompts.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["index", "kind", "prompt"])
        for i, (k, p) in enumerate(PROMPTS):
            w.writerow([i, k, p])

    report = {"efficientat_commit": EAT_COMMIT, "torch": torch.__version__, "onnxruntime": ort.__version__}
    # the first of each is needed; the others are compared with it and may fail without stopping the run
    for i, n in enumerate(EAT_MODELS):
        try:
            export_eat(n, a.out, src, report)
        except Exception as e:
            if i == 0:
                raise
            print(f"{n}: export failed: {e!r}")
            report[f"efficientat-{n}"] = {"error": repr(e)}
    for i, (k, r) in enumerate(CLAP_MODELS.items()):
        try:
            export_clap(k, r, a.out, report)
        except Exception as e:
            if i == 0:
                raise
            print(f"{k}: export failed: {e!r}")
            report[k] = {"error": repr(e)}
    with open(os.path.join(a.out, "export.json"), "w") as f:
        json.dump(report, f, indent=1)
    print(json.dumps(report, indent=1))


if __name__ == "__main__":
    main()
