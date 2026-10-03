// Credentials and bootstrap codes exist only in process memory. Never print raw errors/responses.
import { randomBytes, randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import WebSocket from 'ws';
const origin = process.env.AEGIS_PROBE_ORIGIN ?? 'https://aegis.phcastello.com';
const local = process.env.AEGIS_PROBE_CLEANUP_ORIGIN ?? 'http://127.0.0.1:8090';
const container = process.env.AEGIS_PROBE_CONTAINER ?? 'aegis-api';
const version = JSON.parse(readFileSync(new URL('../../apps/aegis-node/package.json', import.meta.url))).version;
const cycles = Number(process.env.AEGIS_PROBE_CYCLES ?? 6);
if (!Number.isInteger(cycles) || cycles < 4 || cycles > 12) throw Error('Require 4–12 heartbeat cycles');
const nodes = [], sockets = [];
// Opt-in for isolated Stage 05 fixtures; production probe remains backward compatible.
const notifications = process.env.AEGIS_PROBE_NOTIFICATIONS === 'true';
const capabilities = process.env.AEGIS_PROBE_CAPABILITIES === 'true' || notifications;
checkOrigin(origin);
function checkOrigin(value) { const url = new URL(value); if (url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost', '127.0.0.1'].includes(url.hostname))) throw Error('Require HTTPS or explicit loopback fixture'); }
const safeCodes = new Set(['websocket_required', 'node_disabled', 'node_revoked', 'protocol_mismatch', 'node_authentication_required']);
function check(condition, reason) { if (!condition) throw Error(reason); }
async function http(path, secret, body, base = origin, method) {
  const response = await fetch(base + '/api/nodes' + path, { method: method ?? (body === undefined ? 'GET' : 'POST'), redirect: 'error', signal: AbortSignal.timeout(10000),
    headers: { 'Content-Type': 'application/json', ...(secret ? { Authorization: 'AegisNode ' + secret } : {}) }, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) { console.log(`API HTTP=${response.status} Path=${path}`); throw Error("api_http_status"); } return response.json();
}
async function pair(code, platform) {
  const attemptId = randomUUID();
  const receipt = await http('/pair', null, { attemptId, code, recoveryKey: randomBytes(32).toString('base64url'), name: 'Disposable public WSS probe ' + platform, platform, appVersion: version, protocolVersion: 1 });
  // Track before finalize so cleanup still revokes a partially established diagnostic identity.
  const node = { secret: receipt.credential, id: receipt.nodeId, attemptId, confirmed: false, capabilities: platform === 'windows' ? [{ name: 'audio.input', version: 1 }, { name: 'audio.output', version: 1 }] : [{ name: 'audio.input', version: 1 }] }; nodes.push(node);
  if (notifications) node.capabilities.push({name:"notification.show",version:1});
  const confirmed = await http('/pair/finalize', null, { attemptId, credential: node.secret }); node.id = confirmed.id; node.confirmed = true;
  console.log(`Paired disposable NodeId=${node.id} Platform=${platform}`); return node;
}
async function connect(node, socketOrigin = origin) {
  const ws = new WebSocket(socketOrigin.replace(/^http/, 'ws') + '/api/nodes/connect', { maxPayload: 4096, handshakeTimeout: 10000,
    headers: { Authorization: 'AegisNode ' + node.secret, 'X-Aegis-Node-Protocol': '1' } });
  const socket = { ws, timer: null, ackCount: 0 }; sockets.push(socket);
  let upgrade = false, helloId = randomUUID(), pending, deadline, ready, rejectReady;
  socket.ready = new Promise((resolve, reject) => { ready = resolve; rejectReady = reject; });
  socket.ready.catch(() => {});
  socket.closed = new Promise(resolve => ws.once('close', code => { clearTimeout(deadline); clearInterval(socket.timer); console.log(`Closed NodeId=${node.id} Code=${code}`); resolve(code); }));
  socket.done = new Promise((resolve, reject) => {

    deadline = setTimeout(() => reject(Error('hello_timeout')), 15000);
    ws.on('upgrade', response => { upgrade = response.statusCode === 101; console.log(`Upgrade NodeId=${node.id} HTTP=${response.statusCode}`); });
    ws.on('unexpected-response', (_, response) => {
      let body = ''; response.on('data', chunk => { if (body.length < 4096) body += chunk; });
      response.on('end', () => { let code = 'http_status'; try { const parsed = JSON.parse(body); if (safeCodes.has(parsed.code)) code = parsed.code; } catch {}
        console.log(`Rejected NodeId=${node.id} HTTP=${response.statusCode} Reason=${code}`); reject(Error(code)); });
    });
    ws.on('error', () => reject(Error('websocket_network_error')));
    ws.on('close', () => { if (socket.ackCount < cycles) reject(Error('closed_before_required_heartbeats')); });
    ws.on('open', () => { check(upgrade, 'upgrade_failed'); ws.send(JSON.stringify({ protocolVersion: 1, type: 'hello', messageId: helloId, sentAt: new Date().toISOString(), payload: { appVersion: version, ...(capabilities ? { capabilities: node.capabilities } : {}) } })); });
    ws.on('message', raw => {
      try {
        const message = JSON.parse(String(raw)); check(message.protocolVersion === 1, 'protocol_error');
        if (message.type === 'hello_ack') {
          check(message.messageId === helloId, 'hello_rejected'); const { heartbeatSeconds, timeoutSeconds } = message.payload;
          check(Number.isInteger(heartbeatSeconds) && heartbeatSeconds >= 1 && timeoutSeconds >= heartbeatSeconds * 2, 'invalid_heartbeat_configuration');
          if (origin.startsWith('https:')) check(heartbeatSeconds === 25 && timeoutSeconds === 75, 'unexpected_production_timing');
          console.log(`HelloAck NodeId=${node.id} Interval=${heartbeatSeconds}s Timeout=${timeoutSeconds}s`); clearTimeout(deadline); ready();
          socket.timer = setInterval(() => { if (pending) { reject(Error('heartbeat_timeout')); return; } pending = randomUUID();
            ws.send(JSON.stringify({ protocolVersion: 1, type: 'heartbeat', messageId: pending, sentAt: new Date().toISOString() }));
          }, heartbeatSeconds * 1000);
          deadline = setTimeout(() => reject(Error('session_timeout')), heartbeatSeconds * (cycles + 1) * 1000 + 10000);
        } else if (notifications && message.type === 'command') {
          check(message.payload.capability === 'notification.show' && message.payload.capabilityVersion === 1, 'unsupported_command');
          check(Date.parse(message.payload.expiresAt)>Date.now(), 'expired_command');
          ws.send(JSON.stringify({protocolVersion:1,type:'command_result',messageId:randomUUID(),sentAt:new Date().toISOString(),payload:{commandId:message.payload.commandId,status:'success'}}));
          console.log(`CommandResult NodeId=${node.id} Status=success Executor=fixture`);
        } else {
          check(message.type === 'heartbeat_ack' && message.messageId === pending, 'heartbeat_rejected'); pending = undefined; socket.ackCount++;
          console.log(`HeartbeatAck NodeId=${node.id} Cycle=${socket.ackCount}`);
          if (socket.ackCount === cycles) { clearTimeout(deadline); resolve(); }
        }
      } catch { reject(Error('protocol_error')); }
    });
  });
  socket.done.catch(rejectReady);
  return socket;
}
async function cleanup() {
  for (const s of sockets) { clearInterval(s.timer); if (s.ws.readyState === WebSocket.OPEN) s.ws.close(1000, 'probe_complete'); else s.ws.terminate(); }
  for (const node of nodes) {
    if (!node.confirmed) {
      try { await http('/pair/finalize', null, { attemptId: node.attemptId, credential: node.secret }, local); node.confirmed = true; }
      catch { console.error(`CLEANUP FINALIZE FAILED NodeId=${node.id}`); process.exitCode = 1; continue; }
    }
    try { await http('/' + node.id + '/revoke', node.secret, {}); console.log(`Revoked disposable NodeId=${node.id}`); }
    catch { try { await http('/' + node.id + '/revoke', node.secret, {}, local); console.log(`Revoked disposable via local cleanup NodeId=${node.id}`); } catch { console.error(`CLEANUP FAILED NodeId=${node.id}`); process.exitCode = 1; } }
  }
}
process.once('SIGINT', () => { void cleanup().finally(() => process.exit(130)); });
process.once('SIGTERM', () => { void cleanup().finally(() => process.exit(143)); });
try {
  const output = process.env.AEGIS_PROBE_CODE_FILE ? 'Pairing code: ' + readFileSync(process.env.AEGIS_PROBE_CODE_FILE, 'utf8').trim() : execFileSync('docker', ['exec', container, 'dotnet', 'node-admin/Aegis.NodeAdmin.dll', 'pairing-code'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  const code = output.match(/Pairing code: ([A-Z0-9-]+)/)?.[1]; check(code, 'bootstrap_failed');
  const first = await pair(code, 'windows');
  if (process.env.AEGIS_PROBE_REJECT_ORIGIN) {
    const rejected = await connect(first, process.env.AEGIS_PROBE_REJECT_ORIGIN);
    let reason; try { await rejected.ready; } catch (error) { reason = error.message; }
    check(reason === 'websocket_required', 'missing_upgrade_not_reproduced'); rejected.ws.terminate();
    console.log('PASS negative proxy fixture: authenticated upgrade stripped => HTTP 400 websocket_required');
  }
  const s1 = await connect(first); await s1.ready;
  const next = await http('/pairing-codes', first.secret, {}); const second = await pair(next.code, 'android'); const s2 = await connect(second); await s2.ready;
  if (notifications) {
    const result = await http('/notifications/test',second.secret,{preferredNodeId:first.id,title:'Aegis probe',body:'Isolated command fixture'});
    check(result.node?.id===first.id && result.transport==='live_websocket' && result.status==='success','notification_dispatch_failed');
    console.log('PASS public/fixture authenticated notification command/result; executor mocked, no physical display assertion');
  }
  await Promise.all([s1.done, s2.done]);
  for (const node of nodes) { const me = await http('/me', node.secret); const list = await http('', node.secret);
    check(me.id === node.id && list.filter(n => n.id === me.id).length === 1, 'current_node_not_unique');
    for (const test of nodes) check(list.find(n => n.id === test.id)?.availability === 'online', 'availability_inconsistent');
    check(me.availability === 'online', 'me_availability_inconsistent');
    check(me.lastHeartbeatAt && Date.now() - Date.parse(me.lastHeartbeatAt) < 60000, 'heartbeat_history_not_advancing');
    console.log(`Presence NodeId=${node.id} Online=true LastHeartbeatAt=${me.lastHeartbeatAt}`); }
  if (capabilities) {
    const list = await http('', first.secret);
    for (const node of nodes) check(JSON.stringify(list.find(n => n.id === node.id).capabilities) === JSON.stringify(node.capabilities), 'capability_snapshot_inconsistent');
    await http('/' + first.id + '/priority', first.secret, { targetPriority: 10 }, origin, 'PATCH');
    await http('/' + second.id + '/priority', first.secret, { targetPriority: 20 }, origin, 'PATCH');
    const resolve = (name, minimumVersion = 1, preferredNodeId = null) => http('/resolve', first.secret, { requiredCapabilities: [{ name, minimumVersion }], preferredNodeId });
    check((await resolve('audio.output')).node?.id === first.id, 'capability_filter_failed');
    check((await resolve('audio.input')).node?.id === second.id, 'priority_failed');
    check((await resolve('audio.input', 1, first.id)).node?.id === first.id, 'preferred_failed');
    check((await resolve('audio.output', 2)).code === 'no_eligible_node', 'version_filter_failed');
    clearInterval(s2.timer); s2.ws.close(1000, 'fixture_offline'); await s2.closed;
    const end = Date.now() + 10000;
    while ((await resolve('audio.input')).node?.id !== first.id) { check(Date.now() < end, 'offline_fallback_failed'); await new Promise(r => setTimeout(r, 100)); }
    // Reconnect uses the same identity but replaces the session and persisted snapshot.
    second.capabilities = [{ name: 'audio.output', version: 2 }];
    const replacement = await connect(second); await replacement.ready; await replacement.done;
    check((await resolve('audio.output', 2)).node?.id === second.id, 'replacement_session_failed');
    check((await resolve('audio.input')).node?.id === first.id, 'old_capability_retained');
    check(JSON.stringify((await http('/me', second.secret)).capabilities) === JSON.stringify(second.capabilities), 'replacement_persistence_failed');
    clearInterval(replacement.timer); replacement.ws.close(1000, 'probe_complete'); await replacement.closed;
    console.log('PASS Stage05 two different capability sets, snapshot replacement, live resolution, priority, preferred, version and offline fallback');
  }
  for (const socket of [s1, s2]) { clearInterval(socket.timer); if (socket.ws.readyState === WebSocket.OPEN) socket.ws.close(1000, 'probe_complete'); check(await socket.closed === 1000, 'abnormal_close'); }
  for (const node of nodes) {
    const end = Date.now() + 10000;
    while ((await http('/me', node.secret)).availability !== 'offline') { check(Date.now() < end, 'disconnect_presence_stale'); await new Promise(r => setTimeout(r, 100)); }
  }
  console.log(`PASS authenticated transport: two Nodes, HTTP 101, hello_ack, ${cycles} heartbeat ACKs each, live list/me consistency`);
} catch (error) { console.error('FAIL phase=' + (safeCodes.has(error.message) || /^[a-z_]+$/.test(error.message) ? error.message : 'diagnostic_failed')); process.exitCode = 1; }
finally { await cleanup(); }
