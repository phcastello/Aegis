#!/usr/bin/env python3
"""Audit the classified private activation ZIP, preserving every original byte.

Outputs must be new and outside Git. Acoustic checks operate on copies only;
coarse downsampling is for duplication analysis, never detector preprocessing.
No scores or model output participate in grouping or eligibility decisions.
"""
import argparse
from collections import Counter, defaultdict
import csv
from datetime import datetime
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import unicodedata
import wave
import zipfile

import numpy as np
from scipy.signal import correlate
from prepare import private_output, sha256

BASELINE = 'f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514'


def validate_archive(archive):
    seen = set()
    entries = archive.infolist()
    for entry in entries:
        raw = entry.filename
        parts = raw.rstrip('/').split('/')
        mode = entry.external_attr >> 16
        key = unicodedata.normalize('NFC', raw).casefold()
        if (not raw or raw != entry.orig_filename or '\x00' in raw or raw.startswith('/') or '\\' in raw or any(
                p in ('', '.', '..') or ':' in p for p in parts)
                or key in seen or stat.S_ISLNK(mode)
                or stat.S_IFMT(mode) not in (0, stat.S_IFREG, stat.S_IFDIR)):
            raise ValueError(f'Unsafe or duplicate ZIP path: {raw!r}')
        if entry.file_size > 20_000_000 or entry.flag_bits & 1:
            raise ValueError('Oversized or encrypted member')
        seen.add(key)
    if sum(e.file_size for e in entries) > 250_000_000:
        raise ValueError('ZIP uncompressed size exceeds audit limit')
    if archive.testzip() is not None:
        raise ValueError('ZIP CRC mismatch')
    return entries


def wav_stats(raw, declared):
    with wave.open(io.BytesIO(raw), 'rb') as f:
        params = (f.getframerate(), f.getnchannels(), f.getsampwidth(), f.getcomptype())
        if params != (16000, 1, 2, 'NONE'):
            raise ValueError(f'Unexpected WAV format: {params}')
        frames = f.getnframes()
        pcm = f.readframes(frames)
        if len(pcm) != frames * 2:
            raise ValueError('Truncated WAV PCM')
    digest = hashlib.sha256(raw).hexdigest()
    if digest != declared['sha256'] or frames != declared['samples']:
        raise ValueError('WAV SHA-256 or sample-count mismatch')
    if (declared['sampleRate'], declared['channels'], declared['encoding']) != (16000, 1, 'PCM16LE'):
        raise ValueError('Metadata format mismatch')
    # Export durationMs is integer-rounded/truncated, permit one millisecond.
    if abs(frames / 16 - declared['durationMs']) > 1:
        raise ValueError('Metadata duration mismatch')
    x = np.frombuffer(pcm, dtype='<i2').copy()
    xf = x.astype(np.float64)
    # Long zero runs and clipping are capture diagnostics, not automatic labels.
    zero = x == 0
    edges = np.diff(np.r_[False, zero, False].astype(np.int8))
    longest = int(max(np.flatnonzero(edges == -1) - np.flatnonzero(edges == 1), default=0))
    stats = {'sha256': digest, 'pcm_sha256': hashlib.sha256(pcm).hexdigest(),
             'samples': frames, 'seconds': frames / 16000,
             'rms': float(np.sqrt(np.mean(xf * xf))) if frames else 0.,
             'dc': float(np.mean(xf)) if frames else 0.,
             'peak': int(np.max(np.abs(x.astype(np.int32)))) if frames else 0,
             'zero_fraction': float(np.mean(zero)) if frames else 1.,
             'longest_zero_run_samples': longest,
             'clipped_fraction': float(np.mean(np.abs(x.astype(np.int32)) >= 32767)) if frames else 0.}
    return stats, x


