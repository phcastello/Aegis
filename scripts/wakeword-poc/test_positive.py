#!/usr/bin/env python3
"""Training guards: bounded source reuse and exact frozen temporal roles."""
import json
from pathlib import Path
import unittest
import numpy as np
from train_positive import draw_events

class SamplingTests(unittest.TestCase):
    def test_singleton_group_cannot_dominate(self):
        meta=[{'session':'one','id':'singleton'}]+[{'session':'many','id':f'e{i}'} for i in range(20) for _ in range(6)]
        choices,counts,cap=draw_events(meta,256,np.random.default_rng(20261105))
        self.assertEqual(len(choices),256)
        self.assertLessEqual(max(counts.values()),cap)
        self.assertEqual(sum(counts.values()),256)
        self.assertLess(counts['singleton'],256//4)
        self.assertTrue(all(0<=i<len(meta) for i in choices))
    def test_derivatives_do_not_increase_event_capacity(self):
        meta=[{'session':'g','id':'a'}]*100+[{'session':'g','id':'b'}]
        _,counts,cap=draw_events(meta,64,np.random.default_rng(4))
        self.assertEqual(cap,64)
        self.assertLess(abs(counts['a']-counts['b']),24)
    def test_deterministic_source_draws(self):
        meta=[{'session':f'g{i//3}','id':f'e{i}'} for i in range(12)]
        a=draw_events(meta,128,np.random.default_rng(5));b=draw_events(meta,128,np.random.default_rng(5))
        self.assertEqual(a,b)
    def test_frozen_private_manifest_when_present(self):
        root=Path.home()/'.cache/aegis-wakeword-positive-retraining'
        if not (root/'protocol-v1.json').exists():self.skipTest('No private protocol installed')
        p=json.loads((root/'protocol-v1.json').read_text())
        old=json.loads((Path.home()/'.cache/aegis-wakeword-hardneg/protocol-v3.json').read_text())
        byid={r['id']:r for r in p['events']}
        self.assertEqual(len(byid),260)
        for r in old['events']:
            for k,v in r.items():self.assertEqual(byid[r['id']][k],v)
        self.assertEqual(sum(p['batch_sources'].values()),64)
        self.assertEqual(sum(p['batch_sources'][k] for k in ('synthetic_positive','reference_positive','real_positive')),32)
        self.assertEqual(sum(r['origin']=='new99' and r['split']=='buffer' for r in byid.values()),10)
if __name__=='__main__':unittest.main()
