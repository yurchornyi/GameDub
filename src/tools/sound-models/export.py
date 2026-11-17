#!/usr/bin/env python3
"""
Exports EfficientAT mn10_as (Schmid et al., JKU Linz, MIT; AudioSet, 527 classes) to ONNX for the stick lighting's
blast check (src/GameDub/Lights/SoundFx.cs), from the waveform in to the scores out: mono 32 kHz waveform [1, n] ->
sigmoid scores [1, 527]. The mel front end is inside, so the app needs no audio code beyond resampling.

The model hears only 10 s clips, as it was trained (short clips padded with silence): one second alone scores every
class near 1.0, with PyTorch 1.13 as the repository pins and with 2.5 alike (16.11.2026). The app pads its one-second
cut with 9 s of silence; the check below makes the export name a padded second of a real dog bark, besides matching
PyTorch (which alone proves nothing: both can be wrong the same way).

Tried and dropped (16-17.11.2026, on 3,200 public sounds): EfficientAT in place of YAMNet (fewer shots and horns
caught, 30 times the time at 10 s), dymn10_as (does not export to ONNX at any length), CLAP for car crashes (no
crash recordings to check it on).

Run by .github/workflows/sound-models.yml on a GitHub-hosted runner. Output: efficientat.onnx,
efficientat_class_map.csv and export.json (the checks and the speed), for the GameDub release that
src/tools/get-models.ps1 downloads.
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
import torchaudio.functional as AF

EAT_REPO = "https://github.com/fschmid56/EfficientAT"
EAT_COMMIT = "a425fdce92572e602a1d5634799bd9f1f2efa806"
NAME = "mn10_as"
RATE = 32000
PADDED = RATE * 10
DOG_WAV = "https://raw.githubusercontent.com/karolpiczak/ESC-50/master/audio/1-100032-A-0.wav"
DOG = {"Dog", "Bark", "Bow-wow", "Yip", "Growling", "Howl", "Whimper (dog)", "Animal", "Domestic animals, pets"}


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


def dog_second():
    """The loudest second of a dog barking (ESC-50, K. Piczak, CC BY-NC 3.0; downloaded, never stored), ending 60 ms
    after its loudest 10 ms, as the app cuts."""
    x, sr = sf.read(io.BytesIO(requests.get(DOG_WAV, timeout=60).content), dtype="float32", always_2d=True)
    w = AF.resample(torch.from_numpy(x.mean(1)), sr, RATE).numpy().astype(np.float32)
    n = len(w) // 320
    loud = int(np.argmax(np.sqrt((w[:n * 320].reshape(n, 320) ** 2).mean(1))))
    end = min(len(w), max(RATE, (loud + 6) * 320))
    return w[end - RATE:end]


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
    with open(os.path.join(a.out, "export.json"), "w") as f:
        json.dump(report, f, indent=1)
    print(json.dumps(report, indent=1))
    if not ok:
        os.remove(path)
        raise SystemExit("the export does not match the model, or does not hear the dog")


if __name__ == "__main__":
    main()
