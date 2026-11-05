#!/usr/bin/env python3
"""Integrity and all-event inclusion tests without private owner recordings."""
from collections import Counter
import csv
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock
import wave
import zipfile

import numpy as np
import prepare_direct_training as direct


def wav_bytes(samples):
    stream=io.BytesIO()
    with wave.open(stream,'wb') as out:
        out.setparams((1,2,16000,len(samples),'NONE','not compressed'))
        out.writeframes(np.asarray(samples,dtype='<i2').tobytes())
    return stream.getvalue()


def metadata(raw,samples,path):
    return {'path':path,'sha256':hashlib.sha256(raw).hexdigest(),'samples':len(samples),
            'sampleRate':16000,'channels':1,'encoding':'PCM16LE','durationMs':len(samples)/16}


class DirectPreparationTests(unittest.TestCase):
    def owner_rows(self):
        result=[]
        for split,labels in direct.EXPECTED.items():
            for label,count in labels.items():
                for i in range(count):
                    eid=f'{split}-{label}-{i}'
                    result.append({'id':eid,'split':split,'humanLabel':label,'sessionId':'same-session',
                                   'triggerContext':{'path':eid+'/trigger_context.wav'},
                                   'postActivation':{'path':eid+'/post_activation.wav'}})
        return result

    def test_exact_supplied_roles_all221_included(self):
        rows=self.owner_rows();before=json.dumps(rows,sort_keys=True)
        self.assertEqual(direct.check_rows(rows),direct.EXPECTED)
        self.assertEqual(before,json.dumps(rows,sort_keys=True))
        self.assertEqual(sum(r['split']=='train' for r in rows),221)
        # Shared sessions spanning splits are not a reason to redistribute rows.
        self.assertEqual({r['sessionId'] for r in rows},{'same-session'})

    def test_wrong_split_counts_or_duplicate_id_abort(self):
        rows=self.owner_rows();rows[0]['split']='test'
        with self.assertRaises(ValueError):direct.check_rows(rows)
        rows=self.owner_rows();rows[1]['id']=rows[0]['id']
        with self.assertRaises(ValueError):direct.check_rows(rows)

    def test_corrupted_pcm_aborts_instead_of_exclusion(self):
        samples=np.arange(250,dtype=np.int16);raw=wav_bytes(samples)
        record=metadata(raw,samples,'sample.wav')
        np.testing.assert_array_equal(direct.read_pcm(raw,record),samples)
        with self.assertRaises(ValueError):direct.read_pcm(raw[:-2],record)
        truncated=raw[:-2];record['sha256']=hashlib.sha256(truncated).hexdigest()
        with self.assertRaises(ValueError):direct.read_pcm(truncated,record)

    def test_exact_overlap_and_arbitrarily_short_context(self):
        post=np.arange(4000,dtype=np.int16)
        trigger=np.r_[np.array([11],np.int16),post[:3200]]
        self.assertEqual(direct.check_pair(trigger,post),1)
        self.assertEqual(direct.check_pair(post[:3200],post),0)
        trigger[-1]+=1
        with self.assertRaises(ValueError):direct.check_pair(trigger,post)

    def test_feature_frontend_never_sees_postdecision_audio_and_pads_short(self):
        seen=[]
        def fake_features(samples):
            seen.append(samples.copy())
            return np.empty((0,40),np.float32)
        with mock.patch.object(direct,'features',fake_features):
            result=direct.make_real_window(np.array([11,-7],np.int16),1.)
        self.assertEqual(result.shape,(186,40))
        self.assertFalse(np.any(result))
        np.testing.assert_array_equal(seen[0],np.array([11,-7],np.int16))

    def test_sums_and_csv_split_mismatch_abort(self):
        body=b'original';stream=io.BytesIO()
        with zipfile.ZipFile(stream,'w') as z:
            z.writestr('dataset.jsonl',body)
            z.writestr('SHA256SUMS.txt',hashlib.sha256(body).hexdigest()+'  dataset.jsonl\n')
        stream.seek(0)
        with zipfile.ZipFile(stream) as z:
            self.assertTrue(direct.check_declared_sums(z)[1])
        rows=[{'id':'one','humanLabel':'correct','split':'train'}]
        direct.check_csv(rows,b'id,humanLabel,split\none,correct,train\n')
        with self.assertRaises(ValueError):
            direct.check_csv(rows,b'id,humanLabel,split\none,correct,test\n')

    def test_complete_technical_audit_accepts_all_silent_duplicates_and_shared_session(self):
        rows=self.owner_rows()
        pcm=np.zeros(3201,np.int16);post=np.zeros(3200,np.int16)
        trigger_raw=wav_bytes(pcm);post_raw=wav_bytes(post)
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);archive_path=root/'owner-split.zip'
            bodies={}
            for row in rows:
                for kind,raw,samples in (('triggerContext',trigger_raw,pcm),('postActivation',post_raw,post)):
                    path=row[kind]['path'];bodies[path]=raw;row[kind]=metadata(raw,samples,path)
            bodies['dataset.jsonl']=('\n'.join(json.dumps(r) for r in rows)+'\n').encode()
            stream=io.StringIO();writer=csv.DictWriter(stream,fieldnames=['id','humanLabel','split'])
            writer.writeheader()
            for row in rows:writer.writerow({k:row[k] for k in writer.fieldnames})
            bodies['dataset.csv']=stream.getvalue().encode()
            bodies['SHA256SUMS.txt']=''.join(hashlib.sha256(body).hexdigest()+'  '+name+'\n' for name,body in bodies.items()).encode()
            with zipfile.ZipFile(archive_path,'w',compression=zipfile.ZIP_DEFLATED) as z:
                for name,body in bodies.items():z.writestr(name,body)
            inventory,summary=direct.technical_audit(archive_path,root/'audit')
            self.assertEqual(summary['training_events_included'],221)
            self.assertEqual(summary['excluded_training_events'],0)
            self.assertEqual(summary['cross_split_session_ids_count'],1)
            self.assertEqual(len(inventory['events']),260)
            self.assertTrue(all(e['audit']['decision_sample_in_context']==1 for e in inventory['events']))
            self.assertEqual(sum(e['audit']['training_included'] for e in inventory['events']),221)
            self.assertEqual((root/'audit/dataset'/rows[0]['triggerContext']['path']).read_bytes(),trigger_raw)
            # Replacing one body while retaining declared hashes aborts before extraction.
            bodies[rows[0]['triggerContext']['path']]=trigger_raw[:-2]
            corrupt_path=root/'corrupt.zip'
            with zipfile.ZipFile(corrupt_path,'w',compression=zipfile.ZIP_DEFLATED) as z:
                for name,body in bodies.items():z.writestr(name,body)
            with self.assertRaises(ValueError):direct.technical_audit(corrupt_path,root/'bad-audit')
            self.assertFalse((root/'bad-audit').exists())

    def test_every_owner_event_becomes_labelled_window_even_silent_short(self):
        rows=self.owner_rows()
        with tempfile.TemporaryDirectory() as temp:
            root=Path(temp);old=root/'old';old.mkdir();output=root/'features'
            # The source layout is fixed:6988 synthetic/prefix+960 refs+921DEV.
            oldy=np.zeros((8869,1),np.float32);oldy[:1032]=1;oldy[6988:7468]=1
            hashes={}
            for split,n in (('train',8869),('validation',1857)):
                for key in ('x','y'):
                    arr=np.zeros((n,1),np.float32) if key=='x' else oldy if split=='train' else np.zeros((n,1),np.float32)
                    np.save(old/f'{split}_{key}.npy',arr)
                    hashes[f'{split}_{key}.npy']=direct.sha256(old/f'{split}_{key}.npy')
            (old/'features.json').write_text(json.dumps({'array_sha256':hashes}))
            corpus=root/'corpus';(corpus/'comparison/training').mkdir(parents=True)
            (corpus/'comparison/training/manifest.json').write_text(json.dumps({'records':[{'split':'train'}]*3494}))
            raw=wav_bytes(np.zeros(3201,np.int16));decl=metadata(raw,np.zeros(3201,np.int16),'short.wav');wav=root/'short.wav';wav.write_bytes(raw)
            for r in rows:r['triggerContext']=dict(decl);r['audit']={'trigger_path':str(wav),'decision_sample_in_context':1}
            audit=root/'inventory.json';audit.write_text('{}');protocol=root/'protocol.json';protocol.write_text('{}')
            with mock.patch.object(direct,'OLD_FEATURES',old),mock.patch.object(direct,'ROOT',corpus),mock.patch.object(direct,'baseline_guards',return_value={}),mock.patch.object(direct,'features',return_value=np.empty((0,40),np.float32)):
                manifest=direct.prepare_feature_arrays({'events':rows},protocol,audit,output)
            self.assertEqual(manifest['training_original_events'],221)
            self.assertEqual(manifest['excluded_training_events'],0)
            self.assertEqual(len(manifest['real_windows']['train']),663)
            self.assertEqual(len({r['id'] for r in manifest['real_windows']['train']}),221)
            labels=np.load(output/'train_real_y.npy')[:,0]
            self.assertEqual(Counter(labels),{1.:273,0.:390})
            self.assertEqual(len(manifest['real_windows']['validation']),26)
            self.assertFalse((output/'test_real_x.npy').exists())


if __name__=='__main__':unittest.main()
