#!/usr/bin/env python3
"""Export safe aggregate validation evidence; omit private event IDs and scores."""
import os
from pathlib import Path
from tradeoff_eval import CACHE, PREVIOUS, write_json
from seal_tradeoff import load
from prepare import sha256


def main():
    os.umask(0o077)
    selection=load(CACHE/'selection-v1.json')
    models=selection['models']
    grid=load(CACHE/'threshold-results-v1.json')
    training=load(CACHE/'finetune-v1/training.json')
    candidates={}
    paths={**grid['evaluations'],**{name:spec['validation_path'] for name,spec in models.items()}}
    paths.update({f'finetune-epoch{r["epoch"]}':str(CACHE/'finetune-v1'/f'epoch-{r["epoch"]:02d}'/'validation/evaluation.json') for r in training['history']})
    for name,values in selection['all_validation_candidates'].items():
        report=load(Path(paths[name]));threshold=report['rules']['threshold']
        candidates[name]={'model_sha256':report['model_sha256'],'threshold':threshold,'validation_sha256':sha256(Path(paths[name])),
                          'runtimes':{runtime:{key:value for key,value in metrics.items() if key in ('counts','utility','positives_recognized','false_contexts_activated')} for runtime,metrics in values.items()}}
    historical={}
    for name,values in selection['historical_strong_epochs'].items():
        epoch=int(name[-1]);path=PREVIOUS/'model-v1'/f'epoch-{epoch:02d}'/'validation/evaluation.json'
        historical[name]={'epoch':epoch,'model_sha256':load(path)['model_sha256'],'threshold':.5,
                          'runtimes':{runtime:{key:value for key,value in metrics.items() if key in ('counts','utility','positives_recognized','false_contexts_activated')} for runtime,metrics in values.items()}}
    result={'schema':1,'selection_sha256':sha256(CACHE/'selection-v1.json'),'selected_model':selection['selected_model'],'selected_threshold':models[selection['selected_model']]['threshold'],'selected_model_sha256':models[selection['selected_model']]['model_sha256'],'utility_policy':'FP eliminated - FP introduced -4*positive lost +4*positive recovered; paired event changes','positive_criterion':'near decision -500/+200 ms','negative_criterion':'anywhere in capture','validation_counts':{'positives':11,'negatives':15},'threshold_release_policy':'only event threshold varies; fixed release .3, average5, refractory16000 samples','candidates':candidates,'pareto':selection['pareto'],'historical_strong_epochs':historical,'pareto_including_historical':selection['pareto_including_historical'],'fine_tuning_best_epoch':training['best_epoch'],'fine_tuning_plan':training['plan'],'native_threshold_parity':load(CACHE/'native-threshold-parity-v1.json'),'interpretation':'Exploratory reuse of previously examined activation-selected corpus. No FP/hour or population recall inference. No final test results used in this selection.'}
    output=CACHE/'validation-summary-v1.json'
    if output.exists():raise ValueError('Refusing summary overwrite')
    write_json(output,result)
    print(str(output))

if __name__=='__main__':main()
