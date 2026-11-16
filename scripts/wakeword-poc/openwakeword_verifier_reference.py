#!/usr/bin/env python3
"""Bounded ONNX-only reference and private package/fixture export for Android.

The three models are reused unchanged. Private weights, audio and scores never
belong in Git or the APK. State contains deterministic noise embeddings, not a
recording. There is no continuous second-stage inference.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import sys
import time
import wave
import zipfile
import numpy as np
import onnxruntime as ort

FILES=('aegis.int8.tflite','melspectrogram.onnx','embedding_model.onnx','aegis.onnx','initial_features.f32le')

def digest(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def private(path):
    path=path.resolve()
    repo=Path(__file__).resolve().parents[2]
    if path==repo or repo in path.parents:raise ValueError('Private model/fixture output must remain outside Git')
    path.mkdir(parents=True,exist_ok=True,mode=0o700);path.chmod(0o700);return path

def write(path,value):
    path.write_bytes(value);path.chmod(0o600)

class OpenWakeWordVerifier:
    """A direct port of upstream80ms streaming preprocess with fixed warm state."""
    def __init__(self, directory):
        directory=Path(directory)
        options=ort.SessionOptions();options.intra_op_num_threads=1;options.inter_op_num_threads=1
        options.add_session_config_entry('session.intra_op.allow_spinning','0')
        options.add_session_config_entry('session.inter_op.allow_spinning','0')
        self.mel=ort.InferenceSession(str(directory/'melspectrogram.onnx'),options,providers=['CPUExecutionProvider'])
        self.embedding=ort.InferenceSession(str(directory/'embedding_model.onnx'),options,providers=['CPUExecutionProvider'])
        self.keyword=ort.InferenceSession(str(directory/'aegis.onnx'),options,providers=['CPUExecutionProvider'])
        self.initial=np.frombuffer((directory/'initial_features.f32le').read_bytes(),dtype='<f4').reshape(16,96).copy()
        if not np.isfinite(self.initial).all():raise ValueError('Nonfinite initial embeddings')
    def frame_scores(self, audio):
        if audio.dtype != np.int16 or audio.ndim !=1 or not 0<=len(audio)<=42560:raise ValueError('Invalid PCM window')
        mel_history=np.ones((76,32),dtype=np.float32);feature_history=self.initial.copy();scores=[]
        for start in range(0,len(audio)-1279,1280):
            end=start+1280
            raw=audio[max(0,end-1760):end].astype(np.float32)[None,:]
            spec=self.mel.run(None,{'input':raw})[0].squeeze().reshape(-1,32)/10+2
            mel_history=np.concatenate([mel_history,spec],axis=0)[-76:]
            embedding=self.embedding.run(None,{'input_1':mel_history[None,:,:,None].astype(np.float32)})[0].reshape(1,96)
            feature_history=np.concatenate([feature_history,embedding],axis=0)[-16:]
            value=float(self.keyword.run(None,{'features':feature_history[None,:,:].astype(np.float32)})[0].ravel()[0])
            scores.append(0. if len(scores)<5 else value)
        return np.asarray(scores,dtype=np.float32)
    def score(self,audio,candidate_sample):
        scores=self.frame_scores(audio);times=np.arange(1,len(scores)+1)*1280
        selected=scores[(times>=candidate_sample-7680)&(times<=candidate_sample+2560)]
        return float(selected.max()) if len(selected) else 0.

def package(args):
    if digest(args.strong)!='1e7c686420f65d8da892f02c71d945f03a8dd528a1005de8c9c8482dce52ad2e':
        raise ValueError('The frozen package requires the approved Strong primary model')
    output=private(args.output);model=private(output/'model')
    sys.path.insert(0,str(args.adapter_root))
    from compare_openwakeword import OpenWakeWordDetector
    detector=OpenWakeWordDetector('default');detector.reset()
    for name,path in [('aegis.int8.tflite',args.strong),('melspectrogram.onnx',detector.paths['melspectrogram']),
                      ('embedding_model.onnx',detector.paths['embedding']),('aegis.onnx',detector.paths['classifier'])]:
        write(model/name,Path(path).read_bytes())
    state=detector.model.preprocessor.feature_buffer[-16:].astype('<f4')
    if state.shape!=(16,96):raise ValueError('Unexpected upstream initialization')
    write(model/'initial_features.f32le',state.tobytes())
    manifest={'schema':1,'kind':'aegis-wake-verifier','version':'aegis-strong-oww-2026-11-16',
              'name':'Strong + verificador','threshold':.5,'primaryModelSha256':digest(args.strong),
              'window':{'preSamples':40000,'postSamples':2560,'blockSamples':1280,'scoreLookbackSamples':7680,'padding':False},
              'files':[{'name':name,'bytes':(model/name).stat().st_size,'sha256':digest(model/name)} for name in FILES]}
    write(model/'manifest.json',(json.dumps(manifest,indent=2)+'\n').encode())
    bundle=output/'aegis-strong-verifier-20261116.zip'
    with zipfile.ZipFile(bundle,'w',compression=zipfile.ZIP_DEFLATED) as archive:
        for name in (*FILES,'manifest.json'):archive.write(model/name,name)
    bundle.chmod(0o600)
    print(json.dumps({'bundle':str(bundle),'bytes':bundle.stat().st_size,'sha256':digest(bundle),'manifest':manifest}),flush=True)

def fixtures(args):
    output=private(args.output);sys.path.insert(0,str(args.adapter_root))
    from compare_openwakeword import OpenWakeWordDetector
    old=OpenWakeWordDetector('default');new=OpenWakeWordVerifier(args.models)
    evaluation=json.loads(args.evaluation.read_text())
    inventory={r['id']:r for r in json.loads(args.audit.read_text())['events']}
    source=evaluation['runtimes']['native_host']['contexts']['events']
    cases=[];maximum_difference=0.;started=time.monotonic()
    for row in source:
        if args.split!='all' and row['split']!=args.split:continue
        for event in row['events']:
            origin=inventory[row['id']];path=Path(origin['audit']['trigger_path'])
            if digest(path)!=origin['triggerContext']['sha256']:raise ValueError('PCM hash mismatch')
            with wave.open(str(path),'rb') as wav:
                if (wav.getnchannels(),wav.getsampwidth(),wav.getframerate())!=(1,2,16000):raise ValueError('PCM format')
                audio=np.frombuffer(wav.readframes(wav.getnframes()),dtype='<i2').copy()
            candidate=event['sample'];begin=max(0,candidate-40000);end=min(len(audio),candidate+2560)
            window=audio[begin:end];relative=candidate-begin;old.reset();expected=[]
            for offset in range(0,len(window)-1279,1280):
                old.process(window[offset:offset+1280]);expected.append(old.current_score)
            actual=new.frame_scores(window);expected=np.asarray(expected,dtype=np.float32)
            difference=float(np.max(np.abs(expected-actual))) if len(expected) else 0.
            maximum_difference=max(maximum_difference,difference)
            if difference>1e-5:raise ValueError('Reference differs from upstream: '+str(difference))
            name=f'case-{len(cases):03d}';write(output/(name+'.pcm'),window.astype('<i2').tobytes())
            write(output/(name+'.scores.f32le'),expected.astype('<f4').tobytes())
            write(output/(name+'.properties'),f'candidateSample={relative}\ncount={len(window)}\nlabel={row["humanLabel"]}\nsplit={row["split"]}\n'.encode())
            cases.append(name)
    # Include deterministic non-speech and extreme-length controls.
    for name,audio in [('control-silence',np.zeros(42560,dtype=np.int16)),('control-noise',np.random.RandomState(20261116).randint(-1000,1000,42560).astype(np.int16))]:
        old.reset();expected=[]
        for offset in range(0,len(audio)-1279,1280):old.process(audio[offset:offset+1280]);expected.append(old.current_score)
        actual=new.frame_scores(audio);difference=float(np.max(np.abs(np.asarray(expected)-actual)))
        maximum_difference=max(maximum_difference,difference)
        if difference>1e-5:raise ValueError('Control reference differs from upstream')
        write(output/(name+'.pcm'),audio.astype('<i2').tobytes());write(output/(name+'.scores.f32le'),np.asarray(expected,dtype='<f4').tobytes())
        write(output/(name+'.properties'),b'candidateSample=40000\ncount=42560\nlabel=control\nsplit=control\n');cases.append(name)
    write(output/'cases.txt',('\n'.join(cases)+'\n').encode())
    summary={'cases':len(cases),'maximumScoreDifference':maximum_difference,'pythonOnnxruntime':ort.__version__,
             'elapsedSeconds':time.monotonic()-started,'scope':'Exact upstream80ms streaming parity for private recorded and synthetic control windows; no Android physical proof'}
    write(output/'summary.json',(json.dumps(summary,indent=2)+'\n').encode());print(json.dumps(summary),flush=True)

def main():
    ap=argparse.ArgumentParser(description=__doc__);sub=ap.add_subparsers(dest='action',required=True)
    p=sub.add_parser('package');p.add_argument('--strong',type=Path,required=True);p.add_argument('--adapter-root',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    p=sub.add_parser('fixtures');p.add_argument('--models',type=Path,required=True);p.add_argument('--adapter-root',type=Path,required=True);p.add_argument('--evaluation',type=Path,required=True);p.add_argument('--audit',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--split',choices=['train','validation','test','all'],default='all')
    args=ap.parse_args();globals()[args.action](args)
if __name__=='__main__':main()
