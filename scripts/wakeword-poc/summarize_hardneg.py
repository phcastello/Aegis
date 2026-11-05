#!/usr/bin/env python3
"""Aggregate sealed comparisons and copy a private candidate, without deployment."""
import argparse
from collections import Counter
from datetime import datetime
import json
import os
from pathlib import Path
import shutil
import numpy as np
from prepare import private_output,sha256


def verify_sealed_inputs(root, sealed, revision):
    """Verify reports actually sealed at selection, including prior A/B chain."""
    for tag,digest in sealed['validation_reports_sha256'].items():
        path=root/f'eval-{tag}-validation-v{revision}/evaluation.json'
        if sha256(path)!=digest:raise ValueError('Sealed validation report changed: '+tag)
    for tag,digest in sealed['training_reports_sha256'].items():
        if sha256(root/f'model-{tag}-v1/training.json')!=digest:
            raise ValueError('Sealed training report changed: '+tag)
    if 'prior_selection_sha256' in sealed:
        prior_path=root/'selection-ab-v1.json'
        if sha256(prior_path)!=sealed['prior_selection_sha256']:
            raise ValueError('Prior A/B selection changed')
        prior=json.loads(prior_path.read_text())
        if prior['protocol_sha256']!=sealed['parent_protocol_sha256'] or prior['validation_gate_passed']:
            raise ValueError('Unexpected parent protocol or A/B acceptance')
        if prior['protocol_sha256']!=sha256(root/'protocol-v2.json'):
            raise ValueError('Parent protocol changed')
        verify_sealed_inputs(root,prior,2)


