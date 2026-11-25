<script setup lang="ts">
import { listen, type UnlistenFn } from '@tauri-apps/api/event';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { handsFree } from '../services/handsFree';
import { PlaybackSpectrumDisplay } from '../services/playbackSpectrum';
import { recentVoiceHistory, voiceHistoryBarHeight, VOICE_HISTORY_STEP_MS } from '@aegis/client/services/voiceWaveform';
import './thinking-motion.css';

const labels = new Map([
  ['capturing', 'Escutando'],
  ['processing', 'Preparando resposta'],
  ['speaking', 'Falando'],
  ['continuation', 'Aguardando você'],
]);
const captureBarCount = 64;
const rawSpectrumBarCount = 32;
const staleMs = 1600;
const playbackStaleMs = 500;
const playbackQuietClearMs = 120;
const playback = new PlaybackSpectrumDisplay(32, 65, 130);
const emit = defineEmits<{ activeChanged: [active: boolean]; conversation: [id: string | null] }>();
const status = ref('');
const label = computed(() => labels.get(status.value));
watch(label, value => emit('activeChanged', Boolean(value)), { immediate: true });
const inputPhase = computed(() => status.value === 'capturing' || status.value === 'continuation');
const meterPhase = computed(() => inputPhase.value || status.value === 'speaking');
const levels = ref<number[]>([]);
const visualArea = ref<HTMLElement | null>(null);
const visualWidth = ref(0);
let resizeObserver: ResizeObserver | undefined;

// Keep the newest measured input slots when a narrow chat column cannot fit
// the same thin bars. Output may pool adjacent frequency bins instead.
const displayedLevels = computed(() => {
  const measured = levels.value;
  if (!measured.length) return measured;
  if (inputPhase.value) return recentVoiceHistory(measured, visualWidth.value * .94);
  let groupSize = 1;
  const width = inputPhase.value ? visualWidth.value : Math.min(460, visualWidth.value);
  while (measured.length / groupSize > 16 && width * .94 / (measured.length / groupSize) < (inputPhase.value ? 3 : 4))
    groupSize *= 2;
  return groupSize === 1 ? measured : Array.from({ length: measured.length / groupSize }, (_, index) =>
    Math.max(...measured.slice(index * groupSize, (index + 1) * groupSize)));
});

watch(visualArea, area => {
  resizeObserver?.disconnect();
  resizeObserver = undefined;
  visualWidth.value = area?.getBoundingClientRect().width ?? 0;
  if (!area) return;
  resizeObserver = new ResizeObserver(entries => { visualWidth.value = entries[0]?.contentRect.width ?? 0; });
  resizeObserver.observe(area);
}, { flush: 'post' });
let generation: number | undefined;
let revision = 0;
let disposed = false;
let expiry: ReturnType<typeof setTimeout> | undefined;
let stopState: UnlistenFn | undefined;
let stopWave: UnlistenFn | undefined;
let stopConversation: UnlistenFn | undefined;

function barHeight(level: number) {
  if (inputPhase.value) return voiceHistoryBarHeight(level);
  return level > 0.002 ? 2 + level * 50 : 0;
}
const barWidth = 3;

function barPosition(index: number) {
  // Both meters use actual pixel geometry, with only their centers spread
  // proportionally. The container width never scales a bar's thickness.
  return `${3 + (index + .5) * (94 / displayedLevels.value.length)}%`;
}

async function cancelInteraction() {
  try { const next = await handsFree('cancel'); if (!disposed) applyStatus(next.status, next.generation); }
  catch { /* The native state event remains authoritative. */ }
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
      payload.generation !== generation || !Array.isArray(payload.levels) || payload.levels.length !== (inputPhase.value ? captureBarCount : rawSpectrumBarCount) ||
      !payload.levels.every((level: unknown) => typeof level === 'number' && Number.isFinite(level) && level >= 0 && level <= 1)) return;
  const quiet = !payload.levels.some(level => level > 0.002);
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
  try {
    const unlisten = await listen<{ conversationId?: unknown }>('aegis-windows-voice-conversation', event => {
      const id = event.payload?.conversationId;
      if (!disposed && (id === null || typeof id === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(id)))
        emit('conversation', id);
    });
    if (disposed) { unlisten(); return; }
    stopConversation = unlisten;
  } catch { /* Older native clients do not announce an active conversation. */ }
  if (disposed) return;
  document.addEventListener('visibilitychange', visibilityChanged);
  visibilityChanged();
});

