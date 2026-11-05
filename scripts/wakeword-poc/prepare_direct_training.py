#!/usr/bin/env python3
"""Prepare every owner-labelled training event using only technical checks.

The ZIP's split and humanLabel are authoritative. No ASR, acoustic quality,
component, margin, duration, intensity, or reference-similarity eligibility is
computed. Invalid integrity aborts the preparation; no event is silently dropped.
All originals and derived artifacts remain in a new private directory.
"""
import argparse
from collections import Counter, defaultdict
import csv
import hashlib
import io
import json
import os
from pathlib import Path
import shutil
import wave
import zipfile

import numpy as np
from hardneg_audit import validate_archive
from prepare import private_output, sha256
from train_hardneg import baseline_guards, ROOT
from train_microwakeword import features, feature_window, FEATURES

EXPECTED = {'train': {'correct': 91, 'false_positive': 130},
            'validation': {'correct': 11, 'false_positive': 15},
            'test': {'correct': 5, 'false_positive': 8}}
OLD_FEATURES = Path.home()/'.cache/aegis-wakeword-hardneg/features-v1'
GAINS = (.85, 1., 1.15)


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n')


def scalar(value):
    if value is None:
        return ''
    if isinstance(value, bool):
        return str(value).lower()
    return str(value)


def check_rows(rows, expected=EXPECTED):
    if len(rows) != sum(sum(v.values()) for v in expected.values()):
        raise ValueError('Unexpected number of owner-labelled events')
    ids = [r['id'] for r in rows]
    if len(set(ids)) != len(ids):
        raise ValueError('Duplicate event ID')
    if any(r['split'] not in expected for r in rows):
        raise ValueError('Unknown supplied split; refusing to assign or redistribute it')
    actual = {split: dict(Counter(r['humanLabel'] for r in rows if r['split']==split))
              for split in expected}
    if actual != expected:
        raise ValueError('Supplied split/class counts differ from the requested inventory')
    paths = [r[kind]['path'] for r in rows for kind in ('triggerContext', 'postActivation')]
    if len(paths) != len(set(paths)):
        raise ValueError('Duplicate audio path across event records')
    return actual


def check_declared_sums(archive):
    """Verify a standard SHA256SUMS inventory if supplied; always hash members."""
    files = {e.filename for e in archive.infolist() if not e.is_dir()}
    hashes = {name: hashlib.sha256(archive.read(name)).hexdigest() for name in sorted(files)}
    if 'SHA256SUMS.txt' not in files:
        return hashes, False
    declared = {}
    for line in archive.read('SHA256SUMS.txt').decode('utf-8-sig').splitlines():
        if not line.strip():
            continue
        digest, separator, filename = line.partition('  ')
        if (not separator or len(digest)!=64 or any(c not in '0123456789abcdef' for c in digest)
                or not filename or filename in declared):
            raise ValueError('Malformed or repeated SHA256SUMS entry')
        declared[filename] = digest
    if set(declared) != files-{'SHA256SUMS.txt'}:
        raise ValueError('SHA256SUMS does not cover the complete archive')
    if any(hashes[name] != digest for name,digest in declared.items()):
        raise ValueError('Member SHA256SUMS mismatch')
    return hashes, True


def csv_value(row, field):
    if field in row and not isinstance(row[field], (dict,list)):
        return scalar(row[field])
    for kind in ('triggerContext', 'postActivation'):
        if field.startswith(kind):
            suffix = field[len(kind):]
            if suffix:
                key = suffix[0].lower()+suffix[1:]
                if key in row[kind]:
                    return scalar(row[kind][key])
    if field == 'sourceExportedAt':
        return scalar(row['provenance']['sourceExportedAt'])
    raise ValueError('Unrecognised CSV field; cannot verify against JSONL: '+field)


