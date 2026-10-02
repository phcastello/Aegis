import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { clearMocks, mockIPC } from '@tauri-apps/api/mocks';
import { diagnosticServices } from '../.test-build/services/runtime.js';
import { useDiagnostics } from '../.test-build/composables/useDiagnostics.js';

afterEach(() => clearMocks());

const connected = { status: 'connected', message: null };
const unavailable = { status: 'unavailable', message: 'Backend is unreachable.' };
function services(platform = 'windows', checkBackend = async () => connected) {
  return { runtimeInfo: async () => ({ platform, backendUrl: 'https://backend.example' }), checkBackend };
}

test('native service invokes only the two diagnostic commands without arguments', async () => {
  // Official Tauri mock needs a window object; no WebView or fake network required.
  globalThis.window = {};
  const commands = [];
  mockIPC((command, args) => {
    commands.push(command);
    assert.equal(Object.keys(args ?? {}).length, 0);
    return command === 'runtime_info' ? { platform: 'android', backendUrl: null } : connected;
  });
  assert.equal((await diagnosticServices.runtimeInfo()).platform, 'android');
  assert.deepEqual(await diagnosticServices.checkBackend(), connected);
  assert.deepEqual(commands, ['runtime_info', 'check_backend']);
});

test('uses native platform identifiers for presentation', async () => {
  for (const [platform, label] of [['windows', 'Windows'], ['android', 'Android'], ['unsupported', 'Unsupported']]) {
    const state = useDiagnostics(services(platform));
    assert.equal(state.platformLabel.value, 'Unknown');
    await state.retry();
    assert.equal(state.platformLabel.value, label);
    assert.equal(state.backendLabel.value, 'Connected');
    assert.equal(state.message.value, null);
    assert.equal(state.checking.value, false);
  }
});

test('Retry reflects connected, offline and recovered backend', async () => {
  const results = [connected, unavailable, connected];
  const state = useDiagnostics(services('android', async () => results.shift()));
  await state.retry();
  assert.equal(state.backendLabel.value, 'Connected');
  await state.retry();
  assert.equal(state.backendLabel.value, 'Unavailable');
  assert.equal(state.message.value, unavailable.message);
  await state.retry();
  assert.equal(state.backendLabel.value, 'Connected');
  assert.equal(state.message.value, null);
});

test('IPC failure is unavailable and a subsequent Retry recovers', async () => {
  let failing = true;
  const state = useDiagnostics(services('windows', async () => {
    if (failing) throw new Error('IPC unavailable');
    return connected;
  }));
  await state.retry();
  assert.equal(state.backendLabel.value, 'Unavailable');
  assert.match(state.message.value, /Native diagnostics/);
  assert.equal(state.checking.value, false);
  failing = false;
  await state.retry();
  assert.equal(state.backendLabel.value, 'Connected');
});

test('runtime info failure does not send a health request', async () => {
  const state = useDiagnostics({
    runtimeInfo: async () => { throw new Error('No native runtime'); },
    checkBackend: async () => { assert.fail('should not check without runtime'); }
  });
  await state.retry();
  assert.equal(state.platformLabel.value, 'Unknown');
  assert.equal(state.backendLabel.value, 'Unavailable');
  assert.equal(state.checking.value, false);
});

test('prevents overlapping Retry and clears stale Connected while pending', async () => {
  let resolve;
  let calls = 0;
  const state = useDiagnostics(services('windows', async () => {
    calls++;
    if (calls === 1) return connected;
    return new Promise(done => { resolve = done; });
  }));
  await state.retry();
  const pending = state.retry();
  await Promise.resolve();
  assert.equal(state.checking.value, true);
  assert.equal(state.backendLabel.value, 'Unavailable');
  await state.retry();
  assert.equal(calls, 2);
  resolve(connected);
  await pending;
  assert.equal(state.backendLabel.value, 'Connected');
  assert.equal(state.checking.value, false);
});
