import assert from 'node:assert/strict';
import test from 'node:test';
import { useUpdates } from '../.test-build/composables/useUpdates.js';

test('update checks run once per session and remain invisible on no update/network failure', async () => {
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
