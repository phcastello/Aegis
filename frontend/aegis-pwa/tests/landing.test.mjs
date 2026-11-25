import assert from 'node:assert/strict';
import test from 'node:test';
import { recommendedDownloads, oauthMessage } from '../landing.js';
const version = '0.8.0-unstable.45';
const base = `https://github.com/phcastello/Aegis/releases/download/node-v${version}/`;
const fixture = () => ({ schema: 2, version,
  platforms: { 'windows-x86_64': { url: base + 'Aegis-Windows-x86_64-Setup.exe', sha256: 'a'.repeat(64) } },
  android: { aarch64: { url: base + 'Aegis-Android-arm64.apk', sha256: 'b'.repeat(64) } }
});
test('recommended downloads are linked only to the official exact release', () => {
  const value = fixture();
  assert.deepEqual(recommendedDownloads(value), { version, windows: value.platforms['windows-x86_64'].url, android: value.android.aarch64.url });
  assert.equal(recommendedDownloads(null), null);
  for (const mutate of [
    v => v.schema = 1,
    v => v.version = '0.8.0/invalid',
    v => v.android.aarch64.url = 'https://example.com/installer.apk',
    v => v.platforms['windows-x86_64'].sha256 = 'missing',
    v => delete v.android
  ]) { const malformed = fixture(); mutate(malformed); assert.equal(recommendedDownloads(malformed), null); }
});
test('OAuth only displays fixed messages and ignores query contents', () => {
  assert.match(oauthMessage('?email=connected'), /Conta Google conectada/);
  assert.match(oauthMessage('?email=connect_failed&email_error_code=unexpected'), /Não foi possível/);
  assert.equal(oauthMessage('?email=unrecognized'), null);
  assert.equal(oauthMessage(''), null);
});
