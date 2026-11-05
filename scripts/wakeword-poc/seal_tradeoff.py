#!/usr/bin/env python3
"""Seal validation-only tradeoff selection and read-only private model snapshots."""
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import shutil

from tradeoff_eval import CACHE, PREVIOUS, compare, pareto, write_json
from prepare import sha256


def load(path):return json.loads(path.read_text())


def main():
    os.umask(0o077)
    target=CACHE/'selection-v1.json'
    if target.exists():raise ValueError('Selection already sealed')
    protocol=PREVIOUS/'preparation-v1/protocol.json'
    plan=CACHE/'round-plan-v1.json';grid=load(CACHE/'threshold-results-v1.json')
    training=load(CACHE/'finetune-v1/training.json')
    baseline=load(CACHE/'validation-baseline-v1/evaluation.json')
    if not load(CACHE/'native-threshold-parity-v1.json')['pass']:raise ValueError('Native threshold .5 parity failed')
    def checked(path,model,threshold):
        report=load(path)
        if report['model_sha256']!=sha256(model) or report['split_manifest_sha256']!=sha256(protocol):raise ValueError('Validation provenance differs')
        if report['split']!='validation' or report['rules']['threshold']!=threshold:raise ValueError('Not intended validation config')
        if set(report['runtimes'])!={'litert','native_host'}:raise ValueError('Both runtimes required')
        for result in report['runtimes'].values():
            rows=result['contexts']['events']
            if len(rows)!=26 or any(r['split']!='validation' for r in rows):raise ValueError('Wrong validation events')
        return report
    specs={
        'baseline':{'source_path':str(Path.home()/'.cache/aegis-wakeword-poc/comparison/microwakeword/model-realneg/aegis.int8.tflite'),'threshold':.5,'validation_path':grid['baseline_evaluation']},
        'strong':{'source_path':str(CACHE/'strong-epoch02-snapshot-v1/aegis.int8.tflite'),'threshold':.5,'validation_path':grid['evaluations']['strong-t0.50'],'epoch':2,'weights_source':str(CACHE/'strong-epoch02-snapshot-v1/best.weights.h5')},
        'finetune':{'source_path':str(CACHE/'finetune-v1'/f"epoch-{training['best_epoch']:02d}"/'aegis.int8.tflite'),'threshold':.5,'validation_path':str(CACHE/'finetune-v1'/f"epoch-{training['best_epoch']:02d}"/'validation/evaluation.json'),'epoch':training['best_epoch'],'weights_source':str(CACHE/'finetune-v1'/f"epoch-{training['best_epoch']:02d}"/'best.weights.h5')},
        'conservative':{'source_path':str(PREVIOUS/'model-v2/epoch-01/aegis.int8.tflite'),'threshold':.5,'validation_path':str(PREVIOUS/'model-v2/epoch-01/validation/evaluation.json'),'epoch':1,'weights_source':str(PREVIOUS/'model-v2/epoch-01/best.weights.h5')},
    }
    if grid['selected_threshold']!=.5:
        selected_grid=grid['paired'][grid['selected']]['litert']['utility']
        if selected_grid<=grid['paired']['strong-t0.50']['litert']['utility']:raise ValueError('Threshold must strictly improve LiteRT utility')
        specs['strong_calibrated']={**specs['strong'],'threshold':grid['selected_threshold'],'validation_path':grid['evaluations'][grid['selected']]}
    comparisons={}
    for name,spec in specs.items():
        comparisons[name]=compare(baseline,checked(Path(spec['validation_path']),Path(spec['source_path']),spec['threshold']))
    # All directed checkpoints compete; best training choice must reproduce here.
    fit_candidates={}
    for row in training['history']:
        epoch=row['epoch'];model=CACHE/'finetune-v1'/f'epoch-{epoch:02d}'/'aegis.int8.tflite'
        path=model.parent/'validation/evaluation.json'
        fit_candidates[f'finetune-epoch{epoch}']=compare(baseline,checked(path,model,.5))
    fit_order=lambda name:(fit_candidates[name]['litert']['utility'],min(r['utility'] for r in fit_candidates[name].values()),-int(name.split('epoch')[1]))
    best_fit=max(fit_candidates,key=fit_order)
    if int(best_fit.split('epoch')[1])!=training['best_epoch']:raise ValueError('Training selection differs from independently paired utility')
    def order(name):
        value=comparisons[name];threshold=specs[name]['threshold']
        return (value['litert']['utility'],min(r['utility'] for r in value.values()),int(name in ('strong','strong_calibrated')),-abs(threshold-.5),-specs[name].get('epoch',0))
    selected=max(comparisons,key=order)
    all_pareto={**grid['paired'],**comparisons,**fit_candidates}
    historical={}
    for epoch in (1,2,3,4):
        path=PREVIOUS/'model-v1'/f'epoch-{epoch:02d}'/'validation/evaluation.json'
        historical[f'prior-strong-epoch{epoch}']=compare(baseline,load(path))
    root=CACHE/'frozen-v1';root.mkdir(mode=0o700,exist_ok=False)
    for name,spec in specs.items():
        directory=root/name;directory.mkdir(mode=0o700)
        model=Path(spec['source_path']);frozen=directory/'aegis.int8.tflite'
        shutil.copyfile(model,frozen)
        if sha256(model)!=sha256(frozen):raise ValueError('Frozen copy hash differs')
        spec['model_path']=str(frozen);spec['model_sha256']=sha256(frozen)
        if spec.get('weights_source'):
            source=Path(spec['weights_source']);dest=directory/'best.weights.h5';shutil.copyfile(source,dest)
            if sha256(source)!=sha256(dest):raise ValueError('Float copy differs')
            spec['weights_path']=str(dest);spec['weights_sha256']=sha256(dest);dest.chmod(0o400)
        spec['rules']={'threshold':spec['threshold'],'average_window':5,'release_below':.3,'refractory_samples':16000,'block_samples':160,'state':'streaming; fresh state per independent context'}
        spec['validation_sha256']=sha256(Path(spec['validation_path']))
        write_json(directory/'configuration.json',spec)
        frozen.chmod(0o400);(directory/'configuration.json').chmod(0o400);directory.chmod(0o500)
    root.chmod(0o500)
    report={'schema':1,'created_at':datetime.now(timezone.utc).isoformat(),'protocol_sha256':sha256(protocol),'round_plan_sha256':sha256(plan),'threshold_results_sha256':sha256(CACHE/'threshold-results-v1.json'),'training_sha256':sha256(CACHE/'finetune-v1/training.json'),'decision':'GO PARA PILOTO - validation only' if comparisons[selected]['litert']['utility']>0 else 'NO-GO','production_approved':False,'selected_model':selected,'selected':selected,'models':specs,'paired_validation':comparisons,'all_validation_candidates':all_pareto,'fine_tuning_all_epochs':fit_candidates,'historical_strong_epochs':historical,'pareto':{runtime:pareto(all_pareto,runtime) for runtime in ('litert','native_host')},'pareto_including_historical':{runtime:pareto({**all_pareto,**historical},runtime) for runtime in ('litert','native_host')},'selection_key':order(selected),'selection_policy':load(plan)['global_selection_order'],'test_policy':'No test/legacy consulted in this round before this seal. Known historical sets previously exposed; exploratory final comparison only. No adjustments after final replay.','native_configurable_library':str(CACHE/'native-threshold-v1/libaegis_mww_threshold.so'),'native_configurable_library_sha256':sha256(CACHE/'native-threshold-v1/libaegis_mww_threshold.so'),'seal_script_sha256':sha256(Path(__file__))}
    write_json(target,report);target.chmod(0o400)
    print(json.dumps({'selected_model':selected,'decision':report['decision'],'models':list(specs),'utility':{name:{r:v['utility'] for r,v in value.items()} for name,value in comparisons.items()},'pareto':report['pareto']},indent=2))

if __name__=='__main__':main()
