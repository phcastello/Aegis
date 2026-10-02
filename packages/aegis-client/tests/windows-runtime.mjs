// Real Windows/WebView2 smoke test. CDP is enabled only in this child process's CI environment.
// Official mechanism: https://playwright.dev/dotnet/docs/webview2
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { chromium } from 'playwright-core';
if (process.platform !== 'win32') throw new Error('This smoke test requires a real Windows runner.');
const profile = mkdtempSync(join(tmpdir(), 'aegis-webview2-'));
const version = JSON.parse(readFileSync('package.json', 'utf8')).version;
const application = spawn(resolve(process.argv[2]), [], {
  env: { ...process.env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=9460', WEBVIEW2_USER_DATA_FOLDER: profile },
  stdio: 'inherit'
});
let exitCode;
application.on('exit', code => { exitCode = code; });
let browser;
try {
  const deadline = Date.now() + 30000;
  while (true) {
    if (exitCode !== undefined) throw new Error('Aegis exited before creating WebView2: ' + exitCode);
    try { const response = await fetch('http://127.0.0.1:9460/json/version'); if (response.ok) break; } catch {}
    if (Date.now() >= deadline) throw new Error('WebView2 CDP unavailable; verify runner WebView2 installation/session.');
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  browser = await chromium.connectOverCDP('http://127.0.0.1:9460');
  // CDP can become available before WebView2 publishes its first page target.
  const pageDeadline = Date.now() + 30000;
  let page;
  while (!page) {
    if (exitCode !== undefined) throw new Error('Aegis exited before creating its WebView page: ' + exitCode);
    page = browser.contexts().flatMap(context => context.pages())[0];
    if (!page && Date.now() >= pageDeadline) throw new Error('WebView2 did not create a page target.');
    if (!page) await new Promise(resolve => setTimeout(resolve, 100));
  }
  await page.getByRole('region', { name: 'Conversa com a Aegis' }).waitFor();
  const runtime = await page.evaluate(() => window.__TAURI_INTERNALS__.invoke('runtime_info'));
  assert.equal(runtime.platform, 'windows'); assert.equal(runtime.version, version);
  assert.equal(runtime.backendUrl, 'https://aegis.phcastello.com');
  await page.getByLabel('Sobre a Aegis').click();
  await page.getByText(`Aegis ${version}`, { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Retry', exact: true }).waitFor();
  await page.waitForFunction(() => !document.querySelector('.diagnostics button')?.disabled);
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await page.waitForFunction(() => !document.querySelector('.diagnostics button')?.disabled);
  const labels = await page.locator('.diagnostics dd').allTextContents();
  assert.equal(labels[0], 'Windows'); assert.ok(['Connected', 'Unavailable'].includes(labels[1]));
  // No conversation content or screenshots of the production account are published.
  console.log(`Windows native runtime PASS: Vue renders; native platform Windows; version ${version}; health ${labels[1]}; Retry responds.`);
  const closed = spawnSync('powershell.exe', ['-NoProfile', '-Command', `(Get-Process -Id ${application.pid}).CloseMainWindow()`], { encoding: 'utf8' });
  if (closed.status !== 0 || closed.stdout.trim() !== 'True') throw new Error('Could not close the native main window normally.');
  const closeDeadline = Date.now() + 10000;
  while (exitCode === undefined && Date.now() < closeDeadline) await new Promise(resolve => setTimeout(resolve, 100));
  assert.equal(exitCode, 0, 'Normal window close must exit successfully.');
  console.log('Windows normal close PASS. Audio, OAuth system browser and update install require separate device acceptance.');
} finally {
  await browser?.close().catch(() => {});
  if (exitCode === undefined) spawnSync('taskkill', ['/PID', String(application.pid), '/T', '/F'], { stdio: 'ignore' });
  rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
