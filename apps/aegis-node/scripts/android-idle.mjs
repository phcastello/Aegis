// Disposable emulator only. Same target-independent instrumentation measures accepted/candidate APKs.
import assert from 'node:assert/strict';
import {spawn,spawnSync} from 'node:child_process';
import {readFileSync,writeFileSync,existsSync} from 'node:fs';
import {resolve} from 'node:path';
const [mode,apk,testApk,bootstrap,output]=process.argv.slice(2);
assert(['before','after'].includes(mode)&&apk&&testApk&&bootstrap&&output,'mode APK TEST_APK bootstrap outputJSON required');
assert(existsSync(apk)&&existsSync(testApk),'Both APK artifacts must exist before measuring');
const expectedNodeId=process.env.AEGIS_IDLE_EXPECT_NODE_ID;
if(mode==='after')assert(/^[a-f0-9-]{36}$/.test(expectedNodeId??''),'Upgrade requires the previous disposable NodeId');
const expectedVersion=mode==='before'?'0.7.0-unstable.11':JSON.parse(readFileSync(new URL('../package.json',import.meta.url))).version;
const serial=process.env.AEGIS_TEST_EMULATOR_SERIAL??'emulator-5554';assert(/^emulator-\d{4}$/.test(serial),'Disposable emulator required');
const adb=process.env.ANDROID_HOME?resolve(process.env.ANDROID_HOME,'platform-tools/adb'):'adb';
const command=args=>{const r=spawnSync(adb,['-s',serial,...args],{encoding:'utf8',timeout:30000});assert.equal(r.status,0,'adb operation failed');return r.stdout;};
const wait=ms=>new Promise(r=>setTimeout(r,ms));
const api=Number(command(['shell','getprop','ro.build.version.sdk']).trim());assert.equal(api,35,'Idle comparison uses the same API35 emulator');
let rooted=false;try{rooted=!/cannot run as root/i.test(command(['root']));command(['wait-for-device']);}catch{}
if(mode==='before'){
 try{command(['uninstall','com.aegis.node.test']);}catch{}
 try{command(['uninstall','com.aegis.node']);}catch{}
}
command(['install','-r',resolve(apk)]);command(['install','-r',resolve(testApk)]);
command(['reverse','tcp:18104','tcp:18104']);command(['shell','pm','grant','com.aegis.node','android.permission.POST_NOTIFICATIONS']);command(['logcat','-c']);
let ticksPerSecond=100;try{const value=Number(command(['shell','getconf','CLK_TCK']).trim());if(value>0)ticksPerSecond=value;}catch{}
const machine={api,rooted,ticksPerSecond,model:command(['shell','getprop','ro.product.model']).trim(),hardware:command(['shell','getprop','ro.hardware']).trim(),abis:command(['shell','getprop','ro.product.cpu.abilist']).trim(),logicalCpus:command(['shell','getconf','_NPROCESSORS_ONLN']).trim()};
const processSnapshot=()=>{
 const lines=command(['shell','ps','-A','-o','PID,NAME']).trim().split('\n').slice(1);
 const names=new Map(lines.map(line=>line.trim().split(/\s+/)).filter(([pid,name])=>/^\d+$/.test(pid)&&(/^com\.aegis\.node(?::|$)/.test(name)||/webview.*sandboxed|sandboxed.*webview/i.test(name))).map(([pid,name])=>[pid,name]));
 if(!names.size)return {available:false,reason:'process_not_observed',processes:[]};
 const rows=command(['shell',`for aegis_pid in ${[...names.keys()].join(' ')}; do if [ -r /proc/$aegis_pid/stat ]; then cat /proc/$aegis_pid/stat 2>/dev/null; fi; done`]);
 const processes=[];
 for(const line of rows.trim().split('\n')){const match=line.match(/^(\d+) \(.*\) (.+)$/);if(!match||!names.has(match[1]))continue;const fields=match[2].split(/\s+/);if(fields.length<20)continue;processes.push({pid:Number(match[1]),name:names.get(match[1]),kind:/^com\.aegis\.node(?::|$)/.test(names.get(match[1]))?'app':'webview',cpuTicks:Number(fields[11])+Number(fields[12]),startTicks:Number(fields[19])});}
 return {available:processes.length>0,reason:processes.length>0?null:'proc_access_unavailable',processes};
};
const snapshot=async()=>{
 const capturedAt=Date.now();let metrics;try{const response=await fetch('http://127.0.0.1:18104/fixture/idle');assert(response.ok);metrics=await response.json();}catch{metrics=null;}
 return {capturedAt,metrics,cpu:processSnapshot()};
};
const phases=new Map();let chain=Promise.resolve();let logBuffer='';
const logs=spawn(adb,['-s',serial,'logcat','-v','raw','-s','AegisIdleTest:I','*:S'],{stdio:['ignore','pipe','ignore']});
logs.stdout.setEncoding('utf8');logs.stdout.on('data',chunk=>{logBuffer+=chunk;const lines=logBuffer.split('\n');logBuffer=lines.pop();for(const line of lines){let event;try{event=JSON.parse(line);}catch{continue;}if(event.mode!==mode||!['start','end'].includes(event.event)||!['foreground_closed','devices_visible','diagnostics_visible','background'].includes(event.phase))continue;chain=chain.then(async()=>{const value=await snapshot();const phase=phases.get(event.phase)??{};phase[event.event]={event,...value};phases.set(event.phase,phase);console.log(`Android idle ${mode} ${event.phase} ${event.event}`);});}});
const code=readFileSync(bootstrap,'utf8').trim();assert(/^\d{6,12}$|^[A-Z0-9-]{4,32}$/i.test(code),'Pairing code shape invalid');
const instrumentation=spawn(adb,['-s',serial,'shell','am','instrument','-w','-r','-e','class','com.aegis.node.NodeIdleFrameworkTest','-e','waitForActivitiesToComplete','false','-e','idleMode',mode,'-e','phaseDurationMs','150000','-e','pairingCode',code,'-e','expectedNodeId',expectedNodeId??'', '-e','expectedVersion',expectedVersion,'com.aegis.node.test/androidx.test.runner.AndroidJUnitRunner'],{stdio:['ignore','pipe','pipe']});
let results='';instrumentation.stdout.setEncoding('utf8');instrumentation.stdout.on('data',chunk=>{results+=chunk;});instrumentation.stderr.on('data',()=>{});
let exitCode;
try{exitCode=await new Promise((res,rej)=>{instrumentation.once('error',rej);instrumentation.once('exit',res);});await wait(200);await chain;}finally{logs.kill('SIGTERM');command(['shell','am','force-stop','com.aegis.node']);}
const cpuDelta=(start,end,kind,seconds)=>{
 if(!start.available||!end.available)return {cpuSeconds:null,percentOneCore:null,reason:'proc_access_or_process_unavailable'};
 const first=start.processes.filter(p=>p.kind===kind);const last=end.processes.filter(p=>p.kind===kind);if(!first.length&&!last.length)return {cpuSeconds:null,percentOneCore:null,reason:'process_not_observed'};
 let ticks=0;let matched=0;for(const p of last){const original=first.find(x=>x.pid===p.pid&&x.startTicks===p.startTicks);if(original){ticks+=Math.max(0,p.cpuTicks-original.cpuTicks);matched++;}}
 return {cpuSeconds:matched?ticks/ticksPerSecond:null,percentOneCore:matched?100*ticks/ticksPerSecond/seconds:null,partial:matched!==first.length||matched!==last.length,matchedProcessCount:matched,startProcessCount:first.length,endProcessCount:last.length};
};
const windows=[];
for(const [name,value] of phases){if(!value.start||!value.end)continue;const seconds=(value.end.capturedAt-value.start.capturedAt)/1000;const metrics={};if(value.start.metrics&&value.end.metrics)for(const key of new Set([...Object.keys(value.start.metrics),...Object.keys(value.end.metrics)])){const delta=(value.end.metrics[key]??0)-(value.start.metrics[key]??0);metrics[key]={delta,perMinute:delta*60/seconds};}windows.push({phase:name,seconds,androidDurationMs:value.end.event.elapsedRealtimeMs-value.start.event.elapsedRealtimeMs,ui:value.start.event.ui,pushConfigured:value.start.event.pushConfigured,cpu:{app:cpuDelta(value.start.cpu,value.end.cpu,'app',seconds),webview:cpuDelta(value.start.cpu,value.end.cpu,'webview',seconds)},metrics,start:value.start,end:value.end});}
const nodeId=windows[0]?.start?.event?.nodeId;
const report={schema:2,mode,nodeId,expectedNodeId,expectedVersion,machine,scope:{traffic:'Fixture HTTP body and WebSocket JSON payload counters; excludes TCP/TLS, other applications and RDP.',cpu:'App process includes instrumentation thread; measurements use process /proc stat CPU ticks. WebView lists provider sandboxed processes observed in an otherwise isolated emulator; per-app mapping is not asserted.',fcm:'Registration metrics apply only if pushConfigured is true. No Firebase project or token is invented.',timers:'Existing timer registrations are not enumerable through public WebView APIs; no timer count is fabricated.'},windows,totalMeasuredSeconds:windows.reduce((n,w)=>n+w.androidDurationMs/1000,0),totalSnapshotSeconds:windows.reduce((n,w)=>n+w.seconds,0),instrumentationPassed:exitCode===0&&/OK \(1 test\)/.test(results)&&!/FAILURES!!!|INSTRUMENTATION_FAILED/.test(results)};
writeFileSync(output,JSON.stringify(report,null,2)+'\n');
assert(report.instrumentationPassed,'Idle framework instrumentation failed; inspect safe checkpoints');assert.equal(windows.length,4,'Four idle windows required');assert(/^[a-f0-9-]{36}$/.test(nodeId??''),'Disposable identity must be reported');assert(windows.every(w=>w.start.event.nodeId===nodeId&&w.end.event.nodeId===nodeId),'Identity must remain stable');if(mode==='after')assert.equal(nodeId,expectedNodeId,'APK upgrade must preserve the Node identity');assert(windows.every(w=>w.androidDurationMs>=150000),'Each window must measure at least150s');assert(report.totalMeasuredSeconds>=600,'At least10min equivalent measurement required');
console.log(`Android idle ${mode}: ${report.totalMeasuredSeconds.toFixed(1)}s, report ${output}`);
