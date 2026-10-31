#!/usr/bin/env python3
"""Build evaluation-only public negatives; never accesses the owner's recordings."""

import argparse
import csv
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tarfile
import tempfile
import unicodedata
import urllib.request
import wave
import zipfile

from prepare import private_output, sha256


FILES = {
    "fleurs-pt_br-test.tar.gz": (
        "https://huggingface.co/datasets/google/fleurs/resolve/main/data/pt_br/audio/test.tar.gz",
        "968d1b68a90132ca1f044f664866418b0db0351a93601a671d5637016da4f49b"),
    "fleurs-test.tsv": (
        "https://huggingface.co/datasets/google/fleurs/resolve/main/data/pt_br/test.tsv",
        "4a7b8ae8ce07c355dcd2cf087a6486c7c3ab931d751b9a9218a9ee0cd798964b"),
    "demand-living.zip": (
        "https://zenodo.org/records/1227121/files/DLIVING_16k.zip",
        "2b1726fe06e41551ce2397f2aaf3e4fb692c912d81914b04708df0bbb5252338"),
}


def download_inputs(cache):
    for name, (url, digest) in FILES.items():
        path = cache / name
        if not path.exists():
            with tempfile.NamedTemporaryFile(dir=cache, delete=False) as output:
                temporary = Path(output.name)
                try:
                    with urllib.request.urlopen(url, timeout=60) as response:
                        shutil.copyfileobj(response, output)
                    output.flush()
                    if sha256(temporary) != digest:
                        raise ValueError(f"Public download hash mismatch: {name}")
                    temporary.replace(path)
                finally:
                    temporary.unlink(missing_ok=True)
        if sha256(path) != digest:
            raise ValueError(f"Public input hash mismatch: {name}")


def normalized(text):
    return "".join(c for c in unicodedata.normalize("NFD", text.casefold()) if c.isalpha())


def convert(ffmpeg, original, converted):
    subprocess.run([ffmpeg, "-nostdin", "-v", "error", "-xerror", "-n", "-i", str(original),
                    "-map", "0:a:0", "-ac", "1", "-ar", "16000", "-c:a", "pcm_s16le",
                    "-map_metadata", "-1", str(converted)], check=True, capture_output=True)


