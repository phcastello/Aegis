// Real Chromium, Vue and HTTP streaming; private in-memory API and simulated
// native IPC. These checks do not attest physical Android/Windows behavior.
import assert from 'node:assert/strict';
import http from 'node:http';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { mkdirSync, readFileSync } from 'node:fs';
import { chromium } from 'playwright-core';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const requireNative = createRequire(join(root, 'apps/aegis-node/package.json'));
const { createServer } = await import(requireNative.resolve('vite'));
const version = JSON.parse(readFileSync(join(root, 'apps/aegis-node/package.json'), 'utf8')).version;
const a = '10000000-0000-4000-8000-000000000001';
const b = '20000000-0000-4000-8000-000000000002';
const created = '30000000-0000-4000-8000-000000000003';
const date = '2026-10-01T12:00:00Z';
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(condition, label) {
  for (let attempt = 0; attempt < 150; attempt++) { if (condition()) return; await delay(20); }
  throw Error(`Timed out: ${label}`);
}
function createFixture(viteOrigin) {
  const initial = id => ({ id: `${id}-history`, conversationId: id, role: 'assistant', createdAt: date,
    content: `Histórico ${id === a ? 'Alfa' : 'Beta'}.\n\n` + Array.from({ length: 12 }, (_, i) => `Parágrafo ${i + 1}. Texto anterior para ler durante a geração.\n\n- Primeiro item\n- Segundo item\n\n\`\`\`js\nconst item${i} = ${i};\n\`\`\``).join('\n\n') });
  const data = new Map([a, b].map(id => [id, { id, title: id === a ? 'Alfa' : 'Beta', createdAt: date, updatedAt: date, messages: [initial(id)], read: 0 }]));
  const requests = [], cancellations = [], completions = [], deletions = [], speech = [];
  let current, failNext = false, heldConversation = null, holdConversationId = null;
  const summary = row => ({ id: row.id, title: row.title, createdAt: row.createdAt, updatedAt: row.updatedAt, messageCount: row.messages.length,
    hasUnreadProactive: row.messages.some(m => (m.proactiveSequence ?? 0) > row.read) });
  const json = (res, body, code = 200) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
  const server = http.createServer(async (req, res) => {
    res.setHeader('Access-Control-Allow-Origin', req.headers.origin ?? '*');
    res.setHeader('Access-Control-Allow-Headers', 'Content-Type,Accept');
    res.setHeader('Access-Control-Allow-Methods', 'GET,POST,PATCH,DELETE,OPTIONS');
    if (req.method === 'OPTIONS') return json(res, null, 204);
    const url = new URL(req.url, 'http://localhost'), path = url.pathname;
    if (!path.startsWith('/api/')) {
      const response = await fetch(viteOrigin + req.url), headers = Object.fromEntries(response.headers);
      for (const name of ['content-encoding', 'content-length', 'transfer-encoding']) delete headers[name];
      res.writeHead(response.status, headers); res.end(Buffer.from(await response.arrayBuffer())); return;
    }
    let raw = ''; for await (const part of req) raw += part;
    const body = raw ? JSON.parse(raw) : {};
    if (path === '/api/health') return json(res, { status: 'ok' });
    if (path === '/api/voice/status') return json(res, { enabled: false, available: false });
    if (path === '/api/voice/speech') { speech.push(body); return json(res, { error: 'Unexpected automatic speech.' }, 503); }
    if (path === '/api/voice/transcription/status') return json(res, { enabled: false, configured: false });
    if (path === '/api/email/status') return json(res, { isConnected: false });
    if (path === '/api/notifications/configuration') return json(res, { enabled: false });
    if (path.endsWith('/memory-activity')) return json(res, { pending: false, sections: [] });
    if (path === '/api/chat/proactive-activity') return json(res, [...data.values()].map(row => ({ conversationId: row.id,
      lastProactiveSequence: Math.max(0, ...row.messages.map(m => m.proactiveSequence ?? 0)), readProactiveSequence: row.read })));
    if (path === '/api/chat/conversations') return json(res, { items: [...data.values()].map(summary), hasMore: false });
    const turnPath = /^\/api\/chat\/turns\/([^/]+)(\/complete)?$/.exec(path);
    if (turnPath) {
      (turnPath[2] ? completions : cancellations).push(turnPath[1]);
      if (req.method === 'DELETE' && current?.request.turnId === turnPath[1]) current.res.end();
      return json(res, null, 204);
    }
    const conversationPath = /^\/api\/chat\/conversations\/([^/]+)(?:\/(proactive-messages|proactive-read))?$/.exec(path);
    if (conversationPath) {
      const row = data.get(conversationPath[1]);
      if (req.method === 'DELETE') { data.delete(conversationPath[1]); deletions.push(conversationPath[1]); return json(res, null, 204); }
      if (!row) return json(res, { error: 'Conversa não encontrada.' }, 404);
      if (conversationPath[2] === 'proactive-messages') return json(res, row.messages.filter(m => (m.proactiveSequence ?? 0) > Number(url.searchParams.get('after'))));
      if (conversationPath[2] === 'proactive-read') { row.read = Math.max(row.read, body.sequence); return json(res, null, 204); }
      const snapshot = structuredClone({ ...summary(row), messages: row.messages });
      if (holdConversationId === row.id) {
        holdConversationId = null;
        let gate;
        await new Promise(resolve => { heldConversation = gate = { id: row.id, release: resolve, failure: false }; });
        if (gate.failure) return json(res, { error: 'Falha atrasada no carregamento do histórico.' }, 503);
      }
      return json(res, snapshot);
    }
    if (path === '/api/chat/messages/stream') {
      requests.push(body);
      if (failNext) { failNext = false; return json(res, { error: 'Falha de envio antes da aceitação.' }, 503); }
      res.writeHead(200, { 'Content-Type': 'application/x-ndjson' }); res.flushHeaders();
      const id = body.conversationId ?? created;
      current = { request: body, id, res, content: '', accepted: false,
        event(event) { if (!res.destroyed && !res.writableEnded) res.write(JSON.stringify({ turnId: body.turnId, ...event }) + '\n'); },
        accept() {
          if (!data.has(id)) data.set(id, { id, title: 'Nova conversa', createdAt: date, updatedAt: date, messages: [], read: 0 });
          data.get(id).messages.push({ id: `${body.turnId}-user`, conversationId: id, role: 'user', content: body.content, createdAt: date });
          this.accepted = true; this.event({ type: 'conversation', conversationId: id });
        },
        token(content) { this.content += content; this.event({ type: 'token', content }); },
        done(title = data.get(id)?.title) {
          const row = data.get(id);
          if (row) {
            row.title = title; row.updatedAt = new Date().toISOString();
            row.messages.push({ id: `${body.turnId}-assistant`, conversationId: id, role: 'assistant', content: this.content, createdAt: row.updatedAt });
          }
          this.event({ type: 'done', conversationId: id, messageId: `${body.turnId}-assistant`, conversationTitle: title }); res.end();
        },
        fail() { this.event({ type: 'error', message: 'Falha de rede na conversa de origem.' }); res.end(); }
      };
      return;
    }
    return json(res, { error: `Unknown fixture path: ${path}` }, 404);
  });
  return { server, data, requests, cancellations, completions, deletions, speech,
    holdConversation(id) { holdConversationId = id; },
    get heldConversation() { return heldConversation; },
    releaseConversation(failure = false) {
      if (heldConversation) { heldConversation.failure = failure; heldConversation.release(); heldConversation = null; }
    },
    get current() { return current; }, get failNext() { return failNext; }, set failNext(value) { failNext = value; } };
}

