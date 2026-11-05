#!/usr/bin/env python3
"""Conservative local pre-decision positive inspection; all evidence stays private.

Human event labels, unprompted local ASR and acoustic margins jointly support
experimental inclusion. These are instrument-assisted boundaries, not verified
phonetic alignment or a substitute for listening. Ambiguous examples are kept
for conditional replay but excluded from fitting. No detector scores are read.
"""
import argparse
from collections import Counter
import importlib.metadata
import json
import os
from pathlib import Path
import re
import time
import unicodedata
import wave

import numpy as np
from prepare import private_output, sha256

RULES = {
    'revision': 2,
    'revision_note': 'ASR start=0 on single words is not a measured onset; acoustic onset with 40ms guard determines capture-edge eligibility.',
    'human_label': 'correct', 'collection_day': '2026-11-05',
    'word_probability_min': .30, 'max_no_speech_probability': .5,
    'min_keyword_seconds': .18, 'max_keyword_seconds': 1.15,
    'edge_margin_seconds': .04, 'activity_frame_seconds': .01,
    'activity_threshold_min_pcm': 60., 'activity_threshold_peak_fraction': .10,
    'activity_threshold_noise_multiplier': 2.5,
    'activity_gap_bridge_seconds': .08,
    'word_activity_min_overlap_seconds': .10,
    'keyword_forms': ['aegis', 'egis', 'eges', 'egues', 'edges', 'aigis',
                      'aeges', 'egys', 'eguis', 'eigis', 'ages', 'egues'],
    'cue_spectral_peak_fraction_min': .80,
    'cue_frequency_min_hz': 700., 'cue_frequency_max_hz': 5000.,
    'cue_min_seconds': .03,
    'confirmation_decode': 'speech-region second decode, same local model; corroboration only',
    'post_activation_used': False, 'trigger_post_overlap_removed_samples': 3200,
}


def normalize(word):
    text = ''.join(c for c in unicodedata.normalize('NFKD', word.lower())
                   if not unicodedata.combining(c))
    return ''.join(re.findall('[a-z]+', text))


def decode(model, samples):
    segments, _ = model.transcribe(samples.astype(np.float32) / 32768,
        language='pt', beam_size=5, word_timestamps=True,
        condition_on_previous_text=False, vad_filter=False, temperature=0)
    return [{'start': float(s.start), 'end': float(s.end), 'text': s.text,
             'avg_logprob': float(s.avg_logprob),
             'no_speech_prob': float(s.no_speech_prob),
             'words': [{'start': float(w.start), 'end': float(w.end),
                        'word': w.word, 'probability': float(w.probability)}
                       for w in s.words or []]} for s in segments]


def candidates(segments):
    return [dict(w, segment_no_speech_prob=s['no_speech_prob'])
            for s in segments for w in s['words']
            if normalize(w['word']) in RULES['keyword_forms']]


def acoustics(samples):
    frames = np.pad(samples, (0, (-len(samples)) % 160)).reshape(-1, 160).astype(float)
    rms = np.sqrt(np.mean(frames ** 2, axis=1))
    threshold = max(RULES['activity_threshold_min_pcm'],
                    float(np.percentile(rms, 15)) * RULES['activity_threshold_noise_multiplier'],
                    float(rms.max()) * RULES['activity_threshold_peak_fraction'])
    active = rms >= threshold
    # Bridge only short interior pauses, never make capture-edge silence.
    indexes = np.flatnonzero(active)
    for a, b in zip(indexes, indexes[1:]):
        if b-a <= round(RULES['activity_gap_bridge_seconds']*100)+1:
            active[a:b+1] = True
    edges = np.diff(np.r_[False, active, False].astype(int))
    starts, ends = np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)
    spans = [{'start': int(a)*.01, 'end': min(int(b)*.01, len(samples)/16000),
              'rms': float(np.sqrt(np.mean(samples[a*160:min(b*160,len(samples))].astype(float)**2)))}
             for a,b in zip(starts,ends)]
    # Detect a strong nearly pure tone, a cue candidate, using 30ms windows.
    tones = []
    for start in range(0, len(samples)-479, 160):
        frame = samples[start:start+480].astype(float)
        if np.sqrt(np.mean(frame**2)) < max(100., threshold):
            continue
        power = np.abs(np.fft.rfft((frame-frame.mean())*np.hanning(480)))**2
        peak = int(np.argmax(power)); lo=max(0,peak-1);hi=min(len(power),peak+2)
        fraction = float(power[lo:hi].sum()/max(power.sum(),1e-20))
        frequency = peak*16000/480
        if fraction >= RULES['cue_spectral_peak_fraction_min'] and RULES['cue_frequency_min_hz'] <= frequency <= RULES['cue_frequency_max_hz']:
            tones.append({'start':start/16000,'end':(start+480)/16000,
                          'frequency_hz':frequency,'spectral_peak_fraction':fraction})
    return {'threshold_pcm_rms':threshold,'rms_10ms':rms.tolist(),
            'active_spans':spans,'possible_cue_frames':tones,
            'rms_peak':float(rms.max()),'rms_noise_percentile15':float(np.percentile(rms,15))}


