from pathlib import Path
import random, math, time, os, json, sys
import numpy as np
import torch
from torch import nn
import torch.nn.functional as F

ROOT=Path(os.environ['AEGIS_DTD_WORKDIR']).resolve()
SEED=20261124
random.seed(SEED)
np.random.seed(SEED)
torch.manual_seed(SEED)
torch.set_num_threads(2)
device=torch.device('cuda' if torch.cuda.is_available() else 'cpu')
if device.type=='cuda':
    torch.cuda.set_per_process_memory_fraction(.18,0)
    torch.backends.cudnn.allow_tf32=True
    torch.backends.cuda.matmul.allow_tf32=True
N=193; BASE=64; EMB=48; GRU=64; FUSE=96; MAX_LAG=60
# 7 embeddings, 3 x 6 matching stats, 4 fixed harmonic stats, 4 current
# levels, 3 reference-level history stats, and the software/hardware flag.
MERGED=EMB*7+18+4+4+3+1

class DualTalk(nn.Module):
    def __init__(self,mean,scale):
        super().__init__()
        self.register_buffer('mean',torch.tensor(mean,dtype=torch.float32))
        self.register_buffer('scale',torch.tensor(scale,dtype=torch.float32))
        self.base=nn.Linear(N+N-1,BASE)
        self.temporal=nn.Linear(BASE*3,EMB)
        self.fuse=nn.Linear(MERGED,FUSE)
        self.gru=nn.GRU(FUSE,GRU,batch_first=True)
        self.score=nn.Linear(GRU,1)
        # Auxiliary near-speech spectral ratio target (training only).
        self.mask=nn.Linear(GRU,N-1)

    def encode(self,x):
        norm=((x-self.mean)*self.scale).clamp(-10,10)
        shape=x[...,:N-1]
        shape=shape-shape.mean(dim=-1,keepdim=True)
        base=F.relu(self.base(torch.cat((norm,shape),dim=-1)))
        # Identical causal 3-frame encoder for all four streams.
        p1=F.pad(base[:,:-1],(0,0,0,0,1,0))
        p2=F.pad(base[:,:-2],(0,0,0,0,2,0)) if base.shape[1]>=2 else torch.zeros_like(base)
        base=torch.cat((p2,p1,base),dim=-1)
        return torch.tanh(self.temporal(base)),norm

    @staticmethod
    def history(data):
        # data[B,T,dim] -> [B,T,61,dim], oldest to current.
        return F.pad(data,(0,0,MAX_LAG,0)).unfold(1,MAX_LAG+1,1).transpose(-1,-2)

    def forward(self,x,software):
        # x[B,T,4,N], stream order mic, render, nonlinear, linear. Entire
        # sequence is causal: the lag windows end at the current frame.
        B,T=x.shape[:2]
        enc,norm=self.encode(x)
        far=enc[:,:,1]
        windows=self.history(far)
        unit=F.normalize(windows,dim=-1,eps=1e-6)
        contexts=[];matching=[]
        for i in (0,2,3):
            query=F.normalize(enc[:,:,i],dim=-1,eps=1e-6)
            sim=torch.einsum('btd,btkd->btk',query,unit)
            attention=torch.softmax(sim*8.,dim=-1)
            contexts.append(torch.einsum('btk,btkd->btd',attention,windows))
            matching.append(torch.stack((sim.max(dim=-1).values,sim[:,:,-21:].max(dim=-1).values,
                sim.mean(dim=-1),sim[:,:,-1],
                (MAX_LAG-sim.argmax(dim=-1)).to(x.dtype)/MAX_LAG,attention.max(dim=-1).values),dim=-1))
        # Spectral whitening provides a fixed fine-frequency harmonic match as
        # a strong prior robust to smooth speaker/mic EQ; the learned attention
        # remains free to use other features.
        spectrum=x[:,:,:2,:N-1]
        smooth=F.avg_pool1d(spectrum.reshape(-1,1,N-1),9,1,4,count_include_pad=False)
        h=F.normalize(spectrum-smooth.reshape_as(spectrum),dim=-1,eps=1e-6)
        all_sim=torch.bmm(h[:,:,0],h[:,:,1].transpose(1,2))
        indices=torch.arange(T,device=x.device)[:,None]-torch.arange(MAX_LAG,-1,-1,device=x.device)[None,:]
        harmonics=all_sim.gather(2,indices.clamp_min(0)[None].expand(B,-1,-1))
        harmonics=harmonics.masked_fill(indices[None]<0,0.)
        harmonic_summary=torch.stack((harmonics.max(dim=-1).values,harmonics[:,:,-21:].max(dim=-1).values,
            harmonics.mean(dim=-1),(MAX_LAG-harmonics.argmax(dim=-1)).to(x.dtype)/MAX_LAG),dim=-1)
        level=self.history(x[:,:,1,-1:])
        envelope=torch.cat((level.max(dim=2).values,level[:,:,-21:].max(dim=2).values,level.mean(dim=2)),dim=-1)
        mode=software[:,None,None].expand(B,T,1).to(x.dtype)
        fused=torch.cat((enc[:,:,0],enc[:,:,2],enc[:,:,3],far,*contexts,*matching,harmonic_summary,
                         norm[:,:,:,-1],envelope,mode),dim=-1)
        fused=F.relu(self.fuse(fused))
        hidden,_=self.gru(fused)
        return self.score(hidden).squeeze(-1), self.mask(hidden)

