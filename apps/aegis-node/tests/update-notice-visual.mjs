// Run explicitly: this renders the real Vue SFCs and production styles with an
// isolated update-state fixture. It never invokes Tauri, downloads or installs.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { createServer as createPortAllocator } from 'node:net';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';
import vue from '@vitejs/plugin-vue';

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = resolve(appRoot, '../..');
const clientRequire = createRequire(join(repoRoot, 'packages/aegis-client/package.json'));
const { chromium } = clientRequire('playwright-core');
const options = { phase: 'after', desktopOnly: false, output: null };
for (let index = 2; index < process.argv.length; index++) {
  const argument = process.argv[index];
  if (argument === '--phase') options.phase = process.argv[++index];
  else if (argument === '--output') options.output = process.argv[++index];
  else if (argument === '--desktop-only') options.desktopOnly = true;
  else throw new Error(`Unknown argument: ${argument}`);
}
assert.ok(['before', 'after'].includes(options.phase), '--phase must be before or after');
const output = resolve(options.output ?? join(repoRoot, 'artifacts/v0.7.0-completion/update-notice-visual', options.phase));
mkdirSync(output, { recursive: true });
const sourceFiles = [
  'apps/aegis-node/src/components/UpdateNotice.vue', 'apps/aegis-node/src/styles.css',
  'packages/aegis-client/src/components/SettingsShell.vue', 'packages/aegis-client/src/styles.css',
];
const sourceHashes = () => Object.fromEntries(sourceFiles.map(file => [file, createHash('sha256').update(readFileSync(join(repoRoot, file))).digest('hex')]));
const sourceAtStart = sourceHashes();
const installedVersion = JSON.parse(readFileSync(join(appRoot, 'package.json'), 'utf8')).version;
const normalVersion = installedVersion;
const longVersion = '0.7.0-unstable.123456789012345678901234567890';
const scenarios = [
  { name: 'available', state: 'available', kind: 'apk' },
  { name: 'installing-progress', state: 'installing', kind: 'installer', progress: 47 },
  { name: 'installing-unknown', state: 'installing', kind: 'apk', progress: null },
  { name: 'ready', state: 'ready', kind: 'installer', ready: true },
  { name: 'permission-required', state: 'permissionRequired', kind: 'apk' },
  { name: 'opened', state: 'opened', kind: 'apk' },
  { name: 'error-retry', state: 'error', kind: 'apk', error: 'A assinatura do instalador não corresponde à Aegis instalada.', code: 'update_signature_mismatch' },
];
const virtualId = 'virtual:aegis-update-notice-visual';
const resolvedVirtualId = `\0${virtualId}`;
const fixtureEntry = `
import { createApp, h, ref, nextTick } from 'vue';
import UpdateNotice from '/src/components/UpdateNotice.vue';
import SettingsShell from '/@fs/${join(repoRoot, 'packages/aegis-client/src/components/SettingsShell.vue')}';
import '/@fs/${join(repoRoot, 'packages/aegis-client/src/styles.css')}';
import '/src/styles.css';
const updates = {
  update: ref(null), state: ref('available'), visible: ref(true),
  progress: ref(null), readyToInstall: ref(false), error: ref(null),
  diagnostic: ref(null), checkedMessage: ref(null), checkedAt: ref(null),
  start: async () => {},
  install: async () => { window.__visualCalls.install++; },
  dismiss: () => { window.__visualCalls.dismiss++; },
};
const workActive = ref(false);
window.__visualCalls = { install: 0, dismiss: 0 };
window.__setUpdateNoticeFixture = async (fixture) => {
  updates.update.value = { version: fixture.version, kind: fixture.kind };
  updates.state.value = fixture.state;
  updates.visible.value = true;
  updates.progress.value = fixture.progress ?? null;
  updates.readyToInstall.value = fixture.ready ?? false;
  updates.error.value = fixture.error ?? null;
  updates.diagnostic.value = fixture.code ? { code: fixture.code, phase: 'verification' } : null;
  workActive.value = fixture.workActive ?? false;
  window.__visualCalls = { install: 0, dismiss: 0 };
  await nextTick();
};
createApp({ setup: () => () => h(SettingsShell, { section: 'updates' }, {
  default: () => h('section', [
    h('h2', 'Atualizações'),
    h(UpdateNotice, { updates, workActive: workActive.value }),
  ]),
}) }).mount('#app');
window.__visualMounted = true;
`;
// Vite interprets port 0 as its default. Reserve an OS-selected port first and
// keep strictPort so an unlikely race cannot silently attach to another server.
const allocator = createPortAllocator();
await new Promise((done, fail) => { allocator.once('error', fail); allocator.listen(0, '127.0.0.1', done); });
const allocatedAddress = allocator.address();
assert.ok(allocatedAddress && typeof allocatedAddress === 'object');
const port = allocatedAddress.port;
await new Promise((done, fail) => allocator.close(error => error ? fail(error) : done()));
const server = await createServer({
  root: appRoot,
  configFile: false,
  cacheDir: join(appRoot, 'node_modules/.vite-update-notice-visual'),
  resolve: { dedupe: ['vue'] },
  plugins: [vue(), {
    name: 'aegis-update-notice-visual-fixture',
    resolveId: id => id === virtualId ? resolvedVirtualId : null,
    load: id => id === resolvedVirtualId ? fixtureEntry : null,
    configureServer(vite) {
      vite.middlewares.use('/__update_notice_visual__', async (_request, response, next) => {
        try {
          const html = await vite.transformIndexHtml('/__update_notice_visual__', `<!doctype html><html lang="pt-BR"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Aegis UpdateNotice visual fixture</title></head><body><div id="app"></div><script type="module">import '${virtualId}';</script></body></html>`);
          response.setHeader('Content-Type', 'text/html; charset=utf-8'); response.end(html);
        } catch (error) { next(error); }
      });
    },
  }],
  server: { host: '127.0.0.1', port, strictPort: true, fs: { allow: [repoRoot] } },
});
let browser;
const report = { phase: options.phase, installedVersion, sourceHashes: sourceAtStart, rendered: 'Real UpdateNotice.vue, SettingsShell.vue and production CSS through Vite; only update refs/callbacks are mocked', fontScaling: 'CSS root font size 100% or 200%; this is not an Android system-font hardware test', cases: [], failures: [], pageErrors: [] };
try {
  await server.listen();
  const address = server.httpServer.address();
  assert.ok(address && typeof address === 'object');
  browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE, headless: true });
  const context = await browser.newContext();
  // Unexpected external requests fail closed; this fixture needs no API or IPC.
  await context.route('**/*', route => new URL(route.request().url()).hostname === '127.0.0.1' ? route.continue() : route.abort());
  const page = await context.newPage();
  page.on('pageerror', error => report.pageErrors.push(error.message));
  await page.goto(`http://127.0.0.1:${address.port}/__update_notice_visual__`);
  await page.waitForFunction(() => window.__visualMounted && typeof window.__setUpdateNoticeFixture === 'function');
  const profiles = [{ name: 'desktop', width: 1280, height: 900, font: 100, version: normalVersion }];
  if (!options.desktopOnly && options.phase !== 'before') {
    for (const width of [320, 360, 412]) for (const font of [100, 200]) for (const version of [normalVersion, longVersion]) {
      profiles.push({ name: `mobile-${width}-font${font}-${version === normalVersion ? 'normal' : 'long'}`, width, height: 1200, font, version });
    }
  }
  for (const profile of profiles) {
    await page.setViewportSize({ width: profile.width, height: profile.height });
    await page.evaluate(font => { document.documentElement.style.fontSize = `${font}%`; }, profile.font);
    for (const scenario of scenarios) {
      const name = `${profile.name}-${scenario.name}`;
      await page.evaluate(fixture => window.__setUpdateNoticeFixture(fixture), { ...scenario, version: profile.version });
      const card = page.getByRole('status', { name: 'Atualização da Aegis', exact: true });
      await card.waitFor();
      await page.evaluate(() => document.fonts.ready);
      const measurement = await card.evaluate(element => {
        const rect = node => { const box = node.getBoundingClientRect(); return { x: box.x, y: box.y, width: box.width, height: box.height, right: box.right, bottom: box.bottom }; };
        const content = element.querySelector('.update-notice-content') ?? element.querySelector(':scope > div');
        const actions = element.querySelector('.update-notice-actions');
        const buttons = [...element.querySelectorAll('button')].map(node => ({ text: node.textContent.trim(), disabled: node.disabled, ...rect(node) }));
        const version = element.querySelector('.update-version');
        const mobileTitle = element.querySelector('.update-title-mobile');
        const desktopTitle = element.querySelector('.update-title-desktop');
        const panel = element.closest('.settings-content');
        const header = element.closest('dialog').querySelector('.settings-header');
        const close = header.querySelector('button[aria-label="Fechar configurações"]');
        const heading = header.querySelector('h1');
        const actionStyle = actions ? getComputedStyle(actions) : null;
        return {
          card: rect(element), content: content ? rect(content) : null, buttons,
          actions: actions ? { ...rect(actions), display: actionStyle.display, flexDirection: actionStyle.flexDirection, flexWrap: actionStyle.flexWrap, gridTemplateColumns: actionStyle.gridTemplateColumns } : null,
          version: version ? { ...rect(version), text: version.textContent.trim(), visible: getComputedStyle(version).display !== 'none' } : null,
          mobileTitle: mobileTitle ? { text: mobileTitle.textContent.trim(), visible: getComputedStyle(mobileTitle).display !== 'none' } : null,
          desktopTitle: desktopTitle ? { text: desktopTitle.textContent.trim(), visible: getComputedStyle(desktopTitle).display !== 'none' } : null,
          cardScrollWidth: element.scrollWidth, cardClientWidth: element.clientWidth,
          panelScrollWidth: panel.scrollWidth, panelClientWidth: panel.clientWidth,
          documentScrollWidth: document.documentElement.scrollWidth, viewportWidth: innerWidth,
          rootFontPixels: parseFloat(getComputedStyle(document.documentElement).fontSize),
          header: rect(header), closeButton: rect(close), heading: rect(heading),
        };
      });
      const failures = [];
      const check = (condition, message) => { if (!condition) failures.push(message); };
      check(measurement.documentScrollWidth <= profile.width + 1, 'document horizontal overflow');
      check(measurement.panelScrollWidth <= measurement.panelClientWidth + 1, 'settings content horizontal overflow');
      check(measurement.cardScrollWidth <= measurement.cardClientWidth + 1, 'update card horizontal overflow');
      check(measurement.rootFontPixels >= (profile.font === 200 ? 31.9 : 15.9), 'root font scaling did not apply');
      if (options.phase === 'after') {
        const close = measurement.closeButton;
        check(close.width >= 43.9 && close.height >= 43.9, 'close touch target below 44px');
        check(close.x >= -1 && close.right <= profile.width + 1, 'close button outside viewport');
        check(close.x >= measurement.header.x - 1 && close.right <= measurement.header.right + 1 && close.y >= measurement.header.y - 1 && close.bottom <= measurement.header.bottom + 1, 'close button outside header');
        check(measurement.heading.right <= close.x - 7, 'heading overlaps close button or has less than 8px separation');
      }
      for (const button of measurement.buttons) {
        check(button.height >= 43.9 && button.width >= 43.9, `${button.text}: touch target below 44px`);
        check(button.x >= measurement.card.x - 1 && button.right <= measurement.card.right + 1, `${button.text}: button outside card`);
      }
      if (scenario.state === 'installing') check(measurement.buttons.length === 0, 'installing exposes actions');
      else check(measurement.buttons.some(button => button.text === 'Agora não'), 'dismiss action missing');
      if (options.phase === 'after' && profile.width <= 620) {
        check(Boolean(measurement.content), 'real text block missing');
        check(measurement.mobileTitle?.visible === true, 'mobile title hidden');
        check(measurement.desktopTitle?.visible === false, 'desktop title visible on mobile');
        check(measurement.version?.visible === true && measurement.version.text.includes(profile.version), 'full mobile version missing');
        if (measurement.buttons.length) {
          check(Boolean(measurement.actions), 'separate actions group missing');
          check(measurement.buttons.every(button => button.y >= measurement.content.bottom - 1), 'actions share a row with the text block');
          check(['flex', 'grid'].includes(measurement.actions?.display), 'mobile actions lack an explicit layout');
          if (measurement.buttons.length === 2) {
            const [first, second] = measurement.buttons;
            const sameRow = Math.abs(first.y - second.y) < 1;
            const intentionalStack = second.y >= first.bottom - 1 && (measurement.actions?.display === 'grid' || measurement.actions?.flexWrap === 'wrap' || measurement.actions?.flexDirection === 'column');
            check(sameRow || intentionalStack, 'actions are neither one row nor intentionally stacked');
            check(sameRow ? second.x >= first.right + 7 : second.y >= first.bottom + 7, 'actions overlap or have less than 8px separation');
          }
        }
      }
      if (options.phase === 'after' && profile.width > 620) {
        check(measurement.desktopTitle?.visible === true, 'desktop title hidden');
        check(measurement.mobileTitle?.visible === false, 'mobile title visible on desktop');
        check(measurement.desktopTitle?.text === `Aegis ${profile.version} disponível`, 'desktop title changed');
      }
      if (scenario.progress !== undefined && scenario.progress !== null) check((await card.innerText()).includes(`${scenario.progress}%`), 'known download progress missing');
      await page.screenshot({ path: join(output, `${name}.png`), fullPage: true, animations: 'disabled' });
      report.cases.push({ name, profile, scenario, screenshot: `${name}.png`, ...measurement, failures });
      report.failures.push(...failures.map(message => `${name}: ${message}`));
    }
  }
  const baseline = join(dirname(output), 'before');
  if (options.phase === 'after' && existsSync(join(baseline, 'measurements.json'))) {
    const previous = JSON.parse(readFileSync(join(baseline, 'measurements.json'), 'utf8'));
    report.desktopComparisons = report.cases.filter(item => item.profile.name === 'desktop').map(item => {
      const before = previous.cases.find(candidate => candidate.name === item.name);
      assert.ok(before, `Missing desktop baseline: ${item.name}`);
      assert.equal(before.profile.version, item.profile.version, 'Keep the same version text during a visual before/after comparison');
      const digest = file => createHash('sha256').update(readFileSync(file)).digest('hex');
      const beforeSha256 = digest(join(baseline, before.screenshot));
      const afterSha256 = digest(join(output, item.screenshot));
      const samePixels = beforeSha256 === afterSha256;
      if (!samePixels) report.failures.push(`${item.name}: desktop PNG differs from the captured baseline`);
      return { name: item.name, samePixels, beforeSha256, afterSha256, cardBefore: before.card, cardAfter: item.card, buttonsBefore: before.buttons, buttonsAfter: item.buttons };
    });
  }
  // Verify the actual component's callbacks without running the updater engine.
  await page.evaluate(version => window.__setUpdateNoticeFixture({ state: 'error', kind: 'apk', version, error: 'Falha isolada para verificar a ação de tentar novamente.' }), normalVersion);
  const card = page.getByRole('status', { name: 'Atualização da Aegis', exact: true });
  await card.locator('button:not(.update-dismiss)').click();
  await card.getByRole('button', { name: 'Agora não', exact: true }).click();
  assert.deepEqual(await page.evaluate(() => window.__visualCalls), { install: 1, dismiss: 1 });
  await page.evaluate(version => window.__setUpdateNoticeFixture({ state: 'available', kind: 'apk', version, workActive: true }), normalVersion);
  assert.equal(await card.locator('button:not(.update-dismiss)').isDisabled(), true, 'active work must block install');
  assert.deepEqual(sourceHashes(), sourceAtStart, 'Component/CSS changed during one capture run; repeat with a stable source');
  report.callbackWiring = { retryCallsInstall: true, dismissCallsDismiss: true, activeWorkDisablesInstall: true };
  report.failures.push(...report.pageErrors.map(error => `Vue/page error: ${error}`));
  assert.equal(report.failures.length, 0, report.failures.join('\n'));
  console.log(`${options.phase}: ${report.cases.length} real UpdateNotice visual cases PASS; measurements and screenshots: ${output}`);
} catch (error) {
  report.error = error instanceof Error ? error.message : String(error);
  throw error;
} finally {
  writeFileSync(join(output, 'measurements.json'), JSON.stringify(report, null, 2) + '\n');
  await browser?.close();
  await server.close();
}
