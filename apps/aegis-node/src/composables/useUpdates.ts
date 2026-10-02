import { computed, ref } from 'vue';
import type { AvailableUpdate } from '../services/updates.js';

export function useUpdates(check: () => Promise<AvailableUpdate | null>) {
  const update = ref<AvailableUpdate | null>(null);
  const state = ref<'idle' | 'checking' | 'available' | 'installing' | 'opened' | 'error'>('idle');
  const error = ref<string | null>(null);
  const progress = ref<number | null>(null);
  let checked = false;
  const visible = computed(() => ['available', 'installing', 'opened', 'error'].includes(state.value));
  async function start(): Promise<void> {
    if (checked) return;
    checked = true;
    state.value = 'checking';
    try { update.value = await check(); state.value = update.value ? 'available' : 'idle'; }
    catch { state.value = 'idle'; } // A failed update check must not interrupt chat.
  }
  function dismiss(): void { if (state.value !== 'installing') state.value = 'idle'; }
  async function install(): Promise<void> {
    if (!update.value || state.value === 'installing') return;
    state.value = 'installing'; error.value = null; progress.value = null;
    try {
      await update.value.install((received, total) => { progress.value = total ? Math.min(100, Math.round(received / total * 100)) : null; });
      state.value = 'opened';
    } catch { state.value = 'error'; error.value = 'Não foi possível atualizar. Tente novamente.'; }
  }
  return { update, state, visible, error, progress, start, dismiss, install };
}
