// Emulator-only, six independent ten-minute windows. Never targets a physical Node.
import assert from 'node:assert/strict';
import {spawn, spawnSync} from 'node:child_process';
import {existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join, resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
import {adbFailure,ensureAndroidFrameworkRoot} from '../../apps/aegis-node/scripts/android-framework-host.mjs';
const root=fileURLToPath(new URL('../../',import.meta.url));
const [apk,testApk,outputDirectory]=process.argv.slice(2);
for(const file of [apk,testApk])assert(file&&existsSync(file),'Candidate and instrumentation APKs required');
assert(outputDirectory,'Output directory required');
const output=resolve(outputDirectory);mkdirSync(output,{recursive:true});
const serial=process.env.AEGIS_TEST_EMULATOR_SERIAL??'emulator-5554';
assert(/^emulator-\d{4}$/.test(serial),'Disposable emulator required');
const adb=process.env.ANDROID_HOME?resolve(process.env.ANDROID_HOME,'platform-tools/adb'):'adb';
const command=(args,timeout=30000)=>{const r=spawnSync(adb,['-s',serial,...args],{encoding:'utf8',timeout});if(r.status!==0)throw adbFailure(args,r);return r.stdout;};
const done=child=>new Promise((res,rej)=>{child.once('error',rej);child.once('exit',res);});
const work=mkdtempSync(join(tmpdir(),'aegis-hybrid-efficiency-'));const code=join(work,'bootstrap.txt');
const dll=resolve(root,'backend/tests/Aegis.NodeRuntimeFixture/bin/Debug/net8.0/Aegis.NodeRuntimeFixture.dll');
assert(existsSync(dll),'Build isolated backend fixture first');
const api=spawn('dotnet',[dll,code],{cwd:root,env:{...process.env,AEGIS_IDLE_FIXTURE:'after'},stdio:['ignore','pipe','pipe']});
const apiDone=done(api);let logs,test,watchdog;
const boundaries=[];let chain=Promise.resolve();let outputText='';let boundaryFailure=false;let timedOut=false;
const report={schema:1,version:JSON.parse(readFileSync(resolve(root,'apps/aegis-node/package.json'))).version,providerDelivery:'actual FLP getCurrentLocation in session_active/background_saver; controlled provider fixes in approach/control; actual Android service lifecycle',limitations:['emulator CPU, not physical battery consumption','HTTP/WS payload bytes exclude TLS/TCP and Google Play services traffic','active segment is bounded to three minutes; ten-minute window includes cleanup and idle remainder','armed regions registered through GeofencingClient; movement callbacks are controlled'],windows:[]};
try {
 const deadline=Date.now()+60000;
 while(true){assert(api.exitCode===null,'Isolated backend exited');try{if(existsSync(code+'.geofence')&&(await fetch('http://127.0.0.1:18104/api/health',{signal:AbortSignal.timeout(2000)})).ok)break;}catch{}assert(Date.now()<deadline,'Fixture readiness timeout');await new Promise(r=>setTimeout(r,100));}
 assert.equal(Number(command(['shell','getprop','ro.build.version.sdk']).trim()),35,'Use API35 for the six efficiency windows');
 try{command(['uninstall','com.aegis.node.test']);}catch{}
 try{command(['uninstall','com.aegis.node']);}catch{}
 command(['install','-r',resolve(apk)]);command(['install','-r',resolve(testApk)]);
 await ensureAndroidFrameworkRoot(command);
 command(['emu','geo','fix','0.01','0']);
 command(['reverse','tcp:18104','tcp:18104']);command(['shell','pm','grant','com.aegis.node','android.permission.POST_NOTIFICATIONS']);command(['logcat','-c']);
 const ticksPerSecond=Number(command(['shell','getconf','CLK_TCK']).trim())||100;
 report.machine={api:35,model:command(['shell','getprop','ro.product.model']).trim(),hardware:command(['shell','getprop','ro.hardware']).trim(),ticksPerSecond};
 const snapshot=async()=>{
  const rows=command(['shell','ps','-A','-o','PID,NAME']).trim().split('\n').slice(1).map(line=>line.trim().split(/\s+/)).filter(([pid,name])=>/^\d+$/.test(pid)&&/^com\.aegis\.node(?::|$)/.test(name));
  const names=new Set(rows.map(([pid])=>pid));
  const stats=rows.length?command(['shell',`for aegis_pid in ${[...names].join(' ')}; do cat /proc/$aegis_pid/stat 2>/dev/null; done`]):'';
  const cpu=[];
  for(const line of stats.trim().split('\n')){const m=line.match(/^(\d+) \(.*\) (.+)$/);if(!m||!names.has(m[1]))continue;const f=m[2].split(/\s+/);cpu.push({pid:Number(m[1]),ticks:Number(f[11])+Number(f[12]),started:Number(f[19])});}
  const response=await fetch('http://127.0.0.1:18104/fixture/idle',{signal:AbortSignal.timeout(3000)});assert(response.ok);
  return {capturedAt:Date.now(),cpu,metrics:await response.json()};
 };
 const phases=new Set(['idle_no_geofences','armed_stationary','approach','session_active','session_ended','background_saver']);
 logs=spawn(adb,['-s',serial,'logcat','-v','raw','-s','AegisHybridMeasure:I','*:S'],{stdio:['ignore','pipe','ignore']});
 let buffer='';logs.stdout.setEncoding('utf8');logs.stdout.on('data',chunk=>{
  buffer+=chunk;const lines=buffer.split('\n');buffer=lines.pop();
  for(const line of lines){let event;try{event=JSON.parse(line);}catch{continue;}
   if(!phases.has(event.phase)||!['start','end','active_start','active_end'].includes(event.event)||!/^[a-f0-9-]{36}$/.test(event.snapshotNonce??''))continue;
   chain=chain.then(async()=>{const value=await snapshot();assert(value.cpu.length,'Target CPU must remain observable');boundaries.push({event,...value});command(['shell','rm','-f',`/data/user/0/com.aegis.node/cache/aegis-idle-snapshot-${event.snapshotNonce}.ack`]);console.log(`Hybrid efficiency ${event.phase} ${event.event}`);}).catch(()=>{boundaryFailure=true;console.error('Hybrid efficiency boundary capture failed');});
  }
 });
 const pairing=readFileSync(code+'.geofence','utf8').trim();assert(/^\d{6,12}$|^[A-Z0-9-]{4,32}$/i.test(pairing));
 test=spawn(adb,['-s',serial,'shell','am','instrument','-w','-r','-e','class','com.aegis.node.NodeHybridEfficiencyFrameworkTest','-e','geofencePairingCode',pairing,'-e','hybridWindowMs','600000','com.aegis.node.test/androidx.test.runner.AndroidJUnitRunner'],{stdio:['ignore','pipe','ignore']});
 test.stdout.setEncoding('utf8');test.stdout.on('data',chunk=>{outputText+=chunk;});
 watchdog=setTimeout(()=>{timedOut=true;test.kill('SIGTERM');},75*60*1000);
 const exitCode=await done(test);await new Promise(r=>setTimeout(r,200));await chain;
 report.instrumentationPassed=exitCode===0&&/OK \(1 test\)/.test(outputText)&&!/FAILURES!!!|INSTRUMENTATION_FAILED/.test(outputText);
 const delta=(start,end)=>{
  const seconds=(end.event.elapsedRealtimeMs-start.event.elapsedRealtimeMs)/1000;
  const matched=end.cpu.map(p=>({p,old:start.cpu.find(x=>x.pid===p.pid&&x.started===p.started)})).filter(x=>x.old);
  const cpuSeconds=matched.reduce((n,{p,old})=>n+Math.max(0,p.ticks-old.ticks)/ticksPerSecond,0);
  const network={};for(const key of new Set([...Object.keys(start.metrics),...Object.keys(end.metrics)]))if(Number.isFinite(start.metrics[key]??0)&&Number.isFinite(end.metrics[key]??0))network[key]=(end.metrics[key]??0)-(start.metrics[key]??0);
  const attempts=event=>(event.session.sessions??[]).reduce((n,s)=>n+(s.attempts??0),0);
  const acquired=event=>(event.session.sessions??[]).reduce((n,s)=>n+(s.acquired??0),0);
  return {seconds,cpuSeconds:matched.length?cpuSeconds:null,cpuPercentOneCore:matched.length?100*cpuSeconds/seconds:null,cpuPartial:matched.length!==start.cpu.length||matched.length!==end.cpu.length,network,acquisitions:attempts(end.event)-attempts(start.event),locationsAcquired:acquired(end.event)-acquired(start.event),serviceStarts:end.event.session.serviceStarts-start.event.session.serviceStarts,serviceStops:end.event.session.serviceStops-start.event.session.serviceStops};
 };
 for(const phase of phases){const find=name=>boundaries.find(b=>b.event.phase===phase&&b.event.event===name);const start=find('start'),end=find('end');if(!start||!end)continue;const activeStart=find('active_start'),activeEnd=find('active_end');report.windows.push({phase,...delta(start,end),...(activeStart&&activeEnd?{activeSegment:delta(activeStart,activeEnd)}:{}),sessionAtEnd:end.event.session});}
 report.boundaryFailure=boundaryFailure;report.timedOut=timedOut;report.measurementsComplete=report.windows.length===6&&report.windows.every(w=>w.seconds>=600&&w.cpuSeconds!==null);
 if(!report.instrumentationPassed)report.instrumentationOutput=outputText.slice(-24000);
 writeFileSync(join(output,'android-hybrid-efficiency.json'),JSON.stringify(report,null,2)+'\n');
 if(!report.instrumentationPassed)process.stdout.write(outputText.slice(-24000));
 assert(report.instrumentationPassed,'Hybrid efficiency framework test failed');assert(!boundaryFailure&&!timedOut,'Measurement lifecycle failed');assert(report.measurementsComplete,'Six complete ten-minute CPU/network windows required');
 for(const window of report.windows){assert(window.acquisitions<=6,'Acquisitions exceed session budget');assert(!window.sessionAtEnd.running,'Service must finish');}
 console.log('Six hybrid efficiency windows PASS; inspect active segments separately from idle remainder.');
}finally {
 clearTimeout(watchdog);if(logs)logs.kill('SIGTERM');
 if(test&&test.exitCode===null)test.kill('SIGTERM');
 if(api.exitCode===null)api.kill('SIGTERM');
 await Promise.race([apiDone,new Promise(r=>setTimeout(r,5000))]);if(api.exitCode===null)api.kill('SIGKILL');
 try{command(['shell','am','force-stop','com.aegis.node']);}catch{}
 rmSync(work,{recursive:true,force:true});
}