let assertions = 0;
const equal = (actual, expected, message) => { assert.equal(actual, expected, message); assertions++; };
const ok = (value, message) => { assert.ok(value, message); assertions++; };
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
const errors = [];
try {
  for (const platform of ['browser', 'android', 'windows']) {
    const host = platform === 'browser' ? 'web' : 'native';
    const vite = await createServer({ root: join(root, host === 'web' ? 'frontend/aegis-pwa' : 'apps/aegis-node'),
      server: { host: '127.0.0.1', port: 0, strictPort: false } });
    await vite.listen();
    const fixture = createFixture(`http://127.0.0.1:${vite.httpServer.address().port}`);
    await new Promise(resolve => fixture.server.listen(0, '127.0.0.1', resolve));
    const origin = `http://127.0.0.1:${fixture.server.address().port}`;
    const width = platform === 'android' ? 390 : platform === 'windows' ? 820 : 1440;
    const context = await browser.newContext({ viewport: { width, height: 900 }, hasTouch: platform === 'android', isMobile: platform === 'android' });
    await context.addInitScript(({ version, platform, origin }) => {
      localStorage.setItem('aegis.voice.autoSpeak', 'false');
      window.fixtureClipboard = [];
      Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async text => {
        if (window.fixtureCopyFailure) throw Error('Clipboard denied'); window.fixtureClipboard.push(text);
      } } });
      if (platform !== 'browser') window.__TAURI_INTERNALS__ = { invoke: async command => {
        if (command === 'runtime_info') return { platform, backendUrl: origin, version, processSessionId: 'ux-fixture' };
        if (command === 'node_read_visibility') return true;
        if (command === 'node_status') return { state: 'unpaired', node: null, error: null };
        if (command === 'node_transport_status') return { transportState: 'online' };
        if (command === 'node_action_state') return { pendingActions: 0 };
        if (command === 'node_notification_settings') return { granted: false, pushConfigured: false };
        if (command === 'node_consume_reminder_open') return { reminderId: null, conversationId: null };
        if (command === 'check_android_update' || command === 'plugin:updater|check') return null;
        if (command === 'check_backend') return { status: 'connected' };
        return;
      } };
    }, { version, platform, origin });
    const page = await context.newPage(); page.setDefaultTimeout(10000);
    page.on('pageerror', error => errors.push(`${platform}: ${error.message}`));
    const composer = () => page.locator('.composer textarea');
    const header = () => page.locator('.chat-header h1');
    const menu = () => page.getByRole('button', { name: 'Menu da conversa', exact: true });
    const history = async () => { if (!await page.locator('#conversation-sidebar').evaluate(el => !el.inert)) await page.getByRole('button', { name: 'Abrir histórico', exact: true }).click(); };
    const select = async title => {
      await history(); await page.locator('.history-item__main').filter({ hasText: title }).click();
      await page.waitForFunction(title => document.querySelector('.chat-header h1')?.textContent === title, title);
      await page.waitForFunction(() => !document.querySelector('.messages .empty-state')?.textContent.includes('Carregando'));
    };
    const fresh = async () => { await history(); await page.getByRole('button', { name: 'Nova conversa', exact: true }).click(); await page.waitForFunction(() => document.querySelector('.chat-header h1')?.textContent === 'Nova conversa'); };
    const send = async text => {
      const count = fixture.requests.length;
      await composer().fill(text); await page.getByRole('button', { name: 'Enviar mensagem', exact: true }).click();
      await until(() => fixture.requests.length > count, 'stream request'); return fixture.current;
    };
    const settled = async () => { await page.getByRole('button', { name: 'Enviar mensagem', exact: true }).waitFor(); await delay(80); };
    const scroll = () => page.locator('.messages').evaluate(el => ({ top: el.scrollTop, height: el.scrollHeight, visible: el.clientHeight, width: el.clientWidth,
      pageOverflow: document.documentElement.scrollHeight - document.documentElement.clientHeight, overflowY: getComputedStyle(el).overflowY, gutter: getComputedStyle(el).scrollbarGutter }));
    const userScroll = async (end, amount = 700) => {
      await page.locator('.messages').evaluate((el, { end, amount }) => {
        el.dispatchEvent(new WheelEvent('wheel', { deltaY: end ? 100 : -100, bubbles: true }));
        el.scrollTop = end ? el.scrollHeight : Math.max(0, el.scrollTop - amount);
      }, { end, amount }); await delay(80);
    };
    try {
      await page.goto(origin); await composer().waitFor(); await page.locator('.history-item__main').first().waitFor();
      equal(await menu().count(), 0, 'initial new conversation hides the action menu');
      if (platform !== 'browser') equal(await composer().evaluate(el => el === document.activeElement), false, 'native initialization never requests composer focus');
      await fresh();
      await page.waitForFunction(() => document.querySelector('.composer textarea') === document.activeElement);
      equal(await menu().count(), 0, 'explicit new conversation still has no persisted menu');
      await composer().fill('Rascunho novo original'); await select('Beta'); await composer().fill('Rascunho Beta');
      await fresh(); equal(await composer().inputValue(), 'Rascunho novo original', 'new draft survives a conversation round trip');
      const first = await send('Rascunho novo original');
      equal(await composer().inputValue(), '', 'local acceptance consumes the submitted draft');
      equal(await menu().count(), 0, 'pending local messages do not expose the menu');
      await select('Beta');
      equal(await composer().inputValue(), 'Rascunho Beta', 'navigation restores only the target draft');
      const pendingRequests = fixture.requests.length;
      await composer().press('Enter'); await delay(80);
      equal(fixture.requests.length, pendingRequests, 'another conversation cannot submit an ambiguous concurrent turn');
      equal(await page.getByRole('button', { name: 'Enviar mensagem', exact: true }).isDisabled(), true, 'second-turn submission is visibly disabled');
      await composer().fill('Rascunho Beta');
      equal(fixture.cancellations.length, 0, 'navigation before conversation persistence never cancels');
      first.accept(); first.token('Resposta criada em segundo plano. '); await delay(120);
      equal((await header().textContent()).trim(), 'Beta', 'late persistence and tokens do not replace target identity');
      equal(await page.locator('.messages').getByText('Resposta criada em segundo plano.', { exact: false }).count(), 0, 'late tokens remain with the origin');
      equal(await composer().inputValue(), 'Rascunho Beta', 'background callbacks preserve the target draft');
      await history();
      await page.locator('.history-item__main').filter({ hasText: 'Nova conversa' }).waitFor();
      await page.locator('.history-item__main').filter({ hasText: 'Nova conversa' }).click();
      await page.locator('.messages').getByText('Resposta criada em segundo plano.', { exact: false }).waitFor();
      equal(await menu().count(), 1, 'persisted pending conversation has its action menu');
      await select('Beta');
      await page.evaluate(() => window.dispatchEvent(new StorageEvent('storage', { key: 'aegis.voice.autoSpeak', newValue: 'true' })));
      first.done('Criada pelo teste'); await settled();
      equal(fixture.speech.length, 0, 'background completion stays silent even with automatic TTS enabled');
      ok(fixture.completions.includes(first.request.turnId), 'background turn completes its lifecycle without speech');
      await page.evaluate(() => window.dispatchEvent(new StorageEvent('storage', { key: 'aegis.voice.autoSpeak', newValue: 'false' })));
      equal((await header().textContent()).trim(), 'Beta', 'background completion never navigates or overwrites the visible title');
      equal(await composer().inputValue(), 'Rascunho Beta', 'background completion preserves target draft');
      await select('Criada pelo teste');
      equal(await composer().inputValue(), '', 'sent new draft was not left under the persisted ID');
      equal(await page.locator('.messages').getByText('Rascunho novo original', { exact: true }).count(), 1, 'history restoration does not duplicate the accepted prompt');
      equal(await page.locator('.messages').getByText('Resposta criada em segundo plano.', { exact: true }).count(), 1, 'history restoration does not duplicate the response');
      const second = await send('Segunda mensagem'); second.accept(); second.token('Segunda resposta. '); second.done(); await settled();
      equal(await composer().inputValue(), '', 'second send does not resurrect the obsolete new draft');
      await select('Beta'); await select('Criada pelo teste');
      equal(await composer().inputValue(), '', 'round trip after two sends does not resurrect drafts');

      // The ID is complete in the DOM and clipboard, visually ellipsized only.
      await menu().click(); await page.getByRole('menuitem', { name: 'Informações', exact: true }).click();
      const info = page.getByRole('dialog', { name: 'Informações da conversa', exact: true });
      await info.waitFor();
      equal(await info.locator('.conversation-info__id-box .conversation-real-id').textContent(), created, 'ID stays readable and complete');
      equal(await info.locator('.conversation-info__id-box').getByRole('button', { name: 'Copiar ID', exact: true }).count(), 1, 'copy icon is integrated into the ID box');
      equal(await info.locator('input').count(), 0, 'ID remains read-only text');
      await info.getByRole('button', { name: 'Copiar ID', exact: true }).click();
      await info.getByRole('status').getByText('Copiado', { exact: true }).waitFor();
      equal(await page.evaluate(() => window.fixtureClipboard.at(-1)), created, 'clipboard receives the full ID');
      await page.evaluate(() => { window.fixtureCopyFailure = true; });
      await info.getByRole('button', { name: 'Copiar ID', exact: true }).click();
      await info.getByRole('status').getByText('Não foi possível copiar.', { exact: false }).waitFor();
      await page.keyboard.press('Escape'); await info.waitFor({ state: 'hidden' });
      await page.evaluate(() => { window.fixtureCopyFailure = false; });
      const geometry = await page.locator('.chat-header').evaluate(el => {
        const style = getComputedStyle(el), bounds = el.getBoundingClientRect(), menu = el.querySelector('.conversation-menu-trigger').getBoundingClientRect(), server = el.querySelector('.server-indicator').getBoundingClientRect();
        return { border: style.borderBottomWidth, fade: getComputedStyle(el, '::after').backgroundImage, right: bounds.right - parseFloat(style.paddingRight) - menu.right, serverBeforeMenu: server.right <= menu.left };
      });
      equal(geometry.border, '0px', 'header has no separator'); ok(geometry.fade.includes('linear-gradient'), 'header uses a discreet fade');
      ok(Math.abs(geometry.right) <= 2, 'menu sits at the useful right edge'); ok(geometry.serverBeforeMenu, 'availability indicator does not displace the right-aligned menu');

      await select('Alfa');
      const streaming = await send('Fluxo longo'); streaming.accept();
      streaming.token('Início da resposta longa.\n\n' + Array.from({ length: 22 }, (_, i) => `Parágrafo ${i + 1} do streaming com mais palavras.`).join('\n\n') + '\n\n');
      await page.locator('.messages').getByText('Início da resposta longa.', { exact: true }).waitFor(); await delay(120);
      let position = await scroll();
      ok(position.height - position.top - position.visible <= 4, 'active following keeps streaming end visible');
      equal(position.overflowY, 'scroll', 'message region keeps a stable scrollbar'); ok(position.gutter.includes('stable'), 'message width reserves the scrollbar gutter');
      ok(position.pageOverflow <= 1, 'the chat does not produce a second document scrollbar');
      await page.locator('.messages').evaluate(el => { el.scrollTop -= 250; }); await delay(80);
      const inputlessReading = await scroll();
      streaming.token('Tokens enquanto a rolagem assistiva está pausada.\n\n'); await delay(120);
      ok(Math.abs((await scroll()).top - inputlessReading.top) <= 2, 'scrollbar or assistive upward movement suspends following without wheel events');
      await page.locator('.messages').evaluate(el => { el.scrollTop = el.scrollHeight; }); await delay(80);
      streaming.token('Acompanhamento retomado pela rolagem assistiva.\n\n'); await delay(120);
      position = await scroll();
      ok(position.height - position.top - position.visible <= 4, 'scrollbar or assistive return to bottom resumes following after content growth');
      await userScroll(false, 15); const smallManual = await scroll();
      streaming.token('Tokens após rolagem curta intencional.\n\n'); await delay(120);
      position = await scroll(); ok(Math.abs(position.top - smallManual.top) <= 2, 'even upward input inside the near-end threshold suspends following');
      await userScroll(false); const manual = await scroll();
      streaming.token('Mais tokens enquanto o leitor consulta o histórico.\n\n```ts\nconst resposta = true;\n```\n\n- Um\n- Dois\n\n');
      await page.locator('.messages').getByText('Mais tokens enquanto o leitor consulta o histórico.', { exact: true }).waitFor(); await delay(120);
      position = await scroll(); ok(Math.abs(position.top - manual.top) <= 2, 'manual upward scrolling is preserved across tokens and Markdown reflow');
      equal(position.width, manual.width, 'streaming does not change content width');
      await page.setViewportSize({ width, height: 780 }); await delay(120); position = await scroll();
      ok(Math.abs(position.top - manual.top) <= 2, 'viewport/keyboard height changes preserve manual reading');
      await userScroll(true); streaming.token('Acompanhamento retomado ao voltar ao final.\n\n'); await delay(120); position = await scroll();
      ok(position.height - position.top - position.visible <= 4, 'returning to bottom resumes following');
      await userScroll(false); const beforeDone = await scroll(); streaming.done(); await settled();
      position = await scroll(); ok(Math.abs(position.top - beforeDone.top) <= 2, 'completion and loading removal do not jump while reading');
      await page.setViewportSize({ width, height: 900 });

      // A cached context is interactive while its history refresh is in flight.
      // Release an obsolete captured snapshot only after a new turn completes.
      await select('Beta'); fixture.holdConversation(a); await select('Alfa');
      await until(() => Boolean(fixture.heldConversation), 'held history snapshot');
      const duringRefresh = await send('Envio durante recarga de histórico');
      duringRefresh.accept(); duringRefresh.token('Resposta posterior ao snapshot antigo. '); duringRefresh.done(); await settled();
      fixture.releaseConversation(); await delay(150);
      equal(await page.locator('.messages').getByText('Envio durante recarga de histórico', { exact: true }).count(), 1, 'an old history snapshot cannot erase a newer local prompt');
      equal(await page.locator('.messages').getByText('Resposta posterior ao snapshot antigo.', { exact: true }).count(), 1, 'an old history snapshot cannot erase a newly completed response');

      await select('Beta'); fixture.holdConversation(a); await select('Alfa');
      await until(() => Boolean(fixture.heldConversation), 'held failing history request');
      const beforeStaleFailure = await send('Turno antes da falha atrasada do histórico');
      beforeStaleFailure.accept(); beforeStaleFailure.token('Resposta concluída antes da falha antiga. '); beforeStaleFailure.done(); await settled();
      fixture.releaseConversation(true); await delay(150);
      equal(await page.locator('.composer').getByText('Não foi possível abrir esta conversa.', { exact: true }).count(), 0, 'a stale GET failure cannot inject an error after a newer successful turn');
      equal(await page.locator('.messages').getByText('Resposta concluída antes da falha antiga.', { exact: true }).count(), 1, 'late history failure preserves the newer response');

      const background = await send('Erro no fundo'); background.accept(); background.token('Texto parcial da origem. ');
      await select('Beta'); await userScroll(false); const betaReading = await scroll();
      await composer().fill('Rascunho Beta durante resposta');
      // A selected unrelated conversation still receives its proactive activity.
      fixture.data.get(b).messages.push({ id: 'proactive-beta', conversationId: b, role: 'assistant', content: 'Aviso proativo Beta durante a geração.', createdAt: new Date().toISOString(), isProactive: true, proactiveSequence: 1 });
      await page.evaluate(() => window.dispatchEvent(new Event('focus')));
      await page.locator('.messages').getByText('Aviso proativo Beta durante a geração.', { exact: true }).waitFor();
      ok(Math.abs((await scroll()).top - betaReading.top) <= 2, 'incoming proactivity preserves manual reading in the selected conversation');
      background.fail(); await settled();
      equal((await header().textContent()).trim(), 'Beta', 'background error keeps the selected identity');
      equal(await page.locator('.composer').getByText('Falha de rede na conversa de origem.', { exact: true }).count(), 0, 'background error never appears in the wrong composer');
      equal(await composer().inputValue(), 'Rascunho Beta durante resposta', 'background error preserves the target draft');
      equal(fixture.cancellations.length, 0, 'returning and completing in the background never cancels');
      await select('Alfa');
      await page.locator('.composer').getByText('Falha de rede na conversa de origem.', { exact: true }).waitFor();
      equal(await composer().inputValue(), '', 'an accepted prompt is never restored for automatic duplication');
      fixture.failNext = true; await send('Texto recuperável antes da aceitação');
      const failedRequest = fixture.requests.at(-1);
      await page.locator('.composer').getByText('Falha de envio antes da aceitação.', { exact: true }).waitFor();
      equal(await composer().inputValue(), 'Texto recuperável antes da aceitação', 'pre-acceptance failure preserves the recoverable prompt');
      await select('Beta'); await select('Alfa');
      equal(await composer().inputValue(), 'Texto recuperável antes da aceitação', 'recoverable prompt remains associated with its conversation');
      const retried = await send('Texto recuperável antes da aceitação');
      equal(retried.request.turnId, failedRequest.turnId, 'manual retry preserves the existing idempotency key');
      retried.accept(); retried.token('Resposta da tentativa manual. '); retried.done(); await settled();
      equal(await composer().inputValue(), '', 'accepted retry consumes the recovered prompt');
      equal(await page.locator('.messages').getByText('Texto recuperável antes da aceitação', { exact: true }).count(), 1, 'manual retry adds one accepted prompt');
      await composer().fill('');

      const explicit = await send('Interrupção explícita'); explicit.accept(); explicit.token('Resposta a interromper. ');
      await select('Beta'); equal(fixture.cancellations.length, 0, 'navigation is not a stop request');
      await select('Alfa'); await page.getByRole('button', { name: 'Interromper geração', exact: true }).click(); await settled();
      await until(() => fixture.cancellations.length === 1, 'explicit cancellation');
      equal(fixture.cancellations[0], explicit.request.turnId, 'explicit stop addresses only the owning turn');
      await fresh(); const unaccepted = await send('Mensagem nova recuperável sem ID');
      await fresh(); await composer().fill('Outro rascunho de Nova conversa');
      await select('Beta'); unaccepted.fail(); await settled();
      equal(await composer().inputValue(), 'Rascunho Beta durante resposta', 'failure before persistence preserves the other conversation draft');
      await page.getByRole('button', { name: 'Recuperar mensagem', exact: true }).click();
      await page.waitForFunction(() => document.querySelector('.composer textarea')?.value === 'Mensagem nova recuperável sem ID');
      equal(await menu().count(), 0, 'recovery keeps an unpersisted conversation without an action menu');
      await fresh(); equal(await composer().inputValue(), 'Outro rascunho de Nova conversa', 'recovering a failed new turn preserves an independent new draft');
      const secondUnaccepted = await send('Outro rascunho de Nova conversa');
      await fresh(); await composer().fill('Terceiro rascunho ainda não enviado');
      await select('Beta'); secondUnaccepted.fail(); await settled();
      equal(await composer().inputValue(), 'Rascunho Beta durante resposta', 'two pre-acceptance failures preserve the persisted conversation draft');
      await page.getByRole('button', { name: 'Recuperar mensagem', exact: true }).click();
      await page.waitForFunction(() => document.querySelector('.composer textarea')?.value === 'Mensagem nova recuperável sem ID');
      equal(await composer().inputValue(), 'Mensagem nova recuperável sem ID', 'a second new failure cannot overwrite the first recoverable prompt');
      await page.getByRole('button', { name: 'Recuperar mensagem', exact: true }).click();
      await page.waitForFunction(() => document.querySelector('.composer textarea')?.value === 'Outro rascunho de Nova conversa');
      equal(await composer().inputValue(), 'Outro rascunho de Nova conversa', 'the second failed new prompt is independently recoverable');
      await fresh(); equal(await composer().inputValue(), 'Terceiro rascunho ainda não enviado', 'recovering both failed prompts preserves a third unsent new draft');
      await select('Alfa');
      const deleted = await send('Excluir turno ativo'); deleted.accept(); deleted.token('Resposta antes da exclusão. ');
      await select('Beta'); await history();
      await page.getByRole('button', { name: 'Ações de Alfa', exact: true }).click();
      await page.getByRole('menuitem', { name: 'Excluir', exact: true }).click();
      await page.getByRole('dialog', { name: 'Apagar conversa', exact: true }).getByRole('button', { name: 'Apagar', exact: true }).click();
      await until(() => fixture.deletions.includes(a), 'conversation deletion');
      await until(() => fixture.cancellations.includes(deleted.request.turnId), 'deletion cancellation');
      deleted.done('Título atrasado excluído'); await delay(200);
      equal((await header().textContent()).trim(), 'Beta', 'deleting the background owner does not change selection');
      equal(await page.locator('.history-item__main').filter({ hasText: 'Alfa' }).count(), 0, 'deleted origin stays removed');
      equal(await page.locator('.history-item__main').filter({ hasText: 'Título atrasado excluído' }).count(), 0, 'late completion cannot resurrect deleted origin');
      equal(await composer().inputValue(), 'Rascunho Beta durante resposta', 'deletion preserves the other conversation draft');
      // A saved conversation can disappear on another device before restoration.
      await page.evaluate(() => localStorage.setItem('aegis.currentConversationId', 'missing-conversation'));
      const restored = await context.newPage(); restored.on('pageerror', error => errors.push(`${platform}/restore: ${error.message}`));
      try {
        await restored.goto(origin);
        await restored.locator('.composer').getByText('Não foi possível carregar a conversa anterior.', { exact: true }).waitFor();
        equal((await restored.locator('.chat-header h1').textContent()).trim(), 'Nova conversa', 'missing saved ID restores a usable new conversation');
        equal(await restored.getByRole('button', { name: 'Menu da conversa', exact: true }).count(), 0, 'a missing saved conversation does not retain an invalid action menu');
        equal(await restored.evaluate(() => localStorage.getItem('aegis.currentConversationId')), null, 'missing saved ID is removed from persistent selection');
        await restored.locator('.composer textarea').fill('Mensagem após restauração inválida');
        equal(await restored.getByRole('button', { name: 'Enviar mensagem', exact: true }).isEnabled(), true, 'composer remains usable after missing-history restoration');
      } finally { await restored.close(); }
      if (width <= 900 && await page.locator('#conversation-sidebar').evaluate(el => !el.inert)) await page.getByRole('button', { name: 'Fechar histórico', exact: true }).click();
      if (process.env.UX_SCREENSHOT_DIR) {
        await page.locator('#conversation-sidebar').evaluate(el => Promise.all(el.getAnimations().map(animation => animation.finished.catch(() => undefined))));
        mkdirSync(process.env.UX_SCREENSHOT_DIR, { recursive: true });
        await page.screenshot({ path: `${process.env.UX_SCREENSHOT_DIR}/${platform}-chat.png` });
      }
      console.log(`PASS ${platform}: streaming ownership, late ID, navigation, completion/error/deletion, explicit stop, drafts, user scroll/resize, proactive incoming, header/menu/copy and explicit focus`);
    } finally {
      fixture.releaseConversation(); await context.close(); fixture.server.closeAllConnections(); await new Promise(resolve => fixture.server.close(resolve)); await vite.close();
    }
  }
  assert.deepEqual(errors, []); console.log(`PASS ${assertions} UX browser assertions; native IPC simulated, no physical device claim.`);
} finally { await browser.close(); }
