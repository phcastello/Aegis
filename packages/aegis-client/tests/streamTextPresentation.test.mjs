import assert from 'node:assert/strict';
import test from 'node:test';
import vm from 'node:vm';
import { readFileSync } from 'node:fs';
import ts from 'typescript';
const exports = {};
vm.runInNewContext(ts.transpileModule(readFileSync(new URL('../src/services/streamTextPresentation.ts', import.meta.url), 'utf8'), { compilerOptions: { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.CommonJS } }).outputText, { exports, URL });
const { StreamTextReveal, STREAM_FRAME_MS, NativeVoiceSegments, parseNativeVoiceStreamEvent } = exports;

test('one burst unfolds in small real prefixes and settles with byte-for-byte original text', () => {
  const text = 'Uma resposta que chegou de uma vez, com **Markdown**, links e emojis 🚀. '.repeat(10).trim();
  const reveal = new StreamTextReveal(); reveal.append(text, 0);
  let previous = '', frames = 0;
  for (let now = 0; reveal.pending && now <= 1850; now += STREAM_FRAME_MS) {
    const next = reveal.step(now); assert.ok(next.startsWith(previous) && text.startsWith(next));
    assert.ok(!/[\uD800-\uDBFF]$/.test(next), 'never split a surrogate pair');
    assert.ok(next.length > previous.length); previous = next; frames++;
  }
  assert.ok(frames > 10, 'should be a real sequence, not one giant jump');
  assert.equal(previous, text); assert.equal(reveal.pending, false);
});

test('later pieces extend the same text; interrupt flushes exactly what arrived', () => {
  const reveal = new StreamTextReveal(); reveal.append('Primeiro ', 0); const first = reveal.step(32);
  assert.ok(first.length > 0 && first.length < 9);
  reveal.append('e segundo 🚀.', 40);
  assert.ok(reveal.step(64).startsWith(first));
  assert.equal(reveal.finish(), 'Primeiro e segundo 🚀.'); assert.equal(reveal.pending, false);
});

const turnId = '0b06330a-9942-47dc-8587-2434ca56c6fd';
const conversationId = '0b06330a-9942-47dc-8587-2434ca56c6fe';
const assistantMessageId = '0b06330a-9942-47dc-8587-2434ca56c6ff';
const segmentId = '0b06330a-9942-47dc-8587-2434ca56c6f0';
const segment = { type: 'final_segment', turnId, conversationId, assistantMessageId, segmentId, sequence: 0, content: 'Olá.' };
test('native stream only accepts consecutive committed segments within the same turn, conversation and message', () => {
  const stream = new NativeVoiceSegments(turnId, conversationId);
  assert.equal(stream.append(parseNativeVoiceStreamEvent(segment)), 'Olá.');
  assert.equal(stream.append(parseNativeVoiceStreamEvent(segment)), '');
  assert.equal(stream.append(parseNativeVoiceStreamEvent({...segment, content: 'Conflicting duplicate'})), null);
  assert.equal(stream.append(parseNativeVoiceStreamEvent({...segment, sequence: 2, segmentId: assistantMessageId})), null);
  assert.equal(stream.append(parseNativeVoiceStreamEvent({...segment, sequence: 1, segmentId: assistantMessageId, conversationId: turnId})), null);
  assert.equal(stream.append(parseNativeVoiceStreamEvent({...segment, sequence: 1, segmentId: assistantMessageId, assistantMessageId: turnId})), null);
  assert.equal(stream.append(parseNativeVoiceStreamEvent({...segment, sequence: 1, segmentId: assistantMessageId, content: 'Tudo bem?'})), ' Tudo bem?');
  assert.equal(stream.assistantMessageId, assistantMessageId);
});
test('reject invalid event shapes and accept no-conversation terminal ignored/cancelled', () => {
  for (const value of [null, {}, { ...segment, type: 'token' }, { ...segment, sequence: -1 }, { ...segment, content: '' }, { ...segment, assistantMessageId: 'x' }, { ...segment, segmentId: undefined }, { ...segment, content: 'x'.repeat(16001) }]) assert.equal(parseNativeVoiceStreamEvent(value), null);
  assert.ok(parseNativeVoiceStreamEvent({type:'ignored',turnId,conversationId:null,assistantMessageId:null}));
  assert.equal(parseNativeVoiceStreamEvent({type:'done',turnId,conversationId:null,assistantMessageId:null}), null);
});

test('native tool status verifies accepted turn shape without establishing speech segments', () => {
  const progress = { type: 'tool_status', turnId, conversationId, assistantMessageId: null,
    toolStatus: { category: 'web_search', state: 'started', message: 'Pesquisando na web…' } };
  const parsed = parseNativeVoiceStreamEvent(progress);
  assert.equal(parsed.type, 'tool_status');
  assert.equal(parsed.toolStatus.message, progress.toolStatus.message);
  const script = parseNativeVoiceStreamEvent({ ...progress,
    toolStatus: { category: 'python', state: 'started', message: 'Executando Python…' } });
  assert.equal(script.toolStatus.category, 'python');
  assert.equal(script.toolStatus.message, 'Executando Python…');
  const video = parseNativeVoiceStreamEvent({ ...progress,
    toolStatus: { category: 'video', state: 'started', message: 'Analisando vídeo…' } });
  assert.equal(video.toolStatus.category, 'video');
  const stream = new NativeVoiceSegments(turnId, conversationId);
  assert.ok(stream.matches(parsed));
  assert.equal(stream.append(parsed), null);
  assert.equal(stream.assistantMessageId, null);
  for (const value of [
    {...progress, conversationId: null},
    {...progress, assistantMessageId},
    {...progress, toolStatus: null},
    {...progress, toolStatus: {...progress.toolStatus, category: 'calendar'}},
    {...progress, toolStatus: {...progress.toolStatus, state: 'unknown'}},
    {...progress, toolStatus: {...progress.toolStatus, message: ''}},
    {...progress, toolStatus: {...progress.toolStatus, message: 'x'.repeat(241)}}
  ]) assert.equal(parseNativeVoiceStreamEvent(value), null);
  assert.equal(stream.matches(parseNativeVoiceStreamEvent({...progress, conversationId: assistantMessageId})), false);
});

test('native research increments retain safe URLs and positive counts for the owning turn', () => {
  const event = { type: 'web_research_sources', turnId, conversationId, sources: [
    { url: 'https://EXAMPLE.test:443/source', count: 2 },
    { url: 'https://user:secret@example.test/source', count: 1 },
    { url: 'javascript:alert(1)', count: 1 },
    { url: 'https://example.test/unobserved', count: 0 },
  ] };
  const parsed = parseNativeVoiceStreamEvent(event);
  assert.equal(parsed.type, 'web_research_sources');
  assert.equal(JSON.stringify(parsed.sources), '[{"url":"https://example.test/source","count":2}]');
  const stream = new NativeVoiceSegments(turnId, conversationId);
  assert.ok(stream.matches(parsed));
  assert.equal(stream.append(parsed), null);
  assert.equal(parseNativeVoiceStreamEvent({ ...event, conversationId: null }), null);
  assert.equal(parseNativeVoiceStreamEvent({ ...event, assistantMessageId }), null);
  assert.equal(parseNativeVoiceStreamEvent({ ...event, sources: event.sources.slice(1) }), null);
});
