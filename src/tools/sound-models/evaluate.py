#!/usr/bin/env python3
"""
Compares the sound models on public sounds the way the app uses them (src/GameDub/Lights/SoundFx.cs): the same
resampling, the same onsets, the same cuts, the same rules. YAMNet with today's thresholds is the baseline; EfficientAT
and CLAP are tried over a range of thresholds, so the app can take the ones that catch more and flash less on nothing.

Sounds (downloaded here, never stored in the repository):
- AudioSet evaluation set (Google, CC BY 4.0; via the Hugging Face dataset agkphysics/AudioSet): gunfire, explosions,
  breaking, sirens, horns, car crashes (clips with "Smash, crash" and a vehicle), and what must not flash: doors,
  footsteps, vehicles, speech, music, anything else.
- ESC-50 (K. Piczak, CC BY-NC 3.0): 40 kinds of everyday sounds, 20 clips each.
- SFXMint (CC0, sound effects made for games): vehicles, doors, footsteps, hits and more.

Output: a Markdown report and scores.json.gz (every judged onset and look, for a closer look later).
"""
import argparse
import csv
import gzip
import io
import json
import os
import random
import re
import sys
import time
import zipfile
from concurrent.futures import ProcessPoolExecutor, ThreadPoolExecutor

import numpy as np
import onnxruntime as ort
import requests
import soundfile as sf

# --- the app's name lists (SoundFx.cs) ---
IMPACT = ["Explosion", "Boom", "Smash, crash", "Breaking", "Shatter"]
DOOR = ["Door", "Sliding door", "Slam", "Knock", "Tap", "Thump, thud", "Bang"]
GUN = ["Gunshot, gunfire", "Machine gun", "Fusillade", "Artillery fire", "Cap gun"]
FOOT = ["Walk, footsteps", "Run", "Shuffle"]
SIREN = ["Siren", "Civil defense siren", "Police car (siren)", "Ambulance (siren)", "Fire engine, fire truck (siren)",
         "Alarm", "Car alarm", "Smoke detector, smoke alarm", "Fire alarm"]
HORN = ["Vehicle horn, car horn, honking", "Air horn, truck horn", "Train horn", "Foghorn"]
ENGINE = ["Vehicle", "Motor vehicle (road)", "Car", "Race car, auto racing", "Truck", "Motorcycle", "Engine",
          "Light engine (high frequency)", "Medium engine (mid frequency)", "Heavy engine (low frequency)", "Idling",
          "Accelerating, revving, vroom", "Tire squeal", "Skidding"]
# a sudden sound that may be a hit of some kind: only these go on to CLAP
HINT = ["Smash, crash", "Bang", "Thump, thud", "Slam", "Breaking", "Crushing", "Clang", "Whack, thwack", "Shatter",
        "Glass", "Crumpling, crinkling"]
VEHICLE = set(ENGINE) | {"Bus", "Car passing by", "Traffic noise, roadway noise", "Emergency vehicle", "Police car (siren)"}

UA = {"User-Agent": "sound-model-eval (GitHub Actions)"}
OK_GROUPS = {"shot": ["as: gunfire"], "blast": ["as: explosion", "as: smash, breaking"],
             "siren": ["as: siren, alarm"], "horn": ["as: horn"], "crash": ["as: car crash", "sfx: car collision"]}


def get(url, tries=6, **kw):
    for i in range(tries):
        try:
            r = requests.get(url, headers=UA, timeout=120, **kw)
            if r.status_code == 429 or r.status_code >= 500:
                raise IOError(f"HTTP {r.status_code}")
            r.raise_for_status()
            return r
        except Exception:
            if i == tries - 1:
                raise
            time.sleep(min(60, 2 ** i))


