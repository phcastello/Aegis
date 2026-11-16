import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { clearMocks, mockIPC } from '@tauri-apps/api/mocks';
import { handsFree, handsFreeError, acquireNativeAudio, latestBatteryRuns, batteryRateDifference } from '../.test-build/services/handsFree.js';
import { useHandsFreeSettings } from '../.test-build/composables/useHandsFreeSettings.js';

afterEach(() => clearMocks());
const initial = { enabled: false, diagnostic: false, assistantSelected: false, permissionGranted: false, modelReady: false, captureConfirmed: false, status: 'disabled', message: 'Escuta desligada.' };
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const flush = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };

test('state invocation requires no setup fields, permissions, model, assistant or backend', async () => {
  globalThis.window = {};
  const calls = [];
  mockIPC((command, args) => { calls.push({ command, args }); return initial; });
  assert.deepEqual(await handsFree('state'), initial);
  assert.deepEqual(calls, [{ command: 'node_hands_free', args: { operation: 'state' } }]);
});

test('native and Tauri errors preserve only a sanitized known diagnostic', async () => {
  globalThis.window = {};
  const secret = 'private-token private audio /private/transcript';
  for (const code of ['bridge_unavailable', 'timeout', 'invalid_response', 'native_failure']) {
    mockIPC(() => { throw { code, message: secret }; });
    await assert.rejects(handsFree('state'), error => error.code === code && !error.message.includes(secret));
  }
  for (const reason of [`node_hands_free not allowed on window main ${secret}`, new Error(`node_hands_free explicitly denied ${secret}`)]) {
    assert.equal(handsFreeError(reason).code, 'command_not_authorized');
    assert(!handsFreeError(reason).message.includes(secret));
  }
  assert.equal(handsFreeError('Command node_hands_free not found').code, 'bridge_unavailable');
  assert.equal(handsFreeError(new Error(secret)).code, 'native_failure');
  assert(!handsFreeError(new Error(secret)).message.includes(secret));
});

test('invalid state payloads are rejected instead of becoming invented setup state', async () => {
  globalThis.window = {};
  for (const value of [null, {}, 'state', { ...initial, enabled: 'false' }, { ...initial, captureConfirmed: 'yes' }, { ...initial, message: 'x'.repeat(401) }]) {
    mockIPC(() => value);
    await assert.rejects(handsFree('state'), { code: 'invalid_response' });
  }
});

test('model identity from native state survives baseline, strong, rollback and absent-model responses', async () => {
  globalThis.window = {};
  const baseline = { modelName: 'Baseline', modelVersion: 'aegis-mww-fleurs-dev-2026-10-31', modelSha256: 'f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514' };
  const strong = { modelName: 'Strong — época 2', modelVersion: 'aegis-mww-strong-epoch-02-2026-11-05', modelSha256: '1e7c686420f65d8da892f02c71d945f03a8dd528a1005de8c9c8482dce52ad2e' };
  for (const identity of [baseline, strong, baseline]) {
    const response = { ...initial, modelReady: true, ...identity };
    mockIPC(() => response);
    assert.deepEqual(await handsFree('state'), response);
  }
  const missing = { ...initial, modelName: null, modelVersion: null, modelSha256: null };
  mockIPC(() => missing);
  assert.deepEqual(await handsFree('state'), missing);
  mockIPC(() => initial);
  assert.deepEqual(await handsFree('state'), initial, 'legacy state remains readable without inventing baseline identity');
});

test('invalid model identity is rejected at the native state boundary', async () => {
  globalThis.window = {};
  for (const identity of [{ modelName: {} }, { modelName: '' }, { modelVersion: 2 }, { modelVersion: 'x'.repeat(101) }, { modelSha256: 'not-a-hash' }, { modelSha256: 'a'.repeat(65) }]) {
    mockIPC(() => ({ ...initial, ...identity }));
    await assert.rejects(handsFree('state'), { code: 'invalid_response' });
  }
});

test('first rejection ends loading; retry recovers and clears the old diagnostic', async () => {
  let fail = true;
  const s = useHandsFreeSettings(async () => { if (fail) throw { code: 'command_not_authorized' }; return initial; });
  await s.refresh();
  assert.equal(s.state.value, null); assert.equal(s.busy.value, false); assert.equal(s.canAct.value, false);
  assert.equal(s.diagnosticCode.value, 'command_not_authorized');
  fail = false; await s.refresh();
  assert.deepEqual(s.state.value, initial); assert.equal(s.canAct.value, true); assert.equal(s.current.value, true);
  assert.equal(s.error.value, ''); assert.equal(s.diagnosticCode.value, null);
});

