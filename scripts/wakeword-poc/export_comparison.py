#!/usr/bin/env python3
"""Export only allowlisted metrics from completed comparison summaries/CSVs."""

import argparse
import csv
import json
from pathlib import Path
import sys
import tempfile

ROOT = Path.home() / ".cache/aegis-wakeword-poc"
QUALITY_FIELDS = ["candidate", "config", "id", "original", "device", "label", "group", "form",
                  "duration_seconds", "detected", "activations", "timestamps", "scores",
                  "first_before_audio_end", "first_finish_only", "delay_from_estimated_word_end_seconds"]
EFFICIENCY_FIELDS = ["candidate", "config", "mode", "audio_seconds", "initialization_seconds",
                     "cpu_seconds", "cpu_seconds_per_audio_second", "detector_cpu_seconds",
                     "detector_cpu_seconds_per_audio_second", "rtf", "wall_seconds", "rss_stable_kib",
                     "rss_peak_kib", "initial_rss_kib", "model_bytes", "configured_threads", "runtime_threads",
                     "runtime_versions", "block_seconds", "block_ms_p50", "block_ms_p95", "block_ms_max",
                     "overrun_blocks", "arrival_lateness_ms_p95", "arrival_lateness_ms_max",
                     "silence_detector_cpu_per_audio_second", "noise_detector_cpu_per_audio_second",
                     "speech_detector_cpu_per_audio_second"]
NEGATIVE_FIELDS = ["candidate", "config", "audio_seconds", "activations"]
PACKAGES = {"numpy", "scipy", "python-speech-features", "rhasspy-silence", "webrtcvad-wheels",
            "pocketsphinx", "pywhispercpp", "onnxruntime", "openwakeword", "scikit-learn",
            "pymicro-features", "ai-edge-litert", "tensorflow"}


def model_identity(metadata):
    # Used solely for duplicate detection; hashes are never exported.
    if metadata.get("model_sha256"):
        return metadata["model_sha256"]
    models = metadata.get("models", {})
    if models:
        return json.dumps({k: v.get("sha256") for k, v in models.items()}, sort_keys=True)
    return str((metadata.get("version"), metadata.get("model"), metadata.get("model_bytes")))


def candidate_name(report):
    candidate = report["candidate"]
    if candidate == "microwakeword" and report["metadata"].get("uses_real_dev_negatives"):
        return "microwakeword-realneg"
    return candidate


def completed_runs(results):
    selected = {}
    for path in sorted(results.glob("*/summary.json"), key=lambda p: (p.stat().st_mtime_ns, str(p))):
        folder = path.parent
        if folder.name.upper().startswith("INVALID") or any(p.name.upper().startswith("INVALID") for p in folder.iterdir()):
            continue
        try:
            report = json.loads(path.read_text())
        except json.JSONDecodeError:
            print(f"Skipping incomplete summary: {folder.name}", file=sys.stderr)
            continue
        mode = report.get("mode")
        if mode not in ("quality", "negative", "efficiency") or mode not in report:
            continue
        if mode == "quality" and not (folder / "results.csv").is_file():
            continue
        if mode == "efficiency" and not all(k in report[mode] for k in ("offline", "realtime")):
            continue
        identity = (candidate_name(report), report["config"], model_identity(report["metadata"]), mode)
        if identity in selected:
            print(f"Deduplicating completed run: {selected[identity][0].name} -> {folder.name}", file=sys.stderr)
        selected[identity] = (folder, report)
    # Different models need distinct public candidate aliases to avoid mixing trials.
    public_keys = [(candidate_name(r), r["config"], r["mode"]) for _, r in selected.values()]
    if len(public_keys) != len(set(public_keys)):
        raise ValueError("Different model revisions share a candidate/config; mark obsolete runs INVALID or assign aliases")
    return sorted(selected.values(), key=lambda item: (candidate_name(item[1]), item[1]["config"], item[1]["mode"]))


