#!/usr/bin/env python3
"""Replay frozen private WAVs through the Android C++ detector and accepted PoC.

Run with the existing PoC Python environment. Requires the host build of the
same C++ sources used by JNI. No audio/model bytes leave the machine; reports
must be outside Git. ARM64 replay is a separate required physical check.
"""
import argparse
import ctypes
import gc
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import sys
import time
import wave

import numpy as np

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "scripts/wakeword-poc"))
from prepare import private_output
from run import load_data
from compare_microwakeword import MicroWakeWordDetector

DEFAULT_CACHE = Path.home() / ".cache/aegis-wakeword-poc"
MANIFEST = json.loads(Path(__file__).with_name("model.json").read_text())


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read_wav(path, seconds=None):
    with wave.open(str(path), "rb") as wav:
        if (wav.getnchannels(), wav.getframerate(), wav.getsampwidth()) != (1, 16000, 2):
            raise ValueError("Replay requires mono PCM16 at 16kHz")
        return np.frombuffer(wav.readframes(wav.getnframes() if seconds is None else round(seconds * 16000)), dtype="<i2").copy()


class Native:
    def __init__(self, library, model):
        self.lib = ctypes.CDLL(str(library))
        self.lib.aegis_mww_create.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
        self.lib.aegis_mww_create.restype = ctypes.c_void_p
        self.lib.aegis_mww_process.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int16), ctypes.c_size_t]
        self.lib.aegis_mww_process.restype = ctypes.c_int64
        self.lib.aegis_mww_reset.argtypes = [ctypes.c_void_p]
        self.lib.aegis_mww_reset.restype = ctypes.c_bool
        self.lib.aegis_mww_destroy.argtypes = [ctypes.c_void_p]
        for name in ("last_score", "max_score"):
            fn = getattr(self.lib, "aegis_mww_" + name)
            fn.argtypes = [ctypes.c_void_p]
            fn.restype = ctypes.c_float
        raw = model.read_bytes()
        if len(raw) != MANIFEST["bytes"] or hashlib.sha256(raw).hexdigest() != MANIFEST["sha256"]:
            raise ValueError("Refusing model other than frozen DEV artifact")
        self.handle = self.lib.aegis_mww_create(ctypes.c_char_p(raw), len(raw))
        if not self.handle:
            raise RuntimeError("TFLite Micro could not load frozen Aegis graph")

    def reset(self):
        if not self.lib.aegis_mww_reset(self.handle):
            raise RuntimeError("Native reset failed")

    def process(self, samples):
        x = np.ascontiguousarray(samples, dtype=np.int16)
        event = self.lib.aegis_mww_process(self.handle, x.ctypes.data_as(ctypes.POINTER(ctypes.c_int16)), len(x))
        if event == -2:
            raise RuntimeError("Native inference failed")
        return [] if event < 0 else [event]

    @property
    def last_score(self):
        return self.lib.aegis_mww_last_score(self.handle)

    @property
    def max_score(self):
        return self.lib.aegis_mww_max_score(self.handle)

    def close(self):
        if self.handle:
            self.lib.aegis_mww_destroy(self.handle)
            self.handle = None


def feed(detector, audio, chunks=(160,)):
    events, scores = [], []
    start = 0
    index = 0
    while start < len(audio):
        n = chunks[index % len(chunks)]
        block = audio[start:start+n]
        produced = detector.process(block)
        events.extend(round(e["timestamp"] * 16000) if isinstance(e, dict) else e for e in produced)
        scores.append(float(detector.last_score))
        start += len(block)
        index += 1
    return events, np.asarray(scores)


def padded(audio, tail=0):
    audio = np.concatenate((audio, np.zeros(round(tail * 16000), dtype=np.int16)))
    return np.pad(audio, (0, (-len(audio)) % 160))


def compare(reference, native, audio, label, reset=True, tolerance=1/256):
    if reset:
        reference.reset()
        native.reset()
    pe, ps = feed(reference, audio)
    ne, ns = feed(native, audio)
    score_delta = np.abs(ps - ns)
    event_delta = [abs(a-b) for a, b in zip(pe, ne)]
    ok = len(pe) == len(ne) and max(event_delta, default=0) <= 480 and float(score_delta.max(initial=0)) <= tolerance + 1e-9
    return {"case": label, "samples": len(audio), "reference_events_samples": pe,
            "native_events_samples": ne, "max_event_delta_samples": max(event_delta, default=0),
            "max_score_delta": float(score_delta.max(initial=0)),
            "score_samples_different": int(np.count_nonzero(score_delta)),
            "events_passed": len(pe) == len(ne) and max(event_delta, default=0) <= 480,
            "scores_passed": float(score_delta.max(initial=0)) <= tolerance + 1e-9, "passed": ok}