# ---------------------------------------------------------------- data
def as_group(labels):
    L = set(labels)
    if "Smash, crash" in L and L & VEHICLE:
        return "car crash"
    if L & set(GUN):
        return "gunfire"
    if L & {"Explosion", "Boom"}:
        return "explosion"
    if L & {"Smash, crash", "Breaking", "Shatter"}:
        return "smash, breaking"
    if L & set(SIREN):
        return "siren, alarm"
    if L & set(HORN):
        return "horn"
    if L & {"Fireworks", "Firecracker"}:
        return "fireworks"
    if L & {"Door", "Slam", "Knock", "Sliding door", "Tap", "Doorbell", "Cupboard open or close", "Drawer open or close"}:
        return "door, knock"
    if L & set(FOOT):
        return "footsteps"
    if L & {"Skidding", "Tire squeal"}:
        return "skidding"
    if L & VEHICLE:
        return "vehicle"
    if "Music" in L:
        return "music"
    if L & {"Speech", "Male speech, man speaking", "Female speech, woman speaking", "Conversation", "Narration, monologue"}:
        return "speech"
    return "other"


AS_CAP = {"vehicle": 200, "music": 80, "speech": 80, "other": 200, "siren, alarm": 200, "horn": 150, "door, knock": 200,
          "fireworks": 60, "skidding": 120, "footsteps": 150, "gunfire": 250}


def fetch_audioset(root):
    d = os.path.join(root, "audioset")
    os.makedirs(d, exist_ok=True)
    sel_path = os.path.join(d, "selection.json")
    if os.path.exists(sel_path):
        sel = json.load(open(sel_path))
        if all(os.path.exists(os.path.join(d, s["file"])) for s in sel):
            return [(os.path.join(d, s["file"]), "as: " + s["group"]) for s in sel]
    rows, off, total = [], 0, None
    while total is None or off < total:
        j = get("https://datasets-server.huggingface.co/rows?dataset=agkphysics/AudioSet&config=balanced"
                f"&split=test&offset={off}&length=100").json()
        total = j["num_rows_total"]
        for r in j["rows"]:
            x = r["row"]
            rows.append({"id": x["video_id"], "labels": x["human_labels"], "url": x["audio"][0]["src"]})
        off += 100
    print(f"audioset: {len(rows)} clips listed")
    random.Random(7).shuffle(rows)
    taken, sel = {}, []
    for r in rows:
        g = as_group(r["labels"])
        if taken.get(g, 0) >= AS_CAP.get(g, 10**9):
            continue
        taken[g] = taken.get(g, 0) + 1
        sel.append({"group": g, "labels": r["labels"], "file": r["id"] + ".flac", "url": r["url"]})
    print("audioset groups:", taken)

    def dl(s):
        p = os.path.join(d, s["file"])
        if not os.path.exists(p):
            try:
                open(p, "wb").write(get(s["url"]).content)
            except Exception as e:
                print("audioset: skip", s["file"], e)
                return None
        return s

    with ThreadPoolExecutor(8) as ex:
        sel = [s for s in ex.map(dl, sel) if s]
    json.dump([{k: v for k, v in s.items() if k != "url"} for s in sel], open(sel_path, "w"))
    return [(os.path.join(d, s["file"]), "as: " + s["group"]) for s in sel]


def fetch_esc50(root):
    d = os.path.join(root, "esc50")
    if not os.path.exists(os.path.join(d, "meta.csv")):
        os.makedirs(d, exist_ok=True)
        z = zipfile.ZipFile(io.BytesIO(get("https://codeload.github.com/karolpiczak/ESC-50/zip/refs/heads/master").content))
        rows = list(csv.DictReader(io.TextIOWrapper(z.open("ESC-50-master/meta/esc50.csv"), "utf-8")))
        per, keep = {}, []
        for r in rows:
            if per.get(r["category"], 0) < 20:
                per[r["category"]] = per.get(r["category"], 0) + 1
                open(os.path.join(d, r["filename"]), "wb").write(z.read("ESC-50-master/audio/" + r["filename"]))
                keep.append(r)
        with open(os.path.join(d, "meta.csv"), "w", newline="") as f:
            w = csv.DictWriter(f, fieldnames=["filename", "category"])
            w.writeheader()
            for r in keep:
                w.writerow({"filename": r["filename"], "category": r["category"]})
    return [(os.path.join(d, r["filename"]), "esc: " + r["category"]) for r in csv.DictReader(open(os.path.join(d, "meta.csv")))]


