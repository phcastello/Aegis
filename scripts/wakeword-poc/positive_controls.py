#!/usr/bin/env python3
"""Private post-selection reset, control, resource and concatenated-stream replay.

Concatenated capture contexts with artificial three-second gaps are a regression
stress test, not reconstruction of the original acoustic session or FP/hour.
"""
import argparse
from collections import defaultdict
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import resource
import time

import numpy as np

from compare_microwakeword import MicroWakeWordDetector
from comparison import read_wav
from evaluate_hardneg import NativeCandidate, RULES
from prepare import private_output, sha256

SEED = 20261105


def rss_kib():
    """Linux current RSS; unlike ru_maxrss this is not a lifetime maximum."""
    try:
        for line in Path('/proc/self/status').read_text().splitlines():
            if line.startswith('VmRSS:'):
                return int(line.split()[1])
    except OSError:
        pass
    return None


def stream(detector, audio, durations=None):
    events, scores = [], []
    for offset in range(0, len(audio), 160):
        started = time.perf_counter_ns()
        events.extend(detector.process(audio[offset:offset+160]))
        elapsed = time.perf_counter_ns()-started
        if durations is not None:
            durations.append(elapsed/1e6)
        scores.append(float(detector.last_score))
    return events, np.asarray(scores, dtype=np.float32)


def capture(detector, audio):
    events, scores = stream(detector, audio)
    return {'events': events, 'scores': scores, 'max_average': float(detector.max_score)}


def serial_capture(value):
    return {'events': value['events'], 'max_averaged_score': value['max_average'],
            'max_instantaneous_score': float(np.max(value['scores'], initial=0)),
            'score_samples': len(value['scores'])}


def controls(factory, output, seconds, environment=None):
    detector = factory()
    try:
        rng = np.random.default_rng(SEED)
        size = round(seconds*16000)
        cases = {'silence': np.zeros(size, dtype=np.int16),
                 'seeded_white_noise_rms100': np.clip(rng.normal(0, 100, size), -32768, 32767).astype(np.int16),
                 'seeded_white_noise_rms1000': np.clip(rng.normal(0, 1000, size), -32768, 32767).astype(np.int16)}
        if environment is not None:
            # Read once at CLI boundary; no gain, looping, padding or synthetic
            # replacement. Its limited source construction is recorded there.
            cases['recorded_environment_train_leadins'] = environment
        rows = {}
        for name, audio in cases.items():
            detector.reset()
            result = capture(detector, audio)
            np.save(output/(name+'.npy'), result['scores'])
            rows[name] = serial_capture(result)
            rows[name]['pcm_seconds'] = len(audio)/16000
            rows[name]['pcm_rms'] = float(np.sqrt(np.mean(audio.astype(np.float64)**2)))
        # Dirty every frontend, neural, threshold and refractory state first.
        dirty = np.clip(rng.normal(0, 4000, 16000*3), -32768, 32767).astype(np.int16)
        stream(detector, dirty)
        detector.reset()
        after_reset = capture(detector, cases['seeded_white_noise_rms1000'])
        fresh = factory()
        try:
            from_fresh = capture(fresh, cases['seeded_white_noise_rms1000'])
        finally:
            if isinstance(fresh, NativeCandidate):
                fresh.close()
        detector.reset()
        second_reset = capture(detector, cases['seeded_white_noise_rms1000'])
        reset = {'dirty_then_reset_equals_fresh_scores': bool(np.array_equal(after_reset['scores'], from_fresh['scores'])),
                 'dirty_then_reset_equals_fresh_events': after_reset['events'] == from_fresh['events'],
                 'repeated_reset_equals_fresh_scores': bool(np.array_equal(second_reset['scores'], from_fresh['scores'])),
                 'repeated_reset_equals_fresh_events': second_reset['events'] == from_fresh['events'],
                 'comparison': 'Exact within each runtime; does not loosen cross-runtime tolerance.'}
        detector.reset()
        stream(detector, cases['seeded_white_noise_rms100'][:16000])
        timings = []
        rss_before = rss_kib()
        started = time.perf_counter()
        stream(detector, cases['seeded_white_noise_rms1000'], timings)
        elapsed = time.perf_counter()-started
        cost = {'pcm_seconds': seconds, 'wall_seconds': elapsed,
                'wall_realtime_factor': elapsed/seconds,
                'process_call_ms': {'median': float(np.median(timings)), 'p95': float(np.percentile(timings, 95)),
                                    'max': float(np.max(timings)), 'calls': len(timings)},
                'current_rss_before_kib': rss_before, 'current_rss_after_kib': rss_kib(),
                'process_lifetime_maxrss_kib': resource.getrusage(resource.RUSAGE_SELF).ru_maxrss,
                'limits': 'Host wall timing includes Python adapter/frontend/inference; calls are 10ms PCM, NN stride 30ms. Shared process RSS and lifetime maximum include earlier models/runtimes and allocator retention; cannot attribute memory delta or arena usage to one model. Not ARM64 measurements.'}
        return {'cases': rows, 'reset': reset, 'resource': cost}
    finally:
        if isinstance(detector, NativeCandidate):
            detector.close()