test('known state becomes stale on failure and never authorizes a settings action', async () => {
  const operations = []; let fail = false;
  const s = useHandsFreeSettings(async operation => { operations.push(operation); if (fail) throw { code: 'timeout' }; return initial; });
  await s.refresh(); fail = true; await s.refresh();
  assert.deepEqual(s.state.value, initial); assert.equal(s.stale.value, true); assert.equal(s.canAct.value, false);
  await s.run('configure', true, false);
  assert.deepEqual(operations, ['state', 'state']);
  fail = false; await s.refresh(); assert.equal(s.stale.value, false); assert.equal(s.error.value, '');
});

test('a pending refresh invalidates confirmation and coalesces repeated events into one follow-up', async () => {
  const first = deferred(), second = deferred(); let calls = 0, active = 0, peak = 0;
  const s = useHandsFreeSettings(async () => {
    const index = ++calls; peak = Math.max(peak, ++active);
    try { return await (index === 1 ? first.promise : second.promise); } finally { active--; }
  });
  const pending = s.refresh();
  for (let i = 0; i < 20; i++) void s.refresh();
  assert.equal(calls, 1); assert.equal(s.busy.value, true); assert.equal(s.canAct.value, false);
  first.resolve(initial); await pending; await flush(); assert.equal(calls, 2); assert.equal(s.stale.value, true);
  second.resolve({ ...initial, message: 'Estado mais recente.' }); await flush();
  assert.equal(s.state.value.message, 'Estado mais recente.'); assert.equal(peak, 1); assert.equal(s.busy.value, false);
});

test('unmount drops late success, failure and queued refresh without mutating the prior state', async () => {
  for (const fail of [false, true]) {
    const late = deferred(); let calls = 0;
    const s = useHandsFreeSettings(async () => ++calls === 1 ? initial : late.promise);
    await s.refresh(); const pending = s.refresh(); void s.refresh(); s.dispose();
    if (fail) late.reject(new Error('private exception')); else late.resolve({ ...initial, enabled: true });
    await pending; await flush();
    assert.equal(s.state.value.enabled, false); assert.equal(s.error.value, ''); assert.equal(calls, 2);
    await s.refresh(); assert.equal(calls, 2);
  }
});

test('only fresh state can configure; settings and refresh never overlap', async () => {
  const action = deferred(); const calls = [];
  const s = useHandsFreeSettings(async (operation, options) => { calls.push({ operation, options }); return operation === 'configure' ? action.promise : initial; });
  await s.run('configure', true); assert.equal(calls.length, 0);
  await s.refresh(); const pending = s.run('configure', true, true);
  await s.run('cancel'); void s.refresh(); assert.equal(calls.length, 2);
  assert.deepEqual(calls[1], { operation: 'configure', options: { enabled: true, diagnostic: true } });
  action.resolve({ ...initial, enabled: true, diagnostic: true }); await pending; await flush();
  assert.deepEqual(calls.map(x => x.operation), ['state', 'configure', 'state']);
  assert.equal(s.state.value.enabled, false, 'newer post-action state wins');
});

test('manual audio uses the same closed authorized command and releases a matching lease once', async () => {
  globalThis.window = {}; const calls = [];
  mockIPC((command, args) => { calls.push({ command, args }); return args.operation === 'manualAcquire' ? { acquired: true } : initial; });
  const release = await acquireNativeAudio('capture'); await release(); await release();
  assert.equal(calls.length, 2); assert(calls.every(x => x.command === 'node_hands_free'));
  assert.equal(calls[0].args.operation, 'manualAcquire'); assert.equal(calls[0].args.audioKind, 'capture');
  assert.equal(calls[1].args.operation, 'manualRelease'); assert.equal(calls[1].args.leaseId, calls[0].args.leaseId);
});


test('collection controls use typed closed bridge arguments without PCM or private paths', async () => {
  globalThis.window = {}; const calls = [];
  const collection = { enabled: false, limitMiB: 100, bytes: 0, ready: true, status: 'ready', skippedEvents: 0 };
  mockIPC((command, args) => { calls.push({ command, args }); return { ...initial, collection }; });
  await handsFree('collectionConfigure', { collectionEnabled: true, collectionLimitMiB: 250 });
  await handsFree('collectionDelete'); await handsFree('export');
  assert.deepEqual(calls, [
    { command: 'node_hands_free', args: { operation: 'collectionConfigure', collectionEnabled: true, collectionLimitMiB: 250 } },
    { command: 'node_hands_free', args: { operation: 'collectionDelete' } },
    { command: 'node_hands_free', args: { operation: 'export' } }
  ]);
});