def paired(before,after):
    b={r['id']:r for r in before if r['humanLabel']=='false_positive'}
    a={r['id']:r for r in after if r['humanLabel']=='false_positive'}
    if b.keys()!=a.keys():raise ValueError('Non-paired contexts')
    result={}
    for metric in ('detected_near_decision','detected_any'):
        wins=sum(b[k][metric] and not a[k][metric] for k in b)
        losses=sum(a[k][metric] and not b[k][metric] for k in b)
        total_before=sum(r[metric] for r in b.values());total_after=sum(r[metric] for r in a.values())
        sessions=sorted({r['session'] for r in b.values()})
        groups=[np.array([[int(b[k][metric]),int(a[k][metric])] for k in b if b[k]['session']==s]) for s in sessions]
        rng=np.random.default_rng(20261105);deltas=[]
        for _ in range(10000):
            sample=np.concatenate([groups[i] for i in rng.integers(0,len(groups),len(groups))])
            deltas.append(float(np.mean(sample[:,0]-sample[:,1])))
        result[metric]={'contexts':len(b),'baseline':total_before,'candidate':total_after,
                        'suppressed_baseline_events':wins,'newly_activated_contexts':losses,
                        'relative_net_reduction':(total_before-total_after)/total_before if total_before else None,
                        'baseline_reproduced_subset_suppression':wins/total_before if total_before else None,
                        'absolute_fraction_reduction':(total_before-total_after)/len(b),
                        'cluster_bootstrap_95pct_interval_absolute_reduction':np.quantile(deltas,[.025,.975]).tolist(),
                        'bootstrap_sessions':len(groups),'bootstrap_replicates':10000,'bootstrap_seed':20261105,
                        'interval_limit':'Exploratory session-resampling interval; heuristically grouped, activation-selected captures, not prospective rate evidence.'}
    return result


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--root',type=private_output,required=True)
    p.add_argument('--selected',choices=['A','B','C'],required=True);p.add_argument('--output',type=private_output,required=True)
    a=p.parse_args();os.umask(0o077)
    if a.output.exists():raise ValueError('Candidate export must be new')
    root=a.root
    selection=root/'selection-v1.json'
    if not selection.exists():raise ValueError('Selection must be sealed before final evaluation')
    sealed=json.loads(selection.read_text())
    protocol_path=root/('protocol-v3.json' if a.selected=='C' else 'protocol-v2.json')
    features_path=root/('features-v2' if a.selected=='C' else 'features-v1')/'features.json'
    tags=('baseline','A','B','C') if a.selected=='C' else ('baseline','A','B')
    if sealed['selected_strategy']!=a.selected or sealed['protocol_sha256']!=sha256(protocol_path):
        raise ValueError('Export differs from sealed validation selection')
    verify_sealed_inputs(root,sealed,3 if a.selected=='C' else 2)
    protocol=json.loads(protocol_path.read_text())
    if protocol['audit_sha256']!=sha256(root/'audit-v1/inventory.json'):
        raise ValueError('Audit differs from frozen protocol')
    launch_path=root/'final-replay-launch.json'
    launch=json.loads(launch_path.read_text())
    if (launch['selection_sha256']!=sha256(selection)
            or launch['protocol_sha256']!=sha256(protocol_path)
            or datetime.fromisoformat(launch['timestampUTC'])<datetime.fromisoformat(sealed['timestampUTC'])):
        raise ValueError('Final replay launch does not follow sealed selection')
    reports={}
    for tag in tags:
        for scope in ('validation','final','train'):
            revision=3 if a.selected=='C' and (scope in ('final','train') or tag in ('baseline','C')) else 2
            report_path=root/f'eval-{tag}-{scope}-v{revision}/evaluation.json'
            reports[tag,scope]=json.loads(report_path.read_text())
            report=reports[tag,scope]
            model=(root/f'model-{tag}-v1/aegis.int8.tflite') if tag!='baseline' else Path.home()/'.cache/aegis-wakeword-poc/comparison/microwakeword/model-realneg/aegis.int8.tflite'
            expected_protocol=root/f'protocol-v{revision}.json'
            if report['model_sha256']!=sha256(model) or report['split_manifest_sha256']!=sha256(expected_protocol):
                raise ValueError('Evaluation model/protocol hash mismatch')
            wanted_split={'validation':'validation','final':'test','train':'train'}[scope]
            expected_ids={r['id'] for r in protocol['events'] if r['split']==wanted_split}
            rows=report['runtimes']['litert']['contexts']['events']
            if (report['split']!=wanted_split or {r['id'] for r in rows}!=expected_ids
                    or len(rows)!=len(expected_ids) or report['reference_kernel_mode']
                    or report['evaluation_script_sha256']!=sealed['evaluation_script_sha256']):
                raise ValueError('Evaluation scope, IDs or primary runtime changed')
    if sealed['model_sha256']!=reports[a.selected,'final']['model_sha256']:
        raise ValueError('Selected model hash differs from final replay')
    result={'schema':1,'selected_strategy':a.selected,'decision':'offline validation passed; physical acceptance pending' if sealed['validation_gate_passed'] else 'NO-GO: validation legitimate-call preservation failed; research artifact only',
            'validation_gate_passed':sealed['validation_gate_passed'],'protocol_sha256':sha256(protocol_path),
            'selection_sha256':sha256(selection),'final_replay_launch_sha256':sha256(launch_path),
            'audit':{},'split_counts':{},'training':{},'validation':{},'test':{},'legacy':{},'native':{},'paired_test':{},
            'train_period_diagnostic':{},'new_correct_all15_conditional':{}}
    audit=json.loads((root/'audit-v1/audit.json').read_text())
    for k in ('labels','wav_files','all_declared_hashes_match','all_wav_formats_pcm16_mono_16khz','overlap_exact_events','issues','duration_seconds'):
        result['audit'][k]=audit[k]
    result['split_counts']={s:dict(Counter(r['humanLabel'] for r in protocol['events'] if r['split']==s)) for s in ('train','validation','test')}
    fm=json.loads(features_path.read_text())
    for filename,digest in fm['array_sha256'].items():
        if Path(filename).name!=filename or sha256(features_path.parent/filename)!=digest:
            raise ValueError('Selected feature array changed')
    result['feature_counts']={s:len(fm['hard_windows'][s]) for s in ('train','validation')}
    result['used_hard_events']={s:len({r['id'] for r in fm['hard_windows'][s]}) for s in ('train','validation')}
    for tag in tags[1:]:
        tr=json.loads((root/f'model-{tag}-v1/training.json').read_text())
        used_features=root/('features-v2' if tag=='C' else 'features-v1')/'features.json'
        if tr['features_manifest_sha256']!=sha256(used_features):
            raise ValueError('Training feature provenance changed')
        result['training'][tag]={k:tr[k] for k in ('strategy','config','params','best_epoch','best_objective','model_bytes','model_sha256','input_shape','input_quantization','output_shape','output_quantization','elapsed_seconds','versions')}
    for tag in tags:
        v=reports[tag,'validation'];f=reports[tag,'final']
        result['validation'][tag]=v['runtimes']['litert']['contexts']['counts']
        result['test'][tag]=f['runtimes']['litert']['contexts']['counts']
        diagnostic=reports[tag,'train']['runtimes']['litert']['contexts']['counts']
        result['train_period_diagnostic'][tag]=diagnostic
        def aggregate_correct(runtime):
            by_split={scope:reports[tag,scope]['runtimes'][runtime]['contexts']['counts']['correct']
                      for scope in ('train','validation','final')}
            return {'contexts':sum(r['contexts'] for r in by_split.values()),
                    'near_decision_detected':sum(r['near_decision_event_contexts'] for r in by_split.values()),
                    'any_context_detected':sum(r['any_event_contexts'] for r in by_split.values()),
                    'by_split':by_split,
                    'interpretation':'All 15 activation-selected correct contexts, never used to fit. Pooled descriptive re-detection only: includes observed validation and training-period sessions; not independent generalization or population recall.'}
        result['new_correct_all15_conditional'][tag]=aggregate_correct('litert')
        le=f['runtimes']['litert']['legacy']
        result['legacy'][tag]={k:le[k] for k in ('quality','continuous','public')}
        result['native'][tag]={'unavailable':f.get('native_unavailable')}
        if 'native_host' in f['runtimes']:
            native=f['runtimes']['native_host']
            result['native'][tag].update(test=native['contexts']['counts'],legacy={k:native['legacy'][k] for k in ('quality','continuous','public')},
                max_context_score_delta=max(r['max_score_delta'] for r in f['native_comparison']['cases']),
                context_event_stride_mismatch=sum(not r['events_within_stride'] for r in f['native_comparison']['cases']))
            if all('native_host' in reports[tag,scope]['runtimes'] for scope in ('train','validation','final')):
                result['native'][tag].update(train_period_diagnostic=reports[tag,'train']['runtimes']['native_host']['contexts']['counts'],
                    new_correct_all15_conditional=aggregate_correct('native_host'))
    base=reports['baseline','final']
    for tag in tags[1:]:
        result['paired_test'][tag]=paired(base['runtimes']['litert']['contexts']['events'],reports[tag,'final']['runtimes']['litert']['contexts']['events'])
    result['limits']=['new correct contexts measure conditional re-detection, not population recall','only7correct finaltest, selection already observed4validation','train-period FP diagnostic reuses fitted source events; its 4 untrained positives share training sessions, and the all15 pool includes validation; neither is independent test evidence','original23+8/public35min consulted in prior architecture choice; regression only','no future conversational audio or exposure denominator; cannot infer FP/hour','same device and owner, three days; heuristic sessions; unknown environment persistence','local ASR lexical screen does not establish word absence or phonetic boundaries','host native replay is not Android ARM64 physical validation','no production files, model import allowlist, TTS, backend or conversational pipeline changed']
    a.output.mkdir(parents=True,mode=0o700)
    model=root/f'model-{a.selected}-v1/aegis.int8.tflite'
    shutil.copyfile(model,a.output/'aegis.int8.tflite');os.chmod(a.output/'aegis.int8.tflite',0o600)
    if sha256(a.output/'aegis.int8.tflite')!=sha256(model):
        raise ValueError('Exported model bytes differ from selected source')
    manifest={'schema':1,'status':result['decision'],'strategy':a.selected,
              'sha256':sha256(model),'bytes':model.stat().st_size,'baseline_sha256':base['model_sha256'],
              'training_sha256':sha256(model.parent/'training.json'),'protocol_sha256':result['protocol_sha256'],
              'selection_sha256':result['selection_sha256'],'features_sha256':sha256(features_path),
              'final_replay_launch_sha256':sha256(launch_path),'export_script_sha256':sha256(Path(__file__)),
              'validation_gate_passed':sealed['validation_gate_passed'],
              'sampleRate':16000,'channels':1,'encoding':'PCM16LE','events':protocol['events_rule'],
              'production_import':'existing Android allowlist freezes baseline SHA; candidate not importable without separate authorized app change',
              'deployment':'none; weights private and no runtime change'}
    for name,value in (('model.json',manifest),('summary.json',result)):
        (a.output/name).write_text(json.dumps(value,indent=2,ensure_ascii=False)+'\n')
    evidence={str(p.relative_to(root)):sha256(p) for p in root.rglob('*.json') if a.output not in p.parents}
    (a.output/'evidence-sha256.json').write_text(json.dumps(evidence,indent=2)+'\n')
    print(json.dumps({'candidate':str(a.output),'model_sha256':sha256(model),'selected':a.selected,'test':result['test'],'legacy':result['legacy']},indent=2))
if __name__=='__main__':main()
