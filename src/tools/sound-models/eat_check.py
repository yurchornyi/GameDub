#!/usr/bin/env python3
"""
EfficientAT mn10_as, run as its own inference.py does: a whole 10 s clip is named right, but any one-second cut
scores every class near 1.0 (16.11.2026, with PyTorch 1.13 as the repository pins and with 2.5 alike). Training
padded short clips with silence to 10 s, so this tries one-second cuts padded with silence to 2..10 s, on the
repository's example and on a few ESC-50 sounds, to find the shortest input the model hears right. Run from the
EfficientAT checkout.
"""
import csv
import io
import sys
import time

import librosa
import numpy as np
import requests
import torch

sys.path.insert(0, ".")
from helpers.utils import labels  # noqa: E402
from models.mn.model import get_model  # noqa: E402
from models.preprocess import AugmentMelSTFT  # noqa: E402

net = get_model(width_mult=1.0, pretrained_name="mn10_as")
net.eval()
mel = AugmentMelSTFT(n_mels=128, sr=32000, win_length=800, hopsize=320)
mel.eval()


def scores(w):
    with torch.no_grad():
        p, _ = net(mel(torch.from_numpy(np.ascontiguousarray(w, np.float32))[None]).unsqueeze(0))
    return torch.sigmoid(p.float()).squeeze().numpy()


def top(p):
    return ", ".join(f"{labels[i]} {p[i]:.2f}" for i in np.argsort(-p)[:3]) + f" (mean {p.mean():.3f})"


def loudest_second(w):
    n = len(w) // 320
    i = int(np.argmax(np.sqrt((w[:n * 320].reshape(n, 320) ** 2).mean(1))))
    end = min(len(w), max(32000, (i + 6) * 320))  # the cut ends 60 ms after the loudest 10 ms, as the app cuts
    return w[end - 32000:end]


sounds = [("metro", librosa.load("resources/metro_station-paris.wav", sr=32000, mono=True)[0])]
base = "https://raw.githubusercontent.com/karolpiczak/ESC-50/master"
meta = list(csv.DictReader(io.StringIO(requests.get(f"{base}/meta/esc50.csv", timeout=60).text)))
for cat in ("dog", "glass_breaking", "door_wood_knock", "fireworks", "siren", "car_horn", "footsteps", "engine"):
    f = next(r["filename"] for r in meta if r["category"] == cat)
    w, _ = librosa.load(io.BytesIO(requests.get(f"{base}/audio/{f}", timeout=60).content), sr=32000, mono=True)
    sounds.append((cat, w))

for name, w in sounds:
    one = loudest_second(w)
    print(f"{name}: whole {len(w) / 32000:.0f} s: {top(scores(w))}")
    print(f"{name}:   1 s alone: {top(scores(one))}")
    for total in (2, 3, 5, 10):
        z = np.zeros(32000 * (total - 1), np.float32)
        print(f"{name}:   1 s then silence to {total} s: {top(scores(np.concatenate([one, z])))}")
        print(f"{name}:   silence then 1 s, {total} s: {top(scores(np.concatenate([z, one])))}")

for total in (1, 2, 3, 5, 10):
    x = np.zeros(32000 * total, np.float32)
    scores(x)
    t = time.perf_counter()
    for _ in range(5):
        scores(x)
    print(f"time, {total} s: {(time.perf_counter() - t) / 5 * 1000:.0f} ms (PyTorch, this runner)")
