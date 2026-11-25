#!/usr/bin/env python3
"""Run the selected local wake-word experiments against the existing private dataset."""

import argparse
import csv
import gc
import json
import os
from pathlib import Path
import statistics
import time
import wave

import numpy as np

from prepare import private_output
from run import load_data

ROOT = Path.home() / ".cache/aegis-wakeword-poc"


def make_detector(candidate, config, model_path=None):
    if candidate in ("raven", "pocketsphinx"):
        from compare_classic import RavenDetector, PocketSphinxDetector
        return {"raven": RavenDetector, "pocketsphinx": PocketSphinxDetector}[candidate](config=config)
    if candidate == "whisper":
        from compare_whisper import WhisperTinyDetector
        return WhisperTinyDetector(config=config)
    if candidate == "openwakeword":
        from compare_openwakeword import OpenWakeWordDetector
        return OpenWakeWordDetector(config=config)
    if candidate in ("microwakeword", "microwakeword-realneg"):
        from compare_microwakeword import MicroWakeWordDetector
        model = model_path or (ROOT / "comparison/microwakeword/model-realneg/aegis.int8.tflite" if candidate.endswith("realneg") else None)
        return MicroWakeWordDetector(config=config, model_path=model)
    if candidate == "preciselite":
        from compare_microwakeword import PreciseLiteDetector
        return PreciseLiteDetector(config=config)
    raise ValueError(candidate)


def read_wav(path):
    with wave.open(str(path), "rb") as wav:
        if (wav.getnchannels(), wav.getframerate(), wav.getsampwidth()) != (1, 16000, 2):
            raise ValueError(f"Expected mono 16 kHz PCM16: {path}")
        return np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2").copy()


def rss():
    result = {}
    for line in Path("/proc/self/status").read_text().splitlines():
        if line.startswith(("VmRSS:", "VmHWM:", "Threads:")):
            key, value = line.split(":", 1)
            result[key] = int(value.split()[0])
    return result


