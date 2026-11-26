// Exercise the actual Vue Markdown renderer with visible Chromium scrollbars.
// A vertical transform on inline-block fragments used to create a transient
// one-pixel overflow, a local scrollbar and a second Markdown reflow.
import assert from 'node:assert/strict';
import { chromium } from 'playwright-core';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { tmpdir } from 'node:os';
import { createServer as createPortProbe } from 'node:net';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const requireNative = createRequire(join(root, 'apps/aegis-node/package.json'));
const { createServer } = await import(pathToFileURL(requireNative.resolve('vite')).href);
const moduleId = '/markdown-renderer-test-entry.js';
const source = `
import { createApp, h, ref, nextTick } from 'vue';
import MarkdownMessage from '@aegis/client/components/MarkdownMessage.vue';
import '@aegis/client/styles.css';
const content = ref('');
const research = ref(null);
createApp({ render: () => h(MarkdownMessage, { content: content.value, animateChanges: true, webResearch: research.value }) }).mount('#fixture');
window.updateMarkdown = async (text, metadata = null) => { content.value = text; research.value = metadata; await nextTick(); };
`;
const port = await new Promise((resolve, reject) => {
  const probe = createPortProbe(); probe.once('error', reject);
  probe.listen(0, '127.0.0.1', () => { const { port } = probe.address(); probe.close(() => resolve(port)); });
});
const vite = await createServer({ root: join(root, 'apps/aegis-node'), cacheDir: join(tmpdir(), `aegis-markdown-vite-${process.pid}`),
  resolve: { alias: { '@aegis/client': join(root, 'packages/aegis-client/src') } },
  server: { host: '127.0.0.1', port, strictPort: false },
  plugins: [{ name: 'markdown-stream-regression',
    resolveId(id) { if (id === moduleId) return id; },
    load(id) { if (id === moduleId) return source; },
    configureServer(server) {
      server.middlewares.use('/markdown-stream-fixture', (_req, res) => {
        res.setHeader('Content-Type', 'text/html');
        res.end(`<meta name="viewport" content="width=device-width, initial-scale=1"><div id="fixture" style="width:300px"></div><script type="module" src="${moduleId}"></script>`);
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
  const iconRequests = [];
  const iconRoute = host => '/api/chat/research-favicon?host=' + host;
  const validIcon = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAABAAAAAQCAIAAACQkWg2AAAAFklEQVR4nGMwnNROEmIY1TCqYfhqAAAap0oQAz0fdAAAAABJRU5ErkJggg==', 'base64');
  await page.context().route(url => url.pathname === '/api/chat/research-favicon', route => {
    iconRequests.push(route.request());
    const host = new URL(route.request().url()).searchParams.get('host');
    if (host === 'agenciabrasil.ebc.com.br' || host === 'reuters.com')
      return route.fulfill({ status: 200, contentType: 'image/png', body: validIcon });
    if (host === 'pixel.example.org')
      return route.fulfill({ status: 200, contentType: 'image/gif',
        body: Buffer.from('R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7', 'base64') });
    return route.fulfill({ status: 404 });
  });
  await page.context().route('https://reuters.com/first?article=1', route => route.fulfill({ status: 200, body: 'Source' }));
  await page.goto(`http://127.0.0.1:${vite.httpServer.address().port}/markdown-stream-fixture`);
  await page.waitForFunction(() => typeof window.updateMarkdown === 'function');
  const agencyUrl = 'https://www.agenciabrasil.ebc.com.br/noticia';
  const agencyIcon = iconRoute('agenciabrasil.ebc.com.br');
  await page.evaluate(url => window.updateMarkdown('Informação pública.', {
    sources: [{ url, kind: 'opened', title: 'Uma notícia', siteName: 'Agência Brasil | Últimas notícias do Brasil e do mundo' }],
    citations: [{ url, startIndex: 0, endIndex: 'Informação pública.'.length }]
  }), agencyUrl);
  const agencyBadge = page.getByRole('link', { name: 'Fonte: Agência Brasil · Uma notícia' });
  const agencyImage = agencyBadge.locator('img');
  assert.equal(await agencyImage.getAttribute('src'), agencyIcon);
  assert.equal(await agencyImage.getAttribute('referrerpolicy'), 'no-referrer');
  await agencyBadge.locator('.web-citation__icon--ready').waitFor();
  assert.equal(await agencyBadge.textContent(), 'Agência Brasil');
  const agencyLabel = await agencyBadge.locator('.web-citation__label').evaluate(label => {
    const range = document.createRange();
    range.selectNodeContents(label);
    const text = range.getBoundingClientRect();
    const bounds = label.getBoundingClientRect();
    const css = getComputedStyle(label);
    const chip = getComputedStyle(label.closest('.web-citation-group'));
    return { textTop: text.top, textBottom: text.bottom, top: bounds.top, bottom: bounds.bottom,
      lineHeight: parseFloat(css.lineHeight), fontSize: parseFloat(css.fontSize), border: chip.borderTopWidth };
  });
  assert.ok(agencyLabel.lineHeight >= 1.3 * agencyLabel.fontSize);
  assert.equal(agencyLabel.border, '0px');
  assert.ok(agencyLabel.textTop >= agencyLabel.top - 1 && agencyLabel.textBottom <= agencyLabel.bottom + 1,
    'The label keeps glyph descenders inside its clipping box');
  await agencyBadge.hover();
  const agencyPreview = page.getByRole('tooltip');
  const agencyPreviewImage = agencyPreview.locator('img');
  assert.equal(await agencyPreviewImage.getAttribute('src'), agencyIcon);
  await agencyPreview.locator('.web-source-icon--ready').waitFor();
  assert.ok(iconRequests.some(request => request.url().endsWith(agencyIcon)));
  await page.keyboard.press('Escape');
  await agencyPreview.waitFor({ state: 'hidden' });

  await page.evaluate(() => {
    const url = 'https://pixel.example.org/article';
    return window.updateMarkdown('Referência pública.', {
      sources: [{ url, kind: 'opened', title: 'Um artigo', siteName: 'Portal' }],
      citations: [{ url, startIndex: 0, endIndex: 'Referência pública.'.length }]
    });
  });
  const placeholderBadge = page.getByRole('link', { name: 'Fonte: Portal · Um artigo' });
  const placeholderIcon = placeholderBadge.locator('img');
  assert.equal(await placeholderIcon.getAttribute('src'), iconRoute('pixel.example.org'));
  await page.waitForFunction(() =>
    document.querySelector('.web-citation__favicon')?.naturalWidth === 1);
  assert.equal(await placeholderBadge.locator('.web-citation__icon--ready').count(), 0,
    'A successful transparent one-pixel response must preserve the monogram');
  assert.equal(await placeholderBadge.locator('[data-monogram="P"]').count(), 1);
  await page.mouse.move(1000, 600);

  const sourcedText = 'A primeira informação. A segunda informação.';
  const sourcedMessage = {
    content: sourcedText,
    research: {
      sources: [{ url: 'https://reuters.com/first?article=1', kind: 'consulted', title: 'Primeira fonte', snippet: 'Trecho efetivamente fornecido.' },
        { url: 'https://bbc.com/second', kind: 'opened', title: 'Segunda fonte' },
        { url: 'javascript:alert(1)', kind: 'opened' }],
      citations: [{ url: 'https://reuters.com/first?article=1', startIndex: 0, endIndex: 'A primeira informação.'.length },
        { url: 'https://bbc.com/second', startIndex: 'A primeira informação. '.length, endIndex: sourcedText.length }]
    }
  };
  await page.evaluate(({ content, research }) => window.updateMarkdown(content, research), sourcedMessage);
  const citations = page.locator('.markdown-message a.web-citation');
  await citations.nth(1).waitFor();
  assert.equal(await citations.nth(0).getAttribute('href'), 'https://reuters.com/first?article=1');
  assert.equal(await citations.nth(1).getAttribute('href'), 'https://bbc.com/second');
  assert.equal(await citations.nth(0).textContent(), 'Reuters');
  assert.equal(await citations.nth(1).textContent(), 'BBC');
  assert.equal(await page.locator('.markdown-message').evaluate(element => {
    const copy = element.cloneNode(true);
    copy.querySelectorAll('.web-citation-group').forEach(badge => badge.remove());
    return copy.textContent.trim();
  }), sourcedText);
  const successfulIcon = citations.first().locator('img');
  assert.equal(await successfulIcon.getAttribute('src'), iconRoute('reuters.com'));
  assert.equal(await successfulIcon.getAttribute('referrerpolicy'), 'no-referrer');
  await citations.first().locator('.web-citation__icon--ready').waitFor();
  assert.ok(iconRequests.length > 0 && iconRequests.every(request => !request.headers().referer));
  assert.equal(await citations.nth(1).locator('[data-monogram="B"]').count(), 1);
  assert.equal(await citations.nth(1).locator('.web-citation__icon--ready').count(), 0);

  const preview = page.getByRole('tooltip');
  await citations.first().hover();
  await preview.waitFor();
  assert.match(await preview.textContent(), /Reuters.*Primeira fonte.*Trecho efetivamente fornecido/);
  await preview.hover();
  await page.waitForTimeout(180);
  assert.equal(await preview.isVisible(), true, 'The preview remains available while hovered');
  await page.keyboard.press('Escape');
  await preview.waitFor({ state: 'hidden' });
  await page.mouse.move(1000, 600);
  await page.keyboard.press('Tab');
  await preview.waitFor();
  assert.equal(await citations.first().getAttribute('aria-describedby'), await preview.getAttribute('id'));
  await page.keyboard.press('Tab');
  await preview.waitFor();
  assert.match(await preview.textContent(), /BBC.*Segunda fonte/);
  assert.equal(await preview.locator('p').count(), 0, 'A preview does not invent a snippet');
  const sourceWindowPromise = page.waitForEvent('popup');
  await citations.first().click();
  const sourceWindow = await sourceWindowPromise;
  await sourceWindow.waitForLoadState('domcontentloaded');
  assert.equal(sourceWindow.url(), 'https://reuters.com/first?article=1');
  await sourceWindow.close();

  const touch = await browser.newPage({ viewport: { width: 360, height: 700 }, hasTouch: true, isMobile: true });
  await touch.route(url => url.pathname === '/api/chat/research-favicon', route => route.abort());
  await touch.goto(`http://127.0.0.1:${vite.httpServer.address().port}/markdown-stream-fixture`);
  await touch.waitForFunction(() => typeof window.updateMarkdown === 'function');
  await touch.evaluate(({ content, research }) => window.updateMarkdown(content, research), sourcedMessage);
  const touchDetails = touch.getByRole('button', { name: 'Prévia da fonte Reuters' });
  await touchDetails.tap();
  const touchPreview = touch.getByRole('tooltip');
  await touchPreview.waitFor();
  assert.equal(await touchDetails.getAttribute('aria-expanded'), 'true');
  const mobilePlacement = await touchPreview.evaluate(element => ({ right: element.getBoundingClientRect().right, width: innerWidth }));
  assert.ok(mobilePlacement.right <= mobilePlacement.width - 8, 'The preview fits within the viewport');
  await touchDetails.tap();
  await touchPreview.waitFor({ state: 'hidden' });
  assert.equal(await touchDetails.getAttribute('aria-expanded'), 'false');
  await touch.close();

  assert.equal(await page.locator('details.web-research-list').count(), 0,
    'the full list belongs to the response action bar, not the Markdown body');

  await page.evaluate(() => {
    const sourceUrl = 'https://reuters.com/first?article=1';
    return window.updateMarkdown(`A informação. ([Referência](${sourceUrl})) (parentético normal)`, {
      sources: [{ url: sourceUrl, kind: 'citation' }],
      citations: [{ url: sourceUrl, startIndex: 0, endIndex: 'A informação.'.length }]
    });
  });
  assert.equal(await citations.count(), 1);
  assert.match(await page.locator('.markdown-message').textContent(), /A informação\. Reuters \(parentético normal\)/);
  assert.doesNotMatch(await page.locator('.markdown-message').textContent(), /Referência/);

  await page.evaluate(() => window.updateMarkdown('Sem metadados.'));
  assert.equal(await page.locator('.web-research-list').count(), 0);
  assert.equal(await citations.count(), 0);
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

  const comparison = [
    'Texto antes da tabela.',
    '',
    '| Cidade | Início aproximado | Fim aproximado | Avanço observado |',
    '| --- | --- | --- | --- |',
    '| São Paulo | 1.009 elétricos em operação | 2.009 em operação | O cadastro ainda não confirma todos os veículos em circulação. |',
    '| Rio de Janeiro | Levantamento anterior | Levantamento atualizado | O financiamento anunciado ainda não equivale a veículos circulando. |',
    '| Belo Horizonte | Contagem inicial indisponível | Operação regular | A prefeitura formalizou a compra, com incorporação gradual. |',
    '',
    'O texto depois da tabela também fica no lugar.'
  ].join('\n');
  await page.setViewportSize({ width: 360, height: 780 });
  await page.evaluate(text => window.updateMarkdown(text), comparison);
  const table = page.getByRole('region', { name: 'Tabela' });
  await table.waitFor();
  const tableLayout = () => table.evaluate(element => {
    const firstColumn = element.querySelector('th');
    const message = element.closest('.markdown-message');
    return {
      width: element.clientWidth, scrollWidth: element.scrollWidth, scrollLeft: element.scrollLeft,
      cityWidth: firstColumn.getBoundingClientRect().width,
      messageWidth: message.clientWidth, messageScrollWidth: message.scrollWidth,
      paragraphs: Array.from(message.querySelectorAll(':scope > p')).map(paragraph => paragraph.getBoundingClientRect().left)
    };
  });
  const mobile = await tableLayout();
  assert.equal(mobile.messageScrollWidth, mobile.messageWidth, 'The message itself fits a narrow conversation');
  assert.ok(mobile.scrollWidth > mobile.width, 'The comparative table scrolls by itself');
  assert.ok(mobile.cityWidth >= 100, 'The city column has room for whole words on mobile');
  await table.focus();
  await page.keyboard.press('ArrowRight');
  await page.waitForFunction(() => document.querySelector('.markdown-table-scroll').scrollLeft > 0);
  assert.deepEqual((await tableLayout()).paragraphs, mobile.paragraphs, 'Scrolling the table does not move surrounding paragraphs');

  await page.setViewportSize({ width: 1440, height: 900 });
  await page.locator('#fixture').evaluate(element => { element.style.width = '760px'; });
  const desktop = await tableLayout();
  assert.ok(desktop.cityWidth >= 100, 'A desktop table does not crush its first column');
  assert.equal(desktop.messageScrollWidth, desktop.messageWidth, 'The desktop message itself has no lateral overflow');
  assert.equal(desktop.scrollWidth, desktop.width, 'A table that fits does not need a scrollbar');
  await page.locator('#fixture').evaluate(element => { element.style.width = '300px'; });

  // A single accumulated plain-text update is not wrapped per word. During
  // rapid character/group arrivals, wrappers remain confined to at most six
  // recent groups and disappear once their animation finishes.
  await page.evaluate(() => window.updateMarkdown(''));
  const paragraph = 'Antigo conteúdo estável. '.repeat(40).trimEnd();
  await page.evaluate(text => window.updateMarkdown(text), paragraph);
  const fragmentStats = () => page.locator('.markdown-message').evaluate(el => ({
    spans: el.querySelectorAll('.stream-fade-fragment').length,
    animations: el.getAnimations({ subtree: true }).length,
    text: el.textContent.trimEnd()
  }));
  assert.ok((await fragmentStats()).spans <= 1, 'Accumulated words form a single recent fade group, not one span per word');
  let progression = paragraph;
  for (const char of Array.from(' Texto novo, caractere por caractere, com música 🎵.')) {
    progression += char;
    await page.evaluate(text => window.updateMarkdown(text), progression);
    const current = await fragmentStats();
    assert.equal(current.text, progression.trimEnd());
    assert.ok(current.spans <= 6, 'At most six recent groups in ordinary text');
    assert.ok(current.animations <= 6, 'Animation tree stays bounded as the answer grows');
  }
  await page.waitForTimeout(260);
  const settled = await fragmentStats();
  assert.equal(settled.spans, 0, 'Expired fade wrappers become ordinary text nodes');
  assert.equal(settled.animations, 0);
  assert.equal(settled.text, progression);

  // Partial Markdown re-rendering must preserve sanitization and native link
  // safety even when the text contains a potential HTML or URI injection.
  await page.evaluate(() => window.updateMarkdown('# Título seguro\n\n[Link válido](https://example.test/) [malicioso](javascript:alert(1))\n\n<img src=x onerror=alert(1)>\n\n**Negrito** e `código`.'));
  const markup = await page.locator('.markdown-message').evaluate(el => ({
    unsafe: el.querySelectorAll('script,img,[onerror],[onclick],a[href^="javascript:"]').length,
    heading: el.querySelector('h1')?.textContent,
    strong: el.querySelector('strong')?.textContent,
    code: el.querySelector('code')?.textContent,
    href: el.querySelector('a[href^="https:"]')?.getAttribute('href'),
    rel: el.querySelector('a[href^="https:"]')?.getAttribute('rel')
  }));
  assert.deepEqual(markup, { unsafe: 0, heading: 'Título seguro', strong: 'Negrito', code: 'código', href: 'https://example.test/', rel: 'noopener noreferrer' });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.evaluate(() => window.updateMarkdown('Movimento reduzido preservado.'));
  assert.equal(await page.locator('.markdown-message').evaluate(el => el.getAnimations({ subtree: true }).length), 0);
  assert.equal(await page.locator('.stream-fade-fragment').count(), 0);
  assert.deepEqual(errors, []);
  console.log(`Markdown streaming regression passed: ${samples} layout samples, long prose, code, table, bounded/unwrapped recent fading, sanitization and reduced motion.`);
} finally {
  await browser?.close(); await vite.close();
}
