#!/usr/bin/env python3
"""Prepare public FLEURS DEV negatives without opening evaluation audio."""

import argparse
import csv
import hashlib
import json
from pathlib import Path, PurePosixPath
import random
import re
import shutil
import subprocess
import tarfile
import tempfile
import unicodedata
import urllib.request
import wave

from prepare import private_output, sha256

ROOT = Path.home() / ".cache/aegis-wakeword-poc"
INPUTS = {
    "dev.tar.gz": ("https://huggingface.co/datasets/google/fleurs/resolve/main/data/pt_br/audio/dev.tar.gz",
                   "15f2a075859c3b6448e81c867f9c2a77694b3deab6b24ebc632de09bbf401838"),
    "dev.tsv": ("https://huggingface.co/datasets/google/fleurs/resolve/main/data/pt_br/dev.tsv",
                "69a60f2ef2964da22ac38c3fe223a5ff5d59b9d7fb8ccfcc7003737fc7a1f69c"),
}


def normalize(text):
    plain = "".join(c for c in unicodedata.normalize("NFD", text.casefold())
                    if unicodedata.category(c) != "Mn")
    return " ".join(re.findall("[a-z]+", plain))


def frozen_group_key(text):
    # Keep the grouping identifiers/order frozen by the first preparation.
    # Keyword filtering and TEST dedup use the accent-normalized form above.
    tokens = re.findall("[a-z]+", unicodedata.normalize("NFD", text.casefold()))
    return hashlib.sha256(" ".join(tokens).encode()).hexdigest()


def tsv(path):
    with path.open(encoding="utf-8") as stream:
        rows = list(csv.reader(stream, delimiter="\t", quoting=csv.QUOTE_NONE))
    if any(len(row) != 7 for row in rows):
        raise ValueError("Unexpected FLEURS TSV schema")
    return {row[1]: row for row in rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=private_output,
                        default=ROOT / "comparison/microwakeword/real-negatives")
    parser.add_argument("--test-tsv", type=Path, default=ROOT / "comparison/public/fleurs-test.tsv")
    parser.add_argument("--test-manifest", type=Path, default=ROOT / "comparison/public/manifest.json")
    parser.add_argument("--ffmpeg", default=str(ROOT / "tools/bin/ffmpeg"))
    args = parser.parse_args()
    output = private_output(args.output)
    output.mkdir(parents=True, exist_ok=True, mode=0o700)
    if (output / "manifest.json").exists():
        parser.error("Use a new output; existing training manifest is never overwritten")
    for name, (url, digest) in INPUTS.items():
        path = output / name
        if not path.exists():
            with urllib.request.urlopen(url, timeout=60) as source, path.open("xb") as target:
                shutil.copyfileobj(source, target)
        if sha256(path) != digest:
            raise ValueError("DEV public input hash differs from pinned source")
    if sha256(args.test_tsv) != "4a7b8ae8ce07c355dcd2cf087a6486c7c3ab931d751b9a9218a9ee0cd798964b":
        raise ValueError("Unexpected test TSV metadata")
    # Only TEST metadata is read: never its WAV archive or detector outputs.
    test = tsv(args.test_tsv)
    test_text = {normalize(row[2]) for row in test.values()}
    test_hashes = {s["source_file_sha256"] for s in json.loads(args.test_manifest.read_text())["segments"]
                   if s["source"] == "fleurs-pt_br-test"}
    dev = tsv(output / "dev.tsv")
    records, excluded = [], []
    (output / "wav").mkdir(mode=0o700, exist_ok=True)
    with tarfile.open(output / "dev.tar.gz", "r:gz") as archive, tempfile.TemporaryDirectory(dir=output) as temp:
        for member in sorted(archive.getmembers(), key=lambda item: item.name):
            path = PurePosixPath(member.name)
            if path.is_absolute() or ".." in path.parts or member.issym() or member.islnk():
                raise ValueError("Unsafe DEV archive path")
            if not member.isfile() or path.suffix != ".wav":
                continue
            if len(path.parts) != 2 or path.parts[0] != "dev" or path.name not in dev:
                raise ValueError("Unexpected DEV audio or missing metadata")
            row = dev[path.name]
            text = normalize(row[2])
            wake = set(normalize(" ".join(row[2:5])).split()) & {"aegis", "aejis", "egis", "ejis"}
            if path.name in test or text in test_text or wake:
                excluded.append({"id": member.name, "reason": "TEST metadata overlap or literal wake word"})
                continue
            original = archive.extractfile(member).read()
            source_hash = hashlib.sha256(original).hexdigest()
            if source_hash in test_hashes:
                excluded.append({"id": member.name, "reason": "known TEST source hash overlap"})
                continue
            source = Path(temp) / "source.wav"
            source.write_bytes(original)
            wav_path = output / "wav" / path.name
            if not wav_path.exists():
                subprocess.run([args.ffmpeg, "-nostdin", "-v", "error", "-xerror", "-n", "-i", str(source),
                                "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le", "-map_metadata", "-1",
                                str(wav_path)], check=True, capture_output=True)
            with wave.open(str(wav_path), "rb") as wav:
                if (wav.getnchannels(), wav.getframerate(), wav.getsampwidth()) != (1, 16000, 2):
                    raise ValueError("Invalid converted DEV WAV")
                duration = wav.getnframes() / 16000
            records.append({"path": str(wav_path), "source_id": member.name, "label": "negative",
                            "group_id": frozen_group_key(row[2]),
                            "source_sha256": source_hash, "sha256": sha256(wav_path), "duration_seconds": duration})
    groups = sorted({row["group_id"] for row in records})
    random.Random(20261031).shuffle(groups)
    train_groups = set(groups[:round(len(groups) * 0.8)])
    for row in records:
        row["split"] = "train" if row["group_id"] in train_groups else "validation"
    manifest = {"purpose": "additional real negative training/validation only; no evaluation mining",
                "source": "Google FLEURS pt_br DEV", "license": "CC-BY-4.0",
                "source_url": "https://huggingface.co/datasets/google/fleurs", "seed": 20261031,
                "split_method": "80/20 by normalized-transcription group; all recordings of a phrase share one split",
                "test_dedup": "all TEST TSV filenames/transcriptions plus hashes already present for the 134 evaluation speech sources; no TEST audio opened",
                "excluded": excluded, "inputs": {name: {"url": url, "sha256": digest} for name, (url, digest) in INPUTS.items()},
                "records": records}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    print(json.dumps({split: {"files": sum(r["split"] == split for r in records),
                              "seconds": sum(r["duration_seconds"] for r in records if r["split"] == split)}
                      for split in ("train", "validation")}, indent=2))
    print(f"Excluded {len(excluded)} sources; manifest: {output / 'manifest.json'}")


if __name__ == "__main__":
    main()
