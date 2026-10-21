/** Editorial identity belongs to a release family; the caller owns its runtime/build version. */
export function getReleaseIdentity(value?: string | null) {
  const version = value?.trim().replace(/^v/, '') ?? '';
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/.exec(version);
  const valid = !!match && (!match[4] || match[4].split('.').every(part => !/^0\d+$/.test(part)));
  const knownFamily = valid && version.startsWith('0.7.');
  return {
    version: valid ? version : null,
    versionLabel: valid ? `v${version}` : 'Versão indisponível',
    codename: knownFamily ? "It's Not Stalking. It's a Feature." : 'Codinome indisponível',
    tagline: knownFamily ? "You're terrible at hiding." : null,
  };
}
