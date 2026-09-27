import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import typescript from 'typescript';
async function load(path) {
  const source = await readFile(new URL(path, import.meta.url), 'utf8');
  const compiled = typescript.transpileModule(source, { compilerOptions: { module: typescript.ModuleKind.ESNext, target: typescript.ScriptTarget.ES2022 } }).outputText;
  return import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);
}
const { parseReminderPush, reminderNotificationOptions, handleReminderClick } = await load('../src/services/reminderNotification.ts');
const payload = { type: 'reminder', reminderId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', text: 'Comprar ração', acknowledgeToken: 'ack', openToken: 'open' };
test('payload validates type, identity and bounds; notification has OK and stable deduplication tag', () => {
  assert.deepEqual(parseReminderPush(payload), payload);
  assert.equal(parseReminderPush({ ...payload, reminderId: '../other' }), null);
  assert.equal(parseReminderPush({ ...payload, text: 'X'.repeat(601) }), null);
  assert.equal(parseReminderPush({ ...payload, type: 'email' }), null);
  const options = reminderNotificationOptions(payload);
  assert.deepEqual(options.actions, [{ action: 'acknowledge', title: 'OK' }]);
  assert.equal(options.renotify, false);
  assert.equal(options.tag, reminderNotificationOptions(payload).tag);
  assert.equal(options.badge, '/icons/notification-badge.png');
  assert.equal(Object.hasOwn(options, 'icon'), false);
});
test('OK only records acknowledgement and never opens the application', async () => {
  const calls = [];
  await handleReminderClick(payload, 'acknowledge', async (...args) => calls.push(args), async () => calls.push('opened'));
  assert.deepEqual(calls, [[payload.reminderId, 'acknowledge', 'ack']]);
});
test('body click records opening separately and focuses app even if recording fails', async () => {
  const calls = [];
  await handleReminderClick(payload, '', async (...args) => { calls.push(args); throw new Error('offline'); }, async () => calls.push('opened'));
  assert.deepEqual(calls, [[payload.reminderId, 'open', 'open'], 'opened']);
});
