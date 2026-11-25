from pathlib import Path
import numpy as np
from scipy.signal import resample_poly, lfilter
import json,io,wave,subprocess,math,hashlib,os
FRAME=320

root=Path(os.environ['AEGIS_DTD_WORKDIR']).resolve()
split=json.loads((root/'split.json').read_text())
def rng_for(s):return np.random.default_rng(int.from_bytes(hashlib.sha256(s.encode()).digest()[:8],'big'))
def db(x):return 10**(x/20)
def rms(x):return np.sqrt(np.mean(np.asarray(x,dtype=np.float64)**2))
def clip(x):return np.rint(np.clip(x,-32768,32767)).astype(np.int16)
def read_voice(p):
    a=np.load(p)
    return a['render'].astype(np.float64)
def espeak_voice(phrase,voice,speed):
    wav=subprocess.check_output(['espeak-ng','-v',voice,'-s',str(speed),'--stdout',phrase])
    with wave.open(io.BytesIO(wav)) as f:
        sr=f.getframerate();a=np.frombuffer(f.readframes(f.getnframes()),dtype='<i2')
    g=math.gcd(sr,16000); a=resample_poly(a.astype(np.float64),16000//g,sr//g)
    active=np.flatnonzero(np.abs(a)>=max(20,np.abs(a).max()*.04))
    if len(active):a=a[max(0,active[0]-40):min(len(a),active[-1]+80)]
    return a
def build_voice_bank():
    p=root/'voice-bank.npz'
    if p.exists():return
    banks={}
    phrases={
       'train':['Oi','Sim','Não','Calma','Olha','Confirma','Volta','Diga','Fala','Aguarde','Pode falar','Mas eu queria','Vamos tentar','Agora sim','Com licença','Não é isso','E se for','Tá certo','Tá errado','Continua','Veja aqui','Qual é','Vai','Eu prefiro','Quero saber','Pronto','Vamos lá','Já ouvi'],
       'validation':['Repete','Atenção','Peraí','Só um minuto','Ouve','Meu pedido','De novo','Chega','Acho que sim','Tá bom','Tem certeza','Ótimo','É isso','Um momento']
    }
    for tag,words in phrases.items():
        rng=rng_for(tag)
        for idx,phrase in enumerate(words):
            variants=('pt-br+f1','pt-br+m1','pt-br+f5','pt-br+m5','pt-br+f3','pt-br+m2') if tag=='train' else ('pt-br+f4','pt-br+m4','pt-br+f2','pt-br+m3')
            for n in range(3):
                banks[f'{tag}_{idx}_{n}']=espeak_voice(phrase,variants[(idx+n)%len(variants)],int(rng.integers(135,215)))
    np.savez_compressed(p,**banks)
def natural_bank(split_tag):
    # Natural talkers derive only from acoustic-source-disjoint train or validation references.
    stems=split[split_tag]
    return {s:read_voice(root/'sources'/(s+'.npz')) for s in stems}
BANK=None
NATURAL=None
def init():
    global BANK,NATURAL
    b=np.load(root/'voice-bank.npz')
    BANK={k:b[k] for k in b.files}
    NATURAL={tag:natural_bank(tag) for tag in ('train','validation')}
def choose_voice(rng,tag,avoid,n_samples):
    if rng.random()<0.55:
        for trials in range(30):
            other=rng.choice(list(NATURAL[tag]))
            if other==avoid:continue
            speech=NATURAL[tag][other]
            if len(speech)<n_samples:continue
            at=int(rng.integers(0,(len(speech)-n_samples)//FRAME+1))*FRAME
            crop=speech[at:at+n_samples].copy()
            power=np.sqrt((crop.reshape(-1,FRAME)**2).mean(axis=1))
            if np.sum(power>max(30,power.max()*.07))>=max(5,len(power)*.65):
                return crop
    names=[k for k in BANK if k.startswith(tag)]
    for _ in range(12):
        voice=BANK[rng.choice(names)]
        if len(voice)>=n_samples:
            at=int(rng.integers(0,min(len(voice)-n_samples,1200)+1))
            crop=voice[at:at+n_samples].copy()
        else:
            crop=np.pad(voice,(0,n_samples-len(voice))).copy()
        power=np.sqrt((crop.reshape(-1,FRAME)**2).mean(axis=1))
        if np.sum(power>max(30,power.max()*.07))>=max(5,len(power)*.5):return crop
    return crop
def make_event(rng,tag,stem,seconds,near_len,echo,at,label_mask,gain_db=None):
    width=near_len*FRAME
    voice=choose_voice(rng,tag,stem,width)
    if len(voice)!=width:raise ValueError('speech length')
    if rng.random()<.65:
        delay=int(rng.integers(64,280))
        ref=np.zeros(delay+1);ref[0]=1;ref[-1]=rng.uniform(.05,.3)
        voice=lfilter(ref,[1.],voice)
    if rng.random()<.6:
        # Mild microphone coloration varied independently from the recorded echo path.
        d=float(rng.uniform(-.45,.65))
        voice=lfilter([1.,d],[1.],voice)
    ramp=int(rng.integers(0,240))
    if ramp:voice[:ramp]*=np.linspace(0,1,ramp)
    tail=int(rng.integers(40,260))
    voice[-tail:]*=np.linspace(1,0,tail)
    reference=rms(echo[at:at+width])
    nominal_snr=float(rng.choice([-12,-9,-6,-3,0,3,6,9,12,18])) if gain_db is None else gain_db
    target=max(75,reference)*db(nominal_snr)
    if reference<55:target=float(rng.uniform(90,900))*db(float(rng.uniform(-5,5)))
    voice*=target/max(1,rms(voice))
    voiced=np.sqrt((voice.reshape(-1,FRAME)**2).mean(axis=1))
    labels=(voiced>max(25,float(voiced.max())*.08)).astype(np.uint8)
    # Fill single-frame holes inside a phoneme without smearing across longer silence.
    if len(labels)>2:
        labels[1:-1]=labels[1:-1]|(labels[:-2]&labels[2:])
    label_mask[at//FRAME:at//FRAME+len(labels)] |= labels
    return voice,nominal_snr
def case_inputs(stem,tag):
    rng=rng_for('mix-v2:'+stem)
    a=np.load(root/'sources'/(stem+'.npz'))
    echo=a['mic'].astype(np.float64);far=a['render'].astype(np.float64)
    n=min(len(echo),len(far))//FRAME;echo=echo[:n*FRAME];far=far[:n*FRAME]
    if n<FRAME:raise ValueError('short')
    # Validation uses full natural negatives and independent single-insertion cases.
    def take(seconds):
        m=min(n,int(seconds*50))
        begin=int(rng.integers(0,n-m+1))*FRAME
        return echo[begin:begin+m*FRAME].copy(),far[begin:begin+m*FRAME].copy()
    def shift_reference(x,frames):
        if frames>0:return np.pad(x,(frames,0))[:len(x)]
        if frames<0:return np.pad(x[-frames:],(0,-frames))
        return x
    def augment(x,z,stress=False,allow_gain=True):
        if allow_gain:
            x*=db(rng.uniform(-9,10));z*=db(rng.uniform(-7,7))
        if stress:
            kind=int(rng.integers(0,4))
            n=len(x)
            if kind==0:
                i=int(rng.integers(n//5,3*n//5));x[i:]*=db(rng.uniform(6,15))
            elif kind==1:
                limit=float(rng.uniform(1800,12000))
                x=np.clip(x*db(rng.uniform(5,13)),-limit,limit)
            elif kind==2:
                i=int(rng.integers(n//5,3*n//5));x[i:int(i+n*.4)]*=db(rng.uniform(-24,-8))
            else:
                i=int(rng.integers(n//5,3*n//5));delay=int(rng.integers(80,900))
                x[i:]+=rng.uniform(.25,.65)*x[i-delay:n-delay].copy()
        if rng.random()<.22:
            x+=rng.normal(0,rng.uniform(5,50),len(x))
        # Slight render position errors are common across devices. Shift under known bound.
        if rng.random()<.32:
            z=shift_reference(z,int(rng.integers(-160,161)))
        return x,z
    yield ('natural',clip(echo),clip(far),np.zeros(n,np.uint8),[],np.zeros_like(echo),echo)
    for rep in range(2):
        x,z=take(6 if tag=='train' else 10)
        x,z=augment(x,z,stress=True)
        yield ('stress'+str(rep),clip(x),clip(z),np.zeros(len(x)//FRAME,np.uint8),[],np.zeros_like(x),x)
    repetitions=4 if tag=='train' else 5
    for rep in range(repetitions):
        x,z=take(6 if tag=='train' else 4.6)
        x,z=augment(x,z,stress=(rng.random()<.4))
        nframes=len(x)//FRAME
        # Include echo-attenuated/muted paths and audio with no rendered reference.
        if rep==repetitions-1:
            if rng.random()<.45:
                z*=0; x*=0
            else:
                x*=db(rng.uniform(-55,-18))
            x+=rng.normal(0,rng.uniform(0,22),len(x))
        near=np.zeros_like(x);labels=np.zeros(nframes,np.uint8);events=[]
        durations=[12,16,24] if tag=='validation' else [10,12,16,20,24,28,36]
        at_list=[]
        if tag=='validation':
            width=int(rng.choice(durations))
            starts=[10,23,58,92,145]
            target=starts[rep]
            near_samples=width*FRAME
            choices=range(max(4,target-8),min(target+8,nframes-width-6)+1)
            # Prefer an audible echo segment, except the intentionally quiet path.
            atframes=max(choices,key=lambda i:(rms(x[i*FRAME:(i+width)*FRAME])>100 and rms(z[i*FRAME:(i+width)*FRAME])>100, -abs(i-target)))
            voice,snr=make_event(rng,tag,stem,nframes/50,width,x,atframes*FRAME,labels,float([-6,0,6,0,6][rep]))
            near[atframes*FRAME:(atframes+width)*FRAME]+=voice
            events.append((atframes,width,snr))
        else:
            # Multiple separated interruptions expose startup and late contexts per recording.
            proposed=[int(rng.integers(7,40)),int(rng.integers(75,130)),int(rng.integers(155,215)),int(rng.integers(235,260))]
            for start in proposed:
                width=int(rng.choice(durations))
                if start+width>=nframes-8:continue
                voice,snr=make_event(rng,tag,stem,nframes/50,width,x,start*FRAME,labels)
                near[start*FRAME:(start+width)*FRAME]+=voice
                events.append((start,width,snr))
        yield ('mix'+str(rep),clip(x+near),clip(z),labels,events,near,x)
