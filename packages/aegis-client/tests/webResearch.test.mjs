import test from 'node:test';
import assert from 'node:assert/strict';
import MarkdownIt from 'markdown-it';
import { loadTypeScript } from './loadTypeScript.mjs';
const { prepareWebResearch, renderWebResearchMarkdown } = await loadTypeScript('../src/services/webResearchPresentation.ts');
const markdown = new MarkdownIt({ html: false, breaks: true, linkify: true });
const url1 = 'https://example.test/one';
const url2 = 'https://example.test/two';
const citation = (url, content, range) => ({ url, startIndex: content.indexOf(range), endIndex: content.indexOf(range) + range.length });

test('older messages preserve exactly the original Markdown rendering', () => {
  const content = '**Texto** sem dados web.\n\n- Um\n- Dois';
  for (const research of [undefined, null, { sources: [], citations: [] }]) {
    const result = renderWebResearchMarkdown(markdown, content, research);
    assert.equal(result.html, markdown.render(content));
    assert.deepEqual(result.sources, []);
    assert.deepEqual(result.inlineCitations, []);
  }
});

test('plain text gets multiple clickable sources at their actual UTF-16 boundary, preserving Unicode', () => {
  const content = '🎵 A primeira afirmação. A segunda afirmação. A terceira.';
  const research = { sources: [{ url: url1, title: 'Fonte A', siteName: 'Portal A', kind: 'opened' }, { url: url2, kind: 'consulted' }],
    citations: [citation(url2, content, 'segunda afirmação.'), citation(url1, content, 'primeira afirmação.')] };
  const result = renderWebResearchMarkdown(markdown, content, research);
  assert.match(result.html, /primeira afirmação\.<span class="web-citation-group" data-citation-index="0"><a href="https:\/\/example\.test\/one"[^>]*>.*?<\/button><\/span> A segunda/);
  assert.match(result.html, /segunda afirmação\.<span class="web-citation-group" data-citation-index="1"><a href="https:\/\/example\.test\/two"[^>]*>.*?<\/button><\/span> A terceira/);
  assert.deepEqual(result.inlineCitations.map(({ url, siteName, title }) => [url, siteName, title]),
    [[url1, 'Portal A', 'Fonte A'], [url2, 'Example', 'example.test/two']]);
  assert.match(result.html, /class="web-citation__label">Portal A<\/span>/);
  assert.match(result.html, /class="web-citation__label">Example<\/span>/);
  assert.equal(result.sources.length, 2);
  assert.equal(result.sources[0].kind, 'opened');
  assert.match(result.html, /🎵 A primeira afirmação/);
});

test('formatted Markdown retains markup and avoids nested anchors for source spans crossing existing links', () => {
  const content = '**Texto forte** e [link existente](https://a.test/).\n\n`código` e explicação.';
  const result = renderWebResearchMarkdown(markdown, content, {
    sources: [], citations: [citation(url1, content, 'link existente'), citation(url2, content, 'explicação.')]
  });
  assert.match(result.html, /<strong>Texto forte<\/strong>/);
  assert.match(result.html, /<a href="https:\/\/a\.test\/">link existente<\/a>\.<span class="web-citation-group"[^>]+><a href="https:\/\/example\.test\/one"/);
  assert.match(result.html, /<code>código<\/code> e explicação\.<span class="web-citation-group"[^>]+><a href="https:\/\/example\.test\/two"/);
  assert.equal(result.sources.length, 2);
});

