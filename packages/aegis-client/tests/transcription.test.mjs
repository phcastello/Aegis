import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { randomUUID } from 'node:crypto';
import vm from 'node:vm';
import { test } from 'node:test';
import ts from 'typescript';

// Current production code, compiled by the locked project toolchain. All media,
// PCM, time and HTTP are synthetic: this never opens a device or a real API.
const compile = path => ts.transpileModule(readFileSync(new URL(path, import.meta.url), 'utf8'), {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS }
}).outputText;
const transcriptionCode = compile('../src/composables/useAegisTranscription.ts');
const voiceInputCode = compile('../src/services/voiceInput.ts');
const deferred = () => { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; };
const settle = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };
function stream() {
  const handlers = new Map();
  const track = { stopped: false, readyState: 'live', muted: false,
    stop() { this.stopped = true; this.readyState = 'ended'; },
    addEventListener(name, callback) { handlers.set(name, callback); },
    removeEventListener(name, callback) { if (handlers.get(name) === callback) handlers.delete(name); },
    end() { this.readyState = 'ended'; handlers.get('ended')?.(); },
    get listenerCount() { return handlers.size; }
  };
  return { track, getTracks: () => [track], getAudioTracks: () => [track] };
}
function fixture(options = {}) {
  const streams = [], requests = [], pendingStops = [], uploads = [], inserted = [], recorders = [], contexts = [], timers = new Map(), storage = new Map();
  const documentListeners = new Map(); let now = 10000, nextTimer = 0, unmount;
  if (options.preferred) storage.set('aegis.voice.input.v1', options.preferred);
  class Recorder {
    static isTypeSupported() { return true; }
    constructor(input, config) { this.stream = input; this.mimeType = config?.mimeType ?? 'audio/webm'; this.state = 'inactive'; recorders.push(this); }
    start() { if (options.startError) throw Error('Synthetic recorder failure'); this.state = 'recording'; }
    stop() {
      if (options.stopError) throw Error('Synthetic stop failure'); this.state = 'inactive';
      const data = this.ondataavailable, stop = this.onstop;
      pendingStops.push(() => { data?.({ data: new Blob(options.noData ? [] : ['synthetic encoded audio']) }); stop?.(); });
    }
  }
  class Context {
    constructor() { this.state = options.suspended ? 'suspended' : 'running'; contexts.push(this); }
    createMediaStreamSource() { return { connect() {}, disconnect() {} }; }
    createAnalyser() { return { fftSize: 2048, disconnect() {}, getFloatTimeDomainData(data) { data.fill(options.meterSample ?? 0.01); } }; }
    async resume() {} async close() { this.state = 'closed'; }
    async decodeAudioData() {
      if (options.decodeError) throw Error('Unsupported synthetic codec'); if (options.decode) return options.decode.promise;
      const channels = options.channels ?? [new Float32Array([0, 0.000001, 0])];
      return { numberOfChannels: channels.length, getChannelData: index => channels[index] };
    }
  }
  const sandbox = {
    Blob, AbortController, DOMException, Float32Array, crypto: { randomUUID }, Date: { now: () => now }, AudioContext: Context, MediaRecorder: Recorder,
    localStorage: { getItem: key => storage.get(key) ?? null, setItem: (key, value) => storage.set(key, value), removeItem: key => storage.delete(key) },
    navigator: { mediaDevices: { getUserMedia: async constraints => {
      requests.push(constraints); if (options.getUserMedia) return options.getUserMedia(constraints, requests.length);
      const input = stream(); streams.push(input); return input;
    }, enumerateDevices: async () => options.devices ?? [] } },
    document: { visibilityState: 'visible', addEventListener(name, callback) { documentListeners.set(name, callback); }, removeEventListener(name) { documentListeners.delete(name); } },
    window: { setTimeout(callback, milliseconds) { const id = ++nextTimer; timers.set(id, { callback, milliseconds }); return id; }, clearTimeout(id) { timers.delete(id); } }, exports: {},
    require(name) {
      if (name === 'vue') return { ref: value => ({ value }), computed: getter => ({ get value() { return getter(); } }), onBeforeUnmount: callback => { unmount = callback; } };
      if (name === '../services/voiceInput') return voiceInput;
      if (name === '../services/aegisApi') return {
        getTranscriptionStatus: async () => options.status ?? { enabled: true, configured: true, maxRecordingSeconds: 90 },
        transcribeAudio: async (...args) => { uploads.push({ blob: args[0], duration: args[2], signal: args[4] }); return options.upload ? options.upload.promise : { text: 'transcrição sintética' }; }
      }; throw Error('Unexpected module: ' + name);
    }
  };
  vm.runInNewContext(`(function(exports) { ${voiceInputCode}\n})(exports);`, sandbox); const voiceInput = sandbox.exports; sandbox.exports = {};
  vm.runInNewContext(`(function(exports) { ${transcriptionCode}\n})(exports);`, sandbox); const api = sandbox.exports.useAegisTranscription(text => inserted.push(text));
  return { api, voiceInput, streams, requests, recorders, contexts, timers, uploads, inserted,
    async ready() { await api.refreshStatus(); return this; }, async start() { await api.start(); return this; },
    async flushStops() { while (pendingStops.length) pendingStops.shift()(); await settle(); },
    fireTimer(milliseconds) { const entry = [...timers].find(([, timer]) => timer.milliseconds === milliseconds); assert.ok(entry, 'timer exists: ' + milliseconds); timers.delete(entry[0]); now += milliseconds; entry[1].callback(); },
    hide() { sandbox.document.visibilityState = 'hidden'; documentListeners.get('visibilitychange')?.(); }, unmount() { unmount(); },
    assertReleased() { assert.equal(timers.size, 0, 'no timers'); assert.ok(streams.every(s => s.track.stopped), 'tracks stopped'); assert.ok(streams.every(s => s.track.listenerCount === 0), 'listeners removed'); assert.ok(contexts.every(c => c.state === 'closed'), 'contexts closed'); }
  };
}

