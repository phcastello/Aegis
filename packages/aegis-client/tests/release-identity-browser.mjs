import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { createServer as createHttpServer } from 'node:http';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { createHash } from 'node:crypto';

// Run from any directory after npm ci in both hosts and @aegis/client:
//   node packages/aegis-client/tests/release-identity-browser.mjs
// IDENTITY_SCREENSHOT_DIR changes the report/PNG destination. A compatible
// Chromium from Playwright's cache or PLAYWRIGHT_CHROMIUM_EXECUTABLE is required.
// --before captures both actual Apps and intentionally fails on absent names;
// a screenshot baseline without the codename never counts as a passing result.
// Exercise both production entry points, including App -> ChatView ->
// ConversationSidebar -> AegisIdentityCard. Only external API/IPC are fixtures.
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const requireNative = createRequire(join(root, 'apps/aegis-node/package.json'));
const { createServer } = await import(requireNative.resolve('vite'));
const phase = process.argv.includes('--before') ? 'before' : 'after';
const output = resolve(process.env.IDENTITY_SCREENSHOT_DIR ?? join(root, '.artifacts/release-identity', phase));
mkdirSync(output, { recursive: true });
const installed = JSON.parse(readFileSync(join(root, 'apps/aegis-node/package.json'), 'utf8')).version;
const codename = "It's Not Stalking. It's a Feature.";
const tagline = "You're terrible at hiding.";
const sourceFiles = [
  'apps/aegis-node/src/main.ts', 'apps/aegis-node/src/App.vue',
  'frontend/aegis-pwa/src/main.ts', 'frontend/aegis-pwa/src/App.vue',
  'packages/aegis-client/src/views/ChatView.vue',
  'packages/aegis-client/src/components/ConversationSidebar.vue',
  'packages/aegis-client/src/components/AegisIdentityCard.vue',
  'packages/aegis-client/src/styles.css',
  ...(phase === 'after' ? ['packages/aegis-client/src/services/releaseIdentity.ts'] : []),
];
const sourceHashes = () => Object.fromEntries(sourceFiles.map(file => [file, createHash('sha256').update(readFileSync(join(root, file))).digest('hex')]));
const report = {
  phase, installed, sourceHashes: sourceHashes(),
  rendering: 'Production web and native main.ts/App.vue and shared components; browser Chromium with simulated native IPC, not installed Android or Windows/WebView2.',
  scaling: 'Root font size 100%/200%, no CSS transform; no Android system-font hardware claim.',
  cases: [], failures: [], pageErrors: [],
};
const fixture = {
  id: 'release-identity-chat', title: 'Conversa existente', createdAt: '2026-10-20T12:00:00Z',
  updatedAt: '2026-10-20T12:00:00Z', messageCount: 1,
  messages: [{ id: 'identity-message', conversationId: 'release-identity-chat', role: 'assistant', content: 'Histórico preservado.', createdAt: '2026-10-20T12:00:00Z' }],
};
const profileList = phase === 'before' ? [{ width: 1100, height: 900, font: 100 }] :
  [320, 360, 412, 800, 1100].flatMap(width => [100, 200].map(font => ({ width, height: 1000, font })));

async function availablePort() {
  const allocator = createHttpServer();
  await new Promise(resolve => allocator.listen(0, '127.0.0.1', resolve));
  const port = allocator.address().port;
  await new Promise(resolve => allocator.close(resolve));
  return port;
}

async function api(route) {
  const path = new URL(route.request().url()).pathname;
  let body = null;
  if (path === '/api/health') body = { status: 'ok' };
  else if (path === '/api/voice/status') body = { enabled: false, available: false };
  else if (path === '/api/voice/transcription/status') body = { enabled: false, configured: false };
  else if (path === '/api/email/status') body = { isConnected: false, emailAddress: null };
  else if (path === '/api/notifications/configuration') body = { enabled: false, publicKey: null };
  else if (path === '/api/chat/conversations') body = { items: [fixture], hasMore: false };
  else if (path === '/api/chat/conversations/release-identity-chat') body = fixture;
  else return route.fulfill({ status: 404, contentType: 'application/json', body: JSON.stringify({ error: `Unknown isolated fixture: ${path}` }) });
  return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
}

