import assert from 'node:assert/strict';
import test from 'node:test';
import { loadTypeScript } from './loadTypeScript.mjs';
const { getReleaseIdentity } = await loadTypeScript('../src/services/releaseIdentity.ts');

test('patches retain their release family identity and their own installed version', () => {
  for (const version of ['0.7.0', '0.7.1', '0.7.2', '0.7.1-unstable.1']) {
    const result = getReleaseIdentity(version);
    assert.equal(result.version, version);
    assert.equal(result.versionLabel, `v${version}`);
    assert.equal(result.codename, "It's Not Stalking. It's a Feature.");
    assert.equal(result.tagline, "You're terrible at hiding.");
  }
  assert.equal(getReleaseIdentity('v0.7.1').versionLabel, 'v0.7.1');
});

test('the 0.8 family keeps its codename without a tagline for stable, preview and patch builds', () => {
  for (const version of ['0.8.0', '0.8.0-unstable.1', '0.8.0-unstable.123', '0.8.1', '0.8.9-unstable.2', '0.8.10+build.1']) {
    assert.deepEqual(getReleaseIdentity(version), {
      version, versionLabel: `v${version}`, codename: 'I Miss the Peace and Quiet', tagline: null,
    });
  }
  assert.equal(getReleaseIdentity(' v0.8.0 ').versionLabel, 'v0.8.0');
});

test('missing or unrelated metadata never impersonates a known installed release', () => {
  for (const value of [undefined, null, '', 'unknown', 'vv0.7.1', '0.07.1', '0.7.1-01', '0.08.0', '0.8.0-01', '0.8.0-', '0.8', '0.8.0.1']) {
    assert.deepEqual(getReleaseIdentity(value), {
      version: null, versionLabel: 'Versão indisponível', codename: 'Codinome indisponível', tagline: null,
    });
  }
  for (const version of ['0.6.9', '0.9.0', '1.7.1', '1.8.0', '0.80.0']) {
    assert.deepEqual(getReleaseIdentity(version), {
      version, versionLabel: `v${version}`, codename: 'Codinome indisponível', tagline: null,
    });
  }
});
