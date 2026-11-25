#!/usr/bin/env python3
"""Additional finalist check: one stream after 30 s public speech, no file resets."""
import argparse
import csv
import json
import os
from pathlib import Path

import numpy as np

from comparison import ROOT, make_detector, read_wav, replay, summarize
from prepare import private_output
from run import load_data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", choices=("raven", "openwakeword", "microwakeword-realneg"), required=True)
    parser.add_argument("--config", default="default")
    parser.add_argument("--output", type=private_output, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    args.output.mkdir(parents=True, exist_ok=False, mode=0o700)
    data, inventory = load_data(ROOT / "data")
    selection = json.loads(Path(__file__).with_name("selection.json").read_text())
    refs = {r["id"] for r in selection["references"]}
    detector = make_detector(args.candidate, args.config)
    detector.reset()
    import wave
    with wave.open(str(ROOT / "comparison/public/negatives.wav"), "rb") as wav:
        prefix = np.frombuffer(wav.readframes(30 * 16000), dtype="<i2").copy()
    prefix_events, _ = replay(detector, prefix)
    rows = []
    for record in inventory["records"]:
        if record["id"] in refs:
            continue
        audio = read_wav(data / record["wav_path"])
        # Three seconds separate recorded trials; decoder/frontend stay alive.
        # The local timestamp remains relative to the current original recording.
        events, _ = replay(detector, audio, tail_seconds=3, finish=False)
        rows.append({"candidate": args.candidate, "config": args.config, "id": record["id"],
                     "device": record["device"], "label": record["label"],
                     "group": "independent" if record["label"] == "positive" else "negative",
                     "form": selection["forms"].get(record["id"], "negative"),
                     "detected": bool(events), "activations": len(events),
                     "timestamps": ";".join(str(e["detected_at"]) for e in events),
                     "first_before_audio_end": events[0]["detected_at"] < len(audio) / 16000 if events else ""})
    with (args.output / "results.csv").open("w", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]), lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)
    report = {"candidate": args.candidate, "config": args.config, "prefix_public_seconds": 30,
              "prefix_events": len(prefix_events), "between_trials_silence_seconds": 3,
              "stream_resets_after_prefix": 0, "quality": summarize(rows),
              "note": "Reused evaluation recordings; additional continuous-state check, not new independent data"}
    (args.output / "summary.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
