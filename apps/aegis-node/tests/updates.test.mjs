import assert from 'node:assert/strict';
import test from 'node:test';
import { validateWindowsUpdate } from '../.test-build/services/updateMetadata.js';
import { useUpdates } from '../.test-build/composables/useUpdates.js';
import { updateFailure } from '../.test-build/services/updateDiagnostics.js';

test('automatic update checks are coalesced and do not interrupt chat on no update/network failure', async () => {
  for (const check of [async () => null, async () => { throw new Error('offline'); }]) {
    let calls = 0;
    const state = useUpdates(async () => { calls++; return check(); });
    await Promise.all([state.start(), state.start()]);
    await state.start();
    assert.equal(calls, 1); assert.equal(state.visible.value, false);
  }
});

test('updates require a user action and progress/error can be retried without parallel installs', async () => {
  let installs = 0;
  let fail = true;
  const state = useUpdates(async () => ({ version: '0.7.0-stage.3', kind: 'installer', async install(progress) {
    installs++; progress(2, 4);
    if (fail) throw new Error('download failed');
  } }));
  await state.start();
  assert.equal(state.state.value, 'available'); assert.equal(installs, 0);
  await Promise.all([state.install(), state.install()]);
  assert.equal(installs, 1); assert.equal(state.progress.value, 50); assert.equal(state.state.value, 'error');
  fail = false; await state.install();
  assert.equal(state.state.value, 'opened'); assert.equal(installs, 2);
  state.dismiss(); assert.equal(state.visible.value, false);
});

test('declining a version never installs it or checks again', async () => {
  const state = useUpdates(async () => ({ version: '0.7.0-stage.3', kind: 'apk', install: async () => assert.fail('unexpected install') }));
  await state.start(); state.dismiss(); await state.start();
  assert.equal(state.visible.value, false);
});

test('Windows updates accept only the exact HTTPS versioned installer from this repository', () => {
  const url = 'https://github.com/phcastello/Aegis/releases/download/node-v0.7.0-stage.3/Aegis-Windows-x86_64-Setup.exe';
  validateWindowsUpdate({ platforms: { 'windows-x86_64': { url } } }, '0.7.0-stage.3');
  for (const bad of [url.replace('https:', 'http:'), url.replace('phcastello', 'other'), url.replace('stage.3', 'stage.2'), 'file:///tmp/app.exe'])
    assert.throws(() => validateWindowsUpdate({ platforms: { 'windows-x86_64': { url: bad } } }, '0.7.0-stage.3'));
  assert.throws(() => validateWindowsUpdate({}, '0.7.0-stage.3'));
});

test('manual checks can recover after the automatic check without parallel requests or installation', async () => {
  let calls = 0, release;
  const state = useUpdates(async () => { calls++; if (calls === 1) return null; await new Promise(resolve => { release = resolve; }); return { version: '0.7.0-unstable.12', kind: 'apk', install: async () => assert.fail('checking must never install') }; });
  await state.start(); assert.match(state.checkedMessage.value, /mais recente/);
  const pending = state.start(true); await state.start(true); assert.equal(calls, 2);
  release(); await pending; assert.equal(state.state.value, 'available'); assert.equal(state.update.value.version, '0.7.0-unstable.12');
});

test('Android install permission is a recoverable step and never reports installer opened early', async () => {
  let calls = 0;
  const state = useUpdates(async () => ({ version:'0.7.0-unstable.12',kind:'apk',install:async () => ({status:++calls===1?'permission_required':'installer_opened'}) }));
  await state.start(); await state.install(); assert.equal(state.state.value,'permissionRequired');assert.equal(state.visible.value,true);
  await state.install(); assert.equal(state.state.value,'opened');assert.equal(calls,2);
});

test('Windows download completes before a separate user action hands off the installer', async () => {
  let downloads=0,installs=0;
  const state=useUpdates(async()=>({version:'0.7.0-unstable.12',kind:'installer',prepare:async progress=>{downloads++;progress(4,4);},install:async()=>{installs++;}}));
  await state.start();await state.install();assert.equal(downloads,1);assert.equal(installs,0);assert.equal(state.state.value,'ready');assert.equal(state.readyToInstall.value,true);
  await state.install();assert.equal(downloads,1);assert.equal(installs,1);assert.equal(state.state.value,'opened');
});

test('foreground or opening Updates discovers a release published during the same long session without polling', async () => {
  const originalNow = Date.now; let now = 100000; Date.now = () => now;
  try {
    let calls = 0;
    const state = useUpdates(async () => ++calls === 1 ? null : ({ version:'0.7.0-unstable.14', kind:'apk', install: async () => assert.fail('no silent installation') }));
    await state.start(); assert.equal(state.state.value,'idle');
    now += 29 * 60000; await state.start(); assert.equal(calls,1);
    now += 60000; await state.start(); assert.equal(calls,2); assert.equal(state.visible.value,true);
    state.dismiss(); now += 30 * 60000; await state.start(); assert.equal(calls,3); assert.equal(state.visible.value,false);
    await state.start(true); assert.equal(state.visible.value,true);
  } finally { Date.now = originalNow; }
});

test('failed automatic discovery has a safe code and retries on a later event after a bounded cooldown', async () => {
  const originalNow = Date.now; let now = 200000; Date.now = () => now;
  try {
    let calls = 0;
    const state = useUpdates(async () => { if (++calls === 1) throw 'update_feed_missing'; return null; });
    await state.start(); assert.equal(state.visible.value,false); assert.equal(state.diagnostic.value.code,'update_feed_missing');
    assert.match(state.checkedMessage.value,/não está disponível/); assert.equal(state.checkedAt.value,now);
    now += 59000; await state.start(); assert.equal(calls,1);
    now += 1000; await state.start(); assert.equal(calls,2); assert.equal(state.diagnostic.value,null);
  } finally { Date.now = originalNow; }
});

test('native verification failures keep their phase and never report installer opened', async () => {
  const state = useUpdates(async () => ({ version:'0.7.0-unstable.14', kind:'apk', install: async () => ({status:'failed',phase:'verification',code:'update_signature_mismatch'}) }));
  await state.start(); await state.install(); assert.equal(state.state.value,'error');
  assert.equal(state.diagnostic.value.phase,'verification'); assert.equal(state.diagnostic.value.code,'update_signature_mismatch');
  assert.match(state.error.value,/assinatura/);
  const safe = updateFailure(new Error('secret https://private.example/token'), 'discovery');
  assert.equal(safe.code,'update_check_failed'); assert.doesNotMatch(safe.message,/secret|private/);
  for (const unsafe of ['toString','constructor','__proto__']) assert.equal(updateFailure(unsafe,'discovery').code,'update_check_failed');
});

test('a failed refresh retains the known update and a repeated discovery retains a prepared installer', async () => {
  let calls = 0, disposals = 0, downloads = 0;
  const state = useUpdates(async () => { if (++calls === 2) throw 'update_timeout'; return { version:'0.7.0-unstable.14',kind:'installer',prepare:async () => {downloads++;},dispose:async () => {disposals++;},install:async () => undefined }; });
  await state.start(); const handle = state.update.value;
  await state.start(true); assert.equal(state.visible.value,true); assert.equal(state.update.value,handle); assert.equal(disposals,0);
  assert.equal(state.diagnostic.value.code,'update_timeout');
  await state.install(); assert.equal(downloads,1); assert.equal(state.state.value,'ready');
  await state.start(true); assert.equal(disposals,1); assert.equal(state.update.value,handle); assert.equal(state.readyToInstall.value,true); assert.equal(state.state.value,'ready');
});