class Case:
    def __init__(self,source,name,x,y,eligible,events,category,mask,split):
        self.source=source;self.name=name;self.x=x
        self.y=y.copy();self.eligible=eligible.copy()
        self.events=events;self.category=category;self.mask=mask
        self.split=split
        # Keep brief phoneme gaps causally connected to a near utterance.
        for on,width,*_ in events:
            end=min(len(y),on+width+1)
            self.y[on+1:end]|=y[on:end-1]

def read_cases(tag,allow_partial=False):
    spec=json.loads((ROOT/'split.json').read_text())
    cases=[]
    for source in spec[tag]:
        p=ROOT/'tensors'/(source+'.npz')
        if not p.exists():
            if allow_partial:continue
            raise FileNotFoundError(p)
        a=np.load(p)
        x=np.stack([a[k] for k in ('mic','render','clean','linear')],axis=1).astype(np.float16,copy=False)
        labels=a['y'];eligible=a['eligible']
        if tag=='validation':eligible=np.ones_like(eligible)
        masks=None
        mp=ROOT/'masks'/(source+'.npz')
        if mp.exists():
            masks=np.load(mp)['mask']
            assert masks.shape==(len(x),N-1)
        at=0
        for name,n,events,category in json.loads(str(a['records'])):
            cases.append(Case(source,name,x[at:at+n],labels[at:at+n],eligible[at:at+n],events,category,
                              None if masks is None else masks[at:at+n],tag))
            at+=n
        assert at==len(x)
    return cases

def prepare_streams(x,mode):
    if mode=='sw':return x.copy(),1.
    out=x.copy()
    if mode=='raw':mic=out[:,0].copy()
    elif mode=='clean':mic=out[:,2].copy()
    elif mode=='linear':mic=out[:,3].copy()
    else:raise ValueError(mode)
    out[:,0]=mic;out[:,2]=mic;out[:,3]=mic
    return out,0.

def estimate_stats(cases):
    total=0;sums=np.zeros(N,np.float64);squares=np.zeros(N,np.float64)
    for case in cases:
        x=case.x.astype(np.float32)
        z=x[:,:2] if case.category==2 else x
        sums+=z.sum(axis=(0,1),dtype=np.float64)
        squares+=(z*z).sum(axis=(0,1),dtype=np.float64)
        total+=z.shape[0]*z.shape[1]
    mean=sums/total;std=np.sqrt(np.maximum(squares/total-mean**2,.01))
    return mean.astype(np.float32),(1/std).astype(np.float32)

