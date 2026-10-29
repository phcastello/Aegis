import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import vm from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

const code = ts.transpileModule(readFileSync(new URL('../src/composables/useAegisVoice.ts', import.meta.url), 'utf8'), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS }
}).outputText;
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const flush = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };
function fixture() {
  const preparations = [], streams = [], cancelled = [], played = [], failures = [];
  let unmount;
  class Player {
    onState() {}
    ensureReady() { const item = deferred(); preparations.push(item); return item.promise; }
    begin() { return randomUUID(); }
    stop() {}
    async enqueuePcm(chunk, generation) { played.push({ chunk, generation }); }
    async finish() {}
    fail() { failures.push('failed'); }
  }
  const sandbox = { exports: {}, AbortController, crypto: { randomUUID }, require(name) {
    if (name === 'vue') return { ref: value => ({ value }), computed: getter => ({ get value() { return getter(); } }), watch() {}, onBeforeUnmount: fn => { unmount = fn; } };
    if (name.endsWith('/voicePlaybackService')) return { VoicePlaybackService: Player };
    if (name.endsWith('/voicePreference')) return { autoSpeak: { value: true }, setAutoSpeakPreference() {} };
    if (name.endsWith('/aegisApi')) return {
      getVoiceStatus: async () => ({ available: true }),
      cancelSpeech: async id => { cancelled.push(id); },
      streamSpeech: async (request, token, signal) => { const gate = deferred(); streams.push({ request, token, signal, gate }); return gate.promise; }
    };
    throw Error(name);
  } };
  vm.runInNewContext(`(function(exports) { ${code}\n})(exports)`, sandbox);
  return { api: sandbox.exports.useAegisVoice(), preparations, streams, cancelled, played, failures, unmount: () => unmount() };
}

test('navigation stop during audio preparation cannot start late speech', async () => {
  const f = fixture(); const pending = f.api.speak('turn-A', 'message-A', true); await flush();
  assert.equal(f.preparations.length, 1);
  await f.api.stop(); f.preparations[0].resolve(); await pending;
  assert.equal(f.streams.length, 0);
});

test('two pending preparations permit only the latest manual or automatic playback', async () => {
  for (const manual of [true, false]) {
    const f = fixture(); const a = f.api.speak('turn-A', 'message-A', manual); await flush();
    const b = f.api.speak('turn-B', 'message-B', true); await flush();
    f.preparations[1].resolve(); await flush();
    f.preparations[0].resolve(); await a;
    assert.equal(f.streams.length, 1);
    assert.equal(f.streams[0].request.turnId, 'turn-B');
    assert.equal(f.streams[0].request.assistantMessageId, 'message-B');
    f.streams[0].gate.resolve(); await b;
  }
});

test('new playback aborts only the old speech request and ignores its late audio', async () => {
  const f = fixture(); const a = f.api.speak('turn-A', 'message-A', true); await flush();
  f.preparations[0].resolve(); await flush(); const old = f.streams[0];
  const b = f.api.speak('turn-B', 'message-B', true); await flush(); f.preparations[1].resolve(); await flush();
  assert.equal(old.signal.aborted, true);
  assert.deepEqual(f.cancelled, [old.request.speechRequestId]);
  assert.equal(f.streams[1].signal.aborted, false);
  await old.token('old-audio'); await f.streams[1].token('new-audio');
  assert.deepEqual(f.played.map(item => item.chunk), ['new-audio']);
  old.gate.resolve(); f.streams[1].gate.resolve(); await Promise.all([a, b]);
});

test('unmount invalidates a pending playback before device preparation completes', async () => {
  const f = fixture(); const pending = f.api.speak('turn-A', 'message-A', true); await flush();
  f.unmount(); f.preparations[0].resolve(); await pending;
  assert.equal(f.streams.length, 0);
});

test('a late failure from the same turn cannot interrupt its newer manual replay', async () => {
  const f = fixture(); const first = f.api.speak('same-turn', 'same-message', true); await flush();
  f.preparations[0].resolve(); await flush(); const old = f.streams[0];
  const replay = f.api.speak('same-turn', 'same-message', true); await flush();
  f.preparations[1].resolve(); await flush();
  assert.notEqual(old.request.speechRequestId, f.streams[1].request.speechRequestId);
  old.gate.reject(new Error('Late failure after cancellation')); await first;
  assert.equal(f.api.voiceAvailable.value, true);
  assert.equal(f.api.voiceMessage.value, 'Preparando voz');
  assert.deepEqual(f.failures, []);
  await f.streams[1].token('replay-audio');
  assert.deepEqual(f.played.map(item => item.chunk), ['replay-audio']);
  f.streams[1].gate.resolve(); await replay;
});