def inspect(model, event, root, prior=None):
    metadata=event['triggerContext'];path=(root/metadata['path']).resolve()
    if not path.is_relative_to(root) or sha256(path)!=metadata['sha256']:
        raise ValueError('Changed or unsafe positive input')
    with wave.open(str(path),'rb') as source:
        if (source.getframerate(),source.getnchannels(),source.getsampwidth(),source.getcomptype())!=(16000,1,2,'NONE'):
            raise ValueError('Unexpected positive format')
        full=np.frombuffer(source.readframes(source.getnframes()),dtype='<i2').copy()
    if len(full)!=metadata['samples'] or len(full)<=3200:
        raise ValueError('Incomplete context')
    samples=full[:-3200]  # Never allow the trigger's 200ms post-decision margin.
    duration=len(samples)/16000
    acoustic=acoustics(samples)
    if prior is not None:
        if prior['sha256']!=metadata['sha256'] or prior['decision_sample']!=len(samples):
            raise ValueError('Cached first-pass ASR input changed')
        first=prior['asr']
    else:
        first=decode(model,samples)
    words=candidates(first)
    result={'id':event['id'],'humanLabel':event['humanLabel'],
            'path':str(path),'sha256':metadata['sha256'],
            'decision_sample':len(samples),'predecision_seconds':duration,
            'ended_at_null':event['endedAt'] is None,'asr':first,
            'acoustics':acoustic,'eligible':False,'reasons':[],
            'boundary_type':'instrument-assisted, not human phonetic alignment'}
    reasons=result['reasons']
    if not words:
        reasons.append('no-explicit-wake-token-in-unprompted-local-asr')
        return result,samples
    if len(words)!=1:
        reasons.append('ambiguous-multiple-wake-token-candidates')
        return result,samples
    word=words[0];result['asr_word']=word
    if word['probability']<RULES['word_probability_min'] or word['segment_no_speech_prob']>RULES['max_no_speech_probability']:
        reasons.append('low-confidence-lexical-evidence')
    spans=[s for s in acoustic['active_spans'] if min(s['end'],word['end'])-max(s['start'],word['start'])>=RULES['word_activity_min_overlap_seconds']]
    if len(spans)!=1:
        reasons.append('ambiguous-or-absent-acoustic-word-region')
        return result,samples
    span=spans[0]
    # Include the whole energetic speech component containing the ASR word,
    # so an ASR timestamp cannot crop the onset or trailing fricative.
    begin=max(0.,span['start']-.04)
    end=max(min(duration,span['end']+.02),word['end'])
    if not RULES['min_keyword_seconds']<=end-begin<=RULES['max_keyword_seconds']:
        reasons.append('implausible-or-multiword-acoustic-region-duration')
    if begin<RULES['edge_margin_seconds']:
        reasons.append('possible-truncated-onset-or-insufficient-leading-margin')
    if duration-end<RULES['edge_margin_seconds']:
        reasons.append('possible-truncated-offset-or-insufficient-predecision-tail')
    if acoustic['possible_cue_frames']:
        reasons.append('possible-activation-cue-in-predecision-context')
    crop_start=max(0,round((begin-.04)*16000));crop_end=min(len(samples),round((end+.04)*16000))
    second=decode(model,samples[crop_start:crop_end]);result['confirmation_asr']=second
    confirmation=candidates(second)
    if len(confirmation)!=1:
        reasons.append('wake-token-not-corroborated-in-local-region-decode')
    elif confirmation[0]['probability']<RULES['word_probability_min']:
        reasons.append('low-confidence-region-corroboration')
    result['keyword_start_seconds']=begin
    result['keyword_end_seconds']=end
    result['keyword_start_sample']=round(begin*16000)
    result['keyword_end_sample']=round(end*16000)
    result['training_crop_start_sample']=max(0,round((begin-.04)*16000))
    result['training_crop_end_sample']=min(len(samples),round((end+.04)*16000))
    result['eligible']=not reasons
    return result,samples


