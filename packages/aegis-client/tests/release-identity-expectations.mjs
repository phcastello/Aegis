// Test expectations are deliberately independent of the product implementation.
// Only an editorial identity actually assigned to this family may be asserted.
export const legacyReleaseIdentity = Object.freeze({
  codename: "It's Not Stalking. It's a Feature.",
  tagline: "You're terrible at hiding.",
});
export function expectedReleaseIdentity(version) {
  const normalized = version.replace(/^v/, '');
  const family = /^(\d+)\.(\d+)\./.exec(normalized);
  if (!family) throw Error('Expected a valid installed release version.');
  return {
    versionLabel: `v${normalized}`,
    ...(family[1] === '0' && family[2] === '7' ? legacyReleaseIdentity
      : { codename: 'Codinome indisponível', tagline: null }),
  };
}
export function newerAvailableFixture(version) {
  const match = /^v?(\d+)\.(\d+)\./.exec(version);
  if (!match) throw Error('Expected a valid installed release version.');
  return `${match[1]}.${Number(match[2]) + 1}.0`;
}
