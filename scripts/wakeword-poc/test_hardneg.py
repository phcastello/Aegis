#!/usr/bin/env python3
"""Safety/integrity checks using generated audio, never private recordings."""
import hashlib
import io
import stat
import unittest
import wave
import zipfile
import numpy as np
from hardneg_audit import validate_archive, wav_stats, ncc_match

class AuditTests(unittest.TestCase):
    def archive(self,names):
        buf=io.BytesIO()
        with zipfile.ZipFile(buf,'w') as z:
            for name in names:z.writestr(name,b'x')
        buf.seek(0)
        return zipfile.ZipFile(buf)
    def test_safe_members(self):
        with self.archive(['dataset.jsonl','events/day/id/trigger_context.wav']) as z:
            self.assertEqual(len(validate_archive(z)),2)
    def test_unsafe_paths(self):
        for name in ('../voice.wav','/voice.wav','events/../voice.wav','C:/voice.wav','a\\b.wav','a//b.wav','a/./b.wav'):
            with self.subTest(name=name),self.archive([name]) as z,self.assertRaises(ValueError):validate_archive(z)
    def test_unicode_duplicate(self):
        with self.archive(['café.wav','cafe\u0301.wav']) as z,self.assertRaises(ValueError):validate_archive(z)
    def test_symlink(self):
        info=zipfile.ZipInfo('events/link.wav');info.external_attr=(stat.S_IFLNK|0o777)<<16
        with self.archive([info]) as z,self.assertRaises(ValueError):validate_archive(z)
    def test_case_duplicate(self):
        with self.archive(['Voice.wav','voice.wav']) as z,self.assertRaises(ValueError):validate_archive(z)
    def test_embedded_nul(self):
        # Preserve an actual NUL in serialized headers; ZipInfo otherwise strips
        # the suffix before writing and would not exercise the reader boundary.
        buf=io.BytesIO()
        with zipfile.ZipFile(buf,'w') as z:z.writestr('voice.wav',b'x')
        raw=buf.getvalue().replace(b'voice.wav',b'voice\x00wav')
        with zipfile.ZipFile(io.BytesIO(raw)) as z,self.assertRaises(ValueError):validate_archive(z)
    def test_special_file(self):
        info=zipfile.ZipInfo('events/fifo');info.external_attr=(stat.S_IFIFO|0o600)<<16
        with self.archive([info]) as z,self.assertRaises(ValueError):validate_archive(z)
    def test_crc_corruption(self):
        buf=io.BytesIO()
        with zipfile.ZipFile(buf,'w',compression=zipfile.ZIP_STORED) as z:z.writestr('voice.wav',b'x')
        raw=bytearray(buf.getvalue());raw[30+len('voice.wav')]=ord('y')
        with zipfile.ZipFile(io.BytesIO(raw)) as z,self.assertRaises(ValueError):validate_archive(z)
    def test_wave_hash_format_and_length(self):
        buf=io.BytesIO()
        with wave.open(buf,'wb') as w:
            w.setnchannels(1);w.setsampwidth(2);w.setframerate(16000);w.writeframes(np.arange(16000,dtype='<i2').tobytes())
        raw=buf.getvalue();d={'sha256':hashlib.sha256(raw).hexdigest(),'samples':16000,'durationMs':1000,'sampleRate':16000,'channels':1,'encoding':'PCM16LE'}
        stats,x=wav_stats(raw,d);self.assertEqual(stats['samples'],16000)
        with self.assertRaises(ValueError):wav_stats(raw,{**d,'sha256':'0'*64})
        with self.assertRaises(ValueError):wav_stats(raw,{**d,'samples':15999})
        with self.assertRaises(ValueError):wav_stats(raw[:-10],d)
    def test_similarity_signal_and_silence(self):
        x=np.random.default_rng(3).integers(-2000,2000,32000,dtype=np.int16)
        y=np.r_[np.zeros(800,dtype=np.int16),x//2,np.zeros(1600,dtype=np.int16)]
        self.assertGreater(ncc_match(x,y)['normalized_correlation'],.99)
        self.assertIsNone(ncc_match(np.zeros(32000,dtype=np.int16),np.zeros(32000,dtype=np.int16)))
        self.assertIsNone(ncc_match(np.full(32000,400,dtype=np.int16),np.full(32000,800,dtype=np.int16)))
        self.assertIsNone(ncc_match(x//200,x//200))
        z=np.random.default_rng(4).integers(-2000,2000,32000,dtype=np.int16)
        self.assertIsNone(ncc_match(x,z))
if __name__=='__main__':unittest.main()
