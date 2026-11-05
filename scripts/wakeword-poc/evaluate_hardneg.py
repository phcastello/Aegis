#!/usr/bin/env python3
"""Replay arbitrary private wakeword candidates under frozen event rules.

New collection evaluation reads trigger_context only and adds no EOF silence.
Labels describe the original activation, not every sample: the near-decision
count is reported separately from any event in the captured context. Correct
contexts measure re-detection of observed activations, not population recall.
Optional legacy regression preserves the already-consulted 23+8, 35 minute and
continuous-state protocols. No evaluation audio is made into training data.
"""
import argparse
from collections import defaultdict
import ctypes
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import platform
import time

import numpy as np

from compare_microwakeword import MicroWakeWordDetector
from comparison import read_wav
from prepare import private_output, sha256
from run import load_data

ROOT = Path.home() / '.cache/aegis-wakeword-poc'
RULES = {'threshold': .5, 'average_window': 5, 'release_below': .3,
         'refractory_samples': 16000, 'block_samples': 160,
         'near_decision_before_samples': 8000,
         'near_decision_after_samples': 3200,
         'context_eof_padding_samples': 0}


class NativeCandidate:
    """Private harness only; the unmodified production library validates ABI."""
    def __init__(self, library, model):
        self.handle = None
        self.lib = ctypes.CDLL(str(library))
        self.lib.aegis_mww_create.argtypes = [ctypes.c_void_p, ctypes.c_size_t]
        self.lib.aegis_mww_create.restype = ctypes.c_void_p
        self.lib.aegis_mww_reset.argtypes = [ctypes.c_void_p]
        self.lib.aegis_mww_reset.restype = ctypes.c_bool
        self.lib.aegis_mww_process.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int16), ctypes.c_size_t]
        self.lib.aegis_mww_process.restype = ctypes.c_int64
        self.lib.aegis_mww_destroy.argtypes = [ctypes.c_void_p]
        for name in ('last_score', 'max_score', 'event_score', 'event_average'):
            fn = getattr(self.lib, 'aegis_mww_' + name, None)
            if fn:
                fn.argtypes = [ctypes.c_void_p]
                fn.restype = ctypes.c_float
        raw = model.read_bytes()
        self.handle = self.lib.aegis_mww_create(ctypes.c_char_p(raw), len(raw))
        if not self.handle:
            raise ValueError('Unmodified production library rejected candidate size, tensor contract or graph')

    def reset(self):
        if not self.lib.aegis_mww_reset(self.handle):
            raise RuntimeError('Native reset failed')

    def process(self, samples):
        samples = np.ascontiguousarray(samples, dtype=np.int16)
        result = self.lib.aegis_mww_process(self.handle, samples.ctypes.data_as(ctypes.POINTER(ctypes.c_int16)), len(samples))
        if result == -2:
            raise RuntimeError('Native inference failed')
        if result < 0:
            return []
        average = getattr(self.lib, 'aegis_mww_event_average', None)
        score = float(average(self.handle)) if average else None
        return [{'timestamp': result / 16000, 'score': score}]

    @property
    def last_score(self):
        return self.lib.aegis_mww_last_score(self.handle)

    @property
    def max_score(self):
        return self.lib.aegis_mww_max_score(self.handle)

    def close(self):
        if self.handle:
            self.lib.aegis_mww_destroy(self.handle)
            self.handle = None


def feed(detector, audio, tail=0):
    """Keep all frontend/NN state within each stream; never normalize PCM."""
    original_samples = len(audio)
    if tail:
        audio = np.concatenate((audio, np.zeros(round(tail * 16000), dtype=np.int16)))
        # Legacy runner pads last partial block; contexts never do.
        audio = np.pad(audio, (0, (-len(audio)) % 160))
    events, scores = [], []
    for offset in range(0, len(audio), 160):
        events.extend(detector.process(audio[offset:offset+160]))
        scores.append(float(detector.last_score))
    for event in events:
        event['sample'] = round(event['timestamp'] * 16000)
        event['within_original_capture'] = event['sample'] <= original_samples
    return events, np.asarray(scores, dtype=np.float32)


def counts(rows):
    out = {}
    for label in ('false_positive', 'correct'):
        selected = [r for r in rows if r['humanLabel'] == label]
        out[label] = {'contexts': len(selected),
                      'any_event_contexts': sum(r['detected_any'] for r in selected),
                      'near_decision_event_contexts': sum(r['detected_near_decision'] for r in selected),
                      'events': sum(len(r['events']) for r in selected),
                      'near_decision_events': sum(len(r['near_decision_events']) for r in selected),
                      'full_2s_precontext': sum(r['full_2s_precontext'] for r in selected),
                      'full_precontext_any_event_contexts': sum(r['detected_any'] and r['full_2s_precontext'] for r in selected),
                      'full_precontext_near_decision_event_contexts': sum(r['detected_near_decision'] and r['full_2s_precontext'] for r in selected)}
    return out


