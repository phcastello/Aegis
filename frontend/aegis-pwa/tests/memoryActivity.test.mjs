import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import typescript from 'typescript';

async function loadTypeScript(path) {
  const source = await readFile(new URL(path, import.meta.url), 'utf8');
  const compiled = typescript.transpileModule(source, {
    compilerOptions: { module: typescript.ModuleKind.ESNext, target: typescript.ScriptTarget.ES2022 }
  }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);
}

const presentation = await loadTypeScript('../src/services/memoryActivityPresentation.ts');
const { pollMemoryActivity } = await loadTypeScript('../src/services/memoryActivityPolling.ts');
const section = (kind, items = ['Fato.']) => ({ kind, items, totalCount: items.length });

test('final titles and section order are stable', () => {
  const expected = {
    used: 'Usou memória', consulted: 'Consultou memória', created: 'Guardou memória',
    updated: 'Atualizou memória', deleted: 'Apagou memória'
  };
  for (const [kind, title] of Object.entries(expected))
    assert.equal(presentation.memoryActivityTitle([section(kind)]), title);
  const ordered = presentation.orderedMemorySections([
    section('deleted'), section('created'), section('used'), section('updated'), section('consulted')
  ]);
  assert.deepEqual(ordered.map((item) => item.kind), ['used', 'consulted', 'created', 'updated', 'deleted']);
  assert.equal(presentation.memoryActivityTitle(ordered), 'Memória');
  assert.equal(presentation.memoryActivityTitle([]), 'Memória');
  assert.ok(!Object.values(expected).some((title) => title.includes('Esqueceu')));
});

test('at most three distinct facts are shown, followed by the remaining count', () => {
  const items = ['A', 'B', 'C', 'D', 'E'];
  assert.deepEqual(presentation.visibleMemoryItems(items), ['A', 'B', 'C']);
  assert.equal(presentation.remainingMemoryItems(5, items), 2);
  assert.deepEqual(presentation.visibleMemoryItems(['A', 'A', 'B']), ['A', 'B']);
});

test('native disclosure starts collapsed and has a keyboard accessible summary', async () => {
  const source = await readFile(new URL('../src/components/MemoryActivityDisclosure.vue', import.meta.url), 'utf8');
  assert.match(source, /<details v-if="sections\.length"/);
  assert.match(source, /<summary class="memory-activity__summary">/);
  assert.doesNotMatch(source, /<details[^>]*\sopen(?:=|\s|>)/);
  assert.doesNotMatch(source, /[🧠💾📚✨]/u);
});

test('pending Used becomes Used plus Created without reload', async () => {
  const seen = [];
  let attempts = 0;
  const result = await pollMemoryActivity(async () => {
    attempts++;
    return attempts === 1
      ? { pending: true, sections: [section('used')] }
      : { pending: false, sections: [section('used'), section('created')] };
  }, (snapshot) => seen.push(snapshot), new AbortController().signal, () => true, 1, 100);
  assert.equal(result, 'completed');
  assert.equal(seen.length, 2);
  assert.equal(presentation.memoryActivityTitle(seen[1].sections), 'Memória');
});

test('conversation change or unmount cancels polling and ignores late response', async () => {
  const controller = new AbortController();
  let release;
  const seen = [];
  const running = pollMemoryActivity(() => new Promise((resolve) => { release = resolve; }),
    (snapshot) => seen.push(snapshot), controller.signal, () => true, 1, 100);
  await new Promise((resolve) => setTimeout(resolve, 5));
  controller.abort();
  release({ pending: false, sections: [section('created')] });
  assert.equal(await running, 'cancelled');
  assert.deepEqual(seen, []);
});

test('polling stops after the timeout even when the request is still pending', async () => {
  let requestAborted = false;
  const result = await pollMemoryActivity((signal) => new Promise((_, reject) => {
    signal.addEventListener('abort', () => { requestAborted = true; reject(new Error('aborted')); }, { once: true });
  }), () => assert.fail('no update expected'), new AbortController().signal, () => true, 1, 20);
  assert.equal(result, 'timeout');
  assert.equal(requestAborted, true);
});
