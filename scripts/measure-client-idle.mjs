// Isolated browser measurement. This does not measure Windows WebView2, Android,
// native keepalive/FCM, TLS, RDP, or system-wide traffic. Never uses real Nodes.
import { chromium } from '../packages/aegis-client/node_modules/playwright-core/index.mjs';
import http from 'node:http';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { resolve, extname } from 'node:path';
import os from 'node:os';

const [directory, output, seconds = '600'] = process.argv.slice(2);
if (!directory || !output || Number(seconds) < 600) throw Error('Usage: node scripts/measure-client-idle.mjs DIST OUTPUT [seconds>=600]');
const root = resolve(directory);
const server = http.createServer(async (req, res) => {
  const path = new URL(req.url, 'http://localhost').pathname;
  if (path.startsWith('/api/')) {
    const data = path.endsWith('/health') ? { status: 'ok' } : path.includes('/conversations') ? { items: [], hasMore: false } : { enabled: false, available: false, configured: false, isConnected: false };
    const body = JSON.stringify(data); res.writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) }); res.end(body); return;
  }
  try {
    const file = resolve(root, '.' + (path === '/' ? '/index.html' : path));
    if (!file.startsWith(root + '/')) throw Error('path');
    const body = await readFile(file);
    res.writeHead(200, { 'Content-Type': ({ '.html': 'text/html', '.js': 'application/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png' })[extname(file)] ?? 'application/octet-stream' }); res.end(body);
  } catch { res.writeHead(404); res.end(); }
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const url = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE ?? '/home/phc/.cache/ms-playwright/chromium-1243/chrome-linux64/chrome', headless: true });
await mkdir(resolve(output, '..'), { recursive: true });
const scenarios = [];
try {
  for (const mobile of [false, true]) for (const panel of [false, true]) {
    const context = await browser.newContext({ viewport: mobile ? { width: 390, height: 844 } : { width: 1440, height: 900 } });
    await context.addInitScript(({ url, mobile }) => {
      const now = new Date().toISOString();
      const node = { id: '11111111-1111-4111-8111-111111111111', name: 'Dispositivo de medição', platform: mobile ? 'android' : 'windows', enabled: true, receiveReminders: true, appVersion: '0.7.0-unstable.11', protocolVersion: 1, pairedAt: now, createdAt: now, updatedAt: now, revokedAt: null, availability: 'online', capabilities: [{name:'notification.show', version:1}], targetPriority: 0 };
      window.idleIpc = {};
      window.__TAURI_INTERNALS__ = { invoke: async (command) => {
        window.idleIpc[command] = (window.idleIpc[command] ?? 0) + 1;
        if (command === 'runtime_info') return { platform: mobile ? 'android' : 'windows', backendUrl: url, version: '0.7.0-unstable.11' };
        if (command === 'node_status') return { state: 'paired', node, error: null };
        if (command === 'node_list') return [node];
        if (command === 'check_backend') return { status: 'connected', message: null };
        if (command === 'node_transport_status') return { transportState: 'online', lastError: null };
        if (command === 'node_notification_settings') return { granted: true, pushConfigured: true, autostart: false };
        if (command === 'node_location') return { sourceNodeId: null, nodes: [], latest: null };
        if (command === 'node_places' || command === 'node_geofences' || command === 'node_reminders') return [];
        if (command === 'plugin:updater|check' || command === 'check_android_update') return null;
        if (command.startsWith('plugin:event|')) return 1;
        return null;
      }};
      localStorage.setItem('aegis.voice.autoSpeak', 'false');
      const original = window.setInterval.bind(window);
      window.idleIntervals = [];
      window.setInterval = (handler, delay, ...args) => { window.idleIntervals.push(delay); return original(handler, delay, ...args); };
    }, { url, mobile });
    const page = await context.newPage(); const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.goto(url); await page.waitForTimeout(2000);
    if (panel) {
      const devices = page.getByRole('button', { name: /Dispositivos/ }).first();
      if (await devices.count() && await devices.isVisible()) await devices.click();
      else {
        await page.getByRole('button', { name: /Configurações/ }).first().click();
        const item = page.getByRole('button', { name: /Dispositivos/ }).first(); if (await item.count()) await item.click();
      }
    }
    await page.waitForTimeout(1000);
    await page.evaluate(() => { window.idleIpc = {}; });
    const cdp = await context.newCDPSession(page); await cdp.send('Performance.enable');
    const before = (await cdp.send('Performance.getMetrics')).metrics;
    const traffic = { requests: 0, responseBodyBytes: 0 };
    page.on('request', request => { if (request.url().startsWith(url)) traffic.requests++; });
    page.on('response', async response => { if (response.url().startsWith(url)) traffic.responseBodyBytes += (await response.body().catch(() => Buffer.alloc(0))).length; });
    scenarios.push({ context, page, cdp, before, traffic, errors, mobile, panel, started: Date.now() });
  }
  await new Promise(r => setTimeout(r, Number(seconds) * 1000));
  const measurements = [];
  for (const s of scenarios) {
    const after = (await s.cdp.send('Performance.getMetrics')).metrics;
    const metric = (list, name) => list.find(m => m.name === name)?.value ?? 0;
    const duration = (Date.now() - s.started) / 1000;
    measurements.push({ mobileViewport: s.mobile, devicesPanel: s.panel, durationSeconds: duration,
      rendererTaskSeconds: metric(after, 'TaskDuration') - metric(s.before, 'TaskDuration'),
      rendererTaskPercent: 100 * (metric(after, 'TaskDuration') - metric(s.before, 'TaskDuration')) / duration,
      requestsPerMinute: s.traffic.requests * 60 / duration, responseBodyBytesPerMinute: s.traffic.responseBodyBytes * 60 / duration,
      traffic: s.traffic, state: await s.page.evaluate(() => ({ ipc: window.idleIpc, intervals: window.idleIntervals, animations: document.getAnimations().filter(a => a.playState === 'running').length })), errors: s.errors });
  }
  await writeFile(output, JSON.stringify({ measuredAt: new Date().toISOString(), kind: 'isolated Chromium surrogate; not native/physical', platform: os.platform(), cpus: os.cpus()[0]?.model, logicalCpus: os.cpus().length, memoryBytes: os.totalmem(), browser: browser.version(), note: 'Four independent pages concurrent in both runs. Loopback response body only; IPC counted, native network not executed. No RDP traffic included.', measurements }, null, 2) + '\n');
  console.log(output);
} finally { await browser.close(); server.close(); }
