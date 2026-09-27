export function isGoogleAuthorizationLink(href: string | null): boolean {
  if (!href) return false;
  try {
    const url = new URL(href);
    if (url.protocol !== 'https:' && url.protocol !== 'http:') return false;
    const googleConsent = url.protocol === 'https:' &&
      url.hostname === 'accounts.google.com' && url.pathname === '/o/oauth2/v2/auth';
    const aegisConnect = url.pathname.endsWith('/api/email/connect') &&
      url.searchParams.get('redirect') === 'true';
    return googleConsent || aegisConnect;
  } catch {
    return false;
  }
}
