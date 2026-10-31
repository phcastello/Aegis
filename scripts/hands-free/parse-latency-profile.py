#!/usr/bin/env python3
"""Extract only numeric, pseudonymized opt-in turn timings from API logs.

Usage: docker logs <disposable-api> 2>&1 | python3 scripts/hands-free/parse-latency-profile.py > private.csv
No unrelated log lines, UUIDs, prompts, audio, or provider payloads are copied.
"""
import argparse
import csv
import re
import sys

PATTERN = re.compile(
    r"LatencyProfile ([0-9a-fA-F-]{36}) ([a-zA-Z0-9_.]+): "
    r"elapsed=([0-9.,]+)ms duration=([0-9.,]+)ms count=([0-9]*)"
)


def rows(lines):
    samples = {}
    for line in lines:
        match = PATTERN.search(line)
        if not match:
            continue
        turn, stage, elapsed, duration, count = match.groups()
        if turn not in samples:
            samples[turn] = f"turn-{len(samples) + 1:03d}"
        yield {
            "sample": samples[turn], "stage": stage,
            "elapsed_ms": float(elapsed.replace(",", ".")),
            "duration_ms": float(duration.replace(",", ".")),
            "count": int(count) if count else "",
        }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", nargs="?", help="API log file; default stdin")
    args = parser.parse_args()
    source = open(args.input, encoding="utf-8") if args.input else sys.stdin
    try:
        writer = csv.DictWriter(sys.stdout, fieldnames=["sample", "stage", "elapsed_ms", "duration_ms", "count"])
        writer.writeheader()
        writer.writerows(rows(source))
    finally:
        if args.input:
            source.close()


if __name__ == "__main__":
    main()
