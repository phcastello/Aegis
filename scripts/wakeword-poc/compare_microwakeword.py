#!/usr/bin/env python3
"""Incremental CPU adapter for the locally trained, quantized microWakeWord model.

Runtime needs numpy, pymicro-features and ai-edge-litert only; no TensorFlow.
All voice/model artefacts remain in the private cache outside any Git checkout.
"""
from collections import deque
import json
from pathlib import Path
import importlib.metadata
import numpy as np

CONFIGS = {'default': (.5, 5), 'strict': (.75, 5), 'sensitive': (.35, 3)}
ROOT = Path.home() / '.cache/aegis-wakeword-poc/comparison/microwakeword'


class MicroWakeWordDetector:
    block_samples = 160  # official microfrontend: 10 ms at 16 kHz

    def __init__(self, config='default', model_path=None):
        from ai_edge_litert.interpreter import Interpreter
        from pymicro_features import MicroFrontend
        self._frontend_class = MicroFrontend
        self.config = config
        self.threshold, self.average_window = CONFIGS[config]
        self.model_path = Path(model_path or ROOT / 'model/aegis.int8.tflite')
        self._interpreter_class = Interpreter
        self._model_content = self.model_path.read_bytes()
        self.reset()

    def reset(self):
        # reset_all_variables() did not reproduce a fresh interpreter's
        # resource state in a seeded-noise control for this graph. Recreate
        # state from the cached 39 KiB flatbuffer to rerun its initializers.
        # Trained weights stay cached; no model file is reread or retrained.
        self.interpreter = self._interpreter_class(model_content=self._model_content, num_threads=1)
        self.interpreter.allocate_tensors()
        self.input = self.interpreter.get_input_details()[0]
        self.output = self.interpreter.get_output_details()[0]
        if self.input['dtype'] != np.int8 or self.output['dtype'] != np.uint8:
            raise ValueError('Expected the quantized microWakeWord model')
        self.stride = int(self.input['shape'][1])
        self.frontend = self._frontend_class()
        self._samples = np.empty(0, dtype=np.int16)
        self._features = []
        self._scores = deque(maxlen=self.average_window)
        self._samples_seen = 0
        self._latched = False
        self._last_event = -float('inf')
        self.last_score = 0.
        self.max_score = 0.

    def process(self, samples):
        samples = np.asarray(samples)
        if samples.dtype != np.int16 or samples.ndim != 1:
            raise ValueError('Feed mono int16 PCM samples')
        self._samples = np.concatenate((self._samples, samples))
        events = []
        while len(self._samples) >= self.block_samples:
            block, self._samples = self._samples[:160], self._samples[160:]
            self._samples_seen += 160
            result = self.frontend.process_samples(block.tobytes())
            if result.samples_read != 160:
                raise RuntimeError('Unexpected C microfrontend sample consumption')
            if not result.features:
                continue
            self._features.append(result.features)
            if len(self._features) < self.stride:
                continue
            x = np.asarray(self._features[:self.stride], dtype=np.float32)
            del self._features[:self.stride]
            scale, zero = self.input['quantization']
            quantized = np.clip(np.round(x / scale + zero), -128, 127).astype(np.int8)
            self.interpreter.set_tensor(self.input['index'], quantized.reshape(self.input['shape']))
            self.interpreter.invoke()
            output = float(self.interpreter.get_tensor(self.output['index']).ravel()[0])
            scale, zero = self.output['quantization']
            score = (output - zero) * scale
            self.last_score = score
            self._scores.append(score)
            if len(self._scores) < self.average_window:
                continue
            averaged = float(np.mean(self._scores))
            self.max_score = max(self.max_score, averaged)
            if averaged < self.threshold * .6:
                self._latched = False
            timestamp = self._samples_seen / 16000
            if averaged >= self.threshold and not self._latched and timestamp - self._last_event >= 1.:
                self._latched = True
                self._last_event = timestamp
                events.append({'keyword': 'Aegis', 'score': averaged, 'timestamp': timestamp})
        return events

    def finish(self):
        # The shared runner controls silence padding once for every detector.
        return []

    @property
    def metadata(self):
        training = json.loads((self.model_path.parent / 'training.json').read_text())
        return {'technology': 'microWakeWord', 'architecture': 'official streaming MixConv',
                'config': self.config, 'threshold': self.threshold, 'average_window': self.average_window,
                'refractory_seconds': 1., 'reset_below_threshold_fraction': .6,
                'stream_reset': 'new interpreter state from cached model bytes; no model file IO',
                'block_samples': self.block_samples, 'stride_features': self.stride,
                'model_bytes': self.model_path.stat().st_size, 'quantization': 'INT8 input/weights/state; UINT8 output',
                'source_sha': training['source_sha'], 'model_sha256': training['model_sha256'],
                'versions': {name: importlib.metadata.version(name) for name in ('numpy', 'pymicro-features', 'ai-edge-litert')},
                'threads': 1, 'provider': 'CPU', 'code_license': 'Apache-2.0',
                'uses_reference_audio': training.get('uses_reference_audio', True),
                'variant': 'realneg' if training.get('real_negative_manifest_sha256') else 'synthetic',
                'uses_real_dev_negatives': bool(training.get('real_negative_manifest_sha256')),
                'real_negative_manifest_sha256': training.get('real_negative_manifest_sha256'),
                'training_note': 'Piper synthetic training/validation, six owner reference crops, optional FLEURS DEV negatives; never independent owner recordings or public TEST audio.',
                'native_note': 'TFLite/LiteRT plus C microfrontend; Android/Windows integration not exercised.'}
