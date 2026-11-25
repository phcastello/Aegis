/** Editorial identity belongs to a release family; the caller owns its runtime/build version. */
export function getReleaseIdentity(value?: string | null) {
  const version = value?.trim().replace(/^v/, '') ?? '';
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/.exec(version);
  const valid = !!match && (!match[4] || match[4].split('.').every(part => !/^0\d+$/.test(part)));
  const family = valid ? `${match![1]}.${match![2]}` : null;
  const identities: Record<string, { codename: string; tagline: string | null }> = {
    '0.7': { codename: "It's Not Stalking. It's a Feature.", tagline: "You're terrible at hiding." },
    '0.8': { codename: 'I Miss the Peace and Quiet', tagline: null },
  };
  const identity = family ? identities[family] : undefined;
  return {
    version: valid ? version : null,
    versionLabel: valid ? `v${version}` : 'Versão indisponível',
    codename: identity?.codename ?? 'Codinome indisponível',
    tagline: identity?.tagline ?? null,
  };
}
