// Launch the installed executable on a disposable Windows runner, without pairing.
// CDP observes the real WebView2 and Tauri bridge; only private API data is stubbed.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { reloadReadyWindowsPage } from './windows-webview-ready.mjs';
import { assertNativeReleaseIdentity, routeOfflineIdentityApi } from './native-release-identity.mjs';

const repository = fileURLToPath(new URL('../../../', import.meta.url));
const pause = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

export async function windowsReleaseIdentity(executable, output) {
  assert.equal(process.platform, 'win32', 'Requires a real Windows/WebView2 runner.');
  const version = JSON.parse(readFileSync(join(repository, 'apps/aegis-node/package.json'), 'utf8')).version;
  const profile = mkdtempSync(join(tmpdir(), 'aegis-release-identity-'));
  const app = spawn(resolve(executable), [], { env: { ...process.env,
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=9464 --proxy-server=http://127.0.0.1:9 --proxy-bypass-list=tauri.localhost;localhost;127.0.0.1',
    WEBVIEW2_USER_DATA_FOLDER: profile }, stdio: 'ignore' });
  let exited, browser;
  app.on('exit', value => { exited = value; });
  app.on('error', () => { exited = -1; });
  try {
    const deadline = Date.now() + 45000;
    while (true) {
      assert.equal(exited, undefined, 'Installed application must stay alive.');
      try { if ((await fetch('http://127.0.0.1:9464/json/version', { signal: AbortSignal.timeout(3000) })).ok) break; } catch {}
      if (Date.now() >= deadline) throw Error('Installed Windows WebView2 CDP unavailable.');
      await pause(150);
    }
    browser = await chromium.connectOverCDP('http://127.0.0.1:9464');
    const routed = new Set();
    const page = await reloadReadyWindowsPage(async timeout => {
      const targetDeadline = Date.now() + Math.min(timeout, 30000);
      let page;
      while (!page) {
        page = browser.contexts().flatMap(context => context.pages()).find(candidate => !candidate.isClosed() && /^(?:https?:\/\/tauri\.localhost(?:\/|$)|tauri:\/\/localhost(?:\/|$))/.test(candidate.url()));
        if (!page && Date.now() >= targetDeadline) throw Error('Installed Windows application page unavailable.');
        if (!page) await pause(100);
      }
      if (!routed.has(page)) { await routeOfflineIdentityApi(page); routed.add(page); }
      return page;
    }, () => exited === undefined);
    const report = await assertNativeReleaseIdentity(page, { version, platform: 'windows', output });
    mkdirSync(output, { recursive: true });
    writeFileSync(join(output, 'windows-identity.json'), JSON.stringify({ ...report,
      limits: 'Real installed Windows application and WebView2 with original native IPC. Isolated unpaired WebView profile, process-only offline proxy and disposable HTTP API responses. This is not owner-machine acceptance or Windows display-scale coverage.'
    }, null, 2) + '\n');
    await page.evaluate(() => window.__TAURI_INTERNALS__.invoke('node_exit')).catch(() => {});
    const exitDeadline = Date.now() + 15000;
    while (exited === undefined && Date.now() < exitDeadline) await pause(100);
    assert.equal(exited, 0);
    console.log('Real installed Windows/WebView2 sidebar and About release identity PASS.');
    return report;
  } finally {
    await browser?.close().catch(() => {});
    if (exited === undefined) spawnSync('taskkill', ['/PID', String(app.pid), '/T', '/F'], { stdio: 'ignore' });
    rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  assert.ok(process.argv[2] && process.argv[3], 'Usage: windows-release-identity.mjs INSTALLED.exe OUTPUT');
  await windowsReleaseIdentity(process.argv[2], resolve(process.argv[3]));
}
