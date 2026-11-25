// Exercise the actual production bundle, serving the existing Tauri CSP.
// The simulated agent string exercises the branch; Android instrumentation
// supplies evidence from the genuine WebView provider as well.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { createServer as createNetServer } from 'node:net';
import { fileURLToPath, pathToFileURL } from 'node:url';

const require = createRequire(new URL('../package.json', import.meta.url));
const clientRequire = createRequire(new URL('../../../packages/aegis-client/package.json', import.meta.url));
const { chromium } = clientRequire('playwright-core');
const { preview } = await import(pathToFileURL(require.resolve('vite')).href);
const root = fileURLToPath(new URL('../', import.meta.url));
const csp = JSON.parse(readFileSync(new URL('../src-tauri/tauri.conf.json', import.meta.url))).app.security.csp;
const version = JSON.parse(readFileSync(new URL('../package.json', import.meta.url))).version;
const port = await new Promise((resolve, reject) => {
  const probe = createNetServer();
  probe.once('error', reject);
  probe.listen(0, '127.0.0.1', () => {
    const { port } = probe.address();
    probe.close(error => error ? reject(error) : resolve(port));
  });
});
const server = await preview({ root, preview: { host: '127.0.0.1', port, strictPort: true,
  headers: { 'Content-Security-Policy': csp } } });
const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });

try {
  for (const [platform, major] of [['android',69], ['android',107], ['android',140], ['windows',140]]) {
    const userAgent = platform === 'android'
      ? `Mozilla/5.0 (Linux; Android 8.0.0; Aegis fixture; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/${major}.0.0.0 Mobile Safari/537.36`
      : `Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/${major}.0.0.0 Safari/537.36`;
    const context = await browser.newContext({ userAgent, viewport: { width:390, height:844 } });
    await context.route('**/api/**', route => route.fulfill({status:503,contentType:'application/json',body:'{}'}));
    await context.addInitScript(({platform, version}) => {
      window.runtimeInfoCalls = 0;
      window.__TAURI_INTERNALS__ = { transformCallback: () => 1, unregisterCallback: () => {},
        invoke: async (command) => {
          if (command === 'runtime_info') {
            window.runtimeInfoCalls++;
            return { platform, version, backendUrl: location.origin };
          }
          if (command === 'node_status') return {state:'unpaired',node:null,error:null};
          if (command === 'node_notification_settings') return {granted:true,pendingActions:0,failedActions:0};
          if (command === 'node_transport_status') return {transportState:'offline'};
          if (command === 'node_consume_reminder_open') return {reminderId:null,conversationId:null};
          if (command === 'check_android_update' || command === 'node_list') return null;
          throw Error('Unsupported test operation');
        } };
    }, {platform,version});
    try {
      const page = await context.newPage();
      const response = await page.goto(origin);
      assert.equal(response.headers()['content-security-policy'],csp);
      if (platform === 'android' && major < 107) {
        const notice = page.getByRole('alert').filter({hasText:'Atualize o Android System WebView'});
        await notice.waitFor({state:'visible'});
        assert.match(await notice.innerText(), /reabra a Aegis/);
        // The modern test engine can parse the module despite the synthetic
        // older UA; wait for load so the entrypoint guard is also exercised.
        await page.waitForLoadState('load');
        assert.equal(await page.evaluate(() => window.runtimeInfoCalls), 0);
        assert.equal(await page.locator('.app-shell').count(),0);
        assert.equal(await page.locator('#app').getAttribute('data-aegis-webview-unsupported'),'true');
      } else {
        await page.locator('.app-shell').waitFor({state:'visible'});
        assert.equal(await page.evaluate(() => window.runtimeInfoCalls),1);
        assert.equal(await page.locator('#aegis-webview-unsupported').isVisible(),false);
      }
    } finally { await context.close(); }
  }
  console.log('Production WebView bootstrap: visible Android 69 warning; Android 107/140 and Windows mount normally under existing CSP PASS (simulated UA/IPC).');
} finally {
  await browser.close();
  await new Promise(resolve => server.httpServer.close(resolve));
}
