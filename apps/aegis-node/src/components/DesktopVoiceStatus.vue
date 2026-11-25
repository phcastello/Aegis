<script setup lang="ts">
import { listen, type UnlistenFn } from '@tauri-apps/api/event';
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { handsFree } from '../services/handsFree';

const labels = new Map([
  ['capturing', 'Escutando'],
  ['processing', 'Preparando resposta'],
  ['speaking', 'Falando'],
  ['continuation', 'Aguardando você'],
]);
const status = ref('');
const label = computed(() => labels.get(status.value));
let revision = 0;
let disposed = false;
let stop: UnlistenFn | undefined;

async function readCurrentStatus() {
  const request = ++revision;
  try {
    const state = await handsFree('state');
    if (!disposed && revision === request) status.value = state.status;
  } catch {
    if (!disposed && revision === request) status.value = '';
  }
}

function visibilityChanged() {
  if (document.visibilityState === 'visible') void readCurrentStatus();
}

onMounted(async () => {
  try {
    const unlisten = await listen<{ status: string }>('aegis-windows-voice-state', event => {
      if (disposed) return;
      revision++;
      status.value = event.payload?.status ?? '';
    });
    if (disposed) { unlisten(); return; }
    stop = unlisten;
  } catch { return; }
  document.addEventListener('visibilitychange', visibilityChanged);
  visibilityChanged();
});

onBeforeUnmount(() => {
  disposed = true;
  stop?.();
  document.removeEventListener('visibilitychange', visibilityChanged);
});
</script>

<template>
  <span class="desktop-voice-status" role="status" aria-atomic="true">
    <span v-if="label" class="desktop-voice-indicator" :data-phase="status">
      <svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
        <g v-if="status === 'capturing'">
          <rect x="9" y="3" width="6" height="12" rx="3" />
          <path d="M5.5 11.5a6.5 6.5 0 0 0 13 0M12 18v3m-3 0h6" />
        </g>
        <g v-else-if="status === 'processing'" fill="currentColor" stroke="none">
          <circle cx="5" cy="12" r="2" /><circle cx="12" cy="12" r="2" /><circle cx="19" cy="12" r="2" />
        </g>
        <path v-else-if="status === 'speaking'" d="M3 9h4l5-4v14l-5-4H3z M16 8a6 6 0 0 1 0 8m3-11a10 10 0 0 1 0 14" />
        <path v-else d="M20 11.5a7.5 7.5 0 0 1-7.5 7.5H9l-5 3 1-6a7.5 7.5 0 1 1 15-4.5Z" />
      </svg>
      {{ label }}
    </span>
  </span>
</template>

<style scoped>
.desktop-voice-status { display: inline-flex; pointer-events: none; }
.desktop-voice-indicator {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  margin-inline-end: 6px;
  min-height: 30px;
  padding: 3px 11px 3px 9px;
  border: 1px solid var(--line-strong);
  border-radius: 999px;
  background: var(--surface);
  color: var(--text);
  font-size: .8125rem;
  font-weight: 500;
  line-height: 1.4;
  white-space: nowrap;
}
.desktop-voice-indicator svg { width: 16px; height: 16px; color: var(--accent-strong); }
.desktop-voice-indicator[data-phase="capturing"] svg { color: #92d4b7; }
</style>
