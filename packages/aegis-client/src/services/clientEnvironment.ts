export interface ClientEnvironment {
  apiBaseUrl: string;
  externalLinks: 'browser' | 'system';
  openExternalUrl?: (url: string) => Promise<void>;
}

export function normalizeApiBaseUrl(value: string): string {
  const trimmed = value.trim().replace(/\/+$/, '');
  if (!trimmed) return ''; // Web deployment uses nginx's same-origin /api proxy.
  const url = new URL(trimmed);
  if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password || url.search || url.hash)
    throw new Error('A API precisa de uma URL HTTP(S) sem credenciais, query ou fragmento.');
  return trimmed;
}

let environment: ClientEnvironment = { apiBaseUrl: '', externalLinks: 'browser' };
export function configureClient(value: ClientEnvironment): void {
  if (value.externalLinks === 'system' && !value.openExternalUrl) throw new Error('Missing external browser adapter.');
  environment = { ...value, apiBaseUrl: normalizeApiBaseUrl(value.apiBaseUrl) };
}
export function clientEnvironment(): Readonly<ClientEnvironment> { return environment; }

export function externalUrl(value: string): string | null {
  try {
    const url = new URL(value);
    return ['https:', 'http:', 'mailto:'].includes(url.protocol) && !url.username && !url.password ? url.href : null;
  } catch { return null; }
}

const authorizationListeners = new Set<() => void>();
export function onAuthorizationOpened(callback: () => void): () => void {
  authorizationListeners.add(callback);
  return () => authorizationListeners.delete(callback);
}
// Returns whether the host handles this link. Browser navigation remains unchanged.
export async function openClientLink(href: string, authorization = false): Promise<void> {
  const url = externalUrl(href);
  if (!url || environment.externalLinks !== 'system') throw new Error('External link is unavailable.');
  await environment.openExternalUrl!(url);
  if (authorization) for (const listener of authorizationListeners) listener();
}
