#!/usr/bin/env python3
"""Replay existing private WAVs through sherpa-onnx KWS; never runs Rustpotter."""

import argparse
import csv
import importlib.metadata
import json
import os
from pathlib import Path
import resource
import time
import wave

import numpy as np
import sherpa_onnx

from prepare import private_output, sha256
from run import load_data


MODEL_NAME = "sherpa-onnx-kws-zipformer-zh-en-3M-2025-12-20"
MODEL_FILES = {
    "encoder": "encoder-epoch-13-avg-2-chunk-16-left-64.onnx",
    "decoder": "decoder-epoch-13-avg-2-chunk-16-left-64.onnx",
    "joiner": "joiner-epoch-13-avg-2-chunk-16-left-64.onnx",
    "tokens": "tokens.txt",
}


def decode(spotter, samples, expected_keyword="Aegis"):
    stream = spotter.create_stream()
    events = []

    def drain(fed_samples):
        while spotter.is_ready(stream):
            spotter.decode_stream(stream)
            # get_result consumes the result: read all fields from ONE object.
            result = spotter.keyword_spotter.get_result(stream)
            if result.keyword:
                if result.keyword != expected_keyword:
                    raise ValueError("Unexpected keyword in experiment")
                events.append({
                    "keyword": result.keyword, "tokens": list(result.tokens),
                    "timestamps": [round(t, 6) for t in result.timestamps],
                    "emitted_after_input_seconds": round(fed_samples / 16000, 6),
                })
                spotter.reset_stream(stream)

    # Same PCM, no trimming or gain; small blocks mimic incremental feeding.
    for start in range(0, len(samples), 1600):
        block = samples[start:start + 1600]
        stream.accept_waveform(16000, block)
        drain(start + len(block))
    # Official Python KWS example uses 0.66 s silence to flush pending results.
    stream.accept_waveform(16000, np.zeros(10560, dtype=np.float32))
    stream.input_finished()
    drain(len(samples) + 10560)
    return events


def counts(rows):
    return {"files": len(rows), "detected_files": sum(r["detected"] for r in rows),
            "activations": sum(r["activations"] for r in rows),
            "percent": round(100 * sum(r["detected"] for r in rows) / len(rows), 2) if rows else None}


