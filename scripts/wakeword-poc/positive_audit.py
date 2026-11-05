#!/usr/bin/env python3
"""Audit the lossless 260-event merge locally, preserving audio and old roles.

Acoustic similarity is diagnostic; it is never a phonetic annotation or a split.
Private inventories are written only to a fresh directory outside Git.
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
import uuid
import zipfile

import numpy as np
from hardneg_audit import BASELINE, validate_archive, wav_stats, ncc_match
from prepare import private_output, sha256

ZIP_SHA = '4543631c6842a22708db9f4839109ed3ffdd979eb03274333009b22d201741bf'
PREVIOUS = Path.home()/'.cache/aegis-wakeword-hardneg'


def save(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False)+'\n')


def scalar(value):
    if value is None:
        return ''
    if isinstance(value, bool):
        return str(value).lower()
    return str(value)


def check_sums(archive):
    entries = archive.read('SHA256SUMS.txt').decode().splitlines()
    declared = {}
    for line in entries:
        digest, filename = line.split('  ', 1)
        if len(digest) != 64 or any(c not in '0123456789abcdef' for c in digest) or filename in declared:
            raise ValueError('Invalid or repeated SHA256SUMS entry')
        declared[filename] = digest
    expected = set(archive.namelist()) - {'SHA256SUMS.txt'}
    if set(declared) != expected:
        raise ValueError('SHA256SUMS inventory differs from ZIP')
    for filename, digest in declared.items():
        if hashlib.sha256(archive.read(filename)).hexdigest() != digest:
            raise ValueError('SHA256SUMS mismatch')
    return declared


def check_csv(rows, raw):
    csv_rows = list(csv.DictReader(io.StringIO(raw.decode())))
    if len(csv_rows) != len(rows) or len({r['id'] for r in csv_rows}) != len(rows):
        raise ValueError('CSV row/ID inventory mismatch')
    byid = {r['id']: r for r in rows}
    for c in csv_rows:
        r = byid[c['id']]
        expected = {k: scalar(r[k]) for k in ('id', 'humanLabel', 'labeledAt', 'activatedAt',
                    'endedAt', 'groupId', 'groupUnknown', 'modelVersion', 'modelSha256',
                    'appVersion', 'build', 'datasetGeneratedAt')}
        expected['sourceExportedAt'] = r['provenance']['sourceExportedAt']
        for kind in ('triggerContext', 'postActivation'):
            expected[kind+'Path'] = r[kind]['path']
            expected[kind+'Sha256'] = r[kind]['sha256']
        if c != expected:
            raise ValueError('CSV/JSONL fields differ')


def timestamp(value):
    parsed = datetime.fromisoformat(value)
    if parsed.tzinfo is None:
        raise ValueError('Timestamp lacks timezone')
    return parsed


def temporal_checks(row):
    activated = timestamp(row['activatedAt'])
    exported = timestamp(row['exportedAt'])
    labeled = timestamp(row['labeledAt'])
    generated = timestamp(row['datasetGeneratedAt'])
    if not activated <= exported <= generated or not activated <= labeled <= generated:
        raise ValueError('Non-monotonic capture/export/label/generation timestamps')
    if row['provenance']['sourceExportedAt'] != row['exportedAt'] or row['provenance']['originalEventId'] != row['id']:
        raise ValueError('Provenance metadata mismatch')
    ended = timestamp(row['endedAt']) if row['endedAt'] else None
    if ended is not None and not activated <= ended <= exported:
        raise ValueError('Non-monotonic event end timestamp')
    return (ended-activated).total_seconds() if ended else None


def audit(args):
    os.umask(0o077)
    if args.output.exists():
        raise ValueError('Output must be new')
    if sha256(args.zip) != ZIP_SHA:
        raise ValueError('Unexpected merged ZIP hash')
    old_inventory = json.loads(args.previous_audit.read_text())
    old_rows = {r['id']: r for r in old_inventory['events']}
    old_protocol = json.loads(args.previous_protocol.read_text())
    old_roles = {r['id']: r for r in old_protocol['events']}
    if len(old_rows) != 161 or set(old_rows) != set(old_roles):
        raise ValueError('Old audit and split inventory differ')
    with zipfile.ZipFile(args.zip) as archive:
        entries = validate_archive(archive)
        declared = check_sums(archive)
        rows = [json.loads(line) for line in archive.read('dataset.jsonl').decode().splitlines()]
        if len(rows) != 260 or Counter(r['humanLabel'] for r in rows) != {'correct': 107, 'false_positive': 153}:
            raise ValueError('Unexpected merged event inventory')
        if len({r['id'] for r in rows}) != len(rows) or not set(old_rows).issubset(r['id'] for r in rows):
            raise ValueError('Repeated/missing event ID')
        expected = {'README.txt', 'SHA256SUMS.txt', 'merge-manifest.json', 'dataset.csv', 'dataset.jsonl'}
        for r in rows:
            if str(uuid.UUID(r['id'])) != r['id'] or r['split'] is not None:
                raise ValueError('Invalid ID or already-assigned split')
            for kind, filename in (('triggerContext', 'trigger_context.wav'), ('postActivation', 'post_activation.wav')):
                path = f"events/{r['activatedAt'][:10]}/{r['id']}/{filename}"
                if r[kind]['path'] != path or path in expected:
                    raise ValueError('Repeated or incompatible event/audio path')
                expected.add(path)
            if r['id'] in old_rows:
                previous = {k: v for k, v in old_rows[r['id']].items() if k != 'audit'}
                if previous != r:
                    raise ValueError('Merged metadata changes a previous event')
        if set(archive.namelist()) != expected or len(entries) != 525:
            raise ValueError('Unexpected ZIP member inventory')
        check_csv(rows, archive.read('dataset.csv'))
        merge = json.loads(archive.read('merge-manifest.json'))
        if (merge['events'], merge['wav_files'], merge['modelSha256']) != (260, 520, BASELINE):
            raise ValueError('Merge manifest inconsistent')
        args.output.mkdir(mode=0o700, parents=True)
        dataset = args.output/'dataset'
        dataset.mkdir(mode=0o700)
        for entry in entries:
            destination = dataset/entry.filename
            destination.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
            destination.write_bytes(archive.read(entry))
    clips = []
    for r in rows:
        stats, pcm, issues = {}, {}, []
        wall = temporal_checks(r)
        for kind in ('triggerContext', 'postActivation'):
            path = dataset/r[kind]['path']
            stats[kind], pcm[kind] = wav_stats(path.read_bytes(), r[kind])
            clips.append({'event': r['id'], 'kind': kind, 'pcm': pcm[kind], 'stats': stats[kind]})
            if stats[kind]['rms'] < 8:
                issues.append(kind+':near_silence')
            if stats[kind]['clipped_fraction'] > .01:
                issues.append(kind+':clipping_over_1pct')
            if stats[kind]['longest_zero_run_samples'] >= 3200:
                issues.append(kind+':zero_run_at_least_200ms')
        context, post = pcm['triggerContext'], pcm['postActivation']
        overlap = 3200
        if min(len(context), len(post)) < overlap or not np.array_equal(context[-overlap:], post[:overlap]):
            raise ValueError('Exact 200ms overlap absent')
        decision = len(context)-overlap
        if not 0 < decision <= 32000:
            raise ValueError('Invalid predecision context length')
        if decision < 32000:
            issues.append('decision_context_not_full_two_seconds')
        if wall is None:
            issues.append('endedAt_null_interrupted_metadata_only')
        elif wall < stats['postActivation']['seconds']-.25:
            issues.append('post_duration_exceeds_wall_event_by_over_250ms')
        if r['captureIssues'] or r['storageIssues'] or not r['captureClosed'] or r['snapshotInProgress']:
            issues.append('source_capture_or_storage_issue')
        if r['modelSha256'] != BASELINE or r['modelVersion'] != 'aegis-mww-fleurs-dev-2026-10-31' or r['origin'] != 'wake_word' or r['diagnosticMode']:
            raise ValueError('Unexpected detector provenance')
        config = r['detectorConfig']
        if tuple(config.get(k) for k in ('sampleRate', 'featureStepMs', 'stride', 'threshold', 'scoreWindow', 'hysteresisFraction', 'refractoryMs')) != (16000, 10, 3, .5, 5, .6, 1000):
            raise ValueError('Unexpected operational detector configuration')
        fatal_context = any(x.startswith('triggerContext:') for x in issues)
        r['audit'] = {'trigger_path': str(dataset/r['triggerContext']['path']),
                      'post_path': str(dataset/r['postActivation']['path']),
                      'trigger': stats['triggerContext'], 'post': stats['postActivation'],
                      'decision_sample_in_context': decision, 'overlap_samples': overlap,
                      'overlap_exact': True, 'wall_event_seconds': wall, 'issues': issues,
                      'source': 'old-161' if r['id'] in old_rows else 'new-99',
                      'previous_role': old_roles.get(r['id']),
                      'training_eligible_context': not fatal_context and decision == 32000 and r['humanLabel'] == 'false_positive',
                      'positive_training_eligible': False, 'post_training_eligible': False,
                      'role_note': 'positive inclusion requires acoustic boundary inspection; no postdecision training'}
    inventory = {'schema': 2, 'zip_sha256': ZIP_SHA, 'events': rows,
                 'previous_audit_sha256': sha256(args.previous_audit),
                 'previous_protocol_sha256': sha256(args.previous_protocol)}
    # Stable inventory is available before the expensive pairwise diagnostic.
    save(args.output/'inventory.json', inventory)
    print('Per-file audit passed; stable inventory.json written; scanning similarity', flush=True)
    digest_groups = defaultdict(list)
    for c in clips:
        digest_groups[c['stats']['pcm_sha256']].append({'event': c['event'], 'kind': c['kind']})
    exact = [group for group in digest_groups.values() if len(group)>1]
    near, without_cue, predecision = [], [], []
    decisions = {r['id']: r['audit']['decision_sample_in_context'] for r in rows}
    for i, x in enumerate(clips):
        if i % 40 == 0:
            print(f'Similarity {i}/{len(clips)}', flush=True)
        for y in clips[i+1:]:
            if x['event'] == y['event']:
                continue
            identity = {'event_a': x['event'], 'kind_a': x['kind'], 'event_b': y['event'], 'kind_b': y['kind']}
            match = ncc_match(x['pcm'], y['pcm'])
            if match:
                near.append({**identity, **match})
            # Check speech correlation after removing the confirmation cue.
            # Predecision crop additionally checks the only audio allowed in fit.
            if x['kind'] == y['kind'] == 'postActivation' and match:
                clean = ncc_match(x['pcm'][8000:], y['pcm'][8000:])
                if clean:
                    without_cue.append({**identity, **clean})
            if x['kind'] == y['kind'] == 'triggerContext':
                clean = ncc_match(x['pcm'][:decisions[x['event']]], y['pcm'][:decisions[y['event']]])
                if clean:
                    predecision.append({**identity, **clean})
    new_rows = [r for r in rows if r['id'] not in old_rows]
    ordered = sorted(new_rows, key=lambda r: timestamp(r['activatedAt']))
    gaps = [(timestamp(y['activatedAt'])-timestamp(x['activatedAt'])).total_seconds() for x,y in zip(ordered, ordered[1:])]
    summary = {'schema': 2, 'complete': True, 'zip_sha256': ZIP_SHA, 'zip_bytes': args.zip.stat().st_size,
        'members': len(entries), 'member_sha256_checked': len(declared), 'crc_ok': True,
        'events': len(rows), 'wav_files': len(clips), 'labels': dict(Counter(r['humanLabel'] for r in rows)),
        'by_source': {source: {'events': len(rs), 'labels': dict(Counter(r['humanLabel'] for r in rs)),
                             'short_predecision_context': sum(r['audit']['decision_sample_in_context']<32000 for r in rs),
                             'under_one_second_predecision': sum(r['audit']['decision_sample_in_context']<16000 for r in rs)}
                      for source,rs in (('old-161', [r for r in rows if r['id'] in old_rows]), ('new-99', new_rows))},
        'old_metadata_unchanged': True, 'old_roles_recovered': len(old_roles),
        'all_wav_formats_pcm16_mono_16khz': True, 'all_declared_hashes_match': True,
        'csv_jsonl_all_fields_match': True, 'overlap_exact_events': len(rows),
        'unique_group_ids': len({r['groupId'] for r in rows}), 'unique_session_ids': len({r['sessionId'] for r in rows}),
        'models': dict(Counter(r['modelSha256'] for r in rows)), 'app_versions': dict(Counter(r['appVersion'] for r in rows)),
        'builds': dict(Counter(str(r['build']) for r in rows)),
        'issues': dict(Counter(issue for r in rows for issue in r['audit']['issues'])),
        'missing_endedAt': sum(r['endedAt'] is None for r in rows),
        'duration_seconds': {kind: sum(c['stats']['seconds'] for c in clips if c['kind']==kind) for kind in ('triggerContext','postActivation')},
        'duration_ranges': {kind: [min(c['stats']['seconds'] for c in clips if c['kind']==kind), max(c['stats']['seconds'] for c in clips if c['kind']==kind)] for kind in ('triggerContext','postActivation')},
        'new_session': {'span_seconds': (timestamp(ordered[-1]['activatedAt'])-timestamp(ordered[0]['activatedAt'])).total_seconds(),
                        'consecutive_gap_range_seconds': [min(gaps), max(gaps)], 'independence': 'one short capture session; temporal blocks remain dependent'},
        'exact_pcm_duplicate_groups': exact, 'near_duplicate_pairs': near,
        'near_duplicate_post_pairs_without_first_500ms': without_cue,
        'near_duplicate_predecision_context_pairs': predecision,
        'similarity_protocol': {'all_cross_event_clip_pairs': True, 'ncc_threshold': .90,
            'minimum_overlap_seconds': .75, 'minimum_local_rms': 20, 'analysis_rate': 2000,
            'post_prefix_removed_ms': 500, 'training_audio': 'predecision only',
            'limitations': 'Waveform correlation does not establish independent acoustic conditions or find every similar pronunciation. Cue-dominated raw post matches must not join speech groups.'},
        'script_sha256': sha256(Path(__file__)), 'inventory_sha256': sha256(args.output/'inventory.json'),
        'helper_sha256': sha256(Path(__file__).with_name('hardneg_audit.py')), 'numpy': np.__version__}
    save(args.output/'audit.json', summary)
    print(json.dumps({k:v for k,v in summary.items() if k not in ('exact_pcm_duplicate_groups','near_duplicate_pairs','near_duplicate_post_pairs_without_first_500ms','near_duplicate_predecision_context_pairs')}, indent=2), flush=True)
    print('Similarity counts:', len(exact), len(near), len(without_cue), len(predecision), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--zip', required=True, type=Path)
    parser.add_argument('--output', required=True, type=private_output)
    parser.add_argument('--previous-audit', type=Path, default=PREVIOUS/'audit-v1/inventory.json')
    parser.add_argument('--previous-protocol', type=Path, default=PREVIOUS/'protocol-v3.json')
    audit(parser.parse_args())
