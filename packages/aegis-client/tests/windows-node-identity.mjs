// Real Windows app + Credential Manager + loopback ASP.NET Node API. No production account.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { randomBytes, randomUUID } from 'node:crypto';
import { chromium } from 'playwright-core';
if (process.platform !== 'win32') throw Error('Requires a real Windows runner.');
const work = mkdtempSync(join(tmpdir(), 'aegis-node-identity-'));
const bootstrap = join(work, 'temporary-code.txt');
const exe = resolve(process.argv[2]);
const api = spawn('dotnet', ['run', '--project', '../../backend/tests/Aegis.NodeRuntimeFixture', '--', bootstrap], { stdio: 'inherit' });
let app, browser, page, exited;
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
  assert.equal(result.status, 0); await waitUntil(() => exited !== undefined, 15000); assert.equal(exited, 0);
  await browser.close().catch(() => {}); browser = undefined;
}
try {
  await waitUntil(async () => { try { return existsSync(bootstrap) && (await fetch(origin + '/api/health')).ok; } catch { return false; } });
  await open(); assert.equal((await invoke('node_status')).state, 'unpaired');
  await page.getByLabel('Dispositivos / Nodes', { exact: true }).click();
  const panel = page.getByRole('dialog', { name: 'Dispositivos / Nodes', exact: true });
  await panel.getByLabel('Nome do dispositivo').fill('PC CI'); await panel.getByLabel('Código de pareamento').fill(readFileSync(bootstrap, 'utf8'));
  rmSync(bootstrap); await panel.getByRole('button', { name: 'Parear dispositivo', exact: true }).click();
  await panel.getByText('PC CI', { exact: true }).first().waitFor();
  let identity = await invoke('node_status'); assert.equal(identity.state, 'paired'); const pcId = identity.node.id;
  assert.doesNotMatch(JSON.stringify(identity), /credential|recoveryKey|secretHash/i);
  assert.doesNotMatch(await page.evaluate(() => JSON.stringify(localStorage)), /aegis-node-v1\./);
  const code = await invoke('node_create_pairing_code');
  // A simulated second Android peer exercises the real backend; this does not validate Android storage.
  const attemptId = randomUUID(); const paired = await call('/pair', { attemptId, code: code.code, recoveryKey: randomBytes(32).toString('base64url'), name: 'Android peer CI', platform: 'android', appVersion: identity.node.appVersion, protocolVersion: 1 });
  assert.equal(paired.status, 200); const peerCredential = paired.body.credential;
  const peer = await call('/pair/finalize', { attemptId, credential: peerCredential }); assert.equal(peer.status, 200);
  assert.equal((await invoke('node_list')).length, 2);
  await close(); await open(); identity = await invoke('node_status'); assert.equal(identity.node.id, pcId);
  assert.equal((await invoke('node_list')).length, 2);
  console.log('Windows real app pairing + secure storage + restart with same NodeId PASS; second peer simulated.');
  await invoke('node_rename', { id: peer.body.id, name: 'Pixel CI' }); assert.equal((await call('/me', undefined, peerCredential)).body.name, 'Pixel CI');
  await invoke('node_set_enabled', { id: peer.body.id, enabled: false }); assert.equal((await call('/me', undefined, peerCredential)).status, 403);
  await invoke('node_set_enabled', { id: peer.body.id, enabled: true }); assert.equal((await call('/me', undefined, peerCredential)).status, 200);
  await invoke('node_set_enabled', { id: pcId, enabled: false }); assert.equal((await invoke('node_status')).state, 'disabled');
  assert.equal((await call('/' + pcId + '/enable', {}, peerCredential)).status, 200);
  assert.equal((await invoke('node_status')).node.id, pcId);
  assert.equal((await call('/' + pcId + '/revoke', {}, peerCredential)).status, 200);
  assert.equal((await invoke('node_status')).state, 'revoked');
  await close(); await open(); assert.equal((await invoke('node_status')).state, 'revoked');
  const replacementCode = await call('/pairing-codes', {}, peerCredential); assert.equal(replacementCode.status, 200);
  identity = await invoke('node_pair', { name: 'PC replacement CI', code: replacementCode.body.code });
  assert.equal(identity.state, 'paired'); assert.notEqual(identity.node.id, pcId);
  await invoke('node_revoke', { id: peer.body.id }); assert.equal((await call('/me', undefined, peerCredential)).status, 401);
  await invoke('node_revoke', { id: identity.node.id }); assert.equal((await invoke('node_status')).state, 'revoked');
  await close();
  console.log('Windows real native list/rename/disable/re-enable/revoke/re-pair, invalid-secret discard and chat independence PASS.');
} finally {
  await browser?.close().catch(() => {});
  if (app && exited === undefined) spawnSync('taskkill', ['/PID', String(app.pid), '/T', '/F'], { stdio: 'ignore' });
  spawnSync('taskkill', ['/PID', String(api.pid), '/T', '/F'], { stdio: 'ignore' });
  rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}
