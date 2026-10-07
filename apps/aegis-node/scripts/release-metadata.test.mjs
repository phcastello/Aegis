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
