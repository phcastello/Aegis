import assert from 'node:assert/strict';
import test from 'node:test';
import { androidVersionCode, synchronize, parseDistributionVersion, compareVersions, rollbackVersion, allocateVersionCode, validateVersionCode } from './version.mjs';

test('Android codes increase across legacy previews, unstable channel, stable and next patch', () => {
  const versions = ['0.7.0-stage.1', '0.7.0-stage.10', '0.7.0-unstable.11', '0.7.0-unstable.12', '0.7.0-unstable.13', '0.7.0-unstable.14', '0.7.0-unstable.19', '0.7.0', '0.7.1-unstable.1', '0.8.0-unstable.1', '1.0.0'];
  const codes = versions.map(androidVersionCode);
  assert.deepEqual(codes, [...codes].sort((a, b) => a - b));
  assert.equal(androidVersionCode('0.7.0-stage.2'), 7000002);
  assert.equal(androidVersionCode('0.7.0-unstable.12'), 7000012);
  assert.equal(androidVersionCode('0.7.0'), 7000999);
});

test('ambiguous, invalid and overflowing versions are rejected', () => {
  for (const value of ['0.7.0-stage01', '0.7.0-stage.0', '0.7.0-stage.999', '0.7.0+build', '0.100.0', '21.0.0', '0.7.1000', 'latest', '00.7.0', '0.07.0', '0.7.0-stage.02', '0.7.0-unstable.01', '0.7.0-unstable.0', '0.7.0-unstable.999', '0.7.0-unstable.11.extra'])
    assert.throws(() => androidVersionCode(value), undefined, value);
});

test('checked-in versions and locks agree with the source of truth', () => {
  const { version, versionCode } = synchronize();
  assert.equal(validateVersionCode(versionCode), versionCode);
  assert.ok(parseDistributionVersion(version));
});


test('SemVer recovery events preserve normal preview and stable progression', () => {
  for (const sequence of [
    ['0.7.2-unstable.4','0.7.2-unstable.5','0.7.2-unstable.5.rollback.1','0.7.2-unstable.5.rollback.2','0.7.2-unstable.6','0.7.2'],
    ['0.7.2','0.7.3-rollback.1','0.7.3-rollback.2','0.7.3-unstable.1','0.7.3'],
    ['0.7.2-unstable.9','0.7.2-unstable.10'],
  ]) for (let i=1;i<sequence.length;i++) {
    assert.equal(compareVersions(sequence[i-1], sequence[i]), -1);
    assert.equal(compareVersions(sequence[i], sequence[i-1]), 1);
    assert.equal(compareVersions(sequence[i], sequence[i]), 0);
  }
  assert.equal(rollbackVersion('0.7.2-unstable.5',1),'0.7.2-unstable.5.rollback.1');
  assert.equal(rollbackVersion('0.7.2-unstable.5.rollback.1',2),'0.7.2-unstable.5.rollback.2');
  assert.equal(rollbackVersion('0.7.2',1),'0.7.3-rollback.1');
  assert.equal(rollbackVersion('0.7.3-rollback.1',2),'0.7.3-rollback.2');
  assert.equal(rollbackVersion('0.7.2-unstable.5',2),'0.7.2-unstable.5.rollback.2'); // .1 reserved, build failed.
  assert.equal(rollbackVersion('0.7.3-rollback.1',3),'0.7.3-rollback.3');
  assert.throws(() => rollbackVersion('0.7.2-unstable.5.rollback.1',1));
  for (const value of ['0.7.2.rollback.1','0.7.2+rollback.1','0.7.2-unstable.5.rollback.01','0.7.3-rollback.1.rollback.2'])
    assert.throws(() => parseDistributionVersion(value));
});

test('installation numbers are separate, strictly monotonic and preserve legacy stable slots', () => {
  const codes=[7001999,7002001]; // Published v0.7.1 and admitted first v0.7.2 preview.
  const versions=['0.7.2-unstable.2','0.7.2-unstable.2.rollback.1','0.7.2-unstable.3','0.7.2','0.7.3-rollback.1','0.7.3-unstable.1','0.7.3'];
  for (const version of versions) {
    const code=allocateVersionCode(version,codes);
    assert.ok(code>codes.at(-1)); codes.push(code);
  }
  assert.deepEqual(codes.slice(2),[7002002,7002003,7002004,7002999,7003001,7003002,7003999]);
  assert.throws(() => allocateVersionCode('0.7.2-unstable.3',[7002998]),/stable/);
  assert.throws(() => allocateVersionCode('0.7.2',[7002999]),/exceed/);
  assert.throws(() => allocateVersionCode('0.7.3-unstable.1',[2100000000]),/stable/);
  assert.throws(() => allocateVersionCode('0.7.3-unstable.1',[-1]));
});
