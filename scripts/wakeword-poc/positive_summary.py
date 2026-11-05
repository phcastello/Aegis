#!/usr/bin/env python3
"""Summarize sealed local replays, separating publishable aggregates from IDs.

This script performs no inference or training. It never changes selection after
consulting final results; final gates only approve or reject the sealed choice.
"""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import json
import os
from pathlib import Path

import numpy as np

from prepare import private_output, sha256

MODELS = ('baseline', 'previousA', 'previousC', 'A', 'B')
RUNTIMES = ('litert', 'native_host')


def read(path):
    return json.loads(path.read_text())


def statistics(values):
    values = [v for v in values if v is not None and np.isfinite(v)]
    return {'n': len(values), 'min': float(np.min(values)) if values else None,
            'median': float(np.median(values)) if values else None,
            'mean': float(np.mean(values)) if values else None,
            'max': float(np.max(values)) if values else None}


def latency(row):
    near = row['near_decision_events']
    return (near[0]['sample']-row['decision_sample_in_context'])/16 if near else None


def aggregate(rows):
    return {'contexts': len(rows), 'recognized_near_decision': sum(r['detected_near_decision'] for r in rows),
            'missed_near_decision': sum(not r['detected_near_decision'] for r in rows),
            'contexts_with_any_event': sum(r['detected_any'] for r in rows),
            'near_decision_events': sum(len(r['near_decision_events']) for r in rows),
            'all_events': sum(len(r['events']) for r in rows),
            'additional_events_outside_near_decision': sum(len(r['events'])-len(r['near_decision_events']) for r in rows),
            'max_averaged_score': statistics([r['max_averaged_score'] for r in rows]),
            'first_near_event_relative_to_original_decision_ms': statistics([latency(r) for r in rows]),
            'latency_note': 'Relative to original detector decision, not verified phonetic keyword end.'}


def paired(baseline, candidate):
    if set(baseline) != set(candidate):
        raise ValueError('Paired inventories differ')
    output = {}
    private = {}
    for field in ('detected_near_decision', 'detected_any'):
        lost = [k for k in baseline if baseline[k][field] and not candidate[k][field]]
        added = [k for k in baseline if not baseline[k][field] and candidate[k][field]]
        nb, nc = sum(r[field] for r in baseline.values()), sum(r[field] for r in candidate.values())
        output[field] = {'contexts': len(baseline), 'baseline': nb, 'candidate': nc,
                         'baseline_only': len(lost), 'candidate_only': len(added),
                         'relative_net_reduction': (nb-nc)/nb if nb else None,
                         'baseline_reproduced_suppression_fraction': len(lost)/nb if nb else None}
        private[field] = {'baseline_only_ids': lost, 'candidate_only_ids': added}
    both = [k for k in baseline if baseline[k]['detected_near_decision'] and candidate[k]['detected_near_decision']]
    output['paired_common_detection_latency_delta_ms'] = statistics([latency(candidate[k])-latency(baseline[k]) for k in both])
    output['paired_max_averaged_score_delta'] = statistics([candidate[k]['max_averaged_score']-baseline[k]['max_averaged_score'] for k in baseline])
    return output, private


def safe_training(training):
    safe = {k: training[k] for k in ('strategy', 'config', 'params', 'trainable_params',
            'frozen_batch_norm_layers', 'baseline_original_validation_recall', 'best_epoch',
            'best_key', 'model_sha256', 'initializer_sha256', 'optimizer_resumed', 'elapsed_seconds',
            'script_sha256', 'protocol_sha256', 'features_manifest_sha256') if k in training}
    safe['history'] = []
    for row in training['history']:
        item = {k: v for k,v in row.items() if k not in ('real_event_draws', 'hard_event_draws', 'real_validation_paired_losses')}
        item['real_validation_paired_loss_counts'] = {k: len(v) for k,v in row['real_validation_paired_losses'].items()}
        for field in ('real_event_draws', 'hard_event_draws'):
            counts = list(row[field].values())
            item[field+'_summary'] = {'unique_original_events': len(counts), 'presentations': sum(counts),
                                     'minimum_per_event': min(counts, default=0), 'maximum_per_event': max(counts, default=0)}
        safe['history'].append(item)
    return safe


