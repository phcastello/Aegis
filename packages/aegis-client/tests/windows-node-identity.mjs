// Real Windows app + Credential Manager + loopback ASP.NET Node API. No production account.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import { chromium } from 'playwright-core';
import WebSocket from '../../../scripts/node-transport-probe/node_modules/ws/wrapper.mjs';
if (process.platform !== 'win32') throw Error('Requires a real Windows runner.');
const work = mkdtempSync(join(tmpdir(), 'aegis-node-identity-'));
const bootstrap = join(work, 'temporary-code.txt');
const exe = resolve(process.argv[2]);
const api = spawn('dotnet', ['run', '--project', '../../backend/tests/Aegis.NodeRuntimeFixture', '--', bootstrap], { stdio: 'inherit' });
let app, browser, page, exited;
const peerSockets = [];
async function connectPeer(credential, appVersion) {
  const ws = new WebSocket(origin.replace(/^http/, 'ws') + '/api/nodes/connect', { headers: { Authorization: 'AegisNode ' + credential, 'X-Aegis-Node-Protocol': '1' }, maxPayload: 4096, handshakeTimeout: 10000 });
  peerSockets.push(ws); let timer; const id = randomUUID();
  await new Promise((resolve, reject) => {
    const deadline = setTimeout(() => reject(Error('peer_hello_timeout')), 10000);
    ws.on('error', () => reject(Error('peer_network_failed')));
    ws.on('open', () => ws.send(JSON.stringify({ protocolVersion: 1, type: 'hello', messageId: id, sentAt: new Date().toISOString(), payload: { appVersion, capabilities: [{ name: 'audio.input', version: 1 }] } })));
    ws.on('message', raw => { const message = JSON.parse(String(raw)); if (message.type === 'hello_ack') {
      assert.equal(message.messageId, id); clearTimeout(deadline);
      timer = setInterval(() => ws.send(JSON.stringify({ protocolVersion: 1, type: 'heartbeat', messageId: randomUUID(), sentAt: new Date().toISOString() })), message.payload.heartbeatSeconds * 1000); resolve(); } });
    ws.once('close', code => { clearInterval(timer); clearTimeout(deadline); reject(Error('peer_closed_code_' + code)); });
  });
  return ws;
}
const origin = 'http://127.0.0.1:18104';
async function waitUntil(action, timeout = 120000) {
  const end = Date.now() + timeout;
  while (!(await action())) { if (Date.now() > end) throw Error('Native test readiness timeout'); await new Promise(r => setTimeout(r, 150)); }
}
async function call(path, body, credential, method) {
  const response = await fetch(origin + '/api/nodes' + path, { method: method ?? (body === undefined ? 'GET' : 'POST'),
    headers: { 'Content-Type': 'application/json', ...(credential ? { Authorization: 'AegisNode ' + credential } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  return { status: response.status, body: await response.json() };
}
async function invoke(command, args) { return page.evaluate(({ command, args }) => window.__TAURI_INTERNALS__.invoke(command, args), { command, args }); }
async function open() {
  exited = undefined;
  app = spawn(exe, [], { env: { ...process.env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=9461', WEBVIEW2_USER_DATA_FOLDER: join(work, 'webview') }, stdio: 'inherit' });
  app.on('exit', code => exited = code);
  await waitUntil(async () => { if (exited !== undefined) throw Error('Native app exited'); try { return (await fetch('http://127.0.0.1:9461/json/version')).ok; } catch { return false; } }, 30000);
  browser = await chromium.connectOverCDP('http://127.0.0.1:9461');
  await waitUntil(() => { page = browser.contexts().flatMap(c => c.pages())[0]; return Boolean(page); }, 30000);
  // Existing chat requests use private empty fixtures. Node requests execute in Rust and reach the real API.
  await page.route(origin + '/api/**', route => route.fulfill({ json: route.request().url().endsWith('/conversations') ? { items: [], hasMore: false } : { enabled: false, configured: false, isConnected: false } }));
  await page.getByRole('region', { name: 'Conversa com a Aegis' }).waitFor();
  assert.equal((await invoke('runtime_info')).backendUrl, origin);
}
async function close() {
  const result = spawnSync('powershell.exe', ['-NoProfile', '-Command', `(Get-Process -Id ${app.pid}).CloseMainWindow()`], { encoding: 'utf8' });
  assert.equal(result.status, 0); await new Promise(r=>setTimeout(r,500));assert.equal(exited,undefined,'Close must hide');
  await invoke('node_exit').catch(()=>{}); await waitUntil(() => exited !== undefined, 15000); assert.equal(exited, 0);
  await browser.close().catch(() => {}); browser = undefined;
}
try {
  await waitUntil(async () => { try { return existsSync(bootstrap) && (await fetch(origin + '/api/health')).ok; } catch { return false; } });
  await open(); assert.equal((await invoke('node_status')).state, 'unpaired');
  await page.getByLabel('Dispositivos / Nodes', { exact: true }).click();
  const panel = page.getByRole('dialog', { name: 'Dispositivos / Nodes', exact: true });
  await panel.getByLabel('Nome do dispositivo').fill('PC CI'); await panel.getByLabel('Código de pareamento').fill(readFileSync(bootstrap, 'utf8'));
  rmSync(bootstrap); await panel.getByRole('button', { name: 'Parear dispositivo', exact: true }).click();
  await panel.getByText('PC CI', { exact: true }).waitFor();
  assert.equal(await panel.getByText('PC CI', { exact: true }).count(), 1);
  await waitUntil(async () => (await invoke('node_transport_status')).transportState === 'online', 15000);
  let identity = await invoke('node_status'); assert.equal(identity.state, 'paired'); const pcId = identity.node.id;
  assert.doesNotMatch(JSON.stringify(identity), /credential|recoveryKey|secretHash/i);
  assert.doesNotMatch(await page.evaluate(() => JSON.stringify(localStorage)), /aegis-node-v1\./);
  const code = await invoke('node_create_pairing_code');
  // A simulated second Android peer exercises the real backend; this does not validate Android storage.
  const attemptId = randomUUID(); const paired = await call('/pair', { attemptId, code: code.code, recoveryKey: randomBytes(32).toString('base64url'), name: 'Android peer CI', platform: 'android', appVersion: identity.node.appVersion, protocolVersion: 1 });
  assert.equal(paired.status, 200); const peerCredential = paired.body.credential;
  const peer = await call('/pair/finalize', { attemptId, credential: peerCredential }); assert.equal(peer.status, 200);
  let peerSocket = await connectPeer(peerCredential, identity.node.appVersion);
  const initialList = await invoke('node_list'); assert.equal(initialList.length, 2);
  assert.equal(initialList.filter(n => n.id === pcId).length, 1);
  await waitUntil(async () => (await call('/' + pcId + '/enable', {}, peerCredential)).status === 200, 15000);
  // Observe presence through the second peer's authenticated HTTP API, outside the WebView.
  await waitUntil(async () => (await call('', undefined, peerCredential)).body.find(n => n.id === pcId)?.availability === 'online', 15000);
  await waitUntil(async () => (await call('', undefined, peerCredential)).body.find(n => n.id === pcId)?.lastHeartbeatAt != null, 10000);
  // Keep real native transport healthy through four negotiated heartbeat cycles.
  for (let cycle = 0; cycle < 4; cycle++) {
    await new Promise(r => setTimeout(r, 2100));
    assert.equal((await invoke('node_transport_status')).transportState, 'online');
    const list = (await call('', undefined, peerCredential)).body;
    assert.equal(list.filter(n => n.id === pcId).length, 1);
    assert.equal(list.find(n => n.id === pcId).availability, 'online');
    assert.equal((await invoke('node_list')).find(n => n.id === pcId).availability, 'online');
  }
  assert.equal(await panel.locator(`[data-node-id="${pcId}"]`).count(), 1);
  assert.equal(await panel.getByText('· Este dispositivo', { exact: true }).count(), 1);
  const nativeCapabilities = [{ name: 'audio.input', version: 1 }, { name: 'audio.output', version: 1 }, { name:'notification.show',version:1 }];
  assert.deepEqual((await invoke('node_list')).find(n => n.id === pcId).capabilities, nativeCapabilities);
  assert.deepEqual((await call('/me', undefined, peerCredential)).body.capabilities, [{ name: 'audio.input', version: 1 }]);
  await invoke('node_set_target_priority', { id: pcId, priority: 10 });
  await invoke('node_set_target_priority', { id: peer.body.id, priority: 20 });
  assert.equal((await invoke('node_list')).find(n => n.id === pcId).targetPriority, 10);
  const resolveTarget = (name, minimumVersion = 1, preferredNodeId = null) => invoke('node_resolve_target', { request: { requiredCapabilities: [{ name, minimumVersion }], preferredNodeId } });
  assert.equal((await resolveTarget('audio.input')).node.id, peer.body.id);
  assert.equal((await resolveTarget('audio.output')).node.id, pcId);
  assert.equal((await resolveTarget('audio.input', 1, pcId)).node.id, pcId);
  assert.equal((await resolveTarget('audio.output', 2)).code, 'no_eligible_node');
  peerSocket.close(1000, 'fixture_offline');
  await waitUntil(async () => (await resolveTarget('audio.input')).node?.id === pcId, 10000);
  peerSocket = await connectPeer(peerCredential, identity.node.appVersion);
  assert.equal((await resolveTarget('audio.input')).node.id, peer.body.id);
  console.log('Windows real native capability advertisement + two different peers + priority/preferred/version + live offline/reconnect resolution PASS.');
  // Regression: the real button invokes native node_test_notification -> HttpApi ->
  // the production controller -> live dispatcher -> native command/result.
  const pcRow = panel.locator(`[data-node-id="${pcId}"]`);
  await pcRow.getByRole('button', {name:'Enviar notificação de teste', exact:true}).click();
  await pcRow.getByRole('status').filter({hasText:'PC CI: success · live_websocket'}).waitFor();
  const nativeNotification = await invoke('node_test_notification',{id:pcId});
  assert.equal(nativeNotification.node.id,pcId);
  assert.equal(nativeNotification.transport,'live_websocket');
  assert.equal(nativeNotification.status,'success');
  console.log('Windows real button -> native HttpApi POST /api/nodes/notifications/test -> production controller -> WebSocket notification.show -> native OS call -> command_result success PASS.');
  const hide = spawnSync('powershell.exe',['-NoProfile','-Command',`(Get-Process -Id ${app.pid}).CloseMainWindow()`],{encoding:'utf8'});
  assert.equal(hide.status,0);await new Promise(r=>setTimeout(r,1000));assert.equal(exited,undefined);
  assert.equal((await call('',undefined,peerCredential)).body.find(n=>n.id===pcId).availability,'online');
  const notification = await call('/notifications/test', {preferredNodeId:pcId,title:'Aegis CI',body:'Native fixture'},peerCredential);
  assert.equal(notification.status,200);assert.equal(notification.body.transport,'live_websocket');assert.equal(notification.body.status,'success');
  const secondary=spawn(exe,[],{env:{...process.env},stdio:'inherit'});let secondaryExit;
  secondary.on('exit',code=>secondaryExit=code);await waitUntil(()=>secondaryExit!==undefined,15000);assert.equal(secondaryExit,0);assert.equal(exited,undefined);
  const settings = await invoke('node_notification_settings');assert.equal(settings.granted,true);
  await invoke('node_set_autostart',{enabled:true});assert.equal((await invoke('node_notification_settings')).autostart,true);
  await invoke('node_set_autostart',{enabled:false});assert.equal((await invoke('node_notification_settings')).autostart,false);
  await close();
  await waitUntil(async () => (await call('', undefined, peerCredential)).body.find(n => n.id === pcId)?.availability === 'offline', 10000);
  await open();
  await waitUntil(async () => (await invoke('node_transport_status')).transportState === 'online', 15000); identity = await invoke('node_status'); assert.equal(identity.node.id, pcId);
  assert.equal((await invoke('node_list')).length, 2);
  assert.deepEqual((await invoke('node_list')).find(n => n.id === pcId).capabilities, nativeCapabilities);
  console.log('Windows real app pairing + secure storage + restart with same NodeId PASS; second peer simulated.');
  await invoke('node_rename', { id: peer.body.id, name: 'Pixel CI' }); assert.equal((await call('/me', undefined, peerCredential)).body.name, 'Pixel CI');
  await invoke('node_set_enabled', { id: peer.body.id, enabled: false }); assert.equal((await call('/me', undefined, peerCredential)).status, 403);
  await invoke('node_set_enabled', { id: peer.body.id, enabled: true }); assert.equal((await call('/me', undefined, peerCredential)).status, 200);
  await invoke('node_set_enabled', { id: pcId, enabled: false }); assert.equal((await invoke('node_status')).state, 'disabled');
  assert.equal((await call('/' + pcId + '/enable', {}, peerCredential)).status, 200);
  await waitUntil(async () => (await invoke('node_transport_status')).transportState === 'online', 45000);
  assert.equal((await invoke('node_status')).node.id, pcId);
  assert.equal((await call('/' + pcId + '/revoke', {}, peerCredential)).status, 200);
  await waitUntil(async () => (await invoke('node_transport_status')).transportState === 'offline', 15000);
  assert.equal((await invoke('node_status')).state, 'revoked');
  await close(); await open(); assert.equal((await invoke('node_status')).state, 'revoked');
  const replacementCode = await call('/pairing-codes', {}, peerCredential); assert.equal(replacementCode.status, 200);
  identity = await invoke('node_pair', { name: 'PC replacement CI', code: replacementCode.body.code });
  assert.equal(identity.state, 'paired'); assert.notEqual(identity.node.id, pcId);
  await invoke('node_revoke', { id: peer.body.id }); assert.equal((await call('/me', undefined, peerCredential)).status, 401);
  await invoke('node_revoke', { id: identity.node.id }); assert.equal((await invoke('node_status')).state, 'revoked');
  await close();
  console.log('Windows real native authenticated transport + heartbeat + independent server presence + close/offline + automatic re-enable + active revoke PASS.');
  console.log('Windows real native list/rename/disable/re-enable/revoke/re-pair, invalid-secret discard and chat independence PASS.');
} finally {
  if (page && exited===undefined) await invoke('node_set_autostart',{enabled:false}).catch(()=>{});
  for (const socket of peerSockets) socket.terminate();
  await browser?.close().catch(() => {});
  if (app && exited === undefined) spawnSync('taskkill', ['/PID', String(app.pid), '/T', '/F'], { stdio: 'ignore' });
  spawnSync('taskkill', ['/PID', String(api.pid), '/T', '/F'], { stdio: 'ignore' });
  rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