def check_csv(rows, raw):
    records = list(csv.DictReader(io.StringIO(raw.decode('utf-8-sig'))))
    if len(records)!=len(rows) or len({r['id'] for r in records})!=len(rows):
        raise ValueError('CSV row/ID inventory differs from JSONL')
    byid = {r['id']:r for r in rows}
    for record in records:
        if record['id'] not in byid:
            raise ValueError('CSV has an event absent from JSONL')
        row = byid[record['id']]
        for field,value in record.items():
            if value != csv_value(row,field):
                raise ValueError('CSV/JSONL field mismatch: '+field)
    # JSONL owns the split; if CSV also has it, the comparison above checks it.
    if not records or not {'id','humanLabel'}.issubset(records[0]):
        raise ValueError('CSV must preserve event ID and humanLabel')


def read_pcm(raw, declared):
    if hashlib.sha256(raw).hexdigest()!=declared['sha256']:
        raise ValueError('WAV SHA-256 mismatch')
    try:
        with wave.open(io.BytesIO(raw),'rb') as source:
            if (source.getframerate(),source.getnchannels(),source.getsampwidth(),source.getcomptype())!=(16000,1,2,'NONE'):
                raise ValueError('Expected PCM16 mono 16kHz')
            count=source.getnframes();pcm=source.readframes(count)
    except (wave.Error,EOFError) as exc:
        raise ValueError('Corrupt WAV header or payload') from exc
    if len(pcm)!=count*2 or count!=declared['samples']:
        raise ValueError('Truncated WAV or declared sample-count mismatch')
    if (declared['sampleRate'],declared['channels'],declared['encoding'])!=(16000,1,'PCM16LE'):
        raise ValueError('WAV format metadata differs from PCM')
    if abs(count/16-declared['durationMs'])>1:
        raise ValueError('WAV duration metadata mismatch')
    return np.frombuffer(pcm,dtype='<i2').copy()


def check_pair(trigger, post):
    # This is the exact shared capture overlap, not a quality or word margin.
    if len(trigger)<3200 or len(post)<3200 or not np.array_equal(trigger[-3200:],post[:3200]):
        raise ValueError('Exact 200ms trigger/post overlap absent; decision position cannot be verified')
    decision=len(trigger)-3200
    return decision


def technical_audit(zip_path, output):
    if output.exists():
        raise ValueError('Audit output must be new')
    with zipfile.ZipFile(zip_path) as archive:
        entries=validate_archive(archive)
        hashes,has_sums=check_declared_sums(archive)
        rows=[json.loads(line) for line in archive.read('dataset.jsonl').decode('utf-8-sig').splitlines() if line.strip()]
        counts=check_rows(rows)
        check_csv(rows,archive.read('dataset.csv'))
        expected_wavs={r[k]['path'] for r in rows for k in ('triggerContext','postActivation')}
        wavs={e.filename for e in entries if not e.is_dir() and e.filename.lower().endswith('.wav')}
        if expected_wavs!=wavs or len(wavs)!=520:
            raise ValueError('WAV/member inventory differs from event paths')
        for row in rows:
            pcm={kind:read_pcm(archive.read(row[kind]['path']),row[kind]) for kind in ('triggerContext','postActivation')}
            decision=check_pair(pcm['triggerContext'],pcm['postActivation'])
            row['audit']={'decision_sample_in_context':decision,'overlap_samples':3200,
                          'overlap_exact':True,'training_included':row['split']=='train',
                          'training_eligible_context':row['split']=='train',
                          'trigger_path':str(output/'dataset'/row['triggerContext']['path']),
                          'post_path':str(output/'dataset'/row['postActivation']['path']),
                          'policy':'owner humanLabel and supplied split; no acoustic exclusions'}
        output.mkdir(parents=True,mode=0o700)
        dataset=output/'dataset';dataset.mkdir(mode=0o700)
        for entry in entries:
            destination=dataset/entry.filename
            if entry.is_dir():
                destination.mkdir(parents=True,exist_ok=True,mode=0o700)
            else:
                destination.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
                destination.write_bytes(archive.read(entry))
    inventory={'schema':1,'zip_sha256':sha256(zip_path),'events':rows,'policy':'provided splits and human labels are authoritative'}
    save(output/'inventory.json',inventory)
    groups=defaultdict(set);sessions=defaultdict(set)
    for row in rows:
        groups[row.get('groupId',row['id'])].add(row['split'])
        sessions[row.get('sessionId',row['id'])].add(row['split'])
    summary={'schema':1,'complete':True,'zip_sha256':sha256(zip_path),'zip_bytes':zip_path.stat().st_size,
             'events':len(rows),'wav_files':len(expected_wavs),'counts':counts,
             'crc_ok':True,'member_sha256':hashes,'sha256sums_present_and_verified':has_sums,
             'csv_jsonl_verified':True,'format':'PCM16LE mono16000Hz','exact_overlap_events':len(rows),
             'training_events_included':221,'excluded_training_events':0,
             'cross_split_group_ids_count':sum(len(v)>1 for v in groups.values()),
             'cross_split_session_ids_count':sum(len(v)>1 for v in sessions.values()),
             'split_policy':'No split reassignment, acoustic filtering or label inference. Cross-split metadata groups are reported, not redistributed.',
             'inventory_sha256':sha256(output/'inventory.json'),'script_sha256':sha256(Path(__file__))}
    save(output/'summary.json',summary)
    return inventory,summary