def plot_case(path,row,samples):
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    fig,axes=plt.subplots(3,1,figsize=(9,7),sharex=True)
    axes[0].plot(np.arange(len(samples))/16000,samples,lw=.4)
    axes[0].set_ylabel('PCM16')
    axes[1].plot(np.arange(len(row['acoustics']['rms_10ms']))*.01,row['acoustics']['rms_10ms'])
    axes[1].axhline(row['acoustics']['threshold_pcm_rms'],color='r',lw=.6)
    axes[1].set_ylabel('10ms RMS')
    axes[2].specgram(samples,NFFT=320,Fs=16000,noverlap=240,cmap='magma')
    axes[2].set_ylim(0,6000);axes[2].set_ylabel('Hz');axes[2].set_xlabel('Pre-decision seconds')
    for axis in axes:
        if 'keyword_start_seconds' in row:
            axis.axvspan(row['keyword_start_seconds'],row['keyword_end_seconds'],alpha=.15,color='cyan')
    fig.suptitle('Private acoustic inspection; '+('eligible' if row['eligible'] else ', '.join(row['reasons'])))
    fig.tight_layout();fig.savefig(path,dpi=130);plt.close(fig)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dataset',type=private_output,required=True)
    parser.add_argument('--model',type=private_output,required=True)
    parser.add_argument('--output',type=private_output,required=True)
    parser.add_argument('--prior-report',type=private_output,
                        help='Reuse completed unmodified first-pass ASR from an interrupted inspection; decisions are recomputed.')
    args=parser.parse_args();os.umask(0o077)
    os.environ['HF_HUB_OFFLINE']='1';os.environ['TRANSFORMERS_OFFLINE']='1'
    args.output.mkdir(parents=True,mode=0o700,exist_ok=False)
    source=args.dataset/'dataset.jsonl'
    events=sorted([json.loads(line) for line in source.read_text().splitlines()],key=lambda r:r['activatedAt'])
    chosen=[e for e in events if e['humanLabel']=='correct' and e['activatedAt'].startswith(RULES['collection_day'])]
    if len(chosen)!=92:raise ValueError('Expected exactly 92 new human-labelled correct events')
    report={'schema':1,'rules':RULES,'dataset_jsonl_sha256':sha256(source),
            'script_sha256':sha256(Path(__file__)),
            'model_files_sha256':{p.name:sha256(p) for p in args.model.iterdir() if p.is_file()},
            'versions':{n:importlib.metadata.version(n) for n in ('faster-whisper','ctranslate2','numpy')},
            'local_only':True,'complete':False,'events':[],
            'limitations':['No listening or human phonetic alignment performed.',
                'ASR can omit, substitute or hallucinate names; explicit matches plus acoustic margins support conservative experimental inclusion only.',
                'Second decode reuses the same recognizer and is correlated corroboration.',
                'Energy boundaries can miss low-energy fricatives or merge background speech; ambiguous and boundary-touching regions are excluded.',
                'Spectral cue screening is heuristic; no post-decision samples enter inspection or training.']}
    def persist():
        temporary=args.output/'report.partial.json'
        temporary.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n')
        temporary.replace(args.output/'report.json')
    persist()
    cache={}
    if args.prior_report:
        old=json.loads(args.prior_report.read_text())
        if old['dataset_jsonl_sha256']!=sha256(source):
            raise ValueError('Cached first-pass ASR dataset changed')
        cache={row['id']:row for row in old['events']}
        report['prior_first_pass_report_sha256']=sha256(args.prior_report)
    from faster_whisper import WhisperModel
    model=WhisperModel(str(args.model),device='cpu',compute_type='int8',cpu_threads=2,local_files_only=True)
    start=time.monotonic();plots=Counter()
    for index,event in enumerate(chosen):
        row,samples=inspect(model,event,args.dataset,cache.get(event['id']))
        report['events'].append(row)
        category='short' if row['predecision_seconds']<1 else ('eligible' if row['eligible'] else 'excluded')
        if plots[category]<3:
            plot_case(args.output/f'{category}-{plots[category]+1}.png',row,samples);plots[category]+=1
        persist()
        print(f'Inspected {index+1}/{len(chosen)} positive pre-decision contexts locally',flush=True)
    report['complete']=True;report['elapsed_seconds']=time.monotonic()-start
    report['summary']={'events':len(chosen),'eligible':sum(e['eligible'] for e in report['events']),
                       'excluded':sum(not e['eligible'] for e in report['events']),
                       'exclusion_reasons':dict(Counter(reason for e in report['events'] for reason in e['reasons'])),
                       'lexical_candidates':sum('asr_word' in e for e in report['events']),
                       'under_one_second':sum(e['predecision_seconds']<1 for e in report['events'])}
    persist();print(json.dumps(report['summary'],indent=2),flush=True)


if __name__=='__main__':main()