def continuous(factory, events, roles, output):
    detector = factory()
    elapsed_samples = 0
    rows = []
    all_events = []
    gaps = []
    try:
        detector.reset()
        for event in sorted(events, key=lambda e: datetime.fromisoformat(e['activatedAt'])):
            role = roles[event['id']]
            path = private_output(event['audit']['trigger_path'])
            if sha256(path) != event['triggerContext']['sha256']:
                raise ValueError('Audited context changed')
            audio = read_wav(path)
            if len(audio) != event['triggerContext']['samples']:
                raise ValueError('Audited sample count changed')
            start = elapsed_samples
            detected, scores = stream(detector, audio)
            np.save(output/(event['id']+'.npy'), scores)
            elapsed_samples += len(audio)
            normalized = [{**e, 'stream_sample': round(e['timestamp']*16000),
                           'local_sample': round(e['timestamp']*16000)-start} for e in detected]
            decision = event['audit']['decision_sample_in_context']
            near = [e for e in normalized if decision-8000 <= e['local_sample'] <= min(len(audio), decision+3200)]
            rows.append({'id': event['id'], 'origin': role['origin'], 'split': role['split'],
                         'humanLabel': event['humanLabel'], 'start_stream_sample': start,
                         'samples': len(audio), 'decision_sample': decision,
                         'events': normalized, 'detected_any': bool(normalized),
                         'detected_near_decision': bool(near), 'near_decision_events': near,
                         'first_near_event_relative_to_original_decision_ms':
                         (near[0]['local_sample']-decision)/16 if near else None,
                         'max_instantaneous_score': float(np.max(scores, initial=0))})
            all_events.extend(normalized)
            # No reset between recordings, including crossings of data splits.
            gap_events, _ = stream(detector, np.zeros(48000, dtype=np.int16))
            gaps.append({'after_id': event['id'], 'events': gap_events})
            all_events.extend(gap_events)
            elapsed_samples += 48000
        grouped = defaultdict(list)
        for row in rows:
            grouped[(row['origin'], row['split'], row['humanLabel'])].append(row)
        summary = [{'origin': key[0], 'split': key[1], 'label': key[2], 'contexts': len(value),
                    'any_event_contexts': sum(r['detected_any'] for r in value),
                    'near_decision_event_contexts': sum(r['detected_near_decision'] for r in value),
                    'events': sum(len(r['events']) for r in value),
                    'near_decision_events': sum(len(r['near_decision_events']) for r in value)}
                   for key, value in sorted(grouped.items())]
        return {'contexts': rows, 'by_origin_split_label': summary, 'gap_events': gaps,
                'gap_event_count': sum(len(r['events']) for r in gaps),
                'total_events': len(all_events), 'stream_samples': elapsed_samples,
                'protocol': 'One clean initial state. Audited raw trigger_context, chronological order; 3s artificial silence after every context; no intervening reset. Includes buffer events, reported separately. Original postdecision 200ms retained for evaluation only. Cross-split state is intentionally a post-selection stress test. Not reconstruction of original conversations; no FP/hour inference.'}
    finally:
        if isinstance(detector, NativeCandidate):
            detector.close()