def make_real_window(predecision,gain):
    """Last causal frontend window, including short startup contexts without gate."""
    changed=np.clip(predecision.astype(np.float32)*gain,-32768,32767).astype(np.int16)
    spectrum=features(changed)
    # Even a technically valid very short input remains present as left padding.
    spectrum=np.asarray(spectrum,dtype=np.float32).reshape(-1,40)
    return feature_window(spectrum,len(spectrum))


def prepare_feature_arrays(inventory, protocol_path, audit_path, output):
    if output.exists():
        raise ValueError('Feature output must be new')
    guard=baseline_guards(check_audio=True)
    old_manifest=json.loads((OLD_FEATURES/'features.json').read_text())
    original_names=[f'{split}_{key}.npy' for split in ('train','validation') for key in ('x','y')]
    for name in original_names:
        if sha256(OLD_FEATURES/name)!=old_manifest['array_sha256'][name]:
            raise ValueError('Original synthetic/public feature array changed')
    output.mkdir(parents=True,mode=0o700)
    for name in original_names:
        shutil.copyfile(OLD_FEATURES/name,output/name)
    y=np.load(output/'train_y.npy')[:,0]
    old_training=json.loads((ROOT/'comparison/training/manifest.json').read_text())
    nsynt=2*sum(r['split']=='train' for r in old_training['records']);nrefs=960
    if nsynt+nrefs+307*3!=len(y):
        raise ValueError('Original source/feature layout changed')
    indexes=np.arange(len(y))
    sources={'synthetic_positive':np.flatnonzero((indexes<nsynt)&(y==1)).tolist(),
             'reference_positive':np.flatnonzero((indexes>=nsynt)&(indexes<nsynt+nrefs)&(y==1)).tolist(),
             'synthetic_negative':np.flatnonzero((indexes<nsynt+nrefs)&(y==0)).tolist(),
             'public_dev_negative':list(range(nsynt+nrefs,len(y)))}
    windows={'train':[],'validation':[]};labels={'train':[],'validation':[]};meta={'train':[],'validation':[]}
    events=inventory['events']
    for event_index,row in enumerate(events):
        split=row['split']
        if split not in windows:
            continue  # Test keeps original audited WAVs; no test features/calibration.
        path=Path(row['audit']['trigger_path']);declared=row['triggerContext']
        trigger=read_pcm(path.read_bytes(),declared)
        decision=row['audit']['decision_sample_in_context']
        predecision=trigger[:decision]
        label=float(row['humanLabel']=='correct')
        for gain in GAINS if split=='train' else (1.,):
            windows[split].append(make_real_window(predecision,gain));labels[split].append(label)
            meta[split].append({'id':row['id'],'event_index':event_index,'split':split,
                                'humanLabel':row['humanLabel'],'label':label,'gain':gain,
                                'session':row.get('sessionId',row['id']),
                                'end_sample':decision,'sha256':declared['sha256']})
    if len({r['id'] for r in meta['train']})!=221 or Counter(r['humanLabel'] for r in events if r['split']=='train')!=EXPECTED['train']:
        raise ValueError('Every one of the221 supplied training events must produce features')
    for split in windows:
        np.save(output/f'{split}_real_x.npy',np.stack(windows[split]))
        np.save(output/f'{split}_real_y.npy',np.asarray(labels[split],dtype=np.float32).reshape(-1,1))
    manifest={'schema':1,'protocol_sha256':sha256(protocol_path),'audit_sha256':sha256(audit_path),
              'baseline':guard,'old_features_manifest_sha256':sha256(OLD_FEATURES/'features.json'),
              'array_sha256':{p.name:sha256(p) for p in sorted(output.glob('*.npy'))},
              'source_indices':sources,'real_windows':meta,'training_original_events':221,
              'training_original_by_class':EXPECTED['train'],'derived_training_windows':len(meta['train']),
              'validation_original_events':26,'validation_by_class':EXPECTED['validation'],
              'excluded_training_events':0,'script_sha256':sha256(Path(__file__)),
              'frontend':'Existing C frontend, raw predecision PCM only; last186frames, left padded if short; gains.85/1/1.15 train only; no word/quality filters.',
              'corpus_policy':'Only original synthetic/reference/FLEURS-DEV arrays copied; old real hard-negative pool is not separately duplicated.'}
    save(output/'features.json',manifest)
    return manifest


