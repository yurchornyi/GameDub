#!/usr/bin/env python3
"""
Exports the stick lighting's helper models to ONNX (src/GameDub/Lights/SoundFx.cs), each from the waveform in to the
scores out, the front end inside, so the app needs no audio code beyond resampling:
- EfficientAT mn10_as (Schmid et al., JKU Linz, MIT; AudioSet, 527 classes), the blast check: mono 32 kHz waveform
  [1, n] -> sigmoid scores [1, 527].
- CLAP (LAION clap-htsat-unfused, Apache-2.0), the crash check in racing games: mono 48 kHz waveform, 10 s [1, 480000]
  -> probabilities over the descriptions in PROMPTS [1, 27]; int8 matrix products (32 MB instead of 112). It hears what
  AudioSet has no class for, a car crash; no crash recordings were found to check it on, so it is tried in a game.

The model hears only 10 s clips, as it was trained (short clips padded with silence): one second alone scores every
class near 1.0, with PyTorch 1.13 as the repository pins and with 2.5 alike (16.11.2026). The app pads its one-second
cut with 9 s of silence; the check below makes the export name a padded second of a real dog bark, besides matching
PyTorch (which alone proves nothing: both can be wrong the same way).

Tried and dropped (16-17.11.2026, on 3,200 public sounds): EfficientAT in place of YAMNet (fewer shots and horns
caught, 30 times the time at 10 s), dymn10_as (does not export to ONNX at any length).

Run by .github/workflows/sound-models.yml on a GitHub-hosted runner. Output: efficientat.onnx,
efficientat_class_map.csv, clap.onnx, clap_prompts.csv and export.json (the checks and the speed), for the GameDub
releases that src/tools/get-models.ps1 downloads.
"""
import argparse
import csv
import io
import json
import os
import shutil
import subprocess
import sys
import time

import numpy as np
import onnxruntime as ort
import requests
import soundfile as sf
import torch
import torch.nn as nn
import torch.nn.functional as F
import torchaudio.functional as AF

EAT_REPO = "https://github.com/fschmid56/EfficientAT"
EAT_COMMIT = "a425fdce92572e602a1d5634799bd9f1f2efa806"
NAME = "mn10_as"
RATE = 32000
PADDED = RATE * 10
DOG_WAV = "https://raw.githubusercontent.com/karolpiczak/ESC-50/master/audio/1-100032-A-0.wav"
DOG = {"Dog", "Bark", "Bow-wow", "Yip", "Growling", "Howl", "Whimper (dog)", "Animal", "Domestic animals, pets"}
CLAP_REPO = "laion/clap-htsat-unfused"
GLASS_WAV = "https://raw.githubusercontent.com/karolpiczak/ESC-50/master/audio/1-20133-A-39.wav"

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


def padded(second):
    """One second, then silence to 10 s, as the app feeds the model."""
    return np.concatenate([second, np.zeros(PADDED - len(second), np.float32)])


def dog_second(url=DOG_WAV):
    """The loudest second of an ESC-50 sound (K. Piczak, CC BY-NC 3.0; downloaded, never stored; a dog barking by
    default), at 32 kHz, ending 60 ms after its loudest 10 ms, as the app cuts."""
    x, sr = sf.read(io.BytesIO(requests.get(url, timeout=60).content), dtype="float32", always_2d=True)
    w = AF.resample(torch.from_numpy(x.mean(1)), sr, RATE).numpy().astype(np.float32)
    n = len(w) // 320
    loud = int(np.argmax(np.sqrt((w[:n * 320].reshape(n, 320) ** 2).mean(1))))
    end = min(len(w), max(RATE, (loud + 6) * 320))
    return w[end - RATE:end]


