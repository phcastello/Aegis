// Real Vue / Chromium, public API shape with in-memory fixtures, and simulated
// native IPC. No microphone, private backend, model call or real audio playback.
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { createServer as createPortProbe } from 'node:net';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const requireNative = createRequire(join(root, 'apps/aegis-node/package.json'));
const { chromium } = createRequire(join(root, 'packages/aegis-client/package.json'))('playwright-core');
const { createServer } = await import(pathToFileURL(requireNative.resolve('vite')).href);
const uuid = number => `10000000-0000-4000-8000-${String(number).padStart(12, '0')}`;
const a = uuid(1), b = uuid(2);
const date = '2026-11-25T12:00:00Z';
const message = (id, conversationId, content, role = 'assistant') => ({ id, conversationId, role, content, createdAt: date });
const data = new Map([a, b].map((id, index) => [id, { id, title: index ? 'Beta' : 'Alfa', titleSource: 'manual',
  createdAt: date, updatedAt: date, messages: [message(uuid(10 + index), id, `Histórico ${index ? 'Beta' : 'Alfa'}.`)] }]));
const summary = row => ({ ...row, messages: undefined, messageCount: row.messages.length, lastMessagePreview: row.messages.at(-1)?.content });
const reads = [];
const researchJobs = new Map();
const researchPrompt = 'Compare as fontes sobre as novas regras.';
const researchIntent = 'Vou pesquisar as novas regras e reunir as fontes.';
const followUpPrompt = 'Qual é o próximo item da lista?';
const followUpReply = 'O próximo item é a revisão.';
const typedText = `Fluxo digitado em caracteres. ${'Cada letra de um único lote deve aparecer progressivamente, incluindo a música 🎵 e a acentuação. '.repeat(8).trim()}`;
let typedRequests = 0;
let holdResearchRead;
function enqueueResearch(id, conversationId, userMessageId) {
  const now = new Date().toISOString();
  const job = { id, conversationId, userMessageId, state: 'running',
    phase: 'searching', createdAt: now, updatedAt: now, sources: [] };
  researchJobs.set(id, job);
  return job;
}
const json = (res, value, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' }); res.end(JSON.stringify(value)); };
const port = await new Promise((resolve, reject) => {
  const probe = createPortProbe(); probe.once('error', reject);
  probe.listen(0, '127.0.0.1', () => { const { port } = probe.address(); probe.close(() => resolve(port)); });
});

const vite = await createServer({
  root: join(root, 'apps/aegis-node'),
  cacheDir: join(tmpdir(), `aegis-voice-stream-vite-${process.pid}`),
  resolve: { alias: { '@aegis/client': join(root, 'packages/aegis-client/src') } },
  server: { host: '127.0.0.1', port, strictPort: false },
  plugins: [{ name: 'desktop-voice-stream-fixture', configureServer(server) {
    server.middlewares.use((req, res, next) => {
      const url = new URL(req.url, 'http://localhost');
      const path = url.pathname;
      if (!path.startsWith('/api/')) return next();
      if (path === '/api/health') return json(res, { status: 'ok' });
      if (path === '/api/voice/status') return json(res, { enabled: false, available: false });
      if (path === '/api/voice/transcription/status') return json(res, { enabled: false, configured: false });
      if (path === '/api/email/status') return json(res, { isConnected: false });
      if (path === '/api/chat/proactive-activity') return json(res, []);
      if (path === '/api/chat/conversations') return json(res, { items: [...data.values()].map(summary), hasMore: false });
      if (path === '/api/chat/research-jobs') {
        if (req.method !== 'GET') return json(res, {}, 405);
        const id = url.searchParams.get('conversationId');
        const snapshots = structuredClone([...researchJobs.values()].filter(job => job.conversationId === id));
        if (id === a && holdResearchRead) {
          const hold = holdResearchRead;
          holdResearchRead = undefined;
          hold(() => json(res, snapshots));
        } else {
          json(res, snapshots);
        }
        return;
      }
      if (path.startsWith('/api/chat/research-jobs/') && req.method === 'DELETE') {
        const job = researchJobs.get(path.split('/').at(-1));
        if (!job) return json(res, {}, 404);
        job.state = 'cancelled'; job.updatedAt = new Date().toISOString();
        return json(res, job);
      }
      if (path === '/api/chat/messages/stream') {
        let raw = '';
        req.on('data', chunk => { raw += chunk; });
        req.on('end', () => {
          const body = JSON.parse(raw), target = data.get(body.conversationId);
          assert.ok(target, 'typed request targets known conversation');
          typedRequests++;
          const isResearch = body.content === researchPrompt;
          const isFollowUp = body.content === followUpPrompt;
          const userId = uuid(isResearch ? 89 : isFollowUp ? 93 : 71);
          const assistantId = uuid(isResearch ? 90 : isFollowUp ? 94 : 72);
          const reply = isResearch ? researchIntent : isFollowUp ? followUpReply : typedText;
          target.messages.push(message(userId, target.id, body.content, 'user'), message(assistantId, target.id, reply));
          target.updatedAt = new Date().toISOString();
          if (isResearch) enqueueResearch(body.turnId, target.id, userId);
          const line = (type, extra) => JSON.stringify({ type, turnId: body.turnId, ...extra }) + '\n';
          // One network write containing a token for *every* Unicode code
          // point, and completion in the exact same NDJSON chunk.
          const burst = line('conversation', { conversationId: target.id }) + [...reply].map(content => line('token', { content })).join('') + line('done', { conversationId: target.id, messageId: assistantId, conversationTitle: target.title, titleSource: target.titleSource });
          res.writeHead(200, { 'Content-Type': 'application/x-ndjson', 'Cache-Control': 'no-store' }); res.end(burst);
        });
        return;
      }
      if (path.startsWith('/api/chat/turns/')) return json(res, null, 204);
      const match = path.match(/^\/api\/chat\/conversations\/([^/]+)$/);
      if (match) {
        const row = data.get(match[1]);
        reads.push({ id: match[1], last: row?.messages.at(-1)?.id ?? null });
        return row ? json(res, structuredClone(row)) : json(res, {}, 404);
      }
      if (path.endsWith('/memory-activity')) return json(res, { pending: false, sections: [] });
      return json(res, {}, 404);
    });
  } }]
});

let browser;
try {
  await vite.listen();
  const origin = `http://127.0.0.1:${vite.httpServer.address().port}`;
  browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
  const context = await browser.newContext({ viewport: { width: 1250, height: 850 } });
  await context.addInitScript(origin => {
    localStorage.setItem('aegis.voice.autoSpeak', 'false');
    const callbacks = new Map(), listeners = new Map(); let sequence = 0;
    window.fixtureNativeSubscribed = name => [...listeners.values()].some(row => row.name === name);
    window.fixtureNativeEmit = (name, payload) => {
      for (const [id, row] of listeners) if (row.name === name) callbacks.get(row.callback)?.({ event: name, id, payload });
    };
    window.fixtureCommands = [];
    window.__TAURI_EVENT_PLUGIN_INTERNALS__ = { unregisterListener: (_name, id) => listeners.delete(id) };
    window.__TAURI_INTERNALS__ = {
      transformCallback: callback => { const id = ++sequence; callbacks.set(id, callback); return id; },
      invoke: async (command, args) => {
        if (command === 'plugin:event|listen') { const id = ++sequence; listeners.set(id, { name: args.event, callback: args.handler }); return id; }
        if (command === 'plugin:event|unlisten') { listeners.delete(args.eventId); return; }
        window.fixtureCommands.push({ command, operation: args?.operation ?? args?.request?.operation, url: args?.url });
        if (command === 'runtime_info') return { platform: 'windows', backendUrl: origin, version: '0.8.0', processSessionId: 'desktop-voice-stream-test' };
        if (command === 'node_status') return { state: 'unpaired', node: null };
        if (command === 'node_transport_status') return { transportState: 'online', conversationChanges: true };
        if (command === 'node_notification_settings') return { granted: false, pushConfigured: false, pendingActions: 0 };
        if (command === 'node_consume_reminder_open') return { reminderId: null, conversationId: null };
        if (command === 'node_read_visibility') return true;
        if (command === 'check_android_update') return null;
        if (command === 'node_hands_free') throw new Error('fixture has no device audio');
      }
    };
  }, origin);

  const page = await context.newPage(); page.setDefaultTimeout(10000);
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  const stream = 'aegis-windows-voice-chat-stream';
  const emit = (name, payload) => page.evaluate(({ name, payload }) => window.fixtureNativeEmit(name, payload), { name, payload });
  const row = title => page.locator('.history-item__main').filter({ hasText: title });
  const title = () => page.locator('.chat-header h1').textContent();
  const select = async name => { await row(name).click(); await page.waitForFunction(name => document.querySelector('.chat-header h1')?.textContent?.trim() === name, name); };
  const assistant = page.locator('.message-row--assistant .markdown-message');
  const marker = value => assistant.filter({ hasText: value });
  const currentText = async prefix => (await marker(prefix).first().textContent())?.trim() ?? '';
  const exact = text => page.locator('.messages').getByText(text, { exact: true });
  const researchActivity = label => page.locator('.web-research-activity').filter({ visible: true }).and(page.getByRole('status', { name: label, exact: true }));
  const event = (type, turnId, conversationId = null, assistantMessageId = null, extra = {}) => ({ type, turnId, conversationId, assistantMessageId, ...extra });
  const segment = (turn, conversation, id, sequence, content) => event('final_segment', turn, conversation, id, { sequence, segmentId: uuid(100 + sequence + Number(turn.slice(-3))), content });
  const accepted = (turn, conversation) => emit(stream, event('conversation', turn, conversation));
  const sync = id => emit('aegis-conversation-sync', { kind: 'changed', conversationId: id });
  const researchStatus = (turn, conversationId, state, message) => emit(stream,
    event('tool_status', turn, conversationId, null, { toolStatus: { category: 'web_search', state, message } }));
  const speaking = () => emit('aegis-windows-voice-state', { status: 'speaking', generation: 12 });
  const idle = () => emit('aegis-windows-voice-state', { status: 'idle', generation: 13 });
  function append(conversation, id, content, role = 'assistant') {
    const target = data.get(conversation);
    target.messages.push(message(id, conversation, content, role));
    target.updatedAt = new Date().toISOString();
  }
  async function waitPartial(prefix, full) {
    await page.waitForFunction(({ prefix, full }) => {
      const el = [...document.querySelectorAll('.message-row--assistant .markdown-message')].find(node => node.textContent?.includes(prefix));
      const text = el?.textContent?.trim();
      return text && full.startsWith(text) && text.length < full.length;
    }, { prefix, full });
  }
  async function frameSamples(prefix, duration = 220) {
    return page.evaluate(({ prefix, duration }) => new Promise(resolve => {
      const values = []; let start;
      function frame(at) {
        start ??= at;
        const el = [...document.querySelectorAll('.message-row--assistant .markdown-message')].find(node => node.textContent?.includes(prefix));
        const text = el?.textContent?.trim(); if (text) values.push(text);
        if (at - start < duration) requestAnimationFrame(frame); else resolve(values);
      }
      requestAnimationFrame(frame);
    }), { prefix, duration });
  }
  function assertProgress(values, expected, label) {
    assert.ok(new Set(values.map(text => text.length)).size >= 3, `${label}: characters must be presented in multiple frames (${values.map(value => value.length).join(', ')})`);
    assert.ok(values.every(value => expected.startsWith(value) && value.length < expected.length), `${label}: never fabricate, duplicate or jump ahead`);
    assert.ok(values.every(value => !/[\uD800-\uDBFF]$/u.test(value)), `${label}: never expose a dangling UTF-16 surrogate`);
    assert.ok(values.every((value, index) => index === 0 || value.length >= values[index - 1].length), `${label}: presentation cannot regress`);
  }

  await page.goto(origin); await page.locator('.composer textarea').waitFor();
  await page.waitForFunction(name => window.fixtureNativeSubscribed?.(name), stream);
  await page.waitForFunction(() => window.fixtureNativeSubscribed?.('aegis-windows-voice-state') && window.fixtureNativeSubscribed?.('aegis-conversation-sync'));
  await select('Alfa'); await exact('Histórico Alfa.').waitFor();

  // Research only appears after acceptance, stays scoped to the owning chat,
  // and a terminal event removes the transient status without a fake message.
  const researchTurn = uuid(80), researchId = uuid(81), researchText = 'Resultado da pesquisa confirmado.';
  const researchMessage = 'Pesquisando fontes na web…';
  await researchStatus(researchTurn, a, 'started', 'STATUS ANTES DO ACEITE');
  await page.waitForTimeout(100);
  assert.equal(await researchActivity('STATUS ANTES DO ACEITE').count(), 0);
  const beforeResearch = await assistant.count();
  append(a, uuid(82), 'Pesquisar na web.', 'user');
  await accepted(researchTurn, a);
  await researchStatus(researchTurn, a, 'started', researchMessage);
  await researchActivity(researchMessage).waitFor();
  assert.equal(await exact(researchMessage).count(), 0, 'progress uses icons instead of visible narration');
  await emit(stream, event('web_research_sources', researchTurn, a, null, { sources: [
    { url: 'https://atlas.test/a', count: 2 }, { url: 'https://www.norte.test/a', count: 3 },
    { url: 'https://norte.test/b', count: 1 }, { url: 'https://trilha.test/a', count: 1 },
    { url: 'https://leste.test/a', count: 3 }
  ] }));
  const disks = researchActivity(researchMessage).locator('[data-site]');
  await disks.nth(2).waitFor();
  assert.deepEqual(await disks.evaluateAll(nodes => nodes.map(node => node.dataset.site)), ['norte.test', 'leste.test', 'atlas.test']);
  assert.equal(await assistant.count(), beforeResearch);
  await select('Beta'); await exact('Histórico Beta.').waitFor();
  assert.equal(await researchActivity(researchMessage).count(), 0);
  await select('Alfa'); await researchActivity(researchMessage).waitFor();
  append(a, researchId, researchText);
  const finalSources = [
    { url: 'https://www.norte.test/a?versao=final', title: 'Fonte persistida', kind: 'opened' },
    { url: 'https://norte.test/b', title: 'Segunda página do mesmo site', kind: 'consulted' },
    { url: 'https://leste.test/a', title: 'Leste', kind: 'consulted' },
    { url: 'https://atlas.test/a', title: 'Atlas', kind: 'consulted' },
    { url: 'https://trilha.test/a', title: 'Trilha', kind: 'consulted' },
    { url: 'javascript:alert(1)', title: 'URL inválida', kind: 'consulted' }
  ];
  data.get(a).messages.at(-1).webResearch = { sources: finalSources, citations: [] };
  await emit(stream, event('done', researchTurn, a, researchId)); await sync(a);
  await exact(researchText).waitFor();
  const result = page.locator('article.message-row--assistant').filter({ hasText: researchText });
  const sourceAction = result.getByRole('button', { name: 'Fontes da pesquisa (5 links)' });
  await sourceAction.waitFor();
  assert.equal(await result.locator('.message-actions > button').last().getAttribute('aria-controls'),
    await sourceAction.getAttribute('aria-controls'));
  assert.equal(await result.locator('details').count(), 0);
  assert.doesNotMatch(await result.locator('.message-actions').innerText(), /Fontes da pesquisa/, 'the old disclosure title is visually replaced');
  assert.deepEqual(await sourceAction.locator('[data-site]').evaluateAll(nodes => nodes.map(node => node.dataset.site)),
    ['norte.test', 'leste.test', 'atlas.test']);
  const sourcePanel = result.locator('.message-research-sources');
  assert.equal(await sourceAction.getAttribute('aria-expanded'), 'false');
  assert.equal(await sourcePanel.isVisible(), false);
  await sourceAction.press('Space');
  assert.equal(await sourceAction.getAttribute('aria-expanded'), 'true');
  assert.equal(await sourcePanel.getByRole('link').count(), 5);
  const firstSource = sourcePanel.getByRole('link', { name: 'Fonte persistida' });
  assert.equal(await firstSource.getAttribute('href'), finalSources[0].url);
  await firstSource.click();
  await page.waitForFunction(url => window.fixtureCommands.some(command =>
    command.command === 'plugin:opener|open_url' && command.url === url), finalSources[0].url);
  await sourceAction.press('Enter');
  assert.equal(await sourcePanel.isVisible(), false);
  await researchActivity(researchMessage).waitFor({ state: 'hidden' });
  await researchStatus(researchTurn, a, 'started', 'STATUS DEPOIS DO TÉRMINO');
  await page.waitForTimeout(100);
  assert.equal(await researchActivity('STATUS DEPOIS DO TÉRMINO').count(), 0);

  const composer = page.getByRole('textbox', { name: 'Mensagem', exact: true });
  // Keep one empty GET from before the accepted turn in flight. Its result
  // must not prevent the terminal callback from discovering the queued job.
  const staleResearchRead = new Promise(resolve => { holdResearchRead = resolve; });
  await sync(a);
  const releaseStaleSnapshot = await staleResearchRead;
  await composer.fill(researchPrompt);
  assert.equal(await page.getByRole('button', { name: 'Pesquisar em segundo plano' }).count(), 0);
  await page.getByRole('button', { name: 'Enviar mensagem' }).click();
  await exact(researchPrompt).waitFor();
  await exact(researchIntent).waitFor();
  releaseStaleSnapshot();
  await researchActivity('Pesquisa em segundo plano').waitFor();
  await composer.fill(followUpPrompt);
  await page.getByRole('button', { name: 'Enviar mensagem' }).click();
  await exact(followUpReply).waitFor();
  await researchActivity('Pesquisa em segundo plano').waitFor();
  await select('Beta');
  assert.equal(await researchActivity('Pesquisa em segundo plano').count(), 0);
  const firstJob = [...researchJobs.values()][0];
  const backgroundAnswer = 'Resultado independente da conversa aberta.';
  const unplacedSource = { url: 'https://prefeitura.pbh.gov.br/transporte/relatorio.pdf', title: 'Transporte :: RESULTADOS',
    kind: 'opened', cited: true };
  append(a, uuid(91), backgroundAnswer);
  data.get(a).messages.at(-1).webResearch = { sources: [unplacedSource], citations: [] };
  firstJob.state = 'completed'; firstJob.resultMessageId = uuid(91); firstJob.updatedAt = new Date().toISOString();
  await sync(a);
  assert.equal(await title(), 'Beta', 'research cannot reclaim navigation');
  await select('Alfa'); await exact(backgroundAnswer).waitFor();
  const backgroundResult = page.locator('article.message-row--assistant').filter({ hasText: backgroundAnswer });
  const backgroundSource = backgroundResult.getByRole('button', { name: 'Fontes da pesquisa (1 link)' });
  await backgroundSource.waitFor();
  assert.equal(await backgroundResult.getByText('Fontes citadas', { exact: true }).count(), 0);
  assert.equal(await backgroundResult.getByRole('link', { name: unplacedSource.title }).count(), 0);
  await backgroundSource.click();
  assert.equal(await backgroundSource.getAttribute('aria-expanded'), 'true');
  assert.equal(await sourceAction.getAttribute('aria-expanded'), 'false');
  const sourceWithoutOffsets = backgroundResult.getByRole('link', { name: unplacedSource.title });
  assert.equal(await sourceWithoutOffsets.count(), 1);
  assert.equal(await sourceWithoutOffsets.getAttribute('href'), unplacedSource.url);
  await sourceWithoutOffsets.click();
  await page.waitForFunction(url => window.fixtureCommands.some(command =>
    command.command === 'plugin:opener|open_url' && command.url === url), unplacedSource.url);
  await backgroundSource.click();
  const delegatedVoiceTurn = uuid(95), delegatedVoiceUser = uuid(96), delegatedVoiceAssistant = uuid(97);
  const delegatedVoiceIntent = 'Vou comparar os horários das fontes e salvar o resultado aqui.';
  append(a, delegatedVoiceUser, 'Compare os horários das fontes que você encontrou.', 'user');
  await accepted(delegatedVoiceTurn, a);
  append(a, delegatedVoiceAssistant, delegatedVoiceIntent);
  enqueueResearch(delegatedVoiceTurn, a, delegatedVoiceUser);
  await emit(stream, segment(delegatedVoiceTurn, a, delegatedVoiceAssistant, 0, delegatedVoiceIntent));
  await emit(stream, event('done', delegatedVoiceTurn, a, delegatedVoiceAssistant));
  await exact(delegatedVoiceIntent).waitFor();
  await researchActivity('Pesquisa em segundo plano').waitFor();
  await page.locator('.research-job__cancel').click();
  await exact('Pesquisa cancelada.').waitFor();
  assert.equal([...researchJobs.values()].at(-1).state, 'cancelled');

  const pythonTurn = uuid(301), pythonUser = uuid(302), pythonAssistant = uuid(303), pythonResult = uuid(304);
  const pythonIntent = 'Vou simular as partidas e reunir as probabilidades.';
  const pythonReply = 'A simulação estimou 38% de chance de vitória.';
  append(a, pythonUser, 'Simule dez mil partidas.', 'user');
  await accepted(pythonTurn, a);
  await emit(stream, event('tool_status', pythonTurn, a, null, {
    toolStatus: { category: 'python', state: 'started', message: 'Executando Python…' } }));
  await exact('Executando Python…').waitFor();
  append(a, pythonAssistant, pythonIntent);
  const scriptJob = enqueueResearch(pythonTurn, a, pythonUser);
  Object.assign(scriptJob, { kind: 'python', phase: 'executing' });
  await emit(stream, segment(pythonTurn, a, pythonAssistant, 0, pythonIntent));
  await emit(stream, event('done', pythonTurn, a, pythonAssistant));
  await exact('Executando Python em segundo plano').waitFor();
  await page.getByRole('button', { name: 'Cancelar execução em Python' }).waitFor();
  assert.equal(await researchActivity('Pesquisa em segundo plano').count(), 0);
  append(a, pythonResult, pythonReply);
  Object.assign(scriptJob, { state: 'completed', phase: 'finished', resultMessageId: pythonResult,
    updatedAt: new Date().toISOString() });
  await sync(a);
  await exact(pythonReply).waitFor();
  await exact('Executando Python em segundo plano').waitFor({ state: 'hidden' });

  // Two committed segments arrive in a single IPC burst. Model completion is
  // independent of the speaking state, and must not snap the pending text.
  const turn = uuid(20), mid = uuid(21), marker1 = 'Fluxo nativo';
  const first = `${marker1} com caracteres, não blocos. ${'Primeiro trecho da explicação com texto progressivo. '.repeat(8).trim()}`;
  const second = `Segundo trecho, igualmente fluido. ${'O conteúdo precisa chegar aos poucos mesmo quando o transporte agrupa palavras. '.repeat(7).trim()}`;
  const full = `${first} ${second}`;
  append(a, uuid(22), 'Pergunta falada que iniciou a resposta.', 'user');
  await accepted(turn, a); await speaking();
  await page.locator('.desktop-voice-indicator[data-phase="speaking"]').waitFor();
  await page.evaluate(({ name, events }) => { for (const payload of events) window.fixtureNativeEmit(name, payload); }, { name: stream, events: [
    segment(turn, a, mid, 0, first), segment(turn, a, mid, 0, first), segment(turn, a, mid, 1, second), event('done', turn, a, mid),
    segment(turn, a, mid, 0, first), segment(turn, a, mid, 3, 'TEXTO INDEVIDO FORA DE ORDEM')
  ] });
  await waitPartial(marker1, full);
  assertProgress(await frameSamples(marker1), full, 'done before audio end');
  assert.equal(await page.locator('.desktop-voice-indicator[data-phase="speaking"]').count(), 1);

  // The authoritative complete snapshot arrives while the view is still
  // revealing. It may reconcile the same ID, but never replace the visible
  // prefix with the complete response or create a second assistant row.
  append(a, mid, full);
  const priorReads = reads.filter(read => read.id === a && read.last === mid).length;
  const before = await currentText(marker1);
  await sync(a);
  for (let i = 0; i < 100 && reads.filter(read => read.id === a && read.last === mid).length === priorReads; i++) await new Promise(resolve => setTimeout(resolve, 10));
  assert.ok(reads.filter(read => read.id === a && read.last === mid).length > priorReads, 'GET returned committed full snapshot during reveal');
  const after = await frameSamples(marker1, 160);
  assertProgress(after, full, 'authoritative snapshot mid-reveal');
  assert.ok(after[0].length >= before.length, 'snapshot cannot reset the reveal');
  await exact(full).waitFor({ timeout: 5000 });
  assert.equal(await marker(marker1).count(), 1, 'same persisted assistant ID appears exactly once');
  assert.equal(await page.getByText('TEXTO INDEVIDO FORA DE ORDEM').count(), 0);
  assert.equal(await page.locator('.desktop-voice-indicator[data-phase="speaking"]').count(), 1, 'text completion does not end audio');
  await idle(); await page.locator('.desktop-voice-indicator').waitFor({ state: 'detached' });

  // Interrupted generation only retains accepted/persisted segments; late
  // events for a terminal turn and an ignored activation must never create UI.
  const cutTurn = uuid(30), cutId = uuid(31), cutMarker = 'Trecho interrompido';
  const cutText = `${cutMarker}: ${'Só este conteúdo foi confirmado. '.repeat(12).trim()}`;
  append(a, uuid(32), 'Pergunta que será interrompida.', 'user');
  await accepted(cutTurn, a);
  await emit(stream, segment(cutTurn, a, cutId, 0, cutText));
  await waitPartial(cutMarker, cutText);
  append(a, cutId, cutText);
  // A persisted response can arrive through sync after the local turn was cut.
  data.get(a).messages.at(-1).webResearch = { sources: [unplacedSource], citations: [] };
  await emit(stream, event('interrupted', cutTurn, a, cutId));
  await emit(stream, segment(cutTurn, a, cutId, 1, 'NÃO DEVE APARECER DEPOIS DO CORTE'));
  await sync(a); await exact(cutText).waitFor({ timeout: 5000 });
  assert.equal(await marker(cutMarker).count(), 1);
  assert.equal(await page.getByText('NÃO DEVE APARECER DEPOIS DO CORTE').count(), 0);
  const cutResult = page.locator('article.message-row--assistant').filter({ hasText: cutMarker });
  const cutSources = cutResult.getByRole('button', { name: 'Fontes da pesquisa (1 link)' });
  await cutSources.waitFor();
  assert.equal(await cutResult.locator('.assistant-activity--interrupted').count(), 1);
  assert.equal(await cutResult.locator('.message-actions button').count(), 1);
  assert.equal(await cutResult.getByText('Fontes citadas', { exact: true }).count(), 0);
  await cutSources.click();
  assert.equal(await cutResult.getByRole('link', { name: unplacedSource.title }).getAttribute('href'), unplacedSource.url);
  const priorRows = await assistant.count();
  await emit(stream, event('ignored', uuid(40)));
  await emit(stream, segment(uuid(40), a, uuid(41), 0, 'ATIVAÇÃO INDEVIDA NÃO CONFIRMADA'));
  await page.waitForTimeout(200);
  assert.equal(await assistant.count(), priorRows, 'ignored activation creates no assistant message');
  assert.equal(await page.getByText('ATIVAÇÃO INDEVIDA NÃO CONFIRMADA').count(), 0);

  // Navigate during a different accepted native turn. Subsequent segment,
  // terminal/snapshot and stale events cannot reclaim focus or corrupt Beta.
  const raceTurn = uuid(50), raceId = uuid(51), raceMarker = 'Resposta de Alfa';
  const race1 = `${raceMarker}. ${'Primeira parte da conversa original. '.repeat(8).trim()}`;
  const race2 = 'Segunda parte, recebida depois da navegação.';
  append(a, uuid(52), 'Nova pergunta falada em Alfa.', 'user');
  await accepted(raceTurn, a); await emit(stream, segment(raceTurn, a, raceId, 0, race1));
  await waitPartial(raceMarker, `${race1} ${race2}`);
  await select('Beta'); await exact('Histórico Beta.').waitFor();
  await page.locator('.composer textarea').fill('Rascunho Beta preservado');
  append(a, raceId, `${race1} ${race2}`);
  await emit(stream, segment(raceTurn, a, raceId, 1, race2)); await emit(stream, event('done', raceTurn, a, raceId)); await sync(a);
  await page.waitForTimeout(350);
  assert.equal((await title()).trim(), 'Beta');
  assert.equal(await page.locator('.composer textarea').inputValue(), 'Rascunho Beta preservado');
  assert.equal(await marker(raceMarker).count(), 0);
  await select('Alfa'); await exact(`${race1} ${race2}`).waitFor({ timeout: 5000 });
  assert.equal(await marker(raceMarker).count(), 1);

  // The same scheduler is required for ordinary typing. This catches Vue
  // coalescing all tiny tokens from one NDJSON read into a single render.
  const beforeTypedRequests = typedRequests;
  await page.locator('.composer textarea').fill('Teste digitado em lote único');
  await page.locator('.composer textarea').press('Enter');
  await waitPartial('Fluxo digitado', typedText);
  assertProgress(await frameSamples('Fluxo digitado'), typedText, 'manual per-character NDJSON burst');
  await exact(typedText).waitFor({ timeout: 5000 });
  assert.equal(typedRequests, beforeTypedRequests + 1, 'presentation must not resend the manual request');
  assert.equal(await marker('Fluxo digitado').count(), 1, 'typed snapshot does not duplicate assistant message');

  // Reduced-motion preference bypasses the artificial presentation queue.
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const reducedTurn = uuid(60), reducedId = uuid(61), reducedText = `Sem movimento. ${'Texto já recebido e revelado imediatamente. '.repeat(18).trim()}`;
  append(a, uuid(62), 'Pergunta com movimento reduzido.', 'user');
  await accepted(reducedTurn, a);
  await emit(stream, segment(reducedTurn, a, reducedId, 0, reducedText));
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  assert.equal(await currentText('Sem movimento'), reducedText);
  assert.equal(await marker('Sem movimento').evaluate(node => node.getAnimations({ subtree: true }).length), 0);
  append(a, reducedId, reducedText); await emit(stream, event('done', reducedTurn, a, reducedId)); await sync(a);
  await exact(reducedText).waitFor();
  assert.deepEqual(errors, []);
  const cancelledAudio = await page.evaluate(() => window.fixtureCommands.filter(row => row.operation === 'cancel').length);
  assert.equal(cancelledAudio, 0, 'presentation never cancels playback or the native session');
  console.log('Desktop voice stream PASS: burst/terminal/snapshot frame progression, deduplication, interrupted/ignored, navigation isolation, reduced motion (IPC fixture).');
} finally {
  await browser?.close(); await vite.close();
}
