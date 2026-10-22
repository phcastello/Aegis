import { chromium } from 'playwright-core';
import assert from 'node:assert/strict';
import http from 'node:http';
import { mkdirSync, readFileSync } from 'node:fs';
const appVersion = JSON.parse(readFileSync(new URL('../../../apps/aegis-node/package.json', import.meta.url), 'utf8')).version;
// A preview can advance to its stable release; a stable install needs a newer patch.
const [major, minor, patch] = appVersion.split('-')[0].split('.').map(Number);
const updateVersion = appVersion.includes('-') ? `${major}.${minor}.${patch}` : `${major}.${minor}.${patch + 1}`;
assert.notEqual(updateVersion, appVersion);
assert.equal(JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8')).version, appVersion);
assert.equal(JSON.parse(readFileSync(new URL('../../../frontend/aegis-pwa/package.json', import.meta.url), 'utf8')).version, appVersion);
const fixture = { id: 'c1', title: 'Conversa de teste', createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), messageCount: 2, messages: [{ id: 'm0', conversationId: 'c1', role: 'assistant', content: 'Histórico preservado.', createdAt: new Date().toISOString() }] };
let cancellations = 0, feedback = 0, deletions = 0, renameFailures = 0;
let proactiveMessages = [], proactiveRead = 0, conversationDelay = 0;
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
 if (path === '/api/chat/proactive-activity') return json(res, proactiveMessages.length ? [{ conversationId: 'c1', lastProactiveSequence: proactiveMessages.length, readProactiveSequence: proactiveRead }] : []);
 if (path === '/api/chat/conversations/c1/proactive-messages') return json(res, proactiveMessages.filter(m => m.proactiveSequence > Number(url.searchParams.get('after'))));
 if (path === '/api/chat/conversations/c1/proactive-read') { proactiveRead = Math.max(proactiveRead, body.sequence); return json(res, null, 204); }
 if (path === '/api/chat/conversations') return json(res, { items: deletions ? [] : [{...fixture, hasUnreadProactive: proactiveMessages.length > proactiveRead}], hasMore: false });
 if (path.endsWith('/title')) { if (body.title === 'Falhar uma vez' && renameFailures++ === 0) return json(res, { error: 'fixture failure' }, 503); fixture.title = body.title; return json(res, fixture); }
 if (path.endsWith('/feedback')) { feedback++; return json(res, { id: 'f1', rating: body.rating }); }
 if (path.endsWith('/memory-activity')) return json(res, { pending: false, sections: [{ kind: 'used', items: ['Fato de teste.'], totalCount: 1 }] });
 if (path === '/api/chat/conversations/c1' && req.method === 'DELETE') { deletions++; return json(res, null, 204); }
 if (path === '/api/chat/conversations/c1') { if(conversationDelay) await new Promise(resolve=>setTimeout(resolve,conversationDelay)); return json(res, {...fixture, messages:[...fixture.messages, ...proactiveMessages]}); }
 if (path === '/api/chat/conversations/f0000000-0000-4000-8000-000000000001') return json(res, {...fixture,id:'f0000000-0000-4000-8000-000000000001',messages:[{...fixture.messages[0],content:'Destino explícito preservado.'}]});
 if (path.includes('/turns/') && req.method === 'DELETE') { cancellations++; return json(res, null, 204); }
 if (path.includes('/turns/')) return json(res, null, 204);
 if (path === '/api/chat/messages/stream') {
   res.writeHead(200, { 'Content-Type': 'application/x-ndjson' });
   const event = value => res.write(JSON.stringify({ turnId: body.turnId, ...value }) + '\n');
   event({ type: 'conversation', conversationId: 'c1' });
   event({ type: 'token', content: 'Parcial ' });
   if (body.content === 'cancelar') return;
   if (body.content === 'falha de lembrete') {
     event({ type: 'tool_status', category: 'reminder', state: 'failed', message: 'Servidor de lembretes temporariamente indisponível.' });
     event({ type: 'done', conversationId: 'c1', messageId: 'reminder-failure-fixture', conversationTitle: fixture.title });
     res.end();
     return;
   }
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
if(process.env.PARITY_SCREENSHOT_DIR)mkdirSync(process.env.PARITY_SCREENSHOT_DIR,{recursive:true});
let activePage=null,activeHost='unknown';
const browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
try {
 for (const host of ['web', 'native']) {
   const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
   await context.addInitScript(() => localStorage.setItem('aegis.voice.autoSpeak', 'false')); // Audio/device acceptance is separate.
   if (host === 'native') await context.addInitScript((runtimeVersion) => {
     window.openedUrls = []; window.nativeCalls = []; window.reminderPayloads=[]; window.placePayloads=[];
     const reminders=[],series=[],places=[],geofences=[];
     window.seedPausedReminder=()=>reminders.push({reminderId:crypto.randomUUID(),text:'Ocorrência temporal pausada',dueAt:'2026-10-20T11:00:00Z',timeZoneId:'America/Sao_Paulo',status:'Paused',seriesId:null,revision:2,generation:1,deliveryPolicy:'allEligible'});
     window.reminderListRequests=[];window.extraFocusedReminders=[];
     window.enableReminderPaging=()=>{
       for(let index=1;index<=101;index++)series.push({seriesId:crypto.randomUUID(),text:`Série paginada ${index}`,timeZoneId:'America/Sao_Paulo',rule:{frequency:'daily',interval:1,localTime:'08:00',startDate:'2026-01-01'},status:'active',revision:1,deliveryPolicy:'allEligible',nextDueAt:'2026-10-20T11:00:00Z'});
       for(let index=1;index<=201;index++)reminders.push({reminderId:crypto.randomUUID(),text:`Ocorrência paginada ${index}`,dueAt:'2026-10-20T11:00:00Z',timeZoneId:'America/Sao_Paulo',status:'Scheduled',seriesId:null,revision:1,generation:1,deliveryPolicy:'allEligible'});
       window.oldReminder={reminderId:crypto.randomUUID(),text:'Ocorrência antiga focalizada',dueAt:'2020-01-01T11:00:00Z',timeZoneId:'America/Sao_Paulo',status:'Cancelled',seriesId:null,revision:1,generation:1,deliveryPolicy:'allEligible'};
     };
     const location={sourceNodeId:'native-current',nodes:[],latest:null};
     const now = new Date().toISOString();
     const current = { availability:'online', capabilities: [{ name: 'audio.input', version: 1 }, { name: 'audio.output', version: 1 }, { name:'notification.show',version:1 }], targetPriority: 0, id: 'native-current', name: 'Celular', platform: 'android', enabled: true, appVersion: runtimeVersion, protocolVersion: 1, pairedAt: now, createdAt: now, updatedAt: now, revokedAt: null };
     const other = { ...current, id: 'native-other', name: 'PC', platform: 'windows' };
     window.setPeerPlatform = platform => { other.platform = platform; };
     let identity = { state: 'unpaired', node: null, error: null };
     window.__TAURI_INTERNALS__ = { invoke: async (command, args) => {
       window.nativeCalls.push(command);
       // Match Tauri's JSON IPC boundary instead of retaining Vue proxy references.
       if(args!==undefined)args=JSON.parse(JSON.stringify(args));
       if(command === 'node_settings_open') return;
       if(command === 'node_reminders') { if(args.operation === 'list') {
         const query=args.payload??{};window.reminderListRequests.push(query);if(window.reminderListDelay)await new Promise(resolve=>setTimeout(resolve,window.reminderListDelay));
         if(query.offset>0 || query.seriesOffset>0) {await (window.reminderPageGate ?? new Promise(resolve=>setTimeout(resolve,350)));if(window.reminderPageFailure){window.reminderPageFailure=false;throw Error('Falha sintética de página.');}}
         const offset=query.offset??0,seriesOffset=query.seriesOffset??0;
         return structuredClone({hasMoreDeliveries:window.reminderDeliveryTruncated===true,reminders:reminders.slice(offset,offset+200),series:series.slice(seriesOffset,seriesOffset+100),hasMore:reminders.length>offset+200,nextOffset:reminders.length>offset+200?offset+200:null,seriesHasMore:series.length>seriesOffset+100,nextSeriesOffset:series.length>seriesOffset+100?seriesOffset+100:null,focusedReminder:[window.oldReminder,...window.extraFocusedReminders].find(item=>item?.reminderId===query.focusId)??null});
       } window.reminderPayloads.push(args); if(args.operation==='seriesCreate') {series.push({seriesId:crypto.randomUUID(),...args.payload,revision:1,status:'active'});return series.at(-1);} return {}; }
       if(command === 'node_places') { if(args.operation==='list')return structuredClone(places);if(args.operation==='config')return {tileUrl:null,attribution:'',geocodingConfigured:false};if(args.operation==='create'){
         window.placePayloads.push(structuredClone(args.payload));const place={id:crypto.randomUUID(),revision:1,...args.payload};
         const envelope=args.payload.monitoringEnvelope;
         place.monitoringEnvelope={enabled:envelope?.enabled??false,revision:envelope?1:0,paddingMeters:envelope?.paddingMeters??150,approvedAt:envelope?new Date().toISOString():null,approvalRequired:!envelope,circles:envelope?.enabled?place.circles.map(circle=>({...circle,id:'operational-'+circle.id,radiusMeters:circle.radiusMeters+envelope.paddingMeters})):[]};
         places.push(place);return structuredClone(place);
       }return {}; }
       if(command === 'node_location') { if(args.operation==='settings')return location;if(args.operation==='permissions')return {supported:true,foreground:true,precise:true,background:true,servicesEnabled:true};if(args.operation==='current')return {latitude:0,longitude:0,accuracyMeters:window.fixtureAccuracyMeters??20,capturedAt:new Date().toISOString(),ageSeconds:0,source:'synthetic-fixture'};return {}; }
       if(command === 'node_geofences') { if(args.operation==='list')return structuredClone(geofences);if(args.operation==='diagnostics')return {supported:true,rules:geofences.map(item=>({id:item.id,revision:item.revision,installationStatus:item.syncStatus==='pending_sync'?'pending_sync':'installed',presence:'unknown',confirmedPresence:'unknown',armed:item.status==='active'&&item.syncStatus==='active'&&window.fixtureGeoArmed===true,monitoringState:window.fixtureGeoArmed===true?'armed':'waiting_initial',lastObservationAt:null,accuracyMeters:null,reasonCode:'not_observed',powerSaveMode:false,history:[]}))};if(args.operation==='create'){geofences.push({id:crypto.randomUUID(),revision:1,...args.payload,status:'active',syncStatus:'pending_sync'});return geofences.at(-1);}if(args.operation==='sync'){if(window.fixtureGeoSyncReply)return structuredClone(window.fixtureGeoSyncReply);geofences.forEach(item=>item.syncStatus='active');return {status:'installed'};}if(args.operation==='pause'){const item=geofences.find(item=>item.id===args.id);Object.assign(item,{status:'paused',syncStatus:'pending_sync',revision:item.revision+1});return structuredClone(item);}return {}; }
       if(command==='node_set_reminders'){const node=args.id===current.id?current:other;node.receiveReminders=args.receiveReminders;return node;}
       if(command==='node_remove'){other.revokedAt=now;other.enabled=false;return;}

       if (command === 'runtime_info') return { processSessionId: localStorage.getItem('fixture.processSession') || 'fixture-process', platform: 'android', backendUrl: 'http://127.0.0.1:18093', version: runtimeVersion };
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
       if (command === 'check_android_update') { if(window.fixtureUpdateFailure)throw window.fixtureUpdateFailure;return window.fixtureUpdate??null; }
       if (command === 'install_android_update') return window.fixtureInstallResult??{status:'installer_opened'};
       if (command === 'plugin:opener|open_url') { window.openedUrls.push(args.url); return; }
       throw new Error('Unexpected IPC: ' + command);
     } };
   }, appVersion);
   const page = await context.newPage(); activePage=page;activeHost=host;const errors = [];
   page.on('pageerror', e => { errors.push(e.message); console.log('PAGEERROR', host, e.message); });
   page.on('console', m => { if (m.type() === 'error') console.log('CONSOLEERROR', host, m.text()); });
   await page.goto(host === 'web' ? 'http://127.0.0.1:18093' : 'http://127.0.0.1:1420');
   await page.getByLabel('Abrir histórico').click();
   await page.locator('#conversation-sidebar').getByText(`v${appVersion}`, { exact: true }).waitFor();
   await page.getByRole('button', { name: 'Conversa de teste', exact: true }).click();
   await page.getByText('Histórico preservado.', { exact: true }).waitFor();
   assert.equal(await page.getByRole('button',{name:'Configurações da Aegis',exact:true}).count(),0,'Collapsed mobile navigation exposes only the normal sidebar opener');
   assert.equal(await page.locator('.global-navigation-floating').count(),0);
   await page.getByRole('button',{name:'Abrir histórico',exact:true}).waitFor();
   assert.equal(await page.locator('.conversation-heading').isVisible(), false, 'Mobile hides title/date/category');
   assert.equal(await page.locator('.voice-toggle').count(), 0);
   await page.locator('textarea').fill('Rascunho preservado'); await page.locator('textarea').focus();
   await page.getByLabel('Abrir histórico', { exact: true }).click();
   assert.equal(await page.locator('textarea').evaluate(el => el === document.activeElement), false);
   await page.getByLabel('Fechar histórico', { exact: true }).click();
   assert.equal(await page.locator('textarea').inputValue(), 'Rascunho preservado');
   assert.equal(await page.locator('textarea').evaluate(el => el === document.activeElement), false);
   await page.getByLabel('Menu da conversa', { exact: true }).click();
   assert.deepEqual(await page.getByRole('menuitem').allTextContents(), ['Informações','Renomear','Excluir']);
   await page.keyboard.press('Escape'); assert.equal(await page.getByRole('menu').count(), 0);
   await page.getByLabel('Menu da conversa', { exact: true }).click();
   await page.getByRole('menuitem', { name: 'Informações', exact: true }).click();
   const info = page.getByRole('dialog', { name: 'Informações da conversa', exact: true });
   await info.locator('code').getByText('c1', { exact: true }).waitFor();
   await page.evaluate(() => { navigator.clipboard.writeText = async value => { window.copiedId = value; }; });
   await info.getByRole('button', { name: 'Copiar ID', exact: true }).click();
   assert.equal(await page.evaluate(() => window.copiedId), 'c1'); await info.getByText('Copiado', { exact: true }).waitFor();
   await page.keyboard.press('Escape'); await info.waitFor({state:'hidden'});
   await page.getByLabel('Menu da conversa', { exact: true }).click();
   await page.getByRole('menuitem', { name: 'Renomear', exact: true }).click();
   const rename = page.getByRole('dialog', { name: 'Renomear conversa', exact: true });
   assert.equal(await rename.getByLabel('Título', {exact:true}).inputValue(), fixture.title);
   assert.equal(await rename.getByRole('button', { name: 'Salvar', exact:true }).isDisabled(), true);
   await rename.getByLabel('Título', {exact:true}).fill('   ');
   assert.equal(await rename.getByRole('button', { name: 'Salvar', exact:true }).isDisabled(), true);
   await page.keyboard.press('Escape'); await rename.waitFor({state:'hidden'});
   await page.getByLabel('Menu da conversa', { exact: true }).click(); await page.getByRole('menuitem', { name: 'Renomear', exact: true }).click();
   await rename.getByLabel('Título', {exact:true}).fill('Falhar uma vez');
   await rename.getByRole('button', {name:'Salvar',exact:true}).click(); await rename.getByRole('alert').waitFor();
   assert.equal(await rename.getByLabel('Título', {exact:true}).inputValue(), 'Falhar uma vez');
   await page.evaluate(() => window.dispatchEvent(new Event('aegis-back'))); await rename.waitFor({state:'hidden'});
   assert.equal(await page.locator('textarea').inputValue(), 'Rascunho preservado');
   await page.getByLabel('Menu da conversa', {exact:true}).click();await page.getByRole('menuitem',{name:'Renomear',exact:true}).click();
   await rename.getByLabel('Título',{exact:true}).fill('Título pelo menu superior');await rename.getByRole('button',{name:'Salvar',exact:true}).click();await rename.waitFor({state:'hidden'});
   await page.getByLabel('Menu da conversa', {exact:true}).click();await page.getByRole('menuitem',{name:'Renomear',exact:true}).click();
   await rename.getByLabel('Título',{exact:true}).fill('Conversa de teste');await rename.getByRole('button',{name:'Salvar',exact:true}).click();await rename.waitFor({state:'hidden'});
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
   await page.locator('textarea').fill('falha de lembrete');
   await page.getByLabel('Enviar mensagem').click();
   await page.getByText('Servidor de lembretes temporariamente indisponível.', { exact: true }).waitFor();
   assert.equal(await page.getByRole('button', { name: /Ativar notificações|Permitir notificações/ }).count(), 0,
     'A reminder tool failure preserves its cause and never offers browser push activation');
   assert.equal(await page.locator('.notification-control').count(), 0);
   if (host === 'native') {
     await page.getByRole('link', { name: 'Link', exact: true }).click();
     await page.getByRole('link', { name: 'Conectar Google' }).click();
     await page.evaluate(() => window.dispatchEvent(new Event('focus'))); // Return from the external OAuth browser.
     await page.getByText('Conta Google conectada', { exact: false }).waitFor();
     assert.equal((await page.evaluate(() => window.openedUrls)).length, 2);
     assert.equal(await page.evaluate(() => navigator.serviceWorker.getRegistrations().then(r => r.length)), 0);
     await page.getByRole('button',{name:'Abrir histórico',exact:true}).click();
     await page.getByRole('button',{name:'Configurações da Aegis',exact:true}).click();
     const panel = page.getByRole('dialog', { name: 'Configurações', exact: true });
     await panel.getByRole('button',{name:'Áudio e voz',exact:true}).click();
     const automatic = panel.getByRole('checkbox',{name:'Reproduzir respostas automaticamente',exact:true});
     assert.equal(await automatic.isChecked(),false);await automatic.check();assert.equal(await page.evaluate(()=>localStorage.getItem('aegis.voice.autoSpeak')),'true');await automatic.uncheck();
     await panel.getByRole('button', {name:'Sobre',exact:true}).click();
     const installedVersion = panel.locator('.about-brand .settings-badge');
     await installedVersion.waitFor();
     // Assert the public formatting contract independently of getReleaseIdentity().
     assert.equal((await installedVersion.textContent()).trim(), `v${appVersion}`);
     await panel.getByText('Android', { exact: true }).first().waitFor();
     assert.equal(await panel.getByRole('button',{name:'Verificar atualizações',exact:true}).count(),0);
     assert.equal(await panel.locator('.update-notice').count(),0,'About has no second updater renderer');
     assert.equal(await page.evaluate(()=>window.nativeCalls.filter(command=>command==='check_android_update').length),1,'There is only one initial update check');
     assert.equal(await panel.getByRole('button',{name:'Abrir Atualizações',exact:true}).count(),0);await panel.getByRole('button',{name:'Atualizações',exact:true}).click();
     await page.evaluate(version=>window.fixtureUpdate={version,url:`https://github.com/phcastello/Aegis/releases/download/node-v${version}/Aegis-Android-arm64.apk`},updateVersion);
     await panel.getByRole('button',{name:'Verificar atualizações',exact:true}).click();
     await panel.getByRole('status',{name:'Atualização da Aegis',exact:true}).waitFor();
     assert.equal(await panel.locator('.update-notice').count(),1);
     await panel.getByText(/^Última consulta:/).waitFor();
     if(process.env.PARITY_SCREENSHOT_DIR)await page.screenshot({path:`${process.env.PARITY_SCREENSHOT_DIR}/native-updates-mobile.png`});
     await page.evaluate(()=>window.fixtureUpdateFailure='update_feed_missing');
     await panel.getByRole('button',{name:'Verificar atualizações',exact:true}).click();
     await panel.getByText('O canal de atualizações não está disponível. Tente novamente mais tarde.',{exact:true}).waitFor();
     await panel.locator('.update-notice').getByText('Código: update_feed_missing',{exact:true}).waitFor();
     await panel.locator('.update-notice .update-title-mobile').getByText('Atualização disponível',{exact:true}).waitFor();
     await panel.locator('.update-notice .update-version').getByText(`Versão ${updateVersion}`,{exact:true}).waitFor();
     await page.evaluate(()=>{window.fixtureUpdateFailure=null;window.fixtureInstallResult={status:'failed',phase:'verification',code:'update_signature_mismatch'};});
     await panel.getByRole('button',{name:'Verificar atualizações',exact:true}).click();
     await panel.getByRole('button',{name:'Baixar e instalar',exact:true}).click();
     await panel.getByText('A assinatura do instalador não corresponde à Aegis instalada.',{exact:true}).waitFor();
     await panel.locator('.update-notice').getByText('Código: update_signature_mismatch',{exact:true}).waitFor();
     assert.equal(await panel.getByText('Confirme a instalação na janela do sistema.',{exact:true}).count(),0);
     if(process.env.PARITY_SCREENSHOT_DIR)await page.screenshot({path:`${process.env.PARITY_SCREENSHOT_DIR}/native-update-verification-mobile.png`});
     await page.evaluate(()=>window.fixtureInstallResult={status:'permission_required'});
     await panel.getByRole('button',{name:'Baixar e instalar',exact:true}).click();
     await panel.getByRole('button',{name:'Continuar instalação',exact:true}).waitFor();
     await page.evaluate(()=>window.fixtureInstallResult={status:'installer_opened'});
     await panel.getByRole('button',{name:'Continuar instalação',exact:true}).click();
     await panel.getByText('Confirme a instalação na janela do sistema.',{exact:true}).waitFor();
     assert.equal(await page.evaluate(()=>window.nativeCalls.filter(command=>command==='install_android_update').length),3,'Native installer requests follow only the three explicit UI actions');
     await panel.getByRole('button',{name:'Sobre',exact:true}).click();
     assert.equal(await panel.locator('.update-notice').count(),0);
     assert.equal(await panel.getByRole('button',{name:'Verificar atualizações',exact:true}).count(),0);
     assert.equal(await panel.getByRole('button',{name:'Baixar e instalar',exact:true}).count(),0);
     assert.equal(await page.evaluate(()=>window.nativeCalls.filter(command=>command==='check_android_update').length),4,'Opening About does not run another update check');
     if(process.env.PARITY_SCREENSHOT_DIR)await page.screenshot({path:`${process.env.PARITY_SCREENSHOT_DIR}/native-about-mobile.png`});
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
     const phone = panel.locator('[data-node-id="native-current"]');
     await phone.getByRole('button', { name:'Desativar',exact:true }).click();
     await phone.getByRole('alertdialog').getByText('As regras geográficas deste celular também serão pausadas; retome explicitamente em Lembretes após reativar.', {exact:true}).waitFor();
     await phone.getByRole('button', { name:'Cancelar',exact:true }).click();
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
     await pc.getByRole('button', { name: 'Desativar', exact: true }).click();
     assert.doesNotMatch(await pc.getByRole('alertdialog').innerText(), /regras geográficas deste celular/);
     await pc.getByRole('button', { name: 'Confirmar', exact: true }).click();
     await pc.getByRole('button', { name: 'Reativar', exact: true }).click();
     assert.doesNotMatch(await pc.innerText(), /Abra a Aegis nele para reconectar/);
     // Exercise the Android peer branch separately from the Windows peer above.
     await page.evaluate(() => window.setPeerPlatform('android'));
     await panel.getByRole('button', {name:'Atualizar lista',exact:true}).click();
     await pc.getByRole('button', {name:'Desativar',exact:true}).click();
     await pc.getByRole('button', {name:'Confirmar',exact:true}).click();
     await pc.getByRole('button', {name:'Reativar',exact:true}).click();
     await pc.getByRole('status').filter({hasText:'Dispositivo reativado. Abra a Aegis nele para reconectar. Retome as regras geográficas em Lembretes.'}).waitFor();
     await pc.getByRole('button', { name: 'Remover dispositivo', exact: true }).click(); await pc.getByRole('button', { name: 'Confirmar', exact: true }).click();
     await pc.waitFor({state:'detached'});
     await page.evaluate(()=>window.seedPausedReminder());
     await panel.getByRole('button',{name:'Lembretes',exact:true}).click();
     await panel.locator('article').filter({hasText:'Ocorrência temporal pausada'}).getByText('Pausado',{exact:true}).waitFor();
     await panel.getByRole('button',{name:'Nova recorrência',exact:true}).click();
     await panel.getByLabel('O que lembrar').fill('Recorrência sintética');
     await panel.getByRole('button',{name:'Lugares',exact:true}).click();
     await panel.getByRole('alertdialog',{name:'Descartar alterações?'}).waitFor();
     if(process.env.PARITY_SCREENSHOT_DIR)await page.screenshot({path:`${process.env.PARITY_SCREENSHOT_DIR}/native-unsaved-mobile.png`});
     await panel.getByRole('button',{name:'Continuar editando',exact:true}).click();
     assert.equal(await panel.getByLabel('O que lembrar').inputValue(),'Recorrência sintética');
     await panel.getByRole('button',{name:'Sobre',exact:true}).click();await panel.getByRole('alertdialog',{name:'Descartar alterações?'}).waitFor();await page.keyboard.press('Escape');await panel.getByRole('alertdialog',{name:'Descartar alterações?'}).waitFor({state:'hidden'});
     assert.equal(await panel.getByLabel('O que lembrar').inputValue(),'Recorrência sintética');
     await panel.getByRole('combobox',{name:/Repetição/}).selectOption('weekly');
     await panel.getByLabel('Intervalo',{exact:true}).fill('2');
     await panel.getByLabel('Qua',{exact:true}).check();
     await panel.getByRole('button',{name:'Salvar',exact:true}).click();
     await panel.getByText('Recorrência sintética',{exact:true}).waitFor();
     const recurrence=await page.evaluate(()=>window.reminderPayloads.at(-1));
     assert.equal(recurrence.payload.deliveryPolicy,'allEligible');assert.deepEqual(recurrence.payload.rule.weekdays,[1,3]);assert.equal(recurrence.payload.rule.interval,2);
     await panel.getByRole('button',{name:'Lugares',exact:true}).click();
     await panel.getByRole('button',{name:'Novo lugar',exact:true}).click();
     await panel.getByLabel('Nome',{exact:true}).fill('Lugar sintético');
     await panel.getByLabel('Apelidos, separados por vírgula').fill('referência sintética');
     await panel.getByRole('button',{name:'Adicionar círculo no mapa',exact:true}).click();
     await panel.locator('.place-map').click({position:{x:140,y:140}});
     assert.equal(await panel.getByLabel('Raio em metros',{exact:true}).inputValue(),'50','A new real residence starts at 50 m; Android proximity is configured separately');
     await panel.getByRole('button',{name:'Minha localização',exact:true}).click();
     await panel.getByText(/Precisão estimada: 20 m/).waitFor();
     await page.evaluate(()=>window.fixtureAccuracyMeters=100);
     await panel.getByRole('button',{name:'Minha localização',exact:true}).click();
     await panel.getByLabel('Raio em metros',{exact:true}).fill('50');
     await panel.getByLabel('Raio em metros',{exact:true}).blur();
     await panel.getByText(/A precisão atual pode dificultar/).waitFor();
     await page.evaluate(()=>window.fixtureAccuracyMeters=201);
     await panel.getByRole('button',{name:'Minha localização',exact:true}).click();
     await panel.getByText(/A leitura atual está imprecisa demais/).waitFor();
     assert.equal(await panel.getByLabel('Raio em metros',{exact:true}).inputValue(),'50','Poor accuracy never silently expands a personal area');
     await page.evaluate(()=>window.fixtureAccuracyMeters=20);
     await panel.getByRole('button',{name:'Minha localização',exact:true}).click();
     await panel.getByLabel('Conferi a área que representa este lugar.').check();
     await panel.getByLabel('Usar uma região de proximidade',{exact:true}).check();
     const padding=panel.getByLabel('Extensão além de cada círculo real, em metros',{exact:true});
     assert.equal(await padding.inputValue(),'150');
     assert.equal(await panel.getByLabel('Raio em metros',{exact:true}).inputValue(),'50','Operational review preserves the actual 50 m home');
     assert.equal(await panel.locator('path[stroke="#e6c578"][stroke-dasharray]').count(),1,'Operational preview is visually distinct from the real area');
     await panel.getByRole('button',{name:'Salvar lugar',exact:true}).click();
     await panel.getByText('Confirme a alteração da detecção de proximidade antes de salvar.',{exact:true}).waitFor();
     const approve=panel.getByLabel('Confiro e aprovo esta região de proximidade.');
     await approve.check();await padding.fill('175');
     assert.equal(await approve.isChecked(),false,'Operational edits require fresh explicit approval');
     await padding.fill('150');await approve.check();
     if(process.env.PARITY_SCREENSHOT_DIR)await page.screenshot({path:`${process.env.PARITY_SCREENSHOT_DIR}/native-place-map-mobile.png`});
     await panel.getByRole('button',{name:'Salvar lugar',exact:true}).click();
     await panel.getByText('Lugar sintético',{exact:true}).waitFor();
     const place=await page.evaluate(()=>window.placePayloads.at(-1));assert.equal(place.circles.length,1);assert.equal(place.circles[0].radiusMeters,50);assert.deepEqual(place.aliases,['referência sintética']);
     assert.deepEqual(place.monitoringEnvelope,{enabled:true,paddingMeters:150,confirmed:true,expectedRevision:0});
     await panel.getByText('Área real: 50 m · Proximidade: 200 m',{exact:true}).waitFor();
     await panel.getByRole('button',{name:'Lembretes',exact:true}).click();
     await panel.getByRole('button',{name:'Por lugar',exact:true}).click();
     await panel.getByLabel('O que lembrar').fill('Evento sintético');
     await panel.getByLabel('Lugar sintético',{exact:true}).check();
     await panel.getByRole('button',{name:'Salvar',exact:true}).click();
     await panel.getByText('Sincronização necessária',{exact:true}).waitFor();
     const beforePendingSync=await page.evaluate(()=>{window.fixtureGeoSyncReply={status:'installed',syncPending:true};return window.reminderListRequests.length;});
     await panel.getByRole('button',{name:'Sincronizar neste celular',exact:true}).click();
     const localInstallNotice=panel.getByText('Monitoramento instalado neste celular; confirmação ao servidor pendente. A regra pode aguardar a condição inicial.',{exact:true});await localInstallNotice.waitFor();
     assert.equal(await page.evaluate(()=>window.reminderListRequests.length),beforePendingSync,'A local installation awaiting backend confirmation must not refetch an unreachable backend');
     await page.evaluate(()=>window.fixtureGeoSyncReply={status:'recoverable_error',syncPending:true,errorCode:'native_registration_failed'});
     await panel.getByRole('button',{name:'Sincronizar neste celular',exact:true}).click();
     await panel.getByRole('alert').filter({hasText:'O Android não conseguiu registrar as áreas.'}).waitFor();
     assert.equal(await localInstallNotice.count(),0,'A failed native registration must never retain a local installed claim');
     await page.evaluate(()=>window.fixtureGeoSyncReply=null);
     await panel.getByRole('button',{name:'Sincronizar neste celular',exact:true}).click();
     await panel.getByText('Aguardando condição inicial',{exact:true}).waitFor();
     assert.equal(await panel.getByText('Ativo — aguardando sua saída',{exact:true}).count(),0,'Installation alone does not arm the desired transition');
     await page.evaluate(()=>window.fixtureGeoArmed=true);
     await panel.getByRole('button',{name:'Atualizar',exact:true}).click();
     await panel.getByText('Ativo — aguardando sua saída',{exact:true}).waitFor();
     const geographicRule=panel.locator('article').filter({hasText:'Evento sintético'});
     await geographicRule.getByRole('button',{name:'Pausar',exact:true}).click();
     await geographicRule.getByText('Pausa pendente no celular',{exact:true}).waitFor();
     await geographicRule.getByText('O celular offline pode conservar e executar a regra anterior até sincronizar ou até a validade local expirar.',{exact:true}).waitFor();
     assert.equal(await page.getByRole('dialog').count(),1);
     if(process.env.PARITY_SCREENSHOT_DIR)await page.screenshot({path:`${process.env.PARITY_SCREENSHOT_DIR}/native-settings-mobile.png`});
     await page.evaluate(()=>window.enableReminderPaging());
     await panel.getByRole('button',{name:'Atualizar',exact:true}).click();
     await panel.getByRole('heading',{name:'Série paginada 1',exact:true}).waitFor();
     assert.equal(await panel.getByRole('heading',{name:'Série paginada 101',exact:true}).count(),0);
     const seriesMore=panel.getByRole('button',{name:'Carregar mais séries',exact:true});
     const pageRequests=await page.evaluate(()=>{window.reminderDeliveryTruncated=true;return window.reminderListRequests.length;});
     // Hold the fixture response until the pending UI assertion completes. Wall-clock
     // delays can expire while Playwright scrolls a long list before returning click().
     await page.evaluate(()=>{window.reminderPageGate=new Promise(resolve=>window.releaseReminderPage=()=>{window.reminderPageGate=null;resolve();});});
     await seriesMore.click();assert.equal(await seriesMore.isDisabled(),true);
     await page.evaluate(()=>window.releaseReminderPage());
     await panel.getByRole('heading',{name:'Série paginada 101',exact:true}).waitFor();
     assert.equal(await seriesMore.count(),0);assert.equal(await page.evaluate(()=>window.reminderListRequests.length),pageRequests+1,'One click fetches exactly one series page');
     const deliveryWarning=panel.getByText('Parte dos detalhes de entrega não foi carregada. Isso não altera o estado dos lembretes.',{exact:true});await deliveryWarning.waitFor();await page.evaluate(()=>window.reminderDeliveryTruncated=false);
     const occurrencesMore=panel.getByRole('button',{name:'Carregar mais ocorrências',exact:true});
     await page.evaluate(()=>{window.reminderPageFailure=true;window.reminderPageGate=new Promise(resolve=>window.releaseReminderPage=()=>{window.reminderPageGate=null;resolve();});});await occurrencesMore.click();assert.equal(await occurrencesMore.isDisabled(),true);
     await page.evaluate(()=>window.releaseReminderPage());
     await panel.getByRole('alert').filter({hasText:'Falha sintética de página.'}).waitFor();
     assert.equal(await panel.getByRole('heading',{name:'Ocorrência paginada 1',exact:true}).count(),1,'A failed page preserves existing occurrences');
     assert.equal(await panel.getByRole('heading',{name:'Série paginada 101',exact:true}).count(),1,'A failed occurrence page preserves loaded series');
     await occurrencesMore.click();await panel.getByRole('heading',{name:'Ocorrência paginada 201',exact:true}).waitFor();assert.equal(await occurrencesMore.count(),0);assert.equal(await deliveryWarning.isVisible(),true,'A later complete page must preserve the previous truncation warning');
     await page.evaluate(()=>window.dispatchEvent(new CustomEvent('aegis-reminder-open',{detail:{reminderId:window.oldReminder.reminderId}})));
     const oldReminder=panel.getByRole('heading',{name:'Ocorrência antiga focalizada',exact:true});await oldReminder.waitFor();
     assert.equal(await oldReminder.count(),1);assert.equal(await deliveryWarning.count(),0,'A full refresh resets the delivery truncation warning');assert.equal(await page.evaluate(()=>document.activeElement.id===`reminder-${window.oldReminder.reminderId}`),true,'A notification body focuses its old authenticated occurrence outside the page');
     assert.equal(await panel.getByLabel('Mostrar histórico e cancelados').isChecked(),true);
     const focusedRequest=await page.evaluate(()=>window.reminderListRequests.at(-1));assert.equal(focusedRequest.offset,0);assert.equal(focusedRequest.seriesOffset,0);assert.match(focusedRequest.focusId,/^[0-9a-f-]{36}$/i);
     const focusRace=await page.evaluate(()=>{
       window.reminderListDelay=600;
       const first={...window.oldReminder,reminderId:crypto.randomUUID(),text:'Foco intermediário durante página'};
       const latest={...window.oldReminder,reminderId:crypto.randomUUID(),text:'Último foco durante página'};
       window.extraFocusedReminders.push(first,latest);return {first:first.reminderId,latest:latest.reminderId,requestCount:window.reminderListRequests.length};
     });
     await occurrencesMore.click();assert.equal(await occurrencesMore.isDisabled(),true);
     await page.evaluate(id=>window.dispatchEvent(new CustomEvent('aegis-reminder-open',{detail:{reminderId:id}})),focusRace.first);
     await page.evaluate(id=>window.dispatchEvent(new CustomEvent('aegis-reminder-open',{detail:{reminderId:id}})),focusRace.latest);
     await panel.getByRole('heading',{name:'Último foco durante página',exact:true}).waitFor();
     assert.equal(await page.evaluate(id=>document.activeElement.id===`reminder-${id}`,focusRace.latest),true,'The latest notification must be fetched and focused after a busy page completes');
     const focusRaceRequests=await page.evaluate(count=>window.reminderListRequests.slice(count),focusRace.requestCount);
     assert.equal(focusRaceRequests.length,2,'The busy page and one coalesced focus reload are the only requests');
     assert.equal(focusRaceRequests.filter(query=>query.focusId===focusRace.first).length,0,'The superseded focus must not trigger another reload');
     assert.equal(focusRaceRequests.at(-1).focusId,focusRace.latest);
     await page.evaluate(()=>window.reminderListDelay=0);
     await panel.getByRole('button',{name:'Novo lembrete',exact:true}).click();await panel.getByLabel('O que lembrar').fill('Formulário ainda não salvo');
     const dirtyFocus=await page.evaluate(()=>{const item={...window.oldReminder,reminderId:crypto.randomUUID(),text:'Foco aguardando formulário'};window.extraFocusedReminders.push(item);return {id:item.reminderId,requests:window.reminderListRequests.length};});
     await page.evaluate(id=>window.dispatchEvent(new CustomEvent('aegis-reminder-open',{detail:{reminderId:id}})),dirtyFocus.id);
     await page.waitForTimeout(200);assert.equal(await panel.getByLabel('O que lembrar').inputValue(),'Formulário ainda não salvo');assert.equal(await page.evaluate(()=>window.reminderListRequests.length),dirtyFocus.requests,'A notification must preserve the unsaved form');
     await panel.getByRole('button',{name:'Cancelar',exact:true}).click();
     await panel.locator('.editor-discard').getByRole('button',{name:'Descartar alterações',exact:true}).click();
     await panel.getByRole('heading',{name:'Foco aguardando formulário',exact:true}).waitFor();
     assert.equal(await page.evaluate(id=>document.activeElement.id===`reminder-${id}`,dirtyFocus.id),true);
     await page.evaluate(()=>window.dispatchEvent(new Event('aegis-back')));
     await panel.waitFor({state:'hidden'});
     await page.getByRole('button',{name:'Fechar histórico',exact:true}).click();
     const updateShortcut=page.getByRole('button',{name:'Ver Atualizações',exact:true});await updateShortcut.waitFor();
     assert.equal(await page.locator('.update-notice').count(),0,'Chat only summarizes an update and never renders another installer');
     assert.equal(await page.getByRole('button',{name:'Baixar e instalar',exact:true}).count(),0);
     await updateShortcut.click();await panel.getByRole('status',{name:'Atualização da Aegis',exact:true}).waitFor();
     await page.evaluate(()=>window.dispatchEvent(new Event('aegis-back')));await panel.waitFor({state:'hidden'});
     const listCalls=await page.evaluate(()=>window.nativeCalls.filter(command=>command==='node_list').length);
     await page.waitForTimeout(11000);
     assert.equal(await page.evaluate(()=>window.nativeCalls.filter(command=>command==='node_list').length),listCalls,'Closed settings must not poll Nodes');
     console.log('native UI: management, safe diagnostics, recurrence payload, map union editing, geographic sync state, dirty guard, native Back and no closed-panel polling PASS (IPC mocked).');
   }
   await page.locator('textarea').fill('cancelar'); await page.getByLabel('Enviar mensagem').click();
   await page.getByLabel('Interromper geração').click(); await page.getByLabel('Enviar mensagem').waitFor();
   await page.getByLabel('Abrir histórico').click();
   await page.getByLabel('Ações de Conversa de teste').click(); await page.getByRole('menuitem', { name: 'Renomear', exact: true }).click();
   await page.getByLabel('Título', { exact: true }).fill('Renomeada'); await page.getByLabel('Título', { exact: true }).press('Enter');
   await page.getByRole('button', { name: 'Renomeada', exact: true }).waitFor();
   await page.getByRole('button', { name: 'Nova conversa', exact: true }).click();
   await page.getByRole('button',{name:'Abrir histórico',exact:true}).waitFor();
   await page.locator('#conversation-sidebar').evaluate(element=>Promise.all(element.getAnimations().map(animation=>animation.finished.catch(()=>undefined))));
   if(process.env.PARITY_SCREENSHOT_DIR)await page.screenshot({path:`${process.env.PARITY_SCREENSHOT_DIR}/${host}-chat-mobile.png`});
   assert.equal(await page.getByLabel('Menu da conversa',{exact:true}).isDisabled(),true,'Empty conversation requires no persisted ID');
   proactiveMessages = [{id:'proactive-one',conversationId:'c1',role:'assistant',content:'Preciso que você abra o aplicativo no celular para ativar o lembrete.',createdAt:new Date().toISOString(),isProactive:true,proactiveSequence:1}];
   await page.evaluate(()=>window.dispatchEvent(new Event('focus')));
   await page.getByLabel('Abrir histórico',{exact:true}).click();await page.getByLabel('Mensagem da Aegis não lida',{exact:true}).waitFor();
   assert.equal(await page.getByText(proactiveMessages[0].content,{exact:true}).count(),0,'Incoming message does not navigate to another conversation');
   await page.reload();await page.getByLabel('Abrir histórico',{exact:true}).click();await page.getByLabel('Mensagem da Aegis não lida',{exact:true}).waitFor();
   await page.getByRole('button',{name:/^Renomeada/}).click();await page.getByText(proactiveMessages[0].content,{exact:true}).waitFor();
   await page.waitForFunction(()=>!document.querySelector('[aria-label="Mensagem da Aegis não lida"]'));
   assert.equal(proactiveRead,1,'Only visible proactive content advances the persisted read marker');
   await page.getByLabel('Abrir histórico',{exact:true}).click();
   proactiveMessages.push({id:'proactive-two',conversationId:'c1',role:'assistant',content:'A localização em segundo plano precisa ser habilitada para este outro lembrete.',createdAt:new Date().toISOString(),isProactive:true,proactiveSequence:2});
   await page.evaluate(()=>window.dispatchEvent(new Event('focus')));
   await page.getByText(proactiveMessages[1].content,{exact:true}).waitFor();
   assert.equal(proactiveRead,1,'Selected conversation behind mobile drawer does not imply reading');
   await page.getByLabel('Fechar histórico',{exact:true}).click();
   await page.locator('.messages').evaluate(element=>element.scrollTop=element.scrollHeight);
   await page.waitForFunction(()=>!document.querySelector('[aria-label="Mensagem da Aegis não lida"]'));
   assert.equal(proactiveRead,2);

   if(host==='native') {
     await page.reload();await page.getByText(proactiveMessages[0].content,{exact:true}).waitFor();
     await page.evaluate(()=>localStorage.setItem('fixture.processSession','new-native-process'));await page.reload();
     await page.getByText('Digite ou fale uma mensagem.',{exact:true}).waitFor();
     assert.equal(await page.getByText(proactiveMessages[0].content,{exact:true}).count(),0,'A new native process starts empty');
     conversationDelay = 500;
     await page.getByLabel('Abrir histórico',{exact:true}).click();await page.getByRole('button',{name:'Renomeada',exact:true}).click();
     await page.evaluate(()=>window.dispatchEvent(new CustomEvent('aegis-conversation-open',{detail:{conversationId:'f0000000-0000-4000-8000-000000000001'}})));
     conversationDelay = 0;
     await page.getByText('Destino explícito preservado.',{exact:true}).waitFor();
   }
   await page.setViewportSize({ width: 1600, height: 900 });
   // matchMedia delivers the breakpoint change asynchronously. Wait for Vue's
   // desktop layout, then read both rectangles in the same browser frame.
   await page.locator('.hideout-shell--sidebar-open .chat-panel').waitFor();
   await page.locator('#conversation-sidebar').evaluate(element=>Promise.all(element.getAnimations().map(animation=>animation.finished.catch(()=>undefined))));
   const {panelBounds,composerBounds}=await page.evaluate(()=>({panelBounds:document.querySelector('.chat-panel').getBoundingClientRect().toJSON(),composerBounds:document.querySelector('.composer-field').getBoundingClientRect().toJSON()}));
   assert.ok(Math.abs(composerBounds.x+composerBounds.width/2-(panelBounds.x+panelBounds.width/2))<2,'Composer must center inside usable chat area');
   assert.ok(panelBounds.x>200,'Desktop sidebar must occupy a real layout column');
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth>innerWidth),false);
   await page.getByRole('button',{name:'Fechar histórico',exact:true}).click();
   assert.equal(await page.getByRole('button',{name:'Configurações da Aegis',exact:true}).count(),0,'Collapsed desktop has no floating settings control');
   assert.equal(await page.locator('.global-navigation-floating').count(),0);
   await page.getByRole('button',{name:'Abrir histórico',exact:true}).click();
   if (process.env.PARITY_SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.PARITY_SCREENSHOT_DIR}/${host}-parity.png` });
   if(host==='native') {
     const trigger=page.getByRole('button',{name:'Configurações da Aegis',exact:true});await trigger.click();const settings=page.getByRole('dialog',{name:'Configurações',exact:true});await settings.waitFor();
     if(process.env.PARITY_SCREENSHOT_DIR)await page.screenshot({path:`${process.env.PARITY_SCREENSHOT_DIR}/native-settings-desktop.png`});
     await page.keyboard.press('Shift+Tab');assert.equal(await page.evaluate(()=>Boolean(document.activeElement.closest('dialog'))),true,'Modal focus must remain inside settings');
     await page.keyboard.press('Escape');await settings.waitFor({state:'hidden'});assert.equal(await trigger.evaluate(element=>document.activeElement===element),true,'Escape must restore launcher focus');
     await trigger.click();await settings.waitFor();await page.mouse.click(12,450);await settings.waitFor({state:'hidden'});assert.equal(await trigger.evaluate(element=>document.activeElement===element),true,'Backdrop must restore launcher focus');
   }
   await page.getByRole('button',{name:'Renomeada',exact:true}).click();
   await page.getByLabel('Ações de Renomeada').click();
   await page.getByRole('menuitem', { name: 'Excluir', exact: true }).click();
   await page.getByRole('dialog', { name: 'Apagar conversa' }).getByRole('button', { name: 'Apagar', exact: true }).click();
   await page.getByRole('dialog', { name: 'Apagar conversa' }).waitFor({ state: 'hidden' });
   assert.equal(deletions, 1); await page.getByText('Digite ou fale uma mensagem.',{exact:true}).waitFor();
   assert.deepEqual(errors, []);
   console.log(`${host}: UI, streaming before done, cancel, history, rename, Markdown, feedback, memory, mobile/resize PASS (fixture; native IPC mocked)`);
   await context.close(); fixture.title = 'Conversa de teste'; deletions = 0; renameFailures = 0; proactiveMessages = []; proactiveRead = 0;
 }
 assert.equal(feedback, 4); assert.equal(cancellations, 2);
} catch(error) { if(process.env.PARITY_SCREENSHOT_DIR&&activePage&&!activePage.isClosed())await activePage.screenshot({path:`${process.env.PARITY_SCREENSHOT_DIR}/${activeHost}-failure.png`}).catch(()=>{});throw error;
} finally { await browser.close(); server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