test('collection states reject invalid limits, raw diagnostics and unbounded counters', async () => {
  globalThis.window = {};
  const valid = { enabled: false, limitMiB: 100, bytes: 0, ready: true, status: 'ready', skippedEvents: 0 };
  for (const collection of [null, {}, { ...valid, limitMiB: 0 }, { ...valid, limitMiB: 2049 }, { ...valid, bytes: -1 }, { ...valid, skippedEvents: Number.MAX_SAFE_INTEGER+1 }, { ...valid, status: '/private/audio/error' }, { ...valid, enabled: 'false' }]) {
    mockIPC(() => ({ ...initial, collection }));
    await assert.rejects(handsFree('state'), { code: 'invalid_response' });
  }
});

test('collection configuration requires fresh state and keeps explicit consent separate from diagnostic mode', async () => {
  const calls = []; const collection = { enabled: false, limitMiB: 100, bytes: 0, ready: true, status: 'ready', skippedEvents: 0 };
  const settings = useHandsFreeSettings(async (operation, options) => { calls.push({ operation, options }); return { ...initial, collection }; });
  await settings.configureCollection(true,250); assert.equal(calls.length,0);
  await settings.refresh(); await settings.configureCollection(true,250);
  assert.deepEqual(calls[1],{ operation:'collectionConfigure',options:{collectionEnabled:true,collectionLimitMiB:250} });
  assert.equal(settings.state.value.diagnostic,false);
});


const batteryStudy = (overrides = {}) => ({ schema: 1, active: null, requiresResume: false, latest: null, runs: [], ...overrides });
const batteryRun = (mode = 'off', overrides = {}) => ({ id: `run_${mode}`, comparisonKey: 'a'.repeat(64), mode, status: 'completed', startedAt: '2026-11-16T10:00:00-03:00', endedAt: '2026-11-16T10:30:00-03:00', durationMs: 1800000, eligible: true, issues: [], summary: { phoneChargeMah: 15, phoneDischargeMahPerHour: 30, processCpuMs: 120 }, ...overrides });

test('battery controls preserve closed native mode arguments and backward compatible state', async () => {
  globalThis.window = {}; const calls = [];
  const diagnostics = batteryStudy();
  mockIPC((command, args) => { calls.push({ command, args }); return { ...initial, batteryDiagnostics: diagnostics }; });
  await handsFree('batteryStart', { batteryMode: 'capture_only' }); await handsFree('batteryFinish'); await handsFree('batteryExport');
  assert.deepEqual(calls, [
    { command: 'node_hands_free', args: { operation: 'batteryStart', batteryMode: 'capture_only' } },
    { command: 'node_hands_free', args: { operation: 'batteryFinish' } },
    { command: 'node_hands_free', args: { operation: 'batteryExport' } }
  ]);
  mockIPC(() => initial); assert.deepEqual(await handsFree('state'), initial);
});

test('battery reports preserve unavailable sensors and signed device deltas', async () => {
  globalThis.window = {};
  const summary = { phoneChargeMah: -3.25, phoneEnergyMwh: -12.5, phoneDischargeMahPerHour: -6.5, batteryPercentPoints: -1, detectorCpuMs: null, uidUserCpuMs: 12, uidSystemCpuMs: null, uidAudioTimerMs: 500, futureNumericMetric: -0.5 };
  const diagnostics = batteryStudy({ runs: [batteryRun('detection', { summary })] });
  mockIPC(() => ({ ...initial, batteryDiagnostics: diagnostics }));
  assert.deepEqual((await handsFree('state')).batteryDiagnostics, diagnostics);
});

test('battery boundary rejects malformed histories, active owners and impossible CPU values', async () => {
  globalThis.window = {};
  const valid = batteryRun();
  const invalid = [null, {}, batteryStudy({ schema: 2 }), batteryStudy({ requiresResume: 'false' }),
    batteryStudy({ active: { id: 'x', mode: 'other', startedAt: valid.startedAt, elapsedMs: 0 } }),
    batteryStudy({ active: { id: 'x', mode: 'off', startedAt: 'local time', elapsedMs: 0 } }),
    batteryStudy({ runs: Array.from({ length: 9 }, (_, i) => ({ ...valid, id: `run_${i}` })) }),
    batteryStudy({ runs: [valid, valid] }), batteryStudy({ runs: [{ ...valid, comparisonKey: 'unknown' }] }),
    batteryStudy({ runs: [{ ...valid, comparisonKey: 'A'.repeat(64) }] }), batteryStudy({ runs: [{ ...valid, status: 'error/private/path' }] }),
    batteryStudy({ runs: [{ ...valid, issues: ['private/path'] }] }),
    batteryStudy({ runs: [{ ...valid, summary: { phoneChargeMah: 1, processCpuMs: -1 } }] }),
    batteryStudy({ runs: [{ ...valid, summary: { phoneChargeMah: Infinity } }] }),
    batteryStudy({ runs: [{ ...valid, summary: { phoneChargeMah: 1, transcript: 'private text' } }] }),
    batteryStudy({ latest: { privatePath: '/private/audio' } })];
  for (const batteryDiagnostics of invalid) {
    mockIPC(() => ({ ...initial, batteryDiagnostics }));
    await assert.rejects(handsFree('state'), { code: 'invalid_response' });
  }
});

