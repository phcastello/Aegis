#!/usr/bin/env python3
"""Private, bounded positive retraining. Immutable source, artifacts and heldouts."""
import argparse
from collections import defaultdict, Counter
from datetime import datetime, timezone
import json
import math
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import numpy as np
from train_hardneg import baseline_guards, BASE, ROOT, BASE_SHA, save_json
from train_microwakeword import features, feature_window, wav_samples, FEATURES, STRIDE
from prepare import private_output, sha256
os.environ.setdefault('CUDA_VISIBLE_DEVICES','-1')
os.environ.setdefault('TF_CPP_MIN_LOG_LEVEL','2')
os.environ.setdefault('TF_NUM_INTRAOP_THREADS','2');os.environ.setdefault('TF_NUM_INTEROP_THREADS','1')
OLD=Path.home()/'.cache/aegis-wakeword-hardneg'
LIB=Path.home()/'.cache/aegis-hands-free/activation-native-20261101/libaegis_microwakeword.so'


def load(path):return json.loads(path.read_text())
def verify_arrays(root, manifest):
    for n,h in manifest['array_sha256'].items():
        assert Path(n).name==n and sha256(root/n)==h,'Array hash mismatch'

def prepare(a):
    assert not a.output.exists();baseline_guards(check_audio=True)
    spec=load(a.protocol);audit=load(a.audit);inspection=load(a.inspection)
    assert spec['audit_sha256']==sha256(a.audit) and inspection['complete']
    roles={r['id']:r for r in spec['events']};screen={r['id']:r for r in inspection['events']}
    oldfeatures=OLD/'features-v1';fm=load(oldfeatures/'features.json');verify_arrays(oldfeatures,fm)
    a.output.mkdir(parents=True,mode=0o700)
    for n in fm['array_sha256']:shutil.copyfile(oldfeatures/n,a.output/n)
    # Recover original source indices from the deterministic loader's append order.
    x=np.load(a.output/'train_x.npy');y=np.load(a.output/'train_y.npy')[:,0]
    original_manifest=load(ROOT/'comparison/training/manifest.json')
    nsynt=2*sum(r['split']=='train' for r in original_manifest['records'])
    nrefs=960;assert nsynt+nrefs+307*3==len(x)
    sources={'synthetic_positive':np.flatnonzero((np.arange(len(y))<nsynt)&(y==1)).tolist(),
             'reference_positive':np.flatnonzero((np.arange(len(y))>=nsynt)&(np.arange(len(y))<nsynt+nrefs)&(y==1)).tolist(),
             'synthetic_negative':np.flatnonzero((np.arange(len(y))<nsynt+nrefs)&(y==0)).tolist(),
             'public_dev_negative':list(range(nsynt+nrefs,len(y)))}
    real=[];meta=[];dispositions=[]
    for e in audit['events']:
        r=roles[e['id']];q=screen.get(e['id']);d={'id':e['id'],'origin':r['origin'],'split':r['split'],'label':e['humanLabel'],'included':False}
        dispositions.append(d)
        if r['origin']!='new99' or r['split']!='train' or e['humanLabel']!='correct':
            d['reason']='frozen-role-not-new-training-positive';continue
        if not q or not q['eligible']:
            d['reason']='acoustic-screen-excluded';d['screen_reasons']=q.get('reasons',[]) if q else ['missing'];continue
        path=Path(e['audit']['trigger_path']);assert sha256(path)==e['triggerContext']['sha256']
        decision=e['audit']['decision_sample_in_context'];wordend=q['keyword_end_sample'];wordstart=q['keyword_start_sample']
        assert 0<wordstart<wordend<decision
        audio=wav_samples(path)[:decision]  # Exclude ALL post-decision samples before frontend.
        ends=sorted(set(min(decision,wordend+round(t*16000)) for t in (.06,.12)))
        group='new-train-minute-'+e['activatedAt'][14:16]
        for gain in (.85,1.,1.15):
            spectrum=features(np.clip(audio.astype(np.float32)*gain,-32768,32767).astype(np.int16))
            for end in ends:
                frame=end//160-2
                # A left-padded receptive window must contain the entire labelled word.
                assert max(0,(frame-FEATURES)*160)<wordstart
                real.append(feature_window(spectrum,frame))
                meta.append({'id':e['id'],'session':group,'gain':gain,'end_sample':end,'sha256':e['triggerContext']['sha256']})
        d.update(included=True,reason='human-event-label+conservative-local-acoustic-screen',windows=3*len(ends))
    assert real,'No confirmed new TRAIN positives; do not weaken screen to continue'
    np.save(a.output/'train_real.npy',np.stack(real))
    save_json(a.output/'features.json',{'schema':1,'protocol_sha256':sha256(a.protocol),'audit_sha256':sha256(a.audit),
        'inspection_sha256':sha256(a.inspection),'old_features_sha256':sha256(oldfeatures/'features.json'),
        'array_sha256':{p.name:sha256(p) for p in sorted(a.output.glob('*.npy'))},'source_indices':sources,
        'real_windows':meta,'hard_windows':fm['hard_windows']['train'],'event_roles':dispositions,
        'script_sha256':sha256(Path(__file__)),'baseline':baseline_guards(),
        'unique_real_training_events':len({r['id'] for r in meta}),'derived_real_windows':len(real),
        'note':'Original arrays exact copies; resampled by explicit sources, not exhaustive traversal. Old15 correct never fit. No post WAV used.'})
    print(json.dumps({'real_events':len({r['id'] for r in meta}),'real_windows':len(real),'source_pool_sizes':{k:len(v) for k,v in sources.items()}}),flush=True)


