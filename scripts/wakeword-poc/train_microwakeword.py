#!/usr/bin/env python3
"""Train official microWakeWord MixConv locally, never on the held-out recordings.

Requires the isolated microWakeWord checkout and TensorFlow CPU environment.
Training uses the shared synthetic manifest, the six original reference crops,
and optionally the disjoint public FLEURS DEV negative manifest. Checkpoints use
only the declared validation split; owner holdout recordings are never read.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import time
import wave

os.environ.setdefault('CUDA_VISIBLE_DEVICES', '-1')
os.environ.setdefault('TF_CPP_MIN_LOG_LEVEL', '2')
os.environ.setdefault('TF_NUM_INTRAOP_THREADS', '2')
os.environ.setdefault('TF_NUM_INTEROP_THREADS', '1')

import numpy as np

from prepare import private_output, sha256

SOURCE_SHA = '4665173cd35f1cff9a61e06fc427f124766c488e'
SEED = 20261031
FEATURES = 186
STRIDE = 3


def wav_samples(path):
    with wave.open(str(path)) as source:
        if (source.getframerate(), source.getnchannels(), source.getsampwidth()) != (16000, 1, 2):
            raise ValueError(f'Expected 16 kHz mono PCM16: {path}')
        return np.frombuffer(source.readframes(source.getnframes()), dtype='<i2').copy()


def features(samples):
    """Same C microfrontend as upstream, including PCAN and noise reduction."""
    from pymicro_features import MicroFrontend
    frontend = MicroFrontend()
    rows = []
    for start in range(0, len(samples) - 159, 160):
        output = frontend.process_samples(samples[start:start + 160].tobytes())
        if output.features:
            rows.append(output.features)
    return np.asarray(rows, dtype=np.float32)


def feature_window(spectrogram, end):
    end = max(0, min(end, len(spectrogram)))
    x = spectrogram[max(0, end - FEATURES):end]
    return np.pad(x, ((FEATURES - len(x), 0), (0, 0))).astype(np.float32)


def load_training(args):
    """Read only the synthetic train/validation tree and explicit reference crops."""
    rng = np.random.default_rng(SEED)
    root = private_output(args.dataset)
    manifest_path = root / 'manifest.json'
    manifest = json.loads(manifest_path.read_text())
    rows = manifest['records']
    datasets = {'train': ([], []), 'validation': ([], [])}
    provenance = []
    for record_index, row in enumerate(rows):
        if record_index % 500 == 0:
            print(f'Preparing synthetic features {record_index}/{len(rows)}', flush=True)
        split = row['split']
        if split not in datasets:
            continue
        label = row['label']
        path = (root / row['path']).resolve()
        if not path.is_relative_to(root):
            raise ValueError('Training path escapes synthetic dataset')
        samples = wav_samples(path)
        spectrogram = features(samples)
        positive = label in ('positive', 1, True)
        if positive:
            end_seconds = float(row['keyword_end_seconds'])
            # End the positive window 60–180 ms after the keyword, never after
            # an entire optional command. This trains an acoustic wakeword model.
            end = round((end_seconds + rng.uniform(.06, .18)) * 100) - 2
        else:
            end = int(rng.integers(min(FEATURES, len(spectrogram)), len(spectrogram) + 1))
        datasets[split][0].append(feature_window(spectrogram, end))
        datasets[split][1].append(float(positive))
        # Continuous inference also sees incomplete startup context. Without
        # negative prefix windows, zero-left-padding accidentally identifies
        # positive examples because complete negative windows have no padding.
        if positive:
            prefix_end = max(1, round((float(row['keyword_start_seconds']) - .05) * 100) - 2)
        else:
            prefix_end = int(rng.integers(1, min(FEATURES, len(spectrogram)) + 1))
        datasets[split][0].append(feature_window(spectrogram, prefix_end))
        datasets[split][1].append(0.)
        provenance.append({'path': str(path), 'sha256': sha256(path), 'split': split, 'label': label})
    # Only these six crops from the original reference split may contribute
    # owner-voice training data. The 23+8 independent files are never opened.
    if args.references:
        refs = private_output(args.references)
        paths = sorted(refs.glob('*.wav'))
        expected = {r['id'] for r in json.loads(Path(__file__).with_name('selection.json').read_text())['references']}
        if len(paths) != 6 or {p.stem for p in paths} != expected:
            raise ValueError('Expected precisely the six original reference crops')
        for path in paths:
            original = wav_samples(path)
            provenance.append({'path': str(path), 'sha256': sha256(path), 'split': 'train', 'label': 'reference-positive'})
            for _ in range(80):
                speed = rng.uniform(.9, 1.1)
                length = max(1, round(len(original) / speed))
                resampled = np.interp(np.linspace(0, len(original) - 1, length), np.arange(len(original)), original)
                gain = 10 ** rng.uniform(-.6, .4)
                noise = rng.normal(0, rng.uniform(4, 110), length)
                altered = np.clip(resampled * gain + noise, -32768, 32767).astype(np.int16)
                prefix = np.zeros(int(rng.uniform(.6, 1.3) * 16000), dtype=np.int16)
                suffix = np.zeros(4800, dtype=np.int16)
                samples = np.concatenate((prefix, altered, suffix))
                spectrum = features(samples)
                end = round((len(prefix) + len(altered)) / 160) + int(rng.integers(6, 19)) - 2
                datasets['train'][0].append(feature_window(spectrum, end))
                datasets['train'][1].append(1.)
                datasets['train'][0].append(feature_window(spectrum, max(1, len(prefix) // 160 - 2)))
                datasets['train'][1].append(0.)
    arrays = {}
    for split, (x, y) in datasets.items():
        arrays[split] = (np.stack(x).astype(np.float32), np.asarray(y, dtype=np.float32).reshape(-1, 1))
        if set(np.unique(arrays[split][1])) != {0., 1.}:
            raise ValueError(f'{split} must contain both classes')
    return arrays, provenance, sha256(manifest_path)


def load_real_negatives(path, snapshot):
    """Add only deduplicated FLEURS DEV negatives, never FLEURS TEST audio."""
    root = private_output(path)
    manifest_path = snapshot
    raw_manifest = manifest_path.read_bytes()
    manifest = json.loads(raw_manifest)
    manifest_digest = hashlib.sha256(raw_manifest).hexdigest()
    rng = np.random.default_rng(SEED)
    rows = {'train': ([], []), 'validation': ([], [])}
    provenance = []
    sources = set()
    for index, row in enumerate(manifest['records']):
        if index % 100 == 0:
            print(f'Preparing real DEV negatives {index}/{len(manifest["records"])}', flush=True)
        split = row['split']
        if split not in rows or row['label'] != 'negative':
            raise ValueError('Real-negative manifest must have only train/validation negatives')
        source_id = row['source_id']
        if source_id in sources:
            raise ValueError('Duplicate real-negative source')
        sources.add(source_id)
        audio = (root / row['path']).resolve()
        if not audio.is_relative_to(root) or sha256(audio) != row['sha256']:
            raise ValueError('Changed or unsafe real-negative file')
        spectrum = features(wav_samples(audio))
        # Fixed before any evaluation: two random complete-context windows and
        # one startup window per DEV source. No trigger mining or text selection.
        for _ in range(2):
            end = int(rng.integers(min(FEATURES, len(spectrum)), len(spectrum) + 1))
            rows[split][0].append(feature_window(spectrum, end))
            rows[split][1].append(0.)
        end = int(rng.integers(1, min(FEATURES, len(spectrum)) + 1))
        rows[split][0].append(feature_window(spectrum, end))
        rows[split][1].append(0.)
        provenance.append({'path': str(audio), 'sha256': row['sha256'],
                           'split': split, 'label': 'public-dev-negative', 'source_id': source_id})
    return {k: (np.stack(x).astype(np.float32), np.asarray(y, dtype=np.float32).reshape(-1, 1))
            for k, (x, y) in rows.items()}, provenance, manifest_digest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--dataset', type=Path, required=True)
    parser.add_argument('--references', type=Path)
    parser.add_argument('--real-negatives', type=Path, help='Deduplicated FLEURS DEV negative manifest directory')
    parser.add_argument('--output', type=private_output, required=True)
    parser.add_argument('--epochs', type=int, default=20)
    args = parser.parse_args()
    os.umask(0o077)
    import subprocess
    if subprocess.check_output(['git', '-C', str(args.source), 'rev-parse', 'HEAD'], text=True).strip() != SOURCE_SHA:
        raise ValueError('Unexpected microWakeWord source revision')
    args.output.mkdir(mode=0o700, parents=True, exist_ok=False)
    if args.real_negatives:
        # Freeze metadata before lengthy feature generation; background corpus
        # preparation must never race a model's train/validation assignment.
        raw = (private_output(args.real_negatives) / 'manifest.json').read_bytes()
        (args.output / 'real-negative-manifest.json').write_bytes(raw)
    sys.path.insert(0, str(args.source))
    import tensorflow as tf
    from microwakeword import mixednet, utils
    from microwakeword.layers import modes
    tf.keras.utils.set_random_seed(SEED)
    tf.config.threading.set_intra_op_parallelism_threads(2)
    tf.config.threading.set_inter_op_parallelism_threads(1)
    tf.config.experimental.enable_op_determinism()
    started = time.perf_counter()
    arrays, provenance, manifest_sha = load_training(args)
    original_counts = {k: len(v[0]) for k, v in arrays.items()}
    real_manifest_sha = None
    real_counts = {}
    if args.real_negatives:
        extra, real_provenance, real_manifest_sha = load_real_negatives(args.real_negatives, args.output / 'real-negative-manifest.json')
        for split in arrays:
            real_counts[split] = len(extra[split][0])
            arrays[split] = tuple(np.concatenate((old, new)) for old, new in zip(arrays[split], extra[split]))
        provenance.extend(real_provenance)
    x, y = arrays['train']
    vx, vy = arrays['validation']
    print(f'Features ready: training={len(x)} validation={len(vx)}', flush=True)
    # Official notebook MixConv recipe; width reduced from 64 to 32 for a small
    # CPU PoC. All layers and streaming conversion come from upstream.
    flags = argparse.Namespace(pointwise_filters='32,32,32,32', repeat_in_block='1,1,1,1',
        mixconv_kernel_sizes='[5], [7,11], [9,15], [23]', residual_connection='0,0,0,0',
        first_conv_filters=32, first_conv_kernel_size=5, stride=STRIDE,
        spatial_attention=0, pooled=0, max_pool=0)
    model = mixednet.model(flags, (FEATURES, 40), None)
    model.compile(optimizer=tf.keras.optimizers.Adam(.001), loss='binary_crossentropy')
    rng = np.random.default_rng(SEED)
    best_loss = float('inf')
    history = []
    best_weights = None
    for epoch in range(args.epochs):
        order = rng.permutation(len(x))
        losses = []
        for offset in range(0, len(order), 64):
            idx = order[offset:offset + 64]
            bx = x[idx].copy()
            # The upstream trainer also supports time/frequency masking. Keep
            # it modest and deterministic for this fixed, single training run.
            for sample in bx:
                if rng.random() < .5:
                    start = int(rng.integers(FEATURES - 4))
                    sample[start:start + 4] = 0
                if rng.random() < .5:
                    start = int(rng.integers(36))
                    sample[:, start:start + 4] = 0
            losses.append(float(model.train_on_batch(bx, y[idx])))
        predictions = np.concatenate([model(vx[i:i + 128], training=False).numpy() for i in range(0, len(vx), 128)])
        val_loss = float(tf.keras.losses.binary_crossentropy(vy, predictions).numpy().mean())
        if val_loss < best_loss:
            best_loss = val_loss
            best_weights = model.get_weights()
            best_epoch = epoch + 1
        record = {'epoch': epoch + 1, 'loss': float(np.mean(losses)), 'synthetic_validation_loss': val_loss,
                  'synthetic_validation_accuracy': float(np.mean((predictions >= .5) == vy))}
        history.append(record)
        print(json.dumps(record), flush=True)
    model.set_weights(best_weights)
    model.save_weights(args.output / 'best.weights.h5')
    config = {'spectrogram_length': FEATURES, 'stride': STRIDE, 'train_dir': str(args.output)}
    utils.convert_model_saved(model, config, 'streaming_saved', modes.Modes.STREAM_INTERNAL_STATE_INFERENCE)
    class Calibration:
        def get_data(self, *unused, **kwargs):
            # Quantization uses training examples only, never validation/test.
            return x[:500].copy(), y[:500], None
    utils.convert_saved_model_to_tflite(config, Calibration(), str(args.output / 'streaming_saved'),
                                       str(args.output), 'aegis.int8.tflite', quantize=True)
    from ai_edge_litert.interpreter import Interpreter
    interpreter = Interpreter(model_path=str(args.output / 'aegis.int8.tflite'), num_threads=1)
    interpreter.allocate_tensors()
    inputs, outputs = interpreter.get_input_details(), interpreter.get_output_details()
    if any('float' in str(t['dtype']) for t in interpreter.get_tensor_details()):
        raise ValueError('Unexpected floating-point tensors in quantized streaming model')
    if inputs[0]['dtype'] != np.int8 or outputs[0]['dtype'] != np.uint8:
        raise ValueError('Expected fully quantized INT8 input / UINT8 output')
    metadata = {'source_sha': SOURCE_SHA, 'seed': SEED, 'tensorflow': tf.__version__,
        'training_manifest_sha256': manifest_sha, 'training_samples': len(x), 'validation_samples': len(vx),
        'original_piper_and_reference_window_counts': original_counts,
        'real_negative_manifest_sha256': real_manifest_sha, 'real_negative_window_counts': real_counts,
        'validation_scope': 'Piper plus FLEURS DEV negatives' if args.real_negatives else 'Piper only',
        'uses_reference_audio': bool(args.references),
        'sampling': 'one target window plus one explicitly negative startup/prefix window per clip; keyword-prefix negatives stop before annotation start',
        'architecture': vars(flags), 'spectrogram_frames': FEATURES, 'feature_step_ms': 10,
        'model_stride': STRIDE, 'epochs': args.epochs, 'best_epoch_synthetic_validation': best_epoch,
        'best_synthetic_validation_loss': best_loss, 'history': history, 'provenance': provenance,
        'model_bytes': (args.output / 'aegis.int8.tflite').stat().st_size,
        'model_sha256': sha256(args.output / 'aegis.int8.tflite'),
        'input_shape': inputs[0]['shape'].tolist(), 'input_quantization': inputs[0]['quantization'],
        'output_quantization': outputs[0]['quantization'], 'elapsed_training_export_seconds': time.perf_counter() - started,
        'license': 'microWakeWord Apache-2.0; synthetic voices subject to shared corpus cards',
        'validation_limit': ('Piper and disjoint FLEURS DEV validation only. ' if args.real_negatives else 'Piper validation only. ')
                            + 'Owner test files are never opened during training.'}
    (args.output / 'training.json').write_text(json.dumps(metadata, indent=2) + '\n')
    print(f'Quantized streaming model ready: {args.output}', flush=True)


if __name__ == '__main__':
    main()
