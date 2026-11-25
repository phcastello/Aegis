#!/usr/bin/env python3
"""Compare an on-device replay CSV with the accepted frozen PoC on the same WAV.

The CSV is produced by replay-native.cpp (same core used by JNI). Run on the
private host with the existing PoC environment. No audio is uploaded or printed.
"""
import argparse
import csv
import json
import os
from pathlib import Path
import sys
import numpy as np

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "scripts/wakeword-poc"))
from compare_microwakeword import MicroWakeWordDetector
from prepare import private_output, sha256
# Import the common reference feed without renaming the executable script.
import importlib.util
spec = importlib.util.spec_from_file_location("aegis_parity", Path(__file__).with_name("replay-parity.py"))
parity = importlib.util.module_from_spec(spec)
spec.loader.exec_module(parity)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--csv", type=Path, required=True)
    parser.add_argument("--wav", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--reference-kernels", action="store_true", help="Diagnostic BUILTIN_REF comparison, preserving accepted result separately")
    parser.add_argument("--output", type=private_output, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    if sha256(args.model) != parity.MANIFEST["sha256"]:
        raise ValueError("Model differs from frozen DEV artifact")
    audio = parity.padded(parity.read_wav(args.wav), .5)
    reference = MicroWakeWordDetector(model_path=args.model)
    if args.reference_kernels:
        from ai_edge_litert.interpreter import Interpreter, OpResolverType
        reference._interpreter_class = lambda **kw: Interpreter(**kw, experimental_op_resolver_type=OpResolverType.BUILTIN_REF)
        reference.reset()
    events, scores = parity.feed(reference, audio)
    with args.csv.open() as stream:
        rows = list(csv.DictReader(stream))
    positions = [int(r["fed_samples"]) for r in rows]
    if positions != list(range(160, len(audio)+1, 160)):
        raise ValueError("CSV missing samples or does not use expected 0.5s tail/final padding")
    native_scores = np.asarray([float(r["last_score"]) for r in rows])
    native_events = [int(r["event_sample"]) for r in rows if int(r["event_sample"]) >= 0]
    delta = float(np.abs(scores-native_scores).max(initial=0))
    event_delta = max((abs(a-b) for a,b in zip(events,native_events)), default=0)
    passed = delta <= 1/256+1e-9 and len(events)==len(native_events) and event_delta<=480
    result = {"schema":1, "model_sha256":sha256(args.model), "wav_sha256":sha256(args.wav),
              "csv_sha256":sha256(args.csv), "reference_kernel_mode":"diagnostic BUILTIN_REF" if args.reference_kernels else "accepted default LiteRT delegates", "samples":len(audio), "max_score_delta":delta,
              "max_event_delta_samples":event_delta, "reference_events_samples":events,
              "native_events_samples":native_events, "passed":passed,
              "architecture_note":"CSV provenance must record physical device, ABI, build/source SHA and library hash; this script cannot infer them"}
    with args.output.open('x') as stream:
        stream.write(json.dumps(result, indent=2)+"\n")
    print(json.dumps({k:result[k] for k in ("passed","samples","max_score_delta","max_event_delta_samples")},indent=2))
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())
