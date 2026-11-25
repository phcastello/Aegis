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
const barCount = 32;
const staleMs = 1600;
const status = ref('');
const label = computed(() => labels.get(status.value));
const inputPhase = computed(() => status.value === 'capturing' || status.value === 'continuation');
const meterPhase = computed(() => inputPhase.value || status.value === 'speaking');
const levels = ref<number[]>([]);
let generation: number | undefined;
let revision = 0;
let disposed = false;
let expiry: ReturnType<typeof setTimeout> | undefined;
let stopState: UnlistenFn | undefined;
let stopWave: UnlistenFn | undefined;

function barHeight(level: number) {
  return level > 0.002 ? 1.3 + level * 28.8 : 0;
}

function validGeneration(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
}

function clearWaveform() {
  clearTimeout(expiry);
  expiry = undefined;
  if (levels.value.length) levels.value = [];
}

function applyStatus(nextStatus: string, nextGeneration?: number) {
  if (generation !== undefined && validGeneration(nextGeneration) && nextGeneration < generation) return;
  if (status.value !== nextStatus || generation !== nextGeneration) clearWaveform();
  status.value = nextStatus;
  generation = validGeneration(nextGeneration) ? nextGeneration : undefined;
}

function updateWaveform(payload: { status?: unknown; generation?: unknown; levels?: unknown } | null) {
  if (disposed || document.visibilityState !== 'visible' || !meterPhase.value ||
      !payload || payload.status !== status.value || !validGeneration(payload.generation) ||
      payload.generation !== generation || !Array.isArray(payload.levels) || payload.levels.length !== barCount ||
      !payload.levels.every((level: unknown) => typeof level === 'number' && Number.isFinite(level) && level >= 0 && level <= 1)) return;
  if (!payload.levels.some(level => level > 0.002)) {
    clearWaveform();
    return;
  }
  clearTimeout(expiry);
  levels.value = payload.levels;
  expiry = setTimeout(clearWaveform, staleMs);
}

async function readCurrentStatus() {
  const request = ++revision;
  try {
    const state = await handsFree('state');
    if (!disposed && revision === request) applyStatus(state.status, state.generation);
  } catch {
    if (!disposed && revision === request) applyStatus('');
  }
}

function visibilityChanged() {
  clearWaveform();
  if (document.visibilityState === 'visible') void readCurrentStatus();
}

onMounted(async () => {
  try {
    const unlisten = await listen<{ status: string; generation?: number }>('aegis-windows-voice-state', event => {
      if (disposed) return;
      revision++;
      applyStatus(event.payload?.status ?? '', event.payload?.generation);
    });
    if (disposed) { unlisten(); return; }
    stopState = unlisten;
  } catch { return; }
  try {
    const unlisten = await listen<Parameters<typeof updateWaveform>[0]>('aegis-windows-voice-waveform', event => {
      updateWaveform(event.payload);
    });
    if (disposed) { unlisten(); return; }
    stopWave = unlisten;
  } catch { /* The status indicator remains useful if waveform events are unavailable. */ }
  if (disposed) return;
  document.addEventListener('visibilitychange', visibilityChanged);
  visibilityChanged();
});

onBeforeUnmount(() => {
  disposed = true;
  clearWaveform();
  stopState?.();
  stopWave?.();
  document.removeEventListener('visibilitychange', visibilityChanged);
});
</script>

<template>
  <span class="desktop-voice-status" role="status" aria-atomic="true">
    <span v-if="label" class="desktop-voice-indicator" :data-phase="status">
      <span>{{ label }}</span>
      <template v-if="meterPhase">
        <svg class="desktop-voice-waveform" :data-meter="inputPhase ? 'input' : 'output'" viewBox="0 0 160 36" aria-hidden="true" preserveAspectRatio="none">
          <line v-if="!levels.length" class="desktop-voice-baseline" x1="9.6" :y1="inputPhase ? 18 : 32" x2="150.4" :y2="inputPhase ? 18 : 32" />
          <rect v-for="(level, index) in levels" :key="index"
            :x="9.6 + (index + .5) * 4.4 - 1"
            :y="inputPhase ? 18 - barHeight(level) / 2 : 32 - barHeight(level)"
            width="2" :height="barHeight(level)" rx="1"
            :opacity="level > 0.002 ? 0.4 + 0.6 * level : 0" />
        </svg>
      </template>
      <template v-else>
        <svg class="desktop-voice-state-icon" viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
          <g class="desktop-voice-thoughts" fill="currentColor" stroke="none">
            <circle cx="12" cy="12" r="2" style="transform: translate(-4px, -6px)" />
            <circle cx="12" cy="12" r="2" style="transform: translate(5px, 0px)" />
            <circle cx="12" cy="12" r="2" style="transform: translate(-1px, 6px)" />
          </g>
        </svg>
      </template>
    </span>
  </span>
</template>

<style scoped>
.desktop-voice-status { display: inline-flex; pointer-events: none; }
.desktop-voice-indicator {
  display: inline-flex;
  flex-direction: column;
  align-items: center;
  margin-inline-end: 6px;
  width: clamp(136px, calc(14vw + 20px), 196px);
  padding: 3px 9px;
  border: 1px solid var(--line-strong);
  border-radius: var(--radius-sm);
  background: var(--surface);
  color: var(--text);
  font-size: .8125rem;
  font-weight: 500;
  line-height: 1.4;
  white-space: nowrap;
}
.desktop-voice-state-icon { width: 34px; height: 34px; color: var(--accent-strong); }
.desktop-voice-thoughts circle { animation: 1.8s ease-in-out infinite; }
.desktop-voice-thoughts circle:nth-child(1) { animation-name: desktop-thought-first; }
.desktop-voice-thoughts circle:nth-child(2) { animation-name: desktop-thought-second; }
.desktop-voice-thoughts circle:nth-child(3) { animation-name: desktop-thought-third; }
@keyframes desktop-thought-first {
  0%, 100% { transform: translate(-4px, -6px); }
  25% { transform: translate(6px, -4px); }
  50% { transform: translate(1.5px, .4px); }
  75% { transform: translate(-5px, 3.5px); }
}
@keyframes desktop-thought-second {
  0%, 100% { transform: translate(5px, 0); }
  25% { transform: translate(0, 0); }
  50% { transform: translate(-.8px, -1.5px); }
  75% { transform: translate(0, -6px); }
}
@keyframes desktop-thought-third {
  0%, 100% { transform: translate(-1px, 6px); }
  25% { transform: translate(-6px, 4px); }
  50% { transform: translate(-1px, 1.5px); }
  75% { transform: translate(6px, 2.5px); }
}
@media (prefers-reduced-motion: reduce) { .desktop-voice-thoughts circle { animation: none; } }
.desktop-voice-waveform { display: block; width: 100%; height: 34px; fill: var(--accent); }
.desktop-voice-baseline { stroke: var(--accent); stroke-opacity: .24; stroke-width: 1; }
@media (max-width: 520px) {
  .desktop-voice-indicator { width: 134px; padding: 3px 6px; font-size: .75rem; }
  .desktop-voice-waveform { height: 26px; }
  .desktop-voice-state-icon { width: 26px; height: 26px; }
}
@media (forced-colors: active) {
  .desktop-voice-waveform { fill: CanvasText; }
  .desktop-voice-waveform rect { opacity: 1; }
  .desktop-voice-baseline { stroke: CanvasText; stroke-opacity: 1; }
}
</style>
