from pathlib import Path
import os,json,subprocess,sys
import numpy as np
from scipy.signal import lfilter
from case_data import root,split,choose_voice,make_event,clip,rng_for,db,rms,FRAME

def noisy(rng,n):
    x=lfilter([1.0],[1.0,-float(rng.uniform(-.5,.88))],rng.normal(0,1,n))
    x*=float(np.exp(rng.uniform(np.log(3),np.log(100))))/max(1e-3,rms(x))
    return x
def hardware_voice(rng,tag,stem,width,target):
    p=choose_voice(rng,tag,stem,width*FRAME)
    if rng.random()<.5:
        p=lfilter([1.,float(rng.uniform(-.35,.55))],[1.],p)
    k=int(rng.integers(0,160))
    if k:p[:k]*=np.linspace(0,1,k)
    p[-80:]*=np.linspace(1,0,80)
    p*=target/max(1,rms(p))
    power=np.sqrt((p.reshape(-1,FRAME)**2).mean(axis=1))
    label=(power>max(22,float(power.max())*.07)).astype(np.uint8)
    if len(label)>2:label[1:-1]|=label[:-2]&label[2:]
    return p,label

def cases(stem,tag):
    rng=rng_for('physical-v1:'+stem)
    a=np.load(root/'sources'/(stem+'.npz'))
    n=min(len(a['mic']),len(a['render']),4*16000)//FRAME*FRAME
    base_mic=a['mic'][:n].astype(np.float64);base_far=a['render'][:n].astype(np.float64)
    f=n//FRAME
    validate=tag=='validation'
    # Always start on the original paired recording's sample zero. No lost
    # causal prehistory from arbitrary positive/negative crop boundaries.
    x=base_mic*db(rng.uniform(-6,9))
    z=base_far*db(rng.uniform(-7,7))
    at=int(rng.integers(12,35))*FRAME
    if rng.random()<.5:x[at:]*=db(rng.uniform(6,14))
    else:x=np.clip(x*db(rng.uniform(3,10)),-float(rng.uniform(2200,6500)),float(rng.uniform(2200,6500)))
    yield 'origin-stress','sw',clip(x),clip(z),np.zeros(f,np.uint8),[],np.zeros_like(x),x
    for rep in range(2):
        x=base_mic*db(rng.uniform(-7,8));z=base_far*db(rng.uniform(-7,7))
        x+=noisy(rng,n)*.25
        mix=x.copy()
        labels=np.zeros(f,np.uint8);events=[]
        if rep==0:
            starts=[int(rng.integers(10,36))]
        else:
            starts=[int(rng.integers(75,125))]
        if not validate:starts+=[int(rng.integers(130,160))]
        for start in starts:
            width=int(rng.choice([12,16,24]))
            if start+width>=f:continue
            speech,snr=make_event(rng,tag,stem,f/50,width,x,start*FRAME,labels,
                                 float(rng.choice([-6,0,6])) if validate else None)
            mix[start*FRAME:(start+width)*FRAME]+=speech
            events.append((start,width,snr))
        yield 'origin-mix'+str(rep),'sw',clip(mix),clip(z),labels,events,mix-x,x

    # Explicitly teach the hardware-AEC route to continue seeing user voice
    # despite an active digital reference with no matching acoustic echo.
    for rep in range(2):
        z=base_far*db(rng.uniform(-9,8))
        x=base_mic*db(rng.uniform(-65,-20)) if rng.random()<.55 else np.zeros(n)
        x+=noisy(rng,n)
        if rep==1:
            # Impulsive handling/clicks in addition to stationary background.
            for t in range(int(rng.integers(2,8))):
                at=int(rng.integers(0,n-240))
                dt=int(rng.integers(30,240))
                x[at:at+dt]+=rng.normal(0,float(rng.uniform(80,800)),dt)*np.hanning(dt)
        yield 'hardware-negative'+str(rep),'hw',clip(x),clip(z),np.zeros(f,np.uint8),[],np.zeros_like(x),x
    for rep in range(2):
        z=base_far*db(rng.uniform(-10,10))
        if rep==1 and rng.random()<.35:z*=0
        x=base_mic*db(rng.uniform(-65,-28)) if rng.random()<.55 else np.zeros(n)
        x+=noisy(rng,n)
        background=x.copy()
        labels=np.zeros(f,np.uint8);events=[]
        starts=[int(rng.integers(11,34))] if rep==0 else [int(rng.integers(70,118))]
        if not validate:starts+=[int(rng.integers(135,162))]
        for start in starts:
            width=int(rng.choice([12,16,24]))
            if start+width>=f:continue
            amp=float(np.exp(rng.uniform(np.log(140),np.log(4200))))
            speech,label=hardware_voice(rng,tag,stem,width,amp)
            x[start*FRAME:(start+width)*FRAME]+=speech
            labels[start:start+width]|=label
            events.append((start,width,None))
        yield 'hardware-mix'+str(rep),'hw',clip(x),clip(z),labels,events,x-background,background
