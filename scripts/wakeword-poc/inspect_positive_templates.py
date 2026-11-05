#!/usr/bin/env python3
"""Independent, conservative template corroboration of human-labelled calls.

Calibrate once on six original human word references (leave-one-out), 52 old
training hard negatives and a fixed 32-file FLEURS DEV training sample. Seal the
threshold BEFORE opening any of the 92 new positive WAVs. No detector output or
test negative is read. This is acoustic corroboration, not human phonetic proof.
"""
import argparse
from collections import Counter
import importlib.metadata
import json
import os
from pathlib import Path
import time
import wave
import numpy as np
import python_speech_features
from rhasspywake_raven import dtw
from inspect_positive import acoustics
from prepare import private_output, sha256

ROOT = Path.home()/'.cache/aegis-wakeword-poc'
OLD = Path.home()/'.cache/aegis-wakeword-hardneg'
RULES = {'revision': 1, 'seed': 20261105, 'references': 6,
    'dev_train_files': 32, 'hard_train_events': 52,
    'mfcc': {'winlen': .025, 'winstep': .01, 'numcep': 13, 'nfilt': 40,
             'nfft': 512, 'appendEnergy': False, 'window': 'hamming'},
    'normalization': 'drop c0; utterance cepstral mean; common reference coefficient scale',
    'dtw': {'distance': 'cosine', 'window_fraction': .20,
            'duration_ratio_min': .60, 'duration_ratio_max': 1.67,
            'normalization': 'sum cost divided by sum frame counts'},
    'supporting_references_required': 2,
    'threshold': 'min(max reference LOO second-nearest distance, 0.85*minimum negative second-nearest distance)',
    'negative_windows_seconds': [.4, .6, .8, 1.0], 'negative_window_hop_seconds': .20,
    'positive_activity_components': 1, 'positive_min_speech_seconds': .20,
    'positive_max_speech_seconds': .99, 'capture_edge_margin_seconds': .04,
    'crop_guard_seconds': .08, 'postdecision_used': False,
    'interpretation': 'human correct label plus complete isolated acoustic component and conservative template evidence; no human listening/phonetic validation'}


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n')


def read(path, expected=None):
    if expected and sha256(path) != expected:
        raise ValueError('Changed acoustic input')
    with wave.open(str(path),'rb') as w:
        if (w.getframerate(),w.getnchannels(),w.getsampwidth(),w.getcomptype()) != (16000,1,2,'NONE'):
            raise ValueError('Unexpected acoustic input format')
        return np.frombuffer(w.readframes(w.getnframes()),dtype='<i2').copy()


def mfcc(pcm):
    spec = RULES['mfcc']
    values = python_speech_features.mfcc(pcm, samplerate=16000,
        winlen=spec['winlen'],winstep=spec['winstep'],numcep=spec['numcep'],
        nfilt=spec['nfilt'],nfft=spec['nfft'],appendEnergy=spec['appendEnergy'],winfunc=np.hamming)[:,1:]
    return values-values.mean(axis=0,keepdims=True)


def distances(query, references, scale):
    values=[]
    for reference in references:
        n,m=len(query),len(reference)
        if not RULES['dtw']['duration_ratio_min'] <= n/m <= RULES['dtw']['duration_ratio_max']:
            values.append(1.)
            continue
        window=max(abs(n-m)+1,int(np.ceil(max(n,m)*RULES['dtw']['window_fraction'])))
        engine=dtw.DynamicTimeWarping('cosine')
        cost=float(engine.compute_cost(np.ascontiguousarray(query/scale),np.ascontiguousarray(reference/scale),window=window))
        values.append(cost/(n+m))
    return values


def negative_crops(pcm):
    # Dense fixed windows prevent calibrating only against conveniently isolated
    # negative words. Multiple windows from one event remain one observation.
    for seconds in RULES['negative_windows_seconds']:
        size=round(seconds*16000)
        for offset in range(0,max(0,len(pcm)-size)+1,round(RULES['negative_window_hop_seconds']*16000)):
            part=pcm[offset:offset+size]
            if len(part)==size and np.sqrt(np.mean(part.astype(float)**2))>=60:
                yield offset,part


