#!/usr/bin/env python3
"""Validate and copy the frozen private model for explicit Android SAF import.

No training, networking, model changes or public upload. Output must be new and
outside Git. Only the frozen raw .tflite is imported by Android; model.json is a
human-readable provenance companion, not an alternative detector configuration.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/wakeword-poc"))
from prepare import private_output


def validate(model, manifest):
    raw = model.read_bytes()
    if len(raw) != manifest["bytes"] or hashlib.sha256(raw).hexdigest() != manifest["sha256"]:
        raise ValueError("Model differs from frozen Aegis INT8 DEV artifact")
    training_path = model.with_name("training.json")
    training = json.loads(training_path.read_text())
    for key, expected in (("model_sha256", manifest["sha256"]),
                          ("model_bytes", manifest["bytes"]),
                          ("source_sha", manifest["trainingSourceCommit"]),
                          ("training_manifest_sha256", manifest["trainingManifestSha256"]),
                          ("real_negative_manifest_sha256", manifest["realNegativeManifestSha256"])):
        if training.get(key) != expected:
            raise ValueError(f"Training provenance mismatch: {key}")
    snapshot = model.with_name("real-negative-manifest.json")
    if hashlib.sha256(snapshot.read_bytes()).hexdigest() != manifest["realNegativeManifestSha256"]:
        raise ValueError("Frozen FLEURS DEV manifest changed")
    return raw, hashlib.sha256(training_path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", type=Path, default=Path.home() / ".cache/aegis-wakeword-poc/comparison/microwakeword/model-realneg/aegis.int8.tflite")
    parser.add_argument("--output", type=private_output, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    manifest = json.loads((Path(__file__).with_name("model.json")).read_text())
    raw, training_digest = validate(args.model, manifest)
    args.output.mkdir(parents=True, mode=0o700, exist_ok=False)
    target = args.output / "aegis.int8.tflite"
    target.write_bytes(raw)
    (args.output / "model.json").write_text(json.dumps({**manifest, "sourceTrainingFileSha256": training_digest}, ensure_ascii=False, indent=2) + "\n")
    print(f"Private import prepared: {target}; {len(raw)} bytes; SHA-256 {manifest['sha256']}")


if __name__ == "__main__":
    main()
