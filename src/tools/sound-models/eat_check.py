#!/usr/bin/env python3
"""
Runs EfficientAT mn10_as exactly as its own inference.py does, on the repository's example (a Paris metro station,
for which the README lists trains and the subway on top), a one-second cut of it, and noise. Run from the EfficientAT
checkout, once with the PyTorch the repository asks for and once with ours: if only ours gives nonsense, the
difference is in the libraries, and the printed mel statistics say whether it is before the network or in it.
"""
import sys

import librosa
import numpy as np
import torch
import torchaudio
import torchvision

sys.path.insert(0, ".")
from helpers.utils import labels  # noqa: E402
from models.mn.model import get_model  # noqa: E402
from models.preprocess import AugmentMelSTFT  # noqa: E402

print(f"torch {torch.__version__}, torchaudio {torchaudio.__version__}, torchvision {torchvision.__version__}")
net = get_model(width_mult=1.0, pretrained_name="mn10_as")
net.eval()
mel = AugmentMelSTFT(n_mels=128, sr=32000, win_length=800, hopsize=320)
mel.eval()
first = {}


def hook(name):
    def f(_, __, out):
        first.setdefault(name, out)
    return f


if hasattr(net, "features"):
    net.features[0].register_forward_hook(hook("first conv"))
    net.features[-1].register_forward_hook(hook("last conv"))


def run(what, w):
    first.clear()
    with torch.no_grad():
        spec = mel(torch.from_numpy(np.ascontiguousarray(w))[None])
        p, _ = net(spec.unsqueeze(0))
    p = torch.sigmoid(p.float()).squeeze().numpy()
    top = ", ".join(f"{labels[i]} {p[i]:.2f}" for i in np.argsort(-p)[:5])
    acts = "; ".join(f"{k} {float(v.mean()):.3f}/{float(v.std()):.3f}" for k, v in first.items())
    print(f"{what}: mel {float(spec.min()):.2f}..{float(spec.max()):.2f} mean {float(spec.mean()):.3f} "
          f"std {float(spec.std()):.3f}; {acts}; {top}; mean {float(p.mean()):.3f}")


metro, _ = librosa.load("resources/metro_station-paris.wav", sr=32000, mono=True)
run("metro, whole", metro)
run("metro, 1 s", metro[32000:64000])
run("noise, 1 s", (np.random.default_rng(0).standard_normal(32000) * 0.1).astype(np.float32))