onBeforeUnmount(() => {
  disposed = true;
  resizeObserver?.disconnect();
  clearWaveform();
  stopState?.();
  stopWave?.();
  stopConversation?.();
  document.removeEventListener('visibilitychange', visibilityChanged);
});
</script>

<template>
  <div v-if="label" class="desktop-voice-status" data-expanded="active">
    <div class="desktop-voice-indicator" :data-phase="status" role="status" aria-atomic="true" :style="{ '--voice-history-step': `${VOICE_HISTORY_STEP_MS}ms` }">
      <span class="sr-only">{{ label }}</span>
      <span ref="visualArea" class="desktop-voice-visual">
        <template v-if="meterPhase">
          <svg class="desktop-voice-waveform" :data-meter="inputPhase ? 'input' : 'output'" aria-hidden="true">
            <line v-if="!displayedLevels.length" class="desktop-voice-baseline" x1="3%" :y1="inputPhase ? 32 : 59" x2="97%" :y2="inputPhase ? 32 : 59" />
            <rect v-for="(level, index) in displayedLevels" :key="index"
              :x="barPosition(index)"
              :y="inputPhase ? 32 - barHeight(level) / 2 : 59 - barHeight(level)"
              :width="barWidth" :height="barHeight(level)" :rx="barWidth / 2"
              :opacity="inputPhase ? 1 : level > 0.002 ? 0.4 + 0.6 * level : 0" />
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
    </div>
    <button class="record-discard-button desktop-voice-cancel" type="button" aria-label="Cancelar interação" title="Cancelar interação" @click="cancelInteraction">
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="m6 6 12 12M6 18 18 6" /></svg>
    </button>
  </div>
</template>

<style scoped>
.desktop-voice-status {
  position: relative;
  display: flex;
  align-items: center;
  min-height: 90px;
  padding: 12px 58px;
  border: 1px solid var(--line-strong);
  border-radius: var(--radius-lg);
  background: var(--surface-soft);
}
.desktop-voice-indicator {
  display: flex;
  flex: 1 1 0;
  min-width: 0;
  align-items: center;
}
.desktop-voice-visual {
  display: flex;
  align-items: center;
  justify-content: center;
  flex: 1 1 0;
  min-width: 0;
  height: 64px;
}
.desktop-voice-state-icon { width: 34px; height: 34px; color: var(--accent-strong); }
.desktop-voice-thoughts circle { animation: 10334ms linear infinite; }
.desktop-voice-thoughts circle:nth-child(1) { animation-name: aegis-thought-dot-1; }
.desktop-voice-thoughts circle:nth-child(2) { animation-name: aegis-thought-dot-2; }
.desktop-voice-thoughts circle:nth-child(3) { animation-name: aegis-thought-dot-3; }
@media (prefers-reduced-motion: reduce) { .desktop-voice-thoughts circle { animation: none; } }
.desktop-voice-waveform { display: block; width: 100%; height: 64px; fill: var(--accent); }
.desktop-voice-waveform[data-meter="output"] { max-width: 460px; }
.desktop-voice-waveform[data-meter="input"] { fill: var(--muted); }
.desktop-voice-waveform rect {
  transform-box: fill-box;
  transform: translateX(-50%);
  transition: height 50ms ease-out, y 50ms ease-out, opacity 50ms linear;
}
.desktop-voice-waveform[data-meter="input"] rect { transition: height var(--voice-history-step, 60ms) linear, y var(--voice-history-step, 60ms) linear; }
.desktop-voice-cancel { position: absolute; right: 7px; top: 50%; transform: translateY(-50%); }
.desktop-voice-baseline { stroke: var(--accent); stroke-opacity: .24; stroke-width: 1; }
@media (max-width: 520px) {
  .desktop-voice-status { padding-inline: 52px; }
}
@media (prefers-reduced-motion: reduce) { .desktop-voice-waveform rect, .desktop-voice-waveform[data-meter="input"] rect { transition: none; } }
@media (forced-colors: active) {
  .desktop-voice-waveform { fill: CanvasText; }
  .desktop-voice-waveform rect { opacity: 1; }
  .desktop-voice-baseline { stroke: CanvasText; stroke-opacity: 1; }
}
</style>
