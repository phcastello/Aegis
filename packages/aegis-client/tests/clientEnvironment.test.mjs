import assert from 'node:assert/strict';
import test from 'node:test';
import { loadTypeScript } from './loadTypeScript.mjs';
const env = await loadTypeScript('../src/services/clientEnvironment.ts');

test('same-origin web API and configured backend URL normalize without machine defaults', () => {
  assert.equal(env.normalizeApiBaseUrl(''), '');
  assert.equal(env.normalizeApiBaseUrl(' https://api.example.test/// '), 'https://api.example.test');
  assert.equal(env.normalizeApiBaseUrl('https://api.example.test/aegis/'), 'https://api.example.test/aegis');
  for (const value of ['localhost', 'file:///tmp', 'https://user:secret@host.test', 'https://host.test?token=x', 'https://host.test/#x'])
    assert.throws(() => env.normalizeApiBaseUrl(value));
});

test('native links allow only HTTP(S)/mailto, never IPC, shell commands or credentials', async () => {
  const opened = [];
  env.configureClient({ apiBaseUrl: 'https://api.example.test', externalLinks: 'system', openExternalUrl: async url => opened.push(url) });
  for (const value of ['javascript:alert(1)', 'file:///tmp/a', 'tauri://localhost', 'https://user:secret@host.test', 'invalid']) {
    assert.equal(env.externalUrl(value), null);
    await assert.rejects(env.openClientLink(value));
  }
  await env.openClientLink('https://example.test/article');
  await env.openClientLink('mailto:contact@example.test');
  assert.deepEqual(opened, ['https://example.test/article', 'mailto:contact@example.test']);
});

test('external OAuth begins status observation only after browser opening succeeds', async () => {
  let notifications = 0;
  const stop = env.onAuthorizationOpened(() => notifications++);
  env.configureClient({ apiBaseUrl: '', externalLinks: 'system', openExternalUrl: async () => {} });
  await env.openClientLink('https://accounts.google.com/', true);
  assert.equal(notifications, 1);
  env.configureClient({ apiBaseUrl: '', externalLinks: 'system', openExternalUrl: async () => { throw new Error('unavailable'); } });
  await assert.rejects(env.openClientLink('https://accounts.google.com/', true));
  assert.equal(notifications, 1);
  stop();
  env.configureClient({ apiBaseUrl: '', externalLinks: 'browser' });
  await assert.rejects(env.openClientLink('https://example.test'));
  assert.throws(() => env.configureClient({ apiBaseUrl: '', externalLinks: 'system' }));
});