async function nativeFixture(context, version, platform = 'android') {
  await context.addInitScript(({ version, platform }) => {
    window.__identityNativeCalls = [];
    window.__identityAvailableVersion = '0.8.0';
    window.__TAURI_INTERNALS__ = { invoke: async (command, args) => {
      window.__identityNativeCalls.push(command);
      if (command === 'runtime_info') return { platform, backendUrl: location.origin, version };
      if (command === 'check_backend') return { status: 'connected', message: null };
      if (command === 'node_status') return { state: 'paired', node: { id: 'identity-device', name: 'Dispositivo preservado' }, error: null };
      if (command === 'node_notification_settings') return { granted: true, pushConfigured: true, autostart: false, pendingActions: 0, failedActions: 0, actionDiagnostics: [], actionDiagnosticsAvailable: true, actionSyncState: 'idle' };
      if (command === 'node_transport_status') return { transportState: 'online', lastError: null };
      if (command === 'node_settings_open') return;
      if (command === 'node_consume_reminder_open') return { reminderId: null };
      if (command === 'check_android_update') return { version: window.__identityAvailableVersion, url: `https://github.com/phcastello/Aegis/releases/download/node-v${window.__identityAvailableVersion}/Aegis-Android-arm64.apk` };
      if (command === 'plugin:updater|check') return { rid: 1, currentVersion: version, version: window.__identityAvailableVersion, rawJson: { platforms: { 'windows-x86_64': { url: `https://github.com/phcastello/Aegis/releases/download/node-v${window.__identityAvailableVersion}/Aegis-Windows-x86_64-Setup.exe` } } } };
      if (command === 'plugin:resources|close') return;
      if (command === 'node_list') return [];
      if (command === 'plugin:opener|open_url') return;
      // Native event registration needs a real Tauri callback bridge; App catches
      // its absence. No event system behavior is under test in this UI patch.
      throw new Error(`Unavailable in isolated identity IPC fixture: ${command}`);
    } };
    localStorage.setItem('aegis.voice.autoSpeak', 'false');
  }, { version, platform });
}

async function openSidebar(page) {
  const sidebar = page.locator('#conversation-sidebar');
  if (await sidebar.getAttribute('aria-hidden') === 'true') await page.getByRole('button', { name: 'Abrir histórico', exact: true }).click();
  await sidebar.waitFor({ state: 'visible' });
  await page.waitForFunction(() => document.querySelector('#conversation-sidebar')?.getAttribute('aria-hidden') === 'false');
  await page.waitForTimeout(220);
  return sidebar;
}

async function measure(page) {
  return page.evaluate(() => {
    const box = element => {
      const rect = element.getBoundingClientRect();
      return { x: rect.x, y: rect.y, width: rect.width, height: rect.height, right: rect.right, bottom: rect.bottom };
    };
    const sidebar = document.querySelector('#conversation-sidebar');
    const identity = sidebar.querySelector('.identity-card');
    const copy = identity.querySelector('.identity-card__copy');
    const toggle = sidebar.querySelector('#sidebar-collapse-button');
    const composer = document.querySelector('.composer');
    const panel = document.querySelector('.chat-panel');
    const codenameNode = identity.querySelector('.identity-card__codename');
    return {
      identity: box(identity), copy: box(copy), toggle: box(toggle), composer: box(composer), chat: box(panel),
      sidebar: box(sidebar), sidebarBackground: getComputedStyle(sidebar).backgroundColor, horizontalOverflow: document.documentElement.scrollWidth > innerWidth + 1,
      identityOverflow: identity.scrollWidth > identity.clientWidth + 1,
      codename: codenameNode ? { ...box(codenameNode), scrollHeight: codenameNode.scrollHeight, clientHeight: codenameNode.clientHeight, fontSize: getComputedStyle(codenameNode).fontSize, textOverflow: getComputedStyle(codenameNode).textOverflow, whiteSpace: getComputedStyle(codenameNode).whiteSpace, color: getComputedStyle(codenameNode).color } : null,
      rootFontSize: getComputedStyle(document.documentElement).fontSize,
    };
  });
}

function contrastRatio(foreground, background) {
  const luminance = value => {
    const channels = value.match(/[\d.]+/g).slice(0, 3).map(Number).map(channel => {
      const normalized = channel / 255;
      return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
    });
    return channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
  };
  const first = luminance(foreground), second = luminance(background);
  return (Math.max(first, second) + 0.05) / (Math.min(first, second) + 0.05);
}

