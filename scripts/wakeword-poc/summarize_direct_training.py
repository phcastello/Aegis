#!/usr/bin/env python3
"""Summarize the direct human-label experiment without publishing private IDs.

No inference, label inference, eligibility filters, checkpoint changes or fitting.
The sealed validation selection is evaluated once against exploratory final data.
"""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import json
import os
from pathlib import Path

from prepare import private_output, sha256
from positive_summary import aggregate, paired, legacy_summary, legacy_gates, control_summary

MODELS = ('baseline', 'previous', 'new')
RUNTIMES = ('litert', 'native_host')
EXPECTED = {'train': {'correct': 91, 'false_positive': 130},
            'validation': {'correct': 11, 'false_positive': 15},
            'test': {'correct': 5, 'false_positive': 8}}
BASELINE_SHA = 'f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514'
PREVIOUS_SHA = '5b198451df030dbc2650cbfd20f9e29ed3ad88aecc52213d2dcf24e45f9594ff'


def read(path):
    return json.loads(path.read_text())


def safe_training(training):
    """Retain measured exposure, never private event-to-draw inventories."""
    result = {k: training[k] for k in ('plan', 'best_epoch', 'best_key', 'eligible',
        'params', 'frozen_batch_norm_layers', 'protocol_sha256',
        'features_manifest_sha256', 'training_script_sha256',
        'model_sha256', 'elapsed_seconds') if k in training}
    result['history'] = []
    for row in training['history']:
        safe = {k: value for k, value in row.items()
                if k not in ('real_event_draws', 'real_validation_paired_losses')}
        safe['real_validation_paired_loss_counts'] = {
            runtime: len(ids) for runtime, ids in row['real_validation_paired_losses'].items()}
        safe['real_event_exposure'] = {}
        for label, draws in row['real_event_draws'].items():
            values = list(draws.values())
            safe['real_event_exposure'][label] = {
                'original_events': len(values), 'presentations': sum(values),
                'minimum_per_event': min(values, default=0),
                'maximum_per_event': max(values, default=0)}
        result['history'].append(safe)
    return result


