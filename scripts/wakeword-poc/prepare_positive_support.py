#!/usr/bin/env python3
"""Combine owner labels with conservative acoustic completeness/template support.

This supplements, and preserves, the failed ASR/negative-separation screens.
DTW is not an independent word classifier: the owner's label supplies semantic
identity. A threshold derived solely from six reference LOO distances supports
acoustic compatibility. Known negative matches remain explicit limitations.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import wave
import numpy as np
from prepare import private_output, sha256


def save(path, value):
    with path.open('x') as stream:
        json.dump(value,stream,ensure_ascii=False,indent=2);stream.write('\n')


def plots(output, accepted, inventory):
    if not accepted:
        return []
    import matplotlib
    matplotlib.use('Agg')
    import matplotlib.pyplot as plt
    pages=[]
    for page in range(0,len(accepted),4):
        cases=accepted[page:page+4]
        fig,axes=plt.subplots(len(cases),2,figsize=(12,3.3*len(cases)),squeeze=False)
        for index,row in enumerate(cases):
            event=inventory[row['id']]
            path=Path(event['audit']['trigger_path'])
            if sha256(path)!=row['sha256']:
                raise ValueError('Changed reviewed TRAIN input')
            with wave.open(str(path),'rb') as source:
                pcm=np.frombuffer(source.readframes(source.getnframes()),dtype='<i2')[:row['decision_sample']]
            left,right=axes[index]
            left.plot(np.arange(len(pcm))/16000,pcm,lw=.5)
            left.set_ylabel('PCM16');left.set_xlabel('Predecision seconds')
            right.specgram(pcm,NFFT=320,Fs=16000,noverlap=240,cmap='magma')
            right.set_ylim(0,6000);right.set_xlabel('Predecision seconds');right.set_ylabel('Hz')
            for axis in (left,right):
                axis.axvspan(row['keyword_start_sample']/16000,row['keyword_end_sample']/16000,color='cyan',alpha=.15)
                axis.axvline(row['acoustic_activity_start_seconds'],color='lime',lw=.8)
                axis.axvline(row['acoustic_activity_end_seconds'],color='lime',lw=.8)
                axis.set_xlim(0,len(pcm)/16000)
            left.set_title(row['id']+'; DTW second='+format(row['template_second_nearest'],'.4f'),fontsize=9)
            right.set_title('Owner-labelled call;80ms guards shaded; visual completeness review',fontsize=9)
        fig.tight_layout()
        filename=f'train-review-page-{page//4+1:02d}.png'
        fig.savefig(output/filename,dpi=145);plt.close(fig);pages.append(filename)
    return pages


def main(args):
    os.umask(0o077)
    if args.output.exists() or args.rules_output.exists():
        raise ValueError('Fresh outputs required; previous evidence is immutable')
    calibration=json.loads(args.calibration.read_text())
    parent=json.loads(args.parent_report.read_text())
    audit=json.loads(args.audit.read_text())
    protocol=json.loads(args.protocol.read_text())
    if parent['calibration_sha256']!=sha256(args.calibration) or not parent['complete']:
        raise ValueError('Parent report or calibration changed/incomplete')
    if calibration['audit_sha256']!=sha256(args.audit) or protocol['audit_sha256']!=sha256(args.audit):
        raise ValueError('Mismatched audit provenance')
    threshold=max(r['second_nearest'] for r in calibration['reference_loo'])
    # Freeze before computing any new eligibility decision. Revision is explicit:
    # previous screens observed zero usable sources and failed their own positive
    # controls. No training/checkpoint/final test score is input to this rule.
    rules={'schema':1,'created_at':datetime.now(timezone.utc).isoformat(),
        'revision':'owner-label-with-acoustic-support-v1',
        'revision_reason':'ASR lexical screen omitted the proper name; DTW negative-separation cutoff rejected6/6 known human reference controls. Neither can override owner semantic labels as an accurate independent classifier.',
        'identity_anchor':'Original owner humanLabel=correct; preserved unchanged',
        'threshold':threshold,'threshold_derivation':'maximum second-nearest reference LOO distance among6 original references; no new positive score used to set threshold',
        'reference_positive_controls_supported':sum(r['second_nearest']<=threshold for r in calibration['reference_loo']),
        'references_required':2,'activity_components':1,
        'min_activity_seconds':.20,'max_activity_seconds':.99,
        'crop_guard_samples':1280,'outer_capture_margin_samples':640,
        'keyword_sample_semantics':'Entire energetic component PLUS80ms guard each side; excludes postdecision. Additional40ms of recorded context required outside each guard.',
        'possible_cue_frames_allowed':0,'asr_is_advisory':True,
        'negative_matches':'Known calibration-negative compatibility matches are reported, not relabelled or interpreted as template specificity.',
        'no_detector_threshold_adjustment':True,'no_detector_or_final_test_score_input':True,
        'parent_report_sha256':sha256(args.parent_report),'calibration_sha256':sha256(args.calibration),
        'audit_sha256':sha256(args.audit),'protocol_sha256':sha256(args.protocol),
        'script_sha256':sha256(Path(__file__)),
        'limitations':['Instrument-assisted acoustic support, not independent lexical proof or human phonetic annotation.',
            'Owner-labelled activation-selected calls can contain annotation ambiguity; only isolated complete components are eligible.',
            'Template similarities overlap hard negatives; rule is unsuitable as an automatic wake-word detector.',
            'Energy segmentation can miss low-energy speech;80ms guards and visual TRAIN review reduce but do not eliminate truncation risk.',
            'Rules revised after unsuccessful audit screens, before any fit or candidate checkpoint/final-test consultation.']}
    args.rules_output.parent.mkdir(parents=True,exist_ok=True,mode=0o700)
    save(args.rules_output,rules)
    print('Support rules SEALED',sha256(args.rules_output),flush=True)
    args.output.mkdir(parents=True,mode=0o700)
    byid={r['id']:r for r in audit['events']}
    roles={r['id']:r for r in protocol['events']}
    rows=[]
    for prior in parent['events']:
        event=byid[prior['id']]
        if event['humanLabel']!='correct' or event['audit']['source']!='new-99' or prior['sha256']!=event['triggerContext']['sha256']:
            raise ValueError('Supplement may only screen original new human-labelled calls')
        row=dict(prior)
        row.update(eligible=False,reasons=[],split=roles[prior['id']]['split'],
            semantic_identity_source='owner original humanLabel=correct',
            boundary_type='energetic component with80ms guards; visual review pending; no human phonetic annotation')
        reasons=row['reasons'];spans=prior['acoustics']['active_spans']
        if len(spans)!=1:
            reasons.append('not-one-isolated-acoustic-component')
        else:
            span=spans[0]
            begin=round(span['start']*16000)-rules['crop_guard_samples']
            end=round(span['end']*16000)+rules['crop_guard_samples']
            if not rules['min_activity_seconds']<=span['end']-span['start']<=rules['max_activity_seconds']:
                reasons.append('implausible-or-multiword-component-duration')
            if begin<rules['outer_capture_margin_samples'] or prior['decision_sample']-end<rules['outer_capture_margin_samples']:
                reasons.append('guarded-word-lacks-additional40ms-capture-margin')
            if prior.get('template_second_nearest',float('inf'))>threshold:
                reasons.append('insufficient-two-reference-acoustic-support')
            row.update(acoustic_activity_start_seconds=span['start'],acoustic_activity_end_seconds=span['end'],
                keyword_start_sample=max(0,begin),keyword_end_sample=min(prior['decision_sample'],end),
                keyword_start_seconds=max(0,begin)/16000,keyword_end_seconds=min(prior['decision_sample'],end)/16000,
                training_crop_start_sample=max(0,begin),training_crop_end_sample=min(prior['decision_sample'],end))
        if prior['acoustics']['possible_cue_frames']:
            reasons.append('possible-activation-cue-in-predecision-context')
        row['eligible']=not reasons
        rows.append(row)
    if len(rows)!=92 or len({r['id'] for r in rows})!=92:
        raise ValueError('Supplement must retain all92 events with explicit decisions')
    contraindications=[r for r in calibration['negative_sources'] if r['second_nearest_min']<=threshold]
    accepted_train=[r for r in rows if r['eligible'] and r['split']=='train']
    report={'schema':1,'complete':True,'method':'owner-label-with-acoustic-support',
        'rules_sha256':sha256(args.rules_output),'parent_report_sha256':sha256(args.parent_report),
        'calibration_sha256':sha256(args.calibration),'rules':rules,'events':rows,
        'known_negative_compatibility_matches':{'source_events':len(calibration['negative_sources']),
            'matched_events':len(contraindications),'events':contraindications,
            'interpretation':'Diagnostic known-training calibration sources; this is not a prospective false-positive rate or lexical classification guarantee.'},
        'train_review_pages':plots(args.output,accepted_train,byid),
        'summary':{'events':len(rows),'eligible':sum(r['eligible'] for r in rows),
            'by_split':{split:{'events':sum(r['split']==split for r in rows),
                    'eligible':sum(r['split']==split and r['eligible'] for r in rows)} for split in ('train','validation','test','buffer')},
            'reasons':dict(Counter(x for r in rows for x in r['reasons']))},
        'limitations':rules['limitations'],'visual_train_review_pending':True}
    save(args.output/'report.json',report)
    print(json.dumps(report['summary']),flush=True)
    print('Negative compatibility matches',len(contraindications),'/',len(calibration['negative_sources']),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__)
    for name in ('parent-report','calibration','audit','protocol','output','rules-output'):
        parser.add_argument('--'+name,required=True,type=private_output)
    main(parser.parse_args())