def choose_mode(case,training=True):
    if case.category==2:return 'raw'
    if not training:return 'sw'
    # Hardware-proxy raw, linear and residual-suppressed capture are all seen
    # in addition to explicit dry mic + active playback hardware simulations.
    if random.random()<.20:return random.choice(('raw','clean','linear'))
    return 'sw'

def batch_examples(train,pos_items,negative_items,count=16,target_frames=64,warmup=64,aux=False,hard=None):
    examples=[]
    for i in range(count):
        choose_positive=i<count//2
        if choose_positive:
            case,on,width=random.choice(pos_items)
            target=max(0,on-random.randrange(0,18))
        else:
            if hard and random.random()<.40:
                case,peak=random.choice(hard)
                target=max(0,peak-random.randrange(0,32))
            else:
                case=random.choice(negative_items)
                target=random.randrange(0,max(1,len(case.y)-target_frames))
        start=max(0,target-warmup)
        end=min(len(case.y),target+target_frames)
        x,flag=prepare_streams(case.x[start:end],choose_mode(case))
        y=case.y[start:end].astype(np.float32)
        valid=case.eligible[start:end].astype(np.float32)
        valid[:target-start]=0
        mask=case.mask[start:end].astype(np.float32)/255. if aux and case.mask is not None else None
        examples.append((x,y,valid,flag,mask))
    width=max(len(e[0]) for e in examples)
    x=np.zeros((count,width,4,N),np.float32)
    y=np.zeros((count,width),np.float32)
    keep=np.zeros_like(y);flags=np.zeros(count,np.float32)
    masks=np.zeros((count,width,N-1),np.float32) if aux else None
    mask_keep=np.zeros_like(y) if aux else None
    for i,(a,b,c,flag,h) in enumerate(examples):
        n=len(a);x[i,:n]=a;y[i,:n]=b;keep[i,:n]=c;flags[i]=flag
        if h is not None:
            masks[i,:n]=h;mask_keep[i,:n]=c
    tensors=[torch.from_numpy(a).to(device) for a in (x,y,keep,flags)]
    if aux:tensors.extend(torch.from_numpy(a).to(device) for a in (masks,mask_keep))
    return tensors

def burst_tensor(values,votes=4,width=7):
    window=F.pad(values,(width-1,0),value=-100).unfold(-1,width,1)
    return torch.minimum(values,window.topk(votes,dim=-1).values[...,-1])

def train_step(model,optimizer,batch,aux):
    x,y,keep,flag=batch[:4]
    optimizer.zero_grad(set_to_none=True)
    score,predmask=model(x,flag)
    if keep.sum()==0:return 0.
    # Soft labels limit overconfident scores on acoustically ambiguous frames.
    targets=.02+.96*y
    weight=torch.where(y>0,torch.tensor(2.5,device=device),torch.tensor(1.,device=device))
    frame=F.binary_cross_entropy_with_logits(score,targets,reduction='none')
    loss=(frame*keep*weight).sum()/keep.sum()
    # Explicit differentiable 4/7 burst objective for the actual confirmation
    # rule rather than relying on a frame-only classifier.
    burst=burst_tensor(score)
    true_window=F.pad(y,(6,0)).unfold(-1,7,1)
    by=((true_window.sum(dim=-1)>=4)&(y>0)).float()
    # If there is near speech but not four active frames yet, don't penalize
    # a high prediction during that partially-voiced region.
    bkeep=keep*torch.where((y>0)&(by==0),0.15,1.)
    bce=F.binary_cross_entropy_with_logits(burst,.02+.96*by,reduction='none')
    loss=loss+.6*(bce*bkeep*torch.where(by>0,2.0,1.0)).sum()/bkeep.sum().clamp_min(1)
    # Penalize high false-confirmation bursts averaged over each training clip.
    quiet=keep.bool()&(true_window.sum(dim=-1)==0)
    hardest=burst.masked_fill(~quiet,-100).max(dim=-1).values
    loss=loss+.12*F.softplus(hardest).mean()
    if aux:
        mask,mkeep=batch[4:6]
        weight=(.2+(x[:,:,0,:N-1]/5.).clamp(0,1))*(1.+(mask>.05).float())
        per=(F.binary_cross_entropy_with_logits(predmask,mask,reduction='none')*weight).sum(dim=-1)/weight.sum(dim=-1).clamp_min(1)
        loss=loss+.45*(per*mkeep).sum()/mkeep.sum().clamp_min(1)
    loss.backward()
    nn.utils.clip_grad_norm_(model.parameters(),3.)
    optimizer.step()
    return float(loss.detach())

