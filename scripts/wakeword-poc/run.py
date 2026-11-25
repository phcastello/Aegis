#!/usr/bin/env python3
"""Build six private references, then test held-out recordings with Rustpotter 3.0.2."""

import argparse
import csv
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
import wave

from prepare import private_output, sha256


CONFIGS = {
    "default": [],
    "strict": ["-t", "0.6"],
    "gain": ["-g", "--max-gain", "10"],
}
DETECTION = re.compile(r"^Wakeword detection: \[([^]]+)\].*?avg_score: ([\d.eE+-]+), score: ([\d.eE+-]+),")


def command(args, log):
    result = subprocess.run(args, capture_output=True, text=True, timeout=60)
    log.write_text(result.stdout + result.stderr, encoding="utf-8")
    result.check_returncode()
    return result.stdout


def load_data(path):
    path = private_output(path)
    inventory = json.loads((path / "inventory.json").read_text())
    for row in inventory["records"]:
        wav = (path / row["wav_path"]).resolve()
        if not wav.is_relative_to(path) or sha256(wav) != row["wav_sha256"]:
            raise ValueError(f"Changed or unsafe WAV: {row['id']}")
    return path, inventory


def build(args):
    data, inventory = load_data(args.data)
    selection = json.loads(args.selection.read_text())
    refs = selection["references"]
    rows = {row["id"]: row for row in inventory["records"]}
    if (len(refs) != 6 or len({ref["id"] for ref in refs}) != 6
            or sorted(rows[ref["id"]]["device"] for ref in refs) != ["android"] * 3 + ["windows"] * 3
            or any(rows[ref["id"]]["label"] != "positive" for ref in refs)):
        raise ValueError("Select exactly three distinct positive references per device")
    output = args.output
    output.mkdir(parents=True, mode=0o700, exist_ok=False)
    ref_dir = output / "references"
    ref_dir.mkdir()
    for ref in refs:
        with wave.open(str(data / rows[ref["id"]]["wav_path"]), "rb") as source:
            start, end = round(ref["start"] * 16000), round(ref["end"] * 16000)
            if not 0 <= start < end <= source.getnframes():
                raise ValueError(f"Invalid reference range: {ref['id']}")
            source.setpos(start)
            with wave.open(str(ref_dir / f"{ref['id']}.wav"), "wb") as target:
                target.setparams(source.getparams())
                target.writeframes(source.readframes(end - start))
    cli = str(Path(shutil.which(args.cli) or args.cli).resolve(strict=True))
    command([cli, "build", "--name", "Aegis", "--path", str(output / "aegis.rpw"),
             *map(str, sorted(ref_dir.glob("*.wav")))], output / "build.log")
    manifest = {"cli": cli, "cli_sha256": sha256(Path(cli)),
                "model_sha256": sha256(output / "aegis.rpw"),
                "zip_sha256": inventory["zip_sha256"], "selection": selection}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"Built six private references in {output}")


def test(args):
    data, inventory = load_data(args.data)
    experiment = private_output(args.experiment)
    manifest = json.loads((experiment / "manifest.json").read_text())
    if (manifest["zip_sha256"] != inventory["zip_sha256"]
            or sha256(Path(manifest["cli"])) != manifest["cli_sha256"]
            or sha256(experiment / "aegis.rpw") != manifest["model_sha256"]):
        raise ValueError("Dataset, CLI or model differs from build manifest")
    output = experiment / args.config
    output.mkdir(mode=0o700, exist_ok=False)
    ref_ids = {row["id"] for row in manifest["selection"]["references"]}
    forms = manifest["selection"].get("forms", {})
    results = []
    for row in inventory["records"]:
        result = {"id": row["id"], "original": row["original"], "device": row["device"],
                  "label": row["label"], "role": "reference" if row["id"] in ref_ids else "test",
                  "form": forms.get(row["id"], "negative" if row["label"] == "negative" else "unknown"),
                  "duration_seconds": row["wav_duration_seconds"], "config": args.config,
                  "detected": "", "activations": "", "timestamps": "", "scores": "", "avg_scores": "", "elapsed_seconds": ""}
        if result["role"] == "test":
            started = time.perf_counter()
            stdout = command([manifest["cli"], "test", *CONFIGS[args.config],
                              str(experiment / "aegis.rpw"), str(data / row["wav_path"])],
                             output / f"{row['id']}.log")
            detections = [DETECTION.match(line) for line in stdout.splitlines()
                          if line.startswith("Wakeword detection:")]
            if any(event is None for event in detections):
                raise ValueError("Unexpected CLI output: inspect private log")
            result.update(detected=bool(detections), activations=len(detections),
                          timestamps=";".join(event[1] for event in detections),
                          scores=";".join(event[3] for event in detections),
                          avg_scores=";".join(event[2] for event in detections),
                          elapsed_seconds=round(time.perf_counter() - started, 6))
            print(f"{args.config}: {row['id']} activations={len(detections)}", flush=True)
        results.append(result)
    with (output / "results.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(results[0]))
        writer.writeheader()
        writer.writerows(results)
    summary = {"arguments": CONFIGS[args.config]}
    for device in ("all", "android", "windows"):
        tests = [row for row in results if row["role"] == "test" and (device == "all" or row["device"] == device)]
        positive = [row for row in tests if row["label"] == "positive"]
        negative = [row for row in tests if row["label"] == "negative"]
        hits = sum(row["detected"] for row in positive)
        summary[device] = {"positive_detected": hits, "positive_tested": len(positive),
                           "recognition_percent": round(100 * hits / len(positive), 2),
                           "negative_files_activated": sum(row["detected"] for row in negative),
                           "negative_activations": sum(row["activations"] for row in negative),
                           "negative_seconds": sum(row["duration_seconds"] for row in negative)}
    (output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="action", required=True)
    builder = subparsers.add_parser("build")
    builder.add_argument("--data", required=True, type=Path)
    builder.add_argument("--selection", required=True, type=Path)
    builder.add_argument("--output", required=True, type=private_output)
    builder.add_argument("--cli", default="rustpotter-cli")
    tester = subparsers.add_parser("test")
    tester.add_argument("--data", required=True, type=Path)
    tester.add_argument("--experiment", required=True, type=private_output)
    tester.add_argument("--config", choices=CONFIGS, default="default")
    args = parser.parse_args()
    os.umask(0o077)
    (build if args.action == "build" else test)(args)


if __name__ == "__main__":
    main()