def ncc_match(x, y):
    """Gain/offset invariant waveform similarity at arbitrary alignment, >=0.75s.

    Taking every eighth sample makes analysis cheap and deterministic; it is not
    applied to exported audio or frontend features. Local energy excludes silence.
    """
    x = x[::8].astype(np.float64)
    y = y[::8].astype(np.float64)
    nx, ny = len(x), len(y)
    if min(nx, ny) < 1500:
        return None
    lags = np.arange(-ny + 1, nx)
    xs, ys = np.maximum(lags, 0), np.maximum(-lags, 0)
    n = np.minimum(nx - xs, ny - ys)
    keep = n >= 1500
    lags, xs, ys, n = (a[keep] for a in (lags, xs, ys, n))
    sx, sy = np.r_[0., np.cumsum(x)], np.r_[0., np.cumsum(y)]
    ex, ey = np.r_[0., np.cumsum(x*x)], np.r_[0., np.cumsum(y*y)]
    ax, ay = sx[xs+n]-sx[xs], sy[ys+n]-sy[ys]
    vx, vy = ex[xs+n]-ex[xs]-ax*ax/n, ey[ys+n]-ey[ys]-ay*ay/n
    valid = (vx / n >= 400.) & (vy / n >= 400.)
    product = correlate(x, y, method='fft')[keep]
    values = np.where(valid, (product-ax*ay/n) / np.sqrt(np.maximum(vx*vy, 1e-20)), -1.)
    index = int(np.argmax(values))
    if values[index] < .90:
        return None
    return {'normalized_correlation': float(values[index]),
            'overlap_seconds': float(n[index] / 2000), 'lag_seconds': float(lags[index]/2000)}


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--zip', required=True, type=Path)
    p.add_argument('--output', required=True, type=private_output)
    p.add_argument('--session-gap-minutes', type=float, default=30.)
    a = p.parse_args()
    os.umask(0o077)
    if a.output.exists():
        p.error('Output must be new')
    with zipfile.ZipFile(a.zip) as z:
        entries = validate_archive(z)
        rows = [json.loads(line) for line in z.read('dataset.jsonl').decode().splitlines()]
        if len(rows) != 161 or Counter(r['humanLabel'] for r in rows) != {'false_positive':146, 'correct':15}:
            raise ValueError('Unexpected event inventory or human labels')
        if len({r['id'] for r in rows}) != len(rows):
            raise ValueError('Duplicate event ID')
        expected = {'README.txt', 'dataset.csv', 'dataset.jsonl'}
        for r in rows:
            for key in ('triggerContext', 'postActivation'):
                expected.add(r[key]['path'])
        if set(e.filename for e in entries) != expected or len(entries) != 325:
            raise ValueError('Unexpected ZIP members')
        csv_rows = {r['id']:r for r in csv.DictReader(io.StringIO(z.read('dataset.csv').decode()))}
        if len(csv_rows) != 161:
            raise ValueError('CSV inventory mismatch')
        for r in rows:
            c = csv_rows[r['id']]
            for field in ('humanLabel','activatedAt','endedAt','groupId','modelVersion','modelSha256','appVersion'):
                if str(r[field]) != c[field]:
                    raise ValueError(f'CSV/JSONL mismatch: {field}')
            for field, prefix in (('triggerContext','triggerContext'),('postActivation','postActivation')):
                if r[field]['path'] != c[prefix+'Path'] or r[field]['sha256'] != c[prefix+'Sha256']:
                    raise ValueError('CSV/JSONL audio mismatch')
        a.output.mkdir(mode=0o700, parents=True)
        dataset = a.output / 'dataset'
        dataset.mkdir(mode=0o700)
        # Extraction occurs only after full structural validation. Each path is
        # unique and strictly relative; no symlinks are accepted.
        for e in entries:
            destination = dataset / e.filename
            destination.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            destination.write_bytes(z.read(e))
    clips = []
    for r in rows:
        issues = []
        stats, pcm = {}, {}
        for key in ('triggerContext', 'postActivation'):
            path = dataset / r[key]['path']
            stats[key], pcm[key] = wav_stats(path.read_bytes(), r[key])
            clips.append({'event': r['id'], 'kind':key, 'path':str(path), 'pcm':pcm[key], 'stats':stats[key]})
            if stats[key]['rms'] < 8:
                issues.append(key + ':near_silence')
            if stats[key]['clipped_fraction'] > .01:
                issues.append(key + ':clipping_over_1pct')
            if stats[key]['longest_zero_run_samples'] >= 3200:
                issues.append(key + ':zero_run_at_least_200ms')
        context, post = pcm['triggerContext'], pcm['postActivation']
        overlap = min(3200, len(post))
        # Collection appends a 200ms post-decision margin to the 2s context.
        # For short captures only the available post prefix can be checked.
        exact = overlap > 0 and len(context) >= overlap and np.array_equal(context[-overlap:], post[:overlap])
        decision = len(context) - overlap if exact else None
        if not exact:
            issues.append('context_post_overlap_not_verified')
        if decision != 32000:
            issues.append('decision_context_not_full_two_seconds')
        activated = datetime.fromisoformat(r['activatedAt'])
        ended = datetime.fromisoformat(r['endedAt'])
        wall_seconds = (ended-activated).total_seconds()
        # endedAt includes command processing/playback; equality is not expected.
        if wall_seconds < stats['postActivation']['seconds'] - .25:
            issues.append('post_duration_exceeds_wall_event_by_over_250ms')
        if r['captureIssues'] or r['storageIssues'] or not r['captureClosed'] or r['snapshotInProgress']:
            issues.append('source_capture_or_storage_issue')
        if r['modelSha256'] != BASELINE or r['origin'] != 'wake_word' or r['diagnosticMode']:
            issues.append('unexpected_detector_provenance')
        dc = r['detectorConfig']
        if tuple(dc.get(k) for k in ('sampleRate','featureStepMs','stride','threshold','scoreWindow','hysteresisFraction','refractoryMs')) != (16000,10,3,.5,5,.6,1000):
            issues.append('unexpected_detector_configuration')
        r['audit'] = {'trigger_path':str(dataset/r['triggerContext']['path']),
                      'post_path':str(dataset/r['postActivation']['path']),
                      'trigger':stats['triggerContext'], 'post':stats['postActivation'],
                      'decision_sample_in_context':decision, 'overlap_samples':overlap,
                      'overlap_exact':exact, 'wall_event_seconds':wall_seconds,
                      'issues':issues,
                      'training_eligible_context':not issues and r['humanLabel']=='false_positive',
                      'proposed_session':None,
                      'positive_training_eligible':False,
                      'post_training_eligible':False,
                      'role_note':'candidate negative trigger context pending frozen split; post contextual only' if r['humanLabel']=='false_positive' else 'evaluation only: no reliable phonetic alignment; human event label preserved'}
    print('WAV audit complete; scanning all cross-event clip pairs for near duplication',flush=True)
    exact = []
    digest_groups = defaultdict(list)
    for clip in clips:
        digest_groups[clip['stats']['pcm_sha256']].append({'event':clip['event'],'kind':clip['kind']})
    exact = [v for v in digest_groups.values() if len(v)>1]
    near = []
    for i, x in enumerate(clips):
        if i % 40 == 0:
            print(f'Acoustic similarity {i}/{len(clips)}',flush=True)
        for y in clips[i+1:]:
            if x['event']==y['event']:
                continue
            match = ncc_match(x['pcm'], y['pcm'])
            if match:
                near.append({'event_a':x['event'],'kind_a':x['kind'],'event_b':y['event'],'kind_b':y['kind'],**match})
    # Union metadata groups, chronological sessions, and strong acoustic matches.
    parent = {r['id']:r['id'] for r in rows}
    def find(k):
        while parent[k]!=k:
            parent[k]=parent[parent[k]];k=parent[k]
        return k
    def join(a,b):
        parent[find(b)]=find(a)
    bygroup = defaultdict(list)
    ordered = sorted(rows,key=lambda r:r['activatedAt'])
    for r in rows:bygroup[r['groupId']].append(r['id'])
    for ids in bygroup.values():
        for identifier in ids[1:]:join(ids[0],identifier)
    for x,y in zip(ordered,ordered[1:]):
        gap=(datetime.fromisoformat(y['activatedAt'])-datetime.fromisoformat(x['activatedAt'])).total_seconds()
        if gap<=a.session_gap_minutes*60:join(x['id'],y['id'])
    for match in near:join(match['event_a'],match['event_b'])
    grouped = defaultdict(list)
    for r in ordered:grouped[find(r['id'])].append(r)
    sessions=[]
    for index, rs in enumerate(grouped.values(),1):
        key=f'session-{index:02d}'
        for r in rs:r['audit']['proposed_session']=key
        sessions.append({'id':key,'events':[r['id'] for r in rs],
                         'start':min(r['activatedAt'] for r in rs),'end':max(r['activatedAt'] for r in rs),
                         'counts':dict(Counter(r['humanLabel'] for r in rs))})
    inventory={'schema':1,'zip_sha256':sha256(a.zip),'events':rows}
    summary={'schema':1,'zip_sha256':sha256(a.zip),'zip_bytes':a.zip.stat().st_size,
             'members':325,'wav_files':len(clips),'labels':dict(Counter(r['humanLabel'] for r in rows)),
             'unique_group_ids':len(bygroup),'unique_session_ids':len(set(r['sessionId'] for r in rows)),
             'days':dict(Counter(r['activatedAt'][:10] for r in rows)),
             'models':dict(Counter(r['modelSha256'] for r in rows)),
             'app_versions':dict(Counter(r['appVersion'] for r in rows)),
             'builds':dict(Counter(str(r['build']) for r in rows)),
             'detector_configurations':list({json.dumps(r['detectorConfig'],sort_keys=True) for r in rows}),
             'all_declared_hashes_match':True,'all_wav_formats_pcm16_mono_16khz':True,
             'overlap_exact_events':sum(r['audit']['overlap_exact'] for r in rows),
             'issues':dict(Counter(x for r in rows for x in r['audit']['issues'])),
             'duration_seconds':dict(Counter({kind:sum(c['stats']['seconds'] for c in clips if c['kind']==kind) for kind in ('triggerContext','postActivation')})),
             'duration_ranges':{kind:[min(c['stats']['seconds'] for c in clips if c['kind']==kind),max(c['stats']['seconds'] for c in clips if c['kind']==kind)] for kind in ('triggerContext','postActivation')},
             'exact_pcm_duplicate_groups':exact,'near_duplicate_pairs':near,
             'similarity_protocol':{'all_cross_event_trigger_and_post_pairs':True,'minimum_overlap_seconds':.75,'minimum_local_rms':20,'ncc_threshold':.90,'analysis_sample_rate':2000,'limitations':'Waveform correlation detects copied/near-identical capture, not all semantically related speech or shared environments. All positives remain evaluation-only, post audio contextual only.'},
             'grouping_protocol':{'session_gap_minutes':a.session_gap_minutes,'rules':['same groupId','consecutive capture timestamps within gap','NCC >= .90 for >= .75 s'], 'use_model_scores':False},
             'sessions':sessions,'script_sha256':sha256(Path(__file__)),
             'numpy':np.__version__}
    for name,value in (('inventory.json',inventory),('audit.json',summary)):
        (a.output/name).write_text(json.dumps(value,indent=2,ensure_ascii=False)+'\n')
    print(json.dumps({k:v for k,v in summary.items() if k not in ('near_duplicate_pairs','detector_configurations','sessions')},indent=2),flush=True)
    print('Sessions:',json.dumps([{k:v for k,v in s.items() if k!='events'} for s in sessions],indent=2),flush=True)


if __name__=='__main__':main()
