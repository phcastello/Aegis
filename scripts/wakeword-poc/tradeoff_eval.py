#!/usr/bin/env python3
"""Private utility-based evaluation; fixed hysteresis, streaming and native kernels.

The native experimental library is compiled from a private copy with exactly
three changes: a per-instance threshold, that threshold in the event comparison,
and a setter. Release stays .3; no production source or baseline is changed.
"""
import argparse
import ctypes
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import subprocess
import time

import numpy as np

from compare_microwakeword import MicroWakeWordDetector
from evaluate_hardneg import NativeCandidate, contexts, legacy, RULES
from prepare import private_output, sha256

CACHE = Path.home()/'.cache/aegis-wakeword-tradeoff'
PREVIOUS = Path.home()/'.cache/aegis-wakeword-human-label-training'
NATIVE = Path.home()/'.cache/aegis-hands-free/activation-native-20261101'
GRID = (.40, .45, .50, .55, .60, .65, .70)
SOURCE = Path(__file__).resolve().parents[2]/'apps/aegis-node/src-tauri/gen/android/app/src/main/cpp'


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False)+'\n')


def build_native(output):
    """Reuse immutable build objects/dependencies, with private paths for writes."""
    output.mkdir(mode=0o700, parents=True, exist_ok=False)
    original=SOURCE/'AegisMicroWakeWord.cpp'
    source=original.read_text()
    replacements={
        'float lastScore=0, maxScore=0;': 'float lastScore=0, maxScore=0;\n    float experimentalThreshold=0.5f;',
        'if(average>=0.5f && !latched': 'if(average>=experimentalThreshold && !latched',
    }
    for before, after in replacements.items():
        if source.count(before)!=1: raise ValueError('Unexpected native source shape')
        source=source.replace(before,after)
    source+='\nextern "C" void aegis_mww_experimental_threshold(void* handle,float value){static_cast<AegisDetector*>(handle)->experimentalThreshold=value;}\n'
    target=output/'AegisMicroWakeWord.cpp'
    target.write_text(source)
    ninja=(NATIVE/'build.ninja').read_text()
    stanza=ninja.split('build CMakeFiles/aegis_microwakeword.dir/AegisMicroWakeWord.cpp.o:',1)[1].split('\nbuild ',1)[0]
    def variable(name): return re.search(r'^  '+name+r' = (.*)$',stanza,re.M)[1]
    compiler=Path.home()/'.cache/aegis-hands-free/gcc-rpm/usr/bin/g++'
    flags=shlex.split(variable('FLAGS'))
    obj=output/'AegisMicroWakeWord.cpp.o'
    compile_cmd=[str(compiler),*shlex.split(variable('DEFINES')),*shlex.split(variable('INCLUDES')),*flags,'-I'+str(SOURCE),'-c',str(target),'-o',str(obj)]
    wrapper=NATIVE/'CMakeFiles/aegis_microwakeword.dir/MicroFrontendWrapper.cpp.o'
    library=output/'libaegis_mww_threshold.so'
    link_cmd=[str(compiler),*flags,'-Wl,--gc-sections','-shared','-o',str(library),str(obj),str(wrapper),str(NATIVE/'libtflite_micro_frontend.a'),str(NATIVE/'libtflite_micro_interpreter.a')]
    subprocess.run(compile_cmd,check=True,capture_output=True)
    subprocess.run(link_cmd,check=True,capture_output=True)
    write_json(output/'build.json',{'created_utc':datetime.now(timezone.utc).isoformat(),'source_sha256':sha256(original),'generated_source_sha256':sha256(target),'reference_library_sha256':sha256(NATIVE/'libaegis_microwakeword.so'),'library_sha256':sha256(library),'compile':compile_cmd,'link':link_cmd,'changes':['per-instance threshold default .5','event comparison reads experimentalThreshold','experimental setter only'],'release_below':.3,'dependencies':'reused existing compiled frontend and TFLM; no production writes'})
    return library


class NativeThreshold(NativeCandidate):
    def __init__(self, library, model, threshold=.5):
        super().__init__(library,model)
        self.threshold=float(threshold)
        setter=self.lib.aegis_mww_experimental_threshold
        setter.argtypes=[ctypes.c_void_p,ctypes.c_float]
        setter.restype=None
        setter(self.handle,self.threshold)


class LiteThreshold(MicroWakeWordDetector):
    def __init__(self, model, threshold=.5):
        super().__init__(model_path=model,threshold=threshold,release_below=.3)


