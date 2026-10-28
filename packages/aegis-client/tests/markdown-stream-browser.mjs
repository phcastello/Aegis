// Exercise the actual Vue Markdown renderer with visible Chromium scrollbars.
// A vertical transform on inline-block fragments used to create a transient
// one-pixel overflow, a local scrollbar and a second Markdown reflow.
import assert from 'node:assert/strict';
import { chromium } from 'playwright-core';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const requireNative = createRequire(join(root, 'apps/aegis-node/package.json'));
const { createServer } = await import(requireNative.resolve('vite'));
const moduleId = '/markdown-renderer-test-entry.js';
const source = `
import { createApp, h, ref, nextTick } from 'vue';
import MarkdownMessage from ${JSON.stringify(join(root, 'packages/aegis-client/src/components/MarkdownMessage.vue'))};
import ${JSON.stringify(join(root, 'packages/aegis-client/src/styles.css'))};
const content = ref('');
createApp({ render: () => h(MarkdownMessage, { content: content.value, animateChanges: true }) }).mount('#fixture');
window.updateMarkdown = async text => { content.value = text; await nextTick(); };
`;
const vite = await createServer({ root: join(root, 'apps/aegis-node'), cacheDir: `/tmp/aegis-markdown-vite-${process.pid}`,
  server: { host: '127.0.0.1', port: 0, strictPort: false },
  plugins: [{ name: 'markdown-stream-regression',
    resolveId(id) { if (id === moduleId) return id; },
    load(id) { if (id === moduleId) return source; },
    configureServer(server) {
      server.middlewares.use('/markdown-stream-fixture', (_req, res) => {
        res.setHeader('Content-Type', 'text/html');
        res.end(`<div id="fixture" style="width:300px"></div><script type="module" src="${moduleId}"></script>`);
      });
    }
  }] });
let browser;
try {
  await vite.listen();
  browser = await chromium.launch({ executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE,
    headless: true, ignoreDefaultArgs: ['--hide-scrollbars'] });
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto(`http://127.0.0.1:${vite.httpServer.address().port}/markdown-stream-fixture`);
  await page.waitForFunction(() => typeof window.updateMarkdown === 'function');
  const metrics = () => page.locator('.markdown-message').evaluate(el => ({ width: el.clientWidth, height: el.clientHeight,
    scrollHeight: el.scrollHeight, scrollWidth: el.scrollWidth,
    nestedVertical: [...el.querySelectorAll('*')].filter(child => ['auto', 'scroll'].includes(getComputedStyle(child).overflowY) && child.scrollHeight > child.clientHeight).length }));
  let samples = 0;
  for (const text of [
    'Palavras longas teste '.repeat(5),
    'Palavras longas teste '.repeat(40),
    '# Título\n\n- Primeiro item\n- Segundo item\n\n' + 'Texto de streaming. '.repeat(25),
    'https://example.test/' + 'palavramuitolonga'.repeat(35),
    '```js\nconst value = ' + '1 + '.repeat(100) + '0;\n```',
    '| Primeira coluna | Segunda coluna |\n| --- | --- |\n| ' + 'dados '.repeat(30) + ' | ' + 'resultado '.repeat(30) + ' |'
  ]) {
    await page.evaluate(() => window.updateMarkdown(''));
    await page.evaluate(text => window.updateMarkdown(text), text);
    const initial = await metrics();
    for (const wait of [0, 50, 300, 350]) {
      if (wait) await page.waitForTimeout(wait);
      const current = await metrics(); samples++;
      assert.equal(current.scrollHeight, current.height, 'Markdown has no transient local vertical scrollbar');
      assert.equal(current.nestedVertical, 0, 'Code blocks have no transient local vertical scrollbar');
      assert.equal(current.width, initial.width, 'Fragment fade does not change the content width');
      assert.equal(current.height, initial.height, 'Fragment fade does not change the content height');
    }
    if (text.startsWith('https:')) assert.equal(initial.scrollWidth, initial.width, 'Long prose/links still wrap');
    if (text.startsWith('```')) assert.ok(await page.locator('pre').evaluate(el => el.scrollWidth > el.clientWidth), 'Long code remains horizontally scrollable');
  }
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.evaluate(() => window.updateMarkdown('Movimento reduzido preservado.'));
  assert.equal(await page.locator('.markdown-message').evaluate(el => el.getAnimations({ subtree: true }).length), 0);
  assert.deepEqual(errors, []);
  console.log(`Markdown streaming regression passed: ${samples} layout samples, long prose, code, table and reduced motion.`);
} finally {
  await browser?.close(); await vite.close();
}
