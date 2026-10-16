// The actual Rust HttpApi against real ASP.NET controllers/dispatcher/registry.
// Isolated bootstrap + memory database; no production Node or FCM endpoint.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = fileURLToPath(new URL('../../',import.meta.url));
const work = mkdtempSync(join(tmpdir(),'aegis-native-http-'));
const code = join(work,'bootstrap.txt');
const api = spawn('dotnet',['run','--project',resolve(root,'backend/tests/Aegis.NodeRuntimeFixture'),'--',code],{cwd:root,stdio:'inherit'});
let test;
const completion = child => new Promise((resolve,reject) => { child.once('error',reject); child.once('exit',resolve); });
try {
  const end = Date.now()+120000;
  while (true) {
    if (api.exitCode !== null) throw Error('Isolated backend fixture exited');
    try { if (existsSync(code) && (await fetch('http://127.0.0.1:18104/api/health')).ok) break; } catch {}
    assert(Date.now()<end,'Isolated backend readiness timeout');
    await new Promise(r=>setTimeout(r,100));
  }
  test = spawn('cargo',['test','--manifest-path',resolve(root,'apps/aegis-node/src-tauri/Cargo.toml'),'--locked','--no-default-features','--lib','http_live_notification_and_push_reach_real_backend','--','--ignored','--nocapture'],{cwd:root,env:{...process.env,AEGIS_NATIVE_HTTP_FIXTURE_CODE:code},stdio:'inherit'});
  assert.equal(await completion(test),0,'Native HttpApi/controller regression failed');
} finally {
  for (const child of [test,api]) if (child && child.exitCode === null) {
    if (process.platform==='win32') spawnSync('taskkill',['/PID',String(child.pid),'/T','/F'],{stdio:'ignore'});
    else { child.kill('SIGTERM'); await completion(child); }
  }
  rmSync(work,{recursive:true,force:true});
}
