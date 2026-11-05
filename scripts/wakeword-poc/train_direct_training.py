#!/usr/bin/env python3
"""Baseline fine-tuning with every owner-labelled TRAIN event, no acoustic gates."""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
os.environ.setdefault('CUDA_VISIBLE_DEVICES','-1')
os.environ.setdefault('TF_CPP_MIN_LOG_LEVEL','2')
os.environ.setdefault('TF_NUM_INTRAOP_THREADS','2')
os.environ.setdefault('TF_NUM_INTEROP_THREADS','1')
import numpy as np
from prepare import private_output, sha256
from train_hardneg import baseline_guards, BASE, ROOT, BASE_SHA, save_json
from train_microwakeword import FEATURES

LIB=Path.home()/'.cache/aegis-hands-free/activation-native-20261101/libaegis_microwakeword.so'
PLAN={'seed':20261105,'epochs':4,'learning_rate':1e-5,'steps_per_epoch':32,
      'initializer':'immutable baseline checkpoint; fresh Adam','batch_normalization':'frozen',
      'batch_sources':{'synthetic_positive':20,'reference_positive':4,'real_positive':8,
                       'synthetic_negative':16,'public_dev_negative':8,'real_negative':8},
      'real_sampling':'Cycles of shuffled original event IDs within class; choose gain variant after ID. Every221TRAIN event in everyepoch.',
      'checkpoint_order':'paired real validation positive losses across both runtimes, original validation recall deficit beyond2pp, validation negative contexts, original positive paired losses, original validation BCE',
      'eligible':'zero paired real validation losses in both runtimes and original positive validation recall within2pp of baseline',
      'test_policy':'checkpoint selection uses only provided validation and original corpus validation; test/known legacy opened after selection seal',
      'threshold':.5,'average_window':5,'release_below':.3,'refractory_samples':16000}


def read(path):return json.loads(path.read_text())

def cover_events(rows, labels, label, budget, rng):
    """Augmentation count never changes an original event's sampling probability."""
    groups=defaultdict(list)
    for index,row in enumerate(rows):
        if float(labels[index,0])==label:groups[row['id']].append(index)
    if not groups or budget<len(groups):raise ValueError('Budget cannot cover every labelled event')
    ids=sorted(groups);chosen=[];counts=Counter()
    while len(chosen)<budget:
        for i in rng.permutation(len(ids)):
            event=ids[int(i)];chosen.append(int(rng.choice(groups[event])));counts[event]+=1
            if len(chosen)==budget:break
    if set(counts)!=set(groups):raise ValueError('Original event omitted from epoch')
    return np.asarray(chosen,dtype=int),dict(counts)


