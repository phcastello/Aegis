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
function fixture(options = {}) {
  const preparations = [], streams = [], cancelled = [], played = [], failures = [];
  let unmount;
  class Player {
    onState(listener) { this.listener = listener; }
    ensureReady() { const item = deferred(); preparations.push(item); return item.promise; }
    begin() { return randomUUID(); }
    stop() { this.listener?.('stopped'); }
    async enqueuePcm(chunk, generation) { played.push({ chunk, generation }); }
    async finish() {}
    fail() { failures.push('failed'); }
  }
  const sandbox = { exports: {}, AbortController, crypto: { randomUUID }, require(name) {
    if (name === 'vue') return { ref: value => ({ value }), computed: getter => ({ get value() { return getter(); } }), watch() {}, onBeforeUnmount: fn => { unmount = fn; } };
    if (name.endsWith('/voicePlaybackService')) return { VoicePlaybackService: Player };
    if (name.endsWith('/clientEnvironment')) return { clientEnvironment: () => ({ nativeSpeech: options.nativeSpeech }) };
    if (name.endsWith('/audioOwnership')) return { acquireAudio: options.acquireAudio ?? (async () => async () => {}) };
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

test('Android playback lease spans real player completion and stale acquisition cannot start speech', async () => {
  let released = 0;
  const f = fixture({ acquireAudio: async () => async () => { released++; } });
  const playing = f.api.speak('turn', 'message', true); await flush(); f.preparations[0].resolve(); await flush();
  assert.equal(f.streams.length, 1); assert.equal(released, 0);
  f.streams[0].gate.resolve(); await playing; assert.equal(released, 1);
  const lease = deferred();
  const g = fixture({ acquireAudio: () => lease.promise });
  const pending = g.api.speak('late', 'late-message', true); await flush(); g.preparations[0].resolve(); await flush();
  await g.api.stop(); lease.resolve(async () => { released++; }); await pending;
  assert.equal(g.streams.length, 0); assert.equal(released, 2);
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


test('Android manual reply bypasses Web Audio and holds its lease until native playback drains', async () => {
  const drained = deferred(); const calls = []; let released = 0;
  const nativeSpeech = { speak: (...args) => { calls.push(args); return drained.promise; }, stop: async () => {} };
  const f = fixture({ nativeSpeech, acquireAudio: async () => async () => { released++; } });
  const speaking = f.api.speak('native-turn', 'native-message', true); await flush();
  assert.equal(f.preparations.length, 0); assert.equal(f.streams.length, 0);
  assert.equal(calls.length, 1); assert.deepEqual(calls[0].slice(0, 2), ['native-turn', 'native-message']);
  assert.equal(released, 0); assert.equal(f.api.isBusy.value, true);
  drained.resolve(); await speaking;
  assert.equal(released, 1); assert.equal(f.api.playbackState.value, 'idle');
});

test('Android stop invalidates the matching native request before releasing audio ownership', async () => {
  const drained = deferred(); const calls = []; let request;
  const nativeSpeech = { speak: async (...args) => { request = args[2]; await drained.promise; }, stop: async id => { calls.push(['stop', id]); } };
  const f = fixture({ nativeSpeech, acquireAudio: async () => { let released = false; return async () => { if (!released) { released = true; calls.push(['release']); } }; } });
  const speaking = f.api.speak('native-turn', 'native-message', true); await flush();
  await f.api.stop(); drained.resolve(); await speaking;
  assert.deepEqual(calls, [['stop', request], ['release']]);
  assert.equal(f.api.playbackState.value, 'stopped');
});


test('a delayed native stop cannot abort a newer manual replay controller or release its lease', async () => {
  const stopGate = deferred(); const speechGates = []; let released = 0;
  const nativeSpeech = {
    speak: async () => { const gate = deferred(); speechGates.push(gate); await gate.promise; },
    stop: async () => { await stopGate.promise; }
  };
  const f = fixture({ nativeSpeech, acquireAudio: async () => { let done = false; return async () => { if (!done) { done = true; released++; } }; } });
  const first = f.api.speak('old-turn', 'old-message', true); await flush();
  const stopping = f.api.stop(); await flush();
  const replacement = f.api.speak('new-turn', 'new-message', true); await flush();
  assert.equal(speechGates.length, 2); assert.equal(released, 0);
  stopGate.resolve(); await stopping; assert.equal(released, 1);
  speechGates[0].resolve(); await first; assert.equal(released, 1);
  speechGates[1].resolve(); await replacement;
  assert.equal(released, 2); assert.equal(f.api.playbackState.value, 'idle');
  assert.equal(f.api.voiceMessage.value, null); assert.equal(f.api.voiceAvailable.value, true);
});
