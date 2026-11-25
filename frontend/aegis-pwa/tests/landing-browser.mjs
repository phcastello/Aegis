import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import http from 'node:http';
import { createRequire } from 'node:module';
import { feed } from '../landing.js';

const requireClient = createRequire(new URL('../../../packages/aegis-client/package.json', import.meta.url));
const { chromium } = requireClient('playwright-core');
const version = '0.8.0-unstable.45';
const base = `https://github.com/phcastello/Aegis/releases/download/node-v${version}/`;
const fixture = { schema: 2, version,
  platforms: { 'windows-x86_64': { url: base + 'Aegis-Windows-x86_64-Setup.exe', sha256: 'a'.repeat(64) } },
  android: { aarch64: { url: base + 'Aegis-Android-arm64.apk', sha256: 'b'.repeat(64) } }
};
const files = { '/': ['index.html', 'text/html'], '/landing.css': ['landing.css', 'text/css'], '/landing.js': ['landing.js', 'text/javascript'], '/favicon.svg': ['favicon.svg', 'image/svg+xml'] };
const server = http.createServer(async (request, response) => {
  const file = files[new URL(request.url, 'http://localhost').pathname];
  if (!file) { response.writeHead(404); response.end(); return; }
  response.writeHead(200, {
    'Content-Type': file[1], 'Cache-Control': 'no-store', 'Referrer-Policy': 'no-referrer',
    'Content-Security-Policy': "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src https://raw.githubusercontent.com; base-uri 'none'; frame-ancestors 'none'; form-action 'none'"
  });
  response.end(await readFile(new URL(`../dist/${file[0]}`, import.meta.url)));
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
try {
  for (const [width, height] of [[1440, 900], [390, 844], [320, 700]]) {
    const context = await browser.newContext({ viewport: { width, height } });
    const page = await context.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route(feed, route => route.fulfill({ json: fixture, headers: { 'Access-Control-Allow-Origin': '*' } }));
    await page.goto(origin);
    await page.getByRole('link', { name: 'Baixar para Windows' }).waitFor();
    assert.equal(await page.locator('#android-download').getAttribute('href'), fixture.android.aarch64.url);
    assert.equal(await page.locator('#windows-download').getAttribute('href'), fixture.platforms['windows-x86_64'].url);
    assert.equal(await page.locator('#release-label').textContent(), 'Última versão · v0.8.0-unstable.45');
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true);
    assert.equal(await page.locator('link[rel=manifest], textarea, form').count(), 0);
    assert.equal(await page.evaluate(async () => (await navigator.serviceWorker.getRegistrations()).length), 0);
    assert.deepEqual(errors, []);
    if (process.env.AEGIS_GATEWAY_SCREENSHOT_PREFIX && width !== 320) {
      await page.screenshot({ path: `${process.env.AEGIS_GATEWAY_SCREENSHOT_PREFIX}-${width}.png`, fullPage: true });
    }
    await page.goto(origin + '/?email=connected');
    assert.match(await page.locator('#oauth').textContent(), /Conta Google conectada/);
    assert.equal(await page.locator('#oauth').isVisible(), true);
    await page.goto(origin + '/?email=connect_failed&email_error_code=synthetic-query');
    assert.match(await page.locator('#oauth').textContent(), /Não foi possível/);
    assert.doesNotMatch(await page.locator('body').textContent(), /synthetic-query/);
    await context.close();
  }
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.route(feed, route => route.fulfill({ status: 503, headers: { 'Access-Control-Allow-Origin': '*' } }));
  await page.goto(origin);
  assert.equal(await page.locator('#windows-download').getAttribute('href'), 'https://github.com/phcastello/Aegis/releases');
  assert.equal(await page.locator('#android-download').getAttribute('href'), 'https://github.com/phcastello/Aegis/releases');
  await context.close();
  console.log('Landing passed: responsive layouts, native links, fallback, OAuth result, no browser application/registration.');
} finally {
  await browser.close();
  await new Promise(resolve => server.close(resolve));
}
