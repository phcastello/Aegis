import { ref } from 'vue';
import type { IdentityStatus, NodeServices, NodeView, PairingCode } from '../services/nodes.js';
export function useNodes(services: NodeServices) {
  const identity = ref<IdentityStatus>({ state: 'unpaired', node: null, error: null });
  const nodes = ref<NodeView[]>([]);
  const pairingCode = ref<PairingCode | null>(null);
  const busy = ref(false); const error = ref<string | null>(null);
  const sync = async () => {
    identity.value = await services.status();
    nodes.value = identity.value.state === 'paired' && !identity.value.error ? await services.list() : [];
    if (identity.value.state !== 'paired') pairingCode.value = null;
    error.value = identity.value.error;
  };
  const operation = async (action: () => Promise<unknown>) => {
    if (busy.value) return;
    busy.value = true; error.value = null;
    try { await action(); }
    catch (e) {
      const message = e instanceof Error ? e.message : String(e);
      try { identity.value = await services.status(); if (identity.value.state !== 'paired') { nodes.value = []; pairingCode.value = null; } } catch { /* Keep last safe public state; no automatic retry loop. */ }
      error.value = message;
    } finally { busy.value = false; }
  };
  return { identity, nodes, pairingCode, busy, error,
    refresh: () => operation(sync),
    pair: (name: string, code: string) => operation(async () => {
      identity.value = { state: 'pairing', node: null, error: null };
      identity.value = await services.pair(name, code);
      if (identity.value.state === 'paired' && !identity.value.error) await sync();
      else { error.value = identity.value.error; nodes.value = []; }
    }),
    rename: (id: string, name: string) => operation(async () => { await services.rename(id, name); await sync(); }),
    setEnabled: (id: string, enabled: boolean) => operation(async () => { await services.setEnabled(id, enabled); await sync(); }),
    setPriority: (id: string, priority: number) => operation(async () => { await services.setPriority(id, priority); await sync(); }),
    revoke: (id: string) => operation(async () => { await services.revoke(id); await sync(); }),
    createCode: () => operation(async () => { pairingCode.value = await services.createPairingCode(); }),
    isCurrent: (node: NodeView) => node.id === identity.value.node?.id
  };
}