def draw_events(meta, budget, rng):
    groups=defaultdict(lambda:defaultdict(list))
    for i,r in enumerate(meta):groups[r['session']][r['id']].append(i)
    n=sum(len(g) for g in groups.values());cap=2*math.ceil(budget/n);counts=Counter();chosen=[]
    for _ in range(budget):
        available={k:{e:ii for e,ii in g.items() if counts[e]<cap} for k,g in groups.items()}
        available={k:g for k,g in available.items() if g};g=available[rng.choice(sorted(available))]
        event=rng.choice(sorted(g));chosen.append(int(rng.choice(g[event])));counts[event]+=1
    return chosen,dict(counts),cap


def replay(model,a,out):
    command=[sys.executable,str(Path(__file__).with_name('evaluate_hardneg.py')),'--model',str(model),'--audit',str(a.audit),'--split-manifest',str(a.protocol),'--split','validation','--library',str(LIB),'--output',str(out)]
    with (out.parent/(out.name+'.log')).open('x') as log:subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,check=True)
    result=load(out/'evaluation.json');assert 'native_host' in result['runtimes'];return result


def losses(baseline,candidate):
    paired={};false_counts=[]
    for rt in ('litert','native_host'):
        b={r['id']:r for r in baseline['runtimes'][rt]['contexts']['events']};c=candidate['runtimes'][rt]['contexts']['events']
        lost=[r['id'] for r in c if r['humanLabel']=='correct' and b[r['id']]['detected_near_decision'] and not r['detected_near_decision']]
        paired[rt]=lost;false_counts.append(sum(r['humanLabel']=='false_positive' and r['detected_near_decision'] for r in c))
    return paired,false_counts


