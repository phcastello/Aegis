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
const { enrollNotifications, applicationServerKey, notificationFailureMessage, NotificationSetupError } = await load('../src/services/pushNotifications.ts');
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
test('denied permission does not subscribe or register', async () => {
  await assert.rejects(enrollNotifications({ enabled: true, publicKey: 'AQID' }, async () => 'denied', {
    getSubscription() { assert.fail(); }, subscribe() { assert.fail(); }
  }, async () => assert.fail()), /Permissão negada/);
});
test('registration reuses a subscription; first enrollment asks PushManager with VAPID', async () => {
  const config = { enabled: true, publicKey: 'AQID' }; const saved = { options: {} };
  let registered;
  await enrollNotifications(config, async () => 'granted', { getSubscription: async () => saved, subscribe: async () => assert.fail() }, async s => { registered = s; });
  assert.equal(registered, saved);
  await enrollNotifications(config, async () => 'granted', { getSubscription: async () => null, subscribe: async options => {
    assert.equal(options.userVisibleOnly, true); assert.deepEqual(options.applicationServerKey, applicationServerKey('AQID')); return saved;
  } }, async s => assert.equal(s, saved));
});
test('unconfigured backend refuses enrollment; native prompt only exists in explicit click handler', async () => {
  await assert.rejects(enrollNotifications({ enabled: false, publicKey: null }, async () => assert.fail(), {}, async () => assert.fail()), /configuradas/);
  const source = await readFile(new URL('../src/components/NotificationControl.vue', import.meta.url), 'utf8');
  assert.ok(source.indexOf('Notification.requestPermission()') < source.indexOf('onMounted(async'));
  assert.equal(source.slice(source.indexOf('onMounted(async')).includes('requestPermission'), false);
});

test('notification errors explain denial and keep technical browser details out of the UI', () => {
  assert.match(notificationFailureMessage(new NotificationSetupError('As notificações ainda não estão configuradas na Aegis.')), /configuradas/);
  assert.match(notificationFailureMessage(new DOMException('provider internals', 'NotAllowedError')), /Permissão negada/);
  assert.equal(notificationFailureMessage(new TypeError('Failed to fetch https://private.example/secret')), 'Não foi possível atualizar notificações. Tente novamente.');
});