SFX_CAP = {"Vehicle Sounds": 1000, "Door Sounds": 50, "Footsteps": 50, "Impacts & Hits": 80, "Glass & Ceramic": 30,
           "Mechanical Sounds": 30, "Human Sounds": 30, "UI & Notification Sounds": 30, "Fire & Electricity": 20,
           "Household & Kitchen": 20, "Crowd Sounds": 20}


def sfx_group(r):
    if r["category"] == "Vehicle Sounds" and re.search(r"collision|crash", r["title"], re.I):
        return "car collision"
    return r["category"]


def fetch_sfx(root):
    d = os.path.join(root, "sfx")
    os.makedirs(d, exist_ok=True)
    sel_path = os.path.join(d, "selection.json")
    if os.path.exists(sel_path):
        sel = json.load(open(sel_path))
    else:
        rows = list(csv.DictReader(io.StringIO(get(
            "https://huggingface.co/datasets/flreey/sfxmint-cc0-sound-effects/resolve/main/catalog.csv").text)))
        random.Random(7).shuffle(rows)
        per, sel = {}, []
        for r in rows:
            c = r["category"]
            if per.get(c, 0) < SFX_CAP.get(c, 0):
                per[c] = per.get(c, 0) + 1
                sel.append({"file": r["id"] + ".mp3", "url": r["mp3_url"], "group": sfx_group(r)})

        def dl(s):
            p = os.path.join(d, s["file"])
            if not os.path.exists(p):
                try:
                    open(p, "wb").write(get(s["url"]).content)
                except Exception as e:
                    print("sfx: skip", s["file"], e)
                    return None
            return s

        with ThreadPoolExecutor(4) as ex:
            sel = [s for s in ex.map(dl, sel) if s]
        json.dump(sel, open(sel_path, "w"))
    return [(os.path.join(d, s["file"]), "sfx: " + s["group"]) for s in sel if os.path.exists(os.path.join(d, s["file"]))]


# ---------------------------------------------------------------- the app's audio path
def box(x, sr, rate):
    """SoundFx.Feed: a box filter over one output period, positions stepping by sr/rate."""
    step = sr / rate
    avg = max(1, int(round(step)))
    pos = np.floor(np.arange(0, len(x), step)).astype(np.int64)
    cs = np.concatenate([[0.0], np.cumsum(x, dtype=np.float64)])
    end = np.minimum(pos + avg, len(x))
    return ((cs[end] - cs[pos]) / (end - pos)).astype(np.float32)


def blocks(x, rate):
    """SoundFx.Gate: 10 ms loudness, the decaying peak, and onsets (a jump to 1.6 times the last 30 ms)."""
    B = rate // 100
    gap = rate * 60 // 1000
    n = len(x) // B
    rms = np.sqrt((x[:n * B].astype(np.float64).reshape(n, B) ** 2).mean(1)) if n else np.zeros(0)
    peak = np.zeros(n)
    p, recent, k, last, ons = 0.0, [0.0, 0.0, 0.0], 0, -10**9, []
    for i, r in enumerate(rms):
        p = max(p * 0.95, r)
        peak[i] = p
        before = sum(recent) / 3
        recent[k % 3] = r
        k += 1
        s = (i + 1) * B
        if r < 0.01 or r < 1.6 * before or s - last < gap:
            continue
        last = s
        ons.append((s - B, float(r)))
    return ons, peak


def cut(x, end, n):
    if end <= len(x):
        a = x[max(0, end - n):end]
    else:
        return None
    return np.concatenate([np.zeros(n - len(a), np.float32), a]) if len(a) < n else a


def to48(c32):
    """1 s at 32 kHz to 48 kHz, linear, as the app does before CLAP."""
    n = len(c32) * 3 // 2
    return np.interp(np.arange(n) * (2 / 3), np.arange(len(c32)), c32).astype(np.float32)