def append_segment(writer, wav_path, limit, offset, source, identifier, original_bytes):
    with wave.open(str(wav_path), "rb") as audio:
        if (audio.getnchannels(), audio.getframerate(), audio.getsampwidth()) != (1, 16000, 2):
            raise ValueError("Unexpected public converted format")
        available = audio.getnframes()
        used = min(available, limit)
        pcm = audio.readframes(used)
    writer.writeframesraw(pcm)
    return used, {"source": source, "file_id": identifier,
                  "source_file_sha256": hashlib.sha256(original_bytes).hexdigest(),
                  "offset_samples": offset, "offset_seconds": offset / 16000,
                  "samples": used, "duration_seconds": used / 16000,
                  "source_duration_seconds": available / 16000, "truncated": used < available}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cache", type=private_output,
                        default=Path.home() / ".cache/aegis-wakeword-poc/comparison/public")
    parser.add_argument("--ffmpeg", default=str(Path.home() / ".cache/aegis-wakeword-poc/tools/bin/ffmpeg"))
    args = parser.parse_args()
    cache = private_output(args.cache)
    cache.mkdir(parents=True, exist_ok=True)
    download_inputs(cache)
    output = cache / "negatives.wav"
    manifest_path = cache / "manifest.json"
    if output.exists():
        if manifest_path.exists() and json.loads(manifest_path.read_text())["output_sha256"] == sha256(output):
            print(f"Existing public corpus verified: {output}")
            return
        parser.error("Existing negatives.wav has no matching manifest; use a new cache")
    with (cache / "fleurs-test.tsv").open(encoding="utf-8") as stream:
        # FLEURS is raw TSV: quotation marks belong to the transcription.
        rows = list(csv.reader(stream, delimiter="\t", quoting=csv.QUOTE_NONE))
    if any(len(row) != 7 for row in rows):
        raise ValueError("Unexpected FLEURS TSV schema")
    transcripts = {row[1]: normalized(" ".join(row[2:5])) for row in rows}
    excluded = sorted(name for name, text in transcripts.items()
                      if any(keyword in text for keyword in ("aegis", "aejis")))
    segments, offset = [], 0
    with tempfile.TemporaryDirectory(prefix=".public-convert-", dir=cache) as temporary:
        temp = Path(temporary)
        target = temp / "negatives.wav"
        with wave.open(str(target), "wb") as writer:
            writer.setparams((1, 2, 16000, 0, "NONE", "not compressed"))
            with tarfile.open(cache / "fleurs-pt_br-test.tar.gz", "r:gz") as archive:
                members = {}
                for member in archive.getmembers():
                    path = PurePosixPath(member.name)
                    if path.is_absolute() or ".." in path.parts or member.issym() or member.islnk():
                        raise ValueError("Unsafe FLEURS archive member")
                    if member.isfile() and path.suffix == ".wav":
                        if path.parts[0] != "test" or path.name in members:
                            raise ValueError("Unexpected or duplicate FLEURS WAV")
                        members[path.name] = member
                for name in sorted(members):
                    if offset >= 1800 * 16000:
                        break
                    if name not in transcripts:
                        raise ValueError("FLEURS WAV missing transcript")
                    if name in excluded:
                        continue
                    original = archive.extractfile(members[name]).read()
                    source = temp / "source.wav"
                    source.write_bytes(original)
                    converted = temp / "converted.wav"
                    convert(args.ffmpeg, source, converted)
                    used, segment = append_segment(writer, converted, 1800 * 16000 - offset,
                                                   offset, "fleurs-pt_br-test", "test/" + name, original)
                    converted.unlink()
                    segments.append(segment)
                    offset += used
            if offset != 1800 * 16000:
                raise ValueError("Not enough eligible FLEURS speech for 1800 seconds")
            with zipfile.ZipFile(cache / "demand-living.zip") as archive:
                original = archive.read("DLIVING/ch01.wav")
            source = temp / "source.wav"
            source.write_bytes(original)
            converted = temp / "converted.wav"
            convert(args.ffmpeg, source, converted)
            used, segment = append_segment(writer, converted, 300 * 16000, offset,
                                           "DEMAND-DLIVING", "DLIVING/ch01.wav", original)
            if used != 300 * 16000:
                raise ValueError("DEMAND source is shorter than 300 seconds")
            segments.append(segment)
            offset += used
        target.replace(output)
    manifest = {
        "purpose": "evaluation negatives only; never use this corpus for training or tuning",
        "format": "PCM signed 16-bit mono 16000 Hz", "duration_seconds": offset / 16000,
        "speech_seconds": 1800, "environment_seconds": 300,
        "selection": "FLEURS test WAVs sorted lexicographically; stop at exactly 1800 seconds, then DEMAND channel 1",
        "excluded_transcript_file_ids": excluded,
        "keyword_filter": "casefold + NFD + alphabetic characters; exclude aegis/aejis in all transcript columns",
        "sources": {
            "fleurs-pt_br-test": {"license": "CC-BY-4.0", "attribution": "Google FLEURS dataset contributors",
                                   "url": "https://huggingface.co/datasets/google/fleurs"},
            "DEMAND-DLIVING": {"license": "CC-BY-SA-3.0", "attribution": "Joachim Thiemann, Nobutaka Ito, Emmanuel Vincent",
                                "url": "https://zenodo.org/records/1227121"}},
        "inputs": {name: {"url": url, "sha256": digest} for name, (url, digest) in FILES.items()},
        "ffmpeg_version": subprocess.check_output([args.ffmpeg, "-version"], text=True).splitlines()[0],
        "ffmpeg_sha256": sha256(Path(shutil.which(args.ffmpeg) or args.ffmpeg).resolve()),
        "output_sha256": sha256(output), "segments": segments,
        "limitations": ["Read speech and one living-room/TV channel; no separate music corpus.",
                        "Transcript filtering does not substitute auditory annotation; DEMAND TV has no transcript.",
                        "Concatenation has artificial boundaries and is not an uncontrolled day-long background session.",
                        "Public-only corpus: contains none of the owner's recordings."]}
    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Prepared {offset / 16000:.3f} seconds: 1800 speech + 300 environment; {len(segments)} source segments")
    print(f"Transcript exclusions: {len(excluded)}; output: {output}")


if __name__ == "__main__":
    main()