def legacy_summary(value):
    return {k: value[k] for k in ('quality', 'continuous', 'public', 'protocol')}


def legacy_gates(base, cand):
    result = {}
    for suite, field in (('quality', 'independent_events'), ('continuous', 'continuous_events')):
        b = {r['id']:r for r in base[field]}
        c = {r['id']:r for r in cand[field]}
        if set(b) != set(c):
            raise ValueError('Legacy paired inventory differs')
        result[suite+'_positive_losses'] = sum(r['label']=='positive' and r['detected'] and not c[k]['detected'] for k,r in b.items())
        result[suite+'_new_negative_contexts'] = sum(r['label']=='negative' and not r['detected'] and c[k]['detected'] for k,r in b.items())
    result['public_events_no_increase'] = cand['public']['events'] <= base['public']['events']
    return result


def control_summary(controls, model, baseline):
    current = controls['models'][model]
    reference = controls['models'][baseline]
    result, gates = {}, {}
    for runtime in RUNTIMES:
        run = current['runtimes'][runtime]
        base = reference['runtimes'][runtime]
        values = run['controls']
        check = values['reset']
        exact = all(check[k] for k in ('dirty_then_reset_equals_fresh_scores', 'dirty_then_reset_equals_fresh_events',
                                      'repeated_reset_equals_fresh_scores', 'repeated_reset_equals_fresh_events'))
        new_control_events = {k: max(0,len(v['events'])-len(base['controls']['cases'][k]['events'])) for k,v in values['cases'].items()}
        bc = base['controls']['resource']['wall_realtime_factor']
        cc = values['resource']['wall_realtime_factor']
        # Retain numeric/control aggregates only; per-context IDs stay private.
        control_cases = {k: {key:value for key,value in v.items() if key != 'events'} | {'events':len(v['events'])} for k,v in values['cases'].items()}
        con = run['continuous'];bcon = base['continuous']
        bb = {r['id']:r for r in bcon['contexts']};ss = {r['id']:r for r in con['contexts']}
        if set(bb) != set(ss):
            raise ValueError('Continuous merged paired inventory differs')
        losses = defaultdict(int)
        for key,b in bb.items():
            reserved = b['origin']=='old161' or b['split'] in ('validation','test')
            if reserved and b['humanLabel']=='correct' and b['detected_near_decision'] and not ss[key]['detected_near_decision']:
                losses[b['origin']+'/'+b['split']] += 1
        result[runtime] = {'controls': control_cases, 'reset': check, 'resource': values['resource'],
                           'wall_realtime_factor_ratio_to_baseline': cc/bc if bc else None,
                           'new_control_event_counts_vs_baseline': new_control_events,
                           'continuous_merged': {'by_origin_split_label': con['by_origin_split_label'],
                                                  'gap_events':con['gap_event_count'], 'total_events':con['total_events'],
                                                  'positive_paired_losses_reserved':dict(losses), 'protocol':con['protocol']}}
        gates[runtime] = {'exact_reset': exact, 'no_new_silence_or_noise_events': not any(new_control_events.values()),
                          'continuous_merged_reserved_positive_preservation': not any(losses.values())}
    comparison = current.get('native_comparison')
    if comparison:
        result['native_continuous_comparison'] = {k:v for k,v in comparison.items() if k != 'continuous_contexts'}
        result['native_continuous_comparison']['score_1lsb_failures'] = sum(not r['score_tolerance_1lsb_passed'] for r in comparison['continuous_contexts'])
    return result, gates


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=private_output, required=True)
    parser.add_argument('--output', type=private_output, required=True, help='New private directory for safe and private summaries')
    parser.add_argument('--controls', type=private_output)
    args = parser.parse_args()
    os.umask(0o077)
    root = args.root
    audit_path, protocol_path = root/'audit-v1/inventory.json', root/'protocol-v1.json'
    audit = read(audit_path);audit_result = read(root/'audit-v1/audit.json');protocol = read(protocol_path)
    selection = read(root/'selection-v1.json')
    if protocol['audit_sha256'] != sha256(audit_path) or selection['protocol_sha256'] != sha256(protocol_path):
        raise ValueError('Frozen provenance does not match')
    if not selection.get('decision'):
        raise ValueError('No sealed selection decision')
    roles = {r['id']:r for r in protocol['events']}
    events = {e['id']:e for e in audit['events']}
    if set(roles) != set(events) or len(roles) != 260:
        raise ValueError('Event inventory differs')
    feature_path = root/'features-v1/features.json';features = read(feature_path)
    if features['protocol_sha256'] != sha256(protocol_path):
        raise ValueError('Features refer to another protocol')
    evaluations = {name:read(root/f'eval-{name}-final/evaluation.json') for name in MODELS}
    for name, evaluation in evaluations.items():
        if evaluation['split_manifest_sha256'] != sha256(protocol_path) or evaluation['split'] != 'all':
            raise ValueError('Final evaluation does not cover frozen all-split protocol')
        for runtime in RUNTIMES:
            rows = evaluation['runtimes'][runtime]['contexts']['events']
            if len(rows) != 260 or {r['id'] for r in rows} != set(events):
                raise ValueError('Final runtime/event inventory incomplete')
    controls_path = args.controls or root/'controls-v1/controls.json'
    controls = read(controls_path) if controls_path.exists() else None
    if controls and (controls['selection_sha256'] != sha256(root/'selection-v1.json') or controls['protocol_sha256'] != sha256(protocol_path)):
        raise ValueError('Control provenance differs')
    args.output.mkdir(mode=0o700, parents=True, exist_ok=False)
    public = {'schema':1, 'selection': {k:selection.get(k) for k in ('selected_strategy','decision','eligible')},
              'provenance': {'protocol_sha256':sha256(protocol_path), 'audit_inventory_sha256':sha256(audit_path),
                             'audit_summary_sha256':sha256(root/'audit-v1/audit.json'), 'features_manifest_sha256':sha256(feature_path),
                             'selection_sha256':sha256(root/'selection-v1.json'), 'dataset_zip_sha256':audit['zip_sha256']},
              'audit':{}, 'split_counts':[], 'training':{}, 'models':{}, 'old15_event_matrix':[], 'final_candidate_gates':{},
              'limits': ['Activation-selected positives measure conditional redetection, not population recall.',
                         'False-positive reductions are paired replay counts, not FP/hour.',
                         'Old sets and old splits are known regression data, not new blind validation.',
                         'New temporal splits share one short session; independence and generalization are limited.',
                         'Local acoustic/ASR screening does not constitute human phonetic alignment.',
                         'One-LSB runtime diagnostic remains unchanged even when baseline fails.',
                         'Resources and paired latency are advisory; no new post-test threshold or resource limit is introduced.',
                         'Host native replay does not establish physical ARM64 compatibility; prospective physical testing remains pending.']}
    private = {'schema':1, 'root':str(root), 'created_at':datetime.now(timezone.utc).isoformat(),
               'old15_alias_mapping':{}, 'paired_event_ids':{}, 'evaluation_paths':{name:str(root/f'eval-{name}-final/evaluation.json') for name in MODELS}}
    safe_audit_fields = ('complete','zip_sha256','zip_bytes','members','member_sha256_checked','crc_ok','events','wav_files',
                         'labels','by_source','old_metadata_unchanged','old_roles_recovered','all_wav_formats_pcm16_mono_16khz',
                         'all_declared_hashes_match','csv_jsonl_all_fields_match','overlap_exact_events','unique_group_ids',
                         'unique_session_ids','models','app_versions','builds','issues','missing_endedAt','duration_seconds',
                         'duration_ranges','new_session','similarity_protocol')
    public['audit'] = {k:audit_result[k] for k in safe_audit_fields if k in audit_result}
    public['provenance']['executed_audit_script_sha256'] = audit_result['script_sha256']
    public['provenance']['executed_audit_helper_sha256'] = audit_result['helper_sha256']
    for key in ('exact_pcm_duplicate_groups','near_duplicate_pairs','near_duplicate_post_pairs_without_first_500ms','near_duplicate_predecision_context_pairs'):
        public['audit'][key+'_count'] = len(audit_result[key])
    split_counts = Counter((r['origin'],r['split'],r['humanLabel']) for r in roles.values())
    public['split_counts'] = [{'origin':o,'split':s,'label':l,'events':n} for (o,s,l),n in sorted(split_counts.items())]
    included = [r for r in features['event_roles'] if r['included']]
    new_train_positive = [r for r in features['event_roles'] if r['origin']=='new99' and r['split']=='train' and r['label']=='correct']
    public['feature_counts'] = {'new_original_positive_events_in_training': len(included),
                                'new_positive_derived_windows': features['derived_real_windows'],
                                'new_train_positive_events_excluded': len(new_train_positive)-len(included),
                                'training_positive_exclusion_reasons':dict(Counter(reason for r in new_train_positive if not r['included'] for reason in r.get('screen_reasons',[r.get('reason','unknown')]))),
                                'old15_positive_events_fitted':0,
                                'source_pool_windows':{k:len(v) for k,v in features['source_indices'].items()},
                                'reused_old_hard_negative_events':len({r['id'] for r in features['hard_windows']}),
                                'reused_old_hard_negative_windows':len(features['hard_windows'])}
    for name in ('A','B'):
        path = root/f'model-{name}-v2/training.json'
        public['training'][name] = safe_training(read(path))
        if public['training'][name]['model_sha256'] != evaluations[name]['model_sha256']:
            raise ValueError('Final evaluation model differs from selected fit checkpoint')
        public['provenance'][name+'_training_report_sha256'] = sha256(path)
    old15 = sorted([e for k,e in events.items() if roles[k]['origin']=='old161' and e['humanLabel']=='correct'], key=lambda e:datetime.fromisoformat(e['activatedAt']))
    if len(old15) != 15:
        raise ValueError('Expected exactly fifteen old conditional positives')
    aliases = {e['id']:f'old-P{i:02d}' for i,e in enumerate(old15,1)}
    private['old15_alias_mapping'] = {alias:key for key,alias in aliases.items()}
    for name, ev in evaluations.items():
        model = {'model_sha256':ev['model_sha256'],'model_bytes':ev['model_bytes'], 'runtimes':{}}
        public['models'][name] = model
        public['provenance'][name+'_final_evaluation_sha256'] = sha256(root/f'eval-{name}-final/evaluation.json')
        private['paired_event_ids'][name] = {}
        for runtime in RUNTIMES:
            rows = ev['runtimes'][runtime]['contexts']['events']
            base_rows = evaluations['baseline']['runtimes'][runtime]['contexts']['events']
            bmap = {r['id']:r for r in base_rows};cmap = {r['id']:r for r in rows}
            groups = defaultdict(list)
            for row in rows:
                role = roles[row['id']]
                groups[(role['origin'],role['split'],row['humanLabel'])].append(row)
            grouped = []
            private['paired_event_ids'][name][runtime] = []
            for key, group in sorted(groups.items()):
                ids = {r['id'] for r in group}
                pair, secrets = paired({k:bmap[k] for k in ids},{k:cmap[k] for k in ids})
                label = key[2]
                for criterion in ('detected_near_decision','detected_any'):
                    pair[criterion]['baseline_calls_lost' if label=='correct' else 'baseline_fp_suppressed'] = pair[criterion]['baseline_only']
                    pair[criterion]['calls_gained' if label=='correct' else 'new_fp_introduced'] = pair[criterion]['candidate_only']
                grouped.append({'origin':key[0],'split':key[1],'label':label,**aggregate(group),'paired_to_baseline':pair})
                private['paired_event_ids'][name][runtime].append({'origin':key[0],'split':key[1],'label':label,'paired':secrets})
            model['runtimes'][runtime] = {'by_origin_split_label':grouped,
                                         'legacy':legacy_summary(ev['runtimes'][runtime]['legacy']),
                                         'legacy_paired_to_baseline':legacy_gates(evaluations['baseline']['runtimes'][runtime]['legacy'],ev['runtimes'][runtime]['legacy'])}
            for event in old15:
                row = cmap[event['id']]
                public['old15_event_matrix'].append({'event':aliases[event['id']], 'split':roles[event['id']]['split'],
                    'model':name,'runtime':runtime,'detected_near_decision':row['detected_near_decision'],
                    'detected_any':row['detected_any'],'max_averaged_score':row['max_averaged_score'],
                    'latency_relative_to_original_decision_ms':latency(row)})
        comparisons = ev.get('native_comparison',{}).get('cases',[])
        model['native_comparison'] = {'contexts':len(comparisons),
            'max_score_delta':max((c['max_score_delta'] for c in comparisons),default=None),
            'one_lsb_failures':sum(not c['score_tolerance_1lsb_passed'] for c in comparisons),
            'event_stride_mismatches':sum(not c['events_within_stride'] for c in comparisons),
            'activated_context_mismatches':sum(not c['activated_file_equal'] for c in comparisons),
            'physical_arm64':'pending'}
        if controls and name in controls['models']:
            if controls['models'][name]['model_sha256'] != ev['model_sha256']:
                raise ValueError('Control and final replay models differ')
            model['controls'], control_gates = control_summary(controls,name,'baseline')
        else:
            control_gates = None
        if name not in ('A','B'):
            continue
        gates = {'per_runtime':{},'controls_available':control_gates is not None}
        for runtime in RUNTIMES:
            bg = model['runtimes'][runtime]['by_origin_split_label']
            positives = [g for g in bg if g['label']=='correct' and (g['origin']=='old161' or g['split'] in ('validation','test'))]
            oldtest = next(g for g in bg if g['origin']=='old161' and g['split']=='test' and g['label']=='false_positive')
            p = oldtest['paired_to_baseline']['detected_near_decision']
            lg = model['runtimes'][runtime]['legacy_paired_to_baseline']
            rt = {'preserve_old15_and_new_reserved_baseline_calls':all(g['paired_to_baseline']['detected_near_decision']['baseline_only']==0 for g in positives),
                  'old64_fp_reduction_at_least_half':p['baseline']>0 and p['candidate']<=p['baseline']*.5,
                  'old64_no_new_fp_contexts':p['candidate_only']==0,
                  'legacy23_quality_and_continuous_preserved':lg['quality_positive_losses']==0 and lg['continuous_positive_losses']==0,
                  'legacy8_no_new_negative_contexts':lg['quality_new_negative_contexts']==0 and lg['continuous_new_negative_contexts']==0,
                  'public35min_no_event_count_increase':lg['public_events_no_increase']}
            if control_gates:
                rt.update(control_gates[runtime])
            gates['per_runtime'][runtime] = rt
        gates['all_final_gates_passed'] = gates['controls_available'] and all(all(rt.values()) for rt in gates['per_runtime'].values())
        public['final_candidate_gates'][name] = gates
    selected = selection['selected_strategy']
    public['decision'] = 'GO' if selection.get('eligible') is True and public['final_candidate_gates'][selected]['all_final_gates_passed'] else 'NO-GO'
    public['decision_scope'] = 'Experimental evidence for the sealed selected candidate only; no automatic production replacement, Android import, or physical ARM64 homologation.'
    public['physical_tests_pending'] = ['Physical ARM64 replay/import compatibility and resources.',
                                        'Prospective continuous collection of known duration, with missed attempted calls annotated.']
    if controls:
        public['provenance']['controls_sha256'] = sha256(controls_path)
    public['provenance']['summary_script_sha256'] = sha256(Path(__file__))
    private['public_summary'] = public
    for filename, value in (('public_summary.json',public),('private_summary.json',private)):
        (args.output/filename).write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({'decision':public['decision'],'selected_strategy':selected,
                      'public_summary_sha256':sha256(args.output/'public_summary.json'),
                      'output':str(args.output)},indent=2))


if __name__ == '__main__':
    main()