test('battery snapshots validate available and unsupported public sensor observations', async () => {
  globalThis.window = {};
  const latest = { at: '2026-11-16T10:00:00-03:00', elapsedMs: 2000, uptimeMs: 1500, processId: 'process_1', processCpuMs: 300,
    batteryPercent: 70, chargeMicroAh: null, energyNanoWh: null, currentMicroA: -250000, plugged: 0, batteryStatus: 3,
    temperatureDeciC: 320, voltageMv: 3800, screenInteractive: false, locked: true,
    currentAverageMicroA: -230000, powerSaveMode: false, deviceIdleMode: true, charging: false, thermalStatus: 0, sdkInt: 33, manufacturer: 'samsung', model: 'SM-G781B', androidVersion: '13',
    uidHealth: { status: 'unavailable', scope: 'own_uid_os_accounting', userCpuMs: null, systemCpuMs: null, audioTimerMs: null }, futureObservation: { status: 'not_interpreted' } };
  mockIPC(() => ({ ...initial, batteryDiagnostics: batteryStudy({ latest }) }));
  assert.deepEqual((await handsFree('state')).batteryDiagnostics.latest, latest);
  for (const change of [{ batteryPercent: 101 }, { processCpuMs: -1 }, { uidHealth: { ...latest.uidHealth, status: 'private error' } }, { screenInteractive: 'false' }]) {
    mockIPC(() => ({ ...initial, batteryDiagnostics: batteryStudy({ latest: { ...latest, ...change } }) }));
    await assert.rejects(handsFree('state'), { code: 'invalid_response' });
  }
});

test('latest eligible scenario comparison uses device rates and preserves negative differences', () => {
  const runs = [batteryRun('off'), batteryRun('capture_only', { summary: { phoneChargeMah: 10, phoneDischargeMahPerHour: 20 } }),
    batteryRun('detection', { summary: { phoneChargeMah: 12, phoneDischargeMahPerHour: 24 } }),
    batteryRun('off', { id: 'invalid_later', eligible: false, endedAt: '2026-11-16T12:00:00-03:00' }),
    batteryRun('off', { id: 'older', endedAt: '2026-11-16T09:30:00-03:00' })];
  const latest = latestBatteryRuns(runs);
  assert.equal(latest.off.id, 'run_off');
  assert.equal(batteryRateDifference(latest.capture_only, latest.off), -10);
  assert.equal(batteryRateDifference(latest.detection, latest.capture_only), 4);
  assert.equal(batteryRateDifference(undefined, latest.off), null);
  assert.equal(batteryRateDifference(batteryRun('off', { summary: { phoneChargeMah: null } }), latest.off), null);
});

test('active or interrupted battery study blocks normal mutations until explicit finish', async () => {
  for (const diagnostics of [batteryStudy({ active: { id: 'active', mode: 'off', startedAt: '2026-11-16T10:00:00Z', elapsedMs: 0 } }), batteryStudy({ requiresResume: true })]) {
    const calls = [];
    const settings = useHandsFreeSettings(async (operation, options) => { calls.push({ operation, options }); return { ...initial, batteryDiagnostics: diagnostics }; });
    await settings.refresh(); assert.equal(settings.batteryLocked.value, true); assert.equal(settings.canConfigure.value, false);
    await settings.run('configure', true); await settings.run('import'); await settings.run('export'); await settings.configureCollection(true, 100); await settings.startBattery('detection');
    assert.deepEqual(calls.map(c => c.operation), ['state']);
    await settings.finishBattery(); assert.equal(calls.at(-1).operation, 'batteryFinish');
  }
});