let browser;
const servers = [];
try {
  process.env.npm_package_version = installed;
  for (const host of ['native', 'web']) {
    const appRoot = join(root, host === 'native' ? 'apps/aegis-node' : 'frontend/aegis-pwa');
    const port = await availablePort();
    const server = await createServer({ root: appRoot, configFile: join(appRoot, 'vite.config.ts'), server: { port, host: '127.0.0.1', strictPort: true }, clearScreen: false });
    await server.listen();
    servers.push({ host, url: `http://127.0.0.1:${port}`, server });
  }
  browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
  const targets = servers.flatMap(server => server.host === 'native' && phase === 'after' ?
    [{ ...server, host: 'native-android', platform: 'android' }, { ...server, host: 'native-windows', platform: 'windows' }] : [server]);
  for (const { host, platform, url } of targets) {
    for (const profile of profileList) {
      const name = `${host}-${profile.width}-font${profile.font}`;
      const entry = { name, host, profile, failures: [], screenshots: [] };
      report.cases.push(entry);
      const context = await browser.newContext({ viewport: { width: profile.width, height: profile.height } });
      await context.route('**/api/**', api);
      await context.route('**/*', async route => {
        const requestUrl = new URL(route.request().url());
        if (requestUrl.pathname.startsWith('/api/')) return api(route);
        return requestUrl.hostname === '127.0.0.1' ? route.continue() : route.abort();
      });
      if (host.startsWith('native')) await nativeFixture(context, installed, platform);
      const page = await context.newPage();
      page.on('pageerror', error => report.pageErrors.push(`${name}: ${error.message}`));
      const check = (condition, message) => { if (!condition) entry.failures.push(message); };
      try {
        await page.goto(url);
        await page.locator('.composer textarea').waitFor();
        await page.evaluate(font => document.documentElement.style.fontSize = `${font}%`, profile.font);
        await page.evaluate(() => document.fonts.ready);
        const sidebar = await openSidebar(page);
        const identity = sidebar.getByRole('region', { name: 'Identidade da Aegis', exact: true });
        check(await identity.getByRole('heading', { name: 'Aegis', exact: true }).isVisible(), 'Aegis heading must be visible');
        check(await identity.getByText(`v${installed}`, { exact: true }).isVisible(), `installed version v${installed} missing or duplicated prefix`);
        check(await identity.getByText(codename, { exact: true }).isVisible().catch(() => false), 'full release codename missing from actual App sidebar chain');
        check(!(await identity.innerText()).includes(tagline), 'sidebar must not include secondary tagline');
        entry.open = await measure(page);
        check(!entry.open.horizontalOverflow, 'document horizontal overflow');
        check(!entry.open.identityOverflow, 'identity horizontal overflow');
        check(entry.open.copy.right <= entry.open.toggle.x - 3, 'identity text collides with collapse button');
        check(entry.open.toggle.x >= -1 && entry.open.toggle.right <= profile.width + 1, 'collapse button outside viewport');
        check(entry.open.toggle.width >= 32 && entry.open.toggle.height >= 32, 'collapse button too small');
        check(Math.abs(entry.open.composer.x + entry.open.composer.width / 2 - (entry.open.chat.x + entry.open.chat.width / 2)) <= 2, 'open composer is not centered in conversation');
        if (entry.open.codename) {
          check(entry.open.codename.textOverflow !== 'ellipsis', 'codename truncated by ellipsis');
          check(entry.open.codename.whiteSpace !== 'nowrap', 'codename prevented from wrapping');
          check(parseFloat(entry.open.codename.fontSize) >= (profile.font === 200 ? 23 : 11.5), 'codename font too small or fails 200% scaling');
          check(entry.open.codename.scrollHeight <= entry.open.codename.clientHeight + 1, 'codename vertically clipped');
          entry.codenameContrast = contrastRatio(entry.open.codename.color, entry.open.sidebarBackground);
          check(entry.codenameContrast >= 4.5, 'codename contrast below WCAG AA normal-text threshold');
        }
        const save = async suffix => {
          const filename = `${name}-${suffix}.png`;
          await page.screenshot({ path: join(output, filename), fullPage: true, animations: 'disabled' });
          entry.screenshots.push(filename);
        };
        await save('sidebar-new');
        await sidebar.getByRole('button', { name: 'Fechar histórico', exact: true }).click();
        await page.waitForTimeout(250);
        check(await sidebar.getAttribute('aria-hidden') === 'true', 'collapse button did not close sidebar');
        check(await page.getByRole('button', { name: 'Abrir histórico', exact: true }).isVisible(), 'collapsed sidebar opener inaccessible');
        entry.closed = await measure(page);
        check(!entry.closed.horizontalOverflow, 'collapsed document horizontal overflow');
        const closedCenter = entry.closed.composer.x + entry.closed.composer.width / 2;
        check(Math.abs(closedCenter - (entry.closed.chat.x + entry.closed.chat.width / 2)) <= 2, 'collapsed composer is not centered in conversation');
        await save('collapsed');
        await openSidebar(page);
        await sidebar.getByRole('button', { name: 'Conversa existente', exact: true }).click();
        await page.getByText('Histórico preservado.', { exact: true }).waitFor();
        await openSidebar(page);
        check(await identity.getByText(codename, { exact: true }).isVisible().catch(() => false), 'codename missing with existing conversation');
        entry.existing = await measure(page);
        check(Math.abs(entry.existing.composer.x - entry.open.composer.x) <= 1 && Math.abs(entry.existing.composer.width - entry.open.composer.width) <= 1, 'composer horizontal position changes with existing conversation');
        await save('sidebar-existing');
        await sidebar.getByRole('button', { name: 'Nova conversa', exact: true }).click();
        await openSidebar(page);
        check(await identity.getByText(codename, { exact: true }).isVisible().catch(() => false), 'codename missing after new conversation');
        await sidebar.getByRole('button', { name: 'Configurações da Aegis', exact: true }).click();
        const about = page.getByRole('dialog', { name: 'Configurações', exact: true });
        await about.getByRole('button', { name: 'Sobre', exact: true }).click();
        check(await about.getByText(codename, { exact: true }).isVisible().catch(() => false), 'full release codename missing in About');
        check((await about.getByText(tagline, { exact: true }).count()) <= 1, 'About tagline duplicated');
        if (phase === 'after') check(await about.getByText(tagline, { exact: true }).isVisible(), 'About secondary tagline missing');
        check((await about.getByRole('button', { name: 'Verificar atualizações', exact: true }).count()) === 0, 'About must not duplicate update check');
        check((await about.innerText()).includes(installed), 'About omits installed/build version');
        check(!(await about.innerText()).includes('0.8.0'), 'available updater version substituted for installed identity');
        entry.about = await about.evaluate(element => ({ scrollWidth: element.scrollWidth, clientWidth: element.clientWidth }));
        check(entry.about.scrollWidth <= entry.about.clientWidth + 1, 'About horizontal overflow');
        await save('about');
        if (host.startsWith('native')) check((await page.evaluate(() => window.__identityNativeCalls)).includes(platform === 'windows' ? 'plugin:updater|check' : 'check_android_update'), 'newer available-version fixture not reached');
      } catch (error) {
        entry.failures.push(error.message);
        await page.screenshot({ path: join(output, `${name}-failure.png`), fullPage: true }).catch(() => {});
      } finally {
        report.failures.push(...entry.failures.map(message => `${name}: ${message}`));
        await context.close();
      }
    }
  }
  if (phase === 'after') {
    const nativeUrl = servers.find(server => server.host === 'native').url;
    report.runtimeVersions = [];
    for (const runtimeVersion of ['0.7.0', '0.7.2', 'v0.7.1', undefined, 'not-a-version', '0.8.0']) {
      const context = await browser.newContext({ viewport: { width: 360, height: 1000 } });
      await context.route('**/api/**', api);
      await nativeFixture(context, runtimeVersion);
      const page = await context.newPage();
      const versionCase = { runtimeVersion: runtimeVersion ?? null, failures: [] };
      report.runtimeVersions.push(versionCase);
      try {
        await page.goto(nativeUrl);
        await page.locator('.composer textarea').waitFor();
        const sidebar = await openSidebar(page);
        const identity = sidebar.getByRole('region', { name: 'Identidade da Aegis', exact: true });
        const known = typeof runtimeVersion === 'string' && /^v?0\.7\./.test(runtimeVersion);
        const valid = known || runtimeVersion === '0.8.0';
        const expectedVersion = valid ? `v${runtimeVersion.replace(/^v/, '')}` : 'Versão indisponível';
        const expectedCodename = known ? codename : 'Codinome indisponível';
        assert.equal(await identity.getByText(expectedVersion, { exact: true }).isVisible(), true, `version fallback/normalization for ${runtimeVersion}`);
        assert.equal(await identity.getByText(expectedCodename, { exact: true }).isVisible(), true, `family/fallback for ${runtimeVersion}`);
        assert.equal((await identity.innerText()).includes('vv'), false, 'duplicate version prefix');
        await sidebar.getByRole('button', { name: 'Configurações da Aegis', exact: true }).click();
        const about = page.getByRole('dialog', { name: 'Configurações', exact: true });
        await about.getByRole('button', { name: 'Sobre', exact: true }).click();
        assert.equal(await about.getByText(expectedVersion, { exact: true }).isVisible(), true);
        assert.equal(await about.getByText(expectedCodename, { exact: true }).isVisible(), true);
        assert.equal(await about.getByText(tagline, { exact: true }).count(), known ? 1 : 0);
        if (!valid || !known) await page.screenshot({ path: join(output, `native-runtime-${runtimeVersion ?? 'missing'}-about.png`), fullPage: true });
      } catch (error) {
        versionCase.failures.push(error.message);
        report.failures.push(`Runtime ${runtimeVersion}: ${error.message}`);
      } finally { await context.close(); }
    }
  }
  report.failures.push(...report.pageErrors.map(message => `Page error: ${message}`));
  assert.deepEqual(sourceHashes(), report.sourceHashes, 'Product source changed during captures; repeat against a stable tree.');
  assert.equal(report.failures.length, 0, report.failures.join('\n'));
  console.log(`${report.cases.length} production App identity scenarios PASS; captures: ${output}`);
} finally {
  writeFileSync(join(output, 'report.json'), JSON.stringify(report, null, 2) + '\n');
  await browser?.close();
  for (const { server } of servers) await server.close();
}