def contexts(args, detector, runtime, scores_dir):
    inventory = json.loads(args.audit.read_text())
    split_manifest = json.loads(args.split_manifest.read_text())
    if split_manifest['audit_sha256'] != sha256(args.audit):
        raise ValueError('Frozen split refers to a different audit')
    entries = split_manifest['events']
    assignment = {r['id']: r for r in entries}
    if len(assignment) != len(entries):
        raise ValueError('Duplicate IDs in split manifest')
    source_rows = inventory['events']
    if set(assignment) != {r['id'] for r in source_rows}:
        raise ValueError('Split manifest must cover exactly audited events')
    sessions = defaultdict(set)
    for row in entries:
        sessions[row['session']].add(row['split'])
    if any(len(s) > 1 for s in sessions.values()):
        raise ValueError('Session leakage across splits')
    groups = defaultdict(set)
    for row in source_rows:
        if assignment[row['id']]['humanLabel'] != row['humanLabel']:
            raise ValueError('Human label changed in split manifest')
        groups[row['groupId']].add(assignment[row['id']]['split'])
    if any(len(s) > 1 for s in groups.values()):
        raise ValueError('groupId leakage across splits')
    rows = []
    for original in source_rows:
        split_row = assignment[original['id']]
        if args.split != 'all' and split_row['split'] != args.split:
            continue
        audit = original['audit']
        path = private_output(audit['trigger_path'])
        if sha256(path) != original['triggerContext']['sha256']:
            raise ValueError('Trigger SHA-256 changed since audit')
        audio = read_wav(path)
        if len(audio) != original['triggerContext']['samples']:
            raise ValueError('Trigger sample count changed since audit')
        detector.reset()
        events, scores = feed(detector, audio)
        np.save(scores_dir / (original['id'] + '.npy'), scores)
        decision = audit['decision_sample_in_context']
        near = [e for e in events if decision - RULES['near_decision_before_samples'] <= e['sample'] <= min(len(audio), decision + RULES['near_decision_after_samples'])]
        rows.append({'id': original['id'], 'humanLabel': original['humanLabel'],
                     'split': split_row['split'], 'session': split_row['session'],
                     'samples': len(audio), 'decision_sample_in_context': decision,
                     'full_2s_precontext': decision >= 32000,
                     'integrity_eligible': audit.get('training_eligible_context'),
                     'events': events, 'near_decision_events': near,
                     'detected_any': bool(events), 'detected_near_decision': bool(near),
                     'max_averaged_score': float(detector.max_score)})
    grouped = {key: counts([r for r in rows if r['session'] == key]) for key in sorted({r['session'] for r in rows})}
    return {'counts': counts(rows), 'by_session': grouped, 'events': rows,
            'scope': 'trigger_context only; raw PCM, no EOF silence; fresh state per context',
            'interpretation': 'Activation-selected contexts. Near-decision events are within -500/+200 ms of original decision. Correct re-detection is conditional, not population recall. False-positive context fraction is not false activations/hour.',
            'runtime': runtime}