def tile10(w48):
    """CLAP's repeat-pad: the cut repeated to fill 10 s."""
    reps = 480000 // len(w48)
    return np.concatenate([np.tile(w48, reps), np.zeros(480000 - reps * len(w48), np.float32)])


# ---------------------------------------------------------------- models (one set per worker process)
M = {}


def init_worker(models_dir, yam_dir, clap_keys):
    so = ort.SessionOptions()
    so.intra_op_num_threads = 1
    so.inter_op_num_threads = 1

    def sess(p):
        return ort.InferenceSession(p, so, providers=["CPUExecutionProvider"])

    def names(p):
        rows = list(csv.reader(open(p)))[1:]
        return [r[2].strip().strip('"') for r in rows]

    def idx(ns, wanted):
        return [i for i, n in enumerate(ns) if n in wanted]

    yn = names(os.path.join(yam_dir, "yamnet_class_map.csv"))
    en = names(os.path.join(models_dir, "efficientat_class_map.csv"))
    M["names"] = {"yamnet": yn, "eat": en}
    M["idx"] = {k: {g: idx(ns, w) for g, w in [("gun", GUN), ("impact", IMPACT), ("foot", FOOT), ("door", DOOR),
                                                ("siren", SIREN), ("horn", HORN), ("engine", ENGINE), ("hint", HINT)]}
                for k, ns in [("yamnet", yn), ("eat", en)]}
    y = sess(os.path.join(yam_dir, "yamnet.onnx"))
    M["yamnet"] = (y, y.get_inputs()[0].name, [o.name for o in y.get_outputs() if o.shape and o.shape[-1] == 521][0])
    M["eat_models"] = []
    for m in ("mn10", "dymn10"):
        p = os.path.join(models_dir, f"efficientat-{m}_as.onnx")
        if os.path.exists(p):
            M[m] = sess(p)
            M["eat_models"].append(m)
    M["clap"] = {k: sess(os.path.join(models_dir, f"{k}.onnx")) for k in clap_keys}
    prompts = list(csv.DictReader(open(os.path.join(models_dir, "clap_prompts.csv"))))
    M["crash_idx"] = [int(p["index"]) for p in prompts if p["kind"] == "crash"]
    M["ms"] = {}


def timed(key, f):
    t = time.perf_counter()
    r = f()
    a = M["ms"].setdefault(key, [0, 0.0])
    a[0] += 1
    a[1] += (time.perf_counter() - t) * 1000
    return r


def yamnet(c16):
    s, i, o = M["yamnet"]
    v = timed("yamnet", lambda: s.run([o], {i: c16})[0])
    return v.max(0) if v.ndim == 2 else v


def eat(key, c32):
    return timed(key, lambda: M[key].run(None, {"wave": c32[None]})[0][0])


def summary(model, v):
    ix = M["idx"]["yamnet" if model == "yamnet" else "eat"]
    names = M["names"]["yamnet" if model == "yamnet" else "eat"]
    d = {g: float(v[i].max()) if i else 0.0 for g, i in ix.items()}
    d["top"] = names[int(np.argmax(v))]
    return d


def clap(key, w48):
    p = timed(key, lambda: M["clap"][key].run(["probs"], {"wave": tile10(w48)[None]})[0][0])
    return float(p[M["crash_idx"]].sum()), int(np.argmax(p))


CLAP_GROUPS = re.compile(r"^(as: (car crash|smash, breaking|explosion|door, knock|footsteps|skidding|vehicle|gunfire|other|"
                         r"music|speech)|esc: (door_wood_knock|footsteps|glass_breaking|can_opening|engine|car_horn|train|"
                         r"clapping|mouse_click|keyboard_typing|fireworks|chainsaw|hand_saw|crackling_fire|clock_tick|"
                         r"door_wood_creaks)|sfx: .*)$")
CLAP_CAP = {"as: other": 80, "as: music": 40, "as: speech": 40, "as: vehicle": 120, "as: gunfire": 60}