test('invalid URLs and indices cannot create links, and safely escaped titles are rendered as text', () => {
  const content = '🎵 Uma resposta.';
  const research = {
    sources: [
      { url: 'javascript:alert(1)', kind: 'citation' }, { url: 'file:///etc/passwd', kind: 'opened' },
      { url: 'https://user:password@example.test/', kind: 'consulted' }, { url: 'relative/path', kind: 'opened' },
      { url: url1, kind: 'opened' }, { url: url2, title: '<img src=x onerror=1>', kind: 'consulted' }
    ],
    citations: [
      {url: 'data:text/html,evil', startIndex: 0, endIndex: 2},
      {url: 'https://invalid-boundary.test', startIndex: 1, endIndex: 2},
      {url: 'https://invalid-range.test', startIndex: -1, endIndex: 2},
      {url: url2, title: '"><script>alert(1)</script>', startIndex: 0, endIndex: content.length}
    ]
  };
  const result = renderWebResearchMarkdown(markdown, content, research);
  assert.equal(result.sources.length, 2);
  assert.equal((result.html.match(/class="web-research-link web-citation"/g) || []).length, 1);
  assert.doesNotMatch(result.html, /javascript:|file:|data:|invalid-boundary|invalid-range|<script>|<img /);
  assert.match(result.html, /&lt;script&gt;/);
});

test('unmapped source spans and provider citations without offsets remain available in the sources disclosure', () => {
  const content = '```text\nUm trecho.\n```';
  const research = { sources: [{ url: url2, kind: 'citation' }], citations: [citation(url1, content, 'Um trecho.')] };
  const result = renderWebResearchMarkdown(markdown, content, research);
  assert.equal(result.html, markdown.render(content));
  assert.deepEqual(result.inlineCitations, []);
  assert.deepEqual(result.sources.map(source => source.url).sort(), [url1, url2].sort());
  assert.equal(prepareWebResearch(content, { sources: [{ url: url2, kind: 'consulted' }, { url: url2, kind: 'opened' }], citations: [] }).sources[0].kind, 'opened');
});

test('a cited source without safe offsets stays visible even when explicitly opened or consulted', () => {
  const results = prepareWebResearch('Resposta com fonte.', {
    sources: [{ url: url1, kind: 'opened', cited: true }, { url: url2, kind: 'consulted', cited: true }],
    citations: []
  });
  assert.deepEqual(results.cited.map(source => source.url), [url1, url2]);
  assert.deepEqual(results.sources.map(source => source.kind), ['opened', 'consulted']);
});

test('source preview keeps evidence text optional and derives a referrer-free icon on the cited site', () => {
  const content = 'A primeira afirmação. Outra afirmação.';
  const url = 'https://www.reuters.com/world/article?exact=1';
  const result = renderWebResearchMarkdown(markdown, content, {
    sources: [{ url, kind: 'consulted', title: 'Título da fonte', snippet: 'Um trecho real.\nSegunda frase.' }],
    citations: [citation(url, content, 'A primeira afirmação.'), citation(url2, content, 'Outra afirmação.')]
  });
  assert.equal(result.inlineCitations[0].siteName, 'Reuters');
  assert.equal(result.inlineCitations[0].snippet, 'Um trecho real. Segunda frase.');
  assert.equal(result.inlineCitations[1].snippet, undefined);
  assert.match(result.html, /href="https:\/\/www\.reuters\.com\/world\/article\?exact=1"/);
  assert.match(result.html, /<img src="\/api\/chat\/research-favicon\?host=reuters\.com"[^>]*referrerpolicy="no-referrer"/);
  assert.doesNotMatch(result.html, /example\.test\/favicon\.ico/);
});