def as_app_clap(second32):
    """What the app gives CLAP: its 32 kHz second made 48 kHz (linear) and repeated to 10 s, as CLAP's own processor
    repeats a short sound."""
    n = len(second32) * 3 // 2
    w48 = np.interp(np.arange(n) * (2 / 3), np.arange(len(second32)), second32).astype(np.float32)
    return np.tile(w48, 480000 // n + 1)[:480000]


def export_clap(out, report):
    from onnxruntime.quantization import QuantType, quantize_dynamic
    from transformers import ClapModel, ClapProcessor

    clap = ClapModel.from_pretrained(CLAP_REPO).eval()
    proc = ClapProcessor.from_pretrained(CLAP_REPO)
    fe = proc.feature_extractor
    tok = proc.tokenizer([p for _, p in PROMPTS], padding=True, return_tensors="pt")
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
            mel = torch.matmul(self.melT, spec.pow(2).sum(-1)).clamp(min=1e-10)
            feats = (10.0 * torch.log10(mel)).transpose(1, 2).unsqueeze(1)
            emb = F.normalize(self.clap.get_audio_features(input_features=feats), dim=-1)
            return (scale * emb @ self.text.T).softmax(-1), emb

    model = Wave().eval()
    fpath, path = os.path.join(out, "clap-float.onnx"), os.path.join(out, "clap.onnx")
    x0 = torch.randn(1, 480000) * 0.1
    with torch.no_grad():
        torch.onnx.export(model, (x0,), fpath, input_names=["wave"], output_names=["probs", "embedding"],
                          opset_version=17, do_constant_folding=True)
    # matrix products only: ONNX Runtime has no int8 convolution on the CPU (ConvInteger)
    quantize_dynamic(fpath, path, weight_type=QuantType.QInt8, op_types_to_quantize=["MatMul", "Gemm"])
    fsess = ort.InferenceSession(fpath, providers=["CPUExecutionProvider"])
    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])
    glass = as_app_clap(dog_second(GLASS_WAV))
    tests = [x0.numpy()[0], (np.random.default_rng(2).standard_normal(480000) * 0.02).astype(np.float32), glass]
    worst_cos, worst_q = 1.0, 1.0
    for x in tests:
        # the reference: the processor and the model exactly as Hugging Face runs them
        feats = fe(x, sampling_rate=48000, return_tensors="pt")
        with torch.no_grad():
            ref = F.normalize(clap.get_audio_features(input_features=feats["input_features"]), dim=-1).numpy()[0]
        got = fsess.run(None, {"wave": x[None]})[1][0]
        q = sess.run(None, {"wave": x[None]})[1][0]
        worst_cos = min(worst_cos, float(ref @ got))
        worst_q = min(worst_q, float(got @ q))
    p = sess.run(["probs"], {"wave": glass[None]})[0][0]
    top = ", ".join(f"{PROMPTS[i][1]} {p[i]:.2f}" for i in np.argsort(-p)[:3])
    crash = float(sum(p[i] for i, (k, _) in enumerate(PROMPTS) if k == "crash"))
    ok = worst_cos > 0.999 and worst_q > 0.99
    print(f"clap: onnx vs hugging face, worst cosine {worst_cos:.5f}; int8 vs float {worst_q:.4f}; "
          f"a second of breaking glass, as the app feeds it: {top} (crash {crash:.2f}); {'OK' if ok else 'FAIL'}")
    os.remove(fpath)
    report["clap"] = {"repo": CLAP_REPO, "mb": round(os.path.getsize(path) / 2**20, 1), "worst_cos": worst_cos,
                      "int8_vs_float": worst_q, "glass": top, "glass_crash": crash, "ok": ok,
                      "ms_1thread": bench(path, {"wave": glass[None]}, 1), "ms_2threads": bench(path, {"wave": glass[None]}, 2)}
    with open(os.path.join(out, "clap_prompts.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["index", "kind", "prompt"])
        for i, (k, pr) in enumerate(PROMPTS):
            w.writerow([i, k, pr])
    return ok


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("--work", default="work/src")
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    src = os.path.abspath(os.path.join(a.work, "EfficientAT"))
    if not os.path.isdir(src):
        subprocess.run(["git", "clone", "-q", EAT_REPO, src], check=True)
        subprocess.run(["git", "-C", src, "checkout", "-q", EAT_COMMIT], check=True)
    shutil.copy(os.path.join(src, "metadata", "class_labels_indices.csv"), os.path.join(a.out, "efficientat_class_map.csv"))
    labels = [r[2] for r in csv.reader(open(os.path.join(src, "metadata", "class_labels_indices.csv")))][1:]

    cwd = os.getcwd()
    sys.path.insert(0, src)
    os.chdir(src)  # helpers.utils reads metadata/ from the working folder, the weights go to resources/
    try:
        from models.mn.model import get_model
        from models.preprocess import AugmentMelSTFT
        from helpers.utils import NAME_TO_WIDTH
        net = get_model(width_mult=NAME_TO_WIDTH(NAME), pretrained_name=NAME)
    finally:
        os.chdir(cwd)
    net.eval()
    mel = AugmentMelSTFT(n_mels=128, sr=RATE, win_length=800, hopsize=320).eval()

    class Wave(nn.Module):
        def __init__(self):
            super().__init__()
            self.mel, self.net = mel, net

        def forward(self, wave):
            logits, _ = self.net(self.mel(wave).unsqueeze(1))
            return torch.sigmoid(logits)

    model = Wave().eval()
    path = os.path.join(a.out, "efficientat.onnx")
    with torch.no_grad():
        torch.onnx.export(model, (torch.from_numpy(padded(np.random.default_rng(0).standard_normal(RATE).astype(np.float32) * 0.1))[None],),
                          path, input_names=["wave"], output_names=["scores"], dynamic_axes={"wave": {1: "samples"}},
                          opset_version=17, do_constant_folding=True)
    sess = ort.InferenceSession(path, providers=["CPUExecutionProvider"])

    rng = np.random.default_rng(1)
    dog = dog_second()
    tests = [padded(dog), padded((rng.standard_normal(RATE) * 0.1).astype(np.float32)), dog,
             (rng.standard_normal(RATE * 3) * 0.1).astype(np.float32)]
    worst = 0.0
    for x in tests:
        with torch.no_grad():
            ref = model(torch.from_numpy(x)[None]).numpy()
        worst = max(worst, float(np.abs(ref - sess.run(None, {"wave": x[None]})[0]).max()))
    p = sess.run(None, {"wave": padded(dog)[None]})[0][0]
    top = ", ".join(f"{labels[i]} {p[i]:.2f}" for i in np.argsort(-p)[:5])
    heard = labels[int(np.argmax(p))] in DOG
    ok = worst < 1e-3 and heard
    print(f"{NAME}: onnx vs pytorch, worst difference {worst:.2e}; a second of a dog barking, padded to 10 s: {top}; "
          f"{'OK' if ok else 'FAIL'}")
    report = {
        "efficientat_commit": EAT_COMMIT, "model": NAME, "torch": torch.__version__, "onnxruntime": ort.__version__,
        "mb": round(os.path.getsize(path) / 2**20, 1), "worst_diff": worst, "dog_padded": top, "ok": ok,
        "ms_10s_1thread": bench(path, {"wave": padded(dog)[None]}, 1),
        "ms_10s_2threads": bench(path, {"wave": padded(dog)[None]}, 2),
    }
    if not ok:
        os.remove(path)
    clap_ok = export_clap(a.out, report)
    if not clap_ok:
        os.remove(os.path.join(a.out, "clap.onnx"))
    with open(os.path.join(a.out, "export.json"), "w") as f:
        json.dump(report, f, indent=1)
    print(json.dumps(report, indent=1))
    if not ok or not clap_ok:
        raise SystemExit("an export does not match its model (or EfficientAT does not hear the dog)")


if __name__ == "__main__":
    main()