def run_clip(job):
    path, group, do_clap = job
    try:
        x, sr = sf.read(path, dtype="float32", always_2d=True)
    except Exception as e:
        return {"path": path, "group": group, "error": str(e)}
    x = x.mean(1)
    x16, x32 = box(x, sr, 16000), box(x, sr, 32000)
    rec = {"path": os.path.basename(path), "group": group, "seconds": round(len(x) / sr, 2), "pipes": {}}
    # YAMNet: 16 kHz, a 15600-sample window; EfficientAT: 32 kHz, 1 s (and 0.5 s for the cut, tried too)
    for pipe, xs, rate, win in [("yamnet", x16, 16000, 15600), ("eat", x32, 32000, 32000)]:
        ons, peak = blocks(xs, rate)
        post = rate * 60 // 1000
        B = rate // 100
        looks = []
        hop = rate * 480 // 1000
        last = -10**9
        for s in range(hop, len(xs) + 1, hop):
            bi = s // B - 1
            if s < win or bi < 0 or peak[min(bi, len(peak) - 1)] < 0.01 or s - last < rate // 4:
                continue
            last = s
            w = xs[s - win:s]
            t = s * 1000 / rate
            if pipe == "yamnet":
                looks.append((t, {"yamnet": summary("yamnet", yamnet(w))}))
            else:
                looks.append((t, {m: summary(m, eat(m, w)) for m in M["eat_models"]}))
        judged = []
        for at, r in ons:
            e = at + post
            c = cut(xs, e, win)
            if c is None:
                continue
            t = e * 1000 / rate
            if pipe == "yamnet":
                judged.append({"t": t, "at": at, "rms": r, "yamnet": summary("yamnet", yamnet(c))})
            else:
                j = {"t": t, "at": at, "rms": r, "mn10_w500": summary("mn10", eat("mn10", c[-16000:]))}
                for m in M["eat_models"]:
                    j[m] = summary(m, eat(m, c))
                judged.append(j)
        if pipe == "eat" and do_clap and M["clap"]:
            # CLAP for the three most hit-like onsets: cut 1 s ending 60 ms or 250 ms after the onset, or 0.5 s
            for j in sorted(judged, key=lambda j: -j["mn10"]["hint"])[:3]:
                cl = {}
                for post_ms, secs in [(60, 1.0), (250, 1.0), (250, 0.5)]:
                    c = cut(xs, j["at"] + rate * post_ms // 1000, int(rate * secs))
                    if c is None:
                        continue
                    for k in M["clap"]:
                        if k != "clap-tiny" and (post_ms, secs) != (250, 1.0):
                            continue
                        cl[f"{k}@{post_ms}/{secs}"] = clap(k, to48(c))
                j["clap"] = cl
        rec["pipes"][pipe] = {"looks": looks, "onsets": judged}
    rec["ms"] = {k: list(v) for k, v in M["ms"].items()}
    rec["pid"] = os.getpid()
    return rec


# ---------------------------------------------------------------- the app's rules over the scores
BASE = {"gun": 0.2, "loud": 0.15, "impact": 0.35, "siren": 0.3, "siren_keep": 0.25, "horn": 0.4, "engine": 0.3,
        "crash": None, "hint": 0.0, "clap": None, "gate_engine": True}


def simulate(rec, model, c, judge=None):
    pipe = rec["pipes"]["yamnet" if model == "yamnet" else "eat"]
    judge = judge or model
    ev = {"shot": 0, "blast": 0, "siren": False, "horn": False, "crash": 0, "clap_runs": 0}
    loudest, gun_t, gun_rms, last_imp, siren_until, prev, engine_t = 0.05, -1e9, 0.0, -1e9, -1e9, False, -1e9
    items = [(t, 0, d) for t, d in pipe["looks"]] + [(j["t"], 1, j) for j in pipe["onsets"]]
    items.sort(key=lambda z: (z[0], z[1]))
    for t, kind, d in items:
        if kind == 0:
            s = d[model]
            loudest = max(0.05, loudest * 0.9)
            if s["engine"] > c["engine"]:
                engine_t = t
            if s["siren"] >= c["siren"] and prev:
                siren_until = t + 2500
            prev = s["siren"] >= c["siren"]
            if s["siren"] >= c["siren_keep"] and t < siren_until:
                siren_until = t + 2500
            if t < siren_until:
                ev["siren"] = True
            if s["horn"] > c["horn"]:
                ev["horn"] = True
            continue
        rms, s = d["rms"], d[judge]
        if t - gun_t < 300 and rms >= 0.4 * gun_rms:
            loudest, gun_t, gun_rms = max(rms, loudest), t, rms
            ev["shot"] += 1
            continue
        shot = s["gun"] >= c["gun"] and s["gun"] >= s["impact"] and s["gun"] > s["foot"] and rms >= c["loud"] * loudest
        blast = (not shot and s["impact"] >= c["impact"] and s["impact"] > s["gun"] and s["impact"] > s["foot"]
                 and s["impact"] > s["door"])
        if shot:
            loudest, gun_t, gun_rms = max(rms, loudest), t, rms
            ev["shot"] += 1
        elif blast:
            if t - last_imp >= 700:
                last_imp = t
                ev["blast"] += 1
        elif c["crash"] is not None and (not c["gate_engine"] or t - engine_t < 8000) and s["hint"] >= c["hint"]:
            p = d.get("clap", {}).get(c["clap"])
            if p is None:
                continue
            ev["clap_runs"] += 1
            if p[0] >= c["crash"] and t - last_imp >= 700:
                last_imp = t
                ev["crash"] += 1
    return ev


def rate(recs, model, c, what, judge=None):
    """Share of clips with at least one such flash, by group."""
    out = {}
    for r in recs:
        e = simulate(r, model, c, judge)
        hit = e[what] > 0 if isinstance(e[what], int) and not isinstance(e[what], bool) else bool(e[what])
        g = out.setdefault(r["group"], [0, 0])
        g[0] += hit
        g[1] += 1
    return out


def pct(a):
    return f"{100 * a[0] / a[1]:.0f}%" if a and a[1] else "-"


def neg_groups(groups, what):
    return [g for g in groups if g not in OK_GROUPS[what] and not (what == "blast" and g == "as: car crash")
            and not (what in ("shot", "blast") and g in ("as: fireworks", "esc: fireworks"))
            and not (what == "crash" and g in ("as: smash, breaking", "as: explosion"))]


def false_share(rates, what):
    ng = [g for g in neg_groups(rates, what)]
    hits = sum(rates[g][0] for g in ng)
    n = sum(rates[g][1] for g in ng)
    worst = sorted(ng, key=lambda g: -rates[g][0] / max(1, rates[g][1]))[:3]
    return f"{100 * hits / max(1, n):.1f}%", ", ".join(f"{g} {pct(rates[g])}" for g in worst)


def hit_share(rates, what):
    gs = [g for g in OK_GROUPS[what] if g in rates]
    hits = sum(rates[g][0] for g in gs)
    n = sum(rates[g][1] for g in gs)
    return f"{100 * hits / max(1, n):.0f}% of {n}"


def report(recs, export, out):
    L = ["# Sound models: YAMNet vs EfficientAT and CLAP", ""]
    L += ["Share of clips with at least one flash. Positive groups should flash; every other group is a false flash.", ""]
    if export:
        L += ["## Models", "", "| model | MB | check | ms (1 thread) | ms (2 threads) | int8 MB | int8 ms | int8 vs float |",
              "|---|---|---|---|---|---|---|---|"]
        for k, v in export.items():
            if not isinstance(v, dict):
                continue
            if "error" in v:
                L.append(f"| {k} | export failed: {v['error'][:150]} |")
                continue
            chk = v.get("worst_diff", v.get("worst_cos"))
            L.append(f"| {k} | {v.get('mb')} | {chk:.4g} | {v.get('ms_1s_1thread', v.get('ms_1thread'))} | "
                     f"{v.get('ms_1s_2threads', v.get('ms_2threads'))} | {v.get('int8_mb', '')} | "
                     f"{v.get('int8_ms_1thread', '')} | {v.get('int8_worst_cos', '')} |")
        L.append("")
    last = {}
    for r in recs:  # each worker's counts only grow: its last record has them all
        last[r.get("pid")] = r.get("ms", {})
    ms = {}
    for snap in last.values():
        for k, (n, t) in snap.items():
            a = ms.setdefault(k, [0, 0.0])
            a[0] += n
            a[1] += t
    groups = sorted({r["group"] for r in recs})
    sizes = {g: sum(1 for r in recs if r["group"] == g) for g in groups}
    L += ["## Clips", "", ", ".join(f"{g} {sizes[g]}" for g in groups), ""]

    # 1. today's YAMNet against EfficientAT at the same thresholds
    have = {k for r in recs[:50] for j in r["pipes"]["eat"]["onsets"] for k in j}
    pipes = [p for p in [("yamnet", "yamnet", None), ("mn10", "mn10", None), ("mn10, 0.5 s cut", "mn10", "mn10_w500"),
                         ("dymn10", "dymn10", None)] if p[1] == "yamnet" or p[1] in have]
    for what in ("shot", "blast", "siren", "horn"):
        L += [f"## {what}: same thresholds as today", "", "| group | " + " | ".join(p[0] for p in pipes) + " |",
              "|---|" + "---|" * len(pipes)]
        rs = [rate(recs, m, BASE, what, j) for _, m, j in pipes]
        for g in groups:
            L.append(f"| {g} | " + " | ".join(pct(r.get(g)) for r in rs) + " |")
        L.append("")

    # 2. threshold sweeps
    sweeps = {"shot": ("gun", [0.1, 0.15, 0.2, 0.3, 0.4, 0.5, 0.6]), "blast": ("impact", [0.2, 0.3, 0.35, 0.4, 0.5, 0.6]),
              "siren": ("siren", [0.2, 0.3, 0.4, 0.5, 0.6]), "horn": ("horn", [0.2, 0.3, 0.4, 0.5, 0.6])}
    for what, (key, vals) in sweeps.items():
        L += [f"## {what}: thresholds", "", "| model | threshold | caught | false | worst false groups |", "|---|---|---|---|---|"]
        for name, m, j in pipes:
            for v in vals:
                c = dict(BASE, **{key: v})
                if key == "siren":
                    c["siren_keep"] = max(0.05, v - 0.05)
                rr = rate(recs, m, c, what, j)
                f, worst = false_share(rr, what)
                L.append(f"| {name} | {v} | {hit_share(rr, what)} | {f} | {worst} |")
        L.append("")
    # engine: heard in vehicles, not elsewhere
    L += ["## engine heard (a look over the threshold)", "", "| model | threshold | vehicle groups | other groups |", "|---|---|---|---|"]
    veh = {"as: vehicle", "as: skidding", "as: car crash", "esc: engine", "sfx: Vehicle Sounds", "sfx: car collision"}
    for m in [p[1] for p in pipes if p[2] is None]:
        for v in (0.2, 0.3, 0.4, 0.5):
            hv = nv = ho = no = 0
            for r in recs:
                p = r["pipes"]["yamnet" if m == "yamnet" else "eat"]
                heard = any(d[m]["engine"] > v for _, d in p["looks"])
                if r["group"] in veh:
                    hv += heard
                    nv += 1
                elif not r["group"].startswith("as: siren"):
                    ho += heard
                    no += 1
            L.append(f"| {m} | {v} | {100 * hv / max(1, nv):.0f}% | {100 * ho / max(1, no):.1f}% |")
    L.append("")

    # 3. crashes with CLAP
    keys = sorted({k for r in recs for j in r["pipes"]["eat"]["onsets"] for k in j.get("clap", {})})
    crecs = [r for r in recs if any("clap" in j for j in r["pipes"]["eat"]["onsets"])]
    L += ["## crash (CLAP), after EfficientAT mn10 found no shot and no blast", "",
          "Clips scored by CLAP: " + str(len(crecs)) + ". Gate: an engine heard within 8 s (as in the app) or not.", "",
          "| CLAP | engine gate | hint | crash | caught | false | worst false groups | CLAP runs |", "|---|---|---|---|---|---|---|---|"]
    for k in keys:
        for gate in (True, False):
            for hint in (0.0, 0.05, 0.1):
                for cth in (0.3, 0.4, 0.5, 0.6, 0.7, 0.8):
                    c = dict(BASE, crash=cth, hint=hint, clap=k, gate_engine=gate)
                    rr = rate(crecs, "mn10", c, "crash")
                    runs = sum(simulate(r, "mn10", c)["clap_runs"] for r in crecs)
                    f, worst = false_share(rr, "crash")
                    L.append(f"| {k} | {'yes' if gate else 'no'} | {hint} | {cth} | {hit_share(rr, 'crash')} | {f} | {worst} | {runs} |")
    L.append("")
    L += ["## Speed on this runner (ms per run, one thread)", ""]
    for k, (n, t) in sorted(ms.items()):
        L.append(f"- {k}: {t / max(1, n):.1f} ms")
    open(out, "w").write("\n".join(L) + "\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", required=True)
    ap.add_argument("--yamnet", required=True)
    ap.add_argument("--data", required=True)
    ap.add_argument("--report", required=True)
    ap.add_argument("--scores", required=True)
    ap.add_argument("--clips", default="work/test-clips")
    ap.add_argument("--workers", type=int, default=os.cpu_count() or 2)
    a = ap.parse_args()
    clips = fetch_audioset(a.data) + fetch_esc50(a.data) + fetch_sfx(a.data)
    print(f"{len(clips)} clips")
    per, jobs = {}, []
    for p, g in clips:
        do = bool(CLAP_GROUPS.match(g)) and per.get(g, 0) < CLAP_CAP.get(g, 10**9)
        if do:
            per[g] = per.get(g, 0) + 1
        jobs.append((p, g, do))
    export = json.load(open(os.path.join(a.models, "export.json"))) if os.path.exists(os.path.join(a.models, "export.json")) else {}
    clap_keys = [k for k in ("clap-tiny", "clap-large") if os.path.exists(os.path.join(a.models, f"{k}.onnx"))]
    recs = []
    t0 = time.time()
    with ProcessPoolExecutor(a.workers, initializer=init_worker, initargs=(a.models, a.yamnet, clap_keys)) as ex:
        for i, r in enumerate(ex.map(run_clip, jobs, chunksize=4)):
            if "error" in r:
                print("skip", r["path"], r["error"])
                continue
            recs.append(r)
            if i % 200 == 0:
                print(f"{i}/{len(jobs)} clips, {time.time() - t0:.0f} s", flush=True)
    with gzip.open(a.scores, "wt") as f:
        json.dump(recs, f)
    # a few clips as WAV, for the app's own test on the console (--test soundfx-files)
    os.makedirs(a.clips, exist_ok=True)
    want = {"as: gunfire": 3, "as: explosion": 2, "as: car crash": 3, "sfx: car collision": 1, "as: siren, alarm": 2,
            "as: horn": 1, "as: door, knock": 2, "as: footsteps": 2, "as: vehicle": 2, "sfx: Door Sounds": 1, "sfx: Footsteps": 1}
    paths = {os.path.basename(p): p for p, _ in clips}
    for r in recs:
        if want.get(r["group"], 0) > 0 and r["path"] in paths:
            want[r["group"]] -= 1
            x, sr = sf.read(paths[r["path"]], dtype="float32", always_2d=True)
            sf.write(os.path.join(a.clips, os.path.splitext(r["path"])[0] + ".wav"), x, sr, subtype="PCM_16")
    report(recs, export, a.report)
    print(open(a.report).read())


if __name__ == "__main__":
    sys.exit(main())
