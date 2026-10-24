import assert from 'node:assert/strict';
import { test, afterEach } from 'node:test';
import { mockIPC, clearMocks } from '@tauri-apps/api/mocks';
import { checkUpdate } from '../.test-build/services/updates.js';
afterEach(() => { if (globalThis.window) clearMocks(); });
import { validateWindowsUpdate, compareUpdateVersions } from '../.test-build/services/updateMetadata.js';
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

function manifest(version, versionCode, extra = {}) {
  const base = `https://github.com/phcastello/Aegis/releases/download/node-v${version}/`;
  return { schema: 2, version, versionCode, kind: 'normal', sourceCommit: 'a'.repeat(40), sourceVersion: '0.7.2',
    publishedAt: '2026-10-23T00:00:00.000Z', pub_date: '2026-10-23T00:00:00.000Z', compatibility: { api: 1, nodeData: 1 },
    platforms: { 'windows-x86_64': { url: base + 'Aegis-Windows-x86_64-Setup.exe', signature: 'fixture-not-installable', sha256: 'b'.repeat(64) } },
    android: { aarch64: { url: base + 'Aegis-Android-arm64.apk', versionCode, sha256: 'c'.repeat(64) } }, ...extra };
}
const installed = { version: '0.7.2-unstable.4', versionCode: 7002004 };

test('recommended metadata accepts preview, stable and explicit recovery with independent installation numbers', () => {
  for (const [version, versionCode, extra] of [
    ['0.7.2-unstable.5', 7002005, {}],
    ['0.7.2-unstable.5.rollback.1', 7002006, { kind: 'rollback', rollbackOf: '0.7.2-unstable.5', restoresVersion: '0.7.2-unstable.4' }],
    ['0.7.2-unstable.5.rollback.2', 7002007, { kind: 'rollback', rollbackOf: '0.7.2-unstable.5.rollback.1', restoresVersion: '0.7.2-unstable.4' }],
    ['0.7.2-unstable.5.rollback.2', 7002007, { kind: 'rollback', rollbackOf: '0.7.2-unstable.5', restoresVersion: '0.7.2-unstable.4' }],
    ['0.7.2-unstable.5.rollback.3', 7002008, { kind: 'rollback', rollbackOf: '0.7.2-unstable.5.rollback.1', restoresVersion: '0.7.2-unstable.4' }],
    ['0.7.3-rollback.2', 7003002, { kind: 'rollback', rollbackOf: '0.7.2', restoresVersion: '0.7.1' }],
    ['0.7.2-unstable.6', 7002008, {}],
    ['0.7.2', 7002999, {}],
    ['0.7.3-rollback.1', 7003000, { kind: 'rollback', rollbackOf: '0.7.2', restoresVersion: '0.7.1' }],
  ]) {
    const details = validateWindowsUpdate(manifest(version, versionCode, extra), version, installed);
    assert.equal(details.version, version); assert.equal(details.versionCode, versionCode);
    assert.equal(details.distributionKind, extra.kind ?? 'normal');
  }
});

test('Windows metadata rejects downgrade, same version, old installation codes and incomplete contracts', () => {
  const good = manifest('0.7.2-unstable.5', 7002005);
  for (const patch of [
    { schema: 1 }, { versionCode: 7002004 }, { versionCode: 7002003 }, { versionCode: 2100000001 },
    { sourceCommit: 'unknown' }, { sourceVersion: 'v0.7.2' }, { publishedAt: 'yesterday' },
    { pub_date: '2026-10-24T00:00:00.000Z' }, { kind: 'rollback' }, { rollbackOf: installed.version },
    { compatibility: { api: 2, nodeData: 1 } }, { compatibility: { api: 1, nodeData: 2 } },
  ]) assert.throws(() => validateWindowsUpdate({ ...good, ...patch }, good.version, installed));
  for (const current of [{ version: good.version, versionCode: 7002004 }, { version: '0.7.2', versionCode: 7002004 }, { version: installed.version }])
    assert.throws(() => validateWindowsUpdate(good, good.version, current));
  for (const invalid of ['0.7.2-stage.5', '0.7.2+metadata', '0.7.2.rollback.1', '0.7.2-unstable.05', '0.7.2-unstable.5.rollback.1.rollback.1'])
    assert.throws(() => validateWindowsUpdate(manifest(invalid, 7002005), invalid, installed));
});