def verify_gate(selection_path, protocol_path, audit_path):
    selection = json.loads(selection_path.read_text())
    protocol = json.loads(protocol_path.read_text())
    audit = json.loads(audit_path.read_text())
    if protocol['audit_sha256'] != sha256(audit_path):
        raise ValueError('Protocol and audit differ')
    if selection.get('protocol_sha256') != sha256(protocol_path):
        raise ValueError('Selection is not sealed against this protocol')
    if not selection.get('decision'):
        raise ValueError('Selection has no sealed decision')
    roles = {r['id']: r for r in protocol['events']}
    if len(roles) != len(protocol['events']) or set(roles) != {e['id'] for e in audit['events']}:
        raise ValueError('Audit and protocol event inventory differ')
    for event in audit['events']:
        if roles[event['id']]['humanLabel'] != event['humanLabel']:
            raise ValueError('Protocol label changed')
    return audit, protocol, roles


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--model', action='append', required=True, help='Repeated NAME=/private/path/model.tflite')
    parser.add_argument('--audit', type=private_output, required=True)
    parser.add_argument('--protocol', type=private_output, required=True)
    parser.add_argument('--selection', type=private_output, required=True)
    parser.add_argument('--library', type=private_output)
    parser.add_argument('--environment-wav', type=private_output,
                        help='Private known-source environmental control; raw PCM16 mono16k, no amplitude change or repetition')
    parser.add_argument('--seconds', type=float, default=30)
    parser.add_argument('--output', type=private_output, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    if args.seconds < 3 or not np.isfinite(args.seconds):
        parser.error('--seconds must be finite and at least 3')
    # This gate is checked before model creation or opening any test WAV.
    audit, protocol, roles = verify_gate(args.selection, args.protocol, args.audit)
    environment = None
    if args.environment_wav:
        environment = read_wav(args.environment_wav)
        if not len(environment):
            parser.error('Environmental control is empty')
    models = {}
    for item in args.model:
        name, path = item.split('=', 1)
        if not name or any(c not in 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-' for c in name) or name in models:
            parser.error('Model names must be unique simple path components')
        models[name] = private_output(path)
    args.output.mkdir(mode=0o700, parents=True, exist_ok=False)
    report = {'schema': 1, 'created_at': datetime.now(timezone.utc).isoformat(), 'seed': SEED,
              'selection_sha256': sha256(args.selection), 'protocol_sha256': sha256(args.protocol),
              'audit_sha256': sha256(args.audit), 'script_sha256': sha256(Path(__file__)),
              'rules': RULES, 'models': {}, 'physical_arm64': 'pending'}
    if args.library:
        report['library_sha256'] = sha256(args.library)
    if args.environment_wav:
        report['environment_control'] = {'path':str(args.environment_wav), 'sha256':sha256(args.environment_wav),
            'samples':len(environment), 'rms':float(np.sqrt(np.mean(environment.astype(np.float64)**2))),
            'scope':'Caller supplies a private known-source control. This run preserves its bytes, amplitude and length without looping or padding. If constructed from short confirmed TRAIN lead-ins, joins are artificial; this is limited captured ambient audio, not continuous exposure or FP/hour evidence.'}
    for name, path in models.items():
        result = {'model_sha256': sha256(path), 'model_bytes': path.stat().st_size, 'runtimes': {}}
        report['models'][name] = result
        factories = {'litert': lambda path=path: MicroWakeWordDetector(model_path=path)}
        if args.library:
            factories['native_host'] = lambda path=path: NativeCandidate(args.library, path)
        for runtime, factory in factories.items():
            destination = args.output/name/runtime
            destination.mkdir(mode=0o700, parents=True)
            control_path, continuous_path = destination/'controls', destination/'continuous'
            control_path.mkdir(mode=0o700)
            continuous_path.mkdir(mode=0o700)
            result['runtimes'][runtime] = {'controls': controls(factory, control_path, args.seconds, environment),
                                           'continuous': continuous(factory, audit['events'], roles, continuous_path)}
        if 'native_host' in result['runtimes']:
            lite = result['runtimes']['litert']['continuous']['contexts']
            native = result['runtimes']['native_host']['continuous']['contexts']
            comparisons = []
            for a, b in zip(lite, native):
                if a['id'] != b['id']:
                    raise ValueError('Continuous runtime alignment differs')
                lid = args.output/name/'litert'/'continuous'/(a['id']+'.npy')
                nid = args.output/name/'native_host'/'continuous'/(b['id']+'.npy')
                x, y = np.load(lid), np.load(nid)
                if x.shape != y.shape:
                    raise ValueError('Continuous score shapes differ')
                delta = float(np.max(np.abs(x-y), initial=0))
                sa = [e['local_sample'] for e in a['events']]
                sb = [e['local_sample'] for e in b['events']]
                comparisons.append({'id': a['id'], 'max_score_delta': delta,
                                    'score_tolerance_1lsb_passed': delta <= 1/256+1e-9,
                                    'activated_context_equal': bool(sa) == bool(sb),
                                    'events_within_stride': len(sa) == len(sb) and all(abs(u-v) <= 480 for u,v in zip(sa,sb))})
            result['native_comparison'] = {'continuous_contexts': comparisons,
                                          'max_score_delta': max((c['max_score_delta'] for c in comparisons), default=0),
                                          'event_stride_mismatches': sum(not c['events_within_stride'] for c in comparisons),
                                          'note': 'Existing 1LSB diagnostic retained; event timing tolerance is one unchanged 30ms inference stride.'}
        (args.output/'controls.json').write_text(json.dumps(report, indent=2)+'\n')
        print(json.dumps({'model': name, 'done_runtimes': list(result['runtimes'])}), flush=True)


if __name__ == '__main__':
    main()