def rss():
    for line in Path("/proc/self/status").read_text().splitlines():
        if line.startswith("VmRSS:"):
            return int(line.split()[1])
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--library", type=Path, required=True)
    parser.add_argument("--model", type=Path, default=DEFAULT_CACHE / "comparison/microwakeword/model-realneg/aegis.int8.tflite")
    parser.add_argument("--data", type=Path, default=DEFAULT_CACHE / "data")
    parser.add_argument("--public-wav", type=Path, default=DEFAULT_CACHE / "comparison/public/negatives.wav")
    parser.add_argument("--output", type=private_output, required=True)
    parser.add_argument("--reference-kernels", action="store_true", help="Diagnostic comparison with LiteRT BUILTIN_REF; does not replace accepted default-delegate comparison")
    parser.add_argument("--skip-public", action="store_true", help="Quick iteration only; full verification requires public corpus")
    args = parser.parse_args()
    os.umask(0o077)
    args.output.mkdir(parents=True, mode=0o700, exist_ok=False)
    data, inventory = load_data(args.data)
    selection = json.loads((REPO / "scripts/wakeword-poc/selection.json").read_text())
    refs = {r["id"] for r in selection["references"]}
    reference = MicroWakeWordDetector(model_path=args.model)
    if args.reference_kernels:
        from ai_edge_litert.interpreter import Interpreter, OpResolverType
        reference._interpreter_class = lambda **kw: Interpreter(**kw, experimental_op_resolver_type=OpResolverType.BUILTIN_REF)
        reference.reset()
    native = Native(args.library.resolve(), args.model)
    started = time.monotonic()
    results = []
    try:
        for row in inventory["records"]:
            result = compare(reference, native, padded(read_wav(data / row["wav_path"]), .5), row["id"])
            result.update(label=row["label"], group="reference" if row["id"] in refs else row["label"])
            results.append(result)
        # Accepted continuous runner: 30s public prefix, then 31 held-out files,
        # three seconds silence between trials; no frontend/interpreter resets.
        results.append(compare(reference, native, padded(read_wav(args.public_wav, 30)), "continuous-prefix-30s"))
        for row in inventory["records"]:
            if row["id"] not in refs:
                result = compare(reference, native, padded(read_wav(data / row["wav_path"]), 3), "continuous-" + row["id"], reset=False)
                result.update(label=row["label"], group="continuous")
                results.append(result)
        if not args.skip_public:
            results.append(compare(reference, native, padded(read_wav(args.public_wav)), "public-negatives-35min"))
        sample = read_wav(data / next(r["wav_path"] for r in inventory["records"] if r["label"] == "positive" and r["id"] not in refs))
        for detector in (reference, native):
            detector.reset()
        pe, ps = feed(reference, sample, (17, 257, 89, 480, 3, 161))
        ne, ns = feed(native, sample, (17, 257, 89, 480, 3, 161))
        delta = float(np.abs(ps-ns).max(initial=0))
        results.append({"case": "partial-blocks", "reference_events_samples": pe, "native_events_samples": ne,
                        "max_score_delta": delta, "passed": pe == ne and delta <= 1/256 + 1e-9})
        # Dirty state, reset, then same stream versus a new native instance.
        noise = np.random.default_rng(20261031).integers(-5000, 5001, 32000, dtype=np.int16)
        feed(native, noise)
        native.reset()
        fresh = Native(args.library.resolve(), args.model)
        try:
            re, rs = feed(native, padded(sample, .5))
            fe, fs = feed(fresh, padded(sample, .5))
            results.append({"case": "dirty-reset-vs-fresh", "max_score_delta": float(np.abs(rs-fs).max(initial=0)),
                            "passed": re == fe and np.array_equal(rs, fs)})
        finally:
            fresh.close()
        # Detect native allocation growth independently of Python/LiteRT replays.
        gc.collect()
        before = rss()
        for _ in range(200):
            native.reset()
            feed(native, noise[:4000])
        after = rss()
        results.append({"case": "native-200-resets-bounded-memory", "rss_before_kib": before, "rss_after_kib": after,
                        "passed": before is None or after - before < 2048})
    finally:
        native.close()
    summary = {"schema": 1, "host": platform.machine(), "runtime": "same C++ frontend and TFLite Micro sources as Android JNI",
               "model": MANIFEST, "library_sha256": sha(args.library), "inventory_sha256": sha(data / "inventory.json"),
               "public_wav_sha256": sha(args.public_wav), "elapsed_seconds": time.monotonic()-started,
               "tolerance": {"max_score_delta": 1/256, "max_event_delta_samples": 480, "activated_file_difference": 0,
                             "reset_and_partial_events": "exact"},
               "python_versions": {name: importlib.metadata.version(name) for name in ("numpy", "pymicro-features", "ai-edge-litert")},
               "cases": results, "passed": all(r["passed"] for r in results),
               "full_public_replay": not args.skip_public, "reference_kernel_mode": "diagnostic BUILTIN_REF" if args.reference_kernels else "accepted default LiteRT delegates", "arm64": "pending; host replay does not prove ARM64 parity"}
    (args.output / "parity.json").write_text(json.dumps(summary, indent=2, ensure_ascii=False) + "\n")
    print(json.dumps({"passed": summary["passed"], "cases": len(results), "output": str(args.output),
                      "max_score_delta": max(r.get("max_score_delta", 0) for r in results),
                      "failed": [r["case"] for r in results if not r["passed"]], "arm64": summary["arm64"]}, indent=2))
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
