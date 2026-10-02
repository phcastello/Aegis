import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import ts from 'typescript';

const source = readFileSync(new URL('../src/services/emailConnectionLinks.ts', import.meta.url), 'utf8');
const compiled = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText;
const { isGoogleAuthorizationLink } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);

test('short Google authorization entry links return to the same app tab', () => {
  assert.equal(isGoogleAuthorizationLink('https://api.example.test/api/email/connect?redirect=true'), true);
  assert.equal(isGoogleAuthorizationLink('https://example.test/aegis/api/email/connect?redirect=true'), true);
  assert.equal(isGoogleAuthorizationLink('http://localhost:8090/api/email/connect?redirect=true'), true);
});

test('existing Google consent links remain recognized', () => {
  assert.equal(isGoogleAuthorizationLink('https://accounts.google.com/o/oauth2/v2/auth?client_id=test'), true);
});

test('other links keep their normal navigation', () => {
  for (const href of [null, '', 'invalid', 'javascript:alert(1)', 'https://example.test/',
    'https://example.test/api/email/connect', 'https://example.test/api/email/connect?redirect=false',
    'https://accounts.google.com.example.test/o/oauth2/v2/auth', 'http://accounts.google.com/o/oauth2/v2/auth']) {
    assert.equal(isGoogleAuthorizationLink(href), false, String(href));
  }
});
