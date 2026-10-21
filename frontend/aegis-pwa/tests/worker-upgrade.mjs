import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import http from 'node:http';
import { createRequire } from 'node:module';

const requirePwa = createRequire(new URL('../package.json', import.meta.url));
const requireClient = createRequire(new URL('../../../packages/aegis-client/package.json', import.meta.url));
const { build } = requirePwa('esbuild');
const { chromium } = requireClient('playwright-core');
const repository = new URL('../../../', import.meta.url);
// Immutable pre-maintenance worker: exercise a real upgrade, not a second production implementation.
const baseline = '5552de452c8465ec2eeacdef32308f45d5aa981d';
const historical = file => execFileSync('git', ['show', `${baseline}:${file}`], { cwd: repository, encoding: 'utf8' });
const oldSource = historical('frontend/aegis-pwa/src/sw.ts');
const oldPayload = historical('frontend/aegis-pwa/src/services/reminderNotification.ts');
const currentSource = await readFile(new URL('../src/sw.ts', import.meta.url), 'utf8');
assert.doesNotMatch(currentSource, /addEventListener\(['"]push['"]/);

// Test-only messages dispatch the same notificationclick/sync listeners as the browser.
// The queue itself is Workbox's real IndexedDB queue in both workers.
const instrumentation = `
let displayed = 0;
self.registration.showNotification = async () => { displayed++; };
self.addEventListener('message', event => {
  event.waitUntil((async () => {
    try {
      if (event.data.command === 'click' || event.data.command === 'push') {
        const pending = [];
        const synthetic = new Event(event.data.command === 'click' ? 'notificationclick' : 'push');
        Object.defineProperties(synthetic, {
          notification: { value: { data: event.data.payload, close() {} } },
          data: { value: { json: () => event.data.payload } },
          action: { value: 'acknowledge' },
          waitUntil: { value: promise => pending.push(promise) }
        });
        self.dispatchEvent(synthetic); await Promise.all(pending);
      }
      if (event.data.command === 'sync') {
        const pending = []; const sync = new Event('sync');
        Object.defineProperties(sync, {
          tag: { value: 'workbox-background-sync:aegis-reminder-interactions-v1' },
          lastChance: { value: false },
          waitUntil: { value: promise => pending.push(promise) }
        });
        self.dispatchEvent(sync); await Promise.all(pending);
      }
      event.ports[0].postMessage({ version: TEST_VERSION, count: (await interactionQueue.getAll()).length, displayed });
    } catch (error) { event.ports[0].postMessage({ error: error.message }); }
  })());
});`;
async function bundle(source, version) {
  const result = await build({
    stdin: { contents: source + instrumentation, loader: 'ts', resolveDir: new URL('../src/', import.meta.url).pathname },
    bundle: true, write: false, format: 'iife', target: 'es2022',
    define: { 'import.meta.env.VITE_AEGIS_API_BASE_URL': '""', 'self.__WB_MANIFEST': JSON.stringify([{ url: '/index.html', revision: version }]), 'TEST_VERSION': JSON.stringify(version), 'process.env.NODE_ENV': '"production"' },
    plugins: version === 'old' ? [{ name: 'historical-payload', setup(builder) {
      builder.onResolve({ filter: /^\.\/services\/reminderNotification$/ }, () => ({ path: 'legacy-payload', namespace: 'historical' }));
      builder.onLoad({ filter: /.*/, namespace: 'historical' }, () => ({ contents: oldPayload, loader: 'ts' }));
    } }] : []
  });
  return result.outputFiles[0].text;
}
const workers = { old: await bundle(oldSource, 'old'), current: await bundle(currentSource, 'current') };
let version = 'old', interactionStatus = 503;
const requests = [];
const server = http.createServer(async (request, response) => {
  response.setHeader('Cache-Control', 'no-store');
  const path = new URL(request.url, 'http://localhost').pathname;
  if (path === '/worker.js') { response.setHeader('Content-Type', 'application/javascript'); response.end(workers[version]); return; }
  if (path.startsWith('/api/')) {
    let body = ''; for await (const part of request) body += part;
    requests.push({ path, body }); response.writeHead(interactionStatus, { 'Content-Type': 'text/html' }); response.end('API terminal response'); return;
  }
  response.setHeader('Content-Type', 'text/html'); response.end(`<title>${version}</title><p>${version} application shell</p>`);
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
try {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto(origin);
  await page.evaluate(async () => {
    localStorage.setItem('owner-preference', 'preserved');
    const database = await new Promise((resolve, reject) => {
      const request = indexedDB.open('owner-state', 1);
      request.onupgradeneeded = () => request.result.createObjectStore('preferences');
      request.onsuccess = () => resolve(request.result); request.onerror = () => reject(request.error);
    });
    await new Promise((resolve, reject) => {
      const transaction = database.transaction('preferences', 'readwrite');
      transaction.objectStore('preferences').put('preserved', 'key');
      transaction.oncomplete = resolve; transaction.onerror = () => reject(transaction.error);
    }); database.close();
    await navigator.serviceWorker.register('/worker.js'); await navigator.serviceWorker.ready;
  });
  await page.waitForFunction(() => !!navigator.serviceWorker.controller);
  const message = command => page.evaluate(command => new Promise((resolve, reject) => {
    const channel = new MessageChannel(); const timer = setTimeout(() => reject(new Error('worker message timeout')), 10000);
    channel.port1.onmessage = event => { clearTimeout(timer); event.data.error ? reject(new Error(event.data.error)) : resolve(event.data); };
    navigator.serviceWorker.controller.postMessage(command, [channel.port2]);
  }), command);
  const payload = { type: 'reminder', reminderId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', text: 'Synthetic reminder', acknowledgeToken: 'fixture-ack', openToken: 'fixture-open' };
  assert.equal((await message({ command: 'push', payload })).displayed, 1);
  assert.equal((await message({ command: 'click', payload })).count, 1);
  version = 'current';
  await page.evaluate(async () => {
    const replacement = new Promise(resolve => navigator.serviceWorker.addEventListener('controllerchange', resolve, { once: true }));
    await (await navigator.serviceWorker.getRegistration()).update(); await replacement;
  });
  const upgraded = await message({ command: 'state' });
  assert.equal(upgraded.version, 'current'); assert.equal(upgraded.count, 1);
  assert.equal((await message({ command: 'push', payload })).displayed, 0);
  interactionStatus = 204;
  assert.equal((await message({ command: 'sync' })).count, 0);
  assert.ok(requests.some(request => request.path.endsWith('/acknowledge') && JSON.parse(request.body).token === 'fixture-ack'));
  interactionStatus = 503;
  assert.equal((await message({ command: 'click', payload })).count, 1);
  interactionStatus = 410;
  assert.equal((await message({ command: 'sync' })).count, 0); // Expired tokens never retry forever.
  assert.equal(await page.evaluate(() => localStorage.getItem('owner-preference')), 'preserved');
  assert.equal(await page.evaluate(() => new Promise(resolve => {
    const request = indexedDB.open('owner-state', 1); request.onsuccess = () => {
      const database = request.result; const read = database.transaction('preferences').objectStore('preferences').get('key');
      read.onsuccess = () => { resolve(read.result); database.close(); };
    };
  })), 'preserved');
  await page.goto(`${origin}/conversation/offline`);
  assert.equal(await page.title(), 'current');
  const api = await page.goto(`${origin}/api/must-not-use-shell`);
  assert.equal(api.status(), 410);
  await context.setOffline(true);
  await page.goto(`${origin}/conversation/cached`);
  assert.equal(await page.title(), 'current');
  console.log('Worker upgrade passed: legacy queue/actions, terminal responses, cache, navigation, /api exclusion and stored owner state.');
} finally {
  await browser.close(); await new Promise(resolve => server.close(resolve));
}