def quality_rows(folder, report, inventory, selection):
    with (folder / "results.csv").open(newline="", encoding="utf-8") as stream:
        rows = list(csv.DictReader(stream))
    refs = {r["id"] for r in selection["references"]}
    adaptation = bool(report["metadata"].get("uses_reference_audio"))
    expected = set(inventory) - refs if adaptation else set(inventory)
    by_id = {row["id"]: row for row in rows}
    if len(rows) != len(by_id) or set(by_id) != expected or len(rows) not in (31, 37):
        raise ValueError(f"Unexpected dataset IDs/count in {folder.name}; expected 31 or 37 unique records")
    exported = []
    for identifier, original in inventory.items():
        base = {"candidate": candidate_name(report), "config": report["config"], "id": identifier,
                "original": original["original"], "device": original["device"], "label": original["label"],
                "form": selection["forms"].get(identifier, "negative"),
                "duration_seconds": original["wav_duration_seconds"]}
        if adaptation and identifier in refs:
            exported.append({**base, "group": "adaptation"})
            continue
        row = by_id[identifier]
        group = "complementary" if identifier in refs else "negative" if original["label"] == "negative" else "independent"
        if (row["candidate"] != report["candidate"] or row["config"] != report["config"] or row["group"] != group
                or any(row[k] != str(base[k]) for k in ("original", "device", "label", "form"))):
            raise ValueError(f"Dataset metadata/split differs in {folder.name}: {identifier}")
        if row["detected"] not in ("True", "False") or (row["detected"] == "True") != (int(row["activations"]) > 0):
            raise ValueError("Inconsistent detected/activation fields")
        if abs(float(row["duration_seconds"]) - float(base["duration_seconds"])) > 1 / 16000:
            raise ValueError("Recording duration differs from inventory")
        public = {key: row.get(key, "") for key in QUALITY_FIELDS}
        public["candidate"] = candidate_name(report)
        exported.append(public)
    for group in ("independent", "negative", "complementary"):
        actual = [row for row in rows if row["group"] == group]
        summary = report["quality"][group]["all"]
        if (len(actual), sum(row["detected"] == "True" for row in actual), sum(int(row["activations"]) for row in actual)) != (
                summary["files"], summary["detected"], summary["activations"]):
            raise ValueError(f"CSV and summary differ in {folder.name}")
    return exported


def model_bytes(metadata):
    if "model_bytes" in metadata:
        return metadata["model_bytes"]
    values = [model["bytes"] for model in metadata.get("models", {}).values() if "bytes" in model]
    return sum(values) if values else ""


def efficiency_rows(report):
    metadata = report["metadata"]
    versions = {}
    for key in ("versions", "packages", "runtime"):
        for package, version in metadata.get(key, {}).items():
            if package in PACKAGES and isinstance(version, (str, int, float)):
                versions[package] = str(version)
    if metadata.get("version"):
        versions[report["candidate"]] = str(metadata["version"])
    rows = []
    for mode in ("offline", "realtime"):
        measured = report["efficiency"][mode]
        row = {field: measured.get(field, "") for field in EFFICIENCY_FIELDS}
        row.update(candidate=candidate_name(report), config=report["config"], mode=mode,
                   initialization_seconds=report.get("initialization_including_adapter_import_seconds", ""),
                   rss_peak_kib=measured.get("memory", {}).get("VmHWM", ""),
                   initial_rss_kib=report.get("memory_after_initialization", {}).get("VmRSS", ""),
                   model_bytes=model_bytes(metadata), configured_threads=metadata.get("threads", ""),
                   runtime_threads=measured.get("memory", {}).get("Threads", ""),
                   runtime_versions=";".join(f"{k}={v}" for k, v in sorted(versions.items())))
        for phase in ("silence", "noise", "speech"):
            row[f"{phase}_detector_cpu_per_audio_second"] = measured.get("phase_detector_cpu_per_audio_second", {}).get(phase, "")
        rows.append(row)
    return rows


def negative_row(report):
    negative = report["negative"]
    metrics = negative["metrics"]
    seconds = metrics["audio_seconds"]
    # Report observed counts/duration, without extrapolating a per-hour rate.
    # Other experiment workers can be paused during the separate CPU benchmark.
    return {"candidate": candidate_name(report), "config": report["config"], "audio_seconds": seconds,
            "activations": negative["events"]}


def write_csv(path, fields, rows):
    with tempfile.NamedTemporaryFile(mode="w", dir=path.parent, newline="", encoding="utf-8", delete=False) as stream:
        temporary = Path(stream.name)
        writer = csv.DictWriter(stream, fieldnames=fields, lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)
    temporary.replace(path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results", type=Path, default=ROOT / "comparison/results")
    parser.add_argument("--data", type=Path, default=ROOT / "data")
    parser.add_argument("--selection", type=Path, default=Path(__file__).with_name("selection.json"))
    parser.add_argument("--output-dir", type=Path, required=True)
    args = parser.parse_args()
    records = json.loads((args.data / "inventory.json").read_text())["records"]
    inventory = {record["id"]: record for record in records}
    selection = json.loads(args.selection.read_text())
    if len(inventory) != 37 or len(selection["references"]) != 6:
        raise ValueError("Expected the fixed 37-record dataset and six references")
    quality, efficiency, negatives = [], [], []
    for folder, report in completed_runs(args.results):
        if report["mode"] == "quality":
            quality.extend(quality_rows(folder, report, inventory, selection))
        elif report["mode"] == "efficiency":
            efficiency.extend(efficiency_rows(report))
        else:
            negatives.append(negative_row(report))
    args.output_dir.mkdir(parents=True, exist_ok=True)
    for suffix, fields, rows in (("results", QUALITY_FIELDS, quality), ("efficiency", EFFICIENCY_FIELDS, efficiency),
                                 ("negatives", NEGATIVE_FIELDS, negatives)):
        write_csv(args.output_dir / f"v0.8.0-wakeword-comparison-{suffix}.csv", fields, rows)
        print(f"Exported {len(rows)} allowlisted {suffix} rows")


if __name__ == "__main__":
    main()
