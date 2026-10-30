#!/usr/bin/env python3
"""Prepare the owner's private ZIP locally; never writes voice data into Git."""

import argparse
import csv
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import stat
import subprocess
import unicodedata
import wave
import zipfile


GROUPS = {
    ("Chamadas_Android", "Normais"): ("android", "positive", 13),
    ("Chamadas_Android", "Teste_falsos_positivos"): ("android", "negative", 4),
    ("Chamadas_Computador", "Normais"): ("windows", "positive", 16),
    ("Chamadas_Computador", "Teste_falsos_positivos"): ("windows", "negative", 4),
}


def private_output(path):
    """Resolve symlinks and refuse any destination under a Git checkout."""
    path = Path(path).expanduser().resolve()
    if any((parent / ".git").exists() for parent in (path, *path.parents)):
        raise ValueError("Private output must be outside every Git checkout")
    return path


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_archive(archive):
    seen = set()
    files = []
    counts = dict.fromkeys(GROUPS, 0)
    for entry in archive.infolist():
        raw = entry.filename
        parts = raw.rstrip("/").split("/")
        mode = entry.external_attr >> 16
        key = unicodedata.normalize("NFC", "/".join(parts)).casefold()
        if (not raw or raw.startswith("/") or "\\" in raw
                or any(part in ("", ".", "..") or ":" in part for part in parts)
                or parts[0] != "aegis-wakeword-poc" or key in seen
                or stat.S_ISLNK(mode)
                or (stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR))):
            raise ValueError(f"Unsafe or duplicate ZIP entry: {raw!r}")
        seen.add(key)
        if entry.is_dir():
            continue
        if (len(parts) != 4 or tuple(parts[1:3]) not in GROUPS
                or PurePosixPath(raw).suffix.lower() not in (".mp4", ".m4a")):
            raise ValueError(f"Unexpected dataset entry: {raw!r}")
        counts[tuple(parts[1:3])] += 1
        files.append(entry)
    for group, (_, _, expected) in GROUPS.items():
        if counts[group] != expected:
            raise ValueError(f"Expected {expected} files in {group}, got {counts[group]}")
    bad = archive.testzip()
    if bad:
        raise ValueError(f"ZIP CRC error: {bad!r}")
    return sorted(files, key=lambda entry: entry.filename)


def run(command):
    return subprocess.run(command, check=True, capture_output=True, text=True).stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--zip", required=True, type=Path)
    parser.add_argument("--output", required=True, type=private_output)
    parser.add_argument("--ffmpeg", default="ffmpeg")
    parser.add_argument("--ffprobe", default="ffprobe")
    args = parser.parse_args()
    os.umask(0o077)
    if args.output.exists():
        parser.error("--output must be a new directory (originals are never overwritten)")
    versions = {
        tool: run([getattr(args, tool), "-version"]).splitlines()[0]
        for tool in ("ffmpeg", "ffprobe")
    }
    rows = []
    counters = dict.fromkeys(GROUPS, 0)
    with zipfile.ZipFile(args.zip) as archive:
        entries = validate_archive(archive)
        args.output.mkdir(parents=True, mode=0o700)
        (args.output / "wav").mkdir(mode=0o700)
        for entry in entries:
            relative = PurePosixPath(entry.filename)
            group = tuple(relative.parts[1:3])
            device, label, _ = GROUPS[group]
            counters[group] += 1
            identifier = f"{device}-{label}-{counters[group]:02d}"
            original = args.output / "originals" / relative
            original.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
            with original.open("xb") as stream:
                stream.write(archive.read(entry))
            probe = json.loads(run([
                args.ffprobe, "-v", "error", "-select_streams", "a:0",
                "-show_streams", "-show_format", "-of", "json", str(original),
            ]))
            streams = probe.get("streams", [])
            if len(streams) != 1:
                raise ValueError(f"Missing audio stream: {entry.filename}")
            source = streams[0]
            converted = args.output / "wav" / f"{identifier}.wav"
            # Decode the entire stream, report corrupt packets, downmix stereo;
            # no silence trimming, gain normalization, or test-specific edits.
            command = [
                args.ffmpeg, "-nostdin", "-hide_banner", "-v", "error", "-xerror",
                "-n", "-i", str(original), "-map", "0:a:0", "-vn",
                "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
                "-map_metadata", "-1", str(converted),
            ]
            run(command)
            with wave.open(str(converted), "rb") as wav:
                if (wav.getnchannels(), wav.getframerate(), wav.getsampwidth()) != (1, 16000, 2):
                    raise ValueError(f"Unexpected converted WAV format: {identifier}")
                frames = wav.getnframes()
            rows.append({
                "id": identifier, "original": entry.filename,
                "device": device, "label": label,
                "original_path": str(original.relative_to(args.output)),
                "wav_path": str(converted.relative_to(args.output)),
                "codec": source["codec_name"], "sample_rate": int(source["sample_rate"]),
                "channels": source["channels"],
                "duration_seconds": float(source.get("duration", probe["format"]["duration"])),
                "wav_duration_seconds": frames / 16000,
                "original_sha256": sha256(original), "wav_sha256": sha256(converted),
            })
    inventory = {
        "zip_sha256": sha256(args.zip), "tools": versions,
        "conversion": "FFmpeg -xerror -map 0:a:0 -vn -ac 1 -ar 16000 -c:a pcm_s16le -map_metadata -1",
        "records": rows,
    }
    (args.output / "inventory.json").write_text(
        json.dumps(inventory, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    with (args.output / "inventory.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)
    print(f"Validated and converted {len(rows)} recordings locally into {args.output}")
    print("Private originals, WAVs, hashes and inventory must remain outside Git.")


if __name__ == "__main__":
    main()
