#!/usr/bin/env python3
"""Product-policy and event-exposure regressions for the bounded recovery fit."""
import unittest
import numpy as np
from train_direct_training import cover_events
from train_tradeoff import paired_utility, PLAN


def evaluation(rows):
    return {'runtimes': {'litert': {'contexts': {'events': rows}}}}


def event(name, positive, detected=False, near=None):
    return {'id': name, 'humanLabel': 'correct' if positive else 'false_positive',
            'detected_any': detected,
            'detected_near_decision': detected if near is None else near}


class PolicyTests(unittest.TestCase):
    def test_six_false_activations_pay_for_one_lost_call(self):
        b = [event('p', True, True)]+[event(str(i), False, True) for i in range(6)]
        c = [event('p', True)]+[event(str(i), False) for i in range(6)]
        result = paired_utility(evaluation(b), evaluation(c), 'litert')
        self.assertEqual(result['utility'], 2)
        self.assertEqual(result['paired_counts']['positives_lost'], 1)
        self.assertEqual(result['paired_counts']['fp_eliminated'], 6)

    def test_false_contexts_count_events_outside_original_decision(self):
        b = [event('n', False), event('p', True, False)]
        c = [event('n', False, True, near=False), event('p', True, True, near=True)]
        result = paired_utility(evaluation(b), evaluation(c), 'litert')
        self.assertEqual(result['utility'], 3)
        self.assertEqual(result['paired_counts']['fp_new'], 1)
        self.assertEqual(result['paired_counts']['positives_recovered'], 1)

    def test_all_silent_model_is_penalized(self):
        b = [event(f'p{i}', True, True) for i in range(11)]+[event(f'n{i}', False, True) for i in range(13)]
        c = [dict(r, detected_any=False, detected_near_decision=False) for r in b]
        self.assertEqual(paired_utility(evaluation(b), evaluation(c), 'litert')['utility'], -31)

    def test_mismatched_validation_aborts(self):
        with self.assertRaises(ValueError):
            paired_utility(evaluation([event('a', True)]), evaluation([event('b', True)]), 'litert')

    def test_duplicate_ids_unsupported_label_or_changed_split_abort(self):
        valid = event('p', True)
        for before, after in [
            ([valid, valid], [valid]),
            ([valid], [valid, valid]),
            ([dict(valid, humanLabel='unknown')], [dict(valid, humanLabel='unknown')]),
            ([dict(valid, split='validation')], [dict(valid, split='test')]),
        ]:
            with self.subTest(before=before, after=after):
                with self.assertRaises(ValueError):
                    paired_utility(evaluation(before), evaluation(after), 'litert')

    def test_all_originals_and_uniform_exposure_at_new_batch_ratio(self):
        rows = [{'id': f'p{i}'} for i in range(91) for _ in range(3)]
        rows += [{'id': f'n{i}'} for i in range(130) for _ in range(3)]
        labels = np.array([[1.]]*(91*3)+[[0.]]*(130*3))
        for name, label, total in [('real_positive', 1., 91), ('real_negative', 0., 130)]:
            budget = PLAN['batch_sources'][name]*PLAN['steps_per_epoch']
            indices, counts = cover_events(rows, labels, label, budget, np.random.default_rng(PLAN['seed']))
            self.assertEqual(len(counts), total)
            self.assertEqual(sum(counts.values()), budget)
            self.assertLessEqual(max(counts.values())-min(counts.values()), 1)
            self.assertTrue(np.all(labels[indices] == label))


if __name__ == '__main__':
    unittest.main()