test('lost start reply queries native ownership before allowing another study', async () => {
  const recovery = deferred(); const calls = [];
  const available = { ...initial, assistantSelected: true, permissionGranted: true, modelReady: true, batteryDiagnostics: batteryStudy() };
  const active = { ...initial, batteryDiagnostics: batteryStudy({ active: { id: 'active', mode: 'capture_only', startedAt: '2026-11-16T10:00:00Z', elapsedMs: 1000 } }) };
  const settings = useHandsFreeSettings(async operation => {
    calls.push(operation);
    if (calls.length === 1) return available;
    if (operation === 'batteryStart') throw { code: 'timeout' };
    return recovery.promise;
  });
  await settings.refresh(); await settings.startBattery('capture_only'); await flush();
  await settings.startBattery('detection');
  assert.deepEqual(calls, ['state', 'batteryStart', 'state']);
  assert.equal(settings.canAct.value, false);
  recovery.resolve(active); await flush();
  assert.equal(settings.batteryLocked.value, true); assert.equal(settings.canConfigure.value, false);
  await settings.startBattery('off'); assert.equal(calls.length, 3);
});


test('lost finish reply restores state from native before unlocking normal controls', async () => {
  const recovery = deferred(); const calls = [];
  const active = { ...initial, batteryDiagnostics: batteryStudy({ requiresResume: true }) };
  const settings = useHandsFreeSettings(async operation => {
    calls.push(operation);
    if (calls.length === 1) return active;
    if (operation === 'batteryFinish') throw { code: 'timeout' };
    return recovery.promise;
  });
  await settings.refresh(); await settings.finishBattery(); await flush();
  assert.deepEqual(calls, ['state', 'batteryFinish', 'state']);
  await settings.run('configure', true); assert.equal(calls.length, 3);
  recovery.resolve({ ...initial, batteryDiagnostics: batteryStudy() }); await flush();
  assert.equal(settings.canConfigure.value, true);
});


test('battery JSON export is blocked during capture but allowed for interrupted recovery and completed results', async () => {
  for (const diagnostics of [batteryStudy({ active: { id: 'active', mode: 'off', startedAt: '2026-11-16T10:00:00Z', elapsedMs: 0 } }), batteryStudy({ requiresResume: true }), batteryStudy({ runs: [batteryRun()] })]) {
    const calls = [];
    const settings = useHandsFreeSettings(async operation => { calls.push(operation); return { ...initial, batteryDiagnostics: diagnostics }; });
    await settings.exportBattery(); assert.equal(calls.length, 0, 'export needs fresh native state');
    await settings.refresh(); await settings.exportBattery();
    assert.deepEqual(calls, diagnostics.active ? ['state'] : ['state', 'batteryExport']);
  }
});


test('device/app/model comparison groups never produce mixed or unproven battery differences', () => {
  const old = batteryRun('off');
  const newer = batteryRun('capture_only', { id: 'new_phone', comparisonKey: 'b'.repeat(64), endedAt: '2026-11-16T12:00:00-03:00' });
  const oldDetection = batteryRun('detection', { id: 'old_detector', endedAt: '2026-11-16T11:00:00-03:00' });
  assert.deepEqual(Object.keys(latestBatteryRuns([old, oldDetection, newer])), ['capture_only']);
  assert.equal(batteryRateDifference(newer, old), null, 'different phone/build/model groups cannot be subtracted');
  const legacy = batteryRun('off', { comparisonKey: undefined });
  const legacyCapture = batteryRun('capture_only', { comparisonKey: undefined, endedAt: '2026-11-16T12:00:00-03:00' });
  assert.deepEqual(Object.keys(latestBatteryRuns([legacy, legacyCapture])), ['capture_only']);
  assert.equal(batteryRateDifference(legacyCapture, legacy), null, 'old reports have no proven common identity');
  assert.deepEqual(latestBatteryRuns([]), {});
});


test('study prerequisites are explicit and allow capture-only without a detector model', async () => {
  let response = { ...initial, batteryDiagnostics: batteryStudy() }; const calls = [];
  const settings = useHandsFreeSettings(async operation => { calls.push(operation); return response; });
  await settings.refresh();
  for (const mode of ['off', 'capture_only', 'detection']) { assert.equal(settings.canStartBattery(mode), false); await settings.startBattery(mode); }
  assert.deepEqual(calls, ['state']);
  response = { ...response, assistantSelected: true }; await settings.refresh();
  assert.equal(settings.canStartBattery('off'), true); assert.equal(settings.canStartBattery('capture_only'), false);
  response = { ...response, permissionGranted: true }; await settings.refresh();
  assert.equal(settings.canStartBattery('capture_only'), true); assert.equal(settings.canStartBattery('detection'), false);
  response = { ...response, modelReady: true }; await settings.refresh(); assert.equal(settings.canStartBattery('detection'), true);
});
