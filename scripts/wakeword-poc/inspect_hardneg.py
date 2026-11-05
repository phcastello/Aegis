#!/usr/bin/env python3
"""Local automated lexical inspection; never a human phonetic alignment.

Only the predeclared training-period false-positive trigger contexts and all
human-labelled correct events are read. Other false-positive sessions stay
unopened. Correct post-activation clips are decoded separately for context,
without concatenation or assuming capture continuity. No new training labels
are created. Transcripts and word times remain private, outside Git.
"""
import argparse
from datetime import datetime
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import re
import time
import unicodedata
import wave

from prepare import private_output, sha256


BEGIN = datetime.fromisoformat('2026-11-03T13:00:00-03:00')
END = datetime.fromisoformat('2026-11-03T19:00:00-03:00')
LIMITATION = (
    'Automated Whisper small lexical screen only. ASR may omit, hallucinate or '
    'substitute Aegis and cannot establish word absence or reliable phonetic '
    'boundaries. Human labels remain event-level. All 15 correct events remain '
    'excluded from training because no human-verified wake-word alignment is '
    'available. A negative lexical screen does not certify every audio interval '
    'as negative. Post-activation audio is not used for negative training.'
)


def wake_tokens(text):
    normalized = ''.join(c for c in unicodedata.normalize('NFKD', text.lower())
                         if not unicodedata.combining(c))
    # Broad screening forms, fixed before inference; matches are exclusions,
    # not claimed transcripts or corrections to the owner's classifications.
    return [token for token in re.findall(r'[a-z]+', normalized)
            if token.startswith(('aeg', 'aej', 'egis', 'eges', 'egid'))
            or token in {'egis', 'ages', 'edges', 'ajis', 'ejis', 'aigis'}]


def selected(row):
    return row['humanLabel'] == 'correct' or (
        row['humanLabel'] == 'false_positive'
        and BEGIN <= datetime.fromisoformat(row['activatedAt']) < END)


def inspect_clip(model, root, metadata):
    import numpy as np
    path = (root / metadata['path']).resolve()
    if not path.is_relative_to(root) or sha256(path) != metadata['sha256']:
        raise ValueError('Unsafe or changed inspection WAV')
    with wave.open(str(path), 'rb') as wav:
        if (wav.getframerate(), wav.getnchannels(), wav.getsampwidth(), wav.getcomptype()) != (16000, 1, 2, 'NONE'):
            raise ValueError('Expected mono 16kHz PCM16')
        samples = np.frombuffer(wav.readframes(wav.getnframes()), dtype='<i2')
    segments, _ = model.transcribe(
        samples.astype(np.float32) / 32768, language='pt', beam_size=5,
        word_timestamps=True, condition_on_previous_text=False,
        vad_filter=False, temperature=0,
    )
    rows = []
    for segment in segments:
        rows.append({
            'start': segment.start, 'end': segment.end, 'text': segment.text,
            'avg_logprob': segment.avg_logprob,
            'no_speech_prob': segment.no_speech_prob,
            'words': [{'start': word.start, 'end': word.end, 'word': word.word,
                       'probability': word.probability}
                      for word in segment.words or []],
        })
    text = ''.join(row['text'] for row in rows)
    return {'path': metadata['path'], 'sha256': metadata['sha256'],
            'duration_seconds': len(samples) / 16000,
            'segments': rows, 'screen_matches': wake_tokens(text)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dataset', type=private_output, required=True)
    parser.add_argument('--model', type=private_output, required=True)
    parser.add_argument('--output', type=private_output, required=True)
    args = parser.parse_args()
    os.umask(0o077)
    os.environ['HF_HUB_OFFLINE'] = '1'
    os.environ['TRANSFORMERS_OFFLINE'] = '1'
    args.output.mkdir(mode=0o700, parents=True, exist_ok=False)
    source = args.dataset / 'dataset.jsonl'
    rows = [json.loads(line) for line in source.read_text().splitlines()]
    chosen = sorted((row for row in rows if selected(row)),
                    key=lambda row: (row['activatedAt'], row['id']))
    if len(chosen) != 72 or sum(row['humanLabel'] == 'correct' for row in chosen) != 15:
        raise ValueError('Unexpected predeclared inspection subset')
    report = {
        'schema': 1, 'limitation': LIMITATION,
        'dataset_jsonl_sha256': sha256(source),
        'selection': {'false_positive_begin': BEGIN.isoformat(),
                      'false_positive_end_exclusive': END.isoformat(),
                      'correct': 'all 15; inspection only; never training'},
        'selected_events': [{'id': row['id'], 'humanLabel': row['humanLabel']}
                            for row in chosen],
        'model': {'path': str(args.model),
                  'files_sha256': {str(p.relative_to(args.model)): sha256(p)
                                   for p in sorted(args.model.rglob('*')) if p.is_file()}},
        'versions': {name: importlib.metadata.version(name)
                     for name in ('faster-whisper', 'ctranslate2', 'numpy')},
        'settings': {'device': 'cpu', 'compute_type': 'int8', 'cpu_threads': 2,
                     'language': 'pt', 'beam_size': 5, 'word_timestamps': True,
                     'condition_on_previous_text': False, 'vad_filter': False,
                     'temperature': 0, 'local_files_only': True},
        'script_sha256': sha256(Path(__file__)),
        'events': [], 'complete': False,
    }
    def persist():
        temporary = args.output / 'report.partial.json'
        temporary.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
        temporary.replace(args.output / 'report.json')
    persist()
    from faster_whisper import WhisperModel
    model = WhisperModel(str(args.model), device='cpu', compute_type='int8',
                         cpu_threads=2, local_files_only=True)
    started = time.monotonic()
    for index, row in enumerate(chosen):
        result = {'id': row['id'], 'humanLabel': row['humanLabel'],
                  'trigger_context': inspect_clip(model, args.dataset, row['triggerContext'])}
        result['possible_wake_token'] = bool(result['trigger_context']['screen_matches'])
        if row['humanLabel'] == 'correct':
            result['post_activation'] = inspect_clip(model, args.dataset, row['postActivation'])
            result['positive_training_eligible'] = False
        report['events'].append(result)
        persist()
        print(f'Inspected {index + 1}/{len(chosen)} events locally', flush=True)
    report['elapsed_seconds'] = time.monotonic() - started
    report['complete'] = True
    report['false_positive_flagged'] = sum(
        event['humanLabel'] == 'false_positive' and event['possible_wake_token']
        for event in report['events'])
    persist()
    print(f'Inspection complete: {len(chosen)} events; {report["false_positive_flagged"]} training-period FP flagged; no transcripts on stdout', flush=True)


if __name__ == '__main__':
    main()