def train(a):
    assert not a.output.exists();guard=baseline_guards();spec=load(a.protocol);fm=load(a.features/'features.json')
    assert fm['protocol_sha256']==sha256(a.protocol) and spec['audit_sha256']==sha256(a.audit)
    verify_arrays(a.features,fm);a.output.mkdir(parents=True,mode=0o700)
    sys.path.insert(0,str(ROOT/'comparison/microwakeword/source'))
    import tensorflow as tf
    from microwakeword import mixednet,utils
    from microwakeword.layers import modes
    tf.keras.utils.set_random_seed(spec['seed']);tf.config.experimental.enable_op_determinism()
    flags=argparse.Namespace(**load(BASE/'training.json')['architecture'])
    model=mixednet.model(flags,(FEATURES,40),None)
    cfg=spec['experiments'][a.strategy]
    weights=BASE/'best.weights.h5' if cfg['initialization']=='baseline' else OLD/'model-A-v1/best.weights.h5'
    expected='9e061278cc5279d6d357d42b3d9d549fc01e997e128029c3f1524666cef4b2de' if a.strategy=='A' else '646e4579b3da3315c817e84f9d7d6f745585871b28def11be311cf219d5fa0f5'
    assert sha256(weights)==expected;model.load_weights(weights)
    frozen=[]
    for layer in model._flatten_layers():
        if isinstance(layer,tf.keras.layers.BatchNormalization):layer.trainable=False;frozen.append(layer.name)
    assert frozen,'Expected BN layers'
    model.compile(optimizer=tf.keras.optimizers.Adam(cfg['learning_rate']),loss='binary_crossentropy')
    x=np.load(a.features/'train_x.npy');y=np.load(a.features/'train_y.npy');hx=np.load(a.features/'train_hard.npy');rx=np.load(a.features/'train_real.npy')
    vx=np.load(a.features/'validation_x.npy');vy=np.load(a.features/'validation_y.npy')
    def predict(m,xx):return np.concatenate([m(xx[i:i+128],training=False).numpy() for i in range(0,len(xx),128)])
    original=mixednet.model(flags,(FEATURES,40),None);original.load_weights(BASE/'best.weights.h5');bp=predict(original,vx)
    pos=vy[:,0]==1;base_recall=float(np.mean(bp[pos]>=.5))
    validation_baseline=load(a.baseline_evaluation);assert validation_baseline['model_sha256']==BASE_SHA
    assert validation_baseline['split_manifest_sha256']==sha256(a.protocol)
    rng=np.random.default_rng(spec['seed']);history=[];start=time.perf_counter();best=None;best_epoch=None
    for epoch in range(1,cfg['epochs']+1):
        out=a.output/f'epoch-{epoch:02d}';out.mkdir(mode=0o700)
        steps=spec['steps_per_epoch'];sources=spec['batch_sources'];draws={};exposure={}
        for source in fm['source_indices']:
            # Cycle random permutations to cover available sources without avoidable repetition.
            pool=np.asarray(fm['source_indices'][source]);budget=sources[source]*steps
            indices=np.concatenate([rng.permutation(pool) for _ in range(math.ceil(budget/len(pool)))])[:budget]
            draws[source]=indices;exposure[source]={'draws':budget,'unique_windows':len(set(map(int,indices)))}
        draws['real_positive'],rc,rcap=draw_events(fm['real_windows'],sources['real_positive']*steps,rng)
        draws['hard_negative'],hc,hcap=draw_events(fm['hard_windows'],sources['hard_negative']*steps,rng)
        loss=[];model.reset_metrics()
        for step in range(steps):
            xx=[];yy=[]
            for source,n in sources.items():
                ii=np.asarray(draws[source][step*n:(step+1)*n]);arr=rx if source=='real_positive' else hx if source=='hard_negative' else x
                batch=arr[ii].copy()
                # Preserve the original masking only on synthetic/reference/public features.
                if source not in ('real_positive','hard_negative'):
                    for sample in batch:
                        if rng.random()<.5:
                            k=int(rng.integers(FEATURES-4));sample[k:k+4]=0
                        if rng.random()<.5:
                            k=int(rng.integers(36));sample[:,k:k+4]=0
                xx.append(batch);yy.append(np.ones((n,1),np.float32) if source=='real_positive' else np.zeros((n,1),np.float32) if source=='hard_negative' else y[ii])
            xx=np.concatenate(xx);yy=np.concatenate(yy);assert np.sum(yy)==32
            order=rng.permutation(64);loss.append(float(model.train_on_batch(xx[order],yy[order])))
        model.save_weights(out/'best.weights.h5')
        pred=predict(model,vx);recall=float(np.mean(pred[pos]>=.5));bce=float(tf.keras.losses.binary_crossentropy(vy,pred).numpy().mean())
        # Fresh process gives the canonical first-model tensor names/size and
        # avoids upstream _set_mode mutating the live training model.
        with (out/'export.log').open('x') as log:
            subprocess.run([sys.executable,str(Path(__file__).with_name('export_positive.py')),
                '--checkpoint',str(out/'best.weights.h5'),'--features',str(a.features),
                '--output',str(out)],stdout=log,stderr=subprocess.STDOUT,check=True)
        from ai_edge_litert.interpreter import Interpreter
        it=Interpreter(model_path=str(out/'aegis.int8.tflite'),num_threads=1);it.allocate_tensors();ins=it.get_input_details()[0];outs=it.get_output_details()[0]
        assert ins['shape'].tolist()==[1,3,40] and outs['shape'].tolist()==[1,1]
        assert ins['dtype']==np.int8 and outs['dtype']==np.uint8
        assert ins['quantization']==(.10196078568696976,-128) and outs['quantization']==(1/256,0)
        assert not any('float' in str(t['dtype']) for t in it.get_tensor_details())
        assert (out/'aegis.int8.tflite').stat().st_size==39552, 'Native model size contract'
        ev=replay(out/'aegis.int8.tflite',a,out/'validation');paired,fp=losses(validation_baseline,ev)
        synthetic_lost=int(np.sum((bp[pos]>=.5)&(pred[pos]<.5)))
        real_lost=sum(map(len,paired.values()));deficit=recall<base_recall-.02
        key=[real_lost,int(deficit),sum(fp),synthetic_lost,bce]
        eligible=not real_lost and not deficit
        row={'epoch':epoch,'train_loss':float(np.mean(loss)),'old_validation_bce':bce,'old_validation_positive_recall':recall,
             'old_validation_paired_losses':synthetic_lost,'real_validation_paired_losses':paired,'validation_hard_counts_litert_native':fp,
             'selection_key':key,'eligible':eligible,'exposure':exposure,'real_event_draws':rc,'hard_event_draws':hc,'real_event_cap':rcap,'hard_event_cap':hcap,
             'model_sha256':sha256(out/'aegis.int8.tflite'),'model_bytes':(out/'aegis.int8.tflite').stat().st_size,
             'validation_evidence_sha256':sha256(out/'validation/evaluation.json'),'input_shape':ins['shape'].tolist(),'input_quantization':ins['quantization'],
             'output_shape':outs['shape'].tolist(),'output_quantization':outs['quantization']}
        history.append(row);print(json.dumps({k:v for k,v in row.items() if k not in ('real_event_draws','hard_event_draws','exposure')}),flush=True)
        if best is None or key<best:best=key;best_epoch=epoch
        save_json(a.output/'history.json',history)
    src=a.output/f'epoch-{best_epoch:02d}'
    for name in ('aegis.int8.tflite','best.weights.h5'):shutil.copyfile(src/name,a.output/name)
    save_json(a.output/'training.json',{'strategy':a.strategy,'config':cfg,'protocol_sha256':sha256(a.protocol),'features_manifest_sha256':sha256(a.features/'features.json'),
        'baseline':guard,'initializer_sha256':expected,'optimizer_resumed':False,'frozen_batch_norm_layers':frozen,
        'params':model.count_params(),'trainable_params':sum(int(np.prod(w.shape)) for w in model.trainable_weights),
        'baseline_original_validation_recall':base_recall,'history':history,'best_epoch':best_epoch,'best_key':best,
        'model_sha256':sha256(a.output/'aegis.int8.tflite'),'elapsed_seconds':time.perf_counter()-start,'script_sha256':sha256(Path(__file__)),
        'completed_at':datetime.now(timezone.utc).isoformat()})


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('action',choices=('prepare','train'))
    for n in ('protocol','audit','inspection','features','output','baseline-evaluation'):p.add_argument('--'+n,type=private_output,required=n in ('protocol','audit','output'))
    p.add_argument('--strategy',choices=('A','B'));a=p.parse_args();os.umask(0o077)
    if a.action=='prepare':prepare(a)
    else:train(a)
if __name__=='__main__':main()
