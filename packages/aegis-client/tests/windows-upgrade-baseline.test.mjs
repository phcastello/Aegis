import assert from 'node:assert/strict';
import test from 'node:test';
import { signedUpgradeBaseline } from './windows-upgrade-baseline.mjs';

test('the v0.7.1 historical patch keeps its surviving stable v0.7.0 baseline and digest', () => {
  const baseline = signedUpgradeBaseline('0.7.1');
  assert.equal(baseline.version, '0.7.0');
  assert.equal(baseline.sha256, '34504a0b8e197f24977e7f7e886196ad187a01f0acb9b0e81c23407176faeb5f');
  assert.match(baseline.url, /\/node-v0\.7\.0\/Aegis-Windows-x86_64-Setup\.exe$/);
});

test('every future stable or preview distribution upgrades the exact surviving v0.7.1 bytes', () => {
  for (const candidate of ['0.7.2-unstable.1', '0.7.2-unstable.5.rollback.1', '0.7.2-unstable.5.rollback.3', '0.7.3-rollback.1', '0.7.3-rollback.3', '0.7.2', '0.7.3', '0.8.0-unstable.1', '1.0.0']) {
    const baseline = signedUpgradeBaseline(candidate);
    assert.equal(baseline.version, '0.7.1');
    assert.equal(baseline.sha256, 'f4cda9fc477258ff6c5f3bf420b0430c5ac3fbd2e76c01d145a3466657d494c3');
    assert.match(baseline.url, /\/node-v0\.7\.1\/Aegis-Windows-x86_64-Setup\.exe$/);
    assert.doesNotMatch(baseline.url, /unstable|stage/);
  }
});

test('removed historical preview installers are never an operational dependency or a downgrade', () => {
  for (const candidate of ['0.6.1', '0.7.0-stage.10', '0.7.0-unstable.19', '0.7.0'])
    assert.throws(() => signedUpgradeBaseline(candidate), /No older surviving stable/);
  for (const candidate of ['garbage', '0.7.2+metadata', '0.7.2-beta.1'])
    assert.throws(() => signedUpgradeBaseline(candidate));
});
