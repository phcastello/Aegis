#!/usr/bin/env python3
"""Seal bounded experiment selection before final replay; validation only."""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
from prepare import private_output, sha256

def main():
    p=argparse.ArgumentParser();p.add_argument('--root',type=private_output,required=True);a=p.parse_args();os.umask(0o077)
    protocol=a.root/'protocol-v1.json';candidates=[]
    for s in ('A','B'):
        path=a.root/f'model-{s}-v2/training.json';j=json.loads(path.read_text())
        assert j['protocol_sha256']==sha256(protocol)
        row=j['history'][j['best_epoch']-1]
        assert j['best_key']==row['selection_key']
        candidates.append({'strategy':s,'epoch':j['best_epoch'],'key':row['selection_key'],'eligible':row['eligible'],
                           'model_sha256':j['model_sha256'],'training_sha256':sha256(path)})
    chosen=min(candidates,key=lambda c:(c['key'],c['strategy']))
    result={'created_at':datetime.now(timezone.utc).isoformat(),'protocol_sha256':sha256(protocol),
            'selected_strategy':chosen['strategy'],'selected_epoch':chosen['epoch'],'eligible':chosen['eligible'],
            'decision':'VALIDATION-PASS' if chosen['eligible'] else 'NO-GO','candidates':candidates,
            'note':'Validation result only; final gates pending. Selected weights never change after opening final data.',
            'script_sha256':sha256(Path(__file__))}
    with (a.root/'selection-v1.json').open('x') as f:json.dump(result,f,indent=2);f.write('\n')
    print(json.dumps(result,indent=2))
if __name__=='__main__':main()
