// Shared Vue UI with real Chromium layout/scrolling and an isolated HTTP fixture.
// Native visibility/IPC are simulated; this is not an Android WebView acceptance.
import assert from 'node:assert/strict';
import http from 'node:http';
import { readFileSync } from 'node:fs';
import { chromium } from 'playwright-core';

const version = JSON.parse(readFileSync(new URL('../package.json', import.meta.url))).version;
const first = '10000000-0000-4000-8000-000000000001';
const second = '20000000-0000-4000-8000-000000000002';
const data = new Map([first, second].map((id, index) => [id, {
  id, title: index ? 'Outra conversa' : 'Conversa proativa', read: 0, messages: []
}]));
const posts = [];
let nextActivitySnapshot, releaseActivity, holdActivity = false;
const summary = row => ({ id: row.id, title: row.title, createdAt: '2026-10-01T00:00:00Z', updatedAt: '2026-10-27T00:00:00Z', messageCount: row.messages.length,
  hasUnreadProactive: row.messages.length > row.read });
const json = (res, body, code = 200) => { res.writeHead(code, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
const server = http.createServer(async (req, res) => {
  res.setHeader('Access-Control-Allow-Origin', 'http://127.0.0.1:1420');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
  res.setHeader('Access-Control-Allow-Methods', 'GET,POST,OPTIONS');
  if (req.method === 'OPTIONS') return json(res, null, 204);
  const url = new URL(req.url, 'http://localhost');
  if (!url.pathname.startsWith('/api/')) {
    const response = await fetch('http://127.0.0.1:18092' + req.url);
    const headers = Object.fromEntries(response.headers);
    for (const name of ['content-encoding', 'content-length', 'transfer-encoding']) delete headers[name];
    res.writeHead(response.status, headers); res.end(Buffer.from(await response.arrayBuffer())); return;
  }
  let raw = ''; for await (const part of req) raw += part;
  const body = raw ? JSON.parse(raw) : {};
  if (url.pathname === '/api/health') return json(res, { status: 'ok' });
  if (url.pathname === '/api/voice/status') return json(res, { enabled: false, available: false });
  if (url.pathname === '/api/voice/transcription/status') return json(res, { enabled: false, configured: false });
  if (url.pathname === '/api/email/status') return json(res, { isConnected: true });
  if (url.pathname === '/api/notifications/configuration') return json(res, { enabled: false });
  if (url.pathname.endsWith('/memory-activity')) return json(res, { pending: false, sections: [] });
  if (url.pathname === '/api/chat/proactive-activity') {
    const snapshot = nextActivitySnapshot ?? [...data.values()].filter(row => row.messages.length).map(row => ({ conversationId: row.id, lastProactiveSequence: row.messages.length, readProactiveSequence: row.read }));
    nextActivitySnapshot = undefined;
    if (holdActivity) { holdActivity = false; await new Promise(resolve => { releaseActivity = resolve; }); }
    return json(res, snapshot);
  }
  if (url.pathname === '/api/chat/conversations') return json(res, { items: [...data.values()].map(summary), hasMore: false });
  const match = /^\/api\/chat\/conversations\/([^/]+)(?:\/(proactive-messages|proactive-read))?$/.exec(url.pathname);
  const row = data.get(match?.[1]);
  if (row && match[2] === 'proactive-read') {
    row.read = Math.max(row.read, body.sequence); posts.push({ id: row.id, sequence: body.sequence });
    return json(res, null, 204);
  }
  if (row && match[2] === 'proactive-messages') return json(res, row.messages.filter(m => m.proactiveSequence > Number(url.searchParams.get('after'))));
  if (row) return json(res, { ...summary(row), messages: [{ id: `${row.id}-start`, conversationId: row.id, role: 'assistant', content: 'Histórico de teste.', createdAt: '2026-10-01T00:00:00Z' }, ...row.messages] });
  return json(res, { error: 'Unknown fixture path' }, 404);
});
await new Promise(resolve => server.listen(18094, '127.0.0.1', resolve));
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
const errors = [];
let android, windows;
const pause = () => new Promise(resolve => setTimeout(resolve, 180));
const poll = async page => { await page.evaluate(() => window.dispatchEvent(new Event('focus'))); await pause(); };
const unread = page => page.locator('[aria-label="Mensagem da Aegis não lida"]');
const add = (number, long = false) => {
  const content = `Aviso proativo ${number}.` + (long ? '\n\n' + Array.from({length: 35}, (_, i) => `Parágrafo ${i + 1} do aviso ${number}, para verificar a posição real de leitura.`).join('\n\n') : ' Ative a localização para recuperar os lembretes.');
  data.get(first).messages.push({ id: `proactive-${number}`, conversationId: first, role: 'assistant', content, createdAt: `2026-10-27T12:${String(number).padStart(2, '0')}:00Z`, isProactive: true, proactiveSequence: number });
};
const end = page => page.locator('.messages').evaluate(element => { element.scrollTop = element.scrollHeight; });
const waitRead = async sequence => {
  for (let i = 0; i < 50 && data.get(first).read !== sequence; i++) await new Promise(resolve => setTimeout(resolve, 40));
  assert.equal(data.get(first).read, sequence);
};
try {
  android = await browser.newContext({ viewport: { width: 390, height: 844 } });
  await android.addInitScript(({ version, first }) => {
    localStorage.setItem('aegis.voice.autoSpeak', 'false');
    window.fixtureForeground = true;
    // Reproduces Android hideKeyboard.clearFocus while the Activity stays visible.
    Object.defineProperty(document, 'hasFocus', { configurable: true, value: () => false });
    window.__TAURI_INTERNALS__ = { invoke: async command => {
      if (command === 'runtime_info') return { platform: 'android', backendUrl: 'http://127.0.0.1:18094', version, processSessionId: 'read-fixture' };
      if (command === 'node_read_visibility') {
        if (window.fixtureDeferVisibility) { window.fixtureDeferVisibility = false; await new Promise(resolve => { window.fixtureReleaseVisibility = resolve; }); }
        return window.fixtureForeground;
      }
      if (command === 'node_status') return { state: 'unpaired', node: null, error: null };
      if (command === 'node_transport_status') return { transportState: 'online' };
      if (command === 'node_action_state') return { pendingActions: 0 };
      if (command === 'node_notification_settings') return { granted: false, pushConfigured: false };
      if (command === 'node_consume_reminder_open') return { reminderId: null, conversationId: null };
      if (command === 'check_android_update') return null;
      if (command === 'check_backend') return { status: 'connected' };
      return;
    } };
  }, { version, first });
  const phone = await android.newPage(); phone.on('pageerror', error => errors.push(error.message));
  await phone.goto('http://127.0.0.1:1420');
  await phone.getByLabel('Abrir histórico', { exact: true }).click();
  await phone.getByRole('button', { name: 'Outra conversa', exact: true }).click();

  // One message in an unselected conversation: history is not a read receipt.
  add(1); await poll(phone);
  await phone.getByLabel('Abrir histórico', { exact: true }).click();
  await unread(phone).waitFor(); assert.equal(data.get(first).read, 0);
  // A second client represents the Windows/UI consumer of the shared server marker.
  windows = await browser.newContext({ viewport: { width: 1500, height: 900 } });
  const desktop = await windows.newPage(); desktop.on('pageerror', error => errors.push(error.message));
  await desktop.goto('http://127.0.0.1:18094'); await unread(desktop).waitFor();
  await phone.getByRole('button', { name: /^Conversa proativa/ }).click();
  await phone.locator('[data-proactive-sequence="1"]').waitFor(); await end(phone); await waitRead(1);
  assert.equal(await phone.evaluate(() => document.hasFocus()), false, 'Activity foreground is independent from WebView input focus');
  await phone.getByLabel('Abrir histórico', { exact: true }).click(); assert.equal(await unread(phone).count(), 0);
  await poll(desktop); await unread(desktop).waitFor({ state: 'hidden' });
  assert.equal(await desktop.locator('[data-proactive-sequence]').count(), 0, 'Windows sync clears the badge without opening the conversation');
  await phone.getByLabel('Fechar histórico', { exact: true }).click();

  // New, long content below the current scroll position is not read early.
  await phone.locator('.messages').evaluate(element => { element.scrollTop = 0; });
  add(2, true); await poll(phone); await phone.locator('[data-proactive-sequence="2"]').waitFor();
  assert.equal(data.get(first).read, 1);
  await phone.locator('[data-proactive-sequence="2"]').evaluate(element => {
    const root = element.closest('.messages'); root.scrollTop += element.getBoundingClientRect().bottom - root.getBoundingClientRect().bottom - 5;
  });
  await pause(); assert.equal(data.get(first).read, 1, 'The message end is still clipped by five pixels');

  // A snapshot with an older last sequence cannot hide a known newer message.
  nextActivitySnapshot = [{ conversationId: first, lastProactiveSequence: 1, readProactiveSequence: 0 }];
  await poll(phone); await phone.getByLabel('Abrir histórico', { exact: true }).click();
  assert.equal(await unread(phone).count(), 1, 'Known latest/read sequences are monotonic across stale activity');
  await phone.getByLabel('Fechar histórico', { exact: true }).click();

  // A stale activity response cannot undo a newer acknowledgement.
  holdActivity = true; await phone.evaluate(() => window.dispatchEvent(new Event('focus')));
  for (let i = 0; !releaseActivity && i < 30; i++) await pause();
  assert.ok(releaseActivity); await end(phone); await waitRead(2);
  releaseActivity(); releaseActivity = undefined; await pause();
  await phone.getByLabel('Abrir histórico', { exact: true }).click(); assert.equal(await unread(phone).count(), 0);

  // Multiple messages stay unread behind the actual mobile history drawer.
  add(3); add(4, true); await poll(phone); await end(phone); await pause();
  assert.equal(data.get(first).read, 2);
  // Closing a drawer must still respect native background/window obstruction.
  await phone.evaluate(() => { window.fixtureForeground = false; window.dispatchEvent(new Event('aegis-app-visibility')); });
  await phone.getByLabel('Fechar histórico', { exact: true }).click(); await end(phone); await pause();
  assert.equal(data.get(first).read, 2, 'A visible document cannot override a background Activity');
  // Return to foreground with only message 3 fully seen, message 4 still below.
  await phone.locator('[data-proactive-sequence="3"]').evaluate(element => {
    const root = element.closest('.messages'); root.scrollTop += element.getBoundingClientRect().bottom - root.getBoundingClientRect().bottom + 12;
  });
  await phone.evaluate(() => { window.fixtureForeground = true; window.dispatchEvent(new Event('aegis-app-visibility')); });
  await waitRead(3); assert.equal(data.get(first).read, 3);

  // An overlaid surface also blocks geometric visibility.
  await phone.evaluate(() => {
    const overlay = document.createElement('div'); overlay.id = 'read-cover';
    Object.assign(overlay.style, { position: 'fixed', inset: '0', zIndex: '99999', background: '#222' }); document.body.append(overlay);
  });
  await end(phone); await pause(); assert.equal(data.get(first).read, 3);
  await phone.evaluate(() => { document.getElementById('read-cover').remove(); window.dispatchEvent(new Event('aegis-chat-visible')); });
  await waitRead(4);

  // Offline acknowledgement is retried through the existing online/foreground hook.
  add(5); await poll(phone); await phone.locator('[data-proactive-sequence="5"]').waitFor();
  await android.setOffline(true); await end(phone); await pause(); assert.equal(data.get(first).read, 4);
  await android.setOffline(false); await phone.evaluate(() => window.dispatchEvent(new Event('online'))); await waitRead(5);

  // A delayed native visibility reply from the previous selection cannot read it.
  await phone.locator('.messages').evaluate(element => { element.scrollTop = 0; });
  add(6); await poll(phone); await phone.locator('[data-proactive-sequence="6"]').waitFor();
  await phone.evaluate(() => { window.fixtureDeferVisibility = true; }); await end(phone);
  await phone.waitForFunction(() => Boolean(window.fixtureReleaseVisibility));
  await phone.getByLabel('Abrir histórico', { exact: true }).click();
  await phone.getByRole('button', { name: 'Outra conversa', exact: true }).click();
  await phone.evaluate(() => window.fixtureReleaseVisibility()); await pause();
  assert.equal(data.get(first).read, 5, 'Navigation invalidates an outstanding read candidate');
  // Notification deep link selects the actual conversation, then its visible end is acknowledged.
  await phone.evaluate(id => window.dispatchEvent(new CustomEvent('aegis-conversation-open', { detail: { conversationId: id } })), first);
  await phone.locator('[data-proactive-sequence="6"]').waitFor(); await end(phone); await waitRead(6);
  await poll(desktop); await unread(desktop).waitFor({ state: 'hidden' });
  assert.deepEqual(posts.map(post => post.sequence), [1, 2, 3, 4, 5, 6], 'Each visible sequence is acknowledged once; no stale or regressing writes');
  assert.deepEqual(errors, []);
  console.log('PASS proactive reading: two shared Vue clients, actual layout/scroll, history, clipped end, tall message, overlay, background/resume, offline retry, stale activity, rapid navigation and notification deep link (native IPC simulated).');
} finally {
  releaseActivity?.(); await android?.close(); await windows?.close(); await browser.close(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
}
