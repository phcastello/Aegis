#!/usr/bin/env python3
"""Compare bounded second-stage wake-word verifiers on preserved private replays.

This does not train, import or activate models. Selection uses validation only;
previously consulted tests remain exploratory rather than a fresh holdout.
Personal event IDs, paths and scores are written only to --output outside Git.
"""
import argparse
from collections import deque
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import sys
import time
import wave

STRONG_SHA256 = '1e7c686420f65d8da892f02c71d945f03a8dd528a1005de8c9c8482dce52ad2e'

PLAN = {
    'schema': 1,
    'profiles': ['unchanged', 'confirm_3_strides', 'confirm_5_strides',
                 'openwakeword_0.5', 'openwakeword_0.7'],
    'temporal_rule': 'Five-score mean >= 0.5 for one, three or five consecutive inference strides; release <0.3, refractory 1s.',
    'score_clock': 'First inference at50ms then30ms; verify unchanged events exactly against stored native replay before comparison.',
    'openwakeword_reference': 'Preserved synthetic-trained ONNX; reset before each candidate; feed at most2.5s preceding candidate plus160ms available lookahead, no EOF padding.',
    'openwakeword_acceptance': 'Score >= fixed threshold within480ms before to160ms after candidate; verification only runs on strong candidates.',
    'runtime': 'CPU only, one ONNX thread, no cloud/network/API; detector remains unchanged.',
    'selection': 'Validation first. No selection or retuning on test; all existing splits are previously consulted and exploratory.',
    'labels': 'Human labels only; empty/cancelled/STT outcomes are never treated as false-positive truth.',
}

def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def temporal_events(raw_scores, confirmations=1):
    """The stored native raw score holds its value between30ms inferences."""
    average = deque(maxlen=5)
    latched, last_event, streak = False, -16000, 0
    events = []
    for index in range(4, len(raw_scores), 3):
        score = float(raw_scores[index])
        if not 0 <= score <= 1:
            raise ValueError('Invalid raw score')
        average.append(score)
        if len(average) < 5:
            continue
        mean = sum(average) / 5
        sample = (index + 1) * 160
        if mean < .3:
            latched = False
        streak = streak + 1 if mean >= .5 else 0
        if streak >= confirmations and not latched and sample - last_event >= 16000:
            events.append(sample)
            latched, last_event = True, sample
    return events

def read_audio(path):
    import numpy as np
    with wave.open(str(path), 'rb') as wav:
        if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) != (1,2,16000):
            raise ValueError('Expected mono PCM16 at16kHz')
        return np.frombuffer(wav.readframes(wav.getnframes()), dtype='<i2').copy()

