#!/usr/bin/env python3
"""Bounded A/B experiment, immutable baseline, frozen splits, private outputs only."""
import argparse
import hashlib
import importlib.metadata
import json
import math
import os
from pathlib import Path
import subprocess
import sys
import time
os.environ.setdefault('CUDA_VISIBLE_DEVICES', '-1')
os.environ.setdefault('TF_CPP_MIN_LOG_LEVEL', '2')
os.environ.setdefault('TF_NUM_INTRAOP_THREADS', '2')
os.environ.setdefault('TF_NUM_INTEROP_THREADS', '1')
import numpy as np
from prepare import private_output, sha256
from train_microwakeword import load_training, load_real_negatives, features, feature_window, wav_samples, FEATURES, STRIDE, SOURCE_SHA
ROOT = Path.home()/'.cache/aegis-wakeword-poc'
BASE = ROOT/'comparison/microwakeword/model-realneg'
BASE_SHA='f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514'
BASE_TRAINING_SHA='c1cb2115ba5bb6e925e0d566fc2c0ab575113ce1b6b7e670827cd193c999916a'
BASE_WEIGHTS_SHA='9e061278cc5279d6d357d42b3d9d549fc01e997e128029c3f1524666cef4b2de'
ORIGINAL_MANIFEST_SHA='b1b408ddaaaf40c9e74cad74097bd4590c0712fcd239642df23bbc8c0081c726'
DEV_MANIFEST_SHA='39e95adc759332e8a046f5ee3adaa6a0292019da6ef0ee7fbe30997f733a3978'
EXPECTED_VERSIONS={'tensorflow-cpu':'2.18.1','keras':'3.15.1','numpy':'2.0.2',
                   'pymicro-features':'2.0.2','ai-edge-litert':'2.3.0','h5py':'3.16.0'}
SEED=20261105


def save_json(p, value):
    p.write_text(json.dumps(value, indent=2, ensure_ascii=False)+'\n')


def baseline_guards(check_audio=False):
    for path, expected in ((BASE/'aegis.int8.tflite',BASE_SHA),
                           (BASE/'training.json',BASE_TRAINING_SHA),
                           (BASE/'best.weights.h5',BASE_WEIGHTS_SHA),
                           (ROOT/'comparison/training/manifest.json',ORIGINAL_MANIFEST_SHA),
                           (BASE/'real-negative-manifest.json',DEV_MANIFEST_SHA)):
        if sha256(path)!=expected: raise ValueError('Preserved baseline artifact changed: '+str(path))
    versions={n:importlib.metadata.version(n) for n in EXPECTED_VERSIONS}
    if versions!=EXPECTED_VERSIONS: raise ValueError('Recovered Python dependency versions changed')
    source=ROOT/'comparison/microwakeword/source'
    if subprocess.check_output(['git','-C',str(source),'rev-parse','HEAD'],text=True).strip()!=SOURCE_SHA:
        raise ValueError('Source revision changed')
    status=subprocess.check_output(['git','-C',str(source),'status','--porcelain'],text=True)
    if status.strip(): raise ValueError('Recovered microWakeWord source has uncommitted changes')
    if check_audio:
        for row in json.loads((BASE/'training.json').read_text())['provenance']:
            if sha256(private_output(row['path']))!=row['sha256']:
                raise ValueError('Original training audio changed')
    return {'model_sha256':BASE_SHA,'training_sha256':BASE_TRAINING_SHA,
            'weights_sha256':BASE_WEIGHTS_SHA,'source_sha':SOURCE_SHA,
            'source_git_status':status,'versions':versions}


def validate_protocol(inventory, protocol, audit_path):
    if protocol['audit_sha256']!=sha256(audit_path):
        raise ValueError('Protocol was frozen against a different audit')
    roles={r['id']:r for r in protocol['events']}
    if len(roles)!=len(protocol['events']) or set(roles)!={e['id'] for e in inventory['events']}:
        raise ValueError('Protocol must cover each audited event exactly once')
    sessions={};groups={}
    for event in inventory['events']:
        row=roles[event['id']]
        if row['humanLabel']!=event['humanLabel'] or row['split'] not in ('train','validation','test'):
            raise ValueError('Protocol label or split mismatch')
        sessions.setdefault(row['session'],set()).add(row['split'])
        groups.setdefault(event['groupId'],set()).add(row['split'])
    if any(len(v)>1 for v in (*sessions.values(),*groups.values())):
        raise ValueError('Session/groupId crosses train/validation/test')
    return roles


