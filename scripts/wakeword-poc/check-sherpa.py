#!/usr/bin/env python3
"""Verify official public KWS examples before replaying any private recordings."""

import argparse
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import resource
import sys
import wave

from prepare import private_output, sha256


# Expected outputs from the official pretrained-model documentation.
EXPECTED = {
    "en_0.wav": ("LIGHT_UP", ["L", "AY1", "T", "AH1", "P"],
                 [2.92, 2.96, 3.04, 3.20, 3.28]),
    "en_1.wav": ("LOVELY_CHILD", ["L", "AH1", "V", "L", "IY0", "CH", "AY1", "L", "D"],
                 [5.24, 5.32, 5.40, 5.48, 5.56, 5.68, 5.84, 5.92, 5.96]),
}


def memory(stage):
    status = dict(line.split(":", 1) for line in Path("/proc/self/status").read_text().splitlines()
                  if line.startswith(("VmRSS:", "VmHWM:")))
    return {"stage": stage, "proc_status": {key: value.strip() for key, value in status.items()},
            "resource_peak_kib": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", required=True, type=Path)
    parser.add_argument("--output", required=True, type=private_output)
    args = parser.parse_args()
    os.umask(0o077)
    if args.output.exists():
        parser.error("Use a new private --output directory")
    memories = [memory("before_runtime_imports")]
    import numpy as np
    import sherpa_onnx
    from sherpa import MODEL_FILES, decode
    memories.append(memory("after_runtime_imports"))
    if sherpa_onnx.__version__ != "2.0.1":
        parser.error("This control pins sherpa-onnx 2.0.1")
    inputs = {key: args.model / name for key, name in MODEL_FILES.items()}
    inputs["keywords_file"] = args.model / "test_wavs/keywords.txt"
    parameters = {"num_threads": 1, "provider": "cpu", "sample_rate": 16000,
                  "feature_dim": 80, "max_active_paths": 4, "keywords_score": 1.0,
                  "keywords_threshold": 0.25, "num_trailing_blanks": 1}
    spotter = sherpa_onnx.KeywordSpotter(**{key: str(value) for key, value in inputs.items()},
                                      **parameters)
    memories.append(memory("after_model_load"))
    results = []
    for filename, (keyword, tokens, timestamps) in EXPECTED.items():
        inputs[filename] = path = args.model / "test_wavs" / filename
        with wave.open(str(path)) as wav:
            assert (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) == (1, 2, 16000)
            rate = wav.getframerate()
            samples = np.frombuffer(wav.readframes(wav.getnframes()), dtype="<i2").astype(np.float32) / 32768
        stream = spotter.create_stream()
        stream.accept_waveform(rate, samples)
        stream.accept_waveform(rate, np.zeros(round(0.66 * rate), dtype=np.float32))
        stream.input_finished()
        events = []
        while spotter.is_ready(stream):
            spotter.decode_stream(stream)
            result = spotter.keyword_spotter.get_result(stream)
            if result.keyword:
                events.append({"keyword": result.keyword, "tokens": list(result.tokens),
                               "timestamps": list(result.timestamps)})
                spotter.reset_stream(stream)
        # Exercise the exact 100 ms runner used by the private-file experiment.
        streaming_events = decode(spotter, samples, expected_keyword=keyword)
        for mode, detected in (("batched", events), ("streaming", streaming_events)):
            assert len(detected) == 1, (filename, mode, detected)
            assert detected[0]["keyword"] == keyword, (filename, mode, detected)
            assert detected[0]["tokens"] == tokens, (filename, mode, detected)
            assert np.allclose(detected[0]["timestamps"], timestamps, atol=0.001, rtol=0), (filename, mode, detected)
        results.append({"file": filename, "batched_events": events, "streaming_events": streaming_events})
        memories.append(memory("after_" + filename))
    report = {"status": "PASS", "python": sys.version, "platform": platform.platform(),
              "packages": {name: importlib.metadata.version(name)
                           for name in ("sherpa-onnx", "sherpa-onnx-core", "numpy")},
              "parameters": parameters, "tail_seconds": 0.66, "scores_exposed": False,
              "start_time_exposed": False, "timestamp_tolerance_seconds": 0.001,
              "expected_source": "https://k2-fsa.github.io/sherpa/onnx/kws/pretrained_models/index.html",
              "public_inputs": {key: {"name": value.name, "sha256": sha256(value)}
                                for key, value in inputs.items()}, "results": results, "memory": memories}
    args.output.mkdir(parents=True, mode=0o700)
    (args.output / "official-example.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
