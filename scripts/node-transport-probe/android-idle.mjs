// Emulator-only comparison: one disposable database survives the actual APK upgrade.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdtempSync,existsSync,rmSync,mkdirSync,readFileSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join,resolve} from 'node:path';
import {fileURLToPath} from 'node:url';
const root=fileURLToPath(new URL('../../',import.meta.url));
const [beforeApk,afterApk,testApk,outputDirectory]=process.argv.slice(2);
assert(beforeApk&&afterApk&&testApk&&outputDirectory,'BEFORE_APK AFTER_APK TEST_APK OUTPUT_DIRECTORY required');
for(const file of [beforeApk,afterApk,testApk])assert(existsSync(file),'APK artifact missing');
const output=resolve(outputDirectory);mkdirSync(output,{recursive:true});
const work=mkdtempSync(join(tmpdir(),'aegis-android-idle-'));const code=join(work,'bootstrap.txt');
const dll=resolve(root,'backend/tests/Aegis.NodeRuntimeFixture/bin/Debug/net8.0/Aegis.NodeRuntimeFixture.dll');
assert(existsSync(dll),'Build isolated runtime fixture before measuring');
const done=child=>new Promise((res,rej)=>{child.once('error',rej);child.once('exit',res);});
const api=spawn('dotnet',[dll,code],{cwd:root,env:{...process.env,AEGIS_IDLE_FIXTURE:'before'},stdio:['ignore','pipe','pipe']});
const apiDone=done(api);let test;
async function measure(mode,apk,expectedNodeId){
 test=spawn(process.execPath,[resolve(root,'apps/aegis-node/scripts/android-idle.mjs'),mode,resolve(apk),resolve(testApk),code,join(output,`android-idle-${mode}.json`)],{cwd:root,env:{...process.env,...(expectedNodeId?{AEGIS_IDLE_EXPECT_NODE_ID:expectedNodeId}:{})},stdio:'inherit'});
 assert.equal(await done(test),0,'Android idle measurement failed');
}
try {
 const end=Date.now()+60000;
 while(true){assert(api.exitCode===null,'Disposable backend exited');try{if(existsSync(code)&&(await fetch('http://127.0.0.1:18104/api/health',{signal:AbortSignal.timeout(2000)})).ok)break;}catch{}assert(Date.now()<end,'Disposable backend readiness timeout');await new Promise(r=>setTimeout(r,100));}
 await measure('before',beforeApk);
 const before=JSON.parse(readFileSync(join(output,'android-idle-before.json')));
 const changed=await fetch('http://127.0.0.1:18104/fixture/idle-policy',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({mode:'after'}),signal:AbortSignal.timeout(5000)});assert(changed.ok,'Fixture policy change failed');
 await measure('after',afterApk,before.nodeId);
}finally{
 if(test&&test.exitCode===null)test.kill('SIGTERM');
 if(api.exitCode===null)api.kill('SIGTERM');
 await Promise.race([apiDone,new Promise(r=>setTimeout(r,5000))]);
 if(api.exitCode===null)api.kill('SIGKILL');
 rmSync(work,{recursive:true,force:true});
}
