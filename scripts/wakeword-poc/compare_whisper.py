"""CPU whisper.cpp Tiny Q5_1 gated by streaming Silero VAD, without paid STT."""
from collections import deque
import importlib.metadata
from pathlib import Path
import re
import unicodedata

import numpy as np
import onnxruntime as ort
from pywhispercpp.model import Model

ROOT = Path.home() / ".cache/aegis-wakeword-poc/comparison"


class WhisperTinyDetector:
    block_samples = 512

    def __init__(self, config="default"):
        if config not in ("default", "prompt"):
            raise ValueError(config)
        self.config = config
        self.files = [ROOT / "ggml-tiny-q5_1.bin", ROOT / "silero_vad.onnx"]
        options = ort.SessionOptions()
        options.intra_op_num_threads = options.inter_op_num_threads = 1
        self.vad = ort.InferenceSession(str(self.files[1]), sess_options=options,
                                        providers=["CPUExecutionProvider"])
        self.model = Model(str(self.files[0]), redirect_whispercpp_logs_to=None,
                           n_threads=1, language="pt", translate=False,
                           print_realtime=False, print_progress=False,
                           print_timestamps=False, no_context=True,
                           single_segment=True, temperature=0.0,
                           temperature_inc=0.0, max_tokens=16, audio_ctx=256)
        self.reset()

    def reset(self):
        self.clock = 0
        self.state = np.zeros((2, 1, 128), dtype=np.float32)
        self.context = np.zeros((1, 64), dtype=np.float32)
        self.buffer = deque(maxlen=100)  # 3.2 seconds, including pre-roll
        self.speech_start = None
        self.last_voice = -100000
        self.last_decode = -100000
        self.latched = False
        self.raw_transcripts = []  # private only; never included in metadata/CSV

    def _recognize(self):
        audio = np.concatenate(self.buffer)
        segments = self.model.transcribe(audio, initial_prompt="Nome próprio: Aegis." if self.config == "prompt" else "")
        text = " ".join(segment.text for segment in segments)
        self.raw_transcripts.append({"time": self.clock / 16000, "text": text})
        normalized = "".join(c for c in unicodedata.normalize("NFD", text.lower()) if unicodedata.category(c) != "Mn")
        tokens = re.findall(r"[a-z]+", normalized)
        # Exact original spelling and the owner's supplied pronunciation spelling.
        # No fuzzy match to egide/edson/etc.; only the first two lexical tokens.
        if any(token in ("aegis", "aejis") for token in tokens[:2]):
            self.latched = True
            return [{"keyword": "Aegis", "timestamp": self.clock / 16000}]
        return []

    def process(self, pcm):
        audio = pcm.astype(np.float32) / 32768
        self.clock += len(audio)
        self.buffer.append(audio)
        inputs = np.concatenate((self.context, audio.reshape(1, -1)), axis=1)
        probability, self.state = self.vad.run(None, {"input": inputs, "state": self.state,
                                                    "sr": np.array(16000, dtype=np.int64)})
        self.context = inputs[:, -64:]
        if float(probability[0, 0]) >= 0.5:
            self.last_voice = self.clock
            if self.speech_start is None:
                self.speech_start = max(0, self.clock - 4000)
        elif self.clock - self.last_voice > 8000:
            self.speech_start = None
            self.latched = False
            # Retain only 256 ms pre-roll after an utterance.
            while len(self.buffer) > 8:
                self.buffer.popleft()
        if (self.speech_start is not None and not self.latched
                and self.clock - self.speech_start >= 16000
                and self.clock - self.last_decode >= 8000):
            self.last_decode = self.clock
            return self._recognize()
        return []

    def finish(self):
        # No whole-file transcription fallback: quality comes from live prefixes.
        return []

    def metadata(self):
        return {"implementation": "whisper.cpp via pywhispercpp", "model": "tiny multilingual Q5_1",
                "versions": {p: importlib.metadata.version(p) for p in ("pywhispercpp", "onnxruntime", "numpy")},
                "model_bytes": sum(path.stat().st_size for path in self.files),
                "licenses": {"whisper.cpp/model": "MIT", "Silero VAD": "MIT"},
                "config": self.config, "threads": 1, "uses_reference_audio": False,
                "vad_threshold": 0.5, "buffer_seconds": 3.2, "first_prefix_seconds": 1,
                "recognition_interval_seconds": 0.5,
                "max_tokens": 16, "audio_ctx": 256, "temperature_fallback": False,
                "matching": "exact accent-normalized aegis or aejis among first two tokens",
                "continuous_instance": True, "android": "native whisper.cpp and ONNX Runtime available; not validated",
                "windows": "native whisper.cpp and ONNX Runtime available; not validated"}
