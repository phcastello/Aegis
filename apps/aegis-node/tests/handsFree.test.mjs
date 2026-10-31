import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { clearMocks, mockIPC } from '@tauri-apps/api/mocks';
import { handsFree, handsFreeError, acquireNativeAudio } from '../.test-build/services/handsFree.js';
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
