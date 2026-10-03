import assert from 'node:assert/strict';
import { test, afterEach } from 'node:test';
import { mockIPC, clearMocks } from '@tauri-apps/api/mocks';
import { nodeServices } from '../.test-build/services/nodes.js';
import { useNodes } from '../.test-build/composables/useNodes.js';
afterEach(clearMocks);
const node = { id: 'current', name: 'PC', platform: 'windows', enabled: true, revokedAt: null };
function fixture() {
  let status = { state: 'unpaired', node: null, error: null };
  const list = [node, { ...node, id: 'mobile', name: 'Celular' }];
  return { status: async () => status, pair: async () => status = { state: 'paired', node: list[0], error: null }, list: async () => list,
    rename: async (id, name) => { const item = list.find(n => n.id === id); item.name = name; return item; },
    setEnabled: async (id, enabled) => { const item = list.find(n => n.id === id); item.enabled = enabled; return item; },
    revoke: async () => status = { state: 'revoked', node: null, error: null },
    createPairingCode: async () => ({ code: 'temporary', expiresAt: '2026-10-02T00:10:00Z' }),
    setStatus: value => status = value };
}
test('IPC offers typed management operations and never exports credentials', async () => {
  globalThis.window = {}; const calls = [];
  mockIPC((command, args) => { calls.push([command, args]); return command === 'node_list' ? [] : { state: 'unpaired', node: null, error: null }; });
  await nodeServices.status(); await nodeServices.pair('PC', 'code'); await nodeServices.list();
  await nodeServices.rename('id', 'Pixel'); await nodeServices.setEnabled('id', false); await nodeServices.revoke('id'); await nodeServices.createPairingCode();
  assert.deepEqual(calls.map(c => c[0]), ['node_status', 'node_pair', 'node_list', 'node_rename', 'node_set_enabled', 'node_revoke', 'node_create_pairing_code']);
  assert.doesNotMatch(JSON.stringify(calls), /credential|recoveryKey|Authorization/);
});
test('unpaired chat-independent state becomes paired with current device marker and persistent rename', async () => {
  const service = fixture(); const model = useNodes(service); await model.refresh(); assert.equal(model.identity.value.state, 'unpaired');
  await model.pair('PC', 'code'); assert.equal(model.nodes.value.length, 2); assert.equal(model.isCurrent(model.nodes.value[0]), true); assert.equal(model.isCurrent(model.nodes.value[1]), false);
  await model.rename('mobile', 'Pixel'); assert.equal(model.nodes.value[1].name, 'Pixel');
  const restarted = useNodes(service); await restarted.refresh(); assert.equal(restarted.identity.value.node.id, 'current'); assert.equal(restarted.nodes.value[1].name, 'Pixel');
});
test('disabled preserves current device; revoked clears list and generated code', async () => {
  const service = fixture(); const model = useNodes(service); await model.pair('PC', 'code'); await model.createCode(); assert.equal(model.pairingCode.value.code, 'temporary');
  service.setStatus({ state: 'disabled', node, error: 'disabled' }); await model.refresh(); assert.equal(model.identity.value.state, 'disabled'); assert.deepEqual(model.nodes.value, []); assert.equal(model.pairingCode.value, null);
  service.setStatus({ state: 'paired', node, error: null }); await model.refresh(); assert.equal(model.nodes.value.length, 2);
  await model.revoke('current'); assert.equal(model.identity.value.state, 'revoked'); assert.deepEqual(model.nodes.value, []);
});
test('storage failure and retry show an error without losing accepted pairing state', async () => {
  const service = fixture(); let failing = true; const actualPair = service.pair;
  service.pair = async () => { if (failing) throw Error('secure storage failed'); return actualPair(); };
  const model = useNodes(service); await model.pair('PC', 'code'); assert.equal(model.error.value, 'secure storage failed'); assert.equal(model.busy.value, false);
  failing = false; await model.pair('PC', 'code'); assert.equal(model.identity.value.state, 'paired'); assert.equal(model.error.value, null);
});
test('overlapping pair clicks issue only one pairing request', async () => {
  const service = fixture(); let release; let calls = 0;
  service.pair = async () => { calls++; await new Promise(resolve => release = resolve); return { state: 'unpaired', node: null, error: null }; };
  const model = useNodes(service); const first = model.pair('PC', 'code'); assert.equal(model.identity.value.state, 'pairing'); await model.pair('PC', 'code'); assert.equal(calls, 1); release(); await first;
});
test('a pairing rejection remains visible until the user retries', async () => {
  const service = fixture(); service.pair = async () => ({ state: 'pairingError', node: null, error: 'Código inválido' });
  const model = useNodes(service); await model.pair('PC', 'wrong'); assert.equal(model.identity.value.state, 'pairingError'); assert.equal(model.error.value, 'Código inválido');
});
