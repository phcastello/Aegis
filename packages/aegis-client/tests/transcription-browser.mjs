import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

// Browser integration of both real Apps and the browser's MediaRecorder/WebAudio.
// getUserMedia is replaced with an oscillator/silence destination. No physical
// capture, native device claim, outbound backend request or paid STT is involved.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const requireNative = createRequire(join(root, 'apps/aegis-node/package.json'));
const { createServer } = await import(requireNative.resolve('vite'));
const version = JSON.parse(readFileSync(join(root, 'apps/aegis-node/package.json'), 'utf8')).version;
const servers = [];
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
try {
  for (const host of ['web', 'native']) {
    const server = await createServer({ root: join(root, host === 'web' ? 'frontend/aegis-pwa' : 'apps/aegis-node'), server: { host: '127.0.0.1', port: 0, strictPort: false } });
    await server.listen(); servers.push(server);
    const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
    for (const platform of host === 'native' ? ['android', 'windows'] : ['browser']) {
      const context = await browser.newContext({ viewport: { width: 1100, height: 900 } });
      let uploads = 0;
      await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        let body;
        if (path === '/api/health') body = { status: 'ok' };
        else if (path === '/api/voice/status') body = { enabled: false, available: false };
        else if (path === '/api/voice/transcription/status') body = { enabled: true, configured: true, maxRecordingSeconds: 90 };
        else if (path === '/api/voice/transcriptions') { uploads++; body = { text: 'Texto sintético' }; }
        else if (path === '/api/email/status') body = { isConnected: false };
        else if (path === '/api/chat/proactive-activity') body = [];
        else if (path === '/api/chat/conversations') body = { items: [], hasMore: false };
        else if (path === '/api/notifications/configuration') body = { enabled: false };
        else body = {};
        await route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
      });
      await context.addInitScript(({ version, platform }) => {
        localStorage.setItem('aegis.voice.autoSpeak', 'false');
        window.syntheticAudio = { silence: false, captures: [], constraints: [], decodes: [] };
        const originalDecode = AudioContext.prototype.decodeAudioData;
        AudioContext.prototype.decodeAudioData = async function(...args) {
          try {
            const result = await originalDecode.apply(this, args);
            let peak = 0;
            for (let c = 0; c < result.numberOfChannels; c++) for (const sample of result.getChannelData(c)) peak = Math.max(peak, Math.abs(sample));
            window.syntheticAudio.decodes.push({ peak, samples: result.length });
            return result;
          } catch(error) { window.syntheticAudio.decodes.push({error:error.name}); throw error; }
        };
        const devices = [
          { kind: 'audioinput', deviceId: 'synthetic-a', label: 'Entrada sintética A', groupId: 'synthetic' },
          { kind: 'audioinput', deviceId: 'synthetic-b', label: 'Entrada sintética B', groupId: 'synthetic' }
        ];
        Object.defineProperty(navigator.mediaDevices, 'enumerateDevices', { value: async () => devices });
        Object.defineProperty(navigator.mediaDevices, 'getUserMedia', { value: async constraints => {
          window.syntheticAudio.constraints.push(constraints);
          const context = new AudioContext(); await context.resume();
          const oscillator = context.createOscillator(), gain = context.createGain(), destination = context.createMediaStreamDestination();
          gain.gain.value = window.syntheticAudio.silence ? 0 : 0.005;
          oscillator.connect(gain); gain.connect(destination); oscillator.start();
          const track = destination.stream.getAudioTracks()[0], originalStop = track.stop.bind(track);
          track.stop = () => { originalStop(); oscillator.stop(); void context.close(); };
          window.syntheticAudio.captures.push({ context, stream: destination.stream });
          return destination.stream;
        } });
        if (platform !== 'browser') window.__TAURI_INTERNALS__ = { invoke: async command => {
          if (command === 'runtime_info') return { platform, backendUrl: location.origin, version };
          if (command === 'check_backend') return { status: 'connected', message: null };
          if (command === 'node_status') return { state: 'unpaired', node: null, error: null };
          if (command === 'node_notification_settings') return { granted: true, pendingActions: 0, failedActions: 0, actionDiagnostics: [], actionDiagnosticsAvailable: true };
          if (command === 'node_transport_status') return { transportState: 'online' };
          if (command === 'node_consume_reminder_open') return { reminderId: null };
          if (command === 'node_list') return [];
          if (command === 'check_android_update' || command === 'plugin:updater|check') return null;
          if (command === 'node_settings_open') return;
          throw Error('Unavailable synthetic native command');
        } };
      }, { version, platform });
      const page = await context.newPage();
      try {
        await page.goto(origin);
        const record = page.getByRole('button', { name: 'Gravar mensagem de voz', exact: true });
        await record.waitFor(); await page.waitForFunction(() => !document.querySelector('.record-button')?.disabled);
        assert.equal(await page.evaluate(() => window.syntheticAudio.captures.length), 0, 'idle never captures');
        await record.click();
        await page.waitForFunction(() => document.querySelector('meter')?.value > 0);
        await page.getByRole('button', { name: 'Descartar gravação', exact: true }).click();
        await page.waitForFunction(() => window.syntheticAudio.captures.every(input => input.stream.getTracks().every(track => track.readyState === 'ended')));
        assert.equal(uploads, 0, 'discard never uploads');
        await record.click(); await page.waitForTimeout(250);
        await page.getByRole('button', { name: 'Encerrar gravação', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.composer textarea')?.value === 'Texto sintético');
        assert.equal(uploads, 1, 'quiet synthetic signal uploaded once');
        await page.locator('.composer textarea').fill('');
        await page.evaluate(() => { window.syntheticAudio.silence = true; });
        await record.click(); await page.waitForTimeout(350);
        await page.getByRole('button', { name: 'Encerrar gravação', exact: true }).click();
        await page.getByText('A gravação contém apenas silêncio digital.', { exact: false }).waitFor();
        assert.equal(uploads, 1, 'digital silence never uploaded');
        const decoded = await page.evaluate(() => window.syntheticAudio.decodes);
        assert.equal(decoded.length, 2, 'both complete recordings decoded in Chromium');
        assert.ok(decoded[0].peak > 0.001 && decoded[0].peak < 0.01, 'quiet synthetic signal preserved');
        assert.ok(decoded[1].peak <= 1e-30, 'digital-zero codec residue far below PCM resolution');
        await page.getByRole('button', { name: 'Configurações da Aegis', exact: true }).click();
        const settings = page.getByRole('dialog', { name: 'Configurações', exact: true });
        await settings.getByRole('button', { name: 'Áudio e voz', exact: true }).click();
        await settings.getByText('Microfone', { exact: true }).click();
        await settings.getByLabel('Entrada de áudio', { exact: true }).selectOption('synthetic-b');
        await settings.getByRole('button', { name: 'Fechar configurações', exact: true }).click();
        await page.evaluate(() => { window.syntheticAudio.silence = false; });
        await record.click();
        await page.waitForFunction(() => window.syntheticAudio.constraints.at(-1).audio.deviceId?.exact === 'synthetic-b');
        await page.getByRole('button', { name: 'Descartar gravação', exact: true }).click();
        await page.waitForFunction(() => window.syntheticAudio.captures.every(input => input.context.state === 'closed'));
        console.log(`${host}/${platform}: synthetic MediaRecorder, PCM meter, discard, silence, input preference and cleanup PASS`);
      } finally { await context.close(); }
    }
  }
} finally { await browser.close(); for (const server of servers) await server.close(); }
