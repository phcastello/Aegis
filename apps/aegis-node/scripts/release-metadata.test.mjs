import assert from 'node:assert/strict';
import test from 'node:test';
import { metadata } from './release-metadata.mjs';

const signed = Buffer.from('untrusted comment: fixture\nsignature\ntrusted comment: timestamp:1\tfile:fixture.exe\tversion:0.7.0-stage.2\nglobal-signature').toString('base64');

test('metadata uses immutable versioned assets and contains Windows signature', () => {
  const result = metadata('0.7.0-stage.2', ` ${signed} `, '2026-10-02T00:00:00Z');
  assert.equal(result.platforms['windows-x86_64'].signature, signed);
  assert.equal(result.android.aarch64.versionCode, 7000002);
  for (const platform of Object.values(result.platforms)) {
    assert.ok(platform.url.startsWith('https://github.com/phcastello/Aegis/releases/download/node-v0.7.0-stage.2/'));
  }
});

test('metadata cannot be produced with missing signature or invalid date/version', () => {
  assert.throws(() => metadata('0.7.0-stage.2', ' ', '2026-10-02'));
  assert.throws(() => metadata('latest', 'signed', '2026-10-02'));
  assert.throws(() => metadata('0.7.0-stage.2', signed, 'bad'));
  assert.throws(() => metadata('0.7.0-stage.3', signed, '2026-10-02'), /exact version/);
  assert.throws(() => metadata('0.7.0-stage.2', 'unsigned', '2026-10-02'), /exact version/);
});

test('unstable metadata binds the new preview signature and increasing Android code', () => {
  const signature = Buffer.from(Buffer.from(signed, 'base64').toString('utf8').replace('0.7.0-stage.2', '0.7.0-unstable.11')).toString('base64');
  const result = metadata('0.7.0-unstable.11', signature, '2026-10-07T00:00:00Z');
  assert.equal(result.version, '0.7.0-unstable.11');
  assert.equal(result.android.aarch64.versionCode, 7000011);
  assert.ok(result.platforms['windows-x86_64'].url.includes('/node-v0.7.0-unstable.11/'));
  assert.throws(() => metadata('0.7.0-unstable.12', signature, '2026-10-07T00:00:00Z'), /exact version/);
});


test('stable metadata upgrades unstable.19 with the reserved versionCode and signed immutable URLs', () => {
  const signature = Buffer.from(Buffer.from(signed, 'base64').toString('utf8').replace('0.7.0-stage.2', '0.7.0')).toString('base64');
  const result = metadata('0.7.0', signature, '2026-10-20T00:00:00Z');
  assert.equal(result.version, '0.7.0');
  assert.equal(result.android.aarch64.versionCode, 7000999);
  assert.equal(result.platforms['windows-x86_64'].signature, signature);
  assert.equal(result.android.aarch64.url, 'https://github.com/phcastello/Aegis/releases/download/node-v0.7.0/Aegis-Android-arm64.apk');
  assert.equal(result.platforms['windows-x86_64'].url, 'https://github.com/phcastello/Aegis/releases/download/node-v0.7.0/Aegis-Windows-x86_64-Setup.exe');
  assert.match(result.notes, /Aegis v0\.7\.0 — It's Not Stalking\. It's a Feature\./);
  assert.match(result.notes, /releases\/tag\/v0\.7\.0/);
  assert.throws(() => metadata('0.7.0-unstable.19', signature, '2026-10-20T00:00:00Z'), /exact version/);
});


test('recommended manifests distinguish published version, restored code and installation code', () => {
  const version='0.7.2-unstable.5.rollback.1';
  const signature=Buffer.from(Buffer.from(signed,'base64').toString('utf8').replace('0.7.0-stage.2',version)).toString('base64');
  const distribution={versionCode:7002006,kind:'rollback',rollbackOf:'0.7.2-unstable.5',restoresVersion:'0.7.2-unstable.4',sourceCommit:'a'.repeat(40),sourceVersion:'0.7.2',compatibility:{api:1,nodeData:1},sha256:{'Aegis-Windows-x86_64-Setup.exe':'b'.repeat(64),'Aegis-Android-arm64.apk':'c'.repeat(64)}};
  const result=metadata(version,signature,'2026-10-23T00:00:00Z',distribution);
  assert.equal(result.schema,2); assert.equal(result.kind,'rollback');
  assert.equal(result.sourceVersion,'0.7.2'); assert.equal(result.restoresVersion,'0.7.2-unstable.4');
  assert.equal(result.versionCode,7002006); assert.equal(result.android.aarch64.versionCode,7002006);
  assert.equal(result.publishedAt,result.pub_date);
  assert.equal(metadata(version,signature,'2026-10-23T00:00:00Z',{...distribution,sourceVersion:'0.7.0-stage.10'}).sourceVersion,'0.7.0-stage.10');
  assert.match(result.notes,/recuperação/);
  for (const change of [{versionCode:0},{sourceCommit:'main'},{sourceVersion:'0.7.2+build'},{kind:'normal'},{rollbackOf:version},{restoresVersion:'0.7.2-unstable.6'},{compatibility:{api:2,nodeData:1}},{sha256:{}}])
    assert.throws(() => metadata(version,signature,'2026-10-23T00:00:00Z',{...distribution,...change}));
});
