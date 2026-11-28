import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import vm from 'node:vm';
import ts from 'typescript';

const source = readFileSync(new URL('../src/services/voicePlaybackService.ts', import.meta.url), 'utf8');
const serviceCode = ts.transpileModule(source, {
  compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS }
}).outputText;
const sourceRate = 24000;
const quantum = 128;

function pcm(frames, value = 16384) {
  const bytes = new Uint8Array(frames * 2);
  const samples = new DataView(bytes.buffer);
  for (let frame = 0; frame < frames; frame++) samples.setInt16(frame * 2, value, true);
  return bytes;
}

async function fixture(sampleRate = sourceRate) {
  const toWorklet = [];
  const toService = [];
  const states = [];
  let processor;
  let node;
  let workletModule;
  class AudioContext {
    sampleRate = sampleRate;
    state = 'running';
    audioWorklet = {
      async addModule() {
        vm.runInNewContext(await workletModule.text(), {
          sampleRate,
          AudioWorkletProcessor: class {
            port = { postMessage: data => toService.push(data) };
          },
          registerProcessor(_name, Processor) { processor = new Processor(); }
        });
      }
    };
  }
  class AudioWorkletNode {
    constructor() { node = this; }
    port = { postMessage: (data, transfer = []) => toWorklet.push(structuredClone(data, { transfer })) };
    connect() {}
  }
  const sandbox = {
    AudioContext,
    AudioWorkletNode,
    Blob,
    URL: {
      createObjectURL(blob) { workletModule = blob; return 'blob:fixture'; },
      revokeObjectURL() {}
    },
    exports: {}
  };
  vm.runInNewContext(serviceCode, sandbox);
  const player = new sandbox.exports.VoicePlaybackService();
  player.onState(state => states.push(state));
  await player.ensureReady();
  const deliver = (messages, port) => {
    while (messages.length > 0) port.onmessage({ data: messages.shift() });
  };
  const flush = async () => {
    // A consumed event can release a waiting producer, which posts the next packet.
    for (let i = 0; i < 8; i++) {
      deliver(toWorklet, processor.port);
      deliver(toService, node.port);
      await Promise.resolve();
    }
  };
  const render = async (blocks, acknowledge = true) => {
    const output = new Float32Array(blocks * quantum);
    for (let i = 0; i < blocks; i++) {
      deliver(toWorklet, processor.port);
      processor.process([], [[output.subarray(i * quantum, (i + 1) * quantum)]]);
      if (acknowledge) await flush();
    }
    return output;
  };
  return { player, states, flush, render };
}

test('the real worklet bridges irregular 80 ms packets arriving every 120 ms', async () => {
  const { player, render, states } = await fixture(48000);
  const generation = player.begin();
  // Slightly over 80 ms so two packets meet the reserve even with interpolation carry.
  const packetFrames = 1924;
  await player.enqueuePcm(pcm(packetFrames), generation);
  assert.ok((await render(45)).every(value => value === 0));
  await player.enqueuePcm(pcm(packetFrames), generation);
  const first = await render(45);
  assert.ok(first.every(value => value === 0.5));
  await player.enqueuePcm(pcm(packetFrames), generation);
  const finished = player.finish(generation);
  const remaining = packetFrames * 6 - first.length;
  const last = await render(Math.ceil(remaining / quantum));
  await finished;
  assert.ok(last.subarray(0, remaining).every(value => value === 0.5));
  assert.ok(last.subarray(remaining).every(value => value === 0));
  assert.equal(player.getMetrics().underrunCount, 0);
  assert.equal(states.at(-1), 'idle');
});

test('the worklet rebuilds its reserve after starvation and EOF releases short audio', async () => {
  const { player, render, states } = await fixture();
  const generation = player.begin();
  await player.enqueuePcm(pcm(3840), generation);
  const first = await render(31);
  assert.ok(first.subarray(0, 3840).every(value => value === 0.5));
  assert.ok(first.subarray(3840).every(value => value === 0));
  assert.equal(states.at(-1), 'buffering');
  assert.equal(player.getMetrics().underrunCount, 1);

  await player.enqueuePcm(pcm(1920), generation);
  assert.ok((await render(12)).every(value => value === 0));
  assert.equal(player.getMetrics().underrunCount, 1);
  await player.enqueuePcm(pcm(1920), generation);
  assert.ok((await render(31)).subarray(0, 3840).every(value => value === 0.5));
  assert.equal(player.getMetrics().underrunCount, 2);

  await player.enqueuePcm(pcm(17, -8192), generation);
  const finished = player.finish(generation);
  const final = await render(2);
  await finished;
  assert.ok(final.subarray(0, 17).every(value => value === -0.25));
  assert.ok(final.subarray(17).every(value => value === 0));
  assert.equal(player.getMetrics().underrunCount, 2);
  assert.equal(states.at(-1), 'idle');
});

test('large bursts are backpressured with no frame loss or playback speed change', async () => {
  const { player, render, flush } = await fixture(48000);
  const generation = player.begin();
  const inputFrames = 30017;
  const raw = pcm(inputFrames);
  let accepted = false;
  const streamed = (async () => {
    await player.enqueuePcm(raw.subarray(0, 403), generation);
    await player.enqueuePcm(raw.subarray(403), generation);
    accepted = true;
    await player.finish(generation);
  })();
  await flush();
  assert.equal(accepted, false);
  assert.ok(player.getMetrics().queuedAudioSeconds > 0.16);
  assert.ok(player.getMetrics().queuedAudioSeconds <= 0.4);
  const output = await render(Math.ceil(inputFrames * 2 / quantum) + 1);
  await streamed;
  assert.ok(output.subarray(0, inputFrames * 2).every(value => value === 0.5));
  assert.ok(output.subarray(inputFrames * 2).every(value => value === 0));
  assert.equal(player.getMetrics().underrunCount, 0);
});

test('stop releases backpressure and stale worklet events cannot end the next generation', async () => {
  const { player, flush, render, states } = await fixture();
  const old = player.begin();
  const abandoned = player.enqueuePcm(pcm(24000), old);
  await flush();
  await render(76, false);
  player.stop();
  await abandoned;

  const current = player.begin();
  await player.enqueuePcm(pcm(87, -16384), current);
  await flush();
  assert.equal(player.getMetrics().queuedAudioSeconds, 87 / sourceRate);
  assert.equal(player.getMetrics().underrunCount, 0);
  assert.equal(states.at(-1), 'buffering');

  let drained = false;
  const finished = player.finish(current).then(() => { drained = true; });
  await flush();
  assert.equal(drained, false);

  const output = await render(2);
  await finished;
  assert.ok(output.subarray(0, 87).every(value => value === -0.5));
  assert.ok(output.subarray(87).every(value => value === 0));
  assert.equal(states.at(-1), 'idle');
});
