#!/usr/bin/env python3
"""Reproduce the single conservative C followup without reading audio or fitting.

Derive protocol-v3 from the frozen A/B protocol and sealed validation NO-GO,
then copy the exact six cached feature arrays into a NEW private output root.
The existing protocol, features, selection, models and reports are never edited.
C uses the trainer's strategy A initialization with one epoch, unchanged LR,
splits, feature contents, sampler, seed, checkpoint rule and detector settings.
"""
import argparse
from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import shutil

from prepare import private_output, sha256

ARRAY_NAMES = {f'{split}_{name}.npy' for split in ('train', 'validation')
               for name in ('x', 'y', 'hard')}
C_INITIALIZATION = 'baseline weights; fresh Adam; one-epoch conservative followup C'
C_RULE = ('C only: preserve baseline correct validation re-detection and reduce '
          'near-decision FP vs baseline. Otherwise export C NO-GO. No additional '
          'fit/search after final test.')
C_REVISION = ('v3 before any detector test/legacy replay: A8 and B20 both lost '
              'same 1of4 validation correct; one fixed C=A1 followup. Splits, '
              'features, sampler, seed, LR unchanged. ASR capture audit of all '
              'correct previously allowed; test detector unreplayed.')
FEATURE_DERIVATION = ('Byte-identical cached arrays, original audit and splits; '
                      'rebind to pretest conservative followup protocolv3')


def json_bytes(value):
    """Use the same serialization as the preserved experiment manifests."""
    return (json.dumps(value, indent=2, ensure_ascii=False) + '\n').encode('utf-8')


def derive_followup(parent_protocol, parent_features, prior_selection,
                    parent_protocol_sha256, parent_features_manifest_sha256):
    """Pure derivation for review/tests: no arrays, audio, model inference or writes.

    The caller supplies hashes of original serialized manifests, rather than
    rehashing parsed objects and silently discarding serialization differences.
    """
    if any(x.get('schema', 1) != 1 for x in (parent_protocol, prior_selection)):
        raise ValueError('Unsupported protocol or selection schema')
    if (parent_features.get('protocol_sha256') != parent_protocol_sha256
            or prior_selection.get('protocol_sha256') != parent_protocol_sha256):
        raise ValueError('Parent features and sealed selection must bind to the same protocol')
    if (prior_selection.get('validation_gate_passed') is not False
            or prior_selection.get('detector_test_replay_started') is not False
            or prior_selection.get('legacy_regression_replay_started') is not False):
        raise ValueError('Followup requires a validation NO-GO sealed before final/legacy replay')
    if prior_selection.get('selected_strategy') not in ('A', 'B'):
        raise ValueError('Expected the original A/B selection, not an earlier followup')
    experiments = parent_protocol.get('experiments')
    expected = {'A': {'epochs': 8, 'learning_rate': .0001,
                      'initialization': 'baseline weights; fresh Adam'},
                'B': {'epochs': 20, 'learning_rate': .001,
                      'initialization': 'random; same architecture'}}
    if experiments != expected or 'parent_protocol_sha256' in parent_protocol:
        raise ValueError('Expected the original eight-epoch A / twenty-epoch B protocol only')
    if set(parent_features.get('array_sha256', {})) != ARRAY_NAMES:
        raise ValueError('Parent feature manifest must contain exactly the six frozen arrays')
    if parent_features.get('audit_sha256') != parent_protocol.get('audit_sha256'):
        raise ValueError('Feature provenance differs from the frozen split audit')
    metrics = prior_selection.get('metrics', {})
    baseline = metrics.get('baseline', {})
    if (baseline.get('correct_contexts') != 4
            or baseline.get('near_decision_correct') != 4
            or baseline.get('model_sha256') != parent_features.get('baseline', {}).get('model_sha256')):
        raise ValueError('Unexpected baseline validation guard/provenance')
    for tag in ('A', 'B'):
        candidate = metrics.get(tag, {})
        if (candidate.get('passes_correct_redetection_guard') is not False
                or candidate.get('correct_contexts') != 4
                or candidate.get('near_decision_correct') != 3):
            raise ValueError('Single followup applies only after both A/B lose one of four correct contexts')
    protocol = deepcopy(parent_protocol)
    protocol['experiments']['A']['epochs'] = 1
    protocol['experiments']['A']['initialization'] = C_INITIALIZATION
    protocol['candidate_rule'] = C_RULE
    protocol['protocol_revision_note'] = C_REVISION
    protocol['parent_protocol_sha256'] = parent_protocol_sha256
    features = deepcopy(parent_features)
    features['protocol_sha256'] = hashlib.sha256(json_bytes(protocol)).hexdigest()
    features['derived_from_features_manifest_sha256'] = parent_features_manifest_sha256
    features['derivation'] = FEATURE_DERIVATION
    return protocol, features


def verify_arrays(directory, manifest):
    """Check every input byte before copying; reject aliases outside the source."""
    directory = private_output(directory)
    if {p.name for p in directory.glob('*.npy')} != ARRAY_NAMES:
        raise ValueError('Unexpected source feature array inventory')
    for name, expected in manifest['array_sha256'].items():
        path = (directory / name).resolve()
        if not path.is_relative_to(directory) or sha256(path) != expected:
            raise ValueError('Unsafe or changed source array: ' + name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--parent-protocol', required=True, type=private_output)
    parser.add_argument('--features', required=True, type=private_output)
    parser.add_argument('--prior-selection', required=True, type=private_output)
    parser.add_argument('--output', required=True, type=private_output)
    args = parser.parse_args()
    os.umask(0o077)
    if args.output.exists():
        raise ValueError('Output root must be new; preserved experiments are never overwritten')
    feature_path = args.features / 'features.json'
    parent = json.loads(args.parent_protocol.read_bytes())
    source_features = json.loads(feature_path.read_bytes())
    selection = json.loads(args.prior_selection.read_bytes())
    parent_hash, feature_hash = sha256(args.parent_protocol), sha256(feature_path)
    protocol, features = derive_followup(parent, source_features, selection,
                                        parent_hash, feature_hash)
    verify_arrays(args.features, source_features)
    args.output.mkdir(mode=0o700, parents=True, exist_ok=False)
    target = args.output / 'features-v2'
    target.mkdir(mode=0o700)
    for name in sorted(ARRAY_NAMES):
        shutil.copyfile(args.features / name, target / name)
        if sha256(target / name) != source_features['array_sha256'][name]:
            raise ValueError('Copied feature array differs: ' + name)
    (args.output / 'protocol-v3.json').write_bytes(json_bytes(protocol))
    (target / 'features.json').write_bytes(json_bytes(features))
    evidence = {'schema': 1, 'script_sha256': sha256(Path(__file__)),
                'parent_protocol_sha256': parent_hash,
                'parent_features_manifest_sha256': feature_hash,
                'prior_selection_sha256': sha256(args.prior_selection),
                'protocol_sha256': features['protocol_sha256'],
                'features_manifest_sha256': sha256(target / 'features.json'),
                'array_sha256': features['array_sha256'],
                'operations': 'pure manifest derivation and byte-identical array copies; no fitting, inference or audio reads'}
    (args.output / 'derivation.json').write_bytes(json_bytes(evidence))
    print(json.dumps({'output': str(args.output),
                      'protocol_sha256': evidence['protocol_sha256'],
                      'features_manifest_sha256': evidence['features_manifest_sha256']}, indent=2))


if __name__ == '__main__':
    main()
