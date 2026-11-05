#!/usr/bin/env python3
"""Canonical first-model export in a fresh process; no training or evaluation."""
import argparse
import json
import os
from pathlib import Path
import sys
import time
os.environ.setdefault('CUDA_VISIBLE_DEVICES','-1')
os.environ.setdefault('TF_CPP_MIN_LOG_LEVEL','2')
os.environ.setdefault('TF_NUM_INTRAOP_THREADS','2');os.environ.setdefault('TF_NUM_INTEROP_THREADS','1')
import numpy as np
from train_hardneg import baseline_guards, BASE, ROOT, save_json
from train_microwakeword import FEATURES, STRIDE
from prepare import private_output,sha256

def main():
    p=argparse.ArgumentParser();p.add_argument('--checkpoint',type=private_output,required=True);p.add_argument('--features',type=private_output,required=True);p.add_argument('--output',type=private_output,required=True);a=p.parse_args();os.umask(0o077)
    baseline_guards();assert not (a.output/'aegis.int8.tflite').exists()
    a.output.mkdir(parents=True,exist_ok=True,mode=0o700);start=time.perf_counter()
    sys.path.insert(0,str(ROOT/'comparison/microwakeword/source'))
    import tensorflow as tf
    from microwakeword import mixednet,utils
    from microwakeword.layers import modes
    tf.keras.backend.clear_session()
    flags=argparse.Namespace(**json.loads((BASE/'training.json').read_text())['architecture'])
    model=mixednet.model(flags,(FEATURES,40),None);model.load_weights(a.checkpoint)
    cfg={'spectrogram_length':FEATURES,'stride':STRIDE,'train_dir':str(a.output)}
    utils.convert_model_saved(model,cfg,'streaming_saved',modes.Modes.STREAM_INTERNAL_STATE_INFERENCE)
    x=np.load(a.features/'train_x.npy',mmap_mode='r');y=np.load(a.features/'train_y.npy',mmap_mode='r')
    class Calibration:
        def get_data(self,*unused,**kwargs):return x[:500].copy(),y[:500].copy(),None
    utils.convert_saved_model_to_tflite(cfg,Calibration(),str(a.output/'streaming_saved'),str(a.output),'aegis.int8.tflite',quantize=True)
    from ai_edge_litert.interpreter import Interpreter
    it=Interpreter(model_path=str(a.output/'aegis.int8.tflite'),num_threads=1);it.allocate_tensors();ins=it.get_input_details()[0];outs=it.get_output_details()[0]
    assert ins['shape'].tolist()==[1,3,40] and outs['shape'].tolist()==[1,1]
    assert ins['dtype']==np.int8 and outs['dtype']==np.uint8
    assert ins['quantization']==(.10196078568696976,-128) and outs['quantization']==(1/256,0)
    assert not any('float' in str(t['dtype']) for t in it.get_tensor_details())
    save_json(a.output/'export.json',{'checkpoint_sha256':sha256(a.checkpoint),'features_manifest_sha256':sha256(a.features/'features.json'),'model_sha256':sha256(a.output/'aegis.int8.tflite'),'model_bytes':(a.output/'aegis.int8.tflite').stat().st_size,'params':model.count_params(),'input_shape':ins['shape'].tolist(),'input_quantization':ins['quantization'],'output_shape':outs['shape'].tolist(),'output_quantization':outs['quantization'],'export_script_sha256':sha256(Path(__file__)),'elapsed_seconds':time.perf_counter()-start})
    print(json.dumps({'bytes':(a.output/'aegis.int8.tflite').stat().st_size,'sha256':sha256(a.output/'aegis.int8.tflite')}),flush=True)
if __name__=='__main__':main()
