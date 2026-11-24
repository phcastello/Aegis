from pathlib import Path
import struct,json,hashlib,sys,math,os
import numpy as np
import torch
from train import DualTalk,N,BASE,EMB,GRU,FUSE,MERGED,MAX_LAG

root=Path(os.environ['AEGIS_DTD_WORKDIR']).resolve()
p=torch.load(root/'best.pt',map_location='cpu',weights_only=False)
state=p['state']
votes,window,swcut,hwcut,swstats,hwstats=p['calibration']
prob=lambda x:1/(1+math.exp(-x/3))
sw,hw=prob(swcut),prob(hwcut)
out=root/'neural-candidate.bin'
with out.open('wb') as f:
    f.write(struct.pack('>11i2f',0x41474434,N,BASE,EMB,GRU,FUSE,MAX_LAG+1,0,MERGED,votes,window,sw,hw))
    keys=['mean','scale','base.weight','base.bias','temporal.weight','temporal.bias','fuse.weight','fuse.bias',
          'gru.weight_ih_l0','gru.bias_ih_l0','gru.weight_hh_l0','gru.bias_hh_l0','score.weight','score.bias']
    for key in keys:
        f.write(np.ascontiguousarray(state[key].numpy(),dtype='>f4').tobytes())
model=DualTalk(p['mean'],p['scale'])
model.load_state_dict(state)
model.eval()
# PCM-level numerical regression vectors, deterministic and entirely synthetic.
F=320;frames=241
indices=np.arange(frames*F,dtype=np.int64)
t=indices//F
render=((indices*7919+t*104729)%12007-6003).astype(np.int16)
def delayed(blocks):
    n=F*blocks
    return np.r_[np.zeros(n,np.int16),render[:-n]].astype(np.float64)
echo=np.where(t<120,delayed(5)*.55+delayed(13)*.16,delayed(31)*.70+delayed(2)*.12)
voice=np.where(((t>=12)&(t<22))|((t>=90)&(t<111))|((t>=190)&(t<202)),(indices*631+173)%5077-2538,0)
mic=np.rint(echo+voice).clip(-32768,32767).astype(np.int16)
clean=np.rint(voice+echo*.12).astype(np.int16)
linear=np.rint(voice+echo*.35).astype(np.int16)
taper=.5-.5*np.cos(2*np.pi*np.arange(F)/(F-1))
def spectral(pcm):
    u=pcm.astype(np.float64).reshape(-1,F)
    mag=np.log1p(np.abs(np.fft.rfft(u*taper,n=512,axis=1))[:,1:193]/256)
    level=np.log1p(np.sqrt(np.mean(u*u,axis=1))/32)
    return np.c_[mag,level].astype(np.float32)
def run(m,f,c,l,software):
    seq=np.stack([spectral(x) for x in (m,f,c,l)],axis=1)[None]
    with torch.no_grad():
        z,_=model(torch.from_numpy(seq),torch.tensor([float(software)]))
        return torch.sigmoid(z/3).ravel().tolist()
scores={'software':run(mic,render,clean,linear,True),'hardware':run(mic,render,mic,mic,False)}
# Optional real-development PCM replay, not included in published assets.
real={}
for role in ('mic','render','clean','linear'):
    q=root/('parity-'+role+'.pcm')
    if q.exists():real[role]=np.fromfile(q,dtype='<i2')
if len(real)==4:
    scores['real']=run(real['mic'],real['render'],real['clean'],real['linear'],True)
meta={'schema':'AGD4','epoch':p['epoch'],'rank':p['rank'],'auxiliaryMask':p['aux'],
      'input':{'frameSamples':320,'spectralBins':192,'featureCount':193,'historyFrames':61},
      'requiredVotes':votes,'windowFrames':window,'softwareThreshold':sw,'hardwareThreshold':hw,
      'softwareDevelopment':swstats,'hardwareDevelopment':hwstats,
      'sha256':hashlib.sha256(out.read_bytes()).hexdigest(),'bytes':out.stat().st_size}
(root/'neural-candidate.json').write_text(json.dumps(meta,indent=2)+'\n')
(root/'parity-scores.json').write_text(json.dumps(scores,indent=2)+'\n')
print(json.dumps(meta,indent=2))
