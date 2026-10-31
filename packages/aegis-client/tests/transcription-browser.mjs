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
      if (process.env.TRANSCRIPTION_BROWSER_PLATFORM && process.env.TRANSCRIPTION_BROWSER_PLATFORM !== platform) continue;
      const context = await browser.newContext({ viewport: { width: 1100, height: 900 } });
      let uploads = 0, transcriptionText = 'Texto sintético', transcriptionFailure = false, sendFailure = false, transcriptionGate = null, deletionGate = null, deleted = false;
      const prompts = [], acceptedPrompts = [];
      const conversationId = 'synthetic-voice-conversation';
      const conversation = () => ({ id: conversationId, title: 'Voz sintética', createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), messageCount: acceptedPrompts.length * 2,
        messages: acceptedPrompts.flatMap((content, index) => [{ id: `user-${index}`, conversationId, role: 'user', content, createdAt: new Date().toISOString() }, { id: `assistant-${index}`, conversationId, role: 'assistant', content: 'Resposta sintética.', createdAt: new Date().toISOString() }]) });
      await context.route('**/api/**', async route => {
        const path = new URL(route.request().url()).pathname;
        let body;
        if (path === '/api/health') body = { status: 'ok' };
        else if (path === '/api/voice/status') body = { enabled: false, available: false };
        else if (path === '/api/voice/transcription/status') body = { enabled: true, configured: true, maxRecordingSeconds: 90 };
        else if (path === '/api/voice/transcriptions') {
          uploads++;
          if (transcriptionGate) await transcriptionGate;
          if (transcriptionFailure) return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Falha sintética de transcrição.' }) });
          body = { text: transcriptionText };
        }
        else if (path === '/api/chat/messages/stream') {
          const request = route.request().postDataJSON(); prompts.push(request);
          if (sendFailure) return route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ error: 'Falha sintética antes da aceitação.' }) });
          acceptedPrompts.push(request.content);
          const lines = [
            { type: 'conversation', turnId: request.turnId, conversationId },
            { type: 'token', turnId: request.turnId, content: 'Resposta sintética.' },
            { type: 'done', turnId: request.turnId, conversationId, messageId: `assistant-${acceptedPrompts.length - 1}`, conversationTitle: 'Voz sintética' }
          ];
          return route.fulfill({ contentType: 'application/x-ndjson', body: lines.map(line => JSON.stringify(line)).join('\n') + '\n' });
        }
        else if (path === '/api/email/status') body = { isConnected: false };
        else if (path === '/api/chat/proactive-activity') body = [];
        else if (path === '/api/chat/conversations') body = { items: acceptedPrompts.length && !deleted ? [conversation()] : [], hasMore: false };
        else if (path === `/api/chat/conversations/${conversationId}`) {
          if (route.request().method() === 'DELETE') {
            if (deletionGate) await deletionGate;
            deleted = true;
            return route.fulfill({ status: 204 });
          }
          body = conversation();
        }
        else if (path === '/api/notifications/configuration') body = { enabled: false };
        else body = {};
        await route.fulfill({ contentType: 'application/json', body: JSON.stringify(body) });
      });
      await context.addInitScript(({ version, platform }) => {
        localStorage.setItem('aegis.voice.autoSpeak', 'false');
        window.syntheticAudio = { silence: false, captures: [], constraints: [], decodes: [] };
        window.syntheticHandsFree = { enabled: false, diagnostic: false, assistantSelected: false, permissionGranted: false, modelReady: false, status: 'disabled', message: 'Escuta desligada.', calls: [], lease: null };
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
          if (platform === 'android' && !window.syntheticHandsFree.lease) throw Error('Native audio lease required before recording');
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
        if (platform !== 'browser') window.__TAURI_INTERNALS__ = { invoke: async (command, args) => {
          if (command === 'node_hands_free') {
            const hf = window.syntheticHandsFree; hf.calls.push({ ...args });
            if (args.operation === 'manualAcquire') { if (hf.lease) return { acquired: false }; hf.lease = args.leaseId; return { acquired: true }; }
            if (args.operation === 'manualRelease') {
              if (hf.lease === args.leaseId) {
                if (window.syntheticAudio.captures.some(input => input.stream.getTracks().some(track => track.readyState === 'live'))) throw Error('Tracks still active at lease release');
                hf.lease = null;
              }
              return {};
            }
            if (args.operation === 'configure') { hf.enabled = args.enabled; hf.diagnostic = args.diagnostic; }
            if (args.operation === 'permission') hf.permissionGranted = true;
            if (args.operation === 'assistant') hf.assistantSelected = true;
            if (args.operation === 'import') hf.modelReady = true;
            hf.status = hf.enabled ? hf.permissionGranted && hf.assistantSelected && hf.modelReady ? 'listening' : 'unavailable' : 'disabled';
            hf.message = hf.status === 'listening' ? 'Escutando “Aegis”.' : hf.status === 'disabled' ? 'Escuta desligada.' : 'Confira as permissões, o assistente e o modelo.';
            return JSON.parse(JSON.stringify(hf));
          }
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
        await page.locator('.composer textarea').fill('Rascunho preservado');
        await record.click();
        await page.waitForFunction(() => [...document.querySelectorAll('.recording-waveform-bar')].some(bar => parseFloat(bar.style.height) > 3));
        assert.equal(await page.locator('.composer textarea').count(), 0, 'dedicated recording UI replaces the textarea');
        assert.equal(await page.locator('meter').count(), 0, 'no diagnostic meter in composer');
        const controls = await Promise.all(['Descartar gravação', 'Parar e transcrever para revisão', 'Parar, transcrever e enviar'].map(name => page.getByRole('button', { name, exact: true }).boundingBox()));
        assert.ok(controls.every(Boolean)); assert.ok(controls[0].x < controls[1].x && controls[1].x < controls[2].x, 'cancel, microphone and send ordered horizontally');
        for (const width of [320, 390, 720, 1100]) {
          await page.setViewportSize({ width, height: 900 });
          const boxes = await Promise.all(['Descartar gravação', 'Parar e transcrever para revisão', 'Parar, transcrever e enviar'].map(name => page.getByRole('button', { name, exact: true }).boundingBox()));
          assert.ok(boxes.every(box => box && box.x >= 0 && box.x + box.width <= width), `recording controls fit ${width}px`);
          assert.ok(boxes.every(box => Math.abs(box.y - boxes[0].y) <= 1), 'controls share one line');
        }
        await page.emulateMedia({ reducedMotion: 'reduce' });
        assert.equal(await page.locator('.recording-waveform-bar').first().evaluate(bar => getComputedStyle(bar).transitionDuration), '0s');
        await page.emulateMedia({ reducedMotion: 'no-preference' });
        await page.getByRole('button', { name: 'Descartar gravação', exact: true }).click();
        await page.waitForFunction(() => window.syntheticAudio.captures.every(input => input.stream.getTracks().every(track => track.readyState === 'ended')));
        assert.equal(uploads, 0, 'discard never uploads');
        assert.equal(await page.locator('.composer textarea').inputValue(), 'Rascunho preservado');
        await page.locator('.composer textarea').evaluate(input => input.setSelectionRange(input.value.length, input.value.length));
        await record.click(); await page.waitForTimeout(250);
        await page.getByRole('button', { name: 'Parar e transcrever para revisão', exact: true }).click();
        await page.waitForFunction(() => document.querySelector('.composer textarea')?.value === 'Rascunho preservado Texto sintético');
        assert.equal(await page.locator('.composer textarea').evaluate(input => document.activeElement === input), true, 'review returns focus to the editor after recording UI unmounts');
        assert.equal(uploads, 1, 'quiet synthetic signal uploaded once');
        assert.equal(prompts.length, 0, 'microphone transcribes for review without sending');
        await page.locator('.composer textarea').fill('');
        await page.evaluate(() => { window.syntheticAudio.silence = true; });
        await record.click(); await page.waitForTimeout(350);
        await page.waitForFunction(() => [...document.querySelectorAll('.recording-waveform-bar')].every(bar => parseFloat(bar.style.height) === 3));
        await page.getByRole('button', { name: 'Parar, transcrever e enviar', exact: true }).click();
        await page.getByText('A gravação contém apenas silêncio digital.', { exact: false }).waitFor();
        assert.equal(uploads, 1, 'digital silence never uploaded');
        assert.equal(prompts.length, 0, 'digital silence never sends');
        const decoded = await page.evaluate(() => window.syntheticAudio.decodes);
        assert.equal(decoded.length, 2, 'both complete recordings decoded in Chromium');
        assert.ok(decoded[0].peak > 0.001 && decoded[0].peak < 0.01, 'quiet synthetic signal preserved');
        assert.ok(decoded[1].peak <= 1e-30, 'digital-zero codec residue far below PCM resolution');
        await page.getByRole('button', { name: 'Configurações da Aegis', exact: true }).click();
        const settings = page.getByRole('dialog', { name: 'Configurações', exact: true });
        await settings.getByRole('button', { name: 'Áudio e voz', exact: true }).click();
        if (platform === 'android') {
          await settings.getByRole('heading', { name: 'Hands-free', exact: true }).waitFor();
          await settings.getByRole('button', { name: 'Permitir microfone', exact: true }).click();
          await settings.getByRole('button', { name: 'Selecionar assistente no Android', exact: true }).click();
          await settings.getByRole('button', { name: 'Importar modelo privado', exact: true }).click();
          await settings.getByRole('checkbox', { name: 'Escutar “Aegis”', exact: true }).check();
          await settings.getByText('Escutando “Aegis”.', { exact: true }).waitFor();
          await settings.getByRole('checkbox', { name: 'Diagnóstico: detectar chamadas sem enviar comandos', exact: true }).check();
          await settings.getByRole('button', { name: 'Exportar métricas', exact: true }).click();
          await settings.getByRole('button', { name: 'Cancelar interação', exact: true }).click();
          assert.equal(await page.evaluate(() => window.syntheticHandsFree.diagnostic), true);
          assert.equal(uploads, 1, 'hands-free configuration never uploads audio');
          assert.equal(prompts.length, 0, 'hands-free configuration never sends typed drafts');
        } else assert.equal(await settings.getByRole('heading', { name: 'Hands-free', exact: true }).count(), 0);
        await settings.getByText('Microfone', { exact: true }).click();
        await settings.getByLabel('Entrada de áudio', { exact: true }).selectOption('synthetic-b');
        await settings.getByRole('button', { name: 'Fechar configurações', exact: true }).click();
        await page.evaluate(() => { window.syntheticAudio.silence = false; });
        await record.click();
        await page.waitForFunction(() => window.syntheticAudio.constraints.at(-1).audio.deviceId?.exact === 'synthetic-b');
        await page.getByRole('button', { name: 'Descartar gravação', exact: true }).click();

        await page.locator('.composer textarea').fill('Antes');
        await page.locator('.composer textarea').evaluate(input => input.setSelectionRange(input.value.length, input.value.length));
        await record.click(); await page.waitForTimeout(250);
        await page.getByRole('button', { name: 'Parar, transcrever e enviar', exact: true }).click();
        await page.getByText('Resposta sintética.', { exact: true }).waitFor();
        assert.deepEqual(prompts.map(prompt => prompt.content), ['Antes Texto sintético'], 'send uses the normal textual turn with prior draft');
        assert.equal(await page.locator('.composer textarea').inputValue(), '');
        assert.equal(uploads, 2);

        for (const failure of ['empty', 'transcription', 'send']) {
          transcriptionText = failure === 'empty' ? '  \n ' : 'Texto sintético';
          transcriptionFailure = failure === 'transcription'; sendFailure = failure === 'send';
          const before = prompts.length;
          await page.locator('.composer textarea').fill('Recuperável');
          await page.locator('.composer textarea').evaluate(input => input.setSelectionRange(input.value.length, input.value.length));
          await record.click(); await page.waitForTimeout(250);
          await page.getByRole('button', { name: 'Parar, transcrever e enviar', exact: true }).click();
          const expectedError = failure === 'empty' ? 'A transcrição não contém texto.' : failure === 'transcription' ? 'Falha sintética de transcrição.' : 'Falha sintética antes da aceitação.';
          await page.getByText(expectedError, { exact: false }).first().waitFor();
          const expectedDraft = failure === 'send' ? 'Recuperável Texto sintético' : 'Recuperável';
          await page.waitForFunction(text => document.querySelector('.composer textarea')?.value === text, expectedDraft);
          assert.equal(prompts.length, before + (failure === 'send' ? 1 : 0));
          assert.equal(acceptedPrompts.length, 1, 'failures do not duplicate accepted turns');
        }

        transcriptionFailure = false; sendFailure = false; transcriptionText = 'Resposta tardia inválida';
        for (const action of ['escape', 'navigate']) {
          let release;
          transcriptionGate = new Promise(resolve => { release = resolve; });
          const before = prompts.length;
          await page.locator('.composer textarea').fill('Rascunho de origem');
          await record.click(); await page.waitForTimeout(250);
          const request = page.waitForRequest('**/api/voice/transcriptions');
          await page.getByRole('button', { name: 'Parar, transcrever e enviar', exact: true }).click();
          await request;
          await page.getByText('Transcrevendo…', { exact: true }).waitFor();
          assert.equal(await page.getByRole('button', { name: 'Cancelar transcrição', exact: true }).evaluate(button => document.activeElement === button), true, 'upload keeps keyboard cancellation reachable');
          if (action === 'escape') await page.keyboard.press('Escape');
          else await page.getByRole('button', { name: 'Nova conversa', exact: true }).click();
          release(); transcriptionGate = null;
          await page.waitForTimeout(100);
          assert.equal(prompts.length, before, 'late STT cannot send after capture/context invalidation');
          assert.equal(await page.locator('.composer textarea').inputValue(), action === 'escape' ? 'Rascunho de origem' : '');
          if (action === 'navigate') {
            await page.getByRole('button', { name: 'Voz sintética', exact: true }).click();
            assert.equal(await page.locator('.composer textarea').inputValue(), 'Rascunho de origem', 'navigation preserves the original draft');
          }
        }

        let releaseTranscription, releaseDeletion;
        transcriptionGate = new Promise(resolve => { releaseTranscription = resolve; });
        deletionGate = new Promise(resolve => { releaseDeletion = resolve; });
        const beforeDeletion = prompts.length;
        await page.locator('.composer textarea').fill('Não enviar depois de excluir');
        await record.click(); await page.waitForTimeout(250);
        const pendingUpload = page.waitForRequest('**/api/voice/transcriptions');
        await page.getByRole('button', { name: 'Parar, transcrever e enviar', exact: true }).click();
        await pendingUpload;
        await page.getByRole('button', { name: 'Menu da conversa', exact: true }).click();
        await page.getByRole('menuitem', { name: 'Excluir', exact: true }).click();
        const pendingDeletion = page.waitForRequest(request => request.method() === 'DELETE' && new URL(request.url()).pathname === `/api/chat/conversations/${conversationId}`);
        await page.getByRole('dialog', { name: 'Apagar conversa', exact: true }).getByRole('button', { name: 'Apagar', exact: true }).click();
        await pendingDeletion;
        releaseTranscription(); transcriptionGate = null;
        await page.waitForTimeout(100);
        assert.equal(prompts.length, beforeDeletion, 'STT cannot start a turn while its conversation is being deleted');
        releaseDeletion(); deletionGate = null;
        await page.getByRole('dialog', { name: 'Apagar conversa', exact: true }).waitFor({ state: 'detached' });
        assert.equal(await page.locator('.composer textarea').inputValue(), '');
        assert.equal(await page.getByRole('button', { name: 'Voz sintética', exact: true }).count(), 0, 'deleted conversation is not resurrected');
        await page.waitForFunction(() => window.syntheticAudio.captures.every(input => input.context.state === 'closed'));
        if (platform === 'android') {
          await page.waitForFunction(() => window.syntheticHandsFree.lease === null);
          const calls = await page.evaluate(() => window.syntheticHandsFree.calls);
          assert.equal(calls.filter(call => call.operation === 'manualAcquire').length, calls.filter(call => call.operation === 'manualRelease').length);
        }
        console.log(`${host}/${platform}: real MediaRecorder/WebAudio with synthetic signal, waveform, review, direct send, failures, drafts, silence, input preference and cleanup PASS`);
      } finally { await context.close(); }
    }
  }
} finally { await browser.close(); for (const server of servers) await server.close(); }
