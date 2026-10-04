// Runs real Android framework tests before the live JNI test against the isolated ASP.NET fixture.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtempSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('../../', import.meta.url));
const [buildType = 'debug'] = process.argv.slice(2);
const work = mkdtempSync(join(tmpdir(), 'aegis-android-native-'));
const code = join(work, 'bootstrap.txt');
const api = spawn('dotnet', ['run','--project',resolve(root,'backend/tests/Aegis.NodeRuntimeFixture'),'--',code], {cwd:root,stdio:'inherit'});
const complete = child => new Promise((resolve,reject) => {child.once('error',reject); child.once('exit',resolve);});
let test;
try {
  const end = Date.now()+120000;
  while (true) {
    assert(api.exitCode === null, 'Isolated backend exited');
    try {if (existsSync(code) && (await fetch('http://127.0.0.1:18104/api/health')).ok) break;} catch {}
    assert(Date.now()<end,'Isolated backend readiness timeout');
    await new Promise(r=>setTimeout(r,100));
  }
  test = spawn(process.execPath, [resolve(root,'apps/aegis-node/scripts/android-notification-framework.mjs'),buildType,code], {cwd:root,stdio:'inherit'});
  assert.equal(await complete(test),0,'Android native notification regression failed');
} finally {
  for (const child of [test,api]) if(child && child.exitCode === null) {child.kill('SIGTERM'); await complete(child);}
  rmSync(work,{recursive:true,force:true});
}