def prepare(args):
    os.umask(0o077)
    if args.output.exists():
        raise ValueError('Output must be a new private experiment directory')
    if not args.zip.is_file():
        raise FileNotFoundError('Required owner-split ZIP is missing; no earlier dataset is substituted')
    if args.expected_sha256 and sha256(args.zip)!=args.expected_sha256:
        raise ValueError('ZIP SHA-256 differs from the supplied expected hash')
    # Technical audit validates every member before extracting or building features.
    inventory,summary=technical_audit(args.zip,args.output/'audit')
    protocol={'schema':1,'audit_sha256':sha256(args.output/'audit/inventory.json'),
              'zip_sha256':inventory['zip_sha256'],'seed':20261105,
              'split_policy':'Use exactly the human-provided split field; never redistribute.',
              'label_policy':'Use humanLabel as ground truth; every221TRAIN event participates.',
              'events':[{'id':r['id'],'humanLabel':r['humanLabel'],'split':r['split'],
                         'session':r.get('sessionId',r['id']),'groupId':r.get('groupId'),
                         'origin':'owner-classified-260','training_included':r['split']=='train',
                         'trigger_sha256':r['triggerContext']['sha256'],
                         'post_sha256':r['postActivation']['sha256']} for r in inventory['events']]}
    save(args.output/'protocol.json',protocol)
    manifest=prepare_feature_arrays(inventory,args.output/'protocol.json',args.output/'audit/inventory.json',args.output/'features')
    print(json.dumps({'counts':summary['counts'],'training_original_events':manifest['training_original_events'],
                      'training_original_by_class':manifest['training_original_by_class'],
                      'derived_training_windows':manifest['derived_training_windows'],'excluded_training_events':0,
                      'zip_sha256':inventory['zip_sha256']},ensure_ascii=False),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--zip',required=True,type=Path)
    parser.add_argument('--output',required=True,type=private_output)
    parser.add_argument('--expected-sha256')
    prepare(parser.parse_args())
