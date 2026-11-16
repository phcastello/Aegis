#!/usr/bin/env python3
"""Apply the frozen verifier to previously recorded Strong legacy events.

No additional detector selection, model change or private audio upload occurs.
Original legacy padding is preserved and reported; no new padding is added by
verification. Regression replays are previously known, not unseen holdouts.
"""
import argparse
import json
import os
from pathlib import Path
import time
import wave
import numpy as np
from openwakeword_verifier_reference import OpenWakeWordVerifier,digest,private,write

def audio(path):
    with wave.open(str(path),'rb') as f:
        if (f.getnchannels(),f.getsampwidth(),f.getframerate())!=(1,2,16000):raise ValueError('Audio format changed')
        return np.frombuffer(f.readframes(f.getnframes()),dtype='<i2').copy()

def summary(rows):
    return {label:{'files':sum(r['label']==label for r in rows),
                   'activatedFiles':sum(r['label']==label and bool(r['accepted']) for r in rows),
                   'events':sum(len(r['accepted']) for r in rows if r['label']==label)} for label in ['positive','negative']}

def main():
    p=argparse.ArgumentParser(description=__doc__)
    for name in ['models','evaluation','data','public-wav','output']:p.add_argument('--'+name,type=Path,required=True)
    a=p.parse_args();private(a.output)
    detector=OpenWakeWordVerifier(a.models);source=json.loads(a.evaluation.read_text())
    legacy=source['runtimes']['native_host']['legacy'];inventory=json.loads((a.data/'inventory.json').read_text())
    if digest(a.data/'inventory.json')!=legacy['inventory_sha256'] or digest(a.public_wav)!=legacy['public_wav_sha256']:raise ValueError('Legacy source identity changed')
    mapping={r['id']:r for r in inventory['records']}
    started=time.monotonic();public=audio(a.public_wav)
    def verify(stream,events):
        accepted=[]
        for event in events:
            sample=event['sample'];begin=max(0,sample-40000);end=min(len(stream),sample+2560)
            score=detector.score(stream[begin:end],sample-begin)
            if score>=.5:accepted.append(sample)
        return accepted
    independent=[]
    for r in legacy['independent_events']:
        record=mapping[r['id']];path=a.data/record['wav_path']
        if digest(path)!=record['wav_sha256']:raise ValueError('Private file changed')
        raw=np.concatenate([audio(path),np.zeros(8000,dtype=np.int16)])
        raw=np.pad(raw,(0,(-len(raw))%160))
        independent.append(dict(id=r['id'],label=r['label'],accepted=verify(raw,r['events'])))
    pieces=[public[:480000]]
    for r in legacy['continuous_events']:
        raw=np.concatenate([audio(a.data/mapping[r['id']]['wav_path']),np.zeros(48000,dtype=np.int16)])
        pieces.append(np.pad(raw,(0,(-len(raw))%160)))
    stream=np.concatenate(pieces)
    continuous=[dict(id=r['id'],label=r['label'],accepted=verify(stream,r['events'])) for r in legacy['continuous_events']]
    accepted_public=verify(public,legacy['public_events'])
    report={'sourceEvaluationSha256':digest(a.evaluation),'threshold':.5,'selection':'Frozen before legacy regression; no retuning',
            'original':{'independent':legacy['quality'],'continuous':legacy['continuous'],'public':legacy['public']},
            'cascade':{'independent':summary(independent),'continuous':summary(continuous),
                       'public':{'seconds':len(public)/16000,'events':len(accepted_public)}},
            'privateResults':{'independent':independent,'continuous':continuous,'public':accepted_public},
            'elapsedSeconds':time.monotonic()-started,'scope':'Known regression sets. The original independent0.5s and continuous3s silence tails are retained, not added by the verifier.'}
    write(a.output/'results.private.json',(json.dumps(report,indent=2)+'\n').encode())
    print(json.dumps({k:v for k,v in report.items() if k!='privateResults'}),flush=True)
if __name__=='__main__':main()
