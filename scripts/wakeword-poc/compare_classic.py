#!/usr/bin/env python3
"""Local Raven/Vosk adapters for the recorded-audio comparison; no networking."""

import importlib.metadata
import json
import os
from pathlib import Path
import wave

import numpy as np


CACHE = Path(os.environ.get("AEGIS_WAKEWORD_POC_CACHE", Path.home() / ".cache/aegis-wakeword-poc"))
CLASSIC = CACHE / "comparison/classic"
RAVEN_COMMIT = "9847924ff3ef92b3c497769e870354ddf8d6da1c"


class RavenDetector:
    """Official MFCC/DTW implementation with the six previously frozen crops."""

    block_samples = 480  # Native WebRTC VAD frame: 30 ms, 16 kHz.

    def __init__(self, config="default"):
        from rhasspywake_raven import Raven

        if config not in ("default", "strict", "consensus"):
            raise ValueError("Raven config must be default, strict or consensus")
        self.config = config
        self.threshold = 0.6 if config == "strict" else 0.5
        self.minimum_matches = 2 if config == "consensus" else 1
        selection = json.loads(Path(__file__).with_name("selection.json").read_text())
        references = CACHE / "experiment/references"
        self.reference_ids = [r["id"] for r in selection["references"]]
        if len(self.reference_ids) != 6 or len(set(self.reference_ids)) != 6:
            raise ValueError("Expected the six frozen references")
        self.templates = []
        for identifier in self.reference_ids:
            path = references / f"{identifier}.wav"
            with wave.open(str(path), "rb") as wav:
                if (wav.getnchannels(), wav.getframerate(), wav.getsampwidth()) != (1, 16000, 2):
                    raise ValueError("Unexpected reference format")
            self.templates.append(Raven.wav_to_template(path, name=identifier))
        self.reset()

    def reset(self):
        from rhasspysilence import WebRtcVadRecorder
        from rhasspywake_raven import Raven

        # Recreate only stream state/VAD/DTW buffers; MFCC templates stay loaded.
        recorder = WebRtcVadRecorder(vad_mode=1, min_seconds=0.5, before_seconds=1)
        self.detector = Raven(self.templates, keyword_name="Aegis", recorder=recorder,
                              probability_threshold=self.threshold, minimum_matches=self.minimum_matches,
                              distance_threshold=0.22, refractory_sec=2.0)
        self._pending = b""
        self._samples = 0

    def process(self, pcm_int16_numpy):
        self._pending += np.asarray(pcm_int16_numpy, dtype="<i2").tobytes()
        events = []
        while len(self._pending) >= self.block_samples * 2:
            chunk = self._pending[:self.block_samples * 2]
            self._pending = self._pending[self.block_samples * 2:]
            self._samples += self.block_samples
            matches = self.detector.process_chunk(chunk)
            if matches:
                # Multiple matched templates are one activation, not six hits.
                probabilities = [self.detector.last_probabilities[i] for i in matches]
                events.append({"keyword": "Aegis", "time_seconds": self._samples / 16000,
                               "score": float(max(p for p in probabilities if p is not None)),
                               "matched_templates": len(matches)})
        return events

    def finish(self):
        # Complete only the pending native frame; the common runner owns EOF tail.
        if self._pending:
            missing = self.block_samples - len(self._pending) // 2
            return self.process(np.zeros(missing, dtype=np.int16))
        return []

    def metadata(self):
        return {"engine": "raven", "version": importlib.metadata.version("rhasspy-wake-raven"),
                "source_commit": RAVEN_COMMIT, "license": "MIT",
                "model_bytes": sum((CACHE / "experiment/references" / f"{identifier}.wav").stat().st_size
                                   for identifier in self.reference_ids),
                "mfcc_template_bytes_in_memory": sum(t.mfcc.nbytes for t in self.templates),
                "model_description": "six private frozen MFCC templates; no network training",
                "reference_count": 6, "uses_reference_audio": True, "config": self.config,
                "probability_threshold": self.threshold, "distance_threshold": 0.22,
                "minimum_matches": self.minimum_matches, "refractory_seconds": 2.0, "vad_mode": 1,
                "block_samples": self.block_samples, "file_flush": "complete pending 30 ms frame only",
                "timestamp_semantics": "end of consumed PCM frame, not word alignment",
                "score_semantics": "native distance-to-probability transform, not calibrated confidence",
                "runtime": {p: importlib.metadata.version(p) for p in
                            ("numpy", "scipy", "python-speech-features", "rhasspy-silence", "webrtcvad-wheels")}}


def probe_vosk(model):
    """Exact spellings only. A different Portuguese word is not a wake-word alias."""
    return {word: model.vosk_model_find_word(word)
            for word in ("aegis", "aéjis", "aejis", "égis", "[unk]")}


