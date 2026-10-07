import assert from 'node:assert/strict';
import test from 'node:test';
import { androidVersionCode, synchronize } from './version.mjs';

test('Android codes increase across legacy previews, unstable channel, stable and next patch', () => {
  const versions = ['0.7.0-stage.1', '0.7.0-stage.10', '0.7.0-unstable.11', '0.7.0-unstable.12', '0.7.0-unstable.13', '0.7.0', '0.7.1-unstable.1', '0.8.0-unstable.1', '1.0.0'];
  const codes = versions.map(androidVersionCode);
  assert.deepEqual(codes, [...codes].sort((a, b) => a - b));
  assert.equal(androidVersionCode('0.7.0-stage.2'), 7000002);
});

test('ambiguous, invalid and overflowing versions are rejected', () => {
  for (const value of ['0.7.0-stage01', '0.7.0-stage.0', '0.7.0-stage.999', '0.7.0+build', '0.100.0', '21.0.0', '0.7.1000', 'latest', '00.7.0', '0.07.0', '0.7.0-stage.02', '0.7.0-unstable.01', '0.7.0-unstable.0', '0.7.0-unstable.999', '0.7.0-unstable.11.extra'])
    assert.throws(() => androidVersionCode(value), undefined, value);
});

test('checked-in versions and locks agree with the source of truth', () => {
  const { version, versionCode } = synchronize();
  assert.equal(versionCode, androidVersionCode(version));
});
