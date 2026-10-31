import { computed, onBeforeUnmount, ref } from 'vue';
import { handsFree, handsFreeError, type HandsFreeDiagnosticCode, type HandsFreeOperation, type HandsFreeState } from '../services/handsFree.js';

export function useHandsFreeSettings(call: typeof handsFree = handsFree) {
  const state = ref<HandsFreeState | null>(null);
  const busy = ref(false);
  const current = ref(false);
  const error = ref('');
  const diagnosticCode = ref<HandsFreeDiagnosticCode | null>(null);
  const stale = computed(() => state.value !== null && !current.value);
  const canAct = computed(() => state.value !== null && current.value && !busy.value);
  let disposed = false;
  let generation = 0;
  let refreshQueued = false;

  async function request(operation: HandsFreeOperation, options?: { enabled: boolean; diagnostic: boolean }): Promise<void> {
    if (disposed) return;
    if (busy.value) { if (operation === 'state') refreshQueued = true; return; }
    const token = ++generation;
    busy.value = true;
    current.value = false;
    error.value = '';
    diagnosticCode.value = null;
    const owns = () => !disposed && token === generation;
    try {
      const next = await call(operation, options);
      if (owns()) { state.value = next; current.value = true; error.value = ''; diagnosticCode.value = null; }
    } catch (reason) {
      if (owns()) { const safe = handsFreeError(reason); error.value = safe.message; diagnosticCode.value = safe.code; }
    } finally {
      if (owns()) {
        busy.value = false;
        if (refreshQueued) { refreshQueued = false; void request('state'); }
      }
    }
  }
  const refresh = () => request('state');
  async function run(operation: Exclude<HandsFreeOperation, 'state'>, enabled?: boolean, diagnostic?: boolean): Promise<void> {
    if (!canAct.value || !state.value) return;
    await request(operation, operation === 'configure' ? {
      enabled: enabled ?? state.value.enabled,
      diagnostic: diagnostic ?? state.value.diagnostic
    } : undefined);
  }
  function dispose() { disposed = true; generation++; refreshQueued = false; }
  onBeforeUnmount(dispose);
  return { state, busy, current, stale, canAct, error, diagnosticCode, refresh, run, dispose };
}
