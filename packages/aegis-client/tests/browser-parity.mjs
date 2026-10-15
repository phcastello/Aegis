import { chromium } from 'playwright-core';
import assert from 'node:assert/strict';
import http from 'node:http';
const fixture = { id: 'c1', title: 'Conversa de teste', createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), messageCount: 2, messages: [{ id: 'm0', conversationId: 'c1', role: 'assistant', content: 'Histórico preservado.', createdAt: new Date().toISOString() }] };
let cancellations = 0, feedback = 0, deletions = 0;
const json = (res, body, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(body)); };
const server = http.createServer(async (req, res) => {
 if (req.headers.origin === 'http://127.0.0.1:1420') res.setHeader('Access-Control-Allow-Origin', req.headers.origin);
 res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
 res.setHeader('Access-Control-Allow-Methods', 'GET,POST,DELETE,PATCH,OPTIONS');
 if (req.method === 'OPTIONS') { res.writeHead(204); res.end(); return; }
 const url = new URL(req.url, 'http://localhost');
 if (!url.pathname.startsWith('/api/')) {
   const response = await fetch('http://127.0.0.1:18092' + req.url);
   const headers = Object.fromEntries(response.headers); delete headers['content-encoding']; delete headers['content-length']; delete headers['transfer-encoding'];
   res.writeHead(response.status, headers); res.end(Buffer.from(await response.arrayBuffer())); return;
 }
 let raw = ''; for await (const part of req) raw += part;
 const body = raw ? JSON.parse(raw) : {};
 const path = url.pathname;
 if (path === '/api/health') return json(res, { status: 'ok' });
 if (path === '/api/voice/status') return json(res, { enabled: false, available: false });
 if (path === '/api/voice/transcription/status') return json(res, { enabled: false, configured: false });
 if (path === '/api/email/status') return json(res, { isConnected: true, emailAddress: 'test@example.test' });
 if (path === '/api/notifications/configuration') return json(res, { enabled: false, publicKey: null });
 if (path === '/api/chat/conversations') return json(res, { items: deletions ? [] : [fixture], hasMore: false });
 if (path.endsWith('/title')) { fixture.title = body.title; return json(res, fixture); }
 if (path.endsWith('/feedback')) { feedback++; return json(res, { id: 'f1', rating: body.rating }); }
 if (path.endsWith('/memory-activity')) return json(res, { pending: false, sections: [{ kind: 'used', items: ['Fato de teste.'], totalCount: 1 }] });
 if (path === '/api/chat/conversations/c1' && req.method === 'DELETE') { deletions++; return json(res, null, 204); }
 if (path === '/api/chat/conversations/c1') return json(res, fixture);
 if (path.includes('/turns/') && req.method === 'DELETE') { cancellations++; return json(res, null, 204); }
 if (path.includes('/turns/')) return json(res, null, 204);
 if (path === '/api/chat/messages/stream') {
   res.writeHead(200, { 'Content-Type': 'application/x-ndjson' });
   const event = value => res.write(JSON.stringify({ turnId: body.turnId, ...value }) + '\n');
   event({ type: 'conversation', conversationId: 'c1' });
   event({ type: 'token', content: 'Parcial ' });
   if (body.content === 'cancelar') return;
   event({ type: 'tool_status', category: 'email', state: 'started', message: 'Consultando integração…' });
   setTimeout(() => {
     event({ type: 'token', content: '**completa** [Link](https://example.test/article) [Conectar Google](https://accounts.google.com/o/oauth2/v2/auth?client_id=test)' });
     event({ type: 'done', conversationId: 'c1', messageId: 'm1', conversationTitle: fixture.title, memoryActivity: { pending: false, sections: [{ kind: 'used', items: ['Fato de teste.'], totalCount: 1 }] } });
     res.end();
   }, 1500);
   return;
 }
 json(res, { error: 'Unknown fixture path' }, 404);
});
// The test uses a private in-memory API fixture; it never writes to the real Aegis backend.
for (const url of ['http://127.0.0.1:18092', 'http://127.0.0.1:1420']) {
  const deadline = Date.now() + 15000;
  while (true) {
    try { const response = await fetch(url); if (response.ok) break; } catch {}
    if (Date.now() >= deadline) throw new Error('Start the web preview and native Vite servers before testing: ' + url);
    await new Promise(resolve => setTimeout(resolve, 150));
  }
}
await new Promise(resolve => server.listen(18093, '127.0.0.1', resolve));
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
try {
 for (const host of ['web', 'native']) {
   const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
   await context.addInitScript(() => localStorage.setItem('aegis.voice.autoSpeak', 'false')); // Audio/device acceptance is separate.
   if (host === 'native') await context.addInitScript(() => {
     window.openedUrls = []; window.nativeCalls = []; window.reminderPayloads=[]; window.placePayloads=[];
     const reminders=[],series=[],places=[],geofences=[];
     const location={sourceNodeId:'native-current',nodes:[],latest:null};
     const now = new Date().toISOString();
     const current = { availability:'online', capabilities: [{ name: 'audio.input', version: 1 }, { name: 'audio.output', version: 1 }, { name:'notification.show',version:1 }], targetPriority: 0, id: 'native-current', name: 'Celular', platform: 'android', enabled: true, appVersion: '0.7.0-stage.4', protocolVersion: 1, pairedAt: now, createdAt: now, updatedAt: now, revokedAt: null };
     const other = { ...current, id: 'native-other', name: 'PC', platform: 'windows' };
     let identity = { state: 'unpaired', node: null, error: null };
     window.__TAURI_INTERNALS__ = { invoke: async (command, args) => {
       window.nativeCalls.push(command);
       if(command === 'node_settings_open') return;
       if(command === 'node_reminders') { if(args.operation === 'list') return {reminders,series}; window.reminderPayloads.push(args); if(args.operation==='seriesCreate') {series.push({seriesId:crypto.randomUUID(),...args.payload,revision:1,status:'active'});return series.at(-1);} return {}; }
       if(command === 'node_places') { if(args.operation==='list')return places;if(args.operation==='config')return {tileUrl:null,attribution:'',geocodingConfigured:false};if(args.operation==='create'){window.placePayloads.push(args.payload);const place={id:crypto.randomUUID(),revision:1,...args.payload};places.push(place);return place;}return {}; }
       if(command === 'node_location') { if(args.operation==='settings')return location;if(args.operation==='permissions')return {supported:true,foreground:true,precise:true,background:true,servicesEnabled:true};return {}; }
       if(command === 'node_geofences') { if(args.operation==='list')return geofences;if(args.operation==='create'){geofences.push({id:crypto.randomUUID(),revision:1,...args.payload,status:'active',syncStatus:'pending_sync'});return geofences.at(-1);}if(args.operation==='sync'){geofences.forEach(item=>item.syncStatus='active');return {status:'installed'};}return {}; }
       if(command==='node_set_reminders'){const node=args.id===current.id?current:other;node.receiveReminders=args.receiveReminders;return node;}
       if(command==='node_remove'){other.revokedAt=now;other.enabled=false;return;}

       if (command === 'runtime_info') return { platform: 'android', backendUrl: 'http://127.0.0.1:18093', version: '0.7.0-stage.2' };
       if (command === 'check_backend') return { status: 'connected', message: null };
       if (command === 'node_transport_status') return { transportState: 'online', lastError: null };
       if (command === 'node_transport_reconnect') return;
       if (command === 'node_notification_settings') return {granted:false,pushConfigured:false,autostart:false};
       if (command === 'node_request_notification_permission') return;
       if (command === 'node_test_notification') {
         await new Promise(r => setTimeout(r,700));
         if (window.notificationFailure) throw new Error('arbitrary-secret-server-prose');
         return {node:args.id===current.id?current:other,status:window.notificationDiagnostic?'failed':'success',transport:'live_websocket',diagnosticCode:window.notificationDiagnostic};
       }
       if (command === 'node_status') return identity;
       if (command === 'node_pair') { current.name = args.name; return identity = { state: 'paired', node: current, error: null }; }
       if (command === 'node_list') return [current, other];
       if (command === 'node_create_pairing_code') return { code: 'ABCD-EFGH-JKMP-QRST-VWXY-1234', expiresAt: new Date(Date.now() + 600000).toISOString() };
       if (command === 'node_rename') { other.name = args.name; return other; }
       if (command === 'node_set_target_priority') { const node = args.id === current.id ? current : other; node.targetPriority = args.priority; return node; }
       if (command === 'node_resolve_target') return { node: other.targetPriority > current.targetPriority ? other : current, code: null, onlineNodes: 2, capabilityCompatibleNodes: 2 };
       if (command === 'node_set_enabled') { other.enabled = args.enabled; return other; }
       if (command === 'node_revoke') { other.revokedAt = now; other.enabled = false; return other; }
       if (command === 'check_android_update') return null;
       if (command === 'plugin:opener|open_url') { window.openedUrls.push(args.url); return; }
       throw new Error('Unexpected IPC: ' + command);
     } };
   });
   const page = await context.newPage(); const errors = [];
   page.on('pageerror', e => { errors.push(e.message); console.log('PAGEERROR', host, e.message); });
   page.on('console', m => { if (m.type() === 'error') console.log('CONSOLEERROR', host, m.text()); });
   await page.goto(host === 'web' ? 'http://127.0.0.1:18093' : 'http://127.0.0.1:1420');
   await page.getByLabel('Abrir histórico').click();
   await page.getByRole('button', { name: 'Conversa de teste', exact: true }).click();
   await page.getByText('Histórico preservado.', { exact: true }).waitFor();
   await page.locator('textarea').fill('teste'); await page.getByLabel('Enviar mensagem').click();
   await page.getByText('Parcial', { exact: true }).waitFor();
   assert.equal(await page.locator('.markdown-message strong').count(), 0);
   await page.getByText('Consultando integração…', { exact: true }).waitFor();
   await page.locator('.markdown-message strong').waitFor();
   await page.getByText('Usou memória', { exact: true }).click(); await page.getByText('Fato de teste.', { exact: true }).waitFor();
   await page.getByLabel('Marcar resposta como boa').last().click(); await page.getByRole('button', { name: 'Salvar feedback' }).click();
   await page.getByRole('dialog', { name: 'Boa resposta' }).waitFor({ state: 'hidden' });
   await page.getByLabel('Marcar resposta como ruim').last().click();
   await page.getByRole('button', { name: 'Salvar feedback' }).click();
   await page.getByRole('dialog', { name: 'Dar um bonk nessa resposta' }).waitFor({ state: 'hidden' });
   assert.equal(feedback, host === 'web' ? 2 : 4);
   if (host === 'native') {
     await page.getByRole('link', { name: 'Link', exact: true }).click();
     await page.getByRole('link', { name: 'Conectar Google' }).click();
     await page.evaluate(() => window.dispatchEvent(new Event('focus'))); // Return from the external OAuth browser.
     await page.getByText('Conta Google conectada', { exact: false }).waitFor();
     assert.equal((await page.evaluate(() => window.openedUrls)).length, 2);
     assert.equal(await page.evaluate(() => navigator.serviceWorker.getRegistrations().then(r => r.length)), 0);
     await page.getByRole('button',{name:'Configurações da Aegis',exact:true}).click();
     const panel = page.getByRole('dialog', { name: 'Configurações', exact: true });
     await panel.getByRole('button', {name:'Sobre',exact:true}).click();
     await panel.getByText('Android', { exact: true }).waitFor();
     await panel.getByText('Diagnóstico técnico', {exact:true}).click();
     await panel.getByText('Disponível', { exact: true }).waitFor();
     await panel.getByRole('button', {name:'Dispositivos',exact:true}).click();
     await panel.getByLabel('Nome deste dispositivo').fill('Celular Pedro');
     await panel.getByLabel('Código de pareamento').fill('ABCD-EFGH-JKMP-QRST-VWXY-1234');
     await panel.getByRole('button', { name: 'Parear dispositivo', exact: true }).click();
     await panel.getByText('Celular Pedro', { exact: true }).waitFor();
     assert.equal(await panel.locator('[data-node-id="native-current"]').count(), 1);
     assert.equal(await panel.getByText('· Este dispositivo', { exact: true }).count(), 1);
     assert.equal(await panel.locator('[data-node-id]').count(), 2);
     await panel.getByRole('button', { name: 'Adicionar dispositivo', exact: true }).click();
     await panel.getByText('ABCD-EFGH-JKMP-QRST-VWXY-1234', { exact: true }).waitFor();
     const pc = panel.locator('[data-node-id="native-other"]');
     assert.equal(await pc.getByText(/Capacidades implementadas:/).isVisible(),false);
     await pc.getByText('Detalhes técnicos e diagnóstico', {exact:true}).click();
     await pc.getByRole('button', { name:'Testar notificação',exact:true }).click();
     await pc.getByRole('button', { name:'Enviando…',exact:true }).waitFor();
     await pc.getByRole('status').filter({hasText:'Notificação postada pelo dispositivo.'}).waitFor();
     await page.evaluate(() => { window.notificationFailure=true; });
     await pc.getByRole('button', { name:'Testar notificação',exact:true }).click();
     await pc.getByRole('status').filter({hasText:'Não foi possível enviar.'}).waitFor();
     assert.doesNotMatch(await pc.innerText(), /arbitrary-secret-server-prose/);
     await page.evaluate(() => { window.notificationFailure=false;window.notificationDiagnostic='android_notification_build_failed'; });
     await pc.getByRole('button', { name:'Testar notificação',exact:true }).click();
     await pc.getByText('Código: android_notification_build_failed', {exact:true}).waitFor();
     await page.evaluate(() => { window.notificationDiagnostic='arbitrary-secret-prose'; });
     await pc.getByRole('button', { name:'Testar notificação',exact:true }).click();
     await pc.getByRole('button', { name:'Enviando…',exact:true }).waitFor();
     await pc.getByRole('button', { name:'Testar notificação',exact:true }).waitFor();
     assert.doesNotMatch(await pc.innerText(), /arbitrary-secret-prose|Código:/);
     await page.evaluate(() => { window.notificationDiagnostic=undefined; });
     await pc.getByRole('button', { name: 'Alterar prioridade', exact: true }).click();
     await pc.getByLabel('Prioridade', {exact:true}).fill('10'); await pc.getByRole('button', { name: 'Salvar prioridade', exact: true }).click();
     await pc.getByText('10', { exact: true }).waitFor();
     await pc.getByRole('button', { name: 'Renomear', exact: true }).click(); await pc.getByLabel('Novo nome').fill('PC Pedro'); await pc.getByRole('button', { name: 'Salvar nome', exact: true }).click();
     await pc.getByText('PC Pedro', { exact: true }).waitFor();
     await pc.getByRole('button', { name: 'Desativar', exact: true }).click(); await pc.getByRole('button', { name: 'Confirmar', exact: true }).click();
     await pc.getByRole('button', { name: 'Reativar', exact: true }).click();
     await pc.getByRole('button', { name: 'Remover dispositivo', exact: true }).click(); await pc.getByRole('button', { name: 'Confirmar', exact: true }).click();
     await pc.waitFor({state:'detached'});
     await panel.getByRole('button',{name:'Lembretes',exact:true}).click();
     await panel.getByRole('button',{name:'Nova recorrência',exact:true}).click();
     await panel.getByLabel('O que lembrar').fill('Recorrência sintética');
     await panel.getByRole('button',{name:'Lugares',exact:true}).click();
     await panel.getByRole('alertdialog',{name:'Descartar alterações?'}).waitFor();
     await panel.getByRole('button',{name:'Continuar editando',exact:true}).click();
     assert.equal(await panel.getByLabel('O que lembrar').inputValue(),'Recorrência sintética');
     await panel.getByLabel('Repetição',{exact:true}).selectOption('weekly');
     await panel.getByLabel('Intervalo',{exact:true}).fill('2');
     await panel.getByLabel('Qua',{exact:true}).check();
     await panel.getByRole('button',{name:'Salvar',exact:true}).click();
     await panel.getByText('Recorrência sintética',{exact:true}).waitFor();
     const recurrence=await page.evaluate(()=>window.reminderPayloads.at(-1));
     assert.equal(recurrence.payload.deliveryPolicy,'allEligible');assert.deepEqual(recurrence.payload.rule.weekdays,[1,3]);assert.equal(recurrence.payload.rule.interval,2);
     await panel.getByRole('button',{name:'Lugares',exact:true}).click();
     await panel.getByRole('button',{name:'Novo lugar',exact:true}).click();
     await panel.getByLabel('Nome',{exact:true}).fill('Lugar sintético');
     await panel.getByLabel('Apelidos pessoais, separados por vírgula').fill('referência sintética');
     await panel.getByRole('button',{name:'Adicionar círculo no mapa',exact:true}).click();
     await panel.locator('.place-map').click({position:{x:140,y:140}});
     await panel.getByLabel('Raio em metros',{exact:true}).fill('250');
     await panel.getByLabel('Conferi o ponto e a área. A cobertura representa este lugar.').check();
     await panel.getByRole('button',{name:'Salvar lugar',exact:true}).click();
     await panel.getByText('Lugar sintético',{exact:true}).waitFor();
     const place=await page.evaluate(()=>window.placePayloads.at(-1));assert.equal(place.circles.length,1);assert.equal(place.circles[0].radiusMeters,250);assert.deepEqual(place.aliases,['referência sintética']);
     await panel.getByRole('button',{name:'Lembretes',exact:true}).click();
     await panel.getByRole('button',{name:'Por lugar',exact:true}).click();
     await panel.getByLabel('O que lembrar').fill('Evento sintético');
     await panel.getByLabel('Lugar sintético',{exact:true}).check();
     await panel.getByRole('button',{name:'Salvar',exact:true}).click();
     await panel.getByText('Pendente de sincronização',{exact:true}).waitFor();
     await panel.getByRole('button',{name:'Sincronizar neste celular',exact:true}).click();
     await panel.getByText('Ativo no celular',{exact:true}).waitFor();
     assert.equal(await page.getByRole('dialog').count(),1);
     if(process.env.PARITY_SCREENSHOT_DIR)await page.screenshot({path:`${process.env.PARITY_SCREENSHOT_DIR}/native-settings-mobile.png`});
     await page.evaluate(()=>window.dispatchEvent(new Event('aegis-back')));
     await panel.waitFor({state:'hidden'});
     const listCalls=await page.evaluate(()=>window.nativeCalls.filter(command=>command==='node_list').length);
     await page.waitForTimeout(11000);
     assert.equal(await page.evaluate(()=>window.nativeCalls.filter(command=>command==='node_list').length),listCalls,'Closed settings must not poll Nodes');
     console.log('native UI: management, safe diagnostics, recurrence payload, map union editing, geographic sync state, dirty guard, native Back and no closed-panel polling PASS (IPC mocked).');
   }
   await page.locator('textarea').fill('cancelar'); await page.getByLabel('Enviar mensagem').click();
   await page.getByLabel('Interromper geração').click(); await page.getByLabel('Enviar mensagem').waitFor();
   await page.getByLabel('Abrir histórico').click();
   await page.getByLabel('Ações de Conversa de teste').click(); await page.getByRole('button', { name: 'Renomear', exact: true }).click();
   await page.getByLabel('Renomear conversa').fill('Renomeada'); await page.getByLabel('Renomear conversa').press('Enter');
   await page.getByRole('button', { name: 'Renomeada', exact: true }).waitFor();
   await page.getByRole('button', { name: 'Nova conversa', exact: true }).click();
   await page.setViewportSize({ width: 1600, height: 900 });
   const panelBounds=await page.locator('.chat-panel').boundingBox();const composerBounds=await page.locator('.composer-field').boundingBox();
   assert.ok(Math.abs(composerBounds.x+composerBounds.width/2-(panelBounds.x+panelBounds.width/2))<2,'Composer must center inside usable chat area');
   assert.ok(panelBounds.x>200,'Desktop sidebar must occupy a real layout column');
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
   if (process.env.PARITY_SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.PARITY_SCREENSHOT_DIR}/${host}-parity.png` });
   await page.getByLabel('Ações de Renomeada').click();
   await page.getByRole('button', { name: 'Apagar', exact: true }).click();
   await page.getByRole('dialog', { name: 'Apagar conversa' }).getByRole('button', { name: 'Apagar', exact: true }).click();
   await page.getByRole('dialog', { name: 'Apagar conversa' }).waitFor({ state: 'hidden' });
   assert.equal(deletions, 1);
   assert.deepEqual(errors, []);
   console.log(`${host}: UI, streaming before done, cancel, history, rename, Markdown, feedback, memory, mobile/resize PASS (fixture; native IPC mocked)`);
   await context.close(); fixture.title = 'Conversa de teste'; deletions = 0;
 }
 assert.equal(feedback, 4); assert.equal(cancellations, 2);
} finally { await browser.close(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
