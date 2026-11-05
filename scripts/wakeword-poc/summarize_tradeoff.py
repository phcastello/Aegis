#!/usr/bin/env python3
"""Summarize frozen tradeoff replays; private identities never enter public JSON.

No fitting, threshold search or selection occurs here. Known historical subsets
are diagnostic regressions and may overlap the current training split.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import json
import os
from pathlib import Path

from prepare import private_output, sha256
from positive_summary import aggregate, paired, legacy_summary
from summarize_direct_training import native_summary
from evaluate_hardneg import RULES

PARENT = Path.home()/'.cache/aegis-wakeword-human-label-training'
OLD = Path.home()/'.cache/aegis-wakeword-hardneg/protocol-v3.json'
RUNTIMES = ('litert', 'native_host')
EXPECTED = {'train': {'correct': 91, 'false_positive': 130},
            'validation': {'correct': 11, 'false_positive': 15},
            'test': {'correct': 5, 'false_positive': 8}}


def read(path):
    return json.loads(path.read_text())


def metrics(base, rows):
    """Paired positive near-decision / negative any-position product cost."""
    bm, cm = {r['id']: r for r in base}, {r['id']: r for r in rows}
    if len(bm) != len(base) or len(cm) != len(rows) or bm.keys() != cm.keys():
        raise ValueError('Paired inventory mismatch or duplicate')
    changes = {'fp_eliminated': [], 'fp_introduced': [],
               'positives_lost': [], 'positives_recovered': []}
    for key, b in bm.items():
        c = cm[key]
        if (b['split'], b['humanLabel']) != (c['split'], c['humanLabel']):
            raise ValueError('Paired split/label changed')
        positive = b['humanLabel'] == 'correct'
        field = 'detected_near_decision' if positive else 'detected_any'
        if b[field] and not c[field]:
            changes['positives_lost' if positive else 'fp_eliminated'].append(key)
        if not b[field] and c[field]:
            changes['positives_recovered' if positive else 'fp_introduced'].append(key)
    result = {k: len(v) for k, v in changes.items()}
    result['utility'] = (result['fp_eliminated']-result['fp_introduced']
                         -4*result['positives_lost']+4*result['positives_recovered'])
    result['positive_recognized'] = sum(r['humanLabel']=='correct' and r['detected_near_decision'] for r in rows)
    result['negative_activated'] = sum(r['humanLabel']=='false_positive' and r['detected_any'] for r in rows)
    return result, changes


def group(base, rows):
    result, changes = metrics(base, rows)
    result['by_label'] = {}
    for label in ('correct', 'false_positive'):
        bb = {r['id']: r for r in base if r['humanLabel']==label}
        cc = {r['id']: r for r in rows if r['humanLabel']==label}
        pair, _ = paired(bb, cc)
        result['by_label'][label] = {**aggregate(list(cc.values())), 'paired': pair}
    return result, changes


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--root', type=private_output, required=True)
    p.add_argument('--output', type=private_output, required=True)
    a = p.parse_args()
    os.umask(0o077)
    selection_path = a.root/'selection-v1.json'
    selection = read(selection_path)
    protocol_path = PARENT/'preparation-v1/protocol.json'
    audit_path = PARENT/'preparation-v1/audit/inventory.json'
    protocol, audit = read(protocol_path), read(audit_path)
    if selection['protocol_sha256'] != sha256(protocol_path) or protocol['audit_sha256'] != sha256(audit_path):
        raise ValueError('Selection/protocol/audit provenance mismatch')
    roles = {r['id']: r for r in protocol['events']}
    if len(roles)!=260 or {s:dict(Counter(r['humanLabel'] for r in roles.values() if r['split']==s)) for s in EXPECTED} != EXPECTED:
        raise ValueError('Owner splits changed')
    old_roles = read(OLD)['events']
    old15 = {r['id'] for r in old_roles if r['humanLabel']=='correct'}
    old64 = {r['id'] for r in old_roles if r['humanLabel']=='false_positive' and r['split']=='test'}
    if len(old15)!=15 or len(old64)!=64 or not old15|old64 <= roles.keys():
        raise ValueError('Historical subsets changed')
    # Stable aliases depend only on existing inventory order, never conversation text.
    aliases = {r['id']: f'E{i:03d}' for i,r in enumerate(protocol['events'], 1)}
    evaluations = {name: read(a.root/f'eval-{name}-final/evaluation.json') for name in selection['models']}
    launch = read(a.root/'final-replay-launch-v1.json')
    if launch['selection_sha256'] != sha256(selection_path) or datetime.fromisoformat(launch['created_at']) < datetime.fromisoformat(selection['created_at']):
        raise ValueError('Final replay was not sealed after selection')
    result = {'schema':1, 'experiment':'wakeword-tradeoff', 'decision':'PENDING REVIEW',
              'production_approved':False, 'selected_model':selection['selected_model'],
              'split_counts':EXPECTED, 'utility_policy':read(a.root/'round-plan-v1.json'),
              'models':{}, 'provenance':{'selection_sha256':sha256(selection_path),
              'protocol_sha256':sha256(protocol_path), 'audit_sha256':sha256(audit_path),
              'historical_protocol_sha256':sha256(OLD), 'summary_script_sha256':sha256(Path(__file__))},
              'limitations':[
                  'All existing test and historical sets were examined previously; exploratory known regressions, not a new blind evaluation.',
                  'Historical 15/64 overlap current TRAIN; their results are adjustment diagnostics.',
                  'Activation-selected positives measure conditional redetection, not population recall; 1/11 is not a failure-rate estimate.',
                  'FP contexts have no continuous exposure denominator; 6/13 is not an FP/hour reduction estimate.',
                  'Small correlated sessions and repeated validation constrain generalization.',
                  'One lost call costs four FP by engineering preference, not universal scientific equivalence.',
                  'Host numeric kernels differ; loading and reset checks do not establish physical ARM64 compatibility.']}
    private = {'alias_to_id':{v:k for k,v in aliases.items()}, 'paired_changes':{}}
    validation = read(a.root/'validation-summary-v1.json')
    if validation['selection_sha256'] != sha256(selection_path):
        raise ValueError('Validation summary refers to a different selection')
    result['validation'] = validation
    for name, ev in evaluations.items():
        model = selection['models'][name]
        if (ev['model_sha256']!=model['model_sha256'] or sha256(Path(model['model_path']))!=model['model_sha256']
                or ev['split_manifest_sha256']!=sha256(protocol_path) or ev['audit_sha256']!=sha256(audit_path)
                or ev['split']!='all' or ev['rules']['threshold']!=model['threshold']):
            raise ValueError('Final evaluation/model/rules mismatch: '+name)
        if (ev['library_sha256'] != sha256(a.root/'native-threshold-v1/libaegis_mww_threshold.so')
                or ev['rules'] != {**RULES, 'threshold':model['threshold']}
                or datetime.fromisoformat(ev['created_utc']) < datetime.fromisoformat(launch['created_at'])):
            raise ValueError('Final library/rules/chronology differs: '+name)
        out = {'model_sha256':ev['model_sha256'], 'model_bytes':ev['model_bytes'],
               'threshold':model['threshold'], 'runtimes':{}, 'native_comparison':native_summary(ev)}
        if 'weights_sha256' in model:
            out['float_checkpoint_sha256'] = model['weights_sha256']
        out['native_comparison']['activated_context_mismatch_aliases'] = [
            aliases[r['id']] for r in ev['native_comparison']['cases'] if not r['activated_file_equal']]
        out['native_comparison']['event_stride_mismatch_aliases'] = [
            aliases[r['id']] for r in ev['native_comparison']['cases'] if not r['events_within_stride']]
        result['models'][name] = out
        result['provenance'][name+'_evaluation_sha256'] = sha256(a.root/f'eval-{name}-final/evaluation.json')
        private['paired_changes'][name] = {}
        for rt in RUNTIMES:
            rows, base = ev['runtimes'][rt]['contexts']['events'], evaluations['baseline']['runtimes'][rt]['contexts']['events']
            if len(rows)!=260 or {r['id'] for r in rows}!=roles.keys():
                raise ValueError('Incomplete final coverage')
            if any((r['split'],r['humanLabel'])!=(roles[r['id']]['split'],roles[r['id']]['humanLabel']) for r in rows):
                raise ValueError('Changed labels/splits')
            run = {'by_split':{}, 'historical':{}, 'changed_event_aliases':{}}
            private['paired_changes'][name][rt] = {}
            for split in EXPECTED:
                run['by_split'][split], changes = group([r for r in base if r['split']==split], [r for r in rows if r['split']==split])
                private['paired_changes'][name][rt][split] = changes
                run['changed_event_aliases'][split] = {k:[aliases[i] for i in v] for k,v in changes.items()}
            for key, ids in (('old15_positive',old15), ('old64_negative',old64)):
                run['historical'][key], _ = group([r for r in base if r['id'] in ids], [r for r in rows if r['id'] in ids])
                run['historical'][key]['current_split_overlap'] = dict(Counter(roles[i]['split'] for i in ids))
            run['legacy'] = legacy_summary(ev['runtimes'][rt]['legacy'])
            out['runtimes'][rt] = run
    training = read(a.root/'finetune-v1/training.json')
    # Whitelist fields to avoid accidentally serializing original event identifiers.
    result['finetune'] = {k:training[k] for k in ('plan','best_epoch','best_key','params','frozen_batch_norm_layers','model_sha256','elapsed_seconds','protocol_sha256','features_manifest_sha256') if k in training}
    result['finetune']['execution'] = {k:training[k] for k in ('training_script_sha256','wall_seconds_plan_to_last_epoch_write','finalization_recovery') if k in training}
    result['finetune']['history'] = []
    for row in training['history']:
        safe = {k:row[k] for k in ('epoch','train_loss','original_validation_bce','original_validation_recall','validation_negative_contexts_litert_native','selection_key','model_sha256','validation_sha256','real_training_unique_events','real_training_class_counts') if k in row}
        safe['float_checkpoint_sha256'] = row['checkpoint_sha256']
        draws = row['real_event_draws']
        if len(draws['real_positive'])!=91 or len(draws['real_negative'])!=130 or any(v<1 for d in draws.values() for v in d.values()):
            raise ValueError('TRAIN event omitted in finetune')
        safe['exposure'] = {k:{'original_events':len(v),'presentations':sum(v.values()),'min':min(v.values()),'max':max(v.values())} for k,v in draws.items()}
        val = read(a.root/f'finetune-v1/epoch-{row["epoch"]:02d}/validation/evaluation.json')
        safe['validation'] = {rt:metrics([r for r in evaluations['baseline']['runtimes'][rt]['contexts']['events'] if r['split']=='validation'],val['runtimes'][rt]['contexts']['events'])[0] for rt in RUNTIMES}
        result['finetune']['history'].append(safe)
    controls_path = a.root/'controls-v1/controls.json'
    controls = read(controls_path)
    if controls['selection_sha256']!=sha256(selection_path):
        raise ValueError('Control selection differs')
    if set(controls['models']) != set(selection['models']):
        raise ValueError('Incomplete model controls')
    result['controls'] = {}
    for name, model in controls['models'].items():
        if set(model['runtimes']) != set(RUNTIMES):
            raise ValueError('Incomplete runtime controls')
        if model['model_sha256'] != selection['models'][name]['model_sha256']:
            raise ValueError('Control model differs')
        result['controls'][name] = {}
        for rt, run in model['runtimes'].items():
            con = run['continuous']
            base = controls['models']['baseline']['runtimes'][rt]['continuous']['contexts']
            summary, changes = metrics(base, con['contexts'])
            result['controls'][name][rt] = {'controls':run['controls'], 'continuous':{
                **summary, 'gap_events':con['gap_event_count'],
                'by_origin_split_label':con['by_origin_split_label'], 'protocol':con['protocol']}}
    result['provenance']['controls_sha256'] = sha256(controls_path)
    for filename in ('training-code-hardening-v1.json','finetune-v1/manifest-recovery-v1.json'):
        path = a.root/filename
        if path.exists():
            result['provenance'][filename+'_sha256'] = sha256(path)
    android = a.root/'android-readiness-v1.json'
    if android.exists():
        result['android'] = read(android)
    a.output.mkdir(parents=True, mode=0o700, exist_ok=False)
    (a.output/'public_summary.json').write_text(json.dumps(result,indent=2,ensure_ascii=False)+'\n')
    (a.output/'private_summary.json').write_text(json.dumps(private,indent=2)+'\n')
    print(json.dumps({'summary':str(a.output/'public_summary.json'),'selected':result['selected_model']}))


if __name__=='__main__':
    main()
