import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync, mkdirSync } from 'node:fs';

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
    const windows = new URLSearchParams(location.search).has('windows');
    if (windows) { initial.assistantSelected = true; initial.permissionGranted = true; }
    window.hfFixture = { fail: windows ? null : 'command_not_authorized', calls: [], initial, queue: [], active: 0, peak: 0 };
    const callbacks = new Map();
    const events = new Map();
    let nextId = 1;
    window.hfFixture.voiceEvent = status => {
      for (const [id, name] of events) {
        if (name === 'aegis-windows-voice-state') callbacks.get(id)({ id, event: name, payload: { status } });
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
  await desktop.goto(origin + '?windows');
  const indicator = desktop.locator('.chat-header .desktop-voice-indicator');
  await desktop.waitForFunction(() => window.hfFixture.calls.some(c => c.operation === 'state'));
  assert.equal(await indicator.count(), 0, 'idle desktop has no visual indicator');
  const historyTrigger = desktop.getByRole('button', { name: 'Abrir histórico', exact: true });
  await historyTrigger.focus();
  for (const [phase, label] of Object.entries({
    capturing: 'Escutando', processing: 'Preparando resposta', speaking: 'Falando', continuation: 'Aguardando você'
  })) {
    await desktop.evaluate(status => window.hfFixture.voiceEvent(status), phase);
    await indicator.and(desktop.getByText(label, { exact: true })).waitFor();
    assert.equal(await historyTrigger.evaluate(button => document.activeElement === button), true, 'voice updates preserve keyboard focus');
    assert.equal(await indicator.locator('svg[aria-hidden="true"]').count(), 1);
  }
  await desktop.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
  await desktop.evaluate(() => window.hfFixture.voiceEvent('capturing'));
  await indicator.and(desktop.getByText('Escutando', { exact: true })).waitFor();
  assert.equal(await indicator.evaluate(el => getComputedStyle(el).color !== getComputedStyle(el).backgroundColor), true);
  assert.equal(await indicator.evaluate(el => el.getAnimations({ subtree: true }).length), 0);
  await desktop.emulateMedia({ forcedColors: 'none', reducedMotion: 'no-preference' });
  await desktop.setViewportSize({ width: 1180, height: 800 });
  if (evidence) await desktop.screenshot({ path: `${evidence}/windows-hands-free-capturing.png` });
  await desktop.setViewportSize({ width: 390, height: 844 });
  await desktop.evaluate(() => { window.hfFixture.hold = true; document.dispatchEvent(new Event('visibilitychange')); });
  await desktop.waitForFunction(() => typeof window.hfFixture.resolve === 'function');
  await desktop.evaluate(() => {
    window.hfFixture.voiceEvent('speaking');
    window.hfFixture.resolve({ ...window.hfFixture.initial, status: 'capturing' });
  });
  await indicator.and(desktop.getByText('Falando', { exact: true })).waitFor();
  for (const status of ['listening', 'diagnostic', 'unexpected']) {
    await desktop.evaluate(state => window.hfFixture.voiceEvent(state), status);
    await indicator.waitFor({ state: 'detached' });
  }
  assert.equal(await page.locator('.desktop-voice-status').count(), 0, 'Android keeps its native presentation');
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
