import assert from 'node:assert/strict';
import test from 'node:test';
import { loadTypeScript } from './loadTypeScript.mjs';
const env = await loadTypeScript('../src/services/clientEnvironment.ts');
const { observeGoogleConnection } = await loadTypeScript('../src/services/googleConnection.ts');

test('native OAuth waits for return/focus and cleans up without polling an old connection immediately', async () => {
  globalThis.window = new EventTarget();
  globalThis.document = Object.assign(new EventTarget(), { visibilityState: 'visible' });
  env.configureClient({ apiBaseUrl: '', externalLinks: 'system', openExternalUrl: async () => {} });
  let started = 0, connected = 0;
  const stop = observeGoogleConnection({ started: () => started++, connected: () => connected++, failed() {} });
  await env.openClientLink('https://accounts.google.com/', true);
  assert.equal(started, 1); assert.equal(connected, 0);
  document.visibilityState = 'hidden'; document.dispatchEvent(new Event('visibilitychange'));
  assert.equal(connected, 0);
  document.visibilityState = 'visible'; window.dispatchEvent(new Event('focus'));
  assert.equal(connected, 1);
  window.dispatchEvent(new Event('focus')); assert.equal(connected, 1);
  stop();
  await env.openClientLink('https://accounts.google.com/', true);
  assert.equal(started, 1);
});

test('web callback retains connection/error URL handling and removes only OAuth parameters', () => {
  const replacements = [];
  globalThis.document = { title: 'Aegis' };
  globalThis.window = { location: { href: 'https://web.example.test/?email=connected&keep=1#chat' },
    history: { replaceState(_state, _title, url) { replacements.push(url); } } };
  env.configureClient({ apiBaseUrl: '', externalLinks: 'browser' });
  let connected = 0;
  const stop = observeGoogleConnection({ connected: () => connected++, failed() { assert.fail('Unexpected failure'); } });
  assert.equal(connected, 1); assert.deepEqual(replacements, ['/?keep=1#chat']); stop();
  window.location.href = 'https://web.example.test/?email=connect_failed&email_error_code=google_rejected';
  let code;
  observeGoogleConnection({ connected() { assert.fail('Unexpected connection'); }, failed(value) { code = value; } })();
  assert.equal(code, 'google_rejected');
});
