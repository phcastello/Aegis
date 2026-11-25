import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import typescript from 'typescript';

const source = await readFile(new URL('../src/services/emailConnectionFeedback.ts', import.meta.url), 'utf8');
const compiled = typescript.transpileModule(source, {
  compilerOptions: { module: typescript.ModuleKind.ESNext, target: typescript.ScriptTarget.ES2022 }
}).outputText;
const { emailConnectionSuccessMessage } =
  await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);

test('successful OAuth displays the confirmed account when available', () => {
  assert.equal(emailConnectionSuccessMessage('name@gmail.com'), 'Conta Google conectada como name@gmail.com.');
  assert.equal(emailConnectionSuccessMessage(null), 'Conta Google conectada.');
});
