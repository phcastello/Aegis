import { computed, ref } from 'vue';
import type { AvailableUpdate } from '../services/updates.js';

export function useUpdates(check: () => Promise<AvailableUpdate | null>) {
  const update = ref<AvailableUpdate | null>(null);
  const state = ref<'idle' | 'checking' | 'available' | 'installing' | 'opened' | 'ready' | 'permissionRequired' | 'error'>('idle');
  const error = ref<string | null>(null);
  const progress = ref<number | null>(null);
  const checkedMessage = ref<string | null>(null);
  const preparedVersion = ref<string | null>(null);
  const readyToInstall = computed(() => preparedVersion.value !== null && preparedVersion.value === update.value?.version);
  let checked = false;
  const visible = computed(() => ['available', 'installing', 'opened', 'ready', 'permissionRequired', 'error'].includes(state.value));
  async function start(manual = false): Promise<void> {
    if (state.value === 'checking' || state.value === 'installing' || (!manual && checked)) return;
    checked = true;
    state.value = 'checking';
    error.value = null; checkedMessage.value = null;
    try { await update.value?.dispose?.(); update.value = null; preparedVersion.value = null; update.value = await check(); state.value = update.value ? 'available' : 'idle'; checkedMessage.value = update.value ? null : 'Você está usando a versão mais recente disponível neste canal.'; }
    catch { state.value = 'idle'; if (manual) checkedMessage.value = 'Não foi possível verificar atualizações. Confira a conexão e tente novamente.'; } // A failed update check must not interrupt chat.
  }
  function dismiss(): void { if (state.value !== 'installing') state.value = 'idle'; }
  async function install(): Promise<void> {
    if (!update.value || state.value === 'installing') return;
    state.value = 'installing'; error.value = null; progress.value = null;
    try {
      if (update.value.prepare && !readyToInstall.value) { await update.value.prepare((received,total) => {progress.value = total ? Math.min(100,Math.round(received/total*100)) : null;}); preparedVersion.value = update.value.version; state.value = 'ready'; return; }
      const result = await update.value.install((received, total) => { progress.value = total ? Math.min(100, Math.round(received / total * 100)) : null; });
      state.value = result?.status === 'permission_required' ? 'permissionRequired' : 'opened';
    } catch { state.value = 'error'; error.value = 'Não foi possível atualizar. Tente novamente.'; }
  }
  return { update, state, visible, error, progress, readyToInstall, checkedMessage, start, dismiss, install };
}
