import type MarkdownIt from 'markdown-it';
import { researchFavicon, researchSite } from './researchSites';
import type { WebResearch, WebResearchSource } from '../types/chat';

export interface ResearchLink {
  url: string;
  title: string;
  host: string;
  siteName: string;
  snippet?: string;
}
interface DisplaySource extends ResearchLink {
  kind: WebResearchSource['kind'];
}
interface DisplayCitation extends ResearchLink {
  startIndex: number;
  endIndex: number;
}

function researchLink(value: unknown): ResearchLink | null {
  if (!value || typeof value !== 'object') return null;
  const { url, title, siteName, snippet } = value as {
    url?: unknown; title?: unknown; siteName?: unknown; snippet?: unknown;
  };
  if (typeof url !== 'string' || url.length > 8192 || !/^https?:\/\//i.test(url) || /[\s\\\u0000-\u001f\u007f]/.test(url)) return null;
  try {
    const parsed = new URL(url);
    if (!['https:', 'http:'].includes(parsed.protocol) || parsed.username || parsed.password) return null;
    const site = researchSite(parsed.href, evidenceText(siteName, 80)) ?? { host: parsed.hostname, name: 'Fonte' };
    return {
      url: parsed.href,
      host: site.host,
      siteName: site.name,
      title: evidenceText(title, 300) || (parsed.host + (parsed.pathname === '/' ? '' : parsed.pathname)).slice(0, 300),
      snippet: evidenceText(snippet, 400) || undefined
    };
  } catch { return null; }
}

function evidenceText(value: unknown, limit: number): string {
  return typeof value === 'string' ? value.replace(/\s+/g, ' ').trim().slice(0, limit) : '';
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
    const source = sources.get(link.url);
    if (source) {
      const evidence = citation as typeof citation & { siteName?: unknown };
      if (!evidenceText(citation.title, 300)) link.title = source.title;
      if (!evidenceText(evidence.siteName, 80)) link.siteName = source.siteName;
      link.snippet ??= source.snippet;
    }
    const key = JSON.stringify([link.url, citation.startIndex, citation.endIndex]);
    if (positions.has(key)) continue;
    positions.add(key);
    cited.set(link.url, link);
    if (!sources.has(link.url)) sources.set(link.url, { ...link, kind: 'citation' });
    citations.push({ ...link, startIndex: citation.startIndex, endIndex: citation.endIndex });
  }
  return { sources: [...sources.values()], citations, cited: [...cited.values()] };
}

type MarkdownToken = ReturnType<MarkdownIt['parse']>[number];

/** Source offsets refer to the original answer, before Markdown parsing. Decorate
 * parsed tokens instead of interpolating URLs into untrusted Markdown or HTML.
 * A plain-text inline block can be split exactly at a citation boundary. For
 * formatted blocks, attach the link to that same inline block without breaking
 * emphasis, links, tables or code. Sources without a safe inline position
 * remain available in the response's sources action. The original answer is never changed. */
export function renderWebResearchMarkdown(markdown: MarkdownIt, content: string, research?: WebResearch | null) {
  const prepared = prepareWebResearch(content, research);
  const inlineCitations: ResearchLink[] = [];
  if (!prepared.cited.length) return { html: markdown.render(content), sources: prepared.sources, inlineCitations };
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

  function badge(citation: ResearchLink): MarkdownToken[] {
    const ordinal = inlineCitations.push(citation) - 1;
    const group = elementTokens('span', { class: 'web-citation-group', 'data-citation-index': String(ordinal) });
    const link = elementTokens('a', { href: citation.url, class: 'web-research-link web-citation',
      'aria-label': `Fonte: ${citation.siteName} · ${citation.title}` });
    const label = elementTokens('span', { class: 'web-citation__label' });
    const icon = elementTokens('span', { class: 'web-citation__icon', 'aria-hidden': 'true',
      'data-monogram': Array.from(citation.siteName)[0]?.toLocaleUpperCase('pt-BR') || '?' });
    const favicon = researchFavicon(citation.host);
    const iconImage = favicon ? markdown.parseInline('![](https://example.invalid/favicon.ico)', {})[0].children![0] : null;
    if (iconImage) {
      iconImage.attrSet('class', 'web-citation__favicon');
      iconImage.attrSet('src', favicon!);
      iconImage.attrSet('referrerpolicy', 'no-referrer');
      iconImage.attrSet('loading', 'lazy');
      iconImage.attrSet('loading', 'lazy');
    }
    const details = elementTokens('button', { class: 'web-citation__details', type: 'button',
      'aria-label': `Prévia da fonte ${citation.siteName}`, 'aria-expanded': 'false' });
    const info = elementTokens('span', { 'aria-hidden': 'true' });
    return [group[0], link[0], icon[0], ...(iconImage ? [iconImage] : []), icon[1],
      label[0], textToken(citation.siteName), label[1], link[1], details[0], info[0], info[1], details[1], group[1]];
  }

  // Fixed, generated tags only. MarkdownIt escapes all assigned values.
  function elementTokens(tag: 'a' | 'span' | 'button', attrs: Record<string, string>): [MarkdownToken, MarkdownToken] {
    const [open, , close] = markdown.parseInline('[source](https://example.invalid/)', {})[0].children!;
    open.tag = close.tag = tag;
    open.type = tag === 'a' ? 'link_open' : `${tag}_open`;
    close.type = tag === 'a' ? 'link_close' : `${tag}_close`;
    open.attrs = Object.entries(attrs);
    return [open, close];
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

  const citedLinks = new Map(prepared.cited.map(source => [source.url, source]));
  function replaceParenthesizedCitations(children: MarkdownToken[]): Set<string> {
    const replaced = new Set<string>();
    for (let index = 1; index < children.length - 2; index++) {
      const link = children[index];
      const before = children[index - 1];
      if (link.type !== 'link_open' || before.type !== 'text') continue;
      const opening = before.content.match(/\(\s*$/)?.[0];
      if (!opening) continue;
      const sourceUrl = researchLink({ url: link.attrGet('href') })?.url;
      const citation = sourceUrl ? citedLinks.get(sourceUrl) : undefined;
      if (!citation) continue;
      const close = children.findIndex((token, position) => position > index && token.type === 'link_close');
      const after = close >= 0 ? children[close + 1] : undefined;
      const closing = after?.type === 'text' ? after.content.match(/^\s*\)/)?.[0] : undefined;
      if (!after || !closing) continue;

      before.content = before.content.slice(0, -opening.length);
      after.content = after.content.slice(closing.length);
      const marker = badge(citation);
      children.splice(index, close - index + 1, ...marker);
      index += marker.length - 1;
      replaced.add(citation.url);
    }
    return replaced;
  }

  for (const block of blocks) {
    const children = block.children!;
    // Existing answers may spell out a source as an entire parenthesized link.
    // Replace that rendering only when native evidence cites the exact URL.
    const replaced = replaceParenthesizedCitations(children);
    const citations = (assigned.get(block) ?? []).filter(citation => !replaced.has(citation.url));
    if (!citations.length) continue;
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
        }
        if (token.type === 'text' && cursor < token.content.length) replacement.push(textToken(token.content.slice(cursor)));
        sourceCursor = end;
      }
      block.children = replacement;
    } else {
      // At most one marker per source when exact offsets cannot be mapped.
      for (const citation of new Map(citations.map(citation => [citation.url, citation])).values()) {
        children.push(...badge(citation));
      }
    }
  }
  return { html: markdown.renderer.render(tokens, markdown.options, env), sources: prepared.sources, inlineCitations };
}
