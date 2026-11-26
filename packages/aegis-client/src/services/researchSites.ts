import type { WebResearchSourceCount } from '../types/chat';

export interface ResearchSite { host: string; name: string }

const siteNames: Readonly<Record<string, string>> = {
  'g1.globo.com': 'G1 · Globo',
  'globo.com': 'Globo',
  'wikipedia.org': 'Wikipédia',
  'bbc.com': 'BBC',
  'reuters.com': 'Reuters',
  'gov.br': 'Gov.br'
};

export function researchSite(value: string): ResearchSite | null {
  if (!value || /[\s\\]/.test(value)) return null;
  try {
    const url = new URL(value.includes('://') ? value : 'https://' + value);
    if ((url.protocol !== 'http:' && url.protocol !== 'https:') || url.username || url.password) return null;
    let host = url.hostname.toLowerCase().replace(/\.$/, '').replace(/^www\./, '');
    if (!/^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{1,62}$/.test(host)) return null;
    // The visual group should represent the site, even for different Wikipedia languages.
    if (host.endsWith('.wikipedia.org')) host = 'wikipedia.org';
    return { host, name: Object.prototype.hasOwnProperty.call(siteNames, host) ? siteNames[host] : host };
  } catch {
    return null;
  }
}

export function researchFavicon(host: string): string | undefined {
  const site = researchSite(host);
  if (!site || site.host !== host || /(?:^|\.)(?:local|localhost|test|example|invalid|onion|internal|arpa)$/.test(host)) return undefined;
  return 'https://' + host + '/favicon.ico';
}

export function mergeResearchSites(previous: ReadonlyMap<string, number>, incoming: readonly WebResearchSourceCount[]): Map<string, number> {
  const next = new Map(previous);
  for (const { url, count } of incoming) {
    const site = researchSite(url);
    if (!site || (!next.has(site.host) && next.size >= 512)) continue;
    next.set(site.host, (next.get(site.host) ?? 0) + count);
  }
  return next;
}

export function topResearchSites(counts: ReadonlyMap<string, number>): ResearchSite[] {
  return [...counts].sort((left, right) => right[1] - left[1])
    .slice(0, 3).flatMap(([host]) => {
      const site = researchSite(host);
      return site ? [site] : [];
    });
}
