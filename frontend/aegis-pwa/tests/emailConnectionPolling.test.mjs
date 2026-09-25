import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import typescript from 'typescript';

const source = await readFile(new URL('../src/services/emailConnectionPolling.ts', import.meta.url), 'utf8');
const compiled = typescript.transpileModule(source, {
  compilerOptions: { module: typescript.ModuleKind.ESNext, target: typescript.ScriptTarget.ES2022 }
}).outputText;
const { waitForEmailConnection } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);

test('confirms only after status becomes connected', async () => {
  let attempts = 0;
  const result = await waitForEmailConnection(async () => {
    attempts += 1;
    if (attempts === 2) throw new Error('temporary status failure');
    return attempts === 3;
  }, new AbortController().signal, 1, 200);

  assert.equal(result, 'connected');
  assert.equal(attempts, 3);
});

test('expires when status never confirms connection', async () => {
  let attempts = 0;
  const result = await waitForEmailConnection(async () => {
    attempts += 1;
    return false;
  }, new AbortController().signal, 5, 30);

  assert.equal(result, 'timeout');
  assert.ok(attempts > 0 && attempts < 10);
});

test('aborts an in-flight status request when the flow is cancelled', async () => {
  const flow = new AbortController();
  let requestAborted = false;
  const waiting = waitForEmailConnection((signal) => new Promise((_, reject) => {
    signal.addEventListener('abort', () => {
      requestAborted = true;
      reject(new Error('aborted'));
    }, { once: true });
  }), flow.signal, 1, 200);

  flow.abort();
  assert.equal(await waiting, 'cancelled');
  assert.equal(requestAborted, true);
});

test('does not accept a late connected response after cancellation', async () => {
  const flow = new AbortController();
  let finishRequest;
  const waiting = waitForEmailConnection(() => new Promise((resolve) => {
    finishRequest = resolve;
  }), flow.signal, 1, 200);

  flow.abort();
  finishRequest(true);
  assert.equal(await waiting, 'cancelled');
});
