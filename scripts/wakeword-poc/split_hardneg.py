#!/usr/bin/env python3
"""Freeze chronological session splits BEFORE model fitting or evaluation."""
import argparse
from collections import Counter
from datetime import datetime
import json
import os
from pathlib import Path
from prepare import private_output, sha256


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--audit',type=Path,required=True)
    p.add_argument('--output',type=private_output,required=True)
    a=p.parse_args();os.umask(0o077)
    if a.output.exists(): raise ValueError('Frozen protocol must be new')
    audit=json.loads(a.audit.read_text());rows=[]
    for e in audit['events']:
        d=datetime.fromisoformat(e['activatedAt'])
        if d.date().isoformat()=='2026-11-03' and 13<=d.hour<19: split='train'
        elif d.date().isoformat()=='2026-11-03' and 8<=d.hour<13: split='validation'
        else: split='test'
        role='negative-decision-windows' if e['humanLabel']=='false_positive' and e['audit']['training_eligible_context'] and split=='train' else 'evaluation-only'
        rows.append({'id':e['id'],'split':split,'session':e['audit']['proposed_session'],
                     'humanLabel':e['humanLabel'],'training_role':role,
                     'positive_policy':'never train new positives; no human phonetic alignment'})
    for key in ('session',):
        mapping={}
        for r in rows:mapping.setdefault(r[key],set()).add(r['split'])
        if any(len(v)>1 for v in mapping.values()): raise ValueError('Acoustic/time session spans split; revise protocol before fitting')
    groups={}
    for e,r in zip(audit['events'],rows):groups.setdefault(e['groupId'],set()).add(r['split'])
    if any(len(v)>1 for v in groups.values()):raise ValueError('groupId leak')
    result={'schema':1,'audit_sha256':sha256(a.audit),'events':rows,
        'split_rule':'whole periods, natural gaps: validation Nov03 08-13h; train Nov03 13-19h; test all other periods. Chronological+acoustic unions cannot cross.',
        'test_scope':'unseen sessions conditional on prior baseline activation; not prospective false activations/hour nor population recall',
        'seed':20261105,'experiments':{'A':{'epochs':8,'learning_rate':.0001,'initialization':'baseline weights; fresh Adam'},'B':{'epochs':20,'learning_rate':.001,'initialization':'random; same architecture'}},
        'hard_draws_per_epoch':1024,'hard_event_cap_rule':'2*ceil(hard_draws_per_epoch/eligible_training_events)',
        'max_old_validation_recall_drop':.02,
        'checkpoint_rule':'.75 original Piper+DEV validation BCE + .25 hard-negative validation BCE; original positive validation recall >=baseline-2pp; minimum eligible objective',
        'candidate_rule':'choose lower near-decision validation FP fraction among A/B with new validation correct re-detection no lower than baseline; tie prefer A; final test and legacy regression only after freeze; if neither passes export best objective with NO-GO',
        'events_rule':{'threshold':.5,'averageWindow':5,'releaseBelow':.3,'refractorySamples':16000},
        'label_scope':'only decision-near receptive windows ending -60/-30/0ms before decision; no whole-clip labels, no post used',
        'positive_policy':'all15 new positives assessment-only; original positive corpus primary; ASR advisory does not replace human alignment',
        'augmentation':'training gain .85/1/1.15 only; same frontend; no normalization/resampling; 1024 hard draws session-uniform then event-uniform among sources with capacity; each event capped at twice uniform expectation; preserve original8869 windows',
        'protocol_revision_note':'v2 frozen before any fitting; caps singleton-session repetition identified from audit only; v1 baseline validation counts already observed; test unopened',
        'test_policy':'no test audio/model inference during checkpoint/candidate selection; similarity/integrity audit permitted; all augmentations inherit split',
        'limits':'single phone, owner, three days, only15 selected positives; sessions heuristic, environment may persist across gaps; original holdouts architecture-selection-exposed'}
    a.output.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
    with a.output.open('x') as f:f.write(json.dumps(result,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({'sha256':sha256(a.output),'counts':{s:dict(Counter(r['humanLabel'] for r in rows if r['split']==s)) for s in ('train','validation','test')}},indent=2))
if __name__=='__main__': main()
