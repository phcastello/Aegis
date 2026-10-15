// Emulator-only orchestration: disposable backend and bootstrap, no production credentials.
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdtempSync,existsSync,rmSync,mkdirSync} from 'node:fs';
import {tmpdir} from 'node:os';
import {join,resolve,dirname} from 'node:path';
import {fileURLToPath} from 'node:url';
const root=fileURLToPath(new URL('../../',import.meta.url));
const [mode,apk,testApk,output]=process.argv.slice(2);
assert(['before','after'].includes(mode)&&apk&&testApk&&output,'mode APK TEST_APK outputJSON required');
const work=mkdtempSync(join(tmpdir(),'aegis-android-idle-'));const code=join(work,'bootstrap.txt');
const dll=resolve(root,'backend/tests/Aegis.NodeRuntimeFixture/bin/Debug/net8.0/Aegis.NodeRuntimeFixture.dll');
assert(existsSync(dll),'Build isolated runtime fixture before measuring');
mkdirSync(dirname(resolve(output)),{recursive:true});
const done=child=>new Promise((res,rej)=>{child.once('error',rej);child.once('exit',res);});
const api=spawn('dotnet',[dll,code],{cwd:root,env:{...process.env,AEGIS_IDLE_FIXTURE:mode},stdio:['ignore','pipe','pipe']});
const apiDone=done(api);let test;
try {
 const end=Date.now()+60000;
 while(true){assert(api.exitCode===null,'Disposable backend exited');try{if(existsSync(code)&&(await fetch('http://127.0.0.1:18104/api/health',{signal:AbortSignal.timeout(2000)})).ok)break;}catch{}assert(Date.now()<end,'Disposable backend readiness timeout');await new Promise(r=>setTimeout(r,100));}
 test=spawn(process.execPath,[resolve(root,'apps/aegis-node/scripts/android-idle.mjs'),mode,resolve(apk),resolve(testApk),code,resolve(output)],{cwd:root,stdio:'inherit'});
 assert.equal(await done(test),0,'Android idle measurement failed');
}finally{
 if(test&&test.exitCode===null)test.kill('SIGTERM');
 if(api.exitCode===null)api.kill('SIGTERM');
 await Promise.race([apiDone,new Promise(r=>setTimeout(r,5000))]);
 if(api.exitCode===null)api.kill('SIGKILL');
 rmSync(work,{recursive:true,force:true});
}
