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
    assert.equal(JSON.parse(options.body).includeWebResearchSources, true);
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

test('health request errors do not imply server unavailability', async () => {
  for (const status of [400, 401, 403, 404, 429]) {
    globalThis.fetch = async () => new Response('{}', { status });
    assert.equal(await api.getHealth(), null);
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

test('NDJSON web search status and sources are correlated with the completed turn', async () => {
  const research = {
    sources: [{ url: 'https://example.test/a', title: 'Exemplo', kind: 'consulted' }],
    citations: [{ url: 'https://example.test/a', title: 'Exemplo', startIndex: 0, endIndex: 3 }]
  };
  const events = [
    { type: 'conversation', turnId: 't', conversationId: 'c' },
    { type: 'tool_status', turnId: 't', category: 'web_search', state: 'started', message: 'Pesquisando na web…' },
    { type: 'web_research_sources', turnId: 't', conversationId: 'c', sources: [{ url: 'https://example.test/a', count: 2 },
      { url: 'javascript:alert(1)', count: 1 }, { url: 'https://invalid.test/', count: 0 }] },
    { type: 'token', turnId: 't', content: 'Olá' },
    { type: 'tool_status', turnId: 't', category: 'web_search', state: 'completed', message: 'Pesquisa concluída.' },
    { type: 'done', turnId: 't', conversationId: 'c', messageId: 'm', webResearch: research }
  ];
  globalThis.fetch = async () => new Response(events.map(event => JSON.stringify(event)).join('\n'));
  const observed = [];
  await api.sendMessageStream({}, {
    onConversation: (turn, id) => observed.push([turn, id]),
    onToolStatus: (turn, value) => observed.push([turn, value.category, value.state]),
    onWebResearchSources: (turn, sources) => observed.push([turn, sources]),
    onToken: (turn, content) => observed.push([turn, content]),
    onDone: value => observed.push([value.turnId, value.messageId, value.webResearch]),
    onError: assert.fail
  });
  assert.deepEqual(observed, [['t', 'c'], ['t', 'web_search', 'started'],
    ['t', [{ url: 'https://example.test/a', count: 2 }]], ['t', 'Olá'],
    ['t', 'web_search', 'completed'], ['t', 'm', research]]);
});
