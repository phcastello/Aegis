import { computed, onBeforeUnmount, ref } from 'vue';
import { handsFree, handsFreeError, type HandsFreeDiagnosticCode, type HandsFreeOperation, type HandsFreeState, type HandsFreeOptions, type HandsFreeBatteryMode } from '../services/handsFree.js';

export function useHandsFreeSettings(call: typeof handsFree = handsFree) {
  const state = ref<HandsFreeState | null>(null);
  const busy = ref(false);
  const current = ref(false);
  const error = ref('');
  const diagnosticCode = ref<HandsFreeDiagnosticCode | null>(null);
  const stale = computed(() => state.value !== null && !current.value);
  const canAct = computed(() => state.value !== null && current.value && !busy.value);
  const batteryLocked = computed(() => Boolean(state.value?.batteryDiagnostics?.active || state.value?.batteryDiagnostics?.requiresResume));
  const canConfigure = computed(() => canAct.value && !batteryLocked.value);
  let disposed = false;
  let generation = 0;
  let refreshQueued = false;

  async function request(operation: HandsFreeOperation, options?: HandsFreeOptions): Promise<void> {
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
      if (owns()) {
        const safe = handsFreeError(reason); error.value = safe.message; diagnosticCode.value = safe.code;
        // A lost reply does not cancel a native start/finish. Read ownership before any retry.
        if (operation === 'batteryStart' || operation === 'batteryFinish') refreshQueued = true;
      }
    } finally {
      if (owns()) {
        busy.value = false;
        if (refreshQueued) { refreshQueued = false; void request('state'); }
      }
    }
  }
  const refresh = () => request('state');
  async function run(operation: Exclude<HandsFreeOperation, 'state'>, enabled?: boolean, diagnostic?: boolean): Promise<void> {
    if (!canAct.value || !state.value || batteryLocked.value) return;
    await request(operation, operation === 'configure' ? {
      enabled: enabled ?? state.value.enabled,
      diagnostic: diagnostic ?? state.value.diagnostic
    } : undefined);
  }
  async function configureCollection(enabled: boolean, limitMiB: number): Promise<void> {
    if (!canConfigure.value || !state.value) return;
    await request('collectionConfigure', { collectionEnabled: enabled, collectionLimitMiB: limitMiB });
  }
  function canStartBattery(mode: HandsFreeBatteryMode): boolean {
    const current = state.value;
    return Boolean(canConfigure.value && current?.batteryDiagnostics && current.assistantSelected &&
      (mode === 'off' || current.permissionGranted) && (mode !== 'detection' || current.modelReady));
  }
  async function startBattery(mode: HandsFreeBatteryMode): Promise<void> {
    if (!canStartBattery(mode)) return;
    await request('batteryStart', { batteryMode: mode });
  }
  async function finishBattery(): Promise<void> {
    if (!canAct.value || !batteryLocked.value) return;
    await request('batteryFinish');
  }
  async function exportBattery(): Promise<void> {
    if (!canAct.value || !state.value?.batteryDiagnostics || state.value.batteryDiagnostics.active) return;
    await request('batteryExport');
  }
  function dispose() { disposed = true; generation++; refreshQueued = false; }
  onBeforeUnmount(dispose);
  return { state, busy, current, stale, canAct, canConfigure, batteryLocked, error, diagnosticCode, refresh, run, configureCollection, canStartBattery, startBattery, finishBattery, exportBattery, dispose };
}
