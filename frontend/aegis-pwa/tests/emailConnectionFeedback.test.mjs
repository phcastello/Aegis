import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import typescript from 'typescript';

const source = await readFile(new URL('../src/services/emailConnectionFeedback.ts', import.meta.url), 'utf8');
const compiled = typescript.transpileModule(source, {
  compilerOptions: { module: typescript.ModuleKind.ESNext, target: typescript.ScriptTarget.ES2022 }
}).outputText;
const { emailConnectionFailureMessage, emailConnectionSuccessMessage } =
  await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);

test('successful OAuth displays the confirmed account when available', () => {
  assert.equal(emailConnectionSuccessMessage('name@gmail.com'), 'Gmail conectado como name@gmail.com.');
  assert.equal(emailConnectionSuccessMessage(null), 'Gmail conectado.');
});

test('OAuth errors are useful and never surface raw provider text', () => {
  assert.match(emailConnectionFailureMessage('authorization_cancelled'), /cancelada/);
  assert.match(emailConnectionFailureMessage('connection_unconfirmed'), /não confirmou/);
  assert.equal(emailConnectionFailureMessage('stack trace secret'), 'Falha ao conectar o Gmail.');
});
