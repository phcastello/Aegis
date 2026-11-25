#!/usr/bin/env python3
"""Prepare reproducible public room-echo and locally synthesized voice fixtures.

No private captures are used or uploaded; the explicit output directory is
scratch space. FFmpeg is required for the same PCM format as the Rust replay.
"""

import argparse
import concurrent.futures
import hashlib
import json
from pathlib import Path
import subprocess
import urllib.request


REPO = Path(__file__).resolve().parents[2]
DATASET = "datasets/test_set_icassp2022/farend-singletalk"
HEADERS = {"User-Agent": "Aegis-offline-acoustic-evaluation"}


def request(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers=HEADERS), timeout=60) as response:
        data = response.read(8 * 1024 * 1024 + 1)
        if len(data) > 8 * 1024 * 1024:
            raise ValueError("unexpected oversized public fixture")
        return data


def digest(data):
    return hashlib.sha256(data).hexdigest()


def convert(source, target):
    subprocess.run([
        "ffmpeg", "-y", "-v", "error", "-i", str(source),
        "-ac", "1", "-ar", "16000", "-f", "s16le", str(target),
    ], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--split", choices=["development", "holdout", "final"], required=True)
    parser.add_argument("--near-wav", type=Path, nargs=2, required=True,
                        help="Two offline synthesized independent speech WAVs")
    parser.add_argument("--far-wav", type=Path, nargs=2, required=True,
                        help="Two different offline synthesized far-end speech WAVs")
    args = parser.parse_args()
    pcm = args.output / "pcm"
    room = args.output / "public"
    pcm.mkdir(parents=True, exist_ok=True)
    room.mkdir(parents=True, exist_ok=True)

    voices = {}
    for role, paths in [("near", args.near_wav), ("far", args.far_wav)]:
        for index, source in enumerate(paths, 1):
            name = f"{role}-{index}.pcm"
            target = pcm / name
            convert(source, target)
            voices[name] = {"wav_sha256": digest(source.read_bytes()), "pcm_sha256": digest(target.read_bytes())}
    (pcm / "manifest.json").write_text(json.dumps(voices, indent=2) + "\n")

    manifest = json.loads((REPO / "scripts/barge-in-dtd/sources.json").read_text())
    commit = manifest["sourceCommit"]
    api = f"https://api.github.com/repos/microsoft/AEC-Challenge/contents/{DATASET}?ref={commit}"
    entries = json.loads(request(api))
    excluded = set(manifest["train"] + manifest["validation"])
    candidates = {f["name"][:-8] for f in entries if f["name"].endswith("_mic.wav") and f["name"][:-8] not in excluded}
    ordered = lambda movement: sorted(
        (v for v in candidates if ("movement" in v) == movement),
        key=lambda value: digest(("aegis-windows-barge-v1:" + value).encode()),
    )
    stationary, moving = ordered(False), ordered(True)
    if args.split == "development":
        selected = list(zip(range(1, 4), stationary[:3])) + list(zip(range(10, 13), moving[:3]))
    elif args.split == "holdout":
        selected = list(zip(range(4, 10), stationary[3:9])) + list(zip(range(13, 19), moving[3:9]))
    else:
        selected = list(zip(range(19, 25), stationary[9:15])) + list(zip(range(25, 31), moving[9:15]))

    def fetch(item):
        label, public_id, role = item
        suffix = "mic" if role == "mic" else "lpb"
        target = room / f"room-{label:02}-{role}.pcm"
        wav = target.with_suffix(".wav")
        url = f"https://media.githubusercontent.com/media/microsoft/AEC-Challenge/{commit}/{DATASET}/{public_id}_{suffix}.wav"
        data = request(url)
        wav.write_bytes(data)
        try:
            convert(wav, target)
        finally:
            wav.unlink(missing_ok=True)
        return {"file": target.name, "public_id": public_id, "wav_sha256": digest(data),
                "pcm_sha256": digest(target.read_bytes())}

    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as executor:
        results = list(executor.map(fetch, [(i, stem, role) for i, stem in selected for role in ("mic", "render")]))
    (room / f"{args.split}-manifest.json").write_text(json.dumps({
        "source_commit": commit, "split": args.split,
        "selection": "sha256(aegis-windows-barge-v1:<public id>), disjoint from AGD4 train/validation",
        "files": results,
    }, indent=2) + "\n")
    print(f"Prepared {len(selected)} public recording pairs for {args.split}, with SHA-256 provenance.")


if __name__ == "__main__":
    main()
