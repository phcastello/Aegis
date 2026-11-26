import type { WebResearchSourceCount } from '../types/chat';

export interface ResearchSite { host: string; name: string }

const siteNames: Readonly<Record<string, string>> = {
  'g1.globo.com': 'G1 · Globo',
  'globo.com': 'Globo',
  'wikipedia.org': 'Wikipédia',
  'bbc.com': 'BBC',
  'reuters.com': 'Reuters',
  'gov.br': 'Gov.br',
  'agenciabrasil.ebc.com.br': 'Agência Brasil'
};

const siteIconPaths: Readonly<Record<string, string>> = {
  // The canonical page declares this theme icon. The root icon is a 1×1 pixel.
  'agenciabrasil.ebc.com.br': '/sites/default/themes/agenciabrasil_v3/favicon.ico'
};

// Prefer evidence for unfamiliar sites. If a provider supplies only a URL, use the shorter brand label.
function displaySiteName(host: string, reportedName?: string): string {
  if (Object.prototype.hasOwnProperty.call(siteNames, host)) return siteNames[host];
  if (reportedName && !/^(?:https?:\/\/|www\.)|^[a-z0-9.-]+\.[a-z]{2,}$/i.test(reportedName))
    return reportedName;
  const labels = host.split('.');
  const suffix = labels[labels.length - 1];
  const base = labels[labels.length - 2];
  const secondLevel = suffix.length === 2 && /^(ac|co|com|edu|gov|net|org)$/.test(base);
  const brand = secondLevel && labels.length > 2 ? labels[labels.length - 3] : base;
  if (brand.startsWith('xn--')) return brand;
  return brand.split('-').filter(Boolean)
    .map(part => part[0].toLocaleUpperCase('pt-BR') + part.slice(1)).join(' ');
}

export function researchSite(value: string, reportedName?: string): ResearchSite | null {
  if (!value || /[\s\\]/.test(value)) return null;
  try {
    const url = new URL(value.includes('://') ? value : 'https://' + value);
    if ((url.protocol !== 'http:' && url.protocol !== 'https:') || url.username || url.password) return null;
    let host = url.hostname.toLowerCase().replace(/\.$/, '').replace(/^www\./, '');
    if (!/^(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z][a-z0-9-]{1,62}$/.test(host)) return null;
    // The visual group should represent the site, even for different Wikipedia languages.
    if (host.endsWith('.wikipedia.org')) host = 'wikipedia.org';
    return { host, name: displaySiteName(host, reportedName) };
  } catch {
    return null;
  }
}

export function researchFavicon(host: string): string | undefined {
  const site = researchSite(host);
  if (!site || (host !== site.host && host !== 'www.' + site.host) ||
    /(?:^|\.)(?:local|localhost|test|example|invalid|onion|internal|arpa)$/.test(site.host)) return undefined;
  const path = Object.prototype.hasOwnProperty.call(siteIconPaths, site.host) ? siteIconPaths[site.host] : '/favicon.ico';
  return 'https://' + site.host + path;
}

export function isUsableResearchFavicon(image: Pick<HTMLImageElement, 'complete' | 'naturalWidth' | 'naturalHeight'>): boolean {
  return image.complete && image.naturalWidth > 1 && image.naturalHeight > 1;
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