test('historical controls: normal stop uploads once; discard while recording uploads nothing', async () => {
  for (const cancel of [false, true]) {
    const f = await fixture().ready(); await f.start(); if (cancel) f.api.discard(); else f.api.stopRecording(); await f.flushStops();
    assert.equal(f.uploads.length, cancel ? 0 : 1); assert.equal(f.inserted.length, cancel ? 0 : 1); f.api.dispose(); f.assertReleased();
  }
});
test('discard between stop and onstop invalidates queued callbacks before any upload', async () => {
  const f = await fixture().ready(); await f.start(); f.api.stopRecording(); assert.equal(f.streams[0].track.stopped, true, 'stop immediately releases the microphone'); f.api.discard(); await f.flushStops();
  assert.equal(f.uploads.length, 0); assert.equal(f.inserted.length, 0); f.assertReleased();
});
test('old permission resolution cannot clear new timer or lose its track, in either order', async () => {
  for (const newFirst of [false, true]) {
    const a = deferred(), b = deferred(), old = stream(), fresh = stream();
    const f = await fixture({ getUserMedia: (_c, call) => call === 1 ? a.promise : b.promise }).ready();
    const first = f.api.start(); f.api.discard(); const second = f.api.start();
    if (newFirst) { b.resolve(fresh); await second; } a.resolve(old); await first;
    if (!newFirst) { b.resolve(fresh); await second; }
    assert.equal(old.track.stopped, true); assert.equal(fresh.track.stopped, false); assert.equal(f.api.state.value, 'recording');
    assert.equal([...f.timers.values()].filter(t => t.milliseconds === 90000).length, 1);
    f.api.dispose(); assert.equal(fresh.track.stopped, true); f.assertReleased();
  }
});
test('old permission rejection cannot fail or stop a newer session, in either order', async () => {
  for (const newFirst of [false, true]) {
    const a = deferred(), b = deferred(), fresh = stream();
    const f = await fixture({ getUserMedia: (_c, call) => call === 1 ? a.promise : b.promise }).ready();
    const first = f.api.start(); f.api.discard(); const second = f.api.start();
    if (newFirst) { b.resolve(fresh); await second; } a.reject(new DOMException('Synthetic denial', 'NotAllowedError')); await first;
    if (!newFirst) { assert.equal(f.api.state.value, 'requesting_permission'); b.resolve(fresh); await second; }
    assert.equal(f.api.state.value, 'recording'); assert.equal(fresh.track.stopped, false); f.api.dispose(); assert.equal(fresh.track.stopped, true); f.assertReleased();
  }
});
test('cancel during full-recording inspection prevents upload and releases its context', async () => {
  const decode = deferred(), f = await fixture({ decode }).ready(); await f.start(); f.api.stopRecording(); await f.flushStops();
  f.api.discard(); decode.resolve({ numberOfChannels: 1, getChannelData: () => new Float32Array([1]) }); await settle();
  assert.equal(f.uploads.length, 0); assert.equal(f.inserted.length, 0); f.assertReleased();
});
test('active upload cancellation aborts HTTP and rejects late success/error without affecting new capture', async () => {
  for (const reject of [false, true]) {
    const upload = deferred(), f = await fixture({ upload }).ready(); await f.start(); f.api.stopRecording(); await f.flushStops();
    assert.equal(f.uploads.length, 1); f.api.discard(); assert.equal(f.uploads[0].signal.aborted, true); await f.start();
    if (reject) upload.reject(Error('late synthetic failure')); else upload.resolve({ text: 'must not appear' }); await settle();
    assert.equal(f.inserted.length, 0); assert.equal(f.api.state.value, 'recording'); f.api.dispose(); f.assertReleased();
  }
});
test('repeated stale callbacks and discard after completion never duplicate insertion', async () => {
  const f = await fixture().ready(); await f.start(); const r = f.recorders[0], stop = r.onstop, data = r.ondataavailable, error = r.onerror;
  f.api.stopRecording(); await f.flushStops(); stop(); data({ data: new Blob(['late']) }); error(); await settle();
  f.api.discard(); assert.equal(f.uploads.length, 1); assert.equal(f.inserted.length, 1); f.assertReleased();
});
test('rapid cancel/restart and old stop cannot affect a new capture', async () => {
  const f = await fixture().ready(); await f.start(); f.api.stopRecording(); f.api.discard(); await f.start(); await f.flushStops();
  assert.equal(f.api.state.value, 'recording'); assert.equal(f.streams[1].track.stopped, false); assert.equal(f.uploads.length, 0); f.api.dispose(); f.assertReleased();
});
test('visibility and unmount cancel pending permission, recording, queued stop and uploading', async () => {
  for (const stage of ['permission', 'recording', 'stopping', 'uploading']) for (const action of ['hide', 'unmount']) {
    const permission = deferred(), upload = deferred(), input = stream();
    const f = await fixture({ upload, getUserMedia: () => stage === 'permission' ? permission.promise : Promise.resolve(input) }).ready();
    const start = f.api.start(); if (stage !== 'permission') await start;
    if (stage === 'stopping' || stage === 'uploading') f.api.stopRecording(); if (stage === 'uploading') await f.flushStops();
    f[action](); if (stage === 'permission') { permission.resolve(input); await start; }
    await f.flushStops(); upload.resolve({ text: 'late' }); await settle();
    assert.equal(input.track.stopped, true); assert.equal(f.inserted.length, 0); assert.equal(f.uploads.length, stage === 'uploading' ? 1 : 0);
    if (stage === 'uploading') assert.equal(f.uploads[0].signal.aborted, true); f.assertReleased();
  }
});
test('empty recorder data, recorder errors, start/stop failures, missing onstop and ended tracks release capture', async () => {
  for (const kind of ['empty', 'error', 'start', 'stop', 'ended', 'missing-stop']) {
    const f = await fixture({ noData: kind === 'empty', startError: kind === 'start', stopError: kind === 'stop' }).ready(); await f.start();
    if (kind === 'error') f.recorders[0].onerror(); else if (kind === 'ended') f.streams[0].track.end(); else if (kind !== 'start') f.api.stopRecording();
    if (kind === 'missing-stop') f.fireTimer(5000); await f.flushStops();
    assert.equal(f.api.state.value, 'error', kind); assert.equal(f.uploads.length, 0, kind); f.assertReleased();
  }
});
test('exact selected input and fallback on removed/changed saved device; preference is cleared', async () => {
  const direct = await fixture({ preferred: 'synthetic-input' }).ready(); await direct.start();
  assert.equal(direct.requests[0].audio.deviceId.exact, 'synthetic-input'); direct.api.dispose(); direct.assertReleased();
  for (const name of ['NotFoundError', 'OverconstrainedError']) {
    const input = stream(); const f = await fixture({ preferred: 'synthetic-missing', getUserMedia: (_c, call) => call === 1 ? Promise.reject(new DOMException('missing', name)) : Promise.resolve(input) }).ready();
    await f.start(); assert.equal(f.requests[1].audio, true); assert.equal(f.api.diagnostic.value, 'device_unavailable');
    assert.equal(f.voiceInput.getPreferredVoiceInput(), ''); assert.match(f.api.notice.value, /entrada padrão/); f.api.dispose(); assert.equal(input.track.stopped, true); f.assertReleased();
  }
});
test('missing old selection falls back without erasing a preference saved during permission', async () => {
  for (const name of ['NotFoundError', 'OverconstrainedError']) {
    const permission = deferred(), f = await fixture({ preferred: 'synthetic-old', getUserMedia: (_c, call) => call === 1 ? permission.promise : Promise.resolve(stream()) }).ready();
    const pending = f.api.start();
    f.voiceInput.setPreferredVoiceInput('synthetic-next');
    permission.reject(new DOMException('missing previous input', name)); await pending;
    assert.equal(f.requests[1].audio, true, 'current attempt falls back to default');
    assert.equal(f.voiceInput.getPreferredVoiceInput(), 'synthetic-next', 'new preference survives old selection failure');
    f.api.discard(); await f.api.start();
    assert.equal(f.requests[2].audio.deviceId.exact, 'synthetic-next', 'next capture uses the new preference');
    f.api.dispose(); f.assertReleased();
  }
});
test('input changes apply next capture; denied permission never retries using another input', async () => {
  const f = await fixture().ready(); await f.start(); f.voiceInput.setPreferredVoiceInput('synthetic-second'); assert.equal(f.requests.length, 1);
  f.api.discard(); await f.start(); assert.equal(f.requests[1].audio.deviceId.exact, 'synthetic-second'); f.api.dispose(); f.assertReleased();
  for (const [name, diagnostic] of [['NotAllowedError', 'permission_denied'], ['NotFoundError', 'no_device']]) {
    const denied = await fixture({ preferred: name === 'NotAllowedError' ? 'synthetic-denied' : '', getUserMedia: async () => { throw new DOMException('synthetic failure', name); } }).ready();
    await denied.start(); assert.equal(denied.requests.length, 1); assert.equal(denied.api.diagnostic.value, diagnostic); denied.assertReleased();
  }
});
test('digital silence rejected; quiet/short/initially silent audio and a signal on another channel upload', async () => {
  for (const [name, channels, upload] of [
    ['silence', [new Float32Array(3000)], false], ['codec-digital-zero-residue', [new Float32Array([0, 2e-34])], false], ['quiet', [new Float32Array([0, 1e-12, 0])], true],
    ['short', [new Float32Array([0, 0.1])], true], ['initial-silence', [new Float32Array([...new Array(5000).fill(0), 0.001])], true],
    ['second-channel', [new Float32Array(20), new Float32Array([0, 1e-8])], true]
  ]) {
    const f = await fixture({ channels, meterSample: 0 }).ready(); await f.start(); f.api.stopRecording(); await f.flushStops();
    assert.equal(f.uploads.length, upload ? 1 : 0, name); if (!upload) assert.equal(f.api.diagnostic.value, 'digital_silence'); f.api.dispose(); f.assertReleased();
  }
});
test('uncertain decode, non-finite or empty PCM does not claim absence of speech', async () => {
  for (const options of [{ decodeError: true }, { channels: [new Float32Array([NaN])] }, { channels: [] }]) {
    const f = await fixture({ ...options, meterSample: 0 }).ready(); await f.start(); f.api.stopRecording(); await f.flushStops();
    assert.equal(f.uploads.length, 1); assert.equal(f.api.diagnostic.value, null); f.api.dispose(); f.assertReleased();
  }
});
test('meter follows actual PCM; track.muted reports missing data rather than physical mute', async () => {
  for (const sample of [0, 0.02]) {
    const f = await fixture({ meterSample: sample }).ready(); await f.start(); for (let i = 0; i < 26; i++) f.fireTimer(80);
    assert.equal(f.api.inputLevel.value > 0, sample > 0); if (!sample) assert.match(f.api.inputStatus.value, /Sem sinal detectado/);
    f.streams[0].track.muted = true; f.fireTimer(80); assert.match(f.api.inputStatus.value, /temporariamente sem dados/); f.api.dispose(); f.assertReleased();
  }
});
test('max duration finalizes once; idle creates no capture, context or timer', async () => {
  const f = await fixture({ status: { enabled: true, configured: true, maxRecordingSeconds: 3 } }).ready();
  assert.equal(f.contexts.length, 0); assert.equal(f.requests.length, 0); assert.equal(f.timers.size, 0);
  await f.start(); f.fireTimer(3000); await f.flushStops(); assert.equal(f.uploads.length, 1); assert.equal(f.uploads[0].duration, 3000);
  assert.match(f.api.notice.value, /3 segundos/); f.assertReleased();
});
