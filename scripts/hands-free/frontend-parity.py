#!/usr/bin/env python3
"""Compare production microfrontend features directly with accepted pymicro-features.

Uses the optional frontend probe built with AEGIS_BUILD_REPLAY. All inputs and
outputs stay private; only aggregate deltas are reported, never feature vectors.
"""
import argparse
import ctypes
import importlib.util
import json
import os
from pathlib import Path
import sys
import numpy as np
from pymicro_features import MicroFrontend

REPO=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(REPO/'scripts/wakeword-poc'))
from prepare import private_output
from run import load_data
spec=importlib.util.spec_from_file_location('parity',Path(__file__).with_name('replay-parity.py'))
p=importlib.util.module_from_spec(spec);spec.loader.exec_module(p)


def compare(lib,audio):
    reference=MicroFrontend()
    handle=lib.aegis_frontend_probe_create()
    if not handle: raise RuntimeError('Native frontend initialization failed')
    features=(ctypes.c_float*40)()
    frames=0;delta=0.;different=0;shape_errors=0
    try:
        for offset in range(0,len(audio),160):
            block=np.ascontiguousarray(audio[offset:offset+160],dtype=np.int16)
            if len(block)!=160: raise ValueError('Use complete blocks')
            r=reference.process_samples(block.tobytes())
            produced=lib.aegis_frontend_probe_process(handle,block.ctypes.data_as(ctypes.POINTER(ctypes.c_int16)),features)
            if r.samples_read!=160 or bool(r.features)!=bool(produced): shape_errors+=1;continue
            if produced:
                frames+=1
                expected=np.asarray(r.features,dtype=np.float32)
                if expected.shape!=(40,):shape_errors+=1;continue
                d=np.abs(expected-np.ctypeslib.as_array(features))
                delta=max(delta,float(d.max(initial=0)));different+=int(np.count_nonzero(d))
    finally:lib.aegis_frontend_probe_destroy(handle)
    return {'frames':frames,'max_feature_delta':delta,'different_feature_values':different,'shape_or_consumption_errors':shape_errors,'passed':delta==0 and shape_errors==0}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--library',required=True,type=Path)
    parser.add_argument('--data',type=Path,default=p.DEFAULT_CACHE/'data')
    parser.add_argument('--public-wav',type=Path,default=p.DEFAULT_CACHE/'comparison/public/negatives.wav')
    parser.add_argument('--output',type=private_output,required=True)
    args=parser.parse_args();os.umask(0o077)
    args.output.mkdir(parents=True,exist_ok=False,mode=0o700)
    lib=ctypes.CDLL(str(args.library.resolve()))
    lib.aegis_frontend_probe_create.restype=ctypes.c_void_p
    lib.aegis_frontend_probe_process.argtypes=[ctypes.c_void_p,ctypes.POINTER(ctypes.c_int16),ctypes.POINTER(ctypes.c_float)]
    lib.aegis_frontend_probe_process.restype=ctypes.c_int
    lib.aegis_frontend_probe_destroy.argtypes=[ctypes.c_void_p]
    data,inventory=load_data(args.data)
    cases=[]
    for row in inventory['records']:
        cases.append({'case':row['id'],**compare(lib,p.padded(p.read_wav(data/row['wav_path']),.5))})
    cases.append({'case':'public-negatives-35min',**compare(lib,p.padded(p.read_wav(args.public_wav)))})
    result={'schema':1,'host':'x86_64 Linux','cases':cases,'passed':all(r['passed'] for r in cases),'max_feature_delta':max(r['max_feature_delta'] for r in cases),'tolerance':0,'library_sha256':p.sha(args.library),'public_wav_sha256':p.sha(args.public_wav)}
    (args.output/'frontend.json').write_text(json.dumps(result,indent=2)+'\n')
    print(json.dumps({k:result[k] for k in ('passed','max_feature_delta','tolerance')},indent=2))
    return 0 if result['passed'] else 1


if __name__=='__main__':raise SystemExit(main())
