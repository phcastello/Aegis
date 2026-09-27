import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';
import typescript from 'typescript';
const source = await readFile(new URL('../src/services/pushNotifications.ts', import.meta.url), 'utf8');
const compiled = typescript.transpileModule(source, { compilerOptions: { module: typescript.ModuleKind.ESNext, target: typescript.ScriptTarget.ES2022 } }).outputText;
const { createNotificationControl, readyForPush, readPushRegistration, applicationServerKey, notificationFailureMessage, NotificationSetupError } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);
function fixture() {
  const calls = [];
  const saved = { subscriptionId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa', token: 'management' };
  const subscription = { endpoint: 'https://fcm.googleapis.com/test', expirationTime: null, options: { applicationServerKey: applicationServerKey('AQID').buffer }, unsubscribe: async () => { calls.push('unsubscribe'); return true; } };
  const f = { calls, saved, subscription, local: null, actual: null, permission: 'default', config: { enabled: true, publicKey: 'AQID' }, status: true };
  f.manager = {
    getSubscription: async () => { calls.push('getSubscription'); return f.actual; },
    subscribe: async options => { calls.push('subscribe'); assert.equal(options.userVisibleOnly, true); assert.deepEqual(options.applicationServerKey, applicationServerKey('AQID')); f.actual = subscription; return subscription; }
  };
  f.browser = { supported: true, permission: () => f.permission, requestPermission: async () => { calls.push('permission'); f.permission = 'granted'; return f.permission; }, ready: async () => { calls.push('ready'); return { pushManager: f.manager }; } };
  f.backend = {
    configuration: async () => { calls.push('configuration'); return f.config; },
    register: async s => { calls.push('register'); assert.equal(s, subscription); return saved; },
    status: async (s, endpoint) => { calls.push('status'); assert.deepEqual(s, saved); assert.equal(endpoint, subscription.endpoint); return { active: f.status }; },
    disable: async s => { calls.push('disable'); assert.deepEqual(s, saved); }
  };
  f.storage = { read: () => f.local, save: s => { calls.push('save'); f.local = s; }, clear: () => { calls.push('clear'); f.local = null; } };
  f.controller = () => createNotificationControl(f.browser, f.backend, f.storage);
  return f;
}
const rejectsCode = (operation, code) => assert.rejects(operation, e => e instanceof NotificationSetupError && e.code === code);
test('activation confirms permission, worker, subscription, POST and backend status in order', async () => {
  const f = fixture(); const control = f.controller();
  assert.equal(await control.reconcile(), false); assert.deepEqual(f.calls, ['configuration']); f.calls.length = 0;
  assert.equal(await control.activate(), true);
  assert.deepEqual(f.calls, ['permission', 'ready', 'getSubscription', 'subscribe', 'register', 'save', 'status', 'getSubscription']);
  assert.deepEqual(f.local, f.saved);
});
test('activation does not complete while status confirmation is pending', async () => {
  const f = fixture(); let release; f.backend.status = () => new Promise(resolve => { release = resolve; });
  let active = false; const activation = f.controller().activate().then(result => { active = result; });
  await new Promise(resolve => setImmediate(resolve)); assert.equal(active, false);
  release({ active: true }); await activation; assert.equal(active, true);
});
test('permission denied never subscribes or registers and reconciles old backend record', async () => {
  const f = fixture(); f.local = f.saved;
  f.browser.requestPermission = async () => { f.permission = 'denied'; return 'denied'; };
  await rejectsCode(f.controller().activate(), 'permission_denied');
  assert.equal(f.calls.includes('subscribe'), false); assert.equal(f.calls.includes('register'), false);
  assert.equal(f.local, null); assert.ok(f.calls.includes('disable'));
});
test('permission granted then subscribe NotAllowedError is a subscription failure, never permission denied', async () => {
  const f = fixture(); f.manager.subscribe = async () => { throw new DOMException('provider internals', 'NotAllowedError'); };
  await rejectsCode(f.controller().activate(), 'push_subscription_failed');
  assert.equal(f.permission, 'granted'); assert.equal(f.local, null); assert.equal(f.calls.includes('register'), false);
});
test('permission granted then backend registration fails, activation stays inactive', async () => {
  const f = fixture(); f.backend.register = async () => { throw new Error('POST failed'); };
  await rejectsCode(f.controller().activate(), 'backend_registration_failed');
  assert.equal(f.permission, 'granted'); assert.equal(f.local, null); assert.equal(f.calls.includes('status'), false);
});
test('backend registration succeeds but inactive status cannot activate UI', async () => {
  const f = fixture(); f.status = false;
  await rejectsCode(f.controller().activate(), 'subscription_inactive');
  assert.ok(f.calls.includes('disable')); assert.deepEqual(f.local, f.saved);
});
test('failed status verification retains management credentials for later reconciliation', async () => {
  const f = fixture(); f.backend.status = async () => { throw new Error('offline'); };
  await rejectsCode(f.controller().activate(), 'backend_registration_failed'); assert.deepEqual(f.local, f.saved);
});
test('unconfigured backend reproduces granted-permission incident and refuses activation before prompting', async () => {
  const f = fixture(); f.permission = 'granted'; f.config = { enabled: false, publicKey: null };
  await rejectsCode(f.controller().activate(), 'backend_push_not_configured');
  assert.deepEqual(f.calls, ['configuration']);
});
test('unsupported browser and unavailable worker have distinct diagnostics', async () => {
  const f = fixture(); f.browser.supported = false;
  await rejectsCode(f.controller().activate(), 'push_not_supported'); assert.deepEqual(f.calls, []);
  f.browser.supported = true; f.browser.ready = async () => { throw new Error('no worker'); };
  await rejectsCode(f.controller().activate(), 'service_worker_unavailable');
});
for (const permission of ['default', 'denied']) test(`external permission ${permission} disables previous backend record without prompting`, async () => {
  const f = fixture(); f.permission = permission; f.local = f.saved;
  assert.equal(await f.controller().reconcile(), false);
  assert.equal(f.local, null); assert.deepEqual(f.calls, ['disable', 'clear', 'configuration']);
});
test('missing browser subscription disables old backend registration', async () => {
  const f = fixture(); f.permission = 'granted'; f.local = f.saved;
  assert.equal(await f.controller().reconcile(), false);
  assert.equal(f.local, null); assert.deepEqual(f.calls, ['ready', 'getSubscription', 'disable', 'clear', 'configuration']);
});
test('offline revocation retains token and retries reconciliation next time', async () => {
  const f = fixture(); f.local = f.saved; const disable = f.backend.disable;
  f.backend.disable = async () => { throw new Error('offline'); };
  const control = f.controller(); await rejectsCode(control.reconcile(), 'backend_registration_failed'); assert.deepEqual(f.local, f.saved);
  f.backend.disable = disable; assert.equal(await control.reconcile(), false); assert.equal(f.local, null);
});
test('initial state requires permission plus actual subscription plus matching backend record', async () => {
  const f = fixture(); f.permission = 'granted'; f.actual = f.subscription; f.local = f.saved;
  assert.equal(await f.controller().reconcile(), true); assert.equal(f.calls.includes('permission'), false);
  f.status = false; assert.equal(await f.controller().reconcile(), false); assert.deepEqual(f.local, f.saved);
});
test('subscription without saved registration stays inactive until explicit activation repairs registration', async () => {
  const f = fixture(); f.permission = 'granted'; f.actual = f.subscription;
  const control = f.controller(); assert.equal(await control.reconcile(), false);
  assert.equal(await control.activate(), true); assert.equal(f.calls.includes('subscribe'), false);
});
test('VAPID rotation reconciles old record and replaces mismatched subscription', async () => {
  const f = fixture(); f.permission = 'granted'; f.local = f.saved;
  f.actual = { ...f.subscription, options: { applicationServerKey: applicationServerKey('AQIDBA').buffer } };
  const control = f.controller(); assert.equal(await control.reconcile(), false); assert.equal(f.local, null);
  assert.equal(await control.activate(), true); assert.ok(f.calls.includes('unsubscribe')); assert.ok(f.calls.includes('subscribe'));
});
test('disable retains backend audit through API, unsubscribes and clears local credentials', async () => {
  const f = fixture(); f.local = f.saved; f.actual = f.subscription;
  await f.controller().deactivate(); assert.equal(f.local, null);
  assert.deepEqual(f.calls, ['disable', 'clear', 'ready', 'getSubscription', 'unsubscribe']);
});
test('worker readiness timeout and invalid registration cannot hang activation', async () => {
  await rejectsCode(readyForPush(new Promise(() => {}), 1), 'service_worker_unavailable');
  await rejectsCode(readyForPush(Promise.resolve({ active: null, pushManager: {} })), 'service_worker_unavailable');
  const worker = { active: {}, pushManager: {} }; assert.equal(await readyForPush(Promise.resolve(worker)), worker);
});
test('corrupt storage never proves activation; error messages never expose raw browser/backend errors', () => {
  assert.equal(readPushRegistration({ getItem: () => '{' }), null);
  assert.equal(readPushRegistration({ getItem: () => JSON.stringify({ subscriptionId: '../other', token: 'x' }) }), null);
  assert.match(notificationFailureMessage(new NotificationSetupError('permission_denied')), /Permissão negada/);
  assert.doesNotMatch(notificationFailureMessage(new DOMException('provider internals', 'NotAllowedError')), /Permissão negada|internals/);
  assert.doesNotMatch(notificationFailureMessage(new Error('https://private.example/token')), /https|token/);
});

test('disabled backend endpoint is replaced on explicit re-enrollment, including after reopening', async () => {
  const f = fixture(); f.permission = 'granted'; f.actual = f.subscription; f.local = f.saved; f.status = false;
  assert.equal(await f.controller().reconcile(), false);
  const reopened = f.controller(); assert.equal(await reopened.reconcile(), false);
  f.status = true; assert.equal(await reopened.activate(), true);
  assert.ok(f.calls.includes('unsubscribe')); assert.ok(f.calls.includes('subscribe'));
});
test('browser subscription disappearing during registration disables backend and refuses activation', async () => {
  const f = fixture(); const register = f.backend.register;
  f.backend.register = async s => { const result = await register(s); f.actual = null; return result; };
  await rejectsCode(f.controller().activate(), 'subscription_inactive'); assert.ok(f.calls.includes('disable'));
});
