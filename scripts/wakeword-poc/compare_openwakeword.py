"""Custom openWakeWord ONNX head; CPU-only recorded-file comparison adapter."""

import importlib.metadata
import json
import os
from pathlib import Path

import numpy as np
from openwakeword.model import Model

from prepare import sha256


class OpenWakeWordDetector:
    block_samples = 1280

    def __init__(self, config="default"):
        thresholds = {"default": .5, "strict": .7}
        if config not in thresholds:
            raise ValueError(f"Unknown openWakeWord config: {config}")
        self.config, self.threshold = config, thresholds[config]
        self.root = Path(os.environ.get("AEGIS_OPENWAKEWORD_ROOT",
                                        Path.home() / ".cache/aegis-wakeword-poc/comparison/openwakeword"))
        self.paths = {"classifier": self.root / "trained/aegis.onnx",
                      "embedding": self.root / "models/embedding_model.onnx",
                      "melspectrogram": self.root / "models/melspectrogram.onnx"}
        np.random.seed(20261031)
        self.model = Model(wakeword_models=[str(self.paths["classifier"])], inference_framework="onnx",
                           melspec_model_path=str(self.paths["melspectrogram"]),
                           embedding_model_path=str(self.paths["embedding"]), ncpu=1)
        self.reset()

    def reset(self):
        # The upstream preprocessor initializes history with random low-level audio.
        # Use the same seed as extraction, preserving loaded ONNX sessions.
        np.random.seed(20261031)
        self.model.reset()
        self.frames, self.below = 0, 0
        self.latched = False
        self.last_event = -1000.0
        self.max_score = self.current_score = 0.0

    def process(self, pcm):
        if pcm.dtype != np.int16 or pcm.ndim != 1 or len(pcm) != self.block_samples:
            raise ValueError("Expected one 80 ms int16 mono block")
        score = float(self.model.predict(pcm)["aegis"])
        self.frames += 1
        timestamp = self.frames * .08
        self.current_score = score
        self.max_score = max(self.max_score, score)
        self.below = self.below + 1 if score < self.threshold * .5 else 0
        if self.below >= 2:
            self.latched = False
        if score >= self.threshold and not self.latched and timestamp - self.last_event >= 1.0:
            self.latched, self.last_event = True, timestamp
            return [{"keyword": "Aegis", "timestamp": round(timestamp, 6), "score": score}]
        return []

    def finish(self):
        # The common replay runner already feeds its documented 0.5 s file tail.
        return []

    def metadata(self):
        training = json.loads((self.root / "trained/training.json").read_text())
        return {"candidate": "openWakeWord custom Aegis", "config": self.config,
                "openwakeword_source_commit": "368c03716d1e92591906a84949bc477f3a834455",
                "official_feature_models_release": "v0.5.1",
                "threshold": self.threshold, "provider": "CPUExecutionProvider", "threads": 1,
                "block_samples": 1280, "uses_reference_audio": True,
                "adaptation_reference_ids": training["adaptation_reference_ids"],
                "training_architecture": training["architecture"],
                "training_selection": training["checkpoint_rule"],
                "training_manifest_sha256": sha256(self.root / "trained/training.json"),
                "models": {name: {"bytes": path.stat().st_size, "sha256": sha256(path)}
                           for name, path in self.paths.items()},
                "packages": {name: importlib.metadata.version(name)
                             for name in ("openwakeword", "onnxruntime", "numpy", "scikit-learn")},
                "licenses": {"openWakeWord code": "Apache-2.0", "Google speech embedding": "Apache-2.0 per upstream architecture documentation",
                             "Piper runtime (training only)": "GPL-3.0", "Piper voice datasets": "CC0 per individual cards"},
                "event_rule": "First score >= threshold, rearm after two blocks below threshold/2, minimum 1 second between events",
                "timestamp_meaning": "Processed block end; no word-boundary estimate", "extra_finish_padding_seconds": 0}
