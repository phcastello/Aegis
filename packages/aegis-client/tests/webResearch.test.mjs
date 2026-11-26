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
    assert.deepEqual(result.unplacedCitations, []);
  }
});

test('plain text gets multiple clickable sources at their actual UTF-16 boundary, preserving Unicode', () => {
  const content = '🎵 A primeira afirmação. A segunda afirmação. A terceira.';
  const research = { sources: [{ url: url1, title: 'Fonte A', kind: 'opened' }, { url: url2, kind: 'consulted' }],
    citations: [citation(url2, content, 'segunda afirmação.'), citation(url1, content, 'primeira afirmação.')] };
  const result = renderWebResearchMarkdown(markdown, content, research);
  assert.match(result.html, /primeira afirmação\.<a [^>]*href="https:\/\/example\.test\/one"[^>]*>\[1\]<\/a> A segunda/);
  assert.match(result.html, /segunda afirmação\.<a [^>]*href="https:\/\/example\.test\/two"[^>]*>\[2\]<\/a> A terceira/);
  assert.equal(result.sources.length, 2);
  assert.equal(result.sources[0].kind, 'opened');
  assert.deepEqual(result.unplacedCitations, []);
  assert.match(result.html, /🎵 A primeira afirmação/);
});

test('formatted Markdown retains markup and avoids nested anchors for source spans crossing existing links', () => {
  const content = '**Texto forte** e [link existente](https://a.test/).\n\n`código` e explicação.';
  const result = renderWebResearchMarkdown(markdown, content, {
    sources: [], citations: [citation(url1, content, 'link existente'), citation(url2, content, 'explicação.')]
  });
  assert.match(result.html, /<strong>Texto forte<\/strong>/);
  assert.match(result.html, /<a href="https:\/\/a\.test\/">link existente<\/a>\.<a [^>]+>\[1\]<\/a>/);
  assert.match(result.html, /<code>código<\/code> e explicação\.<a [^>]+>\[2\]<\/a>/);
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

test('unmapped source spans and provider citations without offsets remain visible as fallback links', () => {
  const content = '```text\nUm trecho.\n```';
  const research = { sources: [{ url: url2, kind: 'citation' }], citations: [citation(url1, content, 'Um trecho.')] };
  const result = renderWebResearchMarkdown(markdown, content, research);
  assert.equal(result.html, markdown.render(content));
  assert.deepEqual(result.unplacedCitations.map(source => source.url).sort(), [url1, url2].sort());
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