def evaluate(model, threshold, output, library, split='validation', legacy_suite=False,
             audit=None, protocol=None, provided=True):
    output.mkdir(mode=0o700,parents=True,exist_ok=False)
    audit=audit or PREVIOUS/'preparation-v1/audit/inventory.json'
    protocol=protocol or PREVIOUS/'preparation-v1/protocol.json'
    args=argparse.Namespace(audit=audit,split_manifest=protocol,split=split,provided_splits=provided,
                            legacy_data=Path.home()/'.cache/aegis-wakeword-poc/data',
                            public_wav=Path.home()/'.cache/aegis-wakeword-poc/comparison/public/negatives.wav')
    report={'schema':1,'created_utc':datetime.now(timezone.utc).isoformat(),
            'model_path':str(model),'model_sha256':sha256(model),'model_bytes':model.stat().st_size,
            'library_sha256':sha256(library),'rules':{**RULES,'threshold':threshold,'release_below':.3},
            'audit_sha256':sha256(audit),'split_manifest_sha256':sha256(protocol),
            'split':split,'evaluation_script_sha256':sha256(Path(__file__)),
            'native_threshold_library':'experimental private copy; inference unchanged, event threshold configurable',
            'runtimes':{}}
    started=time.monotonic()
    for name,detector in (('litert',LiteThreshold(model,threshold)),('native_host',NativeThreshold(library,model,threshold))):
        score_dir=output/('scores-'+name);score_dir.mkdir(mode=0o700)
        try:
            value={'contexts':contexts(args,detector,name,score_dir)}
            if legacy_suite:value['legacy']=legacy(args,detector,score_dir)
            report['runtimes'][name]=value
        finally:
            if isinstance(detector,NativeCandidate):detector.close()
    comparisons=[]
    for a,b in zip(report['runtimes']['litert']['contexts']['events'], report['runtimes']['native_host']['contexts']['events']):
        sa=np.load(output/'scores-litert'/(a['id']+'.npy'));sb=np.load(output/'scores-native_host'/(b['id']+'.npy'))
        ea,eb=[e['sample'] for e in a['events']],[e['sample'] for e in b['events']]
        delta=float(np.max(np.abs(sa-sb),initial=0))
        comparisons.append({'id':a['id'],'max_score_delta':delta,'activated_file_equal':bool(ea)==bool(eb),'events_within_stride':len(ea)==len(eb) and all(abs(x-y)<=480 for x,y in zip(ea,eb)),'score_tolerance_1lsb_passed':delta<=1/256+1e-9})
    report['native_comparison']={'cases':comparisons,'arm64':'not tested; host does not establish physical compatibility'}
    report['elapsed_seconds']=time.monotonic()-started
    write_json(output/'evaluation.json',report)
    return report


def paired(baseline,candidate):
    """Event IDs are private; positives near decision, FP anywhere in capture."""
    base={r['id']:r for r in baseline};cand={r['id']:r for r in candidate}
    if len(base)!=len(baseline) or len(cand)!=len(candidate):raise ValueError('Duplicate evaluation IDs')
    if set(base)!=set(cand):raise ValueError('Unpaired evaluation event inventory')
    names=('fp_eliminated','fp_new','positives_lost','positives_recovered')
    out={key:[] for key in names}
    for key,a in base.items():
        b=cand[key]
        if a['humanLabel'] not in ('correct','false_positive') or a['humanLabel']!=b['humanLabel']:raise ValueError('Labels differ or unsupported')
        if a.get('split')!=b.get('split'):raise ValueError('Splits differ')
        positive=a['humanLabel']=='correct';field='detected_near_decision' if positive else 'detected_any'
        if a[field] and not b[field]:out['positives_lost' if positive else 'fp_eliminated'].append(key)
        if not a[field] and b[field]:out['positives_recovered' if positive else 'fp_new'].append(key)
    out['counts']={key:len(out[key]) for key in names}
    c=out['counts'];out['utility']=c['fp_eliminated']-c['fp_new']-4*c['positives_lost']+4*c['positives_recovered']
    out['positives_recognized']=sum(r['humanLabel']=='correct' and r['detected_near_decision'] for r in candidate)
    out['false_contexts_activated']=sum(r['humanLabel']=='false_positive' and r['detected_any'] for r in candidate)
    return out


def compare(base,candidate):
    return {runtime:paired(base['runtimes'][runtime]['contexts']['events'],candidate['runtimes'][runtime]['contexts']['events']) for runtime in base['runtimes']}


def pareto(candidates,runtime='litert'):
    frontier=[]
    for name,value in candidates.items():
        a=value[runtime];dominated=False
        for other,both in candidates.items():
            b=both[runtime]
            if (b['positives_recognized']>=a['positives_recognized'] and b['false_contexts_activated']<=a['false_contexts_activated'] and
                (b['positives_recognized']>a['positives_recognized'] or b['false_contexts_activated']<a['false_contexts_activated'])):dominated=True
        if not dominated:frontier.append(name)
    return frontier