def memory():
    # Linux ru_maxrss can survive exec and include the launcher's old peak.
    status = dict(line.split(":", 1) for line in Path("/proc/self/status").read_text().splitlines()
                  if line.startswith(("VmRSS:", "VmHWM:")))
    return {key: int(value.split()[0]) for key, value in status.items()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", required=True, type=Path)
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--keywords-config", type=Path,
                        default=Path(__file__).with_name("sherpa-keywords.json"))
    parser.add_argument("--variant", default="primary")
    parser.add_argument("--config", required=True)
    parser.add_argument("--score", type=float, default=1.0)
    parser.add_argument("--threshold", type=float, default=0.25)
    parser.add_argument("--output", required=True, type=private_output)
    args = parser.parse_args()
    os.umask(0o077)
    if sherpa_onnx.__version__ != "2.0.1":
        parser.error("This experiment pins sherpa-onnx 2.0.1")
    if not 0 < args.threshold <= 1 or args.score < 0:
        parser.error("Use a positive threshold <= 1 and a nonnegative score")
    if args.output.exists():
        parser.error("Use a new private --output directory")
    data, inventory = load_data(args.data)
    repo = Path(__file__).resolve().parents[2]
    baseline = {r["id"]: r for r in csv.DictReader(
        (repo / "docs/v0.8.0-wakeword-poc-results.csv").open()) if r["config"] == "default"}
    selection = json.loads((Path(__file__).with_name("selection.json")).read_text())
    reference_ids = {r["id"] for r in selection["references"]}
    if len(baseline) != 37 or set(baseline) != {r["id"] for r in inventory["records"]}:
        raise ValueError("Dataset IDs differ from the baseline")
    for row in inventory["records"]:
        if (any(row[k] != baseline[row["id"]][k] for k in ("original", "device", "label"))
                or (row["id"] in reference_ids) != (baseline[row["id"]]["role"] == "reference")):
            raise ValueError("Dataset metadata or split differs from the baseline")
    keywords = json.loads(args.keywords_config.read_text())
    variants = {variant["id"]: variant for variant in keywords["representations"]}
    phones = variants[args.variant]["tokens"]
    models = {k: args.model / v for k, v in MODEL_FILES.items()}
    vocabulary = {line.split()[0] for line in models["tokens"].read_text().splitlines() if line.strip()}
    if not phones or not all(phone in vocabulary for phone in phones):
        raise ValueError("Keyword contains unsupported model tokens")
    args.output.mkdir(parents=True, mode=0o700)
    keywords_path = args.output / "keywords.txt"
    keywords_path.write_text(" ".join(phones) + " @Aegis\n")
    parameters = {"num_threads": 1, "provider": "cpu", "max_active_paths": 4,
                  "num_trailing_blanks": 1, "keywords_score": args.score,
                  "keywords_threshold": args.threshold, "sample_rate": 16000, "feature_dim": 80}
    started = time.perf_counter()
    cpu_started = time.process_time()
    spotter = sherpa_onnx.KeywordSpotter(**{k: str(v) for k, v in models.items()},
                                      keywords_file=str(keywords_path), **parameters)
    load_seconds = time.perf_counter() - started
    after_load_memory = memory()
    results, raw = [], []
    for row in inventory["records"]:
        with wave.open(str(data / row["wav_path"]), "rb") as wav:
            if (wav.getnchannels(), wav.getframerate(), wav.getsampwidth()) != (1, 16000, 2):
                raise ValueError("Unexpected WAV format")
            samples = np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2").astype(np.float32) / 32768
        tick = time.perf_counter()
        events = decode(spotter, samples)
        raw.append({"id": row["id"], "events": events, "decode_seconds": time.perf_counter() - tick})
        group = "complementary" if row["id"] in reference_ids else "independent" if row["label"] == "positive" else "negative"
        results.append({
            "config": args.config, "variant": args.variant, "id": row["id"],
            "original": row["original"], "device": row["device"], "label": row["label"],
            "group": group, "form": selection["forms"].get(row["id"], "negative"),
            "duration_seconds": row["wav_duration_seconds"], "detected": bool(events),
            "activations": len(events),
            "timestamps": ";".join(f"{e['timestamps'][0]:.3f}" for e in events),
            "token_timestamps": json.dumps([e["timestamps"] for e in events], separators=(",", ":")),
            "emission_timestamps": ";".join(f"{e['emitted_after_input_seconds']:.3f}" for e in events),
            "scores": "", "notes": "No acoustic score exposed; token clock may reset; emission time is absolute input position, not latency",
        })
        print(f"{args.config}: {row['id']} ({group}): {len(events)} activation(s)", flush=True)
    elapsed_seconds = time.perf_counter() - started
    with (args.output / "results.csv").open("w", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(results[0]), lineterminator="\n")
        writer.writeheader()
        writer.writerows(results)
    summary = {}
    for group in ("independent", "negative", "complementary"):
        group_rows = [r for r in results if r["group"] == group]
        summary[group] = {"all": counts(group_rows)}
        for dimension, values in (("device", ("android", "windows")), ("form", ("isolated", "continuous"))):
            summary[group][dimension] = {v: counts([r for r in group_rows if r[dimension] == v]) for v in values}
    summary["resources"] = {"load_seconds": load_seconds, "elapsed_seconds": elapsed_seconds,
                            "decode_seconds": sum(r["decode_seconds"] for r in raw),
                            "cpu_seconds": time.process_time() - cpu_started,
                            "audio_seconds": sum(r["wav_duration_seconds"] for r in inventory["records"]),
                            "padding_seconds_per_file": 0.66,
                            "after_model_load_kib": after_load_memory,
                            "after_dataset_kib": memory(),
                            "max_rss_kib_linux_process": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss}
    manifest = {"config": args.config, "variant": args.variant, "parameters": parameters,
                "keyword_tokens": phones, "zip_sha256": inventory["zip_sha256"],
                "packages": {p: importlib.metadata.version(p) for p in ("sherpa-onnx", "sherpa-onnx-core", "numpy")},
                "model_files": {k: {"sha256": sha256(v), "bytes": v.stat().st_size} for k, v in models.items()}}
    for filename, content in (("events.json", raw), ("summary.json", summary), ("manifest.json", manifest)):
        (args.output / filename).write_text(json.dumps(content, indent=2) + "\n")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
