import { computed, ref, shallowRef } from 'vue';
import type { AvailableUpdate } from '../services/updates.js';
import { updateFailure, type UpdatePhase } from '../services/updateDiagnostics.js';

const CHECK_INTERVAL = 30 * 60 * 1000;
const FAILED_CHECK_INTERVAL = 60 * 1000;

export function useUpdates(check: () => Promise<AvailableUpdate | null>) {
  const update = shallowRef<AvailableUpdate | null>(null);
  const state = ref<'idle' | 'checking' | 'available' | 'installing' | 'opened' | 'ready' | 'permissionRequired' | 'error'>('idle');
  const error = ref<string | null>(null);
  const progress = ref<number | null>(null);
  const checkedMessage = ref<string | null>(null);
  const diagnostic = ref<{ code: string; phase: UpdatePhase } | null>(null);
  const checkedAt = ref<number | null>(null);
  const preparedVersion = ref<string | null>(null);
  const readyToInstall = computed(() => preparedVersion.value !== null && preparedVersion.value === update.value?.version);
  let nextAutomaticCheck = 0;
  let dismissedVersion: string | null = null;
  const visible = computed(() => ['available', 'installing', 'opened', 'ready', 'permissionRequired', 'error'].includes(state.value));
  async function start(manual = false): Promise<void> {
    if (state.value === 'checking' || state.value === 'installing' || (!manual && (Date.now() < nextAutomaticCheck || ['opened', 'ready', 'permissionRequired'].includes(state.value)))) return;
    nextAutomaticCheck = Date.now() + CHECK_INTERVAL;
    state.value = 'checking';
    error.value = null; checkedMessage.value = null; diagnostic.value = null;
    try {
      const next = await check();
      const previous = update.value;
      if (previous && next?.version === previous.version) {
        try { if (next !== previous) await next.dispose?.(); } catch { /* Keep the existing usable handle. */ }
      } else {
        update.value = next; preparedVersion.value = null;
        try { await previous?.dispose?.(); } catch { /* Disposal cannot hide a successfully discovered version. */ }
      }
      state.value = update.value && (manual || update.value.version !== dismissedVersion) ? readyToInstall.value ? 'ready' : 'available' : 'idle';
      checkedMessage.value = update.value
        ? `${update.value.distributionKind === 'rollback' ? 'Atualização de recuperação' : 'Versão recomendada'} ${update.value.version} disponível.`
        : 'Você está usando a versão mais recente disponível para este Node.';
    } catch (cause) {
      state.value = update.value && (manual || update.value.version !== dismissedVersion) ? readyToInstall.value ? 'ready' : 'available' : 'idle';
      nextAutomaticCheck = Date.now() + FAILED_CHECK_INTERVAL;
      const failure = updateFailure(cause, 'discovery'); diagnostic.value = failure; checkedMessage.value = failure.message;
    } finally { checkedAt.value = Date.now(); } // Errors remain visible in Updates without interrupting chat.
  }
  function dismiss(): void { if (state.value !== 'installing') { dismissedVersion = update.value?.version ?? null; state.value = 'idle'; } }
  async function install(): Promise<void> {
    if (!update.value || state.value === 'installing') return;
    state.value = 'installing'; error.value = null; progress.value = null; diagnostic.value = null;
    try {
      if (update.value.prepare && !readyToInstall.value) { await update.value.prepare((received,total) => {progress.value = total ? Math.min(100,Math.round(received/total*100)) : null;}); preparedVersion.value = update.value.version; state.value = 'ready'; return; }
      const result = await update.value.install((received, total) => { progress.value = total ? Math.min(100, Math.round(received / total * 100)) : null; });
      if (result?.status === 'failed') { const failure = updateFailure(result.code, result.phase); diagnostic.value = failure; error.value = failure.message; state.value = 'error'; return; }
      state.value = result?.status === 'permission_required' ? 'permissionRequired' : 'opened';
    } catch (cause) { const failure = updateFailure(cause, readyToInstall.value ? 'installer_launch' : 'download'); state.value = 'error'; error.value = failure.message; diagnostic.value = failure; }
  }
  return { update, state, visible, error, progress, readyToInstall, checkedMessage, diagnostic, checkedAt, start, dismiss, install };
}