def burst_np(values,votes,width):
    v=np.asarray(values).copy()
    v[:2]=-100
    p=np.lib.stride_tricks.sliding_window_view(np.r_[np.full(width-1,-100,np.float32),v],width)
    return np.minimum(v,np.partition(p,width-votes,axis=1)[:,width-votes])

@torch.no_grad()
def predictions(model,cases,which,eval_batch=8):
    model.eval()
    out=[]
    allowed=[case for case in cases if (case.category!=2 if which=='sw' else case.category!=1)]
    allowed.sort(key=lambda c:len(c.y))
    for i in range(0,len(allowed),eval_batch):
        chunk=allowed[i:i+eval_batch];n=max(len(c.y) for c in chunk)
        x=np.zeros((len(chunk),n,4,N),np.float32);flags=[]
        for j,c in enumerate(chunk):
            v,flag=prepare_streams(c.x,which);x[j,:len(v)]=v;flags.append(flag)
        y,_=model(torch.from_numpy(x).to(device),torch.tensor(flags,device=device,dtype=torch.float32))
        y=y.cpu().numpy()
        out.extend((c,y[j,:len(c.y)].copy()) for j,c in enumerate(chunk))
    return out

def event_results(items,votes,width):
    stats=[]
    for case,logits in items:
        b=burst_np(logits,votes,width)
        if case.events:
            on,span,*_=case.events[0]
            end=min(len(b),on+span+7)
            before=float(b[:on].max()) if on else -100.
            inside=float(b[on:end].max()) if end>on else -100.
            after=float(b[end:].max()) if end<len(b) else -100.
        else:before=float(b.max());inside=after=-100.
        stats.append((case,inside,before,after))
    # The player stops on the first confirmation. Late unrelated scores only
    # count as false cuts if a genuine speech-window cut has not occurred.
    threshold=max(s[2] for s in stats)+.15
    while True:
        next_threshold=max((s[3]+.15 for s in stats if s[1]<threshold and s[3]>=threshold),default=threshold)
        if next_threshold<=threshold:break
        threshold=next_threshold
    return stats,threshold
def summarize(stats,threshold):
    groups={}
    for case,inside,before,after in stats:
        g=('hw' if case.category==2 else 'origin' if case.category==1 else 'mute' if case.name=='mix4' else 'old')
        total=groups.setdefault(g,[0,0,0,0]) # hit, total positive, false, total negative
        if case.events:
            total[1]+=1
            if before>=threshold:total[2]+=1
            elif inside>=threshold:total[0]+=1
            elif after>=threshold:total[2]+=1
        else:
            total[3]+=1;total[2]+=before>=threshold
    return groups

def mine_hard(model,pool):
    subset=random.sample(pool,min(len(pool),84))
    candidates=[]
    for mode in ('sw','raw'):
        for case,values in predictions(model,subset,mode,eval_batch=8):
            if case.events:continue
            b=burst_np(values,4,7)
            b[case.eligible==0]=-100
            peak=int(np.argmax(b))
            candidates.append((float(b[peak]),case,peak))
    candidates.sort(key=lambda item:item[0],reverse=True)
    return [(c,p) for _,c,p in candidates[:56]]