def validation(model,a,destination):
    prepared=a.prepared
    command=[sys.executable,str(Path(__file__).with_name('evaluate_hardneg.py')),
             '--model',str(model),'--audit',str(prepared/'audit/inventory.json'),
             '--split-manifest',str(prepared/'protocol.json'),'--provided-splits',
             '--split','validation','--library',str(LIB),'--output',str(destination)]
    with destination.with_suffix('.log').open('x') as log:
        subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,check=True)
    result=read(destination/'evaluation.json')
    if 'native_host' not in result['runtimes']:raise ValueError('Native runtime rejected export')
    return result


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--prepared',type=private_output,required=True)
    p.add_argument('--baseline-evaluation',type=private_output,required=True)
    p.add_argument('--output',type=private_output,required=True)
    p.add_argument('--conservative',action='store_true',help='Single bounded validation-motivated follow-up:2epochs at3e-6, same data/sampler')
    a=p.parse_args();os.umask(0o077)
    plan=dict(PLAN)
    if a.conservative:plan.update(epochs=2,learning_rate=3e-6)
    guard=baseline_guards();protocol=a.prepared/'protocol.json';fm=read(a.prepared/'features/features.json')
    if fm['protocol_sha256']!=sha256(protocol):raise ValueError('Prepared protocol changed')
    fp=a.prepared/'features'
    for name,digest in fm['array_sha256'].items():
        if Path(name).name!=name or sha256(fp/name)!=digest:raise ValueError('Prepared feature array changed')
    x=np.load(fp/'train_x.npy');y=np.load(fp/'train_y.npy')
    rx=np.load(fp/'train_real_x.npy');ry=np.load(fp/'train_real_y.npy')
    vx=np.load(fp/'validation_x.npy');vy=np.load(fp/'validation_y.npy')
    metadata=fm['real_windows']['train']
    if len(metadata)!=len(rx) or len(rx)!=len(ry):raise ValueError('Real feature metadata mismatch')
    roles={r['id']:r for r in read(protocol)['events']}
    train={key:r for key,r in roles.items() if r['split']=='train'}
    expected=Counter(r['humanLabel'] for r in train.values())
    if expected!={'correct':91,'false_positive':130}:raise ValueError('Unexpected supplied TRAIN split')
    if {r['id'] for r in metadata}!=set(train):raise ValueError('Not every221TRAIN event prepared exactly in TRAIN')
    for i,row in enumerate(metadata):
        role=roles[row['id']]
        if role['split']!='train' or float(ry[i,0])!=float(role['humanLabel']=='correct'):
            raise ValueError('Human label/split changed in training arrays')
    baseline=read(a.baseline_evaluation)
    if baseline['model_sha256']!=BASE_SHA or baseline['split']!='validation' or baseline['split_manifest_sha256']!=sha256(protocol):
        raise ValueError('Baseline evaluation is not this validation split')
    a.output.mkdir(parents=True,mode=0o700,exist_ok=False)
    save_json(a.output/'plan.json',{'created_at':datetime.now(timezone.utc).isoformat(),'plan':plan,
        'protocol_sha256':sha256(protocol),'features_manifest_sha256':sha256(fp/'features.json'),
        'training_script_sha256':sha256(Path(__file__)),'baseline_evaluation_sha256':sha256(a.baseline_evaluation)})
    sys.path.insert(0,str(ROOT/'comparison/microwakeword/source'))
    import tensorflow as tf
    from microwakeword import mixednet
    tf.keras.utils.set_random_seed(plan['seed']);tf.config.experimental.enable_op_determinism()
    flags=argparse.Namespace(**read(BASE/'training.json')['architecture'])
    model=mixednet.model(flags,(FEATURES,40),None);model.load_weights(BASE/'best.weights.h5')
    frozen=[]
    for layer in model._flatten_layers():
        if isinstance(layer,tf.keras.layers.BatchNormalization):layer.trainable=False;frozen.append(layer.name)
    if not frozen:raise ValueError('Expected baseline BatchNormalization layers')
    model.compile(optimizer=tf.keras.optimizers.Adam(plan['learning_rate']),loss='binary_crossentropy')
    def predict(xx):return np.concatenate([model(xx[i:i+128],training=False).numpy() for i in range(0,len(xx),128)])
    bp=predict(vx);pos=vy[:,0]==1;baseline_recall=float(np.mean(bp[pos]>=.5))
    rng=np.random.default_rng(plan['seed']);history=[];best=None;best_epoch=None;started=time.perf_counter()
    for epoch in range(1,plan['epochs']+1):
        out=a.output/f'epoch-{epoch:02d}';out.mkdir(mode=0o700);steps=plan['steps_per_epoch'];sources=plan['batch_sources'];draws={};exposure={}
        for name,pool in fm['source_indices'].items():
            pool=np.asarray(pool);budget=sources[name]*steps
            draw=np.concatenate([rng.permutation(pool) for _ in range(math.ceil(budget/len(pool)))])[:budget]
            draws[name]=draw;exposure[name]={'presentations':budget,'unique_windows':len(set(map(int,draw)))}
        real_counts={}
        for name,label,n in (('real_positive',1.,91),('real_negative',0.,130)):
            draws[name],counts=cover_events(metadata,ry,label,sources[name]*steps,rng)
            if len(counts)!=n:raise ValueError('Real TRAIN event coverage failed')
            real_counts[name]=counts
        batch_losses=[];model.reset_metrics()
        for step in range(steps):
            batches=[];targets=[]
            for name,size in sources.items():
                ii=draws[name][step*size:(step+1)*size];real=name.startswith('real_')
                batch=(rx if real else x)[ii].copy()
                # Only inherited synthetic/reference/DEV masking; no real audio quality test.
                if not real:
                    for sample in batch:
                        if rng.random()<.5:
                            k=int(rng.integers(FEATURES-4));sample[k:k+4]=0
                        if rng.random()<.5:
                            k=int(rng.integers(36));sample[:,k:k+4]=0
                batches.append(batch);targets.append((ry if real else y)[ii])
            bx=np.concatenate(batches);by=np.concatenate(targets)
            if len(bx)!=64 or np.sum(by)!=32:raise ValueError('Minibatch class balance failed')
            order=rng.permutation(64);batch_losses.append(float(model.train_on_batch(bx[order],by[order])))
        model.save_weights(out/'best.weights.h5');vp=predict(vx)
        original_recall=float(np.mean(vp[pos]>=.5));deficit=original_recall<baseline_recall-.02
        old_lost=int(np.sum((bp[pos]>=.5)&(vp[pos]<.5)))
        bce=float(tf.keras.losses.binary_crossentropy(vy,vp).numpy().mean())
        with (out/'export.log').open('x') as log:
            subprocess.run([sys.executable,str(Path(__file__).with_name('export_positive.py')),
                '--checkpoint',str(out/'best.weights.h5'),'--features',str(fp),'--output',str(out)],stdout=log,stderr=subprocess.STDOUT,check=True)
        if (out/'aegis.int8.tflite').stat().st_size!=39552:raise ValueError('Native model size contract failed')
        ev=validation(out/'aegis.int8.tflite',a,out/'validation');lost={};false=[]
        for rt in ('litert','native_host'):
            b={r['id']:r for r in baseline['runtimes'][rt]['contexts']['events']};rows=ev['runtimes'][rt]['contexts']['events']
            lost[rt]=[r['id'] for r in rows if r['humanLabel']=='correct' and b[r['id']]['detected_near_decision'] and not r['detected_near_decision']]
            false.append(sum(r['humanLabel']=='false_positive' and r['detected_near_decision'] for r in rows))
        key=[sum(map(len,lost.values())),int(deficit),sum(false),old_lost,bce]
        row={'epoch':epoch,'train_loss':float(np.mean(batch_losses)),'original_validation_bce':bce,
             'original_validation_recall':original_recall,'real_validation_paired_losses':lost,
             'validation_negative_contexts_litert_native':false,'selection_key':key,
             'eligible':key[0]==0 and not deficit,'exposure':exposure,'real_event_draws':real_counts,
             'real_training_unique_events':221,'real_training_class_counts':dict(expected),
             'model_sha256':sha256(out/'aegis.int8.tflite'),'validation_sha256':sha256(out/'validation/evaluation.json')}
        history.append(row)
        if best is None or key<best:best=key;best_epoch=epoch
        save_json(a.output/'history.json',history)
        print(json.dumps({k:v for k,v in row.items() if k not in ('real_event_draws','real_validation_paired_losses','exposure')}),flush=True)
    for name in ('aegis.int8.tflite','best.weights.h5'):shutil.copyfile(a.output/f'epoch-{best_epoch:02d}'/name,a.output/name)
    selected=history[best_epoch-1]
    save_json(a.output/'training.json',{'plan':plan,'history':history,'best_epoch':best_epoch,'best_key':best,
        'eligible':selected['eligible'],'baseline':guard,'params':model.count_params(),
        'frozen_batch_norm_layers':frozen,'protocol_sha256':sha256(protocol),
        'features_manifest_sha256':sha256(fp/'features.json'),'training_script_sha256':sha256(Path(__file__)),
        'model_sha256':sha256(a.output/'aegis.int8.tflite'),'elapsed_seconds':time.perf_counter()-started})
    save_json(a.output/'selection.json',{'created_at':datetime.now(timezone.utc).isoformat(),
        'protocol_sha256':sha256(protocol),'training_sha256':sha256(a.output/'training.json'),
        'selected_epoch':best_epoch,'model_sha256':sha256(a.output/'aegis.int8.tflite'),
        'decision':'VALIDATION-PASS' if selected['eligible'] else 'NO-GO','eligible':selected['eligible'],
        'scope':'Sealed by validation only; final test/legacy still pending. No further fitting or threshold changes.'})
if __name__=='__main__':main()
