import assert from 'node:assert/strict';
import { afterEach, test } from 'node:test';
import { loadTypeScript } from './loadTypeScript.mjs';
const api = await loadTypeScript('../src/services/aegisApi.ts');
const env = await loadTypeScript('../src/services/clientEnvironment.ts');
const originalFetch = globalThis.fetch;
afterEach(() => { globalThis.fetch = originalFetch; });
const encode = value => new TextEncoder().encode(value);

test('NDJSON delivers tokens before completion, including UTF-8 split across chunks', async () => {
  env.configureClient({ apiBaseUrl: 'https://api.example.test', externalLinks: 'browser' });
  let controller;
  const stream = new ReadableStream({ start(value) { controller = value; } });
  const abort = new AbortController();
  globalThis.fetch = async (url, options) => {
    assert.equal(url, 'https://api.example.test/api/chat/messages/stream');
    assert.equal(options.signal, abort.signal);
    return new Response(stream);
  };
  const tokens = [];
  let done = false;
  let sawToken;
  const firstToken = new Promise(resolve => { sawToken = resolve; });
  const completion = api.sendMessageStream({ message: 'teste' }, {
    onConversation() {}, onToken(_turn, text) { tokens.push(text); sawToken(); },
    onDone() { done = true; }, onError() {}
  }, abort.signal);
  const bytes = encode('{"type":"token","turnId":"t","content":"Olá"}\n');
  const split = bytes.indexOf(0xc3) + 1;
  controller.enqueue(bytes.slice(0, split));
  controller.enqueue(bytes.slice(split));
  await firstToken;
  assert.deepEqual(tokens, ['Olá']);
  assert.equal(done, false);
  controller.enqueue(encode('{"type":"done","turnId":"t","conversationId":"c","messageId":"m"}\n'));
  controller.close();
  await completion;
  assert.equal(done, true);
});

test('aborting the transport cancels reading, and premature EOF remains an error', async () => {
  const abort = new AbortController();
  globalThis.fetch = async (_url, options) => new Response(new ReadableStream({
    start(controller) { options.signal.addEventListener('abort', () => controller.error(new DOMException('Cancelled', 'AbortError'))); }
  }));
  const request = api.sendMessageStream({}, { onConversation() {}, onToken() {}, onDone() {}, onError() {} }, abort.signal);
  await new Promise(resolve => setImmediate(resolve));
  abort.abort();
  await assert.rejects(request, { name: 'AbortError' });
  globalThis.fetch = async () => new Response('{"type":"token","turnId":"t","content":"partial"}\n');
  await assert.rejects(api.sendMessageStream({}, { onConversation() {}, onToken() {}, onDone() {}, onError() {} }), /confirmação final/);
});

test('health errors and malformed responses are unavailable', async () => {
  for (const [response, expected] of [[new Response('{"status":"ok"}'), true], [new Response('{"status":"down"}'), false], [new Response('invalid'), false], [new Response('{}', { status: 503 }), false]]) {
    globalThis.fetch = async () => response;
    assert.equal(await api.getHealth(), expected);
  }
});

test('PCM TTS keeps streamed chunks and validates exposed format headers', async () => {
  const headers = { 'X-Aegis-Audio-Format': 'pcm_s16le', 'X-Aegis-Sample-Rate': '24000', 'X-Aegis-Channels': '1' };
  globalThis.fetch = async () => new Response(new Uint8Array([1, 2, 3, 4]), { headers });
  const received = [];
  await api.streamSpeech({}, async chunk => received.push(...chunk));
  assert.deepEqual(received, [1, 2, 3, 4]);
  globalThis.fetch = async () => new Response(new Uint8Array([1, 2]));
  await assert.rejects(api.streamSpeech({}, async () => {}), /Invalid audio format/);
});
