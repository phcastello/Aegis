#!/usr/bin/env python3
"""Freeze temporal blocks and bounded sensitivity-first experiments; no inference."""
import argparse
from collections import Counter, defaultdict
from datetime import datetime, timezone
import json
import os
from pathlib import Path
from prepare import private_output, sha256

def save(path, data):
    with path.open('x') as f: json.dump(data, f, indent=2, ensure_ascii=False); f.write('\n')

def main():
    p=argparse.ArgumentParser();p.add_argument('--audit',type=private_output,required=True)
    p.add_argument('--output',type=private_output,required=True);a=p.parse_args();os.umask(0o077)
    oldpath=Path.home()/'.cache/aegis-wakeword-hardneg/protocol-v3.json'
    old={r['id']:r for r in json.loads(oldpath.read_text())['events']}
    audit=json.loads(a.audit.read_text());rows=[]
    for e in sorted(audit['events'],key=lambda e:e['activatedAt']):
        if e['id'] in old:
            r=dict(old[e['id']]);r.update(origin='old161',inclusion='frozen-old-role; all15 correct remain evaluation-only')
        else:
            t=datetime.fromisoformat(e['activatedAt'])
            assert t.date().isoformat()=='2026-11-05'
            h=t.hour*3600+t.minute*60+t.second+t.microsecond/1e6
            if h<12*3600+52*60+45: s='train'
            elif h<12*3600+53*60+15:s='buffer'
            elif h<12*3600+54*60+45:s='validation'
            elif h<12*3600+55*60+15:s='buffer'
            else:s='test'
            r={'id':e['id'],'split':s,'session':'new-short-session-block-'+s,
               'origin':'new99','humanLabel':e['humanLabel'],
               'inclusion':'guard-gap-excluded' if s=='buffer' else 'pending-fixed-acoustic-quality-screen',
               'training_role':'new-real-positive-only-if-confirmed' if s=='train' and e['humanLabel']=='correct' else 'evaluation-only'}
        r['trigger_sha256']=e['triggerContext']['sha256'];r['post_sha256']=e['postActivation']['sha256']
        rows.append(r)
    groups=defaultdict(set)
    for e in audit['events']:groups[e['groupId']].add(next(r['split'] for r in rows if r['id']==e['id']))
    assert all(len(s)==1 for s in groups.values()),'groupId leakage'
    assert len(old)==161 and len(rows)==260 and len({r['id'] for r in rows})==260
    spec={'schema':1,'created_at':datetime.now(timezone.utc).isoformat(),'audit_sha256':sha256(a.audit),
          'old_protocol_sha256':sha256(oldpath),'events':rows,'seed':20261105,
          'split_rule':'Old161 exact frozen roles. New99 chronological: train before12:52:45; gap30s; validation12:53:15-12:54:45; gap30s; test from12:55:15 local UTC-03. Whole short-session independence remains LIMITED.',
          'grouping_limit':'new temporal blocks are not independent acoustic sessions; gaps reduce immediate adjacency only; no claim of session-level independence',
          'experiments':{'A':{'epochs':4,'learning_rate':.00001,'initialization':'baseline'},'B':{'epochs':4,'learning_rate':.00003,'initialization':'previous-A'}},
          'batch_normalization':'Freeze BN affine parameters and moving statistics at initializer before compile; preserves distributions despite balanced batches',
          'steps_per_epoch':32,'batch_sources':{'synthetic_positive':20,'reference_positive':4,'real_positive':8,'synthetic_negative':16,'public_dev_negative':8,'hard_negative':8},
          'real_and_hard_draw_cap':'2*ceil(draws_per_epoch/unique_eligible_events); balance available time-block then event; all variants same source',
          'positive_policy':'Only new TRAIN correct with corroborated acoustic screen. Old15 correct retain evaluation-only. No post-decision samples/features; no post files. Validation/test never fit/calibrate.',
          'augmentation':'real gain .85/1/1.15; end word+60/120ms clipped to decision. No masks, speed, noise, concatenation for real. Original features retain original augmentation; original examples resampled without class domination.',
          'checkpoint_rule':'Each epoch exported INT8 and validation streaming replay. Lexicographic: paired real validation losses baseline->candidate; original validation recall deficit beyond2pp; hard validation near-decision FP count; original synthetic paired losses; original BCE. Zero paired real losses and <=2pp original recall drop required for eligibility.',
          'selection_rule':'Choose eligible checkpoint across two predefined fits with smallest checkpoint tuple; tie A then earlier epoch. If none eligible best research artifact NO-GO. Seal before final replay. No further fits/threshold changes after test.',
          'final_go_rule':'Preserve every old15 and old23 baseline-reproduced positive and all new reserved baseline-reproduced positives in both runtimes; >=50% reduction old64 near-decision FP in both runtimes; no major native/latency/resource regression. Host evidence is not physical ARM64 approval.',
          'rules':{'threshold':.5,'average_window':5,'release_below':.3,'refractory_samples':16000},
          'calibration':'exact previous x[:500] training only; unchanged frontend input quantization required',
          'test_policy':'No detector replay on new test or old regression before selection. Integrity/acoustic quality audit permitted, no detector-score-driven inclusion.'}
    a.output.parent.mkdir(parents=True,exist_ok=True,mode=0o700);save(a.output,spec)
    print(json.dumps({'sha256':sha256(a.output),'counts':{o:{s:dict(Counter(r['humanLabel'] for r in rows if r['origin']==o and r['split']==s)) for s in ('train','validation','test','buffer')} for o in ('old161','new99')}}))
if __name__=='__main__':main()
