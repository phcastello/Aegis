import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync, mkdirSync } from 'node:fs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createServer as createNetServer } from 'node:net';

// UI integration only. This IPC fixture cannot prove Tauri ACL or Android execution.
const require = createRequire(new URL('../package.json', import.meta.url));
const clientRequire = createRequire(new URL('../../../packages/aegis-client/package.json', import.meta.url));
const { chromium } = clientRequire('playwright-core');
const { createServer } = await import(pathToFileURL(require.resolve('vite')).href);
const root = fileURLToPath(new URL('../', import.meta.url));
const version = JSON.parse(readFileSync(new URL('../package.json', import.meta.url))).version;
// Vite treats port 0 as its default rather than as an ephemeral port; allocate
// a free loopback port to avoid colliding with local development or Windows reservations.
const port = await new Promise((resolve, reject) => {
  const probe = createNetServer();
  probe.once('error', reject);
  probe.listen(0, '127.0.0.1', () => {
    const { port } = probe.address();
    probe.close(error => error ? reject(error) : resolve(port));
  });
});
const server = await createServer({ root, server: { host: '127.0.0.1', port, strictPort: false } });
await server.listen();
const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
try {
  const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
  await context.route('**/api/**', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{}' }));
  await context.addInitScript(({ version }) => {
    localStorage.setItem('aegis.voice.autoSpeak', 'false');
    const initial = { enabled: false, diagnostic: false, assistantSelected: false, permissionGranted: false, modelReady: false, captureConfirmed: false, status: 'disabled', message: 'Escuta desligada.' };
    const windows = new URLSearchParams(location.search).has('windows');
    if (windows) { initial.assistantSelected = true; initial.permissionGranted = true; initial.generation = 0; }
    window.hfFixture = { fail: windows ? null : 'command_not_authorized', calls: [], initial, queue: [], active: 0, peak: 0 };
    const callbacks = new Map();
    const events = new Map();
    let nextId = 1;
    window.hfFixture.voiceEvent = status => {
      Object.assign(initial, { status, generation: initial.generation + 1 });
      for (const [id, name] of events) {
        if (name === 'aegis-windows-voice-state') callbacks.get(id)({ id, event: name, payload: { status, generation: initial.generation } });
      }
    };
    window.hfFixture.conversationEvent = conversationId => {
      for (const [id, name] of events) {
        if (name === 'aegis-windows-voice-conversation') callbacks.get(id)({ id, event: name, payload: { conversationId } });
      }
    };
    window.hfFixture.waveEvent = (levels, generation = initial.generation, status = initial.status) => {
      for (const [id, name] of events) {
        if (name === 'aegis-windows-voice-waveform') callbacks.get(id)({ id, event: name, payload: { status, generation, levels } });
      }
    };
    window.__TAURI_EVENT_PLUGIN_INTERNALS__ = { unregisterListener: (_name, id) => { events.delete(id); callbacks.delete(id); } };
    window.__TAURI_INTERNALS__ = { transformCallback: callback => {
      const id = nextId++;
      callbacks.set(id, callback);
      return id;
    }, invoke: async (command, args) => {
      if (command === 'plugin:event|listen') { events.set(args.handler, args.event); return args.handler; }
      if (command === 'node_hands_free') {
        if (args.operation === 'conversationChanges') return { generation: '00000000-0000-4000-8000-000000000001', changes: [] };
        const f = window.hfFixture; f.calls.push({ ...args }); f.peak = Math.max(f.peak, ++f.active);
        try {
          if (f.hold) { f.hold = false; return await new Promise((resolve, reject) => { f.resolve = resolve; f.reject = reject; }); }
          if (f.fail) throw { code: f.fail, message: 'private exception must never be rendered' };
          if (windows && args.operation === 'import') Object.assign(f.initial, { modelReady: true, modelName: 'Aegis Open v2' });
          if (windows && args.operation === 'configure') Object.assign(f.initial, { enabled: args.enabled, diagnostic: args.diagnostic });
          if (args.operation === 'mediaReferenceStart') f.initial.mediaReference = f.mediaGrant
            ? { supported: true, session: true, status: 'ready' }
            : { supported: true, session: false, status: 'denied' };
          if (args.operation === 'mediaReferenceStop') f.initial.mediaReference = { supported: true, session: false, status: 'off' };
          if (args.operation === 'batteryStart') f.initial.batteryDiagnostics.active = { id: 'study_1', mode: args.batteryMode, startedAt: '2026-11-16T10:00:00-03:00', elapsedMs: 60000 };
          if (args.operation === 'batteryFinish') { f.initial.batteryDiagnostics.active = null; f.initial.batteryDiagnostics.requiresResume = false; }
          if (args.operation === 'batteryAutoStart' || args.operation === 'batteryAutoValidate') f.initial.batteryDiagnostics.automation = { id: 'auto_sequence_1', status: 'waiting_screen_off', mode: 'off', stageDurationMs: args.operation === 'batteryAutoValidate' ? 60000 : 1800000, validation: args.operation === 'batteryAutoValidate', remainingEstimateMs: args.operation === 'batteryAutoValidate' ? 180000 : 5400000, runIds: [], alarmPrecision: 'inexact', delayMs: 0, issue: null };
          if (args.operation === 'batteryAutoCancel') { f.initial.batteryDiagnostics.automation.status = 'cancelled'; f.initial.batteryDiagnostics.automation.mode = null; f.initial.batteryDiagnostics.active = null; f.initial.batteryDiagnostics.requiresResume = false; }
          return JSON.parse(JSON.stringify(f.initial));
        } finally { f.active--; }
      }
      if (command === 'runtime_info') return { platform: windows ? 'windows' : 'android', backendUrl: location.origin, version };
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
  assert.equal(await hf.getByText('Modelo instalado:', { exact: false }).count(), 0, 'no model identity is invented before import');
  assert.deepEqual(await page.evaluate(() => [...new Set(window.hfFixture.calls.map(x => x.operation))]), ['state'], 'query never requests permissions/configuration');

  for (const identity of [
    { modelName: 'Baseline', modelVersion: 'aegis-mww-fleurs-dev-2026-10-31', modelSha256: 'f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514' },
    { modelName: 'Strong — época 2', modelVersion: 'aegis-mww-strong-epoch-02-2026-11-05', modelSha256: '1e7c686420f65d8da892f02c71d945f03a8dd528a1005de8c9c8482dce52ad2e' },
    { modelName: 'Baseline', modelVersion: 'aegis-mww-fleurs-dev-2026-10-31', modelSha256: 'f9be8104b6a82dc91ed38febd1601e07eca0ef51f2ebbec4899e06f6e3aea514' }
  ]) {
    await page.evaluate(identity => { Object.assign(window.hfFixture.initial, { modelReady: true, ...identity }); }, identity);
    await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
    await hf.getByText(`Modelo instalado: ${identity.modelName}`, { exact: true }).waitFor();
    assert.equal(await hf.getByRole('button', { name: 'Importar modelo novamente', exact: true }).isEnabled(), true);
  }
  assert.deepEqual(await page.evaluate(() => [...new Set(window.hfFixture.calls.map(x => x.operation))]), ['state'], 'model labels never replace or enable a model implicitly');

  await page.evaluate(() => { window.hfFixture.fail = 'timeout'; });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  await hf.getByText('Código: timeout', { exact: true }).waitFor();
  assert.equal(await hf.getByText('Último estado conhecido: Escuta desligada.', { exact: true }).count(), 1);
  assert.equal(await hf.getByText('Último modelo informado: Baseline', { exact: true }).count(), 1, 'stale identity is presented as last known rather than active');
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
  // Consent is separate from ordinary hands-free; querying or a denied result
  // never retries projection authorization automatically.
  await page.evaluate(() => {
    Object.assign(window.hfFixture.initial, {
      enabled: true, modelReady: true, assistantSelected: true, permissionGranted: true,
      mediaReference: { supported: true, session: false, status: 'off' }
    });
  });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  const mediaReference = hf.locator('.media-reference-settings');
  const authorize = mediaReference.getByRole('button', { name: 'Autorizar áudio de mídia', exact: true });
  await authorize.waitFor();
  assert.equal(await authorize.isEnabled(), true);
  assert.equal(await page.evaluate(() => window.hfFixture.calls.filter(call => call.operation === 'mediaReferenceStart').length), 0);
  await authorize.click();
  await mediaReference.getByText('Autorização não concedida.', { exact: true }).waitFor();
  assert.equal(await authorize.isEnabled(), true);
  assert.equal(await page.evaluate(() => window.hfFixture.calls.filter(call => call.operation === 'mediaReferenceStart').length), 1);
  await page.evaluate(() => { window.hfFixture.mediaGrant = true; });
  await authorize.click();
  await mediaReference.getByText('Referência de mídia disponível.', { exact: true }).waitFor();
  const stopReference = mediaReference.getByRole('button', { name: 'Desativar referência de mídia', exact: true });
  assert.equal(await stopReference.isEnabled(), true);
  await stopReference.click();
  await mediaReference.getByText('Desativada.', { exact: true }).waitFor();
  assert.deepEqual(await page.evaluate(() => window.hfFixture.calls.filter(call => call.operation.startsWith('mediaReference'))), [
    { operation: 'mediaReferenceStart' }, { operation: 'mediaReferenceStart' }, { operation: 'mediaReferenceStop' }
  ]);
  await page.evaluate(() => {
    window.hfFixture.initial.enabled = false;
    window.hfFixture.initial.mediaReference = { supported: false, session: false, status: 'unsupported' };
  });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  await mediaReference.getByText('Disponível a partir do Android 10.', { exact: true }).waitFor();
  assert.equal(await authorize.count(), 0);
  await page.evaluate(() => { delete window.hfFixture.initial.mediaReference; });

  // Battery study uses the real Vue controls with a fixture of the closed native contract.
  await page.evaluate(() => {
    window.hfFixture.initial.batteryDiagnostics = { schema: 1, active: null, requiresResume: false, latest: null, runs: [] };
    Object.assign(window.hfFixture.initial, { assistantSelected: false, permissionGranted: false, modelReady: false });
  });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  const battery = hf.locator('.battery-settings');
  for (const name of ['A · Escuta desligada', 'B · Somente microfone', 'C · Detecção local (sem comandos)']) assert.equal(await battery.getByRole('button', { name, exact: true }).isEnabled(), false);
  await battery.getByText('Selecione a Aegis como assistente no Android antes de iniciar os testes.', { exact: true }).waitFor();
  await page.evaluate(() => { window.hfFixture.initial.assistantSelected = true; });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  assert.equal(await battery.getByRole('button', { name: 'A · Escuta desligada', exact: true }).isEnabled(), true);
  assert.equal(await battery.getByRole('button', { name: 'B · Somente microfone', exact: true }).isEnabled(), false);
  await page.evaluate(() => { window.hfFixture.initial.permissionGranted = true; });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  assert.equal(await battery.getByRole('button', { name: 'B · Somente microfone', exact: true }).isEnabled(), true);
  assert.equal(await battery.getByRole('button', { name: 'C · Detecção local (sem comandos)', exact: true }).isEnabled(), false);
  await page.evaluate(() => { window.hfFixture.initial.modelReady = true; });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  assert.equal(await battery.getByRole('button', { name: 'C · Detecção local (sem comandos)', exact: true }).isEnabled(), true);
  await battery.getByRole('button', { name: 'B · Somente microfone', exact: true }).click();
  await battery.getByText('Em teste: B · Somente microfone · 1 min decorridos.', { exact: true }).waitFor();
  assert.equal(await hf.getByRole('checkbox', { name: 'Escutar “Aegis”', exact: true }).isEnabled(), false);
  assert.equal(await hf.getByRole('button', { name: 'Importar modelo novamente', exact: true }).isEnabled(), false);
  assert.equal(await hf.getByRole('button', { name: 'Exportar métricas e ativações (ZIP)', exact: true }).isEnabled(), false);
  assert.match(await battery.innerText(), /nenhum áudio é salvo ou enviado/);
  assert.match(await battery.innerText(), /encerrar manualmente/);
  assert.match(await battery.innerText(), /Aegis não responde/);
  assert.equal(await battery.getByRole('button', { name: 'Exportar relatório de bateria (sem áudio)', exact: true }).isEnabled(), false);
  const startCall = await page.evaluate(() => window.hfFixture.calls.find(c => c.operation === 'batteryStart'));
  assert.deepEqual(startCall, { operation: 'batteryStart', batteryMode: 'capture_only' });
  await page.evaluate(() => { Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true }); });
  const hiddenCalls = await page.evaluate(() => window.hfFixture.calls.length);
  await page.waitForTimeout(5500);
  assert.equal(await page.evaluate(() => window.hfFixture.calls.length), hiddenCalls, 'hidden study UI performs no polling');
  await page.evaluate(() => { Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true }); });
  await page.waitForTimeout(5500);
  assert((await page.evaluate(() => window.hfFixture.calls.length)) > hiddenCalls, 'visible active study reuses the five-second poll');
  const evidence = process.env.AEGIS_BATTERY_UI_EVIDENCE;
  if (evidence) { mkdirSync(evidence, { recursive: true }); await battery.locator('legend').scrollIntoViewIfNeeded(); await page.screenshot({ path: `${evidence}/battery-active-mobile.png` }); }
  await battery.getByRole('button', { name: 'Encerrar teste e voltar às configurações anteriores', exact: true }).click();
  await battery.getByRole('button', { name: 'A · Escuta desligada', exact: true }).waitFor();
  assert.equal(await hf.getByRole('checkbox', { name: 'Escutar “Aegis”', exact: true }).isEnabled(), true);
  await page.evaluate(() => {
    window.hfFixture.initial.batteryDiagnostics.runs = ['off', 'capture_only', 'detection'].map((mode, i) => ({
      id: `result_${i}`, comparisonKey: 'a'.repeat(64), mode, status: 'completed', startedAt: '2026-11-16T10:00:00-03:00', endedAt: '2026-11-16T10:30:00-03:00', durationMs: 1800000,
      eligible: true, issues: [], summary: { phoneChargeMah: [15, 10, 12][i], phoneDischargeMahPerHour: [30, 20, 24][i], phoneEnergyMwh: 50, batteryPercentPoints: 2, awakeMs: 120000, screenOnMs: 0, processCpuMs: 400,
        captureThreadCpuMs: i ? 300 : 0, detectorCpuMs: i === 2 ? 200 : 0, verifierCpuMs: i === 2 ? 30 : 0 }
    }));
  });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  await battery.getByText('-10 mAh/h', { exact: true }).waitFor();
  await battery.getByText('+4 mAh/h', { exact: true }).waitFor();
  assert.match(await battery.innerText(), /aparelho inteiro/);
  await battery.locator('.battery-history > summary').click();
  assert.match(await battery.innerText(), /CPU é tempo de trabalho medido/);
  assert.equal(await battery.getByText('50 mWh', { exact: true }).count(), 3);
  assert.equal(await battery.getByText('2 p.p.', { exact: true }).count(), 3);
  assert.equal(await battery.getByText('120.000 ms', { exact: true }).count(), 3);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'mobile page retains its viewport');
  if (evidence) {
    await battery.locator('caption').scrollIntoViewIfNeeded(); await page.screenshot({ path: `${evidence}/battery-results-mobile.png` });
    await battery.locator('.battery-result').first().scrollIntoViewIfNeeded(); await page.screenshot({ path: `${evidence}/battery-cpu-mobile.png` });
    await page.setViewportSize({ width: 1280, height: 1000 });
    await battery.locator('caption').scrollIntoViewIfNeeded(); await page.screenshot({ path: `${evidence}/battery-results-desktop.png` });
  }
  await page.evaluate(() => { window.hfFixture.initial.batteryDiagnostics.requiresResume = true; });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  await battery.getByText('O teste foi interrompido. Encerre para voltar às configurações anteriores.', { exact: true }).waitFor();
  await battery.getByRole('button', { name: 'Exportar relatório de bateria (sem áudio)', exact: true }).click();
  assert.equal(await page.evaluate(() => window.hfFixture.calls.at(-1).operation), 'batteryExport');
  assert.equal(await hf.getByRole('checkbox', { name: 'Escutar “Aegis”', exact: true }).isEnabled(), false);
  await battery.getByRole('button', { name: 'Encerrar teste e voltar às configurações anteriores', exact: true }).click();
  await battery.getByRole('button', { name: 'A · Escuta desligada', exact: true }).waitFor();
  // A single native sequence owns scheduling, including the wait before the first audio run.
  await page.setViewportSize({ width: 390, height: 844 });
  await page.evaluate(() => { window.hfFixture.initial.batteryDiagnostics.automation = null; });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  const primary = battery.getByRole('button', { name: 'Iniciar comparação automática', exact: true });
  await primary.waitFor(); assert.equal(await primary.isEnabled(), true);
  assert.match(await battery.innerText(), /cerca de 1h30, além da preparação entre etapas/);
  assert.match(await battery.innerText(), /carga entre 50% e 80%/);
  assert.equal(await battery.locator('.battery-advanced').getAttribute('open'), null, 'advanced controls start collapsed');
  await primary.click();
  await battery.getByText('Comparação preparada. Apague a tela para começar a etapa A.', { exact: true }).waitFor();
  assert.equal(await page.evaluate(() => window.hfFixture.initial.batteryDiagnostics.active), null, 'arming does not invent an audio run');
  assert.equal(await hf.getByRole('checkbox', { name: 'Escutar “Aegis”', exact: true }).isEnabled(), false);
  assert.equal(await battery.getByRole('button', { name: 'Exportar relatório de bateria (sem áudio)', exact: true }).isEnabled(), false);
  await page.evaluate(() => { Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true }); });
  const armedHiddenCalls = await page.evaluate(() => window.hfFixture.calls.length);
  await page.waitForTimeout(5500);
  assert.equal(await page.evaluate(() => window.hfFixture.calls.length), armedHiddenCalls, 'armed sequence never polls a hidden UI');
  await page.evaluate(() => { Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true }); });
  await page.waitForTimeout(5500);
  assert((await page.evaluate(() => window.hfFixture.calls.length)) > armedHiddenCalls, 'armed sequence polls visibly even with no active audio run');
  if (evidence) { await battery.locator('legend').scrollIntoViewIfNeeded(); await page.screenshot({ path: `${evidence}/battery-auto-waiting-mobile.png` }); }
  await battery.getByRole('button', { name: 'Cancelar comparação', exact: true }).click();
  await battery.getByText('Comparação cancelada. A Aegis voltou às configurações anteriores.', { exact: true }).waitFor();
  assert.equal(await hf.getByRole('checkbox', { name: 'Escutar “Aegis”', exact: true }).isEnabled(), true);
  await battery.locator('.battery-advanced > summary').click();
  await battery.getByRole('button', { name: 'Verificar automação (sem medir consumo)', exact: true }).click();
  await battery.getByText('Verificação preparada. Apague a tela para começar a etapa A.', { exact: true }).waitFor();
  assert.match(await battery.innerText(), /Estes resultados não medem consumo de bateria/);
  const automaticCalls = await page.evaluate(() => window.hfFixture.calls.filter(c => c.operation.startsWith('batteryAuto')));
  assert.deepEqual(automaticCalls, [{ operation: 'batteryAutoStart' }, { operation: 'batteryAutoCancel' }, { operation: 'batteryAutoValidate' }]);
  await page.evaluate(() => { Object.assign(window.hfFixture.initial.batteryDiagnostics.automation, { status: 'transitioning', mode: 'capture_only', remainingEstimateMs: 120000, preparationElapsedMs: 80000, delayMs: 27 }); });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  await battery.getByText('Preparando B · Somente microfone.', { exact: true }).waitFor();
  assert.match(await battery.innerText(), /A preparação fica fora da medição. A janela de 1 min ainda não começou/);
  assert.match(await battery.innerText(), /Preparação decorrida: 80 s/);
  assert.doesNotMatch(await battery.innerText(), /Previsão:|Houve atraso na troca de etapa/);
  await page.evaluate(() => { window.hfFixture.initial.batteryDiagnostics.automation.delayMs = 4999; });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  assert.doesNotMatch(await battery.innerText(), /Houve atraso na troca de etapa/, 'sub-five-second scheduling jitter has no prominent warning');
  await page.evaluate(() => { window.hfFixture.initial.batteryDiagnostics.automation.delayMs = 5000; });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  assert.match(await battery.innerText(), /Houve atraso na troca de etapa/);
  if (evidence) { await battery.locator('legend').scrollIntoViewIfNeeded(); await page.screenshot({ path: `${evidence}/battery-auto-preparing-mobile.png` }); }
  await page.evaluate(() => { Object.assign(window.hfFixture.initial.batteryDiagnostics.automation, { status: 'completed', mode: null, remainingEstimateMs: 0, runIds: ['result_2'] }); });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  await battery.getByText('Verificação concluída. A Aegis voltou às configurações anteriores.', { exact: true }).waitFor();
  assert.equal(await battery.locator('table').count(), 0, 'functional checks never show device energy comparisons');
  await page.evaluate(() => {
    const d = window.hfFixture.initial.batteryDiagnostics;
    for (const row of d.runs) { row.endedAt = '2026-11-16T14:00:00-03:00'; row.summary.phoneDischargeMahPerHour = 999; }
    const production = ['off', 'capture_only', 'detection'].map((mode, i) => ({ ...d.runs[i], id: `auto_result_${i}`, preparationMs: [0,80000,850][i], measurementStartBasis: i === 0 ? 'capture_disabled' : 'first_positive_pcm', startedAt: '2026-11-16T11:00:00-03:00', endedAt: '2026-11-16T11:30:00-03:00', summary: { ...d.runs[i].summary, phoneDischargeMahPerHour: [30,20,24][i] } }));
    d.runs.push(...production);
    d.automation = { ...d.automation, validation: false, stageDurationMs: 1800000, runIds: production.map(r => r.id), delayMs: 0 };
  });
  await hf.getByRole('button', { name: 'Atualizar estado', exact: true }).click();
  await battery.getByText('Comparação concluída. A Aegis voltou às configurações anteriores.', { exact: true }).waitFor();
  await battery.getByText('-10 mAh/h', { exact: true }).waitFor(); await battery.getByText('+4 mAh/h', { exact: true }).waitFor();
  assert.equal(await battery.locator('table').getByText('999 mAh/h', { exact: true }).count(), 0, 'results use this sequence runIds rather than newer records from another run');
  assert.match(await battery.innerText(), /Preparação: 80 s, fora da janela de medição/);
  assert.match(await battery.innerText(), /A medição começou após a confirmação de áudio do microfone/);
  assert.match(await battery.innerText(), /A medição começou após confirmar a captura desligada/);
  assert.match(await battery.innerText(), /Este registro não informa o marco de início da medição/);
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
  if (evidence) {
    await battery.locator('caption').scrollIntoViewIfNeeded(); await page.screenshot({ path: `${evidence}/battery-auto-results-mobile.png` });
    await page.setViewportSize({ width: 1280, height: 1000 });
    await battery.locator('caption').scrollIntoViewIfNeeded(); await page.screenshot({ path: `${evidence}/battery-auto-results-desktop.png` });
  }
  console.log('Automatic battery UI: one-click arm, hidden/visible polling, cancellation, functional validation, delay feedback and isolated sequence results PASS (mock IPC)');
  console.log('Battery UI: three modes, explicit silent study, active/recovery controls, manual finish, visible-only polling and signed device comparisons PASS (mock IPC)');
  console.log('Hands-free UI: initial failure, accessible retry, setup without prerequisites/backend, stale state, recovery, serialized events and unmount PASS (mock IPC; no native ACL claim)');
  const desktop = await context.newPage();
  const waveformConversation = { id: 'ddfabbe3-d5b7-4001-9000-123456789abc', title: 'Conversa para medir o layout',
    createdAt: '2026-11-25T00:00:00Z', updatedAt: '2026-11-25T00:00:00Z', messageCount: 0, messages: [] };
  await desktop.route('**/api/chat/conversations?*', route => route.fulfill({
    json: { items: [waveformConversation], hasMore: false }
  }));
  await desktop.route(`**/api/chat/conversations/${waveformConversation.id}`, route => route.fulfill({ json: waveformConversation }));
  await desktop.goto(origin + '?windows');
  const indicator = desktop.locator('.composer .desktop-voice-indicator');
  await desktop.waitForFunction(() => window.hfFixture.calls.some(c => c.operation === 'state'));
  assert.equal(await indicator.count(), 0, 'idle desktop has no visual indicator');
  const historyTrigger = desktop.getByRole('button', { name: 'Abrir histórico', exact: true });
  await historyTrigger.focus();
  const composerCard = desktop.locator('.composer .desktop-voice-status');
  const input = desktop.locator('.composer textarea');
  await input.fill('Rascunho preservado');
  await historyTrigger.focus();
  let narrowPhaseBounds;
  for (const [phase, label] of Object.entries({
    capturing: 'Escutando', processing: 'Preparando resposta', speaking: 'Falando', continuation: 'Aguardando você'
  })) {
    await desktop.evaluate(status => window.hfFixture.voiceEvent(status), phase);
    await indicator.and(desktop.locator(`[data-phase="${phase}"]`)).waitFor();
    assert.equal((await indicator.locator('.sr-only').textContent()).trim(), label);
    assert.equal(await input.count(), 0);
    assert.equal(await desktop.locator('.chat-header .desktop-voice-indicator').count(), 0);
    assert.ok(await composerCard.getByRole('button', { name: 'Cancelar interação' }).isVisible());
    assert.equal(await indicator.locator('.sr-only').evaluate(el => getComputedStyle(el).width), '1px');
    assert.equal(await historyTrigger.evaluate(button => document.activeElement === button), true, 'voice updates preserve keyboard focus');
    assert.equal(await indicator.locator('svg[aria-hidden="true"]').count(), 1);
    const bounds = await indicator.boundingBox();
    assert.ok(bounds);
    if (narrowPhaseBounds) assert.deepEqual(bounds, narrowPhaseBounds, 'all active phases share one bottom composer');
    narrowPhaseBounds = bounds;
  }
  const sample = Array.from({ length: 64 }, (_, i) =>
    Math.abs(Math.sin(i * .155) * Math.sin(i * .045)) * .92);
  const meter = indicator.locator('.desktop-voice-waveform');
  const bars = meter.locator('rect');
  await desktop.evaluate(levels => { window.hfFixture.voiceEvent('capturing'); window.hfFixture.waveEvent(levels); }, sample);
  await bars.nth(31).waitFor({ state: 'attached' });
  assert.equal(await meter.getAttribute('data-meter'), 'input');
  const heights = await bars.evaluateAll(nodes => nodes.map(node => Number(node.getAttribute('height'))));
  assert.equal(heights.length, 32);
  const compactExpected = values => values.slice(-32).map(level => 3 + Math.sqrt(level) * 49);
  assert.ok(heights.every((height, i) => Math.abs(height - compactExpected(sample)[i]) < 1e-10), 'input bars preserve square-root RMS height curve with increased amplitude and newest time slots');
  await desktop.evaluate(levels => {
    const f = window.hfFixture;
    f.waveEvent(levels.slice(1));
    f.waveEvent(levels.slice(0, 32)); // Output-only raw count is not a valid capture frame.
    f.waveEvent(levels.map((value, i) => i === 0 ? 2 : value));
    f.waveEvent(levels.map((value, i) => i === 0 ? NaN : value));
    f.waveEvent(levels.map(() => .8), f.initial.generation - 1);
  }, sample);
  assert.deepEqual(await bars.evaluateAll(nodes => nodes.map(node => Number(node.getAttribute('height')))), heights,
    'reject invalid frames and previous generations without applying partial data');
  await desktop.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
  assert.equal(await indicator.evaluate(el => getComputedStyle(el).color !== getComputedStyle(el).backgroundColor), true);
  assert.equal(await indicator.evaluate(el => el.getAnimations({ subtree: true }).length), 0);
  assert.equal(await bars.first().evaluate(el => getComputedStyle(el).transitionDuration), '0s');
  const fill = await bars.nth(6).evaluate(bar => getComputedStyle(bar).fill);
  assert.notEqual(fill, 'none');
  await desktop.emulateMedia({ forcedColors: 'none', reducedMotion: 'no-preference' });
  assert.equal(await bars.first().evaluate(el => getComputedStyle(el).transitionDuration), '0.06s, 0.06s');
  await desktop.setViewportSize({ width: 1180, height: 800 });
  await desktop.locator('#conversation-sidebar[aria-hidden="false"]').waitFor({ state: 'attached' });
  const sidebar = desktop.locator('#conversation-sidebar');
  await sidebar.getByRole('button', { name: 'Fechar histórico', exact: true }).click();
  await historyTrigger.waitFor({ state: 'visible' });
  await desktop.evaluate(levels => window.hfFixture.waveEvent(levels), sample);
  const wideIndicator = await indicator.boundingBox();
  const wideMeter = await meter.boundingBox();
  const frame = await desktop.locator('.chat-panel').boundingBox();
  const cardWide = await composerCard.boundingBox();
  assert.ok(wideIndicator && wideMeter && frame && cardWide && wideIndicator.width >= cardWide.width - 120 && wideMeter.width >= cardWide.width - 120,
    'microphone waveform occupies the available horizontal composer instead of a small card');
  assert.equal(wideMeter.height, 64, 'the input waveform grows vertically as well');
  const composerBounds = await composerCard.boundingBox();
  assert.ok(composerBounds && composerBounds.height >= 90 && composerBounds.y > frame.y + frame.height / 2);
  await bars.nth(63).waitFor({ state: 'attached' });
  const wideBars = await bars.evaluateAll(nodes => nodes.map(node => node.getBoundingClientRect()));
  assert.equal(wideBars.length, 64);
  assert.ok(wideBars[63].x - wideBars[0].x > wideMeter.width * .8, 'the 64 measured bars span the meter width');
  assert.equal(await bars.first().evaluate(el => getComputedStyle(el).transitionDuration), '0.06s, 0.06s');
  assert.ok(wideBars.every(bar => Math.abs(bar.width - 3) < .01), 'capture bars are exactly 3 CSS pixels, even in wide layouts');
  assert.equal(await meter.getAttribute('viewBox'), null, 'capture geometry is never scaled by a viewBox');
  const beforePositions = await bars.evaluateAll(nodes => nodes.map(node => node.getAttribute('x')));
  await desktop.evaluate(levels => window.hfFixture.waveEvent(levels.map((_, i) => levels[(i + 4) % levels.length])), sample);
  assert.deepEqual(await bars.evaluateAll(nodes => nodes.map(node => node.getAttribute('x'))), beforePositions, 'only height changes as measured time history advances');
  if (evidence) await desktop.screenshot({ path: `${evidence}/windows-hands-free-capturing.png`, animations: 'disabled' });
  // A real desktop sidebar occupies a column. Opening it, selecting a conversation and
  // starting another one do not hide the active microphone display or block controls.
  await historyTrigger.click();
  await desktop.evaluate(id => window.hfFixture.conversationEvent(id), waveformConversation.id);
  const menuTrigger = desktop.getByRole('button', { name: 'Menu da conversa', exact: true });
  await menuTrigger.waitFor({ state: 'visible' });
  const sidebarBounds = await sidebar.boundingBox();
  const chatBounds = await desktop.locator('.chat-panel').boundingBox();
  const inputBounds = await indicator.boundingBox();
  const menuBounds = await menuTrigger.boundingBox();
  assert.ok(sidebarBounds && chatBounds && inputBounds && menuBounds
    && inputBounds.x >= sidebarBounds.x + sidebarBounds.width
    && inputBounds.y >= menuBounds.y + menuBounds.height
    && inputBounds.width >= (await composerCard.boundingBox()).width - 120,
    'expanded microphone layout stays inside the chat column and leaves the conversation menu free');
  await desktop.evaluate(levels => window.hfFixture.waveEvent(levels), sample);
  if (evidence) await desktop.screenshot({ path: `${evidence}/windows-hands-free-capturing-sidebar.png`, animations: 'disabled' });
  await menuTrigger.click();
  await desktop.getByRole('menuitem', { name: 'Informações', exact: true }).waitFor();
  await desktop.keyboard.press('Escape');
  await desktop.setViewportSize({ width: 320, height: 844 });
  await desktop.locator('#conversation-sidebar[aria-hidden="true"]').waitFor({ state: 'attached' });
  const activeNarrowBounds = await indicator.boundingBox();
  const activeNavBounds = await historyTrigger.boundingBox();
  const activeMenuBounds = await menuTrigger.boundingBox();
  assert.ok(activeNarrowBounds && activeNavBounds && activeMenuBounds
    && activeNarrowBounds.y > activeNavBounds.y + activeNavBounds.height
    && activeNarrowBounds.y > activeMenuBounds.y + activeMenuBounds.height,
    'even a narrow active conversation leaves history and menu visible');
  await desktop.evaluate(levels => window.hfFixture.waveEvent(levels), sample);
  await desktop.waitForFunction(() => document.querySelectorAll('.desktop-voice-waveform rect').length === 32);
  const compactHeights = await bars.evaluateAll(nodes => nodes.map(node => Number(node.getAttribute('height'))));
  const expectedCompact = compactExpected(sample);
  assert.ok(compactHeights.every((height, i) => Math.abs(height - expectedCompact[i]) < 1e-10), 'narrow capture preserves the newest real samples at their 60ms cadence');
  assert.ok(await bars.evaluateAll(nodes => nodes.every(node => Math.abs(node.getBoundingClientRect().width - 3) < .01)),
    'compact capture never changes the actual bar thickness');
  await desktop.setViewportSize({ width: 1180, height: 800 });
  await desktop.locator('#conversation-sidebar[aria-hidden="false"]').waitFor({ state: 'attached' });
  await sidebar.getByRole('button', { name: 'Nova conversa', exact: true }).click();
  await menuTrigger.waitFor({ state: 'detached' });
  await indicator.and(desktop.locator('[data-phase="capturing"]')).waitFor();
  await sidebar.getByRole('button', { name: 'Fechar histórico', exact: true }).click();
  await desktop.setViewportSize({ width: 390, height: 844 });
  await desktop.locator('#conversation-sidebar[aria-hidden="true"]').waitFor({ state: 'attached' });
  await desktop.evaluate(levels => window.hfFixture.waveEvent(levels), sample);
  if (evidence) await desktop.screenshot({ path: `${evidence}/windows-hands-free-narrow.png`, animations: 'disabled' });
  await desktop.setViewportSize({ width: 320, height: 844 });
  const box = await indicator.boundingBox();
  const navBox = await historyTrigger.boundingBox();
  assert.ok(box && navBox && box.y >= navBox.y + navBox.height, 'indicator does not cover navigation in narrow layout');
  const narrowMeter = await meter.boundingBox();
  assert.ok(narrowMeter && box && narrowMeter.width > box.width * .8 && narrowMeter.width >= 100,
    'the composer reserves room for the microphone');
  assert.equal(await desktop.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
  await desktop.setViewportSize({ width: 390, height: 844 });
  await desktop.evaluate(() => window.hfFixture.voiceEvent('continuation'));
  await meter.locator('line').waitFor({ state: 'attached' });
  await desktop.evaluate(levels => {
    const f = window.hfFixture;
    f.waveEvent(levels, f.initial.generation - 1, 'capturing');
  }, sample);
  assert.equal(await bars.count(), 0, 'previous capture cannot appear in continuation');
  await desktop.evaluate(levels => window.hfFixture.waveEvent(levels), sample);
  await bars.nth(31).waitFor({ state: 'attached' });
  await meter.locator('line').waitFor({ state: 'attached', timeout: 2500 });
  assert.equal(await bars.count(), 0, 'stalled meter expires');
  await desktop.evaluate(levels => {
    const f = window.hfFixture; f.waveEvent(levels);
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
    document.dispatchEvent(new Event('visibilitychange'));
    f.waveEvent(levels);
  }, sample);
  await meter.locator('line').waitFor({ state: 'attached' });
  assert.equal(await bars.count(), 0, 'hidden UI clears and rejects data');
  await desktop.evaluate(() => { delete document.visibilityState; document.dispatchEvent(new Event('visibilitychange')); });
  await desktop.waitForFunction(() => window.hfFixture.active === 0);
  // Output bars are acoustic frequency bins, aligned to the bottom with the
  // exact same source generation guard used for microphone history.
  await desktop.evaluate(() => window.hfFixture.voiceEvent('speaking'));
  await meter.locator('line').waitFor({ state: 'attached' });
  assert.equal(await meter.getAttribute('data-meter'), 'output');
  await desktop.evaluate(() => window.hfFixture.waveEvent(Array.from({ length: 32 }, (_, i) => i === 7 ? .95 : i === 23 ? .5 : 0)));
  await bars.nth(31).waitFor({ state: 'attached' });
  assert.equal(await bars.count(), 32, 'desktop shows all 32 measured bands while capture retains 64 samples');
  assert.equal(await meter.getAttribute('viewBox'), null, 'playback geometry is never scaled by a viewBox');
  assert.ok(await bars.evaluateAll(nodes => nodes.every(node => Math.abs(node.getBoundingClientRect().width - 3) < .01)));
  const peak = Number(await bars.nth(7).getAttribute('height'));
  assert.ok(peak > Number(await bars.nth(23).getAttribute('height')));
  assert.ok(peak > 0 && peak < 31, 'first output frame uses the softened attack');
  assert.ok(Math.abs(Number(await bars.nth(7).getAttribute('y')) + peak - 59) < 1e-6);
  await desktop.emulateMedia({ reducedMotion: 'reduce' });
  await desktop.evaluate(() => window.hfFixture.waveEvent(Array.from({ length: 32 }, (_, i) => i === 7 ? 1 : 0)));
  assert.ok(Math.abs(Number(await bars.nth(7).getAttribute('height')) - 52) < 1e-6, 'larger desktop output height cap');
  assert.equal(Number(await bars.nth(23).getAttribute('height')), 0, 'reduced motion bypasses the envelope');
  await desktop.emulateMedia({ reducedMotion: 'no-preference' });
  await desktop.evaluate(() => window.hfFixture.waveEvent(Array(32).fill(0)));
  assert.equal(await bars.count(), 32, 'quiet has a short release rather than a jump');
  await meter.locator('line').waitFor({ state: 'attached', timeout: 300 });
  assert.equal(await bars.count(), 0, 'a final quiet frame clears within the short release even without more events');
  await desktop.evaluate(() => window.hfFixture.waveEvent(Array.from({ length: 32 }, (_, i) => i === 23 ? .5 : 0)));
  await bars.nth(31).waitFor({ state: 'attached' });
  assert.equal(Number(await bars.nth(7).getAttribute('height')), 0, 'previous peak cannot leak after silence');
  await meter.locator('line').waitFor({ state: 'attached', timeout: 1500 });
  assert.equal(await bars.count(), 0, 'stalled playback clears independently of capture timeout');
  await desktop.evaluate(() => window.hfFixture.waveEvent(Array.from({ length: 32 }, (_, i) => i === 7 ? .95 : i === 23 ? .5 : 0)));
  await bars.nth(31).waitFor({ state: 'attached' });
  await desktop.setViewportSize({ width: 1180, height: 800 });
  if (evidence) await desktop.screenshot({ path: `${evidence}/windows-hands-free-speaking.png` });
  const meterSize = await indicator.boundingBox();
  await desktop.evaluate(() => window.hfFixture.voiceEvent('processing'));
  await indicator.and(desktop.locator('[data-phase="processing"]')).waitFor();
  const thoughtSize = await indicator.boundingBox();
  assert.ok(meterSize && thoughtSize && Math.abs(meterSize.width - thoughtSize.width) <= 1 &&
    Math.abs(meterSize.height - thoughtSize.height) <= 1, 'the visualizer retains its position and size between phases');
  assert.equal((await indicator.locator('.sr-only').textContent()).trim(), 'Preparando resposta');
  const thoughts = indicator.locator('.desktop-voice-thoughts');
  assert.equal(await thoughts.locator('circle').count(), 3);
  const clusterPositions = await thoughts.evaluate(g => {
    const points = Array.from(g.querySelectorAll('circle'));
    const animations = g.getAnimations({ subtree: true });
    const sample = time => {
      for (const animation of animations) { animation.pause(); animation.currentTime = time; }
      return points.map(point => {
        const m = new DOMMatrix(getComputedStyle(point).transform);
        return [m.e, m.f];
      });
    };
    return {
      animations: animations.length,
      durations: animations.map(animation => animation.effect.getTiming().duration),
      start: sample(0),
      vertical: sample(400),
      rotated: sample(3200),
      nearEnd: sample(10333),
      nextLoop: sample(10334),
    };
  });
  assert.equal(clusterPositions.animations, 3);
  assert.deepEqual(clusterPositions.durations, [10334, 10334, 10334],
    'thinking motion uses the approved shortened loop duration');
  const distance = (a, b) => Math.hypot(a[0] - b[0], a[1] - b[1]);
  const nearPositions = (a, b, tolerance) => a.every((point, i) => distance(point, b[i]) < tolerance);
  assert.ok(nearPositions(clusterPositions.start, [[-6.6, 3.744], [0, -2.963], [6.6, -5]], 0.05),
    'thinking motion starts at the approved first frame');
  assert.ok(clusterPositions.start.every(([x], i) => Math.abs(x - clusterPositions.vertical[i][0]) < .1),
    'the first phase keeps horizontal positions while the dots rise and fall');
  assert.ok(!nearPositions(clusterPositions.start, clusterPositions.vertical, 0.5),
    'the vertical phase actually moves the dots');
  assert.ok(clusterPositions.start.some(([x], i) => Math.abs(x - clusterPositions.rotated[i][0]) > 2),
    'the exchange phase moves the dots through new horizontal positions');
  assert.ok(nearPositions(clusterPositions.nearEnd, clusterPositions.start, 0.2),
    'the loop closes without a visible jump');
  assert.ok(nearPositions(clusterPositions.nextLoop, clusterPositions.start, 0.05),
    'the next iteration restarts at the same formation');
  if (evidence) await desktop.screenshot({ path: `${evidence}/windows-hands-free-processing.png` });
  await desktop.emulateMedia({ reducedMotion: 'reduce' });
  assert.equal(await thoughts.evaluate(g => g.getAnimations({ subtree: true }).length), 0);
  const staticPoints = await thoughts.locator('circle').evaluateAll(points => points.map(point => {
    const m = new DOMMatrix(getComputedStyle(point).transform);
    return [m.e, m.f];
  }));
  assert.ok(nearPositions(staticPoints, clusterPositions.start, 0.05),
    'reduced motion shows the static first formation');
  await desktop.emulateMedia({ reducedMotion: 'no-preference' });
  await desktop.setViewportSize({ width: 390, height: 844 });
  // Visual evidence and a geometric invariant in both sizes: every active state
  // occupies the exact same composer; only the central content changes. This also
  // verifies the output bars are never stretched by a wide viewport.
  const speechSample = Array.from({ length: 32 }, (_, i) => .05 + .85 * Math.abs(Math.sin(i * .51) * Math.cos(i * .16)));
  for (const width of [1180, 390]) {
    await desktop.setViewportSize({ width, height: 844 });
    if (width > 900) {
      await desktop.locator('#conversation-sidebar[aria-hidden="false"]').waitFor({ state: 'attached' });
      await sidebar.getByRole('button', { name: 'Fechar histórico', exact: true }).click();
    }
    let phaseBounds;
    for (const phase of ['capturing', 'processing', 'speaking', 'continuation']) {
      await desktop.evaluate(status => window.hfFixture.voiceEvent(status), phase);
      await indicator.and(desktop.locator(`[data-phase="${phase}"]`)).waitFor();
      const bounds = await indicator.boundingBox();
      assert.ok(bounds);
      if (phaseBounds) assert.deepEqual(bounds, phaseBounds, 'every active phase keeps the exact same composer geometry');
      phaseBounds = bounds;
      if (phase === 'processing') {
        const shape = await indicator.locator('.desktop-voice-state-icon').boundingBox();
        assert.ok(shape && shape.width === 34 && shape.height === 34, 'the approved thinking SVG keeps its fixed dimensions');
      } else {
        const frames = phase === 'speaking' ? 9 : 1;
        for (let frame = 0; frame < frames; frame++) {
          await desktop.evaluate(levels => window.hfFixture.waveEvent(levels), phase === 'speaking' ? speechSample : sample);
          if (frames > 1 && frame + 1 < frames) await desktop.waitForTimeout(40);
        }
        await bars.first().waitFor({ state: 'attached' });
        if (phase === 'speaking') {
          const spectrumBox = await meter.boundingBox();
          const visualBox = await indicator.locator('.desktop-voice-visual').boundingBox();
          assert.ok(spectrumBox && visualBox && Math.abs(spectrumBox.width - Math.min(460, visualBox.width)) < .1,
            'thirty-two frequency bands remain legible in a bounded visual area');
          assert.ok(Math.abs(spectrumBox.x + spectrumBox.width / 2 - visualBox.x - visualBox.width / 2) < .1,
            'the bounded spectrum is centered without shrinking the composer');
          const currentHeights = await bars.evaluateAll(nodes => nodes.map(node => Number(node.getAttribute('height'))));
          assert.ok(Math.max(...currentHeights) > 15, 'the visual fixture represents ongoing speech, not just the first attack frame');
        }
        const widths = await bars.evaluateAll(nodes => nodes.map(node => node.getBoundingClientRect().width));
        assert.ok(widths.every(value => Math.abs(value - 3) < .01));
        assert.equal(widths.length, phase === 'speaking' || width === 390 ? 32 : 64);
      }
      if (evidence) await desktop.screenshot({ path: `${evidence}/windows-voice-${width === 1180 ? 'wide' : 'narrow'}-${phase}.png`, animations: 'disabled' });
    }
  }
  console.log('Windows voice meters: 64 capture levels, 32 smoothed output bands, fixed bar widths, stable bottom composer, responsive layout and reduced motion PASS');
  await desktop.evaluate(() => { window.hfFixture.hold = true; document.dispatchEvent(new Event('visibilitychange')); });
  await desktop.waitForFunction(() => typeof window.hfFixture.resolve === 'function');
  await desktop.evaluate(() => {
    window.hfFixture.voiceEvent('speaking');
    window.hfFixture.resolve({ ...window.hfFixture.initial, status: 'capturing' });
  });
  await indicator.and(desktop.locator('[data-phase="speaking"]')).waitFor();
  assert.equal((await indicator.locator('.sr-only').textContent()).trim(), 'Falando');
  for (const status of ['listening', 'diagnostic', 'unexpected']) {
    await desktop.evaluate(state => window.hfFixture.voiceEvent(state), status);
    await indicator.waitFor({ state: 'detached' });
  }
  assert.equal(await page.locator('.desktop-voice-status').count(), 0, 'Android keeps its native presentation');
  await input.waitFor({ state: 'visible' });
  assert.equal(await input.inputValue(), 'Rascunho preservado', 'the unsubmitted draft returns when the hands-free interaction ends');
  console.log('Windows voice indicator: four states, idle and unknown hidden, focus, forced colors, recovery race PASS');
  await desktop.getByRole('button', { name: 'Abrir histórico', exact: true }).click();
  await desktop.getByRole('button', { name: 'Configurações da Aegis', exact: true }).click();
  await desktop.getByRole('button', { name: 'Áudio e voz', exact: true }).click();
  const voice = desktop.locator('.hands-free-settings');
  await voice.getByRole('button', { name: 'Importar modelo privado', exact: true }).click();
  await voice.getByText('Modelo instalado: Aegis Open v2', { exact: true }).waitFor();
  await voice.getByRole('checkbox', { name: 'Escutar “Aegis”', exact: true }).check();
  assert.equal(await voice.getByRole('checkbox', { name: 'Escutar “Aegis”', exact: true }).isChecked(), true);
  assert.equal(await voice.getByRole('button', { name: 'Selecionar assistente no Android' }).count(), 0);
  assert.equal(await voice.getByRole('button', { name: 'Exportar métricas e ativações (ZIP)' }).count(), 0);
  console.log('Windows settings: model import, opt-in and platform controls PASS');
  await context.close();
} finally { await browser.close(); await server.close(); }
