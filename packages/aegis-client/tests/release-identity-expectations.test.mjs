import assert from 'node:assert/strict';
import test from 'node:test';
import { expectedReleaseIdentity, newerAvailableFixture } from './release-identity-expectations.mjs';

test('native smoke retains the assigned v0.7 identity for installed previews and patches', () => {
  for (const version of ['0.7.0', '0.7.2', '0.7.2-unstable.15', 'v0.7.1']) {
    assert.deepEqual(expectedReleaseIdentity(version), {
      versionLabel: `v${version.replace(/^v/, '')}`,
      codename: "It's Not Stalking. It's a Feature.", tagline: "You're terrible at hiding.",
    });
  }
});
test('native smoke asserts the assigned v0.8 identity without a tagline', () => {
  for (const version of ['0.8.0', '0.8.0-unstable.11', '0.8.2'])
    assert.deepEqual(expectedReleaseIdentity(version), {
      versionLabel: `v${version}`, codename: 'I Miss the Peace and Quiet', tagline: null,
    });
});
test('new release lines assert explicit unavailable editorial identity, not the previous family', () => {
  for (const version of ['0.9.0', '1.0.0', '0.80.0'])
    assert.deepEqual(expectedReleaseIdentity(version), {
      versionLabel: `v${version}`, codename: 'Codinome indisponível', tagline: null,
    });
});
test('available update fixture differs from the installed line including v0.8 and previews', () => {
  assert.equal(newerAvailableFixture('0.7.2-unstable.15'), '0.8.0');
  assert.equal(newerAvailableFixture('0.8.0-unstable.1'), '0.9.0');
  assert.equal(newerAvailableFixture('1.0.0'), '1.1.0');
});