def native_summary(evaluation):
    rows = evaluation.get('native_comparison', {}).get('cases', [])
    return {'contexts': len(rows),
            'max_score_delta': max((r['max_score_delta'] for r in rows), default=None),
            'one_lsb_failures': sum(not r['score_tolerance_1lsb_passed'] for r in rows),
            'activated_context_mismatches': sum(not r['activated_file_equal'] for r in rows),
            'event_stride_mismatches': sum(not r['events_within_stride'] for r in rows),
            'diagnostic_tolerance_lsb': 1, 'physical_arm64': 'pending'}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=private_output, required=True)
    parser.add_argument('--output', type=private_output, required=True)
    parser.add_argument('--controls', type=private_output)
    args = parser.parse_args()
    os.umask(0o077)
    root = args.root
    prep = root/'preparation-v1'
    audit_path, protocol_path = prep/'audit/inventory.json', prep/'protocol.json'
    feature_path = prep/'features/features.json'
    selection_path = root/'selection-v1.json'
    selection = read(selection_path)
    selected_directory = selection['selected_model_directory']
    if selected_directory not in ('model-v1', 'model-v2'):
        raise ValueError('Unknown sealed model directory')
    training_path = root/selected_directory/'training.json'
    audit, technical = read(audit_path), read(prep/'audit/summary.json')
    protocol, features = read(protocol_path), read(feature_path)
    training = read(training_path)
    protocol_sha = sha256(protocol_path)
    if (protocol['audit_sha256'] != sha256(audit_path)
            or features['protocol_sha256'] != protocol_sha
            or training['protocol_sha256'] != protocol_sha
            or selection['protocol_sha256'] != protocol_sha
            or training['features_manifest_sha256'] != sha256(feature_path)
            or ('training_sha256' in selection
                and selection['training_sha256'] != sha256(training_path))):
        raise ValueError('Audit/features/fit/selection provenance differs')
    if (not selection.get('decision') or selection['selected_epoch'] != training['best_epoch']
            or selection['model_sha256'] != training['model_sha256']
            or selection['eligible'] != training['eligible']):
        raise ValueError('Selection and completed fit differ')
    roles = {r['id']: r for r in protocol['events']}
    events = {r['id']: r for r in audit['events']}
    counts = {split: dict(Counter(r['humanLabel'] for r in roles.values() if r['split'] == split))
              for split in EXPECTED}
    if len(roles) != 260 or len(events) != 260 or set(roles) != set(events) or counts != EXPECTED:
        raise ValueError('Supplied split/event inventory differs')
    if any(roles[key]['split'] != row['split'] or roles[key]['humanLabel'] != row['humanLabel']
           for key, row in events.items()):
        raise ValueError('Supplied human labels or splits changed')
    if (features['training_original_events'] != 221 or features['excluded_training_events'] != 0
            or features['training_original_by_class'] != EXPECTED['train']):
        raise ValueError('Training did not include all supplied221 events')
    for epoch in training['history']:
        if (len(epoch['real_event_draws']['real_positive']) != 91
                or len(epoch['real_event_draws']['real_negative']) != 130
                or any(v < 1 for draws in epoch['real_event_draws'].values() for v in draws.values())):
            raise ValueError('An original TRAIN event was omitted from an epoch')
    expected_models = {'baseline': BASELINE_SHA, 'previous': PREVIOUS_SHA,
                       'new': training['model_sha256']}
    evaluations = {name: read(root/f'eval-{name}-final/evaluation.json') for name in MODELS}
    for name, ev in evaluations.items():
        if (ev['model_sha256'] != expected_models[name] or ev['split'] != 'all'
                or ev['split_manifest_sha256'] != protocol_sha
                or ev['audit_sha256'] != sha256(audit_path)):
            raise ValueError('Final evaluation model or provenance differs')
        for runtime in RUNTIMES:
            rows = ev['runtimes'][runtime]['contexts']['events']
            if len(rows) != 260 or {r['id'] for r in rows} != set(events):
                raise ValueError('Final paired event/runtime coverage is incomplete')
            if any(r['split'] != roles[r['id']]['split'] or r['humanLabel'] != roles[r['id']]['humanLabel']
                   for r in rows):
                raise ValueError('Final evaluation changed a human split/label')
            if 'legacy' not in ev['runtimes'][runtime]:
                raise ValueError('Known legacy regression suite is missing')
    controls_path = args.controls or root/'controls-v1/controls.json'
    controls = read(controls_path) if controls_path.exists() else None
    if controls and (controls['selection_sha256'] != sha256(selection_path)
                     or controls['protocol_sha256'] != protocol_sha):
        raise ValueError('Controls refer to a different selection or protocol')
    public = {'schema': 1, 'experiment': 'direct-owner-labelled-260',
        'decision': None, 'production_approved': False,
        'selection': {k: selection[k] for k in ('selected_model_directory', 'selected_epoch', 'eligible', 'decision', 'model_sha256')},
        'split_counts': counts, 'training': safe_training(training), 'models': {},
        'preparation': {'training_original_events': 221,
            'training_original_by_class': features['training_original_by_class'],
            'derived_training_windows': features['derived_training_windows'],
            'excluded_training_events': 0,
            'source_pool_windows': {key: len(value) for key, value in features['source_indices'].items()},
            'frontend': features['frontend'], 'corpus_policy': features['corpus_policy'],
            'policy': 'humanLabel and ZIP splits authoritative; no acoustic exclusions'},
        'technical_audit': {k: technical[k] for k in ('complete', 'zip_sha256', 'zip_bytes',
            'events', 'wav_files', 'counts', 'crc_ok', 'sha256sums_present_and_verified',
            'csv_jsonl_verified', 'format', 'exact_overlap_events', 'training_events_included',
            'excluded_training_events', 'cross_split_group_ids_count', 'cross_split_session_ids_count') if k in technical},
        'provenance': {'zip_sha256': audit['zip_sha256'], 'audit_sha256': sha256(audit_path),
            'audit_summary_sha256': sha256(prep/'audit/summary.json'), 'protocol_sha256': protocol_sha,
            'features_manifest_sha256': sha256(feature_path), 'training_sha256': sha256(training_path),
            'selection_sha256': sha256(selection_path), 'summary_script_sha256': sha256(Path(__file__))},
        'limits': ['The exact owner-provided splits were preserved, without redistributing adjacent recordings.',
            'Events share short capture sessions; split assignment does not establish acoustic independence.',
            'All260 events were inspected in earlier experiments; test13 is exploratory known data.',
            'Training recognition is an adjustment diagnostic, not held-out performance.',
            'Activation-selected positives measure conditional redetection, not population recall.',
            'False-positive replay counts are not false activations/hour.',
            'Human labels are the reference; no ASR, DTW or acoustic eligibility gates are used.',
            'One-LSB numerical tolerance is unchanged; native loading is not numerical equality.',
            'Host replay does not establish physical Android ARM64 approval.']}
    public['training_runs'] = {}
    for directory in ('model-v1', 'model-v2'):
        path = root/directory/'training.json'
        if not path.exists():
            continue
        run = read(path)
        if (run['protocol_sha256'] != protocol_sha
                or run['features_manifest_sha256'] != sha256(feature_path)):
            raise ValueError('A completed fit uses different prepared data')
        public['training_runs'][directory] = safe_training(run)
        public['provenance'][directory+'_training_sha256'] = sha256(path)
    if selected_directory not in public['training_runs']:
        raise ValueError('Selected fit is not complete')
    private = {'schema': 1, 'created_at': datetime.now(timezone.utc).isoformat(),
               'root': str(root), 'paired_event_ids': {}, 'final_paths': {
                   name: str(root/f'eval-{name}-final/evaluation.json') for name in MODELS}}
    # Earlier fitted examples can now belong to any owner-provided split.
    # Report this overlap rather than changing or excluding supplied events.
    previous_training_path = Path.home()/'.cache/aegis-wakeword-positive-retraining/model-A-v2/training.json'
    if previous_training_path.exists():
        previous_fit = read(previous_training_path)
        if previous_fit['model_sha256'] != PREVIOUS_SHA:
            raise ValueError('Previous comparator training manifest differs from its model')
        overlap = Counter()
        source_ids = defaultdict(set)
        for epoch in previous_fit['history'][:previous_fit['best_epoch']]:
            source_ids['previous-real-positive'].update(epoch['real_event_draws'])
            source_ids['previous-hard-negative'].update(epoch['hard_event_draws'])
        overlap_ids = []
        for previous_role, ids in source_ids.items():
            for event_id in ids & set(roles):
                role = roles[event_id]
                overlap[(previous_role, role['split'], role['humanLabel'])] += 1
                overlap_ids.append({'id': event_id, 'previous_training_role': previous_role,
                                    'split': role['split'], 'humanLabel': role['humanLabel']})
        public['previous_comparator_training_exposure'] = {
            'checkpoint_epoch': previous_fit['best_epoch'],
            'previous_real_source_events': {key: len(ids) for key, ids in source_ids.items()},
            'overlap_with_supplied_splits': [{'previous_training_role': key[0],
                'split': key[1], 'humanLabel': key[2], 'events': count}
                for key, count in sorted(overlap.items())],
            'interpretation': 'The previous comparator may have fitted examples now assigned to validation/test. Splits remain unchanged; this is a known exploratory comparison.'}
        private['previous_comparator_exposed_ids'] = overlap_ids
        public['provenance']['previous_training_manifest_sha256'] = sha256(previous_training_path)
    else:
        public['previous_comparator_training_exposure'] = {'verified': False,
            'reason': 'Earlier training manifest unavailable; comparator exposure cannot be independently reconstructed.'}
    for name, ev in evaluations.items():
        output = {'model_sha256': ev['model_sha256'], 'model_bytes': ev['model_bytes'],
                  'runtimes': {}, 'native_comparison': native_summary(ev),
                  'native_host_loaded': 'native_host' in ev['runtimes']}
        public['models'][name] = output
        public['provenance'][name+'_final_sha256'] = sha256(root/f'eval-{name}-final/evaluation.json')
        private['paired_event_ids'][name] = {}
        for runtime in RUNTIMES:
            rows = ev['runtimes'][runtime]['contexts']['events']
            base = evaluations['baseline']['runtimes'][runtime]['contexts']['events']
            bmap = {row['id']: row for row in base}
            private['paired_event_ids'][name][runtime] = []
            groups = []
            for split in EXPECTED:
                for label in ('correct', 'false_positive'):
                    selected = [row for row in rows if row['split'] == split and row['humanLabel'] == label]
                    cmap = {row['id']: row for row in selected}
                    pair, secrets = paired({key: bmap[key] for key in cmap}, cmap)
                    for field in ('detected_near_decision', 'detected_any'):
                        pair[field]['baseline_calls_lost' if label == 'correct' else 'baseline_fp_suppressed'] = pair[field]['baseline_only']
                        pair[field]['calls_gained' if label == 'correct' else 'new_fp_introduced'] = pair[field]['candidate_only']
                    metric = aggregate(selected)
                    metric.update({'split': split, 'label': label, 'paired_to_baseline': pair,
                                   'held_out': split != 'train'})
                    if label == 'false_positive':
                        metric['rejected_near_decision'] = metric['contexts']-metric['recognized_near_decision']
                        metric['rejected_in_entire_context'] = metric['contexts']-metric['contexts_with_any_event']
                    groups.append(metric)
                    private['paired_event_ids'][name][runtime].append({
                        'split': split, 'label': label, 'paired': secrets})
            legacy = ev['runtimes'][runtime]['legacy']
            base_legacy = evaluations['baseline']['runtimes'][runtime]['legacy']
            output['runtimes'][runtime] = {'by_split_label': groups,
                'legacy': legacy_summary(legacy), 'legacy_paired_to_baseline': legacy_gates(base_legacy, legacy)}
        if controls and name in controls['models']:
            if controls['models'][name]['model_sha256'] != ev['model_sha256']:
                raise ValueError('Controls and final replay models differ')
            output['controls'], output['control_gates_advisory'] = control_summary(controls, name, 'baseline')
    gates = {'validation_eligible': selection['eligible'], 'per_runtime': {}}
    for runtime in RUNTIMES:
        run = public['models']['new']['runtimes'][runtime]
        groups = run['by_split_label']
        positive = [row for row in groups if row['label'] == 'correct' and row['split'] in ('validation', 'test')]
        test_negative = next(row for row in groups if row['label'] == 'false_positive' and row['split'] == 'test')
        pair = test_negative['paired_to_baseline']['detected_near_decision']
        any_pair = test_negative['paired_to_baseline']['detected_any']
        lg = run['legacy_paired_to_baseline']
        gates['per_runtime'][runtime] = {
            'preserve_baseline_positive_calls_validation_and_test': all(
                row['paired_to_baseline']['detected_near_decision']['baseline_only'] == 0 for row in positive),
            'test_fp_contexts_strictly_improve': pair['candidate'] < pair['baseline'],
            'test_no_new_fp_contexts_near_decision': pair['candidate_only'] == 0,
            'test_fp_contexts_strictly_improve_anywhere': any_pair['candidate'] < any_pair['baseline'],
            'test_no_new_fp_contexts_anywhere': any_pair['candidate_only'] == 0,
            'known_legacy23_preserved_isolated_and_continuous':
                lg['quality_positive_losses'] == 0 and lg['continuous_positive_losses'] == 0,
            'native_compatible': public['models']['new']['native_host_loaded']}
    gates['all_passed'] = gates['validation_eligible'] and all(
        all(checks.values()) for checks in gates['per_runtime'].values())
    public['final_experimental_gates'] = gates
    public['decision'] = 'GO-EXPERIMENTAL' if gates['all_passed'] else 'NO-GO'
    public['decision_scope'] = ('Exploratory comparison of the sealed validation checkpoint only. '
        'The13-event test does not establish production performance. No automatic promotion, installation or publication is authorized.')
    if controls:
        public['provenance']['controls_sha256'] = sha256(controls_path)
    args.output.mkdir(parents=True, mode=0o700, exist_ok=False)
    private['public_summary'] = public
    for filename, value in (('public_summary.json', public), ('private_summary.json', private)):
        (args.output/filename).write_text(json.dumps(value, indent=2, ensure_ascii=False)+'\n')
    print(json.dumps({'decision': public['decision'], 'best_epoch': training['best_epoch'],
                      'output': str(args.output), 'public_summary_sha256': sha256(args.output/'public_summary.json')}, indent=2))


if __name__ == '__main__':
    main()
