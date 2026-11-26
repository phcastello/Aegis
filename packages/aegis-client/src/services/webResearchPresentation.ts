import type MarkdownIt from 'markdown-it';
import type { WebResearch, WebResearchSource } from '../types/chat';

interface ResearchLink {
  url: string;
  title: string;
  host: string;
}
interface DisplaySource extends ResearchLink {
  kind: WebResearchSource['kind'];
}
interface DisplayCitation extends ResearchLink {
  number: number;
  startIndex: number;
  endIndex: number;
}

function researchLink(value: unknown): ResearchLink | null {
  if (!value || typeof value !== 'object') return null;
  const { url, title } = value as { url?: unknown; title?: unknown };
  if (typeof url !== 'string' || url.length > 8192 || !/^https?:\/\//i.test(url) || /[\s\\\u0000-\u001f\u007f]/.test(url)) return null;
  try {
    const parsed = new URL(url);
    if (!['https:', 'http:'].includes(parsed.protocol) || parsed.username || parsed.password) return null;
    return { url: parsed.href, host: parsed.host, title: typeof title === 'string' && title.trim()
      ? title.trim().slice(0, 300) : parsed.host + (parsed.pathname === '/' ? '' : parsed.pathname) };
  } catch { return null; }
}

function isTextBoundary(content: string, offset: number): boolean {
  return offset === 0 || offset === content.length || !(content.charCodeAt(offset) >= 0xdc00 && content.charCodeAt(offset) <= 0xdfff &&
    content.charCodeAt(offset - 1) >= 0xd800 && content.charCodeAt(offset - 1) <= 0xdbff);
}

export function prepareWebResearch(content: string, research?: WebResearch | null) {
  const sources = new Map<string, DisplaySource>();
  for (const source of Array.isArray(research?.sources) ? research.sources : []) {
    const link = researchLink(source);
    if (!link || !['consulted', 'opened', 'citation'].includes(source.kind)) continue;
    // Keep explicitly opened pages distinguishable from other sources.
    if (!sources.has(link.url) || source.kind === 'opened') sources.set(link.url, { ...link, kind: source.kind });
  }
  const numbers = new Map<string, number>();
  const positions = new Set<string>();
  const citations: DisplayCitation[] = [];
  const cited = new Map<string, ResearchLink>();
  for (const source of Array.isArray(research?.sources) ? research.sources : []) {
    if (source?.kind !== 'citation' && source?.cited !== true) continue;
    const link = researchLink(source);
    if (link) cited.set(link.url, link);
  }
  const rawCitations = Array.isArray(research?.citations) ? research.citations : [];
  const sorted = rawCitations.filter(citation => citation && typeof citation === 'object')
    .sort((a, b) => a.startIndex - b.startIndex || a.endIndex - b.endIndex);
  for (const citation of sorted) {
    const link = researchLink(citation);
    if (!link || !Number.isInteger(citation.startIndex) || !Number.isInteger(citation.endIndex) ||
      citation.startIndex < 0 || citation.endIndex <= citation.startIndex || citation.endIndex > content.length ||
      !isTextBoundary(content, citation.startIndex) || !isTextBoundary(content, citation.endIndex)) continue;
    if (!citation.title && sources.has(link.url)) link.title = sources.get(link.url)!.title;
    const key = JSON.stringify([link.url, citation.startIndex, citation.endIndex]);
    if (positions.has(key)) continue;
    positions.add(key);
    const number = numbers.get(link.url) ?? numbers.size + 1;
    numbers.set(link.url, number);
    cited.set(link.url, link);
    if (!sources.has(link.url)) sources.set(link.url, { ...link, kind: 'citation' });
    citations.push({ ...link, number, startIndex: citation.startIndex, endIndex: citation.endIndex });
  }
  return { sources: [...sources.values()], citations, cited: [...cited.values()] };
}

type MarkdownToken = ReturnType<MarkdownIt['parse']>[number];

/** Source offsets refer to the original answer, before Markdown parsing. Decorate
 * parsed tokens instead of interpolating URLs into untrusted Markdown or HTML.
 * A plain-text inline block can be split exactly at a citation boundary. For
 * formatted blocks, attach the link to that same inline block without breaking
 * emphasis, links, tables or code. Non-inline ranges stay in the visible source
 * summary. The original answer is never changed. */
export function renderWebResearchMarkdown(markdown: MarkdownIt, content: string, research?: WebResearch | null) {
  const prepared = prepareWebResearch(content, research);
  if (!prepared.citations.length) return { html: markdown.render(content), sources: prepared.sources,
    unplacedCitations: prepared.cited };
  const env = {};
  const tokens = markdown.parse(content, env);
  const lineOffsets = [0];
  for (const match of content.matchAll(/\r\n|\r|\n/g)) lineOffsets.push(match.index! + match[0].length);
  lineOffsets.push(content.length);
  const blocks = tokens.filter(token => token.type === 'inline' && token.map && token.children);
  function sourceOffset(block: MarkdownToken): number | null {
    const [first, last] = block.map!;
    const offset = content.indexOf(block.content, lineOffsets[first]);
    if (offset < lineOffsets[first] || offset + block.content.length > lineOffsets[last]) return null;
    const another = content.indexOf(block.content, offset + 1);
    return another >= 0 && another < lineOffsets[last] ? null : offset;
  }
  const assigned = new Map<MarkdownToken, DisplayCitation[]>();
  for (const citation of prepared.citations) {
    const candidates = blocks.filter(token => {
      const [first, last] = token.map!;
      return citation.endIndex > lineOffsets[first] && citation.endIndex <= lineOffsets[last] &&
        citation.startIndex < lineOffsets[last];
    });
    // Some Markdown constructs (e.g., rows of a table) share a line map.
    // Never guess which block the source referred to in an ambiguous match.
    const exactMatches = candidates.filter(block => {
      const offset = sourceOffset(block);
      return offset !== null && citation.endIndex > offset && citation.endIndex <= offset + block.content.length;
    });
    const block = candidates.length === 1 ? candidates[0] : exactMatches.length === 1 ? exactMatches[0] : null;
    if (!block) continue;
    const existing = assigned.get(block) ?? [];
    existing.push(citation);
    assigned.set(block, existing);
  }

  function badge(citation: DisplayCitation): MarkdownToken[] {
    // Construct tokens with MarkdownIt's parser for a fixed literal; a validated
    // URL/title is then assigned as an attribute, escaped by the renderer.
    const link = markdown.parseInline('[source](https://example.invalid/)', {})[0].children!;
    link[0].attrSet('href', citation.url);
    link[0].attrSet('class', 'web-research-link web-citation');
    link[0].attrSet('title', citation.title);
    link[0].attrSet('aria-label', `Fonte ${citation.number}: ${citation.title}`);
    link[1].content = `[${citation.number}]`;
    return link;
  }
  function textToken(value: string): MarkdownToken {
    const token = markdown.parseInline('text', {})[0].children![0];
    token.content = value;
    return token;
  }

  function tokenSource(token: MarkdownToken): string | null {
    if (token.type === 'text') return token.content;
    if (token.type === 'softbreak') return '\n';
    // These delimiters have literal source lengths. We leave unusual Markdown
    // structures (e.g., nested links, escapes, entities, code) to block placement.
    if (['em_open', 'em_close', 'strong_open', 'strong_close', 's_open', 's_close'].includes(token.type)) return token.markup;
    return null;
  }
  const placed = new Set<string>();
  for (const [block, citations] of assigned) {
    const children = block.children!;
    const offset = sourceOffset(block);
    const rawParts = children.map(tokenSource);
    const exact = offset !== null && rawParts.every(part => part !== null) && rawParts.join('') === block.content &&
      citations.every(citation => citation.endIndex > offset && citation.endIndex <= offset + block.content.length);
    if (exact) {
      const replacement: MarkdownToken[] = [];
      const pending = [...citations].sort((a, b) => a.endIndex - b.endIndex);
      let sourceCursor = offset!;
      for (const [index, token] of children.entries()) {
        const end = sourceCursor + rawParts[index]!.length;
        let cursor = 0;
        if (token.type !== 'text') replacement.push(token);
        while (pending.length && pending[0].endIndex <= end) {
          const citation = pending.shift()!;
          const localEnd = Math.max(cursor, citation.endIndex - sourceCursor);
          if (token.type === 'text' && localEnd > cursor) replacement.push(textToken(token.content.slice(cursor, localEnd)));
          replacement.push(...badge(citation));
          cursor = localEnd;
          placed.add(citation.url);
        }
        if (token.type === 'text' && cursor < token.content.length) replacement.push(textToken(token.content.slice(cursor)));
        sourceCursor = end;
      }
      block.children = replacement;
    } else {
      // At most one marker per source when exact offsets cannot be mapped.
      for (const citation of new Map(citations.map(citation => [citation.url, citation])).values()) {
        children.push(...badge(citation));
        placed.add(citation.url);
      }
    }
  }
  return { html: markdown.renderer.render(tokens, markdown.options, env), sources: prepared.sources,
    unplacedCitations: prepared.cited.filter(citation => !placed.has(citation.url)) };
}
