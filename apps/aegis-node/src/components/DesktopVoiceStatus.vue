<script setup lang="ts">
import { listen, type UnlistenFn } from '@tauri-apps/api/event';
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { handsFree } from '../services/handsFree';
import { PlaybackSpectrumDisplay } from '../services/playbackSpectrum';
import './thinking-motion.css';

const labels = new Map([
  ['capturing', 'Escutando'],
  ['processing', 'Preparando resposta'],
  ['speaking', 'Falando'],
  ['continuation', 'Aguardando você'],
]);
const barCount = 32;
const staleMs = 1600;
const playbackStaleMs = 500;
const playbackQuietClearMs = 120;
const playback = new PlaybackSpectrumDisplay();
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
  return level > 0.002 ? 1.3 + level * (inputPhase.value ? 34.7 : 19.5) : 0;
}
const slotWidth = computed(() => 140.8 / (inputPhase.value ? barCount : PlaybackSpectrumDisplay.bandCount));
const barWidth = computed(() => inputPhase.value ? 4 : 3.2);

function barPosition(index: number) {
  // Input history uses actual pixel height and relative horizontal positions.
  // Keep the 32 real samples but never stretch their thickness with the viewport.
  return inputPhase.value ? `${6 + (index + .5) * (88 / barCount)}%`
    : 9.6 + (index + .5) * slotWidth.value - barWidth.value / 2;
}

function validGeneration(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
}

function clearWaveform() {
  clearTimeout(expiry);
  expiry = undefined;
  playback.clear();
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
  const quiet = !payload.levels.some(level => level > 0.002);
  if (inputPhase.value && quiet) {
    clearWaveform();
    return;
  }
  clearTimeout(expiry);
  levels.value = inputPhase.value ? payload.levels : playback.update(payload.levels, performance.now(),
    !window.matchMedia('(prefers-reduced-motion: reduce)').matches);
  if (levels.value.length) expiry = setTimeout(clearWaveform, inputPhase.value ? staleMs
    : quiet ? playbackQuietClearMs : playbackStaleMs);
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
  <span class="desktop-voice-status" :data-expanded="label && inputPhase ? 'input' : undefined" role="status" aria-atomic="true">
    <span v-if="label" class="desktop-voice-indicator" :data-phase="status">
      <span>{{ label }}</span>
      <template v-if="meterPhase">
        <svg class="desktop-voice-waveform" :data-meter="inputPhase ? 'input' : 'output'" :viewBox="inputPhase ? undefined : '0 0 160 36'" aria-hidden="true" preserveAspectRatio="none">
          <line v-if="!levels.length" class="desktop-voice-baseline" :x1="inputPhase ? '6%' : 9.6" :y1="inputPhase ? 20 : 32" :x2="inputPhase ? '94%' : 150.4" :y2="inputPhase ? 20 : 32" />
          <rect v-for="(level, index) in levels" :key="index"
            :x="barPosition(index)"
            :y="inputPhase ? 20 - barHeight(level) / 2 : 32 - barHeight(level)"
            :width="barWidth" :height="barHeight(level)" :rx="barWidth / 2"
            :opacity="level > 0.002 ? 0.4 + 0.6 * level : 0" />
        </svg>
      </template>
      <template v-else>
        <svg class="desktop-voice-state-icon" viewBox="0 0 28 28" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
          <g class="desktop-voice-thoughts" fill="currentColor" stroke="none">
            <circle cx="14" cy="14" r="2" style="transform: translate(-6.6px, 3.744px)" />
            <circle cx="14" cy="14" r="2" style="transform: translate(0px, -2.963px)" />
            <circle cx="14" cy="14" r="2" style="transform: translate(6.6px, -5px)" />
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
.desktop-voice-status[data-expanded="input"] {
  flex: 1 1 0;
  min-width: 0;
}
.desktop-voice-status[data-expanded="input"] .desktop-voice-indicator {
  flex: 1 1 0;
  width: auto;
  min-width: 0;
  flex-direction: row;
  gap: 14px;
}
.desktop-voice-status[data-expanded="input"] .desktop-voice-indicator > span {
  flex: 0 0 auto;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
}
.desktop-voice-state-icon { width: 34px; height: 34px; color: var(--accent-strong); }
.desktop-voice-thoughts circle { animation: 10334ms linear infinite; }
.desktop-voice-thoughts circle:nth-child(1) { animation-name: aegis-thought-dot-1; }
.desktop-voice-thoughts circle:nth-child(2) { animation-name: aegis-thought-dot-2; }
.desktop-voice-thoughts circle:nth-child(3) { animation-name: aegis-thought-dot-3; }
@media (prefers-reduced-motion: reduce) { .desktop-voice-thoughts circle { animation: none; } }
.desktop-voice-waveform { display: block; width: 100%; height: 34px; fill: var(--accent); }
.desktop-voice-waveform[data-meter="input"] {
  flex: 1 1 0;
  min-width: 0;
  height: 40px;
}
.desktop-voice-waveform[data-meter="input"] rect {
  width: clamp(1px, 1.35%, 8px);
  transform-box: fill-box;
  transform: translateX(-50%);
}
.desktop-voice-baseline { stroke: var(--accent); stroke-opacity: .24; stroke-width: 1; }
@media (max-width: 520px) {
  .desktop-voice-indicator { width: 134px; padding: 3px 6px; font-size: .75rem; }
  .desktop-voice-waveform { height: 26px; }
  .desktop-voice-status[data-expanded="input"] .desktop-voice-indicator { flex-direction: column; gap: 0; }
  .desktop-voice-waveform[data-meter="input"] { flex: 0 0 40px; height: 40px; }
  .desktop-voice-state-icon { width: 26px; height: 26px; }
}
@media (forced-colors: active) {
  .desktop-voice-waveform { fill: CanvasText; }
  .desktop-voice-waveform rect { opacity: 1; }
  .desktop-voice-baseline { stroke: CanvasText; stroke-opacity: 1; }
}
</style>

<!-- The voice-status slot exists only in the Windows app. Expand its parent only
     during input, leaving the shared chat and other phases unchanged. -->
<style>
.chat-header > .chat-voice-controls:has(> .desktop-voice-status[data-expanded="input"]) {
  left: max(72px, calc(env(safe-area-inset-left) + 56px));
  pointer-events: none;
}
.chat-header > .chat-voice-controls:has(> .desktop-voice-status[data-expanded="input"]) > :not(.desktop-voice-status) {
  flex-shrink: 0;
  pointer-events: auto;
}
@media (min-width: 901px) {
  .hideout-shell--sidebar-open .chat-header > .chat-voice-controls:has(> .desktop-voice-status[data-expanded="input"]) {
    left: 16px;
  }
}
</style>
