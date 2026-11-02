#!/usr/bin/env python3
"""Prove detector observations preserve the frozen library's outputs on private WAVs.

Run against an immutable baseline library and this checkout's host build. No model,
PCM or transcripts are printed or written. This verifies host C API behavior only;
it is not microphone, Android device or wake accuracy validation.
"""
import argparse
import array
import ctypes
import hashlib
import json
import math
import os
from pathlib import Path
import sys
import wave


class Detector:
    def __init__(self, library, model, observations=False):
        self.lib = ctypes.CDLL(str(library))
        self.lib.aegis_mww_create.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
        self.lib.aegis_mww_create.restype = ctypes.c_void_p
        self.lib.aegis_mww_destroy.argtypes = [ctypes.c_void_p]
        self.lib.aegis_mww_reset.argtypes = [ctypes.c_void_p]
        self.lib.aegis_mww_reset.restype = ctypes.c_bool
        self.lib.aegis_mww_process.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int16), ctypes.c_size_t]
        self.lib.aegis_mww_process.restype = ctypes.c_int64
        for name in ["last_score", "max_score"] + (["event_score", "event_average"] if observations else []):
            fn = getattr(self.lib, "aegis_mww_" + name)
            fn.argtypes = [ctypes.c_void_p]
            fn.restype = ctypes.c_float
        self.handle = self.lib.aegis_mww_create(ctypes.c_char_p(model), len(model))
        assert self.handle, "model_runtime_incompatible"

    def close(self):
        self.lib.aegis_mww_destroy(self.handle)

    def reset(self):
        assert self.lib.aegis_mww_reset(self.handle), "detector_reset_failed"

    def score(self, name):
        return getattr(self.lib, "aegis_mww_" + name)(self.handle)

    def process(self, pcm):
        return self.lib.aegis_mww_process(self.handle, pcm, len(pcm))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-library", type=Path, required=True)
    parser.add_argument("--candidate-library", type=Path, required=True)
    parser.add_argument("--model", type=Path, default=Path.home()/".local/share/aegis/private/hands-free-model-20261031/aegis.int8.tflite")
    parser.add_argument("--wav-directory", type=Path, default=Path.home()/".cache/aegis-wakeword-poc/data/wav")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    model = args.model.read_bytes()
    manifest = json.loads(Path(__file__).with_name("model.json").read_text())
    assert len(model) == manifest["bytes"] and hashlib.sha256(model).hexdigest() == manifest["sha256"], "unexpected_model"
    baseline = Detector(args.baseline_library, model)
    candidate = Detector(args.candidate_library, model, observations=True)
    files = sorted(args.wav_directory.glob("*.wav"))
    assert files, "no_wav_fixtures"
    samples = events = comparisons = 0
    try:
        for path in files:
            with wave.open(str(path), "rb") as wav:
                assert (wav.getnchannels(), wav.getframerate(), wav.getsampwidth()) == (1, 16000, 2)
                raw = array.array("h", wav.readframes(wav.getnframes()))
                if sys.byteorder != "little":
                    raw.byteswap()
            raw.extend([0] * 8000)
            samples += len(raw)
            expected_events = None
            for chunks in [(160,), (3200,), (17, 257, 89, 480, 3200)]:
                baseline.reset(); candidate.reset()
                assert math.isnan(candidate.score("event_score")) and math.isnan(candidate.score("event_average"))
                offset = index = 0
                observed = []
                while offset < len(raw):
                    take = min(chunks[index % len(chunks)], len(raw)-offset)
                    pcm = (ctypes.c_int16 * take).from_buffer_copy(raw[offset:offset+take])
                    before = baseline.process(pcm)
                    after = candidate.process(pcm)
                    assert before == after and after != -2, "event_regression"
                    assert baseline.score("last_score") == candidate.score("last_score"), "instantaneous_score_regression"
                    assert baseline.score("max_score") == candidate.score("max_score"), "aggregate_score_regression"
                    instantaneous, average = candidate.score("event_score"), candidate.score("event_average")
                    if after >= 0:
                        assert offset <= after <= offset+take and after % 160 == 0, "invalid_event_boundary"
                        assert math.isfinite(instantaneous) and math.isfinite(average), "missing_event_scores"
                        observed.append((after, instantaneous, average))
                    else:
                        assert math.isnan(instantaneous) and math.isnan(average), "stale_event_scores"
                    comparisons += 1
                    offset += take; index += 1
                if expected_events is None:
                    expected_events = observed
                    events += len(observed)
                else:
                    assert expected_events == observed, "event_observations_depend_on_batching"
        assert events > 0, "no_activation_exercised"
    finally:
        baseline.close(); candidate.close()
    report = {"schema": 1, "passed": True, "platform": "host native C API; no physical Android validation",
              "modelSha256": manifest["sha256"], "baselineLibrarySha256": hashlib.sha256(args.baseline_library.read_bytes()).hexdigest(),
              "candidateLibrarySha256": hashlib.sha256(args.candidate_library.read_bytes()).hexdigest(),
              "fixtures": len(files), "samples": samples, "activations": events, "processComparisons": comparisons,
              "chunkPatterns": [[160], [3200], [17, 257, 89, 480, 3200]],
              "eventsAndScoresExactlyEqual": True, "eventScoresIndependentOfBatching": True}
    with args.output.open("x") as output:
        json.dump(report, output, indent=2); output.write("\n")
    print("Frozen detector observations passed; private report: " + str(args.output))


if __name__ == "__main__":
    main()
