#!/usr/bin/env python3
"""Prepare the accepted private continuous replay sequence for the ARM64 CLI.

No new recordings. Prefix is 30 seconds of existing public audio, followed by
31 existing reserved/negative trials with 3 seconds of silence and per-trial
160-sample padding, exactly as check-continuous.py. Native CLI adds its normal
0.5-second final flush after this file; that extra silence is explicit.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import wave

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT/'scripts/wakeword-poc'))
from prepare import private_output
from run import load_data
spec=importlib.util.spec_from_file_location('parity',Path(__file__).with_name('replay-parity.py'))
p=importlib.util.module_from_spec(spec);spec.loader.exec_module(p)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--data',type=Path,default=p.DEFAULT_CACHE/'data')
    parser.add_argument('--public-wav',type=Path,default=p.DEFAULT_CACHE/'comparison/public/negatives.wav')
    parser.add_argument('--output',type=private_output,required=True)
    args=parser.parse_args();os.umask(0o077)
    data,inventory=load_data(args.data)
    refs={r['id'] for r in json.loads((ROOT/'scripts/wakeword-poc/selection.json').read_text())['references']}
    records=[r for r in inventory['records'] if r['id'] not in refs]
    if len(records)!=31:raise ValueError('Expected 31 reserved/negative trials')
    args.output.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
    with args.output.open('xb') as raw:
        with wave.open(raw,'wb') as out:
            out.setparams((1,2,16000,0,'NONE','not compressed'))
            out.writeframesraw(p.padded(p.read_wav(args.public_wav,30)).tobytes())
            for row in records:out.writeframesraw(p.padded(p.read_wav(data/row['wav_path']),3).tobytes())
    print('Private continuous replay prepared; 31 trials; SHA-256 '+hashlib.sha256(args.output.read_bytes()).hexdigest())


if __name__=='__main__':main()
