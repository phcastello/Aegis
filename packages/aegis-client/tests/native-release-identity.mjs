// Assertions shared by the real Windows WebView2 and Android WebView smoke tests.
// API responses are disposable test data; the native IPC bridge is never replaced.
import assert from 'node:assert/strict';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';

const codename = "It's Not Stalking. It's a Feature.";
const tagline = "You're terrible at hiding.";

export async function assertNativeReleaseIdentity(page, { version, platform, output }) {
  mkdirSync(output, { recursive: true });
  await page.getByRole('region', { name: 'Conversa com a Aegis' }).waitFor();
  const runtime = await page.evaluate(() => window.__TAURI_INTERNALS__.invoke('runtime_info'));
  assert.equal(runtime.platform, platform, 'Smoke must use the actual native runtime.');
  assert.equal(runtime.version, version, 'Identity must use the installed application version.');
  assert.equal(runtime.backendUrl, 'https://aegis.phcastello.com');
  if (await page.getByRole('button', { name: 'Abrir histórico', exact: true }).isVisible()) {
    await page.getByRole('button', { name: 'Abrir histórico', exact: true }).click();
  }
  const card = page.getByRole('region', { name: 'Identidade da Aegis' });
  await card.waitFor({ state: 'visible' });
  assert.equal((await card.locator('h1').innerText()).trim(), 'Aegis');
  assert.equal((await card.locator('.identity-card__title span').innerText()).trim(), `v${version}`);
  assert.equal((await card.locator('.identity-card__codename').innerText()).trim(), codename);
  assert.equal(await card.getByText(tagline, { exact: true }).count(), 0, 'Sidebar must omit the secondary phrase.');
  const collapse = page.getByRole('button', { name: 'Fechar histórico', exact: true });
  assert.equal(await collapse.isVisible(), true);
  assert.equal(await collapse.isEnabled(), true);
  const layout = await page.evaluate(() => {
    const text = document.querySelector('.identity-card__codename');
    const button = document.querySelector('#sidebar-collapse-button');
    const sidebar = document.querySelector('#conversation-sidebar');
    const a = text.getBoundingClientRect(), b = button.getBoundingClientRect();
    return {
      overlap: a.left < b.right && a.right > b.left && a.top < b.bottom && a.bottom > b.top,
      textClipped: text.scrollWidth > text.clientWidth + 1 || text.scrollHeight > text.clientHeight + 1,
      sidebarOverflow: sidebar.scrollWidth > sidebar.clientWidth + 1,
      pageOverflow: document.documentElement.scrollWidth > innerWidth + 1,
      viewport: { width: innerWidth, height: innerHeight }
    };
  });
  assert.equal(layout.overlap, false, 'Codename and collapse button must remain separate.');
  assert.equal(layout.textClipped, false, 'Full codename must remain readable.');
  assert.equal(layout.sidebarOverflow, false, 'Sidebar must not scroll horizontally.');
  assert.equal(layout.pageOverflow, false, 'App must not scroll horizontally.');
  await page.screenshot({ path: join(output, `${platform}-sidebar.png`) });
  await collapse.click();
  await page.getByRole('button', { name: 'Abrir histórico', exact: true }).waitFor({ state: 'visible' });
  await page.getByRole('button', { name: 'Abrir histórico', exact: true }).click();
  await page.getByRole('button', { name: 'Configurações da Aegis', exact: true }).click();
  await page.getByRole('button', { name: 'Sobre', exact: true }).click();
  const about = page.locator('.settings-content');
  assert.equal((await about.locator('.about-release-codename').innerText()).trim(), codename);
  assert.equal((await about.locator('.about-release-tagline').innerText()).trim(), tagline);
  assert.equal((await about.locator('.settings-badge').innerText()).trim(), `v${version}`);
  assert.equal(await about.getByRole('button', { name: 'Verificar atualizações', exact: true }).count(), 0);
  assert.equal(await about.getByRole('button', { name: 'Abrir Atualizações', exact: true }).count(), 0);
  await page.screenshot({ path: join(output, `${platform}-about.png`) });
  await page.getByRole('button', { name: 'Atualizações', exact: true }).click();
  await about.getByRole('heading', { name: 'Atualizações', exact: true }).waitFor();
  await about.getByRole('button', { name: 'Verificar atualizações', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Fechar configurações', exact: true }).click();
  return { platform: runtime.platform, version: runtime.version, nativeIpc: 'unmodified', codename, tagline, layout };
}

export async function routeOfflineIdentityApi(page) {
  await page.route('**/api/**', route => route.fulfill({ json:
    route.request().url().includes('/chat/') ? { items: [], hasMore: false }
      : { enabled: false, available: false, configured: false, isConnected: false }
  }));
}
