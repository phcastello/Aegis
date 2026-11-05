#!/usr/bin/env python3
"""Selection and streaming contracts, using no owner audio or model weights."""
from types import SimpleNamespace
import unittest

import numpy as np

from compare_microwakeword import MicroWakeWordDetector
from tradeoff_eval import paired, pareto


def row(identifier, label, detected, anywhere=None):
    return {'id': identifier, 'humanLabel': label, 'split': 'validation',
            'detected_near_decision': detected,
            'detected_any': detected if anywhere is None else anywhere}


class PairedUtilityTests(unittest.TestCase):
    def test_supressions_introductions_losses_and_recoveries_remain_distinct(self):
        before = [row('fp-out', 'false_positive', True),
                  row('fp-in', 'false_positive', False),
                  row('call-out', 'correct', True),
                  row('call-in', 'correct', False)]
        after = [row(r['id'], r['humanLabel'], not r['detected_near_decision'])
                 for r in before]
        result = paired(before, after[::-1])
        self.assertEqual(result['counts'], {'fp_eliminated': 1, 'fp_new': 1,
                                           'positives_lost': 1, 'positives_recovered': 1})
        self.assertEqual(result['utility'], 0)
        self.assertEqual(result['fp_eliminated'], ['fp-out'])
        self.assertEqual(result['fp_new'], ['fp-in'])
        self.assertEqual(result['positives_lost'], ['call-out'])
        self.assertEqual(result['positives_recovered'], ['call-in'])

    def test_six_suppressed_fp_pay_for_one_legitimate_loss(self):
        before = [row(f'p{i}', 'correct', True) for i in range(11)]
        before += [row(f'n{i}', 'false_positive', i < 13) for i in range(15)]
        after = [dict(r) for r in before]
        after[0].update(detected_near_decision=False, detected_any=False)
        for r in after[11:17]:
            r.update(detected_near_decision=False, detected_any=False)
        result = paired(before, after)
        self.assertEqual(result['utility'], 2)
        self.assertEqual(result['positives_recognized'], 10)
        self.assertEqual(result['false_contexts_activated'], 7)

    def test_anywhere_fp_and_near_decision_positive_have_different_meanings(self):
        before = [row('call', 'correct', True), row('fp', 'false_positive', True)]
        after = [row('call', 'correct', False, anywhere=True),
                 row('fp', 'false_positive', False, anywhere=True)]
        result = paired(before, after)
        self.assertEqual(result['counts']['positives_lost'], 1)
        self.assertEqual(result['counts']['fp_eliminated'], 0)
        self.assertEqual(result['utility'], -4)

    def test_missing_event_must_not_improve_selection(self):
        rows = [row('p', 'correct', True), row('n', 'false_positive', True)]
        with self.assertRaises(ValueError):
            paired(rows, rows[:1])

    def test_duplicate_ids_are_not_paired_observations(self):
        rows = [row('p', 'correct', True)]
        with self.assertRaises(ValueError):
            paired(rows+rows, rows)
        with self.assertRaises(ValueError):
            paired(rows, rows+rows)

    def test_label_cannot_change_between_models(self):
        with self.assertRaises(ValueError):
            paired([row('p', 'correct', True)], [row('p', 'false_positive', True)])

    def test_pareto_preserves_sensitivity_rejection_tradeoffs_and_ties(self):
        def point(positive, negative):
            return {'litert': {'positives_recognized': positive,
                               'false_contexts_activated': negative}}
        candidates = {'baseline': point(11, 13), 'strong': point(10, 7),
                      'strong-tie': point(10, 7), 'aggressive': point(8, 6),
                      'dominated': point(10, 10)}
        self.assertEqual(set(pareto(candidates)),
                         {'baseline', 'strong', 'strong-tie', 'aggressive'})


def detector(outputs, threshold=.5, release_below=.3):
    """Exercise the actual adapter's process/reset with a controlled NN stream."""
    class Frontend:
        def process_samples(self, raw):
            return SimpleNamespace(samples_read=len(raw)//2, features=[0]*40)

    class Interpreter:
        def __init__(self, **unused):
            self.index = 0

        def allocate_tensors(self):
            pass

        def get_input_details(self):
            return [{'dtype': np.int8, 'shape': np.asarray([1, 3, 40]),
                     'index': 0, 'quantization': (1., -128)}]

        def get_output_details(self):
            return [{'dtype': np.uint8, 'index': 1, 'quantization': (1/256, 0)}]

        def set_tensor(self, *unused):
            pass

        def invoke(self):
            self.index += 1

        def get_tensor(self, unused):
            return np.asarray([[outputs[min(self.index-1, len(outputs)-1)]]], dtype=np.uint8)

    result = MicroWakeWordDetector.__new__(MicroWakeWordDetector)
    result._frontend_class = Frontend
    result._interpreter_class = Interpreter
    result._model_content = b'controlled-test-model'
    result.threshold = threshold
    result.average_window = 5
    result.release_below = release_below
    result.reset()
    return result


def replay(adapter, frames):
    events = []
    for _ in range(frames*3):
        events.extend(adapter.process(np.zeros(160, dtype=np.int16)))
    return events


class StreamingRulesTests(unittest.TestCase):
    def test_one_peak_is_not_a_five_score_detection(self):
        self.assertEqual(replay(detector([0]*5+[255]+[0]*10), 16), [])

    def test_latch_survives_dip_above_release(self):
        # A second high plateau must not retrigger before crossing release.
        outputs = [255]*40+[102]*8+[255]*40
        events = replay(detector(outputs), len(outputs))
        self.assertEqual(len(events), 1)

    def test_release_and_refractory_both_apply(self):
        outputs = [255]*5+[0]*8+[255]*50
        events = replay(detector(outputs), len(outputs))
        self.assertEqual(len(events), 2)
        self.assertGreaterEqual(events[1]['timestamp']-events[0]['timestamp'], 1.)

    def test_stream_and_arbitrary_pcm_chunking_agree(self):
        outputs = [255]*5+[0]*8+[255]*50
        expected = replay(detector(outputs), len(outputs))
        candidate = detector(outputs)
        audio = np.zeros(len(outputs)*480, dtype=np.int16)
        actual = []
        for start in range(0, len(audio), 337):
            actual.extend(candidate.process(audio[start:start+337]))
        self.assertEqual(actual, expected)

    def test_reset_clears_network_frontend_gate_and_partial_pcm(self):
        outputs = [255]*5+[0]*8+[255]*50
        clean = replay(detector(outputs), len(outputs))
        dirty = detector(outputs)
        replay(dirty, 22)
        dirty.process(np.ones(97, dtype=np.int16))
        for _ in range(2):
            dirty.reset()
            self.assertEqual(replay(dirty, len(outputs)), clean)

    def test_default_half_threshold_equals_explicit_point_three_release(self):
        outputs = [255]*5+[102]*8+[0]*8+[255]*50
        self.assertEqual(replay(detector(outputs, release_below=None), len(outputs)),
                         replay(detector(outputs, release_below=.3), len(outputs)))

    def test_lower_threshold_can_change_real_stream_decision(self):
        outputs = [115]*10
        self.assertEqual(replay(detector(outputs, threshold=.5), len(outputs)), [])
        self.assertEqual(len(replay(detector(outputs, threshold=.4), len(outputs))), 1)


if __name__ == '__main__':
    unittest.main()
