import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';

// UI integration only. This IPC fixture cannot prove Tauri ACL or Android execution.
const require = createRequire(new URL('../package.json', import.meta.url));
const clientRequire = createRequire(new URL('../../../packages/aegis-client/package.json', import.meta.url));
const { chromium } = clientRequire('playwright-core');
const { createServer } = await import(require.resolve('vite'));
const root = new URL('../', import.meta.url).pathname;
const version = JSON.parse(readFileSync(new URL('../package.json', import.meta.url))).version;
const server = await createServer({ root, server: { host: '127.0.0.1', port: 0, strictPort: false } });
await server.listen();
const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
try {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
  await context.route('**/api/**', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{}' }));
  await context.addInitScript(({ version }) => {
    localStorage.setItem('aegis.voice.autoSpeak', 'false');
    const initial = { enabled: false, diagnostic: false, assistantSelected: false, permissionGranted: false, modelReady: false, captureConfirmed: false, status: 'disabled', message: 'Escuta desligada.' };
    window.hfFixture = { fail: 'command_not_authorized', calls: [], initial, queue: [], active: 0, peak: 0 };
    window.__TAURI_INTERNALS__ = { invoke: async (command, args) => {
      if (command === 'node_hands_free') {
        const f = window.hfFixture; f.calls.push({ ...args }); f.peak = Math.max(f.peak, ++f.active);
        try {
          if (f.hold) { f.hold = false; return await new Promise((resolve, reject) => { f.resolve = resolve; f.reject = reject; }); }
          if (f.fail) throw { code: f.fail, message: 'private exception must never be rendered' };
          return JSON.parse(JSON.stringify(f.initial));
        } finally { f.active--; }
      }
      if (command === 'runtime_info') return { platform: 'android', backendUrl: location.origin, version };
      if (command === 'node_status') return { state: 'unpaired', node: null, error: null };
      if (command === 'node_notification_settings') return { granted: true, pendingActions: 0, failedActions: 0, actionDiagnosticsAvailable: true, actionDiagnostics: [] };
      if (command === 'node_transport_status') return { transportState: 'offline' };
      if (command === 'node_consume_reminder_open') return { reminderId: null };
      if (command === 'node_list') return [];
      if (command === 'check_android_update') return null;
      if (command === 'check_backend') return { status: 'unavailable', message: null };
      if (command === 'node_settings_open' || command === 'node_hide_keyboard') return;
      if (command.startsWith('plugin:event|')) return 0;
      throw Error('Unsupported fixture operation');
    } };
  }, { version });
  const page = await context.newPage();
  page.on('pageerror', error => console.error(error.message));
  await page.goto(origin);
  await page.getByRole('button', { name: 'Abrir histórico', exact: true }).click();
  await page.getByRole('button', { name: 'Configurações da Aegis', exact: true }).click();
  const settings = page.getByRole('dialog', { name: 'Configurações', exact: true });
  await settings.getByRole('button', { name: 'Áudio e voz', exact: true }).click();
  const hf = page.locator('.hands-free-settings');
  await hf.getByRole('button', { name: 'Tentar novamente', exact: true }).waitFor();
  assert.equal(await hf.getByText('Consultando escuta…', { exact: true }).count(), 0, 'failure ends initial loading');
  assert.equal(await hf.getByRole('checkbox').count(), 0, 'failed communication invents no setup state');
  assert.equal(await hf.getByText('Código: command_not_authorized', { exact: true }).count(), 1);
  assert(!await hf.innerText().then(text => text.includes('private exception')));
  await page.evaluate(() => { window.hfFixture.fail = null; });
  await hf.getByRole('button', { name: 'Tentar novamente', exact: true }).click();
  await hf.getByRole('button', { name: 'Importar modelo privado', exact: true }).waitFor();
  assert.equal(await hf.getByRole('alert').count(), 0, 'successful retry removes old error');
  assert.equal(await hf.getByText('Código:', { exact: false }).count(), 0);
  for (const name of ['Permitir microfone', 'Selecionar assistente no Android', 'Importar modelo privado'])
    assert.equal(await hf.getByRole('button', { name, exact: true }).isEnabled(), true);
  assert.equal(await hf.getByRole('checkbox', { name: 'Escutar “Aegis”', exact: true }).isChecked(), false);
  assert.deepEqual(await page.evaluate(() => [...new Set(window.hfFixture.calls.map(x => x.operation))]), ['state'], 'query never requests permissions/configuration');

  await page.evaluate(() => { window.hfFixture.fail = 'timeout'; });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  await hf.getByText('Código: timeout', { exact: true }).waitFor();
  assert.equal(await hf.getByText('Último estado conhecido: Escuta desligada.', { exact: true }).count(), 1);
  assert.equal(await hf.getByRole('button', { name: 'Permitir microfone', exact: true }).isEnabled(), false);
  assert.equal(await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).isEnabled(), true);
  assert.equal(await hf.getByText('Atualizando estado…', { exact: true }).count(), 0);

  await page.evaluate(() => { window.hfFixture.fail = null; window.hfFixture.hold = true; });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  await hf.getByText('Atualizando estado…', { exact: true }).waitFor();
  await page.evaluate(() => { for (let i = 0; i < 15; i++) window.dispatchEvent(new Event('focus')); window.hfFixture.initial.message = 'Estado mais recente.'; window.hfFixture.resolve({ ...window.hfFixture.initial, message: 'Estado antigo.' }); });
  await hf.getByText('Estado mais recente.', { exact: true }).waitFor();
  assert.equal(await page.evaluate(() => window.hfFixture.peak), 1, 'refresh events never overlap native calls');
  assert.equal(await hf.getByRole('alert').count(), 0);

  await page.evaluate(() => { window.hfFixture.hold = true; });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  await hf.getByText('Atualizando estado…', { exact: true }).waitFor();
  await settings.getByRole('button', { name: 'Sobre', exact: true }).click();
  await page.evaluate(() => { window.hfFixture.resolve({ ...window.hfFixture.initial, enabled: true, message: 'Resultado desmontado.' }); });
  await settings.getByRole('button', { name: 'Áudio e voz', exact: true }).click();
  await hf.getByText('Estado mais recente.', { exact: true }).waitFor();
  assert.equal(await hf.getByText('Resultado desmontado.', { exact: true }).count(), 0);
  assert.equal(await hf.getByRole('checkbox', { name: 'Escutar “Aegis”', exact: true }).isChecked(), false);
  console.log('Hands-free UI: initial failure, accessible retry, setup without prerequisites/backend, stale state, recovery, serialized events and unmount PASS (mock IPC; no native ACL claim)');
  await context.close();
} finally { await browser.close(); await server.close(); }