def evaluate(model,dev):
    sw=predictions(model,dev,'sw')
    hw=predictions(model,dev,'raw')
    best=(-1,-1,-1000);chosen=None
    for votes,width in ((4,7),(4,9),(5,9),(5,10),(6,10),(6,12),(7,12)):
        swstat,swcut=event_results(sw,votes,width)
        hwstat,hwcut=event_results(hw,votes,width)
        ss=summarize(swstat,swcut);hh=summarize(hwstat,hwcut)
        true=ss.get('old',[0])[0]+ss.get('origin',[0])[0]
        rank=(true, hh.get('hw',[0])[0]+ss.get('mute',[0])[0],-width)
        print('window',votes,width,'sw',ss,'hw',hh,'thresholds',round(swcut,3),round(hwcut,3),flush=True)
        if rank>best:
            best=rank;chosen=(votes,width,swcut,hwcut,ss,hh)
    return best,chosen

def main():
    start=time.time()
    allow_partial='--smoke' in sys.argv
    print('device',device,'loading cases',flush=True)
    train=read_cases('train',allow_partial)
    dev=read_cases('validation',allow_partial)
    if allow_partial:
        print('smoke input',len(train),len(dev),flush=True)
    mean,scale=estimate_stats(train)
    aux_fraction=sum(c.mask is not None for c in train)/max(1,len(train))
    aux=aux_fraction>.98
    print('loaded',len(train),len(dev),'train frames',sum(len(c.y) for c in train),'val frames',sum(len(c.y) for c in dev),
          'aux ratio',aux_fraction,'elapsed',round(time.time()-start,1),flush=True)
    positive=[];negative=[]
    for c in train:
        if c.events:
            for on,width,*_ in c.events:
                if (c.eligible[on:min(len(c.y),on+width)]*c.y[on:min(len(c.y),on+width)]).any():
                    positive.append((c,on,width))
        else:negative.append(c)
    print('events',len(positive),'neg cases',len(negative),flush=True)
    model=DualTalk(mean,scale).to(device)
    optimizer=torch.optim.AdamW(model.parameters(),lr=.0012,weight_decay=.0008)
    best=(-1,-1,-1000);hard=[]
    steps=3 if allow_partial else int(os.environ.get('AEGIS_STEPS_PER_EPOCH','140'))
    count=int(os.environ.get('AEGIS_BATCH','16'))
    epochs=1 if allow_partial else int(os.environ.get('AEGIS_EPOCHS','22'))
    for epoch in range(epochs):
        model.train();losses=[]
        t=time.time()
        for i in range(steps):
            batch=batch_examples(train,positive,negative,count=count,aux=aux,hard=hard)
            losses.append(train_step(model,optimizer,batch,aux))
            if (i+1)%35==0:
                print('step',epoch+1,i+1,'loss',round(np.mean(losses[-35:]),4),'sec',round(time.time()-t,1),
                      'gpu peak MB',round(torch.cuda.max_memory_reserved()/2**20) if device.type=='cuda' else '-',flush=True)
        for g in optimizer.param_groups:g['lr']*=.96
        print('epoch',epoch+1,'loss',round(float(np.mean(losses)),4),'train sec',round(time.time()-t,1),flush=True)
        if allow_partial:
            print('smoke complete',flush=True);return
        rank,chosen=evaluate(model,dev)
        if rank>best:
            best=rank
            payload={'state':{k:v.cpu() for k,v in model.state_dict().items()},'epoch':epoch+1,
                     'mean':mean,'scale':scale,'rank':rank,'calibration':chosen,'aux':aux}
            torch.save(payload,ROOT/'best.pt')
            print('saved',best,chosen,flush=True)
        if epoch in (2,5,8,12,16,20):
            hard=mine_hard(model,negative)
            print('training hard-negative windows',len(hard),flush=True)
    print('finished',best,flush=True)

if __name__=='__main__':main()
