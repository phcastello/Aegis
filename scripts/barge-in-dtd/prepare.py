#!/usr/bin/env python3
"""Prepare source-disjoint public AEC training tensors in an explicit scratch directory."""

from pathlib import Path
import atexit
from concurrent.futures import ThreadPoolExecutor, ProcessPoolExecutor
import hashlib
import io
import json
import math
import os
import struct
import subprocess
import urllib.request
import wave

import numpy as np
from scipy.signal import resample_poly

ROOT = Path(os.environ["AEGIS_DTD_WORKDIR"]).resolve()
HERE = Path(__file__).resolve().parent
MANIFEST = json.loads((HERE / "sources.json").read_text())
ROOT.mkdir(parents=True, exist_ok=True)
(ROOT / "split.json").write_text(json.dumps({
    "train": MANIFEST["train"], "validation": MANIFEST["validation"]
}, indent=2))
for name in ("sources", "tensors", "masks"):
    (ROOT / name).mkdir(exist_ok=True)

import case_data
import physical_data
FRAME = 320
TAPER = .5 - .5 * np.cos(2 * np.pi * np.arange(FRAME) / (FRAME - 1))
DATASET = "datasets/test_set_icassp2022/farend-singletalk"
PROC = None


def fetch_source(stem):
    target = ROOT / "sources" / (stem + ".npz")
    expected = MANIFEST["waveSha256"][stem]
    if target.exists():
        with np.load(target) as data:
            assert data["hashes"].tolist() == expected
        return stem
    pcm = []
    for role, digest in zip(("mic", "lpb"), expected):
        url = (
            "https://media.githubusercontent.com/media/microsoft/AEC-Challenge/"
            + MANIFEST["sourceCommit"] + "/" + DATASET + "/" + stem + "_" + role + ".wav"
        )
        request = urllib.request.Request(url, headers={"User-Agent": "Aegis-offline-acoustic-training"})
        with urllib.request.urlopen(request, timeout=60) as response:
            data = response.read()
        assert hashlib.sha256(data).hexdigest() == digest, (stem, role)
        with wave.open(io.BytesIO(data)) as audio:
            assert audio.getnchannels() == 1 and audio.getsampwidth() == 2
            samples = np.frombuffer(audio.readframes(audio.getnframes()), dtype="<i2")
            source_rate = audio.getframerate()
        common = math.gcd(source_rate, 16000)
        down = resample_poly(samples.astype(np.float32), 16000 // common, source_rate // common)
        pcm.append(np.rint(down).clip(-32768, 32767).astype("<i2"))
    n = min(map(len, pcm))
    np.savez_compressed(target, mic=pcm[0][:n], render=pcm[1][:n], hashes=np.array(expected))
    return stem


def close_worker():
    if PROC is not None and PROC.poll() is None:
        try:
            PROC.stdin.write(struct.pack(">i", 0))
            PROC.stdin.flush()
            PROC.wait(timeout=5)
        except Exception:
            PROC.terminate()


def init_worker():
    global PROC
    case_data.init()
    command = json.loads(Path(os.environ["AEGIS_DTD_AEC_BATCH_ARGV"]).read_text())
    PROC = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    atexit.register(close_worker)


def read_exact(stream, count):
    output = bytearray()
    while len(output) < count:
        more = stream.read(count - len(output))
        if not more:
            raise EOFError("AEC host runner returned a truncated response")
        output.extend(more)
    return output


def cancel(mic, render):
    n = len(mic)
    assert n == len(render) and n % FRAME == 0
    PROC.stdin.write(struct.pack(">i", n))
    PROC.stdin.write(np.asarray(render, dtype="<i2").tobytes())
    PROC.stdin.write(np.asarray(mic, dtype="<i2").tobytes())
    PROC.stdin.flush()
    assert struct.unpack(">i", read_exact(PROC.stdout, 4))[0] == n
    clean = np.frombuffer(read_exact(PROC.stdout, 2 * n), dtype="<i2").copy()
    linear = np.frombuffer(read_exact(PROC.stdout, 2 * n), dtype="<i2").copy()
    # The exact production AEC3 linear and nonlinear timelines differ by 4 ms.
    linear = np.r_[np.zeros(64, dtype=np.int16), linear[:-64]]
    return clean, linear


def spectral(pcm):
    samples = np.asarray(pcm, dtype=np.float64).reshape(-1, FRAME)
    magnitude = np.abs(np.fft.rfft(samples * TAPER, n=512, axis=1))[:, 1:193]
    level = np.log1p(np.sqrt((samples * samples).mean(axis=1)) / 32.0)
    return np.c_[np.log1p(magnitude / 256.0), level].astype(np.float16)


def ideal_mask(near, echo, labels):
    out = np.zeros((len(labels), 192), np.uint8)
    active = np.flatnonzero(labels)
    if len(active):
        n = np.abs(np.fft.rfft(near.reshape(-1, FRAME)[active] * TAPER, n=512, axis=1))[:, 1:193]
        e = np.abs(np.fft.rfft(echo.reshape(-1, FRAME)[active] * TAPER, n=512, axis=1))[:, 1:193]
        out[active] = np.rint(n / (n + e + 32.0) * 255).astype(np.uint8)
    return out


def one_source(item):
    tag, stem = item
    arrays = {key: [] for key in ("mic", "render", "clean", "linear", "y", "eligible")}
    masks = []
    records = []

    def append(name, category, mic, render, labels, events, near, echo):
        assert len(mic) == len(render) == len(labels) * FRAME
        m = spectral(mic)
        r = spectral(render)
        if category == 2:
            c = np.zeros_like(m)
            l = np.zeros_like(m)
        else:
            clean, linear = cancel(mic, render)
            c = spectral(clean)
            l = spectral(linear)
        eligible = np.ones(len(labels), np.uint8)
        if category == 0 and name != "natural":
            # The cropped microphone could have echo from absent prior render.
            # Keep those cases in validation, but not the training loss window.
            eligible[:min(60, len(labels))] = 0
        for key, value in (("mic", m), ("render", r), ("clean", c), ("linear", l),
                           ("y", labels), ("eligible", eligible)):
            arrays[key].append(value)
        records.append((name, len(labels), events, category))
        masks.append(ideal_mask(near, echo, labels))

    for name, mic, render, labels, events, near, echo in case_data.case_inputs(stem, tag):
        append(name, 0, mic, render, labels, events, near, echo)
    for name, mode, mic, render, labels, events, near, echo in physical_data.cases(stem, tag):
        append(name, 1 if mode == "sw" else 2, mic, render, labels, events, near, echo)
    combined = {key: np.concatenate(value) for key, value in arrays.items()}
    np.savez_compressed(ROOT / "tensors" / (stem + ".npz"), **combined, records=json.dumps(records))
    np.savez_compressed(ROOT / "masks" / (stem + ".npz"), mask=np.concatenate(masks))
    return stem, len(combined["y"])


if __name__ == "__main__":
    sources = MANIFEST["train"] + MANIFEST["validation"]
    with ThreadPoolExecutor(max_workers=8) as pool:
        for stem in pool.map(fetch_source, sources):
            print("verified", stem, flush=True)
    case_data.build_voice_bank()
    selected = [(tag, stem) for tag in ("train", "validation") for stem in MANIFEST[tag]]
    workers = int(os.environ.get("AEGIS_DTD_WORKERS", "4"))
    with ProcessPoolExecutor(max_workers=workers, initializer=init_worker) as pool:
        for stem, frames in pool.map(one_source, selected):
            print("prepared", stem, frames, flush=True)
