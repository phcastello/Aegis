#!/usr/bin/env node
// Host visual inspection driven by production Kotlin output; no DSP is reimplemented here.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';

const args=Object.fromEntries(process.argv.slice(2).reduce((pairs,value,index,array)=>{
  if(value.startsWith('--'))pairs.push([value.slice(2),array[index+1]]);return pairs;
},[]));
assert.ok(args.frames && args.output,'Use --frames <production-frames.json> --output <new-directory>');
const output=resolve(args.output);mkdirSync(output,{recursive:false,mode:0o700});
const png=join(output,'frames');mkdirSync(png,{mode:0o700});
const data=JSON.parse(readFileSync(args.frames,'utf8'));
assert.equal(data.fps,60);assert.equal(data.barCount,32);
const require=createRequire(import.meta.url);
const {chromium}=require(process.env.AEGIS_PLAYWRIGHT_CORE || 'playwright-core');
const browser=await chromium.launch({
  executablePath:process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE,
  headless:true,args:['--disable-background-timer-throttling','--disable-renderer-backgrounding'],
});
const context=await browser.newContext({viewport:{width:390,height:224},deviceScaleFactor:1});
const page=await context.newPage();
const html=`<!doctype html><meta charset="utf-8"><title>Aegis waveform synthetic inspection</title>
<style>html,body{margin:0;background:#0c111c;color:#bac8e6;font:12px sans-serif}canvas{display:block}p{position:absolute;left:14px;bottom:2px;margin:0;font-size:9px;color:#8090af}</style>
<canvas width="390" height="204"></canvas><p>Host fixture · Kotlin amplitudes · synthetic PCM · not Android physical</p>
<script>
const DATA=${JSON.stringify(data)};
const canvas=document.querySelector('canvas'),ctx=canvas.getContext('2d');
function roundRect(x,y,w,h,r){ctx.beginPath();ctx.roundRect(x,y,w,h,r);ctx.fill();}
window.drawFrame=function(index,reduced=false){
  const frame=DATA.frames[Math.min(index,DATA.frames.length-1)],now=frame.atMs;
  ctx.clearRect(0,0,390,204);ctx.fillStyle='#0c111c';ctx.fillRect(0,0,390,204);
  // Card: 4dp top + 48dp header + 72dp waveform + label + 16dp bottom.
  ctx.fillStyle='#172132';roundRect(12,22,366,164,28);ctx.strokeStyle='#35435b';ctx.lineWidth=1;ctx.stroke();
  ctx.fillStyle='#bdcbf6';ctx.font='500 15px sans-serif';ctx.textAlign='left';ctx.fillText('Aegis',32,55);
  ctx.fillStyle='#253248';ctx.beginPath();ctx.arc(342,50,24,0,Math.PI*2);ctx.fill();
  ctx.fillStyle='#bdcbf6';ctx.font='27px sans-serif';ctx.textAlign='center';ctx.fillText('×',342,59);
  const x=32,y=74,width=334,height=72,cx=x+width/2,cy=y+height/2;
  const left=x+.06*width,right=x+.94*width,slot=(right-left)/32,bar=Math.min(3.2,slot*.46);
  if(frame.state==='capturing'){
    if(frame.bars.every(value=>value<=.002)){
      ctx.strokeStyle='rgba(145,169,238,'+(24/255)+')';ctx.lineWidth=1;
      ctx.beginPath();ctx.moveTo(left,cy);ctx.lineTo(right,cy);ctx.stroke();
    }
    frame.bars.forEach((amp,i)=>{if(amp<=.002)return;const half=.65+amp*height*.40;
      ctx.fillStyle='rgba(145,169,238,'+(Math.trunc(36+219*amp)/255)+')';
      roundRect(left+(i+.5)*slot-bar/2,cy-half,bar,2*half,bar/2);
    });
  }else if(frame.state==='processing'){
    for(let i=0;i<3;i++){
      const phase=(now%1500)/1500*2*Math.PI-i*.85;
      const pulse=reduced ? .5 : (Math.sin(phase)+1)/2;
      ctx.fillStyle='rgba(145,169,238,'+(Math.trunc(100+140*pulse)/255)+')';
      ctx.beginPath();ctx.arc(cx+(i-1)*16,cy+(reduced?0:Math.sin(phase)*2),3.3,0,Math.PI*2);ctx.fill();
    }
  }else if(frame.state==='speaking'){
    const pulse=reduced ? .5 : (Math.sin((now%1800)/1800*2*Math.PI)+1)/2;
    ctx.fillStyle='rgba(145,169,238,'+(Math.trunc(150+90*pulse)/255)+')';
    ctx.beginPath();ctx.arc(cx,cy,2.7,0,Math.PI*2);ctx.fill();
    ctx.lineWidth=2;ctx.lineCap='round';
    for(let i=0;i<2;i++){
      const radius=12+i*9+(reduced?0:pulse*1.5);
      ctx.strokeStyle='rgba(145,169,238,'+(Math.trunc(180-i*50+40*pulse)/255)+')';
      for(const degrees of [-40,140]){ctx.beginPath();ctx.arc(cx,cy,radius,degrees*Math.PI/180,(degrees+80)*Math.PI/180);ctx.stroke();}
    }
    ctx.lineCap='butt';
  }else if(frame.state==='error'){
    ctx.fillStyle='#f2b5b5';ctx.font='27px sans-serif';ctx.textAlign='center';ctx.fillText('!',cx,cy+9);
  }
  const labels={capturing:'Estou ouvindo',processing:'Processando',speaking:'Aegis está falando',error:'Não consegui concluir. Tente novamente.',idle:'Aegis'};
  ctx.fillStyle='#dbe3f4';ctx.font='14px sans-serif';ctx.textAlign='center';ctx.fillText(labels[frame.state],195,166);
  ctx.fillStyle='#8090af';ctx.font='11px monospace';ctx.textAlign='left';ctx.fillText((now/1000).toFixed(3)+' s',16,13);
};
window.measurePresentation=()=>new Promise(resolve=>{
  const intervals=[];let start=null,last=null;
  function tick(now){if(start===null)start=now;if(last!==null)intervals.push(now-last);last=now;
    const elapsed=now-start;window.drawFrame(Math.round(elapsed*60/1000));
    if(elapsed<13000)requestAnimationFrame(tick);else resolve(intervals);
  }requestAnimationFrame(tick);
});
window.drawFrame(0);
</script>`;
writeFileSync(join(output,'inspection.html'),html);
try {
  await page.setContent(html);
  // Real wall-clock RAF cadence is measured independently of the encoded fixture FPS.
  const intervals=await page.evaluate(()=>window.measurePresentation());
  const sorted=[...intervals].sort((a,b)=>a-b);
  const timing={scope:'Host Chromium requestAnimationFrame; not Android/device FPS',
    callbacks:intervals.length,medianMs:sorted[Math.floor(sorted.length/2)],
    p95Ms:sorted[Math.floor(sorted.length*.95)],maxMs:Math.max(...intervals),
    over40ms:intervals.filter(ms=>ms>40).length,intervalsMs:intervals};
  writeFileSync(join(output,'raf-timing.json'),JSON.stringify(timing,null,2)+'\n');
  for(let index=0;index<data.frames.length;index++){
    await page.evaluate(index=>window.drawFrame(index),index);
    await page.screenshot({path:join(png,String(index).padStart(4,'0')+'.png')});
  }
  const mean=values=>values.reduce((a,b)=>a+b,0)/values.length;
  const diff=(a,b)=>mean(a.map((x,i)=>Math.abs(x-b[i])));
  const voice=data.frames.filter(f=>f.atMs>=2400 && f.atMs<5000);
  const nearSilence=data.frames.filter(f=>f.atMs<800 || (f.atMs>=9200&&f.atMs<9600));
  const interior=[];const boundary=[];let varied=0;
  for(let i=1;i<data.frames.length;i++){
    const a=data.frames[i-1],b=data.frames[i];if(b.atMs<2400 || b.atMs>=5000)continue;
    const delta=diff(a.bars,b.bars);
    if(b.atMs%200<18)boundary.push(delta);else interior.push(delta);
    if(new Set(b.bars.map(v=>Math.round(v*10000))).size>8)varied++;
  }
  assert.ok(nearSilence.every(f=>f.bars.every(v=>v<=.002)),'Silence must be almost flat');
  assert.ok(interior.filter(v=>v>1e-5).length/interior.length>.95,'Movement must continue inside each 200ms input block');
  assert.ok(varied/voice.length>.9,'Bars need independent temporal amplitudes');
  const summary={scope:'Production Kotlin amplitudes rendered in host Canvas. Geometry reproduction is not Android Canvas/physical approval.',
    frames:data.frames.length,encodedFps:data.fps,signalWindowMs:data.windowMs,
    historyMs:data.historyMs,presentationDelayMs:data.presentationDelayMs,
    quietFrames:nearSilence.length,quietMax:Math.max(...nearSilence.flatMap(f=>f.bars)),
    voiceFrames:voice.length,independentlyVariedFrames:varied,
    interiorChangingRatio:interior.filter(v=>v>1e-5).length/interior.length,
    meanFrameDeltaInsideBlock:mean(interior),meanFrameDeltaAtBlockBoundary:mean(boundary),
    raf:{callbacks:timing.callbacks,medianMs:timing.medianMs,p95Ms:timing.p95Ms,maxMs:timing.maxMs,over40ms:timing.over40ms},
    assertions:'PASS'};
  writeFileSync(join(output,'visual-summary.json'),JSON.stringify(summary,null,2)+'\n');
  const ffmpeg=process.env.FFMPEG || 'ffmpeg';
  function encode(params){const r=spawnSync(ffmpeg,['-hide_banner','-loglevel','error',...params],{encoding:'utf8'});assert.equal(r.status,0,r.stderr);}
  encode(['-framerate','60','-i',join(png,'%04d.png'),'-c:v','libx264','-pix_fmt','yuv420p','-movflags','+faststart',join(output,'waveform-synthetic.mp4')]);
  // Twelve adjacent frames cross a microphone-block boundary. Separate wider
  // spacing shows the complete phrase and return to silence.
  encode(['-i',join(output,'waveform-synthetic.mp4'),'-vf',"select='between(n,229,240)',scale=390:224,tile=4x3",'-frames:v','1',join(output,'subframe-contact-sheet.png')]);
  encode(['-i',join(output,'waveform-synthetic.mp4'),'-vf',"fps=1,scale=390:224,tile=4x4",'-frames:v','1',join(output,'phrase-contact-sheet.png')]);
  console.log(JSON.stringify(summary,null,2));
}finally{await context.close();await browser.close();}
