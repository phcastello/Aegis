#!/usr/bin/env python3
"""Post-seal final comparison and state controls; never selects or adjusts models."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys

from prepare import private_output, sha256
from tradeoff_eval import CACHE, PREVIOUS, NativeThreshold, LiteThreshold
from positive_controls import controls, continuous, verify_gate


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--root',type=private_output,default=CACHE)
    a=p.parse_args();os.umask(0o077)
    selection_path=a.root/'selection-v1.json'
    selection=json.loads(selection_path.read_text())
    prep=PREVIOUS/'preparation-v1'
    audit,protocol,roles=verify_gate(selection_path,prep/'protocol.json',prep/'audit/inventory.json')
    models=selection['models']
    for name,model in models.items():
        if not name or any(c not in 'abcdefghijklmnopqrstuvwxyz0123456789_-' for c in name):
            raise ValueError('Unsafe candidate name')
        if sha256(Path(model['model_path']))!=model['model_sha256']:
            raise ValueError('Frozen model differs')
    launch={'created_at':datetime.now(timezone.utc).isoformat(),'selection_sha256':sha256(selection_path),
            'models':models,'scope':'All tests/regressions already consulted historically. No subsequent adjustment authorized in this round.',
            'script_sha256':sha256(Path(__file__))}
    with (a.root/'final-replay-launch-v1.json').open('x') as f:
        json.dump(launch,f,indent=2);f.write('\n')
    library=a.root/'native-threshold-v1/libaegis_mww_threshold.so'
    def evaluate(item):
        name,model=item
        command=[sys.executable,str(Path(__file__).with_name('tradeoff_eval.py')),'evaluate',
                 '--model',model['model_path'],'--threshold',str(model['threshold']),
                 '--library',str(library),'--split','all','--legacy',
                 '--output',str(a.root/f'eval-{name}-final')]
        with (a.root/f'eval-{name}-final.log').open('x') as log:
            subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,check=True)
        print(json.dumps({'final_evaluation_complete':name}),flush=True)
    with ThreadPoolExecutor(max_workers=2) as pool:
        list(pool.map(evaluate,models.items()))
    output=a.root/'controls-v1';output.mkdir(mode=0o700,exist_ok=False)
    report={'schema':1,'created_at':datetime.now(timezone.utc).isoformat(),
            'selection_sha256':sha256(selection_path),'protocol_sha256':sha256(prep/'protocol.json'),
            'audit_sha256':sha256(prep/'audit/inventory.json'),'library_sha256':sha256(library),
            'physical_arm64':'pending','models':{},'script_sha256':sha256(Path(__file__))}
    for name,model in models.items():
        path=Path(model['model_path']);threshold=model['threshold']
        result={'model_sha256':sha256(path),'threshold':threshold,'runtimes':{}}
        report['models'][name]=result
        for rt,factory in (('litert',lambda:LiteThreshold(path,threshold)),
                           ('native_host',lambda:NativeThreshold(library,path,threshold))):
            dest=output/name/rt;dest.mkdir(parents=True,mode=0o700)
            (dest/'controls').mkdir(mode=0o700);(dest/'continuous').mkdir(mode=0o700)
            result['runtimes'][rt]={'controls':controls(factory,dest/'controls',30),
                                  'continuous':continuous(factory,audit['events'],roles,dest/'continuous')}
        (output/'controls.json').write_text(json.dumps(report,indent=2)+'\n')
        print(json.dumps({'state_controls_complete':name}),flush=True)


if __name__=='__main__':main()