class VoskDetector:
    """Restrict vocabulary only when the actual name exists, retaining [unk]."""

    block_samples = 1600

    def __init__(self, config="default"):
        import vosk

        if config != "default":
            raise ValueError("Vosk has only one predeclared configuration")
        self.config = config
        self.model_path = CLASSIC / "vosk-model-small-pt-0.3"
        self.model = vosk.Model(str(self.model_path))
        self.vocabulary = probe_vosk(self.model)
        self.words = [word for word, identifier in self.vocabulary.items()
                      if word != "[unk]" and identifier >= 0]
        if not self.words or self.vocabulary["[unk]"] < 0:
            raise RuntimeError("Vosk Portuguese model cannot represent Aegis/Aéjis: OOV. "
                               "Refusing forced grammar or unrelated aliases; see native vocabulary probe.")
        self.reset()

    def reset(self):
        import vosk

        self.recognizer = vosk.KaldiRecognizer(self.model, 16000,
                                               json.dumps([*self.words, "[unk]"], ensure_ascii=False))
        self.recognizer.SetWords(True)
        self.recognizer.SetPartialWords(True)
        self._emitted = set()

    def _events(self, response):
        events = []
        for result in response.get("result", response.get("partial_result", [])):
            word = result.get("word", "").casefold()
            key = (word, round(result["start"], 2))
            if word in self.words and key not in self._emitted:
                self._emitted.add(key)
                event = {"keyword": "Aegis", "time_seconds": result["end"],
                         "word_start_seconds": result["start"]}
                if "conf" in result:
                    event["score"] = result["conf"]
                events.append(event)
        return events

    def process(self, pcm_int16_numpy):
        audio = np.asarray(pcm_int16_numpy, dtype="<i2").tobytes()
        final = self.recognizer.AcceptWaveform(audio)
        return self._events(json.loads(self.recognizer.Result() if final else self.recognizer.PartialResult()))

    def finish(self):
        return self._events(json.loads(self.recognizer.FinalResult()))

    def metadata(self):
        return {"engine": "vosk", "version": importlib.metadata.version("vosk"),
                "license": "Apache-2.0", "model": "vosk-model-small-pt-0.3",
                "model_bytes": sum(p.stat().st_size for p in self.model_path.rglob("*") if p.is_file()),
                "vocabulary": self.vocabulary, "grammar_contains_rejection": "[unk]",
                "config": self.config, "block_samples": self.block_samples}


class PocketSphinxDetector:
    """Native keyphrase search with phonetic dictionary and background rejection."""

    block_samples = 1600

    def __init__(self, config="default"):
        from pocketsphinx import Decoder, get_model_path

        if config not in ("default", "strict"):
            raise ValueError("PocketSphinx config must be default or strict")
        self.config = config
        # Lower threshold admits more keyword paths; strict raises the ratio.
        self.threshold = 1e-30 if config == "default" else 1e-20
        self.model_path = Path(get_model_path()) / "en-us/en-us"
        dictionary = CLASSIC / "pocketsphinx-aegis.dict"
        dictionary.parent.mkdir(parents=True, exist_ok=True)
        text = "aegis AA EH ZH IY S\n"
        if dictionary.exists() and dictionary.read_text() != text:
            raise ValueError("Unexpected PocketSphinx pronunciation dictionary")
        if not dictionary.exists():
            dictionary.write_text(text)
        self.decoder = Decoder(hmm=str(self.model_path), dict=str(dictionary), lm=None,
                               keyphrase="aegis", kws_threshold=self.threshold,
                               samprate=16000, loglevel="ERROR")
        self.frame_rate = self.decoder.config["frate"]
        self._started = False
        self.reset()

    def reset(self):
        if self._started:
            self.decoder.end_utt()
        self.decoder.start_utt()
        self._started = True
        self._seen = set()

    def _events(self):
        events = []
        for segment in self.decoder.seg() or ():
            if segment.word.casefold().strip() != "aegis":
                continue
            key = (segment.word, segment.start_frame, segment.end_frame)
            if key in self._seen:
                continue
            self._seen.add(key)
            events.append({"keyword": "Aegis", "time_seconds": segment.start_frame / self.frame_rate,
                           "word_end_seconds": (segment.end_frame + 1) / self.frame_rate,
                           "score": float(segment.prob)})
        return events

    def process(self, pcm_int16_numpy):
        self.decoder.process_raw(np.asarray(pcm_int16_numpy, dtype="<i2").tobytes(), False, False)
        return self._events()

    def finish(self):
        self.decoder.end_utt()
        self._started = False
        return self._events()

    def metadata(self):
        return {"engine": "pocketsphinx", "version": importlib.metadata.version("pocketsphinx"),
                "license": "BSD-2-Clause", "uses_reference_audio": False, "config": self.config,
                "model": "bundled US-English acoustic model; Portuguese phonetic approximation",
                "model_bytes": sum(p.stat().st_size for p in self.model_path.rglob("*") if p.is_file()),
                "pronunciation": "AA EH ZH IY S", "search": "native keyphrase with filler/background model",
                "kws_threshold": self.threshold, "kws_plp": self.decoder.config["kws_plp"],
                "kws_delay_frames": self.decoder.config["kws_delay"], "frame_rate": self.frame_rate,
                "score_semantics": "native keyphrase segment value, not calibrated confidence",
                "block_samples": self.block_samples, "file_flush": "end_utt only; no synthetic audio"}
