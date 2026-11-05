#!/usr/bin/env python3
"""Freeze A/B selection from validation reports before any final detector replay.

Uses accepted LiteRT/default as primary. Native host results are a separate
engineering compatibility check. Does not read audio or execute any model.
"""
import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path

from prepare import private_output, sha256


def select(protocol_path, baseline_path, candidate_paths, training_paths):
    protocol = json.loads(protocol_path.read_text())
    protocol_digest = sha256(protocol_path)
    baseline = json.loads(baseline_path.read_text())
    reports = {'baseline': baseline}
    reports.update({key: json.loads(path.read_text()) for key, path in candidate_paths.items()})
    train = {key: json.loads(path.read_text()) for key, path in training_paths.items()}
    base_rows = baseline['runtimes']['litert']['contexts']['events']
    base_ids = {r['id'] for r in base_rows}
    expected_ids = {r['id'] for r in protocol['events'] if r['split'] == 'validation'}
    if base_ids != expected_ids:
        raise ValueError('Baseline does not contain exactly frozen validation IDs')
    if baseline['reference_kernel_mode']:
        raise ValueError('Primary baseline must use accepted LiteRT/default')
    base = baseline['runtimes']['litert']['contexts']['counts']
    metrics = {}
    for name, report in reports.items():
        rows = report['runtimes']['litert']['contexts']['events']
        if (report['split_manifest_sha256'] != protocol_digest or report['split'] != 'validation'
                or report['reference_kernel_mode'] or {r['id'] for r in rows} != base_ids
                or len(rows) != len(base_ids) or any(r['split'] != 'validation' for r in rows)
                or report['evaluation_script_sha256'] != baseline['evaluation_script_sha256']
                or report['rules'] != baseline['rules']):
            raise ValueError('Reports must use identical frozen validation/evaluator/rules/default runtime')
        if any(r['humanLabel'] != next(b['humanLabel'] for b in base_rows if b['id'] == r['id']) for r in rows):
            raise ValueError('Validation labels differ across evaluations')
        count = report['runtimes']['litert']['contexts']['counts']
        metrics[name] = {
            'model_sha256': report['model_sha256'],
            'false_positive_contexts': count['false_positive']['contexts'],
            'near_decision_fp': count['false_positive']['near_decision_event_contexts'],
            'any_context_fp': count['false_positive']['any_event_contexts'],
            'correct_contexts': count['correct']['contexts'],
            'near_decision_correct': count['correct']['near_decision_event_contexts'],
            'any_context_correct': count['correct']['any_event_contexts'],
            'native_counts': report['runtimes'].get('native_host', {}).get('contexts', {}).get('counts'),
            'native_unavailable': report.get('native_unavailable'),
        }
        if name == 'baseline':
            continue
        if (train[name]['model_sha256'] != report['model_sha256']
                or train[name]['protocol_sha256'] != protocol_digest
                or train[name]['strategy'] != name):
            raise ValueError('Candidate training provenance differs from validation report')
        if (count['false_positive']['contexts'] != base['false_positive']['contexts']
                or count['correct']['contexts'] != base['correct']['contexts']):
            raise ValueError('Candidate class denominators differ from baseline')
        metrics[name]['passes_correct_redetection_guard'] = (
            count['correct']['near_decision_event_contexts'] >= base['correct']['near_decision_event_contexts'])
        metrics[name]['improves_near_decision_fp'] = (
            count['false_positive']['near_decision_event_contexts'] < base['false_positive']['near_decision_event_contexts'])
        metrics[name]['training_best_objective'] = train[name]['best_objective']
    eligible = [name for name in ('A', 'B') if metrics[name]['passes_correct_redetection_guard']]
    if eligible:
        chosen = min(eligible, key=lambda name: (metrics[name]['near_decision_fp'], name))
        passed = True
        reason = 'Minimum near-decision FP among candidates preserving baseline correct re-detection; tie favors A'
    else:
        chosen = min(('A', 'B'), key=lambda name: (train[name]['best_objective'], name))
        passed = False
        reason = 'NO-GO: neither preserves correct re-detection; export minimum training validation objective as experimental artifact only'
    return {
        'schema': 1,
        'timestampUTC': datetime.now(timezone.utc).isoformat(),
        'selected_strategy': chosen,
        'model_sha256': reports[chosen]['model_sha256'],
        'protocol_sha256': protocol_digest,
        'validation_reports_sha256': {'baseline': sha256(baseline_path), **{name: sha256(path) for name, path in candidate_paths.items()}},
        'training_reports_sha256': {name: sha256(path) for name, path in training_paths.items()},
        'evaluation_script_sha256': baseline['evaluation_script_sha256'],
        'selection_script_sha256': sha256(Path(__file__)),
        'validation_gate_passed': passed,
        'selected_improves_near_decision_fp': metrics[chosen]['improves_near_decision_fp'],
        'primary_runtime': 'accepted LiteRT/default',
        'native_policy': 'Separate unmodified production-library engineering compatibility check; not a replacement selection metric',
        'frozen_protocol_candidate_rule': protocol['candidate_rule'],
        'rule_applied': reason,
        'metrics': metrics,
        'detector_test_replay_started': False,
        'legacy_regression_replay_started': False,
        'prefit_inspection_scope': 'All new correct captures had preregistered local ASR capture inspection; no fitting on them. Test FP accessed only for integrity/similarity audit before this selection.',
        'acceptance': 'Validation selection is not production acceptance; assess final session test, legacy regressions and runtime compatibility without reselecting the candidate',
    }