def legacy(args, detector, scores_dir):
    data, inventory = load_data(args.legacy_data)
    selection = json.loads(Path(__file__).with_name('selection.json').read_text())
    refs = {r['id'] for r in selection['references']}
    rows = []
    for row in inventory['records']:
        if row['id'] in refs:
            continue
        audio = read_wav(data / row['wav_path'])
        detector.reset()
        events, scores = feed(detector, audio, tail=.5)
        np.save(scores_dir / ('legacy-' + row['id'] + '.npy'), scores)
        rows.append({'id': row['id'], 'label': row['label'], 'events': events,
                     'detected': bool(events), 'device': row['device']})
    public = read_wav(args.public_wav)
    detector.reset()
    public_events, _ = feed(detector, np.pad(public, (0, (-len(public)) % 160)))
    detector.reset()
    prefix_events, _ = feed(detector, public[:30*16000])
    continuous = []
    for row in inventory['records']:
        if row['id'] in refs:
            continue
        events, _ = feed(detector, read_wav(data / row['wav_path']), tail=3)
        continuous.append({'id': row['id'], 'label': row['label'], 'events': events, 'detected': bool(events)})
    def summary(records):
        return {label: {'files': sum(r['label'] == label for r in records),
                        'activated_files': sum(r['label'] == label and r['detected'] for r in records),
                        'events': sum(len(r['events']) for r in records if r['label'] == label)}
                for label in ('positive', 'negative')}
    return {'quality': summary(rows), 'continuous': summary(continuous),
            'public': {'seconds': len(public)/16000, 'events': len(public_events)},
            'independent_events': rows, 'continuous_events': continuous,
            'public_events': public_events, 'continuous_prefix_events': prefix_events,
            'inventory_sha256': sha256(data / 'inventory.json'),
            'public_wav_sha256': sha256(args.public_wav),
            'protocol': 'Legacy independent: .5s silence tail; public: no tail; continuous: 30s public prefix then 31 trials with 3s silence, no reset. Previously consulted regression sets, not blind tests.'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', type=private_output, required=True)
    parser.add_argument('--audit', type=private_output)
    parser.add_argument('--split-manifest', type=private_output)
    parser.add_argument('--split', choices=('train', 'validation', 'test', 'all'), default='validation')
    parser.add_argument('--output', type=private_output, required=True)
    parser.add_argument('--library', type=private_output, help='Optional unmodified native host library for second runtime')
    parser.add_argument('--legacy', action='store_true', help='Also replay old 23+8, public35min and continuous regression')
    parser.add_argument('--legacy-data', type=private_output, default=ROOT / 'data')
    parser.add_argument('--public-wav', type=private_output, default=ROOT / 'comparison/public/negatives.wav')
    parser.add_argument('--reference-kernels', action='store_true', help='Diagnostic LiteRT BUILTIN_REF instead of default')
    args = parser.parse_args()
    if bool(args.audit) != bool(args.split_manifest):
        parser.error('--audit and --split-manifest must be supplied together')
    if not args.audit and not args.legacy:
        parser.error('Supply audited contexts or --legacy')
    os.umask(0o077)
    args.output.mkdir(parents=True, mode=0o700, exist_ok=False)
    started = time.monotonic()
    report = {'schema': 1, 'model_sha256': sha256(args.model), 'model_bytes': args.model.stat().st_size,
              'model_path': str(args.model), 'rules': RULES, 'split': args.split,
              'host': platform.machine(),
              'versions': {name: importlib.metadata.version(name) for name in ('numpy', 'pymicro-features', 'ai-edge-litert')},
              'evaluation_script_sha256': sha256(Path(__file__)), 'runtimes': {}}
    if args.audit:
        report.update(audit_sha256=sha256(args.audit), split_manifest_sha256=sha256(args.split_manifest))
    lite = MicroWakeWordDetector(model_path=args.model)
    if args.reference_kernels:
        from ai_edge_litert.interpreter import Interpreter, OpResolverType
        lite._interpreter_class = lambda **kw: Interpreter(**kw, experimental_op_resolver_type=OpResolverType.BUILTIN_REF)
        lite.reset()
    report['reference_kernel_mode'] = bool(args.reference_kernels)
    detectors = [('litert', lite)]
    if args.library:
        report['library_sha256'] = sha256(args.library)
        try:
            detectors.append(('native_host', NativeCandidate(args.library, args.model)))
        except (ValueError, OSError) as exc:
            report['native_unavailable'] = str(exc)
    try:
        for name, detector in detectors:
            score_dir = args.output / ('scores-' + name)
            score_dir.mkdir(mode=0o700)
            result = {}
            if args.audit:
                result['contexts'] = contexts(args, detector, name, score_dir)
            if args.legacy:
                result['legacy'] = legacy(args, detector, score_dir)
            report['runtimes'][name] = result
    finally:
        for _, detector in detectors:
            if isinstance(detector, NativeCandidate):
                detector.close()
    if args.library and 'native_host' in report['runtimes'] and args.audit:
        refs = report['runtimes']['litert']['contexts']['events']
        natives = report['runtimes']['native_host']['contexts']['events']
        comparisons = []
        for ref, native in zip(refs, natives):
            a = np.load(args.output / 'scores-litert' / (ref['id'] + '.npy'))
            b = np.load(args.output / 'scores-native_host' / (native['id'] + '.npy'))
            delta = float(np.max(np.abs(a-b), initial=0))
            event_a, event_b = [e['sample'] for e in ref['events']], [e['sample'] for e in native['events']]
            comparisons.append({'id': ref['id'], 'max_score_delta': delta,
                                'activated_file_equal': bool(event_a) == bool(event_b),
                                'events_within_stride': len(event_a) == len(event_b) and all(abs(x-y) <= 480 for x,y in zip(event_a,event_b)),
                                'score_tolerance_1lsb_passed': delta <= 1/256 + 1e-9})
        report['native_comparison'] = {'cases': comparisons, 'arm64': 'not tested; host does not establish physical compatibility'}
    report['elapsed_seconds'] = time.monotonic() - started
    (args.output / 'evaluation.json').write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n')
    print(json.dumps({'output': str(args.output), 'model_sha256': report['model_sha256'],
                      'runtimes': {name: {suite: (value.get('counts') or value.get('quality')) for suite,value in result.items()} for name,result in report['runtimes'].items()},
                      'native_unavailable': report.get('native_unavailable'),
                      'elapsed_seconds': report['elapsed_seconds']}, indent=2))


if __name__ == '__main__':
    main()