def main(args):
    os.umask(0o077)
    args.output.mkdir(parents=True,mode=0o700,exist_ok=False)
    started=time.monotonic()
    inventory=json.loads(args.audit.read_text())
    byid={r['id']:r for r in inventory['events']}
    manifest=json.loads((OLD/'features-v1/features.json').read_text())
    hard=[r for r in manifest['event_roles'] if r['role']=='hard-negative-train']
    if len(hard)!=RULES['hard_train_events']:
        raise ValueError('Unexpected old hard training inventory')
    paths=sorted((ROOT/'experiment/references').glob('*.wav'))
    if len(paths)!=6:
        raise ValueError('Expected six preserved human reference crops')
    references=[mfcc(read(path)) for path in paths]
    scale=np.maximum(np.std(np.concatenate(references),axis=0),1.)
    loo=[]
    for i,reference in enumerate(references):
        costs=distances(reference,[r for j,r in enumerate(references) if j!=i],scale)
        loo.append({'reference':paths[i].name,'distances':costs,'second_nearest':sorted(costs)[1]})
    devmanifest=ROOT/'comparison/microwakeword/model-realneg/real-negative-manifest.json'
    dev=sorted([r for r in json.loads(devmanifest.read_text())['records'] if r['split']=='train'],key=lambda r:r['source_id'])
    rng=np.random.default_rng(RULES['seed'])
    dev=[dev[i] for i in sorted(rng.choice(len(dev),RULES['dev_train_files'],replace=False))]
    negative_sources=[]
    for role in hard:
        r=byid[role['id']]
        if r['audit']['previous_role']['split']!='train' or r['humanLabel']!='false_positive':
            raise ValueError('Calibration requested held-out or positive audio')
        negative_sources.append({'id':r['id'],'source':'old-hard-train','path':r['audit']['trigger_path'],
                                 'sha256':r['triggerContext']['sha256'],'decision':r['audit']['decision_sample_in_context']})
    for r in dev:
        negative_sources.append({'id':r['source_id'],'source':'FLEURS-DEV-train','path':r['path'],'sha256':r['sha256']})
    negatives=[]
    for index,source in enumerate(negative_sources):
        pcm=read(Path(source['path']),source['sha256'])
        if 'decision' in source:
            pcm=pcm[:source['decision']]
        best=1.;windows=0
        for offset,crop in negative_crops(pcm):
            costs=distances(mfcc(crop),references,scale)
            best=min(best,sorted(costs)[1]);windows+=1
        negatives.append({**source,'second_nearest_min':best,'windows':windows})
        if index%10==0:
            print('Calibration negatives',index+1,len(negative_sources),flush=True)
    positive_limit=max(r['second_nearest'] for r in loo)
    negative_floor=min(r['second_nearest_min'] for r in negatives)
    threshold=min(positive_limit,.85*negative_floor)
    calibration={'schema':1,'frozen_before_new_positive_audio':True,'rules':RULES,
        'script_sha256':sha256(Path(__file__)),'audit_sha256':sha256(args.audit),
        'reference_sha256':{p.name:sha256(p) for p in paths},'reference_loo':loo,
        'old_features_manifest_sha256':sha256(OLD/'features-v1/features.json'),
        'dev_manifest_sha256':sha256(devmanifest),'negative_sources':negatives,
        'threshold':threshold,'positive_loo_max':positive_limit,'negative_min':negative_floor,
        'loo_references_supported':sum(r['second_nearest']<=threshold for r in loo),
        'scale':scale.tolist(),'versions':{n:importlib.metadata.version(n) for n in ('numpy','scipy','python_speech_features','rhasspy-wake-raven')}}
    save(args.output/'calibration.json',calibration)
    print('Calibration SEALED',threshold,'LOO accepted',calibration['loo_references_supported'],flush=True)
    # Only now may new positive waveforms/features be opened.
    report={'schema':1,'complete':False,'method':'independent-template-corroboration',
        'calibration_sha256':sha256(args.output/'calibration.json'),'rules':RULES,
        'events':[],'limitations':['No listening or human phonetic annotation.',
            'DTW can confuse acoustically similar words; calibration negatives are finite and known.',
            'RMS energy can miss low-energy final fricatives;80ms guards reduce but do not eliminate this risk.',
            'Single short session; augmentations and repeated pronunciations are dependent.']}
    positives=[r for r in inventory['events'] if r['audit']['source']=='new-99' and r['humanLabel']=='correct']
    if len(positives)!=92:
        raise ValueError('Expected exactly92 new human-labelled calls')
    for r in positives:
        pcm=read(Path(r['audit']['trigger_path']),r['triggerContext']['sha256'])[:r['audit']['decision_sample_in_context']]
        acoustic=acoustics(pcm);spans=acoustic['active_spans'];duration=len(pcm)/16000
        row={'id':r['id'],'sha256':r['triggerContext']['sha256'],'decision_sample':len(pcm),
             'predecision_seconds':duration,'ended_at_null':r['endedAt'] is None,
             'acoustics':acoustic,'eligible':False,'reasons':[],'boundary_type':'instrument-assisted template corroboration; no human phonetic annotation'}
        reasons=row['reasons']
        if len(spans)!=1:
            reasons.append('not-one-isolated-acoustic-component')
        else:
            span=spans[0];start,end=span['start'],span['end']
            if not RULES['positive_min_speech_seconds']<=end-start<=RULES['positive_max_speech_seconds']:
                reasons.append('implausible-or-multiword-component-duration')
            if start<RULES['capture_edge_margin_seconds'] or duration-end<RULES['capture_edge_margin_seconds']:
                reasons.append('possible-truncation-or-insufficient-capture-margin')
            cropstart=max(0,round((start-.08)*16000));cropend=min(len(pcm),round((end+.08)*16000))
            costs=distances(mfcc(pcm[cropstart:cropend]),references,scale)
            row.update(template_distances=costs,template_second_nearest=sorted(costs)[1],
                keyword_start_seconds=start,keyword_end_seconds=end,
                keyword_start_sample=round(start*16000),keyword_end_sample=round(end*16000),
                training_crop_start_sample=cropstart,training_crop_end_sample=cropend)
            if sorted(costs)[1]>threshold:
                reasons.append('insufficient-independent-template-corroboration')
        if acoustic['possible_cue_frames']:
            reasons.append('possible-activation-cue-in-predecision-context')
        row['eligible']=not reasons;report['events'].append(row)
    report.update(complete=True,elapsed_seconds=time.monotonic()-started,
        summary={'events':len(positives),'eligible':sum(r['eligible'] for r in report['events']),
                 'reasons':dict(Counter(x for r in report['events'] for x in r['reasons']))})
    save(args.output/'report.json',report)
    print(json.dumps(report['summary']),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--audit',required=True,type=private_output)
    parser.add_argument('--output',required=True,type=private_output)
    main(parser.parse_args())