test('Windows updater binds all target URLs and checksums to exact distribution', () => {
  const good = manifest('0.7.2', 7002999);
  const windows = good.platforms['windows-x86_64'];
  for (const patch of [
    { url: windows.url.replace('https:', 'http:') }, { url: windows.url.replace('phcastello', 'other') },
    { url: windows.url.replace('0.7.2', '0.7.1') }, { url: 'file:///tmp/app.exe' }, { sha256: 'bad' }, { signature: '' }
  ]) assert.throws(() => validateWindowsUpdate({ ...good, platforms: { 'windows-x86_64': { ...windows, ...patch } } }, good.version, installed));
  assert.throws(() => validateWindowsUpdate({ ...good, android: { aarch64: { ...good.android.aarch64, versionCode: 7002998 } } }, good.version, installed));
  assert.throws(() => validateWindowsUpdate({ ...good, android: { aarch64: { ...good.android.aarch64, url: 'https://evil.example/node.apk' } } }, good.version, installed));
});

test('recovery metadata rejects a misleading kind, lower precedence or inconsistent event', () => {
  const version = '0.7.2-unstable.5.rollback.1';
  const good = manifest(version, 7002006, { kind: 'rollback', rollbackOf: '0.7.2-unstable.5', restoresVersion: '0.7.2-unstable.4' });
  for (const patch of [
    { kind: 'normal' }, { rollbackOf: '0.7.2-unstable.6' }, { restoresVersion: '0.7.2-unstable.5' },
    { rollbackOf: '0.7.2-unstable.5.rollback.1' }, { restoresVersion: undefined },
    { rollbackOf: '0.7.2-unstable.5+metadata' }, { restoresVersion: '0.7.0-stage.10' },
    { restoresVersion: '0.7.2-unstable.4+metadata' }, { sourceVersion: '0.7.2+metadata' }
  ]) assert.throws(() => validateWindowsUpdate({ ...good, ...patch }, version, installed));
});