test('only whole parenthesized links with cited URLs become badges, without duplicate offset markers', () => {
  const content = `**Afirmação.** ([Uma fonte](${url1})) Outra frase (${url2}). (observação) ([interno](https://unrelated.test/)) (mais [contexto](${url1})).`;
  const research = { sources: [], citations: [citation(url1, content, 'Afirmação.'), citation(url2, content, 'Outra frase')] };
  const result = renderWebResearchMarkdown(markdown, content, research);
  assert.equal(result.inlineCitations.length, 2);
  assert.deepEqual(result.inlineCitations.map(source => source.url), [url1, url2]);
  assert.match(result.html, /<strong>Afirmação\.<\/strong> <span class="web-citation-group"/);
  assert.match(result.html, /Outra frase <span class="web-citation-group"/);
  assert.doesNotMatch(result.html, /Uma fonte|\(<span class="web-citation-group"/);
  assert.match(result.html, /\(observação\) \(<a href="https:\/\/unrelated\.test\/">interno<\/a>\)/);
  assert.match(result.html, /\(mais <a href="https:\/\/example\.test\/one">contexto<\/a>\)/);
  assert.match(content, /\(\[Uma fonte\]/, 'Stored answer text does not change');
});

test('evidenced parenthesized links can be rendered even when sources have no offsets', () => {
  const content = `Uma afirmação ( [Origem](${url1}) ) outra ( [Referência](${url2}) ).`;
  const result = renderWebResearchMarkdown(markdown, content, {
    sources: [{ url: url1, kind: 'opened', cited: true }, { url: url2, kind: 'consulted' }], citations: []
  });
  assert.deepEqual(result.inlineCitations.map(source => source.url), [url1]);
  assert.match(result.html, /Uma afirmação <span class="web-citation-group"/);
  assert.match(result.html, /outra \( <a href="https:\/\/example\.test\/two">Referência<\/a> \)\./);
});

test('tables render exact cited links and leave a bare domain pointing to its original address', () => {
  const url = 'https://example.org/one';
  const content = [
    '| Referência | Outro endereço |',
    '| --- | --- |',
    '| Informação ([Origem](' + url + ')) | Endereço (example.org) |'
  ].join('\r\n');
  const result = renderWebResearchMarkdown(markdown, content, {
    sources: [{ url, kind: 'opened', cited: true }], citations: []
  });
  assert.deepEqual(result.inlineCitations.map(source => source.url), [url]);
  assert.match(result.html, /<td>Informação <span class="web-citation-group"/);
  assert.match(result.html, /<td>Endereço \(<a href="http:\/\/example\.org">example\.org<\/a>\)<\/td>/);
});

test('table citation ranges use the matching cell, retaining overlapping ranges in the source disclosure', () => {
  const content = [
    'A | B',
    '--- | ---',
    'Mesmo trecho. | Mesmo trecho.',
    'Texto \\| literal | Outra célula.'
  ].join('\n');
  const secondCell = content.lastIndexOf('Mesmo trecho.');
  const result = renderWebResearchMarkdown(markdown, content, {
    sources: [],
    citations: [
      citation(url2, content, 'Mesmo trecho. | Mesmo trecho.'),
      { url: url1, startIndex: secondCell, endIndex: secondCell + 'Mesmo trecho.'.length },
      citation('https://example.test/three', content, 'Texto \\| literal')
    ]
  });
  assert.deepEqual(result.inlineCitations.map(source => source.url), [url1]);
  assert.match(result.html, /<td>Mesmo trecho\.<\/td>\s*<td>Mesmo trecho\.<span class="web-citation-group"/);
  assert.equal(result.sources.length, 3);
});

test('Agência Brasil badges delegate icon discovery to Aegis and retain the short site name', () => {
  const content = 'Uma informação consultada.';
  const url = 'https://www.agenciabrasil.ebc.com.br/noticia/uma-informacao';
  const result = renderWebResearchMarkdown(markdown, content, {
    sources: [{ url, kind: 'opened', title: 'Uma informação', siteName: 'Agência Brasil | Últimas notícias do Brasil e do mundo' }],
    citations: [{ ...citation(url, content, content), siteName: 'Agência Brasil - EBC' }]
  });
  assert.equal(result.inlineCitations[0].siteName, 'Agência Brasil');
  assert.match(result.html, /href="https:\/\/www\.agenciabrasil\.ebc\.com\.br\/noticia\/uma-informacao"/);
  assert.match(result.html, /src="\/api\/chat\/research-favicon\?host=agenciabrasil\.ebc\.com\.br"/);
  assert.match(result.html, /class="web-citation__label">Agência Brasil<\/span>/);
});
