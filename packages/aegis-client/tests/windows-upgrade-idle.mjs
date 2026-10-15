// Layered Windows CI evidence: public/real NSIS upgrade, then the same sources'
// isolated native runtimes using one API database, OS vault and WebView profile.
// Published executables retain their production HTTPS configuration. They are
// never paired; native authentication is exercised only by loopback debug builds.
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';

if (process.platform !== 'win32') throw Error('Requires an isolated real Windows runner.');
const repository = fileURLToPath(new URL('../../../', import.meta.url));
const acceptedVersion = '0.7.0-unstable.11';
const acceptedInstallerUrl = 'https://github.com/phcastello/Aegis/releases/download/node-v0.7.0-unstable.11/Aegis-Windows-x86_64-Setup.exe';
const acceptedInstallerSha256 = 'd1edc78d8ffda6aaae86b270413099805164ec8c9256d737d7ad24349e3c56a2';
const candidateVersion = JSON.parse(readFileSync(join(repository, 'apps/aegis-node/package.json'), 'utf8')).version;
const origin = 'http://127.0.0.1:18104';
const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));
const hash = path => createHash('sha256').update(readFileSync(path)).digest('hex');
function save(path, value) { mkdirSync(dirname(path), { recursive: true }); writeFileSync(path, JSON.stringify(value, null, 2) + '\n'); }
async function waitUntil(action, milliseconds = 120000) {
  const deadline = Date.now() + milliseconds;
  while (!(await action())) { if (Date.now() >= deadline) throw Error('Windows upgrade fixture readiness timed out.'); await sleep(150); }
}
function install(installer, destination) {
  // Pass paths as environment values, without interpolating them into shell code.
  const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
    '$ErrorActionPreference="Stop"; $p=Start-Process -FilePath $env:AEGIS_CI_INSTALLER -ArgumentList @("/S", "/D=$env:AEGIS_CI_INSTALL_DESTINATION") -Wait -PassThru; exit $p.ExitCode'],
  { env: { ...process.env, AEGIS_CI_INSTALLER: installer, AEGIS_CI_INSTALL_DESTINATION: destination }, encoding: 'utf8', timeout: 180000 });
  assert.equal(result.status, 0, 'The actual NSIS installer must complete successfully.');
}
function installedExecutable(destination) {
  const executables = readdirSync(destination).filter(name => /\.exe$/i.test(name) && !/uninstall/i.test(name));
  assert.equal(executables.length, 1, 'The installed executable must be unambiguous.');
  return join(destination, executables[0]);
}
async function installedSmoke(executable, version, profile, sentinel, first) {
  const app = spawn(executable, [], { env: { ...process.env,
    // Only this WebView is offline until CDP supplies private API responses. This
    // changes no host file, TLS validation, system proxy or production endpoint.
    WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--remote-debugging-port=9463 --proxy-server=http://127.0.0.1:9 --proxy-bypass-list=tauri.localhost;localhost;127.0.0.1',
    WEBVIEW2_USER_DATA_FOLDER: profile }, stdio: 'ignore' });
  let exited, browser, page;
  app.on('exit', value => { exited = value; }); app.on('error', () => { exited = -1; });
  try {
    await waitUntil(async () => { if (exited !== undefined) throw Error('Installed runtime exited during upgrade smoke.'); try { return (await fetch('http://127.0.0.1:9463/json/version')).ok; } catch { return false; } }, 30000);
    browser = await chromium.connectOverCDP('http://127.0.0.1:9463');
    await waitUntil(() => { page = browser.contexts().flatMap(context => context.pages())[0]; return Boolean(page); }, 30000);
    await page.route('**/api/**', route => route.fulfill({ json: route.request().url().includes('/chat/') ? { items: [], hasMore: false } : { enabled: false, available: false, configured: false, isConnected: false } }));
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.getByRole('region', { name: 'Conversa com a Aegis' }).waitFor();
    const runtime = await page.evaluate(() => window.__TAURI_INTERNALS__.invoke('runtime_info'));
    assert.equal(runtime.platform, 'windows'); assert.equal(runtime.version, version);
    assert.equal(runtime.backendUrl, 'https://aegis.phcastello.com');
    if (first) await page.evaluate(value => { localStorage.setItem('aegis.fixture.upgrade-sentinel', value); localStorage.setItem('aegis.voice.autoSpeak', 'false'); }, sentinel);
    assert.equal(await page.evaluate(() => localStorage.getItem('aegis.fixture.upgrade-sentinel')), sentinel, 'Installed upgrade must retain the shared WebView storage.');
    await page.evaluate(() => window.__TAURI_INTERNALS__.invoke('node_exit')).catch(() => {});
    await waitUntil(() => exited !== undefined, 15000); assert.equal(exited, 0);
    return { version: runtime.version, platform: runtime.platform, storageSentinelPreserved: true, publishedOriginRetained: true, paired: false };
  } finally {
    await browser?.close().catch(() => {});
    if (exited === undefined) spawnSync('taskkill', ['/PID', String(app.pid), '/T', '/F'], { stdio: 'ignore' });
  }
}
async function runIdle(executable, label, output, plan, bootstrap, expectedNodeId) {
  const child = spawn(process.execPath, [join(repository, 'packages/aegis-client/tests/windows-idle.mjs'), executable, label, output, '600'], { stdio: 'inherit', env: { ...process.env,
    AEGIS_IDLE_EXTERNAL_FIXTURE: '1', AEGIS_IDLE_BOOTSTRAP_FILE: bootstrap,
    AEGIS_IDLE_KEEP_IDENTITY: label === 'before' ? '1' : '0', AEGIS_IDLE_REUSE_IDENTITY: label === 'after' ? '1' : '0',
    AEGIS_IDLE_EXPECT_NODE_ID: expectedNodeId ?? '', AEGIS_IDLE_WEBVIEW_PROFILE: plan.profile,
    AEGIS_IDLE_STORAGE_VALUE: plan.sentinel, AEGIS_IDLE_UPGRADE_VERIFY: label === 'after' ? '1' : '0' } });
  const code = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', resolve); });
  assert.equal(code, 0, `The ${label} native idle and upgrade checks must pass.`);
  return JSON.parse(readFileSync(output, 'utf8'));
}
const [mode, ...args] = process.argv.slice(2);
if (mode === 'install-before') {
  const [candidateDirectoryArgument, destinationArgument, planArgument] = args;
  assert.ok(candidateDirectoryArgument && destinationArgument && planArgument, 'Usage: install-before CANDIDATE_NSIS_DIR DESTINATION PLAN.json');
  const candidateDirectory = resolve(candidateDirectoryArgument), destination = resolve(destinationArgument), planPath = resolve(planArgument);
  const candidateFiles = readdirSync(candidateDirectory).filter(name => /\.exe$/i.test(name)); assert.equal(candidateFiles.length, 1);
  const work = mkdtempSync(join(tmpdir(), 'aegis-windows-upgrade-'));
  const acceptedInstaller = join(work, 'accepted-installer.exe');
  const response = await fetch(acceptedInstallerUrl, {signal:AbortSignal.timeout(60000)}); assert.equal(response.status, 200);
  const bytes = Buffer.from(await response.arrayBuffer()); assert.ok(bytes.length > 0 && bytes.length < 10 * 1024 * 1024);
  assert.equal(createHash('sha256').update(bytes).digest('hex'), acceptedInstallerSha256, 'The immutable public installer must match its published digest.');
  writeFileSync(acceptedInstaller, bytes); install(acceptedInstaller, destination);
  const executable = installedExecutable(destination);
  assert.ok(process.env.LOCALAPPDATA);
  // Tauri's actual default Windows WebView data directory is app_local_data_dir.
  // Use that directory on the disposable runner so deleting AppData during an
  // installer upgrade cannot be hidden by a profile outside the app's storage.
  const profile = join(process.env.LOCALAPPDATA, 'com.aegis.node');
  const plan = { kind: 'aegis-windows-upgrade-ci-v1', work, destination, executable, profile, sentinel: randomUUID(), candidateInstaller: join(candidateDirectory, candidateFiles[0]), acceptedInstallerSha256, acceptedInstallerUrl };
  plan.beforeInstalled = await installedSmoke(executable, acceptedVersion, plan.profile, plan.sentinel, true);
  save(planPath, plan);
  assert.ok(process.env.GITHUB_ENV, 'This preparation step is intended for the isolated CI workflow.');
  appendFileSync(process.env.GITHUB_ENV, `AEGIS_INSTALLED_EXE=${executable}\nAEGIS_UPGRADE_PLAN=${planPath}\n`, 'utf8');
  console.log('Public accepted NSIS digest, actual installation and Windows version 11 smoke PASS; no Node paired to production.');
} else if (mode === 'compare') {
  const [planArgument, beforeArgument, afterArgument, outputArgument] = args;
  assert.ok(planArgument && beforeArgument && afterArgument && outputArgument, 'Usage: compare PLAN.json BEFORE_DEBUG.exe AFTER_DEBUG.exe OUTPUT_DIR');
  const plan = JSON.parse(readFileSync(resolve(planArgument), 'utf8')); assert.equal(plan.kind, 'aegis-windows-upgrade-ci-v1');
  assert.equal(dirname(plan.work), tmpdir()); assert.ok(plan.work.startsWith(join(tmpdir(), 'aegis-windows-upgrade-')));
  const output = resolve(outputArgument); mkdirSync(output, { recursive: true });
  const bootstrap = join(plan.work, 'bootstrap.txt');
  const fixture = spawn('dotnet', ['run', '--project', join(repository, 'backend/tests/Aegis.NodeRuntimeFixture'), '--', bootstrap], { env: { ...process.env, AEGIS_IDLE_FIXTURE: 'before' }, stdio: 'inherit' });
  let fixtureExit; fixture.on('exit', value => { fixtureExit = value; }); fixture.on('error', () => { fixtureExit = -1; });
  try {
    await waitUntil(async () => { if (fixtureExit !== undefined) throw Error('The continuous upgrade API exited.'); try { return existsSync(bootstrap) && (await fetch(origin + '/api/health')).ok; } catch { return false; } });
    const before = await runIdle(resolve(beforeArgument), 'before', join(output, 'windows-idle-before.json'), plan, bootstrap);
    install(plan.candidateInstaller, plan.destination);
    assert.equal(installedExecutable(plan.destination), plan.executable, 'Upgrade must preserve the installation path.');
    const afterInstalled = await installedSmoke(plan.executable, candidateVersion, plan.profile, plan.sentinel, false);
    const policy = await fetch(origin + '/fixture/idle-policy', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ mode: 'after' }) }); assert.equal(policy.status, 200);
    const after = await runIdle(resolve(afterArgument), 'after', join(output, 'windows-idle-after.json'), plan, bootstrap, before.nativeNodeId);
    assert.equal(after.nativeNodeId, before.nativeNodeId); assert.equal(after.reusedIdentity, true); assert.equal(after.inventoryNodeCount, 1);
    save(join(output, 'windows-upgrade.json'), { measuredAt: new Date().toISOString(), kind: 'layered actual NSIS upgrade and isolated native identity migration',
      acceptedVersion, candidateVersion, acceptedInstallerUrl, acceptedInstallerSha256, candidateInstallerSha256: hash(plan.candidateInstaller),
      beforeInstalled: plan.beforeInstalled, afterInstalled, nativeNodeIdPreserved: true, nativeNodeId: after.nativeNodeId, nodeCount: after.inventoryNodeCount, nativeAfterRepaired: false, nativeNotificationPost: after.upgradeNotificationPost,
      sameDisposableBackendProcess: true, sameOsVault: 'com.aegis.node.identity.v1 / installation', samePrivateWebViewProfile: true, usesActualAppLocalDataDirectory: true,
      idleSecondsBefore: before.states.reduce((sum, state) => sum + state.durationSeconds, 0), idleSecondsAfter: after.states.reduce((sum, state) => sum + state.durationSeconds, 0),
      limits: ['Actual public accepted NSIS and candidate NSIS are installed sequentially to the same path; their installed runtime versions and the actual app local data directory are checked. The CI runner is isolated; app storage is never broadly deleted.', 'The published runtime keeps its production HTTPS origin and is never paired. Its WebView uses a process-only offline proxy and private CDP API responses.', 'Authentication and NodeId continuity use the immutable accepted and candidate debug executables compiled for one loopback API and in-memory database. No production Node credential is read or written.', 'This does not exercise signed updater feed/download/install, owner data, physical toast interaction or Windows display scaling. Physical upgrade acceptance remains pending.'] });
    console.log('Actual NSIS 11 -> candidate installation, shared storage, same native NodeId/OS vault/database without a second pairing, and equivalent ten-minute idle windows PASS.');
  } finally {
    if (fixtureExit === undefined) spawnSync('taskkill', ['/PID', String(fixture.pid), '/T', '/F'], { stdio: 'ignore' });
    rmSync(plan.work, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }
} else throw Error('Choose install-before or compare.');