def select_followup(protocol_path, baseline_path, followup_path, training_path, prior_path):
    """One preregistered short adaptation after A/B failed, no test tuning."""
    protocol = json.loads(protocol_path.read_text())
    prior = json.loads(prior_path.read_text())
    training = json.loads(training_path.read_text())
    baseline = json.loads(baseline_path.read_text())
    candidate = json.loads(followup_path.read_text())
    protocol_digest = sha256(protocol_path)
    if prior['validation_gate_passed'] or protocol['parent_protocol_sha256'] != prior['protocol_sha256']:
        raise ValueError('Followup requires the failed A/B selection and its frozen parent protocol')
    if (training['strategy'] != 'A' or training['config']['epochs'] != 1
            or training['config']['learning_rate'] != .0001
            or training['protocol_sha256'] != protocol_digest
            or training['model_sha256'] != candidate['model_sha256']):
        raise ValueError('Followup must match the single declared one-epoch baseline fine-tune')
    expected = {r['id'] for r in protocol['events'] if r['split'] == 'validation'}
    metrics = {}
    for name, report in (('baseline', baseline), ('C', candidate)):
        rows = report['runtimes']['litert']['contexts']['events']
        if (report['split_manifest_sha256'] != protocol_digest or report['split'] != 'validation'
                or report['reference_kernel_mode'] or {r['id'] for r in rows} != expected
                or len(rows) != len(expected) or any(r['split'] != 'validation' for r in rows)
                or report['evaluation_script_sha256'] != baseline['evaluation_script_sha256']
                or report['rules'] != baseline['rules']):
            raise ValueError('Followup validation reports must match frozen IDs, rules and evaluator')
        count = report['runtimes']['litert']['contexts']['counts']
        metrics[name] = {'model_sha256': report['model_sha256'], 'counts': count,
                         'native_counts': report['runtimes'].get('native_host', {}).get('contexts', {}).get('counts'),
                         'native_unavailable': report.get('native_unavailable')}
    if baseline['model_sha256'] != prior['metrics']['baseline']['model_sha256']:
        raise ValueError('Baseline changed between A/B and followup')
    if prior['evaluation_script_sha256'] != baseline['evaluation_script_sha256']:
        raise ValueError('Evaluator changed between A/B and followup')
    base = metrics['baseline']['counts']
    current = metrics['C']['counts']
    if (base['false_positive']['contexts'] != current['false_positive']['contexts']
            or base['correct']['contexts'] != current['correct']['contexts']
            or base['correct']['near_decision_event_contexts'] != 4):
        raise ValueError('Followup must preserve exactly the four frozen baseline validation positives')
    correct_passed = current['correct']['near_decision_event_contexts'] >= 4
    improves = current['false_positive']['near_decision_event_contexts'] < base['false_positive']['near_decision_event_contexts']
    return {
        'schema': 1, 'timestampUTC': datetime.now(timezone.utc).isoformat(),
        'selected_strategy': 'C', 'internal_training_strategy': 'A',
        'model_sha256': candidate['model_sha256'], 'protocol_sha256': protocol_digest,
        'parent_protocol_sha256': prior['protocol_sha256'],
        'prior_selection_sha256': sha256(prior_path),
        'validation_reports_sha256': {'baseline': sha256(baseline_path), 'C': sha256(followup_path)},
        'training_reports_sha256': {'C': sha256(training_path)},
        'evaluation_script_sha256': baseline['evaluation_script_sha256'],
        'selection_script_sha256': sha256(Path(__file__)),
        'validation_gate_passed': correct_passed and improves,
        'selected_improves_near_decision_fp': improves,
        'correct_redetection_guard_passed': correct_passed,
        'primary_runtime': 'accepted LiteRT/default',
        'native_policy': 'Separate engineering compatibility check using unmodified production host library',
        'frozen_protocol_candidate_rule': protocol['candidate_rule'],
        'rule_applied': 'Single preregistered one-epoch C followup must preserve 4/4 correct re-detections and improve near-decision FP versus baseline; otherwise export C as NO-GO research artifact, no further search',
        'metrics': metrics,
        'prior_ab_metrics': prior['metrics'],
        'detector_test_replay_started': False,
        'legacy_regression_replay_started': False,
        'prefit_inspection_scope': prior['prefit_inspection_scope'],
        'acceptance': 'Selection frozen before final detector test/legacy replay; final results may reject C but cannot select a different candidate',
    }


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--protocol', type=private_output, required=True)
    p.add_argument('--baseline-evaluation', type=private_output, required=True)
    p.add_argument('--a-evaluation', type=private_output)
    p.add_argument('--b-evaluation', type=private_output)
    p.add_argument('--a-training', type=private_output)
    p.add_argument('--b-training', type=private_output)
    p.add_argument('--followup-evaluation', type=private_output)
    p.add_argument('--followup-training', type=private_output)
    p.add_argument('--prior-selection', type=private_output)
    p.add_argument('--output', type=private_output, required=True)
    a = p.parse_args()
    if a.output.exists():
        raise ValueError('Selection must be a new immutable file')
    followup_args = (a.followup_evaluation, a.followup_training, a.prior_selection)
    ab_args = (a.a_evaluation, a.b_evaluation, a.a_training, a.b_training)
    if all(followup_args) and not any(ab_args):
        result = select_followup(a.protocol, a.baseline_evaluation,
                                 a.followup_evaluation, a.followup_training, a.prior_selection)
    elif all(ab_args) and not any(followup_args):
        result = select(a.protocol, a.baseline_evaluation,
                        {'A': a.a_evaluation, 'B': a.b_evaluation},
                        {'A': a.a_training, 'B': a.b_training})
    else:
        p.error('Supply all four A/B inputs OR all three followup inputs')
    os.umask(0o077)
    a.output.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    with a.output.open('x') as f:
        f.write(json.dumps(result, indent=2, ensure_ascii=False) + '\n')
    print(json.dumps({key: result[key] for key in ('timestampUTC', 'selected_strategy', 'model_sha256', 'protocol_sha256', 'validation_gate_passed', 'selected_improves_near_decision_fp')}, indent=2))


if __name__ == '__main__':
    main()
