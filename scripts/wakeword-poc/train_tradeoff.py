#!/usr/bin/env python3
"""One bounded sensitivity-recovery fit from the strong second-epoch checkpoint.

Reuses the owner-labelled arrays and event-balanced sampler unchanged. Selection
uses paired streaming validation utility, never perfect-recall eligibility or BCE.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

os.environ.setdefault('CUDA_VISIBLE_DEVICES', '-1')
os.environ.setdefault('TF_CPP_MIN_LOG_LEVEL', '2')
os.environ.setdefault('TF_NUM_INTRAOP_THREADS', '2')
os.environ.setdefault('TF_NUM_INTEROP_THREADS', '1')
import numpy as np
from prepare import private_output, sha256
from train_direct_training import cover_events, read, validation
from train_hardneg import baseline_guards, BASE, ROOT, BASE_SHA, save_json
from train_microwakeword import FEATURES

PARENT = Path.home()/'.cache/aegis-wakeword-human-label-training/model-v1'
STRONG_SHA = '1e7c686420f65d8da892f02c71d945f03a8dd528a1005de8c9c8482dce52ad2e'
PLAN = {
    'seed': 20261105, 'epochs': 3, 'learning_rate': 2e-6,
    'steps_per_epoch': 32, 'initializer': 'strong fit epoch-02 float checkpoint; fresh Adam',
    'batch_normalization': 'frozen, including running statistics',
    'batch_sources': {'synthetic_positive': 12, 'reference_positive': 4,
                      'real_positive': 16, 'synthetic_negative': 16,
                      'public_dev_negative': 8, 'real_negative': 8},
    'real_sampling': 'Shuffle original IDs before gain variant; every 221 TRAIN event each epoch; class exposure differs by at most one per event.',
    'selection': 'Maximize paired validation utility LiteRT, then minimum utility across runtimes, then earliest epoch. BCE diagnostic only.',
    'utility': 'FP_eliminated - FP_new - 4 * positives_lost + 4 * positives_recovered',
    'positive_detection': 'detected_near_decision (-500/+200ms)',
    'negative_detection': 'detected_any (any event anywhere in original context)',
    'test_policy': 'Only supplied validation and inherited original validation before seal; no test or legacy replay in this fit.',
    'threshold': .5, 'average_window': 5, 'release_below': .3, 'refractory_samples': 16000,
}


def paired_utility(baseline, candidate, runtime):
    before_rows = baseline['runtimes'][runtime]['contexts']['events']
    after_rows = candidate['runtimes'][runtime]['contexts']['events']
    b = {r['id']: r for r in before_rows}
    c = {r['id']: r for r in after_rows}
    if len(b) != len(before_rows) or len(c) != len(after_rows):
        raise ValueError('Duplicate validation event IDs')
    if b.keys() != c.keys():
        raise ValueError('Validation event IDs changed')
    changes = {key: [] for key in ('fp_eliminated', 'fp_new', 'positives_lost', 'positives_recovered')}
    recognized = false = 0
    for event, row in c.items():
        if (row['humanLabel'] not in ('correct', 'false_positive')
                or row['humanLabel'] != b[event]['humanLabel']):
            raise ValueError('Validation human label changed or unsupported')
        if row.get('split') != b[event].get('split'):
            raise ValueError('Validation split changed')
        positive = row['humanLabel'] == 'correct'
        field = 'detected_near_decision' if positive else 'detected_any'
        before, after = bool(b[event][field]), bool(row[field])
        recognized += int(positive and after)
        false += int(not positive and after)
        if before and not after:
            changes['positives_lost' if positive else 'fp_eliminated'].append(event)
        if after and not before:
            changes['positives_recovered' if positive else 'fp_new'].append(event)
    counts = {key: len(value) for key, value in changes.items()}
    utility = counts['fp_eliminated']-counts['fp_new']-4*counts['positives_lost']+4*counts['positives_recovered']
    return {'utility': utility, 'recognized_positives': recognized, 'false_positive_contexts': false,
            'paired_counts': counts, 'paired_event_ids': changes}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--prepared', type=private_output, required=True)
    parser.add_argument('--baseline-evaluation', type=private_output, required=True)
    parser.add_argument('--output', type=private_output, required=True)
    a = parser.parse_args()
    os.umask(0o077)
    guard = baseline_guards()
    protocol = a.prepared/'protocol.json'
    fp = a.prepared/'features'
    fm = read(fp/'features.json')
    if fm['protocol_sha256'] != sha256(protocol):
        raise ValueError('Prepared protocol changed')
    for name, digest in fm['array_sha256'].items():
        if Path(name).name != name or sha256(fp/name) != digest:
            raise ValueError('Prepared feature array changed')
    parent = read(PARENT/'training.json')
    out_parent = PARENT/'epoch-02'
    export = read(out_parent/'export.json')
    strong_model, strong_weights = out_parent/'aegis.int8.tflite', out_parent/'best.weights.h5'
    if (parent['history'][1]['epoch'] != 2
            or parent['history'][1]['model_sha256'] != STRONG_SHA
            or export['model_sha256'] != STRONG_SHA or sha256(strong_model) != STRONG_SHA
            or export['checkpoint_sha256'] != sha256(strong_weights)
            or parent['protocol_sha256'] != sha256(protocol)
            or export['features_manifest_sha256'] != sha256(fp/'features.json')):
        raise ValueError('Strong float/INT8 checkpoint lineage mismatch')
    x, y = np.load(fp/'train_x.npy'), np.load(fp/'train_y.npy')
    rx, ry = np.load(fp/'train_real_x.npy'), np.load(fp/'train_real_y.npy')
    vx, vy = np.load(fp/'validation_x.npy'), np.load(fp/'validation_y.npy')
    metadata = fm['real_windows']['train']
    if len(metadata) != len(rx) or len(rx) != len(ry):
        raise ValueError('Real feature metadata mismatch')
    roles = {r['id']: r for r in read(protocol)['events']}
    train = {key: r for key, r in roles.items() if r['split'] == 'train'}
    expected = Counter(r['humanLabel'] for r in train.values())
    if expected != {'correct': 91, 'false_positive': 130} or {r['id'] for r in metadata} != set(train):
        raise ValueError('All supplied 221 TRAIN events are required')
    for i, row in enumerate(metadata):
        role = roles[row['id']]
        if role['split'] != 'train' or float(ry[i, 0]) != float(role['humanLabel'] == 'correct'):
            raise ValueError('Human label/split changed')
    baseline = read(a.baseline_evaluation)
    if (baseline['model_sha256'] != BASE_SHA or baseline['split'] != 'validation'
            or baseline['split_manifest_sha256'] != sha256(protocol)):
        raise ValueError('Baseline evaluation does not match supplied validation')
    a.output.mkdir(parents=True, mode=0o700, exist_ok=False)
    lineage = {'parent_training_sha256': sha256(PARENT/'training.json'),
               'parent_export_sha256': sha256(out_parent/'export.json'),
               'float_checkpoint_path': str(strong_weights), 'float_checkpoint_sha256': sha256(strong_weights),
               'int8_checkpoint_path': str(strong_model), 'int8_checkpoint_sha256': STRONG_SHA}
    save_json(a.output/'plan.json', {'created_at': datetime.now(timezone.utc).isoformat(), 'plan': PLAN,
        'lineage': lineage, 'protocol_sha256': sha256(protocol),
        'features_manifest_sha256': sha256(fp/'features.json'),
        'training_script_sha256': sha256(Path(__file__)),
        'baseline_evaluation_sha256': sha256(a.baseline_evaluation)})
    sys.path.insert(0, str(ROOT/'comparison/microwakeword/source'))
    import tensorflow as tf
    from microwakeword import mixednet
    tf.keras.utils.set_random_seed(PLAN['seed'])
    tf.config.experimental.enable_op_determinism()
    flags = argparse.Namespace(**read(BASE/'training.json')['architecture'])
    model = mixednet.model(flags, (FEATURES, 40), None)
    model.load_weights(strong_weights)
    frozen = []
    for layer in model._flatten_layers():
        if isinstance(layer, tf.keras.layers.BatchNormalization):
            layer.trainable = False
            frozen.append(layer.name)
    if not frozen:
        raise ValueError('Expected BatchNormalization layers')
    model.compile(optimizer=tf.keras.optimizers.Adam(PLAN['learning_rate']), loss='binary_crossentropy')
    def predict(xx):
        return np.concatenate([model(xx[i:i+128], training=False).numpy() for i in range(0, len(xx), 128)])
    initial = predict(vx)
    pos = vy[:, 0] == 1
    initial_recall = float(np.mean(initial[pos] >= .5))
    rng = np.random.default_rng(PLAN['seed'])
    history, best, best_epoch = [], None, None
    started = time.perf_counter()
    for epoch in range(1, PLAN['epochs']+1):
        out = a.output/f'epoch-{epoch:02d}'
        out.mkdir(mode=0o700)
        steps, sources = PLAN['steps_per_epoch'], PLAN['batch_sources']
        draws, exposure, real_counts = {}, {}, {}
        for name, pool in fm['source_indices'].items():
            pool, budget = np.asarray(pool), sources[name]*steps
            draw = np.concatenate([rng.permutation(pool) for _ in range(math.ceil(budget/len(pool)))])[:budget]
            draws[name] = draw
            exposure[name] = {'presentations': budget, 'unique_windows': len(set(map(int, draw)))}
        for name, label, n in (('real_positive', 1., 91), ('real_negative', 0., 130)):
            draws[name], counts = cover_events(metadata, ry, label, sources[name]*steps, rng)
            if len(counts) != n or max(counts.values())-min(counts.values()) > 1:
                raise ValueError('Real TRAIN event coverage/balance failed')
            real_counts[name] = counts
        losses = []
        model.reset_metrics()
        for step in range(steps):
            batches, targets = [], []
            for name, size in sources.items():
                ii = draws[name][step*size:(step+1)*size]
                real = name.startswith('real_')
                batch = (rx if real else x)[ii].copy()
                if not real:
                    for sample in batch:
                        if rng.random() < .5:
                            k = int(rng.integers(FEATURES-4)); sample[k:k+4] = 0
                        if rng.random() < .5:
                            k = int(rng.integers(36)); sample[:, k:k+4] = 0
                batches.append(batch)
                targets.append((ry if real else y)[ii])
            bx, by = np.concatenate(batches), np.concatenate(targets)
            if len(bx) != 64 or np.sum(by) != 32:
                raise ValueError('Minibatch class balance failed')
            order = rng.permutation(64)
            losses.append(float(model.train_on_batch(bx[order], by[order])))
        model.save_weights(out/'best.weights.h5')
        vp = predict(vx)
        with (out/'export.log').open('x') as log:
            subprocess.run([sys.executable, str(Path(__file__).with_name('export_positive.py')),
                '--checkpoint', str(out/'best.weights.h5'), '--features', str(fp), '--output', str(out)],
                stdout=log, stderr=subprocess.STDOUT, check=True)
        if (out/'aegis.int8.tflite').stat().st_size != 39552:
            raise ValueError('Native model size contract failed')
        ev = validation(out/'aegis.int8.tflite', a, out/'validation')
        metrics = {rt: paired_utility(baseline, ev, rt) for rt in ('litert', 'native_host')}
        key = [-metrics['litert']['utility'], -min(m['utility'] for m in metrics.values()), epoch]
        row = {'epoch': epoch, 'train_loss': float(np.mean(losses)),
            'original_validation_bce': float(tf.keras.losses.binary_crossentropy(vy, vp).numpy().mean()),
            'original_validation_recall': float(np.mean(vp[pos] >= .5)),
            'utility_by_runtime': metrics, 'selection_key': key, 'exposure': exposure,
            'real_event_draws': real_counts, 'real_training_unique_events': 221,
            'real_training_class_counts': dict(expected),
            'checkpoint_sha256': sha256(out/'best.weights.h5'),
            'model_sha256': sha256(out/'aegis.int8.tflite'),
            'validation_sha256': sha256(out/'validation/evaluation.json')}
        history.append(row)
        if best is None or key < best:
            best, best_epoch = key, epoch
        save_json(a.output/'history.json', history)
        print(json.dumps({k: v for k, v in row.items() if k not in ('real_event_draws', 'exposure')}), flush=True)
    for name in ('aegis.int8.tflite', 'best.weights.h5'):
        shutil.copyfile(a.output/f'epoch-{best_epoch:02d}'/name, a.output/name)
    save_json(a.output/'training.json', {'plan': PLAN, 'lineage': lineage, 'history': history,
        'best_epoch': best_epoch, 'best_key': best, 'baseline': guard, 'params': model.count_params(),
        'frozen_batch_norm_layers': frozen, 'initial_original_validation_recall': initial_recall,
        'protocol_sha256': sha256(protocol), 'features_manifest_sha256': sha256(fp/'features.json'),
        'training_script_sha256': sha256(Path(__file__)), 'model_sha256': sha256(a.output/'aegis.int8.tflite'),
        'elapsed_seconds': time.perf_counter()-started})
    save_json(a.output/'selection.json', {'created_at': datetime.now(timezone.utc).isoformat(),
        'protocol_sha256': sha256(protocol), 'training_sha256': sha256(a.output/'training.json'),
        'selected_epoch': best_epoch, 'model_sha256': sha256(a.output/'aegis.int8.tflite'),
        'threshold': .5, 'selection_key': best,
        'scope': 'Best of this fit by validation only; global comparison and final test/legacy remain pending.'})


if __name__ == '__main__':
    main()