def aggregate(rows):
    out = {}
    for label in ['correct', 'false_positive']:
        subset = [r for r in rows if r['label'] == label]
        out[label] = {'contexts':len(subset), 'detected_any':sum(bool(r['events']) for r in subset),
                      'detected_near':sum(bool(r['near']) for r in subset),
                      'event_count':sum(len(r['events']) for r in subset)}
    return out

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    ap.add_argument('--evaluation',type=Path,required=True)
    ap.add_argument('--audit',type=Path,required=True)
    ap.add_argument('--adapter-root',type=Path,required=True)
    ap.add_argument('--output',type=Path,required=True)
    ap.add_argument('--split',choices=['train','validation','test','all'],default='validation')
    ap.add_argument('--without-openwakeword',action='store_true')
    args=ap.parse_args()
    output=args.output.resolve()
    repo=Path(__file__).resolve().parents[2]
    if output == repo or repo in output.parents:
        raise ValueError('Personal evaluation output must remain outside Git')
    output.mkdir(parents=True,exist_ok=False,mode=0o700)
    plan=dict(PLAN,started_at=datetime.now(timezone.utc).isoformat(),split=args.split,
              evaluation_sha256=sha256(args.evaluation),audit_sha256=sha256(args.audit),
              evaluator_sha256=sha256(__file__))
    (output/'plan.json').write_text(json.dumps(plan,indent=2)+'\n')
    os.chmod(output/'plan.json',0o600)
    import numpy as np
    source=json.loads(args.evaluation.read_text())
    if source['model_sha256'] != STRONG_SHA256 or source['model_bytes'] != 39552:
        raise ValueError('Expected the preserved Strong replay, not another primary model')
    if sha256(Path(source['model_path'])) != STRONG_SHA256:
        raise ValueError('Preserved Strong model identity changed')
    plan['strong_model_sha256']=STRONG_SHA256
    if source['audit_sha256'] != sha256(args.audit):
        raise ValueError('Evaluation/audit identity mismatch')
    records=source['runtimes']['native_host']['contexts']['events']
    audited={r['id']:r for r in json.loads(args.audit.read_text())['events']}
    rows=[r for r in records if args.split=='all' or r['split']==args.split]
    profiles={p:[] for p in PLAN['profiles'] if not(args.without_openwakeword and p.startswith('openwakeword'))}
    detector=None
    if not args.without_openwakeword:
        sys.path.insert(0,str(args.adapter_root))
        from compare_openwakeword import OpenWakeWordDetector
        detector=OpenWakeWordDetector('default')
        plan['openwakeword_models']=detector.metadata()['models']
    started=time.monotonic();verification_seconds=0.;candidate_count=0
    for row in rows:
        if row['humanLabel'] not in ['correct','false_positive']:
            raise ValueError('Non-human label found')
        original=audited[row['id']]
        if original['humanLabel'] != row['humanLabel']:
            raise ValueError('Human label mismatch')
        raw=np.load(args.evaluation.parent/'scores-native_host'/(row['id']+'.npy'),allow_pickle=False)
        original_events=[e['sample'] for e in row['events']]
        unchanged=temporal_events(raw)
        if unchanged != original_events:
            raise ValueError('Score clock/rule does not exactly reproduce native events')
        variants={'unchanged':unchanged,'confirm_3_strides':temporal_events(raw,3),
                  'confirm_5_strides':temporal_events(raw,5)}
        if detector:
            audio_path=Path(original['audit']['trigger_path'])
            if sha256(audio_path) != original['triggerContext']['sha256']:
                raise ValueError('Audio integrity mismatch')
            audio=read_audio(audio_path)
            oww={.5:[],.7:[]}
            for event in original_events:
                candidate_count+=1
                begin=max(0,event-40000);end=min(len(audio),event+2560)
                detector.reset();window=audio[begin:end];maximum=0.
                start=time.monotonic()
                for offset in range(0,len(window)-1279,1280):
                    detector.process(window[offset:offset+1280])
                    at=begin+offset+1280
                    if event-7680<=at<=event+2560:
                        maximum=max(maximum,detector.current_score)
                verification_seconds+=time.monotonic()-start
                for threshold in oww:
                    if maximum>=threshold:oww[threshold].append(event)
            variants['openwakeword_0.5']=oww[.5];variants['openwakeword_0.7']=oww[.7]
        for profile,events in variants.items():
            decision=row['decision_sample_in_context']
            near=[v for v in events if decision-8000<=v<=min(row['samples'],decision+3200)]
            profiles[profile].append({'id':row['id'],'split':row['split'],'label':row['humanLabel'],
                                      'events':events,'near':near})
    summary={name:aggregate(items) for name,items in profiles.items()}
    result={'plan':plan,'summary':summary,'elapsed_seconds':time.monotonic()-started,
            'openwakeword_candidates':candidate_count,'verification_seconds':verification_seconds,
            'verification_mean_ms':1000*verification_seconds/candidate_count if candidate_count else None,
            'results':profiles}
    (output/'results.private.json').write_text(json.dumps(result,indent=2)+'\n')
    os.chmod(output/'results.private.json',0o600)
    print(json.dumps({k:v for k,v in result.items() if k not in ['plan','results']}),flush=True)

if __name__=='__main__':main()
