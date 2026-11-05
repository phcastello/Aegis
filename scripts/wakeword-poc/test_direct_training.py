#!/usr/bin/env python3
"""Coverage, labels and exact owner-split policy, without private training data."""
import argparse
import json
from pathlib import Path
import tempfile
import unittest
import numpy as np
from prepare import sha256
from train_direct_training import cover_events, PLAN
from evaluate_hardneg import contexts

class CoverageTests(unittest.TestCase):
    def test_all221_originals_before_class_repetition(self):
        rows=[];labels=[]
        for label,count in ((1.,91),(0.,130)):
            for i in range(count):
                for gain in (.85,1.,1.15):
                    rows.append({'id':f'{label}-{i}','gain':gain});labels.append([label])
        labels=np.asarray(labels,dtype=np.float32);rng=np.random.default_rng(5)
        covered=set()
        for label,n in ((1.,91),(0.,130)):
            indices,counts=cover_events(rows,labels,label,256,rng)
            self.assertEqual(len(counts),n)
            self.assertEqual(len({rows[i]['id'] for i in indices[:n]}),n)
            self.assertTrue(np.all(labels[indices]==label))
            self.assertLessEqual(max(counts.values())-min(counts.values()),1)
            covered.update(counts)
        self.assertEqual(len(covered),221)
    def test_no_silent_omission_when_budget_too_small(self):
        with self.assertRaises(ValueError):
            cover_events([{'id':str(i)} for i in range(10)],np.ones((10,1)),1.,9,np.random.default_rng(0))
    def test_class_balance(self):
        b=PLAN['batch_sources']
        self.assertEqual(sum(b.values()),64)
        self.assertEqual(b['synthetic_positive']+b['reference_positive']+b['real_positive'],32)

class SplitTests(unittest.TestCase):
    def run_contexts(self, root, provided, change_label=False):
        events=[{'id':str(i),'humanLabel':'correct','groupId':'shared'} for i in range(2)]
        audit=root/'audit.json';audit.write_text(json.dumps({'events':events}))
        roles=[{'id':str(i),'humanLabel':'false_positive' if change_label else 'correct','session':'shared','split':split} for i,split in enumerate(('train','test'))]
        manifest=root/'protocol.json';manifest.write_text(json.dumps({'events':roles,'audit_sha256':sha256(audit)}))
        a=argparse.Namespace(audit=audit,split_manifest=manifest,split='validation',provided_splits=provided)
        return contexts(a,None,'unit',root)
    def test_provided_split_reports_dependencies_without_exclusion(self):
        with tempfile.TemporaryDirectory() as tmp:
            result=self.run_contexts(Path(tmp),True)
            self.assertEqual(result['split_policy'],'exact owner-provided splits')
            self.assertEqual(result['cross_split_group_ids'],1)
            self.assertEqual(result['cross_split_session_ids'],1)
    def test_older_grouped_policy_preserved(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaises(ValueError):self.run_contexts(Path(tmp),False)
    def test_provided_split_does_not_allow_label_substitution(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(ValueError,'Human label changed'):self.run_contexts(Path(tmp),True,True)
if __name__=='__main__':unittest.main()
