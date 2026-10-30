// Real ChatView/Vue/CSS in Chromium with simulated HTTP responses.
// No backend, model calls or physical-device claims.
import assert from 'node:assert/strict';
import { mkdirSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const requireNative = createRequire(join(root, 'apps/aegis-node/package.json'));
const { createServer } = await import(requireNative.resolve('vite'));
const file = path => '/@fs/' + join(root, path);
const entry = `
import { createApp, h, ref } from 'vue';
import Chat from '${file('packages/aegis-client/src/views/ChatView.vue')}';
import '${file('packages/aegis-client/src/styles.css')}';
const view = ref();
const app = createApp({render:()=>h(Chat,{ref:view})});
app.mount('#app');
window.fixture = {openConversation:id=>view.value.openConversation(id),unmount:()=>app.unmount()};
`;
const vite = await createServer({ root: join(root, 'apps/aegis-node'), server: { host: '127.0.0.1', port: 0, strictPort: false, fs: { allow: [root] } }, plugins: [{
  name: 'server-indicator-fixture',
  resolveId(id) { if (id === '/__indicator-entry') return '\0server-indicator'; },
  load(id) { if (id === '\0server-indicator') return entry; },
  configureServer(server) { server.middlewares.use((req, res, next) => {
    if (req.url !== '/__indicator') return next();
    res.setHeader('Content-Type', 'text/html');
    res.end('<!doctype html><html><head><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="app"></div><script type="module" src="/__indicator-entry"></script></body></html>');
  }); }
}] });
await vite.listen();
const origin = `http://127.0.0.1:${vite.httpServer.address().port}`;
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
const screenshots = '/tmp/aegis-server-indicator-visual';
mkdirSync(screenshots, { recursive: true });
let assertions = 0;
const equal = (actual, expected, label) => { assert.equal(actual, expected, label); assertions++; };
const ok = (value, label) => { assert.ok(value, label); assertions++; };
const errors = [];
try {
  for (const width of [390, 1280]) {
    const page = await browser.newPage({ viewport: { width, height: 850 } });
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      const now = Date.now;
      window.clockOffset = 0;
      Date.now = () => now() + window.clockOffset;
      localStorage.setItem('aegis.voice.autoSpeak', 'false');
    });
    let healthCalls = 0, healthStatus = 200, stream = 'functional', releaseHealth;
    const initialHealth = new Promise(resolve => { releaseHealth = resolve; });
    await page.route('**/api/**', async route => {
      const path = new URL(route.request().url()).pathname;
      const json = (body, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
      if (path === '/api/health') {
        healthCalls++;
        if (healthCalls === 1) await initialHealth;
        return json({ status: healthStatus === 200 ? 'ok' : 'unavailable' }, healthStatus);
      }
      if (path === '/api/chat/conversations') return json({ items: [], hasMore: false });
      if (path === '/api/chat/conversations/c1') return json({ id: 'c1', title: 'Conversa', createdAt: '2026-10-29T00:00:00Z', messages: [] });
      if (path === '/api/chat/proactive-activity') return json([]);
      if (path === '/api/voice/status') return json({ enabled: false, available: false });
      if (path === '/api/voice/transcription/status') return json({ enabled: false, configured: false });
      if (path === '/api/chat/messages/stream') {
        if (stream === 'network') return route.abort('connectionfailed');
        if (stream === 'model') return route.fulfill({ contentType: 'application/x-ndjson', body: '{"type":"error","message":"Modelo temporariamente indisponível."}\n' });
        return json({ error: 'Operação não permitida.' }, 400);
      }
      return json({}, 204);
    });
    await page.goto(origin + '/__indicator');
    await page.locator('.ruri-mascot').waitFor();
    await page.waitForFunction(() => !!window.fixture);
    equal(healthCalls, 1, 'initial health probe starts once');
    equal(await page.locator('.server-indicator').count(), 0, 'checking has no visible indicator');
    equal(await page.locator('.chat-voice-controls').count(), 0, 'empty healthy/checking chat has no floating right control');
    await page.evaluate(() => { for (let i = 0; i < 10; i++) window.dispatchEvent(new Event('online')); });
    await page.waitForTimeout(50);
    equal(healthCalls, 1, 'simultaneous recovery events share the pending health probe');
    releaseHealth();
    await page.waitForResponse(response => response.url().endsWith('/api/health'));
    await page.waitForTimeout(30);
    equal(await page.locator('.server-indicator').count(), 0, 'online has no indicator');
    await page.evaluate(() => {
      for (let i = 0; i < 10; i++) { window.dispatchEvent(new Event('focus')); window.dispatchEvent(new Event('aegis-app-visibility')); }
    });
    await page.waitForTimeout(100);
    equal(healthCalls, 1, 'foreground bursts respect the 30-second cooldown');
    const probe = async (event = 'focus', advance = true) => {
      const response = page.waitForResponse(response => response.url().endsWith('/api/health'));
      await page.evaluate(({ event, advance }) => { if (advance) window.clockOffset += 31000; window.dispatchEvent(new Event(event)); }, { event, advance });
      await response;
      await page.waitForTimeout(30);
    };
    healthStatus = 503;
    await probe();
    const indicator = page.locator('.server-indicator');
    await indicator.waitFor();
    equal(await indicator.textContent(), 'Servidor indisponível', 'offline includes a readable status');
    equal(await indicator.getAttribute('role'), 'status', 'offline is accessible without relying on color');
    const solo = await indicator.boundingBox();
    const panel = await page.locator('.chat-panel').boundingBox();
    ok(Math.abs(panel.x + panel.width - solo.x - solo.width - 16) < 1, 'offline occupies the rightmost control position without menu');
    equal(solo.width, 44, 'offline aligns with the control cell');
    equal(await indicator.locator('svg').evaluate(el => el.getBoundingClientRect().width), 18, 'failure icon stays discreet');
    await page.screenshot({ path: join(screenshots, `${width}-offline-empty.png`) });
    await page.evaluate(() => window.fixture.openConversation('c1'));
    const menu = page.getByRole('button', { name: 'Menu da conversa', exact: true });
    await menu.waitFor();
    const alongside = await indicator.boundingBox(), menuBox = await menu.boundingBox();
    equal(alongside.y, menuBox.y, 'offline and menu share the same vertical alignment');
    equal(alongside.x + alongside.width, menuBox.x, 'offline sits immediately before the menu without overlap');
    ok(Math.abs(menuBox.x - solo.x) < 1, 'menu retains the rightmost control position');
    await menu.click();
    await page.getByRole('menu').waitFor();
    await page.keyboard.press('Escape');
    await page.emulateMedia({ reducedMotion: 'reduce' });
    equal(await indicator.locator('svg').evaluate(el => getComputedStyle(el).animationName), 'none', 'offline is static with reduced motion');
    await page.screenshot({ path: join(screenshots, `${width}-offline-menu.png`) });
    healthStatus = 200;
    await probe('online', false);
    equal(await indicator.count(), 0, 'network recovery checks immediately and removes offline');
    equal(await menu.count(), 1, 'recovery preserves the conversation menu');
    for (const status of [401, 429]) {
      healthStatus = status;
      await probe('online', false);
      equal(await indicator.count(), 0, `health HTTP ${status} does not imply connectivity failure`);
    }
    healthStatus = 200;
    await probe('online', false);
    for (const kind of ['functional', 'model']) {
      stream = kind;
      const before = healthCalls;
      await page.locator('textarea').fill('Mensagem de teste');
      await page.getByRole('button', { name: 'Enviar mensagem', exact: true }).click();
      await page.getByText(kind === 'model' ? 'Modelo temporariamente indisponível.' : 'Operação não permitida.', { exact: true }).waitFor();
      equal(healthCalls, before, `${kind} errors do not trigger a connectivity probe`);
      equal(await indicator.count(), 0, `${kind} errors do not mark the server offline`);
    }
    stream = 'network'; healthStatus = 503;
    const beforeFailure = healthCalls;
    await page.locator('textarea').fill('Falha de rede');
    await page.getByRole('button', { name: 'Enviar mensagem', exact: true }).click();
    await indicator.waitFor();
    equal(healthCalls, beforeFailure + 1, 'a real transport failure confirms server health once');
    healthStatus = 200;
    await probe('online', false);
    equal(await indicator.count(), 0, 'server recovery removes the anomaly again');
    const beforeHidden = healthCalls;
    await page.evaluate(() => { Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' }); window.clockOffset += 31000; document.dispatchEvent(new Event('visibilitychange')); window.dispatchEvent(new Event('online')); });
    await page.waitForTimeout(100);
    equal(healthCalls, beforeHidden, 'hidden views do not probe the server');
    await page.evaluate(() => window.fixture.unmount());
    await page.evaluate(() => { Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' }); window.dispatchEvent(new Event('online')); });
    await page.waitForTimeout(50);
    equal(healthCalls, beforeHidden, 'unmount removes health lifecycle listeners');
    await page.close();
  }
  equal(errors.length, 0, 'no browser runtime errors');
  console.log(`PASS server indicator: ${assertions} assertions, mobile/desktop Chromium, lifecycle/cooldown/transport/errors/reduced motion. Physical devices not exercised.`);
} finally { await browser.close(); await vite.close(); }