def prepare(args):
    """Never opens test audio. Old corpus sampler/seed remain exactly as before."""
    if args.output.exists():
        raise ValueError('Output must be new')
    inventory=json.loads(args.audit.read_text())
    split=json.loads(args.protocol.read_text())
    inspection=json.loads(args.inspection.read_text())
    if inspection.get('complete') is not True:
        raise ValueError('Lexical inspection is still incomplete')
    screened={r['id']:r for r in inspection['events']}
    roles=validate_protocol(inventory,split,args.audit)
    train_fp={e['id'] for e in inventory['events']
              if e['humanLabel']=='false_positive' and roles[e['id']]['split']=='train'}
    if len(train_fp)!=57 or not train_fp.issubset(screened) or len(screened)!=len(inspection['events']):
        raise ValueError('Every one of the 57 frozen training FP events must be screened exactly once')
    by_id={e['id']:e for e in inventory['events']}
    if any(r['id'] not in by_id or r['humanLabel']!=by_id[r['id']]['humanLabel'] for r in inspection['events']):
        raise ValueError('Inspection IDs or human labels differ from audit')
    baseline=baseline_guards(check_audio=True)
    args.output.mkdir(parents=True,mode=0o700)
    old=argparse.Namespace(dataset=ROOT/'comparison/training',references=ROOT/'experiment/references')
    arrays, provenance, oldsha=load_training(old)
    extra, ep, devsha=load_real_negatives(ROOT/'comparison/microwakeword/real-negatives',BASE/'real-negative-manifest.json')
    if oldsha!=ORIGINAL_MANIFEST_SHA or devsha!=DEV_MANIFEST_SHA:
        raise ValueError('Recovered corpus manifest changed during feature generation')
    for name in arrays:
        for i,key in enumerate(('x','y')):
            np.save(args.output/f'{name}_{key}.npy',np.concatenate((arrays[name][i],extra[name][i])))
    new={'train':[],'validation':[]}
    meta={'train':[],'validation':[]}
    event_roles=[]
    for event in inventory['events']:
        role=roles[event['id']]
        name=role['split']
        disposition={'id':event['id'],'split':name,'humanLabel':event['humanLabel'],
                     'session':role['session'],'feature_windows':0,'role':'excluded'}
        event_roles.append(disposition)
        if name not in new or event['humanLabel']!='false_positive':
            disposition['reason']='reserved-test' if name=='test' else 'all-new-positives-evaluation-only'
            continue
        if not event['audit']['training_eligible_context']:
            disposition['reason']='audit-ineligible'
            continue
        if name=='train' and (event['id'] not in screened or screened[event['id']]['possible_wake_token']):
            disposition['reason']='possible-wake-token-on-advisory-lexical-screen'
            continue
        if event['audit']['decision_sample_in_context']//160-2<FEATURES:
            disposition['reason']='insufficient-pretrigger-context-for-complete-receptive-window'
            continue
        disposition.update(role='hard-negative-'+name,reason='decision-near-event-label; post-excluded')
        path=Path(event['audit']['trigger_path'])
        if sha256(path)!=event['triggerContext']['sha256']:
            raise ValueError('Changed audited input')
        audio=wav_samples(path)
        decision=event['audit']['decision_sample_in_context']
        # Restrict supervision to the 1.86s receptive field ending BEFORE or AT
        # the labelled decision. No post-activation audio is labelled negative.
        for gain in ((1.,.85,1.15) if name=='train' else (1.,)):
            altered=np.clip(audio.astype(np.float32)*gain,-32768,32767).astype(np.int16)
            spectrum=features(altered)
            for shift in (-6,-3,0):
                end=decision//160-2+shift
                if end<FEATURES:
                    continue
                new[name].append(feature_window(spectrum,end))
                meta[name].append({'id':event['id'],'session':role['session'],'gain':gain,'end_frame':end,'sha256':event['triggerContext']['sha256']})
                disposition['feature_windows']+=1
    for name in new:
        if not new[name]: raise ValueError('No hard negatives in '+name)
        np.save(args.output/f'{name}_hard.npy',np.stack(new[name]))
    save_json(args.output/'features.json',{'protocol_sha256':sha256(args.protocol),'audit_sha256':sha256(args.audit),'inspection_sha256':sha256(args.inspection),
        'array_sha256':{p.name:sha256(p) for p in sorted(args.output.glob('*.npy'))},
        'baseline':baseline,'preparation_script_sha256':sha256(Path(__file__)),
        'original_loader_script_sha256':sha256(Path(__file__).with_name('train_microwakeword.py')),
        'event_roles':event_roles,
        'original_manifest_sha256':oldsha,'dev_manifest_sha256':devsha,'hard_windows':meta,'old_provenance':provenance+ep,
        'extraction':'full-context C microfrontend without normalization; decision-relative pretrigger ends -60,-30,0ms; gains1,.85,1.15 train only; post excluded'})
    print('Prepared features', {k:len(v) for k,v in new.items()},flush=True)


