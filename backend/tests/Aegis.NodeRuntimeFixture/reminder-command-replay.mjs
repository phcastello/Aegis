// Isolated loopback proof. Build this fixture and install scripts/node-transport-probe first.
// Synthetic credentials and scoped tokens stay in memory and are never printed.
import { randomBytes, randomUUID } from 'node:crypto';
import { mkdtemp, readFile, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { setTimeout as delay } from 'node:timers/promises';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const WebSocket = createRequire(join(root, 'scripts/node-transport-probe/package.json'))('ws');
const origin = 'http://127.0.0.1:18104';
const directory = await mkdtemp(join(tmpdir(), 'aegis-fixture-command-cache-'));
const bootstrap = join(directory, 'bootstrap');
const fixture = spawn(process.env.AEGIS_TEST_DOTNET ?? 'dotnet', [join(root, 'backend/tests/Aegis.NodeRuntimeFixture/bin/Debug/net8.0/Aegis.NodeRuntimeFixture.dll'), bootstrap], {
  cwd: root, stdio: ['ignore', 'ignore', 'ignore'], env: { ...process.env, AEGIS_IDLE_FIXTURE: 'after' },
});
const fixtureStopped = new Promise(resolveStopped => { fixture.once('exit', resolveStopped); fixture.once('error', resolveStopped); });
let socket, heartbeat, hold = true, fixtureStartFailed = false;
const received = [];
const check = (condition, code) => { if (!condition) throw Error(code); };
fixture.on('error', () => { fixtureStartFailed = true; });
async function request(path, body, expected = 200, credential) {
  const response = await fetch(origin + path, {
    method: body === undefined ? 'GET' : 'POST', redirect: 'error', signal: AbortSignal.timeout(10000),
    headers: { 'Content-Type': 'application/json', ...(credential ? { Authorization: 'AegisNode ' + credential } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  check(response.status === expected, 'unexpected_http_status_' + expected + '_' + response.status);
  return response.headers.get('content-type')?.includes('application/json') ? response.json() : null;
}
async function bounded(promise, milliseconds, code) {
  let timer;
  try { return await Promise.race([promise, new Promise((_, reject) => { timer = setTimeout(() => reject(Error(code)), milliseconds); })]); }
  finally { clearTimeout(timer); }
}
function acknowledge(message, status = 'duplicate') {
  socket.send(JSON.stringify({ protocolVersion: 1, type: 'command_result', messageId: randomUUID(), sentAt: new Date().toISOString(), payload: { commandId: message.payload.commandId, status } }));
}
async function connect(node) {
  clearInterval(heartbeat);
  socket = new WebSocket(origin.replace('http:', 'ws:') + '/api/nodes/connect', { headers: { Authorization: 'AegisNode ' + node.credential, 'X-Aegis-Node-Protocol': '1' } });
  const current = socket;
  const ready = new Promise((resolveReady, rejectReady) => {
    current.on('error', () => rejectReady(Error('websocket_error')));
    current.on('open', () => current.send(JSON.stringify({ protocolVersion: 1, type: 'hello', messageId: randomUUID(), sentAt: new Date().toISOString(), payload: { appVersion: '0.7.0-unstable.12', capabilities: [{ name: 'reminder.notify', version: 1 }, { name: 'reminder.withdraw', version: 1 }] } })));
    current.on('message', raw => {
      try {
        const message = JSON.parse(String(raw));
        if (message.type === 'hello_ack') {
          heartbeat = setInterval(() => { if (current.readyState === WebSocket.OPEN) current.send(JSON.stringify({ protocolVersion: 1, type: 'heartbeat', messageId: randomUUID(), sentAt: new Date().toISOString() })); }, message.payload.heartbeatSeconds * 1000);
          resolveReady();
        } else if (message.type === 'command') {
          received.push(message);
          if (!hold) acknowledge(message);
          current.emit('fixture-command');
        }
      } catch { rejectReady(Error('websocket_protocol_error')); current.close(); }
    });
  });
  await bounded(ready, 10000, 'websocket_hello_timeout');
}
async function commandCount(count) {
  while (received.length < count) await bounded(once(socket, 'fixture-command'), 10000, 'command_timeout');
}
const command = (body, expected = 200) => request('/fixture/reminder-command', body, expected);
try {
  let code, replacementCode, ready = false;
  for (let i = 0; i < 600; i++) {
    check(!fixtureStartFailed && fixture.exitCode === null && fixture.signalCode === null, 'fixture_exited_before_ready');
    try { code = (await readFile(bootstrap, 'utf8')).trim(); replacementCode = (await readFile(bootstrap + '.replacement', 'utf8')).trim(); await request('/api/health'); ready = true; break; }
    catch { await delay(50); }
  }
  check(ready && code && replacementCode && code !== replacementCode, 'fixture_bootstrap_timeout');
  if (process.platform !== 'win32') for (const path of [bootstrap, bootstrap + '.replacement']) check(((await stat(path)).mode & 0o777) === 0o600, 'bootstrap_file_not_private');
  const attemptId = randomUUID();
  const node = await request('/api/nodes/pair', { code, attemptId, recoveryKey: randomBytes(32).toString('base64url'), name: 'Synthetic cache proof', platform: 'windows', appVersion: '0.7.0-unstable.12', protocolVersion: 1 });
  await request('/api/nodes/pair/finalize', { attemptId, credential: node.credential });
  await connect(node);
  const original = { nodeId: node.nodeId, reminderId: randomUUID(), commandId: randomUUID(), capability: 'reminder.notify', revision: 1, generation: 1 };
  const first = command(original); first.catch(() => {}); await commandCount(1);
  const envelope = JSON.stringify(received[0].payload);
  // The conflict must resolve while the first native send is still awaiting its result.
  await bounded(command({ ...original, revision: 2 }, 409), 2000, 'cache_lock_held_during_native_send');
  hold = false; acknowledge(received[0], 'success'); await first;
  await delay(30); await command(original); await commandCount(2);
  check(JSON.stringify(received[1].payload) === envelope, 'replay_envelope_changed');
  for (const changed of [{ reminderId: randomUUID() }, { generation: 2 }, { capability: 'reminder.withdraw' }]) await command({ ...original, ...changed }, 409);

  const concurrent = { ...original, commandId: randomUUID() };
  await Promise.all([command(concurrent), command(concurrent)]);
  await command(concurrent);
  const concurrentEnvelopes = received.filter(message => message.payload.commandId === concurrent.commandId).map(message => JSON.stringify(message.payload));
  check(concurrentEnvelopes.length >= 2 && concurrentEnvelopes.every(value => value === concurrentEnvelopes[0]), 'concurrent_creation_reminted_command');

  const withdrawal = { ...original, commandId: randomUUID(), capability: 'reminder.withdraw' };
  await command(withdrawal); await delay(30); await command(withdrawal);
  const withdrawals = received.filter(message => message.payload.commandId === withdrawal.commandId).map(message => JSON.stringify(message.payload));
  check(withdrawals.length === 2 && withdrawals[0] === withdrawals[1], 'withdrawal_replay_changed');
  clearInterval(heartbeat); socket.close(); await bounded(once(socket, 'close'), 3000, 'socket_close_timeout');
  // Three identities are already cached. Offline withdrawals avoid waiting for native I/O.
  for (let i = 3; i < 1000; i++) await command({ ...withdrawal, commandId: randomUUID() });
  await command({ ...withdrawal, commandId: randomUUID() }, 429);
  await command({ ...original, revision: 2 }, 409); // Conflict takes precedence over capacity.
  await connect(node); await command(original);
  check(JSON.stringify(received.at(-1).payload) === envelope, 'capacity_evicted_or_reminted_original');
  console.log('PASS fixture exact notify/withdraw replay, concurrent creation, conflict409, lock released before native I/O, bounded1000/capacity429 and original retention');

  // Lifecycle exceptions retain the real controllers and their normal authorization.
  // All domain routes remain inaccessible while this isolated backend is offline.
  await request('/fixture/backend-offline', { seconds: 300 });
  await request('/api/nodes/me', undefined, 503, node.credential);
  await request('/api/nodes/pairing-codes', {}, 503, node.credential);
  await request('/fixture/backend-offline', { seconds: 300, allowPairing: true });
  await request('/api/nodes/me', undefined, 401);
  await request('/api/nodes/pairing-codes', {}, 401);
  await request('/api/nodes/' + node.nodeId + '/revoke', {}, 401);
  await request('/api/nodes/me', undefined, 200, node.credential);
  for (const [path, body] of [
    ['/api/nodes', undefined], ['/api/nodes/me', {}], ['/api/nodes/pair', undefined],
    ['/api/nodes/not-a-uuid/revoke', {}], ['/api/nodes/revoke', {}], ['/api/nodes/geofences/manifest', undefined],
    ['/api/nodes/geofences/events', {}], ['/api/nodes/geofences/sync', {}],
    ['/api/nodes/reminders/' + original.reminderId + '/actions', {}],
    ['/api/reminder-actions/' + original.reminderId, {}],
  ]) await request(path, body, 503, node.credential);
  const nextCode = await request('/api/nodes/pairing-codes', {}, 200, node.credential);
  const nextAttempt = randomUUID();
  const next = await request('/api/nodes/pair', { code: nextCode.code, attemptId: nextAttempt, recoveryKey: randomBytes(32).toString('base64url'), name: 'Synthetic offline lifecycle proof', platform: 'android', appVersion: '0.7.0-unstable.12', protocolVersion: 1 });
  await request('/api/nodes/pair/finalize', { attemptId: nextAttempt, credential: next.credential });
  await request('/api/nodes/me', undefined, 200, next.credential);
  const staleCode = await request('/api/nodes/pairing-codes', {}, 200, node.credential);
  const pendingCode = await request('/api/nodes/pairing-codes', {}, 200, node.credential);
  const pendingAttempt = randomUUID();
  const pending = await request('/api/nodes/pair', { code: pendingCode.code, attemptId: pendingAttempt, recoveryKey: randomBytes(32).toString('base64url'), name: 'Synthetic invalidated establishment', platform: 'android', appVersion: '0.7.0-unstable.12', protocolVersion: 1 });
  await request('/api/nodes/' + node.nodeId + '/revoke', {}, 200, next.credential);
  await request('/api/nodes/me', undefined, 401, node.credential);
  await request('/api/nodes/geofences/events', {}, 503, next.credential);
  await request('/api/nodes/pair', { code: staleCode.code, attemptId: randomUUID(), recoveryKey: randomBytes(32).toString('base64url'), name: 'Synthetic revoked issuer proof', platform: 'android', appVersion: '0.7.0-unstable.12', protocolVersion: 1 }, 400);
  await request('/api/nodes/pair/finalize', { attemptId: pendingAttempt, credential: pending.credential }, 400);
  const replacementAttempt = randomUUID();
  const replacement = await request('/api/nodes/pair', { code: replacementCode, attemptId: replacementAttempt, recoveryKey: randomBytes(32).toString('base64url'), name: 'Synthetic reserved bootstrap proof', platform: 'android', appVersion: '0.7.0-unstable.12', protocolVersion: 1 });
  await request('/api/nodes/pair/finalize', { attemptId: replacementAttempt, credential: replacement.credential });
  await request('/api/nodes/me', undefined, 200, replacement.credential);
  await request('/fixture/backend-offline', { seconds: 0 });
  await request('/api/nodes/me', undefined, 200, next.credential);
  console.log('PASS fixture offline lifecycle exact method/path allowlist, default503, normal authorization, real pairing/finalization/revocation and domain503');
  console.log('PASS private issuerless replacement bootstrap survives issuer revocation; issued pairing and pending finalization remain rejected');
} finally {
  clearInterval(heartbeat); socket?.terminate();
  try {
    if (fixture.pid && fixture.exitCode === null && fixture.signalCode === null) {
      fixture.kill('SIGTERM');
      try { await bounded(fixtureStopped, 5000, 'fixture_stop_timeout'); }
      catch { fixture.kill('SIGKILL'); await bounded(fixtureStopped, 5000, 'fixture_kill_timeout'); }
    }
  } finally { await rm(directory, { recursive: true, force: true }); }
}
