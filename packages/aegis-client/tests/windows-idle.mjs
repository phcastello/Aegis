// Real Windows/WebView2 idle measurement against the disposable loopback Node API.
// Never point this executable at a production backend. Instrumentation is injected
// by CDP in this test process; none of these hooks are compiled into the app.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir, cpus, totalmem, release } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { installWindowsIpcObserver } from './windows-cdp-observer.mjs';

if (process.platform !== 'win32') throw Error('This measurement requires a real Windows runner and WebView2.');
const [executable, label, outputArgument, secondsArgument = '600'] = process.argv.slice(2);
const seconds = Number(secondsArgument);
if (!executable || !['before', 'after'].includes(label) || !Number.isFinite(seconds) || seconds < 600)
  throw Error('Usage: node windows-idle.mjs EXE before|after [OUTPUT.json] [SECONDS>=600]');
const repository = fileURLToPath(new URL('../../../', import.meta.url));
const exe = resolve(executable);
const output = resolve(outputArgument ?? join(repository, 'artifacts/v0.7.0-completion', `windows-idle-${label}.json`));
const port = Number(process.env.AEGIS_IDLE_CDP_PORT ?? '9462');
assert.ok(Number.isInteger(port) && port > 1024 && port < 65536);
const origin = 'http://127.0.0.1:18104';
const work = mkdtempSync(join(tmpdir(), 'aegis-native-idle-'));
const externalFixture = process.env.AEGIS_IDLE_EXTERNAL_FIXTURE === '1';
const reuseIdentity = process.env.AEGIS_IDLE_REUSE_IDENTITY === '1';
const keepIdentity = process.env.AEGIS_IDLE_KEEP_IDENTITY === '1';
const bootstrap = process.env.AEGIS_IDLE_BOOTSTRAP_FILE ?? join(work, 'bootstrap.txt');
const fixture = externalFixture ? null : spawn('dotnet', ['run', '--project', join(repository, 'backend/tests/Aegis.NodeRuntimeFixture'), '--', bootstrap], {
  env: { ...process.env, AEGIS_IDLE_FIXTURE: label }, stdio: ['ignore', 'ignore', 'pipe']
});
let fixtureExit, app, appExit, browser, page, pairedId;
let fixtureError = '';
fixture?.stderr.on('data', value => { fixtureError = (fixtureError + String(value)).slice(-4000); });
fixture?.on('exit', value => { fixtureExit = value; });
fixture?.on('error', () => { fixtureExit = -1; });
const states = [];
const sleep = milliseconds => new Promise(done => setTimeout(done, milliseconds));
async function waitUntil(action, duration = 90000) {
  const deadline = Date.now() + duration;
  while (!(await action())) { if (Date.now() >= deadline) throw Error('Native idle fixture readiness timed out.'); await sleep(200); }
}
async function invoke(command, args) { return page.evaluate(({ command, args }) => window.__TAURI_INTERNALS__.invoke(command, args), { command, args }); }
async function fixtureMetrics() {
  const response = await fetch(origin + '/fixture/idle', {signal:AbortSignal.timeout(5000)});
  assert.equal(response.status, 200, 'The isolated fixture must expose aggregate measurement counters.');
  return response.json();
}
function difference(before, after) {
  return Object.fromEntries([...new Set([...Object.keys(before), ...Object.keys(after)])].sort().map(key => [key, Math.max(0, Number(after[key] ?? 0) - Number(before[key] ?? 0))]));
}
function powershell(script) {
  const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', script], { encoding: 'utf8', timeout: 20000, windowsHide: true });
  if (result.status !== 0) throw Error('Could not collect the Windows process measurement.');
  return result.stdout.trim();
}
function processSnapshot() {
  assert.ok(app && Number.isInteger(app.pid) && app.pid > 0);
  // Only the app's descendants are collected. No command lines, environment,
  // account data, other applications, or RDP traffic enter the result.
  const script = `$rootProcessId=${app.pid}; $all=@(Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name); $ids=New-Object 'System.Collections.Generic.HashSet[int]'; [void]$ids.Add($rootProcessId); do { $changed=$false; foreach($item in $all) { if($ids.Contains([int]$item.ParentProcessId) -and $ids.Add([int]$item.ProcessId)) { $changed=$true } } } while($changed); $rows=@(foreach($item in $all) { if($ids.Contains([int]$item.ProcessId)) { $p=Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue; if($p) { [pscustomobject]@{id=[int]$item.ProcessId; name=$item.Name; cpuSeconds=$p.TotalProcessorTime.TotalSeconds; workingSetBytes=$p.WorkingSet64} } } }); ConvertTo-Json -InputObject $rows -Compress`;
  const parsed = JSON.parse(powershell(script));
  const rows = Array.isArray(parsed) ? parsed : [parsed];
  assert.ok(rows.some(row => row.id === app.pid), 'The native app process must remain alive.');
  assert.ok(rows.every(row => Number.isFinite(row.cpuSeconds)), 'CPU unavailable must not be reported as zero.');
  return rows;
}
function cpuDelta(previous, current) {
  const before = new Map(previous.map(row => [row.id, row]));
  let appSeconds = 0, webViewSeconds = 0, otherChildSeconds = 0;
  for (const row of current) {
    const delta = Math.max(0, row.cpuSeconds - (before.get(row.id)?.cpuSeconds ?? 0));
    if (row.id === app.pid) appSeconds += delta;
    else if (/^msedgewebview2(?:\.exe)?$/i.test(row.name)) webViewSeconds += delta;
    else otherChildSeconds += delta;
  }
  return { appSeconds, webViewSeconds, otherChildSeconds, disappeared: previous.filter(row => !current.some(item => item.id === row.id)).length };
}
async function openDevices() {
  const legacy = page.getByRole('button', { name: 'Dispositivos / Nodes', exact: true });
  if (await legacy.count()) { await legacy.click(); await page.getByRole('dialog', { name: 'Dispositivos / Nodes', exact: true }).waitFor(); return; }
  await page.getByRole('button', { name: 'Configurações da Aegis', exact: true }).click();
  const settings = page.getByRole('dialog', { name: 'Configurações', exact: true }); await settings.waitFor();
  await settings.getByRole('button', { name: 'Dispositivos', exact: true }).click();
}
async function closeDevices() {
  const modern = page.getByRole('button', { name: 'Fechar configurações', exact: true });
  if (await modern.count()) await modern.click();
  else await page.getByRole('dialog', { name: 'Dispositivos / Nodes', exact: true }).getByRole('button', { name: 'Fechar', exact: true }).click();
  await page.getByRole('dialog').waitFor({ state: 'hidden' });
}
async function rendererSnapshot() {
  return page.evaluate(() => ({
    ipc: { ...window.__aegisIpc.counts }, ipcObservation: {fetchWrapped:window.__aegisIpc.fetchWrapped,bridgeWrapped:window.__aegisIpc.bridgeWrapped,fetchFailed:window.__aegisIpc.fetchFailed},
    intervals: [...window.__aegisIdle.intervals.values()].map(value => value.delay).sort((a,b) => a-b),
    timeouts: [...window.__aegisIdle.timeouts.values()].filter(value => value.deadline > performance.now()).map(value => value.delay).sort((a,b) => a-b),
    runningAnimations: document.getAnimations().filter(animation => animation.playState === 'running').map(animation => ({ name: animation.animationName ?? 'script', iterations: String(animation.effect?.getTiming().iterations ?? '') })),
    visibility: document.visibilityState,
    reducedMotion: matchMedia('(prefers-reduced-motion: reduce)').matches
  }));
}
try {
  await waitUntil(async () => { if (fixtureExit !== undefined) throw Error(`Disposable fixture exited (${fixtureExit}).`); try { return (reuseIdentity || existsSync(bootstrap)) && (await fetch(origin + '/api/health', {signal:AbortSignal.timeout(3000)})).ok; } catch { return false; } });
  await fixtureMetrics();
  app = spawn(exe, [], { env: { ...process.env, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: `--remote-debugging-port=${port}`, WEBVIEW2_USER_DATA_FOLDER: process.env.AEGIS_IDLE_WEBVIEW_PROFILE ?? join(work, 'webview') }, stdio: 'ignore' });
  app.on('exit', value => { appExit = value; }); app.on('error', () => { appExit = -1; });
  await waitUntil(async () => { if (appExit !== undefined) throw Error(`Native app exited (${appExit}).`); try { return (await fetch(`http://127.0.0.1:${port}/json/version`, {signal:AbortSignal.timeout(3000)})).ok; } catch { return false; } }, 30000);
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  await waitUntil(() => { page = browser.contexts().flatMap(context => context.pages())[0]; return Boolean(page); }, 30000);
  await page.route(origin + '/api/**', async route => {
    const path = new URL(route.request().url()).pathname;
    if (path.startsWith('/api/chat/')) return route.fulfill({ json: { items: [], hasMore: false } });
    if (path.startsWith('/api/voice/') || path === '/api/email/status') return route.fulfill({ json: { enabled: false, available: false, configured: false, isConnected: false } });
    return route.continue(); // Rust's native Node HTTP/WebSocket requests are never intercepted.
  });
  await page.addInitScript(installWindowsIpcObserver);
  await page.addInitScript(() => {
    localStorage.setItem('aegis.voice.autoSpeak', 'false');
    const state = window.__aegisIdle = { intervals: new Map(), timeouts: new Map() };
    const originalInterval = window.setInterval.bind(window), originalClearInterval = window.clearInterval.bind(window);
    const originalTimeout = window.setTimeout.bind(window), originalClearTimeout = window.clearTimeout.bind(window);
    window.setInterval = (callback, delay, ...args) => { const id = originalInterval(callback, delay, ...args); state.intervals.set(id, { delay: Number(delay) || 0 }); return id; };
    window.clearInterval = id => { state.intervals.delete(id); originalClearInterval(id); };
    window.setTimeout = (callback, delay, ...args) => {
      const duration = Math.max(0, Number(delay) || 0);
      let id;
      const handler = typeof callback === 'function' ? (...values) => { state.timeouts.delete(id); callback(...values); } : callback;
      id = originalTimeout(handler, delay, ...args); state.timeouts.set(id, { delay: duration, deadline: performance.now() + duration }); return id;
    };
    window.clearTimeout = id => { state.timeouts.delete(id); originalClearTimeout(id); };

  });
  await page.reload({ waitUntil: 'domcontentloaded' });
  await page.getByRole('region', { name: 'Conversa com a Aegis' }).waitFor();
  const runtime = await invoke('runtime_info'); assert.equal(runtime.platform, 'windows'); assert.equal(runtime.backendUrl, origin);
  const expectedVersion = label === 'before' ? '0.7.0-unstable.11' : JSON.parse(readFileSync(join(repository, 'apps/aegis-node/package.json'), 'utf8')).version;
  assert.equal(runtime.version, expectedVersion, 'The immutable accepted baseline and candidate must not accidentally run the same executable.');
  // Previous isolated steps may have left a revoked vault record. Native status
  // authenticates and discards any record from the preceding disposable API.
  const startingIdentity = await invoke('node_status');
  let identity;
  if (reuseIdentity) {
    assert.equal(startingIdentity.state, 'paired', 'Upgrade must retain the old native identity without a second pairing.');
    assert.equal(startingIdentity.node.id, process.env.AEGIS_IDLE_EXPECT_NODE_ID);
    identity = startingIdentity;
  } else {
    assert.ok(['unpaired', 'revoked'].includes(startingIdentity.state), 'An idle run must pair afresh with this disposable API.');
    identity = await invoke('node_pair', { name: 'Aegis idle fixture', code: readFileSync(bootstrap, 'utf8') }); rmSync(bootstrap);
  }
  assert.equal(identity.state, 'paired'); pairedId = identity.node.id;
  const inventory = await invoke('node_list');
  assert.deepEqual(inventory.map(node => node.id), [pairedId], 'The disposable upgrade database must contain exactly the original Node.');
  if (process.env.AEGIS_IDLE_STORAGE_VALUE) {
    const stored = await page.evaluate(() => localStorage.getItem('aegis.fixture.upgrade-sentinel'));
    assert.equal(stored, process.env.AEGIS_IDLE_STORAGE_VALUE, 'The same WebView storage must survive both installer and runtime upgrades.');
  }
  await waitUntil(async () => (await invoke('node_transport_status')).transportState === 'online', 30000);
  await page.reload({ waitUntil: 'domcontentloaded' }); await page.getByRole('region', { name: 'Conversa com a Aegis' }).waitFor();
  await page.evaluate(()=>{window.__aegisIpc.counts={};});
  await invoke('node_status');
  assert.ok(await page.evaluate(()=>(window.__aegisIpc.counts.node_status??0)>0), 'The real native IPC observer must be working before reporting polling counts.');
  const pageErrors = []; page.on('pageerror', error => { pageErrors.push(error.name); });
  const cdp = await page.context().newCDPSession(page); await cdp.send('Performance.enable'); await cdp.send('Network.enable');
  const webRequests = new Set(); let pageRequests = 0, pageResponseBytes = 0;
  cdp.on('Network.requestWillBeSent', event => { if (event.request.url.startsWith(origin + '/')) { webRequests.add(event.requestId); pageRequests++; } });
  cdp.on('Network.loadingFinished', event => { if (webRequests.delete(event.requestId)) pageResponseBytes += event.encodedDataLength; });
  const metric = async name => (await cdp.send('Performance.getMetrics')).metrics.find(value => value.name === name)?.value ?? null;
  for (const state of ['window_open', 'devices_open', 'devices_closed', 'tray']) {
    if (state === 'devices_open') await openDevices();
    if (state === 'devices_closed') await closeDevices();
    if (state === 'tray') { assert.equal(powershell(`(Get-Process -Id ${app.pid}).CloseMainWindow()`), 'True'); assert.equal(appExit, undefined, 'Closing the window must keep the tray runtime alive.'); }
    await sleep(2500); // Exclude opening/closing work; both versions use the same settling window.
    await page.evaluate(() => { window.__aegisIpc.counts = {}; });
    const beforeFixture = await fixtureMetrics(), beforeRenderer = await rendererSnapshot(), beforeTask = await metric('TaskDuration');
    const startPageRequests = pageRequests, startPageBytes = pageResponseBytes;
    let previousProcesses = processSnapshot(); const initialProcesses = previousProcesses;
    const started = Date.now(), deadline = started + seconds * 250;
    const cpu = { appSeconds: 0, webViewSeconds: 0, otherChildSeconds: 0, disappeared: 0 };
    while (Date.now() < deadline) {
      await sleep(Math.min(10000, deadline - Date.now()));
      if (appExit !== undefined || fixtureExit !== undefined) throw Error('The app or disposable API exited during measurement.');
      const currentProcesses = processSnapshot(), delta = cpuDelta(previousProcesses, currentProcesses);
      for (const key of Object.keys(cpu)) cpu[key] += delta[key];
      previousProcesses = currentProcesses;
    }
    const durationSeconds = (Date.now() - started) / 1000;
    const afterTask = await metric('TaskDuration'), afterRenderer = await rendererSnapshot();
    const nativeTransportState = (await invoke('node_transport_status')).transportState;
    const counts = difference(beforeFixture, await fixtureMetrics());
    const jsonPayloadBytes = (counts.node_transport_payload_bytes_received_total ?? 0) + (counts.node_transport_payload_bytes_sent_total ?? 0);
    const httpBodyBytes = (counts.http_request_body_bytes ?? 0) + (counts.http_response_body_bytes ?? 0);
    states.push({ state, durationSeconds, nativeTransportState, fixtureCounters: counts, jsonPayloadBytesPerMinute: jsonPayloadBytes * 60 / durationSeconds, httpBodyBytesPerMinute: httpBodyBytes * 60 / durationSeconds,
      requestsPerMinute: (counts.http_requests ?? 0) * 60 / durationSeconds, heartbeatsPerMinute: (counts.node_transport_heartbeats_total ?? 0) * 60 / durationSeconds,
      cpu: { ...cpu, appPercentOfOneCore: 100 * cpu.appSeconds / durationSeconds, webViewPercentOfOneCore: 100 * cpu.webViewSeconds / durationSeconds, processSampleSeconds: 10, childProcessesAtStart: initialProcesses.filter(row => row.id !== app.pid).length, childProcessesAtEnd: previousProcesses.filter(row => row.id !== app.pid).length },
      rendererTaskSeconds: beforeTask === null || afterTask === null ? null : afterTask - beforeTask,
      renderer: { before: beforeRenderer, after: afterRenderer }, pageNetwork: { requests: pageRequests - startPageRequests, encodedResponseBytes: pageResponseBytes - startPageBytes } });
    console.log(`${label}: ${state}, ${durationSeconds.toFixed(1)}s, app CPU ${cpu.appSeconds.toFixed(3)}s, WebView CPU ${cpu.webViewSeconds.toFixed(3)}s, JSON payload ${(jsonPayloadBytes * 60 / durationSeconds).toFixed(1)} bytes/min`);
  }
  assert.ok(states.reduce((sum,state) => sum + state.durationSeconds, 0) >= 600);
  mkdirSync(dirname(output), { recursive: true });
  writeFileSync(output, JSON.stringify({ measuredAt: new Date().toISOString(), kind: 'real Windows application and WebView2, disposable loopback API', label, appVersion: runtime.version, nativeNodeId: pairedId, reusedIdentity: reuseIdentity, inventoryNodeCount: inventory.length, storageSentinelPreserved: Boolean(process.env.AEGIS_IDLE_STORAGE_VALUE),
    hardware: { platform: process.platform, osRelease: release(), cpuModel: cpus()[0]?.model, logicalCpus: cpus().length, memoryBytes: totalmem(), webViewBrowser: browser.version() },
    configuration: { fixture: 'Aegis.NodeRuntimeFixture', heartbeatSeconds: label === 'before' ? 25 : 120, timeoutSeconds: label === 'before' ? 75 : 360, persistSeconds: label === 'before' ? 120 : 300, sameExecutableMode: 'debug with loopback API compiled in', durationRequestedSeconds: seconds },
    limits: ['CPU comes from the native app and its WebView2 descendant processes, not RDP or whole-machine CPU.', 'Fixture network bytes count HTTP bodies and WebSocket JSON payloads; no TCP framing, WebSocket framing, TLS, proxy, FCM or physical network bytes are measured.', 'Page network metrics exclude Rust native HTTP/WebSocket; those are counted separately by the fixture.', 'Each version runs four sequential states for at least 150 seconds each. Process accounting is sampled every 10 seconds; disappeared processes can make child CPU an underestimate.', 'Timers and IPC are observed through CDP injection in this CI process. This is a real Windows runtime measurement, not physical owner acceptance or an Android battery measurement.'], states }, null, 2) + '\n');
  console.log(output);
  if (process.env.AEGIS_IDLE_UPGRADE_VERIFY === '1') {
    const post = await invoke('node_test_notification', { id: pairedId });
    assert.equal(post.status, 'success', 'The upgraded native identity must still receive and post a real notification.');
    assert.equal(post.transport, 'live_websocket');
    const recorded = JSON.parse(readFileSync(output, 'utf8'));
    recorded.upgradeNotificationPost = {status:post.status,transport:post.transport,userReadConfirmed:false};
    writeFileSync(output, JSON.stringify(recorded,null,2) + '\n');
    console.log('Upgraded native identity: authenticated live notification posted by Windows PASS; no human-read assertion.');
  }
  // Preserve the measured artifact even when an acceptance assertion fails.
  assert.deepEqual(pageErrors, [], 'The app must remain usable during idle measurement.');
  for (const sample of states) {
    assert.ok(!sample.renderer.after.ipcObservation.fetchFailed || sample.renderer.after.ipcObservation.bridgeWrapped, 'IPC fallback must remain observable; missing instrumentation is not zero polling.');
    assert.equal(sample.nativeTransportState, 'online', `${sample.state}: a quiet but disconnected app is not a valid idle-delivery baseline.`);
    assert.ok(Number.isFinite(sample.rendererTaskSeconds), 'Missing renderer CPU metrics cannot be reported as zero consumption.');
    if(label==='before'&&sample.state==='devices_open')assert.ok((sample.renderer.after.ipc.node_list??0)>0,'The accepted visible panel must expose its real polling to the IPC observer.');
    assert.ok((sample.fixtureCounters.node_transport_heartbeats_total ?? 0) > 0, `${sample.state}: the real negotiated channel must remain healthy and instrumented.`);
  }
  if (label === 'after') for (const sample of states) {
    if (sample.state !== 'devices_open') assert.equal(sample.renderer.after.ipc.node_list ?? 0, 0, `${sample.state}: closed settings must not poll the device inventory.`);
    assert.equal(sample.renderer.after.runningAnimations.filter(animation => animation.iterations === 'Infinity').length, 0, `${sample.state}: quiet UI must not animate indefinitely.`);
    assert.ok((sample.fixtureCounters.node_transport_connections_total ?? 0) <= 1, `${sample.state}: stable networking must not cause a reconnect loop.`);
  }
} finally {
  if (page && appExit === undefined) { if (pairedId && !keepIdentity) await invoke('node_revoke', { id: pairedId }).catch(() => {}); await invoke('node_exit').catch(() => {}); await waitUntil(() => appExit !== undefined, 15000).catch(() => {}); }
  await browser?.close().catch(() => {});
  if (app && appExit === undefined) spawnSync('taskkill', ['/PID', String(app.pid), '/T', '/F'], { stdio: 'ignore' });
  if (fixture && fixtureExit === undefined) spawnSync('taskkill', ['/PID', String(fixture.pid), '/T', '/F'], { stdio: 'ignore' });
  rmSync(work, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  // Full fixture diagnostics stay in CI, never in the measured application's UI.
  if (fixtureExit && fixtureError) { mkdirSync(dirname(output),{recursive:true}); writeFileSync(output.replace(/\.json$/i,'')+'.fixture.log',fixtureError); console.error('Disposable measurement fixture failed; inspect the fixture log artifact.'); }
}