def train(args):
    baseline=baseline_guards()
    source=ROOT/'comparison/microwakeword/source'
    if args.output.exists(): raise ValueError('Output must be new')
    args.output.mkdir(mode=0o700,parents=True)
    sys.path.insert(0,str(source))
    import tensorflow as tf
    from microwakeword import mixednet, utils
    from microwakeword.layers import modes
    tf.keras.utils.set_random_seed(SEED)
    tf.config.experimental.enable_op_determinism()
    spec=json.loads(args.protocol.read_text())
    feature_manifest=json.loads((args.features/'features.json').read_text())
    if sha256(args.protocol)!=feature_manifest['protocol_sha256']:
        raise ValueError('Protocol changed since feature extraction')
    expected_arrays={f'{s}_{k}.npy' for s in ('train','validation') for k in ('x','y','hard')}
    if set(feature_manifest['array_sha256'])!=expected_arrays:
        raise ValueError('Unexpected feature array inventory')
    for filename,digest in feature_manifest['array_sha256'].items():
        path=(args.features/filename).resolve()
        if not path.is_relative_to(args.features) or sha256(path)!=digest:
            raise ValueError('Feature array changed since extraction')
    flags=argparse.Namespace(**json.loads((BASE/'training.json').read_text())['architecture'])
    model=mixednet.model(flags,(FEATURES,40),None)
    original=mixednet.model(flags,(FEATURES,40),None)
    original.load_weights(BASE/'best.weights.h5')
    if args.strategy=='A': model.load_weights(BASE/'best.weights.h5')
    cfg=spec['experiments'][args.strategy]
    model.compile(optimizer=tf.keras.optimizers.Adam(cfg['learning_rate']),loss='binary_crossentropy')
    x=np.load(args.features/'train_x.npy'); y=np.load(args.features/'train_y.npy')
    vx=np.load(args.features/'validation_x.npy'); vy=np.load(args.features/'validation_y.npy')
    hx=np.load(args.features/'train_hard.npy'); hv=np.load(args.features/'validation_hard.npy')
    fm=json.loads((args.features/'features.json').read_text())
    groups={}
    for i,row in enumerate(fm['hard_windows']['train']):
        groups.setdefault(row['session'],{}).setdefault(row['id'],[]).append(i)
    event_count=sum(len(group) for group in groups.values())
    event_cap=2*math.ceil(spec['hard_draws_per_epoch']/event_count)
    def predict(m,a):
        return np.concatenate([m(a[i:i+128],training=False).numpy() for i in range(0,len(a),128)])
    def bce(t,p): return float(tf.keras.losses.binary_crossentropy(t,p).numpy().mean())
    basep=predict(original,vx)
    base_recall=float(np.mean(basep[vy[:,0]==1]>=.5))
    rng=np.random.default_rng(SEED)
    history=[]; best=float('inf'); start=time.perf_counter();best_epoch=None
    for epoch in range(cfg['epochs']):
        model.reset_metrics()
        # Fixed budget: 1024 hard draws vs8869 original examples/epoch.
        # Equal available session/event probability, with a source repetition
        # cap so a singleton session cannot occupy 25% of the hard pool.
        chosen=[];source_draws={}
        for _ in range(spec['hard_draws_per_epoch']):
            available={key:{event:indices for event,indices in group.items()
                             if source_draws.get(event,0)<event_cap}
                       for key,group in groups.items()}
            available={key:group for key,group in available.items() if group}
            group=available[rng.choice(sorted(available))]
            event=rng.choice(sorted(group))
            source_draws[event]=source_draws.get(event,0)+1
            chosen.append(rng.choice(group[event]))
        tx=np.concatenate((x,hx[chosen]));ty=np.concatenate((y,np.zeros((len(chosen),1),np.float32)))
        order=rng.permutation(len(tx));loss=[]
        for offset in range(0,len(order),64):
            idx=order[offset:offset+64];bx=tx[idx].copy()
            for sample in bx:
                if rng.random()<.5:
                    a=int(rng.integers(FEATURES-4));sample[a:a+4]=0
                if rng.random()<.5:
                    a=int(rng.integers(36));sample[:,a:a+4]=0
            loss.append(float(model.train_on_batch(bx,ty[idx])))
        vp=predict(model,vx);hp=predict(model,hv)
        oldloss=bce(vy,vp);hardloss=bce(np.zeros_like(hp),hp)
        recall=float(np.mean(vp[vy[:,0]==1]>=.5))
        objective=.75*oldloss+.25*hardloss
        eligible=recall>=base_recall-spec['max_old_validation_recall_drop']
        row={'epoch':epoch+1,'train_loss':float(np.mean(loss)),'old_validation_bce':oldloss,'hard_validation_bce':hardloss,
             'old_validation_positive_recall':recall,'hard_validation_fraction_ge_half':float(np.mean(hp>=.5)),
             'objective':objective,'eligible':eligible,
             'max_draws_per_event':max(source_draws.values()),'hard_event_cap':event_cap}
        history.append(row);print(json.dumps(row),flush=True)
        if eligible and objective<best:
            best=objective;best_epoch=epoch+1;model.save_weights(args.output/'best.weights.h5')
        save_json(args.output/'history.json',history)
    if best_epoch is None: raise ValueError('No checkpoint passed original-validation recall guard')
    model.load_weights(args.output/'best.weights.h5')
    config={'spectrogram_length':FEATURES,'stride':STRIDE,'train_dir':str(args.output)}
    utils.convert_model_saved(model,config,'streaming_saved',modes.Modes.STREAM_INTERNAL_STATE_INFERENCE)
    class Calibration:
        def get_data(self,*unused,**kwargs):return x[:500].copy(),y[:500],None
    utils.convert_saved_model_to_tflite(config,Calibration(),str(args.output/'streaming_saved'),str(args.output),'aegis.int8.tflite',quantize=True)
    from ai_edge_litert.interpreter import Interpreter
    interpreter=Interpreter(model_path=str(args.output/'aegis.int8.tflite'),num_threads=1);interpreter.allocate_tensors()
    ins=interpreter.get_input_details()[0];outs=interpreter.get_output_details()[0]
    if ins['dtype']!=np.int8 or outs['dtype']!=np.uint8 or any('float' in str(t['dtype']) for t in interpreter.get_tensor_details()):raise ValueError('Not fully integer')
    save_json(args.output/'training.json',{'schema':1,'strategy':args.strategy,'source_sha':SOURCE_SHA,'baseline_sha256':BASE_SHA,
        'checkpoint_sha256':BASE_WEIGHTS_SHA if args.strategy=='A' else None,'baseline_guard_checkpoint_sha256':BASE_WEIGHTS_SHA,
        'baseline':baseline,'optimizer_resumed':False,'training_script_sha256':sha256(Path(__file__)),
        'original_loader_script_sha256':sha256(Path(__file__).with_name('train_microwakeword.py')),
        'protocol_sha256':sha256(args.protocol),'features_manifest_sha256':sha256(args.features/'features.json'),'seed':SEED,
        'config':cfg,'architecture':vars(flags),'params':model.count_params(),'best_epoch':best_epoch,'best_objective':best,
        'original_validation_baseline_recall':base_recall,'history':history,'model_bytes':(args.output/'aegis.int8.tflite').stat().st_size,
        'model_sha256':sha256(args.output/'aegis.int8.tflite'),'input_shape':ins['shape'].tolist(),'input_quantization':ins['quantization'],
        'output_shape':outs['shape'].tolist(),'output_quantization':outs['quantization'],'elapsed_seconds':time.perf_counter()-start,
        'versions':{n:importlib.metadata.version(n) for n in ('tensorflow-cpu','keras','numpy','pymicro-features','ai-edge-litert')},
        'evaluation_excluded':'all test sessions; all new positives; old23+8; public35min; FLEURS TEST'})


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('action',choices=['prepare','train']);p.add_argument('--audit',type=Path)
    p.add_argument('--inspection',type=Path);p.add_argument('--protocol',type=Path,required=True);p.add_argument('--features',type=private_output)
    p.add_argument('--strategy',choices=['A','B']);p.add_argument('--output',type=private_output,required=True)
    a=p.parse_args();os.umask(0o077)
    if a.action=='prepare':prepare(a)
    else:train(a)
if __name__=='__main__':main()