def snapshot():
    source=PREVIOUS/'model-v1/epoch-02'
    export=json.loads((source/'export.json').read_text())
    train=json.loads((source.parent/'training.json').read_text())
    history=next(r for r in train['history'] if r['epoch']==2)
    for filename,expected in (('aegis.int8.tflite',export['model_sha256']),('best.weights.h5',export['checkpoint_sha256'])):
        if sha256(source/filename)!=expected:raise ValueError('Corrupt strong epoch2')
    if export['model_sha256']!=history['model_sha256']:raise ValueError('Export/training differ')
    target=CACHE/'strong-epoch02-snapshot-v1';target.mkdir(mode=0o700,parents=True,exist_ok=False)
    for filename in ('aegis.int8.tflite','best.weights.h5','export.json'):
        shutil.copyfile(source/filename,target/filename)
        if sha256(source/filename)!=sha256(target/filename):raise ValueError('Copy differs')
        (target/filename).chmod(0o400)
    write_json(target/'snapshot.json',{'created_utc':datetime.now(timezone.utc).isoformat(),'source':str(source.resolve()),'source_training_sha256':sha256(source.parent/'training.json'),'source_model_sha256':export['model_sha256'],'checkpoint_sha256':export['checkpoint_sha256'],'epoch':2,'learning_rate':train['plan']['learning_rate'],'initializer':'baseline; fresh Adam','frozen_batch_norm_layers':train['frozen_batch_norm_layers'],'purpose':'immutable recovered strong candidate; not conservative selected checkpoint'})
    (target/'snapshot.json').chmod(0o400)
    target.chmod(0o500)
    return target/'aegis.int8.tflite'


def grid():
    os.umask(0o077);CACHE.mkdir(mode=0o700,parents=True,exist_ok=True)
    plan={'created_utc':datetime.now(timezone.utc).isoformat(),'grid':GRID,'release_below':.3,'average_window':5,'refractory_samples':16000,'selection':'maximum LiteRT paired utility, then minimum runtime utility, then threshold .5 / closest to .5; no BCE','positive_criterion':'near original decision -500/+200ms','false_positive_criterion':'anywhere in context','utility':'FP eliminated - new - 4*positive lost + 4*recovered','suite':'validation only; no test or legacy','threshold_fallback':'original .5 if no grid candidate strictly improves utility/tie rule over .5'}
    planpath=CACHE/'threshold-plan-v1.json'
    if planpath.exists():raise ValueError('Refusing overwrite')
    write_json(planpath,plan)
    model=snapshot();library=build_native(CACHE/'native-threshold-v1')
    baseline=Path.home()/'.cache/aegis-wakeword-poc/comparison/microwakeword/model-realneg/aegis.int8.tflite'
    base=evaluate(baseline,.5,CACHE/'validation-baseline-v1',library)
    comparisons={};paths={}
    for threshold in GRID:
        name=f'strong-t{threshold:.2f}';directory=CACHE/('validation-'+name+'-v1')
        candidate=evaluate(model,threshold,directory,library)
        comparisons[name]=compare(base,candidate);paths[name]=str(directory/'evaluation.json')
    baseline_cmp=compare(base,base)
    all_candidates={'baseline':baseline_cmp,**comparisons}
    order=lambda name:(comparisons[name]['litert']['utility'],min(r['utility'] for r in comparisons[name].values()),-abs(float(name[-4:])-.5))
    selected=max(comparisons,key=order)
    if comparisons[selected]['litert']['utility']<=comparisons['strong-t0.50']['litert']['utility']:selected='strong-t0.50'
    report={'schema':1,'created_utc':datetime.now(timezone.utc).isoformat(),'plan_sha256':sha256(planpath),'model':str(model),'model_sha256':sha256(model),'library':str(library),'baseline_evaluation':str(CACHE/'validation-baseline-v1/evaluation.json'),'evaluations':paths,'paired':all_candidates,'pareto':{runtime:pareto(all_candidates,runtime) for runtime in ('litert','native_host')},'selected':selected,'selected_threshold':float(selected[-4:]),'selection_scope':'validation only; global choice awaits directed fine-tuning','selection_key':order(selected)}
    write_json(CACHE/'threshold-results-v1.json',report)
    print(json.dumps({'selected':selected,'pareto':report['pareto'],'results':{k:{r:{kk:vv for kk,vv in x.items() if kk in ('counts','utility','positives_recognized','false_contexts_activated')} for r,x in v.items()} for k,v in all_candidates.items()}},indent=2))


def main():
    p=argparse.ArgumentParser(description=__doc__);sub=p.add_subparsers(dest='command',required=True)
    sub.add_parser('grid')
    e=sub.add_parser('evaluate');e.add_argument('--model',type=private_output,required=True);e.add_argument('--threshold',type=float,default=.5);e.add_argument('--output',type=private_output,required=True);e.add_argument('--library',type=private_output,default=CACHE/'native-threshold-v1/libaegis_mww_threshold.so');e.add_argument('--split',choices=('train','validation','test','all'),default='validation');e.add_argument('--legacy',action='store_true')
    args=p.parse_args();os.umask(0o077)
    if args.command=='grid':grid()
    else:
        r=evaluate(args.model,args.threshold,args.output,args.library,args.split,args.legacy)
        print(json.dumps({name:value['contexts']['counts'] for name,value in r['runtimes'].items()},indent=2))

if __name__=='__main__':main()