test('SemVer order agrees across normal, successive recovery and stable recovery sequences', () => {
  for (const sequence of [
    ['0.7.2-unstable.4', '0.7.2-unstable.5', '0.7.2-unstable.5.rollback.1', '0.7.2-unstable.5.rollback.2', '0.7.2-unstable.6', '0.7.2'],
    ['0.7.2', '0.7.3-rollback.1', '0.7.3-rollback.2', '0.7.3-unstable.1', '0.7.3'],
    ['0.7.0-stage.10', '0.7.0-unstable.11', '0.7.0', '0.7.1']
  ]) for (let i = 1; i < sequence.length; i++) {
    assert.equal(compareUpdateVersions(sequence[i - 1], sequence[i]), -1);
    assert.equal(compareUpdateVersions(sequence[i], sequence[i - 1]), 1);
    assert.equal(compareUpdateVersions(sequence[i], sequence[i]), 0);
  }
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

test('discovery presents recovery explicitly and still requires a deliberate installation action', async () => {
  let installs = 0;
  const state = useUpdates(async () => ({ version: '0.7.2-unstable.5.rollback.1', kind: 'apk', distributionKind: 'rollback', rollbackOf: '0.7.2-unstable.5', restoresVersion: '0.7.2-unstable.4', install: async () => { installs++; } }));
  await state.start(); assert.match(state.checkedMessage.value, /recuperação/); assert.equal(installs, 0);
  assert.equal(state.update.value.restoresVersion, '0.7.2-unstable.4');
  await state.install(); assert.equal(installs, 1);
});

test('Windows discovery binds native preparation and verified bytes to a separate install action', async () => {
  globalThis.window = { crypto: globalThis.crypto };
  const calls = [], progress = [];
  const metadata = manifest('0.7.2-unstable.5', 7002005);
  mockIPC((command, args) => {
    calls.push([command, args]);
    if (command === 'plugin:updater|check') return { rid: 12, currentVersion: installed.version, version: metadata.version, rawJson: metadata };
    if (command === 'prepare_windows_update') {
      assert.equal(args.updateRid, 12);
      args.onProgress.onmessage({ received: 4, total: 4 });
      return 34;
    }
    if (command === 'install_windows_update') assert.deepEqual(args, { updateRid: 12, bytesRid: 34 });
  });
  const update = await checkUpdate({ platform: 'windows', backendUrl: null, ...installed });
  assert.equal(update.distributionKind, 'normal');
  assert.deepEqual(calls.map(([command]) => command), ['plugin:updater|check']);
  await assert.rejects(() => update.install(), /update_archive_invalid/);
  await update.prepare((received, total) => progress.push([received, total]));
  assert.deepEqual(progress, [[4, 4]]);
  assert.equal(calls.filter(([command]) => command === 'install_windows_update').length, 0);
  await update.install();
  assert.equal(calls.filter(([command]) => command === 'install_windows_update').length, 1);
  assert.equal(calls.some(([command]) => ['plugin:updater|download', 'plugin:updater|download_and_install', 'plugin:updater|install'].includes(command)), false);
  await update.dispose();
});

test('a native Windows checksum or signature rejection never exposes an installable resource', async () => {
  for (const failure of ['update_archive_invalid', 'update_signature_mismatch']) {
    globalThis.window = { crypto: globalThis.crypto };
    const calls = [];
    const metadata = manifest('0.7.2-unstable.5', 7002005);
    mockIPC((command, args) => {
      calls.push(command);
      if (command === 'plugin:updater|check') return { rid: 12, currentVersion: installed.version, version: metadata.version, rawJson: metadata };
      if (command === 'prepare_windows_update') throw failure;
      if (command === 'install_windows_update') assert.fail('unverified bytes must never install');
    });
    const update = await checkUpdate({ platform: 'windows', backendUrl: null, ...installed });
    const state = useUpdates(async () => update);
    await state.start(); await state.install();
    assert.equal(state.state.value, 'error'); assert.equal(state.readyToInstall.value, false);
    assert.equal(state.diagnostic.value.code, failure);
    await assert.rejects(() => update.install(), /update_archive_invalid/);
    assert.equal(calls.includes('install_windows_update'), false);
    await update.dispose(); clearMocks();
  }
});

test('closing an abandoned Windows download releases both verified bytes and updater handle', async () => {
  globalThis.window = { crypto: globalThis.crypto };
  const closed = [];
  const metadata = manifest('0.7.2-unstable.5', 7002005);
  mockIPC((command, args) => {
    if (command === 'plugin:updater|check') return { rid: 12, currentVersion: installed.version, version: metadata.version, rawJson: metadata };
    if (command === 'prepare_windows_update') return 34;
    if (command === 'plugin:resources|close') closed.push(args.rid);
  });
  const update = await checkUpdate({ platform: 'windows', backendUrl: null, ...installed });
  await update.prepare(() => {}); await update.dispose();
  assert.deepEqual(closed, [34, 12]);
});

test('a Windows installer launch failure can retry the same verified download', async () => {
  globalThis.window = { crypto: globalThis.crypto };
  const metadata = manifest('0.7.2-unstable.5', 7002005);
  let downloads = 0, attempts = 0;
  mockIPC((command, args) => {
    if (command === 'plugin:updater|check') return { rid: 12, currentVersion: installed.version, version: metadata.version, rawJson: metadata };
    if (command === 'prepare_windows_update') { downloads++; return 34; }
    if (command === 'install_windows_update') { assert.equal(args.bytesRid, 34); if (++attempts === 1) throw 'update_installer_failed'; }
  });
  const update = await checkUpdate({ platform: 'windows', backendUrl: null, ...installed });
  const state = useUpdates(async () => update);
  await state.start(); await state.install(); await state.install();
  assert.equal(state.state.value, 'error'); assert.equal(state.readyToInstall.value, true);
  await state.install(); assert.equal(state.state.value, 'opened');
  assert.equal(downloads, 1); assert.equal(attempts, 2);
  await update.dispose();
});