def replay(detector, audio, paced=False, tail_seconds=0, finish=False, measure_phases=False):
    step = detector.block_samples
    if tail_seconds:
        audio = np.concatenate((audio, np.zeros(round(tail_seconds * 16000), dtype=np.int16)))
    events, timings, memory_samples, lateness = [], [], [], []
    detector_cpu = 0.0
    phases = {name: {"cpu_seconds": 0.0, "samples": 0} for name in ("silence", "noise", "speech")}
    cpu_start, started = time.process_time(), time.perf_counter()
    for start in range(0, len(audio), step):
        block = audio[start:start + step]
        fed = start + len(block)
        if len(block) < step:
            block = np.pad(block, (0, step - len(block)))
        if paced:
            time.sleep(max(0, started + fed / 16000 - time.perf_counter()))
            lateness.append(max(0, time.perf_counter() - (started + fed / 16000)))
        tick = time.perf_counter()
        detector_tick = time.process_time()
        produced = detector.process(block)
        block_cpu = time.process_time() - detector_tick
        detector_cpu += block_cpu
        if measure_phases:
            phase = phases[("silence", "noise", "speech")[min(2, start * 3 // len(audio))]]
            phase["cpu_seconds"] += block_cpu
            phase["samples"] += fed - start
        timings.append(time.perf_counter() - tick)
        for event in produced or []:
            events.append({**event, "detected_at": round(fed / 16000, 6), "finish_only": False})
        if len(timings) % max(1, 16000 // step) == 0:
            memory_samples.append(rss()["VmRSS"])
    if finish and hasattr(detector, "finish"):
        for event in detector.finish() or []:
            events.append({**event, "detected_at": round(len(audio) / 16000, 6), "finish_only": True})
    elapsed, cpu_seconds = time.perf_counter() - started, time.process_time() - cpu_start
    n = max(1, len(audio)) / 16000
    return events, {"audio_seconds": n, "wall_seconds": elapsed, "cpu_seconds": cpu_seconds,
                    "cpu_seconds_per_audio_second": cpu_seconds / n,
                    "detector_cpu_seconds": detector_cpu, "detector_cpu_seconds_per_audio_second": detector_cpu / n,
                    "phase_detector_cpu_per_audio_second": {name: value["cpu_seconds"] / (value["samples"] / 16000)
                                                             for name, value in phases.items() if value["samples"]},
                    "rtf": elapsed / n,
                    "block_samples": step, "block_seconds": step / 16000,
                    "block_ms_p50": float(np.percentile(timings, 50)) * 1000,
                    "block_ms_p95": float(np.percentile(timings, 95)) * 1000,
                    "block_ms_max": max(timings) * 1000,
                    "overrun_blocks": sum(t > step / 16000 for t in timings),
                    "arrival_lateness_ms_p95": float(np.percentile(lateness, 95)) * 1000 if lateness else None,
                    "arrival_lateness_ms_max": max(lateness) * 1000 if lateness else None,
                    "rss_stable_kib": statistics.median(memory_samples[len(memory_samples) // 2:]) if memory_samples else rss()["VmRSS"],
                    "memory": rss()}


def summarize(rows):
    summaries = {}
    for group in ("independent", "negative", "complementary"):
        selected = [row for row in rows if row["group"] == group]
        def count(subset):
            return {"files": len(subset), "detected": sum(row["detected"] for row in subset),
                    "activations": sum(row["activations"] for row in subset),
                    "duplicates": sum(max(0, row["activations"] - 1) for row in subset)}
        summaries[group] = {"all": count(selected)}
        for field, values in (("device", ("android", "windows")), ("form", ("isolated", "continuous"))):
            summaries[group][field] = {value: count([r for r in selected if r[field] == value]) for value in values}
    return summaries


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--config", default="default")
    parser.add_argument("--mode", choices=("quality", "negative", "efficiency"), required=True)
    parser.add_argument("--data", type=Path, default=ROOT / "data")
    parser.add_argument("--public-wav", type=Path, default=ROOT / "comparison/public/negatives.wav")
    parser.add_argument("--model-path", type=Path, help="Optional explicit microWakeWord artifact for a fresh reproduction")
    parser.add_argument("--seconds", type=int, default=120)
    parser.add_argument("--output", required=True, type=private_output)
    args = parser.parse_args()
    os.umask(0o077)
    args.output.mkdir(parents=True, exist_ok=False, mode=0o700)
    # Cold adapter/runtime/model preparation excludes corpus reads below.
    import_start = time.perf_counter()
    if args.model_path and args.candidate not in ("microwakeword", "microwakeword-realneg"):
        parser.error("--model-path applies only to the custom microWakeWord variants")
    detector = make_detector(args.candidate, args.config, args.model_path)
    initialization_seconds = time.perf_counter() - import_start
    metadata = detector.metadata() if callable(detector.metadata) else detector.metadata
    report = {"candidate": args.candidate, "config": args.config, "mode": args.mode,
              "initialization_including_adapter_import_seconds": initialization_seconds,
              "metadata": metadata, "cpu_affinity": sorted(os.sched_getaffinity(0)),
              "memory_after_initialization": rss()}
    raw = []
    if args.mode == "quality":
        data, inventory = load_data(args.data)
        selection = json.loads(Path(__file__).with_name("selection.json").read_text())
        refs = {ref["id"] for ref in selection["references"]}
        boundaries_path = ROOT / "sherpa/wakeword-boundaries.json"
        boundaries = {r["id"]: r for r in json.loads(boundaries_path.read_text())["records"]} if boundaries_path.exists() else {}
        rows = []
        for record in inventory["records"]:
            group = "adaptation" if record["id"] in refs and metadata.get("uses_reference_audio") else "complementary" if record["id"] in refs else "independent" if record["label"] == "positive" else "negative"
            if group == "adaptation":
                continue
            audio = read_wav(data / record["wav_path"])
            detector.reset()
            events, metrics = replay(detector, audio, tail_seconds=0.5, finish=True)
            raw.append({"id": record["id"], "events": events, "metrics": metrics})
            if args.candidate == "whisper":
                raw[-1]["private_transcripts"] = detector.raw_transcripts
            first = events[0]["detected_at"] if events else None
            boundary = boundaries.get(record["id"])
            rows.append({"candidate": args.candidate, "config": args.config,
                         "id": record["id"], "original": record["original"], "device": record["device"],
                         "label": record["label"], "group": group,
                         "form": selection["forms"].get(record["id"], "negative"),
                         "duration_seconds": len(audio) / 16000, "detected": bool(events), "activations": len(events),
                         "timestamps": ";".join(str(e["detected_at"]) for e in events),
                         "scores": ";".join(str(e.get("score", "")) for e in events),
                         "first_before_audio_end": first < len(audio) / 16000 if first is not None else "",
                         "first_finish_only": events[0]["finish_only"] if events else "",
                         "delay_from_estimated_word_end_seconds": round(first - boundary["end_estimate_seconds"], 3) if first is not None and boundary else ""})
            print(f"{args.candidate}/{args.config} {record['id']} {len(events)}", flush=True)
        with (args.output / "results.csv").open("w", newline="") as stream:
            writer = csv.DictWriter(stream, fieldnames=list(rows[0]), lineterminator="\n")
            writer.writeheader()
            writer.writerows(rows)
        report["quality"] = summarize(rows)
    elif args.mode == "negative":
        audio = read_wav(args.public_wav)
        detector.reset()
        events, metrics = replay(detector, audio)
        raw = events
        report["negative"] = {"events": len(events), "metrics": metrics}
    else:
        # Read only the two short source excerpts. Holding the full 35-minute
        # corpus here would inflate stable detector RSS by roughly 64 MiB.
        with wave.open(str(args.public_wav), "rb") as wav:
            if (wav.getnchannels(), wav.getframerate(), wav.getsampwidth()) != (1, 16000, 2):
                raise ValueError("Unexpected efficiency source format")
            speech = np.frombuffer(wav.readframes(30 * 16000), dtype="<i2").copy()
            wav.setpos(max(0, wav.getnframes() - 30 * 16000))
            noise = np.frombuffer(wav.readframes(30 * 16000), dtype="<i2").copy()
        part = max(16000, args.seconds * 16000 // 3)
        # Public corpus begins with speech and ends with environmental noise.
        audio = np.concatenate((np.zeros(part, dtype=np.int16),
                                np.resize(noise, part), np.resize(speech, part)))
        del noise, speech
        detector.reset()
        _, offline = replay(detector, audio, measure_phases=True)
        detector.reset()
        gc.collect()
        _, realtime = replay(detector, audio, paced=True, measure_phases=True)
        report["efficiency"] = {"offline": offline, "realtime": realtime,
                                "sequence": "equal thirds silence, public noise, Portuguese public read speech"}
    (args.output / "events.json").write_text(json.dumps(raw, indent=2, default=float) + "\n")
    (args.output / "summary.json").write_text(json.dumps(report, indent=2, default=float) + "\n")
    print(json.dumps(report, indent=2, default=float))


if __name__ == "__main__":
    main()
