#!/usr/bin/env python3
"""Create a private, explicitly approved-for-pilot package; never deploy anything.

Only two INT8 flatbuffers and safe reports are packaged. The current Android app
intentionally rejects every candidate hash; separate private homologation tooling
is required and is not installed or modified by this script.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import zipfile

import numpy as np

from prepare import private_output, sha256
from train_hardneg import BASE_SHA

CACHE = Path.home()/'.cache/aegis-wakeword-tradeoff'


def read(path):
    return json.loads(path.read_text())


def write(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False)+'\n')


def reject_private_fields(value):
    """Fail closed if a private evaluation accidentally replaces the safe summary."""
    blocked = {'id', 'session', 'groupId', 'sessionId', 'activatedAt', 'humanLabel',
               'trigger_path', 'post_path', 'transcript', 'transcription', 'audio',
               'real_event_draws', 'alias_to_id', 'paired_event_ids'}
    if isinstance(value, dict):
        if blocked.intersection(value):
            raise ValueError('Private event fields are forbidden in the pilot metrics')
        for item in value.values():
            reject_private_fields(item)
    elif isinstance(value, list):
        for item in value:
            reject_private_fields(item)
    elif isinstance(value, str) and re.search(r'(?i)\b[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}\b', value):
        raise ValueError('Raw event UUID is forbidden in the pilot metrics')


def tensor_contract(path):
    from ai_edge_litert.interpreter import Interpreter
    interpreter = Interpreter(model_path=str(path), num_threads=1)
    interpreter.allocate_tensors()
    inputs, outputs = interpreter.get_input_details(), interpreter.get_output_details()
    if len(inputs)!=1 or len(outputs)!=1:
        raise ValueError('Expected one input and one output')
    ins, outs = inputs[0], outputs[0]
    if (path.stat().st_size != 39552 or ins['shape'].tolist()!=[1,3,40]
            or outs['shape'].tolist()!=[1,1] or ins['dtype']!=np.int8
            or outs['dtype']!=np.uint8 or ins['quantization']!=(.10196078568696976,-128)
            or outs['quantization']!=(1/256,0)
            or any(np.issubdtype(t['dtype'],np.floating) for t in interpreter.get_tensor_details())):
        raise ValueError('Frozen INT8 native tensor contract differs')
    return {'model_bytes':39552, 'input_dtype':'int8', 'input_shape':[1,3,40],
            'input_scale':ins['quantization'][0], 'input_zero_point':ins['quantization'][1],
            'output_dtype':'uint8', 'output_shape':[1,1], 'output_scale':1/256,
            'output_zero_point':0, 'float_tensors':False,
            'architecture':'official streaming MixConv', 'parameters':13057,
            'pcm':'PCM16LE mono 16000 Hz', 'block_samples':160,
            'inference_stride_samples':480, 'streaming_state':True}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root',type=private_output,default=CACHE)
    parser.add_argument('--summary',type=private_output)
    parser.add_argument('--output',type=private_output)
    args=parser.parse_args();os.umask(0o077)
    summary_path=args.summary or args.root/'summary-v1/public_summary.json'
    output=private_output(args.output or args.root/'piloto-v1')
    archive=output.with_suffix('.zip')
    report_path=output.parent/(output.name+'-package.json')
    if any(p.exists() for p in (output,archive,report_path)):
        raise ValueError('Package destinations must be new')
    selection_path=args.root/'selection-v1.json'
    selection,summary=read(selection_path),read(summary_path)
    if summary['decision']!='GO PARA PILOTO' or summary['production_approved'] is not False:
        raise ValueError('Final review has not approved this candidate for a private pilot')
    if (summary['provenance']['selection_sha256']!=sha256(selection_path)
            or summary['selected_model']!=selection['selected_model']):
        raise ValueError('Final metrics and frozen selection differ')
    reject_private_fields(summary)
    selected=selection['selected_model']
    entries={'aegis.int8.tflite':selection['models'][selected],
             'rollback-baseline.int8.tflite':selection['models']['baseline']}
    threshold=float(entries['aegis.int8.tflite']['threshold'])
    if not math.isfinite(threshold) or not 0<threshold<1:
        raise ValueError('Invalid frozen threshold')
    contracts={}
    for filename,model in entries.items():
        path=private_output(model['model_path'])
        if sha256(path)!=model['model_sha256']:
            raise ValueError('Frozen model changed')
        name=selected if filename=='aegis.int8.tflite' else 'baseline'
        if summary['models'][name]['model_sha256']!=model['model_sha256']:
            raise ValueError('Metrics do not describe packaged model')
        contracts[filename]=tensor_contract(path)
    if entries['rollback-baseline.int8.tflite']['model_sha256']!=BASE_SHA:
        raise ValueError('Rollback must be the immutable original baseline')
    if entries['rollback-baseline.int8.tflite']['threshold']!=.5:
        raise ValueError('Baseline rollback threshold changed')
    output.mkdir(parents=True,mode=0o700)
    for filename,model in entries.items():
        shutil.copyfile(model['model_path'],output/filename)
        if sha256(output/filename)!=model['model_sha256']:
            raise ValueError('Packaged copy differs')
    shutil.copyfile(summary_path,output/'metrics.json')
    manifest={'schema':1,'created_at':datetime.now(timezone.utc).isoformat(),
        'decision':'GO PARA PILOTO','production_approved':False,'selected_model':selected,
        'pilot_scope':'Restricted comparative and reversible homologation; experience gain remains unproven. Final test was unfavorable; inspect metrics before use.',
        'selection_sha256':sha256(selection_path),'metrics_sha256':sha256(output/'metrics.json'),
        'models':{filename:{'sha256':model['model_sha256'], 'threshold':model['threshold'],
            'contract':contracts[filename]} for filename,model in entries.items()},
        'detector':{'threshold':threshold,'average_window':5,'release_below':.3,
            'refractory_samples':16000,'near_decision_window_ms':[-500,200],
            'reset':'clear frontend, NN variables, score window, latch, refractory and pending PCM'},
        'android':{'current_app_accepts_candidate':False,'current_app_allowed_sha256':BASE_SHA,
            'current_threshold_configurable':False,'physical_arm64_homologation':'PENDING',
            'required':'Separate private homologation harness/build configured for these exact bytes and threshold. No production app or allowlist changes made.'},
        'privacy':'Private local package; no audio, features, conversations, event IDs, float weights or checkpoints.',
        'package_script_sha256':sha256(Path(__file__))}
    write(output/'manifest.json',manifest)
    (output/'INSTRUCOES.txt').write_text(f'''Aegis v0.8.0 — pacote privado de piloto

Decisão: GO PARA PILOTO restrito, comparativo e reversível. Produção não aprovada.
Candidata congelada: {selected}. Ganho de experiência ainda não demonstrado;
o teste final teve perdas de chamadas relevantes. Leia metrics.json antes do uso.
Threshold: {threshold}; média de 5 scores; liberação abaixo de 0,3;
refratário de 16.000 amostras (1 s). Preserve estado streaming entre blocos.

Preparação e importação
1. Transfira este pacote somente pelo acesso privado autenticado. Extraia em
   pasta privada e execute: sha256sum -c SHA256SUMS.txt
2. Confira manifest.json e metrics.json. Os conjuntos já eram conhecidos e
   os resultados são exploratórios, sem estimativa de falsas ativações/hora.
3. O seletor do aplicativo atual RECUSA a candidata por SHA: aceita somente
   o baseline. Ele também não importa threshold livre. Não tente contornar
   essa guarda nem substituir arquivos/dados do aplicativo em produção.
4. A homologação exige harness ou build privado separado, preparado à parte,
   que carregue aegis.int8.tflite pelo SHA do manifesto e configure exatamente
   o threshold acima. Este pacote não instala nem altera aplicativos.
5. Antes de qualquer promoção, execute replay ARM64 e compare reset/streaming,
   chamadas reais perto/longe e uso contínuo no telefone CONTRA O BASELINE.
   Use o piloto como homologação acompanhada, com rollback imediato; não como
   substituição diária não monitorada. Registre todas as tentativas legítimas,
   inclusive as perdidas, e a exposição contínua de cada configuração.
   Registre chamadas
   perdidas, falsos disparos, latência, CPU/bateria e estabilidade. Não envie
   áudio a serviços externos. Não altere TTS para esses testes.

Rollback
1. Pare o detector de piloto e recarregue rollback-baseline.int8.tflite no
   ambiente isolado, com threshold 0,5 e as mesmas demais regras.
2. Confira SHA-256 do baseline: {BASE_SHA}
3. O aplicativo atual continua intacto. Caso seja necessário reimportar seu
   baseline, use o seletor oficial hands-free com esse arquivo, fora de captura,
   processamento, fala ou continuação. Não limpe os dados do aplicativo.
4. Confirme o reconhecimento e mantenha as métricas da candidata preservadas.

Pendência: não foi estabelecido aparelho Android ARM64 acessível nesta rodada;
nenhum teste físico, APK, instalação ou deploy é declarado por este pacote.
''')
    files=sorted(p for p in output.iterdir() if p.is_file())
    (output/'SHA256SUMS.txt').write_text(''.join(f'{sha256(path)}  {path.name}\n' for path in files))
    for path in output.iterdir():path.chmod(0o600)
    with zipfile.ZipFile(archive,'x',compression=zipfile.ZIP_DEFLATED) as zipped:
        for path in sorted(output.iterdir()):zipped.write(path,arcname=path.name)
    archive.chmod(0o600)
    with zipfile.ZipFile(archive) as zipped:
        if zipped.testzip() is not None:
            raise ValueError('Package ZIP CRC failed')
        for path in output.iterdir():
            if hashlib.sha256(zipped.read(path.name)).hexdigest()!=sha256(path):
                raise ValueError('Archived payload differs')
    report={'created_at':datetime.now(timezone.utc).isoformat(),'directory':str(output),
        'archive':str(archive),'archive_sha256':sha256(archive),'archive_bytes':archive.stat().st_size,
        'manifest_sha256':sha256(output/'manifest.json'),'selected_model':selected,
        'model_sha256':entries['aegis.int8.tflite']['model_sha256'],'threshold':threshold,
        'files':{path.name:sha256(path) for path in sorted(output.iterdir())},
        'production_action':'none','physical_arm64':'PENDING'}
    write(report_path,report)
    print(json.dumps(report,indent=2))


if __name__=='__main__':main()
